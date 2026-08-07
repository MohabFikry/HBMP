-- ===========================================================================================================
-- ADR-0034 — the fulfilment authorization: what was handed over is not the prescription.
-- ===========================================================================================================
--
-- Dispensing a prescription line, or consuming an investigation-order line, now ISSUES an authorization: a
-- record of what was actually delivered, separate from the clinical instruction it was delivered against.
-- A substitution lands on that record and nowhere else — prescription_line.drug_id is never written, and
-- the schema below deliberately gives the delivered molecule its own column rather than a place to overwrite
-- the prescribed one.
--
-- WHY THIS SHARES THE AGGREGATE. One authorization number space, one worklist, one audit trail, one RLS
-- policy — the argument 0005 made when validity extensions became a fourth `source` rather than a parallel
-- table. What it does NOT share is the lifecycle: `Issued` is terminal and unreachable, so settled work can
-- never be assigned to a reviewer.
--
-- EXPAND PHASE THROUGHOUT. Every column added is nullable or defaulted, and every CHECK is WIDENED — an
-- instance still running the previous build keeps writing rows that pass.

-- ---- kind: review request vs fulfilment record --------------------------------------------------------
-- Defaulted to 'Review' so every existing row (and every row a previous-build instance writes) is correct
-- without a backfill: everything written before this migration WAS a review request.
ALTER TABLE approvals.authorization
    ADD COLUMN IF NOT EXISTS kind text NOT NULL DEFAULT 'Review' CHECK (kind IN ('Review','Fulfilment'));

COMMENT ON COLUMN approvals.authorization.kind IS
    'Review = a question awaiting a decision (the reviewer inbox). Fulfilment = a record of something already '
    'delivered at a counter or a bench (ADR-0034). Set at creation; never updated.';

-- ---- status: 'Issued' ---------------------------------------------------------------------------------
-- WIDENING only. Every value the old constraint admitted the new one still admits.
ALTER TABLE approvals.authorization DROP CONSTRAINT IF EXISTS authorization_status_check;  -- migrate-compat: contract-ok (widening a CHECK; the old value set stays valid for a previous-build instance)
ALTER TABLE approvals.authorization
    ADD CONSTRAINT authorization_status_check
    CHECK (status IN ('Draft','Submitted','UnderReview','Approved','PartiallyApproved','Rejected',
                      'InfoRequested','Overridden','EmergencyApproved','Expired','Issued'));

-- A fulfilment is Issued and an Issued row is a fulfilment. Belt and braces for the workflow guard, which
-- lives in the application: the DB is where an invariant survives a bug in the code that was supposed to
-- enforce it.
ALTER TABLE approvals.authorization DROP CONSTRAINT IF EXISTS authorization_kind_status_check;  -- migrate-compat: contract-ok (idempotent re-run guard for a constraint this same migration creates two lines below; it has never existed on a previously-deployed build, so nothing is being taken away from one)
ALTER TABLE approvals.authorization
    ADD CONSTRAINT authorization_kind_status_check
    CHECK ((kind = 'Fulfilment') = (status = 'Issued'));

-- 0001 required a requesting provider on every non-Manual source. A fulfilment is exempted rather than
-- refused: the dispensing path already tolerates a principal that carries no provider, and dropping the
-- record of a medicine that was handed over — because the token was thin — would lose the only trace that
-- it happened. WIDENING: every row the old constraint admitted the new one still admits.
ALTER TABLE approvals.authorization DROP CONSTRAINT IF EXISTS authorization_check;  -- migrate-compat: contract-ok (widening a CHECK; the old value set stays valid for a previous-build instance)
ALTER TABLE approvals.authorization
    ADD CONSTRAINT authorization_check
    CHECK (source = 'Manual' OR kind = 'Fulfilment' OR requesting_provider_id IS NOT NULL);

-- The fulfilment register is listed by kind and read newest-first. The existing (status, sla_due_at) index
-- serves the SLA queue and does not serve this at all — added now rather than discovered under load.
CREATE INDEX IF NOT EXISTS ix_auth_kind_submitted ON approvals.authorization (kind, submitted_at DESC);

-- One fulfilment authorization per source item. A second dispense against the same prescription appends an
-- item to the SAME authorization: the prescription is one course of treatment, and the authorization is what
-- was delivered against it. Partial, because manual authorizations carry no source_ref.
CREATE UNIQUE INDEX IF NOT EXISTS ux_auth_fulfilment_source
    ON approvals.authorization (tenant_id, source, source_ref)
    WHERE kind = 'Fulfilment' AND source_ref IS NOT NULL;

-- ---- authorization_item: what was actually delivered ---------------------------------------------------
CREATE TABLE IF NOT EXISTS approvals.authorization_item (
    item_id             uuid PRIMARY KEY,
    tenant_id           text NOT NULL,
    authorization_id    uuid NOT NULL REFERENCES approvals.authorization(authorization_id),
    source_line_id      uuid,
    -- The dispense_event / order_fulfillment id. The idempotency anchor: at-least-once delivery is guarded
    -- once by the processed_event ledger and once here, because only this one survives a replay that arrives
    -- under a new event id.
    fulfilment_ref      text NOT NULL,
    ordered_code        text NOT NULL,
    ordered_label       text,
    fulfilled_code      text NOT NULL,
    fulfilled_label     text,
    quantity            numeric(12,3) NOT NULL,
    substitution_reason text,
    fulfilled_at        timestamptz NOT NULL DEFAULT now(),
    -- A substitution without a stated reason is a molecule the prescriber did not choose and no account of
    -- why, which is worse than either the substitution or a refusal on its own.
    CHECK (ordered_code = fulfilled_code OR substitution_reason IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS ix_auth_item_auth ON approvals.authorization_item (authorization_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_auth_item_fulfilment
    ON approvals.authorization_item (tenant_id, fulfilment_ref);

-- ---- grants + RLS, matching 0003 ------------------------------------------------------------------------
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON approvals.authorization_item TO hbmp_app;
    END IF;
END $$;

ALTER TABLE approvals.authorization_item ENABLE ROW LEVEL SECURITY;
ALTER TABLE approvals.authorization_item FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_authorization_item ON approvals.authorization_item;
CREATE POLICY rls_authorization_item ON approvals.authorization_item
    USING (tenant_id = current_setting('app.tenant_id', true));

-- ---- the dedupe ledger the fulfilment consumer reads ----------------------------------------------------
-- Its own table rather than reusing processed_request: that one is keyed on an HTTP Idempotency-Key and
-- carries a status code, and a broker message has neither. Intentionally RLS-FREE (mirrors
-- policy.processed_event): a transport-level ledger of event ids, with no tenant data in it.
CREATE TABLE IF NOT EXISTS approvals.processed_event (
    event_id     uuid PRIMARY KEY,
    processed_at timestamptz NOT NULL DEFAULT now()
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON approvals.processed_event TO hbmp_app;
    END IF;
END $$;

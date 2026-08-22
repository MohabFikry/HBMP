-- policy-service — 0020: the payer record grows the facts that decide whether it can pay, and a history twin.
-- ADDITIVE (expand phase). Every column is nullable or defaulted; nothing existing changes shape.
--
-- ============================================================================================================
-- WHAT A PAYER IS, AND WHAT THE ROW ACTUALLY HELD
-- ============================================================================================================
-- A payer is the counterparty a policy is funded BY: a donor grant, a government programme, a partner NGO, an
-- insurer, or Mersal's own funds. `policy.policy.payer_id` points at it (0008), the utilization surface rolls
-- up to it (19.4), and `admin.payer_assignment` restricts a user to one (19.5) — so it is the top of the
-- commercial hierarchy the whole benefit book hangs from.
--
-- The row stored six facts: a code, two names, a type, an unused jsonb contact blob, and a status. Which is
-- enough to LABEL a payer and not enough to administer one. Every operational question about a payer lived
-- somewhere outside the platform:
--
--   · "Is this grant still running?"            — the agreement window was in somebody's inbox
--   · "How much have we committed against it?"  — the ceiling was in a spreadsheet
--   · "Who do we invoice, and by when?"         — settlement terms were in the signed PDF
--   · "Who do we call when a claim stalls?"     — `contact` was '{}' on every row ever created
--
-- The last one is the tell. `contact jsonb NOT NULL DEFAULT '{}'` has existed since 0005 and NO endpoint ever
-- read or wrote it: a column reserved for a need nobody then implemented. It is given a shape here rather
-- than a second table, because a payer contact is a small, whole, always-read-together block — but a TYPED
-- shape (see PayerContact), not free-form, so the fields can be rendered and validated instead of guessed.
--
-- ============================================================================================================
-- WHY THE AGREEMENT WINDOW IS NOT THE STATUS
-- ============================================================================================================
-- A payer whose agreement ended is not the same as a payer that was deactivated, and collapsing them would
-- lose the difference between "the grant ran its course" and "we stopped working with them". The window is a
-- FACT about the contract; the status is a DECISION about the record. Both are kept, and the screen shows an
-- Active payer whose agreement window has closed as exactly that — active, and out of window — because that
-- combination is a thing somebody needs to act on rather than a state to hide.
--
-- ============================================================================================================
-- THE HISTORY TWIN
-- ============================================================================================================
-- Same construction as provider.practitioner_history (0014) and emr.roster_exception_history (0016), and for
-- the same reason. Every write here is audited into the hash-chained trail, which sits behind `audit:read` —
-- Security, Compliance and the DPO. Correctly: it is tamper-evident evidence whose own reads are audited.
-- But it leaves the policy administrator who maintains a funding ceiling with no way to ask who last raised
-- it, about a record they own. The information existed, in a store they are rightly not given.
--
-- So the twin answers the operational question at the SAME authority that maintains the payer, and the audit
-- event still fires. They answer different questions for different people; both are written.
--
-- The actor is snapshotted BY NAME as well as by subject, following 0014's precedent: resolving names at read
-- time renders "unknown" for everyone who has since left, and making policy-service call the issuer to draw a
-- history row is a dependency in the wrong direction for a read that must not fail.

-- ---- the payer's own facts -------------------------------------------------------------------------------

ALTER TABLE policy.payer
    -- Identity beyond the internal code: the reference the PAYER knows this agreement by. A donor's grant
    -- number, an insurer's licence. Reconciliation is done against their reference, not ours.
    ADD COLUMN IF NOT EXISTS external_ref                text,
    ADD COLUMN IF NOT EXISTS agreement_no                text,

    -- The funding window. Half-open like every other window in this schema: [from, to).
    ADD COLUMN IF NOT EXISTS agreement_from              date,
    ADD COLUMN IF NOT EXISTS agreement_to                date,

    -- The commitment. numeric(14,2) matches coverage_limit.limit_value — a ceiling and a limit are compared
    -- against each other on the utilization surface, and two different numeric shapes is how a rounding
    -- difference becomes a reconciliation dispute.
    ADD COLUMN IF NOT EXISTS funding_ceiling             numeric(14,2),
    ADD COLUMN IF NOT EXISTS currency                    char(3) NOT NULL DEFAULT 'EGP',

    -- Settlement. Days, because "net 30" is a number of days in every contract anyone has signed, and a text
    -- field would be sorted alphabetically by the first person who tried to report on it.
    ADD COLUMN IF NOT EXISTS settlement_terms_days       int,
    ADD COLUMN IF NOT EXISTS invoicing_cadence           varchar(16),
    ADD COLUMN IF NOT EXISTS claim_submission_window_days int,

    ADD COLUMN IF NOT EXISTS notes                       text,

    -- Why the status is what it is. A deactivation with no reason is a record of the fact and none of the
    -- decision, and the reason is the half somebody needs six months later.
    ADD COLUMN IF NOT EXISTS status_reason               text,
    ADD COLUMN IF NOT EXISTS status_changed_at           timestamptz,
    ADD COLUMN IF NOT EXISTS status_changed_by           uuid,

    -- 0014's precedent: the actor by name, snapshotted.
    ADD COLUMN IF NOT EXISTS created_by_name             text,
    ADD COLUMN IF NOT EXISTS updated_by_name             text;

-- A ceiling of zero is not "uncapped", it is "funded for nothing" — and nothing in the platform can spend
-- against it, so it would present as a payer that refuses every claim for a reason no screen explains.
-- Negative is meaningless. Both are refused here rather than discovered downstream.
ALTER TABLE policy.payer
    DROP CONSTRAINT IF EXISTS ck_payer_funding_ceiling_positive;  -- migrate-compat: contract-ok (re-adding the same predicate; no deployed row can violate it — the column is new in THIS migration)
ALTER TABLE policy.payer
    ADD CONSTRAINT ck_payer_funding_ceiling_positive
        CHECK (funding_ceiling IS NULL OR funding_ceiling > 0);

ALTER TABLE policy.payer
    DROP CONSTRAINT IF EXISTS ck_payer_agreement_window;  -- migrate-compat: contract-ok (columns are new in THIS migration)
ALTER TABLE policy.payer
    ADD CONSTRAINT ck_payer_agreement_window
        CHECK (agreement_from IS NULL OR agreement_to IS NULL OR agreement_to > agreement_from);

ALTER TABLE policy.payer
    DROP CONSTRAINT IF EXISTS ck_payer_invoicing_cadence;  -- migrate-compat: contract-ok (column is new in THIS migration)
ALTER TABLE policy.payer
    ADD CONSTRAINT ck_payer_invoicing_cadence
        CHECK (invoicing_cadence IS NULL
               OR invoicing_cadence IN ('OnClaim','Monthly','Quarterly','SemiAnnual','Annual'));

ALTER TABLE policy.payer
    DROP CONSTRAINT IF EXISTS ck_payer_terms_days_sane;  -- migrate-compat: contract-ok (columns are new in THIS migration)
ALTER TABLE policy.payer
    ADD CONSTRAINT ck_payer_terms_days_sane
        CHECK ((settlement_terms_days IS NULL OR (settlement_terms_days >= 0 AND settlement_terms_days <= 365))
           AND (claim_submission_window_days IS NULL
                OR (claim_submission_window_days >= 0 AND claim_submission_window_days <= 1095)));

-- ---- the history twin ------------------------------------------------------------------------------------

CREATE TABLE IF NOT EXISTS policy.payer_history (
    history_id   bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    payer_id     uuid NOT NULL,
    tenant_id    text NOT NULL,
    operation    text NOT NULL,
    row_snapshot jsonb NOT NULL,
    recorded_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_payer_history_id ON policy.payer_history (payer_id, history_id);

CREATE OR REPLACE FUNCTION policy.write_payer_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO policy.payer_history (payer_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.payer_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_payer_history ON policy.payer;
CREATE TRIGGER trg_payer_history AFTER INSERT OR UPDATE ON policy.payer
    FOR EACH ROW EXECUTE FUNCTION policy.write_payer_history();

-- ---- grants + tenant RLS (ADR-0011, the shape 0002/0005 use) ---------------------------------------------

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT SELECT, INSERT ON policy.payer_history TO hbmp_app;

ALTER TABLE policy.payer_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE policy.payer_history FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_payer_history ON policy.payer_history;
-- Fail-CLOSED: an unset or empty app.tenant_id matches nothing.
CREATE POLICY rls_payer_history ON policy.payer_history
    USING (tenant_id = current_setting('app.tenant_id', true));

-- A tenant_id of '' belongs to no tenant. New tables start with the constraint rather than acquiring it in a
-- later backfill (0016_no_unscoped_rows's lesson).
ALTER TABLE policy.payer_history
    DROP CONSTRAINT IF EXISTS ck_payer_history_tenant_not_blank;  -- migrate-compat: contract-ok (idempotency guard on a table created in THIS migration — there is no deployed reader to break)
ALTER TABLE policy.payer_history
    ADD CONSTRAINT ck_payer_history_tenant_not_blank CHECK (btrim(tenant_id) <> '');

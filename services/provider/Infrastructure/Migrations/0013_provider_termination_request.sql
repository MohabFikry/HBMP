-- provider-service — 0013: make provider termination actually dual-controlled.
-- Found by the 2026-08-09 platform audit. ADDITIVE: adds one table, changes no existing row.
--
-- WHY
-- ---
-- POST /providers/{id}/terminate advertised itself as "dual-controlled (second approver must differ from
-- actor)" and enforced exactly that string comparison: `req.SecondApproverSubject != actor`. The second
-- approver never authenticated, never consented, and was never checked to be a real user — the value was
-- typed by the person doing the terminating, recorded into the audit reason, and the termination completed
-- in that one request. Naming yourself a colleague was the whole control.
--
-- Termination is not a reversible administrative edit. It flips the provider out of the routable network,
-- revokes every provider-scoped user's access, and publishes both facts to the rest of the platform. It is
-- exactly the shape of action the platform already dual-controls properly in admin break-glass, where the
-- approver acts under their OWN bearer token (BreakGlassStatus.Requested → Approved). This mirrors that.
--
-- WHAT IT LOOKS LIKE NOW
-- ----------------------
-- First POST from an authorised user opens a REQUEST and changes nothing about the provider (202). A second
-- POST, from a DIFFERENT authenticated subject, approves it and performs the termination in one transaction.
-- The approver is whoever holds the token on the second call, so "who agreed to this" is a fact the system
-- observed rather than a name the requester supplied.
--
-- WHY A TABLE AND NOT TWO COLUMNS ON provider
-- -------------------------------------------
-- A pending termination is an event with its own actors, timestamps and outcome, and it can be superseded
-- or abandoned. Columns on `provider` would make "who asked, who agreed, when, and was it withdrawn" a set
-- of nullable fields that every other provider query has to ignore, and would keep no history once a second
-- request replaced the first.

CREATE TABLE IF NOT EXISTS provider.provider_termination_request (
    request_id        uuid PRIMARY KEY,
    tenant_id         text NOT NULL,
    provider_id       uuid NOT NULL REFERENCES provider.provider(provider_id),
    reason            text NOT NULL,
    status            text NOT NULL DEFAULT 'Requested',
    requested_by      text NOT NULL,
    requested_at      timestamptz NOT NULL,
    approved_by       text,
    approved_at       timestamptz,
    withdrawn_at      timestamptz,
    CONSTRAINT ck_ptr_tenant_not_blank CHECK (length(btrim(tenant_id)) > 0),
    CONSTRAINT ck_ptr_status CHECK (status IN ('Requested', 'Approved', 'Withdrawn', 'Superseded')),
    -- The control itself, at the datastore. An endpoint check is not an invariant a repair script or a psql
    -- session respects, and this is the one rule the whole table exists to hold.
    CONSTRAINT ck_ptr_distinct_approver CHECK (approved_by IS NULL OR approved_by <> requested_by)
);

-- One live request per provider: a second open request would let two people each approve the other's and
-- turn dual control back into single control with extra steps.
CREATE UNIQUE INDEX IF NOT EXISTS ux_ptr_one_open_request
    ON provider.provider_termination_request (tenant_id, provider_id)
    WHERE status = 'Requested';

CREATE INDEX IF NOT EXISTS ix_ptr_provider ON provider.provider_termination_request (tenant_id, provider_id);

-- Same isolation as every other provider-owned row (0003_rls.sql). FORCE so the owner is subject to it too.
ALTER TABLE provider.provider_termination_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider.provider_termination_request FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_provider_termination_request ON provider.provider_termination_request;
CREATE POLICY rls_provider_termination_request ON provider.provider_termination_request USING (
    tenant_id = current_setting('app.tenant_id', true)
    AND (
        coalesce(current_setting('app.provider_id', true), '') = ''
        OR provider_id::text = current_setting('app.provider_id', true)
    )
);

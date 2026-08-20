-- patient-service — 0008: the idempotency ledger registration has demanded a key for since phase 3 and
-- never had.
--
-- ============================================================================================================
-- THE HEADER WAS REQUIRED AND THEN DISCARDED
-- ============================================================================================================
--
-- `POST /api/v1/beneficiaries` answers 400 without an `Idempotency-Key` — and then does nothing with it. No
-- row recorded it, no read consulted it. So the header bought exactly nothing: a retry after a dropped
-- response, a double-submit from a slow form, a client-side reconnect — each one registered a SECOND PERSON.
--
-- The duplicate-identifier check is not a substitute and never was. It fires only when the operator entered a
-- card or a national id; registration accepts a person with neither (a newly arrived refugee frequently has
-- neither), and those are exactly the registrations most likely to be retried on a poor connection. Two
-- beneficiary rows for one human is the worst duplicate this platform can hold: coverage, encounters,
-- prescriptions and claims all attach to the id, so the two halves of somebody's care diverge permanently and
-- neither record is complete.
--
-- ============================================================================================================
-- WHAT IS STORED
-- ============================================================================================================
--
-- The key, what it produced, and a HASH of the request that produced it (`IdempotencyKeyRules.Hash`, the same
-- rule 18.A3 put on consume and dispense). The hash is what makes a replay honest: a key reused for a
-- DIFFERENT person must be refused, not answered with the first person's record — a 201 naming somebody else
-- is a worse failure than the duplicate it was meant to prevent.
--
-- TENANT-SCOPED and RLS-forced, unlike the transport-level `processed_event` ledgers: this row points at a
-- beneficiary, so it is patient data and must not be readable across tenants. `entity_id` is a uuid rather
-- than a reference, because the ledger outlives nothing — it is never cascaded from, and a foreign key here
-- would make purging a beneficiary a two-table problem for no gain.
--
-- Additive (expand/contract). A previous-build instance neither reads nor writes it, and keeps its current
-- behaviour — which is why the ledger is consulted, never assumed: an absent row means "not seen", which is
-- true of every request made before this deploy.

CREATE TABLE IF NOT EXISTS patient.processed_request (
    idempotency_key text        PRIMARY KEY,
    tenant_id       text        NOT NULL DEFAULT current_setting('app.tenant_id', true),
    operation       text        NOT NULL,
    entity_id       uuid        NOT NULL,
    status_code     int         NOT NULL,
    request_hash    text        NULL,
    created_at      timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_processed_request_entity
    ON patient.processed_request (tenant_id, entity_id);

-- Tenant isolation (ADR-0011), mirroring every other table in this schema.
ALTER TABLE patient.processed_request ENABLE ROW LEVEL SECURITY;
ALTER TABLE patient.processed_request FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_processed_request ON patient.processed_request;
CREATE POLICY rls_processed_request ON patient.processed_request
    USING (tenant_id = current_setting('app.tenant_id', true));

-- SELECT and INSERT only. A ledger row is a statement that a request happened; editing one would let a
-- retried request be re-answered as though it were new, which is the whole thing this table prevents.
GRANT SELECT, INSERT ON patient.processed_request TO hbmp_app;
REVOKE UPDATE, DELETE ON patient.processed_request FROM hbmp_app;

COMMENT ON TABLE patient.processed_request IS
    'Idempotency ledger for the registration write path. The Idempotency-Key header has been REQUIRED since '
    'phase 3 and, until this table, discarded — so a retried registration created a second person.';

COMMENT ON COLUMN patient.processed_request.request_hash IS
    'SHA-256 of the canonical request (IdempotencyKeyRules.Hash). A replay whose hash differs is refused 422 '
    'rather than answered with the earlier beneficiary — a 201 naming somebody else is worse than a '
    'duplicate. NULL on rows written before the column: unverifiable, so treated as a match.';

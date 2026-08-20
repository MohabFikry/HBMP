-- provider-service — 0014: the practitioner's own change history, and the actor behind each change.
-- ADDITIVE (expand phase).
--
-- WHY
-- ---
-- `provider.practitioner` holds the LICENCE NUMBER and its expiry, and since 25.3 that expiry is a safety
-- gate: it decides whether a clinician can be booked, it strands existing appointments when it is brought
-- forward, and it is the single most consequential field a branch coordinator can edit.
--
-- Every one of those edits has been audited since 25.2 — and the audit trail lives behind `audit:read`, which
-- is Security, Compliance and the DPO. Correctly: it is hash-chained evidence and its own reads are audited.
-- But it means the person who RUNS the clinic has had no way to answer "who renewed this, and when" about a
-- record they administer themselves. The information existed, in a store they are rightly not given.
--
-- So: the same history twin the rest of the platform uses (provider.provider_history in 0001,
-- emr.roster_exception_history in 0016) — an AFTER INSERT OR UPDATE trigger snapshotting to_jsonb(NEW).
--
-- This does NOT replace the audit event, and the licence endpoint still emits one. They answer different
-- questions for different people: the audit chain answers an investigator and is tamper-evident; this answers
-- a clinic manager and is readable under the same branch reach as the practitioner. Both are written.
--
-- THE ACTOR
-- ---------
-- `practitioner` has carried `created_at` and `updated_at` since 0006 and has never recorded WHO. A snapshot
-- of a row that cannot name the person who changed it is a timeline of anonymous events — which answers the
-- "when" and leaves the question people actually ask.
--
-- `updated_by_name` alongside the subject follows 0022's precedent (appointment_note_author_name): resolving
-- names at read time shows "unknown" for everybody who has since left, and making provider-service call the
-- issuer to render a history row is a dependency in the wrong direction for a read that must not fail.

ALTER TABLE provider.practitioner
    ADD COLUMN IF NOT EXISTS created_by      text,
    ADD COLUMN IF NOT EXISTS updated_by      text,
    ADD COLUMN IF NOT EXISTS updated_by_name text;

CREATE TABLE IF NOT EXISTS provider.practitioner_history (
    history_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    practitioner_id uuid NOT NULL,
    tenant_id       text NOT NULL,
    operation       text NOT NULL,
    row_snapshot    jsonb NOT NULL,
    recorded_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_practitioner_history_id
    ON provider.practitioner_history (practitioner_id, history_id);

CREATE OR REPLACE FUNCTION provider.write_practitioner_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO provider.practitioner_history (practitioner_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.practitioner_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_practitioner_history ON provider.practitioner;
CREATE TRIGGER trg_practitioner_history AFTER INSERT OR UPDATE ON provider.practitioner
    FOR EACH ROW EXECUTE FUNCTION provider.write_practitioner_history();

-- ---- tenant RLS (0007's shape, applied to the new table) ------------------------------------------------

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT SELECT, INSERT ON provider.practitioner_history TO hbmp_app;

ALTER TABLE provider.practitioner_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE provider.practitioner_history FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_practitioner_history ON provider.practitioner_history;
-- Fail-CLOSED, matching 0007: an unset or empty app.tenant_id matches nothing.
CREATE POLICY rls_practitioner_history ON provider.practitioner_history
    USING (tenant_id = current_setting('app.tenant_id', true));

-- A tenant_id of '' belongs to no tenant: invisible to every real one, visible to any session binding an
-- empty GUC. New tables start with the constraint rather than acquiring it in a later backfill.
ALTER TABLE provider.practitioner_history
    DROP CONSTRAINT IF EXISTS ck_practitioner_history_tenant_not_blank;  -- migrate-compat: contract-ok (idempotency guard on a table created in THIS migration — there is no deployed reader to break)
ALTER TABLE provider.practitioner_history
    ADD CONSTRAINT ck_practitioner_history_tenant_not_blank CHECK (btrim(tenant_id) <> '');

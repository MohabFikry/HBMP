-- emr-service — 0025: the weekly availability rule becomes an ADMINISTERED record, and gains a daily cap.
-- ADDITIVE (expand phase).
--
-- WHY
-- ---
-- `emr.provider_availability` has been the weekly recurring rule since 0002, and nothing has ever been able
-- to read, correct or retire one. There is no GET, no PUT and no DELETE anywhere in the codebase. Its only
-- writer is `POST /appointment-slots`, which constructs a BRAND NEW rule row on every call
-- (Appointments.cs) — and there is no unique key stopping it.
--
-- Three consequences, all live before this migration:
--
--   • materializing the same clinic's Tuesdays twice leaves two identical rules, and n times leaves n. Every
--     one of them is a source slot generation will honour, so the duplicates are not inert.
--   • a rule can never be fixed. Changing a clinic's hours means leaving the old rule in place and deleting
--     the slots it already produced, by hand, in the database.
--   • the Roster & Availability screen opens by telling the coordinator that "the weekly pattern says when
--     the clinic normally runs" — a sentence about data no endpoint could fetch. The roster was
--     exceptions-only, which is why leave could be recorded and working hours could not.
--
-- THE DAILY CAP
-- -------------
-- `max_per_day` is the capacity control the platform had no way to express. Until now a clinic's capacity was
-- implicit in `slot_minutes × (end_time − start_time)`, which cannot say "Dr Hala takes twenty patients a day
-- however long the session runs" — and that is the sentence a clinic manager actually needs.
--
-- NULLABLE, and NULL means uncapped. Every rule that exists on deploy therefore keeps its present behaviour
-- exactly; the cap is something somebody chooses, never something a migration decides on their behalf.
--
-- It lives on the RULE, which makes it per practitioner per clinic per weekday. A doctor working Maadi
-- mornings and Dokki evenings holds two rules and two caps. That is deliberate: the cap is administered by
-- whoever runs the clinic, and a coordinator who reaches one branch must be able to set the cap that applies
-- there without touching another clinic's.

-- ---- the administered columns -------------------------------------------------------------------------
--
-- `tenant_id` is already here (0007 added it with the RLS policy). What is missing is everything that makes
-- a row administered rather than derived: who made it, when, whether it is still current.

ALTER TABLE emr.provider_availability
    ADD COLUMN IF NOT EXISTS max_per_day int,
    ADD COLUMN IF NOT EXISTS is_deleted  boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS row_version integer NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS created_at  timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS created_by  text,
    ADD COLUMN IF NOT EXISTS updated_at  timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN IF NOT EXISTS updated_by  text,
    -- The actor's NAME, not only their subject. Same reasoning as 0022's appointment_note_author_name: a
    -- timeline that resolves names at read time shows "unknown" for everybody who has since left, and making
    -- a scheduling read depend on the issuer to render a history row is a dependency in the wrong direction.
    ADD COLUMN IF NOT EXISTS updated_by_name text;

-- A zero cap is not "uncapped", it is "closed" — and a clinic that is closed is a roster exception, which
-- carries a reason and an impact preview. Allowing 0 here would be a second, silent way to shut a clinic.
ALTER TABLE emr.provider_availability
    DROP CONSTRAINT IF EXISTS ck_availability_max_per_day;  -- migrate-compat: contract-ok (idempotency guard; the constraint is (re)created on the next statement, so no window exists where it is absent for a deployed reader)
ALTER TABLE emr.provider_availability
    ADD CONSTRAINT ck_availability_max_per_day CHECK (max_per_day IS NULL OR max_per_day > 0);

-- ---- collapse the duplicates that already exist -------------------------------------------------------
--
-- Runs BEFORE the unique index, because the index cannot be created while they are there — and a migration
-- that fails halfway through on a real dataset is worse than one that states its policy.
--
-- POLICY: the most recently created rule per key wins and the rest are SOFT-deleted. Not hard-deleted: they
-- are what produced the slots that already exist, and an appointment booked into one of those slots must
-- remain explicable. `ctid` breaks the tie for rows created in the same transaction, so the choice is
-- deterministic rather than whatever order the planner returns.
WITH ranked AS (
    SELECT ctid,
           row_number() OVER (
               PARTITION BY tenant_id, provider_id, location_id,
                            coalesce(doctor_id, '00000000-0000-0000-0000-000000000000'::uuid),
                            coalesce(branch_id, '00000000-0000-0000-0000-000000000000'::uuid),
                            day_of_week
               ORDER BY created_at DESC, ctid DESC) AS rn
    FROM emr.provider_availability
    WHERE is_deleted = false
)
UPDATE emr.provider_availability a
   SET is_deleted = true,
       updated_at = now(),
       updated_by = 'migration:0025'
  FROM ranked r
 WHERE a.ctid = r.ctid AND r.rn > 1;

-- ---- one rule per practitioner, per clinic, per weekday -----------------------------------------------
--
-- The natural key, at last stated. `coalesce` on the two nullable columns because a NULL doctor (a clinic-wide
-- rule) and a NULL branch (an external provider location) are each ONE value, not "any value" — under plain
-- UNIQUE semantics NULLs never collide, so the duplicates this exists to prevent would slip straight through.
CREATE UNIQUE INDEX IF NOT EXISTS ux_availability_rule
    ON emr.provider_availability (
        tenant_id, provider_id, location_id,
        coalesce(doctor_id, '00000000-0000-0000-0000-000000000000'::uuid),
        coalesce(branch_id, '00000000-0000-0000-0000-000000000000'::uuid),
        day_of_week)
    WHERE is_deleted = false;

-- The read the roster screen makes: every live rule for this clinic, or for this doctor.
CREATE INDEX IF NOT EXISTS ix_availability_branch
    ON emr.provider_availability (tenant_id, branch_id, day_of_week) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_availability_doctor
    ON emr.provider_availability (tenant_id, doctor_id, day_of_week) WHERE is_deleted = false;

-- ---- history twin (append-only) -----------------------------------------------------------------------
--
-- The same shape and the same trigger as `emr.roster_exception_history` (0016) and `provider.provider_history`
-- (provider/0001). Changing a clinic's hours or its daily cap changes who can be seen and when, so "who
-- narrowed Tuesday to twelve patients, and when" has to survive the row being edited again.
--
-- This is NOT the audit trail and does not replace it. The audit chain is hash-linked, tamper-evident and
-- readable only by Security/Compliance/DPO. This is the OPERATIONAL record, readable by the people who run
-- the clinic, under the same branch reach as the rule itself. Both are written; neither substitutes for the
-- other.

CREATE TABLE IF NOT EXISTS emr.provider_availability_history (
    history_id      bigserial PRIMARY KEY,
    availability_id uuid NOT NULL,
    tenant_id       text NOT NULL,
    operation       text NOT NULL,
    row_snapshot    jsonb NOT NULL,
    recorded_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_availability_history_id
    ON emr.provider_availability_history (availability_id, history_id);

CREATE OR REPLACE FUNCTION emr.write_provider_availability_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO emr.provider_availability_history (availability_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.availability_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_provider_availability_history ON emr.provider_availability;
CREATE TRIGGER trg_provider_availability_history AFTER INSERT OR UPDATE ON emr.provider_availability
    FOR EACH ROW EXECUTE FUNCTION emr.write_provider_availability_history();

-- ---- tenant RLS on the new table (0007's shape) -------------------------------------------------------

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON emr.provider_availability_history TO hbmp_app;
GRANT USAGE, SELECT ON SEQUENCE emr.provider_availability_history_history_id_seq TO hbmp_app;

ALTER TABLE emr.provider_availability_history ENABLE ROW LEVEL SECURITY;
ALTER TABLE emr.provider_availability_history FORCE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_provider_availability_history ON emr.provider_availability_history;
CREATE POLICY rls_provider_availability_history ON emr.provider_availability_history
    USING (tenant_id = current_setting('app.tenant_id', true));

-- 24.5 — a tenant_id of '' belongs to no tenant: invisible to every real one, visible to any session that
-- binds an empty GUC. New tables start with the constraint rather than acquiring it in a later backfill.
ALTER TABLE emr.provider_availability_history
    DROP CONSTRAINT IF EXISTS ck_availability_history_tenant_not_blank;  -- migrate-compat: contract-ok (idempotency guard on a table created in THIS migration — there is no deployed reader to break)
ALTER TABLE emr.provider_availability_history
    ADD CONSTRAINT ck_availability_history_tenant_not_blank CHECK (btrim(tenant_id) <> '');

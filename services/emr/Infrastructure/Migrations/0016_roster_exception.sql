-- emr-service — 0016: the roster EXCEPTION layer (phase 25.4, design 42 §4). ADDITIVE.
--
-- WHY
-- ---
-- `emr.provider_availability` is a WEEKLY RECURRING RULE — day-of-week, start, end, slot minutes — and
-- nothing else. There is no way to say "Dr Hala is on leave next Tuesday" or "the Aswan clinic closes for
-- Eid". So today the only way to stop slots appearing is to DELETE the rule, which also erases the normal
-- pattern: the clinic loses its Tuesdays permanently to cover one Tuesday's absence, and somebody has to
-- remember to re-create it.
--
-- This adds the layer that was missing. Availability becomes:
--
--     recurring rule − exceptions ∩ active branch assignment ∩ valid licence ∩ practitioner Active
--
-- computed in EXACTLY ONE function (`SlotGeneration`). A second implementation is the bug, not an
-- optimisation — and the way that failure presents is a patient given an appointment with a doctor who is
-- on leave.
--
-- SUBTRACTIVE vs ADDITIVE
-- -----------------------
-- Leave / PublicHoliday / ClinicClosed REMOVE availability. AdHocClinic ADDS it — an extra Friday clinic is
-- the same kind of object (a dated, reasoned, audited deviation from the weekly pattern) and modelling it
-- anywhere else would mean two places to look when asking "why are there slots that day".

CREATE TABLE IF NOT EXISTS emr.roster_exception (
    exception_id    uuid PRIMARY KEY,
    tenant_id       text NOT NULL,

    -- AT LEAST ONE of branch/practitioner must be set, and they mean different scopes:
    --   branch only        — the clinic is shut (a public holiday, a burst pipe): everyone there.
    --   practitioner only  — this clinician is away, wherever they were due to work that day.
    --   both               — this clinician is away FROM THIS CLINIC only (covering another branch).
    -- Neither would be an exception that applies to nothing, which is a row that silently does nothing.
    branch_id       uuid,
    practitioner_id uuid,
    CONSTRAINT ck_roster_exception_target CHECK (branch_id IS NOT NULL OR practitioner_id IS NOT NULL),

    date_from       date NOT NULL,
    date_to         date NOT NULL,
    CONSTRAINT ck_roster_exception_range CHECK (date_to >= date_from),

    kind            varchar(16) NOT NULL
                    CHECK (kind IN ('Leave','PublicHoliday','ClinicClosed','AdHocClinic')),

    -- NULL start/end ⇒ WHOLE DAY. Both or neither: a half-open time window ("from 14:00, until unspecified")
    -- reads as a whole afternoon to one person and a data-entry slip to another.
    start_time      time,
    end_time        time,
    CONSTRAINT ck_roster_exception_times CHECK (
        (start_time IS NULL AND end_time IS NULL)
        OR (start_time IS NOT NULL AND end_time IS NOT NULL AND end_time > start_time)),

    -- MANDATORY. A cancelled clinic day is something a patient will ask about, and "no reason recorded" is
    -- not an answer anyone can give them. It is also what a coordinator reads six weeks later deciding
    -- whether the absence is repeating.
    reason          varchar(300) NOT NULL CHECK (btrim(reason) <> ''),

    -- An AdHocClinic needs a window to generate INTO; a subtractive kind may be a whole day.
    CONSTRAINT ck_roster_exception_adhoc_window CHECK (
        kind <> 'AdHocClinic' OR (start_time IS NOT NULL AND end_time IS NOT NULL)),

    is_deleted      boolean NOT NULL DEFAULT false,
    row_version     integer NOT NULL DEFAULT 0,
    created_at      timestamptz NOT NULL DEFAULT now(),
    created_by      text,
    updated_at      timestamptz NOT NULL DEFAULT now(),
    updated_by      text
);

-- The lookup slot generation makes: "every exception overlapping this date range, for this branch or this
-- practitioner". Two indexes rather than one composite, because the two targets are queried independently.
CREATE INDEX IF NOT EXISTS ix_roster_exception_branch
    ON emr.roster_exception (tenant_id, branch_id, date_from, date_to) WHERE is_deleted = false;
CREATE INDEX IF NOT EXISTS ix_roster_exception_practitioner
    ON emr.roster_exception (tenant_id, practitioner_id, date_from, date_to) WHERE is_deleted = false;

-- ---- history twin (append-only) ---------------------------------------------------------------------
--
-- Same shape as `appointment_history`. A roster change cancels people's clinic days, so "who closed Aswan
-- on the 12th, and when" has to survive the row being edited.

CREATE TABLE IF NOT EXISTS emr.roster_exception_history (
    history_id   bigserial PRIMARY KEY,
    exception_id uuid NOT NULL,
    tenant_id    text NOT NULL,
    operation    text NOT NULL,
    row_snapshot jsonb NOT NULL,
    recorded_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_roster_exception_history_id ON emr.roster_exception_history (exception_id);

CREATE OR REPLACE FUNCTION emr.write_roster_exception_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO emr.roster_exception_history (exception_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.exception_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_roster_exception_history ON emr.roster_exception;
CREATE TRIGGER trg_roster_exception_history AFTER INSERT OR UPDATE ON emr.roster_exception
    FOR EACH ROW EXECUTE FUNCTION emr.write_roster_exception_history();

-- ---- tenant RLS (0007's shape, applied to the two new tables) ---------------------------------------

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT SELECT, INSERT, UPDATE, DELETE ON emr.roster_exception, emr.roster_exception_history TO hbmp_app;
GRANT USAGE, SELECT ON SEQUENCE emr.roster_exception_history_history_id_seq TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['roster_exception','roster_exception_history']
    LOOP
        EXECUTE format('ALTER TABLE emr.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE emr.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON emr.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON emr.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

-- 24.5's finding: a tenant_id of '' belongs to no tenant — invisible to every real one, visible to any
-- session binding an empty GUC. New tables start with the constraint rather than acquiring it in a later
-- backfill, which is the only cheap moment to add it.
ALTER TABLE emr.roster_exception
    DROP CONSTRAINT IF EXISTS ck_roster_exception_tenant_not_blank;  -- migrate-compat: contract-ok (idempotency guard on a table created in THIS migration — there is no deployed reader to break)
ALTER TABLE emr.roster_exception
    ADD CONSTRAINT ck_roster_exception_tenant_not_blank CHECK (btrim(tenant_id) <> '');
ALTER TABLE emr.roster_exception_history
    DROP CONSTRAINT IF EXISTS ck_roster_exception_history_tenant_not_blank;  -- migrate-compat: contract-ok (idempotency guard on a table created in THIS migration — there is no deployed reader to break)
ALTER TABLE emr.roster_exception_history
    ADD CONSTRAINT ck_roster_exception_history_tenant_not_blank CHECK (btrim(tenant_id) <> '');

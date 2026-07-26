-- emr-service — 0007 tenant Row-Level Security (audit H1 / ADR-0011). See patient 0003 for rationale.
-- The clinical core (encounters, notes, diagnoses, vitals, allergies, med history) + the appointment/queue
-- tables had no tenant column. This adds tenant_id to all 13 tables (incl. the appointment_history twin),
-- backfills the sole Mersal tenant, updates the history trigger to carry tenant, and enforces RLS under
-- hbmp_app so a clinical row is tenant-isolated at the datastore. Additive + idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['appointment','appointment_slot','provider_availability','waitlist_entry',
                             'appointment_queue','encounter','queue_entry','emr_note','diagnosis','vital',
                             'allergy','medication_history','appointment_history']
    LOOP
        EXECUTE format(
            'ALTER TABLE emr.%I ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
    END LOOP;
END $$;

-- History trigger carries the tenant onto the append-only twin.
CREATE OR REPLACE FUNCTION emr.write_appointment_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO emr.appointment_history (appointment_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.appointment_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

GRANT USAGE ON SCHEMA emr TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA emr TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA emr GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['appointment','appointment_slot','provider_availability','waitlist_entry',
                             'appointment_queue','encounter','queue_entry','emr_note','diagnosis','vital',
                             'allergy','medication_history','appointment_history']
    LOOP
        EXECUTE format('ALTER TABLE emr.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE emr.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON emr.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON emr.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

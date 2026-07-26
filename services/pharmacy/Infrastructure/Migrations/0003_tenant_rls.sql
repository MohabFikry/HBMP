-- pharmacy-service — 0003 tenant Row-Level Security (audit H1 / ADR-0011). See patient 0003 for rationale.
-- Adds tenant_id to the prescription aggregate + dispense/referral/alert tables, backfills the sole Mersal
-- tenant, and enforces isolation under hbmp_app. The append-only dispense_event ledger + atomic idempotent
-- dispense invariants are unaffected (DEFAULT covers ADO inserts; concurrency tests run superuser).
-- Additive + idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['prescription','prescription_line','dispense_event','referral','prescription_alert']
    LOOP
        EXECUTE format(
            'ALTER TABLE pharmacy.%I ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
    END LOOP;
END $$;

GRANT USAGE ON SCHEMA pharmacy TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA pharmacy TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA pharmacy GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['prescription','prescription_line','dispense_event','referral','prescription_alert']
    LOOP
        EXECUTE format('ALTER TABLE pharmacy.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE pharmacy.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON pharmacy.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON pharmacy.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

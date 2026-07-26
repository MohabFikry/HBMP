-- case-service — 0002 tenant Row-Level Security (audit H1 / ADR-0011). See patient 0003 for rationale.
-- case_file already carried tenant_id (10.1); this adds it to the child tables and enables RLS across all
-- four so a coordination task / assignment / escalation is as tenant-isolated as its parent case. The schema
-- name "case" is a reserved word ⇒ quoted throughout. Additive + idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['case_assignment','coordination_task','escalation']
    LOOP
        EXECUTE format(
            'ALTER TABLE "case".%I ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
    END LOOP;
END $$;
-- Ensure case_file inserts without an explicit tenant still land a valid value.
ALTER TABLE "case".case_file ALTER COLUMN tenant_id SET DEFAULT '11111111-1111-1111-1111-111111111111';

GRANT USAGE ON SCHEMA "case" TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA "case" TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA "case" GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['case_file','case_assignment','coordination_task','escalation']
    LOOP
        EXECUTE format('ALTER TABLE "case".%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE "case".%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON "case".%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON "case".%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

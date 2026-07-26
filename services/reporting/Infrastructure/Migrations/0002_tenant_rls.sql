-- reporting-service — 0002 tenant Row-Level Security (audit H1 / ADR-0011). See patient 0003.
-- Every reporting fact/read-model table already carried tenant_id (the projector stamps it from the
-- de-identified event). This enables RLS on them all under hbmp_app so a cross-tenant read is impossible
-- at the datastore. processed_event (dedup ledger) is left RLS-free. Additive + idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['authorization_fact','code_count','encounter_fact','financial_fact',
                             'pending_authorization','report_job','utilization_fact']
    LOOP
        EXECUTE format('ALTER TABLE reporting.%I ALTER COLUMN tenant_id SET DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
    END LOOP;
END $$;

GRANT USAGE ON SCHEMA reporting TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA reporting TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA reporting GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['authorization_fact','code_count','encounter_fact','financial_fact',
                             'pending_authorization','report_job','utilization_fact']
    LOOP
        EXECUTE format('ALTER TABLE reporting.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE reporting.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON reporting.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON reporting.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

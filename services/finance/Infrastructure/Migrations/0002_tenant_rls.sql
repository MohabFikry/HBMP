-- finance-service — 0002 tenant Row-Level Security (audit H1 / ADR-0011). See patient 0003 for rationale.
-- utilization_fact / settlement / export_record already carried tenant_id (set in code); this adds it to
-- settlement_line, backfills the sole Mersal tenant, and enables RLS on all four. processed_event (a
-- consumer dedup ledger) is intentionally left RLS-free. Additive + idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

ALTER TABLE finance.settlement_line ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL
    DEFAULT '11111111-1111-1111-1111-111111111111';
-- Give the pre-existing tenant columns a default too, so any raw insert stays valid under RLS.
ALTER TABLE finance.utilization_fact ALTER COLUMN tenant_id SET DEFAULT '11111111-1111-1111-1111-111111111111';
ALTER TABLE finance.settlement       ALTER COLUMN tenant_id SET DEFAULT '11111111-1111-1111-1111-111111111111';
ALTER TABLE finance.export_record    ALTER COLUMN tenant_id SET DEFAULT '11111111-1111-1111-1111-111111111111';

GRANT USAGE ON SCHEMA finance TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA finance TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA finance GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['utilization_fact','settlement','settlement_line','export_record']
    LOOP
        EXECUTE format('ALTER TABLE finance.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE finance.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON finance.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON finance.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

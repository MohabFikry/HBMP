-- eligibility-service — 0002 tenant Row-Level Security (audit H1 / ADR-0011). See patient 0003.
-- Adds tenant_id to the read-model projections (member/coverage/snapshot), backfills the sole Mersal tenant,
-- and enables RLS under hbmp_app. NOTE: these tables are written by the BACKGROUND EventConsumer, which has
-- no HTTP principal — it binds the tenant GUC itself (EventConsumer.SoleTenantId) so the FORCE-RLS insert
-- check passes. processed_event (the consumer's dedup ledger) is intentionally left RLS-free. Idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['member_projection','coverage_projection','eligibility_snapshot']
    LOOP
        EXECUTE format(
            'ALTER TABLE eligibility.%I ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
    END LOOP;
END $$;

GRANT USAGE ON SCHEMA eligibility TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA eligibility TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA eligibility GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['member_projection','coverage_projection','eligibility_snapshot']
    LOOP
        EXECUTE format('ALTER TABLE eligibility.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE eligibility.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON eligibility.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON eligibility.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

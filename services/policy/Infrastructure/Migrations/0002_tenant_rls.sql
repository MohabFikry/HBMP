-- policy-service — 0002 tenant Row-Level Security (audit H1 / ADR-0011). See patient 0003 for rationale.
-- Adds tenant_id to policy/benefit_category/coverage/coverage_limit + the coverage_limit_history twin,
-- backfills the sole Mersal tenant, updates the history trigger to carry tenant onto the twin, and enforces
-- isolation under hbmp_app. Additive + idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['policy','benefit_category','coverage','coverage_limit','coverage_limit_history']
    LOOP
        EXECUTE format(
            'ALTER TABLE policy.%I ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
    END LOOP;
END $$;

-- History trigger carries the tenant onto the append-only twin (runs as the inserting role under RLS).
CREATE OR REPLACE FUNCTION policy.write_limit_history()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO policy.coverage_limit_history (coverage_limit_id, tenant_id, operation, row_snapshot)
    VALUES (NEW.coverage_limit_id, NEW.tenant_id, TG_OP, to_jsonb(NEW));
    RETURN NEW;
END $$;

GRANT USAGE ON SCHEMA policy TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA policy TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA policy GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['policy','benefit_category','coverage','coverage_limit','coverage_limit_history']
    LOOP
        EXECUTE format('ALTER TABLE policy.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE policy.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON policy.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON policy.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

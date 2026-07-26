-- approvals-service — 0003 tenant Row-Level Security (audit H1 / ADR-0011). See patient 0003.
-- Adds tenant_id to authorization + the append-only authorization_decision ledger, backfills the sole
-- tenant, and enables RLS on both under hbmp_app. CRITICAL: authorization_decision is insert-only — 0001
-- REVOKEs UPDATE/DELETE from hbmp_app and a trigger blocks mutation. The blanket grant below would restore
-- those privileges, so we re-REVOKE them immediately to preserve the append-only guarantee. Idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['authorization','authorization_decision']
    LOOP
        EXECUTE format(
            'ALTER TABLE approvals.%I ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
    END LOOP;
END $$;

GRANT USAGE ON SCHEMA approvals TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA approvals TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA approvals GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;
-- Restore the append-only guarantee the blanket grant would have undone (matches 0001).
REVOKE UPDATE, DELETE ON approvals.authorization_decision FROM hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['authorization','authorization_decision']
    LOOP
        EXECUTE format('ALTER TABLE approvals.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE approvals.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON approvals.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON approvals.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

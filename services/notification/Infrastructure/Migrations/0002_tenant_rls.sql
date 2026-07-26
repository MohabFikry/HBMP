-- notification-service — 0002 tenant Row-Level Security (audit H1 / ADR-0011). See patient 0003.
-- notification already carried tenant_id; this adds it to notification_template and enables RLS on both.
-- processed_event (consumer dedup ledger) is intentionally RLS-free. Additive + idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

ALTER TABLE notification.notification_template ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL
    DEFAULT '11111111-1111-1111-1111-111111111111';
ALTER TABLE notification.notification ALTER COLUMN tenant_id SET DEFAULT '11111111-1111-1111-1111-111111111111';

GRANT USAGE ON SCHEMA notification TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA notification TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA notification GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['notification','notification_template']
    LOOP
        EXECUTE format('ALTER TABLE notification.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE notification.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON notification.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON notification.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

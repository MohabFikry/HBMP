-- orders-service — 0006 tenant Row-Level Security (audit H1 / ADR-0011). See patient 0003 for rationale.
-- Adds tenant_id to the order aggregate + report-access tables, backfills the sole Mersal tenant, and
-- enforces isolation at the datastore under hbmp_app. The append-only order_fulfillment ledger and its
-- optimistic-concurrency consume path are unaffected: the column carries a DEFAULT so raw/ADO inserts stay
-- valid, and the consume invariants run under the migration/superuser role in tests (RLS bypassed there).
-- Additive + idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['investigation_order','order_line','order_fulfillment',
                             'report_access_request','report_access_grant']
    LOOP
        EXECUTE format(
            'ALTER TABLE orders.%I ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
    END LOOP;
END $$;

GRANT USAGE ON SCHEMA orders TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA orders TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA orders GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['investigation_order','order_line','order_fulfillment',
                             'report_access_request','report_access_grant']
    LOOP
        EXECUTE format('ALTER TABLE orders.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE orders.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON orders.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON orders.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

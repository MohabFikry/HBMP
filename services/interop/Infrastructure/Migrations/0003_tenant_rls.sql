-- interop-service — 0003 close the fail-OPEN policy and extend RLS to the integration tables
-- (audit R2 S2; ADR-0011). Additive + idempotent.
--
-- 0001 shipped the only fail-OPEN policy in the repo:
--     USING (tenant_id = current_setting('app.tenant_id', true)
--            OR current_setting('app.tenant_id', true) IS NULL
--            OR current_setting('app.tenant_id', true) = '')
-- and interop never bound the GUC, so `IS NULL` was ALWAYS true. The policy existed, was enabled, and
-- permitted every row to every connection — it read as isolation in review and enforced nothing at runtime.
-- That is strictly worse than no policy, because it made the gap invisible. The GUC binder lands in the same
-- commit (Infrastructure/DependencyInjection.cs + Api/Program.cs): closing the policy without the binder
-- would deny every row instead.
--
-- ALSO: 0002 added integration_partner and inbound_staging with NO tenant_id and NO RLS. inbound_staging
-- holds `body` — the raw partner payload, quarantined precisely because it has not been validated. That is
-- the least-trusted PHI-bearing text in the platform and it had no row-level boundary at all.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

-- ---------------------------------------------------------------- tenant columns
-- fhir_create.tenant_id was nullable. A fail-closed policy denies NULL (NULL = x is NULL, not true), so any
-- legacy unstamped ledger row would become permanently invisible and its Idempotency-Key would replay as a
-- fresh create — a duplicated native command downstream. Backfill to the sole tenant, then pin NOT NULL.
UPDATE interop.fhir_create SET tenant_id = '11111111-1111-1111-1111-111111111111' WHERE tenant_id IS NULL;
ALTER TABLE interop.fhir_create ALTER COLUMN tenant_id SET NOT NULL;  -- migrate-compat: contract-ok (the UPDATE on the line above backfills every NULL first, and the platform is single-tenant per ADR-0011 so there is one possible value; leaving it nullable is the actual hazard — a fail-closed RLS policy makes an unstamped row permanently invisible and replays its Idempotency-Key as a fresh create)

-- The partner registry is tenant configuration, and staging is tenant data. Single-tenant today (ADR-0011),
-- so the default backfills the sole Mersal tenant; partner_id stays the PK because a second tenant is a
-- migration, not a runtime case.
ALTER TABLE interop.integration_partner
    ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111';
ALTER TABLE interop.inbound_staging
    ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT '11111111-1111-1111-1111-111111111111';

CREATE INDEX IF NOT EXISTS ix_integration_partner_tenant ON interop.integration_partner (tenant_id);
CREATE INDEX IF NOT EXISTS ix_inbound_staging_tenant ON interop.inbound_staging (tenant_id, state);

GRANT USAGE ON SCHEMA interop TO hbmp_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA interop TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA interop GRANT SELECT, INSERT, UPDATE ON TABLES TO hbmp_app;

-- ---------------------------------------------------------------- fail-closed policies
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['fhir_create','integration_partner','inbound_staging']
    LOOP
        EXECUTE format('ALTER TABLE interop.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE interop.%I FORCE ROW LEVEL SECURITY', t);
        -- Drop BOTH the 0001 name and the fleet name so re-running cannot leave the permissive policy in
        -- place beside the strict one — RLS policies are OR-ed, so one survivor re-opens the whole table.
        EXECUTE format('DROP POLICY IF EXISTS %1$s_tenant_isolation ON interop.%1$s', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_tenant_isolation ON interop.%I', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON interop.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON interop.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))
                WITH CHECK (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

-- identity-service — 0002 runtime-role grants (ADR-0011 / ADR-0015). The identity store is the platform's
-- authentication authority: the issuer looks a user up BY USERNAME to *discover* their tenant before any
-- request-scoped tenant context exists. Tenant-scoped RLS on identity.user would therefore break login
-- (there is no app.tenant_id set yet). So — like the RLS-free consumer-dedup ledgers (finance 0002) — the
-- identity core + scope/role_scope tables are deliberately NOT tenant-RLS: tenant_id here is a CLAIM SOURCE,
-- not a row filter, and the schema is reachable only by identity-service under the hbmp_app grant.
-- Isolation of who-can-see-whom is enforced downstream by every OTHER service's tenant RLS on its own data.
-- Additive + idempotent.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT USAGE ON SCHEMA identity TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity TO hbmp_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA identity TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA identity GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA identity GRANT USAGE, SELECT ON SEQUENCES TO hbmp_app;

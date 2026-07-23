-- provider-service — 0004 application DB role. RLS (0003) is only a real guarantee when the app connects
-- as a NON-superuser, NON-bypassrls role: a superuser (or a BYPASSRLS role) silently ignores every policy.
-- This migration provisions that role idempotently and grants it DML on the provider schema. The password
-- is set OUT OF BAND by ops (secrets-managed) — never commit it:  ALTER ROLE hbmp_app PASSWORD '…';
-- The service's ConnectionStrings__Provider must use hbmp_app (not the owner/superuser) in every env.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT USAGE ON SCHEMA provider TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA provider TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA provider GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

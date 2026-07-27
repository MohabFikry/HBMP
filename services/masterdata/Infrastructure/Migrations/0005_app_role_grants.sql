-- masterdata-service — 0005 runtime-role grants so the service can drop the superuser connection
-- (audit R2 X6 class). Additive + idempotent.
--
-- masterdata is the platform's reference catalogue: ICD-10, CPT, LOINC, ATC, allergens, examination types.
-- It is deliberately tenant-FREE — a diagnosis code means the same thing for every tenant — so there is no
-- tenant RLS to add here and none is wanted. What there WAS is a superuser connection string, which is a
-- standing invitation for a SQL-injection or a compromised container to reach every other schema in the
-- database, including audit and identity. Least privilege is the whole point even where isolation is not.
--
-- 0003 and 0004 granted hbmp_app on examination_type and the outbox only; the ICD/CPT/LOINC/ATC tables from
-- 0001 were never granted, so flipping the connection string without this would 42501 on every code lookup.

DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

GRANT USAGE ON SCHEMA masterdata TO hbmp_app;
GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA masterdata TO hbmp_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA masterdata TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA masterdata GRANT SELECT, INSERT, UPDATE ON TABLES TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA masterdata GRANT USAGE, SELECT ON SEQUENCES TO hbmp_app;

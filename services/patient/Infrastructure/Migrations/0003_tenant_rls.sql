-- patient-service — 0003 tenant Row-Level Security (audit H1 / ADR-0011).
-- The patient schema was single-tenant by omission (no tenant column). This retrofit adds a tenant_id to
-- every PHI table, backfills the sole Mersal tenant, and enforces isolation at the datastore via RLS so a
-- bug in an application predicate can never return another tenant's beneficiaries. Runtime connects as the
-- NOBYPASSRLS role hbmp_app; migrations run as the owner (hbmp). Additive + idempotent.
--
-- tenant_id is text to match the JWT `tenant_id` claim (a UUID string) and the provider-service precedent.
-- NOT NULL DEFAULT the sole tenant so existing rows backfill and any non-EF insert stays valid; the
-- TenantStampingInterceptor sets it from the principal on EF inserts (= the RLS GUC), so the policy's insert
-- check passes. When a second tenant is onboarded the default is dropped and every insert sources it live.

-- 1) Application DB role (idempotent; password set out of band by ops — never in git).
DO $$ BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        CREATE ROLE hbmp_app LOGIN NOSUPERUSER NOBYPASSRLS NOCREATEDB NOCREATEROLE;
    END IF;
END $$;

-- 2) tenant_id column on every PHI table (base + history twin).
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beneficiary','beneficiary_identifier','contact','family_group',
                             'dependent_link','registration','beneficiary_history']
    LOOP
        EXECUTE format(
            'ALTER TABLE patient.%I ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT ''11111111-1111-1111-1111-111111111111''', t);
    END LOOP;
END $$;

-- 3) History trigger must carry the tenant onto the append-only twin (runs as the inserting role under RLS).
CREATE OR REPLACE FUNCTION patient.write_beneficiary_history()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE op text;
BEGIN
    op := CASE
        WHEN TG_OP = 'INSERT' THEN 'INSERT'
        WHEN TG_OP = 'UPDATE' AND NEW.is_deleted AND NOT OLD.is_deleted THEN 'SOFT_DELETE'
        ELSE 'UPDATE' END;
    INSERT INTO patient.beneficiary_history (beneficiary_id, tenant_id, operation, row_snapshot, changed_by)
    VALUES (NEW.beneficiary_id, NEW.tenant_id, op, to_jsonb(NEW), NEW.updated_by);
    RETURN NEW;
END $$;

-- 4) Grants for the runtime role.
GRANT USAGE ON SCHEMA patient TO hbmp_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA patient TO hbmp_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA patient GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO hbmp_app;

-- 5) Enable + FORCE RLS with a tenant policy on every PHI table. FORCE so even the table owner is subject;
--    the runtime role hbmp_app is NOBYPASSRLS, the migration role hbmp is superuser and bypasses.
DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['beneficiary','beneficiary_identifier','contact','family_group',
                             'dependent_link','registration','beneficiary_history']
    LOOP
        EXECUTE format('ALTER TABLE patient.%I ENABLE ROW LEVEL SECURITY', t);
        EXECUTE format('ALTER TABLE patient.%I FORCE ROW LEVEL SECURITY', t);
        EXECUTE format('DROP POLICY IF EXISTS rls_%1$s ON patient.%1$s', t);
        EXECUTE format($p$
            CREATE POLICY rls_%1$s ON patient.%1$s
                USING (tenant_id = current_setting('app.tenant_id', true))$p$, t);
    END LOOP;
END $$;

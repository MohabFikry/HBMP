-- interop-service — Phase 13.1 FHIR R4 façade. The façade owns NO clinical data (17-api-specifications §12:
-- mapping is at the adapter layer; internal relational storage stays the source of truth). The only relational
-- state here is a create-idempotency ledger so a replayed FHIR `If-None-Exist` / `Idempotency-Key` returns the
-- resource created the first time and never issues a second native command downstream.
--
-- RLS: the ledger is tenant-tagged and runs as the NOBYPASSRLS hbmp_app role (16.4). No PHI is stored — only the
-- id of the resource created in the OWNING service, the tenant, and the dedupe key.

CREATE SCHEMA IF NOT EXISTS interop;

CREATE TABLE IF NOT EXISTS interop.fhir_create (
    dedupe_key          text PRIMARY KEY,           -- "{resourceType}:{tenant}:{ifNoneExist|idempotencyKey}"
    resource_type       text NOT NULL,
    created_resource_id text,                        -- id assigned by the OWNING service (not owned here)
    tenant_id           text,
    status_code         int  NOT NULL,
    created_at          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_fhir_create_tenant ON interop.fhir_create (tenant_id, resource_type);

ALTER TABLE interop.fhir_create ENABLE ROW LEVEL SECURITY;
DO $$ BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON interop.fhir_create TO hbmp_app;
        -- Tenant isolation (defense in depth), permissive when the app.tenant_id GUC is unset — mirroring the
        -- fleet RLS convention in admin 0001 (16.4) so migrations/admin connections still function.
        DROP POLICY IF EXISTS fhir_create_tenant_isolation ON interop.fhir_create;
        CREATE POLICY fhir_create_tenant_isolation ON interop.fhir_create
            USING (tenant_id = current_setting('app.tenant_id', true)
                   OR current_setting('app.tenant_id', true) IS NULL
                   OR current_setting('app.tenant_id', true) = '')
            WITH CHECK (tenant_id = current_setting('app.tenant_id', true)
                   OR current_setting('app.tenant_id', true) IS NULL
                   OR current_setting('app.tenant_id', true) = '');
    END IF;
END $$;

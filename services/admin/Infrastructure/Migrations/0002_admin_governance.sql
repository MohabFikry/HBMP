-- admin-service — Phase 8b.2 (Master-data / template / config governance, 07-functional-requirements §12
-- FR-MDM-007/008/009 + FR-NOT-005). Effective-dated version tables so a historical order/prescription resolves the
-- version in force at ITS time (append a new version, never mutate history). Every change is audited by the service.

-- Effective-dated master-data versions (one row per code version across all governed code systems).
CREATE TABLE IF NOT EXISTS admin.master_data_version (
    version_id     uuid PRIMARY KEY,
    system         text NOT NULL CHECK (system IN
                     ('Icd10','Cpt','Loinc','Atc','Drug','DrugInteraction','Allergen','Formulary')),
    code           text NOT NULL,
    version_no     int  NOT NULL,
    attributes     jsonb NOT NULL DEFAULT '{}'::jsonb,
    retired        boolean NOT NULL DEFAULT false,
    effective_from timestamptz NOT NULL DEFAULT now(),
    effective_to   timestamptz,          -- null = currently in force
    changed_by     text NOT NULL,
    rationale      text NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now(),
    CHECK (effective_to IS NULL OR effective_to > effective_from)
);
CREATE INDEX IF NOT EXISTS ix_mdv_code ON admin.master_data_version (system, code);
CREATE UNIQUE INDEX IF NOT EXISTS ux_mdv_version ON admin.master_data_version (system, code, version_no);

-- Bilingual, effective-dated notification templates (AR/EN parity + PHI-safe linter enforced in the service).
CREATE TABLE IF NOT EXISTS admin.notification_template_version (
    template_version_id uuid PRIMARY KEY,
    tenant_id      text NOT NULL,
    template_key   text NOT NULL,
    channel        text NOT NULL,
    subject_en     text NOT NULL DEFAULT '',
    subject_ar     text NOT NULL DEFAULT '',
    body_en        text NOT NULL,
    body_ar        text NOT NULL,
    version_no     int  NOT NULL,
    effective_from timestamptz NOT NULL DEFAULT now(),
    effective_to   timestamptz,
    changed_by     text NOT NULL,
    created_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_ntv_key ON admin.notification_template_version (tenant_id, template_key, channel);
CREATE UNIQUE INDEX IF NOT EXISTS ux_ntv_version ON admin.notification_template_version (tenant_id, template_key, channel, version_no);

-- Typed, effective-dated system configuration (feature flags / thresholds / lead-times). tenant_id = '*' is platform-level.
CREATE TABLE IF NOT EXISTS admin.system_config (
    config_id      uuid PRIMARY KEY,
    tenant_id      text NOT NULL,
    key            text NOT NULL,
    value_type     text NOT NULL CHECK (value_type IN ('Text','Whole','Number','Boolean','Duration')),
    value          text NOT NULL,
    version_no     int  NOT NULL,
    effective_from timestamptz NOT NULL DEFAULT now(),
    effective_to   timestamptz,
    updated_by     text NOT NULL,
    updated_at     timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_sysconfig_key ON admin.system_config (tenant_id, key);
CREATE UNIQUE INDEX IF NOT EXISTS ux_sysconfig_version ON admin.system_config (tenant_id, key, version_no);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON admin.master_data_version TO hbmp_app;
        GRANT SELECT, INSERT, UPDATE ON admin.notification_template_version TO hbmp_app;
        GRANT SELECT, INSERT, UPDATE ON admin.system_config TO hbmp_app;
        -- effective-dated versions are append + close-prior (UPDATE effective_to); no DELETE (auditable history).
    END IF;
END $$;

-- Tenant-scoped governance tables are RLS-isolated (master_data_version is a global reference surface → no RLS).
ALTER TABLE admin.notification_template_version ENABLE ROW LEVEL SECURITY;
ALTER TABLE admin.system_config ENABLE ROW LEVEL SECURITY;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['notification_template_version','system_config']
    LOOP
        EXECUTE format($f$
            DROP POLICY IF EXISTS rls_tenant_isolation ON admin.%I;
            CREATE POLICY rls_tenant_isolation ON admin.%I
                USING (tenant_id = current_setting('app.tenant_id', true)
                       OR tenant_id = '*'
                       OR current_setting('app.tenant_id', true) IS NULL
                       OR current_setting('app.tenant_id', true) = '')
                WITH CHECK (tenant_id = current_setting('app.tenant_id', true)
                       OR tenant_id = '*'
                       OR current_setting('app.tenant_id', true) IS NULL
                       OR current_setting('app.tenant_id', true) = '');
        $f$, t, t);
    END LOOP;
END $$;

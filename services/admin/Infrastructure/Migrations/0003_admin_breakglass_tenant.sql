-- admin-service — Phase 8b.3 (Tenant/provider governance, break-glass administration, dashboards;
-- 07-functional-requirements FR-IAM-008/009, 18-security-model §11, 19-audit-strategy §7). Break-glass grant
-- lifecycle + per-access log (loud high-severity audit, dashboard for post-hoc review) + tenant registry. No
-- DELETE grant anywhere (auditable history); tenant-scoped tables RLS-isolated.

-- Platform tenants (Mersal = tenant 0; future orgs/donors). Managed by Super Admin.
CREATE TABLE IF NOT EXISTS admin.tenant (
    tenant_id  text PRIMARY KEY,
    name       text NOT NULL,
    active     boolean NOT NULL DEFAULT true,
    created_by text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);

-- Break-glass grants (request → dual-control approve → step-up activate → scoped time-box → expire/revoke).
CREATE TABLE IF NOT EXISTS admin.break_glass_grant (
    grant_id              uuid PRIMARY KEY,
    tenant_id             text NOT NULL,
    requester_user_id     text NOT NULL,
    reason_code           text NOT NULL,
    justification         text NOT NULL,
    scoped_resource_types jsonb NOT NULL DEFAULT '[]'::jsonb,
    scoped_resource_ids   jsonb NOT NULL DEFAULT '[]'::jsonb,
    window_minutes        int  NOT NULL DEFAULT 60,
    status                text NOT NULL DEFAULT 'Requested'
        CHECK (status IN ('Requested','Approved','Active','Rejected','Expired','Revoked')),
    approver_user_id      text,
    approved_at           timestamptz,
    reject_reason         text,
    step_up_satisfied     boolean NOT NULL DEFAULT false,
    activated_at          timestamptz,
    not_before            timestamptz,
    expires_at            timestamptz,
    revoked_by            text,
    revoked_at            timestamptz,
    requested_at          timestamptz NOT NULL DEFAULT now(),
    post_review_done      boolean NOT NULL DEFAULT false,
    -- dual control: an approver, when set, must differ from the requester (defense in depth for the handler check).
    CHECK (approver_user_id IS NULL OR approver_user_id <> requester_user_id)
);
CREATE INDEX IF NOT EXISTS ix_bg_grant_status ON admin.break_glass_grant (tenant_id, status);
CREATE INDEX IF NOT EXISTS ix_bg_grant_requester ON admin.break_glass_grant (requester_user_id);

-- Every access under an active grant — high-severity, surfaced for post-hoc review.
CREATE TABLE IF NOT EXISTS admin.break_glass_access (
    access_id     uuid PRIMARY KEY,
    grant_id      uuid NOT NULL REFERENCES admin.break_glass_grant(grant_id),
    tenant_id     text NOT NULL,
    actor_user_id text NOT NULL,
    resource_type text NOT NULL,
    resource_id   text,
    action        text NOT NULL,
    within_scope  boolean NOT NULL,
    accessed_at   timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_bg_access_grant ON admin.break_glass_access (grant_id);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON admin.tenant TO hbmp_app;
        GRANT SELECT, INSERT, UPDATE ON admin.break_glass_grant TO hbmp_app;
        GRANT SELECT, INSERT ON admin.break_glass_access TO hbmp_app;  -- access log is append-only
    END IF;
END $$;

ALTER TABLE admin.break_glass_grant  ENABLE ROW LEVEL SECURITY;
ALTER TABLE admin.break_glass_access ENABLE ROW LEVEL SECURITY;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['break_glass_grant','break_glass_access']
    LOOP
        EXECUTE format($f$
            DROP POLICY IF EXISTS rls_tenant_isolation ON admin.%I;
            CREATE POLICY rls_tenant_isolation ON admin.%I
                USING (tenant_id = current_setting('app.tenant_id', true)
                       OR current_setting('app.tenant_id', true) IS NULL
                       OR current_setting('app.tenant_id', true) = '')
                WITH CHECK (tenant_id = current_setting('app.tenant_id', true)
                       OR current_setting('app.tenant_id', true) IS NULL
                       OR current_setting('app.tenant_id', true) = '');
        $f$, t, t);
    END LOOP;
END $$;

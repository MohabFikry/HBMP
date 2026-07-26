-- admin-service — 0004 staff↔branch assignment (phase 14.2, design 37 §2.2). ADDITIVE / backward-compatible.
-- Branch assignment is an identity/administration concern, so it lives in the admin schema next to role
-- bindings. Soft-lifecycle: a revoke stamps status/metadata (no DELETE — auditable history). Tenant-scoped
-- and RLS-isolated under the NOBYPASSRLS hbmp_app role, matching 0001.

CREATE TABLE IF NOT EXISTS admin.user_branch_assignment (
    assignment_id   uuid PRIMARY KEY,
    tenant_id       text        NOT NULL,
    subject_user_id text        NOT NULL,   -- logical FK to identity (value, not a cross-service FK)
    branch_id       uuid        NOT NULL,   -- logical FK to provider.branch (value, not a cross-service FK)
    assignment_type varchar(10) NOT NULL CHECK (assignment_type IN ('Home','Additional')),
    valid_from      date        NOT NULL,
    valid_to        date,
    status          varchar(10) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Revoked')),
    created_by      text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    revoked_by      text,
    revoked_at      timestamptz
);

-- THE INVARIANT: exactly one active Home branch per user (design 37 §2.2).
CREATE UNIQUE INDEX IF NOT EXISTS ux_user_home_branch
    ON admin.user_branch_assignment (tenant_id, subject_user_id)
    WHERE assignment_type = 'Home' AND status = 'Active';

CREATE INDEX IF NOT EXISTS ix_uba_subject ON admin.user_branch_assignment (tenant_id, subject_user_id, status);
CREATE INDEX IF NOT EXISTS ix_uba_branch  ON admin.user_branch_assignment (branch_id);

-- App-role grants (no DELETE — soft-lifecycle, like the rest of the admin schema).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON admin.user_branch_assignment TO hbmp_app;
    END IF;
END $$;

-- Tenant-isolation RLS, matching 0001's pattern (dormant under superuser, enforced under hbmp_app).
ALTER TABLE admin.user_branch_assignment ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_tenant_isolation ON admin.user_branch_assignment;
CREATE POLICY rls_tenant_isolation ON admin.user_branch_assignment
    USING (tenant_id = current_setting('app.tenant_id', true)
           OR current_setting('app.tenant_id', true) IS NULL
           OR current_setting('app.tenant_id', true) = '')
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)
           OR current_setting('app.tenant_id', true) IS NULL
           OR current_setting('app.tenant_id', true) = '');

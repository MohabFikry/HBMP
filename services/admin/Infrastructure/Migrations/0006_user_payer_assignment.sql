-- admin-service — 0006 user↔payer restriction (phase 19.5, design 38 §6). ADDITIVE / backward-compatible.
--
-- Payer scope restricts a user to one payer's book of business. It sits next to user_branch_assignment and
-- role_binding because "who may this person act for" is one question, and an entitlement the phase-16 access
-- review cannot enumerate is an entitlement nobody revokes.
--
-- NOTE THE DIRECTION OF THE DEFAULT: no row means UNRESTRICTED. The alternative — everyone sees nothing until
-- granted — would have required assigning every existing officer to every payer on the day this shipped, and a
-- grant that must be given to everyone stops being read as a grant. Restricting is the deliberate act.
--
-- payer_id is a logical reference to policy.payer (a value, not a cross-schema FK), exactly as branch_id
-- references provider.branch in 0004.

CREATE TABLE IF NOT EXISTS admin.user_payer_assignment (
    assignment_id   uuid PRIMARY KEY,
    tenant_id       text        NOT NULL,
    subject_user_id text        NOT NULL,   -- logical FK to identity (value, not a cross-service FK)
    payer_id        uuid        NOT NULL,   -- logical FK to policy.payer (value, not a cross-service FK)
    valid_from      date        NOT NULL,
    valid_to        date,
    status          varchar(10) NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Revoked')),
    created_by      text,
    created_at      timestamptz NOT NULL DEFAULT now(),
    revoked_by      text,
    revoked_at      timestamptz
);

-- One active restriction per (user, payer). Re-granting after a revoke is allowed; two live copies of the same
-- grant are not, because revoking the one an admin can see would leave the other quietly in force.
CREATE UNIQUE INDEX IF NOT EXISTS ux_user_payer_active
    ON admin.user_payer_assignment (tenant_id, subject_user_id, payer_id)
    WHERE status = 'Active';

CREATE INDEX IF NOT EXISTS ix_upa_subject ON admin.user_payer_assignment (tenant_id, subject_user_id, status);
CREATE INDEX IF NOT EXISTS ix_upa_payer   ON admin.user_payer_assignment (payer_id);

-- App-role grants (no DELETE — soft-lifecycle, like the rest of the admin schema).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT SELECT, INSERT, UPDATE ON admin.user_payer_assignment TO hbmp_app;
    END IF;
END $$;

-- Tenant-isolation RLS, matching 0004's pattern (dormant under superuser, enforced under hbmp_app).
ALTER TABLE admin.user_payer_assignment ENABLE ROW LEVEL SECURITY;
DROP POLICY IF EXISTS rls_tenant_isolation ON admin.user_payer_assignment;
CREATE POLICY rls_tenant_isolation ON admin.user_payer_assignment
    USING (tenant_id = current_setting('app.tenant_id', true)
           OR current_setting('app.tenant_id', true) IS NULL
           OR current_setting('app.tenant_id', true) = '')
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true)
           OR current_setting('app.tenant_id', true) IS NULL
           OR current_setting('app.tenant_id', true) = '');

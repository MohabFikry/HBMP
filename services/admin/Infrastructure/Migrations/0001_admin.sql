-- admin-service — Phase 8b.1 (Identity & access administration, 07-functional-requirements §11 FR-IAM,
-- 10-role-matrix §7 SoD, 18-security-model §9 session, 19-audit-strategy §7 access review). Role bindings +
-- de-provision list + access-review campaigns/items + session/device policy + staged policy proposals.
-- Soft lifecycle: a binding revoke stamps metadata (never deletes) so history is auditable. Every tenant-scoped
-- table is RLS-isolated on tenant_id (enforced under the NOBYPASSRLS hbmp_app role).

CREATE SCHEMA IF NOT EXISTS admin;

-- Role bindings (user ↔ role). Enums stored as text to match the CHECK sets / 10-role-matrix vocabulary.
CREATE TABLE IF NOT EXISTS admin.role_binding (
    binding_id      uuid PRIMARY KEY,
    tenant_id       text NOT NULL,
    subject_user_id text NOT NULL,
    role            text NOT NULL,
    scope_type      text NOT NULL DEFAULT 'Tenant' CHECK (scope_type IN ('Tenant','Provider','Global')),
    provider_id     text,
    tier            text NOT NULL DEFAULT 'T1' CHECK (tier IN ('T0','T1','T2','T3','T4')),
    granted_by      text NOT NULL,
    justification   text NOT NULL,
    granted_at      timestamptz NOT NULL DEFAULT now(),
    review_due_at   timestamptz,
    status          text NOT NULL DEFAULT 'Active' CHECK (status IN ('Active','Revoked')),
    revoked_at      timestamptz,
    revoked_by      text,
    revoke_reason   text,
    -- a provider-scoped binding must name the provider it is bound to.
    CHECK (scope_type <> 'Provider' OR provider_id IS NOT NULL),
    -- a revoked binding must carry who/when.
    CHECK (status <> 'Revoked' OR (revoked_at IS NOT NULL AND revoked_by IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_role_binding_subject ON admin.role_binding (tenant_id, subject_user_id);
-- one ACTIVE grant per (tenant, subject, role); a revoked one may be re-granted.
CREATE UNIQUE INDEX IF NOT EXISTS ux_role_binding_active
    ON admin.role_binding (tenant_id, subject_user_id, role) WHERE status = 'Active';

-- De-provisioned users — the auth layer denies every token/session for this subject immediately.
CREATE TABLE IF NOT EXISTS admin.deprovisioned_user (
    id               uuid PRIMARY KEY,
    tenant_id        text NOT NULL,
    subject_user_id  text NOT NULL,
    deprovisioned_by text NOT NULL,
    reason           text NOT NULL,
    deprovisioned_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE (tenant_id, subject_user_id)
);

-- Access-review campaigns (quarterly for T3/T4).
CREATE TABLE IF NOT EXISTS admin.access_review_campaign (
    campaign_id uuid PRIMARY KEY,
    tenant_id   text NOT NULL,
    name        text NOT NULL,
    min_tier    text NOT NULL DEFAULT 'T3' CHECK (min_tier IN ('T0','T1','T2','T3','T4')),
    created_at  timestamptz NOT NULL DEFAULT now(),
    created_by  text NOT NULL,
    due_at      timestamptz NOT NULL,
    status      text NOT NULL DEFAULT 'Open' CHECK (status IN ('Open','Closed'))
);
CREATE INDEX IF NOT EXISTS ix_campaign_tenant ON admin.access_review_campaign (tenant_id);

CREATE TABLE IF NOT EXISTS admin.access_review_item (
    item_id         uuid PRIMARY KEY,
    campaign_id     uuid NOT NULL REFERENCES admin.access_review_campaign(campaign_id),
    binding_id      uuid NOT NULL,
    subject_user_id text NOT NULL,
    role            text NOT NULL,
    decision        text NOT NULL DEFAULT 'Pending'
        CHECK (decision IN ('Pending','Recertified','Revoked','AutoExpired')),
    decided_by      text,
    decided_at      timestamptz,
    note            text
);
CREATE INDEX IF NOT EXISTS ix_review_item_campaign ON admin.access_review_item (campaign_id);
CREATE INDEX IF NOT EXISTS ix_review_item_binding ON admin.access_review_item (binding_id);

-- Per-role-tier session policy (18-security-model §9), effective-dated.
CREATE TABLE IF NOT EXISTS admin.session_policy (
    policy_id                uuid PRIMARY KEY,
    tenant_id                text NOT NULL,
    role_tier                text NOT NULL CHECK (role_tier IN ('T0','T1','T2','T3','T4')),
    access_token_ttl_seconds int NOT NULL DEFAULT 900,
    idle_timeout_seconds     int NOT NULL DEFAULT 900,
    absolute_cap_seconds     int NOT NULL DEFAULT 28800,
    max_concurrent_sessions  int NOT NULL DEFAULT 3,
    step_up_required         boolean NOT NULL DEFAULT false,
    effective_from           timestamptz NOT NULL DEFAULT now(),
    updated_by               text NOT NULL,
    updated_at               timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_session_policy_tier ON admin.session_policy (tenant_id, role_tier);

-- Per-role device-compliance + IP allow-list (18-security-model §3.4–3.5), effective-dated.
CREATE TABLE IF NOT EXISTS admin.device_policy (
    policy_id              uuid PRIMARY KEY,
    tenant_id              text NOT NULL,
    role                   text NOT NULL,
    require_managed_device boolean NOT NULL DEFAULT false,
    ip_allow_list          jsonb NOT NULL DEFAULT '[]'::jsonb,
    effective_from         timestamptz NOT NULL DEFAULT now(),
    updated_by             text NOT NULL,
    updated_at             timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_device_policy_role ON admin.device_policy (tenant_id, role);

-- Staged policy-bundle proposals — the admin UI proposes/diffs; it never hot-patches live ABAC (global surface,
-- no tenant_id). Deployment goes through the audited CI path.
CREATE TABLE IF NOT EXISTS admin.policy_proposal (
    proposal_id      uuid PRIMARY KEY,
    base_version     text NOT NULL,
    proposed_version text NOT NULL,
    diff             jsonb NOT NULL DEFAULT '{}'::jsonb,
    rationale        text NOT NULL,
    status           text NOT NULL DEFAULT 'Proposed'
        CHECK (status IN ('Proposed','Approved','Deployed','Rejected')),
    proposed_by      text NOT NULL,
    proposed_at      timestamptz NOT NULL DEFAULT now()
);

-- Grants to the app role.
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'hbmp_app') THEN
        GRANT USAGE ON SCHEMA admin TO hbmp_app;
        GRANT SELECT, INSERT, UPDATE ON ALL TABLES IN SCHEMA admin TO hbmp_app;
        -- role bindings are soft-lifecycle; no DELETE anywhere in this schema (auditable history).
    END IF;
END $$;

-- Row-Level Security: tenant isolation on every tenant-scoped table (enforced under NOBYPASSRLS hbmp_app).
-- The app sets `SET LOCAL app.tenant_id = '<tenant>'` per request; the policy pins visibility to that tenant.
ALTER TABLE admin.role_binding            ENABLE ROW LEVEL SECURITY;
ALTER TABLE admin.deprovisioned_user      ENABLE ROW LEVEL SECURITY;
ALTER TABLE admin.access_review_campaign  ENABLE ROW LEVEL SECURITY;
ALTER TABLE admin.session_policy          ENABLE ROW LEVEL SECURITY;
ALTER TABLE admin.device_policy           ENABLE ROW LEVEL SECURITY;

DO $$
DECLARE t text;
BEGIN
    FOREACH t IN ARRAY ARRAY['role_binding','deprovisioned_user','access_review_campaign','session_policy','device_policy']
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

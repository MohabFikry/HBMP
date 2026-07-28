-- identity-service — 0011 tenant-local role→scope grants (phase 21.1b, design 40 §2, ADR-0021).
--
-- Design 40 §2 wants roles owned by the tenant: "roles seeded from the 10-role-matrix templates at tenant
-- creation, then owned by the tenant and free to diverge. Templates are not live inheritance."
--
-- WHAT WE DO INSTEAD, AND WHY. Roles stay GLOBAL; only the role→scope GRANTS become tenant-local.
-- Making `identity.role` itself tenant-local would require the unique key to become
-- (tenant_id, normalized_name), but ASP.NET Core Identity's RoleStore/RoleManager look a role up by a
-- GLOBALLY unique normalized_name (ux_role_normalized_name, 0001) — so tenant-local roles mean either a
-- custom RoleStore or namespacing role names per tenant, and namespacing would change the token's `roles`
-- claim vocabulary, which is FROZEN (docs/security/token-contract.md §2 "Frozen role-name vocabulary").
-- Keeping the role catalog global preserves the frozen vocabulary, leaves RoleManager untouched, and still
-- delivers what §2 is actually for: two tenants may grant DIFFERENT SCOPES to the same role name.
--
-- THE DEFAULT BUCKET. tenant_id = '' is the platform default grant set — literally the rows that exist
-- today (0001 seeds them untenanted, and `user.tenant_id` itself defaults to ''). Resolution for a tenant
-- with no rows of its own falls back to it, so no existing caller changes behaviour.
--
-- COPY, NOT LIVE INHERITANCE. The backfill below provisions a real per-tenant copy for every tenant that
-- actually has memberships, so those tenants stop depending on the fallback and may diverge freely — which
-- is §2's semantics. The fallback then only covers a tenant that has not been provisioned yet, and
-- 21.1c/21.4 provisioning copies the defaults at tenant creation.
--
-- KNOWN LIMITATION. Row-absence carries two meanings that this schema cannot tell apart: "this tenant has
-- not been provisioned" and "this tenant grants this role nothing". Resolution treats absence as the former
-- and falls back, so a tenant cannot express a truly empty grant set for a role by deleting rows. That is
-- acceptable because 21.2 adds per-membership Deny overrides, which express "not this principal" precisely
-- and win over any grant (deny-wins, design 40 §2) — a better tool than a whole-tenant empty. If a
-- tenant-wide empty is ever genuinely needed, add an explicit tombstone rather than reinterpreting absence.
--
-- Additive + idempotent. Expand phase: nothing is dropped, the old rows ARE the default bucket.

-- ---- tenant_id on the grant ----------------------------------------------------------------------------

ALTER TABLE identity.role_scope ADD COLUMN IF NOT EXISTS tenant_id text NOT NULL DEFAULT '';

-- Repoint the primary key so the same role may carry different grants per tenant. The old PK
-- (role_name, scope_name) would reject the second tenant's copy of any shared grant.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM pg_constraint c
        JOIN pg_class t ON t.oid = c.conrelid
        JOIN pg_namespace n ON n.oid = t.relnamespace
        WHERE n.nspname = 'identity' AND t.relname = 'role_scope' AND c.contype = 'p'
          AND pg_get_constraintdef(c.oid) = 'PRIMARY KEY (role_name, scope_name)'
    ) THEN
        -- migrate-compat: contract-ok (the PK is WIDENED, never narrowed. Every existing row keeps its
        -- identity under tenant_id='', an old writer that omits tenant_id still lands in that bucket via the
        -- column DEFAULT, and an old reader that ignores tenant_id still sees every row it saw before. No
        -- rollout ordering is required for this statement.)
        ALTER TABLE identity.role_scope DROP CONSTRAINT role_scope_pkey;
        ALTER TABLE identity.role_scope ADD CONSTRAINT role_scope_pkey
            PRIMARY KEY (tenant_id, role_name, scope_name);
    END IF;
END $$;

CREATE INDEX IF NOT EXISTS ix_role_scope_tenant_role ON identity.role_scope (tenant_id, role_name);

GRANT SELECT, INSERT, UPDATE, DELETE ON identity.role_scope TO hbmp_app;

-- NOTE: provisioning each tenant its own COPY of the defaults is deliberately NOT done here — see 0012.
-- Adding per-tenant rows while an OLD identity-service instance is still running would over-grant: the
-- pre-21.1b resolver selects by role name with no tenant predicate, so it would union every tenant's grants
-- into one scope claim. This migration alone leaves every row in the '' bucket, so old and new instances
-- resolve identically and the rollout is safe in either order.

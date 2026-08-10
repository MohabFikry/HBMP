-- identity-service — 0036: a tenant may author its own roles.
--
-- ============================================================================================================
-- WHY
-- ============================================================================================================
-- 28.9. The platform ships twenty-one roles, and no organisation's staff structure is exactly twenty-one
-- shapes. Until now an administrator facing a job the catalogue does not name had two options: hand the
-- person the nearest BIGGER role, or file a request for a platform change. The first is what actually
-- happens, and it is how least-privilege erodes — not by anyone deciding against it, but by the alternative
-- being unavailable at the moment of the decision.
--
-- ============================================================================================================
-- WHAT THIS COLUMN IS, AND WHAT IT IS NOT
-- ============================================================================================================
-- It records the OWNER, not a namespace. Role names remain GLOBALLY unique — ASP.NET Identity's RoleStore
-- requires it, and the token's `roles` claim is a flat vocabulary with nowhere to put a tenant qualifier.
-- So two tenants cannot both author `triage_lead`; the second gets a 409. What this buys is the ability to
-- refuse one tenant editing another's role, and to tell an administrator which roles are theirs to change.
--
-- NULL means built-in: every role that existed before this migration, which is the entire seeded catalogue.
-- The column is nullable rather than defaulted to '' so that "authored by nobody" and "authored by the
-- platform-default bucket" cannot be confused — '' is a real, meaningful tenant id in `role_scope`.
--
-- A custom role grants PERMISSIONS, never a PORTAL. The SPA derives a workspace from the built-in
-- role→portal map, so a custom role adds keys to whatever portal its holder already has. Somebody holding
-- only custom roles has no portal and lands on the fail-closed "no portal assigned" page — which is correct:
-- a workspace is a designed thing with screens in it, and no list of scopes can conjure one.

ALTER TABLE identity.role
    ADD COLUMN IF NOT EXISTS owner_tenant_id varchar(64);

COMMENT ON COLUMN identity.role.owner_tenant_id IS
    'Tenant that authored this role (28.9). NULL = built-in platform catalogue. Names stay globally unique; '
    'this records ownership so one tenant cannot edit another''s role.';

-- Finding a tenant's own roles is the admin console's most frequent query against this table and the one
-- that runs on every render of the role list. Partial, because the built-ins are the large majority and are
-- never looked up this way.
CREATE INDEX IF NOT EXISTS ix_role_owner_tenant
    ON identity.role (owner_tenant_id)
    WHERE owner_tenant_id IS NOT NULL;

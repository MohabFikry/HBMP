-- identity-service — 0012 provision each tenant its own copy of the default role→scope grants
-- (phase 21.1b, design 40 §2 "copy, not live inheritance").
--
-- ****  ORDERING REQUIREMENT — this is the CONTRACT half of 0011's expand.  ****
-- Apply this only once EVERY identity-service instance is running >= 21.1b. The pre-21.1b
-- RoleScopeResolver selects grants by role name with NO tenant predicate; the moment per-tenant rows exist,
-- that old query unions every tenant's grants into a single `scope` claim — i.e. it over-grants across
-- tenant boundaries. 0011 alone is safe in any order because it leaves every row in the '' default bucket;
-- this file is the step that must wait.
--
-- After this runs, each provisioned tenant owns its grant set outright and may diverge from the platform
-- default freely (that divergence is the point — design 40 §2). Tenants created later are provisioned by the
-- application at tenant-creation time; the resolver's per-role fallback to '' covers any tenant in between.
--
-- Idempotent (ON CONFLICT DO NOTHING) and additive: no existing row is modified or removed, and the ''
-- bucket stays in place as the fallback for unprovisioned tenants.

-- Tenants come from tenant_membership (0010) — the authoritative list of tenants that actually have
-- principals. '' is skipped: it IS the default bucket, so copying it onto itself is a no-op.
INSERT INTO identity.role_scope (tenant_id, role_name, scope_name)
SELECT t.tenant_id, d.role_name, d.scope_name
FROM (
    SELECT DISTINCT tenant_id
    FROM identity.tenant_membership
    WHERE tenant_id <> '' AND NOT is_deleted
) t
CROSS JOIN (
    SELECT role_name, scope_name FROM identity.role_scope WHERE tenant_id = ''
) d
ON CONFLICT (tenant_id, role_name, scope_name) DO NOTHING;

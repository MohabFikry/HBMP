-- identity-service — 0041 name the platform-default sentinel at the schema.
--
-- ============================================================================================================
-- WHY A MIGRATION THAT CHANGES NO DATA AND NO STRUCTURE
-- ============================================================================================================
-- `identity.role_scope.tenant_id = ''` is not an unstamped row. It is `RoleScope.PlatformDefault` — the
-- default grant bucket every tenant falls back to until it is provisioned its own copy — and
-- `RoleScopeResolver` reads it on every token issued to an unprovisioned tenant. Migration 0011 established
-- it and its banner says so.
--
-- That was written down in two places a reader of the DATABASE never sees: a C# constant and a migration
-- banner four years of files back. Meanwhile the platform-wide "no row may carry tenant_id = ''" cleanup ran
-- three times, and each time these 341 rows came back as the one open question — *is role_scope tenant-scoped
-- or not?* — because nothing at the schema said. Twice the proposed remedy was `CHECK (tenant_id <> '')`,
-- which would not have cleaned anything up: it would have deleted the fallback and left every unprovisioned
-- tenant's users holding no scopes at all.
--
-- So the answer goes where the question gets asked. `\d+ identity.role_scope` now states it, and
-- `tools/ci/check-tenant-stamping.py` carries the same sentence as its one sanctioned exemption. The two are
-- deliberately duplicated: a census that skipped this table silently would be indistinguishable from one
-- that had never looked at it.
--
-- Idempotent by construction — COMMENT ON replaces. Takes no lock worth naming and needs no rollback.

COMMENT ON COLUMN identity.role_scope.tenant_id IS
    'The tenant that OWNS this grant (design 40 §2, migration 0011). '''' is RoleScope.PlatformDefault — the '
    'platform default grant bucket, NOT an unstamped row: RoleScopeResolver falls back to it for any tenant '
    'that has not been provisioned its own copy, so a CHECK (tenant_id <> '''') here would strand every '
    'unprovisioned tenant with no scopes. Sanctioned in tools/ci/check-tenant-stamping.py; every OTHER '
    'tenant_id column on the platform is required to be non-empty and that gate enforces it.';

COMMENT ON TABLE identity.role_scope IS
    'Role -> scope grants, tenant-local since 0011. The role CATALOG stays global (the token''s roles '
    'vocabulary is frozen and ASP.NET Identity requires globally unique role names); tenant-locality lives '
    'here, so two tenants may grant different scopes to the same role name.';

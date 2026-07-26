# identity-service (Phase 17 — In-App Identity, ADR-0015)

The platform's in-app OpenID Connect issuer, replacing Keycloak. Built on **ASP.NET Core Identity** (user +
role store) and, from 17.2, **OpenIddict** (the OAuth2/OIDC authorization server). It issues access tokens
that are byte-compatible with the **frozen token contract** (`docs/security/token-contract.md`), so the 15
services validate them through `libs/auth` with only a config change.

## Layout
- **Domain** — `ApplicationUser`/`ApplicationRole` (Identity + Mersal ABAC attributes: `tenant_id`,
  `provider_id`, `display_name`), and the roles/scopes-as-data model (`Scope`, `RoleScope`).
- **Infrastructure** — `IdentityStoreDbContext` (Identity EF store over the `identity` schema),
  `RoleScopeResolver` (roles → scope union, the seam the issuer uses for the `scope` claim), SQL migrations.
- **Api** — the host. 17.1 exposes read-only `/identity/roles`, `/identity/scopes`, `/identity/effective-scopes`
  for verification; the issuer + login/2FA + admin land in 17.2–17.4.

## Roles & scopes as DATA (17.1)
Roles, the scope catalog, and the **role→scope matrix** are seeded rows (`0001_identity.sql`), not code
constants, so the 17.4 admin surface manages them without a redeploy. The matrix mirrors
`11-permission-matrix` / `apps/web/.../permissions.ts`; the min-necessary hard rules hold structurally
(Reception/Finance/Claims carry no clinical scope; Lab carries no pharmacy/rx scope).

## Schema & RLS
`identity` schema, applied by `tools/ci/apply-migrations.sh`. The identity core + scope/role_scope tables are
deliberately **not tenant-RLS**: the issuer authenticates a user (by username) to *discover* their tenant
before any request-scoped tenant context exists, so tenant_id here is a claim source, not a row filter (see
`0002_identity_grants.sql`). Reachable only by identity-service under the `hbmp_app` grant.

## Tests
Env-gated on `IDENTITY_TEST_DB` (a connection string to a migrated DB). Cover the frozen role vocabulary,
role→scope resolution + min-necessary hard rules, and a user store round-trip proving the DDL matches the
EF model. DB-less CI skips them.

## Phase map
17.1 store + roles/scopes-as-data (**this**) → 17.2 OpenIddict issuer (frozen claims) → 17.3 login + TOTP
2FA → 17.4 in-app admin (closes C3) → 17.5 SPA rewire (closes H6) → 17.6 cutover + Keycloak retirement.

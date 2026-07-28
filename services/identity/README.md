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

## Membership administration (phase 21.6, design 40 §1/§6)

The read surface behind the admin SPA's user & access screens. All of it requires `admin:read` (writes
`admin:write`) **and** an MFA session, enforced at the pipeline and again per action.

- `GET /identity/admin/memberships?tenant=&status=&query=` — the roster: status, roles with their tier,
  platform-admin flag, and live/lapsed override counts. **Tenant-pinned**: a caller sees their own tenant
  unless the identity carries the platform-admin flag, and asking for another tenant is **403 + audited**,
  never silently narrowed — a page of your own tenant under another tenant's heading would let someone
  review the wrong organisation while believing they reviewed the right one.
- `GET /identity/admin/memberships/{id}` — one membership with every override, each carrying its reason and
  grantor. Lapsed overrides are returned flagged `expired`, not dropped: the evaluator already ignores them,
  and hiding them leaves nobody able to explain why someone's access changed overnight.
- `GET /identity/admin/memberships/{id}/effective` — mode-2 effective access, plus `platformAdminKeys` so a
  preview can show which keys the A1 short-circuit accounts for rather than mislabelling them as role grants.
- `DELETE /identity/admin/users/{id}/sessions/{sessionId}` — revoke ONE session. The prior surface only
  offered revoke-all, which is right for off-boarding and wrong for a single stolen device; the cost of
  signing a clinician out everywhere meant the safe action got postponed.

Deliberately **not** the access-review snapshot (`/identity/admin/access-review/{tenant}`): that recomputes
every membership's effective set and is audited as an **Export**, because signing a review pack is a bulk
disclosure. Browsing a roster is not, and reusing it here would make the screen O(memberships) evaluator
calls while burying the real exports under routine navigation.

## Phase map
17.1 store + roles/scopes-as-data (**this**) → 17.2 OpenIddict issuer (frozen claims) → 17.3 login + TOTP
2FA → 17.4 in-app admin (closes C3) → 17.5 SPA rewire (closes H6) → 17.6 cutover + Keycloak retirement.

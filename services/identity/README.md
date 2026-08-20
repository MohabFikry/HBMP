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

## Token retention (phase 28.11)
OpenIddict persists a row per artefact it mints — access token, id_token, authorization code, refresh token —
and prunes none of them by default; its own pruning ships as an opt-in Quartz job this service never opted
into. So the table only grew: the development database reached 55,590 rows / 51 MB in sixteen days, of which
all but ~400 had expired. An access token lives five minutes and its row lived forever.

`TokenPruner` (a plain `BackgroundService`, no Quartz) runs daily and calls OpenIddict's own
`PruneAsync` on tokens then authorizations — that order, because an authorization is only prunable once its
tokens are gone. **It cannot sign anybody out:** OpenIddict prunes only what is already expired or spent, at
any threshold. The window governs how far back this table can answer a forensic question, and
`identity.login_attempt` plus the hash-chained audit store answer that better and for far longer.

| Setting | Default | Meaning |
|---|---|---|
| `Issuer:TokenRetentionDays` | `30` | Days a spent token's row survives. Floored at 1 — a window under the 10h refresh lifetime is a typo, not a policy. |

30 days is the conservative end of the 30–90 day "transient" class in `20-compliance-checklist.md` §6, whose
line 110 lists "retention schedule configured and enforced by purge jobs" as a control. `TokenPruningTests`
holds both halves: a spent token past the window goes, and a token still backing a session stays however old
its row is.

## Tests
Env-gated on `IDENTITY_TEST_DB` (a connection string to a migrated DB). Cover the frozen role vocabulary,
role→scope resolution + min-necessary hard rules, and a user store round-trip proving the DDL matches the
EF model. DB-less CI skips them.

**Fixtures are swept, not just cleaned up.** Every test that creates an account removes it in a `finally` or
an `await using`, and none of that runs when the process is killed — seven accounts and ~19,000 orphaned
token rows had accumulated in the shared development database that way. `TestFixtureSweep` brackets the
`identity-db` collection: it clears fixture accounts (matched on the RFC 2606 `@example.org` domain), the
roles the catalogue tests mint, and the tokens a deleted account leaves behind (`OpenIddictTokens.subject`
has no foreign key to the user it names). It runs *before* the collection as well as after, because only the
before-pass can clean up after a run that never reached its own `finally`.

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

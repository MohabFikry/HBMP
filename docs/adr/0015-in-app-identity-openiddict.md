# 15. In-app identity: retire Keycloak for ASP.NET Identity + OpenIddict

Date: 2026-07-26
Status: Accepted
Phase: 17.0 (In-App Identity — foundation)

## Context

Identity today is **Keycloak** (`infra/keycloak/realm-mersal.json`): a separate Java service that owns
the user store, the login UI, MFA, and token issuance. Every service validates its RS256 access tokens
through `libs/auth` (`AddHbmpAuthentication` → JwtBearer + JWKS), and the SPA authenticates via a
hand-rolled auth-code + PKCE client (`apps/web/src/auth/oidcClient.ts`). This works, but three open audit
findings and the platform's operating model all point the same way:

- **C3 (admin MFA / user administration).** There is no in-app way to create a user, grant/revoke a role,
  or enforce MFA on privileged accounts — it all lives in the Keycloak admin console, outside our RBAC,
  RLS, hash-chained audit, and Segregation-of-Duties engine. Admin of identity is therefore un-audited by
  our spine and un-governed by our SoD rules.
- **H3 (edge JWT) / H6 (realm-role reconciliation).** The realm's roles/scopes/attributes duplicate
  concepts the app already owns (the 16-role catalog + tiers in `RoleCatalog`, `tenant_id`/`provider_id`
  ABAC attributes, the scope set the services enforce). Keeping two sources of truth in sync is manual and
  drift-prone (e.g. `claims_officer` exists in the realm but not yet in `RoleCatalog`).
- **Operating model.** Mersal is a charity on an on-prem-first, `$0`-licensing, single-server→k3s stack.
  Running and patching a separate Keycloak (plus its own DB, backups, and CVE surface) is a heavy moving
  part for what is, functionally, "issue our own users a token." The split-horizon issuer hack
  (`ValidIssuers`: browser sees `localhost:8080`, services fetch JWKS via `keycloak:8080`) is pure
  incidental complexity from Keycloak being a separate host.

The token-*validation* side (`libs/auth`) is already issuer-agnostic: it validates iss/aud/signature/expiry
and reads a specific, small set of claims. Nothing about the services or the SPA is Keycloak-specific
*except the issuer*. That makes the issuer replaceable without touching validation — provided the new
issuer reproduces the **exact same token contract**.

## Decision

Replace Keycloak with an **in-app OpenID Connect authorization server built on ASP.NET Core Identity +
OpenIddict**, hosted by a new `identity-service`, issuing tokens that are **byte-for-byte contract-compatible**
with what `libs/auth` and the SPA already consume.

- **User/credential store — ASP.NET Core Identity** over a new `identity` Postgres schema (users, roles,
  role claims, user logins, authenticator keys, recovery codes). Governed like every other schema: RLS as
  `hbmp_app`, mutations hash-chain-audited, no hard deletes.
- **Issuer — OpenIddict** as the OAuth2/OIDC server:
  - **auth-code + PKCE** for the SPA (replaces the Keycloak auth endpoint; the SPA's existing PKCE client
    only changes its `authority`/URLs),
  - **client-credentials** for service-to-service,
  - **refresh-token rotation** for session continuity.
- **The token contract is frozen first** (`docs/security/token-contract.md`, this phase). OpenIddict is
  configured to emit precisely those claims — `sub`, `roles` (flat, lower-cased app role names), space-
  delimited `scope`, `tenant_id`, `provider_id`, `sid`, `amr`/`acr` MFA signals, `aud=hbmp-api`. `libs/auth`
  changes by **configuration only** (`Auth:Authority` → the in-app issuer); the claim-reading code
  (`HbmpPrincipal`, `KeycloakClaims`, `MfaEvaluator`) is unchanged because it already reads the flat/std
  shapes the new issuer emits.
- **MFA in-app** via Identity's TOTP authenticator (enrolment, recovery codes, and step-up for privileged
  scopes), emitting an `amr`/`acr` value the existing `MfaEvaluator` already accepts. This is where **C3's
  MFA enforcement** actually lands.
- **User/role/scope administration in-app** (Phase 17.4), on the real store, through the app's RBAC + SoD +
  audit — closing **C3**.

### Sequence

`17.1` Identity store + roles/scopes as data → `17.2` OpenIddict issuer (frozen claims) → `17.3` login
pages + TOTP 2FA + recovery + step-up → `17.4` in-app user/role/scope admin (closes C3) → `17.5` SPA rewire
to the new issuer (closes H6 by design) → `17.6` cutover, Keycloak retirement, global edge-JWT plugin
(H3), and doc-truth pass. Phase 16's Keycloak-specific steps (16.3 MFA, 16.5 edge-JWT) were deliberately
deferred here so they land once on the new issuer rather than twice.

## Why not keep Keycloak

Keycloak is excellent, and "roll your own issuer" is normally an anti-pattern. Three things make it the
right call *here*: (1) we are not rolling our own — OpenIddict is a mature, certified OIDC server library;
(2) the identity data (roles, tiers, tenant/provider attributes, SoD) is *already* a first-class app
domain, so hosting it externally creates a second source of truth rather than removing responsibility; and
(3) the deployment/operability win on a charity single-server stack is real. We keep OIDC/OAuth2 as the
protocol (cloud-ready, standards-based) — we only move *who issues*.

## Migration & risk

- **No password-hash migration.** The dev realm has no real users, and production is a greenfield pilot
  (Phase 12), so accounts are **re-provisioned** on the new store rather than migrated — avoiding a
  hash-rehash/import risk entirely.
- **Contract regression is the primary risk**, mitigated by freezing the contract (this phase) plus a
  conformance test in 17.2 that asserts a minted token satisfies `HbmpPrincipal.FromClaims` +
  `MfaEvaluator` for every role/scope/MFA combination the platform relies on.
- **WebAuthn/passkeys are deferred** — TOTP first (covers the MFA requirement); hardware keys are a later
  additive step (`amr` already reserves `hwk`/`webauthn`).
- **Rollback:** until 17.6 cutover, Keycloak stays deployed and `libs/auth` can point back at it by config
  (both issuers speak the same contract), so the migration is reversible up to retirement.

## Consequences

- One fewer external service to run, patch, and back up; the split-horizon `ValidIssuers` hack disappears.
- Identity becomes app data — RLS-governed, hash-chain-audited, SoD-checked — and user/role/scope admin
  becomes an in-app, RBAC-gated screen (closes **C3**).
- MFA is enforced by the app on privileged scopes (closes the 16.3 MFA gap on the new issuer).
- The realm-role/scope duplication and its drift (**H6**) end: the app is the single source of truth.
- Token *validation* is untouched, so the blast radius on the 15 services is a config value, not code.
- See the companion **frozen token-contract snapshot** (`docs/security/token-contract.md`) — the normative
  spec the OpenIddict issuer must satisfy — and ADRs [0011 (hbmp_app RLS)], [0013 (durable outbox)].

## Update — cutover complete (Phase 17.6, 2026-07-26)

17.1–17.6 are implemented and merged. `identity-service` is the issuer; the SPA and all 15 services
authenticate against it (services' `Auth:Authority` → `http://identity-service:8080`, browser via
`localhost:8090`, split-horizon reconciled by each service's `ValidIssuers`). Keycloak is removed from
`infra/compose/compose.yaml`; `infra/keycloak/*` is retired-for-reference. Kong routes `/connect/*` +
`/.well-known/*` to the issuer; edge JWKS validation stays at the service layer in Tier 1 (community Kong
cannot do JWKS discovery with rotating RS256 keys) with the openid-connect plugin noted for Tier 2/3.
Demo staff accounts (one per role, dev-only) are seeded by `UserSeeder`. Dev/test use ephemeral signing
keys; **production must supply persistent RS256 keys from OpenBao** (tracked as the one remaining prod-hardening
follow-up for go-live, Phase 12).

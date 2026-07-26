# Phase 17 — In-App Identity: Replace Keycloak with ASP.NET Identity + OpenIddict

**Goal:** Make identity-service the platform's own OIDC issuer so that **login happens inside the app** (Mersal-branded, bilingual, no external portal), **users are created and managed from the admin portal** against the real store (no Admin-API proxying, no realm sync), **RBAC + ABAC live in one place** (the app — roles/scopes in identity-service + `libs/authz`, with the Keycloak realm retired), and **2FA is built in** (TOTP for everyone, recovery codes, step-up for admin/break-glass, WebAuthn-ready). The token contract is **frozen**: every downstream consumer (Kong, `libs/auth`, all 19 services, the SPA) keeps validating the same JWT shape — this is an issuer swap, not platform surgery.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> **Sequencing (important):** run 17 **instead of** re-fixing Keycloak in phase 16 — specifically, 16.3's MFA enforcement and 16.7's realm/scope reconciliation land **on the new issuer**, not on Keycloak. Recommended order: 16.1, 16.2 → **17.1–17.6** → 16.3–16.9 (with their Keycloak-specific steps re-pointed here). Do not do the H6 realm-scope fixes twice.

---

## Skills to activate
> `mersal-platform-architect`, `refugee-healthcare-management` (always-on), plus `healthcare-database-architect` (17.2 schema), `healthcare-uiux-designer` (17.5 login/security UI). Index: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first
- **ADR first (17.0 writes it):** this phase implements ADR-0015 "In-app identity: ASP.NET Identity + OpenIddict replace Keycloak".
- [`../../docs/AUDIT-2026-07-26.md`](../../docs/AUDIT-2026-07-26.md) — H6 (scope drift, fail-open role, token storage, keep-alive) is *solved structurally* by this phase; C3 (admin MFA) must be enforced against the new issuer.
- [`../18-security-model.md`](../18-security-model.md) — MFA, session/timeout policy, break-glass step-up; [`../11-permission-matrix.md`](../11-permission-matrix.md) — scopes/roles; [`../10-role-matrix.md`](../10-role-matrix.md) — the role catalog incl. `call_center`, `claims_officer`, Branch/Clinic Manager.
- [`../37-branch-scoping-and-clinical-sensitivity.md`](../37-branch-scoping-and-clinical-sensitivity.md) §2 — `user_branch_assignment` already lives in identity-service; the new user model must link to it.
- [`../0B-DESIGN-SYSTEM-UI.md`](../0B-DESIGN-SYSTEM-UI.md) + [`../21-accessibility-checklist.md`](../21-accessibility-checklist.md) — the login/security screens are first-class portal UI (AR/EN, RTL, WCAG 2.2 AA, Mersal logo lockup).
- **Existing code you are changing (read before writing):**
  - `services/identity/` (current Keycloak-adjacent service + `user_branch_assignment`), `services/admin/` (user/role admin, SoD, access reviews — its Keycloak group-membership calls become local calls).
  - `libs/auth/` — `ServiceCollectionExtensions.cs` (JWT validation: issuer/audience/lifetime/signing — **stays**, only the authority URL changes), `ScopePolicyProvider.cs` (MFA-per-scope), `HbmpAuthOptions.cs`.
  - `infra/keycloak/realm-mersal.json` + `scope-catalog.yaml` — the scope catalog is the **source of truth to import**, then the realm files retire.
  - `apps/web/src/auth/` — `oidcClient.ts` (PKCE — **stays**, endpoint URLs change), `authClient.ts`, `tokenStore.ts`, `AuthProvider.tsx`, `config.ts` (ROLE_MAP, OIDC scopes).
  - `infra/compose/` — keycloak container + kong.yml JWKS reference.
  - `docs/HANDOFF.md` gotchas: `./dotnet.sh`, PG :55432, .NET 8 only, analyzers-as-errors, CPM (add OpenIddict versions to `Directory.Packages.props`).

---

## THE INVARIANTS

1. **Token contract frozen.** Access tokens remain JWTs signed with an asymmetric key published at `/.well-known/jwks`, carrying the SAME claims the platform already validates: `iss`, `aud=hbmp-api`, `sub`, `exp/iat`, realm-role claim (keep the exact claim path `libs/auth` reads today — verify before coding), `scope`, `provider_id` (provider users), `tenant`, and the MFA signal (`amr`/`acr`) that `ScopePolicyProvider` checks. **Zero changes to the 19 services.** Kong's JWT plugin just re-points its JWKS URL.
2. **Authorization Code + PKCE stays the SPA flow.** "In-app login" means the *pages are ours* (served by identity-service, themed, embedded seamlessly) — NOT the deprecated password grant from the SPA. The SPA never touches raw passwords.
3. **One identity source of truth.** Users, roles, scopes, MFA state, branch assignments — all in the `identity` schema. No parallel store, no realm sync. The scope catalog is imported once and the YAML retired (or regenerated FROM the DB).
4. **Passwords & secrets:** ASP.NET Identity's hasher (PBKDF2 default; bump iterations to current OWASP guidance), lockout enabled, no password ever logged or audited in cleartext; signing keys in OpenBao (file-based dev fallback), rotated with `kid` rollover (publish old+new during overlap).
5. **Everything audited:** login success/failure, lockout, MFA enrolment/verification/failure, password change/reset, user create/disable, role/scope grant, token issuance for break-glass — hash-chained via `libs/audit-client`, with **no credentials in the audit payload**.
6. **Fail closed:** unknown role → no portal (the H6 fix); unverified 2FA where required → no token; identity-service down → services keep validating cached JWKS but issue nothing.

---

## Prompts

### 17.0 — ADR + token-contract snapshot (do this first, small commit)
```text
1. Write docs/adr/0015-inapp-identity-openiddict.md: context (Keycloak friction: external login page,
   Admin-API proxying, realm/scope drift per AUDIT H6), decision (ASP.NET Core Identity + OpenIddict in
   identity-service as the platform OIDC issuer), alternatives considered (keep+theme Keycloak, Zitadel,
   Ory Kratos+Hydra — and why they lost for THIS stack), consequences (+one source of truth, in-app UX,
   -owned token-lifecycle risk mitigated by phase-11 security gates covering identity endpoints).
2. TOKEN-CONTRACT SNAPSHOT: read libs/auth/ServiceCollectionExtensions.cs + ScopePolicyProvider.cs +
   a captured Keycloak access token (tests/fixtures) and write docs/security/token-contract.md listing
   EVERY claim the platform reads (exact JSON paths: role claim location, scope format, amr/acr, aud,
   provider_id, tenant). This file is the acceptance oracle for 17.2 — the new issuer must emit
   byte-compatible claims. Add a contract test in libs/auth/Tests that asserts a sample token parses
   into the same HbmpPrincipal regardless of issuer.
Acceptance: ADR merged; token-contract.md complete; contract test green against a recorded Keycloak token.
```

### 17.1 — Identity store: ASP.NET Identity in identity-service
```text
Add ASP.NET Core Identity to services/identity (schema `identity`, hand-authored SQL migration per house
style — model the tables explicitly: app_user, app_role, app_user_role, app_user_claim, app_user_login,
app_user_token, plus password/lockout/2FA columns; snake_case; standard audit columns; soft-delete).
- Custom user: HbmpUser (Guid v7 id, user_name/email UNIQUE where not deleted, display_name_en/ar,
  status Active/Disabled, provider_id NULL for provider-scoped users, must_change_password,
  last_login_at). Link (value, not FK) to the existing user_branch_assignment table.
- Roles seeded from ../10-role-matrix.md: reception, appointment_coordinator, doctor, nurse, lab_tech,
  imaging_tech, pharmacist, medical_approval, medical_director, case_manager, finance, claims_officer,
  claims_reviewer, network_team, provider_admin, org_admin, super_admin, call_center,
  call_center_supervisor, branch_manager, auditor. Idempotent seed.
- SCOPES AS DATA: scope + role_scope tables; one-time importer reads infra/keycloak/scope-catalog.yaml
  → DB (all ~44 scopes incl. callcentre:*, claims:*, admin:read/write). The DB is now the source of
  truth; add an export endpoint that can regenerate the YAML for reference.
- Password policy per ../18 (length ≥ 12, breach-list check via a local top-100k denylist, lockout
  5 attempts/15 min, history 5); PBKDF2 iterations raised to current OWASP guidance.
- Password hashes verified never to appear in logs/audit (test greps the audit payloads).
Acceptance: migration applies; roles+scopes seeded idempotently; user CRUD via repository works with
soft-delete + audit; lockout triggers after 5 failures and is audited.
Tests: store round-trip, seed idempotency, lockout, policy rejection matrix, no-hash-in-audit.
```

### 17.2 — OpenIddict issuer with the frozen token contract
```text
Make identity-service the OIDC authorization server with OpenIddict (add versions to
Directory.Packages.props; .NET 8 APIs only).
- Flows: Authorization Code + PKCE (the SPA), client_credentials (service-to-service/test harness),
  refresh_token (rotating, sliding, absolute cap per ../18 session policy). NO password grant.
- Endpoints: /connect/authorize, /connect/token, /connect/logout, /.well-known/openid-configuration,
  /.well-known/jwks. Signing: asymmetric key from OpenBao (dev: file), kid-based rotation with overlap.
- CLAIMS: emit exactly docs/security/token-contract.md — aud=hbmp-api, the role claim at the SAME path
  libs/auth reads today, space-delimited scope, provider_id/tenant, amr/acr reflecting completed MFA
  (amr=["pwd","otp"] after TOTP). Access token TTL + refresh policy per ../18 §9.
- Register clients: hbmp-web (public, PKCE, redirect URIs from config), hbmp-test (confidential, CC
  grant, test-only), future provider clients as data.
- Wire libs/auth: Authority becomes the identity-service URL (config change only — validation code
  untouched). Update Kong's JWT plugin JWKS to the new URL. Compose: identity-service exposes the
  issuer; keycloak container stays UP but unused until 17.6.
Acceptance (the big one): the 17.0 contract test passes against a LIVE token minted by OpenIddict;
one representative protected endpoint per service zone (patient, orders, admin, callcentre) returns
200 with the new token and 401 without — WITHOUT any service-code change.
Tests: discovery/jwks correctness, PKCE happy path + wrong-verifier rejection, refresh rotation +
reuse-detection (revoke family on replay), TTLs, kid rotation overlap, claims byte-compat.
```

### 17.3 — In-app login + full 2FA (TOTP now, WebAuthn-ready)
```text
Build the login experience served by identity-service at /connect/authorize — Mersal-branded, bilingual,
felt as part of the app (same design tokens; the SPA navigates to it and returns via PKCE redirect —
users see one seamless product, never a third-party portal).
- Pages (Razor, consuming the design-system CSS tokens; AR/EN with full RTL; WCAG 2.2 AA; Mersal logo
  lockup per ../0B §8): login (username/password), TOTP challenge, recovery-code entry, forced
  password-change, self-service password change; "forgot password" issues an ADMIN-mediated reset flow
  (no email infra assumption: admin generates a one-time reset link/code, audited) — plus optional
  email reset behind config when SMTP exists.
- 2FA:
  * TOTP enrolment: QR (otpauth:// URI) + manual key + verify-to-activate; 10 one-time recovery codes
    (hashed at rest, single-use, regenerate invalidates old); drift window ±1 step.
  * POLICY: 2FA REQUIRED for admin-tier roles (org_admin, super_admin, medical_director,
    claims_reviewer, auditor) and for any break-glass-capable account — enforced at login (cannot skip)
    AND reflected in amr/acr so ScopePolicyProvider's per-scope MFA checks bite. Optional-but-nudged
    for other staff (configurable per role).
  * STEP-UP: an authenticated session without recent MFA hitting an MFA-required scope → re-challenge
    (acr bump), per ../18 §11 break-glass.
  * WEBAUTHN-READY: define IStrongAuthenticator abstraction with TotpAuthenticator now; leave a
    documented seam (fido2-net-lib) — do NOT implement WebAuthn in this phase.
- Session policy per ../18 §9: idle timeout with warning, absolute cap, concurrent-session limit
  (revoke oldest), all enforced via refresh-token semantics.
- Audit every event listed in invariant 5.
Acceptance (Given/When/Then):
- Given valid credentials for a TOTP-enrolled user, When they log in with a correct code, Then a token
  with amr containing otp is issued and the login is audited.
- Given an org_admin WITHOUT 2FA enrolled, When they log in, Then they are forced into enrolment before
  any token is issued.
- Given a wrong TOTP 5 times, Then MFA lockout + audit; recovery code works once and never again.
- Given AR locale, Then the whole login flow renders RTL with Arabic text; axe passes on every page.
Tests: TOTP verify/drift/replay, recovery single-use, forced-enrolment matrix per role, step-up on
MFA-required scope, session limits, a11y (axe) on login pages, audit completeness.
```

### 17.4 — In-app user & access management (admin portal on the real store)
```text
Rewire services/admin from Keycloak Admin-API proxying to DIRECT identity-service management (in-mesh
call or shared identity store via a clean IIdentityAdmin API in services/identity — choose, justify in
the ADR; keep admin-service as the policy/SoD brain either way).
- User lifecycle from the admin portal: create user (username, names AR/EN, role(s), branch Home/
  Additional, provider_id for provider users, temp password with must_change flag), disable/enable,
  unlock, force password reset (one-time link/code), require 2FA re-enrolment, view sessions + revoke.
- Role & scope management: grant/revoke roles (SoD matrix from ../10 §7 enforced at assignment — the
  existing SegregationOfDuties engine now guards the REAL store), edit role→scope mappings (versioned,
  audited, peer-review flag for high tiers per 8b conventions).
- ABAC stays where it is (libs/authz + policy bundles) — but the admin "access matrix" view now reads
  live from identity DB + policy bundles with zero sync lag.
- De-provision propagates immediately: disable → all refresh tokens revoked → next access-token expiry
  ends access everywhere (document the ≤ TTL window; keep access TTL short per ../18).
- All endpoints behind .RequireAuthorization(admin scopes) + MFA (this IS audit finding C3's fix,
  landing on the new issuer) + Idempotency-Key on mutations.
- FRONTEND (apps/web admin portal): user-create/edit forms (bilingual, validated), role/scope editors,
  session viewer, reset-link generator with copy-once display — design-system components, a11y DoD.
Acceptance:
- Given org_admin creates a reception user with Home=Maadi, When that user logs in and changes the temp
  password, Then they land on the Reception portal scoped to Maadi.
- Given a SoD-conflicting grant, Then 409 with the conflict reason (existing tests keep passing).
- Given a user is disabled, Then their refresh is revoked and API access ends within the access TTL.
- Given a non-MFA admin token, When any admin mutation is called, Then 403.
Tests: full lifecycle E2E (create→login→2FA-enrol→use→disable), SoD regression suite green, revocation
timing, idempotent replay, admin UI component tests + axe.
```

### 17.5 — SPA auth rewire (and the H6 fixes land here, once)
```text
Update apps/web to the new issuer — small, surgical:
- config.ts: OIDC authority/endpoints → identity-service; request the scopes from the DB catalog
  (the SPA scope list and the issuer now share one source, killing the 44-vs-10 drift class);
  ROLE_MAP driven by the same role codes 17.1 seeded; rename reception:search→reception:read.
- oidcClient.ts: keep PKCE exactly; only endpoint URLs change. authClient.ts DevAuthClient excluded
  from production bundles (build-time).
- FAIL CLOSED: unmapped role → "no portal assigned" page (never default reception).
- TOKEN HANDLING: in-memory only (remove the sessionStorage mirror); on reload, silent re-auth
  (prompt=none) restores the session; "keep alive" performs a real refresh-token exchange and derives
  expiresAt from the new token's exp; idle-timeout warning wired to real session policy.
- Login UX: the SPA redirects to the identity-service pages (same design tokens ⇒ visually seamless);
  logout hits /connect/logout then clears memory.
Acceptance: live login E2E through Kong (password+TOTP) reaches the correct portal; refresh works
across a reload without re-entering credentials; unknown role → no-portal page; fixture mode unchanged;
axe green.
Tests: auth-flow integration (mock issuer), fail-closed role test, refresh/reload test, bundle check
that DevAuthClient/DevApiClient are absent in prod build.
```

### 17.6 — Cutover, Keycloak retirement & doc truth
```text
1. MIGRATION: a one-time tool (tools/identity-migration) exports Keycloak realm users (if any real
   users exist beyond dev/test — likely just seeds) → creates HbmpUsers with must_change_password=true
   and 2FA re-enrolment required (TOTP secrets are NOT portable); maps realm roles → new roles; writes
   a reconciliation report (counts in/out/skipped). Reversible: the tool never deletes Keycloak data.
2. CUTOVER: flip Kong JWKS + libs/auth Authority in one config change; run the full backend suite +
   the 17.5 E2E; keep Keycloak container present-but-unrouted for one sprint as rollback, then remove
   it from compose/kong/up.sh and delete infra/keycloak (realm JSON archived under docs/security/
   keycloak-archive/ for history).
3. DOC TRUTH: update 0A §4 + 0C stack tables (Keycloak → "ASP.NET Identity + OpenIddict, in-app"),
   18-security-model identity sections, 16-service-architecture identity row, README, CLAUDE.md
   conventions, BUILD-STATUS (tick 17.x), AUDIT-2026-07-26 (mark H6 closed-by-design, C3 closed in
   17.4), and the phase-16 prompt (strike its Keycloak-specific steps with a pointer here).
4. SECURITY GATE: add identity-service endpoints to the phase-11 DAST/pen scope + threat model
   (docs/security/threat-model-stride.md gains the issuer attack surface: token endpoint brute force,
   redirect-uri validation, PKCE downgrade, refresh replay) — with the mitigations implemented above
   explicitly cross-referenced.
Acceptance: full suite + E2E green on the new issuer; Keycloak absent from the running stack; every
doc lists the new identity architecture; migration report reconciles; rollback path documented until
the container is removed.
```

---

## Guardrails
- **Never change `libs/auth` validation logic or any service's authorization code** — if a service needs a change to accept the new token, the token is wrong, not the service.
- **No password grant, ever.** In-app UX comes from owning the pages, not from the SPA posting credentials.
- Signing keys never in git/config — OpenBao (dev file fallback gitignored); rotation must keep old `kid` published through the overlap window.
- Credentials, TOTP secrets, and recovery codes never appear in logs, audit payloads, or error messages (test-enforced).
- 2FA enforcement for admin-tier/break-glass roles is not configurable-off in production.
- Additive migrations; Keycloak stays as rollback until the cutover sprint completes; every sub-prompt ends with the full suite green (`./dotnet.sh test HbmpPlatform.sln -c Release` + `pnpm -r test`).

## Done when
- [ ] identity-service is the OIDC issuer; the frozen token contract is proven by contract tests and by untouched services returning 200/401 correctly.
- [ ] Login, TOTP challenge, recovery, password flows are **in-app pages** — bilingual, RTL, WCAG AA, Mersal-branded; axe green.
- [ ] 2FA: TOTP enrolment + recovery codes for all; **mandatory for admin-tier/break-glass with step-up**; `amr/acr` drives the existing per-scope MFA checks; WebAuthn seam documented.
- [ ] Users, roles, and scopes are created/managed **from the admin portal against the real store**, SoD-guarded, MFA-gated, idempotent, fully audited; de-provision revokes within the access TTL.
- [ ] SPA: PKCE against the new issuer, in-memory tokens + silent re-auth, real refresh on keep-alive, fail-closed unknown role — H6 closed by design.
- [ ] Keycloak removed from the stack; scope catalog lives in the DB; docs (0A/0C/16/18/README/CLAUDE.md/BUILD-STATUS/AUDIT) all tell the truth; ADR-0015 + token-contract.md merged.
- [ ] Identity endpoints added to the pen-test/threat-model scope; full backend + frontend suites green.

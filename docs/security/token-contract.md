# Frozen access-token contract (Phase 17.0)

**Status:** Frozen 2026-07-26 · **Owner:** Phase 17 (In-App Identity) · **Companion:** ADR-0015
**Extended additively** 2026-07-28 by [ADR-0021](../adr/0021-user-access-model.md) (phase 21) — see §2b. §1–§2
are unchanged and stay unchanged; extensions are additive, optional, and arrive only by ADR.

This is the **normative** description of the JWT access token the platform consumes. It is frozen at the
17.0 boundary so the new in-app issuer (ASP.NET Identity + OpenIddict, ADR-0015) can be built as a
**drop-in** for Keycloak. Every claim below is one that `libs/auth` and/or the SPA read **today**; the new
issuer MUST reproduce these shapes and values. Anything not listed here is free to change.

> **Source of truth in code:** `libs/auth/HbmpClaimTypes.cs`, `KeycloakClaims.cs`, `HbmpPrincipal.cs`,
> `MfaEvaluator.cs`, `ServiceCollectionExtensions.cs`; SPA `apps/web/src/auth/oidcClient.ts`,
> `apps/web/src/config.ts`. If code and this doc diverge, that is a bug in one of them — reconcile, don't
> silently deviate.

## 1. Envelope

| Property | Frozen value | Read by | Notes |
|---|---|---|---|
| Type | JWT access token | all services + SPA | Bearer, `Authorization: Bearer <jwt>` |
| Signature | RS256, keys via JWKS discovery | `libs/auth` (`ValidateIssuerSigningKey`) | `RequireSignedTokens = true` |
| `iss` | the issuer URL | `libs/auth` (`ValidateIssuer`, `ValidIssuers`) | 17.6: services use `http://identity-service:8080`; browser tokens carry `iss=http://localhost:8090`, accepted via `ValidIssuers`. A single ingress hostname (Tier 2/3) collapses this to one |
| `aud` | `hbmp-api` | `libs/auth` (`ValidateAudience` when set) | keep the audience name `hbmp-api` |
| `exp` / `iat` / `nbf` | present; `exp` required | `libs/auth` (`ValidateLifetime`, `RequireExpirationTime`) | 30s clock skew allowed |

## 2. Claims (frozen)

| Claim | Shape | Meaning / ABAC use | Read by |
|---|---|---|---|
| `sub` | string (stable user id) | principal identity | `HbmpPrincipal.Subject`, SPA `userId`; also `NameClaimType` |
| `roles` | array **or** repeated claim of lower-case app role names | RBAC | `HbmpPrincipal.Roles`; `RoleClaimType="roles"` |
| `scope` | space-delimited string | OAuth2 scopes (per-endpoint gate) | `HbmpPrincipal.Scopes` (also reads `scp`) |
| `tenant_id` | string | ABAC: tenant isolation | `HbmpPrincipal.TenantId` |
| `provider_id` | string (nullable) | ABAC: provider ownership | `HbmpPrincipal.ProviderId` |
| `sid` | string | session id (revoke/timeout correlation) | `HbmpPrincipal.SessionId` |
| `src_ip` | string (optional) | conditional access / audit | `HbmpPrincipal.SourceIp` |
| `amr` | array of method refs | MFA evidence | `MfaEvaluator` |
| `acr` | string | LoA / step-up evidence | `MfaEvaluator` |
| `name` / `preferred_username` | string (optional) | display name only | SPA `displayName` |

## 2b. Claims added by phase 21 (ADR-0021) — additive, optional

**Frozen means frozen: nothing in §2 changed.** These three claims are *added*. Every one is **optional** —
a token minted before phase 21 carries none of them, and MUST still yield the same `HbmpPrincipal` it always
did. That is asserted byte-for-byte over checked-in fixture tokens in
`libs/auth/Tests/TokenContractByteCompatTests.cs`, which is the guard on this section: extend the contract
only additively, and only by ADR.

| Claim | Shape | Meaning / use | Read by |
|---|---|---|---|
| `membership_id` | string (uuid), optional | The **active `tenant_membership`** — the security principal (design 40 §1). Authorization evaluates against this, never `sub`, because one identity may hold several memberships with different authority. Absent on client-credentials tokens (no membership) and on all pre-21 tokens | `HbmpPrincipal.MembershipId` |
| `level` | number (int), optional | Ordinal trust tier of the active membership's role(s), **lower = more privileged** (design 40 §2). **Tier-shaped questions only** — MFA-required tiers, peer-review-required grants. Capability questions use `scope`; never substitute one for the other | `HbmpPrincipal.Level` |
| `features` | array of strings (or repeated claim), optional | Program-enablement switches for the membership's tenant (design 40 §4). A **gate, never a grant**: a feature listed here still requires the endpoint's scope | `HbmpPrincipal.Features`, `HasFeature(...)` |

**Absent must mean absent.** `MembershipId` is null, `Features` is empty, and `Level` is **null — not 0** —
when the claim is missing or unparseable. Level 0 is the *most privileged* tier, so defaulting a missing
level to 0 would hand every legacy token platform authority; two of the byte-compat tests exist solely to
keep that from regressing.

**What is deliberately NOT in the token.** Branch scope grants resolve **per-request** from the store
(in-process + Valkey), not from claims — ADR-0021 §2 records the size measurement (a uuid grant set exceeds
the ~8 KB proxy header buffer at ~130 branches) and the two other reasons: grants are time-bounded, so a
300 s-cached copy blurs the expiry boundary; and the out-of-session evaluator has no token, so claims would
force a second resolution path. Data-dependent ABAC conditions — treating relationship, provider ownership,
case assignment, sensitive-result grants, break-glass state — are **never** claims (adaptation A5); they stay
request-time in `libs/authz`.

**Staleness.** These claims are a cache with the access-token TTL (§4) as its bound. Mutating a role grant,
override, scope grant, feature or limit — or suspending a membership or tenant — revokes the refresh family,
so the next exchange recomputes them (ADR-0021 §3).

### Role claim — important compatibility note

Keycloak emits roles **nested** under `realm_access.roles` and `resource_access.{client}.roles`.
`KeycloakClaims.ExtractRoles` reads those **and** a flat `roles`/`role` claim, lower-casing + de-duping all
of them. **The new issuer should emit the flat form: a `roles` claim** (array or repeated) of lower-case app
role names. That satisfies both `libs/auth` (which reads flat `roles`) and the SPA
(`decodeJwt` currently reads `realm_access.roles`, so the SPA's `sessionFrom`/`config.ts` mapping is updated
in **17.5** to read the flat `roles` — the one deliberate SPA change at cutover). No nested `realm_access`
shape is required going forward.

### Frozen role-name vocabulary

Lower-case, exactly these (authoritative catalog: `services/admin/Domain/RoleCatalog.cs` + realm):

```
reception  call_center  beneficiary_mgmt  finance  network_team  claims_officer
case_manager  doctor  nurse  lab_tech  radiology_tech  pharmacist
medical_approval  medical_director  provider_admin  org_admin  super_admin
```

> **29.1 — `imaging_tech` → `radiology_tech` (design 45 §1, ADR-0029).** This **amends** the frozen contract;
> it does not break it. A rename is not additive the way phase 21's three claims were, so it is handled as a
> **dual-accept window** rather than a cutover: `imaging_tech` remains a valid, grantable role name and both
> spellings resolve to the same authority until the contract step. `TokenContractByteCompatTests` carries a
> checked-in pre-switch fixture (`PreRadiologySwitchToken`) proving a token minted before the switch still
> authorises with its scopes and provider binding unchanged, and it must stay green for the whole window.
> Sequence, preconditions and the removal list: [runbooks/radiology-rename.md](../runbooks/radiology-rename.md).
>
> The **scope** vocabulary below is unchanged by the rename — see the note in §"Scope vocabulary".

> `claims_officer` is used by the realm + SPA `ROLE_MAP` but is **not yet** in `RoleCatalog.Tiers`; 17.1
> adds it to the catalog (tier T2) so the store is complete and drift ends. The SPA's clinical-title →
> portal-key mapping (`lab_tech`→`lab`, `pharmacist`→`pharmacy`, `radiology_tech`→`radiology`,
> `network_team`→`provider_admin`) stays in `config.ts`; the token carries the clinical titles above.
> During the 29.1 window `config.ts` maps **both** `radiology_tech` and `imaging_tech` to `radiology`: the SPA
> reads the raw `roles` claim, so it does not inherit `libs/auth`'s server-side alias expansion.

### Scope vocabulary (enforced per endpoint)

> **29.1 — the scope vocabulary is NOT affected by the Radiology rename.** Design 45 §1's table lists
> `imaging:*` → `radiology:*`, but no OAuth scope on this platform has ever been spelled `imaging:*`: a
> radiology technician's capabilities are `orders:read` and `orders:consume`, which are **order** scopes shared
> with the lab bench. The `imaging.*` identifiers that do exist are the SPA's client-side permission keys in
> `apps/web/src/authz/permissions.ts`, renamed at the switch. Flagged rather than silently resolved — ADR-0029.

Scopes are **data**, enforced per-endpoint by `ScopePolicyProvider`/`ScopeAuthorizationHandler`; the issuer
must be able to mint any subset. The set the platform enforces today (union across services; see the SPA's
requested `OIDC.scope` for the live list) includes at least:

```
admin:read admin:write admin:break-glass
orders:read orders:consume orders:write  pharmacy:read pharmacy:dispense
auth:read auth:review auth:decide auth:emergency auth:override auth:manual auth:ingest auth:retrospective
reception:search  emr:read emr:write encounter:write rx:write patient:write  eligibility:check
appointment:read appointment:write  document:write  case:read case:write case:manage
finance:read finance:write finance:approve finance:export finance:project provider:finance
provider:read provider:write referral:write
policy:read policy:write policy:admin policy:supervise
callcentre:read callcentre:act callcentre:interaction callcentre:verify callcentre:history:read
profile:read profile:export
claims:read claims:reconcile claims:export  reporting:read reporting:project reporting:export
notification:read notification:ingest  audit:read
```

## 3. MFA signal contract (frozen)

`MfaEvaluator.IsSatisfied(acr, amr)` returns **true** — i.e. the token evidences a second factor — when
**any** of:

- `amr` contains any of: `mfa`, `otp`, `hwk`, `totp`, `webauthn`, `sms` (case-insensitive); **or**
- `amr` contains **≥ 2 distinct** methods (e.g. `pwd` + `otp`); **or**
- `acr` ∈ { `mfa`, `aal2`, `aal3`, `loa2`, `loa3`, `2fa` } (case-insensitive).

**Issuer requirement:** after a TOTP (or higher) second factor, emit `amr` including `otp` (and/or `mfa`),
**or** set `acr=aal2`. Scope-protected endpoints require MFA by default
(`HbmpAuthOptions.ProtectedScopeRequiresMfa`), so a single-factor token is rejected for those scopes. The
SPA mirrors the same vocabulary in `oidcClient.ts` (`mfaSatisfied`).

## 4. Lifetimes (carry forward)

| Setting | Current (realm) | Carry forward |
|---|---|---|
| Access-token lifespan | 300 s | 300 s (± tuning) |
| SSO session idle | 1800 s | ≥ 1800 s, with the SPA's session-timeout warning |
| SSO session max | 36000 s | 36000 s |
| Refresh tokens | (add) | **rotation on use** |

## 5. Flows (frozen)

- **SPA:** authorization-code + **PKCE (S256)**, public client `hbmp-web`, redirect to the app root; the
  existing SPA client only re-points `authority` + the auth/token/logout URLs at the new issuer.
- **Service-to-service:** client-credentials.
- **Confidential API client id:** `hbmp-api` remains the audience/resource identifier.

## 6. Conformance checklist for 17.2 (the issuer)

A token minted by the new issuer, for each `(role, scope-set, MFA?)` the platform uses, must:

- [ ] pass `libs/auth` validation (iss/aud/signature/exp) with only a config change to `Auth:Authority`;
- [ ] yield the correct `HbmpPrincipal` (Subject, Roles lower-cased, Scopes, TenantId, ProviderId, Sid);
- [ ] make `MfaEvaluator.IsSatisfied` return the expected value for single- vs two-factor sessions;
- [ ] let a scope-protected endpoint authorize an MFA token and reject a single-factor one;
- [ ] drive the SPA `sessionFrom` mapping to the correct portal `Role` (fail-closed to `null` when the
      role is unmapped — never default to a portal).

Automate this as `IssuerContractConformanceTests` in 17.2.

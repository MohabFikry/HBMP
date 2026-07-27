# Frozen access-token contract (Phase 17.0)

**Status:** Frozen 2026-07-26 · **Owner:** Phase 17 (In-App Identity) · **Companion:** ADR-0015

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
case_manager  doctor  nurse  lab_tech  imaging_tech  pharmacist
medical_approval  medical_director  provider_admin  org_admin  super_admin
```

> `claims_officer` is used by the realm + SPA `ROLE_MAP` but is **not yet** in `RoleCatalog.Tiers`; 17.1
> adds it to the catalog (tier T2) so the store is complete and drift ends. The SPA's clinical-title →
> portal-key mapping (`lab_tech`→`lab`, `pharmacist`→`pharmacy`, `imaging_tech`→`imaging`,
> `network_team`→`provider_admin`) stays in `config.ts`; the token carries the clinical titles above.

### Scope vocabulary (enforced per endpoint)

Scopes are **data**, enforced per-endpoint by `ScopePolicyProvider`/`ScopeAuthorizationHandler`; the issuer
must be able to mint any subset. The set the platform enforces today (union across services; see the SPA's
requested `OIDC.scope` for the live list) includes at least:

```
admin:read admin:write admin:break-glass
orders:read orders:consume orders:write  pharmacy:read pharmacy:dispense
auth:read auth:review auth:decide auth:emergency auth:override auth:manual auth:ingest
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

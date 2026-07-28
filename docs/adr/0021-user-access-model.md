# ADR-0021 — User & access model: membership as the security principal

**Status:** Accepted · **Date:** 2026-07-28 · **Phase:** 21.0 · **Supersedes:** nothing
**Extends:** [ADR-0015](0015-in-app-identity-openiddict.md) (in-app identity) and the frozen token contract
(`docs/security/token-contract.md`) — **additively**.
**Design:** [`HBMP-Design/40-user-access-model.md`](../../HBMP-Design/40-user-access-model.md) ·
Build prompt: `HBMP-Design/claude-code-prompts/phase-21-user-access-model.md`

---

## Context

User management today answers "what may you do" with a single blended principal: `identity."user"` carries
`tenant_id` + `provider_id`, `identity.user_role` binds it to roles, `identity.role_scope` maps roles to the
~74-key scope catalog, and `admin.user_branch_assignment` bolts on branch reach. Four different questions —
*who are you here*, *what may you do*, *over which data*, *is it enabled for this organization* — are answered
by one flattened structure, so none of them can vary independently. A doctor who is also a provider-admin at a
partner NGO has no way to be two principals; a branch grant for October has no way to expire.

Phase 21 restructures this onto four independent axes, adopted from a proven reference design. That design is
a **SaaS multi-tenancy pattern**, and four of its choices are actively unsafe for a platform holding refugee
PHI. Doc 40 §0 records six adaptations; they are normative and this ADR carries them verbatim.

## Decision

### 1. Adopt the reference design with adaptations A1–A6 (doc 40 §0, normative)

| # | Reference design says | We do instead | Why |
|---|---|---|---|
| A1 | Superuser wildcard = **total bypass** of permissions *and* the scope predicate | Platform-admin flag grants **platform administration only** (tenants, catalog, identities, infra config). It is **never a PHI wildcard**: it does not bypass min-necessary projection, ABAC conditions, RLS, branch scope, or the [37 §6](../../HBMP-Design/37-branch-scoping-and-clinical-sensitivity.md) sensitive gate. The only elevation into clinical data is **break-glass** — justified, time-boxed, loud | Min-necessary is invariant 2 of the whole platform. A standing account that can read any PHI row is the single credential whose theft undoes everything; the reference design is a SaaS pattern, not a healthcare one |
| A2 | Out-of-scope tenant-switch requests are **silently ignored** | **403 + audit event** (`TenantSwitchDenied`). Nothing security-relevant is silent | [19-audit-strategy.md](../../HBMP-Design/19-audit-strategy.md): every denied privileged action is evidence. Silent drops also hide bugs |
| A3 | Namespace-prefix fallback: "holds *any* key in a module ⇒ may see the module" — a deliberate gate-loosening for legacy principals | **Not adopted.** Route → explicit `module:view` key mapping only. We have no pre-existing principals to grandfather; deny-by-default stays intact | The source itself calls it "a migration affordance [that] deliberately loosens the gate". We don't have the migration, so we don't take the loosening |
| A4 | Entitlements = commercial plan features; failure remedy is "**upgrade**" (sell) | **Program enablement**: per-tenant/partner feature switches + numeric caps set by platform administration (e.g. a partner NGO without the claims module; a cap on active provider users). Distinct error `PROGRAM_NOT_ENABLED` / `PROGRAM_LIMIT_REACHED`; remedy is "contact Mersal programme administration" | Mersal is a charity — there is no upsell. The *separation* (authorization failure ≠ enablement failure, different codes, different remedies) is the valuable part and is kept |
| A5 | Effective permissions, scope units and entitlements all baked into the token; every check token-only | Adopted **for set-membership checks only**, inside the frozen token contract (ADR-0015, extended by this ADR — never broken). **Data-dependent ABAC conditions are never baked into the token**: treating relationship, provider ownership, case assignment, sensitive-result grants and break-glass state are evaluated at request time by `libs/authz`, as today | A token claiming "may read patient X" is stale the moment the treating relationship ends. Claims answer *role-shaped* questions; ABAC answers *data-shaped* ones. Collapsing them is exactly the "two questions collapsed into one" failure the source warns about |
| A6 | Device revocation fail-open on datastore error | Adopted, **bounded and alarmed**: stateless access-token validation is unaffected by store outages by construction; the *revocation-list* check degrades open on infra error with a Prometheus alarm, and exposure is bounded by the short access TTL ([18 §9](../../HBMP-Design/18-security-model.md)). An **explicit** revoke is always fail-closed | Their rationale (an outage must not sign out every clinician mid-shift) is a patient-safety argument here, stronger than in SaaS. The bound + alarm makes it auditable |

### 2. Token claims — three additive claims, and branch grants deliberately left OUT

`membership_id`, `level` and `features` are added to the access token. Exact JSON paths and shapes are in
`docs/security/token-contract.md` §2b. All three are **optional**: a token minted before this ADR carries none
of them and parses into an identical `HbmpPrincipal`, proven byte-for-byte by
`libs/auth/Tests/TokenContractByteCompatTests.cs`.

**Branch scope grants are NOT carried in the token.** They resolve per-request (in-process + Valkey cache,
21.3). Doc 40 §5 left this open — "in the token only if small; otherwise resolved per-request … decided in the
ADR, not ad hoc" — so here is the measurement and the decision.

#### The measurement

Token sizes computed over a realistic `medical_director` claim set (25 scopes — the widest role in the live
catalog — plus `iss/aud/exp/iat/nbf/jti/sub/roles/scope/tenant_id/provider_id/sid/src_ip/amr/acr/name/preferred_username`),
RS256/2048 signature (342 B base64url), measured as the full `Authorization: Bearer …` header line against an
**8 KB** budget. That budget is the binding one in the deployed path: neither Kong nor Kestrel has a header
limit configured anywhere in `infra/`, so stock defaults apply — nginx/Kong `large_client_header_buffers 4 8k`
(~8 KB per header line) is the tightest, well below Kestrel's 32 KB total.

| Variant | Header bytes | % of 8 KB |
|---|---:|---:|
| Baseline (frozen contract today) | 1 523 | 18.6 % |
| **+ `membership_id`, `level`, `features`** (this ADR) | **1 747** | **21.3 %** |
| + 6 branch grants as uuid (today's live branch count) | 2 076 | 25.3 % |
| + 20 branch grants as uuid | 2 804 | 34.2 % |
| + 50 branch grants as uuid | 4 364 | 53.3 % |
| + 100 branch grants as uuid | 6 964 | **85.0 %** |
| + 200 branch grants as uuid | 12 164 | **148.5 % — exceeds the buffer** |
| + 200 branch grants as 8-char code | 4 431 | 54.1 % |

The three new claims cost **224 bytes (+2.7 % of budget)** — unconditionally safe.

#### Why branch grants stay out

1. **The only claim whose size grows with the charity's expansion.** Every other claim is bounded by the role
   catalog, which changes by design review. Branch count grows when Mersal opens clinics. At ~100 branches the
   header is 85 % consumed and at ~130 it exceeds the buffer — and the failure mode is not a clean error but
   `400 Request Header Or Cookie Too Large` from the proxy for *every* request by the widest-reach users
   (network team, medical director), i.e. an outage that arrives with a ribbon-cutting. Encoding branches as
   `varchar(8)` codes rather than uuids defers the cliff but does not remove it, and `libs/auth`'s
   `IBranchContext`/`BranchAssignment` are keyed on `Guid BranchId` throughout — switching the wire format to
   codes to buy header room would be the token dictating the domain model.
2. **Grants are time-bounded (doc 40 §3); a token is a 300 s cache.** Baking an expiring set into a cached
   token makes the expiry boundary fuzzy by up to the access TTL, precisely at the moment the grant is being
   withdrawn. Resolving at request time makes 21.3's "expired yesterday ⇒ out of the set today" exact rather
   than eventually-exact.
3. **Invariant 5 wants one resolution path.** Mode 2 (out-of-session: supervisor-override validation,
   background jobs, admin preview) has no token at all. If grants rode in claims, mode 2 would need a second
   resolution path against the store — a divergence the parity suite would then have to police. Resolving from
   the store in both modes means there is only one path to keep honest.

Cost accepted: one cache lookup per branch-scoped request. Mitigated by the same in-process + Valkey layering
as the mode-2 effective-set cache (TTL ≤ 60 s, explicit invalidation on grant mutation).

### 3. Staleness and re-resolution

The token is a cache of role-shaped facts; staleness is the price of not hitting the store per request. Bounded by:

- **Access TTL 300 s** (token-contract.md §4) — the maximum window in which a revoked grant still authorizes.
- **Re-resolution triggers** — each revokes the refresh family (phase-17 machinery), so the next exchange
  recomputes claims from the store: role grant/revoke · membership override mutation · scope-grant mutation ·
  feature/limit change · membership suspension · membership switch · tenant suspension.
- **De-provisioning** keeps phase-17 semantics: disable → revoke family → access ends within the access TTL.

Data-dependent conditions are exempt from this reasoning entirely because they are never cached in the token (A5).

## Consequences

**Good.** One identity can hold several memberships with genuinely different authority. Branch reach becomes
attributed and self-expiring instead of a permanent row someone must remember to delete. Enablement failures
stop being indistinguishable from permission failures, so "contact Mersal" and "ask your administrator" reach
the right person. Token size stays flat as the branch network grows.

**Costs.** A per-request grant lookup on branch-scoped reads. Two evaluation entry points that must not
diverge — mitigated by the CI-pinned parity suite, which is invariant 5 and the standing risk the reference
design itself names. A backfill that must be provably access-neutral.

**Divergences from the build prompt found while writing this ADR** (flagged per CLAUDE.md rather than silently
followed; the prompt describes tables that do not exist under those names):

| Prompt says | Reality on this branch | Effect |
|---|---|---|
| `app_user`, `app_role`, `app_user_role` | `identity."user"`, `identity.role`, `identity.user_role` | naming only; 21.1 uses the real names |
| `role_scope(role_id, scope_id)` | `role_scope(role_name, scope_name)` — varchar keys, no FK to `role` | 21.1's tenant-local roles must key on names or introduce ids deliberately |
| `app_role.level int` to be seeded | `identity.role.sensitivity_tier varchar(2)` (`T1`/`T2`/…) already exists | 21.2 maps the existing tier rather than adding a parallel axis |
| `branch_scope_grant(membership_id, branch_code)`, sentinel `branch_code = '__none__'` | `admin.user_branch_assignment(branch_id uuid)`; `provider.branch` has both `branch_id` and `branch_code varchar(8)`; `libs/auth` `IBranchContext`/`BranchAssignment` are uuid-keyed | 21.3 keys grants on `branch_id uuid` and uses a **reserved uuid** sentinel, not the string `'__none__'` |
| `user_branch_assignment` implied to sit in `identity` | it is in the **`admin`** schema | 21.3's migration is an admin-service migration |

**Note on the live dev database (operational, not a code defect).** `RoleScopeMatrixTests` — the tests
asserting the DB catalog equals the frozen vocabulary — are gated on `IDENTITY_TEST_DB`, so they skip on a
DB-less run. Pointed at the **running dev DB** on `:55432` they fail (19 roles against the frozen 17; 71
scopes against 74, missing `patient:read`). Pointed at a **freshly migrated** database they all pass — 39/39.
So the migrations and the code are consistent with each other, and CI is unaffected (`print-test-db-env.sh`
does export `IDENTITY_TEST_DB`, against a clean Postgres service): the dev database has simply drifted behind
the phase-19/20 migrations. Re-migrate it rather than changing code. Phase 21 work uses a scratch
`hbmp_p21` database provisioned by `tools/ci/apply-migrations.sh` for exactly this reason.

### 4. Divergences decided while BUILDING 21.2–21.6

| Prompt says | What was built | Why |
|---|---|---|
| Overrides pass through `SegregationOfDuties` unchanged | added `TokensForScope` / `EvaluateScopeGrant` | overrides hand out CATALOG KEYS; SoD is defined over DUTIES, and no scope key is spelled like a duty token. Unmapped, the check would silently never fire — a control that always passes, worse than none. The map covers only the duties 10-role-matrix §7 actually splits; everything else is genuinely SoD-neutral |
| — | SoD reports only conflicts a grant INTRODUCES | the coarse `finance` role already implies both payment halves, so an override naming one changes nothing. Refusing it blocks a no-op, leaves the real problem (the role definition) untouched, and trains administrators to read SoD refusals as noise |
| Kong scopes `admin:access:read/write`, `platform:admin` | kept `admin:read` / `admin:write` | those three would extend the FROZEN scope vocabulary to split a permission already enforced correctly, and platform administration is already modelled by `is_platform_admin` + the catalog's `is_platform_admin_key` (A1). A parallel scope is a second mechanism for one rule, and two mechanisms drift |
| `tenant_membership` gets RLS "like every other tenant-scoped table" | deliberately NOT RLS'd | the issuer resolves a login BY USERNAME to discover which tenants an identity belongs to, BEFORE any request-scoped `app.tenant_id` exists. A tenant predicate makes the membership chooser unable to read the rows it exists to list — i.e. it breaks login. `tenant_id` here is a CLAIM SOURCE, not a row filter |
| revoke the refresh family on every grant mutation | invalidate the mode-2 cache only | the token endpoint already re-resolves the membership AND recomputes the effective set on every exchange, so the next refresh picks changes up regardless. Revoking the family forces a full re-login, which is strictly more disruptive than the stated goal ("so the next exchange recomputes") |
| "extend the existing no-hash-in-audit grep test" | wrote it | no such test existed. `NoCredentialMaterialInAuditTests` now scans all production code, with a self-test proving it fires on the realistic mistake |
| branch grants keyed on `membership_id` | carries `membership_id` AND `subject_user_id` | memberships live in the identity schema; admin-service must not read across a service boundary (the same rule that kept identity out of `admin.tenant`). The copy preserves `subject_user_id`, authoritative today; a later migration contracts to membership-only |

### 5. Known defects found and fixed while building this phase

Recorded because each was live on `main` before phase 21, and each is the kind that stays invisible:

- **`RowScope.WithBranchScope` fell open.** A BranchScoped caller whose active branch failed to resolve got
  `BranchUnrestricted = true`. An empty branch predicate does not mean "no branches" — it means every branch
  in the tenant. Fixed with the reserved-uuid sentinel; the fail-closed test asserts zero rows AND the
  negation (that the same dataset would have returned rows under an empty predicate), so it cannot rot into
  a tautology.
- **Accounts created after migration 0010 would have been locked out.** `0010` backfills memberships but is
  a migration, not a trigger, so any account created afterwards had none — it would authenticate and then be
  refused at authorize with no way to fix itself. `EnsureMirroredAsync` keeps `user_role` and the membership
  in lockstep for the expand phase, and mirrors REMOVALS too, without which role revocation is cosmetic.
- **A membership-chooser redirect loop.** A cookie naming a suspended membership with exactly one other left
  bounced authorize → chooser → authorize forever, because the chooser redirected straight back when only
  one option remained.
- **`UpdateSecurityStampAsync` does not revoke OpenIddict refresh tokens.** The token endpoint checks
  `IsActive` but never compares security stamps, so the deactivate path (17.4) did not end live sessions the
  way its comment claimed — an off-boarded account kept every session it already had until each happened to
  refresh. Deactivate now calls `SessionService.RevokeAllAsync`, which fails CLOSED: an administrator told
  the account is deprovisioned must not be told that on the strength of a revocation that did not persist.

## Alternatives considered

- **Branch grants in the token, capped with an overflow flag** — embed when the set is small, fall back to
  store resolution above a threshold. Rejected: it makes the size cliff a *conditional* code path, so the
  rarely-exercised branch is the one that runs for the highest-reach users, and it still leaves mode 2 needing
  the store path. Two paths, one of them rarely tested, on a security boundary.
- **Keep the identity as the principal and add a "current tenant" claim** — cheaper, but a single blended
  principal cannot express different authority per membership, which is the entire point (invariant 1).
- **Adopt the reference superuser wildcard and rely on audit to catch abuse** — rejected outright by A1.
  Detection after the fact is not a control for a standing PHI-wildcard credential.

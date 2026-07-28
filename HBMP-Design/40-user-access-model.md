# 40 — User & Access Model (identity / membership / authority / reach / enablement)

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [10-role-matrix.md](10-role-matrix.md) · [11-permission-matrix.md](11-permission-matrix.md) · [18-security-model.md](18-security-model.md) · [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md) · ADR-0015 (in-app identity), `docs/security/token-contract.md`
> Build prompt: [claude-code-prompts/phase-21-user-access-model.md](claude-code-prompts/phase-21-user-access-model.md)

**What this is.** A restructuring of user management around four independently answerable questions — *who are you here* (membership), *what may you do* (effective permission set), *over which data* (scope), *is it enabled for this organization* (program enablement) — adopted from a proven reference design, **adapted where that design conflicts with Mersal's security rules**. It reorganizes what phases 8b/14/17 built; it does not replace them.

---

## 0. Adaptations — where the reference design bends to our rules

The source design is structurally sound. Four of its choices are incompatible with this platform and are **changed, not copied**. This table is normative; the build prompt enforces it.

| # | Reference design says | We do instead | Why |
|---|---|---|---|
| A1 | Superuser wildcard = **total bypass** of permissions *and* the scope predicate | Platform-admin flag grants **platform administration only** (tenants, catalog, identities, infra config). It is **never a PHI wildcard**: it does not bypass min-necessary projection, ABAC conditions, RLS, branch scope, or the [37 §6](37-branch-scoping-and-clinical-sensitivity.md) sensitive gate. The only elevation into clinical data is **break-glass** — justified, time-boxed, loud | Min-necessary is invariant 2 of the whole platform. A standing account that can read any PHI row is the single credential whose theft undoes everything; the reference design is a SaaS pattern, not a healthcare one |
| A2 | Out-of-scope tenant-switch requests are **silently ignored** | **403 + audit event** (`TenantSwitchDenied`). Nothing security-relevant is silent | [19-audit-strategy.md](19-audit-strategy.md): every denied privileged action is evidence. Silent drops also hide bugs |
| A3 | Namespace-prefix fallback: "holds *any* key in a module ⇒ may see the module" — a deliberate gate-loosening for legacy principals | **Not adopted.** Route → explicit `module:view` key mapping only. We have no pre-existing principals to grandfather; deny-by-default stays intact | The source itself calls it "a migration affordance [that] deliberately loosens the gate". We don't have the migration, so we don't take the loosening |
| A4 | Entitlements = commercial plan features; failure remedy is "**upgrade**" (sell) | **Program enablement**: per-tenant/partner feature switches + numeric caps set by platform administration (e.g. a partner NGO without the claims module; a cap on active provider users). Distinct error `PROGRAM_NOT_ENABLED` / `PROGRAM_LIMIT_REACHED`; remedy is "contact Mersal programme administration" | Mersal is a charity — there is no upsell. The *separation* (authorization failure ≠ enablement failure, different codes, different remedies) is the valuable part and is kept |
| A5 | Effective permissions, scope units and entitlements all baked into the token; every check token-only | Adopted **for set-membership checks only**, inside the frozen token contract (ADR-0015, extended by ADR — never broken). **Data-dependent ABAC conditions are never baked into the token**: treating relationship, provider ownership, case assignment, sensitive-result grants and break-glass state are evaluated at request time by `libs/authz`, as today | A token claiming "may read patient X" is stale the moment the treating relationship ends. Claims answer *role-shaped* questions; ABAC answers *data-shaped* ones. Collapsing them is exactly the "two questions collapsed into one" failure the source warns about |
| A6 | Device revocation fail-open on datastore error | Adopted, **bounded and alarmed**: stateless access-token validation is unaffected by store outages by construction; the *revocation-list* check degrades open on infra error with a Prometheus alarm, and exposure is bounded by the short access TTL ([18 §9](18-security-model.md)). An **explicit** revoke is always fail-closed | Their rationale (an outage must not sign out every clinician mid-shift) is a patient-safety argument here, stronger than in SaaS. The bound + alarm makes it auditable |

Everything else — the membership principal, the set algebra with deny-wins, tenant-local roles from templates, time-bounded scope grants, the precedence chain with dual failure semantics, sentinel fail-closed predicates, deprecation with `replacedBy`, ambient attribution, the access-review snapshot, parity-tested dual evaluation — is adopted as described.

## 1. Subject model — identity ≠ membership ≠ tenant

Three entities, deliberately not collapsed:

- **Identity** (`identity` schema, exists today as `app_user`): global account — credentials, verification/lockout/2FA state, and the platform-admin flag (per A1: administration, never PHI). Exists independent of any organization.
- **Membership** (new: `tenant_membership`): the join of an identity to a tenant, and **the actual security principal**. Carries role(s), status (`Invited/Active/Suspended/Ended`), lifecycle timestamps, soft-delete, and owns the per-principal overrides and scope grants below. One identity, several memberships, different authority in each — e.g. a doctor who is also a provider-admin at a partner organization, under two memberships, never one blended principal.
- **Tenant** (exists): the isolation boundary and the **owner of roles**.

**All authorization evaluates against the active membership, never the identity.** The active membership is selected at login (single-membership identities skip the chooser), asserted in the token (`membership_id`, `tenant`), and switched only by re-resolution (§5). Today's `app_user_role` rows migrate to memberships in a backfill; `provider_id` becomes a property of the membership, not the user.

## 2. Authority model — RBAC + per-membership overlay

Four layers, evaluated as set algebra:

| Layer | Owner | Mersal mapping |
|---|---|---|
| Permission catalog | Platform | The existing scope catalog (`scope` table, ~44 keys, `module:action` form) — **already flat namespaced keys**; gains `deprecated` + `replaced_by` (§6) |
| Role → permission grants | Tenant | `role_scope`, becoming **tenant-local**: roles seeded from the [10-role-matrix.md](10-role-matrix.md) templates at tenant creation, then owned by the tenant and free to diverge. Templates are not live inheritance |
| Membership → overrides | Membership | **New**: per-membership exceptions, each an explicit `Allow` or `Deny` of a catalog key, with reason, grantor, and optional expiry |
| Platform-admin | Platform | Per **A1**: short-circuits *platform-administration* keys only. No wildcard over clinical/benefit keys — the evaluator hard-excludes them |

**Effective set = (role grants ∪ membership allows) − membership denies**, deny wins over allow, always. Overrides are the SoD-guarded exception path (the existing `SegregationOfDuties` engine vets overrides exactly as it vets role grants — an override that would create a forbidden combination is a 409, not a bypass).

**The ordinal axis.** A coarse `role.level` (lower = more privileged) coexists with the fine keys, answering only tier-shaped questions: "is this an administrative persona" (MFA-required tiers, peer-review-required grants per 8b). Discipline rule, enforced in review: **capability questions use keys; trust-tier questions use level; never substitute one for the other.**

**What this layer never answers:** anything data-shaped. Field projection ([11](11-permission-matrix.md), `FieldProjector`), row conditions (`AbacConditions`), RLS and the sensitive gate all remain downstream and unweakened (A5).

## 3. Scope model — authority vs. reach

Holding `orders:consume` says nothing about *which branch's* orders. Reach is a second dimension:

- `user_branch_assignment` is restructured onto the membership as **time-bounded scope grants**: `(membership_id, branch_code, valid_from, valid_until NULL, granted_by, granted_reason)`, soft-deleted, audited. "Doctor covering Alexandria for October only" becomes a first-class, expiring, attributed fact instead of a permanent row someone must remember to delete.
- **Active-scope resolution precedence** (formalizing today's `X-Active-Branch`): ① explicit `X-Active-Branch` header → ② persisted user preference → ③ membership's home branch → ④ first accessible branch. Dual failure semantics: an **explicit header out of scope → 403 + audit** (a programmatic caller must never be silently redirected onto a different dataset); a **stale soft preference → skip and fall through** (a remembered UI selection must not break the session).
- **Reads:** scope predicate injected via the existing `RowScope`/RLS machinery. Unresolvable scope injects a **sentinel that matches nothing** — never an empty predicate, because an empty predicate leaks the whole tenant. Fail-closed by construction (this is already the RLS no-GUC behaviour; the sentinel extends it to the application predicate).
- **Writes:** validated up front against the grant set, rejected if out of scope. No implicit default.
- **Nobody bypasses the predicate on clinical data** (A1). Platform administration operates on administrative entities and needs no branch reach into PHI. `MemberScoped`/`ProviderScoped` resources ([37 §3](37-branch-scoping-and-clinical-sensitivity.md)) keep their own reach rules — branch grants govern `BranchScoped` resources only.

## 4. Program enablement — the third, orthogonal gate

Per **A4**: per-tenant feature switches (booleans: `claims`, `callcentre`, `interop`, …) and numeric caps (active users per role tier, storage, monthly extracts), owned by platform administration, versioned and audited.

- Checked **after** authorization, **before** execution. A fully authorized principal can still get `403 PROGRAM_NOT_ENABLED` (RFC 7807, distinct `type`) — with a distinct UI treatment ("not enabled for your organization — contact Mersal programme administration"), never conflated with a permission denial, because the remedies differ: one is "ask your administrator", the other is "ask Mersal".
- **Limits are enforced by counting live rows at mutation time** inside the transaction — no drift-prone counters.
- Enablement never *grants*: a switched-on module still requires the permission; a switched-off one hides nothing retroactively from audit.

## 5. Evaluation — precomputed claims + out-of-session evaluator, parity-tested

**Mode 1 — in-session (the token).** The effective permission set, active `membership_id`/`tenant`, role(s), level, and enabled features are resolved once at token issuance and carried in the signed token, **within the frozen token contract** — existing claims keep their exact paths (`scope` stays space-delimited; `libs/auth` is untouched); new claims (`membership_id`, `level`, `features`) are additive, recorded in `docs/security/token-contract.md` by ADR, and covered by the byte-compat contract test. Branch grants ride in the token only if small; otherwise resolved per-request from a Valkey cache — decided in the ADR, not ad hoc. Every set-membership check is then an in-memory lookup.

The token is a cache; staleness is the price. Mitigations: short access TTL ([18 §9](18-security-model.md)), refresh rotation, and **re-resolution triggers** — membership switch, role/override/scope-grant mutation, tenant suspension — each of which revokes the refresh family so the next exchange recomputes claims. De-provisioning keeps its phase-17 semantics: disable → revoke family → access ends within the access TTL.

**Mode 2 — out-of-session.** Recomputes *any* membership's effective set directly from the store, for: **supervisor-override flows** (the approver is validated server-side out-of-band — the acting user's token must never carry the elevated right), **background jobs** (no session), and admin previews ("what would this user see"). Backed by a short-TTL in-process + Valkey cache with explicit invalidation on grant mutation.

**Both modes implement one algebra.** One shared evaluator library (`libs/authz`), two entry points, and a **parity test suite** that runs the same fixture matrix through both and fails on any divergence — duplicated evaluation logic is the standing risk the source names, and the tests are the mitigation.

Data-dependent ABAC stays request-time in both modes (A5).

## 6. Enforcement — layered, only the innermost trusted

Unchanged from platform law, restated because the reference design agrees with it:

| Layer | Effect | Trust |
|---|---|---|
| Navigation / UI affordances | Hide what can't be used | **Cosmetic only** |
| Route entry guard | Redirect to denial / not-enabled page | Advisory |
| API handler guard (scope + level + enablement) | 401/403 | **Authoritative** |
| Data predicate (RowScope + RLS + field projection) | Rows/fields outside scope don't exist in the payload | **Authoritative** |

Nothing is protected unless the API guard **and** the data predicate both enforce it. UI hiding is a usability courtesy — a hand-crafted request must hit the same wall. Route → explicit `module:view` keys per **A3**; no prefix fallback.

**Governance mechanics adopted:** *Deprecation* — catalog entries carry `deprecated` + `replaced_by`; deprecated keys keep working, are muted in the admin UI, are excluded from newly seeded roles, and log a one-time structured warning per consumer on use, so umbrella-permission splits are driven from logs, without a flag day. *Ambient attribution* — the acting membership is pushed into a request-scoped context; the existing stamping interceptors auto-fill created/updated-by, so attribution cannot be forgotten at a call site. *Access review* — a point-in-time snapshot per tenant: every membership with roles, status, level, scope grants (incl. expiry), overrides with reasons, per-role key and holder counts; exportable, itself audited, feeding the 8b access-review process. *Membership switching* — re-authentication-grade: new claim resolution, audited `MembershipSwitched`, home membership pinned for reversibility; cross-tenant switching without a target membership requires the platform-admin flag and yields **administrative** reach only, else **403 + audit** (A2).

**Authentication controls stay authentication's** (phase 17, unchanged): lockout, verification gating, tenant-status gating at sign-in (a suspended tenant's members cannot mint tokens), per-identity session caps with revocation, login-attempt history. Revocation degradation per **A6**.

## 7. Invariants

1. Authorization is evaluated against the **active membership**, never the identity.
2. **Deny wins.** Effective set = (role ∪ allows) − denies; absence of membership → no access; unresolvable scope → sentinel matching nothing; missing session → reject.
3. **No PHI wildcard exists.** The platform-admin flag never bypasses projection, ABAC, RLS, branch scope, or the sensitive gate. Break-glass remains the only elevation, and it is loud.
4. Token claims answer role-shaped questions only; **data-dependent conditions are evaluated at request time** in both evaluation modes.
5. Both evaluators share one algebra, **proven by parity tests**.
6. Every grant, override, scope grant, enablement change, membership switch and **denied** switch/scope attempt is audited with actor + reason.
7. UI hiding is never enforcement; API guard + data predicate are the only trusted layers.
8. The frozen token contract is extended only by ADR, never broken; the contract test stays green throughout.

## 8. Acceptance criteria

- [ ] `tenant_membership` is the security principal; one identity holds ≥2 memberships with different roles and the evaluator proves different effective sets under each.
- [ ] Set algebra with deny-wins and SoD-guarded overrides; platform-admin short-circuits administration keys only — a test asserts it **cannot** read a PHI row, a projected field, or a sensitive result.
- [ ] Time-bounded, attributed scope grants; precedence chain with hard-reject (explicit header) vs skip (soft preference), both audited where denied; sentinel predicate proven by a test that breaks resolution and asserts **zero rows**, not all rows.
- [ ] Enablement gate with distinct RFC 7807 types and UI treatments; caps counted live in-transaction; enablement never substitutes for permission.
- [ ] Claims within the frozen contract (byte-compat test green); re-resolution triggers revoke the refresh family; out-of-session evaluator + cache invalidation; **parity suite green**.
- [ ] Deprecation lifecycle, ambient attribution, access-review snapshot, membership-switch guards (A2 semantics) all built and tested.
- [ ] Existing suites — min-necessary, RLS isolation, SoD, sensitive-gate, phase-17 auth — remain green: this phase restructures *who holds authority*, and must not move *what authority reaches*.

---

### Cross-references
Roles/SoD: [10](10-role-matrix.md) · Min-necessary: [11](11-permission-matrix.md) · Security/session: [18](18-security-model.md) · Branch scope & sensitivity: [37](37-branch-scoping-and-clinical-sensitivity.md) · Audit: [19](19-audit-strategy.md) · Identity: ADR-0015 + `docs/security/token-contract.md` · Build: [claude-code-prompts/phase-21-user-access-model.md](claude-code-prompts/phase-21-user-access-model.md)

# Phase 21 — User & Access Model (membership, overlay, scoped reach, enablement)

**Goal:** Restructure user management onto four independent questions — *who are you here* (membership), *what may you do* (effective set with deny-wins overrides), *over which data* (time-bounded scope grants with a precedence chain), *is it enabled for this organization* (program enablement) — **on top of** the phase-17 identity store and **without weakening one existing gate**.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> ⚠️ **This phase reorganizes who holds authority. It must not move what authority reaches.** The reference design this comes from is a SaaS pattern; [`../40-user-access-model.md`](../40-user-access-model.md) **§0 lists six adaptations (A1–A6) that are normative** — read them before any code. The two that matter most:
> - **A1 — no PHI wildcard.** The platform-admin flag short-circuits platform-administration keys only. It never bypasses FieldProjector, AbacConditions, RLS, branch scope, or the ../37 §6 sensitive gate. Break-glass stays the only elevation.
> - **A5 — tokens carry role-shaped claims only.** Treating relationship, provider ownership, case assignment, sensitive grants, break-glass state stay request-time in `libs/authz`. Never bake them into claims.
>
> **Sequencing:** after phase 17 (identity-service is the issuer) and phase 18 Gate B/C (RLS engaged, secrets clean). Touches `services/identity`, `services/admin`, `libs/auth`, `libs/authz`, `libs/data`, `apps/web` admin portal.

## Skills to activate
> `mersal-platform-architect`, `refugee-healthcare-management` (always-on), plus `healthcare-database-architect`, `healthcare-business-rules-engine`, `healthcare-uiux-designer` (21.6). Index: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first
- [`../40-user-access-model.md`](../40-user-access-model.md) — **AUTHORITATIVE**, especially §0 (adaptations) and §7 (invariants).
- [`../10-role-matrix.md`](../10-role-matrix.md) (roles + SoD §7) · [`../11-permission-matrix.md`](../11-permission-matrix.md) · [`../18-security-model.md`](../18-security-model.md) §9 (sessions) · [`../37-branch-scoping-and-clinical-sensitivity.md`](../37-branch-scoping-and-clinical-sensitivity.md) §3 · [`../19-audit-strategy.md`](../19-audit-strategy.md).
- **Existing code you are restructuring, not replacing:** `services/identity` (app_user/app_role/scope/role_scope, OpenIddict issuer, `docs/security/token-contract.md` + its byte-compat contract test), `services/admin` (SegregationOfDuties engine, access review, session policy), `libs/auth` (HbmpPrincipal, ScopePolicyProvider, IBranchContext), `libs/authz` (FieldProjector, RowScope, AbacConditions, BreakGlass), `libs/data` (RlsConnectionInterceptor, TenantStampingInterceptor), `user_branch_assignment`.
- `docs/HANDOFF.md` gotchas (`./dotnet.sh`, PG :55432, .NET 8 only, analyzers-as-errors, CPM, pnpm).

## THE INVARIANTS (from ../40 §7 — restated because they are the phase)
1. Authorization evaluates against the **active membership**, never the identity.
2. **Deny wins**; no membership → nothing; unresolvable scope → sentinel matching **zero** rows; missing session → reject.
3. **No PHI wildcard.** Platform-admin ≠ clinical access, ever.
4. Claims are role-shaped; data-shaped conditions stay request-time in both evaluation modes.
5. One set algebra, two entry points, **parity tests** between them.
6. Every grant/override/scope/enablement mutation AND every denied switch or out-of-scope header is audited.
7. The frozen token contract is extended additively by ADR; the contract test never goes red.

---

## Prompts

### 21.0 — ADR + adaptation record (small commit, do first)
```text
Write docs/adr/0021-user-access-model.md: context (reference design summary), decision (adopt with
adaptations A1–A6 from ../40 §0 — copy the table in verbatim, it is normative), the token-claim
decision (which new claims are added: membership_id, level, features; whether branch grants ride in
the token or resolve from Valkey per-request — decide by measuring realistic grant-set size against
../18 token-size limits, and RECORD the measurement), and consequences (staleness window = access TTL;
re-resolution triggers listed).
Update docs/security/token-contract.md ADDITIVELY: new claims documented with exact JSON paths;
existing claims untouched. Extend the byte-compat contract test: old fixture tokens MUST still parse
into the same HbmpPrincipal (new claims optional-absent), plus a new fixture with the new claims.
Acceptance: ADR merged; contract test green on BOTH old and new fixtures.
```

### 21.1 — Subject model: `tenant_membership` as the security principal
```text
Read ../40 §1. Schema `identity`, hand-authored migration, house style (snake_case, uuid v7, audit
columns, soft-delete, *_history twins, RLS + tenant stamping like every other tenant-scoped table).

- tenant_membership: membership_id PK, user_id FK app_user, tenant_id, provider_id NULL (moves here
  from app_user), status CHECK IN ('Invited','Active','Suspended','Ended'), home_branch_code NULL,
  invited_at/activated_at/ended_at, UNIQUE(user_id, tenant_id) WHERE NOT deleted.
- membership_role: membership_id FK, role_id FK app_role — replaces app_user_role as the live source.
- Roles become TENANT-LOCAL: app_role gains tenant_id; a role_template table holds the ../10 seeds;
  tenant creation copies templates → tenant roles (copy, not live inheritance — divergence is allowed
  and audited). Platform-level roles (super_admin) stay tenant_id NULL and are flagged platform_only.
- app_user: keep credentials/2FA/lockout + is_platform_admin flag (default false, grantable only by
  another platform admin, both sides audited). REMOVE role semantics from the identity level.
- BACKFILL migration: every app_user_role row → an Active membership in the user's current tenant with
  that role; provider_id copied onto the membership; user_branch_assignment rows re-pointed to
  membership_id (21.3 restructures them further). Expand/contract: keep app_user_role readable until
  21.2 flips the evaluator, then drop in a follow-up migration.
- Login flow (identity-service): single membership → auto-select; multiple → membership chooser page
  (Razor, design tokens, AR/EN RTL, axe clean). Selected membership_id + tenant go into the token per
  21.0. Suspended/Ended membership → not listed; suspended TENANT → sign-in blocked with a clear,
  non-enumerating message (../18 tenant-status gating).
ACCEPTANCE
- Given one identity with memberships in two tenants holding different roles, When they log in and pick
  each, Then tokens differ in membership_id/tenant/roles and the SAME request under each token hits
  different RLS partitions (prove with the existing two-tenant RLS test harness).
- Given an Ended membership, Then it cannot be selected and existing refresh tokens for it are revoked.
- Given the backfill, Then every pre-existing user keeps EXACTLY their previous effective access
  (snapshot before/after comparison test — no gained scopes, no lost scopes).
TESTS: uniqueness, lifecycle transitions, backfill snapshot-parity, chooser a11y, RLS-per-membership.
```

### 21.2 — Authority: catalog + overrides + one evaluator, two modes
```text
Read ../40 §2 and §5. This is the core.

CATALOG (identity schema)
- scope table gains: deprecated bool default false, replaced_by varchar NULL, is_platform_admin_key
  bool (marks administration keys: tenant management, catalog management, identity administration).
- membership_override: override_id PK, membership_id FK, scope_key FK, effect CHECK IN ('Allow','Deny'),
  reason varchar(300) NOT NULL, granted_by, valid_until NULL, + audit/soft-delete/history.
- Overrides pass through the EXISTING SegregationOfDuties engine exactly like role grants — an override
  creating a forbidden combination is 409 with the conflict reason, not a bypass.

EVALUATOR (libs/authz — ONE implementation)
- EffectiveSetEvaluator.Compute(membershipSnapshot) → (roleGrants ∪ allows) − denies, deny wins;
  expired overrides ignored; deprecated keys still resolve but emit a ONE-TIME structured warning per
  (consumer, key) — Serilog + counter metric, so umbrella-splits are driven from logs (../40 §6).
- Platform-admin flag: short-circuits ONLY keys where is_platform_admin_key = true. Hard-exclude
  everything else. Add THE test: a platform-admin principal with zero memberships attempts (a) a
  patient read, (b) a projected clinical field, (c) a sensitive result, (d) a branch-scoped order list
  → all denied/empty. This test is the A1 guarantee — it never gets deleted or skipped.
- Ordinal level: app_role.level int (lower = more privileged), seeded from ../10 tiers. Used ONLY for
  tier-shaped checks (MFA-required tiers per phase 17, peer-review-required grants per 8b). Add an
  analyzer-style review rule to CONVENTIONS: capability checks use keys, tier checks use level.

MODE 1 — in-session: identity-service resolves the effective set at token issuance into the claims per
21.0. ScopePolicyProvider keeps reading the same scope claim — untouched.
RE-RESOLUTION TRIGGERS: role/override/scope-grant/enablement mutation, membership suspension, tenant
suspension → revoke the refresh family (phase-17 machinery) so the next exchange recomputes. Audit each.

MODE 2 — out-of-session: IEffectiveSetService.ForMembership(membershipId) recomputes from the store —
for supervisor-override validation (the approver's right is checked server-side out-of-band; the acting
user's token NEVER carries it), background jobs, and the admin "preview effective access" view. Cache:
in-process + Valkey, TTL ≤ 60s, explicitly invalidated on any grant mutation (same triggers as above).

PARITY: a fixture matrix (roles × allows × denies × expiry × deprecated × platform-admin × no-membership)
run through BOTH modes asserting identical sets. This suite is invariant 5 — wire it into CI by name so
its removal fails the build (extend the phase-18 route-coverage-guard pattern).
ACCEPTANCE
- Given a role granting orders:read and a Deny override on orders:read, Then denied in both modes.
- Given an Allow override with valid_until in the past, Then absent in both modes.
- Given a deprecated key in use, Then it works, warns once, and is excluded from newly seeded roles.
- Given any grant mutation, Then the refresh family is revoked AND the mode-2 cache invalidated.
- The A1 test above.
TESTS: algebra table-driven; SoD-on-overrides; parity suite; deprecation; cache invalidation;
staleness-window integration test (mutate → old access token works until expiry, refresh yields new set).
```

### 21.3 — Reach: time-bounded scope grants + precedence chain
```text
Read ../40 §3 and ../37 §3. Restructure user_branch_assignment, keep IBranchContext's consumers working.

- branch_scope_grant (replaces user_branch_assignment; migration copies rows): grant_id PK,
  membership_id FK, branch_code FK, valid_from date NOT NULL, valid_until date NULL, granted_by,
  granted_reason varchar(300), + audit/soft-delete/history. Expiry is evaluated at resolution time —
  no cron needed; an expired grant simply stops matching.
- RESOLUTION (libs/auth, extending the existing IBranchContext): precedence ① X-Active-Branch header
  ② persisted user preference ③ membership home_branch_code ④ first accessible (stable order).
  DUAL FAILURE SEMANTICS: explicit header not in the active grant set → 403 problem+json
  'branch-out-of-scope' + audit BranchScopeDenied. Stale soft preference → skip, fall through, and
  surface the fallback to the SPA (response header) so the UI can update the switcher silently.
- READS: RowScope injects the branch predicate for BranchScoped resources; if resolution fails entirely,
  inject the SENTINEL (WHERE branch_code = '__none__') — never an empty predicate. Add THE fail-closed
  test: break resolution on a seeded dataset → assert ZERO rows, and assert the negation (an empty
  predicate WOULD have returned N>0 rows) so the test cannot rot into tautology.
- WRITES: validate the target branch against the grant set up front → 403 if out. No implicit default.
- NO BYPASS: platform-admin gets no branch predicate bypass on clinical/benefit data (A1). MemberScoped/
  ProviderScoped resources keep their ../37 rules — grants govern BranchScoped resources only.
ACCEPTANCE
- Given a grant valid_until yesterday, Then that branch is out of the active set today — reads exclude
  it, writes reject it, and the switcher no longer offers it.
- Given X-Active-Branch: ALX without an ALX grant, Then 403 + BranchScopeDenied audit (with actor,
  header value, active set).
- Given a stale cookie preference, Then the request succeeds under the fallback branch + the response
  signals the correction.
- The sentinel test above.
TESTS: expiry boundary (on the day), precedence order, dual failure semantics, sentinel + negation,
migration row-parity from user_branch_assignment.
```

### 21.4 — Program enablement (features + caps)
```text
Read ../40 §4. This is NOT commercial upsell (A4) — it is per-tenant/partner enablement administered
by Mersal platform administration.

- tenant_feature: tenant_id, feature_key CHECK IN ('claims','callcentre','interop','reporting_extracts',
  ...seed from the module list), enabled bool, changed_by/changed_at + history. tenant_limit: tenant_id,
  limit_key ('active_users','active_provider_users','monthly_extracts','storage_mb'), max_value int,
  + history. Owned by admin-service; mutations require platform-admin + MFA + Idempotency-Key; audited.
- ENFORCEMENT: a gateway/service middleware AFTER authorization, BEFORE handler: disabled feature →
  403 problem+json type 'program-not-enabled' (DISTINCT from 'forbidden' — different remedy text,
  ../40 §4). Limits: counted LIVE inside the mutating transaction (SELECT count WHERE ... FOR the
  relevant rows) → 'program-limit-reached' on breach. No counters, no drift.
- Enablement NEVER grants: feature on + no permission → still forbidden. Feature off → existing data
  remains readable per its normal rules unless the module's own rules say otherwise; audit history
  never hidden.
- Features ride in the token claims per 21.0; limit checks are always live (they are counts, not caps
  you can cache).
ACCEPTANCE
- Given an authorized claims_officer in a tenant with claims disabled, Then 'program-not-enabled' (not
  'forbidden'), and the SPA shows the not-enabled treatment, not the permission-denied one.
- Given active_users at cap, When creating one more, Then 'program-limit-reached' and the transaction
  rolls back; deleting a user frees the slot immediately (live count).
- Given a feature toggled, Then refresh families revoke (21.2 trigger) and the change is audited.
TESTS: distinct problem types asserted; live-count race (two parallel creates at cap-1 → exactly one
succeeds — reuse the consume-concurrency harness); enablement-never-grants; audit.
```

### 21.5 — Governance: attribution, access review, switching, session controls
```text
Read ../40 §6 + A2 + A6.
- AMBIENT ATTRIBUTION: extend the request-scoped principal context to carry membership_id; the existing
  TenantStampingInterceptor pattern auto-stamps created_by/updated_by = membership (not raw user) on
  every write. Test: a write through any service records the membership without the handler doing it.
- ACCESS REVIEW SNAPSHOT (admin-service, extends the 8b access-review): per tenant, point-in-time:
  every membership (user, roles, level, status, scope grants with expiry, overrides with reasons and
  grantors), per-role key list + holder count, platform admins, enabled features + limits. Generated
  server-side, exportable (CSV/PDF via the existing export path), itself audited as an export, listed
  in the review calendar. This is the least-privilege review artifact — SOC2-style evidence.
- MEMBERSHIP SWITCHING: an in-session switch = re-resolution (new claims via refresh), audited
  MembershipSwitched {from, to}. Cross-tenant without a membership: platform-admin flag required and
  reach is ADMINISTRATIVE keys only; otherwise 403 + TenantSwitchDenied audit — NEVER silently ignored
  (A2). Home membership pinned in the session for one-click reversal.
- SESSION/DEVICE CONTROLS (identity-service, extends phase 17): per-identity session list with device
  metadata, per-identity concurrent cap (../18 §9, revoke-oldest), explicit revoke (single + all).
  DEGRADATION per A6: stateless access-token validation never depends on the revocation store; the
  refresh-time revocation check fails OPEN on infra error WITH a Prometheus counter + alert rule
  (exposure bounded by access TTL — state the bound in the runbook); an EXPLICIT revoke that cannot be
  persisted returns an error to the operator (fail-closed) rather than pretending success.
- Login-attempt history: per-identity, queryable by the identity owner (own) and admins (all), retained
  per ../20 §retention, no password material ever stored (extend the existing no-hash-in-audit grep test).
ACCEPTANCE
- Given any mutation anywhere, Then created_by/updated_by carry the membership id — proven by a
  cross-service sweep test, not per-handler assertions.
- Given a non-platform-admin token requesting a cross-tenant switch, Then 403 + TenantSwitchDenied.
- Given the revocation store down, Then refresh proceeds (alert fires); given an explicit revoke during
  the outage, Then the operator gets an error, not silent success.
- Given an access-review export, Then it contains overrides WITH reasons and is audited as an export.
TESTS: attribution sweep, switch guards both branches, degradation both branches, review completeness,
history retention.
```

### 21.6 — Admin UI, routes, docs
```text
Read ../0B (+ §10b), ../21, existing apps/web admin portal (phase 17.4).
- USER & ACCESS SCREENS (design-system components, bilingual AR/EN RTL, axe clean, ≥44px, four-cue
  status chips): membership list per tenant (status, roles, level, branch grants with expiry badges);
  membership detail with tabs: roles, overrides (Allow/Deny with REQUIRED reason field, expiry picker,
  SoD conflicts surfaced inline as blocking errors), scope grants (time-bounded editor showing
  granted-by/reason), sessions (list + revoke), effective-access PREVIEW (calls the mode-2 evaluator —
  "what can this person actually do, and why": each key annotated role/override/denied-by).
- Deprecated keys render muted with their replaced_by pointer; newly seeded roles exclude them.
- Feature/limit administration screen (platform-admin only): switches + caps with current live usage
  shown against each cap; every change confirm-dialogued (typed tenant name for destructive toggles).
- UI GATING IS COSMETIC ONLY (../40 §6): hide unusable affordances for usability, but every screen's
  actions re-check via the API; add the standing test that a hand-crafted request to a hidden action
  is 403 — assert the API, not the DOM.
- 403 pages: THREE distinct treatments — permission denial ("ask your administrator"), program-not-
  enabled ("contact Mersal programme administration"), branch-out-of-scope (offer the switcher). Never
  one generic page (the remedy differs — that is the point of the separation).
- KONG: routes for membership/override/scope-grant/feature endpoints; scopes admin:access:read/write,
  platform:admin; route-coverage guard green.
- DOCS: ../10 gains membership + level column; ../11 gains override semantics + the A1 rule; ../18
  gains the degradation policy; ../22 gains the new tables; 00-README-INDEX + README gain doc 40;
  BUILD-STATUS gains 21.0–21.6. Update docs/security/token-contract.md per 21.0 if not already.
ACCEPTANCE: preview matches mode-2 output exactly (same fixture); SoD conflict blocks with reason
inline; the three 403 treatments render distinctly; axe EN+AR; hidden-affordance API test green.
TESTS: component tests per screen, preview-parity, a11y, the cosmetic-only guarantee test.
```

---

## Guardrails
- **A1 is absolute:** no principal, flag, or wildcard reads PHI outside min-necessary + ABAC + RLS + the sensitive gate. The A1 test (21.2) is permanent — its deletion fails CI.
- **The frozen token contract is extended additively by ADR only**; the byte-compat test never goes red.
- Data-shaped conditions (treating, ownership, case, sensitive grants, break-glass) are never claims.
- One evaluator algebra, two modes, parity-tested; the parity suite's presence is CI-enforced.
- Fail closed: sentinel predicates, deny-wins, explicit-header hard-reject. The ONLY fail-open is the A6 revocation-check degradation — bounded, alarmed, documented.
- Nothing security-relevant is silent: denied switches, out-of-scope headers, override grants, feature toggles all audit.
- Backfill is snapshot-parity: no user gains or loses access by migration alone.
- Full suite green after each sub-prompt (`./dotnet.sh test HbmpPlatform.sln -c Release` + `pnpm -r test`) — **including the untouched min-necessary, RLS, SoD, and sensitive-gate suites**.

## Done when
- [ ] `tenant_membership` is the sole security principal; backfill proven access-neutral by snapshot comparison.
- [ ] Effective set = (role ∪ allows) − denies with SoD-guarded overrides; platform-admin limited to administration keys — the A1 denial test passes and is CI-pinned.
- [ ] Time-bounded, attributed branch grants; precedence chain with hard-reject vs skip semantics; sentinel fail-closed proven with the negation assertion.
- [ ] Program enablement with distinct problem types and live-count caps; enablement never grants.
- [ ] Claims additive within the frozen contract; re-resolution triggers revoke refresh families; mode-2 evaluator + cache invalidation; **parity suite green and CI-pinned**.
- [ ] Attribution ambient via membership; access-review snapshot exportable + audited; switch guards audit both grant and denial; session degradation per A6 with alert.
- [ ] Admin UI: membership/override/grant/feature screens with effective-access preview matching mode-2; three distinct 403 treatments; UI gating provably cosmetic.
- [ ] All pre-existing security suites still green; docs + ADR-0021 merged; routes covered.

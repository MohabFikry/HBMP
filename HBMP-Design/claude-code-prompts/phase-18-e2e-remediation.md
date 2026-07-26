# Phase 18 — E2E Review Remediation & Enhancement

**Goal:** Close every finding from the end-to-end review ([`../../docs/AUDIT-R2-E2E.md`](../../docs/AUDIT-R2-E2E.md) — **read it first; it carries the evidence file:lines**) and land the highest-value best-practice enhancements. Six gates, in order. The theme: **the benefit/money layer must become correct, the security layers must finish engaging, six wires must be connected, and CI must enforce what it claims.**

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> **Gate A is not optional and not reorderable.** Today the platform **cannot enforce a coverage limit or a contract tariff** — those are benefit-administration correctness bugs, not hardening. No pilot with real beneficiary data before Gates A and B are green.

## Skills to activate
> Always-on `mersal-platform-architect`, `refugee-healthcare-management`. Per gate: A → `policy-eligibility-engine`, `medical-claims-engine`, `healthcare-business-rules-engine`; B → `healthcare-database-architect`; C → `mersal-platform-architect`, `fhir-integration-architect`; D/F-UX → `healthcare-uiux-designer`, `executive-dashboard-designer`. Index: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first
- [`../../docs/AUDIT-R2-E2E.md`](../../docs/AUDIT-R2-E2E.md) (authoritative) · [`../../docs/AUDIT-2026-07-26.md`](../../docs/AUDIT-2026-07-26.md) (R1, for closure history).
- Specs: [`../23-state-machines.md`](../23-state-machines.md), [`../36-claims-management.md`](../36-claims-management.md) §5, [`../37-branch-scoping-and-clinical-sensitivity.md`](../37-branch-scoping-and-clinical-sensitivity.md), [`../11-permission-matrix.md`](../11-permission-matrix.md), [`../18-security-model.md`](../18-security-model.md), [`../0B-DESIGN-SYSTEM-UI.md`](../0B-DESIGN-SYSTEM-UI.md), [`../21-accessibility-checklist.md`](../21-accessibility-checklist.md).
- Machine gotchas in `docs/HANDOFF.md` (`./dotnet.sh`, PG :55432, .NET 8 only, analyzers-as-errors, CPM, pnpm).
- **Every sub-prompt ends with the full suite green** (`./dotnet.sh test HbmpPlatform.sln -c Release` + `pnpm -r test`) and a ✅/date line added to the AUDIT-R2 finding row.

---

## GATE A — Benefit & money correctness

### 18.A1 — Make coverage limits actually bind (X1)
```text
THE HEADLINE BUG: coverage_limit.consumed_value is never incremented, so every member is eligible
forever and LIMIT_EXCEEDED can never fire. Read ../37 + services/policy/Domain/Entities.cs:55-57
(whose comment already promises this behaviour) + services/eligibility/Domain/EligibilityEngine.cs:19.

- Add a policy-service consumer for OrderLinesConsumed and RxLinesDispensed that increments
  coverage_limit.consumed_value for the matching coverage + benefit category, in a GUARDED UPDATE
  (WHERE consumed_value = @expected, or an atomic += with a CHECK), inside a transaction, with
  dedupe-on-event-id via the existing IdempotentConsumer.
- Respect the spec: claims must NEVER move this accumulator (36 §2.3) — only consume/dispense do.
- Emit CoverageLimitChanged so the eligibility projection invalidates (the consumer already exists).
- Handle the reversal path: a Void/compensating fulfillment must decrement symmetrically.

ACCEPTANCE
- Given a consume of qty 2 against an Annual limit of 10, Then consumed_value becomes 2 exactly once,
  and a replayed event does not double-count.
- Given consumption reaches the limit, When eligibility is checked, Then it returns Ineligible/
  NeedsAuthorization with a LIMIT reason (not Eligible).
- Given a claim is adjudicated, Then consumed_value is unchanged by the claims path.
TESTS (required): Consuming_a_line_increments_coverage_limit_consumed_value_exactly_once;
Replayed_consume_event_does_not_double_count; Eligibility_flips_to_limit_exceeded_at_the_boundary;
Claims_adjudication_never_moves_the_accumulator.
```

### 18.A2 — Fix the claims money layer (X2, X3, X8)
```text
Three defects in the same layer. Read ../36 §5 and claims/Domain/{ClaimDecision,AutoDerive}.cs +
claims/Infrastructure/{AdjustmentService,DecisionService,BatchService,BatchLogic}.cs first.

X2 ROLLUP ERASURE: DecisionService.cs:137 and BatchService.cs:225 call BatchRollup.Compute(lines)
with adjusted=0, wiping what AdjustmentService.cs:124-129 netted in — including at the → Decided
transition immediately before totals freeze at SettlementIssued.
FIX: one canonical IRollupService/RecomputeBatchAsync used by ALL mutation paths (decision, adjustment,
add/remove claim, transition), always summing non-pending claim_adjustment.amount_delta.

X3 CAP INVERSION: ClaimDecision.cs:46,51 uses Math.Max(billed, contractPrice) — approval above the
contract tariff. Adjust (:65) has no cap at all.
FIX: cap = Math.Min(billed, contractPrice ?? billed), applied to Approve, PartiallyApprove AND Adjust.

X8 QUANTITY IGNORED: AutoDerive.Price returns the unit tariff verbatim; ev.Quantity is stored, never
multiplied. ReconClassifier.PriceDiffers then flags every multi-unit line as PriceVariance.
FIX: ContractPrice = tariff * quantity at intake; compare unit-price × qty in reconciliation.

Also: claim.AdjustedAmount is a self-assignment (AdjustmentService.cs:94) — accumulate the delta;
and an adjustment that would drive AllowedAmount negative must be REJECTED, not clamped to 0 while
the audit row records the true negative (:90 vs :161).

ACCEPTANCE
- Given a batch with a -50 deduction, When it transitions to Decided and settlement is issued, Then
  net_payable still reflects the deduction.
- Given billed 500 / contract 100, When an officer approves, Then allowed cannot exceed 100 (all three
  decision kinds).
- Given a qty-3 line at 100/unit, Then contract price is 300 and reconciliation reports no variance.
TESTS: Batch_totalAdjusted_survives_the_transition_to_Decided; Partial_above_the_contract_tariff_is_
rejected; An_Adjust_decision_above_the_cap_is_rejected; Contract_price_scales_with_line_quantity;
Adjustment_that_would_make_allowed_negative_is_rejected.
```

### 18.A3 — Concurrency & temporal correctness (X7, X9, X10 + F17/F18/F21/F22)
```text
X7 LOST UPDATE: ConsumeExecutor.cs:63 computes the aggregate status from the in-memory graph loaded at
:37 and applies it unguarded at :91-92 (same in DispenseExecutor.cs:60,88-89). Two racers on DIFFERENT
lines both write PartiallyUsed → the order/Rx is stranded and OrderCompleted/RxDispensed never emit.
FIX: recompute from freshly re-read line rows INSIDE the transaction and apply with a guarded
UPDATE (WHERE status = @expected) + bounded retry. Do NOT weaken the per-line xmin guard.

X9 CACHE KEY: EligibilityCache.cs:21 keys on (beneficiary, category) but the decision depends on
serviceCode + requiresPreAuth (EligibilityEngine.cs:81-84) — a cached non-gated Eligible is served for
a GATED service for 15 minutes, bypassing pre-auth.
FIX: include serviceCode + requiresPreAuth in the key, or cache only coverage/limit facts and always
run the engine.

X10 LIMIT RESET: LimitReset.cs:28-30 treats LastResetOn == null as "reset due whenever consumed > 0",
wiping in-period consumption. LimitResetTests.cs:45 asserts the BUG — fix the test too.
FIX: seed LastResetOn = PeriodStart(period, coverage.EffectiveFrom) at creation; drop the null case.

ALSO: waitlist promotion has no lock (AppointmentTransitions.cs:159-170 — two cancels promote the same
entry) → SELECT … FOR UPDATE SKIP LOCKED + status guard; cancel/no-show are three unwrapped
SaveChanges (:128,168,182) → one transaction; idempotency keys are not bound to the request payload
and match by StartsWith prefix (ConsumeExecutor.cs:42-44,82) → store a body hash, reject `::` in the
header, use equality on (key, line_id).

TIME: introduce IBusinessCalendar.Today() returning the Africa/Cairo date and inject TimeProvider
everywhere DateTimeOffset.UtcNow is used in production code (eligibility ProjectionUpdater/Cache, emr
AppointmentTransitions, interop). Ban bare UtcNow via an architecture test.

TESTS: parallel_consume_of_different_lines_completes_the_order; same for dispense;
Replaying_a_key_with_a_different_payload_is_rejected; prefix_key_does_not_false_replay;
Eligibility_returns_NeedsAuthorization_for_a_gated_service_after_a_cached_non_gated_check;
A_reset_is_not_due_within_the_same_period_when_last_reset_is_null;
Two_concurrent_cancels_promote_two_distinct_waitlist_entries;
Coverage_validity_at_23_30_Cairo_evaluates_against_the_Cairo_date.
```

### 18.A4 — Report-access & state-machine conformance (F10–F12, F19, F20, F27, F30)
```text
- Report-access: cap the caller-supplied TTL (ReportAccess.cs:73) to the policy max (72h Sensitive /
  24h HighlySensitive); implement the missing transitions so UnderReview/Expired/Revoked are reachable
  and InfoRequested can be resupplied (today a request entering InfoRequested is stuck).
- Settlement: enforce SoD (releaser ≠ batch creator, SettlementService.cs:37-76) and regenerate advice
  from the FROZEN snapshot, not live rows (:42-49) — corrections go into a new batch (23 §9).
- Adjustment dual-control is TOCTOU (AdjustmentService.cs:52 reads AsNoTracking, writes in another
  transaction) → compute net inside the same transaction with SELECT … FOR UPDATE on the batch.
- Policy reactivation never restores coverage (ProjectionUpdater.cs:127-128 only writes non-Active) →
  mirror the status unconditionally.
- A fully-voided claim is stuck in UnderAdjudication (ClaimDecision.cs:73) → map to Void.
- Emit a TransitionDenied audit event on EVERY rejected transition (currently 409s are silent).
- Align patient BeneficiaryLifecycle.cs:14-16 with 23 §1 (or amend the spec — decide and document).

THEN BUILD THE GUARD: a state-machine conformance test that parses the `stateDiagram-v2` blocks in
../23-state-machines.md into (from,event,to) triples and asserts BOTH directions against
OrderWorkflow, PrescriptionWorkflow, AuthorizationWorkflow, BatchTransitions, AppointmentWorkflow,
BeneficiaryLifecycle and the report-access flow: no declared transition missing, no undeclared
transition permitted, every declared state reachable.
ACCEPTANCE: the conformance test is green and fails if a state or transition drifts from the doc.
```

## GATE B — Security closure

### 18.B1 — Secrets regression + fail-fast (X4, X5, F13)
```text
- Delete the Blob credentials from services/document/Api/appsettings.json (base) and throw on missing
  Blob__SecretKey, matching the DB fallback pattern from 16.1.
- identity ClientSeeder.cs:57: throw when Issuer:ServiceClientSecret is unset outside Development;
  scope the hbmp-services client to the ingest/projection scopes only (NOT all of IdentityContract.
  Scopes); make the seeder RECONCILE an existing client instead of skipping (:58) so rotation applies.
- Restrict the hbmp-web public client to the interactive-user scope set (:53).
- Move UserSeeder.cs:15 DemoPassword to config with no default; extend .gitleaks.toml to catch literal
  passwords of this shape.
ACCEPTANCE: no credential literal in any tracked file; gitleaks green with the new rules; a rotated
service secret takes effect on restart (test asserts reconciliation).
```

### 18.B2 — Finish RLS: no superuser, no deny-all trap (X6, S1, S2)
```text
Order matters per service: wire the binder FIRST, then flip the connection string, then add the test.
- claims, callcentre, admin: add AddHbmpRls + app.UseHbmpRls() (callcentre also needs a
  0003_tenant_rls.sql mirroring document/…/0002_tenant_rls.sql — it has tenant_id columns and ZERO
  RLS DDL today), then flip compose to hbmp_app.
- provider: flip compose.yaml:424 to hbmp_app (its binder and policies are already correct — today the
  green isolation test proves the policy, not the deployment).
- interop: remove the fail-OPEN escape (`OR current_setting(...) IS NULL OR = ''`) from
  0001_interop.sql:29-34, extend RLS to integration_partner + inbound_staging (0002), bind the GUC.
- eligibility EventConsumer.cs:76-79 stamps a hardcoded SoleTenantId → take the tenant from the event
  envelope and fail the message if absent.
- admin write paths accept a body-supplied tenant (PlatformEndpoints.cs:53, UsersEndpoints.cs:23,44,57,
  BranchAssignmentEndpoints.cs:22,39) → derive from the principal; reject body tenant unless the
  principal holds the global super-admin scope.
ACCEPTANCE: every runtime connection string uses hbmp_app; each of claims/callcentre/admin/provider/
interop has a 2-role RlsIsolationTests proving A-only / B-only / no-GUC-zero; a CI assertion fails the
build if any compose connection string contains POSTGRES_USER.
```

### 18.B3 — Admin/issuer authorization, CSRF, scope integrity (S3, S4, S5, S6, S7, S8, S9)
```text
- S3: add .RequireAuthorization(HbmpPolicies.Scope("admin:read"|"admin:write")) to EVERY admin MapGroup
  (Users, Platform, PolicyConfig, AccessReview, Governance, BranchAssignment) so the framework enforces
  authn + scope + MFA BEFORE the in-handler gate (keep the gate as layer two). Apply the same to
  identity/Api/Auth/AdminEndpoints.cs:27 and interop's groups — the pattern was replicated.
- S4: add antiforgery tokens to the three rendered identity forms (login, 2FA submit, enroll-2fa) and
  REMOVE .DisableAntiforgery() from AccountPages.cs:75 — a cross-site POST currently registers the
  attacker's authenticator as the victim's second factor. Set Cookie.SecurePolicy=Always,
  SameSite=Strict in IssuerSetup.cs:28-37.
- S5: TokenPrincipalFactory.cs:37-40 — grant the intersection unconditionally; return invalid_scope
  when empty. Never fall back to the user's full entitlement.
- S6: patient-service — split patient:read from patient:write, run the authorization engine on by-id
  and search, emit a PHI-read audit event, and project the DTO through FieldProjector (model the
  pii/contact field classes the R1 audit deferred, so reception keeps legitimate contact access).
- S7: add app.UseHbmpTransportSecurity() as the first middleware in identity-service.
- S8: add the Kong JWT/OIDC plugin globally (validating identity-service JWKS), health/metrics excepted.
- S9: per-route rate limits on /connect/* (e.g. 10/min) plus AddRateLimiter on token/login/2fa.
- Also: require auth on identity's /identity/roles, /identity/scopes, /identity/effective-scopes
  (currently AllowAnonymous — a free RBAC map for attackers).
ACCEPTANCE: unauthenticated → 401 at middleware on every admin/identity-admin endpoint; no-MFA token →
403; a cross-site 2FA enrolment POST fails; a token request with no in-vocabulary scope is rejected;
patient by-id is audited, row-scoped and projected; Kong rejects an unauthenticated call at the edge.
```

## GATE C — Last-mile wiring

### 18.C1 — Restore live sessions and branch scoping (W1, W2)
```text
W1 (blocks all live testing): identity issues 5-minute access tokens and the SPA requests
offline_access but oidcClient.ts has no refresh path and discards the refresh token (exchangeCode:151).
FIX: persist the refresh token, add a silent-renew before expiry, wire it to the existing keep-alive so
the session-timeout modal reflects real token lifetime. Add rotation + reuse-detection handling.
W2 (branch scoping is inert end-to-end): apps/web/src/api/http.ts:74-80 never sends X-Active-Branch and
kong.yml:212-213 omits it from CORS headers AND exposed_headers.
FIX: send the header from http.ts + useBranchContext; add it to both Kong lists; surface the echoed
active branch in the switcher.
ACCEPTANCE: a session survives >30 minutes of activity without re-login; switching branch changes the
emr worklist (E2E test asserts different rows for two branches).
```

### 18.C2 — Make finished features reachable (W3, W4, W5, W6, W7, W8)
```text
- W3 interop: add an interop-service compose block + Kong service with paths ["/fhir","/interop"];
  smoke-test GET /fhir/r4/metadata through :8000.
- W4 report-access: build the approver inbox (Doctor + Medical Director portals) calling
  /report-access-requests/{id}/decision and /report-access-grants/{id}/revoke, and schedule
  /report-access/sweep-expiry. Without this the sensitive gate is permanent-deny (../37 §6).
- W5 identity admin: route /identity through Kong and repoint AdminConsole's AdminUsers/AdminPolicies
  at the real store (they still edit the legacy admin-service projection).
- W6: uncomment the web service in compose.yaml:644 + add its Kong origin.
- W7 FR-BRN-026/027: add an IPractitionerBranchDirectory seam in emr; validate doctor↔branch at BOTH
  availability-create and booking (422 with a clear reason); wire the /practitioners?branchId&
  specialtyCode picker into the booking screens.
- W8: either wire Tesseract ara+eng + the authorized-service resolver in claims ReimbursementSeams.cs,
  or downgrade 10b.6 to ◐ in BUILD-STATUS naming the seam as remaining work. Do not leave it ticked.
ACCEPTANCE: every capability in the AUDIT-R2 reachability matrix reads "Reachable"; a booking at an
unassigned branch returns 422; the FHIR metadata endpoint responds through the gateway.
```

## GATE D — UX safety

### 18.D1 — Stop silent failures and undifferentiated errors (U1, U2, E3, E4)
```text
- U1: ResultUpload.tsx:56, BeneficiaryPortal.tsx:179, NetworkPortal.tsx:209, ApprovalsExtra.tsx:95 all
  `catch { setStatus("idle") }` — the spinner stops, nothing is shown, and none of the four sends an
  idempotency key. A retrying operator creates duplicate clinical records.
  FIX: render the RFC-7807 detail via InlineAlert role="alert" AND add newIdempotencyKey() to all four
  (mint once per form instance with useRef; rotate only after confirmed success — apply this rule to
  ApprovalsWorklist/PharmacyDispense/LabQueue/CallCentre too).
- U2: add ONE bilingual writeErrorMessage(ApiError) used by every mutation — 401 re-auth, 403 "your
  access changed", 409 "already actioned — refreshing", 412 "changed since you loaded — reloading",
  422 field errors, network retry. ApiError already carries kind/status/problem (http.ts:20-35).
- E3: codify + ESLint-guard the rule "reads may be optimistic; any server-invariant operation (book,
  consume, dispense, decide, check-in, cancel) renders only server-confirmed state" and fix the one
  violation (ReceptionDesk.tsx:94 — reload and derive the chip from r.status).
- Add typed confirmations on the DS Modal for reject / break-glass override / dispense / lab consume /
  appointment cancel (today the only window.confirm guards the REVERSIBLE finance export); default
  dispense quantities to 0, not the full remaining amount (PharmacyDispense.tsx:84-86).
ACCEPTANCE: every mutation surfaces a typed, translated message; no write path lacks an idempotency
key; a 409 on a decision reads differently from a network failure.
```

### 18.D2 — Correct, safe and reachable UI (U3, U4, U5, U7, U8, U9)
```text
- U3 SAFETY: CallCentre.tsx:301 hardcodes StatusChip kind="ok" for every member status — a Suspended/
  Expired member displays as eligible. Map status → StatusKind via a shared lookup + bilingual label.
  Sweep for other hardcoded kinds.
- U4: restore navigation below 760px (app.css:392 deletes the rail with no replacement) — bottom tab
  bar (≤5 items) or a focus-trapped drawer with aria-expanded, per ../14 §5.
- U5: either wire the app-bar search to a permission-scoped endpoint or remove it AND its "/" binding
  (AppShell.tsx:141 is a dead field the shortcut trains users to reach for). Prefer the command palette
  in 18.F2.
- U7: one useFormat() hook — Intl.DateTimeFormat(lang === "ar" ? "ar-EG" : "en-GB", { timeZone:
  "Africa/Cairo" }) — replacing every bare toLocale*; return raw numbers from HttpApiClient and format
  currency at render with Intl.NumberFormat(locale,{style:"currency",currency:"EGP"}). Ban bare
  toLocale* via ESLint. Appointment times currently shift by hours on a UTC-set clinic PC.
- U8: rename the undefined tokens in app.css (--brand-teal → --accent/--focus, --status-bad-fg →
  --st-bad-fg, --radius-N → --r-sm/md/lg); add a stylelint allowlist so undefined tokens fail CI.
- U9: stop using --brand as an avatar surface with white text (~2.2:1) — use --accent or --accent-tint.
  Drop the legacy #1d9ba6/#16808d var() fallbacks entirely.
- Sweep the remaining raw enum literals rendered to users (CallCentre outcomes/identifier types/
  categories/appointment types/referral status; ApprovalsWorklist priority + break-glass segments) into
  bilingual label maps — the file already does this for REASONS/CANCEL_REASONS.
ACCEPTANCE: member status is truthful in the Call Centre; the app is navigable at 375px; no undefined
CSS token; dates/times/currency render in Africa/Cairo and the active locale; axe clean on the touched
screens in EN and AR.
```

### 18.D3 — Accessibility conformance sweep (U6, U10)
```text
- Expand the axe suite to a table-driven run over EVERY route in SCREENS × {en, ar} × {light, dark};
  add a Playwright + @axe-core/playwright job for the color-contrast rules jsdom cannot evaluate
  (currently disabled in both suites, so contrast is unverified everywhere).
- Fix what that will surface, starting with the known set: <dt>/<dd> outside any <dl> (34 occurrences
  across 7 screens — wrap in <dl> with <div> pairs); worklist rows focusable but Enter does nothing
  (wire DataTable onSelect); aria-selected on <tr> in a plain role="table" (use role="grid"/gridcell);
  duplicate accessible names on every PharmacyDispense quantity input (append the drug name — this is a
  medication-error risk); ExecutiveDashboard charts aria-hidden with the data table behind a default-off
  toggle (always render the table in an .sr-only wrapper); skip link uses `left` not inset-inline-start;
  sticky headers obscure focused rows (scroll-margin-block-start); DS hardcoded English strings
  (DataTable "Loading…"/"No results", KpiCard delta direction).
- Raise .mrs-sm/.mrs-seg targets to meet the project's own ≥44px bar (or stop using size="sm" in rows).
ACCEPTANCE: axe zero serious/critical on every route in both locales and themes; keyboard-only
traversal works on all four high-volume worklists.
```

## GATE E — CI truth & quality

### 18.E1 — Make the gates enforce what they claim (Q1, Q2, Q4)
```text
- Wire tools/ci/check-kong-route-coverage.py into backend-ci.yml AND extend it past /api/v1 to
  /fhir, /interop, /identity, /connect (it is blind to exactly the class of gap that shipped as W3).
- Add IDENTITY to print-test-db-env.sh and Identity + Interop to the OpenAPI key list in
  backend-ci.yml:114 — the newest, most security-critical service currently has NO CI regression gate.
- Commit the generated OpenAPI specs to docs/api/ and add a `git diff --exit-code` drift check.
- Ratchet COVERAGE_MIN_DOMAIN from 55 toward the documented 80 (e.g. +3 per merge to main, with the
  target date in the workflow); gate overall coverage too.
- Resolve the CI split-brain: delete .gitlab-ci.yml or make it include:-delegate to the same tools/ci
  scripts, and amend ADR-0001 to record the decision.
- Replace the last 3 `if (Db is null) return;` early-returns (patient/emr/interop tests) with
  SkippableFact.
ACCEPTANCE: CI fails if a MapGroup prefix has no Kong route; identity tests run in CI; OpenAPI drift is
a red build; one authoritative pipeline.
```

### 18.E2 — Structural quality (libs/testing, gate dedup, arch tests, thin suites)
```text
- Extract libs/testing (HbmpDbFixture, RlsIsolationTheory, AuthedClientFactory) and refactor the 13
  hand-copied RlsIsolationTests + per-service TestInfra onto it.
- Consolidate the 16 near-identical *Gate.cs into libs/authz HbmpGate<TPolicy> with a pluggable result
  factory (problem+json vs FHIR OperationOutcome). Duplication GREW since R1 — stop it here.
- ARCHITECTURE TESTS (NetArchTest) encoding the house pattern — these would have caught 4 of the 6
  criticals at build time: every services/*/Api/Program.cs calls UseHbmpTransportSecurity AND
  UseHbmpRls (or carries a documented opt-out attribute); every DbContext entity with tenant_id has a
  matching RLS migration; Domain never references Infrastructure; no bare DateTimeOffset.UtcNow in
  production code; no raw decimal on money-named properties.
- Fill the thin suites: MasterDataAuthzTests (21 endpoints, 1 test file today), CallCentreAuthzTests +
  RlsIsolationTests, DocumentPolicies at endpoint level, audit chain-tamper + verifier tests,
  libs/data tests for the RLS/tenant interceptors (the most safety-critical lib has none).
- Frontend: one interaction + a11y test per screen in screens/registry.tsx (8 test files for 22
  screens today); burn down the 133 `any` in HttpApiClient.ts by typing raw payloads as unknown and
  letting the existing zod schemas narrow, then delete the file-wide eslint-disable.
- Convert the remaining 7 anonymous-object 404s to Results.Problem; delete services/hello; remove the
  tools/migration NoWarn or move it centrally with a rationale.
ACCEPTANCE: architecture tests green and enforced in CI; no duplicated gate/test harness; every
service has an authz suite; zero file-wide eslint-disable.
```

## GATE F — Enhancements (highest value first)

### 18.F1 — Correctness engineering
```text
- Property-based tests (FsCheck/CsCheck) over ConsumeExecutor/DispenseExecutor against real Postgres:
  random interleavings of (lineId, qty, key) asserting 0 ≤ consumed ≤ ordered, Σ fulfillment.quantity
  == line.quantity_consumed, aggregate_status == RecomputeFrom(lines), and replay-is-a-no-op. Catches
  X7 and the idempotency-prefix class mechanically.
- A Money value type (readonly record struct Money(decimal, Currency)) with MidpointRounding.ToEven at
  2dp and no implicit decimal conversion; route every claim amount through it (makes X3 impossible).
- Mutation testing (Stryker.NET) gated on libs/authz + services/*/Domain only, so it stays fast.
```

### 18.F2 — UX enhancements (P1 set)
```text
- COMMAND PALETTE (⌘K/Ctrl+K), permission-scoped over sections, recent records and actions — replaces
  the dead search and is the only navigation that scales across 16 role portals.
- SERVER-SIDE sort + filter + pagination on approvals, claims, pharmacy, lab (DataTable already
  implements onSort/aria-sort and ZERO screens use it). SLA-remaining sort on approvals is the
  difference between working a deadline and guessing.
- KEYBOARD-FIRST worklist mode: j/k traverse, Enter open, a/r/p decision presets, Esc close, ? overlay;
  fix the duplicate g h / g q binding.
- Inline validation on blur using the existing zod contracts (zDecisionRequest already validates on
  submit); draft autosave for SOAP notes / rationale / call wrap-up (the session-timeout modal can
  appear mid-note today).
- Offline/poor-connectivity: shell-level connection banner + backoff retry; content-shaped skeletons.
  Optionally queue write-behind for NON-authoritative writes only (vitals, notes) — explicitly EXCLUDE
  booking/dispense/consume/decisions, which must fail loudly.
- Persist density, default branch and default worklist filter per user alongside theme+lang.
- Privacy-safe UX telemetry ({event, screenKey, roleKey, durationMs, outcomeClass} — never IDs, values
  or free text) on the existing audit transport; the retry-after-failure metric would have surfaced
  every silent-failure defect in this audit within a week.
```

### 18.F3 — Security & ops enhancements
```text
- OpenBao dynamic Postgres credentials (short-lived per-service leases) replacing static hbmp_app.
- Tenant-isolation fuzzing in CI driven off information_schema: for EVERY table with tenant_id, assert
  A-only / B-only / no-GUC-zero — a new table without a policy fails the build (this is the control
  that would have caught X6/S2 automatically).
- Streaming anomaly detection on the audit topic: break-glass outside change windows, one subject
  reading >N beneficiaries/hour, exports out of hours, repeated *Denied reason codes.
- Authenticated DAST/API fuzzing (ZAP + Schemathesis off the committed OpenAPI) with three role tokens
  asserting 403-not-200 and no out-of-contract PHI fields.
- SBOM (CycloneDX) + cosign signing + SLSA provenance + admission policy refusing unsigned images.
- Pact contract tests for FE↔API and interop↔partner; codegen the FE client from the committed specs.
```

---

## Guardrails
- **Never weaken a control or a test to make a gate pass.** If a test now fails because behaviour was wrong, fix the behaviour — and fix the test that asserted the bug (`LimitResetTests.cs:45` is one).
- Preserve the consume/dispense/booking invariants exactly; re-run their suites after every gate.
- RLS binder registration and the connection-string flip land **together per service**.
- Additive migrations only (expand/contract); backfill defaults so existing rows behave as before.
- Update `docs/AUDIT-R2-E2E.md` with ✅/date per finding id as it closes, and mirror a phase-18 row per sub-prompt into `docs/BUILD-STATUS.md`.

## Done when
- [ ] **Gate A:** coverage limits bind (X1); rollups preserve adjustments (X2); allowed ≤ contract tariff on all decision kinds (X3); quantity-aware pricing (X8); no lost update on concurrent consume (X7); eligibility cache is service-aware (X9); limit reset respects the period (X10); the state-machine conformance test is green.
- [ ] **Gate B:** no committed credentials; rotation works; every runtime connection is `hbmp_app` with a bound GUC and a 2-role isolation test; no fail-open policy; admin + identity-admin behind middleware with MFA; CSRF closed on the issuer; scopes never fail open; patient PHI reads scoped, projected and audited; Kong authenticates; `/connect/*` rate-limited.
- [ ] **Gate C:** sessions survive; branch switching changes worklists; interop, report-access approval, identity admin and the SPA are all reachable; doctor↔branch validated; 10b.6 either implemented or honestly re-labelled.
- [ ] **Gate D:** no silent write failures; typed bilingual error messages; truthful status chips; navigable at 375px; Africa/Cairo dates; axe clean across all routes in EN + AR.
- [ ] **Gate E:** route-coverage, identity tests, OpenAPI drift and coverage ratchet all enforced in one pipeline; architecture tests green; `libs/testing` extracted; gates consolidated.
- [ ] **Gate F:** property-based executor tests, `Money` type, command palette, worklist sort/filter/paginate, keyboard mode, tenant-isolation fuzzing, SBOM/signing — as prioritized with the sponsor.
- [ ] `AUDIT-R2-E2E.md`, `BUILD-STATUS.md`, `HANDOFF.md` and `security-sign-off.md` are true statements about the code.

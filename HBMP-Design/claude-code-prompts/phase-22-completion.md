# Phase 22 — Completion (finish the frontend, wire the backend, make the docs true)

**Goal:** Take the platform from "everything exists" to "everything works": every screen finished to **L1 exists → L2 routed → L3 live data → L4 safe writes → L5 a11y/RTL**, every backend event actually consumed, every design-specified surface either built or retired by ADR.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md) · Audit: [`../../docs/AUDIT-R3-COMPLETION-MAP.md`](../../docs/AUDIT-R3-COMPLETION-MAP.md)

> **This phase adds almost no new features.** It finishes built ones. The single most common defect in this codebase is a screen that reads beautifully and cannot write — treat "no error surface" and "no Idempotency-Key" as bugs of the same severity as a missing endpoint.
>
> **Definition of done for every screen touched:** routed, live data, writes that submit + surface RFC 7807 detail + carry a stable Idempotency-Key where the operation must not double-apply, and axe-clean in EN **and** AR against a **populated** state (not an error state).

## Skills to activate
> `mersal-platform-architect`, `refugee-healthcare-management` (always-on), plus `healthcare-uiux-designer` (Gates 1/2/4), `clinical-workflow-designer` (Gate 1), `healthcare-business-rules-engine` (Gate 3), `healthcare-database-architect` (Gate 3).

## Context — read first
- [`../../docs/AUDIT-R3-COMPLETION-MAP.md`](../../docs/AUDIT-R3-COMPLETION-MAP.md) — **AUTHORITATIVE**; every gate below maps to its findings.
- [`../0B-DESIGN-SYSTEM-UI.md`](../0B-DESIGN-SYSTEM-UI.md) (+ §10b v1.1) · [`../21-accessibility-checklist.md`](../21-accessibility-checklist.md) · [`../14-navigation-structure.md`](../14-navigation-structure.md) · [`../11-permission-matrix.md`](../11-permission-matrix.md) · [`../16-service-architecture.md`](../16-service-architecture.md) · [`../19-audit-strategy.md`](../19-audit-strategy.md).
- `docs/HANDOFF.md` gotchas (`./dotnet.sh`, PG :55432, .NET 8 only, analyzers-as-errors, CPM, pnpm) — **and note it is stale; Gate 5 fixes it.**

---

## Gate 0 — Unsplit the working tree (do this first, nothing else is trustworthy until it lands)

```text
Docs 39/40, prompts phase-20/phase-21 and services/profile exist ONLY on the
.claude/worktrees/phase-20-patient-profile worktree. docs/BUILD-STATUS.md on MAIN already
references 20.1-20.5 and 21.0-21.6; 00-MASTER-PROMPT-LIST.md on main references NEITHER.

1. Merge (or copy) from the worktree onto main:
   HBMP-Design/39-patient-profile.md, HBMP-Design/40-user-access-model.md,
   HBMP-Design/claude-code-prompts/phase-20-patient-profile.md,
   HBMP-Design/claude-code-prompts/phase-21-user-access-model.md,
   and services/profile/** if it is complete — otherwise leave the service and merge the docs only,
   marking 20.x ☐ in BUILD-STATUS so status matches reality.
2. Add the missing phase 20 and phase 21 rows to 00-MASTER-PROMPT-LIST.md; add doc 39 and doc 40 to
   00-README-INDEX.md and HBMP-Design/README.md (deliverable count -> 40).
3. CLEAN THE DEV DB: the worktree seeded profile:* and callcentre:history:read scopes into the shared
   dev database, breaking identity-service's frozen-vocabulary test on main. Either remove those rows
   or add the scopes to IdentityContract properly if phase 20 is being merged. Do not "fix" the test
   by widening its expectations without a decision.
4. Delete the worktree once merged so it cannot diverge again.
ACCEPTANCE: main contains all four docs; the master list and README indexes agree; identity's
frozen-vocabulary test is green on main; no worktree remains.
```

## Gate 1 — Unsafe and silent write paths (patient-safety class)

```text
Read AUDIT-R3 §2 Z1/Z2/Z3/Z7 and §3 "Frontend write paths".

1.1 IDEMPOTENCY — make keys STABLE PER INTENT, not per attempt.
- ApprovalsWorklist.tsx:163,187 (decide) and LabQueue.tsx:46,51 (consume) mint newIdempotencyKey() on
  every click, so a timeout retry double-applies the two operations CLAUDE.md names by name.
  Adopt the PharmacyDispense pattern (useWrite, PharmacyDispense.tsx:136,141): one key minted when the
  user opens the intent, reused across retries, discarded on success.
- Add missing keys: requestReportAccess (HttpApiClient.ts:537-543), report-access decision (:570-576),
  beneficiary status change (:1404-1407), finance export (:1124), every CallCentre mutation.
- FIX THE OPPOSITE BUG: order/prescription keys are CONTENT-derived (HttpApiClient.ts:448,472), so two
  legitimately identical orders collapse into one. Content-hash is not an intent key.
- Add a lint/test that fails if a mutating HttpApiClient method has no idempotencyKey parameter.

1.2 ERROR SURFACES — no silent failures anywhere.
- Replace every `catch{}` that discards the problem+json detail with the translated-error path already
  used by PharmacyDispense.tsx:187-198. Offenders: ApprovalsWorklist, LabQueue, DoctorEncounter
  (208,267), ReportAccessInbox (72-74), FinancePortal (268), Substitutions (52-56).
- Add try/catch + message + retry to: NursePortal.tsx:91 (bare await — a failure hangs on "saving"),
  BeneficiaryPortal.tsx:117-128, ApprovalsExtra.tsx:140-151.
- ApprovalsExtra.tsx:161 and BeneficiaryPortal drive their success chip from a LOCAL `done` set — the
  UI claims success the server never confirmed. Drive confirmation from the server response only.

1.3 DoctorEncounter: the clinician portal must be able to author and sign.
- Add note authoring + amend + SIGN to the encounter workspace (DoctorEncounter.tsx:136-141 is
  read-only) and the matching client methods (api/client.ts:93-119 has none).
- Follow ../23-state-machines.md for the encounter/note lifecycle; signing is append-only with history,
  never an overwrite; audited; Idempotency-Key on sign.

1.4 CallCentre: stop fabricating slot ids.
- CallCentre.tsx:103,108-110 posts crypto.randomUUID() as a slot id. Implement real slot discovery
  (the emr availability endpoint), a slot picker, and post the SELECTED slot.
- Replace the raw fetch (CallCentre.tsx:72-81) with the shared http client so Authorization,
  Idempotency-Key, X-Active-Branch and RFC7807 parsing all apply uniformly.

1.5 Branch switcher is dead in live mode.
- useBranchContext.ts:46,47,79 builds ${API_BASE}/api/v1/... while API_BASE already ends in /api/v1
  (config.ts:33, http.ts:82) -> 404 -> fail-soft -> the switcher never renders and
  POST /me/active-branch never lands. Fix the paths, then ADD A TEST that asserts no client path
  contains '/api/v1/api/v1'.
- AppShell.tsx:170-177 never renders the member-scoped "All branches" indicator/filter and never passes
  onFilter, so BranchSwitcher.tsx:44-59 is dead code. Wire it per ../14 §1.1.

ACCEPTANCE: a forced 500 on every mutating screen shows a translated message and leaves no false
success chip; a replayed decide/consume with the same key applies once; booking uses a real slot;
the branch switcher renders and switches in live mode.
TESTS: per-screen error-path tests; idempotency replay tests; the no-double-prefix path test; a
lint/test for mutations lacking an idempotency key.
```

## Gate 2 — Built but unreachable (make existing capability usable)

```text
Read AUDIT-R3 §3 "Orphaned capability", "over-promising labels", "duplicate routes".

2.1 ADMIN + PLATFORM ARE READ-ONLY ACROSS 12 ROUTES (AdminConsole.tsx:106-317). Build the write side,
    reusing the phase-17.4 endpoints that already exist: user create/edit, enable/disable, unlock,
    force password reset, require 2FA re-enrolment, session revoke; role grant/revoke with the SoD
    engine surfacing 409 conflicts INLINE as blocking errors; tenant create/suspend; break-glass
    approve/revoke/attest; system config edit. All behind admin scopes + MFA + Idempotency-Key.
2.2 CLAIMS cannot be adjudicated though the SPA holds claims:adjudicate/decide/settle (config.ts:65-66).
    Build claim detail + line-level decision (approve/partial/deny/adjust, coded reasons), batch
    management, and reconciliation ACTIONS per ../36. Read-only tables are not a claims portal.
2.3 Wire these orphaned client methods to UI: revokeReportAccessGrant (a granted sensitive-result
    window currently CANNOT be revoked — a privacy defect), assignTier + updateTier (provider tier
    assignment is the point of the tier screen), enrol (no new-member enrolment exists),
    attachPolicyPlan, createGroup, pinNote, and document UPLOAD (DocumentsPanel lists/downloads only).
    Add the payer/policy/group CREATE forms that the read screens imply.
2.4 Fix over-promising labels — build the capability or rename the nav, and say which you chose:
    NurseResults renders vitals under a "Results inbox" label (NursePortal.tsx:124-137);
    NetworkLocations is "Locations & users" with no users (catalog.ts:220);
    BeneficiaryManage is "Search / manage" and is read-only;
    ResultUpload promises report files with no file input (ResultUpload.tsx:16) — add real upload via
    document-service (ClamAV path already exists);
    NetworkPerformance shows client-derived counts, not performance data (fix by routing
    /api/v1/metrics in Gate 3, then consuming it).
2.5 Collapse or differentiate duplicate routes: /lab/queue ≡ /lab/consume, /pharmacy/queue ≡
    /pharmacy/dispense, /cases/beneficiary-360 ≡ /cases/my-cases (registry.tsx:112-120,138-139).
    Either give each its own screen or remove the nav entry — six entries, three components today.
2.6 Make the app-bar search real (AppShell.tsx:126-129 has no onChange/onSubmit) or remove it and
    promote the working Ctrl-K palette. A focused field that ignores typing is worse than no field.
2.7 UtilizationScreen hard-codes setScope("policies") (PolicyBook.tsx:399,419-424) — wire the
    groups/plans/payers scopes the scopeMap already defines.
2.8 Delete the now-unreachable SectionPage stub (pages/SectionPage.tsx) and its strings.

ACCEPTANCE: no client method exists without a UI caller (add a test asserting this for the api client
surface); every nav label matches what its screen does; no two nav entries render the same component.
```

## Gate 3 — Backend wiring (the silent-failure class)

```text
Read AUDIT-R3 §2 Z4/Z5 and §3 "Backend seams", "never-loaded reference data", "wiring/gateway".

3.1 EVENT NAME SYMMETRY — do this BEFORE wiring more consumers, or you ship silent no-ops.
- Build a CI gate (tools/ci/check-event-symmetry.py) that extracts every published event name and every
  consumed event name and fails on either orphan direction, with an explicit allow-list file for
  deliberate cases. This is the gate that would have caught all five near-misses.
- Fix the mismatches (choose ONE canonical name each, update both sides, note in ../16 event catalog):
  OrderLineConsumed vs OrderLinesConsumed; EncounterCreated vs EncounterStarted; AppointmentBooked vs
  ApptBooked; AppointmentNoShow vs ApptNoShow; ClaimSettled/ClaimAdjudicated vs
  SettlementAdviceIssued.v1/ClaimAdjudicated.v1 (decide whether .v1 suffixing is the house rule and
  apply it everywhere or nowhere).
- Publish the events consumers legitimately need but nobody emits: DiagnosisRecorded
  (../16 requires it), MemberGroupChanged (ChangeGroupAsync writes an enrollment_event row and emits
  nothing — MembershipCommands.cs:359), BenefitConsumed/DimensionLabelled or retire those branches.
- REMOVE the silent-swallow: EventProjector.cs:105 `default: return false` marks unknown events
  PROCESSED. Unknown event -> log a warning + metric, and fail the CI symmetry gate.

3.2 OUTBOX RELAY: services/document/Api/Program.cs:22 calls AddHbmpDurableOutbox and never
  AddHbmpOutboxRelay — the only one of 19. DocumentAttached never leaves the outbox. Fix it, then add
  an assertion to libs/architecture so this class of defect fails the build (it currently checks RLS
  and transport but not relay registration).

3.3 audit-service runs useInMemoryOutbox: true (audit/Api/Program.cs:21) — the audit spine does not
  durably audit its own reads. Switch to the durable outbox like every other service.

3.4 IProcessedEventStore has only InMemoryProcessedEventStore (libs/events/ServiceCollectionExtensions.cs:23)
  — consumer dedupe is process-local and lost on restart, so a redelivery after a crash re-applies.
  Add a durable (DB-backed) implementation and make it the default.

3.5 NO-OP SEAMS — implement or explicitly defer with an ADR and a runtime STARTUP WARNING (a silent
  null implementation in production is the failure mode):
  notification delivery (NotificationChannels.cs:43-50,78-101 never sends);
  claims NullOcrProvider + NoAuthorizedServiceResolver (every reimbursement forced to ManualAssessment);
  claims NullWormStore (SettlementService.cs:16-19 — settlement advice stores NO BYTES; this one is
  not deferrable if settlement is in scope);
  provider AdjudicatedClaimProbe (self-labelled "KNOWN OPEN GAP, not a safe default");
  interop's 4 quarantine-only adapters + no-op OCR + the outbound Map() that only a test calls.

3.6 REFERENCE DATA: masterdata.drug_interaction has NO loader anywhere, so /drug-interactions/check
  can only return empty while tools/masterdata-loader/README.md:46 claims it is loaded. Either load it
  from ../../Master Lists or make the endpoint return 501 and fix the README. Same decision for
  loinc_code (migrated + EF-mapped, no loader, no endpoint).

3.7 GATEWAY:
- Route GET /api/v1/metrics (provider) and STOP the route-coverage guard from blanket-ignoring the
  'metrics' segment (check-kong-route-coverage.py:29) — ignore only the OTel scrape path. Then make
  NetworkPerformance consume it (Gate 2.4).
- /fhir/r4/metadata's public exemption is INERT: a route-scoped `jwt: enabled: false` under a
  service-scoped plugin is an inactive instance, not an override (kong.yml:275-281 — the file's own
  comment at :302-309 explains this). Give it its own Kong service without the edge_jwt anchor, as
  identity-oidc-routes already does. THEN FIX libs/data/Tests/ReachabilityTests.cs:76-77, which
  currently ASSERTS the broken pattern.
- Remove or implement the dangling Location headers /api/v1/service-lines/{id} and /api/v1/waitlist/{id}
  (provider/Api/Program.cs:201, emr/Api/Appointments.cs:415) — a client cannot follow either.
- Extend the route-coverage guard to scan Location-header paths, not just Map* calls.

ACCEPTANCE: the event-symmetry gate is green with an explicit allow-list; every outbox service relays;
no null seam runs without a startup warning; every implemented endpoint is gateway-reachable.
TESTS: symmetry gate self-test; relay registration architecture test; durable dedupe across restart;
FHIR metadata reachable anonymously through Kong.
```

## Gate 4 — a11y and i18n truth (close the measurement holes)

```text
Read AUDIT-R3 §1 Theme 4 and §3 "a11y / i18n". The sweep is real (test/a11y-routes.test.tsx:27-29,
EN+AR x light+dark over every route) — the problem is what it cannot see.

4.1 VACUOUS COVERAGE: the 9 policy-family screens default to createHttpPolicyApi() (PolicyBook.tsx:79,
  MemberAdmin.tsx:160, PolicyBulk.tsx:112, PolicyProductAdmin.tsx:100,141, NetworkTierAdmin.tsx:66,
  PolicyAnalytics.tsx:125) and CallCentre uses raw fetch — with NO fixture path, so the axe sweep only
  ever sees their ERROR state. Give them DevApiClient fixtures like every other screen, then re-run.
  Expect real violations to appear: fix them.
4.2 CONTRAST: axe's color-contrast rule is disabled under jsdom (a11y-routes.test.tsx:63-65). The
  Playwright job exists — extend it to cover EVERY route in EN+AR x light+dark, not a sample, and make
  it blocking. Contrast is the rule most likely to break with the v1.1 glass tokens.
4.3 ARABIC IS BROKEN FOR SERVER DATA: HttpApiClient.ts:83 does `ar: String(s)` — every server label
  renders English in the Arabic UI, at ~45 sites. Invisible in dev and in the whole test suite because
  DevApiClient supplies real Arabic.
  - Where the API returns bilingual pairs, USE THEM: nameAr is deliberately dropped at
    PolicyBook.tsx:232,488 and NetworkTierAdmin.tsx:126 — follow the correct <BiName> pattern at
    PolicyProductAdmin.tsx:124.
  - Where the API returns an ENUM, translate client-side with a shared enum-label map (the pattern
    already exists: identifierTypeLabel is imported at CallCentre.tsx and used at :320 but NOT at :286;
    the wrap-up outcome select at :370 renders raw enums while reason selects at :51-69 are translated).
  - Where the API returns free text with no Arabic, render it with lang/dir attributes so it is at
    least announced correctly, and log the gap.
  - ADD THE TEST that would have caught this: run a representative set of screens against the LIVE
    client shape in AR and assert no rendered label equals its English source where an Arabic value
    was available.
4.4 Sweep the remaining ../21 DoD per screen: >=44px targets, visible 3px focus, four-cue status
  (hue+icon+shape+text), aria-live on async outcomes, keyboard reachability, RTL mirroring of
  directional icons. Fix what fails; do not widen the tests.

ACCEPTANCE: every route's axe run exercises a POPULATED state; contrast is enforced in a real browser
across all routes in both locales; no server-supplied label renders English in the AR UI where an
Arabic value exists.
```

## Gate 5 — Documentation, pipeline and architectural truth

```text
5.1 BUILD-STATUS.md:120-129 and :143-144 carry ten stale all-☐ rows that CONTRADICT the ☑ rows directly
    above them. Delete them. Add the 22.x rows. Define the ◪ glyph at :89 or replace it.
5.2 docs/HANDOFF.md is named as the entry point (BUILD-STATUS.md:8) and is ~12 phases stale — it still
    documents Keycloak, hello-service, ":8090 hello", and "107 tests green" (now ~1,633). Rewrite it.
5.3 release.yml is almost entirely `echo`: image build (:69), cosign (:79), Harbor push (:81), all four
    Helm deploys (:91,:101,:113,:136), DAST (:115), migration dry-run (:117), canary (:139), smoke
    (:142) — and it omits finance + interop (:29-31,:62-63). Either implement or mark the pipeline
    HONESTLY as a scaffold in the file header and in BUILD-STATUS:101, which currently claims a gated
    dev->QA->staging->prod flow.
5.4 GitLab's a11y gate is a literal echo placeholder (.gitlab-ci.yml:171-183) and GitLab has no
    frontend gates and no SPA-scope gate at all — ADR-0001's amendment promises "neither reimplements a
    check". Make that true or amend the ADR again.
5.5 prometheus.yml:33-51 scrapes 17 of 20 services — add document, identity, interop.
5.6 infra/keycloak/README.md:13 declares a dead 16-scope catalog "authoritative … enforced at Kong and
    each service" against a live 79-scope contract (IdentityContract.cs:24-65). Kong enforces NO scope.
    Delete the directory or mark it DEPRECATED pointing at IdentityContract.
5.7 .gitlab-ci.yml:31 declares COVERAGE_MIN "80" while the real gate is domain>=58/overall>=45
    (backend-ci.yml:67,71) and CLAUDE.md says 80. Pick one number, put it in one place, state the ramp.
5.8 ARCHITECTURAL DEVIATIONS — build or retire by ADR, do not leave silent:
    - Web BFF + Mobile BFF (../16 §115-117,171) do not exist and no ADR retires them. The SPA calls
      services directly through Kong. Decide and write ADR-0022.
    - ADR-0002 commits to infra/tofu, infra/ansible, per-service Helm: ZERO .tf files and no Chart.yaml
      exist, so GitLab's `tofu validate` and `helm lint` jobs are structural no-ops passing on nothing.
      Author the IaC or amend the ADR and delete the fake gates.
    - CLAUDE.md + ADR-0005 name Cerbos/OPA; nothing exists and Kong validates signature+exp only, so
      doc 18's "gateway coarse authz" leg is unimplemented. Record the deferral explicitly.
    - ADR-0019 and ADR-0020 are unsigned while code already depends on ADR-0020 via
      Membership:PlanChangeConsumption. Get signatures or mark the setting provisional.
5.9 Orphan tables: expose or drop policy/provider backfill_reconciliation (the migrations say a human
    must read a report that no endpoint serves), and the *_history twins with no DbSet.

ACCEPTANCE: no status row contradicts another; HANDOFF matches reality; every CI gate either enforces
something or is deleted; every architectural deviation is covered by a signed ADR.
```

---

## Guardrails
- **Finish, don't restart.** No screen is rewritten; each gains the missing layer.
- **Never widen a test to make it pass** — Gate 4 exists because tests were passing against error states and English-in-Arabic. If a newly-honest test fails, fix the code.
- **No silent anything**: no swallowed catch, no local success chip, no `default: return false` on an unknown event, no null seam without a startup warning.
- Idempotency keys are **per intent**, never per attempt and never content-derived.
- Every mutation added must audit, respect min-necessary projection, and carry the branch/tenant context — Gate 2 adds a lot of write surface and must not become an authorization regression.
- Full suite green after each gate (`./dotnet.sh test HbmpPlatform.sln -c Release` + `pnpm -r test`), **including the untouched min-necessary, RLS, SoD and sensitive-gate suites**.

## Done when
- [ ] Main tree holds docs 39/40 + prompts 20/21; indexes agree; dev-DB scope pollution cleaned; worktree gone.
- [ ] Every mutating screen surfaces server errors, confirms from server state, and carries a stable per-intent Idempotency-Key; a replay test proves consume/decide apply once.
- [ ] Doctors can author, amend and sign notes; call-centre books real slots; the branch switcher works in live mode.
- [ ] Admin/Platform, Claims, tier assignment, enrolment, grant revocation and document upload are all operable; no client method lacks a caller; no nav label over-promises; no duplicate routes.
- [ ] Event-symmetry CI gate green with an explicit allow-list; every outbox service relays; dedupe survives restart; no null seam runs silently; every endpoint is gateway-reachable and `/fhir/r4/metadata` is anonymously reachable.
- [ ] axe runs against populated states for every route; contrast enforced in a real browser EN+AR; no English-in-Arabic where an Arabic value exists.
- [ ] Status docs, HANDOFF, release pipeline, scope catalog and coverage numbers are all true; BFF, IaC and Cerbos deviations each resolved by a signed ADR.

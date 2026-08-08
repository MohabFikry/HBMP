# Mersal HBMP — Completion Map (Audit R3)

**Date:** 2026-07-29 · **Question asked:** *what is half built, especially screens and portals?*
**Method:** four parallel evidence-based audits (file:line) — design-set expectation inventory, frontend layer-ladder grading, backend service depth, wiring/CI/status truth. Prior audits: [R1](AUDIT-2026-07-26.md) (security/quality/wiring), [R2](AUDIT-R2-E2E.md) (+ domain correctness/UX).
**Done bar (user-set):** a screen counts as done only at **L1 exists → L2 routed → L3 live data → L4 working writes → L5 a11y/RTL**. Anything less is PARTIAL with the missing layer named.
**Remediation:** every finding maps to a gate in [`../HBMP-Design/claude-code-prompts/phase-22-completion.md`](../HBMP-Design/claude-code-prompts/phase-22-completion.md)

---

## 0. Read this first — the working tree is split

Four design artefacts exist **only on the `phase-20-patient-profile` git worktree**, not on the main tree:

| File | Main tree | Worktree |
|---|---|---|
| `HBMP-Design/39-patient-profile.md` | ❌ absent | ✅ `.claude/worktrees/phase-20-patient-profile/HBMP-Design/39-patient-profile.md` |
| `HBMP-Design/40-user-access-model.md` | ❌ absent | ✅ same worktree |
| `claude-code-prompts/phase-20-patient-profile.md` | ❌ absent | ✅ same worktree |
| `claude-code-prompts/phase-21-user-access-model.md` | ❌ absent | ✅ same worktree |

Meanwhile `docs/BUILD-STATUS.md` **on main** already carries the 20.1–20.5 and 21.0–21.6 rows, and `00-MASTER-PROMPT-LIST.md` on main carries **neither**. `profile-service` is likewise built in that worktree and absent from `services/`. The worktree has also **leaked `profile:*` and `callcentre:history:read` scopes into the shared dev database**, breaking identity-service's frozen-vocabulary test on main.

**This is finding Z0 and it blocks everything else** — the design set on main is not the design set the audits were graded against. Merge or copy first (phase-22 Gate 0).

---

## 1. Executive summary

The honest headline: **the backend is close to complete and the frontend is a very complete reading surface with a thin writing surface.** "Half built" is real, but it is not spread evenly — it concentrates in four places.

**Theme 1 — the frontend's missing layer is L4, not L1.** All 97 portal×section routes resolve to a real screen; there are no placeholder pages left. But of ~57 screens, **~16 are DONE and ~40 are PARTIAL**, and the overwhelming majority of those are partial *because their write paths are missing, unsafe, or silent*. The most consequential: **the doctor cannot author or sign a clinical note** (`DoctorEncounter.tsx:136-141` is read-only; `api/client.ts:93-119` has no note or sign method) — that is the clinician portal's reason to exist. **Admin and Platform are 100% read-only across 12 routes** — nothing can be administered from the admin console. **Claims cannot be adjudicated** despite the SPA holding `claims:adjudicate/decide/settle`. Four write paths still fail silently, and `decide`/`consume` mint a **fresh idempotency key per attempt**, which defeats the exact replay protection CLAUDE.md names by name.

**Theme 2 — one screen is genuinely missing, and it is the newest one.** The patient profile and patient context bar do not exist anywhere in `apps/web/src`. That is expected — phase 20 was written, executed into a worktree, and never merged (Z0).

**Theme 3 — the backend is built but under-wired, and the wiring gap is event names.** No service is skeletal; all 20 have real migrations, endpoints, domain logic. But only **three real bus consumers exist repo-wide**, ~40 event types are published with no subscriber, and **~18 consumer branches are keyed on event names no producer emits** — including five near-misses (`OrderLineConsumed` vs `OrderLinesConsumed`, `EncounterCreated` vs `EncounterStarted`, `AppointmentBooked` vs `ApptBooked`, `AppointmentNoShow` vs `ApptNoShow`, `ClaimSettled` vs `SettlementAdviceIssued.v1`). Because the projectors `return false` into a `default:` case that marks the event **processed**, these fail **silently**. Wire the bus tomorrow and 7 of 16 reporting cases, 4 of 11 analytics cases and 6 of 11 notification routes would still never fire.

**Theme 4 — a11y is measured, but the measurement has a hole.** The axe sweep is genuinely table-driven over every route in EN+AR × light+dark (`test/a11y-routes.test.tsx:27-29`) — that is better than most projects ever get. But the 9 policy-family screens and the call-centre workspace have **no fixture path** (`PolicyBook.tsx:79` and siblings default to `createHttpPolicyApi()`; `CallCentre.tsx:72-81` uses raw `fetch`), so in test mode the sweep only ever sees their **error state**. Their axe pass is vacuous. Separately, `color-contrast` is disabled under jsdom by design, and **~45 server-supplied strings render English in the Arabic UI** because `HttpApiClient.ts:83` stubs `ar` with the English value — invisible in dev and in the entire test suite, because `DevApiClient` supplies real Arabic.

**Verdict:** nothing needs rebuilding. What is needed is **completion**: merge the split tree, finish ~40 write paths, wire the event bus with matching names, close the a11y measurement holes, and make the status docs stop contradicting themselves.

| Stream | R2 | **R3** | Movement |
|---|---|---|---|
| Frontend breadth (L1/L2) | B− | **A** | Every route resolves; no stubs left; portal catalog complete |
| **Frontend depth (L4 writes)** | *not graded* | **C−** | ~40 screens partial; 3 portals effectively read-only |
| Frontend a11y (L5) | *partly* | **B** | Real EN/AR × light/dark sweep — with a vacuous subset and an AR data gap |
| Backend depth | B | **A−** | 20 services, ~200 tables, ~380 endpoints, ~1,300 tests, no skeletons |
| **Backend wiring (events)** | *not graded* | **D** | 3 consumers; ~40 orphan events; ~18 dead handler branches; silent failure |
| Infra/CI/status truth | B | **C+** | Real gates exist; release pipeline is `echo`; status docs self-contradict |

---

## 2. Critical findings (block a pilot)

| # | Finding | Evidence | Gate |
|---|---|---|---|
| **Z0** | **Split working tree.** Docs 39/40, prompts 20/21 and `profile-service` exist only on the `phase-20-patient-profile` worktree; BUILD-STATUS on main references them; the worktree has polluted the shared dev DB with `profile:*` scopes, breaking identity's frozen-vocabulary test | `.claude/worktrees/phase-20-patient-profile/…`; `docs/BUILD-STATUS.md` 20.x/21.x rows; `00-MASTER-PROMPT-LIST.md` (no 20/21 rows) | **0** |
| **Z1** | **Doctor cannot write or sign a clinical note.** The encounter workspace renders SOAP/dx/allergies read-only and the API client has no note/sign method at all | `DoctorEncounter.tsx:136-141`; `api/client.ts:93-119` | **1** |
| **Z2** | **Idempotency keys are minted per attempt on `decide` and `consume`** — the two operations CLAUDE.md names as must-not-double-apply. A timeout retry double-applies | `ApprovalsWorklist.tsx:163,187`; `LabQueue.tsx:46,51` | **1** |
| **Z3** | **Call-centre booking posts a fabricated `crypto.randomUUID()` slot id**, with no slot discovery, no Idempotency-Key on any mutation, and no RFC7807 handling (own `fetch`) | `CallCentre.tsx:103,108-110,72-81` | **1** |
| **Z4** | **~18 event consumer branches are keyed on names nobody publishes**, 5 of them near-miss singular/plural/prefix mismatches, all failing **silently** via `default: return false` marking the event processed | `reporting/EventProjector.cs:72-105`; `AnalyticsProjector.cs:47-66`; `notification/Routing.cs:47-64`; `eligibility/ProjectionUpdater.cs:29` | **3** |
| **Z5** | **`document-service` publishes into a void** — the only one of 19 outbox services that calls `AddHbmpDurableOutbox` and never `AddHbmpOutboxRelay`. `DocumentAttached` is never relayed off the outbox | `services/document/Api/Program.cs:22` | **3** |
| **Z6** | **Admin + Platform portals are entirely read-only** (12 routes): no user create, deprovision, role grant/revoke, MFA reset, tenant create/suspend, break-glass approve/revoke, or system config | `AdminConsole.tsx:106-317`; `api/client.ts:173-184` | **2** |
| **Z7** | **Branch switcher is dead in live mode** — `useBranchContext` builds `${API_BASE}/api/v1/me/branches` while `API_BASE` already ends in `/api/v1` → `…/api/v1/api/v1/…` → 404 → fail-soft → the switcher never renders and `POST /me/active-branch` never lands | `useBranchContext.ts:46,47,79`; `config.ts:33`; `http.ts:82` | **1** |

## 3. High findings

**Frontend write paths (Gate 1/2).** No error surface and no idempotency on `BeneficiaryStatus` (`BeneficiaryPortal.tsx:117-128`), `ApprovalsEmergency` (`ApprovalsExtra.tsx:140-151` — success chip driven by a *local* `done` set, not server state), `NurseVitals` (`NursePortal.tsx:91` — bare `await`, failure hangs on "saving"). `requestReportAccess` and the report-access **decision** send no Idempotency-Key (`HttpApiClient.ts:537-543,570-576`). Order/prescription modals derive the key from **content**, so two legitimately identical orders collapse into one (`HttpApiClient.ts:448,472`).

**Screens whose label over-promises (Gate 2).** `NurseResults` is nav-labelled "Results inbox" and renders **vitals** (`NursePortal.tsx:124-137`). `NetworkLocations` is "Locations **& users**" with no users list (`catalog.ts:220`). `BeneficiaryManage` is "Search / **manage**" and is read-only. `ResultUpload` promises report files and has **no file input** (`ResultUpload.tsx:16`). `NetworkPerformance` shows four counts derived client-side from the directory, not performance data — the code itself admits the metrics endpoint is unrouted (`HttpApiClient.ts:1313-1314`).

**Orphaned capability — built, unreachable (Gate 2).** `revokeReportAccessGrant` (a granted sensitive-result window **cannot be revoked from the UI**), `assignTier`/`updateTier` (you cannot put a provider *into* a tier — the point of the tier screen), `enrol` (no new-member enrolment), `attachPolicyPlan`, `createGroup`, `pinNote`, document **upload** (panel lists and downloads only), `S.substitute` (substitution flow cannot substitute).

**Duplicate routes (Gate 2).** `/lab/queue` ≡ `/lab/consume`, `/pharmacy/queue` ≡ `/pharmacy/dispense`, `/cases/beneficiary-360` ≡ `/cases/my-cases` — six nav entries, three components. The app-bar search is inert (`AppShell.tsx:126-129`); the working search is the separate Ctrl-K palette.

**a11y / i18n (Gate 4).** Policy-family + call-centre routes have no fixture path, so their axe pass only ever exercises the error state. `HttpApiClient.ts:83` copies English into `ar` for every server label — ~45 render sites, incl. `nameAr` deliberately dropped at `PolicyBook.tsx:232,488` and `NetworkTierAdmin.tsx:126` (compare the correct `<BiName>` at `PolicyProductAdmin.tsx:124`). Call-centre identifier checkboxes render raw enums (`CallCentre.tsx:286`) while the helper exists 34 lines below (`:320`); wrap-up outcome select renders raw enums (`:370`).

**Backend seams that are no-ops (Gate 3).** Notification delivery never sends (`NotificationChannels.cs:43-50,78-101`). Claims: `NullOcrProvider`, `NoAuthorizedServiceResolver` (every reimbursement forced to ManualAssessment), `NullWormStore` — **settlement advice stores no bytes** (`SettlementService.cs:16-19`). Provider `AdjudicatedClaimProbe` self-labels *"KNOWN OPEN GAP, not a safe default"* and always returns 0. Interop: 4 of 5 adapters quarantine unconditionally, OCR no-ops, outbound `Map()` is only ever called from a test. **audit-service runs `useInMemoryOutbox: true`** — the audit spine does not durably audit itself (`audit/Api/Program.cs:21`). `IProcessedEventStore` has only an in-memory impl, so consumer dedupe is process-local and lost on restart.

**Never-loaded reference data (Gate 3).** `masterdata.drug_interaction` has **no loader anywhere** — `/drug-interactions/check` can only ever return empty, while `tools/masterdata-loader/README.md:46` claims otherwise. `loinc_code` is migrated and EF-mapped with no loader and no endpoint.

**Wiring / gateway (Gate 3).** `GET /api/v1/metrics` (provider) has no Kong route and the guard **hard-ignores the segment** (`check-kong-route-coverage.py:29`) — the SPA already works around it. `/fhir/r4/metadata`'s public exemption is **inert**: a route-scoped `jwt: enabled: false` under a service-scoped plugin is an inactive instance, not an override — and `ReachabilityTests.cs:76-77` **asserts the broken pattern**, locking it in. `Location` headers point at `/api/v1/service-lines/{id}` and `/api/v1/waitlist/{id}`, which no handler serves.

**Missing by design-spec (Gate 5).** **Web BFF and Mobile BFF** (`16-service-architecture.md:115-117,171`) do not exist and **no ADR retires them** — an undocumented architectural deviation. IaC promised by ADR-0002 (`infra/tofu/`, `infra/ansible/`, per-service Helm) was **never authored**: zero `.tf` files, no `Chart.yaml` — so the GitLab `tofu validate` and `helm lint` jobs are structural no-ops. Cerbos/OPA is in CLAUDE.md and ADR-0005; nothing exists, and Kong validates signature + `exp` only, so the "gateway coarse authz" leg of doc 18 is unimplemented.

**Status-doc truth (Gate 5).** `BUILD-STATUS.md:120-129` and `:143-144` carry **ten stale all-☐ rows that contradict the ☑ rows directly above them**. `docs/HANDOFF.md` — which BUILD-STATUS names as the entry point — is ~12 phases stale, still documenting Keycloak and hello-service and "107 tests green" (now ~1,633). `release.yml` is almost entirely `echo` (build, sign, push, all four deploys, DAST, canary, smoke) and omits finance + interop. Prometheus scrapes 17 of 20 services. `infra/keycloak/README.md:13` still calls a dead 16-scope catalog *"authoritative"* against a live 79-scope contract. GitLab's a11y gate is a literal `echo` placeholder.

---

## 4. Portal completeness ranking

| # | Portal | State |
|---|---|---|
| 1 | **Policy administration** | Deepest in the app — version editor, member detail with dry-run plan change, bulk pipeline, 6-view analytics. Missing: payer/policy/group **create**, enrolment; no fixtures ⇒ vacuous a11y |
| 2 | **Director** | 6 read-only sections, all working, all covered |
| 3 | **Reception** | Small but genuinely complete; check-in is the best write path in the codebase (If-Match, 412 handling, server-confirmed chip) |
| 4 | **Pharmacy** | Dispense is exemplary (`useWrite` stable key, typed-text confirm, translated errors); queue≡dispense duplication; substitution cannot substitute |
| 5 | **Finance** | 5 solid reads + working export; no settlement actions |
| 6 | **Approvals** | Worklist/manual/SLA real; the **decision** path has the idempotency + error defects |
| 7 | **Clinician** | Worklists and the sensitive-result flow are strong; **the encounter cannot author or sign a note** |
| 8 | **Provider network** | Only onboarding writes; performance isn't real data; "users" don't exist |
| 9 | **Beneficiary mgmt** | 10 sections, 5 borrowed from policy; its own three lack error surfaces and idempotency |
| 10 | **Lab / Imaging** | 3 sections ≈ 2 screens; consume can double-apply; no file upload |
| 11 | **Cases** | 3 sections, 2 components, zero writes |
| 12 | **Nurse** | "Results inbox" shows vitals; vitals form swallows failures |
| 13 | **Admin / Platform** | 12 routes, **100% read-only** |
| 14 | **Claims** | 3 read-only tables; no detail, no adjudication |
| 15 | **Call Centre** | Right shape (call bar, verify-before-disclose, 360, wrap-up); least trustworthy write surface |
| — | **Patient profile** | **Does not exist on main** (Z0) |

---

## 5. Gate order (why this sequence)

**Gate 0 — unsplit the tree.** Nothing else can be trusted while main and the worktree disagree about the design set, and the dev-DB scope pollution is failing a test on main today.
**Gate 1 — unsafe writes.** Double-apply on consume/decide, fabricated slot ids, and silent failures are patient-safety and data-integrity issues, not polish.
**Gate 2 — unreachable capability.** Everything already built but with no way to invoke it: admin actions, claims adjudication, tier assignment, enrolment, grant revocation, document upload.
**Gate 3 — backend wiring.** Event-name symmetry, outbox relay, no-op seams, never-loaded reference data, gateway holes. Fixing names before wiring the bus avoids shipping silent no-ops.
**Gate 4 — a11y and i18n truth.** Close the measurement holes (fixtures for policy/call-centre, contrast in a real browser) and the Arabic server-string gap.
**Gate 5 — doc and pipeline truth.** Status rows, HANDOFF, release stubs, dead Keycloak catalog, BFF/IaC/Cerbos deviations either built or retired by ADR.

---

### Cross-references
R1: [AUDIT-2026-07-26.md](AUDIT-2026-07-26.md) · R2: [AUDIT-R2-E2E.md](AUDIT-R2-E2E.md) · Remediation: [phase-22-completion.md](../HBMP-Design/claude-code-prompts/phase-22-completion.md) · Status: [BUILD-STATUS.md](BUILD-STATUS.md)

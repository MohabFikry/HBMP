# Mersal HBMP — Platform-Wide Audit

**Date:** 2026-08-09 · **Scope:** full platform — 22 .NET 8 services, shared libs, React/TS SPA, infra, design system, docs · **HEAD:** `950f056` · **Nature:** read-only audit. No code was modified. This report is the basis for a separate, prioritized fix effort.

**Method:** the platform was audited across fifteen parallel workstreams (core benefit-chain services; operational services; shared libs; security vs `18-security-model.md`; frontend code & wiring; UI/UX & design consistency; status/feedback; component drift; RTL/Arabic; token/a11y/UX; requirements & MVP coverage; NFR & operational readiness; prior-audit open items; build-status & doc drift; cross-service wiring). Every finding cites `file:line` evidence and was confirmed by reading code. The headline Critical findings in §1 were additionally re-verified by the coordinator directly against the source.

---

## 0. Executive summary

**This is a high-quality, unusually disciplined codebase with a small number of severe wiring gaps that stop the core benefit workflow from completing end-to-end.** The engineering hygiene is excellent — near-zero TODO/dead-code, no unparameterized SQL, nullable throughout, architecture tests that mechanically enforce the money type, the Cairo clock, RLS, and outbox atomicity, a self-enforcing CI gate suite with a freshness watchdog, and a status ledger honest enough to downgrade its own past claims. Individual subsystems (the orders consume path, the pharmacy dispense path, JWT validation, audit hash-chaining, ClamAV upload handling, the SPA's contract/auth seams, chart accessibility) are genuinely strong.

The problems cluster at the **seams between services**, not inside them:

- **The prior-authorization saga has neither leg.** Orders and prescriptions that require approval emit domain events that *no consumer routes into the approvals worklist*, and *no consumer applies an approval decision back* to release the order/Rx. The platform's central promise — gated benefits — cannot complete through domain events.
- **Terminated members stay eligible**, because termination doesn't emit the one event eligibility listens to.
- **Message loss is systemic**: consumers `nack(requeue:false)` while no dead-letter exchange exists anywhere, so any transient failure silently destroys the message — including benefit-accumulator movements and audit events.
- **The only concretely-deployable environment ships with MFA off, HTTPS-metadata off, and a shared demo password**, and the production infra layer (Helm/k3s/mTLS/WAF/OpenBao) is not built — it exists only as design.
- **Two audit holes**: the audit service audits itself into an in-memory buffer, and document-service never relays its outbox — so its PHI-access audit events never leave the box.

None of these are hard to fix; several are one-liners. But until the saga and the messaging reliability are addressed, the system demos correctly (the SPA calls endpoints directly) while failing under the event-driven flow it's designed around.

### Findings by severity

| Severity | Count | Theme concentration |
|---|---|---|
| Critical | 7 | Event choreography (saga, termination, message loss), deployable-stack security, audit integrity |
| High | ~26 | Idempotency gaps, audit atomicity, RLS coverage, unconsumed queues, no contract/E2E tests, PHI-read gating, frontend bundle/validation |
| Medium | ~40 | Money/time consistency, component & token drift, a11y (RTL keys, headings, hover contrast), doc drift, resilience |
| Low | ~35 | Polish, naming, cosmetics, deferred hardening |

---

## 1. Critical findings (verified)

### C1 — The prior-authorization saga forward leg does not exist
Orders emit `OrderPendingApproval` and pharmacy emits `RxSubmitted` to `orders.events`/`pharmacy.events`, whose only consumer (policy) returns `[]` for them (`services/policy/Api/BenefitConsumptionConsumer.cs:160-179`). The ingestion endpoint `POST /api/v1/authorizations` (scope `auth:ingest`, `services/approvals/Api/Worklist.cs:110`) has **zero callers** — `auth:ingest` appears only on the endpoint itself, in comments, and in the identity scope catalog (verified). No mirror routes these events to approvals. **An order or prescription routed for approval never reaches the reviewer worklist** through the event flow; it only appears if a human raises a manual/break-glass entry or the SPA posts an authorization directly.
*Fix:* mirror `OrderPendingApproval`/`RxSubmitted` to an approvals-owned queue with a consumer that calls the ingestion seam, or dual-publish as `FulfilmentRecorded` already does.

### C2 — The approval decision return leg is missing; approved orders/prescriptions stay stranded
No service consumes `approvals.events`. Orders defines `PendingApproval → Approved/Rejected` (`services/orders/Domain/OrderWorkflow.cs:11`) but nothing executes that transition; approvals pushes downstream only for validity extensions (`ValidityExtensionApplier.cs`). Because `IsDispensable` requires `Approved` and the only path that sets a prescription `Approved` is the auto-route at creation, **a gated prescription can never be dispensed after approval**, and rejection compensation (release/void) is absent.
*Fix:* a consumer in orders/pharmacy on a mirrored decisions queue keyed by source ref, using the existing idempotency stores; implement the reject/compensation path.

> C1+C2 interlock with the completeness finding that **no approvals rules engine exists** beyond a single boolean: `RequiresPreauthAsync` reads one flag off the plan version; assignment is manual; "SLA" is a reported TAT, not an enforced target (`docs/BUILD-STATUS.md:208`, ADR-0035 spec-only). The decisioning is thin *and* the wiring is incomplete.

### C3 — Terminated members remain eligible
`TerminateAsync` end-dates coverages locally and publishes only `MemberTerminated` (`services/policy/Infrastructure/MembershipCommands.cs:421`). Eligibility's projector has a `case "CoverageChanged"` but no case for termination — it hits `default: break` (`services/eligibility/Infrastructure/ProjectionUpdater.cs:32`) — and the enrolment path's own comment states eligibility builds coverage "from CoverageChanged and from nothing else." Termination and reinstatement emit no `CoverageChanged`, so eligibility keeps answering **Eligible** from stale coverage rows (`EligibilityChecker.cs:89`). Verified.
*Fix:* publish per-coverage `CoverageChanged` (status/effectiveTo) inside Terminate/Reinstate, exactly as enrolment does.

### C4 — Systemic event loss: consumers drop messages, no dead-letter exchange exists
Every consumer except one handles any processing exception with `BasicNack(requeue:false)` (`eligibility/Api/EventConsumer.cs:96`, `policy/Api/BenefitConsumptionConsumer.cs:128`, `identity/Api/ProgramEventConsumer.cs:131`, `emr/Api/CareEpisodeConsumer.cs:129`, and others), while **no queue anywhere declares `x-dead-letter-exchange`** (verified: zero matches across services/libs/infra). A nacked message is dropped by RabbitMQ, not parked. A transient DB blip during `OrderLinesConsumed` permanently loses a benefit-accumulator movement (limits silently wrong); a dropped `BeneficiaryRegistered` leaves a person unknown at every desk. The audit consumer has the same shape with a comment claiming a broker-side DLQ that its own `QueueDeclare` never configures (`services/audit/Infrastructure/RabbitMqAuditConsumer.cs:40,89`). The lone opposite case (approvals `FulfilmentConsumer.cs:134`, `requeue:true`, no backoff) hot-loops instead.
*Fix:* declare DLX/DLQ arguments in the shared consumer setup; distinguish transient (requeue-with-backoff) from poison (DLQ); replace the approvals hot-loop with bounded redelivery.

### C5 — document-service never relays its outbox, so its PHI-access audit trail is stranded
`services/document/Api/Program.cs:21-22` registers `AddHbmpEvents` + `AddHbmpDurableOutbox<DocumentDbContext>` but **not** `AddHbmpOutboxRelay()`. Because `AddHbmpEvents` routes the audit client through the outbox, every document upload/download audit event and every `DocumentAttached` sits in `document.outbox` forever. inventory-service documents this exact bug in a code comment (`services/inventory/Api/Program.cs:22-25`) — document was never fixed. Verified. This is a silent audit-coverage hole on the service that streams "lists of identified people."
*Fix:* add the one line `AddHbmpOutboxRelay()`; add an architecture test asserting durable-outbox ⇒ relay pairing.

### C6 — The only deployable stack ships insecure by default
`infra/compose/compose.yaml` sets `Auth__ProtectedScopeRequiresMfa: "false"` on **21 services** (verified; code default is `true`, `libs/auth/HbmpAuthOptions.cs:36`), alongside `Auth__RequireHttpsMetadata: "false"` and a shared demo password (`services/identity/Api/Auth/UserSeeder.cs`). Compose Tier 1 is the only environment that exists — `infra/helm`, `infra/tofu`, `infra/ansible` are `.gitkeep`/README stubs, so there is no k3s NetworkPolicy, no Linkerd mTLS, no ModSecurity/OWASP CRS, no OpenBao wiring. As deployable today, staff authenticate password-only over plaintext and receive a fully-scoped token with no second factor on every PHI endpoint.
*Fix:* make MFA-off/HTTPS-off strictly ephemeral Development flags that CI forbids elsewhere; build the Helm/mTLS/WAF/secrets layer before any real deployment; enforce MFA + HTTPS metadata in every non-dev profile.

### C7 — The audit spine cannot durably audit itself
`services/audit/Api/Program.cs:21` registers `AddHbmpAuditClient("audit-service", useInMemoryOutbox: true)` unconditionally, on a stale comment ("until libs/events (0.5) provides the durable outbox" — which shipped in 16.2). The `audit.read` event emitted for every read of the audit log lands in an in-memory outbox nothing drains — lost on restart, in production too. This violates the "audit reads are themselves audited" invariant. Reported independently by three workstreams.
*Fix:* bind the durable sink over the audit DbContext (or a direct-write sink into its own store); delete the flag.

---

## 2. High-severity findings

Grouped by theme; deduplicated across workstreams.

### 2.1 Idempotency & concurrency
- **Patient registration demands `Idempotency-Key` then ignores it** — 400 without the header (`patient/Api/Program.cs:79-80,533-534`) but no ledger stores/replays it; retries can create duplicate people.
- **Claims reimbursement submission has no idempotency at all** (`claims/Api/ReimbursementEndpoints.cs:22` + `ReimbursementService.cs:49-57`) — retry double-creates; auto-match could approve the same receipt twice. It is the only claims write without the header.
- **Approvals replay ignores the request body** — a reject retried with a key previously used for an approve returns the approval as 200 OK (`approvals/Api/Decisions.cs:122-130`). Same body-blind replay in claims decisions/submissions/adjustments.
- **Approvals applies the validity extension downstream *before* the decision commits** (`Decisions.cs:157-167`) — a lost xmin race leaves an extended expiry with no approving decision (or a rejection).
- **Eligibility snapshot persist is an unguarded delete-then-insert** (`EligibilityChecker.cs:69-74`) — concurrent front-desk checks 500, and prior point-in-time snapshots are hard-deleted.
- **No optimistic-concurrency token on Beneficiary** (`PatientDbContext`) — conflicting lifecycle transitions (e.g. Block vs Activate) both commit; contradictory `BeneficiaryStatusChanged` events downstream.
- Several duplicate-key races surface as 500 instead of the mapped 409 (patient duplicate-identifier; orders/approvals concurrent same-key).

### 2.2 Audit atomicity & PHI-read coverage
- **Audit emitted after commit** in approvals, emr, and orders-create (`emr/Api/ClinicalRecords.cs:218-219, 346-347, 493-494`; `approvals/Api/Decisions.cs:262-271`; `orders/Api/Orders.cs:233-240`) — a crash between commit and emit leaves a mutation with no hash-chained audit record. Patient/policy/orders-cancel do it correctly inside the transaction; the outbox makes the fix free.
- **Pharmacy audits successful PHI reads without the entity id** — `AuditRead` records `EntityId = "queue"|"search"|"open"` (`pharmacy/Api/Dispensing.cs:443-449`), so "who viewed RX-X" is unanswerable; the reject path does it right.
- **Unaudited PHI reads**: eligibility `GET /members/{id}/status`; orders `GET /investigation-orders/{id}` and `/mine`.

### 2.3 Security & access control
- **operational-document PHI downloads have no role/scope gate** — `document/Api/OperationalDocuments.cs:111-141` ends in a bare `.RequireAuthorization()` while upload requires `document:write`; any same-tenant token of any role can enumerate and stream bulk extracts described in code as "a list of identified people." (Reported by security and operational-services streams.)
- **Provider-isolation RLS fails OPEN on a missing `provider_id`** (`provider/Infrastructure/Migrations/0003_rls.sql:25-26`) — an empty/unset provider context grants tenant-wide read of all providers' rows; the inverse of the hardened tenant sentinel. A mis-provisioned or machine token silently gets cross-provider access.
- **Admin break-glass "step-up MFA" is client-asserted** — `BreakGlassActivateBody(bool StepUpSatisfied…)` is trusted (`admin/Api/PlatformEndpoints.cs:11`, `BreakGlassAdminService.cs:82-99`); POST `{"stepUpSatisfied":true}` opens the elevated window with no MFA challenge.
- **Provider "dual-controlled" termination accepts any typed string as the second approver** (`provider/Api/Onboarding.cs:74-76`) — the second approver never authenticates; termination completes in one request.
- **Finance settlement prices unpriced service codes at the provider's own uncapped observed average** (`finance/Infrastructure/SettlementGenerator.cs:28,53`) — contrary to "absence of a tariff is not permission to pay anything"; one mispriced small delivery skews the rate.

### 2.4 Wiring — unconsumed queues & dead paths
- **~10 queues are published-to and consumed by nobody, with no TTL/DLX** (approvals.events, provider.events, emr.events, claims.events, case.events, callcentre.events, document.events, finance.events, notification.events, interop.inbound) — unbounded persistent backlog.
- **Break-glass decisions carry no `tenantId`** (`approvals/Api/BreakGlass.cs:110-115`) and are dead-lettered by both mirror consumers — emergency/manual approvals vanish from the authorization read model (TAT/breach counts) and the care timeline.
- **Notification routes for `OrderResultUploaded`, `RxApproved`, `ApptNoShow` can never fire** — the routing keys were renamed to publisher names but no notification-shaped copy is ever enqueued; only 4 event types actually arrive on `notification.domain-events`.
- **callcentre appointment confirmations are double-dead** — published to a queue nobody binds, with no routing-table entry (`callcentre/Api/CallAppointments.cs:125`).
- **Interop inbound pipeline ends in a void** — ACL-passed partner messages are marked `Mapped` and staged to `interop.inbound`, which nothing consumes.

### 2.5 Frontend & delivery
- **The 4,111-line fixture client and demo auth ship in the production main chunk** (verified strings in `dist/assets/index-*.js`) — cause: static import of both clients + a non-foldable `LIVE` flag (`ApiProvider.tsx:3-4`, `config.ts:30`). A live deployment carries a synthetic demo backend and role-picker login in its JS.
- **`policyApi.ts`/`branchApi.ts` skip zod entirely** (~80 operations incl. money fields) — `branchApi.ts:10` is a bare cast; forfeits the loud schema-failure behavior the rest of the app relies on. `HttpApiClient` also defaults required fields *before* validating, converting contract drift into plausible wrong data.
- **CallCentre owns a private `fetch` wrapper** (`CallCentre.tsx:135-154`) that bypasses the RFC-7807 error contract and collapses failures into empty-string sentinels.
- **Playwright covers only color contrast — no browser E2E of any critical flow**; all ~1,000 vitest cases run jsdom against fixtures. The repo already ate one production drift incident this class would have caught.

### 2.6 Testing, contracts & CI
- **No Pact/contract tests exist** despite the CLAUDE.md mandate — every sync and async service pair is uncontracted; this is exactly how the termination and notification payload drift shipped.
- **31.3 migration gate is red**: 21 pre-existing `DROP CONSTRAINT` lines across 5 migrations fail `check-migration-compat`, "still unacknowledged" (`docs/BUILD-STATUS.md:989-997`); also 4 callcentre/emr contract ops.
- **The PERF go-live gate is hollow** — `check-golive-gates.py` sets `signed_check=None` for PERF, so `PERFORMANCE-BASELINE.md` passes on file existence alone despite 14 PENDING measurement cells.
- **Helm is a stub whose liveness probe (`/health/live`) no service maps** (services map `/health/ready` only) — deployed as-is, every pod crash-loops.

### 2.7 Requirements
- **Email notification delivery is a log-only stub** — the one unmet MVP "Must." notification-service registers `LoggingEmailProvider` and has no recipient-address source (`notification/Infrastructure/DependencyInjection.cs:30`; `libs/email/EmailSender.cs:27-32`). Only identity's password-reset mail actually sends.
- **RTL/Arabic keyboard direction is inverted** — `SegmentedControl` arrow keys and Radix `Tabs` ignore document direction (no `DirectionProvider` anywhere), so ArrowRight moves focus the wrong way in Arabic on the spec's signature filter control.

---

## 3. Medium-severity findings (condensed)

**Money & time consistency.** `Mersal.Money` is adopted by only claims + eligibility; pharmacy pricing, finance settlement, policy limits, and reporting aggregates all do raw decimal math — and with *conflicting* rounding (`AwayFromZero` in policy/reporting vs the mandated banker's `ToEven`), so dashboards can disagree with settlement at the cent level. `Money.CapTo` is dead code while claims re-implements the clamp. Chronic refill windows are evaluated on the UTC date, not Cairo (`pharmacy/…/DispenseExecutor.cs:201`, `RefillWindowSweeper.cs:65`) — a patient at the counter 00:00–02:00 Cairo on their window-open date is refused, and the time lib documents this exact defect.

**RLS coverage is partial, not universal.** `ENABLE ROW LEVEL SECURITY` ratios are low on clinical services (emr 5/25 migrations, orders 3/16, pharmacy 5/16); identity has none. pgcrypto column-level PHI encryption (§5.2) is entirely absent — PHI columns are plaintext relying solely on LUKS. There is no true edge authorization (Kong OSS `jwt` verifies only `exp`).

**Case & callcentre.** Case idempotency ledger is dead code (retried `POST /cases` duplicates); coordination-task/escalation mutations write no audit; the profile seam over-discloses a beneficiary's other-manager cases; callcentre writes the raw phone/national-ID search query into the audit `EntityId`.

**Component & token drift (frontend).** Four parallel table systems beside the DS `DataTable` (two entirely unclassed); three hand-rolled modals, one (`PolicyPanels.tsx:435`) with a real focus-trap/scrim a11y bug the codebase already diagnosed elsewhere; 18 raw `rx-field-input`s that skip error/required wiring; three async-combobox reimplementations of the platform's riskiest widget; 10 competing panel-header families and 5 KPI-tile families in a 5,591-line `app.css`; no skeleton loaders anywhere. ~99 hardcoded font-sizes and off-ladder overlay shadows sit beside the token scale; `ReportAccessInbox` references a token that doesn't exist so a hardcoded red always wins.

**Accessibility beyond keys.** Hover-on-tint states drop to 4.44:1 (only the active state was fixed); heading hierarchy skips h1→h3 on several admin screens (invisible to the axe gate, which filters to serious/critical); the in-browser contrast job samples 12 of 112 routes and never paints modals/toasts/hover.

**Doc/reality drift.** Service count is 22 in the repo but 21 in HANDOFF and 14 in CLAUDE.md's layout; architecture doc 16 omits admin/finance/interop and still names Keycloak (retired Phase 17); the design set runs to doc 46 but HANDOFF says "0A–40." Phases 22 and 23 are effectively untracked (23 doesn't exist yet is a declared dependency).

**Resilience.** No retry/circuit-breaker layer by explicit decision, but most typed HTTP clients keep the 100s default timeout — a hung masterdata pins order creation ~100s.

**Frontend seams.** No fixture implementations for the narrow policy/branch/call-centre APIs (demo harness dead-ends); no unsaved-changes protection outside the two clinical composers; the `ApiClient` god-interface (~190 methods, two 4,000-line implementations); `i18next` declared but never imported (homegrown scheme is actually stronger — needs an ADR, not a "fix").

---

## 4. Low-severity findings (themes)

Hard-delete of specialty assignments and role-scope config (soft-delete-only platform); RFC-7807 stragglers with no `type`; JSON audit payloads built by string interpolation; `ar-EG` renders all `Intl` output in Arabic-Indic numerals while IDs stay Western (two numeral systems per screen — a deliberate decision to make, not a bug); no bidi isolation on identifier spans (a two-line `.tnum`/`.mono` fix covering ~20 render sites); fonts hotlinked from Google (on-prem/PDPL concern); hardcoded `→` glyphs and a few unmirrored decorations; `window.confirm` in FinancePortal (browser-language dialog); dead exports (`ConfirmAction`, `priority()`, `referralStatus()`); NATS container runs unused; `HttpApiClient` `any` count regressed to 285; the `/login` a11y-contrast case can never pass as written. Full per-stream detail is in the working notes.

---

## 5. What's done well (verified)

This matters for calibration — the defects above sit on top of real strength:

- **Orders consume and pharmacy dispense** are exemplary: append-only event tables, unique idempotency key + request-hash reuse rejection, xmin optimistic concurrency, DB CHECK backstop, guarded status CAS with bounded retry, outbox enqueued inside the transaction — proven by real parallel-Postgres racer tests.
- **Security controls that ARE in place**: strict complete JWT validation (issuer/audience/JWKS/lifetime/skew); parameterized RLS context on every pooled connection with a `(no-tenant)` sentinel and FORCE RLS on 57 tables; zero SQL-injection surface; ClamAV upload scanning that fails closed; real hardened audit hash-chaining (append-only DB, deny triggers, resume-past-break verify); NIST-ish password/lockout with a non-enumerating failure reason; ABAC predicates enforced in code; PKCE + antiforgery + same-site returnUrl.
- **Money & time discipline where adopted**: banker's rounding at construction, cross-currency throws, cost-share computed as residue so splits reconcile; an architecture test bans bare clocks platform-wide with zero domain violations.
- **Frontend**: principled server/client state separation, single `useWrite` mutation path with correct idempotency-key lifecycle, single-flight token renewal, fail-closed role mapping, systematic logical-property CSS, per-role code-splitting, and an `AsyncSection` with remedy-aware error states (401→sign-in, 403→no false Retry).
- **Design system**: `StatusChip` makes a colour-only status structurally impossible; `tokens.css` is an audited contract with measured contrast ratios; axe runs the full portal catalog × en/ar × light/dark; chart accessibility (always-present data tables, hatch patterns) exceeds the spec.
- **Process**: ~20 blocking CI gates plus a freshness watchdog on the gates themselves; a status ledger honest enough to downgrade its own claims; outbox-atomicity debt ratcheted to near-zero.

---

## 5a. Remediation status (updated 2026-08-09, branch `fix/audit-2026-08-09-critical`)

Fixes land with the evidence that they work: every item below was verified against the running Compose
Postgres and broker, not just compiled. `--with-db` throughout, so the DB-gated suites actually ran.

| # | Finding | Status | Evidence |
|---|---|---|---|
| C1 | a routed order/prescription reached no reviewer (`auth:ingest` had zero callers) | **Fixed** | `ApprovalRoutingFeed` mirrors `OrderPendingApproval`/`RxSubmitted` to `approvals.routing-events`; `RoutingConsumer` + `RoutedAuthorizationIngestor` raise the same Submitted authorization the endpoint does; 12 tests (ADR-0041) |
| C2 | nothing consumed `approvals.events`, so an approved order/Rx was never released | **Fixed** | `ApprovalDecisionFeed` mirrors every settling decision to `orders.approval-decisions` and `pharmacy.approval-decisions`; new consumers apply `PendingApproval→Approved→Active` and `Submitted→Approved`, cancel out-of-scope lines on a partial, and make rejection terminal; 16 tests |
| C5 | document-service never relayed its outbox | **Fixed** | `AddHbmpOutboxRelay()` added; `OutboxRelayRegistrationTests` now asserts the outbox⇒relay pair across all 20 staging services |
| C7 | audit spine self-audited into an in-memory buffer | **Fixed** | Publishes via `DirectAuditSink` onto `audit.events`, ingested through the same single write path; 22 audit tests green |
| C3 | terminated members stayed Eligible | **Fixed** | `CoverageChanged` now published on terminate/reinstate; consumer honours explicit-null-clears; 483 policy + 66 eligibility tests green |
| C4 | rejected messages were dropped (no DLX anywhere) | **Fixed** | `hbmp.dlx`/`hbmp.dead-letter` + policy applied by `rabbitmq-init`; **proven end-to-end** — a message rejected with `requeue:false` arrived in the DLQ; approvals hot-loop bounded; `DeadLetterQueueNotEmpty` alert added |
| C6 | MFA-off/HTTPS-off could travel out of dev | **Fixed** | `check-dev-auth-flags.py` in backend-ci + `REQUIRED_GATES`; verified it catches both a `Production` pin and a missing environment |
| H | patient registration demanded a key and ignored it | **Fixed** | `patient.processed_request` (0008) records key + body hash; a retry replays 200, a key reused for a different person is 422. 4 tests, including the no-card/no-identifier arrival every existing duplicate check is blind to |
| H | claims reimbursement had no idempotency at all | **Fixed** | Header now required; key + hash on `reimbursement_request` (0009); the replay short-circuits **before** the malware scan and the OCR run. 3 tests |
| H | replays were body-blind in approvals and claims | **Fixed** | `request_hash` on approvals' `processed_request` (0011) and on claims' decision / adjustment / submission rows (0009); a mismatched replay is 422 rather than the earlier answer. 5 tests, incl. a reject-under-an-approve's-key and a deny-under-an-approve's-key |
| H | audit emitted AFTER its own commit | **Fixed** | A crash between the two lost the record forever. The audit named 3 files; a new `AuditAtomicityTests` scan found **24 sites in 21 files**, all now emitting inside the transaction, with a one-way debt register at zero |
| H | list PHI reads audited without record ids | **Fixed** | pharmacy's `AuditRead` recorded `EntityId = "queue"\|"search"\|"open"`, so "who viewed RX-X" had no answer — orders' queue carried the identical defect. Both now emit one event per disclosed record |
| H | unaudited PHI reads | **Fixed** | eligibility `GET /members/{id}/status` (incl. the miss, which is still an answer about a person), orders `GET /{id}` and `/mine` |
| H | operational-document PHI downloads ungated | **Fixed** | Gated on `document:write` through the authorization engine so denials are audited; 3 new policy tests |
| H | provider RLS failed **open** on a missing `provider_id` | **Fixed** | `(no-provider)` sentinel; role list unified onto `HbmpPrincipal` so both layers agree; 9 new tests |
| H | break-glass step-up was client-asserted | **Fixed** | Reads `principal.MfaSatisfied`; field removed from the contract. *Known limit recorded:* proves session MFA, not per-action freshness — that needs `auth_time` in the frozen token contract |
| H | provider "dual control" accepted any typed string | **Fixed** | Two-call flow; approver acts under their own token; `approved_by <> requested_by` and one-open-request enforced **at the database**; 4 tests |
| H | the fixture backend and the bypass sign-in shipped in the LIVE bundle | **Fixed** | Verified first (`MRS-M-10231`, `Amal Hassan`, `أمل حسن` were plain strings in a `VITE_LIVE=1` `dist/`). `DevApiClient`, `DevAuthClient` and the role picker now sit behind `@dev/fixtures`, aliased to a refusing stub for a live build; `check-live-bundle-clean.py` rebuilds both variants and reads the emitted JS |
| H | `policyApi`/`branchApi` skipped zod on ~80 operations | **Fixed** | 62 shapes rewritten as `z.object` with `z.infer` types — one definition, not two, which answers the objection the file's own header raised. `.passthrough()` keeps a server that ADDS a field from breaking an older bundle |
| H | `HttpApiClient` defaulted required fields BEFORE validating | **Fixed** | `money()` refused `?? 0` (10 sites) and `required()` refuses `?? ""` on 21 identifiers, both naming the field. Narrow on purpose: the rule is about values that would be BELIEVED, not a ban on `??` |
| H | CallCentre's private `fetch` could not report a transport failure | **Fixed** | A dropped connection raises `ApiError("network")` instead of escaping as `TypeError: Failed to fetch`; verdicts are now `CcOutcome` so the server's RFC-7807 `detail` reaches the agent on the generic failure (the named ones — 409/412/422/403 — keep their own sentences) |
| H | nothing the SPA started could be cancelled | **Fixed** | Every `http.ts` verb takes an `AbortSignal`, `useAsync` supplies and aborts one, the three per-keystroke comboboxes forward it. A cancellation is `ApiError("aborted")`, never `network`. *Stated precisely:* this was never a correctness bug — `live` guards already discarded superseded answers — it was the inability to stop the work |
| M | chronic refill windows evaluated on the UTC date | **Fixed** | The audit named 2 pharmacy sites; a new `NoUtcBusinessDateArchitectureTests` found **20 in 12 files**. 16 now go through `BusinessCalendar.DateIn`; the 4 that are genuine offset probes acknowledge themselves inline. The counter test runs at 00:30 Cairo — the only time the defect exists, which is why it survived |
| M | conflicting rounding (`AwayFromZero` in policy/reporting vs the mandated `ToEven`) | **Fixed** | The 3 money sites now round banker's; the rule is ratcheted by scale (2dp ⇒ `ToEven`), which holds across the four services that have *not* adopted the `Money` type. Every remaining `AwayFromZero` is a 1dp displayed percentage, plus one acknowledged month count |
| M | `Money.CapTo` dead while claims re-implements the clamp | **Fixed differently** | Not deleted. There are two clamps because there are two type worlds, and deleting the `Money` one removes what the migration lands on. `TheTwoClampsAgreeTests` pins them equal across the boundaries — including the clause a re-implementation drops, that no tariff caps at *billed*, not at infinity |
| H | settlement priced unpriced codes at the provider's own uncapped observed **average** | **Fixed** | Now the observed **floor**, and the line records `PriceSource` so the reviewer issuing the draft can see which prices have no tariff behind them. Test: two deliveries at 100 and one mispriced at 400 settled at 200/unit — double the real rate — and said nothing |
| M | RTL/Arabic keyboard direction inverted | **Fixed** | `SegmentedControl` reads the direction; Radix `Tabs` is passed `dir` (a prop, not a new `DirectionProvider` dependency). 6 tests that press the key and ask which control has focus — verified to FAIL without the fix, since a handler consistent with the wrong direction passes any test of its internals |
| M | hover-on-tint contrast 4.44:1 | **Fixed** | `--accent` is 5.2:1 on WHITE and 4.44:1 on `--accent-tint`; the ACTIVE nav row was fixed when the browser job first ran and its hover twin, the ghost button and the icon button were not. All three on `--accent-press` (6.72:1). Guarded by a STRUCTURAL sweep of the stylesheet, not a hand-listed pair, plus a hover pass added to the browser job |
| M | heading hierarchy skips h1→h3 | **Fixed** | The axe sweep cannot catch it: `heading-order` is MODERATE and that gate filters to serious/critical. A new sweep over all 112 routes found **12 skips on 8 screens**; `.panel-h` keeps the h3 appearance on an h2 so the promotion is invisible on screen and audible where it matters |
| L | no bidi isolation on identifier spans | **Fixed** | `unicode-bidi: isolate` on `.tnum` and `.mono` — two lines covering ~20 render sites. An `RX-2026-000410` inside an Arabic sentence was being reordered by the bidi algorithm, so the code read aloud was not the code in the database |
| M | `PolicyPanels` modal: real focus-trap/scrim bug | **Fixed** | It was `<div role="dialog" aria-modal="true">` with no trap, no scrim, no Escape and no focus restore — `aria-modal` ASSERTS the background is inert and Tab walked straight out into it. Now the DS `Modal`, which the same file already used twenty lines above |
| M | service inventory: 22 on disk, 21 / 14 / 17 in the docs | **Fixed** | Corrected, and gated. `check-service-inventory.py` compares CLAUDE.md's layout, HANDOFF's count and doc 16's catalog against `services/` in both directions. Fixing three numbers resets the clock; a document cannot fail on its own |
| M | doc 16 named Keycloak (retired Phase 17) and omitted 5 services | **Fixed** | Rows added for masterdata, finance, admin, case and interop; the issuer is identity-service in the table, the C4 diagram, the gateway paragraph and the provider-claims section |
| M | design set recorded as "0A–40"; phases 22/24 untracked; phantom 23 | **Fixed** | HANDOFF says 0A–46. Phases 22 and 24 exist as files and were missing from the master list — both added. **There is no phase 23 and never was**; recorded in place rather than left as a hole, which is what sent the audit looking for it |
| L | `i18next` declared but never imported by the SPA | **ADR, not a fix** | [ADR-0042](adr/0042-the-spa-does-not-use-i18next.md). The typed `Localized` scheme makes a missing translation a COMPILE error rather than a silent English fallback in front of an Arabic reader — the failure mode half the i18n findings across three audits have had. CLAUDE.md's stack line corrected to describe what is actually true |

Gates re-run clean after the changes: OpenAPI drift (22 specs), migration-compat, architecture suite (23),
`libs/auth` token byte-compat (62). The two contract changes (removed `stepUpSatisfied` and
`secondApproverSubject`) are reflected in `docs/api/` — the drift gate caught both, which is what it is for.

Frontend: **1,192 tests across 91 files green**, `tsc --noEmit` clean, eslint 0 errors. The new
`live-bundle` gate runs in frontend-ci and is in `REQUIRED_GATES`; because it is the first gate outside
backend-ci, `check-gate-freshness.py` now merges `gate-heartbeats*.json` from both pipelines rather than
reading one file, and gate-health downloads both artifacts.

One measurement worth stating plainly rather than rounding up: removing the fixture backend takes **~18 kB
off the minified bundle**, not the ~200 kB its 4,111 source lines suggest — that file is mostly comment. The
case for the change was never the weight.

**Two invariants added to the registry**, both scale/shape rules rather than type rules, which is what lets
them hold across services mid-migration: `INV-DATE-IS-CAIRO` and `INV-ONE-ROUNDING-MODE`.

**On the `Mersal.Money` type adoption** — carried out afterwards as its own piece of work; see the two rows
above and ADR-0043. The estimate stated here first ("hundreds of signatures plus the EF mapping layer") was
wrong, and wrong in the direction that would have justified not doing it: reading the four services for money
*arithmetic* rather than for fields that merely hold amounts found two that needed it, one already typed
through a shared library, and one correct without it.

### Two security gates that were red before any of this, found on opening the PR (2026-08-10)

Both predate the branch and both fail on `master`. Neither was in the audit, because the audit read the
code and these are only visible from the CI scoreboard.

**`sca-sast-image` had never executed — not once.** The action was pinned to `aquasecurity/trivy-action@0.24.0`,
and that tag does not exist: every tag this action publishes is `v`-prefixed. GitHub resolves actions during
*Set up job*, so the job died before its first step and the scoreboard showed a red X with no step attached,
which reads as infrastructure flake. Fixed to `@v0.33.1`.

Making it run turned it green-then-red on one real finding: **the SPA container ran nginx as root**. The
Dockerfile's own comment said "nginx:alpine already runs worker processes unprivileged", which is true and
beside the point — the master process, the one an nginx compromise gets you, was root. The image already
listened on 8080, so `nginxinc/nginx-unprivileged` was a base-image swap rather than a re-plumb. Verified by
building and running it: serves 200, master and workers both uid 101, CSP header rendered through envsubst,
SPA deep-route fallback intact.

**`secret-scan` fails on 17 findings, of which zero are live secrets.** Eight are in a fresh checkout of
HEAD, nine exist only in history. Triaged individually:

- *Nine historical* — dev MinIO access keys in tracked `appsettings.json`, `Dev_*_20YY` literals in a
  Keycloak provisioning script and two documents, and a baked `${POSTGRES_PASSWORD:-default}` in the DR
  rehearsal script. All were removed from the tree by the 18.B1 R2 purge; gitleaks scans all 482 commits, so
  they fail forever regardless. Pinned by fingerprint in `.gitleaksignore`, one line each with its reason.
- *Seven false positives* — five pharmaceutical names in the ATC/CPT reference exports that score like
  high-entropy keys (a hair-mask row whose `+`-joined ingredient list runs to forty characters; an
  anti-diabetic row ending in a `DPP-4` inhibitor class code), a portal nav key, and a synthetic seed UUID.
  Allowlisted by data-file path and by stopword, not by blanket rule disablement.
- *One live* — `amqp://hbmp:ci_hbmp_rmq_pw@localhost` in backend-ci. A throwaway credential for an ephemeral
  service container, already documented as such in the workflow, and flagged only because it happens to be
  URI-shaped while the identical `ci_hbmp_pw` beside it is not. The allowlist encodes the existing convention
  and is keyed to `ci_<name>_pw@localhost`, so the same value pointed at a real host still fails the build —
  verified against five planted secrets, all five caught.

**Still outstanding, and not fixable from this repository:** those historical `Dev_*_2026` values are still
the live passwords in `infra/compose/.env` today. History exposure plus a still-current value means the real
remediation is rotating them in the dev environment. Severity is low — private repository, local-only
services, no production reach — which is why this is recorded rather than escalated, but it is not closed by
anything in this branch.

Both gates are now in `REQUIRED_GATES` and write heartbeats, which is the durable half of the fix: a gate
that has never run was exactly the failure `check-gate-freshness.py` exists to catch, and these two sat
outside its coverage because it listed only the gates in `tools/ci/`.

**And wiring that up exposed why the watchdog never caught it: it has never received a single heartbeat.**
`.ci-state` is a hidden directory, and `actions/upload-artifact@v4` silently skips hidden files unless
`include-hidden-files: true` is set. Every heartbeat artifact since the mechanism was written has therefore
uploaded EMPTY — verified against the last five completed runs of both backend-ci and frontend-ci: zero
`gate-heartbeats*` artifacts, on every one. That is why `gate-health` fails on `master` reporting all
eighteen gates as "never executed", and why the same run locally says the same thing.

The failure mode is worth stating plainly, because it is the one the watchdog was built to prevent. A gate
that genuinely stopped running would have produced *exactly* the output the broken plumbing was producing —
so the alarm was indistinguishable from the fault, and reading it as noise was the reasonable response.
Fixed on all four upload steps.

`secret-scan` also required `GITHUB_TOKEN` on `pull_request` events (a v2 breaking change in the action). It
refuses before scanning and fails in a way that looks identical to a found secret, so on pull requests — the
branch where a secret is most likely to be caught before it lands — this gate has never examined the code at
all. It only ever ran on push-to-master.

### What closing the saga turned up (C1+C2, ADR-0041)

Wiring it surfaced three things that only a real caller could have found, because each path had never had
one. All are recorded in the ADR rather than quietly worked around:

1. **The `authorization_check` constraint did not hold for the sources it names.** Phase 7 required a
   `requesting_provider_id` on every non-manual authorization, and the two sources it was written for — a
   gated order and a gated prescription — never created a row, so the rule was never exercised against them.
   A doctor's token is practitioner-scoped and carries no provider, so a prescription has none to give.
   Migration `approvals/0010` restates it as **attributable**: a provider that raised it, or a person who
   did. A widening; the endpoint's own 422 is unchanged.
2. **`ProcedureSessions.ApplyApproval` cannot run.** Its summary says it is "applied when an approval
   decision is recorded"; it narrows `QuantityOrdered`, which orders 0013's signed-content trigger freezes
   against in-place update. It has only ever been called on detached objects in its own tests. The decision
   contract carries **codes and no quantities**, so a partial approval is applied at the code level and the
   refused lines are cancelled — which is what the contract can actually express.
3. **`amendment_reason` had no code for "the reviewer did not authorise this".** Every entry was written for
   a clinician amending their own work. `NotEligible` would have been a false sentence on a row read back in
   a dispute, so orders 0017 / pharmacy 0019 add `not-in-approved-scope`.

Also worth flagging, found while running the gates: **`check-migration-compat.py` is red on `main`** with 20
unacknowledged contract-phase operations, and is `continue-on-error: true` in `backend-ci.yml`, so it has
never blocked anything. This change added one drop-and-widen and acknowledged it inline (21 → 20), but the
standing 20 are a gate that reports and is not read.

**Not yet started:** Highs #11–#12 and Mediums #13–#14 in the plan below. Two idempotency items from §2.1
remain deliberately open and are called out rather than quietly folded in: the approvals validity-extension
ordering (it applies downstream BEFORE the decision commits) and the eligibility snapshot's unguarded
delete-then-insert. Both are concurrency reorderings rather than missing ledgers, and each needs its own
racer test to be worth claiming.

## 6. Proposed remediation plan

Ordered by risk-reduction per unit effort. Phases are independent enough to parallelize within a phase.

### Phase A — Make the core workflow complete and durable (Critical)
1. **Wire the approval saga (C1+C2).** Mirror `OrderPendingApproval`/`RxSubmitted` into an approvals routing queue + ingestion consumer; add a decisions-return consumer in orders/pharmacy that applies Approved/Rejected and compensates rejection. Add an integration test that drives an order from create → routed → approved → dispensable.
2. **Fix termination→eligibility (C3).** Emit per-coverage `CoverageChanged` from Terminate/Reinstate; add an authz/behaviour test that a terminated member reads Ineligible.
3. **Add DLX/DLQ to the shared consumer setup (C4)**; split transient vs poison; replace the approvals hot-loop with bounded redelivery. Add `check-event-symmetry.py` (still missing since phase 22) to catch publisher/consumer drift mechanically.
4. **Add `AddHbmpOutboxRelay()` to document-service (C5)** + an architecture test asserting durable-outbox ⇒ relay everywhere.
5. **Bind the durable outbox in audit-service (C7).**

### Phase B — Close the deployable-security gap (Critical/High)
6. **Gate the insecure Compose flags (C6):** CI fails if MFA-off/HTTPS-off appear outside a Development profile; document that Compose Tier 1 is dev-only.
7. **Gate operational-document downloads** on a `document:read`-class scope through the authorization engine; **add a `(no-provider)` sentinel** to provider RLS.
8. **Derive break-glass step-up from the authenticated principal**, not a request boolean; make provider termination a real two-token dual-control flow.
9. Begin the infra layer (Helm charts with correct probes, mTLS, OpenBao) — this is the single blocker behind ~20 pending NFR/DR/pentest items.

### Phase C — Idempotency, audit atomicity, money/time consistency (High/Medium)
10. Add idempotency ledgers to patient registration and claims reimbursement; add body-hash comparison to approvals/claims replay; reorder the approvals extension to after-commit-or-compensate.
11. Move every `EmitAsync` inside the business transaction (one shared helper + architecture test); give pharmacy PHI-read audits the real entity id; add the missing read audits.
12. Adopt `Mersal.Money` in pharmacy/finance/policy/reporting and unify rounding to `ToEven`; route chronic refill windows through `IBusinessCalendar.Today()`.
13. Cap or manual-route unpriced settlement codes.

### Phase D — Reliability testing & CI truth (High)
14. Add contract tests (start with policy⇄eligibility events and orders/pharmacy⇄approvals) and a minimal live-stack Playwright smoke (sign-in incl. refresh, book→409, check-in, consume, dispense).
15. Acknowledge/resolve the 21 red migration-compat constraints; make the PERF go-live gate assert measured values, not file existence; make the Playwright contrast job blocking and grow its route sample.

### Phase E — Frontend hardening (High/Medium)
16. Lazy-load the fixture client out of the production bundle; add zod to policyApi/branchApi and stop pre-validation defaulting; retire the CallCentre private fetch; add request cancellation to the transport.
17. Implement real email delivery + a recipient-address source in notification-service (the last MVP Must).

### Phase F — Design-system consolidation & a11y (Medium)
18. Fix the RTL keyboard direction (SegmentedControl + Radix `DirectionProvider`) and add an `ar` keyboard test; fix hover-on-tint contrast; promote skipped h3→h2.
19. Migrate the hand-rolled tables/modals/fields to DS components (start with the PolicyPanels modal a11y bug and the 18 `rx-field-input`s); extract an `AsyncCombobox`; add `--fs-micro/mini` tokens and move overlay shadows onto the elevation ladder; add `unicode-bidi: isolate` to `.tnum`/`.mono`.

### Phase G — Documentation truth (Low/Medium)
20. Reconcile the service inventory across CLAUDE.md / HANDOFF / doc 16 (22 services); update doc 16's identity provider and service catalog; record the design set as 0A–46; resolve or delete the phantom phase-23 dependency; ADR the i18next divergence.

---

## 7. Coverage & confidence

Fifteen workstreams read the Api/Domain/Infrastructure/Tests of all 22 services, all shared libs, the full SPA and design system, the infra/compose/kong/helm configuration, and the design + status docs. Findings are code-verified with `file:line` evidence; the seven Critical findings were re-verified directly. Areas sampled rather than exhaustively read (noted in the working streams): finance beyond the settlement path, masterdata internals, and notification internals — these carry lower confidence and may hold additional Medium/Low items. Detailed per-stream notes are retained in the audit working directory and can be expanded into per-service tickets on request.

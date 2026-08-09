# Mersal HBMP core benefit-chain audit

**Date:** 2026-08-09
**Scope:** `/services/{identity,patient,policy,eligibility,approvals,orders,emr}` (~72k LOC). Read-only audit of Api controllers/endpoints, Domain aggregates/state machines, Infrastructure persistence + outbox, event consumers, and test suites. Consume/decide flows read in depth. Every finding was verified by reading code — no speculation.

---

## Platform-wide (evidenced in multiple services)

### [Critical] all event consumers — nack(requeue:false) with no dead-letter exchange configured silently destroys domain events
Every consumer except approvals' fulfilment consumer handles *any* processing exception with `BasicNack(..., requeue: false)`:
`eligibility/Api/EventConsumer.cs:96`, `policy/Api/BenefitConsumptionConsumer.cs:128`, `policy/Api/RegistrationEnrolmentConsumer.cs:123`, `policy/Api/BeneficiaryEventConsumer.cs:142`, `identity/Api/ProgramEventConsumer.cs:131`, `emr/Api/CareEpisodeConsumer.cs:129`, `emr/Api/PractitionerLicenceExpiredConsumer.cs:133`.
Comments say "dead-lettering rather than guessing", but no queue anywhere declares `x-dead-letter-exchange` (grep across `services/`, `libs/`, `infra/` finds none), so a nacked message is **dropped by RabbitMQ**, not parked. A transient DB outage during `OrderLinesConsumed` processing permanently loses a benefit-accumulator movement (limits silently wrong); losing `BeneficiaryRegistered` leaves a person unknown at every desk.
**Fix:** declare DLX/DLQ arguments in the shared consumer setup, and distinguish transient (requeue with backoff) from poison (DLQ).

### [High] all services with consumers — broker-connect failure at startup is permanent; only eligibility surfaces it in readiness
`ExecuteAsync` connects once inside try/catch, logs a warning, and returns (`policy/Api/RegistrationEnrolmentConsumer.cs:69-75`, `identity/Api/ProgramEventConsumer.cs:66-71`, `eligibility/Api/EventConsumer.cs:53-59`; same shape in emr/approvals). If RabbitMQ is briefly down at boot the service serves traffic forever without consuming, and only eligibility registers a consumer health check (`eligibility/Api/Program.cs:41-42`); the other five pass `/health/ready` while their projections rot.
**Fix:** retry-with-backoff connect loop in a shared base class + readiness check in every consuming service.

### [High] approvals/orders/emr — audit event is emitted after the commit, so a crash leaves a mutation with no audit record
`libs/audit-client/IAuditClient.cs` and `libs/events/OutboxAuditSink.cs` document audit as "staged in the same transaction as the business change", and `EfOutbox.EnqueueRawAsync` runs its own `SaveChangesAsync` — so an emit after `tx.CommitAsync()` is a *separate* transaction. Approvals decides then audits after commit (`approvals/Api/Decisions.cs:262-271`, `Worklist.cs:232-239`); orders *create* audits after commit (`orders/Api/Orders.cs:233-240`) while orders *cancel* correctly audits inside the tx (`Orders.cs:293-312`); every emr clinical write does `SaveChangesAsync` then `EmitAsync` with no transaction (`emr/Api/ClinicalRecords.cs:218-219, 346-347, 493-494`). Patient and policy do it correctly inside the tx (`patient/Api/Program.cs:392-420`, `policy/Api/EnrollmentEndpoints.cs:61-66`). A process kill in the window = state change with no hash-chained audit event, violating CLAUDE.md's audit must-have.
**Fix:** move `EmitAsync` inside the open transaction everywhere (the outbox makes this free).

---

## patient

### [High] Idempotency-Key is demanded then ignored on registration; retries can create duplicate people
`POST /api/v1/beneficiaries` and `POST /api/v1/registrations` 400 without the header (`patient/Api/Program.cs:79-80, 533-534`) but no code ever stores or replays the key — there is no idempotency ledger in the patient schema. A client retry after a timeout re-runs the whole create; the only guard is the duplicate-identifier/card check, and a beneficiary registered without identifiers (or the same person with a re-keyed request) is double-created, each with its own open registration and `BeneficiaryRegistered` event.
**Fix:** a `processed_request` ledger keyed on the header (as approvals/orders do), replaying the original 201.

### [Medium] duplicate-identifier race surfaces as 500, not the mapped 409
The duplicate check is check-then-act in `BeneficiaryRegistrar` (application code); the real guard is the DB partial unique index `uq_identifier_active` (`patient/Infrastructure/Migrations/0001_patient_schema.sql:37`). Two concurrent registrations for the same national ID both pass the probe; the loser's `SaveChangesAsync` throws an unhandled `DbUpdateException` (23505) → 500 instead of the documented `urn:hbmp:duplicate-identifier` 409 (`patient/Api/Program.cs:91-95` handles only the pre-checked path).
**Fix:** catch 23505 and map to the same 409, like `emr/Infrastructure/AppointmentBooking.cs:88-91` does.

### [Medium] no concurrency token on Beneficiary; lifecycle transitions can interleave
`PatientDbContext.cs` maps no `xmin`/`IsRowVersion` (orders, approvals and emr all do). `POST /{id}/status` (`patient/Api/Program.cs:831+`) reads status, validates the transition, writes — two concurrent conflicting transitions (e.g. fraud Block vs desk Activate) both validate against the stale status, both commit, both emit events; last write wins and downstream projections receive contradictory `BeneficiaryStatusChanged` events.
**Fix:** add the xmin token + map `DbUpdateConcurrencyException` → 409 as approvals does.

---

## policy

### [Low] rules-set replaces benefit rules by hard delete, relying solely on DB triggers for immutability
`policy/Api/PlanEndpoints.cs:423-424` does `RemoveRange` of a version's `BenefitRules`/`Tiers` and re-inserts. The activated-version immutability trigger (`trg_benefit_rule_immutable`, visible in `policy/Tests/PlanVersionStoreTests.cs:61-63`) is the only thing preventing destruction of in-force benefit terms, and draft-rule edit history is lost entirely (no `*_history` write on this path). Acceptable for drafts, but the endpoint never checks `version.Status` before deleting.
**Fix:** add an explicit Draft-only guard so the 422 comes from the API, not a trigger message.

### [Low] cross-seam control flow by exception, against the Result&lt;T&gt; convention
`BeneficiaryProbeRefusedException` and `BulkFileInfectedException` are thrown in Infrastructure (`policy/Infrastructure/BeneficiaryIntakeSeam.cs:93`, `BulkStorage.cs:51`) and caught at endpoints/engine (`policy/Api/EnrollmentEndpoints.cs:274`, `BulkJobEngine.cs:90`) as expected-failure signaling. Every other service models expected failures as outcome unions (`ConsumeResult`, `RegistrationResult`, `TransitionResult`). Works, but it is the one service using exceptions for expected outcomes.

---

## eligibility

### [High] snapshot persist is delete-then-insert with no concurrency handling; parallel checks 500
`eligibility/Infrastructure/EligibilityChecker.cs:69-74`: load all snapshots for (beneficiary, category), `RemoveRange`, add new, save. Two concurrent cache-miss checks for the same member (a busy reception desk after an invalidation) both load the same rows; the loser's DELETE affects 0 rows → EF throws `DbUpdateConcurrencyException` → unhandled → 500 on the platform's front-of-house endpoint. It also hard-deletes prior snapshots, erasing the point-in-time evidence of what a desk was shown.
**Fix:** append snapshots (query latest by `ComputedAt`) or upsert with `ON CONFLICT`; never delete derived-evidence rows.

### [Medium] GET /members/{id}/status is the only unaudited PHI read in the service
`eligibility/Api/Program.cs:187-194` returns member status + memberNo with no `EmitAsync`, while `/check` (line 174) and `/reception/search` (line 219) both audit every read. It sits under the broad `eligibility:check` scope, so any checker can poll member status invisibly.
**Fix:** add the same `AuditAction.Read` emit.

### [Low] Enum.Parse on stored strings fails open to 500
`EligibilityChecker.cs:95` (`Enum.Parse<LimitType>`) and `:108` (`Enum.Parse<EligibilityDecision>`) throw on any unexpected stored/cached value — one bad projection row makes every check for that member 500. Member status by contrast is `TryParse` with `Inactive` fallback (line 87).
**Fix:** use TryParse + fail-closed reason.

---

## approvals

### [High] idempotent replay ignores the request body: a reused key returns the wrong decision as success
`approvals/Api/Decisions.cs:122-130` replays on key match alone — no request-hash comparison and no check that the prior operation matches. A client that retries a *reject* with a key previously used for an *approve* receives 200 OK with the approval's `DecisionView`. Orders explicitly rejects this (`ConsumeOutcome.IdempotencyKeyReuse`, `orders/Infrastructure/ConsumeExecutor.cs:76-81`); approvals stores `Operation = $"decision:{decision}"` (`Decisions.cs:194`) but never compares it.
**Fix:** compare operation + a body hash on replay; 422 on mismatch.

### [High] validity extension is applied downstream before the decision commits; a losing racer leaves an extended expiry with no approving decision
`Decisions.cs:157-167` calls `Extensions.ApplyAsync` (moves the prescription/order expiry in pharmacy/orders) *before* the local transaction. If the subsequent `SaveChangesAsync` then loses the xmin race to another reviewer (`:188-189` → 409) or the tx fails, the item's expiry has already been extended with no recorded decision — the exact "screen disagreement" the long comment says the ordering prevents, in the opposite direction (worse: the other reviewer may have *rejected*).
**Fix:** re-verify the authorization state and take the row lock (or apply the xmin-guarded status update) before calling out, or compensate on conflict.

### [Medium] concurrent same-key decisions 500 on the ledger PK instead of replaying
`processed_request.idempotency_key` is the PK (`approvals/Infrastructure/Migrations/0001_approvals.sql:84`); the read-then-insert in `Decisions.cs:122,192-196` has no 23505 handler, so of two simultaneous same-key submissions one returns 200 and the other 500 (after the extension call may already have run). Orders catches the unique violation and replays (`ConsumeExecutor.cs:125-134`).
**Fix:** same 23505-catch-and-replay pattern.

### [Medium] the decision concurrency test bypasses the production decide path
`approvals/Tests/DecisionConcurrencyTests.cs:47-73` re-implements the parent-first save + child insert inline against the DbContext, so the barrier race proves the *xmin mechanism*, not `Decisions.Decide` — regressions in the real path (extension-before-record, replay handling, insert ordering) are invisible to it. Orders' equivalent races the real `ConsumeExecutor`.
**Fix:** drive the race through `Decide` (or the endpoint via the test factory).

### [Medium] fulfilment consumer requeues unconditionally: a deterministic failure hot-loops forever
`approvals/Api/FulfilmentConsumer.cs:134` is the one consumer that uses `requeue: true`, with no delay, attempt count, or poison detection — a message that fails deterministically *after* deserialization (the `:93` malformed-payload nack only covers parse failures) redelivers immediately at prefetch 20, pinning the consumer and starving the queue.
**Fix:** bounded redelivery (death-header count) then DLQ.

---

## orders

### [Medium] create replay ignores the body, and the concurrent-create race 500s
`orders/Api/Orders.cs:36-38` returns the existing order for a matching key without comparing the request (same key + different lines silently returns the old order — the consume path treats exactly this as `IdempotencyKeyReuse`). And two concurrent creates with one key both pass the probe; the loser hits `ux_order_idempotency` (`Migrations/0001_orders.sql:34`) → unhandled 23505 → 500. The EF model even maps that index as non-unique (`OrdersDbContext.cs:91`), drifting from the DB.
**Fix:** request-hash on replay + catch-23505-and-replay; mark the index unique in the model.

### [Medium] order reads are the service's only unaudited PHI reads
`GET /investigation-orders/{id}` and `/mine` (`Orders.cs:247-274`) return the full clinical order (codes, sensitivity, lines) with no read audit; result reads (`Results.cs:138-174`), queue and service-history all emit `AuditAction.Read`. The gate audits only denials (`OrdersGate.cs` — engine-side).
**Fix:** emit a read audit on the success path, as `patient/Api/BeneficiaryReadGuard.cs` does.

### Positive note
`orders/Infrastructure/ConsumeExecutor.cs` is the strongest code in the audit — three-layer guard (unique key, xmin, DB CHECK), body-hash reuse rejection, guarded aggregate-status CAS with bounded retry, outbox injected inside the transaction — and `orders/Tests/OrderConsumeConcurrencyTests.cs` proves it with real parallel Postgres racers and exact row-count assertions.

---

## emr

### [Medium] queue requeue/remove/complete mutate state with no audit and no actor
`emr/Api/Queue.cs:117-127` route straight to `MutateTicket` (`:145-155`), which saves the state change with no `EmitAsync` and never stamps who did it — while check-in (`:37,55`) and call-next (`:107`) in the same file audit. "Removed from queue" is precisely the action a dispute needs attributed.
**Fix:** audit + actor in `MutateTicket`.

### [Medium] clinical writes (notes, diagnoses, vitals, allergies) have the largest audit crash-window
All single-save mutations in `emr/Api/ClinicalRecords.cs` (`:218-219, 249-250, 284-285, 346-347, 493-494, 672-673, 729-730`) commit the PHI write, then emit audit as a second commit. This is the systemic finding above, but EMR is the highest-volume PHI-write surface, so it deserves its own fix pass (wrap save+emit in one transaction; `/complete` at `:384` already shows the correct local pattern).

### [Low] audit Before/AfterState JSON built by string interpolation
E.g. `ClinicalRecords.cs:219,347` (`$"{{\"icd\":\"{dx.IcdCode}\"}}"`) — a quote or backslash in a code/type value corrupts the audit payload JSON. Serialize an anonymous object instead (patient does `Describe(...)`; orders interpolates too at `Orders.cs:239`).

---

## identity

### [Low] role-scope replacement is a hard delete of authorization config with no history row
`identity/Api/Auth/AdminEndpoints.cs:222` `RoleScopes.RemoveRange(existing)` then re-adds. Not clinical data, so the soft-delete rule doesn't strictly apply, but "which scopes did this role hold last month" is answerable only from audit prose, not data.
`SessionService.IsLiveAsync`'s fail-open bare `catch` (`Infrastructure/SessionService.cs:139-143`) is deliberate, counter-instrumented, and documented — noted, not flagged. Otherwise identity is clean: parameterized raw SQL (`TenantFeatureStore.cs`), atomic `ON CONFLICT` dedupe, endpoint-security tests that assert non-anonymity (`Tests/IssuerEndpointSecurityTests.cs:84`).

---

## Test quality (cross-service)

### [Medium] eligibility — min-necessary "proof" checks DTO property-name substrings, not responses
`eligibility/Tests/ReceptionMinNecessaryTests.cs` reflects over `ReceptionResultCard` property names against a blocklist ("diagnos", "icd", …). A leaking field named `Assessment`, `Findings` or `ReportText` would pass. It proves compile-time shape, never that the endpoint's serialized payload is clean.
**Fix:** complement with an endpoint test asserting the exact allowed JSON keys. (The authz *engine* tests — `orders/Tests/OrderAuthzTests.cs`, `emr/Tests/ClinicalAuthzTests.cs` — are real, with deny+audit assertions.)

### [Low] all — ~100 DB-gated tests skip silently without `*_TEST_DB`
The concurrency/RLS proofs are `Skip.If(Db is null)` (e.g. `OrderConsumeConcurrencyTests.cs:23`), so plain `dotnet test` is green while proving nothing. Mitigated: CLAUDE.md documents `./dotnet.sh test --with-db` and CI exports the vars — flagged only because a green local suite without the flag is misleading by design.

---

## Cross-service consistency

- **Audit-emit placement:** patient & policy emit inside the business transaction; approvals, emr, and orders-create emit after commit; orders-cancel emits inside — inconsistent even within `orders/Api/Orders.cs`. One rule, one helper, one architecture test would end it.
- **Idempotency maturity gradient:** orders = key + body-hash + unique index + 23505 replay (gold standard); emr appointments = key + partial unique index + 23505 mapped; approvals = key ledger, no body-hash, unhandled PK race; policy enrolment = key ledger via `MembershipCommands`; patient = header required then discarded.
- **Consumer failure policy is bimodal:** approvals requeues forever (hot-loop risk); the other five drop forever (data-loss risk, no DLX). Neither is the documented "dead-letter" behavior; there is no shared consumer base class though all seven copies are near-identical (~80 duplicated lines each — real duplication worth extracting).
- **Optimistic concurrency:** xmin tokens in orders, approvals, emr; absent in patient (lifecycle race) and eligibility (snapshot race). Policy uses a `SaveOrConflict` helper.
- **Result&lt;T&gt; convention:** literal `Result<T>` appears in only 9 non-test files; in practice services use per-flow outcome unions (`ConsumeResult`, `RegistrationResult`, `TransitionResult`) — faithful in spirit — except policy, which throws typed exceptions across seams, and identity, which throws `RevocationNotPersistedException` (documented fail-closed).
- **RFC 7807:** broadly consistent (`urn:hbmp:*` types); stragglers return `Results.Problem` with no `type` (`patient/Api/Program.cs:833`, `eligibility/Api/Program.cs:77,213`).
- **PHI-read auditing:** patient (read guard), eligibility check/search, orders results, emr clinical-context all audit reads; orders order-GET/mine and eligibility member-status do not — the rule is applied per-endpoint, not structurally.
- **Hygiene:** zero TODO/FIXME/HACK markers, no commented-out code found, no unparameterized SQL, nullable enabled throughout — overall discipline is unusually high; the findings are concentrated at seams (consumers, idempotency ledgers, audit atomicity), not in the core state machines.

---

## Severity summary

| Severity | Count | Headlines |
|----------|-------|-----------|
| Critical | 1 | Consumers drop events on failure — no DLX exists |
| High | 6 | Consumer connect never retried; audit-after-commit crash window; patient idempotency ignored; eligibility snapshot race → 500; approvals replay returns wrong decision; extension applied before decision commits |
| Medium | 12 | Idempotency race 500s (patient/approvals/orders); missing xmin (patient); unaudited PHI reads (orders, eligibility); unaudited queue mutations (emr); shallow min-necessary test; concurrency test bypasses production path; consumer hot-loop (approvals) |
| Low | 6 | Hard-deleted config/draft rules; enum-parse 500s; interpolated audit JSON; exception-based seams; silent test skips |

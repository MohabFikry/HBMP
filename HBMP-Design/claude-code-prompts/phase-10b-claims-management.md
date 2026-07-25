# Phase 10b — Claims Management (Capture, Batching, Adjudication, Settlement Advice)

**Goal:** Build the `claims-service` that turns already-delivered, authorized services into reviewed, decided and settled financial records — three origination channels (auto-derived, provider-submitted, beneficiary reimbursement with OCR), batching, rules-based pre-adjudication, **line-level Claims Officer decisions rolled up to batch**, reconciliation + append-only adjustments, and an immutable settlement advice — **with no payment execution and no clinical data in any claims projection**. Release **R6 / post-v1**.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> Root `CLAUDE.md` already defines stack, conventions, security, audit, testing, and Definition of Done. This file adds phase-10b scope only.

---

## Skills to activate
> Activate `medical-claims-engine`, `health-insurance-tpa-operations`, `healthcare-business-rules-engine`, `provider-network-management` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [`../36-claims-management.md`](../36-claims-management.md) — **AUTHORITATIVE claims design. Read this first and in full.** §2 core principles, §3 origination channels, §4 batching, §5 the 9-step adjudication order, §6 review/decision + batch lifecycle, §7 reconciliation & adjustment types, §8 settlement advice & exports, §9 roles/minimum-necessary, §10 events, §12 acceptance criteria.
- [`../22-data-dictionary.md`](../22-data-dictionary.md) — the **claims schema + enums** (`claim`, `claim_line`, `claim_batch`, `claim_decision`, `claim_adjustment`, `reimbursement_request`, `ocr_extraction`, `settlement_advice`) plus the existing anchors this phase depends on: §3.3 coverage limits, §5.3 `contract_service_line` tariffs, §7.3 `order_fulfillment`, §8.3 `dispense_event`, §9 `authorization`. Column names, types, and enum values are authoritative — use them EXACTLY.
- [`../23-state-machines.md`](../23-state-machines.md) — **claim**, **claim-line**, **batch**, and **reimbursement** lifecycles with their transition tables (guards, events, actors). Mirrors §5 (authorization) in shape and rigor. Use the canonical state names only.
- [`../11-permission-matrix.md`](../11-permission-matrix.md) — §3.2/§4 hard rule **`Finance/Claims → diagnosis = denied`**; §4 field-level sensitivity (`diagnosis`, `emr_note`, `lab_result`, `imaging_result`, `financials`); §6.2 Rego example (finance reads claim, diagnosis stripped); §6.7 **SoD** rules.
- [`../10-role-matrix.md`](../10-role-matrix.md) — **Claims Officer** / **Claims Reviewer (Senior)** roles, their scopes, and the SoD separations (originator ≠ adjudicator; adjudication ≠ settlement release).
- [`../07-functional-requirements.md`](../07-functional-requirements.md) — **FR-CLM-\*** claims requirements, plus FR-INV-006/008 (never re-decrement coverage; reversal only via compensating action) and FR-RPT-002/004 (finance ≠ diagnosis; exports audited).
- [`../16-service-architecture.md`](../16-service-architecture.md) — `claims-service` bounded context, §7 event/CloudEvents conventions, §8 sagas.
- [`../19-audit-strategy.md`](../19-audit-strategy.md) — immutable hash-chained `audit_event` on every decision, adjustment, export, and PHI/financial read.
- [`../32-user-stories.md`](../32-user-stories.md) — **US-CLM-\*** acceptance criteria.
- Reference: [`../18-security-model.md`](../18-security-model.md) (provider isolation `PO`), [`../0C-OPEN-SOURCE-STACK.md`](../0C-OPEN-SOURCE-STACK.md) (self-hosted OCR, MinIO object-lock/WORM), [`phase-13-interoperability-and-roadmap.md`](phase-13-interoperability-and-roadmap.md) (`IDocumentOcrProvider`).

**Depends on:** phase **7** (authorization linkage — a gated claim line needs a valid authorization), phases **5** and **6** (`order_fulfillment` / `dispense_event` are the payable anchors), phase **2b** (provider contracts + `contract_service_line` tariffs, provider isolation), phase **1** (`document-service`), phase **0** (identity, ABAC, audit spine, outbox). Pairs with phase **10** (finance consumes settlement advice and executes payment **outside** the platform).

If any doc named above lacks a section this prompt references, **flag it — do not invent schema or states.** [`../36-claims-management.md`](../36-claims-management.md) wins.

---

## THE INVARIANTS (read before writing any sub-prompt)

1. **Claims are downstream of fulfillment.** A payable line exists only where an `order_fulfillment` (consume) or `dispense_event` row exists. Beneficiary reimbursement is the single exception and must still be *matched* to such a record or to an **authorized** order/prescription.
2. **NO DOUBLE-BILLING.** At most one non-void payable claim line per fulfillment reference — enforced by a **UNIQUE partial index** `(fulfillment_ref) WHERE status <> 'Void'`. The database is the final guarantee, not application logic. Duplicates are denied `DUPLICATE_CLAIM`.
3. **Never re-decrement coverage.** Consume/dispense already moved `consumed_value` transactionally. Claims **read and reconcile** against that accumulator; the claims path never writes it and never keeps a parallel one.
4. **Adjudicate on codes and amounts — never on diagnosis.** Every claims projection is a server-side allow-list DTO. `diagnosis`, `emr_note`, `lab_result`/`imaging_result` **values**, and clinical prescription detail are never returned. Result *existence* + date + document reference only. Medical-necessity questions route to a **clinical reviewer**; the Claims Officer never sees clinical content.
5. **Append-only, never mutate.** Submitted claims, `claim_decision`, `claim_adjustment`, `ocr_extraction`, and `settlement_advice` are append-only (no UPDATE/DELETE grants; DB rule/trigger). Corrections are **adjustments** or a compensating **Void + re-claim**.
6. **Never a guessed price.** Pricing comes from `contract_service_line.agreed_price` for the code + service date. No tariff ⇒ `NO_TARIFF` ⇒ manual pricing.
7. **SoD + provider isolation.** The decider is never the originator/submitter and never affiliated with the claiming provider. Providers see only their own claims/batches (ABAC `PO` + RLS). Overrides above a configurable value threshold need a second approver.
8. **OCR is assistive, never authoritative.** Every extracted value carries a confidence score and its source document region; a human confirms before it affects money.
9. **The platform never moves money.** Settlement advice is the hand-off artifact. No payment execution, no bank rails — Finance/treasury pays externally and may record a reference back.

---

## Prompts

### 10b.1 — `claims-service` foundation + auto-derived claims (no double-billing)

```text
Build the claims-service (.NET 8, bounded context `claims`, schema `claims`) and the auto-derived origination channel. READ FIRST: ../36-claims-management.md §2, §3.1, §5 (pricing step), ../22-data-dictionary.md (claims schema + §5.3 tariffs + §7.3/§8.3 fulfillment), ../23-state-machines.md (claim + claim-line lifecycles), ../16-service-architecture.md, ../19-audit-strategy.md, US-CLM-* .

SERVICE & SCHEMA
- New independently-deployable service from the phase-0 service template: Api/ Domain/ Infrastructure/ Tests/ + README. Owns schema `claims` exclusively (schema-per-service, RLS on); it NEVER reads another service's tables — cross-context data comes over the API/events.
- Tables per ../22 (use the documented column names/types/enums EXACTLY): `claim` (id uuid v7, business key `CLM-<yyyy>-<base32(8)>`, beneficiary_id, provider_id, provider_location_id, origin (AutoDerived|ProviderSubmitted|BeneficiaryReimbursement), status (canonical claim states), service_date_from/to, currency, totals, submitted_at, correlation_id, created/updated audit columns) and `claim_line` (claim_id, line_no, code_system + code, description, quantity, billed_amount, contract_price, allowed_amount, status, fulfillment_ref, authorization_id, system_recommendation, reason_codes[], rule_version).
- Timestamps timestamptz UTC; money as numeric with an explicit currency column — never float.

THE UNIQUE INDEX (the whole point of this prompt)
- CREATE UNIQUE INDEX ux_claim_line_fulfillment ON claims.claim_line (fulfillment_ref) WHERE status <> 'Void';
  This makes double-billing IMPOSSIBLE, not merely unlikely. A second claim line for the same order_fulfillment/dispense_event reference must fail at the database and surface as a DUPLICATE_CLAIM denial (RFC7807 409), never as a silent second payable line.

AUTO-DERIVED INTAKE
- Consume `OrderLinesConsumed` (phase 5) and `RxLinesDispensed` (phase 6) from the event bus. Consumers are IDEMPOTENT: dedupe on event id in a processed-events table; replay of the same event creates nothing new and returns the existing claim line.
- For each fulfillment/dispense row create (or attach to) a claim for that (provider, beneficiary, period) and insert one claim_line anchored to the fulfillment reference, with quantity and service code taken from the fulfillment record.
- PRICE from the performing provider's contract tariff by calling provider-service for contract_service_line.agreed_price matching code_system + code, in effect on the SERVICE DATE. No tariff ⇒ set reason code NO_TARIFF, leave contract_price null, mark the line RequiresManualReview. NEVER default, estimate, or copy a price from another provider/date.
- Claim starts in the canonical initial state per ../23; emit `ClaimCreated` (+ `ClaimSubmitted` where applicable) via the TRANSACTIONAL OUTBOX in the same transaction as the insert. CloudEvents envelope, `.vN` versioned.
- Write an immutable hash-chained audit_event for every claim/line creation via libs/audit-client.

DELIVER
- EF Core migration SQL including the unique partial index, RLS policies (provider isolation via ABAC PO), and append-only grants; OpenAPI 3.1 for GET /api/v1/claims and GET /api/v1/claims/{id} (min-necessary projection, no clinical fields); service README + ADR.

ACCEPTANCE (Given/When/Then)
- Given an `OrderLinesConsumed` event for a fulfilled line, When it is consumed, Then exactly one claim_line exists anchored to that fulfillment_ref, priced from the provider's contract tariff for the service date, and `ClaimCreated` is published via the outbox.
- Given the SAME event is redelivered, When it is consumed again, Then no new claim or claim_line is created and no new event is published.
- Given two concurrent attempts to create a payable line for the same fulfillment_ref, When they commit, Then exactly one succeeds and the other fails with DUPLICATE_CLAIM (409 problem+json).
- Given no contract tariff exists for the code on the service date, When the line is created, Then reason code NO_TARIFF is recorded, contract_price is null, and the line is RequiresManualReview — no price is guessed.

REQUIRED TESTS
- CONCURRENCY test: N parallel real DB transactions inserting a claim_line for the SAME fulfillment_ref → exactly one non-void row survives; assert on the DB, not mocks.
- IDEMPOTENCY test: the same OrderLinesConsumed/RxLinesDispensed event delivered twice (and out of order) → one claim_line, one outbox event.
- Unit: tariff resolution by code_system+code+service date; NO_TARIFF path never produces a price.
- Integration: migration applies; unique partial index rejects the duplicate; audit events written and hash-chain verifies.
```

### 10b.2 — Batching (date range / branch / group / manual) + batch lifecycle

```text
Add BATCHING to claims-service. READ FIRST: ../36-claims-management.md §4 and §6 (batch lifecycle), ../23-state-machines.md (batch lifecycle + transition table), ../19-audit-strategy.md.

DATA
- Table `claim_batch` per ../22: id uuid v7, business key `BAT-<yyyy>-<base32(8)>`, name, payee (provider_id | provider_location_id | reimbursement cohort), period_from/to, creation_mode (DateRange|ProviderBranch|ProviderGroup|Manual), status (Open|UnderReview|Decided|SettlementIssued|Closed|Cancelled), rollup totals (claimed, priced, approved, adjusted, denied, net_payable), decided_at, frozen_at, created/updated audit columns.
- Join table `claim_batch_item` (batch_id, claim_id, added_by, added_at, removed_by, removed_at, removal_reason) — membership changes are recorded, never deleted.

SINGLE-OPEN-BATCH (enforce in the database)
- CREATE UNIQUE INDEX ux_claim_one_open_batch ON claims.claim_batch_item (claim_id) WHERE removed_at IS NULL AND batch_status IN ('Open','UnderReview');
  (materialize batch_status on the item row, or use an equivalent constraint/trigger — the guarantee must be a DB constraint.) A claim can therefore never sit in two live batches and can never be settled twice. Violation ⇒ 409 problem+json CLAIM_ALREADY_BATCHED.

ENDPOINTS (/api/v1, scope claims:batch)
- POST /claim-batches — body: creation_mode + selector. DateRange: {serviceDateFrom, serviceDateTo, optional receivedDateFrom/To}. ProviderBranch: {providerLocationId}. ProviderGroup: {providerGroupId} (all branches of the chain). Manual: {claimIds[]} picked from a filtered worklist. Batches are provider-homogeneous for settlement (one payee); reimbursement batches group by period.
- POST /claim-batches/{id}/claims and DELETE /claim-batches/{id}/claims/{claimId} — add/remove. Removal from an Open batch is audited; removal from an UnderReview batch REQUIRES a reason and is audited as an exception. Nothing can be added to or removed from a Decided/SettlementIssued/Closed batch (409).
- POST /claim-batches/{id}/submit-for-review (Open → UnderReview), POST /claim-batches/{id}/decide (UnderReview → Decided), POST /claim-batches/{id}/close, POST /claim-batches/{id}/cancel.
- GET /claim-batches, GET /claim-batches/{id} — rollups + member claims, min-necessary projection (codes, amounts, statuses; ZERO clinical fields).

GUARDS
- Model the ../23 batch transition table explicitly; illegal transitions → 409 problem+json, no state change.
- **Decided requires EVERY line decided.** The Open→...→Decided guard fails (422 with the count/list of undecided lines) if any claim_line in the batch lacks a recorded decision. Recompute rollups on every membership/decision change; FREEZE rollups at SettlementIssued (no further recompute).
- Emit `BatchCreated`, `BatchUnderReview`, `BatchDecided` via the outbox; audit every membership change and transition.

ACCEPTANCE
- Given claims in a service-date window, When I create a DateRange batch, Then exactly the matching claims are attached and rollup totals are computed.
- Given a claim already in an Open or UnderReview batch, When I add it to a second batch, Then it is rejected 409 CLAIM_ALREADY_BATCHED at the database constraint.
- Given a batch with any undecided line, When I attempt Decided, Then it is rejected 422 listing the undecided lines and the batch stays UnderReview.
- Given a claim is removed from an UnderReview batch, When it commits, Then a reason is required and an exception-flagged audit event is written.

REQUIRED TESTS
- Integration: full batch lifecycle Open→UnderReview→Decided→SettlementIssued→Closed with rollups recomputed then frozen; illegal transitions 409.
- Integration/concurrency: parallel attempts to batch the same claim → exactly one succeeds (DB constraint proof).
- Integration: decided-requires-all-lines guard.
- Unit: each creation mode's selector; rollup arithmetic including adjustments.
- Audit: every add/remove/transition produces a hash-chained event.
```

### 10b.3 — Automated pre-adjudication (fixed 9-step order, ALL reasons)

```text
Implement AUTOMATED PRE-ADJUDICATION in claims-service. READ FIRST: ../36-claims-management.md §5 (the fixed evaluation order — this prompt implements it literally), ../22-data-dictionary.md §3.3 limits / §5.3 tariffs / §9 authorization, ../11-permission-matrix.md §3.2, and the `healthcare-business-rules-engine` skill.

EVALUATION ORDER — run per claim_line, in this exact order, and COLLECT ALL APPLICABLE REASON CODES. Do NOT stop at the first failure: partial approvals must be precise, so a line that fails checks 3 and 8 reports both.
 1. Beneficiary status + policy validity on the SERVICE DATE → NOT_ELIGIBLE, POLICY_EXPIRED
 2. Coverage category matches the service's benefit_category (LAB/IMAGING/PHARMACY/CONSULT/REFERRAL) → NOT_COVERED_CATEGORY
 3. Pre-auth linkage — a gated service needs a valid, non-expired authorization in Approved|PartiallyApproved|EmergencyApproved|Overridden; a PartiallyApproved scope CAPS the payable line → NO_PRIOR_AUTH, AUTH_EXPIRED, EXCEEDS_AUTH_SCOPE
 4. Fulfillment linkage — a matching order_fulfillment/dispense_event exists → NO_FULFILLMENT_RECORD
 5. Duplicate check — no existing non-void payable line for that fulfillment reference → DUPLICATE_CLAIM
 6. Provider network status — active provider + contract in effect on the service date → PROVIDER_OUT_OF_NETWORK, CONTRACT_NOT_EFFECTIVE
 7. Tariff pricing — contract_service_line.agreed_price for code + date → NO_TARIFF ⇒ route to MANUAL PRICING. NEVER a guessed, defaulted, averaged, or carried-over price.
 8. Coverage limit availability by limit_type (Annual|PerEncounter|Lifetime|Count with reset_period) → LIMIT_EXCEEDED. **READ ONLY** — reading limit_value − consumed_value. The claims path MUST NOT write consumed_value: consume/dispense already owns that decrement (FR-INV-006).
 9. Co-pay / deductible split if configured → member vs payer share.

OUTPUT per line (persisted, append-only): system_recommendation ∈ {RecommendApprove, RecommendPartial, RecommendDeny, RequiresManualReview}, the full reason_codes[] set, computed allowed_amount, and the RULE_VERSION used. Rules are declarative and versioned; the rule version is recorded on the line and carried onto every decision for auditability.

THE SYSTEM RECOMMENDS; THE OFFICER DECIDES (10b.4). A recommendation is never auto-final for gated, high-value, reimbursement, or RequiresManualReview lines. Auto-approval of clean low-value lines is a per-policy config flag, DEFAULT OFF.

ENDPOINTS
- POST /api/v1/claims/{id}/adjudicate (scope claims:adjudicate) and an automatic run on claim submission. Idempotent: re-running produces a new append-only evaluation row, never mutates a prior one. Emit `ClaimAdjudicated` via the outbox.

ACCEPTANCE
- Given a line failing multiple checks, When pre-adjudication runs, Then ALL applicable reason codes are recorded (not just the first) and the recommendation reflects the combination.
- Given a gated service with no linked authorization, Then NO_PRIOR_AUTH is recorded and the recommendation is never RecommendApprove.
- Given a PartiallyApproved authorization narrower than the billed line, Then EXCEEDS_AUTH_SCOPE is recorded and allowed_amount is capped at the approved scope.
- Given no contract tariff, Then NO_TARIFF + RequiresManualReview and allowed_amount stays null — no price is invented.
- Given adjudication runs, When it completes, Then consumed_value on any coverage limit is UNCHANGED.

REQUIRED TESTS
- Unit: a full RULE MATRIX table-driven test — one case per check and the key combinations, asserting the exact reason-code set, recommendation, and allowed_amount for each.
- Unit: reason-code catalogue completeness (every code emitted is in the catalogue; every catalogue code is reachable).
- Integration: adjudication does NOT modify coverage accumulators (assert consumed_value before/after).
- Integration: rule_version is persisted and a version bump changes the recorded version, not history.
```

### 10b.4 — Claims Officer review + line-level decisions (SoD, dual control, min-necessary)

```text
Build the CLAIMS OFFICER worklist and LINE-LEVEL DECISION endpoints. READ FIRST: ../36-claims-management.md §6 and §9, ../11-permission-matrix.md §3.2/§4/§6.7, ../10-role-matrix.md (Claims Officer / Claims Reviewer + SoD), ../19-audit-strategy.md, ../23-state-machines.md (claim + claim-line lifecycles), US-CLM-*.

WORKLIST (GET /api/v1/claims/worklist, scope claims:review)
- Filter by batch, provider, status, recommendation, reason code, value band, age; sort by age/value; cursor pagination.
- MIN-NECESSARY PROJECTION (this is code, not a comment): per line return service code + description, service date, provider/branch, billed amount, contract price, system recommendation + reason codes, linked authorization id + validity, fulfillment reference, and supporting DOCUMENT REFERENCES.
- NEVER return diagnosis, emr_note, clinical notes, prescription clinical detail, or lab/imaging RESULT VALUES. Result EXISTENCE only — a boolean that a result exists, its date, and its document reference — so the officer can verify "the service was rendered" without reading it. Enforce as a server-side allow-list DTO plus OPA/Cerbos policy plus RLS; the field must be absent from the payload, not merely null.
- Every worklist/detail read of financial+PHI-adjacent data writes an audit event.

DECISION ENDPOINTS (/api/v1/claims/{claimId}/lines/{lineId}/decisions, scope claims:decide, Idempotency-Key REQUIRED)
- Decisions: Approve · PartiallyApprove (allowed_amount required, must be > 0 and ≤ billed/contract cap) · Deny · Adjust (see 10b.7) · RequestInfo · RouteToClinical.
- MANDATORY reason code + free-text rationale on Deny, Adjust, and any override → 422 if missing/blank. Approve records rationale if supplied.
- Each decision inserts an APPEND-ONLY `claim_decision` row: decider_id, decided_at (UTC), decision, allowed_amount, reason_codes[], rationale, rule_version, correlation_id. Enforce append-only with a DB rule/trigger and no UPDATE/DELETE grants — corrections are NEW rows or adjustments, never edits.
- RouteToClinical hands the line to a Clinical Reviewer (Medical Approval/Director) who sees clinical context and records an OPINION only — never a payment decision. The Claims Officer never sees what the clinical reviewer saw.
- Roll up: recompute the claim status (Approved | PartiallyApproved | Denied per ../23) and the parent batch rollups in the SAME transaction; emit `ClaimLineDecided`, `ClaimApproved`/`ClaimPartiallyApproved`/`ClaimDenied` via the outbox.

SEGREGATION OF DUTIES (enforce at the service, not the UI)
- The decider MUST NOT be the claim's originator/submitter → 403 SOD_ORIGINATOR_CANNOT_ADJUDICATE (audited).
- The decider MUST NOT be affiliated with the claiming provider (ABAC provider-affiliation attribute) → 403 SOD_PROVIDER_AFFILIATED (audited).
- DUAL CONTROL: any decision or override whose value exceeds a CONFIGURABLE threshold enters PendingSecondApproval and requires a second, distinct approver (also SoD-checked) before it takes effect. One person can never satisfy both roles.
- Adjudication is separate from settlement release (10b.8).

ACCEPTANCE
- Given a line with a recommendation, When the officer Approves/PartiallyApproves/Denies/Adjusts/RequestsInfo/RoutesToClinical, Then an append-only claim_decision is written with decider, timestamp, reason code(s), rationale, and rule_version, and the batch rollup updates.
- Given a Deny or Adjust with no reason code or blank rationale, Then 422 and no decision is recorded.
- Given the user who created/submitted the claim, When they try to decide it, Then 403 and the attempt is audited.
- Given a decision above the dual-control threshold, When one approver acts, Then it does NOT take effect until a second distinct approver confirms.
- Given a Claims Officer opens any claims endpoint, Then no diagnosis/emr_note/result value is present in any response body.

REQUIRED TESTS
- AUTHORIZATION tests: Claims Officer cannot read diagnosis, emr_note, or lab/imaging result values through ANY claims endpoint (assert field absence in the serialized payload, and 403 on direct cross-service attempts); a provider cannot read another provider's claims/batches (403/empty, audited).
- SoD tests: originator cannot adjudicate; provider-affiliated user cannot decide their own provider's claim; dual control requires two DISTINCT approvers above the threshold.
- Unit: mandatory reason code + rationale on deny/adjust/override; allowed_amount bounds on partial.
- Integration: line decisions roll up to claim status and batch totals; append-only enforcement (UPDATE/DELETE on claim_decision fails).
- Concurrency: two officers deciding the same line → one wins, the other 409, exactly one decision recorded.
- Audit: every decision is hash-chained and the chain verifies.
```

### 10b.5 — Provider-submitted claims + document matching

```text
Add the PROVIDER-SUBMITTED origination channel. READ FIRST: ../36-claims-management.md §3.2 and §5 (checks 4 and 5), ../18-security-model.md (provider isolation), document-service (phase 1).

ENDPOINTS (/api/v1)
- POST /claims/submissions (scope claims:submit; provider role or Mersal acting on their behalf, recorded as submitted_on_behalf_of) — header Idempotency-Key REQUIRED. Body: provider, beneficiary reference, invoice number, lines [{code_system, code, description, service_date, quantity, billed_amount}], document references.
- POST /claims/submissions/{id}/documents — attach invoices/supporting docs via document-service (type + size validation, malware scan, encrypted at rest). Claims-service stores only the document REFERENCE, never the bytes.
- GET /claims/submissions/{id} — provider sees only its OWN submissions (ABAC PO + RLS).

MATCHING
- Match each submitted line to an existing auto-derived claimable item on (provider, beneficiary, service code, service date, authorization). Allow a configurable service-date tolerance window; document the tolerance.
- MATCHED → proceed on the existing claim_line, recording the provider's BILLED amount ALONGSIDE the contract price (never overwriting it). A billed ≠ contract difference is a price-variance candidate for reconciliation (10b.7) — it is not silently accepted.
- UNMATCHED → flag reason code NO_FULFILLMENT_RECORD, set RequiresManualReview, and route to the manual assessment queue. NEVER auto-approve an unmatched line, at any value.
- Re-submission of an already-claimed fulfillment reference hits the 10b.1 unique index → DUPLICATE_CLAIM (409), audited.
- Emit `ClaimSubmitted` via the outbox; audit submission, every document attach, and every match/no-match outcome (with the matching key used).

ACCEPTANCE
- Given a provider submits lines that match auto-derived items, When matching runs, Then billed amount and contract price are BOTH recorded on the line and any variance is flagged.
- Given a submitted line with no fulfillment record, Then it is flagged NO_FULFILLMENT_RECORD, queued for manual assessment, and is never auto-approved.
- Given a provider submits a line for a fulfillment already claimed, Then DUPLICATE_CLAIM 409 and no second payable line exists.
- Given provider A, When they read submissions, Then provider B's submissions are invisible (403/empty, audited).

REQUIRED TESTS
- Unit: matching key + date-tolerance logic (match, near-miss, no-match).
- Integration: matched path records billed + contract price; unmatched path lands in the manual queue with the reason code; duplicate hits the unique index.
- AUTHORIZATION test: cross-provider read/submit is denied and audited.
- Idempotency: same Idempotency-Key on submission → one submission, identical response.
```

### 10b.6 — Beneficiary reimbursement + OCR (assistive only)

```text
Add the BENEFICIARY REIMBURSEMENT channel with OCR assistance. READ FIRST: ../36-claims-management.md §3.3 (rules are literal — implement them exactly), ../23-state-machines.md (reimbursement lifecycle), phase-13-interoperability-and-roadmap.md (IDocumentOcrProvider), ../0C-OPEN-SOURCE-STACK.md (self-hosted OCR, MinIO), ../19-audit-strategy.md.

SUBMISSION
- POST /api/v1/reimbursement-requests (scope claims:reimburse:submit) — submitted by the beneficiary or by Reception/Case Manager ON BEHALF (record acting_for). Body: beneficiary, the underlying AUTHORIZED prescription/investigation order reference, claimed amount + currency, receipt document(s), and result/dispense EVIDENCE document(s).
- Validation gate: allowed file types + size limits, MALWARE SCAN (ClamAV), store in document-service encrypted; claims-service keeps only references. Reject and audit anything that fails the scan.
- Prerequisites (hard): an AUTHORIZED underlying order/prescription (or an explicitly configured non-gated category), a legible receipt, and evidence the service was actually rendered. Missing any → the request cannot proceed to auto-match; it goes to ManualAssessment.
- Table `reimbursement_request` per ../22 with the canonical states from ../23. Emit `ReimbursementSubmitted`.

OCR — PLUGGABLE AND ASSISTIVE
- Define `IDocumentOcrProvider` in the service's Infrastructure abstractions: ExtractAsync(documentRef, languages) → fields with value, CONFIDENCE SCORE, and SOURCE REGION (page + bounding box). Default implementation: a SELF-HOSTED Arabic+English engine (e.g. Tesseract with `ara+eng`) running in-cluster — no external SaaS, no PHI leaving the deployment. The interface must be swappable (registered by DI, covered by a swappability test).
- Persist every extraction as an append-only `ocr_extraction` row: document_ref, field_name, extracted_value, confidence, source_region, engine + engine_version, extracted_at. Never overwrite a prior extraction.
- Extract candidates: provider, service date, amount, currency, drug/service codes.

MATCHING & THE HUMAN GATE
- HIGH confidence (above a configurable per-field threshold) AND an unambiguous match to an AUTHORIZED order/prescription → PRE-FILL claim lines flagged AUTO_MATCHED, with the OCR values, confidences, and source regions attached for review. Emit `ReimbursementMatched`.
- LOW confidence, AMBIGUOUS (more than one candidate), or ANY MISMATCH (provider/date/amount/code disagreeing with the authorized record) → ManualAssessment queue for hand matching. Emit `ReimbursementRequiresManualAssessment`.
- OCR IS ASSISTIVE, NEVER AUTHORITATIVE: an AUTO_MATCHED line NEVER becomes payable without a human confirmation. A reimbursement decision requires an explicit Claims Officer decision (10b.4) in every case — there is no auto-approval path for reimbursements regardless of confidence.
- CAP: payable amount = min(contract tariff, receipt amount). Exceeding the cap requires an explicit officer OVERRIDE with mandatory justification, dual-controlled per the 10b.4 threshold, and audited.
- Do NOT store the beneficiary's bank/payout details in the claim. Settlement advice references the member; payout runs through Mersal's existing finance process.
- Show the OCR overlay data (value + confidence + region) in the review payload so the human can verify against the document image.

ACCEPTANCE
- Given a submitted receipt, When OCR runs, Then every extracted field is persisted with a confidence score and source region, and the document passed a malware scan.
- Given a high-confidence unambiguous match to an authorized Rx/order, Then claim lines are pre-filled and flagged AUTO_MATCHED — and remain unpayable until a human decides.
- Given low confidence, ambiguity, or any mismatch, Then the request goes to ManualAssessment and no line is pre-filled as matched.
- Given no authorized underlying order/prescription, Then the request cannot be auto-matched and is queued for manual assessment.
- Given a receipt above the contract tariff, Then the payable amount is capped at the tariff unless an audited, justified override is recorded.

REQUIRED TESTS
- Unit: confidence thresholds (high/low/boundary), ambiguity detection, mismatch detection, min(tariff, receipt) cap and override path.
- Integration: OCR pipeline end-to-end with a fake IDocumentOcrProvider — ocr_extraction rows append-only with confidence + region; swappability test asserting a second provider implementation is used without code change.
- Integration: no reimbursement line can reach a payable state without a recorded human decision.
- Security: malware-scan rejection path is audited; no bank/payout fields exist in the claims schema.
- Audit: submission, OCR run, match outcome, and override are hash-chained.
```

### 10b.7 — Reconciliation worklist + append-only adjustments

```text
Build RECONCILIATION and ADJUSTMENTS. READ FIRST: ../36-claims-management.md §7 (buckets + adjustment type table are authoritative), ../07-functional-requirements.md FR-INV-008 (reversal only via compensating action), ../19-audit-strategy.md.

RECONCILIATION WORKLIST (GET /api/v1/reconciliation, scope claims:reconcile)
- Compare three views over a period: what Mersal's records say was DELIVERED (auto-derived from fulfillment), what the provider BILLED (submitted claims/statement), and what was APPROVED for payment.
- Bucket every discrepancy: matched · billed-not-delivered (no fulfillment record) · delivered-not-billed (provider hasn't claimed; this is the aged-unbilled feed) · price variance (billed ≠ contract tariff) · quantity variance · duplicate.
- Filter by provider, branch, period, bucket, value; each row links to the claim/line and the evidence references. Min-necessary projection — codes and amounts only, zero clinical fields.

ADJUSTMENTS (POST /api/v1/claims/{claimId}/lines/{lineId}/adjustments, scope claims:adjust, Idempotency-Key REQUIRED)
- Table `claim_adjustment` — APPEND-ONLY (DB rule/trigger, no UPDATE/DELETE grants): id, claim_line_id, type, signed amount_delta, reason_code (mandatory), rationale (mandatory, non-blank → 422 otherwise), adjusted_by, adjusted_at, correlation_id, original_claim_line_id (for recoveries), before_amount, after_amount.
- Types (exactly these): PriceCorrection · QuantityCorrection · Deduction · Recovery/Clawback · Writeoff · Reversal/Void · Reallocation.
- Rules: adjustments carry a SIGN (debit/credit) and ALWAYS net into the batch rollup. A Recovery/Clawback MUST reference the original claim line it recovers against (422 without) and may be carried into a LATER batch. Reversal/Void is a compensating entry — it never mutates or deletes the original decision. Reallocation moves the line to the correct provider/branch/period as a new anchored entry plus a reversing entry, never an in-place edit.
- DUAL CONTROL: if the net payable for the batch would go NEGATIVE, the adjustment requires an explicit second approver before it takes effect (422/PendingSecondApproval otherwise).
- Every adjustment writes an immutable hash-chained audit event with BEFORE and AFTER amounts. Emit `ClaimAdjusted` (and `ClaimVoided` for Reversal/Void) via the outbox.

ACCEPTANCE
- Given a period, When I open the reconciliation worklist, Then every discrepancy appears in exactly one bucket with its claim/line link.
- Given a price variance, When I raise a PriceCorrection, Then an append-only adjustment with a signed delta, reason code, and rationale is recorded and the batch rollup nets it in.
- Given a Recovery with no reference to an original line, Then 422 and nothing is recorded.
- Given an adjustment that would make batch net payable negative, Then it requires a second approver before taking effect.
- Given any adjustment, When it commits, Then the original decision row is unchanged and an audit event records before/after amounts.

REQUIRED TESTS
- Unit: bucket classification (each of the six buckets, including edge cases where a line qualifies for two — assert the documented precedence); sign handling per adjustment type; rollup netting arithmetic.
- Integration: append-only enforcement (UPDATE/DELETE on claim_adjustment fails); recovery-reference requirement; negative-net dual control.
- Audit: every adjustment hash-chained with before/after; chain verification passes.
```

### 10b.8 — Settlement advice + exports (NO payment execution)

```text
Generate the SETTLEMENT ADVICE and EXPORTS. READ FIRST: ../36-claims-management.md §8, ../19-audit-strategy.md, ../0C-OPEN-SOURCE-STACK.md (MinIO object-lock/WORM), document-service (phase 1).

**STATE PLAINLY IN THE README AND THE ADR: the platform NEVER moves money.** There is no payment execution, no bank/payment-rail integration, and no payout endpoint in this or any other service. The settlement advice is the hand-off artifact; Finance/treasury executes payment externally.

GENERATION (on batch Decided)
- Produce one IMMUTABLE settlement advice per payee: header (payee provider/branch or reimbursement cohort, period, batch number, generated_by, generated_at), per-claim/line detail (approved, adjusted, denied WITH reason codes), and totals: claimed → priced → approved → adjustments → NET PAYABLE.
- Render to a stable document and store it in document-service in a WORM bucket (MinIO object-lock / retention) so it cannot be altered or deleted; record `settlement_advice` (append-only) with the document reference, content hash, totals snapshot, and batch link. Reference it from the batch and move the batch to SettlementIssued, FREEZING the rollups.
- Emit `SettlementAdviceIssued` via the outbox (consumed by finance/reporting, phase 10 / phase 8).
- Regeneration NEVER overwrites: it creates a new versioned advice referencing the superseded one.

EXPORTS (/api/v1/claim-batches/{id}/exports, scope claims:export)
- Formats: CSV and XLSX for finance, PDF for the provider. Content = the settlement advice projection.
- ZERO CLINICAL FIELDS in any export — no diagnosis, no emr_note, no result values, no clinical prescription detail. Assert this in a test over the generated file content, not just the DTO.
- EVERY export writes an audit event: actor, batch, format, row count, filters, timestamp, correlation id. Exports respect the exporter's permissions and provider isolation (a provider exports only its own batch).

OPTIONAL EXTERNAL PAYMENT REFERENCE
- POST /api/v1/claim-batches/{id}/payment-reference (scope claims:settle, SoD-separated from claims:decide) — records an EXTERNAL payment reference/date supplied by Finance after paying outside the platform, then moves the batch to Closed. This RECORDS a fact; it initiates nothing. Append-only and audited.

ACCEPTANCE
- Given a Decided batch, When settlement advice is generated, Then an immutable document is stored WORM in document-service, referenced from the batch, rollups are frozen, and the batch is SettlementIssued.
- Given a settlement advice exists, When anyone attempts to modify or delete it, Then it fails (object-lock + append-only row).
- Given any export, When it is produced, Then it contains no clinical fields and an audit event is written.
- Given a provider, When they export, Then they can export only their own batch.
- Given the whole service, When searched, Then there is no code path that initiates a payment or transfer.

REQUIRED TESTS
- Integration: Decided → advice generated, stored WORM, batch SettlementIssued with frozen rollups; regeneration creates a new version and preserves the old.
- Content test: parse the generated CSV/XLSX/PDF and assert the absence of every clinical field name and value.
- AUTHORIZATION test: cross-provider export denied and audited; the settle scope is separate from the decide scope (SoD).
- Audit test: every export and payment-reference recording is hash-chained.
```

### 10b.9 — Appeals + claims KPIs

```text
Add APPEALS and the claims KPI read-model feed. READ FIRST: ../36-claims-management.md §6 (appeal transitions) and §11 (KPI list), ../23-state-machines.md (claim lifecycle: Approved|PartiallyApproved|Denied → Appealed → UnderAdjudication), ../08-non-functional-requirements.md, phase-8-notifications-reporting.md (reporting-service read-models).

APPEALS
- POST /api/v1/claims/{id}/appeals (scope claims:appeal) — a provider or beneficiary (or Mersal on their behalf) appeals a decided claim/line with a mandatory reason and optional supporting documents.
- The claim RE-ENTERS UnderAdjudication while PRESERVING THE ORIGINAL DECISION THREAD: the prior claim_decision rows are untouched and remain readable; the appeal and its re-decision are NEW append-only rows linked to the original via appeal_id. Nothing is edited or hidden. Model it as the parallel of the authorization InfoRequested/resubmit path.
- SoD applies to the re-decision: the appeal cannot be decided by the original decider (403 SOD_SAME_DECIDER) — escalate to a Claims Reviewer/Senior.
- If the appeal lands on a settled batch, the correction flows as an adjustment/recovery (10b.7) in a later batch — a settled batch is never reopened.
- Emit `ClaimAppealed` and the resulting decision events via the outbox; audit everything.

KPI READ-MODEL FEED
- Publish/expose read-model data (events + a GET /api/v1/claims/kpis aggregate) for: claims TAT (submission→decision), approval/denial rate, TOP DENIAL REASONS (by reason code), adjustment value BY TYPE, provider variance league table, reimbursement OCR AUTO-MATCH RATE (and manual-assessment rate), batch cycle time, AGED UNBILLED (delivered-not-billed), recovery outstanding.
- Aggregates are AGGREGATE-ONLY and carry NO clinical fields and no direct identifiers where not required (de-identify/pseudonymize per FR-RPT-006). Reporting-service (phase 8) consumes these; do not duplicate dashboards here.

ACCEPTANCE
- Given a Denied claim, When it is appealed, Then it re-enters UnderAdjudication, the original decision thread is intact and readable, and the appeal is linked to it.
- Given an appeal, When the ORIGINAL decider tries to re-decide it, Then 403 and the attempt is audited.
- Given a settled batch, When an appeal succeeds, Then the correction is an adjustment/recovery in a later batch and the settled batch is untouched.
- Given decided claims, When KPIs are queried, Then TAT, approval/denial rate, top denial reasons, adjustment value by type, provider variance, OCR auto-match rate, and aged unbilled are available with no clinical fields.

REQUIRED TESTS
- Integration: appeal re-entry preserves prior decisions (assert the original rows are byte-identical after the appeal) and links the thread.
- SoD test: original decider cannot decide the appeal.
- Integration: settled batch is not reopened; correction appears as an adjustment in a later batch.
- Unit: each KPI's computation on a fixture dataset; assertion that KPI payloads contain no clinical field names.
```

---

## Guardrails

- **No double-billing — enforced by the database.** UNIQUE partial index on `fulfillment_ref WHERE status <> 'Void'`, proven by a real parallel-transaction concurrency test. Event consumers dedupe on event id.
- **Claims never touch coverage accumulators.** Adjudication READS `limit_value − consumed_value`; the consume/dispense transaction owns the decrement (FR-INV-006). A test asserts `consumed_value` is unchanged by adjudication.
- **No clinical data in claims, ever.** Server-side allow-list DTOs + OPA/Cerbos + RLS. Claims Officers see codes, amounts, authorization linkage, document references, and result *existence* — never diagnosis, EMR notes, or result values. Medical-necessity questions route to a clinical reviewer who records an opinion, not a payment decision.
- **Append-only everywhere.** `claim_decision`, `claim_adjustment`, `ocr_extraction`, `settlement_advice`, `claim_batch_item` history — no UPDATE/DELETE grants, DB rule/trigger enforced. Corrections are adjustments or compensating Void + re-claim. No hard deletes.
- **Never a guessed price.** `NO_TARIFF` → manual pricing. No defaults, averages, or carried-over rates.
- **Mandatory reason code + rationale** on every deny, adjust, and override (422 otherwise). Every decision records decider, timestamp, allowed amount, reason codes, rationale, and rule version.
- **SoD + dual control.** Originator ≠ adjudicator; provider-affiliated users cannot decide their own provider's claims; adjudication ≠ settlement release; a second distinct approver above the configurable value threshold and for negative-net adjustments.
- **Provider isolation.** ABAC `PO` + RLS on every read/export; a provider never sees another provider's claims, batches, or advices.
- **OCR is assistive, never authoritative.** Confidence score + source region on every extraction; a human confirms before anything affects money; low confidence/ambiguity/mismatch → manual assessment.
- **The platform never executes payments.** No payment rails, no payout endpoints. Settlement advice is immutable, stored WORM, and handed to Finance; an external payment reference may be *recorded* afterwards.
- **Batching integrity.** A claim sits in at most one open/under-review batch (DB constraint); a batch reaches `Decided` only when every line is decided; rollups freeze at `SettlementIssued`.
- **Canonical states + outbox only.** States exactly as in `../23-state-machines.md`; illegal transitions → 409 problem+json; all events published via the transactional outbox in the same transaction as the state change.

## Done when

- `claims-service` owns schema `claims`, auto-derives priced claim lines from `OrderLinesConsumed`/`RxLinesDispensed` idempotently, and a **concurrency test proves the fulfillment-ref unique index makes double-billing impossible**.
- Batches can be created by date range, provider branch, provider group, and manual selection; the single-open-batch constraint holds under parallel attempts; `Decided` is blocked while any line is undecided; rollups freeze at `SettlementIssued`.
- Pre-adjudication runs the fixed 9-step order, collects **all** applicable coded reasons per line, records `system_recommendation` + `allowed_amount` + `rule_version`, routes `NO_TARIFF` to manual pricing, and leaves coverage accumulators untouched.
- Claims Officers record **line-level** decisions with mandatory reason code + rationale on deny/adjust/override, append-only, rolled up to claim and batch — with **authorization tests proving no diagnosis/EMR/result values are readable** and **SoD tests proving originator ≠ adjudicator, no provider-affiliated self-decision, and dual control above the threshold**.
- Provider-submitted claims match to auto-derived items and record billed vs contract price; unmatched lines are flagged `NO_FULFILLMENT_RECORD` and manually assessed, never auto-approved.
- Beneficiary reimbursements run malware-scanned document intake and pluggable self-hosted Arabic+English OCR writing `ocr_extraction` rows with confidence + source region; high-confidence matches pre-fill `AUTO_MATCHED` lines, everything else queues for manual assessment, **and no reimbursement is payable without a human decision**; payable amount capped at min(tariff, receipt) unless an audited override.
- Reconciliation buckets every discrepancy (matched, billed-not-delivered, delivered-not-billed, price variance, quantity variance, duplicate); adjustments are append-only, signed, coded, dual-controlled when net payable would go negative, and net into batch rollups.
- Settlement advice is generated on `Decided`, immutable and WORM-stored, exportable to CSV/XLSX/PDF with **zero clinical fields** and every export audited; **no payment execution exists anywhere in the platform**; an external payment reference can optionally be recorded.
- Appeals re-enter `UnderAdjudication` preserving the original decision thread (decided by someone other than the original decider), and claims KPIs (TAT, approval/denial rate, top denial reasons, adjustment value by type, provider variance, OCR auto-match rate, aged unbilled) feed the reporting read-model.
- All US-CLM-\* acceptance criteria pass; unit/integration/contract/authorization/SoD/concurrency/idempotency/audit tests green; OpenAPI 3.1 + service README + ADR updated; migrations backward-compatible. Global Definition of Done (root `CLAUDE.md`) met.

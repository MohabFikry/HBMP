# claims-service (Phase 10b)

Turns already-delivered, authorized services into **reviewed, decided and settled financial records** — three
origination channels (auto-derived, provider-submitted, beneficiary reimbursement), batching, rules-based
pre-adjudication, line-level Claims Officer decisions rolled up to batch, reconciliation + append-only adjustments,
and an immutable settlement advice. **The platform never moves money** — settlement advice is the hand-off to
Finance; there is no payment execution or bank-rail integration anywhere.

Authoritative design: [`36-claims-management.md`](../../HBMP-Design/36-claims-management.md). Schema/enums:
[`22-data-dictionary.md §10A / §11.5`](../../HBMP-Design/22-data-dictionary.md). Lifecycles:
[`23-state-machines.md §7–§10`](../../HBMP-Design/23-state-machines.md). Build prompt:
[`phase-10b-claims-management.md`](../../HBMP-Design/claude-code-prompts/phase-10b-claims-management.md).

Owns schema `claims` exclusively (schema-per-service, RLS on). It never reads another service's tables — cross-context
data (tariffs, authorizations, eligibility, fulfillment) comes over the API/events.

## The invariants (enforced, not commented)

1. **No double-billing — enforced by the database.** `UNIQUE(fulfillment_ref) WHERE fulfillment_ref IS NOT NULL AND
   status <> 'Void'` (`ux_claim_line_fulfillment`) makes a second live payable line for a fulfillment/dispense record
   *impossible*, not merely unlikely. A loser fails on SQLSTATE 23505 and surfaces as `DUPLICATE_CLAIM` (409). Proven
   by a real 8-way parallel-transaction concurrency test.
2. **Idempotent intake.** Auto-derive consumers dedupe on event id (`processed_event`); a redelivered event is a
   no-op returning the prior line.
3. **Never a guessed price.** Pricing comes from `contract_service_line.agreed_price` for the code + service date. No
   tariff ⇒ `NO_TARIFF` ⇒ `RequiresManualReview`, `contract_price` stays null. Never defaulted/averaged/carried-over.
4. **Claims ≠ diagnosis.** The `claims` schema carries **no clinical column anywhere**. Every projection is a
   server-side allow-list DTO (codes + amounts + linkage + statuses). Enforced in three layers: the authz overlay
   grants claims roles no clinical action (default-deny), the DTOs are structurally clinical-free, and the schema has
   no clinical field to source one from. Proven by `ClaimsCannotReadDiagnosisTests`.
5. **Provider isolation.** ABAC provider-ownership + Postgres RLS (`app.tenant_id` / `app.provider_id` GUCs). A
   provider never sees another provider's claims.
6. **Never re-decrement coverage.** Claims read/reconcile against `consumed_value`; they never write it.

## Slices

- **10b.1 (this slice) — foundation + auto-derived claims.** `claim` + `claim_line` (+ `claim_seq`, `processed_event`),
  the no-double-billing unique index, RLS, the idempotent auto-derive intake executor (`ClaimIntakeExecutor`), tariff
  pricing (`IContractTariffProvider` → provider-service), min-necessary reads, and the `/api/v1/claims/intake` seam
  (mirrors finance `/projections` pending the fanout bus). Emits `ClaimCreated` / `ClaimLineCreated` via the outbox.
- **10b.2 — batching + batch lifecycle.** `claim_batch` + `claim_batch_item` (+ `batch_seq`), the single-open-batch
  partial unique index `ux_claim_one_open_batch` (a claim can never sit in two live batches → never settled twice),
  the 23 §9 lifecycle (Open→UnderReview→Decided→SettlementIssued→Closed + Cancelled) with its guards (≥1 claim to
  review, every line decided to Decide, reason to cancel/exception-remove), and rollups recomputed on every change and
  frozen at SettlementIssued. Emits `BatchCreated` / `BatchUnderReview` / `BatchDecided` via the outbox.
- **10b.3 — automated pre-adjudication.** The `Adjudicator` runs the fixed 9-step order (eligibility → coverage →
  pre-auth → fulfillment → duplicate → network → tariff → limit → co-pay) per line, **collects ALL applicable reason
  codes** (never stops at the first failure), and computes `system_recommendation` + `allowed_amount` (capped by
  auth scope and limit remaining, minus member share) + `rule_version`. Hard blocks ⇒ Deny; `NO_TARIFF` ⇒
  RequiresManualReview (price stays null); caps ⇒ Partial. Coverage accumulators are **read, never written**.
  `POST /claims/{id}/adjudicate`; emits `ClaimAdjudicated`; the append-only per-run history is the audit event.
- **10b.4 — Claims Officer worklist + line-level decisions.** `claim_decision` (append-only: DB trigger + no
  UPDATE/DELETE grant). The worklist is a min-necessary, clinical-free projection (codes, amounts, recommendation,
  result EXISTENCE — never values), audited on read. Decisions (Approve/PartiallyApprove/Deny/RequestInfo/
  RouteToClinical) enforce **SoD** (decider ≠ originator, not provider-affiliated → 403), **dual control** above a
  configurable value threshold (a second distinct approver), mandatory reason code + rationale on deny/override, and
  allowed-amount bounds on partial. Optimistic concurrency (line `xmin`) → two officers = one winner + one 409.
  Decisions roll up to the claim status and batch rollups; `Idempotency-Key` required. Emits `ClaimLineDecided` +
  `ClaimApproved`/`ClaimPartiallyApproved`/`ClaimDenied`.
- **10b.5 — provider-submitted claims + document matching.** `claim_submission` + `claim_submission_line` +
  `claim_document` (+ RLS). A provider (or Mersal on their behalf, recorded as `submitted_on_behalf_of`) submits an
  invoice; each line is matched to a delivered/authorized fulfillment on `(provider, beneficiary, code, service
  date ± tolerance, authorization)` via `IFulfillmentResolver` (seam to orders/pharmacy; the 2-day tolerance lives in
  `SubmissionMatcher`). **Matched** → a priced payable line records the provider's billed amount ALONGSIDE the contract
  price and flags a `price_variance` when they differ (reconciliation candidate, never silently accepted). **Unmatched**
  → a `NO_FULFILLMENT_RECORD` / RequiresManualReview line (no fulfillment_ref) in the manual queue, never auto-approved.
  **Re-submission of an already-claimed fulfillment** hits the 10b.1 unique index → the whole submission rolls back
  atomically → `DUPLICATE_CLAIM` (409), no second payable line. Idempotent on the header `Idempotency-Key`;
  provider-isolated (ABAC PO + RLS); submission, document attach, and every match/no-match outcome audited. Emits
  `ClaimSubmitted`. Documents are stored by REFERENCE only (bytes stay scanned + encrypted in document-service).
### Idempotency (migration 0009)

Every write here takes an `Idempotency-Key`, and every replay is now bound to the **body**. Until the
2026-08-09 audit the key was compared alone, so a key reused for a different request was answered with the
earlier one, 200 OK: a **deny** retried under an approve's key returned the approval (the officer believes
they refused a line that is now payable, with no error to investigate); an **adjustment** reused across two
amounts returned the first (the second correction never happens and the batch total stays wrong by the
difference); a **submission** reused across two invoices returned the first claim (the provider is told an
invoice was received that never was). A mismatched replay is now **422 `idempotency-key-reuse`;** a genuine
retry still replays.

**Reimbursement had no idempotency at all** — the only write in this service without it, and the one a
BENEFICIARY submits through, from a phone, on a connection that drops. A retry created a second request over
the same receipts, both ran the OCR pipeline, and both could auto-match. The header is now required (400
without it) and the replay short-circuits **before** the malware scan and the extraction.

- **10b.6 — beneficiary reimbursement + OCR (assistive, human-gated).** `reimbursement_request` + `ocr_extraction`
  (append-only — a re-run is new rows; the ONLY permitted UPDATE is a human setting `accepted_by`/`accepted_at`, and
  the extracted value/confidence/region are trigger-immutable). Pipeline: file type/size validation → **malware scan**
  (rejected + audited) → persist request → **OCR** via pluggable `IDocumentOcrProvider` (default self-hosted Tesseract
  `ara+eng`; only receipt/invoice/statement docs are read, never clinical result/dispense proofs) with confidence +
  source region per field → decide **AutoMatched vs ManualAssessment** (`ReimbursementRules.DecideMatch`: needs an
  authorized order, exactly one candidate, no mismatch, every field ≥ threshold — else manual). **OCR is assistive,
  never authoritative:** `ConfirmAsync` is the human gate (records acceptance, creates the Reimbursement claim with
  **Pending** lines); a line is payable only through an explicit officer decision (10b.4). Cap = **min(tariff,
  receipt)** with an audited justified override (`ValidateOverride`). **No bank/payout field** anywhere (proven by a
  structural test). Seams (`IDocumentOcrProvider` swappability-tested, `IDocumentScanner`, `IAuthorizedServiceResolver`)
  all DI-swappable. Emits `ReimbursementSubmitted` / `ReimbursementMatched` / `ReimbursementRequiresManualAssessment` /
  `ClaimCreated`.
- **10b.7 — reconciliation worklist + append-only adjustments.** `claim_adjustment` (append-only: the 0003 trigger +
  no UPDATE/DELETE grant). `GET /reconciliation` buckets each discrepancy over a period by the pure
  `ReconClassifier` with a fixed precedence (**Duplicate → BilledNotDelivered → DeliveredNotBilled → PriceVariance →
  QuantityVariance → Matched** — every row lands in exactly one), a min-necessary clinical-free projection. `POST
  /claims/{id}/lines/{lineId}/adjustments` records a signed entry with mandatory reason + rationale and BEFORE/AFTER
  amounts; `AdjustmentRules` enforce sign-per-type (Deduction/Recovery/Clawback/Writeoff/Reversal/Void must reduce)
  and that a **Recovery/Clawback references the original line** (422 without). Reversal/Void voids the line (a
  compensating entry — the original decision is never mutated); every other type marks it Adjusted and re-nets the
  rollup. **Dual control** when the batch/claim net payable would go NEGATIVE (recorded Pending → a second distinct
  approver confirms). `Idempotency-Key` required; the before/after amounts ride the hash-chained audit event. Emits
  `ClaimAdjusted` / `ClaimVoided`.
- **10b.8 — settlement advice + exports (NO payment execution).** `settlement_advice` + `settlement_payment_reference`
  (both append-only). On a **Decided** batch, `POST /claim-batches/{id}/settlement-advice` builds an **immutable**
  advice (append-only row + SHA-256 content hash + WORM document reference), freezes the rollups, and moves the batch to
  `SettlementIssued`; **regeneration writes a new version** referencing the superseded one — never an overwrite.
  `GET /claim-batches/{id}/exports?format=CSV|XLSX|PDF` renders the advice projection with **zero clinical fields**
  (asserted by parsing the generated bytes, not just the DTO), audited (actor/batch/format/row-count), and
  **provider-isolated**. `POST /claim-batches/{id}/payment-reference` (scope `claims:settle`, SoD-separate from
  `claims:decide`) **records an external payment fact only** → `Closed`. **THE PLATFORM NEVER MOVES MONEY** — no payout
  path exists (enforced by a source-scan test); see [ADR 0007](../../docs/adr/0007-claims-never-executes-payment.md).
  Renderers are dependency-free (self-hostable). Emits `SettlementAdviceIssued`.
- **10b.9 — appeals + claims KPIs.** `claim_appeal` (append-only). `POST /claims/{id}/appeals` re-enters a live decided
  claim into UnderAdjudication (appealed lines return to Pending for a fresh decision) while the **original
  `claim_decision` thread is preserved byte-identical** and the appeal links to it; the re-decision **cannot be made by
  the original decider** (SoD enforced in the decision handler — a person may not decide the same line twice). An appeal
  on an **already-settled batch** is recorded as `RoutedToAdjustment` — the settled batch is never reopened; the
  correction flows as a compensating adjustment/recovery (10b.7) in a later batch. `GET /claims/kpis` exposes the
  aggregate read-model feed (`ClaimsKpiCalculator`): TAT, approval/denial rate, top denial reasons, adjustment value by
  type, provider variance league, OCR auto-match rate, aged unbilled, recovery outstanding — **aggregate-only, no
  clinical fields**. reporting-service (phase 8) consumes it. Emits `ClaimAppealed`.

**Phase 10b is COMPLETE** — all nine slices (10b.1–10b.9) landed, 153 tests green against real Postgres.

## Endpoints (10b.1)

| Method | Path | Scope | Notes |
|---|---|---|---|
| GET | `/api/v1/claims` | `claims:read` | Min-necessary list; provider-isolated; audited read |
| GET | `/api/v1/claims/{id}` | `claims:read` | Min-necessary detail; provider-isolated; audited read |
| POST | `/api/v1/claims/intake` | `claims:ingest` | Auto-derive seam; `Created` / `Replayed` / 409 `DUPLICATE_CLAIM` |

## Local run / tests

```bash
# apply the migration to the dev DB (host Postgres :55432 or the compose postgres)
psql "host=localhost port=55432 dbname=hbmp user=hbmp" -f Infrastructure/Migrations/0001_claims.sql

# unit/authz tests always run; DB integration + concurrency tests are env-gated (need the hbmp superuser conn)
CLAIMS_TEST_DB="Host=localhost;Port=55432;Database=hbmp;Username=hbmp;Password=***" \
  dotnet test services/claims/Tests/Mersal.Claims.Tests.csproj
```

Behind Kong at `/api/v1/claims`; compose service `claims-service` (`:8106` on the host).

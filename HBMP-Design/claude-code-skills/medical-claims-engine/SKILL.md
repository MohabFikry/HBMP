---
name: Medical Claims Engine
description: Designs and reviews claim capture, batching, adjudication, officer decisions, reimbursement/OCR, reconciliation, settlement advice, and appeals for Mersal HBMP, built on the existing authorizations/orders/prescriptions core with contract tariff pricing and strict data minimization. Claims are IN SCOPE as build Phase 10b (design doc `../../36-claims-management.md`). Use when designing, implementing, or reviewing claims processing, adjudication rules, claim batching, denial/appeal logic, adjustments, settlement output, or claim state machines.
---

# Medical Claims Engine

## Purpose
Give Claude Code a consistent, Mersal-correct model for the **Claims Management** capability. Claims were originally deferred to the R6+ roadmap; they are now **in scope as build Phase 10b**, with `../../36-claims-management.md` as the **authoritative design** and `../../claude-code-prompts/phase-10b-claims-management.md` as the build prompt. They attach additively to the completed service-oriented HBMP core (authorizations, fulfillment, contracts/tariffs) without re-platforming (`../../16-service-architecture.md` §1). A claim is the *financial settlement* record derived from an already-fulfilled clinical benefit (a consumed order line or a dispensed prescription line), never a new source of clinical truth.

## When to use / when not to use
- **Use when:** modelling claim capture across the three origination channels; batching claims for review/settlement; writing adjudication rules (eligibility, coverage, limits, pre-auth linkage, tariff pricing); designing Claims Officer line-level decisions, reconciliation buckets, adjustments, settlement advice/exports, denial reason codes, medical-necessity routing, or appeals; reviewing a `claims`/`finance`/`reporting` service design that touches `claim`/`claim_batch`/`settlement_advice`.
- **Do not use for:** the live clinical fulfillment invariants themselves (use `pbm-adjudication-engine` and the order/prescription lifecycles); prior-authorization *at point of care* (use `health-insurance-tpa-operations`); expressing a single benefit rule declaratively (use `healthcare-business-rules-engine`).

## Mersal domain knowledge & rules
- **Claims are downstream of fulfillment.** A claimable event exists only after an `order_fulfillment` (consume) or `dispense_event` row is written — these append-only rows are the *authoritative usage record* (`../../22-data-dictionary.md` §7.3, §8.3). The claim references that fulfillment; it never re-decrements a coverage limit already decremented at consume/dispense time.
- **Adjudicate on codes and amounts, never on diagnosis.** `Finance.diagnosis = denied` is a **hard rule** (`../../11-permission-matrix.md` §3.2, §4). A claim carries billing/service codes (`CPT`/`LOINC`/`LOCAL`) and amounts. A procedure code is exposed only at the minimum granularity needed to price/adjudicate. Any claim payload that leaks the diagnosis narrative or clinical notes to a finance actor is a defect.
- **Pricing = contract tariff.** Line price is resolved from the performing provider's `contract_service_line.agreed_price` for the matching `code_system` + `code`, valid on the service date (`../../22-data-dictionary.md` §5.3). No agreed tariff on an active contract ⇒ the line cannot be auto-priced; route to manual pricing/network review, do not guess.
- **Adjudication checks (evaluation order):** (1) beneficiary status `Active` and policy validity window covers the service date; (2) coverage category matches the service's `benefit_category` (LAB/IMAGING/PHARMACY/CONSULT/REFERRAL); (3) pre-auth linkage — if the service was gated, a valid non-expired `authorization` in `Approved`/`PartiallyApproved`/`EmergencyApproved`/`Overridden` must link the claim's subject, and a `PartiallyApproved` scope caps the payable lines; (4) coverage limit availability by `limit_type`; (5) tariff pricing; (6) co-pay/deductible split.
- **Coverage limits are typed.** `Annual | PerEncounter | Lifetime | Count` with `reset_period` `None/Monthly/Quarterly/Yearly` (`../../22-data-dictionary.md` §3.3). Adjudication reads `limit_value − consumed_value`; because consume/dispense already moved `consumed_value` transactionally, claims must reconcile against that accumulator, not maintain a parallel one.
- **EmergencyApproved claims** carry the retrospective-review flag; a claim linked to an emergency authorization is payable but must remain visible to utilization review until the retrospective decision is recorded.
- **Denials require a coded reason.** Every denied line gets a machine reason code (e.g. `NOT_ELIGIBLE`, `POLICY_EXPIRED`, `LIMIT_EXCEEDED`, `NO_PRIOR_AUTH`, `AUTH_EXPIRED`, `EXCEEDS_AUTH_SCOPE`, `NOT_COVERED_CATEGORY`, `NO_TARIFF`, `NO_FULFILLMENT_RECORD`, `DUPLICATE_CLAIM`, `PROVIDER_OUT_OF_NETWORK`, `CONTRACT_NOT_EFFECTIVE`, `NOT_MEDICALLY_NECESSARY`) plus human-readable rationale. Rationale is **mandatory** on deny, adjust, and override.

### Three origination channels (`../../36-claims-management.md` §3)
1. **Auto-derived** — the claims service consumes `OrderLinesConsumed`/`RxLinesDispensed` (idempotently) and creates a claim line per fulfillment/dispense row, priced from the performing provider's contract tariff. This is the baseline truth: what the network actually delivered.
2. **Provider-submitted** — an invoice + documents, each line **matched** to an auto-derived item by `(provider, beneficiary, service code, service date, authorization)`. Matched → billed amount recorded *alongside* the contract price (variance is an adjustment candidate). Unmatched → `NO_FULFILLMENT_RECORD` + manual assessment, **never auto-approved**.
3. **Beneficiary reimbursement (OCR-assisted)** — receipts + result/dispense evidence against an **authorized** order/prescription. Documents are malware-scanned and stored in `document-service`; extraction runs through a pluggable, self-hosted Arabic+English `IDocumentOcrProvider` (e.g. Tesseract `ara+eng`) writing `ocr_extraction` rows with **confidence score + source region**. High-confidence unambiguous match → pre-filled lines flagged `AUTO_MATCHED`; low confidence, ambiguity, or any mismatch → **ManualAssessment** queue. Payable is capped at **min(contract tariff, receipt amount)** unless an audited, justified override. Bank/payout details are never stored on the claim.

### Batching (§4)
- A **batch** is the unit of review and settlement: one payee, a named/dated collection, numbered `BAT-<yyyy>-<base32(8)>`.
- Creation modes: **date range** · **provider branch** · **provider group** (chain-wide) · **manual selection** from a filtered worklist.
- **Single-open-batch:** a claim belongs to at most one `Open`/`UnderReview` batch, enforced by a unique partial index — so it can never be settled twice.
- Batches carry rollups (claimed, priced, approved, adjusted, denied, net payable); membership changes are audited (removal from `UnderReview` requires a reason). Lifecycle `Open → UnderReview → Decided → SettlementIssued → Closed` (+ `Cancelled`); rollups **freeze** at `SettlementIssued`.

### Line-level decisions rolled up to batch (§6)
- The system **recommends** (`RecommendApprove | RecommendPartial | RecommendDeny | RequiresManualReview` + all reason codes + `allowed_amount` + `rule_version`); the **Claims Officer decides**, per line: Approve · PartiallyApprove · Deny · Adjust · RequestInfo · **RouteToClinical**.
- Line decisions roll up to claim status and batch totals. **A batch reaches `Decided` only when every line has a recorded decision.**
- Clinical reviewers (Medical Approval/Director) see clinical context for routed lines and record an **opinion**, not a payment decision. The Claims Officer never sees clinical content.
- **SoD:** decider ≠ originator/submitter, and never affiliated with the claiming provider; adjudication ≠ settlement release; **dual control** above a configurable value threshold and when net payable would go negative.

### Reconciliation & adjustments (§7)
- Reconciliation compares delivered (fulfillment) vs billed (submissions/statement) vs approved, bucketing every discrepancy as: **matched · billed-not-delivered · delivered-not-billed (aged unbilled) · price variance · quantity variance · duplicate**.
- **Adjustment types (exactly these):** `PriceCorrection`, `QuantityCorrection`, `Deduction`, `Recovery`/`Clawback`, `Writeoff`, `Reversal`/`Void`, `Reallocation`. Each is **append-only**, signed (debit/credit), coded, with mandatory rationale, nets into the batch rollup, and is audited with before/after amounts. A recovery must reference the original claim line and may be carried into a later batch.

### Settlement advice — and NO payment execution (§8)
- On batch `Decided`, generate an **immutable settlement advice per payee**: header, per-line detail (approved/adjusted/denied with reason codes), totals claimed → priced → approved → adjustments → **net payable**. Stored **WORM** (MinIO object-lock) in `document-service` and referenced from the batch; regeneration creates a new version, never an overwrite.
- Exports (CSV/XLSX for finance, PDF for the provider) are **audited** and carry **zero clinical fields**.
- **The platform never moves money.** Finance/treasury executes payment externally and may *record* an external payment reference back against the batch. There is no payment rail, payout endpoint, or transfer initiation anywhere in the platform.

## Key entities, states & invariants
- **Entities (Phase 10b, schema `claims`):** `claim`, `claim_line`, `claim_batch` (+ `claim_batch_item`), `claim_decision`, `claim_adjustment`, `reimbursement_request`, `ocr_extraction`, `settlement_advice`, `appeal`. Reuse `provider_contract`/`contract_service_line`, `coverage_limit`, `authorization`, `order_fulfillment`, `dispense_event`.
- **Claim lifecycle (`../../36-claims-management.md` §6, `../../23-state-machines.md`):** `Draft → Submitted → UnderAdjudication → (PendingInfo | ClinicalReview) → (Approved | PartiallyApproved | Denied) → Settled ; Appealed → UnderAdjudication ; Void`. Mirrors the authorization lifecycle's shape and rigor (`../../23-state-machines.md` §5): partial outcomes are first-class, decisions are append-only, reversals are compensating `Void` events — never mutation/hard-delete (`../../07-functional-requirements.md` FR-INV-008, FR-AUD-003).
- **Batch lifecycle:** `Open → UnderReview → Decided → SettlementIssued → Closed` (+ `Cancelled`), with the guard that `Decided` requires every line decided.
- **Invariants:** immutable audit on every state change, decision, adjustment, and export (actor, from/to, correlationId, rationale); SoD — adjudicator ≠ originator, no provider-affiliated self-decision, adjudication ≠ settlement release (`../../11-permission-matrix.md` §6.7); **one non-void claim line per fulfillment/dispense reference** (no double-billing, enforced by a unique partial index `WHERE status <> 'Void'`); one open batch per claim (unique partial index); provider isolation — a provider sees only its own claims/batches/advices (`PO`); **OCR is assistive — a human confirms before anything affects money**.

## How to apply
1. Anchor every claim line to a specific `order_fulfillment` or `dispense_event`; if none exists, there is nothing to claim — except a reimbursement, which must still be matched to such a record or to an **authorized** order/prescription.
2. Run adjudication in the fixed 9-step order above and **collect ALL applicable reason codes per line** (do not stop at the first failure) so partial approvals are precise; record `system_recommendation` + `allowed_amount` + `rule_version`.
3. Price only via active contract tariffs; emit `NO_TARIFF` → manual pricing rather than a default, averaged, or carried-over price.
4. Batch claims for review/settlement (date range · branch · group · manual), one open batch per claim, rollups recomputed on every change and frozen at `SettlementIssued`.
5. Emit an append-only `claim_decision` per **line** with mandatory reason code + rationale on deny/adjust/override, roll it up to claim and batch, and write a high-severity audit event. Enforce SoD and dual control in the service, not the UI.
6. Correct only by **adjustment** (signed, typed, coded, append-only) or a compensating `Void` + re-claim; reconcile against provider statements using the six buckets.
7. On batch `Decided`, generate the immutable WORM settlement advice and audited exports — and stop there. Never initiate a payment.
8. Model appeals as a re-entry into `UnderAdjudication` that preserves the original decision thread (parallel to `AuthInfoRequested`/resubmit), decided by someone other than the original decider; a settled batch is never reopened — the correction is a later-batch adjustment/recovery.
9. Keep all clinical fields (`diagnosis`, `emr_note`, result values) stripped server-side from every claims/finance-facing projection; expose result **existence + date + document reference** only.

## Canonical references
- `../../36-claims-management.md` — **authoritative claims design (Phase 10b)**: origination channels, batching, adjudication order, decisions, reconciliation, settlement advice, roles/min-necessary, events, KPIs
- `../../claude-code-prompts/phase-10b-claims-management.md` (the build prompt: sub-prompts 10b.1–10b.9)
- `../../35-implementation-plan.md` (§10 — claims were originally an R6+ roadmap train; now built as Phase 10b)
- `../../16-service-architecture.md` (§1 extensibility, §7 events, §8 sagas)
- `../../11-permission-matrix.md` (§3.2/§4 Finance clinical = denied; §6.7 SoD on payment)
- `../../22-data-dictionary.md` (§3.3 limits, §5.3 tariffs, §7.3/§8.3 fulfillment, §9 authorization)
- `../../07-functional-requirements.md` (FR-INV-*, FR-RPT-002, FR-AUTH-004/007)

## Guardrails
- Never let a finance/claims actor read `diagnosis`, `emr_note`, `prescription`, `lab_result`, or `imaging_result`. Adjudicate on codes + amounts only.
- Never decrement a coverage limit from the claims path; the consume/dispense transaction already owns that decrement.
- Never mutate or delete a submitted claim, decision, adjustment, OCR extraction, or settlement advice — correct with an audited `Void`/reversal or an append-only adjustment.
- Never auto-approve a gated service's claim without a valid, in-scope, non-expired linked authorization; never auto-approve an unmatched (`NO_FULFILLMENT_RECORD`) line at any value.
- Never let a claim be payable twice: one non-void line per fulfillment reference, one open batch per claim — both enforced by database constraints, not application logic.
- Never treat OCR output as authoritative. Every extraction carries a confidence score and source region; low confidence, ambiguity, or any mismatch goes to manual assessment, and a human confirms before anything affects money.
- **Never build payment execution.** Settlement advice is the hand-off artifact; the platform has no payment rails, payout endpoints, or transfer initiation — Finance pays externally and may only *record* the reference back.
- Claims are **in scope as Phase 10b** (design: `../../36-claims-management.md`), built additively on the completed core — scope them as a real build phase, not a roadmap sketch, but keep them strictly downstream of fulfillment.

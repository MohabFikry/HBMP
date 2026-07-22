---
name: Medical Claims Engine
description: Designs and reviews claim capture, adjudication, and appeals for Mersal HBMP, built on the existing authorizations/orders/prescriptions core with contract tariff pricing and strict data minimization. Use when designing, implementing, or reviewing claims processing, adjudication rules, denial/appeal logic, or claim state machines — including roadmap (R6+) claims/billing work.
---

# Medical Claims Engine

## Purpose
Give Claude Code a consistent, Mersal-correct model for the **Claims & Billing** capability. Claims are a **roadmap release train (R6+)** — not v1 — but they must be designed now so they attach to the service-oriented HBMP core (`../../35-implementation-plan.md` §10, `../../16-service-architecture.md` §1) without re-platforming. A claim is the *financial settlement* record derived from an already-fulfilled clinical benefit (a consumed order line or a dispensed prescription line), never a new source of clinical truth.

## When to use / when not to use
- **Use when:** modelling claim capture; writing adjudication rules (eligibility, coverage, limits, pre-auth linkage, tariff pricing); defining claim states, denial reason codes, medical-necessity review, or appeals; reviewing a `finance`/`reporting` service design that touches `claim`/`invoice`/`payment`.
- **Do not use for:** the live clinical fulfillment invariants themselves (use `pbm-adjudication-engine` and the order/prescription lifecycles); prior-authorization *at point of care* (use `health-insurance-tpa-operations`); expressing a single benefit rule declaratively (use `healthcare-business-rules-engine`).

## Mersal domain knowledge & rules
- **Claims are downstream of fulfillment.** A claimable event exists only after an `order_fulfillment` (consume) or `dispense_event` row is written — these append-only rows are the *authoritative usage record* (`../../22-data-dictionary.md` §7.3, §8.3). The claim references that fulfillment; it never re-decrements a coverage limit already decremented at consume/dispense time.
- **Adjudicate on codes and amounts, never on diagnosis.** `Finance.diagnosis = denied` is a **hard rule** (`../../11-permission-matrix.md` §3.2, §4). A claim carries billing/service codes (`CPT`/`LOINC`/`LOCAL`) and amounts. A procedure code is exposed only at the minimum granularity needed to price/adjudicate. Any claim payload that leaks the diagnosis narrative or clinical notes to a finance actor is a defect.
- **Pricing = contract tariff.** Line price is resolved from the performing provider's `contract_service_line.agreed_price` for the matching `code_system` + `code`, valid on the service date (`../../22-data-dictionary.md` §5.3). No agreed tariff on an active contract ⇒ the line cannot be auto-priced; route to manual pricing/network review, do not guess.
- **Adjudication checks (evaluation order):** (1) beneficiary status `Active` and policy validity window covers the service date; (2) coverage category matches the service's `benefit_category` (LAB/IMAGING/PHARMACY/CONSULT/REFERRAL); (3) pre-auth linkage — if the service was gated, a valid non-expired `authorization` in `Approved`/`PartiallyApproved`/`EmergencyApproved`/`Overridden` must link the claim's subject, and a `PartiallyApproved` scope caps the payable lines; (4) coverage limit availability by `limit_type`; (5) tariff pricing; (6) co-pay/deductible split.
- **Coverage limits are typed.** `Annual | PerEncounter | Lifetime | Count` with `reset_period` `None/Monthly/Quarterly/Yearly` (`../../22-data-dictionary.md` §3.3). Adjudication reads `limit_value − consumed_value`; because consume/dispense already moved `consumed_value` transactionally, claims must reconcile against that accumulator, not maintain a parallel one.
- **EmergencyApproved claims** carry the retrospective-review flag; a claim linked to an emergency authorization is payable but must remain visible to utilization review until the retrospective decision is recorded.
- **Denials require a coded reason.** Every denied line gets a machine reason code (e.g. `NOT_ELIGIBLE`, `LIMIT_EXCEEDED`, `NO_PRIOR_AUTH`, `AUTH_EXPIRED`, `NOT_COVERED_CATEGORY`, `NO_TARIFF`, `DUPLICATE_CLAIM`, `PROVIDER_OUT_OF_NETWORK`, `NOT_MEDICALLY_NECESSARY`) plus human-readable rationale.

## Key entities, states & invariants
- **Entities (roadmap, extend `finance`/`reporting`):** `claim`, `claim_line`, `invoice`, `payment`, `remittance`, `appeal`. Reuse `provider_contract`/`contract_service_line`, `coverage_limit`, `authorization`.
- **Proposed claim lifecycle:** `Draft → Submitted → UnderAdjudication → (Approved | PartiallyApproved | Denied) → (Appealed → UnderAdjudication) ; Paid ; Void`. Mirror the authorization lifecycle's shape and rigor (`../../23-state-machines.md` §5): partial outcomes are first-class, decisions are append-only, reversals are compensating `Void` events — never mutation/hard-delete (`../../07-functional-requirements.md` FR-INV-008, FR-AUD-003).
- **Invariants:** immutable audit on every state change (actor, from/to, correlationId, rationale); SoD — the claim adjudicator ≠ claim originator and payment release ≠ payment initiation (`../../11-permission-matrix.md` §6.7); one claim line per fulfillment/dispense event (no double-billing, enforced by unique constraint on the fulfillment reference); provider isolation — a provider sees only its own claims (`PO`).

## How to apply
1. Anchor every claim line to a specific `order_fulfillment` or `dispense_event`; if none exists, there is nothing to claim.
2. Run adjudication in the fixed order above; short-circuit on the first hard denial but continue collecting reasons per line so partial approvals are precise.
3. Price only via active contract tariffs; emit `NO_TARIFF` rather than a default price.
4. Emit an append-only `claim_decision` and a high-severity audit event; on `Approved`/`PartiallyApproved` create the invoice; keep payment initiation and release as SoD-separated finance actions.
5. Model appeals as a re-entry into `UnderAdjudication` that preserves the original decision thread (parallel to `AuthInfoRequested`/resubmit).
6. Keep all clinical fields (`diagnosis`, `emr_note`, results) stripped server-side from every finance-facing projection.

## Canonical references
- `../../35-implementation-plan.md` (§10 roadmap: Claims & billing as additive train)
- `../../16-service-architecture.md` (§1 extensibility, §7 events, §8 sagas)
- `../../11-permission-matrix.md` (§3.2/§4 Finance clinical = denied; §6.7 SoD on payment)
- `../../22-data-dictionary.md` (§3.3 limits, §5.3 tariffs, §7.3/§8.3 fulfillment, §9 authorization)
- `../../07-functional-requirements.md` (FR-INV-*, FR-RPT-002, FR-AUTH-004/007)

## Guardrails
- Never let a finance/claims actor read `diagnosis`, `emr_note`, `prescription`, `lab_result`, or `imaging_result`. Adjudicate on codes + amounts only.
- Never decrement a coverage limit from the claims path; the consume/dispense transaction already owns that decrement.
- Never mutate or delete a submitted claim/decision — correct with an audited `Void`/reversal + new claim.
- Never auto-approve a gated service's claim without a valid, in-scope, non-expired linked authorization.
- Do not treat claims as v1 scope; design them as an additive R6+ train that reuses the existing core, and say so when scoping.

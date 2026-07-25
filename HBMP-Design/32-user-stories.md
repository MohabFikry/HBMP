# 32 — User Stories & Acceptance Criteria

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [31-product-backlog.md](31-product-backlog.md) · [07-functional-requirements.md](07-functional-requirements.md) · [13-ux-flows.md](13-ux-flows.md) · [23-state-machines.md](23-state-machines.md)

Detailed user stories with **Given/When/Then** acceptance criteria for the highest-value features. Every story inherits two global acceptance criteria (not repeated each time):

- **G-A11y:** the screen meets the accessibility gate in [21-accessibility-checklist.md](21-accessibility-checklist.md) (keyboard, screen reader, non-color status, AR/EN RTL, ≥44px targets).
- **G-Audit:** every create/update/decision/consume/dispense/export writes an immutable audit event ([19-audit-strategy.md](19-audit-strategy.md)) and honors the minimum-necessary field rules ([11-permission-matrix.md](11-permission-matrix.md)).

Priority: M/S/C. Format: **US-nnn** · role · story · AC. Module-scoped stories use a prefixed id — **US-CLM-nnn** for Claims Management ([36-claims-management.md](36-claims-management.md)).

---

## Phase 1 — Registration

**US-001 (M) — Register by any identifier**
*As a Registration officer, I want to register a beneficiary using National ID, Passport, Refugee ID, UNHCR number, or Member number so that anyone eligible can be enrolled regardless of documents held.*
- Given a new beneficiary, When I enter any one supported identifier, Then the system accepts it, validates format, and stores it with its type and issuing authority.
- Given an identifier that already exists, When I submit, Then the system warns and offers to open/merge the existing record rather than creating a duplicate.
- Given required personal/contact fields are missing, When I try to proceed, Then inline + summary errors block progress and name each missing field.

**US-002 (M) — Upload & validate documents**
*As a Registration officer, I want to upload supporting documents so that eligibility can be verified.*
- Given a file, When it exceeds size or is a disallowed type, Then it is rejected with a clear reason.
- Given an accepted file, When uploaded, Then it is malware-scanned and attached to the beneficiary with timestamp and uploader.

**US-003 (M) — Approve & activate registration**
*As a Beneficiary Management approver, I want to review and approve registrations so that only eligible people are activated.*
- Given a Pending registration, When I approve, Then status becomes Active, a Member number is issued, and an eligibility snapshot is created.
- Given incomplete info, When I choose Request Info, Then it returns to the officer with my notes and stays Pending.
- Given ineligibility, When I Reject, Then a reason is mandatory and recorded.

**US-004 (S) — Manage status lifecycle**
*As a Beneficiary Management officer, I want to suspend, expire, block, or reactivate a beneficiary so that coverage reflects reality.*
- Given an Active member, When I suspend/block with a reason, Then eligibility checks immediately reflect the new status.
- Given a Suspended member, When I reactivate, Then history records both transitions.

## Phase 2 — Eligibility

**US-010 (M) — Reception eligibility search**
*As a Reception officer, I want to search by ID/Passport/Card/Policy/Phone so that I can confirm eligibility fast.*
- Given a valid identifier, When I search, Then I see a result card with status, coverage, remaining limits, and a visit-history summary within 2s (p95).
- Given no match, When I search, Then I get an empty state suggesting another identifier or registration.
- Given my Reception role, When I view the card, Then no EMR/diagnosis data is shown.

**US-011 (M) — Status-driven visit gating**
*As a Reception officer, I want the system to block visit creation for ineligible members so that ineligible care isn't rendered.*
- Given Expired/Suspended/Blocked/Inactive, When I try to create a visit, Then it is blocked with guidance (e.g., refer to Case Manager).
- Given Active, When I proceed, Then an encounter is created and appears in the clinician queue.

## Phase 3 — Appointments

**US-020 (M) — Book scheduled appointment**
*As an Appointment coordinator, I want to book against doctor availability so that patients get a slot.*
- Given available slots, When I select one, Then it is reserved and confirmed (status Scheduled).
- Given no availability, When I search, Then I'm offered the next slots or a waitlist.

**US-021 (M) — Reschedule / cancel**
- Given a Scheduled appointment, When I reschedule, Then the old slot is released and the new one confirmed, both audited.
- Given a cancellation, When confirmed, Then the slot is released.

**US-022 (S) — No-show handling**
*As an Appointment coordinator, I want to mark no-shows so that slots are reused and patterns tracked.*
- Given a passed appointment not checked-in, When I mark No-show, Then it's recorded, the slot can be backfilled, and reporting captures it.

## Phase 4 — Consultation / EMR

**US-030 (M) — Treating-only clinical access**
*As a Doctor, I want to open only patients I am treating so that access stays minimum-necessary.*
- Given a patient not assigned to me, When I try to open the record, Then access is denied and the attempt is audited.
- Given an assigned patient, When I open the encounter, Then I see summary, history, diagnoses, allergies, vitals, and medication history.

**US-031 (M) — Create SOAP note + diagnosis**
- Given an open encounter, When I record SOAP and select an ICD-10 diagnosis, Then it is saved to the EMR with author and timestamp.
- Given a required field missing, When I save, Then errors are shown and save is blocked.

**US-032 (M) — Create investigation/radiology order**
*As a Doctor, I want to order labs/imaging so providers can fulfil them.*
- Given a diagnosis/context, When I create an order, Then it enters Requested; if high-cost it routes to Approvals (PendingApproval), else becomes Active and available to authorized providers.
- Given an Active order, Then it is discoverable by the authorized provider only.

**US-033 (M) — Create e-prescription**
- Given an encounter, When I prescribe, Then a prescription is created (Draft→Submitted) with lines, quantities, and (future) interaction/allergy alerts surfaced.
- Given an expensive drug requiring approval, When I submit, Then it routes to Approvals before becoming dispensable.

**US-034 (S) — Create referral**
- Given a need for another provider, When I create a referral, Then it enters Requested and can be accepted/scheduled, closing the loop back to me on completion.

## Phase 5 — Lab & Imaging

**US-040 (M) — Provider finds authorized orders**
*As a Lab technician, I want to search active orders so I can fulfil them.*
- Given an order for my facility, When I search by patient identifier or order number, Then I see only lines authorized for my provider and cannot see prescriptions.
- Given an order not for me / already used, When I search, Then it does not appear as available.

**US-041 (M) — Atomically consume an order line**
*As a Lab technician, I want to consume an order line exactly once so duplicate usage is impossible.*
- Given an Active line, When I consume it, Then it is locked and marked consumed in a single atomic transaction.
- Given the line is already consumed, When I attempt to consume again, Then I get "already used" and no state changes (idempotent, duplicate-proof).
- Given I consume some of several lines, Then the order becomes PartiallyUsed and remaining lines stay Active.

**US-042 (M) — Upload result & complete**
- Given a consumed line, When I upload a result and attach a report, Then it is stored and routed to the ordering doctor/approvals.
- Given all lines fulfilled, Then the order becomes Completed.

## Phase 6 — Pharmacy

**US-050 (M) — Find a prescription**
*As a Pharmacist, I want to search prescriptions by Rx/Patient/Policy/Passport/Member so I can dispense.*
- Given a valid search, When it matches a dispensable Rx, Then I see its lines and remaining quantities and cannot see investigation results.
- Given an expired or completed Rx, When I open it, Then dispensing is rejected with the reason.

**US-051 (M) — Partial dispense with batch/expiry**
*As a Pharmacist, I want to dispense partially and record batch/expiry so records are accurate and the remainder stays available.*
- Given remaining quantity, When I dispense ≤ remaining with batch + expiry, Then a dispense event is recorded and remaining decremented.
- Given I dispense less than prescribed, Then the Rx becomes PartiallyDispensed and the remainder stays available for later.
- Given full dispensing, Then the Rx becomes Dispensed.

**US-052 (S) — Substitution / out-of-stock**
- Given an approved alternative, When I substitute, Then the substitution and reason are recorded.
- Given out-of-stock, When I flag it, Then the out-of-stock workflow is triggered and the beneficiary/prescriber can be notified.

## Phase 7 — Approvals

**US-060 (M) — Review & decide authorization**
*As a Medical Approval reviewer, I want to review requests with full clinical context so I can decide correctly.*
- Given a submitted request, When I open it, Then I can see EMR, clinical notes, and supporting documents.
- Given my decision, When I Approve/Reject/Partial/Request-info, Then reviewer, timestamp, decision, and rationale are recorded; rejection reason is mandatory.

**US-061 (S) — Emergency / override**
*As a Medical Approval reviewer, I want emergency approval with break-glass so urgent care isn't delayed.*
- Given an emergency, When I issue an emergency approval/override, Then extra justification is required and a break-glass audit entry is written.

**US-062 (S) — Manual authorization**
*As a Medical Approval reviewer, I want to create an authorization directly for a member without a provider submission.*
- Given a member search, When I create a manual authorization, Then it is valid, linked to the member, and fully audited.

## Phase 10b — Claims Management

Design: [36-claims-management.md](36-claims-management.md) · Backlog: EPIC-13 in [31-product-backlog.md](31-product-backlog.md). Post-v1; priorities are MoSCoW *within Phase 10b*. Note throughout: the Claims Officer adjudicates on **codes and amounts only** — clinical narrative is stripped server-side.

**US-CLM-001 (M) — Review a batch and decide lines**
*As a Claims Officer, I want to open a claim batch and record a decision on every line so that we pay only what was authorized, delivered, and covered.*
- Given a batch in UnderReview, When I open a line, Then I see service code + description, service date, provider/branch, billed amount, contract price, system recommendation with reason codes, linked authorization, fulfillment reference, and supporting documents.
- Given a line, When I choose Approve / Partially approve / Deny / Adjust / Request info / Route to clinical review, Then the decision is written append-only with decider, timestamp, allowed amount, reason codes, rationale, rule version, and correlation id.
- Given a partial approval, When I enter an allowed amount, Then it must be ≤ the lower of billed amount and contract tariff unless I record an explicit override with justification.
- Given some lines are undecided, When I try to move the batch to Decided, Then it is blocked and the undecided lines are listed.
- Given every line is decided, When I close review, Then the batch becomes Decided and rollup totals (claimed → priced → approved → adjustments → net payable) are recomputed.
- *Notes:* line-level decisions always roll up to the batch; a decision is never editable, only superseded by an adjustment or void.

**US-CLM-002 (M) — Create a batch by date range, branch, group, or manual selection**
*As a Claims Officer, I want to assemble claims into a batch several ways so that settlement matches how each provider actually bills.*
- Given a service-date range, When I create a batch, Then all eligible unbatched claims in that range for the selected payee are included and the count/total is shown before I confirm.
- Given a provider branch or a provider group (parent across branches), When I create a batch, Then only that branch's / that group's claims are included.
- Given a filtered worklist, When I hand-pick claims, Then a manual batch is created from exactly that selection.
- Given a claim already in an Open or UnderReview batch, When I try to add it to another, Then it is rejected as already batched (a claim belongs to at most one open batch).
- Given an Open batch, When I remove a claim, Then it is audited; Given the batch is UnderReview, Then removal additionally requires a reason and is audited as an exception.
- Given a new batch, Then it is numbered `BAT-<yyyy>-<base32(8)>` and is provider-homogeneous for settlement.

**US-CLM-003 (M) — See automated pre-adjudication with coded reasons**
*As a Claims Officer, I want the system to pre-check every line and show me all applicable reasons so that my decision is fast and precise.*
- Given a submitted claim, When pre-adjudication runs, Then each line gets a recommendation ∈ {RecommendApprove, RecommendPartial, RecommendDeny, RequiresManualReview} plus **all** applicable reason codes — not just the first failure.
- Given eligibility, coverage category, authorization linkage/scope, fulfillment linkage, duplicates, network/contract effectivity, tariff, limits and co-pay are checked, When any fails, Then the matching coded reason (e.g. `NOT_ELIGIBLE`, `POLICY_EXPIRED`, `NOT_COVERED_CATEGORY`, `NO_PRIOR_AUTH`, `AUTH_EXPIRED`, `EXCEEDS_AUTH_SCOPE`, `NO_FULFILLMENT_RECORD`, `DUPLICATE_CLAIM`, `PROVIDER_OUT_OF_NETWORK`, `CONTRACT_NOT_EFFECTIVE`, `LIMIT_EXCEEDED`) is attached with a human-readable explanation.
- Given no contract tariff exists for the code on the service date, When pricing runs, Then the line is flagged `NO_TARIFF` and routed to manual pricing — the system never guesses a price.
- Given a recommendation, When I view the line, Then it is clearly labelled as a *recommendation*; nothing is auto-final for gated, high-value, reimbursement, or RequiresManualReview lines.
- Given a decision is recorded, Then the **rule version** used is stored with it.

**US-CLM-004 (M) — Deny with a mandatory coded reason**
*As a Claims Officer, I want denial to require a reason code and rationale so that every refusal is defensible and appealable.*
- Given a line I intend to deny, When I submit without a reason code, Then it is blocked with an inline error.
- Given a reason code, When rationale free text is empty, Then submission is blocked (rationale is mandatory for deny, adjust, and override).
- Given a completed denial, Then it appears on the settlement advice and in the payee's statement with the coded reason, and is available as an appeal basis.

**US-CLM-005 (M) — Adjust a line (price, quantity, deduction, recovery)**
*As a Claims Officer, I want to record adjustments rather than edit amounts so that the financial trail is append-only and reconstructable.*
- Given an adjudicated line, When I record a `PriceCorrection`, `QuantityCorrection`, `Deduction`, `Recovery`/`Clawback`, `Writeoff`, `Reversal`/`Void`, or `Reallocation`, Then a new signed, coded adjustment row is written with amount delta and mandatory rationale — the original amounts are never mutated.
- Given a `Recovery`, When I save, Then it must reference the original claim line it recovers against, and it nets into a later batch's rollup.
- Given adjustments would drive a batch's net payable below zero, When I submit, Then it is blocked pending an explicit dual-controlled approval.
- Given any adjustment, Then the audit event records before/after amounts.

**US-CLM-006 (M) — Route a line to clinical review**
*As a Claims Officer, I want to hand a medical-necessity question to a clinical reviewer so that I never have to read clinical content myself.*
- Given a line where necessity is in question, When I route it to clinical review, Then its state becomes ClinicalReview and it leaves my decision queue.
- Given I am a Medical Approval reviewer / Director, When I open a routed line, Then I see the clinical context and can record an opinion — but not a payment decision.
- Given an opinion is recorded, Then the line returns to UnderAdjudication with the opinion visible to me as a conclusion only, with no clinical narrative exposed.

**US-CLM-007 (M) — Provider submits a claim batch with documents**
*As a Provider billing user, I want to submit my invoice and supporting documents so that Mersal can adjudicate and settle what I delivered.*
- Given an invoice and documents, When I submit, Then each file is type/size validated and malware-scanned before acceptance.
- Given submitted lines, When matching runs on (provider, beneficiary, service code, service date, authorization), Then matched lines proceed with the billed amount recorded alongside the contract price.
- Given a line with no corresponding fulfillment record, When matching runs, Then it is flagged `NO_FULFILLMENT_RECORD` and routed to manual assessment — never auto-approved.
- Given I am a provider user, When I browse claims/batches/statements, Then I see only my own provider's data and receive a 403 (audited) on anything else.

**US-CLM-008 (M) — Beneficiary reimbursement with OCR pre-fill**
*As a beneficiary (or Reception/Case Manager acting for them), I want to submit paid receipts and proof of service so that I can be reimbursed for care I paid for out of pocket.*
- Given receipts plus result/dispense evidence, When I submit against an existing authorized prescription or investigation order, Then a reimbursement request is created and the documents are scanned, encrypted, and stored in document-service.
- Given the documents, When OCR runs (Arabic + English), Then candidate provider, date, amount, currency and drug/service codes are extracted, each with a **confidence score** and the source document region.
- Given high-confidence extraction that auto-matches an authorized order/prescription, When matching completes, Then claim lines are pre-filled and flagged `AUTO_MATCHED` — still requiring human confirmation before any money is affected.
- Given no authorized underlying order/prescription (and no explicitly allowed non-gated category), or missing proof the service was rendered, When I submit, Then the request is rejected with the reason.
- Given a decision, Then the reimbursement is capped at the contract tariff or the receipt amount, whichever is lower, unless the officer records an explicit override with justification.
- Given the request, Then no bank/payout details are stored on the claim — payout runs through Mersal's existing finance process.
- *Notes:* OCR is assistive, never authoritative.

**US-CLM-009 (M) — Manual assessment of low-confidence reimbursements**
*As a Claims Officer, I want low-confidence or ambiguous OCR results queued for me so that no money moves on a machine guess.*
- Given OCR confidence below threshold, or an ambiguous/no match, When extraction finishes, Then the request lands in the manual-assessment queue and never in auto-match.
- Given a queued request, When I open it, Then I see the document with the OCR overlay and each extracted field's confidence, and I can correct any value by hand.
- Given I confirm a match, Then the claim lines are created from the confirmed values and my confirmation is audited against the original extraction.

**US-CLM-010 (M) — Reconcile billed vs delivered**
*As a Claims Officer, I want a worklist of discrepancies between what we recorded, what was billed, and what was approved so that nothing is over- or under-paid.*
- Given a period and payee, When I open reconciliation, Then each item is bucketed as matched, billed-not-delivered, delivered-not-billed, price variance, quantity variance, or duplicate.
- Given a billed-not-delivered item, When I act on it, Then I can deny it or route it for provider clarification, always with a coded reason.
- Given a price variance, When I act on it, Then I can raise a `PriceCorrection` adjustment (US-CLM-005) directly from the worklist.
- Given aged delivered-not-billed items, Then they are reportable as aged unbilled.

**US-CLM-011 (M) — Settlement advice generated and exported**
*As a Claims Officer, I want an immutable settlement advice per payee so that Finance has one authoritative hand-off artifact.*
- Given a batch reaching Decided, When settlement advice is issued, Then an immutable document is generated with header (payee, period, batch no, generated-by/at), per-claim/line detail (approved, adjusted, denied with reason codes), and totals ending in **net payable**.
- Given the advice is issued, Then rollup totals are frozen and the document is stored in the WORM bucket in document-service and referenced from the batch.
- Given I export, When I choose CSV/XLSX (finance) or PDF (provider), Then the export contains **no clinical fields** and the export itself is audited.
- Given the advice, Then **no payment is executed by the platform** — Finance/treasury pays externally and may record a payment reference back against the batch.

**US-CLM-012 (M) — Appeal a decision**
*As a Provider billing user (or a beneficiary via Case Manager), I want to appeal a denial or partial so that genuine errors can be corrected.*
- Given an Approved / PartiallyApproved / Denied claim, When an appeal is lodged with a stated basis, Then the claim moves to Appealed and then back to UnderAdjudication for re-adjudication.
- Given re-adjudication, Then the original decision remains visible and intact; the new outcome is an additional append-only decision, never an overwrite.
- Given an already-settled batch, When an appeal succeeds, Then the correction is applied as an adjustment carried into a later batch, not by reopening the settled one.

**US-CLM-013 (M) — Segregation of duties on claim decisions**
*As a Compliance/Finance lead, I want the system to prevent self-review so that claims cannot be approved by an interested party.*
- Given a claim I created or submitted, When I try to decide it, Then the action is blocked and the attempt is audited (originator ≠ adjudicator).
- Given I am affiliated with the claiming provider, When I open its claims to decide, Then the decision controls are unavailable and the attempt is audited.
- Given an override above the configured value threshold, When I submit it, Then a second approver must confirm before it takes effect (dual control).
- Given adjudication, Then settlement release is a separate permission from adjudication.

**US-CLM-014 (M) — Claims Officer cannot read clinical data** *(authorization test)*
*As a Security/DPO owner, I want it proven that claims staff cannot reach clinical content so that minimum-necessary holds in the money workflow.*
- Given any claims screen or API projection, When it is returned to a Claims Officer, Then it contains no diagnosis, no EMR/clinical note, no lab/imaging **result value**, and no prescription clinical detail — stripped **server-side**, not hidden in the UI.
- Given a Claims Officer calls an EMR/result endpoint directly (deep link or API), When authorization is evaluated, Then it returns 403 and the attempt is audited.
- Given a claims line needs proof the service was rendered, When I view it, Then I see only result **existence** — that a result exists, its date, and its document reference — never its content.
- *Notes:* implemented as automated authorization tests per [26-testing-strategy.md](26-testing-strategy.md); `Finance/Claims → diagnosis = denied` is a hard rule ([11-permission-matrix.md](11-permission-matrix.md)).

**US-CLM-015 (S) — Claims KPIs & dashboards**
*As a Finance/Operations manager, I want claims KPIs so that I can manage cycle time, leakage, and provider behaviour.*
- Given claims data, When I open the dashboard, Then I see claims TAT (submission→decision), approval/denial rate, top denial reasons, adjustment value by type, provider variance league table, reimbursement OCR auto-match and manual-assessment rates, batch cycle time, aged unbilled, and recovery outstanding.
- Given any chart, Then an accessible data-table alternative is available and no clinical fields appear.

## Cross-cutting

**US-070 (M) — Secure login with MFA**
*As any internal/provider user, I want SSO + MFA so access is secure.*
- Given valid credentials + MFA, When I sign in, Then I land on my role's portal only.
- Given inactivity beyond the timeout, When I return, Then I'm warned and re-authenticated.

**US-071 (M) — Role-scoped portal & data**
*As a user, I want to see only my portal and permitted data so minimization holds.*
- Given my role, When I navigate, Then I only see routes/data my permissions allow (e.g., Finance sees no diagnoses).
- Given a deep link I can't access, When I open it, Then I get a 403 with a request-access affordance, audited.

**US-072 (S) — Notifications & alerts**
- Given a relevant event (order available, approval decision, result ready), When it occurs, Then the right role receives an in-app (and email) notification.

**US-073 (S) — Operational dashboard**
*As a Medical Director/Manager, I want KPIs so I can monitor operations.*
- Given data, When I open the dashboard, Then I see clinic workload and key KPIs, each with an accessible data-table alternative.

**US-074 (M) — Manage users, roles & master data**
*As a Super Admin, I want to manage users/roles and master data so the platform stays correct.*
- Given admin rights, When I assign roles or update ICD/CPT/Drug master data, Then changes take effect and are audited; segregation-of-duties is enforced.

---

### Definition of Ready / Done
- **Ready:** story has role, value, AC, priority, dependencies, and links to the relevant FR/flow/state-machine.
- **Done:** AC pass, G-A11y + G-Audit met, tests (unit/integration/E2E as relevant) green, min-necessary verified, demoed.

### Cross-references
- Backlog: [31-product-backlog.md](31-product-backlog.md) · Functional reqs: [07-functional-requirements.md](07-functional-requirements.md)
- Flows: [13-ux-flows.md](13-ux-flows.md) · Lifecycles: [23-state-machines.md](23-state-machines.md) · Sprints: [33-sprint-roadmap.md](33-sprint-roadmap.md)

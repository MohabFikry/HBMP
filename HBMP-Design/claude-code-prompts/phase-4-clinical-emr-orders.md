# Phase 4 — Clinical EMR, Investigation Orders & E-Prescriptions

**Goal:** Deliver the clinician's consultation slice: an **emr-service** (encounter, SOAP notes, vitals, allergies, diagnoses, medication history) with **ABAC treating-relationship** enforcement and FHIR R4 mapping; an **orders-service** that creates investigation/radiology orders whose lines reference validated CPT/LOINC master data and route high-cost items to approvals; and prescription creation + referral creation in **pharmacy-service**. This is release **R2** and the doctor-facing half of the platform. Phases 5 (lab/imaging fulfillment) and 6 (pharmacy dispensing) consume what this phase produces.

Back to master list: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> Root `CLAUDE.md` already defines the stack, repo layout, naming, security, audit, testing, and Definition of Done. **Do not restate it.** This file adds phase-4 scope only.

---

## Skills to activate
> Activate `clinical-workflow-designer`, `healthcare-business-rules-engine`, `pbm-adjudication-engine` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

Open and follow these before writing code:

- [`../07-functional-requirements.md`](../07-functional-requirements.md) — EMR, ordering, and prescribing functional requirements.
- [`../15-database-erd.md`](../15-database-erd.md) — `emr`, `orders`, `pharmacy` schema relationships.
- [`../22-data-dictionary.md`](../22-data-dictionary.md) — §6 (encounter, emr_note, diagnosis, vital, allergy, medication_history), §7 (investigation_order, order_line), §8 (prescription, prescription_line). Column types, enums, and validation are authoritative.
- [`../23-state-machines.md`](../23-state-machines.md) — §2 Investigation Order, §3 Prescription, §4 Referral, §6 Encounter lifecycles. Use the canonical states **exactly**.
- [`../24-sequence-diagrams.md`](../24-sequence-diagrams.md) — order-create/route-approval and prescribe sequences.
- [`../32-user-stories.md`](../32-user-stories.md) — US-030, US-031, US-032, US-033, US-034.
- Reference (do not re-read fully): [`../11-permission-matrix.md`](../11-permission-matrix.md) for field-level min-necessary, [`../18-security-model.md`](../18-security-model.md) for ABAC attributes.

Master data (loaded in phase 0b): ICD-10, CPT, LOINC, Drug/ATC, allergens — reachable via **masterdata-service**. Never hardcode clinical codes; validate against masterdata.

---

## Prompts

### 4.1 — `emr-service`: encounter, clinical documentation, ABAC treating-relationship

```text
Build the emr-service (.NET 8, `emr` schema, schema-per-service + RLS) exposing REST /api/v1 with OpenAPI 3.1.

READ FIRST: ../22-data-dictionary.md §6, ../23-state-machines.md §6, ../11-permission-matrix.md, ../32-user-stories.md US-030 and US-031.

Entities & tables (match ../22 §6 exactly — columns, enums, validation):
- encounter (encounter_no ENC-YYYY-NNNNNN; status InProgress/Finished/Cancelled; encounter_class enum).
- emr_note (SOAP: subjective/objective/assessment/plan; note_type SOAP/Progress/Nursing; is_signed boolean).
- vital (vital_type BP/HR/Temp/SpO2/Weight/Height/BMI; value_num with per-type range validation; loinc_code optional, validated vs masterdata LOINC when present).
- allergy (allergen_id validated vs masterdata.allergen; severity Mild/Moderate/Severe; status Active/Inactive/Resolved).
- diagnosis (icd_code MUST exist in masterdata.icd_code — reject unknown codes with RFC7807; diagnosis_rank Primary/Secondary; clinical_status Active/Resolved/Recurrence).
- medication_history (drug_id vs masterdata.drug; source Prescribed/SelfReported/External; status Active/Stopped).

Behaviour:
- Signable SOAP note: signing sets is_signed=true and LOCKS the note (immutable thereafter; any edit attempt rejected). Unsigned notes are editable by their author only. Corrections after signing require an addendum note, never in-place edit.
- Diagnosis/vital/allergy validation calls masterdata-service; cache validated code lookups in Redis; fail closed if masterdata is unreachable for a write.
- All mutations write immutable hash-chained audit_event via the shared audit client (before/after minimized). Soft-delete + *_history only; no hard delete.

ABAC — treating-relationship (the core access rule, US-030):
- A doctor may read/write a patient's EMR ONLY if a treating relationship exists (assigned via encounter provider_id / appointment / care-team). Enforce at the policy engine (OPA/Cerbos) AND with row-level filters — do not rely on UI.
- Attempted access to a non-treated patient returns 403 and writes an audit event (attempted PHI access). PHI reads are themselves audited.
- Approval-team role may read EMR/notes/reports (per ../11); reception/labs/pharmacy/finance may NOT read clinical notes/diagnoses.

FHIR R4 mapping (align, do not fork the internal model): expose read projections mapping
encounter→Encounter, diagnosis→Condition, vital→Observation, allergy→AllergyIntolerance
(medication_history→MedicationStatement where practical). Keep the FHIR representation as a read/interop projection over the canonical tables; document in OpenAPI.

Acceptance criteria (Given/When/Then):
- US-030: Given a patient NOT assigned to me, When I open the record, Then access is denied (403) and the attempt is audited. Given an assigned patient, When I open the encounter, Then I see summary, history, diagnoses, allergies, vitals, and medication history.
- US-031: Given an open encounter, When I record SOAP and select a valid ICD-10 diagnosis, Then it is saved with author + timestamp. Given a required field missing OR an unknown ICD-10 code, When I save, Then errors are shown and save is blocked.
- Given a signed SOAP note, When I attempt to edit it, Then the edit is rejected and only an addendum is allowed.

Tests: unit (validation, sign-lock), integration (masterdata validation, encounter->note->dx flow), AUTHORIZATION tests proving a non-treating doctor is denied and reception cannot read notes/diagnoses. Contract test for FHIR projections. Coverage ≥80% on domain logic.
```

### 4.2 — `orders-service`: investigation/radiology orders with code validation + approval routing

```text
Build the orders-service (.NET 8, `orders` schema + RLS) exposing REST /api/v1 + OpenAPI 3.1.

READ FIRST: ../22-data-dictionary.md §7, ../23-state-machines.md §2, ../24-sequence-diagrams.md (order create/route), ../32-user-stories.md US-032.

Entities (match ../22 §7 exactly):
- investigation_order (order_no ORD-YYYY-NNNNNN; order_type Lab/Imaging/Procedure; status per §11; expires_at validity window; links encounter_id, beneficiary_id, ordering_provider_id, optional authorization_id).
- order_line (code_system CPT/LOINC/LOCAL; code MUST exist in masterdata for its system; quantity_ordered > 0; quantity_consumed accumulator with CHECK 0 ≤ consumed ≤ ordered; status Active/PartiallyUsed/Completed/Cancelled).

Canonical order lifecycle (../23 §2 — use exactly):
Requested → PendingApproval → (Approved | Rejected) → Active → PartiallyUsed → Completed; plus Expired, Cancelled.

Create-order behaviour (POST /investigation-orders):
- Validate the ordering doctor has a treating relationship to the beneficiary (ABAC, same rule as emr-service) and the encounter is valid.
- Validate every line code against masterdata (CPT/LOINC); reject unknown codes with RFC7807 problem+json.
- Order starts at Requested. Then route: if any line is a HIGH-COST / gated service (cost/policy rule — start with a masterdata/policy cost threshold, config-driven), transition Requested→PendingApproval and emit OrderPendingApproval (approvals-service picks it up in phase 7). Otherwise auto-activate Requested→Active and emit OrderActivated.
- Emit domain events via the OUTBOX pattern in the same transaction as the state change (OrderCreated, then OrderPendingApproval or OrderActivated). Consumers dedupe on event id.
- An Active order (and its lines) is discoverable by AUTHORIZED providers only (enforced in phase 5); do not leak orders to unauthorized facilities.
- All mutations audited (hash-chained); soft-delete + history; no hard delete.

Acceptance criteria (US-032):
- Given a diagnosis/context, When I create an order, Then it enters Requested; if high-cost it routes to Approvals (PendingApproval), else becomes Active and available to authorized providers.
- Given an Active order, Then it is discoverable by the authorized provider only.
- Given a line code not present in masterdata, When I submit, Then the order is rejected with a clear problem+json error.

Tests: unit (routing decision, code validation, status transitions), integration (masterdata validation, outbox → event emission, OrderActivated vs OrderPendingApproval), authorization test (non-treating doctor cannot create an order for the beneficiary). Assert OrderActivated is emitted exactly once via outbox.
```

### 4.3 — `pharmacy-service`: e-prescription creation + referral creation

```text
Extend pharmacy-service (.NET 8, `pharmacy` schema + RLS) with PRESCRIPTION CREATION (dispensing is phase 6). Add referral creation (approvals/referral domain).

READ FIRST: ../22-data-dictionary.md §8, ../23-state-machines.md §3 (Prescription) and §4 (Referral), ../32-user-stories.md US-033 and US-034.

Prescription entities (match ../22 §8 exactly):
- prescription (rx_no RX-YYYY-NNNNNN; status per §11; expires_at; links encounter_id, beneficiary_id, prescriber_id, optional authorization_id).
- prescription_line (drug_id vs masterdata.drug; dose/route/frequency; quantity_prescribed > 0; quantity_dispensed accumulator CHECK 0 ≤ dispensed ≤ prescribed; refills_allowed ≥ 0; status Active/PartiallyDispensed/Dispensed/Cancelled).

Canonical prescription lifecycle (../23 §3 — use exactly):
Draft → Submitted → (Approved | Rejected) → PartiallyDispensed → Dispensed; plus Expired, Cancelled.

Create/submit behaviour (US-033):
- Prescriber must have a treating relationship to the beneficiary (ABAC).
- Each line references a drug validated against masterdata (drug_id/ATC); dose/route/frequency/quantity captured.
- Draft on create; Draft→Submitted on submit (RxCreated then RxSubmitted via outbox).
- Routing: if a line contains an EXPENSIVE / gated drug (config-driven policy/cost threshold), route to approvals (stays Submitted, awaiting decision — approvals-service in phase 7 approves before dispensable). Otherwise it may auto-approve per policy. It becomes dispensable only once Approved.
- DRUG-INTERACTION & ALLERGY ALERTS (ADVISORY, non-blocking): using masterdata (drug interactions + the beneficiary's allergies from emr-service), surface alerts at prescribe time. Alerts are advisory — the prescriber may proceed with an acknowledged override, which is recorded. Do NOT hard-block.
- All mutations audited; soft-delete + history.

Referral creation (US-034), Referral lifecycle ../23 §4 (Requested → Accepted → Scheduled → Completed; + Cancelled, Expired):
- Create referral in Requested with target specialty/provider, linked to encounter/beneficiary; emit ReferralRequested (outbox). Acceptance/scheduling and loop-closure are downstream.

Acceptance criteria:
- US-033: Given an encounter, When I prescribe, Then a prescription is created (Draft→Submitted) with lines/quantities and interaction/allergy alerts surfaced. Given an expensive drug requiring approval, When I submit, Then it routes to Approvals before becoming dispensable.
- US-034: Given a need for another provider, When I create a referral, Then it enters Requested and can be accepted/scheduled later, closing the loop back to me on completion.

Tests: unit (routing, alert surfacing, override recording, status transitions), integration (masterdata drug validation, allergy alert using emr allergy data, outbox events RxCreated/RxSubmitted/ReferralRequested), authorization test (non-treating prescriber denied). Min-necessary: pharmacy prescription views must not expose investigation results.
```

---

## Guardrails

- **Treating-relationship is enforced and tested** in emr-service, orders-service, and pharmacy-service — at the policy engine AND row level. A non-treating clinician is denied (403) and audited. This is US-030 and non-negotiable.
- **Every clinical code is validated against master data** — ICD-10 (diagnosis), CPT/LOINC (order lines), Drug/ATC (prescription lines), allergen. Unknown codes are rejected with RFC 7807. Never hardcode codes.
- **Canonical states only** (../23 §2, §3, §4, §6). No ad-hoc statuses. Illegal transitions are rejected and audited as `TransitionDenied`.
- **Signed SOAP notes are immutable** — corrections via addendum, never in-place edit.
- **Drug-interaction/allergy alerts are advisory** in this phase — surfaced, acknowledgeable, recorded; not blocking.
- **Outbox for all events** (OrderActivated, OrderPendingApproval, RxSubmitted, ReferralRequested); consumers idempotent.
- **Immutable hash-chained audit** on every mutation and PHI read; soft-delete + history; no hard delete.
- **Min-necessary at field level** — reception/labs/pharmacy/finance never read clinical notes or diagnoses; approval team may.

## Done when

- A doctor with a treating relationship can open an assigned patient's encounter, record a SOAP note, and add a validated ICD-10 diagnosis; a non-treating doctor is denied and audited.
- The doctor can create an investigation order whose CPT/LOINC lines are validated; a high-cost line routes the order to **PendingApproval** (emits OrderPendingApproval), a normal one auto-activates (emits **OrderActivated**).
- The doctor can create an e-prescription (Draft→Submitted) with drug-validated lines, sees advisory interaction/allergy alerts, and an expensive drug routes to approvals before it is dispensable; a referral can be created in **Requested**.
- Authorization tests prove treating-only access and that non-clinical roles cannot read notes/diagnoses; unit/integration tests green; outbox events assert-once; OpenAPI + service READMEs updated; audit events present for all mutations. Global Definition of Done (root `CLAUDE.md`) met.

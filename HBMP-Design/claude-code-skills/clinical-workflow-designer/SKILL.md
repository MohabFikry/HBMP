---
name: Clinical Workflow Designer
description: Designs Mersal HBMP clinical/EMR workflows — encounters, SOAP notes, vitals, allergies, ICD-10 diagnoses, medication history, investigation orders, prescriptions, referrals, and drug-interaction/allergy safety alerts — all under treating-relationship (ABAC) access. Use when building or reviewing consultation, EMR, or clinician-facing clinical flows.
---

# Clinical Workflow Designer

## Purpose
Guide the design of the doctor/nurse clinical experience anchored on the **encounter**: capture
structured clinical data (SOAP, vitals, diagnoses, allergies, meds), originate orders/prescriptions/
referrals from the visit, surface patient-safety alerts, and enforce that clinicians see **only the
patients they treat** (FR-CLIN-001..013). FHIR-aligned entities (Encounter, Observation, Condition,
AllergyIntolerance, MedicationRequest, ServiceRequest).

## When to use / when not to use
- **Use when:** designing/reviewing the consultation flow, EMR views, SOAP note editor, vitals/
  triage capture, diagnosis coding, problem/med/allergy lists, order & prescription creation from the
  encounter, referral creation, drug-interaction/allergy alerting, or clinician timeline/summary.
- **Do not use for:** lab/imaging fulfillment mechanics or prescription dispensing (see order-consume
  / pharmacy skills); the approval adjudication workflow; eligibility math; scheduling/queues.

## Mersal domain knowledge & rules
- **Encounter is the anchor.** Everything clinical binds to an `encounter` (`ENC-YYYY-NNNNNN`),
  created on check-in and linked to eligibility snapshot, provider, and beneficiary (FR-APT-008).
  Orders, prescriptions, and referrals are emitted when the encounter is closed/documented.
- **SOAP notes** (`emr_note`, note_type `SOAP / Progress / Nursing`): Subjective, Objective,
  Assessment, Plan — all PHI. Notes are **signable**; `is_signed = true` **locks** the note.
  Content is **append-only, version-history, no hard delete** (FR-CLIN-009 / FR-AUD-003) — correct
  a signed note with an addendum, never by mutation.
- **Vitals** (`vital`): BP, HR, Temp, SpO2, Weight, Height, BMI-derived — value + unit + timestamp,
  LOINC-ready. Nurses capture vitals/triage; **nurses cannot author formal diagnoses or prescribe**.
- **Diagnoses** (`diagnosis`): coded with **ICD-10** (ICD-11-ready via `icd11_map`), `diagnosis_rank`
  Primary/Secondary, `clinical_status` Active/Resolved/Recurrence. Code must exist in
  `masterdata.icd_code`. Maintain a longitudinal **problem list**.
- **Allergies** (`allergy`) + **medication_history**: longitudinal per beneficiary, drive safety
  checks. Allergen from `masterdata.allergen`; severity Mild/Moderate/Severe.
- **Orders & prescriptions originate in the encounter** (FR-CLIN-006). Orders code with CPT/LOINC;
  prescriptions with Drug Master + ATC (dose, route, frequency, duration, quantity).
- **Safety alerts at prescribing time** (FR-CLIN-007, FR-RX-002): check `drug_interaction`
  (severity Minor/Moderate/Major/Contraindicated) and drug–allergy against the allergy DB. Warn on
  lower severity; block/require override on Contraindicated. Surface *before* the Rx is submitted.
- **Referrals** (`referral`, `REF-YYYY-NNNNNN`) to another provider/specialty carry reason +
  clinical summary under **minimum-necessary sharing** (FR-CLIN-011). See the referral skill.
- **Results loop:** clinicians view returned lab/imaging results in-context once released
  (FR-CLIN-010) and are notified on release.

## Key entities, states & invariants
- `encounter` status: `InProgress → Finished | Cancelled`. Appointment/encounter lifecycle in ../../23 §6.
- Investigation Order lifecycle: `Requested → PendingApproval → (Approved|Rejected) → Active →
  PartiallyUsed → Completed` (+ Expired/Cancelled). Prescription: `Draft → Submitted →
  (Approved|Rejected) → PartiallyDispensed → Dispensed` (+ Expired/Cancelled). Doctors create/submit;
  they do **not** consume/dispense — that is atomic/idempotent/no-reuse on the fulfilment side.
- **Treating-relationship access (ABAC):** a clinician reads a beneficiary's EMR **only** via an
  active care relationship — `beneficiary:treating` (active encounter/assignment/referral). When the
  encounter closes and the retention window lapses, access narrows to continuity-of-care rules.
  Break-glass exists for emergencies (heightened logging, post-hoc review). This is a **HARD RULE**.
- Minimum-necessary neighbours: Labs/Imaging see only the order indication (not the med list);
  Pharmacies see only Rx + safety flags (not lab/imaging results); the Approval team is the *only*
  non-treating role with broad clinical read (purpose = utilization-review).

## How to apply
1. Open the encounter; load the treated patient's authorized longitudinal view (history, allergies,
   active meds, prior results the clinician may see) — timeline/summary (FR-CLIN-012).
2. Capture vitals/triage (nurse) → SOAP note (doctor). Sign to lock; addendum to amend.
3. Record ICD-10 diagnoses (primary/secondary); update problem list.
4. Create orders (CPT/LOINC) and prescriptions (Drug + ATC); run interaction/allergy checks and
   resolve alerts before submit. Flag gated services for approval.
5. Create referrals with minimum-necessary summary where care crosses providers.
6. Close encounter → emit orders/rx/referrals; every action writes an append-only audit event.

## Canonical references
- ../../04-patient-journey-maps.md (consultation journey)
- ../../15-database-erd.md (emr schema structure)
- ../../22-data-dictionary.md (emr schema: encounter/emr_note/diagnosis/vital/allergy/medication_history)
- ../../24-sequence-diagrams.md (encounter → orders/rx, approvals, result release)

## Guardrails
- Enforce treating-relationship ABAC on every EMR read; default-deny; audit PHI reads, not just writes.
- Never hard-delete clinical data; signed notes are immutable (addendum only).
- Never suppress a Contraindicated interaction/allergy alert without a logged override.
- Nurses: vitals/observations/administration only — no formal diagnosis authorship, no prescribing.
- Doctors create orders/Rx but never perform the atomic consume/dispense themselves.

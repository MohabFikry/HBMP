# pharmacy-service

E-**prescriptions**, **referrals** (Release R2, Phase 4.3 — US-033/US-034) and **dispensing** (Release R3, Phase 6 —
US-050/051/052). Owns the `pharmacy` schema. A treating doctor prescribes drug-validated medications with advisory
interaction/allergy alerts and raises referrals; a **pharmacist** searches dispensable prescriptions and performs the
**atomic idempotent dispense** with batch/expiry and policy-approved substitution.

## Prescribe (US-033)

`POST /api/v1/prescriptions` (scope `rx:write`, `Idempotency-Key` required):

1. **Treating-relationship gate** — the prescriber must treat the beneficiary (row-level truth from emr-service's
   `GET /treating-relationship`, enforced by the shared authorization engine over `PharmacyPolicies`). Non-treating
   → **403** + audit.
2. **Drug validation** — each line's `drug_id` must exist in masterdata (`/drugs/by-id/{id}/exists`); unknown →
   **422**; fail-closed.
3. **Advisory alerts (non-blocking)** — the prescription's drugs are screened for **drug interactions**
   (masterdata `/drug-interactions/check-by-ids`) and **allergy conflicts** against the beneficiary's allergies
   (pulled from emr `GET /beneficiaries/{id}/allergies`, checked in masterdata `/allergies/check-by-ids`). Alerts
   are surfaced in the response and **recorded** (`prescription_alert`) with the prescriber's acknowledgement
   (`acknowledgeAlerts`) — they never hard-block. Best-effort: a screening service outage yields no alert.
4. **Draft → Submitted**, then **route** (`RxRoutingPolicy`, config `Pharmacy:Routing`): an expensive/gated drug
   keeps it **Submitted** awaiting an approvals decision (dispensable only once **Approved**, phase 7); otherwise
   it **auto-approves** to **Approved**. `dispensable` in the response reflects this.
5. **Outbox** — `RxCreated`, `RxSubmitted` (+ `RxApproved` when auto-approved) to `pharmacy.events`, in the same
   transaction as the state change; consumers dedupe on event id.

Idempotent on `Idempotency-Key`; every mutation audited. `GET /api/v1/prescriptions/{id}` (treating-gated),
`POST /api/v1/prescriptions/{id}/cancel` (legal only while not fully dispensed → audited `409` otherwise).
**Min-necessary:** prescription views never expose investigation results.

## Refer (US-034)

`POST /api/v1/referrals` (scope `referral:write`) — a treating doctor raises a referral to a target specialty /
provider; it enters **Requested** and emits `ReferralRequested`. Acceptance / scheduling / loop-closure are
downstream (the appointments flow already emits `ReferralScheduled` when a `REF-*` appointment is booked).
Idempotent; treating-gated; audited. `GET /api/v1/referrals/{id}`.

## Dispensing — search / dispense / substitution / out-of-stock (phase 6)

A **pharmacist** whose token carries a `provider_id` (their pharmacy) dispenses. Every dispensing endpoint is
provider-scoped by the shared engine's **provider-ownership** ABAC rule (`DispensingGate` over
`ProviderPolicies.QueueRead` / `PharmacyPolicies.Dispense`). There is **no** treating gate — a pharmacist doesn't
treat the patient and a prescription is dispensable network-wide. **Min-necessary:** a pharmacist never sees
investigation results — this service exposes none, and the pharmacy policy bundle grants no order/result action.

- `GET /prescriptions/search?rxNo=|patientIdentifier=|policyNo=|passport=|memberNo=` (scope `pharmacy:read`,
  US-050) — returns only **dispensable** prescriptions (status `Approved`/`PartiallyDispensed`, within the validity
  window, with ≥1 line still remaining), projected (`DispensableRxView`) to dispensing-relevant fields only (drug,
  dose/route/frequency, **remaining qty**, patient id) — never diagnoses/notes. `patientIdentifier` is the
  beneficiary id; policy/passport/member numbers resolve to a beneficiary via patient-service (`IBeneficiaryResolver`,
  fail-safe). PHI reads audited.
- `GET /prescriptions/{id}/dispensing` (scope `pharmacy:read`) — open one Rx for dispensing; the **reject rule**
  (23 §3) returns an audited `409` with a clear reason when the Rx is Expired/Cancelled/Rejected/fully-Dispensed.
- `POST /prescriptions/{rxId}/lines/{lineId}/dispense` (scope `pharmacy:dispense`, **`Idempotency-Key` required**,
  US-051) — **the atomic, idempotent, duplicate-proof dispense** (`DispenseExecutor`, the medication analogue of
  orders' `ConsumeExecutor`). Body `{quantity, batchNo, expiryDate, substitutedDrugId?, substitutionReason?}`. Three
  mechanisms combine, all required: (1) an append-only `dispense_event` insert keyed by a **UNIQUE idempotency key**;
  (2) **optimistic concurrency** on the prescription line's `xmin` — the dispense `UPDATE` lands only if the line
  hasn't moved, so exactly one of N racers wins; (3) a **required `Idempotency-Key`** — replaying it returns the prior
  dispense_event with no new row. The DB `CHECK (0 ≤ dispensed ≤ prescribed)` is the final backstop. A fully-dispensed
  line can **never** be dispensed again (`409`); an **expired lot** or an **expired/cancelled/rejected/dispensed Rx**
  is rejected. Partial dispense → line/Rx `PartiallyDispensed`, remainder stays **available** for a later visit; all
  dispensed → `Dispensed`. `RxLinesDispensed` (+ `RxDispensed`) emit via the outbox atomically with the state change.
- **Substitution** (US-052) — a `substitutedDrugId` is honored only when it is a **policy-approved alternative** for
  the prescribed drug (`IFormularyService` → masterdata approved-alternatives; a clearly-marked, swappable stand-in
  for a future external PBM). Off-list → **not dispensed**; instead `RxSubstitutionRoutedToApproval` is emitted and a
  `409` returned. An approved substitution records `substituted_drug_id` + reason on the `dispense_event` and audits.
- `POST /prescriptions/{rxId}/lines/{lineId}/out-of-stock` (scope `pharmacy:dispense`) — **flags** OOS without
  consuming the line (accumulator untouched, quantity stays available); emits `RxLineOutOfStock` to notify the
  prescriber + beneficiary (delivered by notification-service in phase 8) and audits.

## Domain

- `prescription` (`RX-YYYY-NNNNNN`; status per §3; xmin RowVersion) + `prescription_line` (`drug_id` →
  masterdata.drug, `quantity_prescribed > 0`, `quantity_dispensed` accumulator `CHECK (0 ≤ dispensed ≤ prescribed)`,
  `refills_allowed ≥ 0`, xmin RowVersion for the dispense guard).
- `dispense_event` (append-only, 22-data-dictionary §8.3 + `expiry_date` extension) — one immutable row per dispense:
  `quantity`, `idempotency_key` **UNIQUE** (the dedup anchor), `batch_no`, `expiry_date`, optional
  `substituted_drug_id` + `substitution_reason`, `dispensed_by`. Never updated or soft-deleted; history in `audit_event`.
- `referral` (`REF-YYYY-NNNNNN`; status per §4) + `prescription_alert` (recorded advisory alerts).
- Canonical lifecycles: Rx `Draft → Submitted → (Approved|Rejected) → PartiallyDispensed → Dispensed` (+ Expired,
  Cancelled); Referral `Requested → Accepted → Scheduled → Completed` (+ Cancelled, Expired). Dispense rules live in
  `Dispensing` (23 §3 "Pharmacy-specific guards"); substitution gate in `SubstitutionPolicy`.

## Data

- `Infrastructure/Migrations/0001_pharmacy.sql` — `rx_seq`/`referral_seq`, `prescription` (unique `rx_no`,
  partial idempotency index, enum CHECK), `prescription_line` (accumulator CHECK), `prescription_alert`,
  `referral`, `processed_request`.
- `Infrastructure/Migrations/0002_dispense.sql` — `dispense_event` (UNIQUE `idempotency_key`, `quantity > 0` CHECK,
  `batch_no`, `expiry_date`, substitution columns, FK to `prescription_line`, indexes).

masterdata gained by-id screening endpoints (`/drug-interactions/check-by-ids`, `/allergies/check-by-ids`); emr
gained a treating-gated `GET /beneficiaries/{id}/allergies`.

## Tests

- `PrescriptionWorkflowTests` — the §3 transition table, cancel guard, dispensable-only-when-approved, key formats.
- `RxRoutingAndAlertTests` — gated/high-cost → approval vs auto-approve; advisory alerts recorded, override
  required, never blocking.
- `PharmacyAuthzTests` — a treating doctor may prescribe/refer; a non-treating prescriber is denied + audited; a
  pharmacist cannot prescribe (default-deny) — against the real authorization engine over `PharmacyPolicies`.
- `PharmacyIntegrationTests` — prescription + lines and referral round-trip with routed status; monotonic sequence
  issuer; DB enforces the dispense accumulator invariant (env-gated `PHARMACY_TEST_DB`; green against live PG).
- `DispensingRulesTests` — the pure dispense rules (23 §3): partial → PartiallyDispensed (remainder available), full
  → Dispensed, no-reuse, over-dispense, expired lot, non-dispensable Rx / past validity window; substitution allowed
  only for a policy-approved alternative (`SubstitutionPolicy`).
- `DispensingAuthzTests` — a pharmacist may read/dispense only its OWN pharmacy's work (provider-ownership); it cannot
  reach another pharmacy's scope; a doctor cannot dispense; and — proving pharmacies ≠ investigation results — the
  pharmacy bundle grants a pharmacist no order-result read at all (against the real engine over `PharmacyPolicies`).
- `DispenseConcurrencyTests` (env-gated `PHARMACY_TEST_DB`, real parallel PG transactions, **not mocked**) — N racers
  on one line → **exactly one wins**, `quantity_dispensed` never exceeds prescribed, **one** dispense_event row;
  replaying an Idempotency-Key adds no row and returns the original; partial-then-remainder → Dispensed; a used line
  cannot be reused; an expired lot is rejected with no state change; an approved substitution + batch/expiry pin onto
  the dispense_event. Serialized via the `pharmacy-db` collection so the many-connection race doesn't collide.

Endpoint wiring (treating gate → 403, drug validation → 422, advisory alerts, auto-approve vs approval routing,
dispense/search auth → 403, reject rule → 409, idempotent replay, substitution routing, outbox, audit) is exercised
against the live stack.

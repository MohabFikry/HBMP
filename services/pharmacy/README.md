# pharmacy-service

E-**prescriptions** and **referrals** (Release R2, Phase 4.3 — US-033/US-034). Owns the `pharmacy` schema. A
treating doctor prescribes drug-validated medications with advisory interaction/allergy alerts and raises
referrals. This service covers **create/submit + routing**; **dispensing** (partial, batch/expiry, substitution)
is phase 6.

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

## Domain

- `prescription` (`RX-YYYY-NNNNNN`; status per §3; xmin RowVersion) + `prescription_line` (`drug_id` →
  masterdata.drug, `quantity_prescribed > 0`, `quantity_dispensed` accumulator `CHECK (0 ≤ dispensed ≤ prescribed)`
  for phase-6 dispense, `refills_allowed ≥ 0`).
- `referral` (`REF-YYYY-NNNNNN`; status per §4) + `prescription_alert` (recorded advisory alerts).
- Canonical lifecycles: Rx `Draft → Submitted → (Approved|Rejected) → PartiallyDispensed → Dispensed` (+ Expired,
  Cancelled); Referral `Requested → Accepted → Scheduled → Completed` (+ Cancelled, Expired).

## Data

- `Infrastructure/Migrations/0001_pharmacy.sql` — `rx_seq`/`referral_seq`, `prescription` (unique `rx_no`,
  partial idempotency index, enum CHECK), `prescription_line` (accumulator CHECK), `prescription_alert`,
  `referral`, `processed_request`.

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

Endpoint wiring (treating gate → 403, drug validation → 422, advisory alerts, auto-approve vs approval routing,
idempotent replay, outbox, audit) is exercised against the live stack.

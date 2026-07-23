# emr-service

Clinical EMR service. **Phase 2.3 is a thin stub**: status-driven **visit gating** + encounter creation
(US-011, Release R1). Full SOAP / diagnoses / investigation orders / prescriptions arrive in phase 4.
Owns the `emr` schema.

## Visit gating (US-011)

`POST /api/v1/encounters` (scope `encounter:write`, `Idempotency-Key` required):

1. Reads member status from eligibility-service (`GET /eligibility/members/{id}/status`), forwarding the
   caller's bearer token.
2. **Gate** (`VisitGate`, 23-state-machines §1): only `Active` may start a visit. `Expired / Suspended /
   Blocked / Inactive / Pending` (and unknown members) are **blocked** with RFC 7807 `422` + actionable
   guidance — **nothing is persisted**.
3. **Active** → creates an `Encounter` shell (`ENC-YYYY-NNNNNN`, status `InProgress`) + a clinician
   **queue entry** (so the patient appears on the doctor's worklist), marks the appointment checked-in
   where one is supplied, and emits `EncounterStarted` (+ `ApptCheckedIn`).

Creation is **idempotent** on `Idempotency-Key` (a replay returns the existing encounter). Both the gate
decision and the encounter creation are audited.

Other endpoints: `GET /api/v1/encounters/{id}`, `GET /api/v1/encounters/queue` (clinician worklist).

## Data

Migration `Infrastructure/Migrations/0001_emr.sql` — `encounter` (unique `encounter_no`, partial-unique
`idempotency_key`), `queue_entry`, `encounter_seq`. Apply with `psql`.

## Tests

- `VisitGateTests` — parameterized over every member status (Active allowed / all others blocked with
  guidance; director-override + Case-Manager routing).
- `EncounterNoTests` — `ENC-YYYY-NNNNNN` formatting.

Endpoint wiring (gate → 422 / create + queue, idempotent replay, audit, events) is exercised against the
live stack.

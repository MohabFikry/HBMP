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

## Appointments (Phase 3.1 — US-020, 23-state-machines §6)

Scheduling lives in the `emr` schema. Persisted `appointment.status` uses **exactly** the canonical set
`Booked → CheckedIn → Completed` (+ `NoShow`/`Cancelled`); the pre-booking Requested/Waitlisted sub-states
live on `waitlist_entry`. Types: `WalkIn | Scheduled | Referral | FollowUp`.

- `POST /api/v1/appointment-slots` (`appointment:write`) — materialize bookable slots from a **recurring
  availability rule** (`provider + location + doctor`, day-of-week, start/end, `slotMinutes`) over a date
  range. Slot times are Africa/Cairo wall-clock. Idempotent: existing slot definitions are skipped.
- `GET /api/v1/appointment-slots?providerId&locationId&from&to&onlyOpen` (`appointment:read`) —
  minimum-necessary slot list (scheduling fields only); `onlyOpen` hides held/past slots.
- `POST /api/v1/appointments` (`appointment:write`, `Idempotency-Key` required) — **book**. Pass `slotId`
  to hold a specific slot, or omit it (non-walk-in) to auto-take the earliest open slot; walk-ins may be
  slotless. Referral bookings require a `referralRef` (REF-*) and emit `ReferralScheduled`; follow-ups
  require an `originEncounterId`. Returns `201` with the appointment; emits `ApptBooked` + audit.
- `GET /api/v1/appointments/{id}` (`appointment:read`).

**No double-book (critical).** A slot holds at most one **active** (`Booked`/`CheckedIn`) appointment,
enforced in depth: (1) the booking transaction locks the slot row `FOR UPDATE` so concurrent bookers
serialize and an existing hold is detected; (2) the `ux_appointment_active_slot` **partial-unique index**
is the datastore backstop — the losing concurrent `INSERT` raises `23505`, surfaced as **HTTP 409** with
the next available slots. Proven by `AppointmentBookingConcurrencyTests` (12 parallel bookers → exactly one
success). When no slot is free, the caller is offered the next slots or (with `joinWaitlistIfFull`) a
`202` waitlist entry (`ApptWaitlisted`).

### Transitions (Phase 3.2 — US-021/US-022)

All mutating endpoints accept `Idempotency-Key` (replays are no-ops via `emr.processed_request`) and honor
`If-Match` (the `ETag` returned by `GET /appointments/{id}` is the row's `xmin`; a stale value → **412**).
Illegal moves are rejected as an audited **409** `TransitionDenied` per the §6 transition table. "Releasing"
a slot is implicit — it is simply no longer held once the appointment leaves an active status (or moves off
it), because `ux_appointment_active_slot` only counts `Booked`/`CheckedIn`.

- `POST /api/v1/appointments/{id}/reschedule { newSlotId }` — **atomic**: the new slot is acquired
  concurrency-safely and the old slot released in ONE transaction (never both held or both free). Emits
  `ApptRescheduled`.
- `POST /api/v1/appointments/{id}/cancel { reason }` — → `Cancelled`, slot released, reason recorded; a
  freed slot **promotes the waitlist** (`ApptWaitlistPromoted`). Emits `ApptCancelled`.
- `POST /api/v1/appointments/{id}/no-show` — guarded (window passed **and** still `Booked`, never once
  `CheckedIn`): → `NoShow`, sets the `no_show` reporting flag, frees the slot for **backfill** (waitlist
  promotion), and when the beneficiary's no-show tally reaches the threshold emits
  `BeneficiaryNoShowThresholdReached` for **Case Manager** follow-up (05 X3). Emits `ApptNoShow`.

## Data

- `Infrastructure/Migrations/0001_emr.sql` — `encounter` (unique `encounter_no`, partial-unique
  `idempotency_key`), `queue_entry`, `encounter_seq`.
- `Infrastructure/Migrations/0002_appointments.sql` — `provider_availability`, `appointment_slot`,
  `appointment` (+ `appointment_history` twin trigger; partial-unique active-slot and idempotency indexes;
  status/type/linkage CHECKs), `waitlist_entry`.
- `Infrastructure/Migrations/0003_appointment_transitions.sql` — `processed_request` idempotency ledger.

Apply in order with `psql`.

## Tests

- `VisitGateTests` — every member status (Active allowed / all others blocked); director-override +
  Case-Manager routing. `EncounterNoTests` — `ENC-YYYY-NNNNNN` formatting.
- `AppointmentWorkflowTests` — the §6 transition table (legal + illegal), the no-show guard (passed window
  AND still Booked; never once CheckedIn), reschedule/cancel guards, referral/follow-up linkage.
- `SlotGenerationTests` — recurring availability → whole slots within the window, trailing-partial drop,
  weekday matching across a range, bad-input rejection.
- `AppointmentBookingConcurrencyTests` — **no-double-book** proof: parallel bookings at one slot yield
  exactly one success (env-gated `EMR_TEST_DB`; runs green against the live PG).
- `AppointmentTransitionTests` — reschedule atomicity (old freed + new held), cancel release + waitlist
  promotion, no-show guard + reporting flag + backfill, illegal-transition refusal, stale-If-Match `412`,
  and idempotency-ledger replay (env-gated `EMR_TEST_DB`).

Endpoint wiring (gate → 422 / create + queue, booking 409/waitlist, idempotent replay, audit, events) is
exercised against the live stack.

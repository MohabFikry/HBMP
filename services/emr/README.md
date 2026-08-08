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

### The booking pickers, and why they are two reads (14.5)

The booking screen filters on **specialty**, then **doctor**. Both of those facts live in provider-service;
whether a doctor has any free time lives here. Reception holds no `provider:read` — correctly, since that is
the whole network directory — so there were two tempting ways to close the gap, and both are wrong:

- have emr fetch practitioner metadata under a service account and return one rich list — the aggregation
  shape the platform forbids outright (`NoServiceAccountArchitectureTests` in profile-service);
- grant the front desk `provider:read` — contracts, tariffs and tiers along with it.

Instead the scope was sized to the need (`practitioner:read`, identity migration 0018), and the answer is
assembled from **two authorized reads, each from the service that owns the data**:

- `GET /api/v1/booking/doctor-availability?branchId=` (`appointment:read`) — **this** service, from the slot
  table alone, exactly as `/branch-clinics` is: `{ doctorId, branchId, openSlots, nextSlotStart }`. No name,
  no specialty; `DoctorAvailabilityBoundaryTests` pins that shape so the convenience of adding them cannot
  arrive unnoticed.
- `GET /api/v1/practitioners?branchId=&specialtyCode=` (provider-service, `practitioner:read`) — who they are.

The client joins them (`bookableDoctors`) and keeps the intersection, so a doctor with a full calendar and a
doctor who does not work at that branch are both simply not offered.

### The reception dashboard's reads (14.5)

- `GET /api/v1/appointments/summary?date=` (`appointment:read`) — `{ total, checkedIn, noShow }` for one
  Cairo day, branch-scoped exactly like the board. Counted in the database rather than tallied from
  `GET /appointments`, which is capped at 200 rows: on a busy branch a client-side tally would undercount,
  and undercounting is the direction nobody notices.
- `GET /api/v1/appointments?from=&to=` — an INCLUSIVE range of Cairo civil days, each end expanded to its own
  day, so "Sunday to Thursday" includes Thursday's evening clinic (`AppointmentDay.CairoRange`).
- `GET /api/v1/appointment-days` — per-day open-slot counts for the booking calendar.
- `GET /api/v1/appointment-slots?doctorId=` — the chosen doctor's slots only.

**`AppointmentResponse.beneficiaryName`** is populated from the QUEUE TICKET, which captured it at check-in.
So an arrived patient has a name here and a merely-booked one does not — and that asymmetry is the honest
one: emr holds no beneficiary demographics and must not fetch them from a sibling to fill the gap. The
dashboard shows names for today's *visits*, i.e. people who have arrived, which is exactly the set this
covers. Reception seeing the name is a signed-off decision; the masked token remains on the boards that do
not need it. Doctor name and specialty are NOT here — the client joins those from provider-service under
`practitioner:read`.

### Eligibility at booking (14.5)

`POST /appointments` refuses a member who is not **Active** — 422 `urn:hbmp:member-not-active`, with
`BookingGate`'s per-status guidance. The harm being prevented is concrete: a suspended member told they have
an appointment travels to a clinic, often a long way and at their own cost, and is turned away.

`BookingGate` shares its DECISION with `VisitGate` (Active-only) and deliberately differs on WORDING — one
speaks to a desk with the patient present, the other to someone arranging a date weeks out.
`BookingGateTests` asserts both halves, so collapsing them into one call fails the build rather than quietly
telling a call-centre agent to "complete activation before starting a visit".

**Unknown is allowed through**, on the same reasoning as the practitioner probe beside it: `GetStatusAsync`
answers null when eligibility-service is unreachable, and refusing every booking on the platform because one
sibling is briefly down does more harm than an occasional booking for a lapsed member — which check-in and
the visit gate still catch before any care is given. Fail-open is safe *because* the rule is enforced again
at the door.

The booking screen checks too, at the moment of search, and that is a courtesy rather than the control: the
call centre reaches this endpoint through its own façade, and any client skipping the search step would
otherwise book freely.

### The booking note (14.5) — administrative, and deliberately not clinical

`appointment.note` (migration 0011) is a short GENERAL note captured at booking: access needs, an interpreter,
an arrangement. Written by reception or the call centre; read by both plus the treating doctor.

**Why the boundary is enforced rather than documented.** This field is readable across a line the platform
otherwise holds hard — the call centre writes it and a clinician reads it, and the call centre is given no
clinical surface anywhere else (11-permission-matrix; callcentre-service holds no emr scope). A free-text box
spanning that line is exactly where clinical detail accumulates unless something stops it. So:

- `AppointmentNote` caps it at 500 and **refuses** rather than truncating (400 `urn:hbmp:note-too-long`) —
  silently cutting a note loses a tail the operator believes they wrote;
- migration 0011 caps it again as `varchar(500)`, because the API is not the only writer a schema outlives;
- it lives on the **appointment**, not the encounter, so it is not part of the clinical record, does not
  reach the profile seam's encounter projection, and cannot be read with `emr:read` alone
  (`AppointmentNoteTests` pins that);
- the audit event records **`hasNote`, never the text** — copying free text into the hash-chained store would
  make whatever an operator typed permanent and uncorrectable.

No new scope: a caller who may read the appointment may read its note, which is exactly the sharing the three
teams asked for.

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

## Queue + reminders (Phase 3.3)

A **reception walk-in queue** per `(location, provider, optional doctor)`, fed by check-ins and walk-ins,
kept consistent with appointment status.

- `POST /api/v1/appointments/{id}/check-in { memberNo, displayName, priority }` — `Booked → CheckedIn` and
  enqueues a **minimum-necessary** ticket (display identity only). Emits `ApptCheckedIn`.
- `GET /api/v1/queues?locationId&providerId&doctorId` (`appointment:read`) — ordered queue (priority-desc
  then arrival) exposing **only** position / memberNo / display name / type / wait time — **no EMR data**
  (guarded by `QueueMinNecessaryTests`).
- `POST /api/v1/queues/call-next` — pop the head (`Waiting → InConsultation`); `.../requeue`, `.../remove`,
  `.../complete` manage the rest. Cancel/no-show remove a ticket automatically (in the transition service).

**Reminders hook.** `IReminderChannel` has a live **in-app** implementation (enqueues
`AppointmentReminderIssued` for notification-service) and **SMS/WhatsApp stubs** behind the same interface —
no provider integration this phase. `ReminderDispatcher` selects by the beneficiary's preferred channel and
**falls back to in-app**. A reminder fires on booking; `POST /api/v1/appointments/reminders/run?withinMinutes`
sweeps imminent bookings for upcoming reminders.

## Clinical documentation (Phase 4.1 — US-030/US-031, 22-data-dictionary §6.3–6.7)

The clinician's consultation slice. Every endpoint is gated by the **treating-relationship** rule and every
mutation is audited; clinical codes are validated against **masterdata-service** (fail-closed on writes).

**Treating-relationship (US-030).** Access is decided by `ClinicalGate`, which combines the two halves the
guardrail requires: the **row-level** query `ITreatingRelationship` (does the caller own/provide on an encounter
for this beneficiary?) feeds the **policy-level** ABAC condition in the shared authorization engine
(`EmrPolicies` bundle, `libs/authz`). A non-treating clinician gets **403** and the engine writes the attempted-
PHI-access audit event. The **medical-approval** team reads for oversight (distinct `emr:read-oversight` action,
no treating relationship needed); reception / labs / pharmacy / finance have **no** clinical rule → default-deny.

- `GET /api/v1/encounters/{id}/clinical` (`emr:read`) — the full record (encounter + notes + diagnoses + vitals
  + allergies + medication history) for a treating clinician or the approval team.
- `POST /api/v1/encounters/{id}/notes` (`emr:write`) — create a SOAP/Progress/Nursing note (author = caller).
  `PUT …/notes/{noteId}` edits an **unsigned** note (author only). `POST …/notes/{noteId}/sign` **locks** it
  (immutable thereafter → **409** on edit). `POST …/notes/{noteId}/addendum` is the ONLY correction path after
  signing (a new note linked via `addendum_of_note_id`).
- `POST …/diagnoses` — ICD-10 validated vs masterdata; unknown code → **422** problem+json.
- `POST …/vitals` — per-type plausible-range validation (`VitalRange`) + optional LOINC.
- `POST /api/v1/beneficiaries/{id}/allergies` — allergen validated vs masterdata.
- `POST /api/v1/beneficiaries/{id}/medication-history` — drug validated vs masterdata.
- `GET /api/v1/encounters/{id}/fhir` — FHIR R4 **read projection** (Bundle of Encounter / Condition /
  Observation / AllergyIntolerance / MedicationStatement) over the canonical tables — interop only, not a fork.

Clinical rows are **soft-deletable** (`is_deleted`); there is no hard delete. masterdata gained by-id existence
checks `GET /drugs/by-id/{id}/exists` and `GET /allergens/{id}/exists` for drug/allergen validation.

## Data

- `Infrastructure/Migrations/0001_emr.sql` — `encounter` (unique `encounter_no`, partial-unique
  `idempotency_key`), `queue_entry`, `encounter_seq`.
- `Infrastructure/Migrations/0002_appointments.sql` — `provider_availability`, `appointment_slot`,
  `appointment` (+ `appointment_history` twin trigger; partial-unique active-slot and idempotency indexes;
  status/type/linkage CHECKs), `waitlist_entry`.
- `Infrastructure/Migrations/0003_appointment_transitions.sql` — `processed_request` idempotency ledger.
- `Infrastructure/Migrations/0004_queue.sql` — `appointment_queue` (partial-unique active ticket per
  appointment).
- `Infrastructure/Migrations/0005_clinical.sql` — `emr_note`, `diagnosis`, `vital`, `allergy`,
  `medication_history` (canonical-enum CHECKs; `is_deleted` soft delete; addendum self-FK on notes).

- `Infrastructure/Migrations/0019_care_timeline.sql` — `care_timeline`, the care episode (ADR-0031).

Apply in order with `psql`.

## The care episode (ADR-0031)

An appointment is not an event — it is the start of an episode, and almost everything the platform then does
for that patient descends from it. `GET /appointments/{id}/timeline` used to read only `appointment_history`, a
row trigger over the appointment ROW, so it was excellent at "booked, rescheduled, checked in" and
structurally incapable of anything after arrival. A desk asking *"why is this member still here at four
o'clock?"* got a history that stopped two hours before the question.

`emr.care_timeline` is the episode, keyed on the **encounter** with `appointment_id` carried alongside so it
reads from either end. The endpoint merges the two sources newest-first.

Steps arrive two ways:

- **emr's own** — `VisitStarted`, `VitalsRecorded`, `DiagnosisCoded`, `NoteSigned`, `VisitEnded` — staged by
  `CareTimelineWriter` inside the transaction of the thing that caused them. The writer deliberately **does not
  save**: a step that commits separately from its cause is a timeline that can claim a visit ended when it did
  not.
- **From siblings** — orders, pharmacy, approvals — over the `CareFeed` mirror on emr's own queue.
  `CareEpisodeMapping` (pure: no clock, no database) decides what a message means; `CareEpisodeAppender`
  decides what to do about it; `CareEpisodeConsumer` is only transport. The **appointment and the member are
  read from our own encounter row, never from the payload** — the siblings are truthful about
  `beneficiaryId`, but emr owns encounters, so emr is the only service that can be *wrong* about which member a
  visit is for. Dedupe is the `ux_care_timeline_event` unique index; a consumer that restarts has forgotten
  what it processed and the database has not.

One mirrored event maps **conditionally**: `RxSubmitted` is published for every prescription and carries the
routing outcome as a flag, so it becomes `PrescriptionSentForApproval` only when `requiresApproval` is set —
the medication counterpart of `OrderSentForApproval`. A missing or non-boolean flag reads as false: a step is
an assertion, and an unclaimed one should not be made.

**A step is a label, a time, an actor and a business key — never clinical content.** Reception and the call
centre read this timeline, so `DiagnosisCoded` appears and the ICD code does not; `MedicineDispensed` appears
and the drug does not. `CareTimelineTests` and `CareEpisodeMappingTests` both assert it.

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
- `ReminderDispatcherTests` — channel selection honors the preferred channel, falls back to in-app, fires
  the chosen channel, and enforces queue ordering (priority then arrival).
- `QueueMinNecessaryTests` — reflection proof the queue ticket carries no clinical/EMR/PII field.
- `QueueIntegrationTests` — check-in enqueues + ordering, and cancel removes the ticket (env-gated).

Endpoint wiring (gate → 422 / create + queue, booking 409/waitlist, idempotent replay, audit, events) is
exercised against the live stack.

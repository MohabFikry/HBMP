# Phase 3 — Appointments: Scheduling, Reschedule/Cancel, No-Show & Queue

**Goal:** Add appointment scheduling to `emr-service` — walk-in/scheduled/referral/follow-up types, doctor availability and **concurrency-safe slot booking**, reschedule/cancel with slot release, no-show handling, a per-clinic/doctor walk-in queue, and a reminders hook (in-app now; SMS/WhatsApp stubbed for later). (Release **R2**)

Back to [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md).

---

## Skills to activate
> Activate `appointment-queue-management`, `patient-journey-designer`, `healthcare-uiux-designer` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- `../05-business-process-maps.md` — scheduling, queue, and no-show ("X3") process flows and where reminders fire.
- `../07-functional-requirements.md` — R2 appointment/scheduling requirements.
- `../13-ux-flows.md` — booking, reschedule/cancel, and queue UX; reminder touchpoints.
- `../15-database-erd.md` §7 (`appointment`, `encounter`) — appointment fields and status enum.
- `../23-state-machines.md` §6 (Appointment/Encounter lifecycle) — transitions, guards, slot-free side-effects, waitlist promotion.
- `../32-user-stories.md` — **US-020** (book scheduled), **US-021** (reschedule/cancel), **US-022** (no-show).

Root `CLAUDE.md` governs stack, security, audit, a11y, testing, and Definition of Done — not repeated here.

> **Canonical persisted status** (`../15-database-erd.md` §7): `Booked → CheckedIn → Completed`, plus `NoShow` and `Cancelled`. Optional pre-booking scheduling sub-states (Requested/Waitlisted) from `../23-state-machines.md` §6 may be modeled for waitlist promotion but the stored appointment.status uses exactly the canonical set.

---

## Prompts

### 3.1 — Appointment domain: types, doctor availability, slot booking

```text
Implement the appointment domain in emr-service (schema `emr`), per ../15-database-erd.md §7,
../23-state-machines.md §6, ../17-api-specifications.md §6, and US-020.

Scope:
- appointment(appointment_id, beneficiary_id logical-FK, provider_id logical-FK, location_id logical-FK,
  appointment_type, scheduled_start, scheduled_end, status, + standard audit columns + _history twin).
- appointment_type ∈ {WalkIn, Scheduled, Referral, FollowUp}.
- status TEXT with CHECK for exactly: Booked|CheckedIn|Completed|NoShow|Cancelled (new bookings start Booked;
  CheckedIn is reached via the phase-2 gate; Completed when the encounter closes).
- Doctor availability: model availability/slots per provider+location+doctor (recurring availability →
  bookable slots). A slot may hold at most one active appointment.

Booking — POST /api/v1/appointments { beneficiaryId, providerId, locationId, appointmentType, slot/datetime }:
- Reserve the chosen slot and create the appointment in Booked; return 201 with the appointment + slot.
- Referral-type bookings link the referral (REF-*) and transition it to Scheduled (../23 §4) via event.
- FollowUp links the originating encounter.
- If no slot is available, return the next available slots or offer a waitlist entry (US-020).

CONCURRENCY-SAFE booking (critical): two coordinators must never double-book one slot. Enforce with a UNIQUE
constraint on (slot_id) for active bookings (partial index WHERE status IN ('Booked','CheckedIn')) PLUS a
guarded transaction (SELECT ... FOR UPDATE or optimistic row_version). The losing request gets a 409 conflict.

Acceptance (US-020):
- Given available slots, When a coordinator selects one, Then it is reserved and confirmed (status Booked).
- Given no availability, When they search, Then the next slots or a waitlist option are offered.
- Given two coordinators booking the same slot concurrently, When both submit, Then exactly one succeeds and
  the other receives a 409 — never a double-book.

Tests: unit (availability → slots), integration (book + link referral/follow-up), and a CONCURRENCY test firing
parallel bookings at one slot asserting single success. Emit `ApptBooked` (+ `ReferralScheduled` where relevant).
Audit every booking. Update OpenAPI + README.
```

### 3.2 — Reschedule / cancel (release slot) + no-show handling

```text
Implement reschedule, cancel, and no-show for appointments per ../23-state-machines.md §6 and US-021/US-022.

Reschedule — POST /api/v1/appointments/{id}/reschedule (If-Match; Idempotency-Key):
- Book the new slot (reusing 3.1's concurrency-safe path) and RELEASE the old slot in ONE transaction, so a
  reschedule never leaves both slots held or both free. Both the release and the new booking are audited.

Cancel — POST /api/v1/appointments/{id}/cancel { reason }:
- status → Cancelled, RELEASE the slot, reason recorded. Emit `ApptCancelled`; a freed slot triggers waitlist
  promotion if a waitlist exists (../23 §6).

No-show — POST /api/v1/appointments/{id}/no-show:
- Guard: appointment time passed AND not CheckedIn. status → NoShow, free the slot for BACKFILL (promote
  waitlist / open to walk-ins), and set a reporting flag so no-shows are captured for analytics.
- Repeat no-shows should be flagged for Case Manager follow-up (per ../05 X3).

Acceptance:
- (US-021) Given a Booked appointment, When rescheduled, Then the old slot is released and the new one
  confirmed, both audited.
- (US-021) Given a cancellation, When confirmed, Then the slot is released.
- (US-022) Given a passed appointment not checked-in, When marked No-show, Then it is recorded, the slot can be
  backfilled, and reporting captures it.

Tests: integration (reschedule atomicity — old freed + new held), state-machine tests rejecting illegal
transitions (e.g., no-show on a CheckedIn/Completed appt) as audited 409s, and an idempotency test on each
endpoint. Audit every transition. Update OpenAPI + README.
```

### 3.3 — Walk-in queue management + reminders hook

```text
Implement per-clinic/per-doctor queue management and a reminders hook, per ../05-business-process-maps.md,
../13-ux-flows.md §queue, and ../23-state-machines.md §6.

Queue:
- A walk-in queue scoped by (location_id, provider_id/doctor). CheckedIn appointments and walk-ins enter the
  queue in arrival/priority order; support call-next, requeue, and remove.
- Expose GET /api/v1/queues?locationId&providerId returning ONLY minimum-necessary fields (queue position,
  memberNo/display name, appointment type, wait time) — NO diagnoses or EMR data.
- Keep queue state consistent with appointment status: CheckedIn → in queue; encounter start (InConsultation)
  and Completed remove from queue; NoShow/Cancelled remove and free the slot.

Reminders hook:
- Fire reminders on booking and ahead of the appointment. Implement an IReminderChannel abstraction with an
  in-app/notification implementation NOW (via notification-service) and STUB implementations for SMS and
  WhatsApp (interface + no-op/logging adapter) for a future phase — do not build the SMS/WhatsApp providers.
- Respect the beneficiary's preferred_channel (contact table) when selecting a channel.

Acceptance:
- Given CheckedIn/walk-in patients, When Reception views the queue, Then they see an ordered, min-necessary
  list per clinic/doctor and can call-next.
- Given a booked appointment, When it is created (and as it approaches), Then an in-app reminder is emitted via
  the reminders hook; SMS/WhatsApp channels exist as stubs behind the same interface.
- Given an encounter starts or the appointment completes/cancels, Then the entry leaves the queue.

Tests: integration (queue order + call-next + removal on state change), an authorization/min-necessary test on
the queue DTO (no EMR fields), and a reminders test asserting the in-app channel fires and stubs are pluggable.
Audit queue mutations. Update OpenAPI + README.
```

---

## Guardrails

- **No double-booking.** Slot booking is concurrency-safe via a partial UNIQUE constraint on active slot holds **plus** a guarded (`FOR UPDATE` / optimistic `row_version`) transaction; the loser gets a 409. Prove it with a concurrency test.
- **Slot integrity on every transition.** Reschedule releases-and-rebooks atomically; cancel and no-show release the slot; a freed slot drives waitlist promotion/backfill. No orphaned or double-held slots.
- **Audit everything.** Book, reschedule, cancel, no-show, and queue mutations each write an immutable `audit_event` via `libs/audit-client`.
- **Minimum-necessary.** Appointment and queue DTOs expose scheduling/identity fields only — never diagnoses or EMR data.
- **Idempotency on mutations.** Booking, reschedule, cancel, no-show honor `Idempotency-Key`; replays are no-ops.
- **Legal transitions only.** Enforce the `../23-state-machines.md` §6 transition table; reject illegal moves with an audited 409 (`TransitionDenied`).
- **Reminders are pluggable.** In-app now; SMS/WhatsApp behind the same `IReminderChannel` interface as stubs — no provider integration in this phase.

## Done when

- Appointments can be **booked** (Walk-in/Scheduled/Referral/Follow-up) against doctor availability with **concurrency-safe slots** (a proven no-double-book test).
- **Reschedule** atomically frees the old slot and holds the new; **cancel** and **no-show** release slots, no-show sets the reporting flag and enables backfill.
- A **per-clinic/doctor queue** reflects check-in/consult/complete/no-show transitions and exposes only minimum-necessary fields.
- Reminders fire **in-app now** with **SMS/WhatsApp stubs** behind one interface.
- All transitions are audited and unit/integration/concurrency/idempotency tests are green — meeting the root `CLAUDE.md` Definition of Done.

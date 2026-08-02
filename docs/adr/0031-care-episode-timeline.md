# ADR-0031 — the care episode is the correlation key, and it must be traceable end to end

**Status:** Accepted · **Date:** 2026-08-02 · **Supersedes:** nothing
**Extends:** [ADR-0026](0026-patient-profile-server-side-projection.md) (server-side profile projection)

---

## Context

An appointment is not an event. It is the **start of an episode of care**, and almost everything the platform
subsequently does for that patient on that day descends from it: the check-in, the visit, the SOAP note, the
coded diagnosis, the investigation orders, the prescriptions, the authorizations those raise, the results the
lab reports back, and the medicines the pharmacy dispenses.

The platform records every one of those things. It records **none of them in a way that lets you follow one
episode through**.

Two concrete symptoms found this:

1. **`GET /appointments/{id}/timeline` shows appointment status changes and nothing else.** It reads
   `emr.appointment_history`, which a row trigger fills on every insert and update of the appointment row —
   so it can answer "booked, rescheduled, checked in" and cannot answer "and then what happened to the
   patient". A desk asking "why is this member still here at four o'clock?" gets a timeline that stops at
   check-in.

2. **No order, prescription or dispensing event carries the encounter it came from.** `orders.order` and
   `pharmacy.prescription` both hold `encounter_id` as a column, and neither `OrderCreated`, `RxCreated`,
   `RxSubmitted`, `RxLinesDispensed` nor `OrderLinesConsumed` puts it in the payload. So a consumer that
   wanted to assemble an episode has nothing to assemble it *by*. The same gap appeared in the patient
   profile's own order list, where it made "what did this consultation order?" unanswerable — fixed in the
   commit that added `encounterId` to `InvestigationRow` and `RxRow`.

There is a related discovery worth recording because it explains how (1) survived so long: **the visit could
not be ended.** `EncounterStatus.Completed` has been in the enum since phase 1 and `AppointmentWorkflow` has
listed `CheckedIn → Completed` with the comment "encounter closed (phase 4)" since phase 3, and no code path
ever wrote either value. A timeline whose last two steps are unreachable does not look incomplete; it looks
finished.

## Decision

**Anything that originates inside an episode of care carries the episode's key, and emr assembles the
episode.** Concretely:

### 1. The correlation key is `encounterId`, with `appointmentId` as its parent

A visit is the unit a clinician acts in, so the encounter is what downstream work descends from. The
appointment is the episode's parent: booking and check-in happen before an encounter exists, and one
appointment yields at most one encounter. A walk-in has an encounter and no appointment, and its episode is
still whole.

Every event emitted as a consequence of work done in a visit MUST carry `encounterId`. Where the emitting
service also knows the appointment, it carries that too. This is an id and nothing else — it discloses no
clinical content, and a caller who may not read the encounter still may not.

### 2. emr owns the episode timeline

emr owns the appointment and the encounter, so it is the only service that can say what an episode *is*.
It keeps an append-only `emr.care_timeline`, writes its own steps directly, and appends steps for sibling
services from the events they already publish. `GET /appointments/{id}/timeline` merges the appointment's
status history with the episode's steps.

**Not audit-service**, even though it already consumes everything and hash-chains it. audit-service is the
compliance record: it spans every entity, carries before/after states, and reads require `audit:read` —
Security, Compliance, DPO. The desk and the treating clinician need a far narrower thing under the
`appointment:read` they already hold. The same reasoning already separated these two for the appointment's
own timeline; this extends it rather than revisiting it.

**Not the browser.** A client-side join is the endorsed pattern where the caller holds both reads
(`bookableDoctors`, the encounters table's branch labels). An episode is not that shape: it needs the steps
ordered against each other, it spans four services, and a role that may read the appointment frequently may
not hold `orders:read` — so the join would silently lose steps rather than showing an episode with a gap in
it, which is the one thing a timeline must never do.

### 3. A step is a label, a time, an actor and a reference — never clinical content

`"OrderPlaced" · 09:22 · Dr Karim · ORD-2026-000014`. Not the test, not the diagnosis, not the drug. The
timeline is read by reception and the call centre as well as by clinicians, and a step that named the
medicine would put a prescription in front of a desk that is structurally forbidden it. What each reference
resolves to stays behind the owning service's own gate.

### 4. Steps are additive and never rewritten

An episode's history is what happened, so a step is appended and never updated or deleted. A cancelled order
produces an `OrderCancelled` step beside its `OrderPlaced` — it does not remove one.

## The episode, in full

The spine, in the order a patient experiences it:

| Step | Emitted by | Trigger |
|---|---|---|
| `Booked` | emr | appointment created |
| `Rescheduled` / `Cancelled` / `NoShow` | emr | appointment transitions |
| `CheckedIn` | emr | the patient arrives |
| `VisitStarted` | emr | encounter opened |
| `NoteSigned` | emr | SOAP note signed |
| `DiagnosisCoded` | emr | ICD-10 recorded |
| `VitalsRecorded` | emr | vitals captured |
| `OrderPlaced` / `OrderCancelled` | orders | investigation order raised |
| `OrderSentForApproval` | orders | high-cost routing |
| `AuthorizationDecided` | approvals | approve / deny / partial |
| `SampleConsumed` | orders | lab consumes the order line |
| `ResultReported` | orders | result uploaded |
| `PrescriptionWritten` / `PrescriptionCancelled` | pharmacy | prescription written / withdrawn |
| `MedicineDispensed` | pharmacy | lines dispensed |
| `VisitEnded` | emr | encounter closed |

## Status of the implementation

Delivered with this ADR:

- **`VisitStarted` and `VisitEnded` are reachable at all.** `POST /encounters/{id}/complete` closes the
  encounter and moves the appointment `CheckedIn → Completed` in one transaction — see migration 0018 and
  `EndVisitTests`.
- The emr-owned steps above, written directly and merged into `GET /appointments/{id}/timeline`.

Delivered in the second slice — the steps emr does not perform itself:

- **`encounterId` on the wire.** `orders.order` and `pharmacy.prescription` had both held the column since
  phase 4 and neither had ever published it, so the visit and the work it caused were two facts with nothing
  joining them. It is now on `OrderCreated`, `OrderPendingApproval`, `OrderCancelled`, `OrderLinesConsumed`,
  `OrderResultUploaded`, `RxCreated`, `RxCancelled` and `RxLinesDispensed`. approvals-service had no such
  column at all — an authorization knew who was waiting and what it was raised against, and nothing about the
  visit — so migration 0004 adds it and the ingest contract carries it.
- **`CareFeed`, a mirror rather than a subscription.** The transport is point-to-point, so an emr consumer
  bound to `orders.events` would compete with policy-service's benefit accumulator and RabbitMQ would deal
  each event to one of them: half the timeline missing *and* half the coverage accumulator frozen, with
  nothing in either service's log to say so. emr gets its own queue and its own copy, exactly as reporting
  does. `RabbitMqEventPublisher` also now stamps the outbox row's `OccurredAt` on the message, so a step is
  timed when it happened rather than when the relay caught up.
- **`CareEpisodeConsumer`**, splitting transport from judgement: `CareEpisodeMapping` (pure, no clock, no
  database) decides what a message means, `CareEpisodeAppender` decides what to do about it. The appointment
  and the member are read from emr's **own** encounter row and never from the payload — the siblings are
  truthful about `beneficiaryId`, but emr owns encounters, so emr is the only service that can be *wrong*
  about which member a visit is for, and trusting a copy would render a sibling's staleness as this patient's
  history. Dedupe is the `ux_care_timeline_event` unique index; a consumer that restarts has forgotten what it
  processed and the database has not.

Two deliberate omissions inside that slice, both because the alternative misleads:

- **`AuthInfoRequested` is not a step.** It lands on the same append-only decision ledger as the real
  decisions, so it is tempting to treat it as one — but it is a reviewer asking for more information, not an
  answer. A desk shown "authorization decided" stops chasing, and this is the case where somebody must keep
  chasing.
- **The five approvals decisions collapse to one `AuthorizationDecided` step.** Approved, partially approved,
  rejected, overridden and emergency-approved are benefit decisions on a named clinical request. What the desk
  legitimately needs is that the wait ended and the `AUTH-` number to quote; whoever is entitled to the
  outcome opens the authorization, where approvals-service gates it.

Still not delivered:

- **Nothing populates `approvals.authorization.encounter_id` automatically**, because the routing saga that
  would — a consumer of `OrderPendingApproval` / `RxSubmitted` that calls `POST /authorizations` — does not
  exist in this platform yet. The seam is complete and the field is carried end to end, so `AuthorizationDecided`
  works the moment anything ingests an authorization with an encounter on it; until then the *order* records
  that it went for approval (`OrderSentForApproval`) and the decision does not come back. Building that saga
  is order→approval routing, a feature in its own right, not timeline plumbing.

## Consequences

- Four services gain one field in a payload they already emit. None of them gains a read of another
  service's data, and none of them learns anything about the episode beyond the id it was handed.
- emr gains a table that grows with clinical activity. It is append-only and per-episode, so it is bounded by
  what actually happened to one patient on one day.
- A step whose event is lost leaves a gap. The outbox makes delivery at-least-once and consumers dedupe on
  event id, so the failure mode is a duplicate rather than a hole — and the timeline collapses consecutive
  identical steps for exactly this reason, as it already does for appointment status.

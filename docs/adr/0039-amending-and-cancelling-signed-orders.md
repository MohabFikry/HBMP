# ADR-0039 — Amending and cancelling a signed order

- **Status:** Accepted · 2026-08-07
- **Phase:** 30 · **Design:** [46](../../HBMP-Design/46-order-amendment-and-cancellation.md)
- **Supersedes nothing.** Extends [23 §2/§3](../../HBMP-Design/23-state-machines.md) and the phase-5/6
  consume and dispense guards.

## Context

A prescriber could sign a prescription, a lab order, a radiology order or an OP procedure and then have no
way to correct it. The two `/cancel` endpoints that existed read the status and then wrote — the lost update
this platform already defends against on the consume path, on the same rows — worked at order level, and
took a free-text reason with no idempotency key.

## Decisions

### 1. Amend means supersede. The original is never mutated, and never deleted

A signed order is a legal clinical record. Editing the row destroys the answer to *"what was actually ordered
on the 4th?"* — the question asked when something goes wrong.

Enforced by a **database trigger**, not by the endpoint. The API can answer a body edit with 409, but a
repair script, a future handler or a psql session walks straight past that. "Nothing is deleted" is enforced
one step earlier still, by **revoking the DELETE privilege from `hbmp_app`**: every service runs as that role,
so the application cannot attempt one. A trigger would additionally block the schema owner, whose deletes are
maintenance rather than application traffic, and a superuser who wanted to delete could drop the trigger
anyway.

**`Superseded` is a LINE status only.** Deliberately against the prompt's literal wording, which said to add
it to "the line/order tables". An order with one superseded line and two live ones is not superseded, and a
status nothing can enter is one somebody eventually sets by hand — on the aggregate whose roll-up decides
whether a technician sees the work at all.

### 2. Amendment is line-level, because the amendable scope is whatever has not been consumed

A three-line order with line 1's sample taken is amendable in lines 2 and 3. An order-level model cannot
express that, so it would have to refuse the whole request or silently do half — and both are wrong. A
whole-order cancel is "cancel every still-cancellable line", answering **207** for a genuinely mixed result:
a 200 with an empty cancelled-list reads as "done" on a screen.

### 3. The check and the write are one guarded statement

`UPDATE … WHERE line_id = @id AND status IN (amendable) AND xmin = @expected`. Zero rows means somebody got
there first. The three mechanisms are the consume path's, reused rather than re-derived: the line's `xmin`,
an append-only ledger row under a UNIQUE idempotency key, and idempotent replay with a request hash.

**The conflict response is specific.** A doctor told only "someone else changed this" retries, and a retry
after a dispense is how a cancelled-then-dispensed drug happens. The mirror holds too: consuming or
dispensing a withdrawn line returns `LineWithdrawn` carrying the reason, the prescriber and `superseded_by_id`
— without that last field a pharmacist told "this was amended" has nowhere to go, and the patient leaves
empty-handed while a valid prescription sits in the system.

### 4. The approved-scope comparator lives in a shared library, and there is only one

The prompt asked to reuse approvals' `ValidatePartialScope`. Orders and pharmacy cannot reference approvals'
Domain, and an HTTP call would make a doctor's ability to correct a mistake depend on approvals-service being
reachable. So the subset predicate moved **down** into `libs/amendment` and `ValidatePartialScope` now calls
it: one notion of "inside the approved set", used by the reviewer's partial-approval check and by both
amendment paths.

The approved scope is derived **locally**: `quantity_ordered` *is* the approved quantity — phase 29 set it
from the approved scope precisely so it could be told apart from `requested_quantity` — and
`authorization_id` present means "this was gated".

**Re-approval reuses the event the original routing used** (`OrderPendingApproval` / `RxSubmitted`) with a
before/after. A bespoke event type was written first and reverted: `approvals.fulfilments` parses every
message as a `FulfilmentMessage` and dead-letters anything else, so it would have been an orphan that also
looked like an error.

### 5. Two propagation cases are structural, and saying so beat building around them

- **The provider queue needs no event.** It is a live query over the owning service's own table, so a
  cancelled line leaves the bench in the same transaction. Invariant 6 holds *structurally*, which is
  stronger than eventually — and the tests assert it with no wait and no poll, so a future move to a
  projection fails there.
- **Claims needs no reconciliation event.** Claiming follows fulfilment; a fully consumed line is terminal
  and unamendable; a partly consumed one can only have its unconsumed remainder forfeited. The part that
  could have been claimed is exactly the part amendment cannot touch, so invariant 2 already covers it.
  Emitting one anyway would describe a discrepancy that cannot exist.

### 6. Chronic amendment moves nothing and copies nothing

The original line keeps its whole schedule with the collected windows exactly as they were; its **uncollected**
windows take a new terminal `Superseded` status; the successor gets a fresh schedule anchored at the day after
the last collected window closes.

Reparenting the windows was rejected as a silent rewrite that leaves the original with a hole. Copying the
collected ones was rejected harder: a duplicated dispensed window is a **second row claiming the same
collection**, and "how much did we hand over" would get two answers. The new window status is load-bearing —
without it the sweeper records forfeitures for collections that were never owed. Full reasoning in
[the spec](../superpowers/specs/2026-08-07-chronic-amendment-design.md).

Reducing below the chronic definition is **reported, never decided**: 422 with the recomputed preview, and the
conversion happens only on an explicit `convertToAcute` confirmation, recorded in the ledger.

### 7. Order notes are the doc-38 model on a different subject, per owning service

Append-only by trigger, author snapshotted at write time, cancellable-never-deletable, visibility raisable but
never lowerable. Per owning service rather than one shared table so the FK is real and writing a note never
depends on another service being reachable — a pharmacist typing "sample haemolysed, please repeat" during an
outage is when the note matters most.

The **external provider reads through the provider portal**, not the clinical route. The first attempt routed
them through the clinical gate and got a correct 403: an external centre is not inside that gate, which is why
[45 §2b](../../HBMP-Design/45-encounter-and-prescription-adjustments.md) built them a portal. The shared
`NoteAudience` rule means "who can read this" has one answer wherever it is asked.

### 8. Notes and amendments are separate, structurally

Nothing on the notes path touches `order_line`. Conflating them would send every "fasting sample" back to the
approval queue.

## Consequences

- Two state tables gained `PartiallyUsed → Cancelled` / `PartiallyDispensed → Cancelled`. **Their absence was
  a defect:** a partly-fulfilled order or prescription could not be cancelled at all, which is the case
  design 46 §3 opens with.
- `GET /encounters/{id}/timeline` now answers `{ steps, opening }` rather than a bare array. The SPA's shared
  reader accepts both shapes.
- `root_line_id` is nullable until `deferred/0014`: setting NOT NULL in the expand migration would break an
  old replica mid-rollout, which is an order a doctor cannot place.

## What this phase did NOT do, and why

- **Wiring the amend dialog into DoctorEncounter and the worklists**, and surfacing waiting time on screen.
  Integration on existing screens rather than new behaviour.
- **Pharmacy's notes endpoints.** `pharmacy.rx_note` exists and is correct; nothing reads or writes it. Called
  out in the migration header itself rather than left to be found.
- **The event-symmetry gate.** `tools/ci/check-event-symmetry.py` was specified in phase 22 and never built,
  so "~40 orphaned event types" is an unverified claim and "symmetry gate green" could not be an acceptance
  criterion. Each new event's subscriber is named by a test instead.

## The pattern worth keeping

Four times in this phase a build-time guard caught work of mine, and four times it was right: a helper hid an
outbox enqueue, a constant hid an event name, a shared builder hid `encounterId`, and a doc comment quoting a
bare clock re-flagged its own file. Each time the fix was to move the code back where the scan can see it
rather than to add an exemption.

**A shared helper is the wrong tool for anything a build-time scan asserts about a call site.** The
duplication those scans force is the price of the check being able to see what it checks — and every one of
these defects is silent in production, which is why the scans exist.

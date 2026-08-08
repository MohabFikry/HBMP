# 46 — Order & Prescription Amendment and Cancellation

> Back to [00-README-INDEX.md](00-README-INDEX.md) · Siblings: [23-state-machines.md](23-state-machines.md) · [45](45-encounter-and-prescription-adjustments.md) · [19-audit-strategy.md](19-audit-strategy.md)
> Build prompt: [claude-code-prompts/phase-30-order-amendment.md](claude-code-prompts/phase-30-order-amendment.md)

**What this adds.** A prescriber can **cancel or amend** a prescription, lab order, radiology order or OP procedure **after signing it**, for as long as it has not been consumed — and everyone holding that order finds out. Chronic prescriptions can additionally have their **duration and frequency** changed.

---

## 1. You do not edit a signed clinical record — you supersede it

A signed prescription is a legal clinical record and the basis of a dispensing decision. Editing the row in place destroys the answer to "what was actually prescribed on the 4th?" — which is the question asked when something goes wrong.

So **amend means supersede**, following the discipline already used for plan versions and benefit lists:

- The original is marked `Superseded` and **never mutated**.
- A new version is created, carrying `supersedes_id`, a version number, the amending clinician, the timestamp and a **mandatory reason**.
- `Cancel` transitions to `Cancelled` with the same evidence. Nothing is deleted, ever.
- Both remain visible in history and in the per-service history modal ([45](45-encounter-and-prescription-adjustments.md) §4). **A cancelled order is clinically meaningful** — knowing an antibiotic was prescribed and withdrawn two hours later matters.

## 2. The window is "not yet consumed", and the check is a race

"As long as it's not dispensed" is not a state you can read and then act on. Between the doctor's click and the server's write, a pharmacist may have begun dispensing. Checking first and writing second is exactly the lost-update the platform already defends against on the consume path.

**Amendment and cancellation are atomic guarded transitions**, using the same mechanism as consume: a single `UPDATE … WHERE status IN (amendable states) AND row_version = @expected`. Zero rows affected means somebody got there first — and the response must say *what* happened ("line 2 was dispensed at 14:32 by Maadi Pharmacy"), not a generic conflict. A doctor who is told "someone else changed this" and nothing else will simply retry.

The pharmacy side needs the mirror of this: a dispense attempt against a cancelled order fails with the cancellation reason and who cancelled it.

## 3. Partial consumption — amend the remainder, never the whole

The interesting cases are all partial, and the rule is the same in each: **the amendable scope is what has not been consumed.**

| Situation | What may change |
|---|---|
| 3-line prescription, line 1 dispensed | Lines 2 and 3 only. Line 1 is fact |
| Chronic script, window 1 collected, 2–3 pending | The remaining windows. Collected quantity is untouchable |
| 6-session physiotherapy, 4 delivered | Reduce to 4 delivered + 2 cancelled. Delivered sessions stand |
| Lab order, sample already taken | Not amendable — consumption has begun |

Therefore **cancellation and amendment operate at line level**, not order level. A whole-order cancel is simply "cancel every still-cancellable line", and if some lines are already consumed it reports partial success plainly rather than failing the lot or silently doing half.

## 4. Chronic prescriptions: changing duration and frequency

Recomputation follows one principle — **what was dispensed is a fact and is never recalculated**:

1. Dispensed windows keep their quantities exactly as dispensed.
2. The remaining duration is recomputed from the new end date.
3. The remaining quantity is re-allocated across the new remaining windows using the same largest-remainder, highest-first method — **and must still sum exactly to the new total** ([45](45-encounter-and-prescription-adjustments.md) §5).
4. New total may not fall below what has already been dispensed. Asking for that implies un-dispensing; refuse it and say so.

**Reducing duration to a month or less makes the script no longer chronic.** The system must not silently keep a "chronic" script that no longer meets the definition. Either refuse, or convert it to acute with an explicit confirmation from the prescriber — and the conversion is recorded, because a chronic-to-acute change alters the dispensing pattern the patient has been told to expect.

Frequency changes reschedule only the *future* windows; a collected window's dates are history.

## 5. Authorisation: does the amendment stay inside what was approved?

If the order carried an authorisation, the amendment's relationship to the approved scope decides everything:

- **Within the approved scope** — reducing quantity, shortening duration, cancelling a line — the authorisation remains valid and no approver is troubled.
- **Beyond it** — increasing quantity, changing the drug or the service code, extending duration — the authorisation's basis no longer holds. The order returns to *pending authorisation* and the approval team is notified that a previously approved item changed.

Getting this backwards in either direction is costly: treat every amendment as re-approvable and you flood the approval queue; treat none as re-approvable and you have built a way to obtain an approval for one thing and dispense another.

## 6. Propagation — the queue must change, not just a notification fire

"All concerned stakeholders" resolves to a specific list, and each needs a different thing:

| Who | What they need |
|---|---|
| **The fulfilling provider** (pharmacy, lab, radiology, procedure centre) | The item **leaves or changes in their working queue**. This is the urgent one — they may be preparing it now |
| **The approval team** | Notified only when the amendment leaves the approved scope (§5) |
| **The beneficiary** | Told, especially for chronic scripts where they may be travelling to collect |
| **The ordering doctor** | Confirmation, and notification if someone else amended their order |
| **The case manager**, where assigned | The change, as part of coordination |
| **Claims** | If anything was already claimed, the amendment is a reconciliation event, not a silent edit |

**A notification is not propagation.** The failure mode is a cancelled order that still sits in the lab's queue because only an email was sent. So the domain event must be **consumed** by the fulfilment read models, and the notification is additional. Every event published here needs a real subscriber — the platform's event-symmetry gate exists precisely because ~40 event types are published today with nobody listening.

## 7. Who may do it, and why

- **The authoring prescriber** by default.
- **Another treating clinician** may amend with a reason — cover happens, and a doctor who has gone home should not block a correction.
- **Never** reception, call centre, or the fulfilling provider. A pharmacy that disagrees with a prescription raises a clarification; it does not edit it.
- Bounded by the order's own validity — an expired order is not amendable, it is expired.

**Reason is mandatory and coded.** A controlled vocabulary — prescribing error, dose correction, patient declined, clinical change, duplicate, drug unavailable, patient not eligible — plus free text. The codes are what make "how often do we cancel, and why" answerable; free text alone answers nothing at scale, and the same vocabulary should feed the quality reporting the medical director already has.

## 7b. Notes on prescriptions, labs, radiology and procedures

Every order line gains **notes** — the instruction a doctor needs to send with an order ("fasting sample", "left knee, post-op review", "patient cannot swallow tablets — syrup if available") and the answer that comes back ("sample haemolysed, please repeat", "patient did not attend").

### Reuse the notes model that already exists

[Doc 38 §5](38-policy-member-administration.md) already defines a notes model for policies and members: append-only, signed with the author's name, timestamped, **cancellable but never deletable**, class-projected — with a shared Notes Panel component. Order notes are the **same model on a different subject**, not a fourth implementation. A second notes mechanism means two behaviours for "cancel a note" and two answers to "who can read this".

### Three visibility classes, because the reader differs

| Class | Written by | Read by |
|---|---|---|
| **ToFulfiller** | Ordering clinician | The pharmacy / lab / radiology / procedure centre holding the order, plus internal clinical roles |
| **Internal** | Ordering or treating clinician | Internal clinical roles only — **never** the external provider |
| **FromFulfiller** | The fulfilling provider | The ordering clinician and internal clinical roles |

An external centre seeing a clinician's internal reasoning would widen the deliberately narrow projection built for them in [45 §2b](45-encounter-and-prescription-adjustments.md). The class is chosen at write time and the default is `ToFulfiller` — the common case is an instruction meant to be read.

### A note is not a clinical record, and must not become one

This is the same trap as the appointment note. A free-text box on an order will attract clinical findings, and anything written there sits **outside the EMR, outside the sensitivity classification, and outside the record the next clinician reads**. The next doctor opens the encounter and never sees it.

So: notes are **operational instructions**, length-capped, with helper text at the point of writing saying exactly that. Clinical findings belong in the encounter note; a note that needs to change the clinical picture is a sign the encounter record is the wrong place to have been skipped.

### Sensitivity is inherited, not re-decided

A note attached to a sensitive examination ([37 §6](37-branch-scoping-and-clinical-sensitivity.md)) **inherits that order's sensitivity**. A note on a mental-health investigation must not be readable by someone who cannot read the result itself — otherwise the note becomes the gap in the gate.

### Adding a note is not an amendment

Annotating an order does **not** supersede it, does not create a new version, and does not invalidate an authorisation. Only the clinical content of the order — drug, dose, quantity, duration, service code, sessions — triggers §1's supersede path. Conflating the two would send every "fasting sample" note back to the approval queue.

Notes appear on the line in the doctor's view, **prominently in the fulfiller's queue detail** (an instruction nobody reads is worthless), and in the service-history modal.

## 7c. The encounter timeline starts at check-in

The timeline currently opens at **Visit started**. It should open at **Checked in**, then **Visit started**, then everything that follows.

That means joining two aggregates: check-in lives on `emr.appointment` (recorded by reception), the encounter begins later when the doctor opens the visit. The timeline is a composed view over both — it does not move the check-in data onto the encounter.

**The byproduct is worth having.** `visit started − checked in` is the patient's **waiting time**, and once the two events sit on one timeline it is derivable for free. That number is the one a clinic manager actually wants, and it belongs on the branch dashboard beside the checked-in and no-show counts.

**Three cases that must not be collapsed:**

- **Checked in, then seen** — the normal path. Both entries, both actors, waiting time shown.
- **No check-in recorded** — a walk-in taken straight into the room, or a missed step. The timeline says **"no check-in recorded"**; it does not silently begin at Visit started as though the two were the same moment. Absence of a record is not evidence the step happened.
- **Check-in timestamped after the visit started** — a retroactive entry, and a real data-quality signal. Show both as recorded and **flag the inconsistency**; do not quietly reorder them into a plausible sequence. Silently sorting bad timestamps into a tidy story is how you lose the ability to notice the process is broken.

Each entry carries its actor and branch, consistent with every other timeline in the platform.

## 8. Adding to the procedure types

`procedure_type` ([45](45-encounter-and-prescription-adjustments.md) §2) gains **Occupational Therapy** and **Speech Therapy**, both `is_session_based = true` — the same shape as physiotherapy, which is exactly why that flag was made data rather than code.

## 9. Invariants

1. **Nothing signed is mutated.** Amend supersedes; cancel transitions; neither deletes.
2. **The consumed portion is immutable.** Amendment applies only to the not-yet-consumed remainder.
3. **The state check and the write are one atomic guarded transition**, and a conflict says exactly what happened.
4. **A chronic re-allocation still sums exactly to the new total**, and the new total is never below what was dispensed.
5. **Amendments beyond the approved scope return to pending authorisation**; within it, they do not.
6. **Propagation updates the fulfilling party's queue** — a notification alone is not propagation.
7. **Every amendment and cancellation carries a coded reason and an actor**, and is audited.
8. **Cancelled and superseded records stay visible** in history and in the service-history modal.
9. **Order notes reuse the doc-38 notes model** — append-only, signed, cancellable, never deleted.
10. **Adding a note is never an amendment** — no supersede, no re-authorisation.
11. **A note inherits its order's sensitivity**, and an external provider never sees an `Internal` note.

## 10. Acceptance criteria

- [ ] A signed prescription/lab/radiology/procedure order can be cancelled or amended while unconsumed; the original is `Superseded` or `Cancelled` and never mutated; the new version links back.
- [ ] Line-level: a 3-line prescription with line 1 dispensed allows lines 2–3 to be cancelled and reports partial success plainly.
- [ ] Concurrency: a cancel racing a dispense produces exactly one winner, and the loser is told which line was dispensed, when and by whom — proven by a parallel test on the existing consume harness.
- [ ] Dispensing against a cancelled order fails with the cancellation reason and actor.
- [ ] Chronic: duration and frequency editable; dispensed windows unchanged; remaining windows re-allocated and still summing exactly; a new total below the dispensed amount is refused; reducing to ≤ 1 month refuses or converts to acute with explicit confirmation, recorded.
- [ ] Authorisation: an in-scope reduction keeps the authorisation; an out-of-scope change returns the order to pending authorisation and notifies approvals.
- [ ] Propagation: the item disappears from or updates in the fulfilling provider's queue (asserted against the queue endpoint, not the notification); beneficiary, ordering doctor and case manager notified as applicable; every event published has a subscriber.
- [ ] Reason vocabulary seeded and mandatory; amendments by a non-authoring treating clinician permitted with a reason; reception, call centre and the fulfilling provider refused.
- [ ] Cancelled and superseded orders remain visible in history and the service-history modal with their status and reason.
- [ ] Notes on every order kind, reusing the doc-38 model (append-only, signed, timestamped, cancellable, never deleted); three visibility classes with an external provider proven unable to read `Internal`; a note on a sensitive order inherits that sensitivity; adding a note neither supersedes the order nor re-triggers authorisation; notes render prominently in the fulfiller's queue detail and in the service-history modal.
- [ ] `procedure_type` gains Occupational Therapy and Speech Therapy, both session-based.
- [ ] All existing suites green — consume atomicity, min-necessary, RLS, sensitivity, chronic allocation.

---

### Cross-references
State machines: [23](23-state-machines.md) · Chronic windows & procedure types: [45](45-encounter-and-prescription-adjustments.md) · Audit: [19](19-audit-strategy.md) · Claims reconciliation: [36](36-claims-management.md) · Build: [phase-30](claude-code-prompts/phase-30-order-amendment.md)

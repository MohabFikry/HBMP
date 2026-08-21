# 50 — The Prescriber's Portal: what the doctor's screen could not do

> **Status:** implemented (pass 5 of the client-vs-service audit, 2026-08-20).
> **Spec:** `docs/superpowers/specs/2026-08-20-prescriber-portal-design.md` · **Plan:** `docs/superpowers/plans/2026-08-20-prescriber-portal.md`
> **Reads on:** [43](43-approval-engine-and-prescribing-support.md), [44](44-clinical-validation-hardening.md),
> [45](45-encounter-and-prescription-adjustments.md), [46](46-order-amendment-and-cancellation.md),
> [37](37-branch-scoping-and-clinical-sensitivity.md) §6.

---

## 1. Why this document exists

Passes one to four of this audit covered clinic management, the medical director, approvals and claims, and
finance with the pharmacy counter. Each found the same defect class — *a capability that exists in the
contract, in the screen and in the fixtures, and nowhere on the wire* — and each closed it.

This pass covered the surface where clinical decisions are made, and the class showed up with a different
weight. On a finance screen an unreached endpoint means a number nobody can export. Here it meant a check
that reported a clean result it had not computed, and a signed clinical note that could not be corrected.

**The finding that generalises past this pass:** every one of these had a green test. The engine's
interaction loop was proven by a unit test that handed it a populated list while production handed it an
empty one. The result-access state machine was proven legal by a domain test while the requester could not
reach the row. `ChronicAmendExecutor` was not only tested but *debugged* — 31.5 fixed a real division bug in
it — for a code path no user could open.

> A test proves the code it is handed. Nothing in this repository was checking that anybody could get to it.

---

## 2. What changed

### 2.1 The interaction check learns what the patient is already taking

`ValidationRequest` carried `ActiveMedicationDrugIds`, documented as existing "so interactions are checked
against what they are already taking and not only across the lines being written now".
`PrescriptionValidator` iterated it. Both production call sites passed `[]`, and every unflagged line was
then reported **`Ok` — "No interaction found"**.

The fix is the one 28.2 already made for diagnoses, and `ValidationRequest`'s own remarks explain it:
fetched data belongs on `ValidationSnapshot` behind `Fetched<T>`, because "an invariant carried by the type
system survives and one carried by review does not". The active-medication list never made that move, which
is exactly why nothing filled it — not behind the fetch seam, so the ports never learned to fetch it, and no
type could complain because an empty list is a valid list.

It is now `Fetched<ActiveMedications>`, sourced from **active Mersal prescriptions ∪ recorded
`medication_history`**. Neither half is sufficient: prescriptions cannot know what a patient bought
elsewhere, and a reported history cannot keep itself current. **An outage in either makes the whole fact
`Unavailable`** — a half-list presented as complete carries the authority of a completed check.

Three outcomes, three sentences: `Unavailable` naming the reason · `Ok` that says *"no current medications
recorded for this patient"* · `Ok` that says how many it compared against. A warning names the medicine
**and its source**, because a dispensing record and a patient's recollection are both worth acting on and
are not equally certain.

### 2.2 The medication list a patient is already on

`medication_history` had a POST with no caller since phase 4.1, so `/clinical` and the FHIR
`MedicationStatement` projection both reported "no medications" about every patient on the platform. It
gains a read, a **stop** transition (not a delete — what someone *was* taking is clinical history), and a
section in the encounter beside allergies, which holds the same line for the same reason: *"no medications
recorded — not the same as taking none"*.

`emr.emr_note` and `emr.medication_history` both gained a **name snapshot** (0026, 0027), following
0020's `allergen_display` and 0022's note author. That header's lesson is now recorded for the third time:
*asking for the weaker fact — does this drug exist — is why the name was never captured.*

### 2.3 A signed clinical note can be corrected

The addendum endpoint had existed since phase 4.1; the workspace told the doctor twice that it was the only
correction route; no client had a method. `getEncounter` **filtered addenda out and discarded them**, so a
correction written by any route was invisible to every reader.

The correction renders **below** the signed note, never merged into it. That separation is the feature: the
value of an addendum is that the original text stays exactly as signed and stays readable. A UI that folded
the correction in would produce a tidier record and a less truthful one.

### 2.4 No state the product can enter and not leave

The inbox offers **Ask for more**, which drives a request to `InfoRequested`. 18.A4 built `supply-info` as
the exit, *because* "a request that entered InfoRequested had NO path back". Two layers kept it shut: the
client had no method, and — the part no state-machine test could see — **the requester could not reach the
row**. `GET /report-access-requests` returned what the caller may *decide*: every pending request for a
medical director, otherwise only requests against orders the caller placed. A clinician asking to see
someone else's result is by definition not that order's provider.

The inbox now also returns **the caller's own requests**, in any open state. It discloses nothing new — the
requester wrote the justification and chose the beneficiary — and someone else's request on an order neither
party placed stays invisible. `canDecide` and `isRequester` are computed **server-side**: whether a caller
may decide a sensitive-result release is an authorization question, not one a browser answers by comparing
identity strings.

### 2.5 Notes on prescriptions, labs, radiology and procedures

Doc [46 §7b](46-order-amendment-and-cancellation.md) is titled *"Notes on prescriptions, labs, radiology and
procedures"*. orders-service built all of it in 30.5b; **pharmacy had none**, so the order kind the doc names
first had nowhere to put *"patient cannot swallow tablets — syrup if available"*. Ported, not reimplemented,
with `libs/amendment`'s shared vocabulary — the doc's reason being that a second mechanism means two
behaviours for "cancel a note".

Two decisions worth recording:

- **The endpoint picks its gate by what the caller is.** `PharmacyGate` asks whether you *treat* this
  beneficiary; `DispensingGate` asks whether you are a dispensing pharmacy. A pharmacist treats nobody, so
  the clinician's gate in front of a note written *for* the counter refuses the reader the note exists for.
  Each caller still passes the check written for them.
- **Notes key on `root_line_id`, not the line id.** 30.1 supersedes a line rather than mutating it, and a
  note is about the clinical intent, which survives the amendment. Keying on the line would silently discard
  every instruction attached to a script the moment it was amended.

### 2.6 A chronic amendment is confirmed against the arithmetic that will run

`AmendLineDialog` has rendered a `chronicPreview` since 30.3 — for a caller that never existed. The preview
is now a **server read**: `PreviewScheduleAsync` is the read half of `AmendScheduleAsync`, same query, same
`Reallocate`, living on the executor so it cannot drift. `zChronicPreview`'s header is the reason — a forked
largest-remainder "would appear as a doctor being shown a schedule the pharmacy never honours."

The three refusals are rendered **before** the request and disable confirmation, because each is knowable
then: a prescriber should not learn from a 409 that they asked a patient to return medicine. `NotChecked` is
among them — an amendment must not be the route by which a missing `is_pack_splittable` becomes an assumed
one.

`PrescriptionResponse` also gained **`Kind`**. The row has carried Acute/Chronic since 29.5 and no response
projected it, so no screen could tell which rows the schedule control applies to — part of why the endpoint
stayed unreachable.

---

## 3. What was audited and found correct

Recorded so the next pass does not re-raise them. Five endpoints look unreached from the SPA and are meant
to be:

| Endpoint | Called by | Why it must not be a SPA call |
|---|---|---|
| `GET /encounters/{id}/validation-context` | pharmacy | 28.2's whole point: the diagnosis list comes from the encounter, never a request body |
| `GET /beneficiaries/{id}/clinical-context` | approvals | field-scoped review DTO, PHI-audited under purpose |
| `.../for-beneficiary/{id}` (orders, pharmacy, referrals) | profile | the 360 is composed server-side under the caller's token |
| `GET /prescriptions/history/{id}` | orders | service-to-service |
| `POST .../extend-validity` (orders + pharmacy) | approvals | only someone who may decide an authorization may move an expiry |

### 3.1 F8 — `GET /prescriptions/{id}/dispensing`, and a correction to this pass's own audit

The audit listed this as an unreached endpoint to "wire or delete". **Both options were wrong, and so was
the finding.** Reading it settled the question the spec's dichotomy could not:

- Its projection is **identical** to `queue` and `search` — the same `DispensableRxView.From`, not a
  superset or a subset.
- Its distinct behaviours are a 409 naming why a prescription may not be filled, and an `open` audit event.
- The refusal is already enforced where it decides anything (the dispense path's domain rule), and the
  status is already visible: `Outstanding` deliberately **returns expired prescriptions** so a pharmacist is
  told "this has lapsed and can be extended" rather than "this member has nothing" — with the reasoning
  written in that method's own header.
- The `open` audit discloses nothing beyond the search that already returned and audited the row.

So it is neither a defect nor dead weight: it is the read for a **scan-the-prescription-number** counter
flow that does not exist yet, and it stays. Not filed in `deferred-findings.md`, which is for divergences
from a design document — this diverges from none.

---

## 4. Invariants this pass registered

| Invariant | Severity |
|---|---|
| `INV-NO-CHECK-REPORTS-OK-ABOUT-A-COMPARISON-IT-DID-NOT-MAKE` | Critical |
| `INV-A-CHRONIC-AMENDMENT-IS-CONFIRMED-AGAINST-THE-ARITHMETIC-THAT-WILL-RUN` | Critical |
| `INV-A-SIGNED-CLINICAL-RECORD-HAS-A-CORRECTION-PATH` | High |
| `INV-NO-STATE-THE-PRODUCT-CAN-ENTER-AND-NOT-LEAVE` | High |
| `INV-A-NOTE-IS-NEVER-AN-AMENDMENT` | High |

---

## 5. What this pass did NOT cover

**The reception walk-in queue.** `emr/Api/Queue.cs` serves `/queues`, `call-next`, `requeue`, `remove` and
`complete` (phase 3.3). **Nothing calls any of them** — no service, no screen. The doctor's day list reads
appointments. That is reception's portal, so it belongs to the next pass; recorded here because it was found
here.

**Thirteen roles remain un-audited:** `reception`, `nurse`, `lab`, `radiology`, `procedure_provider`,
`beneficiary_mgmt`, `beneficiary_mgmt_supervisor`, `case_manager`, `call_center`, `provider_admin`,
`policy_admin`, `org_admin`, `super_admin`.

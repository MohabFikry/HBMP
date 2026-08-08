# ADR-0040 — Procedure courses, unit-aware dosing, and transaction-level actions

*Status:* Accepted · *Phase:* 31.1 · *Supersedes parts of:* [ADR-0038](0038-radiology-rename-op-procedures-and-chronic-prescribing.md),
`HBMP-Design/45-encounter-and-prescription-adjustments.md` §2 and §5

---

## Context

Phase 29 built the encounter's four composer tabs against design 45. Using them against real data surfaced
three things the design had modelled in a shape the clinical work does not take, and one class of defect that
had by then appeared four times.

**The recurring defect.** A correct, tested server capability with no client that calls it. The E/M routing
map, the chronic schedule preview, the service-history modal and — found in this phase — the Quantity check
were all complete, all covered by passing tests, and all unreachable: `doseAmount` and `timesPerDay` had
never been sent, so `QuantityChecks` reported *"this line has no numeric dose, frequency and duration to
compute a quantity from"* on **every prescription this platform had ever written**. The tests passed because
they ran against `DevApiClient` fixtures that supplied what `HttpApiClient` never mapped.

## Decision

### 1. An OP-Procedure order is one COURSE — the type and the session count move to the ORDER

Design 45 §2 held that **"sessions ARE the quantity, never a parallel counter"**, with the procedure type and
the session count on each *line*. That cannot express an outpatient course:

- a physiotherapy course is **one clinical decision** — one kind, one number of attendances — but a two-item
  course could be composed as six sessions of one item and eight of the other, which is not a course any
  centre can deliver;
- there was **nowhere to record "three of these at each attendance"**, because the quantity slot was already
  spent on the session count.

So `procedure_type_code` and `sessions` move to `investigation_order`, and `order_line` gains
`quantity_per_session`.

**What deliberately does not change is `quantity_ordered`.** It is still the metered total — what the atomic
consume path decrements, what a partial approval narrows, and what the delivering centre's queue counts down —
and it is now `sessions × per-session`. That is what makes this affordable: the consume path, the
partial-approval arithmetic and the provider projection are untouched. Sessions *delivered* is **derived**
(`ProcedureCourse.SessionProgress`) rather than stored, because a second stored counter that could disagree
with the first is exactly the parallel counter §2 was right to forbid.

§2's session-ceiling rule is now checked against the **course length**, not the metered total: *"at most 12
sessions"* is a statement about attendances, and comparing it to `sessions × per-session` would refuse an
ordinary six-session course of a three-per-visit item as though eighteen had been asked for.

### 2. The dose is a NUMBER in the drug's own unit, and the quantity is computed once

The dose field becomes a numeric amount plus a times-per-day, with the **prescribing unit read from master
data and shown, not chosen** — it is a fact about the product. The sig stored on the line (`"1 Tablet x
3/day"`) is *derived* from those numbers rather than typed beside them, because that string is what the
pharmacist reads at the counter and a free-text box next to a numeric dose is two statements of one
instruction.

The arithmetic moves out of `QuantityChecks` into **`Mersal.Prescribing.QuantityMath`**, and pharmacy exposes
`POST /prescriptions/quantity-preview`. Three callers now share one implementation: the composer prefills its
quantity field from it, the validation check grades against it, and the counter meters against it. A
TypeScript copy in the browser would be a second answer to *"how much medicine does this person get"*.

The prefilled quantity stays **editable**, and an edit is sticky — a prescriber who deliberately writes 90
because the patient is travelling must not watch it snap back on the next keystroke.

### 3. The chronic script's treatment length is read from its lines

Design 45 §5 put a script-level *"Treatment duration (days)"* beside each line's own duration. One fact, two
fields — and the schedule was computed from whichever the doctor filled in second. The script's length is now
the **longest line**: a chronic prescription runs until its last medicine does, and windowing it to the
shortest would strand the rest.

### 4. Amend and Withdraw act on the TRANSACTION, from the row

Both lived inside the detail dialog, so a doctor correcting an order they had just raised had to open it to
find out whether it could be corrected. They become icon buttons on each row of all four tables.
**Withdraw** acts on the whole transaction and reports **partial success by name** (design 46 §3) — a line
already dispensed cannot be withdrawn, and *which* one matters more than *how many*. **Amend** opens a
dialog listing the lines with editable quantities and one coded reason, and sends only the quantities that
actually changed: superseding five lines because the dialog was opened would put four amendments into the
record that nobody made.

### 5. Master data: splittability comes from the pack columns, not the dosage form

`is_pack_splittable` was derived from the free-text `Dosage Form`. Measured against all 22,653 workbook rows,
the form is wrong in both directions: it calls a box of three ampoules unsplittable (three separate items, and
giving one is routine) and says nothing at all about the 38 forms it does not recognise. The catalogue carries
a measured pair — `Major Units (per box)` and `Minor Units (total)` — and the rule is about the second alone:
**a pack holding more than one prescribing unit can be split; a pack that *is* one unit cannot.**

The dosage form keeps the one job it is good for — naming the prescribing unit — and its vocabulary was
widened by `masterdata/0018` to cover the 2,495 products that previously loaded with no unit at all.

Trade names and active ingredients are **cased at load time** (`MasterDataNormalize.DisplayName`): one source
shouts and the other whispers, and they sit in the same list. Casing in CSS would fix whichever screen
remembered to and leave the search index, the exports and the name snapshotted onto a prescription line
disagreeing.

## Consequences

**Migrations are expand-only.** `orders/0016` adds nullable/defaulted columns and leaves the line-level
`procedure_type_code` in place — still written, so a rollback finds the data it expects. `quantity_per_session`
defaults to `1`, under which a pre-31.1 row's stored total still equals its session count, so old data reads
correctly under the new rule without being rewritten. Dropping the line-level column is a later contract step.

**Two design documents now describe superseded behaviour.** Design 45 §2's "sessions ARE the quantity" and
§5's script-level treatment duration are contradicted by this ADR, which is the authority. Both are annotated
in place rather than edited away — a design doc that quietly matches the code loses the record of why it
changed.

**96.1% of the catalogue is now usable for quantity calculation** (21,775 of 22,653), up from zero. The
remaining 878 rows report `NotChecked` **naming the missing field**, which is the honest answer and the one
invariant 8 requires.

**One risk accepted.** The composer's quantity settles a moment after the last keystroke, and it is part of
the line's staleness fingerprint — so a validation run started before it lands is correctly marked stale. That
is the right behaviour (the checks were graded against a different number) and costs a second click in the
rare case a prescriber reaches Validate faster than the round trip.

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
invariant 8 requires. *(REVISED by the 31.3 addendum below: that 96.1% counted rows whose divisor was the
wrong number. The honest figure is 84.8%.)*

**One risk accepted.** The composer's quantity settles a moment after the last keystroke, and it is part of
the line's staleness fingerprint — so a validation run started before it lands is correctly marked stale. That
is the right behaviour (the checks were graded against a different number) and costs a second click in the
rare case a prescriber reaches Validate faster than the round trip.

---

# Addendum (31.3) — the divisor was the wrong column

## Context

The decision above put `pack_size` at the centre of every quantity: the composer prefilled from it, the check
graded against it, and a `PackCounts(unit)` gate decided whether a box count could be offered at all. Measured
against the catalogue, that was wrong for most of it.

`pack_size` is the workbook's "Minor Units (total)". It counts what the catalogue counts, and the catalogue
counts containers for anything not supplied as discrete items:

- a 120 ml bottle of syrup is `minor = 1`, so a 210 ml course divided to **210 bottles**;
- a box of five insulin pens is `minor = 5` and dosed in IU, so no box count was offered at all — the gate
  correctly refused to divide IU by pens, and a prescriber saw "boxes cannot be counted for this product";
- a box of 24 tablets is `minor = 24`, which is the one case where the number was right.

The first is the dangerous one. It printed with the same confidence as a correct answer.

## Decision

**Divide by what the box HOLDS.** `masterdata.drug.pack_content` (migration 0019) records how many
*prescribing units* are in one box, derived at load from `Major Units (per box)` × the per-container
measurement in `Volume / Weight`, times the concentration in `Strength` where the product is measured in IU.
`QuantityMath.Compute` takes it in place of `packSize`, and `PackUnitRules.PackCounts` is deleted — with the
content known, "does the pack count the same thing the dose does?" is not a question anyone needs to ask.

**A concentration in IU per millilitre also decides the unit.** Insulin is supplied in vials, cartridges and
pre-filled pens and is dosed in IU in all three; taking the unit from the container put "Cartridge" beside the
dose field of a medicine nobody has ever dosed in cartridges. A bare total — `50000 iu` on a vitamin D
capsule — is deliberately not read as a concentration: that product *is* prescribed in capsules.

**The major column is the container count, and only for the measured forms.** For items — tablets, capsules,
sachets — the minor column is the answer and the major column is packaging trivia (24 tablets in 2 strips).
For containers — vials, ampoules, syringes, cartridges — both columns claim to count the same thing and
disagree on 106 rows in both directions, so a disagreement there derives nothing. For measured forms the two
are *expected* to disagree, because the minor column is counting millilitres.

## Consequences

**The honest coverage figure fell, and that is the point.** 19,213 of 22,653 rows (84.8%) now have a divisor
that is the right number, against a nominal 96.1% that included every syrup, cream and pen it was wrong for.
The 2,610 rows that know their unit but not their contents are listed by name at load time.

**The composer's Quantity field holds a box count**, and its label says so. A prescription is written in what
the patient carries home; "2250" beside an insulin pen is a number of international units, and no pharmacy
counts those out. Where the box's contents are unknown the field falls back to the dose total and the label
falls back to the unit — the two states are never the same control showing an unlabelled number.

**Absence still refuses to become a default.** "Lantus Solostar 100 I.U./ML 5 Pens" states its concentration
and never its volume. Three millilitres is the usual fill of an insulin pen; assuming it would produce a box
count that is right most of the time, which is the failure mode invariant 8 exists for.

**And the unit is persisted with the number.** Making the quantity a box count changes what
`quantity_prescribed` MEANS, and the dispensing counter renders it as a bare figure. So
`pharmacy.prescription_line.quantity_unit` (migration 0017) records what the number counts — "boxes", or the
prescribing unit — snapshotted at prescribing time for the same reason `drug_name` is: what the catalogue
says next year must not change what a prescription written today meant. Without it, "1" against a 24-tablet
box reaches a pharmacist who has no way to tell it from one tablet. Absent on lines written before this, and
rendered as no unit rather than a plausible one.


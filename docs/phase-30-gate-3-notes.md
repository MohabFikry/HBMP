# Phase 30 Gate 3 — chronic amendment, and a gap in phase 29 it exposed

## What Gate 3 built

Amending a chronic script's duration and frequency, per [design 46 §4](../HBMP-Design/46-order-amendment-and-cancellation.md):

- `Mersal.Prescribing.ChronicAmendment` — the arithmetic, pure. Recomputes the total for the new duration,
  subtracts what was actually handed over, splits only the remainder through the **existing**
  `ChronicAllocation.Split`. The sum invariant becomes `dispensed + Σ(remaining) == newTotal`, exactly.
- `ChronicAmendExecutor` — the persistence. Supersedes the line through the same guarded transition as every
  other amendment; the original keeps its whole schedule; its **uncollected** windows take a new terminal
  `Superseded` status so the sweeper stops seeing them; the successor gets a fresh schedule anchored at the
  day after the last collected window closes.
- `POST /prescriptions/{id}/lines/{lineId}/amend-schedule`, returning the recomputed preview on **every**
  outcome including the refusals — a prescriber deciding whether to convert a script to acute needs the
  numbers that decision turns on.

The design decision (three options for the windows, and why nothing is moved or copied) is recorded in
[the spec](superpowers/specs/2026-08-07-chronic-amendment-design.md).

## ✅ FIXED — the gap this exposed (originally in phase 29, closed here)

> The section below is kept as written, because the *finding* is the useful part. What it described is now
> wired: see `ChronicPrescribingIsWiredTests`, and the "How it was closed" section at the end.

## The gap this exposed, and it was in phase 29, not phase 30

**Nothing in production ever creates a refill window.** Searching the whole `services/` tree for a write of
`prescription_dispense_window`, a call to `WindowSchedule.Build`, a call to `ChronicAllocation.Plan`, or a
call to `ChronicDispensing.Evaluate` returns, outside tests, **only the file this gate added**.

Concretely, as it stood when this was written:

| Piece | State |
|---|---|
| `pharmacy.prescription.kind` / `refill_frequency_code` / `duration_days` | columns exist; **no endpoint sets them** — `Prescriptions.cs` contains no reference to "Chronic" |
| `pharmacy.prescription_dispense_window` | table, RLS, constraints and indexes exist; **no row is ever written** |
| `ChronicAllocation.Plan` (the 34/33/33 split) | 66 tests, **no production caller** |
| `WindowSchedule.Build` | tested, **no production caller** until Gate 3 |
| `ChronicDispensing.Evaluate` (the counter's window check) | tested, **never called by the dispense path** |
| `RefillWindowSweeper` | runs hourly, **against an empty table** |

Phase 29 Gate 5 built the machinery — library, schema, sweeper, domain rules, and a full test suite — and
never wired it to the prescribing write path. Every one of those tests passes, which is exactly why it was
not noticed: the arithmetic is correct, the schema is correct, and none of it is reachable.

**This is my own work from phase 29 and it was reported as complete.** It was not.

### What that means for Gate 3

The amendment path is correct and proven against real rows — its DB-gated tests seed a chronic script with
windows and assert the whole behaviour. But in production it currently has nothing to amend, because no
script is ever written as chronic and no windows ever exist.

Gate 3 is therefore **complete as specified and inert until phase 29 Gate 5 is finished**.

### What finishing it requires (phase-29 work, deliberately not done here)

1. `POST /prescriptions` and the submit path accept `kind`, `refillFrequencyCode` and `durationDays`, and
   write the window rows through `ChronicAllocation.Plan` + `WindowSchedule.Build`.
2. The dispense path calls `ChronicDispensing.Evaluate` so a collection is metered against its window, and
   `dispensed_quantity` on the window moves with it.
3. The prescribing composer offers the acute/chronic choice and shows the schedule before submit.

That is several gates' worth of work in a different phase. Doing it inside phase 30 would bury a phase-29
regression inside an unrelated feature commit, and the register of what is actually shipped would be wrong in
the other direction. It is recorded here instead, and belongs on the phase-29 completion list.


---

## How it was closed

| Piece | Now |
|---|---|
| `POST /prescriptions` accepts `kind` / `refillFrequencyCode` / `durationDays` | ✅ additive, defaulted to Acute so every existing caller is unaffected |
| `ChronicAllocation.Plan` | ✅ called per line at submit, from the drug's real pack facts |
| `WindowSchedule.Build` → `prescription_dispense_window` rows | ✅ written in the SAME transaction as the prescription |
| `ChronicDispensing.Evaluate` at the counter | ✅ the dispense path refuses a collection outside its window and names the date to come back |
| The window's own `dispensed_quantity` | ✅ moves with the line's, guarded on the window id |
| `RefillWindowSweeper` | ✅ now has rows to sweep |

**Four things were needed beyond "call the library".**

1. **A pack lookup.** The allocation needs `is_pack_splittable` and `pack_size`, and pharmacy had no way to
   ask. It turned out masterdata's existing `/drugs/by-id/{id}` already returns the whole row — so this is a
   wider DTO on the call the name already comes from, not a new endpoint or a second round trip.
2. **Refusing before writing.** A chronic script that cannot be scheduled must not be committed as a chronic
   script with no windows: that is undispensable in a way nothing reports. Every refusal — duration ≤ 1 month,
   unknown or inactive frequency, missing pack data — happens before the transaction opens.
3. **Insert ordering.** Windows reference `prescription_line` and the model declares no navigation between
   them, so EF emitted the inserts in tracking order and the foreign key rejected them. They are written
   after the first `SaveChanges`, in the same transaction — exactly the hazard the overrides block in the same
   file already documents.
4. **Absence carried through.** A drug whose `is_pack_splittable` master data does not record produces a 422
   naming the missing field, not a guessed quantity. "A silently wrong quantity is a dispensing error."

**The lesson worth keeping.** Every one of the 66 phase-29 tests passed while none of this was reachable.
A green suite says the code is correct, never that it is *called* — so the regression suite added here asserts
reachability specifically: it goes in through the HTTP endpoint, and its first assertion is that windows
exist at all.

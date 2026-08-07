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

## The gap this exposed, and it is in phase 29, not phase 30

**Nothing in production ever creates a refill window.** Searching the whole `services/` tree for a write of
`prescription_dispense_window`, a call to `WindowSchedule.Build`, a call to `ChronicAllocation.Plan`, or a
call to `ChronicDispensing.Evaluate` returns, outside tests, **only the file this gate added**.

Concretely, as of this commit:

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

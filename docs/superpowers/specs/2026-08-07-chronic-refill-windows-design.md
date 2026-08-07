# Chronic refill windows — the window model (29.5)

> Design: [45 §5](../../../HBMP-Design/45-encounter-and-prescription-adjustments.md) · Build: phase-29 Gate 5

## What is already decided (not re-opened here)

The four chronic decisions are settled: **one authorisation** for the whole script with eligibility
**re-validated at each dispense**; limits **consumed per dispense as collected**; rounding to the **sub-unit
where the form allows splitting a pack**, whole units otherwise; **fixed windows with an early tolerance**
(default 5 days), **a missed window forfeited**.

This document decides only what those leave open: **how windows are represented, when their rows exist, and
what drives their status.**

## The question

Design 45 §5 says "**Missed** is set by a sweeper when a window closes undispensed" and "**Blocked** is set
when eligibility fails at the pharmacy". That names two writers — a background job and a counter — for one
`status` column, which is exactly the shape that produces a stuck row nobody can explain.

## Approaches considered

### A — Materialise every window at prescribe time; the sweeper drives every transition

The allocation runs once at submission and writes N rows. A sweeper flips `Pending → Open` at `opens_at` and
`Open → Missed` at `closes_at`.

- **For:** the whole schedule exists immediately, so the pharmacy queue is a plain query; `Missed` is a real
  stored event with a timestamp, which the case team can see and report on.
- **Against:** `status` duplicates state that is already derivable from the dates, so the two can disagree —
  and the way they disagree is the problem, not the redundancy. **If the sweeper stalls, every window stays
  `Pending`, and a `Pending` window that the counter refuses to dispense turns a background-job outage into
  patients being turned away.**

### B — Materialise rows, derive status entirely

No stored status: a window is open if `now ≥ opens_at`, missed if `now > closes_at` and nothing was dispensed.

- **For:** cannot drift, because there is nothing to drift from.
- **Against:** **`Blocked` cannot be derived.** It records that a named pharmacist presented a real
  beneficiary at a real counter and eligibility said no — an event, not a function of dates. Forfeiture has
  the same problem in a quieter way: a derived `Missed` has no timestamp, so "when did this become missed,
  and had the member's coverage already lapsed by then?" is unanswerable. Design 45 requires Blocked to be
  "visible to the case team", and a state with no row and no time is not visible to anyone.

### C — No rows until the first dispense; compute the schedule on demand

- **For:** least storage.
- **Against:** the allocation would be recomputed after dispensing had begun, so a later change to the
  rounding code would silently re-cut a script mid-course. Design 45 warns this model is "hard to change once
  dispensing data exists"; this approach guarantees that every change is applied retroactively.

## Decision — A, with the Pending/Open distinction derived

**Rows are materialised at submission** (approach A), and `Blocked` and `Missed` are **stored**, because both
are events with money consequences that need a timestamp and an actor.

**But `Open` is never written.** A window's dispensability is computed at read time:

```
dispensable(w, now) = w.status ∉ {Dispensed, Missed, Blocked}
                    ∧ now ≥ w.opens_at            (scheduled_open − early tolerance)
                    ∧ now ≤ w.closes_at
```

`Pending` is simply "stored status is Pending", and the UI shows *Open* when `dispensable` is true. The
column keeps its `Open` value only so a row can be read back the way design 45 §5's state list describes it —
nothing ever transitions **into** it.

### Why that split, stated as a failure mode

- **A stalled sweeper must never prevent a dispense — only delay a forfeiture.** With `Open` derived, a
  sweeper that has been down for a day means some closed windows are not yet marked `Missed`; it does not
  mean a patient standing at the counter is refused. The blast radius of the background job is reduced to
  the one thing it is genuinely authoritative about.
- **A stalled sweeper must not let a forfeited window be collected either.** `closes_at` is in the
  `dispensable` predicate, so a window past its close is refused by the *counter* whether or not the sweeper
  has caught up. The sweeper records the forfeiture; it does not enforce it.

That is the whole design: **the counter enforces, the sweeper records.**

## Consequences

| Concern | How it is handled |
|---|---|
| Sweeper writes `Missed` | Only for windows past `closes_at` with `dispensed_quantity = 0` and status still `Pending`/`Open`. Idempotent by that predicate — a second pass matches nothing. |
| Sweeper races a dispense | The dispense writes `Dispensed`/`PartiallyDispensed`, which the sweeper's predicate excludes. The update is guarded by the row version, so the loser retries and finds the row no longer matches. |
| `Blocked` set at the counter | Written by the dispense path with a `blocked_reason`, and it does **not** cancel the script (design 45 §5). Eligibility restored ⇒ the window is dispensable again while it is still inside its dates. |
| `Blocked` vs `Missed` | Different statuses on purpose: one is the system stopping the patient, the other is the patient not coming. Only the second is the patient's doing, and only the first should reach a case worker's queue. |
| Allocation stability | Computed once at submission and stored per window. Never recomputed — a rounding change must not re-cut a script somebody is halfway through. |
| Early tolerance | Stored as `opens_at` on the row, not applied at read time. The tolerance is configurable, and a window issued under a 5-day tolerance must keep it if the setting later changes. |

## Allocation (pure domain, TDD)

```
1. total   = dose × timesPerDay × durationDays          (in PRESCRIBING units)
2. round   ONCE, at the total: splittable → sub-unit; non-splittable → ceil to whole items
3. windows = ceil(durationDays ÷ (frequencyMonths × 30))
4. allocate integers across windows by largest-remainder, HIGHEST FIRST
```

**The allocation sums exactly to the total.** Rounding per window lets the sum drift *above* the prescribed
amount, which over-supplies the patient and over-consumes their benefit — so the rounding happens once,
before the split, and the split only ever distributes integers that already add up.

Worked cases, all of which become tests:

| Case | Expected |
|---|---|
| 90 days, monthly, 1×3/day | 3 windows of 90 |
| 100 units over 3 windows | 34 / 33 / 33 |
| 90 days, every 2 months | 2 windows (60 days' worth, then 30) |
| Non-splittable inhaler over 3 windows | whole items summing to the rounded total |

## Testing

- The allocation is pure arithmetic with exact expected values → **written test-first**, in the domain, with
  no database.
- A property test that the allocation **always** sums to the total, over generated inputs — the invariant is
  universal, and four worked examples do not establish it.
- Dispensing behaviour (early refusal, lapsed member ⇒ `Blocked` without cancelling, missed ⇒ forfeited) is
  DB-gated integration, because each one is a state transition under concurrency.

# Chronic amendment — duration and frequency on a partly-dispensed script

> Phase 30 Gate 3 · design [46 §4](../../../HBMP-Design/46-order-amendment-and-cancellation.md) ·
> builds on [45 §5](../../../HBMP-Design/45-encounter-and-prescription-adjustments.md) and the phase-29 window
> model ([spec](2026-08-07-chronic-refill-windows-design.md)).

**Settled before this document starts** (design 46 §4, not re-opened): what was dispensed is a fact and is
never recalculated; the remaining quantity is re-allocated by the same largest-remainder, highest-first
method and must still sum exactly; a new total below the dispensed amount is refused; frequency changes
reschedule only future windows.

What needed deciding is **what happens to the `prescription_dispense_window` rows**, because a chronic line
owns a schedule and Gate 1's supersede model creates a second line beside it.

## The question

A 90-day script at monthly frequency has three windows. Window 1 is collected. The prescriber shortens it to
60 days. Gate 1 says: the original line is `Superseded` and never mutated; a new line is inserted. So where
do the windows live?

## Three options

### A — move the uncollected windows onto the successor

`UPDATE prescription_dispense_window SET prescription_line_id = <v2> WHERE dispensed_quantity = 0`.

The collected window stays on v1, the future ones move to v2, and each line's schedule is "its own windows".

**Rejected.** Reparenting a row is precisely the silent rewrite this phase exists to stop. It also leaves v1
holding a schedule with a hole in it — window 1 and then nothing — which reads as data loss to anyone
querying the original line, and there is no record of where windows 2 and 3 went.

### C — copy the collected windows onto the successor as well

v2 then holds the complete picture: the collected windows (frozen) plus the newly allocated ones.

**Rejected, hardest.** A copied dispensed window is a **second row claiming the same collection**. "How much
did we hand over" becomes answerable two ways, and the two answers differ by exactly the amount that was
amended. Utilisation reporting reads these rows.

### B — nothing moves. v1 keeps its whole schedule; v2 gets a fresh one · **CHOSEN**

The original line keeps every window it was issued, including the collected ones, exactly as they were. Its
**uncollected** windows take a new terminal status, `Superseded`. The successor line gets a fresh schedule,
numbered from 1, covering the remaining duration.

The complete picture is the `root_line_id` chain read in version order — which is what `root_line_id` was
added for in Gate 1, and what the service-history modal already needs for every other purpose.

**Why the new window status is not optional.** Without it the sweeper would find v1's uncollected windows
past their close date and mark them `Missed` — recording a forfeiture for a window that was never owed,
against a line the prescriber replaced. Phantom forfeitures on a report nobody can reconstruct. The sweeper's
partial index already filters `status IN ('Pending','Open')`, so a terminal status removes them from its
sight without touching the sweeper's code.

`Superseded` is also honest in a way `Missed` and `Cancelled` are not: the patient did not fail to collect,
and nobody withdrew their medicine. The window was replaced.

## Where the successor's schedule starts

Not at today, and not at the original start. **At the day after the last COLLECTED window closes.**

- Anchoring at today would let a patient who collected on day 1 and had the script amended on day 3 collect
  again on day 3 — the fixed-window rhythm exists to stop exactly that.
- Anchoring at the original start would re-issue windows that have already been served.

With no window collected, the anchor is the original start: nothing has happened yet, so nothing constrains
it. A frequency change then applies from the anchor with the **new** period, which is what "reschedules only
future windows" means in practice.

## The arithmetic

Given the new duration:

1. `newTotal` = the phase-29 allocation for the new duration — dose × times/day × newDurationDays, rounded
   **once**, at the total, in the drug's dispensable unit. The rounding rule does not relax because this is
   an amendment.
2. `alreadyDispensed` = the sum of what the collected windows actually handed over. **Read, never
   recomputed.**
3. If `newTotal < alreadyDispensed` → **refused**. It implies un-dispensing.
4. `remaining` = `newTotal − alreadyDispensed`.
5. `remainingWindows` = the window count for the remaining duration at the (possibly new) frequency.
6. Split `remaining` across `remainingWindows` by largest-remainder, highest first — the same
   `ChronicAllocation.Split`, not a second implementation.

**The sum invariant restated for amendment:** `alreadyDispensed + Σ(new windows) == newTotal`, exactly. That
is the property the tests assert, because it is the one that silently breaks.

### The prompt's worked cases

| Case | Total | Dispensed | Remaining | Windows | Result |
|---|---|---|---|---|---|
| 90d monthly, w1 (90u) collected, → 60d | 180 | 90 | 90 | 1 | one window of 90; w1 untouched |
| same → 120d monthly | 360 | 90 | 270 | 3 | 90 / 90 / 90 |
| same → total below 90 | — | 90 | — | — | **refused** |
| same → 25d | — | — | — | — | **chronic-definition prompt** |

## Reducing to ≤ 1 month

Design 46 §4: "reducing duration to a month or less makes the script no longer chronic. The system must not
silently keep a 'chronic' script that no longer meets the definition. Either refuse, or convert it to acute
with an explicit confirmation."

**Chosen: refuse by default, convert on an explicit flag.** The request carries `convertToAcute`; without it
the endpoint answers 422 with a problem type that says what confirming would do. With it, the successor line
is written on an **acute** prescription shape — no refill schedule at all — and the conversion is recorded in
the amendment ledger as its own reason text.

Refusing outright would leave a prescriber unable to shorten a course they got wrong. Converting silently
would change the dispensing pattern the patient was told to expect — they were told to come back monthly, and
nothing would tell them not to. The flag is the only option that does neither.

## What this does NOT do

- **It does not re-open the four settled decisions.** One authorisation for the whole script, limits consumed
  per dispense as collected, rounding to the sub-unit where the form allows splitting, fixed windows with an
  early tolerance and a missed window forfeited. All unchanged.
- **It does not touch a collected window.** Not its quantity, not its dates, not its status.
- **It does not change `ChronicAllocation`.** The amendment computes a total and a remainder and hands both
  to the existing `Split`. A second rounding implementation is how the sum stops being exact.

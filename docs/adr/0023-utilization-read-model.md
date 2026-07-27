# ADR-0023 — Utilization is a direct query over the accumulator, not a projection

- **Status:** Accepted
- **Date:** 2026-07-27
- **Phase:** 19.4

## Context

Design 38 §4.3 asks for utilization at five scopes — member, group, plan, policy, payer — with the hard
requirement that **every response reconciles EXACTLY to `coverage_limit.consumed_value`**, the accumulator
phase 18 owns and eligibility refuses care on.

The build prompt allows either a projection over the consumption/claim event streams or a direct query, and
asks for the choice to be recorded.

## Decision

### A direct query, in policy-service

A projection would make reconciliation a property **somebody has to keep true**. A direct read makes it a
property that **cannot become false**.

That difference is the whole argument. A missed event, a replay bug, or a rebuild run against the wrong window
and the report quietly disagrees with the number a receptionist is refusing care on — and nobody finds out,
because finding out means comparing two things nobody compares. Utilization is the figure Finance renegotiates
contracts on and the figure a supervisor uses to decide whether a member has anything left; when it disagrees
with the counter, the person at the counter is a refugee with no way to appeal a spreadsheet.

policy-service, because it owns `coverage`, `coverage_limit`, `enrollment`, `policy_plan`, `member_group` and
`policy.payer_id` — every scope resolves locally. reporting-service is a **de-identified aggregate** read model
by design (its `financial_fact` has no diagnosis column, asserted against `information_schema`); a per-member
utilization table is the opposite of that property, the same reasoning ADR-0022 used for the timeline.

If latency ever demands a projection, the reconciliation test written in this phase is what makes adding one
safe: it already asserts the invariant a projection would have to preserve.

### "Consumed" and "activity in a window" are different numbers, and are never named the same

`coverage_limit.consumed_value` **resets** at each period boundary (`LimitReset`). `policy.benefit_consumption`
is append-only and **does not** — its rows survive the reset that zeroed the accumulator.

So summing the ledger over all time yields a *larger* number than the accumulator, and both are correct: one
answers "how much of this year's entitlement is gone", the other "how much care did this member receive".
Reporting either under the other's name is how a report tells Finance a member is over their limit when they
are not.

The API therefore keeps them lexically apart:

- `limit` / `consumed` / `remaining` / `percentUsed` / `resetsOn` — the **accumulator**, current period.
- `windowActivity` / `windowEvents` / the tier split — the **ledger**, window-scoped.

### Reconciliation is asserted at runtime, not only in a test

Every response carries `reconciliation { accumulatorTotal, reportedTotal, reconciled }`, computed along a
second, independent path (`SUM(consumed_value)` straight from SQL). A report is read on days no test runs; if
the two ever disagree, the person about to act on the number must see it, not discover it afterwards. A
mismatch is also audited as `RECONCILIATION-MISMATCH`.

### The network-tier split required attributing the ledger

19.1b's tier is a property of *(provider, service date)*, and the ledger recorded neither, so the split was
unanswerable. Migration 0012 adds `provider_id`, `provider_location_id` and `service_date` to
`benefit_consumption`; orders-service and pharmacy-service now emit `providerId` on the fulfillment events.

Two sub-decisions:

- **Store the provider, resolve the tier at report time.** Freezing a resolved tier code at consume time would
  defeat 19.1b's *correction* verb — an assignment made against the wrong provider is supposed to be fixable,
  and a correction has to correct the reports that follow. It also keeps an HTTP call off the consume path,
  where a provider-service outage must never stall the accumulator.
- **Store the service date, not just `applied_at`.** `applied_at` is when the accumulator moved, which lags the
  care by however long the broker, outbox and retries took. Resolving at `applied_at` would price February's
  care against March's network — the exact error 19.1b's service-date rule exists to prevent.

### Unknown attribution is its own bucket and is never in-network

Movements with no provider (every row written before 0012, and any event from a principal with no provider)
report as `UNATTRIBUTED`. So does a movement whose tier the resolver could not determine.

Folding either into in-network would bias the error in the direction that **flatters the network**, on the one
number the network is judged by. Note this is deliberately *not* 19.1b's rule: pricing treats an unresolved
provider as out-of-network because charging the safer amount protects the member. A report has no such
asymmetry, and recording a resolution outage as real out-of-network volume would send the Network Team
renegotiating a contract that already exists.

### Cross-service facts are read per source, and "unavailable" is not zero

Encounter counts (emr), authorization outcomes (approvals) and claim value (claims) are owned elsewhere. Each
is asked **separately and fails separately**: a composed call would let an approvals outage blank the claim
value too, and a report that hides three facts because one service is down is useless during exactly the
incident it is needed for.

Every external figure is nullable and null means *could not ask*. A zero is indistinguishable from "this member
used nothing", and the two lead to opposite decisions. The response names which services did not answer,
because someone comparing two groups has to know one of them is missing its claim value.

Three new endpoints serve these — `/api/v1/encounters/utilization`, `/api/v1/authorizations/utilization`,
`/api/v1/claims/utilization` — each returning **counts and amounts only**. The narrowness is enforced at the
owner, not by trimming after the wire: a projection applied post-wire has already put PHI in a log, a trace and
a retry buffer.

## Consequences

- **No clinical field exists in any utilization type**, for any role — asserted by reflection rather than
  filtered per role. A filter has to be remembered; a missing field cannot be forgotten. Amounts remain
  role-gated (`financials`), on the same line 19.3 draws for a Financial note.
- Percentages are **nullable**. An unlimited benefit rendered as 0% invites "plenty left" on something never
  metered; as 100% it flags an outlier that does not exist. Over-consumption is reported past 100% uncapped —
  a limit reduced mid-period legitimately leaves consumed > limit, and that row is the only one worth reading.
- Terminated members are excluded from the member *list* but their consumption stays in scope totals: a report
  that drops a leaver's spend understates every period in which anybody left.
- The outlier threshold is a request parameter (default 80%). A chronic-care cohort at 80% in June is normal; a
  general cohort at 80% in February is not, and a fixed threshold makes the feature useless for one of them.

## Open

- Historic ledger rows (pre-0012) can never be tier-attributed — the provider was not recorded and cannot be
  recovered. They report as `UNATTRIBUTED` permanently. A backfill from the orders/pharmacy fulfillment tables
  is possible and is deliberately deferred to 19.7's backfill migration rather than smuggled in here.
- `asOf` is always today. The accumulator is a live balance with no historical version to read; asking "what
  was consumed as of last March" of a value since reset would return this period's number wearing last March's
  label. Point-in-time utilization would need a periodic accumulator snapshot, which is a separate decision.

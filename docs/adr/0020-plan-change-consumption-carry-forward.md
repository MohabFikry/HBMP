# ADR-0020 — Multiple plans under a policy; consumption carries forward on a plan change

- **Status:** Proposed · **BLOCKED ON SPONSOR SIGN-OFF** — Medical Director + Finance signatures required
- **Date:** 2026-07-27
- **Phase:** 19.2b (recorded now because 19.1b's cost-share model depends on the same accumulator semantics)

## Context

A policy offers one or more plans (`policy_plan`); a member is elected onto exactly one. Members move between
plans — a promotion, a programme change, a household reclassification — and each move raises a question the
schema cannot answer for us: **what happens to what they have already used?**

A member on "Standard" has consumed 300 of a 1,000 EGP Lab limit. They move to a plan whose Lab limit is 500.
Do they now have 500 remaining, or 200?

## Decision (pending confirmation)

**Consumption carries forward.** Remaining at the new plan = `new_limit − already_consumed`, floored at 0. In
the example: **200 remaining, not 500.**

Moving plan must never reset a member's used amounts. The alternative makes a plan change a way to obtain a
fresh benefit ceiling mid-year, which is both a cost exposure and — more importantly — an unfairness between
members who happened to be moved and members who were not.

The change itself is an `enrollment_event('PlanChanged')` with a **mandatory reason**, never an edit. Coverage
regenerates from the new plan version; the accumulator is carried per benefit category.

## Why this needs a signature and not just a record

This is a benefit-policy decision about entitlement, with two defensible answers:

- **Carry forward** (above) treats the year's usage as the member's, independent of which plan they sat in.
- **Reset per plan** treats each plan as its own contract with its own ceiling — arguably more correct where
  the plans are funded by *different payers*, since one payer's spend should not consume another's limit.

The second reading matters at Mersal specifically, because a member could plausibly move from a donor-funded
plan to a government-funded one mid-year. Under carry-forward, the government payer inherits the donor's spend
against its own ceiling. That may be exactly wrong.

Engineering cannot settle this. It needs the Medical Director (entitlement fairness) and Finance (payer
exposure and inter-payer accounting).

## Interaction with 19.1b

The same question shapes `copay_counts_toward_deductible` (ADR-0019 §5): if consumption carries forward across
a plan change, so must accrued deductible progress, or a member pays a second deductible in one year purely
because they were moved. The two decisions must be made **together** and by the same signatories.

## Consequences

- Renewal maps members by `plan_label` and **reports** unmapped members rather than silently defaulting them.
- At most one default plan per policy (partial unique index); an eligibility rule that is not satisfied is a
  422 naming the failing criterion, never a silent fallback to the default.
- Until this is signed, 19.2b should implement carry-forward **behind an explicit, documented setting** rather
  than hard-coding it, so a reversal does not require a migration of member accumulators.

## Open

- Both signatures.
- Whether the answer differs when the two plans are funded by different payers.
- Whether accrued deductible progress carries with consumption (it should, under carry-forward).

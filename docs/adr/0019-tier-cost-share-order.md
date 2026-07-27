# ADR-0019 — Network tiers are owned by network administration; cost share is resolved per tier at the service date

- **Status:** Accepted (engineering) · **AWAITING SPONSOR SIGN-OFF** — Medical Director + Finance signatures required before go-live
- **Date:** 2026-07-27
- **Phase:** 19.1b
- **Supersedes:** the free-text `benefit_rule.network_tier` column and rule-level co-pay introduced in 19.1

## The prior question, unanswered

**Does Mersal charge cost share to refugee beneficiaries at all?**

This must be answered before anything below matters operationally. For self-funded charity policies the answer
may well be: **no deductible, no coinsurance, at most a nominal co-pay** — in which case the tier cost-share
grid is live only for **donor-funded and government-funded payers**, and self-funded plans price every tier at
zero member share.

The platform is built to support either answer. The schema, the calculator and the three consumers work
unchanged whether the grid is populated or uniformly zero. But the answer is a **charity-policy decision about
what a displaced person is asked to pay at the point of care**, and it is not engineering's to make. It is
recorded here as **unconfirmed** so that it cannot be settled by default — by someone populating a grid
because the field exists.

Until it is answered, treat every non-zero cost share configured in a self-funded plan as provisional.

## Decision

### 1. Tiers belong to the Network Team, not to policy administration

`network_tier` and `provider_network_assignment` live in **provider-service**. Deciding *which* tier a hospital
sits in is a commercial statement about the network, negotiated by the Network Team. Deciding what a member
*pays* at a tier is benefit design, owned by policy administration (`policy.benefit_rule_tier`).

Collapsing them would let one person set the out-of-network penalty **and** decide who is out of network. A
policy administrator receives 403 on every tier write; the separation is asserted by `NetworkTierAuthzTests`
rather than documented and hoped for. The authority is carried by a new `provider:admin` scope, split out of
`provider:write` because a tier reassignment reprices every plan referencing that tier, for every member, from
its effective date — while adding a provider's address does not.

### 2. The tier is resolved at the SERVICE date, most-specific-wins

Contract service line > location > provider. `serviceDate` is a required parameter on the resolver; there is no
"today" default, because a resolver that defaults to today answers the wrong question for every retrospective
adjudication. A provider moved from out-of-network to T1 in March does not change what February's care is
priced at, and a February claim recomputed later reaches the same numbers.

Windows are half-open `[from, to)`, matching `plan_version` — a move leaves no uncovered day and no day covered
twice.

### 3. Resolution fails safe

A provider with no assignment resolves to the **out-of-network** tier, never to in-network by omission. The
alternative pays the best negotiated rate to a provider nobody negotiated with, and nothing downstream would
question it. Because that fallback must be unambiguous, a partial unique index enforces **at most one Active
out-of-network tier**; with two, an unassigned provider would be priced by whichever row the planner returned
first.

The resolver reports its **basis**, so "assigned to out-of-network" and "nothing was assigned, so out-of-network
was the default" can be told apart. They price identically and call for entirely different follow-up.

### 4. The order of operations in the cost-share split

**Deductible → co-pay → coinsurance on what remains.**

This is a benefit-policy decision, not an arithmetic detail, and it changes what real people pay. Taking a
percentage before the deductible would charge the member a share of money they are already paying in full — a
double count. On a 1,000 EGP service with a 200 deductible and 10% co-pay, this order yields a member share of
**280**; the reverse yields **300**.

Two properties hold for every input and are asserted, not assumed: the member never pays more than the allowed
amount, and member + payer reconciles to it **exactly**. The payer share is computed as the residue rather than
as a second calculation, so no rounding remainder can go unaccounted for.

### 5. Two terms that were silent defaults are now explicit

Both were being decided implicitly, and both change real amounts:

- **`benefit_rule.deductible_waived`** — whether the plan's deductible applies to this benefit category.
  Primary care commonly waives it. Deliberately *not* modelled as a zero deductible: "this category is exempt
  from the plan's 200 EGP" and "this plan has no deductible" survive an amendment differently, and only the
  exemption should follow the category.
- **`benefit_rule_tier.copay_counts_toward_deductible`** — whether the co-pay accrues toward the member's
  deductible for later services. It does not change what they pay today; it changes what they pay **next**,
  which is why leaving it implicit is worse than getting it wrong loudly. The split reports
  `AccruesToDeductible`; the running accumulator that consumes it arrives with member-level accumulators
  (19.2).

### 6. All three consumers share one path

approvals, eligibility and claims reach the same `libs/benefit-pricing` composition, which reaches the same
`libs/money` split.

The amount a receptionist quotes to a beneficiary at a counter and the amount the claim finally charges **must
be the same number**. They are produced by different services, at different times, for different audiences —
exactly the situation where two implementations drift and nobody notices until a person is told one figure and
billed another. `CrossServiceParityTests` exercises the shared path as each consumer does and asserts the
amounts match to the piastre.

The ordering of the three wirings follows the same reasoning: **approvals** is the gate that prevents the bad
state, **eligibility** is the promise made to a person's face, **claims** is the consequence. A claims error
passes officer review, settlement advice and adjustment — recoverable. A wrong number quoted to a refugee at a
counter has no reviewer in the loop and no recovery path.

## Consequences

- Activation requires the cost-share grid to be **complete**: a covered category with no row for an Active tier
  is a validation error. That shape is dangerous precisely because nothing looks wrong — the plan reads as
  covered, the tier exists, and adjudication reaches a real service with no agreed member share. "Not covered
  at this tier" must be stated explicitly.
- `trg_benefit_rule_tier_immutable` extends 19.1's immutability discipline to prices. Freezing a plan's shape
  while leaving its amounts writable would freeze most of what a plan is not.
- Where the tier or its cost share cannot be resolved, every consumer **fails closed and says so**. Approvals
  requires authorization and marks the answer indeterminate; eligibility shows no amount rather than a zero
  (a zero reads as "free"); claims marks the line out-of-network for officer review rather than paying it.
- policy-service publishes what was **agreed** and performs no arithmetic. Keeping the split in one place is
  the whole mechanism by which parity is structural rather than coincidental.

## Open

- **The prior question above.** Unconfirmed.
- The cost-share order of operations needs Medical Director + Finance signatures, not just this record.
- `IAdjudicatedClaimProbe` currently reports zero (see ADR-0020 and the correction verb) — a known gap awaiting
  the claims read-model query.

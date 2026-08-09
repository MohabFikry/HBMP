# ADR-0043 — `Mersal.Money` is the type for arithmetic, not for storage

**Status:** Accepted · **Date:** 2026-08-09 · **Builds on:** `libs/money/Money.cs` (phase 18.F1)

> The `Money` type has never had an ADR of its own — its rationale lives in the doc comment at the top of
> `libs/money/Money.cs`, which is worth reading first and is the source this file cites. This ADR settles
> only the question that came after it: *where* the type is used.

## Context

The 2026-08-09 audit found `Mersal.Money` "adopted by only claims + eligibility", while pharmacy pricing,
finance settlement, policy limits and reporting aggregates "all do raw decimal math". The remediation plan
asked for adoption across the four.

Reading the code before designing the migration turned up three facts that decide its shape.

**1. Money is not persisted anywhere today.** Not in claims, not in eligibility. Its single use in the whole
platform is one transient line in `TierAwareAdjudicationFacts`. There is no existing persistence pattern to
extend — whatever we do here we are inventing.

**2. A benefit limit is not always an amount.** `LimitType` is `{ Annual, PerEncounter, Lifetime, Count }`.
A `Count` limit is three physiotherapy sessions, and `LimitValue` holds the 3. Typing that field as `Money`
would assert a currency on a session count and make `Money.Egp(3)` mean "three pounds of physiotherapy".
This is the single largest group of decimals in policy and reporting, and it is not money.

**3. The currency lives on the aggregate root, once.** `Settlement.CurrencyCode` covers the whole settlement;
`SettlementLine` has no currency and needs none — a line in a different currency from its own settlement is
not a case anybody wants to be able to represent.

## The decision

**`Money` is the type money ARITHMETIC is done in. Storage stays `numeric(14,2)` plus the aggregate's own
currency column.**

Concretely:

- Every multiplication, sum, cap and comparison of amounts goes through `Money`. That is where the defects
  this type exists to prevent actually occur — the rounding disagreement the audit found, the un-capped
  allowed amount that produced it in the first place, and cross-currency addition when a second currency
  arrives.
- Entities keep `decimal` properties and the root keeps `CurrencyCode`. `Currencies.Parse` converts at the
  boundary, so an unrecognised code in the database is a loud failure at the point of reading rather than a
  silently mis-typed amount.
- Amount-typed fields that are only sometimes amounts — the `LimitValue` / `ConsumedValue` / `Remaining`
  family, which `LimitType.Count` makes a count — stay `decimal`, and say why at the declaration.

## What adoption turned out to mean, service by service

Reading each of the four for money ARITHMETIC — as opposed to fields that merely hold amounts — gave a
smaller and more specific answer than "migrate all four", and the difference is worth recording so that the
next reader does not mistake restraint for an unfinished job.

**finance — adopted.** The settlement generator prices lines, multiplies by quantity and sums a total. All of
it is now `Money` in the settlement's own currency. This is the densest money path on the platform and the
one where a rounding step between a total and its lines would be visible to a provider.

**pharmacy — adopted.** `RxRoutingPolicy` estimates a prescription's cost and compares it to a threshold to
decide whether a human must approve it. As bare decimals the per-line products were unrounded and accumulated
their tails, so a prescription near the threshold could fall either way depending on how many lines it had.

**policy — nothing to adopt, and that is the finding.** Policy does no money arithmetic. It stores cost-share
TERMS and hands them to `CostShareCalculator.Split(Money allowed, …)` in `libs/money`, which has been fully
typed since 18.F1. The audit's "policy limits … raw decimal math" is the `LimitValue` family, and item 2 above
is why those stay decimal.

**reporting — nothing to adopt, and for a better reason.** Its aggregates are `g.Sum(x => x.NetPayable)`
inside EF LINQ: they are translated to SQL `sum(numeric)` and computed by Postgres, which is exact. Pulling
them into `Money` would mean materialising every fact row into memory to add it up in C# — a real performance
cost on an analytics query, for no correctness gain. The one derived figure that is not a plain sum,
per-member-per-month, is a DIVISION, and `Money` deliberately has none: apportioning money needs an explicit
remainder policy, and a silent `/` is how a batch total stops matching the sum of its lines (`Money.cs`). Its
rounding mode was the actual defect and is fixed and ratcheted separately.

So two services adopted the type, one already had it through a shared library, and one is correct without it.

## What we rejected, and why

**Typed properties with an EF value converter (`Money` ⟷ `decimal`).** A `ValueConverter` cannot see a
sibling column, so the currency would have to be hardcoded to EGP inside it. A `Money` that always says EGP
regardless of what the row says is decorative: it type-checks, it proves nothing, and it would be *worse*
than a decimal because it looks like a guarantee.

**Complex types, one currency column per amount.** EF 8 can map `Money` to `amount` + `amount_currency`.
That is ~14 new columns across four schemas, every one of them containing `EGP` forever, duplicating a fact
the aggregate root already states — and creating a failure mode that does not exist today: a settlement line
whose currency disagrees with its settlement. Adding columns to weaken an invariant is the wrong trade.

The honest summary of the difference: the rejected options put the type in the schema, this one puts it in
the calculations. The defects were all in the calculations.

## The namespace had to move first

`Mersal.Money.Money` — a type with the same name as its own namespace — cannot be referred to as `Money` from
anywhere inside `Mersal.*`. C# resolves a simple name against enclosing namespaces before using-directives and
before global aliases, so from `namespace Mersal.Finance.Domain` the name `Money` finds the *namespace*
`Mersal.Money` through the shared `Mersal` root, and every use of the type is a compile error. No using
directive, and no `<Using Alias>` in the project file, can override that: both were tried.

The evidence that this was already costing something: `libs/benefit-pricing` had a parameter typed
`Money.Money`, and the one production use in claims was written `Mersal.Money.Money.Egp(...)`. Those are not
style choices, they are what the clash forces, and they are why "adopt this type more widely" was a bigger job
than it should have been.

The namespace is now **`Mersal.Amounts`**. The assembly and the directory stay `Mersal.Money` — the library's
identity — so this is a namespace/assembly mismatch, which is common,
harmless, and much cheaper than either alternative (renaming the type away from the domain's own word, or
leaving every future adopter to discover the trap).

## Consequences

- `Currencies.Parse(string)` is new in `libs/money`, with the enum still holding one member. Adding a second
  currency stays the compile-time event `Money.cs` wanted, and now the *stored* code is validated too.
- `TheTwoClampsAgreeTests` (services/claims) stays until claims adjudication itself moves to `Money` — the
  two clamps remain two, and the test keeps them equal. It should be deleted, not weakened, when that
  happens.
- A settlement's total is computed as a `Money` sum of `Money` line totals, so the total and the sum of the
  lines cannot disagree by a rounding step. That property is asserted rather than assumed.
- No migration and no schema change. This ADR is the reason a future reader will not find one.

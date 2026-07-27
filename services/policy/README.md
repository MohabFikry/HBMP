# policy-service

Owns the `policy` schema. Two layers now live here:

- **The benefit spine** (phase 1.2): policy, benefit_category, coverage, coverage_limit (15-database-erd §5). `consumed_value` is the **authoritative usage accumulator** — written only by `BenefitConsumptionApplier` (18.A1), read everywhere else.
- **The PAS product layer** (phase 19, design [38](../../HBMP-Design/38-policy-member-administration.md)): payer → plan → **effective-dated, immutable plan version** → benefit rules. Coverage and limits become *generated* from a plan version rather than hand-entered, so an entitlement is explainable back to a specific, dated configuration.

## Delivered
- Entities + `0001_policy_schema.sql` (FKs, enum CHECKs, `consumed_value>=0`, coverage_limit history trigger, seeded LAB/IMAGING/PHARMACY/CONSULT/REFERRAL categories).
- **Reset math** (`LimitReset`): period-start boundaries (Monthly/Quarterly/Yearly), reset-due detection, apply→consumed=0 + stamp last_reset; **Lifetime/None never reset**; `Remaining = limit − consumed`.
- **Accumulator** (18.A1): `BenefitConsumptionApplier` is the sole writer of `consumed_value` — atomic guarded UPDATE, `UNIQUE(source_ref)`, symmetric reversal, append-only `benefit_consumption` ledger (`0003`).
- **19.1 — payer / plan / plan version / benefit rule** (`0005_pas_plan.sql`):
  - `payer` replaces the free-text `policy.sponsor` so every query and report can be scoped to a payer.
  - `plan_version` carries the whole benefit configuration on a **half-open effective window** `[effective_from, effective_to)` (design 38 §7.1) — `effective_to` is EXCLUSIVE, so a successor starts on exactly the day its predecessor ends: no gap, no double cover.
    - `benefit_rule` per category: covered, limit type/value/reset, deductible, waiting period, pre-auth + threshold, coded exclusions.
- **19.1b — tier-aware cost share** (`0006_benefit_rule_tier.sql`):
  - 19.1 put co-pay and a **free-text** `network_tier` label on the rule, so a category had one price and the tier was a string the plan invented. Real benefit design prices the **same** category differently per tier, and the tier is a row the **Network Team** owns in `provider.network_tier`. Cost share therefore moved to `benefit_rule_tier` (rule × tier), and those three columns were dropped.
  - `benefit_rule_tier`: covered-at-this-tier, co-pay fixed **or** percent, coinsurance percent, `requires_preauth_override` (null = inherit), `limit_multiplier` (null = inherit). `network_tier_id` is a cross-service **value**, not an FK; the tier's **code is snapshotted** so a years-old version stays readable without a live call into provider-service.
  - **Activation requires the grid to be COMPLETE.** A covered category with no row for an Active tier is the dangerous shape: the plan reads as covered, the tier exists, and adjudication reaches a real service with no agreed member share. "Not covered at this tier" must be stated explicitly.

## The two invariants the database enforces
Both are structural rather than API-level, because "the endpoint refuses" is not an invariant — a repair script or a psql session walks straight past it.

- **An activated version is immutable.** `trg_plan_version_immutable` freezes `plan_id`, `version_no`, `effective_from` and `activated_at` once the row leaves `Draft`, permits only `Active→Superseded|Retired`, and refuses to reopen a closed `effective_to`. `trg_benefit_rule_immutable` refuses any INSERT/UPDATE/DELETE of a rule whose parent version is not a Draft — freezing the version row alone would freeze nothing that matters, since the configuration lives in the rules. `trg_benefit_rule_tier_immutable` (19.1b) extends the same rule to the **cost-share grid**: freezing the plan's shape while leaving its prices writable would freeze most of what a plan is not.
- **The version in force on a date is unambiguous.** `ex_plan_version_no_overlap` (GiST) rejects two versions of a plan covering the same day. Note this is **wider than the build prompt's "no overlapping Active ranges"**: the resolver must answer for a PAST service date too, and a past date lands on a `Superseded` version — if two superseded versions could overlap the resolver would have two right answers. Drafts are exempt, because an amendment is authored while its predecessor is still live and only has to be disjoint at the moment it activates.

## APIs
Benefit spine — `POST /policies`, `POST /policies/{id}/coverages`, `GET /coverages?beneficiaryId=`, `POST /coverage-limits/reset-run`.

Product layer (19.1) — reads need `policy:read`, writes additionally need `policy:admin` at the gate:
- `POST /payers`, `GET /payers`, `GET /payers/{id}`
- `POST /plans`, `GET /plans`, `GET /plans/{id}/versions`
- **`GET /plans/{id}/version-at?date=`** — the resolver other services must call. Adjudicating against "the current version" instead of this is the bug the whole layer exists to prevent.
- `POST /plans/{id}/amend` — clones the Active version into a new Draft (`version_no+1`). The only way to change a live plan.
- `POST /plan-versions`, `GET /plan-versions/{id}`, `PUT /plan-versions/{id}/rules` (whole rule set, Draft only)
- `POST /plan-versions/{id}/validate` — dry run of the activation checks
- `POST /plan-versions/{id}/activate` — validates, flips to Active, closes and supersedes the predecessor

Emits **PolicyChanged / CoverageChanged / CoverageLimitChanged** and (19.1) **PayerCreated / PlanVersionActivated / PlanVersionSuperseded** via the outbox.

Cross-service `beneficiary_id` is a logical value, not a cross-schema FK.

## Scopes
`policy:write` used to mean everything policy-shaped. Phase 19 splits it (identity `0006_pas_policy_scopes.sql`):

| scope | authority |
|---|---|
| `policy:admin` | author the benefit **product** — payers, plans, versions, rules. Activating a version decides what thousands of members are entitled to. |
| `policy:write` | administer an individual **member** against an authored plan. Unchanged in meaning. |
| `policy:supervise` | supervisory increment: cancel another user's note, approve a retro-effective change. |
| `policy:read` | read the configuration. Deliberately broad — it is the vocabulary the platform adjudicates against, and carries no PHI. |

## Tests
- `LimitResetTests` — reset math per period + limit type; remaining calculation.
- `BenefitConsumptionTests` / `BenefitConsumptionTranslateTests` (env-gated `POLICY_TEST_DB`, live PG) — the accumulator actually binds; idempotent redelivery; symmetric reversal.
- `PlanVersionValidationTests` (pure) — the activation matrix (nothing covered, zero limit on a covered category, reset on a Lifetime limit, a pre-auth threshold above its own limit, an elapsed window …), all problems reported at once, and the half-open window asserted on both boundary days.
- `PlanVersionStoreTests` (env-gated `POLICY_TEST_DB`, live PG) — the immutability triggers and the overlap exclusion, attempted **directly through EF with no endpoint in the way**; abutting versions allowed; drafts exempt; the resolver returns the **Superseded** version for a date inside its window, never a Draft, and loads its rules.
- `PolicyAuthzTests` (real engine) — member administration does not confer the authority to author a plan, in either direction (role without scope, scope without role); the supervisory increment is separate; every action is tenant-scoped.
- `RlsIsolationTests` (env-gated two-role conn) — tenant isolation under `hbmp_app`.

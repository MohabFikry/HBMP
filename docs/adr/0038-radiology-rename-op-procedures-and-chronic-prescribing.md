# ADR-0038 — The Radiology rename, OP Procedures as an order type, and the four chronic decisions

- **Status:** Accepted
- **Date:** 2026-08-07
- **Phase:** 29
- **Design:** [`HBMP-Design/45-encounter-and-prescription-adjustments.md`](../../HBMP-Design/45-encounter-and-prescription-adjustments.md)
- **Window model:** [`docs/superpowers/specs/2026-08-07-chronic-refill-windows-design.md`](../superpowers/specs/2026-08-07-chronic-refill-windows-design.md)
- **Runbook:** [`docs/runbooks/radiology-rename.md`](../runbooks/radiology-rename.md)

> The build prompt calls this ADR-0029. That number was taken by the branch-role decision before phase 29 was
> written — the same collision ADR-0037 records. The content is what matters and it is recorded here.

## 0. A correction to the prompt's own invariant list

The phase-29 prompt's INVARIANTS block (lines 21–32) contradicts its own gates, and doc 45 §8 agrees with the
**gates**:

| Prompt invariant | Gate / doc 45 §8 | Followed |
|---|---|---|
| 1. "Radiology is a **label**; no identifier, scope, role or enum is renamed" | Gate 1: "This is a FULL rename, not a label change" | **the gate** |
| 3. "**E/M codes are never orderable**" | Gate 2 + §8.3: E/M creates a **Referral** | **the gate** |

Doc 45 is declared AUTHORITATIVE by the prompt's own Context section, so the stale header was treated as
stale. Gate 8's instruction to record "why identifiers were left alone" is likewise inapplicable — they were
not left alone.

## 1. The rename is a full identifier rename, executed as a window

**Decision.** `imaging_tech` → `radiology_tech`, `provider_type`/`service_type` `'Imaging'` → `'Radiology'`,
`OrderType.Imaging` → `OrderType.Radiology`, SPA permission keys `imaging.*` → `radiology.*`, portal base
`/imaging` → `/radiology`, and every user-facing string. Executed **expand → backfill → switch → contract**.

**Why a window rather than a cutover.** Three artefacts outlive the deploy that renames them, and a fourth is
implied by the architecture:

1. An **unexpired access token** names the old role for the rest of its 300 s TTL.
2. **Services deploy independently**, so the switched issuer mints the new name at a service that has not been
   redeployed. This is the case that recurs on *every* rollout, not only in the first 300 s — and it is why
   the alias expansion is **bidirectional** rather than a one-way normalisation to canonical.
3. **In-flight outbox events** carry the old value and relay after the switch.
4. **The audit chain is hash-linked**, so historical rows can never be rewritten.

**Consequences.**

- `libs/auth/LegacyRoleAliases` expands roles at the single token→principal boundary, so ~40 downstream
  comparison sites needed no dual check each.
- The **contract step is structurally deferred**: it lives in `Migrations/deferred/`, which the migration
  runner does not glob. Had it sat beside the others it would have applied on the same deploy as the switch,
  and the window would have been zero seconds wide. Applied by `tools/ci/apply-deferred-migrations.sh` once
  the runbook's preconditions are verified.
- The **frozen token contract is amended, not broken** — `docs/security/token-contract.md` §2 now names
  `radiology_tech`, and `TokenContractByteCompatTests` carries a checked-in pre-switch fixture proving a
  token minted before the switch still authorises with its scopes and provider binding unchanged.
- The SPA carries its **own** dual-accept, because it reads the raw `roles` claim and never sees the server's
  alias expansion. Without it a mid-deploy technician gets "No portal assigned".

### What was deliberately NOT renamed

| Left alone | Why |
|---|---|
| The **benefit category** `IMAGING` | A *coverage* vocabulary: a CHECK in policy 0001 and eligibility 0006, a seeded row, a value inside every plan's limits, and the key claims and interop map against. Doc 45 §1 renames the role, scopes, provider type, order type, events, portal base and UI strings — not this. Renaming it would rewrite live benefit accumulators to chase a label. `BenefitCategoryMap` is the seam. |
| **Historical audit rows** | Hash-chained. `services/audit/Domain/LegacyIdentifierDisplay` resolves them for readers, **permanently**; the rows are never updated. Two tests pin this: one that the row still hashes to what it hashed to, and a counter-proof that rewriting it breaks the chain. |
| **OAuth scopes** | Doc 45 §1's table says `imaging:*` → `radiology:*`, but no OAuth scope was ever spelled that way — the technician's capabilities are `orders:read`/`orders:consume`, shared with the lab bench. The `imaging.*` identifiers that existed are the SPA's client-side permission keys, which *were* renamed. |
| **The reporting consumer's translation** | `ProjectionMapping` maps `'Imaging'` → Radiology **forever**, because it maps historical events and the projection is replayed from the log. Dropping it at the contract step would split years of radiology volume across two dimension values. |

## 2. OP Procedures are an order type, not a new service

**Decision.** `order_type = 'Procedure'` reuses `orders-service` entirely — the same consume, authorisation
routing, validity stamping and claim path.

**Why.** Building a parallel mechanism would fork the consume/authorise/claim path that took several phases to
get right, including its concurrency proofs. Sessions are the **order line's quantity**, not a parallel
counter, for exactly the same reason: a second counter would need its own atomicity, idempotency and no-reuse
guarantees, and the first time the two disagreed one of them would be the one the claim was built from.

**E/M creates a Referral.** A referral needs its loop closed with a report back; a procedure needs fulfilment
and consumption. Route E/M to a procedure and the loop is never opened, so it can never be found open — the
classic outpatient patient-safety failure.

### Two reconciliation findings, reported rather than silently resolved

1. **`cpt_code.category` cannot drive routing.** The gate says to build the map from it. Its loaded values
   are `Category I` (9,584), `Category II` (565), `Category III` (383), `PLA` (265), `MAAA` (13) — the CPT
   *taxonomy*, recording how a code was adopted into the book, not whether it is a scan or an office visit.
   Routing on it would send a chest x-ray and a hysterectomy down one identical path. Routing derives from
   the code's numeric range via `CptSections`.
2. **Doc 45 §2's Medicine (90281–99607) and E/M (99202–99499) ranges overlap.** Read literally, every
   office-visit code is both a Procedure order and a Referral. Resolved to Referral, which is the section's
   plain intent.

Both are emitted with every catalogue load by `CptRoutingReconciliation`, against all 10,810 real codes.

## 3. The external provider portal binds ownership at the row

**Decision.** `orders.investigation_order.assigned_provider_id`, checked by a pure `ProviderOwnership`
function, with the two-provider test written **before** the queue endpoint.

**Why.** Audit R3 found `DispensingGate` building its ABAC resource as `ProviderId = p.ProviderId` — the
caller's *own* id — so the ownership rule compared the caller against themselves and any authenticated
pharmacist browsed the whole network queue. Nothing failed: no error, no empty screen, just other pharmacies'
work in the list, which looks like a busy queue. No test caught it because answering "can provider A see
provider B's work?" requires **two** providers and every test had one.

**Consequences.** Not-yours returns **404, not 403** — a 403 confirms the order exists, which to a competitor
centre holding an order number is a membership oracle. The queue carries **no beneficiary name**: it is a list
of work, and identity is verified at the counter behind two identifiers.

## 4. The four chronic decisions, and the one thing they left open

The four were settled before this phase and are not re-opened: one authorisation for the whole script with
eligibility re-validated at each dispense; limits consumed per dispense as collected; rounding to the sub-unit
where the form allows splitting a pack; fixed windows with an early tolerance, a missed window forfeited.

**What they left open was the window model**, and the decision taken is:

> **The counter enforces; the sweeper records.**

Windows are materialised at submission. `Blocked` and `Missed` are **stored**, because both are events with
money consequences needing a timestamp and an actor. **`Open` is never written** — dispensability is computed
from `opens_at`/`closes_at` at read time.

**Why that split.** If the sweeper had to promote windows to `Open`, an outage would leave every window
`Pending` and a counter keying on the status would turn a background-job failure into patients being turned
away. Conversely `closes_at` is in the counter's predicate, so a stalled sweeper cannot let a forfeited window
be collected either. The sweeper's blast radius is reduced to the one thing it is genuinely authoritative
about: the record.

**Round once, at the total.** Rounding per window lets the sum drift *above* the prescribed amount — 100 over
three windows becomes 34+34+34 = 102 — over-supplying the patient and over-consuming their benefit, silently,
because every individual window looks like a sensible number. The allocation was written test-first, and the
sum invariant is a **property** test, not four examples.

## 5. Price comparison is per prescribing unit

**Decision.** The equivalence group is **active ingredient + strength + dosage form**, and the comparison is
`price_egp ÷ pack_size`.

**Why.** Ingredient alone is not a valid group — a 500 mg tablet and a 250 mg/5 mL syrup share an ingredient
and cannot be compared. And a 20-tablet pack at 100 EGP is *more* expensive per tablet than a 30-tablet pack
at 120 EGP, so labelling by pack price would point a prescriber trying to save a beneficiary money at the
dearer box.

**A drug with no pack size is never labelled.** Falling back to the pack price is precisely the error the
decision exists to prevent; an absent label says "not compared", a wrong one says "cheapest".

**`availability` is three-valued and defaults to `Unknown`, which renders nothing.** A boolean defaulting to
false would show all 31,651 drugs as out of stock on day one, and prescribers would learn to ignore the
indicator before it ever carried real data.

## 6. Drug-master pack data

`pack_size` maps to **X "Minor Units (total)"** at 100% coverage. **W "Major Units (per box)" is
strips-per-box** — a 20-tablet pack is `W=2, X=20` — and mapping it would make every tablet quantity out by a
factor of ten. Recorded in the mapper as well as in
[`29-6-drug-workbook-pack-column-mapping.md`](../decisions/29-6-drug-workbook-pack-column-mapping.md).

The 11% of rows with no derivable dosage form set `unit_data_incomplete` and report **NotChecked naming the
missing field** — never a guessed quantity, because a silently wrong quantity is a dispensing error.

# inventory-service

Clinic stock for the six Mersal branches: medical and non-medical consumables, on an append-only movement
ledger. Phase 25.5/25.6 · design [`42-branch-management.md`](../../HBMP-Design/42-branch-management.md) §5 ·
[ADR-0029](../../docs/adr/0029-branch-management.md).

## What it is, and what it deliberately is not

It is the storekeeping system for a clinic: what is on the shelf, what came in, what went out, what is running
low, and what expires next month.

**It is not a second dispensing path, and that is the most important sentence in this README.** Anything
requiring a prescription goes through `pharmacy-service`, against an `Rx`, with the authorization and benefit
rules that entails. If clinic inventory could issue medication to a beneficiary it would be a route around
eligibility, coverage limits, formulary and the dispense audit trail — every control the platform exists to
enforce, bypassed by a service that was never designed to enforce them.

So: **no endpoint here accepts a beneficiary identifier, and no column stores one.** Not a `beneficiary_id`,
not a `patient_id`, not an `encounter_id` "just for costing" (that is decision D2, and it is the change that
would quietly make this service PHI). `NoPhiInInventoryTests` asserts this over the routes, the request
contracts, the domain entities and the live schema, and its failure message says what the change would cost.

Keeping inventory PHI-free is also what lets a storekeeper use it without holding a clinical role.

## The two rules the schema exists to enforce

**1. On-hand is derived, never stored.** There is no `quantity_on_hand` column anywhere. On-hand is
`SUM(quantity)` over `stock_movement`, exposed by the `stock_on_hand` view. A balance you can recompute is a
balance you can reconcile, and a balance you cannot reconcile is a number people stop trusting. A physical
stock-take is a `Count` movement recording the **variance**, not an overwrite of history.

**2. The ledger is append-only.** `REVOKE UPDATE, DELETE` from `hbmp_app`, plus a trigger that refuses both
even for a mis-granted role — the same belt-and-braces as `approvals.authorization_decision`. A mistake is
corrected by a further movement, which is what keeps the history reconstructable.

## Things worth knowing before you change something

**Movement signs live on the row.** Callers send a positive magnitude and a kind; the ledger applies the sign.
An API that made clients send `-5` for an issue would eventually receive `+5` for one, and the balance would
be wrong in the direction nobody checks. `Adjustment` and `Count` are the two kinds that keep the caller's
sign, because both record a variance and a variance goes in both directions.

**Negative on-hand is prevented by an advisory lock, not by a row lock — and the difference is a bug we
shipped and caught.** The first implementation took `SELECT ... FOR UPDATE` over the movement rows. That locks
rows which *exist*; it does nothing about a concurrent INSERT. Two callers issuing the last unit each locked
the same receipt row, each computed on-hand = 1 against a snapshot taken before the other's insert, and both
succeeded — leaving −1 on the shelf. On a derived balance there is no balance row to lock instead, because not
storing one is the whole design. `pg_advisory_xact_lock` keyed on (branch, item, batch) locks the stock line
as a *concept*: it needs no row to exist, and it is released on commit or rollback with nothing to clean up.
`PARALLEL_ISSUE_OF_THE_LAST_UNIT_YIELDS_EXACTLY_ONE_SUCCESS` is the test that found this.

**`Idempotency-Key` is required on every movement.** A double-posted receipt is a phantom stock level and the
ledger has no UPDATE to correct it with — only a compensating movement, which leaves two rows where one
belonged. The key must be stable per **intent**, never per attempt.

**Transfers are two paired movements** sharing a `transfer_ref`, written in one transaction, summing to zero.
Each clinic's ledger then stands alone *and* the network total is unchanged. A single "move" row would make
each branch's balance unexplainable without reading the other's.

**Expired medical stock is quarantined, not deleted.** `Issue` against an expired batch is refused
(`urn:hbmp:batch-expired`); clearing it requires an explicit `WriteOff` with a reason. `WriteOff`, `Count` and
`Return` are exempt from the expiry check on purpose — that exemption *is* the quarantine mechanism. If expiry
blocked every movement, expired stock could never leave the ledger.

**Controlled substances are excluded by a CHECK constraint** (`is_controlled = false`), not by convention.
Enabling them is therefore a deliberate, reviewable migration rather than a checkbox: a controlled register
needs dual signature, a per-ampoule running balance and regulator-facing reporting, which is a module of its
own. That is decision D1, and it is **provisional pending sponsor sign-off**.

**Medical ⇒ batch-tracked and expiry-tracked**, enforced by CHECK. A medical consumable whose batch nobody
recorded cannot be recalled, and one whose expiry nobody recorded cannot be blocked from issue.

## Reach

Every endpoint is branch-reach checked. A **branch coordinator** sees their own clinic; a **clinics manager**
sees all six in one response. That falls out of `BranchSetScoped` (25.1) — there are no separate "manager"
routes, because two implementations of one rule means the narrower one eventually drifts.

Unlike practitioner administration there is **no network-wide escape hatch**: nobody administers the network's
stock. `BranchReachGuard.IsNetworkWide` is hard-coded `false` here, kept as a named member so the absence is
legible rather than looking forgotten.

## Endpoints

| | |
|---|---|
| `GET/POST /api/v1/inventory/items` | the catalogue (network-wide reference data; create needs the write scope) |
| `GET /api/v1/inventory/stock` | computed on-hand, filterable by `branchId`, `category`, `lowStock`, `expiringWithinDays` |
| `POST /api/v1/inventory/movements` | the ledger write. **`Idempotency-Key` required** |
| `POST /api/v1/inventory/transfers` | the paired movements, atomically |
| `GET /api/v1/inventory/movements` | the ledger, paginated and filterable |
| `GET /api/v1/inventory/alerts` | low stock + expiring 90/60/30 + expired-quarantined |

Scopes: `branch:inventory:read` / `branch:inventory:write` (identity migration `0021`).

## Running the tests

```bash
./dotnet.sh test services/inventory/Tests/Mersal.Inventory.Tests.csproj -c Release --with-db
```

`--with-db` is not optional here. The concurrency proof, the append-only proof and the live-schema no-PHI scan
are all `Skip.If(INVENTORY_TEST_DB is null)`, and a concurrency proof that never runs is worse than none —
it reports green. `INVENTORY_TEST_DB` was added to `tools/ci/print-test-db-env.sh` **with** this service
rather than after it, because every service already on that list was added late, each time because a suite had
been silently skipping.

The DB-backed suites share one collection (`inventory-db`, `DisableParallelization = true`). They share a test
tenant and each cleans up by deleting that tenant's rows, so cross-class parallelism had one class's teardown
wiping another's fixture mid-test — which failed as impossible balances and read exactly like a concurrency
bug in the code under test.

# 10. Atomic idempotent consume/dispense executor invariant

Date: 2026-07-26 (retro-documented in 16.6)
Status: Accepted
Phase: 5 / 6 (documented in 16.6)

## Context

Two flows irreversibly draw down a scarce, money-backed resource: **consuming** an authorized order line
(orders-service, lab/imaging fulfilment) and **dispensing** a prescription line (pharmacy-service). Both must
never double-apply — a retried request, a duplicate click, or two providers racing the same line cannot
consume/dispense twice — while still being safely retryable (at-least-once callers, network retries).

## Decision

Both flows share one executor shape (`ConsumeExecutor` in orders, its twin `DispenseExecutor` in pharmacy):

1. **Append-only event ledger** (`order_fulfillment` / `dispense_event`) with a **UNIQUE idempotency key**.
   The same `Idempotency-Key` replayed returns the original result (read the prior ledger row) — never a
   second draw-down.
2. **Optimistic concurrency on the line** via its `xmin` system column: the state transition is a
   conditional update guarded by the row version, so two concurrent consumers of the same line serialise —
   one wins, the other observes the changed version and is rejected/replayed, never double-applied.
3. **One executor, shared by the endpoint and the tests**, so the invariant is exercised by the same code
   path in production and under the concurrency tests (parallel requests prove single-apply + no reuse).
4. Idempotency-Key is **required** on these mutating endpoints (API convention).

## Consequences

- Exactly-once *effect* under at-least-once *delivery* — the durable guarantee for the only two flows that
  spend real benefit budget.
- The pattern is duplicated (not abstracted into a shared lib) on purpose: the two domains differ enough
  (batch/expiry/substitution in dispense; provider-ownership + result upload in consume) that a forced
  abstraction would leak. The *shape* is kept identical so the invariant reads the same in both.
- Tests are serialized per datastore collection to avoid cross-test races on the shared sequences.

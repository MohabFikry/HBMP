# orders-service

Investigation / radiology / procedure **orders** (Release R2, Phase 4.2 — US-032). Owns the `orders` schema.
A doctor creates an order within an encounter; lines reference **masterdata-validated** codes; high-cost/gated
services route to **Approvals** (phase 7) while the rest auto-activate and become discoverable by authorized
providers (phase 5). This service covers **create + routing**; the atomic-consume fulfillment path is phase 5.

## Create an order (US-032)

`POST /api/v1/investigation-orders` (scope `orders:write`, `Idempotency-Key` required):

1. **Treating-relationship gate** — the ordering doctor must treat the beneficiary. The row-level truth comes
   from **emr-service** (`GET /treating-relationship`, caller's token forwarded, boolean only) and is enforced by
   the shared authorization engine's treating-relationship ABAC condition (`OrdersPolicies`). A non-treating
   doctor → **403** + audit. Same rule as emr-service.
2. **Code validation** — every line's `code` must exist in masterdata for its `code_system` (CPT via
   `/cpt-codes/{code}/exists`; LOINC accepted-and-recorded until a dataset loads; LOCAL free). Unknown → **422**
   problem+json. Fail-closed if masterdata is unreachable.
3. **Route** (`OrderRoutingPolicy`, config-driven `Orders:Routing`) — a gated order type, a gated code, or an
   estimated total cost ≥ `HighCostThreshold` sends the order **Requested → PendingApproval** (emits
   `OrderPendingApproval`); otherwise **Requested → Active** (emits `OrderActivated`). Default: `Imaging` is gated.
4. **Outbox** — `OrderCreated` then `OrderActivated | OrderPendingApproval` are enqueued in the same transaction
   as the state change (destination `orders.events`); consumers dedupe on event id.

Creation is idempotent on `Idempotency-Key` (a replay returns the existing order). Every mutation is audited.

Other endpoints: `GET /api/v1/investigation-orders/{id}` (treating-gated read),
`POST /api/v1/investigation-orders/{id}/cancel` (legal only while not fully consumed → audited `409`
`TransitionDenied` otherwise).

## Domain

- `investigation_order` (`ORD-YYYY-NNNNNN`; status per 23-state-machines §2; `expires_at` validity window; xmin
  RowVersion) + `order_line` (`code_system` CPT/LOINC/LOCAL, `quantity_ordered > 0`, `quantity_consumed`
  accumulator with `CHECK (0 ≤ consumed ≤ ordered)` for phase-5 consume).
- Canonical lifecycle: `Requested → PendingApproval → (Approved | Rejected) → Active → PartiallyUsed → Completed`;
  plus `Expired`, `Cancelled` (`OrderWorkflow`).

## Data

- `Infrastructure/Migrations/0001_orders.sql` — `order_seq`, `investigation_order` (unique `order_no`, partial
  idempotency + expiry indexes, enum + validity CHECKs), `order_line` (accumulator CHECK), `processed_request`.

Apply with `psql`.

## Tests

- `OrderRoutingTests` — gated type / gated code / high-cost → approval; below threshold → auto-activate.
- `OrderWorkflowTests` — the §2 transition table (legal + illegal), cancel guard, `ORD-` formatting.
- `OrderAuthzTests` — a treating doctor may create; a non-treating doctor is denied + audited; a lab tech cannot
  create (default-deny) — exercised against the real authorization engine over `OrdersPolicies`.
- `OrdersIntegrationTests` — order + lines round-trip with the routed status; monotonic order-number issuer; the
  DB enforces the consume accumulator invariant (env-gated `ORDERS_TEST_DB`; green against live PG).

Endpoint wiring (treating gate → 403, code validation → 422, routing → OrderActivated vs OrderPendingApproval,
idempotent replay, outbox, audit) is exercised against the live stack.

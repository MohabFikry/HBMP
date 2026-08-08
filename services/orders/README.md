# orders-service

Investigation / radiology / procedure **orders** and their **lab/imaging fulfillment** (Release R2 create/route,
R3 fulfilment — US-032/US-040/US-041/US-042). Owns the `orders` schema. A doctor creates an order within an
encounter; lines reference **masterdata-validated** codes; high-cost/gated services route to **Approvals** (phase 7)
while the rest auto-activate and become discoverable by authorized providers. A fulfilling provider (lab/imaging)
sees an authorized **queue**, performs the **atomic idempotent consume** (the heart of the platform), and uploads
**results**.

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

Every order event carries **`encounterId`** (ADR-0031). The column has been on `orders.order` since phase 4 and
was never published, so the visit and the work it caused were two facts with nothing joining them: "what did
this consultation order?" had no answer. emr's care-episode consumer reads these off the `CareFeed` mirror —
its own queue, because the transport is point-to-point and binding it to `orders.events` would make it compete
with policy-service's benefit accumulator. Do not drop the field; `CareFeedEnvelopeArchitectureTests` fails the
build if a publish site does, because the symptom otherwise is a silently missing step.

Creation is idempotent on `Idempotency-Key` (a replay returns the existing order). Every mutation is audited.

Other endpoints: `GET /api/v1/investigation-orders/{id}` (treating-gated read),
`POST /api/v1/investigation-orders/{id}/cancel` (legal only while not fully consumed → audited `409`
`TransitionDenied` otherwise).

## Fulfillment — queue / consume / result (phase 5)

A fulfilling provider is a **lab/imaging tech** whose token carries a `provider_id`. Every fulfillment endpoint is
provider-scoped by the shared engine's **provider-ownership** ABAC rule (`ProviderPolicies`) and by a **capability**
match (`ProviderCapability`: `lab_tech → Lab`, `imaging_tech → Imaging`) — a lab tech can never see or fulfil an
imaging order, and neither can read prescriptions/pharmacy data (this service does not expose them). All PHI reads
are audited.

- `GET /investigation-orders/queue` and `GET /investigation-orders/search?patientIdentifier=|orderNo=` (scope
  `orders:read`, US-040) — return only **available** lines (order `Active`/`PartiallyUsed`, line not used/cancelled)
  of the caller's capability, projected to the minimum a fulfiller needs (patient id, line code, **quantity
  remaining**) — never diagnoses/notes. `patientIdentifier` is the beneficiary id.
- `POST /investigation-orders/{orderId}/consume` (scope `orders:consume`, **`Idempotency-Key` required**, US-041) —
  **the atomic, idempotent, duplicate-proof consume** (`ConsumeExecutor`). Three mechanisms combine, all required:
  (1) an append-only `order_fulfillment` insert per line keyed by a **UNIQUE idempotency key**; (2) **optimistic
  concurrency** on the line's `xmin` — the consume `UPDATE` lands only if the line hasn't moved, so exactly one of N
  racers wins; (3) a **required `Idempotency-Key`** — replaying it returns the prior fulfillment with no new row or
  state change. The DB `CHECK (0 ≤ consumed ≤ ordered)` is the final backstop. A used/Completed line can **never** be
  reused (`409`). Partial consume → line/order `PartiallyUsed`, remainder stays `Active`; all lines consumed →
  `Completed`. `OrderLinesConsumed` (+ `OrderCompleted`) emit via the outbox atomically with the state change.
- `POST /investigation-orders/{orderId}/lines/{lineId}/result` (scope `orders:consume`, US-042) — upload result
  value(s) + an optional report; the report goes to **document-service** (scanned, CMK blob) and its ref pins on the
  consumed line's fulfillment row. A result may only be attached to a line **this provider consumed**. Emits
  `OrderResultUploaded` (routed to the ordering doctor, and approvals if the order was gated).
- `GET /investigation-orders/{orderId}/lines/{lineId}/result` (scope `orders:read`) — **min-necessary**: readable
  only by the ordering doctor (treating) or the approval team; anyone else is denied + audited.

### What the bench sees, and what consuming issues (ADR-0034)

- `GET /investigation-orders/{orderId}/pricing` (scope `orders:read`) — what the order costs and how it splits.
  The split is **not** computed here: it comes from `eligibility/check` through `libs/benefit-pricing`, the same
  path claims adjudicates with, so the figure a member is quoted and the figure their claim is charged cannot
  diverge. Catalogue prices come from `masterdata /examination-types/prices/by-codes`, keyed on CODE because an
  order line always carries one and only carries an `examination_type_id` if it was written after phase 14.6.
  **Nothing is ever quoted at zero when it is unknown** — a missing price, an unresolvable tier or a plan that
  does not price LAB/IMAGING all produce `determinate: false` plus a reason. Today that is every order: no
  examination carries a price and no plan version prices either category.
- Consuming a line **issues a fulfilment authorization** (approvals-service). A second, approvals-shaped copy of
  the consume event is enqueued to `approvals.fulfilments` inside the consume transaction — its own queue, because
  `orders.events` is point-to-point and policy-service already consumes it to move the benefit accumulator.

## Domain

- `investigation_order` (`ORD-YYYY-NNNNNN`; status per 23-state-machines §2; `expires_at` validity window; xmin
  RowVersion) + `order_line` (`code_system` CPT/LOINC/LOCAL, `quantity_ordered > 0`, `quantity_consumed`
  accumulator with `CHECK (0 ≤ consumed ≤ ordered)`, xmin RowVersion for the consume guard).
- `order_fulfillment` (append-only, 22-data-dictionary §7.3) — one immutable row per consumed line: `quantity`,
  `idempotency_key` **UNIQUE** (the dedup anchor), optional `result_document_id` + `result_value`, `consumed_by`.
  Never updated (except the one-time result attach) or soft-deleted; full history in `audit_event`.
- Canonical lifecycle: `Requested → PendingApproval → (Approved | Rejected) → Active → PartiallyUsed → Completed`;
  plus `Expired`, `Cancelled` (`OrderWorkflow`). Consume rules live in `OrderConsume` (23 §2 atomic-consume guard).

## Data

- `Infrastructure/Migrations/0001_orders.sql` — `order_seq`, `investigation_order` (unique `order_no`, partial
  idempotency + expiry indexes, enum + validity CHECKs), `order_line` (accumulator CHECK), `processed_request`.
- `Infrastructure/Migrations/0002_fulfillment.sql` — `order_fulfillment` (UNIQUE `idempotency_key`, `quantity > 0`
  CHECK, FK to `order_line`, indexes).

Apply with `psql`.

## Tests

- `OrderRoutingTests` — gated type / gated code / high-cost → approval; below threshold → auto-activate.
- `OrderWorkflowTests` — the §2 transition table (legal + illegal), cancel guard, `ORD-` formatting.
- `OrderAuthzTests` — a treating doctor may create; a non-treating doctor is denied + audited; a lab tech cannot
  create (default-deny) — exercised against the real authorization engine over `OrdersPolicies`.
- `OrdersIntegrationTests` — order + lines round-trip with the routed status; monotonic order-number issuer; the
  DB enforces the consume accumulator invariant (env-gated `ORDERS_TEST_DB`; green against live PG).
- `OrderConsumeTests` — the pure consume rules: partial → PartiallyUsed (remainder Active), all → Completed,
  no-reuse, over-consume, non-consumable order, provider capability mapping.
- `FulfillmentAuthzTests` — a lab tech may read/consume only its OWN provider's work (provider-ownership); it cannot
  reach another facility's queue; a doctor cannot consume; a result is readable by the approval team but not an
  unrelated role — against the real engine over `OrdersPolicies`.
- `OrderConsumeConcurrencyTests` (env-gated `ORDERS_TEST_DB`, real parallel PG transactions, **not mocked**) — N
  racers on one line → **exactly one wins**, `quantity_consumed` never exceeds ordered, **one** fulfillment row;
  replaying an Idempotency-Key adds no row and returns the original; partial-then-remainder → Completed; a used line
  cannot be reused; a result blob ref + value pin onto the consumed fulfillment. Serialized via the `orders-db`
  collection so the many-connection race doesn't collide with other datastore tests.

Endpoint wiring (queue/consume/result auth → 403, code validation → 422, routing → OrderActivated vs
OrderPendingApproval, idempotent replay, outbox, audit, document-service blob attach) is exercised against the live
stack.

# Phase 30 Gate 0 — what already exists for cancel / amend

> Read before building. Design [46](../HBMP-Design/46-order-amendment-and-cancellation.md); prompt
> [phase-30](../HBMP-Design/claude-code-prompts/phase-30-order-amendment.md) Gate 0.
> The instruction was "extend what is there rather than adding a parallel mechanism". This records what
> "there" actually is, including three things the prompt assumed and one it did not.

## The four order kinds are two services and two tables

Lab, radiology and OP procedure are **not three things**. They are one table,
`orders.investigation_order`, discriminated by `order_type` — so they share one status vocabulary, one
consume path and one set of amendment columns. Prescriptions are the separate case, in pharmacy-service.

That halves Gate 1's migration surface, and it means a bug fixed on the lab path is fixed on the
radiology and procedure paths by construction rather than by remembering.

## Per order kind

| | **Prescription** (pharmacy) | **Lab / Radiology / OP Procedure** (orders) |
|---|---|---|
| Head table | `pharmacy.prescription` | `orders.investigation_order` (`order_type` discriminates) |
| Head statuses | Draft, Submitted, Approved, Rejected, PartiallyDispensed, Dispensed, Expired, **Cancelled** | Requested, PendingApproval, Approved, Rejected, Active, PartiallyUsed, Completed, Expired, **Cancelled** |
| Terminal | Rejected, Dispensed, Expired, Cancelled | Rejected, Completed, Expired, Cancelled |
| Line table | `pharmacy.prescription_line` | `orders.order_line` |
| Line statuses | Active, PartiallyDispensed, Dispensed, **Cancelled** | Active, PartiallyUsed, Completed, **Cancelled** |
| `Superseded` | **absent** — Gate 1 adds it to all four CHECKs | **absent** |
| Cancel endpoint | `POST /prescriptions/{id}/cancel` (`Prescriptions.cs:314`), scope `rx:write` | `POST /investigation-orders/{id}/cancel` (`Orders.cs:244`), scope `orders:write` |
| Amend endpoint | **none** | **none** |
| Consume guard | `DispenseExecutor` | `ConsumeExecutor` |
| Guards on | UNIQUE idempotency key on `dispense_event` **+ line `xmin`** + request-hash replay | UNIQUE idempotency key on `order_fulfillment` **+ line `xmin`** + request-hash replay, then a compare-and-set roll-up |
| Fulfilment queue | `Dispensing.Outstanding()` | `Queue.AvailableOrders()`; `ProcedureProvider.Owned()` for the centre portal |
| Queue filters on | head status ∈ {Approved, PartiallyDispensed, Expired} **AND** ∃ line ∈ {Active, PartiallyDispensed} with `dispensed < prescribed` | head status ∈ {Active, PartiallyUsed, Expired} **AND** ∃ line ∈ {Active, PartiallyUsed}; plus `order_type ∈ caller capability`, or `assigned_provider_id = caller` for procedures |

## The existing cancel endpoints are the thing Gate 2 exists to fix

Both are the same shape, and both have the same three defects:

1. **Read-then-write.** `FirstOrDefaultAsync` → `CanCancel(status)` → mutate → `SaveChangesAsync`. This is
   precisely the lost-update the consume path defends against, on the same rows.
2. **Order level, not line level.** Both do
   `foreach (var l in ...Where(l => l.Status == Active)) l.Status = Cancelled` — an all-or-nothing sweep
   with no notion of a partly-consumed order and no partial-success report.
3. **Free-text reason, no idempotency, no version.** `req.Reason` goes straight into the event and the
   audit row. No coded vocabulary, no `Idempotency-Key`, no `version_no`.

They do get two things right, and both are worth keeping: the state change and its event share **one
transaction** (`BeginTransactionAsync` … `CommitAsync`), and both audit.

**Neither is deleted.** Gate 1/2 rewrites their bodies onto the guarded line-level path and leaves the
routes, the scopes and the ABAC gates where they are.

## The `xmin` guard Gate 2 asks for is already mapped

`row_version` exists on both line entities, mapped to Postgres `xmin`:

```csharp
e.Property(x => x.RowVersion).HasColumnName("xmin").HasColumnType("xid").IsRowVersion();
```

So `UPDATE … WHERE status IN (…) AND row_version = @expected` needs no migration and no new mechanism —
it is the same optimistic-concurrency guard the dispense and consume paths already win their races with.

## Events published on consume, and who actually consumes them

| Event | Destination | Consumer |
|---|---|---|
| `OrderLinesConsumed` | `orders.events` | policy-service `BenefitConsumptionConsumer` |
| `OrderCompleted` | `orders.events` | policy-service `BenefitConsumptionConsumer` |
| `ProcedureSessionDelivered`, `ProcedureLoopClosed` | `orders.events` | policy-service `BenefitConsumptionConsumer` |
| `FulfilmentRecorded` (orders **and** pharmacy) | `approvals.fulfilments` | approvals-service `FulfilmentConsumer` |
| dispense events | `pharmacy.events` | policy-service `BenefitConsumptionConsumer` |
| `OrderCancelled` | `orders.events` | policy-service `BenefitConsumptionConsumer` |
| `RxCancelled` | `pharmacy.events` | policy-service `BenefitConsumptionConsumer` |

**The transport is point-to-point.** `orders.events` and `pharmacy.events` are RabbitMQ queues, not
fan-out exchanges, and policy-service is already bound to both. A second consumer bound there would
**compete** for messages: roughly half the benefit-accumulator movements would silently stop arriving.
The codebase says so in five places, and approvals and notification both solved it the same way — the
publisher enqueues a **second, differently-shaped copy** to the consumer's own queue.

Gate 5 must follow that, not bind a new consumer to `orders.events`.

## The finding that changes Gate 5

**The fulfilment queues are not read models.** They are live queries over the owning service's own tables
(`db.Orders`, `db.Prescriptions`) — see the filter rows in the table above.

So for the lab bench, the radiology bench, the procedure centre and the pharmacy counter, cancelling a
line **removes it from that provider's queue in the same transaction as the cancellation**. No event, no
consumer, no propagation SLA, no window in which a cancelled order is still sitting in the queue.

This does not weaken invariant 6 — it satisfies it structurally, which is stronger than satisfying it
eventually. It does change what Gate 5 has to build: the acceptance test still asserts against the queue
endpoint (and will pass synchronously), and the events are for the parties who are **not** reading the
owning service's tables — beneficiary, ordering doctor, case manager, approvals, claims.

Any read model that *is* downstream — reporting-service projections — still needs its event.

## Three things the prompt assumed that are not there

1. **`tools/ci/check-event-symmetry.py` does not exist.** Gate 5 says "run the phase-24 event-symmetry
   gate before and after". Phase-22's prompt specified it (line 142) and it was never built — `tools/ci/`
   has no such file. So the "~40 published event types with no subscriber" figure
   is an unverified claim from a prompt, not a measurement — nothing is currently counting.
   **Consequence:** "symmetry gate green" cannot be an acceptance criterion for this phase until the gate
   exists. Gate 5 will assert each new event's subscriber by a named test instead, and the gate itself is
   phase-22 work that should be recorded as outstanding rather than quietly assumed done.

2. **`orders` and `pharmacy` have no `*_history` twins.** Gate 1 says "the `*_history` twins … are
   untouched by this; do not rewrite either". There are none to leave untouched — ten other services have
   them, these two do not. Nothing to do, but the instruction should not be read as confirmation they
   exist.

3. **Nothing writes `CareSteps.CheckedIn`.** Gate 5c's "the timeline opens at Visit started" has a cause:
   of the 20 declared care steps, `Booked`, `Rescheduled`, `CheckedIn`, `NoShow` and `Cancelled` are
   declared and **never written by any service**. `VisitStarted` is written (`emr/Api/Program.cs:254`).
   The timeline does not begin at check-in because check-in has never been recorded on it.

   Worse for the waiting-time derivation: **`emr.appointment` has no `checked_in_at` column.** Check-in
   sets `status = 'CheckedIn'` and stamps `updated_at`, which every later transition overwrites. The only
   durable timestamp is `QueueTicket.EnqueuedAt`. Gate 5c therefore needs a real check-in timestamp
   before waiting time can be derived from anything trustworthy — deriving it from `updated_at` would
   produce a number that silently degrades as the appointment is touched again.

## Notes: the model to reuse is `policy.note` (design 38 §5)

`policy.note` (migration `0009_note.sql`) is the implementation Gate 5b must copy: append-only enforced
by a **database trigger** (not just a 409), author snapshotted at write time rather than joined,
cancellable-never-deletable with a mandatory reason, visibility class that may be **raised but never
lowered**, tenant RLS, no DELETE grant. `patient.registration_note` and `emr.emr_note` are the other two
implementations; Gate 5b's order notes must be the fourth *use* of the first model, not a fourth model.

## What Gate 1 onward will do

- **Extend** both `/cancel` endpoints onto a guarded, line-level, coded-reason path — same routes, same
  scopes, same gates.
- **Reuse** `xmin` / `row_version`, already mapped, for the guarded transition.
- **Reuse** `ConsumeExecutor` / `DispenseExecutor`'s idempotency-key + request-hash discipline.
- **Reuse** `policy.note`'s schema and trigger for order notes.
- **Reuse** `DecisionRules.ValidatePartialScope` for the code-set dimension of the authorisation check.
- **Add** `Superseded` to four status CHECKs, the version/supersedes columns, and `amendment_reason`.
- **Add** a `checked_in_at` on the appointment, and write the `CheckedIn` care step.
- **Not add** a second notes model, a second concurrency mechanism, or a consumer bound to
  `orders.events` / `pharmacy.events`.

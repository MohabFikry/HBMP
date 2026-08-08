# Phase 30 Gate 5 — propagation, and three things that turned out to be structural

> Design [46 §6](../HBMP-Design/46-order-amendment-and-cancellation.md): *"A notification is not
> propagation. The failure mode is a cancelled order that still sits in the lab's queue because only an email
> was sent."*

## What was built

| Recipient | How it is reached | Subscriber |
|---|---|---|
| **The fulfilling provider's queue** | nothing — it is a live query over `order_line` / `prescription_line`, so the line leaves in the **same transaction** | — |
| **The care-episode timeline** | `OrderLineCancelled` / `OrderLineAmended` / `PrescriptionLineCancelled` / `PrescriptionLineAmended` on the domain stream | emr-service, via the `CareFeed` mirror + four new care steps |
| **Beneficiary, ordering doctor, fulfilling provider** | a second, notification-shaped copy on `notification.domain-events` | notification-service (template-driven; no new consumer) |
| **The approval team** | `OrderPendingApproval` / `RxSubmitted` re-emitted with a before/after — **only when out of scope** (Gate 4) | whatever routes a newly-gated order |
| **Claims** | nothing — see below | — |

Four new care steps rather than reusing `OrderCancelled` / `PrescriptionCancelled`: withdrawing **one test**
from a three-line order is a different fact from withdrawing the order, and a timeline that said the latter
would have a desk telling a patient their bloods were cancelled when two of the three still stand.
`Amended` is separate again — the item was not withdrawn, it was changed, and the successor is live; a reader
who saw "cancelled" would chase a replacement that already exists.

## 1. The provider queue needs no event, and that is stronger than one

The Gate 0 audit found these queues are **live queries** over the owning service's own tables, not read
models. So a cancelled line leaves the bench, the counter and the centre's portal in the same transaction as
the cancellation: no consumer, no SLA, no window in which a withdrawn order is still offered.

`CancellationLeavesTheProviderQueueTests` asserts this with **no wait, no poll and no eventual assertion** —
if it ever needed one, the queue would have become a projection and invariant 6 would have weakened without
anyone deciding to weaken it.

## 2. Claims needs no reconciliation event, because a claimed item cannot be amended

Design 46 §6 says a claimed item's amendment is "a reconciliation event, not a silent edit". Following it
through:

- Claiming follows **fulfilment** — `claim_line.fulfillment_ref` is what makes a line count as delivered in
  `ReconciliationQueries.ListAsync`, which is itself a derived query rather than an event-fed projection.
- A **fully** consumed line is terminal and cannot be amended or cancelled at all (Gate 1).
- A **partly** consumed line can be cancelled, but that forfeits only the *unconsumed* remainder — the
  consumed portion is immutable (invariant 2), so the part that could have been claimed is exactly the part
  amendment cannot touch.

So there is no state in which an amendment invalidates a claim. **Invariant 2 already covers §6's claims
row.** Emitting a reconciliation event anyway would put entries in a finance worklist describing a
discrepancy that cannot exist — noise on a control whose value is that its entries are real.

If claiming ever moves ahead of fulfilment, this stops holding, and the reasoning above is the thing to
re-check.

## 3. A guard caught two shortcuts, and it was right both times

`CareFeedEnvelopeArchitectureTests` scans source for a mirrored event's **name** and reads the **payload
literal** beside it, to prove `encounterId` is on the wire. `TenantOnEnvelopeArchitectureTests` does the same
for `tenantId`.

Both shortcuts I took hid the thing being checked:

1. Publishing under a constant (`AmendmentEvents.LineCancelled`) made the name invisible to the scan.
2. Building the payload in a shared helper made `encounterId` invisible to it.

The name and the payload are now **literal at every enqueue site**, and the domain-payload builders are
gone. That is duplication, deliberately: a mirrored event missing its encounter does not fail, warn or
dead-letter — the consumer correctly declines to place the step, acks, and the timeline is quietly missing
the order. That is the exact defect the scan was written for, and it has already happened once on this
codebase.

This is the third time this session a source-scanning guard has caught a helper hiding what it checks
(`OutboxAtomicityTests` was the first). The pattern is worth naming: **a shared helper is the wrong tool for
anything a build-time scan is asserting about a call site.**

## Diagnosed and fixed: `GET /investigation-orders/queue` returned 500

It was **two defects that hid each other**, and the second is why the first survived from phase 5 to phase 30.

**The production bug.** The handler declared `int page, int pageSize` — non-nullable, no defaults. A call with
no query string, which is the natural one and the one the bench screen makes, died in the model binder before
the handler ran. The `Page()` helper directly below it has always clamped and defaulted both values; nothing
ever let it. The procedure-centre queue (`ProcedureProvider.cs`) has always used the nullable form, which is
why that one worked. Fixed: `int? page, int? pageSize`.

**The fixture that kept it unreachable.** `OrdersApiFactory.LabClient` granted `orders:consume orders:read`.
A real `lab_tech` token carries `provider:read` as well (`identity.role_scope`), and
`ProviderPolicies.QueueRead` requires it — so the fixture's technician could consume but could never read a
queue. No test could reach the endpoint to find the 500.

That is the more useful half of the finding. **A fixture that grants LESS than the real token silently puts
endpoints out of reach**, and the gap reads as "no coverage here" rather than as "this cannot be called". The
mirror mistake — granting more — hides authorization defects instead. `LabClient` now mirrors the issuer.

The propagation tests assert through the real endpoint, and one of them exists solely to make the regression
explicit: `The_bench_queue_answers_a_call_with_no_query_string`.

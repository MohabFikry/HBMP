# ADR-0041 — Closing the prior-authorization saga: a routed request reaches a reviewer, and the decision comes back

**Status:** **Accepted** · **Date:** 2026-08-09 · **Phase:** audit remediation (2026-08-09 platform audit, C1+C2)
**Relates to:** [ADR-0031](0031-care-episode-timeline.md) (the mirror pattern),
[ADR-0034](0034-fulfilment-authorization.md) (the fulfilment consumer this is modelled on),
`23-state-machines.md §2, §3, §5`, `24-sequence-diagrams.md` SEQ-3,
`docs/AUDIT-2026-08-09-PLATFORM-WIDE.md` (C1, C2)

---

## Context

The platform's headline benefit-control workflow — a clinician orders something gated, an approval team
decides, the thing becomes deliverable or does not — **was not connected at either end**. Both ends were
built. Neither was wired.

**Forward.** orders-service published `OrderPendingApproval` to `orders.events` and pharmacy-service
published `RxSubmitted` to `pharmacy.events`. approvals-service consumed neither. Its ingestion endpoint,
`POST /api/v1/authorizations` (scope `auth:ingest`), was written in phase 7 with a comment naming its caller
— "the phase-4 routing saga / the `OrderPendingApproval`|`RxSubmitted` event consumer" — and that caller was
never built. `auth:ingest` appeared in the scope catalogue, on the endpoint, and in comments; it was held by
nobody. So a gated order changed status to `PendingApproval`, told the patient to wait, and reached no
reviewer worklist. The only authorizations that ever existed were ones a human raised by hand and the
fulfilment records ADR-0034 issues after the fact.

**Return.** No service consumed `approvals.events` at all. `OrderWorkflow` has declared
`PendingApproval → Approved → Active` since phase 4 and `PrescriptionWorkflow` has declared
`Submitted → Approved`; nothing executed either. The consequences compound:

- **A gated prescription could never be dispensed.** `IsDispensable` admits only `Approved` and
  `PartiallyDispensed`, and the only path that ever set `Approved` was the auto-route at creation — for
  scripts that needed no approval. A script that WAS reviewed stayed `Submitted` for ever, and the counter
  refused it, correctly, while the reviewer's screen said Approved.
- **Rejection had no effect and no compensation.** A rejected order stayed `PendingApproval`, which is
  exactly what a still-queued order looks like. No screen anywhere could distinguish "refused" from
  "waiting", so the only honest thing a desk could tell a patient was nothing.

Both services' READMEs documented the missing consumer as future work. Two years of phases were built on top.

---

## Decision 1 — Both legs travel as MIRRORED events, not as HTTP calls

The transport is point-to-point: `RabbitMqEventPublisher` publishes to the default exchange with the
destination as the routing key, so everything bound to a queue **competes** for its messages. policy-service
is already bound to `orders.events` and `pharmacy.events` for the benefit accumulator, so a second consumer
on either would take roughly half of each stream and the accumulator would silently stop moving. Each
consumer therefore gets its own copy, exactly as `ProjectionFeed` and `CareFeed` do:

| Feed | Queue(s) | Events |
|---|---|---|
| `ApprovalRoutingFeed` | `approvals.routing-events` | `OrderPendingApproval`, `RxSubmitted` |
| `ApprovalDecisionFeed` | `orders.approval-decisions`, `pharmacy.approval-decisions` | `AuthApproved`, `AuthPartiallyApproved`, `AuthRejected`, `AuthOverridden`, `AuthEmergencyApproved` |

**Not a second enqueue at each call site**, which is how `FulfilmentRecorded` reaches
`approvals.fulfilments`. That pattern is right when the producer knows something the consumer cannot work
out — a fulfilment carries the delivered items, which only the dispensing service holds. Routing needs
nothing of the kind: ADR-0031 already made both events carry the tenant, the encounter and the ordering
clinician. A second enqueue would be a second thing to forget at the fourth call site, and both services
re-emit these on an out-of-scope amendment (design 46 §5).

**Not a synchronous callback**, which is how an approved *validity extension* travels
(`HttpValidityExtensionApplier`). That one is coupled on purpose: the reviewer must get both the decision and
the new expiry or neither, because an authorization reading Approved beside a prescription the counter still
refuses is a contradiction the pharmacist cannot resolve. An ordinary decision has no such coupling —
`Decisions.Decide` has always documented the release as something "consumers of the emitted event" do — and
making it synchronous would mean a reviewer could not reject a request while orders-service was restarting.

**Both decision queues receive every decision and filter by `source`.** Routing by source at the relay would
put approvals' `AuthSource` vocabulary in the publisher and require it to parse payloads. Filtering costs a
discarded message; mis-routing costs a decision that reaches nobody, and there is no third party to notice.

`AuthInfoRequested` is deliberately absent from the decision feed: it is a reviewer asking a question, not an
answer. The order stays `PendingApproval` and the prescription stays `Submitted`, which is already true.

---

## Decision 2 — The routing consumer is the ingestion endpoint's missing caller, not a loopback client

`RoutedAuthorizationIngestor` creates the same row `POST /api/v1/authorizations` creates: `Kind = Review`,
`Status = Submitted`, a `ProcessedRequest` idempotency row. Deliberately the same object, so a request that
arrived by event and one that arrived by HTTP are indistinguishable to every reviewer, report and decision
path downstream. What it does not reuse is the HTTP plumbing: no machine token, no loopback call, no second
network hop between two services already talking.

**Idempotency is keyed on the EVENT ID, never on the order.** An amendment that leaves the approved scope
re-publishes the same event for the same order (design 46 §5) and that second request is a real one — the
authorisation's basis no longer holds and a reviewer must look again. Deduping on `(source, sourceRef)` would
swallow it and leave the order in `PendingApproval` with nothing in any queue. The `processed_event` ledger
short-circuits redelivery; the PRIMARY KEY on `processed_request` is what holds when two deliveries of one
message race at prefetch 20.

**`RxSubmitted` is filtered on `requiresApproval`.** pharmacy emits it for every prescription; the routing
outcome is the flag, not the event name. An ungated script is acked and forgotten — raising an authorization
for each would put a few hundred a day into a queue whose entire value is that everything in it needs a
decision.

---

## Decision 3 — An authorization must be ATTRIBUTABLE, which is not the same as provider-raised

Phase 7 wrote `CHECK (source = 'Manual' OR requesting_provider_id IS NOT NULL)` — "manual authorizations have
no requesting provider; all others must name one". It held for every path that actually created a row,
because each was raised by a provider-scoped account: a pharmacist asking for a validity extension, a
technician proposing a substitution.

**The two sources it was written for never created a row at all**, so nothing ever tested the rule against the
case it names — and it does not hold there. A doctor's token is practitioner-scoped and carries no
`provider_id`, which is why `pharmacy.prescription` has no such column and an order raised in a Mersal branch
carries `ordering_branch_id` instead. Enforcing it would mean dead-lettering every gated prescription in the
platform to satisfy a field with nothing to put in it.

Migration `approvals/0010` restates the rule as the property it was reaching for: a provider that raised it,
**or** a person who did (`created_by`, which carries the ordering clinician and is also who the decision
notice is addressed to). This is a widening — every row that satisfied the old constraint satisfies the new
one — and the endpoint's own 422 is unchanged, because there a missing provider means "this system cannot say
who is asking".

---

## Decision 4 — A partial approval is applied at the CODE level, and refused lines are cancelled with a reason

The decision contract carries a list of **codes** and nothing else: `DecisionRules.ValidatePartialScope`
checks the reviewer's scope is a strict, non-empty subset of the requested codes. There is no quantity
anywhere in it. So "partially approved" means these codes yes, those no — the refused lines are cancelled and
the allowed ones are untouched. A two-test order with one refusal is still one test the patient has today.

**Not `ProcedureSessions.ApplyApproval`**, despite its own summary saying it is "applied when an approval
decision is recorded". It narrows `QuantityOrdered`, and orders 0013's signed-content trigger freezes that
column against in-place update — the write raises *"order line … is signed clinical content and can never be
edited in place"*. The method has only ever been called on detached objects in its own tests, and no decision
path can currently produce a quantity-level approval. That mismatch is recorded here rather than papered
over; changing it means either putting quantities in the decision contract or superseding the line, and
neither belongs in this change.

Cancelling a line requires **why, who and when** (`ck_order_line_amendment_attributed` / its pharmacy twin) —
"a line that left the live set says why, who and when, or it did not leave it". The actor is the **reviewer**,
carried on the decision event: attributing it to a background consumer would put a machine's name on the row
a dispute is read back from. `amendment_reason` is a closed vocabulary with no entry for this, and
`NotEligible` would have been a false sentence (the patient may be perfectly eligible; one item was refused),
so orders 0017 and pharmacy 0019 add `not-in-approved-scope`.

**Rejection changes the status and not the lines.** The technician's queue is a live query admitting
Active / PartiallyUsed / Expired, so `Rejected` removes the order from every worklist in the same
transaction; `IsDispensable` excludes `Rejected` likewise. Cancelling the lines as well would record a
line-level withdrawal that nobody performed.

---

## Decision 5 — An approved order goes all the way to Active in one transaction

23 §2 lists `approve` (approval team) and `activate` (orders-service) as two rows, and both events are
emitted. But there is no state anyone can act on in between and no second trigger to wait for: leaving an
order `Approved`-but-not-`Active` would mean a technician with the patient in front of them seeing an empty
queue — the failure this whole path exists to remove.

pharmacy has no equivalent second hop: `Approved` is already the dispensable state. It re-uses the existing
`RxApproved` event with `auto: false` rather than inventing a second name, so no consumer has to handle two
event types to answer "is this script live?".

---

## Consequences

- **`auth:ingest` has a caller.** The reviewer worklist now contains the requests it was built for.
- **A gated prescription can be dispensed after approval.** That sentence was false from phase 4 until now.
- **Rejection is visible and terminal** on both sides, so "refused" and "waiting" are finally different states.
- **Two services became event consumers for the first time**, each gaining a `processed_event` dedupe ledger
  (orders 0017, pharmacy 0019) and a bounded-retry consumer that dead-letters rather than hot-looping.
- **Ten of the platform's unheard queues became nine and then eight** — `approvals.events` now has consumers.
- **What is still not wired:** nothing consumes `AuthInfoRequested` to notify the requester that information
  is wanted (notification-service receives its own copy from `Decisions.Decide`, which is the right seam);
  and a quantity-level partial approval remains unexpressible, per Decision 4.

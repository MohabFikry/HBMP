# ADR-0034 — The fulfilment authorization: what was handed over is not the prescription

**Status:** **Accepted** · **Date:** 2026-08-04 · **Phase:** 26 (amendment)
**Relates to:** [ADR-0033](0033-live-label-checking-openfda.md) (the dispensing counter),
`23-state-machines.md §5` (the authorization lifecycle), `19-audit-strategy.md` (append-only records)

---

## Context

A prescription is a **clinical instruction**. A doctor wrote it, signed it, and it says what they decided the
patient should take. An investigation order is the same thing for a test.

What happens at the counter is a **different act**. The pharmacist hands over a quantity, from a batch, on a
date, at a branch — and sometimes hands over a different molecule than the one written, because the written
one is out of stock. The technician performs some of the panels and not others. None of that is what the
doctor decided; all of it is what the payer is going to be billed for.

The platform has been recording the second act correctly — `dispense_event` and `order_fulfillment` are
append-only and `prescription_line.drug_id` is never written — but it has had **no name for the result**.
There was nothing you could point at and say "this is what was authorized and delivered against
RX-2026-000410". The consequences:

1. **A substitution had no home.** `DispenseEvent.SubstitutedDrugId` was recorded and then read by nobody. A
   pharmacist could see what they had chosen only until they navigated away.
2. **The approval team could not see completed work.** Their worklist holds requests *awaiting* a decision.
   Everything the platform authorized by rule rather than by review — which is almost everything — was
   invisible to them. A team accountable for what the payer pays could see only the exceptions.
3. **Labs and imaging had no counter surface at all.** The dispensing counter got a prescription page, a
   prescribed/dispensed/remaining listing, a cost breakdown and a substitution control. The lab bench got a
   row in a table and a modal with a number in it, for work of exactly the same shape.

---

## Decision 1 — Fulfilment issues an authorization, and it is a separate document

Dispensing a prescription line, or consuming an investigation-order line, **issues an authorization** in
approvals-service: `AUTH-YYYY-NNNNNN`, `Kind = Fulfilment`, `Status = Issued`, `SourceRef` = the
prescription / order it came from.

One authorization per prescription (per order), accumulating one `authorization_item` per fulfilment. A
second dispense against the same prescription appends a second item to the same authorization; the
prescription is one course of treatment and the authorization is what was delivered against it.

Each item records **what was ordered and what was fulfilled as two separate fields**:

```
ordered_code / ordered_label     — the molecule or examination the clinician wrote
fulfilled_code / fulfilled_label — the molecule or examination actually handed over / performed
substitution_reason              — mandatory when they differ
```

They are different columns rather than one column plus a flag because a substitution is not an edit. Storing
the delivered drug in the field that once held the prescribed one would destroy the record of what the
prescriber decided, which is the fact a later reviewer most needs. **The prescription is never written to by
this path, and the schema gives it nowhere to be written to.**

### Why the existing `Authorization` aggregate and not a new table

The alternative was `pharmacy.dispense_authorization` + `orders.fulfilment_authorization`. That would have
put half of the approval team's subject matter in two schemas the approval team has no reader for, and would
have needed the numbering, the tenant RLS, the audit seam and the worklist projection written twice.

The 0005 migration made exactly this argument when validity extensions became a fourth `source` rather than a
parallel aggregate, and it was right. One authorization number space, one worklist, one audit trail.

### Why `Issued` is a terminal status with no path in or out

`AuthorizationWorkflow` refuses every transition into and out of `Issued`. A fulfilment authorization is a
**record of something that already happened**; there is nothing for a reviewer to approve. Letting one be
assigned would put settled work in the review queue and start an SLA clock on a question nobody asked.

The type system carries the same rule: `Kind` is set at creation and never updated.

---

## Decision 2 — Issuance is asynchronous, on the service's own queue

pharmacy-service and orders-service enqueue a fulfilment-shaped copy of the dispense / consume event to
**`approvals.fulfilments`**, inside the same transaction as the state change, through the durable outbox.

Not by an HTTP call from the dispensing path: an authorization that could not be issued must never be able to
fail a dispense. The patient has the medicine; a bookkeeping record catching up thirty seconds later is
correct, and refusing to hand over medicine because approvals-service is restarting is not.

Not by binding approvals to `pharmacy.events` / `orders.events` either. That transport is **point-to-point**,
and policy-service already consumes both queues to move the benefit accumulator. A second consumer on the
same queue would *compete* for messages — each event would reach one service and not the other, and the
accumulator would silently stop moving for every event approvals happened to win. A service that wants its
own copy enqueues its own copy; `notification.domain-events` established this and the same rule applies here.

Idempotency is doubled, because at-least-once delivery means both halves get exercised: the `processed_event`
ledger short-circuits a redelivered event id, and `authorization_item` has a UNIQUE `(tenant_id,
fulfilment_ref)` so a replay under a *different* event id still cannot double-post the same dispense.

A message that names no tenant is dead-lettered rather than applied under a guessed one.

---

## Decision 3 — Every authorization is visible to the approval team, and the inbox is not flooded

`GET /api/v1/authorizations` takes `kind` — `Review` (the default) or `Fulfilment` — plus `GET
/{id}/items` for the itemised detail.

**The default stays `Review`.** The reviewer inbox is a work queue: it means "these are waiting for you". A
few hundred dispenses a day landing in it would drown the twelve that need a decision, and the natural
response to a queue that is mostly noise is to stop reading it. Fulfilments are a *register* — a different
question, asked deliberately, on its own screen.

The item projection carries **codes, labels, quantities and the substitution reason, and nothing clinical**.
That is the same bounded exception the worklist already makes for an extension request's reason: a
substitution reason is logistics written by a pharmacist ("prescribed brand out of stock this morning"), and
it is the entire substance of what a reviewer is looking at. Routing them through the PHI-audited clinical
review view to read one sentence would add an audited access to a patient's record for a question that is not
about the patient.

---

## Decision 4 — Labs and imaging get the same counter surface, with one difference

The investigation order gets its own page at `/lab/order/ORD-…`: ordered / consumed / remaining as three
separate columns, the three cost tiles, and a per-line control in the same place the pharmacy page has one.

**The substitution control behaves differently, because the data is different.** Pharmacy has a real
equivalence set — the drug's ATC-5 class — so the modal offers a choice between clinical equivalents and the
server refuses anything outside it. Examinations have no such set anywhere in master data: `examination_type`
carries a category and a sensitivity, and nothing that says "this test may stand in for that one". Deriving
one from the category would put "any radiology procedure" behind a button, which is a technician
prescribing.

So on the lab page the control **raises a request to the approval team** — `Kind = Review`, `Source =
OrderLine`, with a mandatory reason and an optional proposed code — rather than offering a list. The honest
version of "we do not know what is equivalent" is to ask someone who does. It also lands in the queue those
people already work, alongside validity extensions, which is where a fulfiller's questions already go.

### The cost tiles, and what they will say today

`examination_type` gained a nullable `price_egp` for the same reason `drug` has one, and the pricing endpoint
mirrors `RxPricing` exactly: catalogue price × quantity for the total, the member/payer split from
`eligibility/check` through `libs/benefit-pricing`, and **nulls with a stated reason wherever a figure cannot
be established — never a zero.**

No examination has a price yet, and no plan version prices `LAB` or `RADIOLOGY` (as no plan version prices
`PHARMACY`). So all three tiles will read "cannot be quoted" with a reason until that data is authored
through the proper plan-amendment path. That is the mechanism working: a counter that quotes 0.00 tells a
refugee family their scan is free, and they either get a bill later or decline something they could have
afforded.

---

## Consequences

- The prescription and the order become, correctly, **read-only clinical facts** downstream of the counter.
  Anything that wants to know what the patient actually received reads the authorization.
- The approval team gains visibility of every authorized act, which is a **disclosure surface widening**. It
  is bounded to codes, quantities and amounts by the item projection, and it is the team the permission
  matrix already trusts with the clinical review view.
- Two writers now exist for `approvals.authorization` — the ingest endpoint and the fulfilment consumer. They
  are separated by `Kind`, and the workflow refuses every transition that would let one produce a row the
  other's invariants assume cannot exist.
- Fulfilment authorizations accumulate at roughly the rate of dispenses. `authorization_item` is indexed on
  `(authorization_id)` and the listing is filtered on `kind`; the worklist's existing `(status, sla_due_at)`
  index does not serve it, so a `(kind, submitted_at)` index is added rather than left to be discovered under
  load.

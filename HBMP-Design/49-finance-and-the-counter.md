# 49 — Finance & the counter

*The audit of 2026-08-12. Design 48 covered the approvals and claims portals, where every defect was a client
reaching the wrong way into a service that worked. This one covers the finance portal and the pharmacy
counter, and it finds a third family: **a client that has never been run against its own contract**.*

---

## 1. The third family

Designs 47 and 48 named two families of defect. A control sent somewhere that does not accept it: the
parameter binds to nothing, the endpoint answers 200, the filter does nothing and nobody is told. An authority
granted with no affordance: the scope is held, the endpoint is built, and no screen calls it.

The finance portal adds a third, and it is the worst of the three because it is invisible to every test in the
repository.

**A client adapter that has never met its own schema.** `HttpApiClient.settlements()` builds a settlement
object with `status: "ok"` — a string — and validates it against `zSettlement`, whose `status` is
`zStatus = { kind, label }`. `parseOr` throws. Every call to that method fails, always, with
`ApiError("schema")`. The same line appears in `exportReport()`.

Nothing catches it because nothing runs it. The web tests construct `DevApiClient`; `HttpApiClient` is the
class that only exists when there is a gateway on the other end, and there is never a gateway on the other end
of a test. So the Provider Settlements screen and the Exports screen are permanently in their error state in
production, and permanently green in CI.

The proof is three lines of zod, run directly:

```
zSettlement.safeParse({ …, status: "ok", … })
→ [{ code: "invalid_type", expected: "object", received: "string", path: ["status"] }]
```

The two families of design 48 are about a client that reaches the wrong way. This one is about a client that
does not reach at all, and it means the two most consequential screens in the finance portal — the one that
authorises payment and the one that produces the file a donor reads — have never worked outside a fixture.

**The rule this produces.** The HTTP adapter is code, and untested code is code that does not work. Its
mappings are now exercised against the real schemas.

---

## 2. Exports: three lies in one button

The Exports screen offers a report (utilization / settlement / summary), a format (CSV / XLSX), a period, and
a button. Set aside §1 and assume the call succeeds. Here is what each control does.

**The report selector does nothing.** `POST /api/v1/finance/exports` runs
`deps.Queries.UtilizationAsync(...)` unconditionally. `req.Report` names the file and the audit event and is
otherwise unread. Choosing "Provider Settlements" produces utilization rows in a file called
`settlement-2026-07-01_2026-07-31.csv`, and writes a high-severity `data.export` audit event asserting a
settlement export that did not happen. **The audit trail is wrong, not merely incomplete** — it records an
action nobody performed, which is a worse failure than recording nothing, because the record is the thing an
auditor trusts.

**The format selector does nothing.** The handler always calls `Results.File(..., "text/csv", "….csv")`.
XLSX has never been produced. The claimed format *is* stored — `Format = req.Format ?? "csv"` — so the export
ledger says a spreadsheet was generated that never existed.

**The button produces no file.** The endpoint returns `text/csv` through `Results.File`. The client calls
`postRaw`, which parses JSON, and reads `rowCount` off the result. There is no `Blob`, no object URL and no
anchor click anywhere in the SPA. Even repaired to the point of returning, the Exports screen would show a
row count and hand the operator nothing.

Three controls, none of which does what it says. The fix is to make the server honour `report`, to remove
XLSX rather than ship a control that silently substitutes another format, and to download the file.

**Why XLSX is deleted rather than implemented.** A CSV opens in Excel. The gap between the two is a nicety,
and the cost of the nicety is a spreadsheet library in a service whose entire security argument is that it
cannot express a clinical field. Nothing here is worth a new parser.

---

## 3. The settlement lifecycle had no door

`finance` holds `finance:read`, `finance:write`, `finance:approve` and `finance:export`. The service
implements the whole lifecycle: generate a draft for a provider and period, submit it, approve it — the last
two split for segregation of duties, with `SubmittedBy` compared against the approving principal and a 409
`urn:hbmp:sod-violation` when they match. The approval emits `SettlementApproved` through the outbox inside
the same transaction, because a payment authorised and never announced is the failure 24.3 is about.

The portal has a table and a "View lines" button.

There is no way to create a settlement, submit one, or approve one. A settlement can only exist if something
outside the product puts it there. This is design 48's second family again — but where an unreachable claims
adjudication screen wasted work, an unreachable settlement lifecycle means **the finance role cannot do the
job the role exists for**, and the SoD control that the permission matrix requires has never been exercised by
a person.

### 3.1 Segregation of duties belongs on the screen, not only in the refusal

The service refuses correctly. But `SettlementView` carried neither `SubmittedBy` nor `ApprovedBy`, so a
screen offering an Approve button would offer it to the submitter too, and answer the click with a 409.

That is a control working and reading as a bug (invariant 29). The view now carries both, the Approve button
is unavailable to the person who submitted, and the reason is written out: *you submitted this settlement, so
somebody else has to approve it*. The 409 remains — the client is not the authority — but the ordinary path no
longer runs through a refusal.

### 3.2 The price source a reviewer could not see

`SettlementLine.PriceSource` is `Contract` or `ObservedFloor`. The domain comment on it says what it is for:

> An unpriced code used to be settled at the provider's own observed AVERAGE unit cost with nothing recording
> that it had been… a reviewer issuing the draft has to be able to tell them apart.

`SettlementLineView` projects it. The SPA's mapping drops it. So the reviewer, at the moment of authorising a
payment, sees a column of agreed prices in which some are the contract's tariff and some are a floor this
platform inferred because no tariff exists — rendered identically.

It is now a column, with its own chip, and the settlement header says how many of its lines are unpriced.
A settlement that is entirely contract-priced says nothing extra; one that is not says so before the Approve
button.

---

## 4. Every finance figure now states its window

`/utilization` and `/summaries` both accept `from` and `to` and default to the trailing month against the
Cairo business date. Neither screen sent either. So the finance analyst saw the trailing month, always, and
**could not close a prior month** — the single most routine thing a finance function does.

The Utilization screen rendered `state.data.from → to` as a label, which is design 47 §7's rule (a figure
states its period) honoured in the reading and broken in the asking: the period was announced and not
selectable.

`PeriodControl` — built for the director's portal, already carrying the Cairo-vs-UTC business-date handling —
now drives Utilization, Summaries and Exports off one stored period, so the three screens cannot disagree
about which month is on screen.

**Settlements deliberately does not take it.** A settlement carries its own `periodStart`/`periodEnd` as
columns. A global period control over a list of period-stamped rows means either containment or overlap, and
the two give different answers for exactly the settlements a query is most likely to be about — the ones
spanning a boundary. Rather than pick silently, the Settlements screen filters on the things the endpoint
already declares, `providerId` and `status`, and the period stays a column you read.

---

## 5. The pharmacy counter cannot say "we don't have it"

`POST /api/v1/prescriptions/{rxId}/lines/{lineId}/out-of-stock` is complete. It changes no accumulator, so
the unfilled quantity stays available for a later visit. It publishes `RxLineOutOfStock` to `pharmacy.events`
and a second, notification-shaped copy to `notification.domain-events` addressed to the **prescriber** — with
a comment explaining that the point-to-point transport is why the copy exists, that the route is actionable,
and that it escalates to the pharmacy supervisor after eight hours. It audits.

Nothing in the SPA calls it.

That alone is family two. What makes this one worth its own section is the state of the flag on the way back.

| Layer | What it says about `outOfStock` |
|---|---|
| `zPrescriptionLine` (contract) | `outOfStock: z.boolean()` — a first-class field |
| `PrescriptionPage.tsx` | renders a `warn` chip; excludes the line from `fillable` |
| `DispensableLineView` (server) | **does not carry it** |
| `HttpApiClient` | `outOfStock: false` — a literal |
| `DevApiClient` | `outOfStock: true` on one fixture |

The feature renders in development, renders in the tests, and cannot render in production. Invariant 26 said
a fixture that agrees with a broken client is a second implementation of the bug; here the fixture is the
*only* implementation. Everything downstream of it — the chip, the exclusion from the fillable set, the tests
that assert both — is exercising a value the real client is structurally incapable of producing.

And the pharmacist, with an empty shelf and a patient at the counter, has no control at all. The prescriber
is never told. The escalation never fires. A refugee beneficiary makes a second journey for a medicine
nobody recorded as missing.

### 5.1 Notify-only is not enough once it has a button

The endpoint stores nothing by design — "no accumulator change… notify + audit only". That is right about the
accumulator and wrong about the record. With a button in front of it:

- a reload loses the flag, so the chip the contract promises can never survive a page refresh;
- the same line can be flagged five times, and the prescriber gets five actionable notifications with five
  eight-hour escalations behind them;
- nobody can answer "what are we out of", which is the question that turns a counter's problem into a
  purchasing decision.

So the flag persists: who raised it, when, how much and the note. Re-flagging an already-flagged line is
idempotent — it returns what was recorded and does **not** notify again. Dispensing against the line clears
it, because stock arriving is the flag's natural end and a chip that outlives the shortage is worse than no
chip.

What does **not** change: the accumulator. The line stays available, `QuantityRemaining` is untouched, and
`RxLineStatus` is not a state machine this writes to. Out of stock is a fact about the pharmacy, not about the
prescription.

### 5.2 What it is not

It is not an inventory count. `inventory-service` owns stock levels and branch balances; this records that a
counter could not fill a line on a day, which is a different fact with a different owner and a different
consumer. Nothing here reads or writes a stock balance.

---

## 6. Two more literal statuses, outside these portals

The `status: "ok"` defect appears at four sites in `HttpApiClient`. Two are finance. The other two —
`caseTasks()` and `escalations()` — are the case-management portal, outside this audit's scope.

They are fixed anyway. Leaving a proven, one-line, always-throwing crash in a file being edited, on the
grounds that it belongs to a different portal, is scope discipline applied until it stops making sense. They
are called out here so the next person reads them as what they are: two lines carried along, not two lines
audited.

---

## 7. Invariants

Numbered from 35, continuing the series in `48-approvals-and-claims-workbench.md`.

35. The HTTP adapter's mappings are executed against the real contracts in a test. A fixture client proves
    the screens; it proves nothing about the client the user gets.
36. A control that names an option produces that option. A selector whose value the server ignores is worse
    than a missing selector, because the operator believes they chose.
37. An audit record names the action that happened. A record naming an action nobody performed is worse than
    no record.
38. A format that is not produced is not offered.
39. An export delivers a file. A row count is a receipt, not a deliverable.
40. Where segregation of duties will refuse, the screen says so first. The refusal stays; the ordinary path
    does not run through it.
41. A value the reviewer must weigh before authorising money is on the screen where the authorising happens.
42. A period-stamped list is not silently filtered by a global period. Containment and overlap are different
    questions and the difference falls on the boundary rows.
43. A flag a screen can raise is a flag the server stores. Otherwise it survives until the next reload and
    notifies again every time it is raised.
44. Raising the same flag twice notifies once.

# 48 — Approvals & Claims: the workbench

*The audit of 2026-08-11, second pass. Design 47 covered the Medical Director's analytical plane; this covers
the two operational portals that feed it — the approval reviewer's queue and the claims officer's desk.*

---

## 1. One shape of defect, found five times

Both portals are read surfaces sitting on top of services that implement the whole job. In every case the
service was right and the client was not, and the failures divide into two families.

**A control sent somewhere that does not accept it.** The claims worklist called `/claims/worklist` with a
`status` parameter that endpoint does not declare. ASP.NET binds nothing and answers 200, so a four-segment
filter returned identical rows and nothing anywhere reported a problem.

**An authority granted with no affordance.** `claims_officer` holds every claims write scope; the portal had
no write screen. `medical_approval` and the director raise break-glass authorizations that are flagged for
review; nothing could perform one.

The second family is the one design 47 §8 named in the other direction — a permission with no door. Here the
door is missing on the actions, not the navigation, and the consequence is worse: an unreachable *screen*
wastes the work that built it, an unreachable *control* means the control does not exist.

---

## 2. The claims worklist read the wrong endpoint

`GET /api/v1/claims/worklist` (`DecisionEndpoints.cs`) is the per-**line** adjudication queue. It is hard
filtered to `ClaimStatus.UnderAdjudication` and `ClaimLineStatus.Pending`, its parameters are
`batchId, providerId, recommendation, reasonCode, minValue, maxValue, take`, and it returns a `WorklistRow`.

`GET /api/v1/claims` (`ClaimsEndpoints.cs`) is the claim-level list. It parses `status` into a `ClaimStatus`
and returns a `ClaimView` carrying `Origin`, `ClaimedAmount`, `ApprovedAmount`, `NetPayable`, `SubmittedAt`
and the lines.

The portal called the first and rendered the second's fields:

| What the screen showed | What it meant |
|---|---|
| Status filter with four segments | All four returned the same rows |
| `Origin` — empty | Not on a line |
| `Claimed` — `0.00` | Not on a line |
| `Net payable` — blank | Not on a line |
| `Submitted` — blank | Not on a line |
| One row per line, keyed by claim id | Colliding keys within a claim |

Two of the four status segments — `Adjudicated`, `Rejected` — are not members of `ClaimStatus` at all. Nor
were they in the chip map, the dev fixtures or the tests: **the client, the fixtures and the tests all spoke a
vocabulary the service does not have**, which is how a screen wired to the wrong endpoint stayed
indistinguishable from a working one. Nothing rendered an error. It rendered zeros.

**The rule this produces.** A fixture speaks the service's vocabulary, or it is a second implementation of the
bug. Fixtures now use real enum members, and the chip maps are keyed on them.

---

## 3. Two screens, because there are two questions

`Claims` is claim-level: one row per claim, real amounts, a status filter with the eight states an officer
actually triages by, and — new — the claim's lines and the adjustments raised against it in a side panel.

`Adjudication` is line-level: the queue the adjudication engine fills, with the parameters that endpoint
accepts (`recommendation`, `reasonCode`, `minValue`), and the decision itself.

Folding them into one screen is what the old worklist attempted by calling the line endpoint and rendering
claim columns. The two ask different questions of different rows and neither answer fits in the other's table.

### 3.1 What a decision consists of

- Six decision kinds (`ClaimDecisionKind`).
- Reason codes **picked** from the fifteen `ReasonCodes.All` holds, never typed. A code the adjudicator does
  not recognise is refused with a 422 *after* the reviewer has written a rationale — work thrown away by a
  free-text field.
- `AllowedAmount` required for `PartiallyApprove` and `Adjust`; a rationale required for anything that is not
  a plain approval. Validated client-side with the **same zod schema** the request is built from.
- **`PendingSecondApproval` is an outcome, not an error.** The server answers 202 when a decision exceeds the
  dual-control threshold and holds it for a second, distinct approver. Rendering that as a failure teaches
  reviewers that the threshold is a malfunction and that the way past it is to retry.
- **Each segregation-of-duties refusal is said as itself.** `SOD_ORIGINATOR_CANNOT_ADJUDICATE`,
  `SOD_PROVIDER_AFFILIATED` and `SOD_SAME_DECIDER` mean three different things about what the reviewer should
  do next. A 403 reading only "forbidden" tells them only that the software is refusing them, which reads as
  a defect rather than as the control working.

### 3.2 Minimum-necessary is unchanged

`ResultExists` stays a boolean derived server-side from the fulfilment linkage: the officer confirms a service
was rendered without reading what it found. Every DTO on these screens is one of the existing server-side
allow-list projections, and none of them has a field a diagnosis could travel in.

---

## 4. Reconciliation: the two buckets that carry the money

`ReconBucket` has six members. The portal offered three. Missing:

- **`Duplicate`** — the double-billing signal.
- **`DeliveredNotBilled`** — money the platform is owed and never asked for.
- **`QuantityVariance`** — a provider billing more units than were delivered.

All three were being classified server-side on every request. They were unselectable, they had no status chip
so they rendered as their raw English token in both languages, and they were invisible under "All", which is
the absence of a filter rather than a bucket you can reason about.

The row key was `claimId + code`, which collides for two lines of one claim on the same code — precisely what
`QuantityVariance` describes. `ClaimLineId` was on the payload and was being dropped by the mapper.

---

## 5. The retrospective review that could not happen

**The most serious finding of the pass.**

Emergency approval, director override and manual authorization each set `RetrospectiveReviewRequired`, write a
High-severity break-glass audit event, and appear in `GET /api/v1/authorizations/retrospective-queue`. That
endpoint has been served since phase 7.3.

- Nothing in the web application ever called it. The queue had no screen.
- **`RetrospectiveReviewed` was never assigned `true` anywhere in the repository.** It appeared exactly twice:
  its own declaration on the entity, and the `NOT` predicate that reads it. No endpoint, service or job could
  complete a review.

So the queue was write-only. Every break-glass authorization entered it and none ever left.

That is not an unfinished feature. **The after-the-fact review is the control that makes break-glass
defensible at all** — an override is acceptable *because* somebody checks it afterwards. Unreviewable, the
flag recorded that a review was *owed* and never that one *happened*, and the audit trail could not
distinguish "reviewed and upheld" from "nobody ever looked". Those are the two states the control exists to
tell apart.

### 5.1 What completing one now consists of

`POST /api/v1/authorizations/{id}/retrospective-review`, scope `auth:retrospective`:

| Refusal | Why |
|---|---|
| 409 — not break-glass | There is nothing to review |
| 409 — already reviewed | A completed review is a record, not a draft |
| 422 — blank rationale | A review that records no reasoning is a checkbox, which is what this already was |
| 403 — `SOD_SELF_RETROSPECTIVE_REVIEW` | Somebody signing off their own override is the exact failure this catches |

Migration `approvals/0016` adds the five columns and two CHECK constraints. `ck_auth_retrospective_complete`
requires a reviewed row to carry reviewer, timestamp and outcome together: **half a review — an outcome with
no reviewer, a reviewer with no conclusion — is a record that cannot be defended to anyone asking who signed
this off**, and it is the state every row in the queue would have been in had anything ever set the flag.

### 5.2 Segregation of duties, twice

**Per person**, in the handler: the reviewer may not be the actor who took the break-glass decision.

**Per role**, in `ApprovalsPolicies`: `auth:retrospective` goes to `medical_director` and `super_admin`, and
pointedly not to `medical_approval` — who hold `auth:manual` and `auth:emergency` and therefore *raise*
break-glass authorizations. The per-person check does not cover this: it stops somebody reviewing their own,
not a team reviewing its own. One team acting as both actor and auditor is the arrangement this control
replaces, not the one it formalises.

The queue therefore lives on the **director's** portal, not the approval team's.

### 5.3 `NotJustified` is a finding, not a reversal

There is deliberately no outcome that unwinds the authorization, and the database constraint models only two.
The care was delivered under it. Reversing it retroactively would refuse a service that has already happened,
to a beneficiary who had no part in the decision. The finding is the output — what an oversight report is
built from and what a conversation with the decider starts from.

### 5.4 The queue reports its age

`ageDays` is on the row and the oldest open case is a KPI, because the question asked of a compliance backlog
is not how many but **how long the oldest has been sitting there**. A count alone looks identical whether the
queue turned over yesterday or has been stuck since March, and only one of those is a finding.

---

## 6. The approvals queue

### 6.1 A column that could never be filled

`Est. cost` was declared `numeric: true, sortable: true` over a client-side literal `"—"` — the column sorted
a constant. `Requested amount` in the review panel was the same.

Neither is a mapping bug. **approvals-service holds no prices**: no amount on the `Authorization` aggregate, no
tariff client, no column in the schema to source one from. A column headed "Est. cost" that is always blank
tells a reviewer the platform knows the cost and is declining to show it.

Both are **removed**, with the reason recorded where the column stood. That says the true thing — this system
does not price a request at review time — and leaves room for a real one later, sourced from the tariff
service, which is a different change with a real cost.

### 6.2 Filters that never left the browser

`GET /api/v1/authorizations/` accepts `status`, `priority`, `slaBreached` and `unassigned`. The client sent
only `kind`, took the server's 200-row page, and filtered *that*. A tenant with three hundred pending requests
narrowing to "breached" was narrowing a truncated list and was told nothing.

All four now travel. The endpoint returns `X-Total-Count` — a header, so every existing caller keeps the array
shape it parses — and the screen says "showing the first 200 of 314" when they differ.

### 6.3 Ownership

`assignedTo=me` was added on both sides. The projection carried no `AssignedReviewerId` at all, so a queue
worked by several reviewers could not answer "is this mine" or "has anyone picked it up" — the two questions a
shared queue is read with before anything else on it means anything.

### 6.4 Smaller truths

- **All requested codes are shown.** `serviceCodes[0]` was rendered as "the service" and `slice(1)` as
  "supporting codes". They were never supporting anything; a three-service request read as one service with
  two footnotes, and a reviewer decided on a third of what was asked.
- **`requestedBy` is derived from the source.** It was the literal string `"Provider"` on every row, including
  manual authorizations, which by definition have no requesting provider. A constant that is true of some rows
  and false of others is worse than a blank: it never looks missing, so it is never questioned.
- **`submittedAt` comes from the server.** It was computed as `now − tatElapsedSeconds` on every render, so a
  row's submission time crept forward while the page sat open.
- **The Emergency screen asks for `status=Submitted`.** It loaded the unfiltered queue, so decided
  authorizations appeared with an "Emergency approve" button that returned a 409 the screen never surfaced —
  the row simply did not change, which reads as a broken application rather than an illegal action. A refusal
  is now shown.

---

## 7. The window every figure states

Reconciliation and the claims KPIs both default to the last **ninety Cairo days** and neither screen sent nor
displayed a window. "Denial rate 12%" did not say twelve percent of what span — and the director's dashboards
default to thirty, so two figures covering different periods could sit in one conversation with nothing on
screen saying so.

Both now carry the period control built in design 47 §7, keyed **per portal**: a claims officer's choice must
not silently retune an oversight dashboard in the same browser, which is the same confusion that control
exists to prevent, one level up.

---

## 8. Invariants

Numbered from 25, continuing the series in `47-oversight-and-analytics.md`.

25. A client sends a parameter the endpoint declares, or it does not send it. An ignored query parameter is a
    filter that silently does nothing.
26. A fixture speaks the service's vocabulary — real enum members, real field names. A fixture that agrees
    with a broken client is a second implementation of the bug.
27. A role that holds a write scope has a screen that uses it, or the scope is not granted.
28. A column that cannot be filled is removed, not left blank. Blank reads as missing data; absent reads as
    absent data.
29. A refusal is rendered as the specific rule that refused. A generic "forbidden" on a control that is
    working reads as a defect in the software.
30. A held decision — dual control, second approver — is an outcome, not an error.
31. A list that caps its page says so, in the count.
32. A break-glass decision can be closed. A control that can be entered and not exited is not a control.
33. A completed review carries reviewer, time, conclusion and reasoning together, or it is not recorded.
34. Segregation of duties binds the person **and** the role. One team cannot be both the actor and the
    auditor of its own exceptions.

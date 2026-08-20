# Approvals & Claims — audit and redesign

**Date:** 2026-08-11
**Portals:** `/approvals` (`medical_approval`), `/claims` (`claims_officer`)
**Status:** approved — sections 1–6 are the design, §7 records what changed during implementation.

---

## 1. What the audit found

Both portals are read surfaces sitting on top of services that implement the whole job. The
services are not the problem. In both cases the officer holds every write scope the work needs
and has no screen that uses one, and in one case the screen that does exist is reading the
wrong endpoint.

### 1.1 The claims worklist reads the wrong endpoint

`HttpApiClient.claimsWorklist(status)` calls `GET /api/v1/claims/worklist`.

That endpoint is the **line-level adjudication queue** (`DecisionEndpoints.cs:22`). It is hard
filtered to `c.Status == UnderAdjudication` and `l.Status == Pending`, and its query parameters
are `batchId, providerId, recommendation, reasonCode, minValue, maxValue, take`. There is no
`status` parameter. ASP.NET binds nothing and returns 200, so the screen's four-segment status
control — All / Submitted / Adjudicated / Rejected — returns **identical rows for all four
segments**, none of which are in any of the three named statuses.

The response shape does not match either. It is `WorklistRow`, one row per claim **line**. The
client maps `c.origin`, `c.claimedAmount`, `c.netPayable`, `c.submittedAt` — none of which exist
on it. So on every row today:

| Column | Renders |
|---|---|
| Origin | empty |
| Claimed | `0.00` |
| Net payable | blank |
| Submitted | blank |
| Status | the *line* status (`Pending`), chipped through a *claim* status map |

and `rowKey={(r) => r.id}` is the claim id, so a claim with three lines contributes three rows
with the same React key.

The endpoint the screen wants already exists: `GET /api/v1/claims` (`ClaimsEndpoints.cs:22`)
takes `providerId, beneficiaryId, status, take`, parses `status` into `ClaimStatus`, and returns
`ClaimView` — `Origin`, `ClaimedAmount`, `NetPayable`, `SubmittedAt`, `Status`, `Lines`.

### 1.2 The claims officer's job has no interface

`claims_officer` holds `claims:read`, `:reconcile`, `:export`, `:review`, `:decide`,
`:adjudicate`, `:adjust`, `:batch`, `:submit`, `:reimburse:submit`, `:appeal`
(`identity/0001_identity.sql:182`, `0005_policy_seed_reconciliation.sql:54-61`).

The portal is three read screens. Served and unreachable: per-line decisions with reason codes,
the dual-control second-approver hand-off, the three segregation-of-duties refusals, adjustment
raise and read, appeals, batches, submissions, reimbursements.

`0005`'s own comment says this was noticed once before, at the scope layer: *"claims_officer
held claims:read, claims:reconcile and claims:export only, so every [decision endpoint] was
unreachable."* The scopes were granted. The screens were not built.

### 1.3 Half the reconciliation buckets are unreachable

`ReconBucket` has six members. The segmented control offers three:

| Bucket | In the UI |
|---|---|
| `Matched` | yes |
| `PriceVariance` | yes |
| `BilledNotDelivered` | yes |
| `DeliveredNotBilled` | **no** — revenue leakage |
| `QuantityVariance` | **no** |
| `Duplicate` | **no** — double-billing |

`Duplicate` is the fraud signal and `DeliveredNotBilled` is the money the platform is owed and
never asked for. Neither can be selected. They are also invisible under "All", which is not a
filter but the absence of one — they are in the list, unlabelled and unsortable-to.

`rowKey={`${r.claimId}-${r.code}`}` collides for two lines of one claim carrying the same code
on different dates or quantities, which is exactly what `QuantityVariance` describes.

### 1.4 Two hidden 90-day windows

`GET /reconciliation` and `GET /claims/kpis` both default to `to = Cairo today`,
`from = to - 90 days`. Neither screen sends a window and neither displays one. A KPI tile
reading "Denial rate 12%" does not say twelve percent *of what period*, and a reconciliation
list silently ends 90 days back with no indication that anything precedes it.

### 1.5 The retrospective-review queue is unreachable and cannot be closed

This is the most serious finding.

Emergency approval, director override and manual authorization all set
`RetrospectiveReviewRequired` and write a High-severity break-glass audit event
(`BreakGlass.cs`). `GET /api/v1/authorizations/retrospective-queue` serves the open ones.

- **Nothing in the web application calls it.** The queue has no screen.
- **`RetrospectiveReviewed` is never assigned `true` anywhere in the repository.** Grep returns
  two hits: the property declaration, and the `!a.RetrospectiveReviewed` filter that reads it.
  There is no endpoint, service or job that completes a review.

So the queue is write-only. Every break-glass authorization enters it; none ever leaves. The
after-the-fact review is the control that justifies break-glass existing at all — an override
is acceptable *because* somebody checks it afterwards. Here nobody can, and nothing records
that nobody did.

### 1.6 The approvals queue shows two columns it cannot fill

`estimatedCost: "—"` is a literal in `HttpApiClient.approvalWorklist`, and the column is
declared `numeric: true, sortable: true, sortValue: (r) => r.estimatedCost` — sorting a
constant. `requestedAmount: "—"` is the same in `approvalReview`.

Neither is a mapping bug. **approvals-service does not hold prices.** There is no amount on
`Authorization`, no tariff client, and no column in the schema to source one from. A reviewer
looking at a column headed "Est. cost" reasonably believes the platform knows the cost and is
declining to show it, when it does not know.

### 1.7 Every server-side filter on the approvals queue is unused

`GET /api/v1/authorizations/` accepts `status`, `priority`, `slaBreached`, `unassigned`, `kind`.
The client sends only `kind`. The server then returns `.Take(200)` and the browser filters
that. Two consequences:

- The 200-row cap is silent. A tenant with 300 pending requests filtering to "Breached" is
  filtering a truncated list and is told nothing.
- `unassigned` is never used and `AssignedReviewerId` is never surfaced, so a **shared queue has
  no notion of ownership**. There is no "mine" and no "nobody has this yet" — the two questions
  a queue worked by several people is actually worked by.

### 1.8 Smaller, still wrong

- **Multi-code requests are misrepresented.** `serviceCodes[0]` is rendered as "the service" and
  `serviceCodes.slice(1)` as "supporting codes". They are not supporting codes; they are the
  rest of the requested services. A three-code request reads as one service with two
  attachments.
- **`requestedBy` is the literal string `"Provider"`** on every row, including manual
  authorizations, which by definition have no requesting provider (`RequestingProviderId` is
  null for them).
- **The Emergency screen offers to emergency-approve decided authorizations.** It reuses
  `approvalWorklist()` with no status filter, so Approved and Rejected rows appear with an
  "Emergency approve" button. `emergency-approve` is only legal from `Submitted`; pressing it
  returns a 409 that the screen does not surface — the row simply does not change.
- **`submittedAt` is fabricated** as `now - tatElapsedSeconds`, recomputed on every render.

---

## 2. Decisions taken

| # | Question | Decision |
|---|---|---|
| 1 | How far does the claims write surface go? | **Adjudication + adjustments.** Line decisions with reason codes, dual control and SoD; adjustment raise and read. Batches, submissions and reimbursements stay out — they are provider-side and payer-side flows, not this officer's queue. |
| 2 | The retrospective-review queue | **Close the loop end to end.** Add the missing completion endpoint, with its own SoD, plus the queue screen. |
| 3 | Worklist vs line queue | **Two screens, honestly named.** "Claims" is claim-level off `GET /api/v1/claims`; "Adjudication" is line-level off `GET /claims/worklist`. |
| 4 | Approvals queue filtering | **Server-side, plus an ownership axis.** Pass the filters the endpoint already accepts; add Mine / Unassigned / All. |

---

## 3. Approvals — the design

### 3.1 Completing a retrospective review

New endpoint:

```
POST /api/v1/authorizations/{id}/retrospective-review
     { outcome: "Upheld" | "NotJustified", rationale: string }
     Idempotency-Key: required
     scope: auth:retrospective
```

Rules:

- **404** if the authorization does not exist; **409** if `RetrospectiveReviewRequired` is false
  (there is nothing to review) or `RetrospectiveReviewed` is already true.
- **422** on a blank rationale. A review that records no reasoning is not a review; it is a
  checkbox, and a checkbox is what this control already effectively was.
- **403, segregation of duties**, if the reviewer is the actor who took the break-glass decision.
  Somebody signing off their own override is the exact failure this control exists to catch, and
  it is the same rule the claims decision path already enforces between originator and decider.
- Sets `RetrospectiveReviewed`, `RetrospectiveReviewedBy`, `RetrospectiveReviewedAt`,
  `RetrospectiveOutcome`, `RetrospectiveRationale`.
- Writes a **High**-severity audit event and emits `AuthRetrospectivelyReviewed` on
  `approvals.events` inside the same transaction.
- `NotJustified` does **not** reverse the authorization. The care was already delivered under it;
  unwinding it retroactively would deny a service that has happened. It is a finding, and it is
  what an oversight report is built from.

Migration `services/approvals/Infrastructure/Migrations/0016_retrospective_review_outcome.sql`
adds the five columns, idempotently (`ADD COLUMN IF NOT EXISTS`), plus a partial index on the
open queue.

The queue read gains `outcome` and `reviewedAt` on closed rows and an **age in days**, because
the question asked of a compliance backlog is not "how many" but "how long has the oldest been
sitting there".

### 3.2 Ownership and server-side filtering

`GET /api/v1/authorizations/` gains `assignedTo` (a reviewer id, or the literal `me`), alongside
the `unassigned` it already has. The client sends `status`, `priority`, `slaBreached` and the
ownership axis to the server; only free-text search stays in the browser, because it spans
fields the endpoint does not index.

The response gains `totalMatching` so the 200-row cap can say so. A truncated list that admits
it is truncated is a different object from one that does not.

### 3.3 Removing what cannot be filled

The "Est. cost" column and the "Requested amount" line are **removed**, not left as dashes, and
the reason is recorded where the column used to be: approvals-service holds no prices. A column
that is always blank teaches a reviewer that the data is missing today and may arrive tomorrow.
Removing it says the true thing — this system does not price a request at review time — and
leaves the door open to a real one later, sourced from the tariff service, which is a different
change with a different cost.

### 3.4 Truthful request identity

- All requested service codes are shown as one list. The review panel's "Supporting codes"
  becomes "Requested services", which is what they are.
- `requestedBy` is derived from `Source` (`OrderLine` → "Clinician order", `Prescription` →
  "Prescription", `Manual` → "Raised by the approval team", `ValidityExtension` → "Validity
  extension request"), with `RequestingProviderId` carried on the row for the cases that have
  one. No literal string that is true of some rows and false of others.
- The Emergency screen asks the server for `status=Submitted`. A screen whose only action is
  legal from one state lists that state.

---

## 4. Claims — the design

### 4.1 `Claims` — the claim-level worklist

Reads `GET /api/v1/claims?status=&take=`. One row per claim. The status control's segments
become real `ClaimStatus` values and are sent to the server. Money renders because the fields
exist.

Statuses offered: All, `Submitted`, `UnderAdjudication`, `PendingInfo`, `Approved`,
`PartiallyApproved`, `Denied`, `Settled`. The previous three ("Submitted / Adjudicated /
Rejected") named two states that do not exist in the enum.

Selecting a claim opens its detail: the lines, their codes, quantities, billed / contract /
allowed amounts, per-line status and reason codes, and the adjustments raised against it
(`GET /claims/{id}/adjustments`). No diagnosis, no note, no result value — the minimum-necessary
boundary is unchanged and the DTO is still structurally incapable of carrying one.

### 4.2 `Adjudication` — the line queue and the decision

Reads `GET /api/v1/claims/worklist` with the parameters it actually accepts:
`recommendation`, `reasonCode`, `minValue`, `maxValue`, `providerId`, `batchId`.

Deciding a line posts to `POST /claims/{claimId}/lines/{lineId}/decisions`:

- Decision kinds `Approve`, `PartiallyApprove`, `Deny`, `Adjust`, `RequestInfo`,
  `RouteToClinical`.
- Reason codes are picked from `ReasonCodes.All` (15 codes), not typed. A free-text reason code
  that the server rejects with a 422 after the reviewer has written a rationale is a form that
  wastes work.
- `AllowedAmount` required for `PartiallyApprove` / `Adjust`; rationale required for everything
  that is not a plain `Approve`.
- **The three SoD refusals are rendered as what they are**, not as a generic failure:
  `SOD_ORIGINATOR_CANNOT_ADJUDICATE`, `SOD_PROVIDER_AFFILIATED`, `SOD_SAME_DECIDER` each get
  their own sentence explaining who may act instead. A 403 that says only "forbidden" on a
  segregation-of-duties control teaches the reviewer that the system is broken rather than that
  the control is working.
- **`PendingSecondApproval` (202) is a first-class outcome**, not an error. The line moves to a
  "waiting for a second approver" state in the queue, carrying its `decisionId`; a second,
  distinct reviewer confirms it by posting the same decision with `confirmsDecisionId`.

### 4.3 Adjustments

Raised from a settled line: type (nine kinds), amount delta, reason code, rationale. Read back
on the claim detail with before/after amounts. `PendingSecondApproval` here means the adjustment
would make the batch net payable negative, and is surfaced with that sentence.

### 4.4 Reconciliation

All six buckets in the control. A date-range control bound to the `from`/`to` the endpoint
already accepts, defaulting to the Cairo 90 days it already defaults to — the difference is that
the window is now stated. `rowKey` becomes `claimLineId`, which the row already carries and the
client was dropping.

### 4.5 Claims insights

The same period control, so the KPI tiles say what period they describe. No new metrics: the
seven that exist are right, they were merely undated.

---

## 5. Boundaries that do not move

- **No diagnosis, note or result value enters the claims portal.** Every DTO used here is one of
  the existing server-side allow-list projections. `ResultExists` stays a boolean.
- **No clinical payload enters the approvals worklist.** The one bounded exception —
  `ExtensionReason` — is unchanged and still null for every other source.
- **Break-glass stays specially audited.** The new completion endpoint adds an audit event; it
  removes none.
- Four-cue status (hue + icon + shape + word), WCAG 2.2 AA, full Arabic RTL, typed `Localized`
  throughout, Africa/Cairo via `useFormat`.

## 6. Tests

- **Approvals**: the retrospective endpoint's five refusals (404, 409 not-required, 409
  already-reviewed, 422 blank rationale, 403 self-review); the queue closes and the row leaves;
  the ownership filter returns only the caller's; `totalMatching` exceeds the page on a
  truncated list.
- **Claims**: the worklist screen renders money from `ClaimView`; the status filter reaches the
  server; each SoD refusal renders its own sentence; `PendingSecondApproval` renders as pending
  rather than failed; all six reconciliation buckets are selectable.
- **Web**: screen tests for the new surfaces, table/bundle architecture gates, `tsc --noEmit`.
- Full solution with `--with-db`, plus the OpenAPI drift gate.

---

## 7. Revised during implementation

Six divergences from the design above, each recorded where the code makes the choice.

**1. The claim-status chip map was wrong, and so were the fixtures.** The audit found the *filter* naming two
states that do not exist in `ClaimStatus`. Implementation found the same vocabulary in
`claimStatusChip` (`Adjudicated`, `UnderReview`, `Rejected`, `Cancelled` — none of them members) and in the
dev fixtures. Seven real statuses fell through to the neutral fallback, which puts the raw English token in
the Arabic slot, so an Arabic reader saw "Denied" in Latin script on a denied claim. The client, the fixtures
and the tests all spoke a vocabulary the service does not have — which is how a screen wired to the wrong
endpoint stayed indistinguishable from a working one. All three now use real enum members.
`ClaimLineStatus` got its own map; it was borrowing the claim's.

**2. `reconBucketChip` was missing two of the six buckets**, not just the filter. `Duplicate` and
`QuantityVariance` rendered as their raw tokens in both languages.

**3. The truncation count is a header, not a body wrapper.** `X-Total-Count`, so every existing caller of the
worklist keeps the array shape it parses today.

**4. `usePeriod` became key-scoped.** The claims window and the director's are different questions; one
`sessionStorage` key would have let a claims officer's choice silently retune an oversight dashboard in the
same browser — the confusion the control exists to prevent, one level up.

**5. A pre-existing test had to change, and the change is the finding.**
`BreakGlassTests.A_manual_break_glass_authorization_lands_in_the_retrospective_queue` closed its case by
flipping `RetrospectiveReviewed = true` and nothing else. `ck_auth_retrospective_complete` now refuses that.
It is the exact record the constraint exists to forbid — reviewed, by nobody, at no time, concluding nothing —
and it is the state every row in the queue would have been in had anything ever set the flag.

**6. Two helpers exist to keep the numeric-column gate honest.** `CodeList` and `extraCodes` in `_shared.tsx`.
The gate reads a column literal for `.length`, `Count`, `Qty` to decide whether it holds a magnitude that
should be end-aligned; a service-code cell holds neither, and inline `codes.length ? … : …` — and then the
name `extraCodeCount` — were enough to flag it. Extracting them removes the false positive without weakening
the gate, and de-duplicates three copies of the same cell.

## 8. Verification

| | |
|---|---|
| `apps/web` — `tsc --noEmit` | clean |
| `apps/web` — vitest | 114 files, **1456 passed, 0 failed** (17 new) |
| `Mersal.Approvals.Tests` (`--with-db`) | **146/146** (7 new) |
| `Mersal.Authz.Tests` | 235/235 |
| Full solution (`--with-db`) | 36 assemblies; two failures, both traced to other branches — see below |
| OpenAPI drift gate | regenerated `docs/api/approvals.json` |

### The two solution-run failures

Neither is from this work, and each was isolated rather than assumed.

**`Mersal.Admin.Tests.BranchScopeGrantParityTests`** fails with `23505 ux_bsg_home_per_subject` — the exact
bug PR #7 fixes in `admin/0007_branch_scope_grant.sql`. That fix is on a sibling branch, so this one does not
carry it. Proved by substituting PR #7's version of that single file: **115/115**, and restoring it brings the
failure straight back. The parity test's copy has no tenant filter, so it picks up any real row the shared dev
database is left holding.

**`Mersal.Orders.Tests`** — one failure inside the solution run, **221/221 when the assembly runs on its own**.
Cross-assembly contention on the shared database, not a defect: assemblies run in parallel against one
Postgres, and orders is one of the suites the `fix/migration-rerunnability` work already showed to be
sensitive to residual rows.

The same contention accounted for ten web failures observed while a solution run was in flight — the first was
a 20-second axe timeout, cascading into "Axe is already running". `patient-profile-sections.test.tsx` passes
**27/27** on its own, and the full suite is green when nothing else is competing for the machine.

### Pre-existing web suites updated

Four, each recording why:

- `branch.test.tsx` — the heading is "Claims", not "Claims Worklist"; the line queue is its own screen now.
- `table-sortable.test.tsx` — the claims worklist is row-selectable, so the design system renders it as a
  `grid` rather than a `table`. That is the correct ARIA role for a table with operable rows.
- `portal-access.test.tsx` — the break-glass section belongs to the director's portal.
- `table-numeric-columns.test.ts` — see divergence 6.

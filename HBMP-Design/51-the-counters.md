# 51 — The Counters: the desks, the benches and the delivery centre

> **Status:** implemented (pass 6 of the client-vs-service audit, 2026-08-20).
> **Reads on:** [11](11-permission-matrix.md) §3.1–3.2, [14](14-navigation-structure.md) §3.3,
> [37](37-branch-scoping-and-clinical-sensitivity.md) §6, [39](39-patient-profile.md) §3,
> [45](45-encounter-and-prescription-adjustments.md) §2b and §7, [50](50-the-prescribers-portal.md).

---

## 1. Why this document exists

Pass 5 covered the surface where clinical decisions are made and closed with a sentence that generalised:

> A test proves the code it is handed. Nothing in this repository was checking that anybody could get to it.

This pass covered the **counters** — reception, the nurse, the lab and radiology benches, the external
delivery centre, and the call centre's contact block. Six roles, and the same defect class, but the weight
moved again. On a prescriber's screen an unreached endpoint meant a check that reported a clean result it
had not computed. At a counter it means a **person is standing there**, and the thing the screen offers to
do for them does not work.

Two findings define the pass:

**A write that had never once succeeded.** The delivery centre's "Record session" button sent the ORDER id
where the server expected a LINE id, because the projection did not carry a line id and nothing on either
side compared the two. Eleven passing tests covered that endpoint. Every one of them handed it ids fetched
from the database; none of them was ever the screen.

**A verdict computed in the browser.** Reception's eligibility check made no network call at all. It read
the search cache, compared a member status string to `"active"`, and returned that as the answer. Every
property eligibility-service exists to apply — the network tier, the plan version in force on the service
date, the waiting period, the remaining limits — was absent from what a beneficiary was told at the desk.
So was the audit event: *"who checked this person's eligibility, and what were they told?"* had no answer on
the chain, because as far as the platform was concerned nobody had checked anything.

> **The finding that generalises past this pass:** a required parameter nobody at the desk can supply is
> indistinguishable, in production, from an endpoint that does not exist. Three of the eight defects below
> are that: `GET /queues` wanted a `locationId` and a `providerId`; `POST /eligibility/check` wanted a
> benefit category; the procedure counter wanted a line id it was never given. In each case the caller did
> not start supplying the missing thing — the caller went away, or invented an answer locally, and the
> subsystem went quiet in a way no test could see.

---

## 2. What changed

### 2.1 Reception's eligibility verdict comes from eligibility-service (C1)

`checkEligibility` now posts to `/eligibility/check`. The benefit category is **optional** rather than a 400.

The 400 was right about the rule — *"is this member covered" is not a question without naming what for* —
and wrong about what the rule cost. Callers did not start naming a category; they stopped calling the
service. So the category-less question is now answered at **membership scope** and the scope travels on the
wire, because "Eligible" means two different things at the two scopes and renders as the same word. A
consumer that ignores `decisionScope` is wrong in a way it cannot detect, which is why the field is not
optional.

No snapshot is written for a membership check: a snapshot row is keyed by beneficiary **and** category, and
writing one under an invented category would corrupt the next real check for it.

The desk gets a category selector over the five values eligibility `0006` CHECK-constrains, and asks **two**
questions when one is chosen — membership for the visit gate, benefit for cover and cost share. They stay
separate because `NeedsAuthorization` is, in the engine's own words, "a soft No that routes to the approval
team, not a denial"; collapsing them would turn it into a person turned away at the door.

Cost share is a discriminated union — `known`, or a reason why not. An absent copay and a copay of zero look
identical in a nullable field and mean opposite things at a desk with a beneficiary in front of it. The
screen's idle text had promised a copay since phase 2 and no code path could produce one.

### 2.2 The waiting room exists again (C2)

emr's phase-3.3 queue issues a ticket on **every check-in** and five endpoints serve it. Nothing in the
product called one for four phases, so the rows piled up in `Waiting` and were never read, ordered or
cleared. The reason was the signature: `GET /queues` required a `locationId` **and** a `providerId` as
mandatory Guids, and a reception desk has neither — it knows its branch.

Both are optional now, and the reception dashboard has a waiting-room band: who is in the building, in the
server's call order, with call-next, send-back and remove. All five endpoints land on reception because
reception holds `appointment:write`; a doctor holds `appointment:read` only, so arrival decisions were
always the desk's and no scope needed changing.

**Dropping the filters would have widened a disclosure as a side effect.** `ApplyBranchScope` is
deliberately unrestricted for `MemberScoped` callers and the call centre holds `appointment:read`, so an
unfiltered call would have listed every person waiting in every branch on the platform. That is a typed 422,
guarded with `IsBranchRestricted` rather than `== BranchScoped` so a `BranchSetScoped` clinics manager is not
falsely refused. `call-next` also gained the branch narrowing its sibling read always had — the act that
moves somebody must not reach across a boundary the same request could not read across.

### 2.3 The queue that was not one is retired (C3)

`GET /encounters/queue` stood since phase 2.3 with no caller. Three reasons to retire rather than wire it,
and the first alone settles it:

1. **It described the wrong moment.** A `QueueEntry` is written when an **encounter** is created — which is
   when the person stops waiting and starts being seen. A "waiting" list built from encounters lists people
   who are already with a clinician.
2. **It was a second, weaker queue over the same room.** No branch scope, no priority ordering, no audit on
   any transition, and a projection of raw beneficiary ids. Two queues over one waiting room is one queue
   plus somewhere for the two to disagree, and the disagreement would be about who is next.
3. **Its gate was `.RequireAuthorization()` with no scope.** Any authenticated principal on the platform
   could list the beneficiary ids of everyone currently waiting. Nothing called it, so nothing exercised
   that.

The `QueueEntry` entity stays — it is the encounter's open/closed bookkeeping, created with the encounter and
closed by `EndVisit` — with a note on the type saying so, because the name is the trap.

### 2.4 The delivery counter can record a session (C5)

`ProcedureQueueItem` did not carry `OrderLineId`. `ProcedureCentre` passed the order id in both positions,
the server looked for a line with the order's id, and answered **404 to every tap**. The counter's one write
had never worked in the product.

The projection now names its line. The regression test works only from the payload the portal receives —
nothing in it reads the database — because that is the only version of the test that would have failed.

### 2.5 The verification counter shows who was verified (C10)

`/procedure-orders/search` passed `displayName: null` into a projection whose own contract says the name
belongs on that path. A section called **"Verify & Deliver"** rendered nothing to verify against: the centre
was checking a card number against the card number it had just typed in.

The name comes from the resolve response patient-service **already returned** — one disclosure decision, not
two. Asking twice would be two decisions about the same person in the same act, and they would drift. A name
the projector withheld stays withheld and renders as "not disclosed to your centre", never as a blank and
never as a placeholder: the point of the name is to verify the person present, and a fabricated one verifies
nobody.

The **photo** is still null, deliberately. There is a photo endpoint, in profile-service, behind
`profile:read` — which an external delivery centre does not hold and must not be given as a side effect of
this fix. Populating it needs a disclosure decision nobody has made.

### 2.6 The referral loop can be closed (C6)

`POST /procedure-orders/{id}/report` and `reportProcedureCompletion()` had both existed since 29.2b with no
caller, so design 45 §7's obligation — *"a referral loop cannot close without a report back"* — was one no
centre could discharge. The doctor's worklist showed the referral open for ever and the centre had no button.

Wired, with the closure state on the row: a centre cannot be asked to close a loop it cannot see is open, and
re-reporting because the screen said nothing is how one episode becomes two entries in the doctor's inbox.

### 2.7 The nurse's Results Inbox contains results (C7)

The rail said "Results Inbox", the permission was `results.inbox`, and the screen rendered the heart rate and
temperature the same nurse had typed on the other tab. Design 11 §3.2 grants nurses `lab_result R🟠(TR)` and
`imaging_result R🟠(TR)` — the read existed on paper and had no door.

It asks profile-service for the **investigations** section rather than a nurse-specific endpoint, because
that projection is already composed under the caller's own token and already applies design 37 §6: a
restricted result comes back marked restricted rather than omitted, so the nurse sees a locked door instead
of a gap they would read as "not back yet". A section this caller may not read is **absent** from the
response, which is a third answer again, and it is said out loud rather than shown as an empty table.

The vitals readout moved to Vitals, where a nurse about to record a set wants the last one, and now shows the
diastolic and SpO₂ it had been dropping — a panel that displays half of what was measured is how a systolic
reading gets read as a blood pressure.

### 2.8 The bench can attach the report (C9)

orders-service has accepted a `report` file on a result upload since phase 5 and stores it through
document-service. The screen sent only the summary and told the operator that report files "upload from the
workstation", naming a workflow that does not exist here. For radiology **the report is the result**, and a
radiographer with a signed report and nothing to type had to invent a sentence to get past a required field.

Either half is now a complete upload, which is the service's own rule rather than a stricter one.

### 2.9 The call centre can correct a contact (C4)

Design 11 §3.1 gives the call centre `U🟠(contact, CVP)`, and both endpoints have existed since 15.4:
verified-caller-only (403 + audit otherwise), value validated server-side before anything is persisted,
forwarded to patient-service which owns the one-primary rule and the history, audited with the `call_ref`.
Nothing in the workspace called either. *"My number changed"* is among the commonest reasons a member rings
and it was the one thing the agent could not act on; the recourse was to write it in the call summary and
hope somebody read it.

Nothing is decided in the browser. Well-formedness is the service's 422, whether this call may write is its
403, and which contact is primary is patient-service's — so the file is re-read after every write rather than
patched locally. The two refusals are told apart on purpose: 422 is about the value and the member is on the
phone to read it back; 403 is about the call, and retyping the number will never fix it.

---

## 3. What was audited and found correct

| Surface | Why it is right as it stands |
|---|---|
| `POST /investigation-orders/{id}/extend-validity` | Called by approvals' `ValidityExtensionApplier`. Server-to-server by design. |
| `GET /investigation-orders/for-beneficiary/{id}` | Called by profile-service's `ClinicalProviders`. The composed profile is the SPA's door. |
| `GET /eligibility/members/{id}/status` | emr's visit gate calls it before creating a visit. |
| `GET /beneficiaries/resolve`, `…/by-card` | The shared `IBeneficiaryResolver`'s lookup, reached under the caller's token. |
| `POST /investigation-orders/{id}/cancel` | Duplicated by `cancel-lines`, which the SPA's `withdrawOrder` uses and which reports 200/207/409. Left standing; a whole-order cancel is not a defect for having a better sibling. |
| `labSearch` / `awaitingResult` client-side `orderType` filter | See §3.1. |

### 3.1 C8 — the `Imaging` filter, and a second correction to this pass's own audit

The audit flagged `labSearch` and `awaitingResult` for filtering client-side on
`orderType.toLowerCase() === kind`, on the grounds that pre-rename orders carry `Imaging` and would be
silently dropped — a radiographer searching a patient's older imaging order would be told "No match" with the
patient standing there. The same file's `investigationOrder` mapper accepts both spellings and carries a
comment explaining exactly that hazard.

**It is not a defect.** Migration `orders/0009` rewrote every stored `Imaging` to `Radiology` **in place** and
asserts none remain; the runbook's step 2 says so and the enum's own doc says the value "disappears entirely
at the contract step". No row can carry the old spelling today.

What was wrong was the *comment*: it claimed pre-switch orders keep `Imaging` "for the life of the order",
which the backfill contradicts. Left uncorrected it invites the opposite mistake — a reader adding a
dual-accept filter somewhere else to fix a problem that is not there. The comment is corrected and the
dual-accept mapper stays as belt and braces, with the reason stated.

This is the second pass running in which the audit over-called a finding and the correction is recorded
rather than quietly dropped. Pass 5 retired F8 for the same reason.

---

## 4. What the guards caught, and why that matters

Three CI guards rejected the first cut of this pass, and each was right:

- **`queue-table-view`** — the waiting room is an operational queue, so it wants `DataTableView` (toolbar,
  table, pager) rather than a bare `DataTable`.
- **`destructive-actions`** — removing somebody from the waiting room fired `api.removeWaiting` from a
  `ghost` button. It now routes through `ConfirmAction`, which moves the call out of the button entirely.
  They are standing in the room: a mis-tap takes them off the board with nothing on screen to say so.
- **axe `empty-table-header`** — an unlabelled actions column. Only "minor" by axe's grading, which is why
  the route-wide sweep (serious/critical only) let it through and a per-screen audit did not. Two screens
  fixed.

Also found while writing the tests, and worth its own line: emr's test `IBranchDirectory` was called
**`NoBranchRestriction`** and did the opposite of its name — it granted an **empty** permitted set, so every
branch-narrowed read returned nothing whatever was in the table. No test could exercise a branch-scoped read
at all, and one that tried would look like a query bug. It now grants nothing by default and a branch on
request.

---

## 5. Invariants this pass registered

| Invariant | Severity |
|---|---|
| `INV-AN-ELIGIBILITY-VERDICT-COMES-FROM-ELIGIBILITY-SERVICE` | Critical |
| `INV-A-COUNTER-CAN-ACT-ON-WHAT-ITS-ROW-GAVE-IT` | Critical |
| `INV-A-VERDICT-SAYS-WHAT-IT-IS-ABOUT` | High |
| `INV-A-VERIFICATION-COUNTER-HAS-SOMETHING-TO-VERIFY-AGAINST` | High |
| `INV-A-REFERRAL-LOOP-CAN-BE-CLOSED-FROM-THE-CENTRE-THAT-DELIVERED-IT` | High |
| `INV-A-SECTION-SHOWS-WHAT-ITS-NAME-PROMISES` | High |
| `INV-A-WAITING-ROOM-IS-READ-BY-THE-DESK-THAT-KEEPS-IT` | High |
| `INV-A-CONTACT-CORRECTION-IS-DECIDED-BY-THE-SERVICE-THAT-OWNS-IT` | High |

---

## 6. What this pass did NOT cover

**Seven roles remain un-audited:** `beneficiary_mgmt`, `beneficiary_mgmt_supervisor`, `case_manager`,
`provider_admin`, `policy_admin`, `org_admin`, `super_admin`.

**The standing debt was cleared straight after this pass** — and two of the four items this document listed
were already closed and were being carried forward stale. See `docs/BUILD-STATUS.md`, "2026-08-21 — the
standing debt, measured": `admin/0007` was fixed on 2026-08-20 (parity test green, 250-file replay clean),
`inventory:Api` measured 86.5% against a floor of 83, `identity.role_scope`'s empty tenant turned out to be a
named constant the resolver reads rather than debt, and only the `notification:Api` floor was what the list
said it was.

**The procedure counter's photo.** Named in §2.5 rather than left as an empty field with no explanation: the
contract carries `beneficiaryPhotoUrl` and it is null on every path, because the endpoint that could fill it
sits behind a scope an external centre does not hold. Filling it is a disclosure decision, not a wiring job.

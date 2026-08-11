# Medical Director portal — audit and redesign

**Date:** 2026-08-11
**Scope:** the `medical_director` portal (`/director`), the reporting read-model behind it, and the approvals
and claims signals it is supposed to be built from.
**Companion:** `2026-08-11-clinic-management-portal-design.md` — the same exercise for `/branch`. Two of the
findings below are the same defect class as that pass found, in a different plane.

---

## 1. What the portal is today

Ten sections. Three of them — Approval Oversight, Quality & Outcomes, Escalations — are one generic
component (`ReportView.DirectorReport`) that renders KPI headlines and data tables. One is the executive
dashboard. The remaining six are governance editors that were built for other reasons and given a second
door here: master lists, validity periods, document validity, the approvals engine, result-access
escalations.

So the analytical portion of a Medical Director's portal is four screens, and behind them
`reporting-service` is considerably richer than what any of them expose: seven report endpoints, four
utilization dimensions, a six-view analytics subsystem, an async job seam for long ranges and an audited
CSV export. The portal reaches about a third of it, sends no parameters, and drops some of what comes back.

**The framing that produced this document.** A supervisory portal has one job that no other portal has: it
must be *trustworthy about things the supervisor cannot check by hand*. A reviewer can tell when their own
worklist is wrong. A director looking at "p95 turnaround: 4.2 hours" has nothing to compare it against. That
makes two properties load-bearing here in a way they are not elsewhere — the numbers must be **derived from
something the reader cannot author**, and the screen must **say what it is counting and over what window**.
The findings below are sorted by how badly each violates one of those.

---

## 2. Findings

### D1 — the supervised can author their own supervision numbers, and nothing records that they did

`ReportingPolicies.Project` guards `POST /api/v1/reports/projections`, the seam that writes facts into the
read model. The rule is:

```csharp
new PolicyRule {
    Action = Project, ResourceType = Resource,
    Scopes = Set("reporting:project"),
    RequiredConditions = [AbacConditions.TenantMatch],
}
```

It names no roles, and `IAuthorizationEngine.cs:54` reads an empty role set as *any authenticated role*:

```csharp
if (rule.Roles.Count > 0 && !rule.Roles.Any(p.IsInRole))
```

`0001_identity.sql:208` grants `reporting:project` to `medical_director`. So a director's own browser token
authorizes a write into `authorization_fact`, `pending_authorization`, `encounter_fact`, `utilization_fact`,
`code_count` and `financial_fact` — the six tables that produce their own turnaround, SLA-breach, no-show,
rejection and cost figures. The endpoint emits no audit event; every other consequential handler in the
service does.

The rule's own summary already says what it is for: *"System projection seam — a domain event refreshes the
read-model (not a human action)."* The doc comment and the seed disagree, and the seed wins.

**The platform already has the mechanism it needed.** `identity.scope` carries a `service_only` boolean, and
`AdminEndpoints.cs:501` refuses to attach such a key to a tenant-local role — *"a service credential attached
to a person, and no review would ever catch it as one."* `auth:ingest`, `claims:ingest` and
`notification:ingest` are all marked `true`. `reporting:project` is marked `false`. Nothing enforces the flag
against the **built-in** roles, which are seeded straight into `identity.role_scope` by SQL and never pass
through that check.

`finance:project` is the identical defect — same shape of endpoint, same empty-role rule, same
`service_only = false`, granted to `finance`. It is in scope because the invariant that catches one has to
catch both or it is not an invariant.

### D2 — the only money on the portal is structurally always zero

`FinancialFact` powers `/reports/financial-summary` and the dashboard's financial widget. It is written by
exactly one projector case, `ServiceValued`, and **nothing in the platform publishes that event**. This is
not a suspicion; it is recorded in the codebase, in `ProjectionFeedTests.KnownUnfed`:

> `ServiceValued` — nothing values a service as an event. finance-service publishes `SettlementApproved`,
> which is a settlement total for a provider — a different grain from 'this service line was worth this
> much'.

Meanwhile, in the same service, `AnalyticsProjector` consumes `ClaimSettled` for its cost fact, and claims
publishes it (`DecisionEndpoints.cs:84`) at the moment a claim reaches a terminal status, carrying
`ClaimedAmount`, `ApprovedAmount`, `AdjustedAmount`, `CurrencyCode` and `providerId`. The comment on that
payload states the reasoning explicitly: *"`reporting.fact_cost` held zero rows, and this terminal decision
is the moment a claim becomes a cost."*

So: two projectors over one event stream. One was taught where cost comes from. The other was not, and the
one that was not is the one the Medical Director's dashboard reads.

The known-gap registry is a good mechanism and it did its job — it kept the gap visible instead of letting
it rot silently. What it could not do is notice that the reason it records stopped being true.

### D3 — the three dashboards are the same dashboard

`api.executiveDashboard(scope)` takes `"executive" | "finance" | "director"`, and then:

```typescript
const d = await getRaw(`/dashboards/executive`);
```

The scope never leaves the client. The server has no scope concept. Executive, Finance and Medical Director
receive byte-identical payloads; only the page heading differs. The fixture client does the same thing, so
the demo agrees with the defect.

### D4 — the client throws away data the server computed and shipped

Every widget carries a mandatory `dataTable` — that requirement is load-bearing for accessibility (US-073)
and the reason `ExecutiveDashboard.tsx` renders tables unconditionally. But the client sorts widgets first:

```typescript
const isKpi = (w: any) => w.kind === 2 || w.kind === 5;   // Gauge | Summary
```

and KPI widgets are mapped to `{ kind, id, title, value }`. The `dataTable` is dropped. Two widgets are
affected, and they are the two with the most detail in them:

- **Pending approvals** (Gauge) — its table is the breakdown by status × priority × age bucket × SLA
  breaches. It is computed, serialised, sent, and discarded. It renders nowhere in the product.
- **Financial summary** (Summary) — its table is the breakdown by service line. Same.

The KPI value for those widgets is `String(points.reduce((a, p) => a + p.value, 0))`. For pending approvals
that is a count and it is fine. For the financial summary it is a **sum of decimal currency amounts rendered
by `String()`** — no currency, no locale grouping, and exposed to binary-float artefacts — in an application
that has `useFormat().money` and uses it on every other money surface, precisely because money in `ar-EG`
must render in the active locale rather than `en-US`.

### D5 — no period control exists anywhere in the portal

Every reporting endpoint takes `from` and `to`. `Period.Parse` defaults to the last 30 days on Africa/Cairo
business dates; the claims KPI endpoint defaults to 90. The director portal sends neither parameter from any
screen. The consequences are two, and the second is worse than the first:

1. The director cannot ask a question about last quarter, or about the month a policy changed.
2. **Nothing on screen says what window they are looking at.** Two figures from two endpoints with different
   defaults sit in the same KPI row, and there is no way to know they do not cover the same days.

### D6 — the clinic dimension does not work, in two separate ways

This is the one the request named directly — *"more view on utilization, clinics"* — and it is broken deeper
than the UI.

**(a) Clinic workload has no clinic.** `ClinicWorkloadAsync` filters `f.Kind == "Encounter"`, and encounter
facts come from `EncounterStarted`, whose emr payload is:

```csharp
new { encounterId = encounter.EncounterId, encounter.EncounterNo, beneficiaryId = req.BeneficiaryId }
```

No location. `ProjectionMapping.Derive` aliases `locationId → clinicId` for `ApptBooked`, `ApptCheckedIn`
and `ApptNoShow` — but not for `EncounterStarted`, because there is nothing to alias. So the projector falls
to its default and **every encounter fact is written with `ClinicId = "unknown"`**. The Clinic Workload
report groups by clinic and has exactly one group, forever. The dashboard renders it as a bar chart of one
bar.

**(b) Where there is a clinic, it is a raw identifier.** No-show facts do carry a real `locationId`, and it
is rendered to the director as a bare GUID in the dashboard table and in Quality & Outcomes.

`reporting.dim_label` exists for exactly this and its `CHECK` constraint already reserves `'branch'` as a
kind. Nothing writes a branch label. And provider-service — which owns `provider.branch` — already publishes
`BranchCreated` with `branchId`, `BranchCode`, `NameEn` and `NameAr`: everything a label needs. It is not on
the projection feed, and it carries **no `tenantId`**, so `ProjectionMapping.TryMap` would refuse it as
unattributable even if it were. `BranchUpdated` carries the code but not the names, so a rename could not
propagate either.

### D7 — utilization is pinned to one dimension and labelled as a different one

`/reports/utilization` accepts `dimension=provider|drug|lab|radiology`. The dashboard calls it once:

```csharp
var r = await q.UtilizationAsync(tenant, UtilizationDimension.Provider, f, t, ct: ct);
...
new BiText("Utilization by service line", "الاستخدام حسب خط الخدمة"),
```

It ranks **providers** and is titled **by service line**. The other three dimensions — drug, lab, radiology —
are reachable from no screen in the application. The rows are raw codes with no labels.

### D8 — a permission granted, a screen that works, and no way to find it

`medical_director` holds `approvals.sla`. `/approvals/sla` renders the SLA / TAT board, and it renders
correctly for a director: `ResolveRoute` looks the path up in `ALL_ROUTES` (the whole catalog, not the
caller's portal) and then checks the permission, which passes. But the section is declared on the
`approvals` portal base, and `portalForRole("medical_director")` returns the `director` portal, so the
board appears in no navigation the director ever sees.

It is the mirror image of the org-admin "Tenants" item that design 40 removed. That one was a nav entry that
could only ever 403. This is a working screen with no nav entry. Both are a link between authority and
affordance that came apart; this one is the direction that wastes work rather than the direction that
wastes trust.

---

## 3. Decisions

Four questions were put to the product owner. All four took the recommended option.

| # | Question | Decision |
|---|---|---|
| 1 | How far to fix the projection seam | Revoke the human grants, name a service principal on the rule, audit every projection |
| 2 | Where the director's money comes from | Feed `FinancialFact` from `ClaimSettled` — the event claims already publishes |
| 3 | How much new surface | Fix the eight findings in place, add **Utilization** and **Claims & Cost**, give the SLA board a door |
| 4 | Approvals drill-down | A de-identified SLA-breach list from the read model, inside the PHI-free reporting plane |

Two consequences of decision 4 worth stating, because they were the argument against the alternative. The
director does hold `auth:read`, so linking a breach row through to the live authorization was available and
would have been richer. It was declined because it moves the director from an aggregate plane into the
reviewer's operational queue — a supervisor who opens individual cases to check them is doing the reviewer's
job, and the portal would be inviting it. The breach list therefore carries an authorization *number*,
priority, age, status and reviewer, and no beneficiary.

---

## 4. Target architecture

### 4.1 The projection seam becomes a machine seam

- `reporting:project` and `finance:project` are marked `service_only` in the scope catalogue.
- The human grants (`medical_director`, `org_admin`, `super_admin` for reporting; `finance` for finance) are
  deleted from `identity.role_scope`.
- Both `Project` rules name the service principal role explicitly, so an empty role set stops being the
  thing that decides who may write facts.
- Both projection handlers emit an audit event carrying the event type, the tenant and whether the
  projection was applied or deduplicated.

**And an invariant, because the fix without it is a fix for today.** A new test asserts that *any policy rule
naming no roles corresponds to a scope marked `service_only`, and no built-in role holds a `service_only`
scope*. This is the assertion that would have caught D1 the day it was written, and it catches the
`finance:project` twin without anybody having to notice it separately.

### 4.2 Cost comes from the claim that settled

`EventProjector` gains a `ClaimSettled` case writing `FinancialFact`, matching the event
`AnalyticsProjector` already trusts:

- `ServiceLine` — the claim's service line where the payload carries one, else the benefit category.
- `Amount` — `ApprovedAmount` where present, else `AdjustedAmount`, else `ClaimedAmount`. A denied claim
  with a zero net is still a cost fact, as `AnalyticsProjector`'s comment argues.
- `Period` — the settlement day on the Cairo calendar.

The `ServiceValued` case is retired along with its `KnownUnfed` entry. The registry entry is not merely
deleted — the reason it recorded (settlement is the wrong grain for a service valuation) is still true, and
the design note records that the gap closed because a *different* event turned out to be the right grain,
not because the original objection was wrong.

### 4.3 The clinic dimension gets a clinic, and a name

- emr publishes `locationId` on `EncounterStarted`, and `ProjectionMapping.Derive` aliases it the same way
  it does for the three appointment events. Encounter facts stop being `"unknown"`.
- provider-service adds `tenantId` to `BranchCreated`, and adds the names to `BranchUpdated` so a rename
  propagates. Both map to `DimensionLabelled` with `kind = "branch"`.
- `ReportQueries` joins `dim_label` so clinic workload and no-show return a bilingual display name beside
  the id. The id stays in the payload: a director comparing against another system needs it, and a label
  that has silently replaced its key is a label you cannot verify.

Facts written before the label exists keep rendering their id, and are not backfilled. A report about last
month is about the clinic as it was identified last month.

### 4.4 Reads the portal actually needs

| Endpoint | Zone | Purpose |
|---|---|---|
| `GET /dashboards/executive?scope=` | operational | Scope reaches the server; a director's dashboard stops being the finance dashboard |
| `GET /reports/sla-breaches` | operational | The de-identified breach list behind the breach count |
| `GET /reports/utilization?dimension=` | operational | Already exists; all four dimensions become reachable |
| `GET /reports/claims-summary` | financial | Claim outcomes and cost for the director, from the zone they already hold |

Every one takes `from` and `to`, and every one returns the resolved window in its payload so the client can
state what it is showing rather than assume.

**Claims & Cost is served from reporting, not from claims-service.** The director holds
`reporting:read-financial` and does not hold `claims:read` or `claims:reconcile`, and that is the correct
boundary rather than an obstacle: a supervisor needs the shape of what was claimed and denied, not the
authority to open a claimant's file. Granting claims scopes to reach a chart would have widened an operational
authority to satisfy an analytical need.

### 4.5 The portal

**A shared period control.** One control, at the top of every analytical director screen, stating the
resolved window in words. Sent as `from`/`to` on every read. Persisted per session, so moving between
Oversight and Utilization does not silently change the question.

**Two new sections.**

- **Utilization** — all four dimensions, selectable, correctly labelled, period-controlled. The mislabelled
  dashboard widget is retitled to what it ranks.
- **Claims & Cost** — claim outcomes, denial reasons and cost by service line. The section that only becomes
  honest once §4.2 is real; until then it would be a screen of zeros.

**Two repairs.**

- The dashboard client keeps the `dataTable` on Gauge and Summary widgets, so the pending-approvals and
  financial breakdowns render. Money formats through `useFormat().money`.
- The SLA / TAT board is declared on the director portal, and the breach list renders under Approval
  Oversight so a breach count leads somewhere.

---

## 5. Invariants

Numbered from 16, continuing `42-branch-management.md`'s series; recorded there and in the reporting design
notes.

16. **A policy rule that names no roles must guard a `service_only` scope.** An empty role set means "any
    authenticated principal", which is only ever correct for a machine seam.
17. **No built-in role holds a `service_only` scope.** The tenant-local role editor already refuses this;
    the seeded roles bypass that path and must be checked separately.
18. **Every projection is audited.** A write into the read model is a write, and the read model is what the
    platform's oversight is made of.
19. **A fact table that no publisher feeds is either wired or written down.** `ProjectionFeedTests.KnownUnfed`
    already enforces this; the addition is that its reasons are re-read when the event catalogue changes.
20. **An analytical screen states the window it is showing.** A figure with an unstated period is not a
    figure a supervisor can act on.
21. **A permission granted to a role has a door in that role's navigation** — or it is not granted.

---

## 6. Out of scope

- The six-view analytics subsystem (`AnalyticsEndpoints`, phase 19.6b) already has a portal — the policy
  admin's. A director workbench over the same views was offered and declined as duplication.
- `check-gate-freshness` reports five gates that have never recorded an execution. Flagged in the previous
  pass, still true, still unrelated to this work.
- The claims portal's own gaps: claims-service exposes appeals, batches, settlement advice, reimbursement
  and submissions, and the `claims_officer` portal surfaces three screens. That is a claims-portal audit,
  not a director-portal one.

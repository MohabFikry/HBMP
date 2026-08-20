# 47 — Oversight & Analytics (the Medical Director's plane)

**Status:** adopted 2026-08-11.
**Supersedes nothing.** Extends 08 (NFR-006), 11 (permission matrix), 19 (audit strategy) and 36 (claims).
**Spec:** `docs/superpowers/specs/2026-08-11-medical-director-portal-design.md`.

---

## 1. What this document is for

The Medical Director's portal is the platform's **oversight plane**: aggregate, de-identified, and read-only
about work other people did. It has one property no other portal needs, and the whole design follows from it.

> A supervisor cannot check the numbers by hand.
>
> A reviewer can tell when their own worklist is wrong; the queue is in front of them. A director looking at
> "p95 turnaround: 4.2 hours" has nothing to compare it against. Every figure on this portal is therefore
> load-bearing in a way an operational screen's figures are not — and so are two things about it: **who could
> have authored it**, and **what window it covers**.

The 2026-08-11 audit found the portal failing both. This document records the rules that came out of it.

---

## 2. The projection seam is a machine seam

`POST /api/v1/reports/projections` and `POST /api/v1/finance/projections` write the read models. Both are
guarded by a policy rule that names **no roles**, which `IAuthorizationEngine` evaluates as *any authenticated
principal holding the scope*.

That is correct for a machine caller and only for one. The event relay authenticates with client credentials
and carries no role at all, so naming a role on the rule would deny the sole legitimate caller. What makes the
construction safe is the other half of the pair: the scope must be one **no person can hold**.

Both scopes were `service_only = false` and granted to people — `reporting:project` to `medical_director`,
`finance:project` to `finance`. So the Medical Director's own browser token authorized a write into the six
fact tables their own turnaround, breach, no-show, rejection and cost figures are computed from, and the
handler recorded nothing.

**The rule.** A policy rule that names no roles must guard a `service_only` scope, or the exception must be
written down with its reason. `ProjectionSeamTests` (libs/authz) asserts both directions across every bundle.
Three roleless rules are legitimate — `document:write`, `policy:read`, `eligibility:check` — where a scope
grant IS the whole authority; each is registered.

**Known and unfixed.** `ProtectedScopeRequiresMfa` gates these endpoints on an `acr`/`amr` only an interactive
login produces, so the transport check admits a human and refuses a client credential — pointing the opposite
way to the authorization rule in front of it. Not changed: it is a platform-wide auth decision, and the
production path for projections is the queue consumer, not HTTP.

---

## 3. A fact that cannot be attributed is not written

`ProjectionConsumer` binds its RLS session from the event envelope and **dead-letters** a message carrying no
tenant, because a guessed tenant puts one organisation's figures in another's dashboard. This is the right
behaviour and it has a consequence the audit found the hard way: a publisher that omits `tenantId` does not
corrupt the read model, it **silently empties** it.

Four emr publishers on the projection feed omitted it — `ApptBooked`, `ApptCheckedIn`, `ApptNoShow`,
`EncounterStarted` — so clinic workload and the no-show rate had no facts at all. approvals-service's
break-glass path omitted it too, so the TAT report was missing exactly the manual and emergency decisions a
supervisor most wants to see. Nothing failed; the dashboards rendered zero.

**The rule.** Every event on a queue a tenant-binding consumer reads carries `tenantId`, asserted at the
publish site by `TenantOnEnvelopeArchitectureTests`. Its guard counts publish sites **per queue**, because a
summed count stays healthy while one queue goes dark.

---

## 4. Cost enters the read model at settlement, at two grains

| Event | Grain | Table | Answers |
|---|---|---|---|
| `Claim{Status}.v1` | one claim | `fact_cost` | what was claimed, approved, adjusted; by payer and tier |
| `ClaimLineSettled.v1` | one claim line | `financial_fact` | what it cost, by service line |

Both fire in the terminal decision's own transaction. They are deliberately **not** interchangeable: a
projector that learned the other's event would count every settled claim twice, and a financial summary that
quietly doubles looks like growth.

Three constraints worth stating:

- **Settlement, not adjudication.** Adjudication is a pre-decision recommendation; booking it as cost records
  money a reviewer may still reduce, and records it again when they do.
- **Allowed, not billed.** A summary of what providers asked for is not a summary of what the benefit cost. A
  denied line settles at zero and is still emitted, so the denominator is every line that reached a decision.
- **The service line is the coding system** (CPT / LOINC / DRUG / LOCAL), not the fulfillment type. The latter
  is `OrderFulfillment | DispenseEvent | None` — it records *how* a line was fulfilled, so lab and radiology
  would share one row and the breakdown would have two entries for the whole benefit.

---

## 5. The oversight portal reads from reporting, never from the operational services

The Medical Director holds `reporting:read`, `reporting:read-financial` and `reporting:export`. They hold
neither `claims:read` nor `claims:reconcile`, and **that is the boundary rather than an obstacle**: a
supervisor needs the shape of what was claimed and denied; opening a claimant's file is the claims officer's
authority. Claims & Cost is therefore served from `/reports/claims-summary` in reporting's financial zone.

The same reasoning governs the SLA-breach drill-down. The director holds `auth:read` and could open any
breached case, and the breach list still carries no beneficiary — the authorization number, its priority, its
age and whose desk it is on. A supervisor who opens individual files to check them is doing the reviewer's
job, and a portal that made that one click would be inviting it.

**The rule.** An analytical need is met by widening the analytical plane, never by granting an operational
scope to render a chart.

---

## 6. Every figure states its window

Every reporting endpoint has accepted `from`/`to` since phase 8.2. The director portal sent neither, from any
screen — so the director could not ask about last quarter, and two KPIs built from endpoints with different
server defaults (thirty days and ninety) sat in one row with nothing saying they covered different spans.

One period control, shared across the portal, persisted for the session, stating the resolved dates in words
beside the preset name. Presets rather than a free range: a supervisory question is almost always "this
month", "last month", "the quarter", and a free range invites the one failure this control exists to prevent —
two screens quietly disagreeing about the window. "This quarter" is the calendar quarter, not the last ninety
days; a director comparing against a board report is comparing against quarters.

---

## 7. A widget says what it ranks, and nothing computed is thrown away

Two defects of the same family, both in the dashboard client:

- The utilization widget was keyed and titled **"by service line"** while querying
  `UtilizationDimension.Provider`. It ranked providers beneath a heading promising a different axis, and three
  of the four dimensions were reachable from no screen. It now says "by provider", and the Utilization section
  covers all four.
- Gauge and Summary widgets were mapped to a bare `{ title, value }`, **discarding a `dataTable` the server
  had already computed, serialised and sent** — pending by status × priority × age × SLA breach, and cost by
  service line. Neither rendered anywhere in the product.

The financial KPI was additionally `String(sum-of-decimals)`: no currency, no locale grouping, in an
application where every other amount goes through `useFormat().money` precisely because `ar-EG` must not read
as `en-US`.

**The rule.** A widget's title names the axis it queries, and a payload the server sends is either rendered or
deliberately not requested.

---

## 8. Authority and affordance are one join

`medical_director` has always held `approvals.sla`, and `/approvals/sla` has always rendered for them — the
router resolves a path against the whole catalog and then checks the permission, which passed. But the section
was declared only on the approvals portal, which `portalsForRoles` never returns for a director. A working
screen they were entitled to appeared in no navigation they could see.

It is the mirror image of the org-admin "Tenants" entry design 40 removed, which was a link that could only
ever 403. One wastes trust — the person concludes the platform is broken. The other wastes the work that
built the screen.

**The rule.** A permission a role holds has a door in that role's navigation, or it is not granted. Asserted
in both directions by `director-portal-screens.test.tsx`, with `profile.read` / `profile.export` registered as
the deliberate exception: the unified patient profile is opened *for* somebody from a worklist or a search
result, never navigated to from a menu (design 39 §6).

---

## 9. Invariants

Numbered from 16, continuing the series in `42-branch-management.md`.

16. A policy rule that names no roles must guard a `service_only` scope, or be a registered exception.
17. No built-in role holds a `service_only` scope.
18. Every projection into a read model is audited, and takes its tenant from the principal, not the body.
19. Every event on a tenant-bound queue carries `tenantId`, asserted at the publish site.
20. A fact table that no publisher feeds is either wired or written down with the reason.
21. An analytical screen states the window it is showing, in dates.
22. A widget's title names the axis it queries.
23. A permission a role holds has a door in that role's navigation.
24. An analytical need is met by widening the analytical plane, not by granting an operational scope.

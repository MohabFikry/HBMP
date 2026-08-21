# 52 — Administration and Coordination: the governance console, the network and the case load

> **Status:** implemented (pass 7 of the client-vs-service audit, 2026-08-21).
> **Reads on:** [07](07-functional-requirements.md) FR-IAM-003 and FR-IAM-007,
> [10](10-role-matrix.md) §3.11 and §3.13–3.14, [11](11-permission-matrix.md) §3.3,
> [18](18-security-model.md) §2 and §4, [19](19-audit-strategy.md) §7,
> [38](38-policy-member-administration.md) §4, [50](50-the-prescribers-portal.md), [51](51-the-counters.md).

---

## 1. Why this document exists

Pass 5 covered the prescriber's portal and closed with a sentence about tests:

> A test proves the code it is handed. Nothing in this repository was checking that anybody could get to it.

Pass 6 covered the counters and closed with one about parameters:

> A required parameter nobody at the desk can supply is indistinguishable, in production, from an endpoint
> that does not exist.

This pass covered the **administrative and coordinating** roles — the ones with no patient in front of them:
`org_admin`, `super_admin`, `provider_admin`, `policy_admin`, `case_manager`, `beneficiary_mgmt` and its
supervisor. Six portals, thirty-three sections. The weight moves again, and the sentence that generalises is
about **authority**:

> **An authority granted end to end in the token, and never given a door, is invisible from every side.**
> The identity seed grants it. The service implements it and tests it. The design document specifies it. The
> token carries it into every request the portal makes. And nobody can do it, because no screen ever asks.
> Nothing fails; there is simply a job the platform was built to support and does not.

Three of the four findings below are that. The fourth is its mirror image — an authority the client **offered**
and the server refuses — and the two turn out to have the same root: a client-side copy of a server rule,
written against names that do not mean what the rule means.

Two findings from this pass are also worth stating on their own, because they are not about a portal:

**A cross-tenant write.** `admin.access_review_item` carries no `tenant_id` and has no RLS policy, on the
stated ground that its rows are "reached only through their campaign, which IS isolated". The decision path
did not reach it through the campaign. §2.2.

**A CI gate that had been told to look away from the thing it exists to catch.** §3.1.

---

## 2. The governance console (org_admin, super_admin)

### 2.1 An access-review campaign could not be reviewed (A1)

`POST /admin/access-reviews/items/{itemId}/recertify` and its `revoke` twin are keyed by an item id, and **no
endpoint on any service returned one.** There was no items read anywhere in the platform, so the two decisions
an access review consists of were addressable only by somebody who already knew a uuid.

The consequence is a campaign that can be created, counted, and swept at its deadline — where sweeping means
auto-expiring every grant nobody confirmed, which revokes the underlying binding. So the only reachable
outcome of a recertification campaign was **removing everybody's access without anybody having assessed
anything**. FR-IAM-007 was satisfied on paper by a surface no administrator could complete.

`GET /admin/access-reviews/{campaignId}/items` is new. It is grouped under `admin:read` rather than
`admin:write`, because reading a worklist is not a change.

The row carries the subject's **user id**, and that is the opposite decision from the break-glass register
below — deliberately. This surface asks *"does this person still need this role"*, which nobody can answer
about `•••4f2a`. The name is resolved through identity's `/user-labels`, the same helper the appointment
timeline uses, and a failure there degrades to the id rather than dropping the row: a reviewer who cannot see
a name can still look an id up, and a worklist that quietly omits a grant is worse than an unfamiliar one.

### 2.2 A tenant could defeat its neighbour's access review (A1b)

Found while wiring §2.1, and the more serious of the two.

`AccessReviewService.DecideAsync` read the item with `FirstOrDefaultAsync(i => i.ItemId == itemId)`. The
`tenant` argument it accepted was used only to stamp the audit event.

`admin.access_review_item` is listed in migration `0005_tenant_rls_fail_closed.sql` under "deliberately NOT
tenant-isolated", with the reason *"child rows reached only through their campaign, which IS isolated; the FK
makes an orphan read impossible without first passing the campaign policy."* That reasoning is correct and
this query did not reach the row through the campaign, so it did not hold here. A foreign key prevents an
orphan; it does not prevent a read.

What was reachable is narrower than it first looks, and worse than it sounds. `role_binding` **is**
RLS-isolated, so the revoke half found no binding and returned silently — the underlying grant was never at
risk. What did land was the write to the review record: another tenant's administrator, holding an item id,
could mark a grant **Recertified**. Recertifying is exactly what stops `SweepExpiredAsync` removing it at the
deadline. The act available was not "read something you should not"; it was "silently defeat another tenant's
access review of its own T4 grants".

The read is now scoped through the campaign, with an explicit tenant predicate *and* the campaign's own RLS
policy in the same subquery, so the two controls have to fail together.
`One_tenant_cannot_recertify_another_tenants_grant_and_defeat_its_review` fails against the old query — it was
run against it to check.

### 2.3 The registers dropped the numbers they exist for (A2)

`DashboardService.BreakGlassAsync` joins every recorded access under each emergency grant and returns
`accessCount` and `outOfScopeCount` — the second being **the number of times the grant was used to reach
something it did not cover.** `zBreakGlassGrant` had no field for it. Nor for the use count, nor the approver,
nor `postReviewDone`.

So the governance dashboard rendered "Active · expires 14:20" and silently discarded *"and four of its eleven
uses were out of scope, and nobody has looked at it since it lapsed"*. That is not an incomplete report; it is
the report omitting its own subject.

`AccessReviewCampaignView` was the same shape of loss. Its **server-side doc comment already explains why the
counts are broken out**:

> `pending` is work outstanding, `revoked` is access actually removed, and `autoExpired` is access removed BY
> THE DEADLINE PASSING rather than by anyone deciding. A single "closed" figure would fold the last two
> together, and only one of them means somebody reviewed anything.

All five were dropped in the client mapping. A campaign row read "Open, due Friday" whether nobody had started
it or somebody had finished it.

An org admin also holds `AdminPolicies.BreakGlassApprove` — the rule names `medical_director`, `org_admin`,
`super_admin` — and the console that lists emergency requests had no approve or refuse control, so a
clinician's request sat at `Requested` with nobody able to answer it from the screen that showed it.

### 2.4 The minimisation was performed in the browser (A2b)

The break-glass table shows a governance token instead of a name, and says so on screen:

> Requesters appear as governance tokens, not names — this dashboard records emergency access, and pairing a
> name with each one would make it a directory of who reached what.

That reasoning is right. The implementation put it in the wrong tier: admin-service sent `RequesterUserId`
whole and `HttpApiClient` rendered `` `•••${id.replace(/-/g, "").slice(-4)}` `` before display. The rule held
for anybody looking at the table and for nobody looking at the response. Design 18 §2 puts min-necessary
projection on the server precisely because the client is not where a disclosure decision can be enforced.

`GovernanceToken` is now the single place it happens, and its doc comment states what it is not: a truncation,
not a keyed pseudonym. If the register ever needs one, there is now one place to put it.

### 2.5 The SoD rules were shown and the breaches were not (A3)

The Access Catalogue renders the pairs of roles that must not be held together.
`GET /admin/dashboards/sod-violations` returns the people **currently holding one** — defence-in-depth against
a grant path that missed a check — and nothing called it. An administrator could read the entire
separation-of-duties policy without learning it was being broken in their own tenant. It also had no
`.Produces`, so the spec could not describe it.

---

## 3. The network (provider_admin)

### 3.1 The roll-up was counted in the browser, past the authorization (N1)

`NetworkPerformance` fetched the provider **directory** and counted it:

```ts
const by = (label: string) => rows.filter((r) => r.status.label.en === label).length;
```

`status` is the `{kind, label}` chip this client assembles for rendering. Three things follow, and only one is
about tidiness:

1. **It counted a display label.** The tally depended on a piece of English prose surviving unchanged. A
   relabelling, or a server status the chip mapper does not recognise, produces four zeroes — silently, and
   four zeroes look like a small network rather than a broken screen.
2. **It counted the projection**, not the tenant.
3. **It computed a figure the service refuses.** `GET /api/v1/metrics` answers a provider-scoped caller with
   403: a provider must not learn the shape of the network it competes in. An authorization the client can
   route around by counting rows is not one.

The endpoint has returned exactly `{total, active, suspended, terminated}` since phase 2b. It had **no Kong
route**, which is why nothing called it.

And the gate that exists to catch an unrouted resource had been told to look away. `check-kong-route-coverage.py`
carried `IGNORE_SEGMENTS = {"health", "metrics"}`, meant for the Prometheus scrape and `/health/live`. Both of
those are served **unversioned**, so neither ever reached that check — the set only ever did anything to
`/api/v1/metrics`. Narrowed to `health`; the gate then failed, which is how the route came to be added.

> A guard's exemption list is a claim about what is safe to ignore. This one had never been re-read against
> what it actually excluded.

### 3.2 Two issuer roles share one portal, and the client mirror could not tell them apart (N1b)

`ROLE_MAP` contains both of these:

```ts
["provider_admin", "provider_admin"],   // one provider's own administrator — T4, ABAC provider-bound
["network_team",   "provider_admin"],   // Mersal's Network Team — T2, tenant-wide
```

Design 07 FR-IAM-003 lists them as separate portals. Design 11 §3.3 gives them different rows: the Network
Team has `C✅ R✅ U✅ A🟠SOD` on provider/contract/catalog; Provider Admin has `C🟠PO R🟠PO U🟠PO` — their own
operations only. `HbmpPrincipal.ProviderScopedRoles` names `provider_admin` and not `network_team`.

`mayAdministerTiers` compared the **portal** name against a server rule naming
`network_team | org_admin | super_admin`:

```ts
return role === "provider_admin" || role === "org_admin" || role === "super_admin";
```

So a provider's own administrator was offered Create tier and Revoke assignment, each refused by the server
with `urn:hbmp:network-tier-access-denied`. And the function's own doc comment said *"the server's
`NetworkTierGate` draws exactly this line and returns 403 either way"* — which is what made it hard to see. The
comment described the intent accurately and the code did something else.

`Session.issuerRoles` now keeps the token's own names alongside the mapped portals. It grants nothing; it is
the same claim, not discarded, so a mirror of a server rule can be written against the names the rule uses.

> Where a client mirrors a server authorization decision, it must compare the same vocabulary. A portal is the
> right unit for choosing a rail and the wrong unit for deciding an authority.

### 3.3 What could be withdrawn could not be created (N5)

`NetworkTierAdmin` revokes tier assignments. Its confirmation dialog says:

> The assignment can be re-created, but claims already priced are not repriced.

`assignTier` was implemented in `policyApi` and called by nothing, and `updateTier` likewise. So the screen
removed rows from a table nothing in the platform could fill, while telling the operator otherwise. A tier
created with a typo in its Arabic name kept it — and the only remedy on offer was the create panel's advice to
retire the tier and make another, which is right for the code and the out-of-network flag (priced claims refer
to them) and not right for a name.

The provider is **picked, not typed**. The resolver on the same screen takes a raw uuid because it is a read —
getting it wrong wastes a lookup. Getting it wrong here reprices somebody's claims from a date. Its actions
column also had an empty `<th>`, which axe reports as `empty-table-header`; the portal had no tests at all, so
nothing had ever scanned it.

---

## 4. The case load (case_manager)

### 4.1 The role could read everything and do nothing (P1)

`case_manager` has held `case:read`, `case:write` **and** `case:manage` since the 0001 identity seed. Design 11
§3.3 gives it `C🟠ASG R🟠ASG U🟠ASG` on `approval_case`. Design 10 §3.11 lists "open/track cases; coordinate
referrals; manage care plans" among its key capabilities. case-service implements nine write endpoints against
those scopes, with a state machine, an outbox event and audit on each.

The SPA gave the role three read permissions and reached none of the nine.

Concretely: a coordination task could be listed and never completed. An escalation could be read and never
raised or resolved. A case could never be closed. A caseworker's load only ever grew, and the count beside
their name stopped meaning anything after the first week.

This is the pass's defining shape and it is **not** the pass-6 finding. There was no missing parameter and no
broken call. Every layer was complete and correct on its own terms; the door was never cut.

Wired: start and complete a task with its outcome note, raise an escalation, acknowledge and resolve one, and
close a case. Every transition offered is one `CaseWorkflow` allows from the row's current state — `Done` and
`Resolved` are terminal, and get no control rather than a control that returns 409.

### 4.2 The escalation register showed everything as outstanding, permanently (P1b)

`HttpApiClient.escalations` wrote the status chip as a **literal**:

```ts
// An escalation is by definition something that needed raising.
status: { kind: "warn", label: { en: "Escalated", ar: "مُصعَّدة" } },
```

True of the act; false of the record. case-service tracks Raised → Acknowledged → Resolved. Every row rendered
the same amber chip, in the one table whose entire purpose is separating what is outstanding from what is
done — and since nothing could resolve an escalation anyway, there was no state of the world in which that
display would ever have been wrong for a different reason.

### 4.3 One screen under two names (P2)

`/cases/my-cases` and `/cases/beneficiary-360` both routed to `<MyCases />`. The rail offered one screen twice
— the duplication the lab and pharmacy portals had each removed by 32.6. The 360 is the detail panel that
opens beside the list when a case is selected; it has never been a separate screen, and a nav entry claiming
otherwise is how somebody comes to look for a beneficiary-first view that does not exist.

Removed, along with `case.beneficiary360`, which had nothing behind it once the section went — the rule 19.7
stated: *a permission granted to a role with nothing behind it is one nobody can reason about.*

**A correction from this pass's own audit.** The audit initially recorded
`GET /api/v1/cases/for-beneficiary/{beneficiaryId}` as unreachable. It is not: profile-service composes the
patient profile's `cases` section from it (`ProfileFacts.cs`, `ClinicalProviders.cs`), and
`SectionWiringTests` pins the wiring. The endpoint is service-to-service and was correctly excluded from the
SPA. Recorded here rather than dropped, as C8 was in [51](51-the-counters.md) §3.1 — an audit that quietly
deletes its wrong answers is one whose right answers cannot be checked either.

---

## 5. What this pass did NOT cover

Everything below was found, verified and deliberately left. Each is a **product decision or a build**, not a
client-vs-service repair — the line this pass drew is: *fix where the screen makes a claim it cannot keep;
record where the screen simply does not offer something.*

**The provider portal split.** §3.2 fixes the tier-administration mirror. It does not fix the underlying
arrangement: design 07 FR-IAM-003 lists Provider Admin and Network Team as separate portals and design 11 §3.3
gives them different rows, and the SPA serves both from one `/network` portal with one section list. A
provider's own administrator sees a "Providers Directory" of exactly one row, an Onboarding form for creating
providers, and a Performance section that now explains it is not theirs. Splitting them is a question about
what a provider's own administrator should see, which is a design decision with no current answer in the repo
beyond the matrix rows.

**Provider lifecycle.** `POST /providers/{id}/activate | suspend | terminate | credentials | users | locations |
contracts` and `POST /contracts/{id}/activate | service-lines` are all unreached. The Onboarding form's success
message says *"Provider created (Draft) — proceed to credentialing"*, and there is no credentialing screen — so
a Draft provider can never leave Draft, while the directory filters and the Performance card display the
states it can never reach. This is the largest single gap the pass found and it is a build: eight endpoints and
a workflow, not a wiring fix.

**Creating the benefit product.** `POST /payers`, `POST /plans`, `POST /plan-versions`, `POST /policies`,
`POST /policies/{id}/renew`, `POST /policies/{id}/plans` and `POST /policies/{id}/groups` are unreached. A
policy administrator can amend a plan, price it, validate and activate a version — and cannot create a payer,
a plan or a policy. Nothing on screen claims otherwise, which is why it is here rather than in §3.

**The extract engine.** Design 38 §4.4b specifies extracts as a first-class half of the bulk subsystem
("One engine both ways"). `GET /extracts/columns`, `GET|POST /extracts/definitions`, `POST /extracts/run` and
`GET /extracts/runs` are all built and have no door. `POST /bulk-jobs/{id}/rollback` likewise, which means the
"reversible by batch" property the same section promises is not currently reachable.

**Single-member enrolment and group management.** Design 38 §11 states the beneficiary-management officer's job
as *"Enrol/terminate members, manage groups"*. The portal can terminate, reinstate, change plan and change
group; it cannot **enrol** one member (only a CSV batch can), and cannot create or edit a group. `enrol` and
`createGroup` are implemented in `policyApi` and called by nothing.

**Case assignment.** `POST /cases/{id}/assign` and `/unassign` are `case:manage`, which `case_manager` holds.
They are left unwired because who holds a case is a supervisor's decision and there is no supervisor surface to
make it from; adding the buttons to the caseworker's own screen would let a caseworker assign themselves a
case, which is the opposite of the ABAC anchor the whole portal rests on.

**Note pinning and document verification.** `POST /notes/{id}/pin|unpin` and
`POST /documents/{linkId}/verify|withdraw` are unreached.

---

## 6. The numbers

| | |
|---|---|
| Roles audited | 7 (`org_admin`, `super_admin`, `provider_admin`, `policy_admin`, `case_manager`, `beneficiary_mgmt`, `beneficiary_mgmt_supervisor`) |
| Portals | 6 · 33 sections |
| Findings recorded | 19 |
| Fixed in this pass | 10 |
| Recorded as non-scope, with reasons | 9 (§5) |
| New invariants | 10 |
| Services touched | admin, provider |
| CI gates changed | `check-kong-route-coverage.py` (ignore list narrowed), `check-response-schemas.py` (`--update` no longer erases `_lowered`) |

All seven roles named at the close of [51](51-the-counters.md) §6 are now audited. Every portal in the catalog
has been through a client-vs-service pass.

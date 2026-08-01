# ADR-0029 — Branch management: one permission set, two reaches

**Status:** **Accepted** — D1–D5 ratified as recommended 2026-08-01 (§4) · **Date:** 2026-07-31 · **Phase:** 25.0
**Supersedes:** nothing · **Extends:** [ADR-0021](0021-user-access-model.md) (authority vs reach) and
[ADR-0014](0014-phase-14-sensitivity-retrofit-scope.md) (branch scoping) — **additively**.
**Design:** [`HBMP-Design/42-branch-management.md`](../../HBMP-Design/42-branch-management.md) ·
Build prompt: `HBMP-Design/claude-code-prompts/phase-25-branch-management.md`

> **Numbering.** The build prompt asks for this as `0025-branch-management.md`. `docs/adr/0025` was already
> taken by the bulk-and-extract engine, and ADRs run to 0028. ADR numbers are a single sequence across the
> platform, not per-phase — phase 21 hit the same collision and 24.7 resolved it by renumbering (ADR-0028).
> This is that resolution, recorded rather than silently applied: the phase is 25, the ADR is 0029.

---

## Context

Six Mersal clinics (ASW/ALX/OCT/MAA/DOK/NSR) are run by people the platform has no role for. A **Branch
Coordinator** runs one clinic; a **Clinics Manager** supervises all six. They need everything Reception can do
plus the practitioner roster, specialties and licences for their branch, the availability that feeds
appointment slots, and the clinic's own consumable stock.

Three of the four things they need already exist and one does not:

| | State before this phase |
|---|---|
| `provider.branch` + six seeded codes, `provider.practitioner` (incl. `license_no`/`license_expiry`), `practitioner_specialty`, `practitioner_branch_assignment`, `admin.user_branch_assignment`, `emr.provider_availability` → `emr.appointment_slot`, `BranchScope` ABAC, `RowScope` sentinel | **Built** |
| Multi-branch-simultaneous reach | **Not expressible** — `RowScope.WithBranchScope` narrows to exactly one active branch |
| Licence enforcement | **Fields exist, nothing reads them** — an expired doctor is still bookable |
| Roster exceptions (leave, holiday, closure) | **No concept** — the only way to stop slots is to delete the weekly rule |
| Clinic inventory | **Does not exist anywhere** |

`branch_manager` and `clinic_manager` appear in `libs/authz/BranchScope.cs` and
`apps/web/src/shell/useBranchContext.ts` as **phantom names** — referenced in branch-scoping code, never
seeded as identity roles, never held by any principal. Two spellings of an idea that was never built.

## Decision

### 1. One permission set, two reaches — authority and reach are separate questions

Branch Coordinator and Clinics Manager hold an **identical scope set** and differ *only* in how many branches
they reach.

**Rejected alternative: two roles with two capability lists.** It is the obvious shape and it fails
predictably. The two lists drift — someone adds "revoke specialty" to the coordinator, forgets the manager,
and the person supervising six clinics can do less than the person running one of them. Nobody notices,
because the manager's remedy is to ask a coordinator, and asking works. The drift is only discovered when it
has been true for months.

This is the [doc 40](../../HBMP-Design/40-user-access-model.md) separation applied literally: *what may you
do* (authority) is one question, *over which data* (reach) is another, and collapsing them is where these
systems go wrong. Keeping them separate means the invariant is mechanically checkable, so we check it: a test
asserts the two scope sets are **equal in both directions** and fails loudly if a future phase grants one a
scope the other lacks.

Sixteen scopes: reception's exact twelve (`reception:search`, `reception:read`, `eligibility:check`,
`appointment:read`, `appointment:write`, `patient:read`, `practitioner:read`, `note:read`, `profile:read`,
`callcentre:history:read`, `notification:read`, `claims:reimburse:submit`) plus four new branch-scoped ones
(`branch:practitioner:write`, `branch:roster:write`, `branch:inventory:read`, `branch:inventory:write`).

**Not `emr:read`.** They run the clinic; they do not read clinical notes.

**Not `provider:write`, ever.** That scope is network-wide: it would let a clinic coordinator create branches
and edit external labs and pharmacies, and it is also the scope that currently unmasks `license_no`. Sizing
the new scopes to the branch is what lets a coordinator maintain a licence without holding the authority to
re-price the external network.

### 2. `BranchSetScoped` — a third reach mode, because the two we had are both wrong here

`BranchScopeModes.ModeFor()` classifies a principal into `BranchScoped` (narrowed to one active branch),
`MemberScoped` (unrestricted) or `ProviderScoped`. "All six clinics at once" is expressible in neither:

- **`BranchScoped`** makes the manager switch branches one at a time. Wrong — they supervise *across*
  clinics; a licence-alert worklist that shows one sixth of the alerts is not a supervisory tool.
- **`MemberScoped`** is unrestricted. Wrong, and worse: it is an ungoverned *everything* with no grant behind
  it. Reach that no assignment produced cannot be reviewed, revoked, or explained.

**Therefore a third mode**, where the predicate is `branch_id ∈ PermittedBranchIds` rather than
`= ActiveBranchId`. The permitted set still comes from real, auditable `user_branch_assignment` rows — a
clinics manager simply holds all six. **Reach stays grant-derived, never role-derived.**

`ActiveBranchId` becomes an optional **filter** in this mode rather than a restriction: setting it narrows the
manager's view to one clinic, clearing it restores all six. That is what makes one branch control serve both
roles — it *switches* for a coordinator and *filters* for a manager — and it is why this phase builds one
portal instead of two.

The `NoBranchSentinel` fail-closed behaviour is preserved exactly: an unresolvable set injects the sentinel
and matches **zero** rows, never all of them. An empty branch predicate does not mean "nothing", it means
"every branch in the tenant", which is why the sentinel is a value and not a null check.

`ModeFor` still derives mode from role names — that seam is unchanged and phase 21 moves reach to grants.
`clinics_manager` maps to `BranchSetScoped` explicitly. It must **not** fall through to `MemberScoped`.

### 3. Licence expiry is a safety gate, not a field

`license_no` and `license_expiry` have existed since provider migration `0006` and **nothing reads them**.
Bookability checks practitioner status and branch assignment only. A doctor whose licence expired last year
is bookable today. That is the most consequential finding in the design, and it is closed here:

- **Slot generation and booking exclude a practitioner whose licence has expired *as at the slot date*** —
  not as at today. Booking three months out against a licence expiring next month must fail at generation,
  not surprise a patient on the day.
- **Existing future appointments are flagged, never cancelled.** `appointment.reassignment_needed_at` already
  exists (migration `0012`); a coordinator worklist surfaces them with patient contact. **A person decides
  who covers the clinic. The system does not cancel a refugee's appointment by itself** — the harm of an
  automated cancellation lands on someone who may not have a reliable phone number and cannot easily travel
  again.
- **Warnings at 90/60/30 days**, to the coordinators of every branch the practitioner serves, following the
  existing `ProviderCredentialExpiring` precedent and the `ReportAccessExpirySweeper` pattern.
- **Never retroactive.** Past encounters, past appointments and historical records are untouched. Expiry is a
  gate on future scheduling, not a re-judgement of care already given.

### 4. Inventory is a ledger, and it never touches a patient

`stock_movement` is **append-only** — `REVOKE UPDATE, DELETE` at the database, the same discipline as the
approvals decision ledger and the audit chain. **On-hand is `SUM(quantity)` over movements; there is no
`quantity_on_hand` column anywhere.** A balance you can recompute is a balance you can reconcile, and a
balance you cannot reconcile is a number people stop trusting. A physical stock-take is a `Count` movement
recording the variance, not an overwrite of history.

**Clinic inventory never dispenses to a patient, and carries no beneficiary identifier — not in a column, not
in a parameter.** Anything requiring a prescription goes through `pharmacy-service`, against an `Rx`, with
the authorization and benefit rules that entails. If clinic inventory could issue medication to a
beneficiary, it would be a route around eligibility, coverage limits, formulary and the dispense audit trail
— every control the platform exists to enforce. Keeping inventory PHI-free is also what lets a storekeeper
use it without a clinical role. Asserted by a test over both the route table and the schema, so the boundary
cannot erode by someone adding "just an optional encounter id".

### 5. Availability is computed in exactly one place

`emr.provider_availability` is a weekly recurring rule with no way to express leave, a public holiday or a
clinic closure, so today the only way to stop slots appearing is to delete the rule — which also erases the
normal pattern. A `roster_exception` layer is added (`Leave`, `PublicHoliday`, `ClinicClosed` subtract;
`AdHocClinic` adds), and availability becomes:

> recurring rule − exceptions ∩ active branch assignment ∩ valid licence ∩ practitioner Active

**One function computes this**, and the doctor picker, `GET /booking/doctor-availability`,
`GET /appointment-days`, slot materialization and the booking validator all call it. A second implementation
is the bug, not an optimisation: the way that failure presents is a patient given an appointment with a
doctor who is on leave.

Roster changes affect appointments that already exist, so a change produces an **impact preview** (how many
booked appointments, which) before it applies, then flags them — never bulk-cancels.

## Consequences

- A future phase that grants a capability to one branch role grants it to both, or CI fails. That is the
  point, and it is the only enforcement that survives people leaving the project.
- `BranchSetScoped` is a third case every branch-predicate site must handle. The set form is added to
  `RowScope` and `AbacConditions` rather than to each caller, so the sites compose it and do not re-derive it.
- Licence enforcement will make some currently-bookable practitioners unbookable the day it lands. That is
  the correct behaviour and it will look like a regression; the flagged-appointment worklist exists so the
  affected bookings are visible and actionable rather than merely broken.
- The `branch_manager` / `clinic_manager` phantom names are reconciled to one seeded spelling. Leaving two
  spellings of the same idea in the codebase is how the next reader concludes both exist.

## 4. Sponsor decisions D1–D5 — RATIFIED as recommended, 2026-08-01

Doc 42 §8 raises five questions for the sponsor. The recommended answers were implemented so the phase was
buildable and carried as **provisional** for one day short of a fortnight; **all five were ratified unchanged
on 2026-08-01**. The decision record is the table at the foot of
[`docs/decisions/phase-25-sponsor-pack.md`](../decisions/phase-25-sponsor-pack.md), which is the source of
truth — this section restates the outcome, it does not constitute it.

Because nothing was overturned, **no DPIA was triggered and neither the DPO nor the Medical Director was
required.** Both were only ever in scope for overturning D2 or D5, which move inventory across the PHI
boundary; confirming them keeps it where it was.

> **What the pack found that this ADR had missed.** Writing the decisions out for a non-engineer forced the
> question "what is each of these actually enforced BY?", and the answer for D5 was **nothing** — no reference
> to vaccines, injectables or any medicine identifier existed anywhere in inventory-service, so nothing
> stopped a vaccine being catalogued as clinic stock. Four decisions were enforced by a constraint, an index,
> a test suite and a role-reach mode; the fifth was enforced by memory, and this document had said so in the
> same confident tone as the other four. **See §4.1 for what closed it.** The general lesson is cheap and
> worth keeping: a decision table should record the MECHANISM beside the answer, because "we decided X" and
> "the platform does X" look identical in prose and are not the same claim.

| # | Question | Provisional answer | Why, and what changes if the sponsor decides otherwise |
|---|---|---|---|
| **D1** | Do clinics hold **controlled substances**? | **Excluded from v1**, enforced by `CHECK (is_controlled = false)` — not by convention | A controlled register needs dual signature, a running balance per ampoule and regulator-facing reporting: a module of its own, not a category flag. Making it a constraint means enabling it is a deliberate, reviewable migration rather than someone ticking a checkbox |
| **D2** | Should consumption link to an **encounter**? | **No.** Consumption is recorded by quantity per branch | A patient link makes inventory PHI and drags RLS, min-necessary, retention and the sensitivity gate into a storekeeping system. Revisit only if per-patient costing is genuinely required — and then as a designed change, because it moves inventory across the PHI boundary |
| **D3** | May a coordinator **create** a practitioner, or only assign existing ones? | **Create**, guarded by licence uniqueness | Central-only creation makes every new locum a ticket to head office. The unique `license_no` index is the defence against duplicate clinical identities; a duplicate returns 409 with the existing id so the UI offers "assign them to my clinic instead" |
| **D4** | Does the clinics manager get **write** everywhere, or read-everywhere/write-own? | **Write everywhere** | They supervise the network of clinics; a supervisor who must raise a request to fix a roster is not a supervisor. Audited, and the branch filter makes the target branch explicit on every write |
| **D5** | Are vaccines/injectables clinic stock or pharmacy stock? | **Pharmacy**, wherever a prescription or authorization applies | Clinic stock is consumables. Ambiguous items get classified once, centrally — classifying per branch is how the same vial ends up governed two different ways in two clinics |

Had D2 or D5 been decided the other way, invariants 8 and 9 (no patient dispensing, no PHI) would have been
the ones at stake, and the change a design decision with a DPIA rather than a schema tweak. Neither was.

---

## 4.1 Closing D5: the catalogue asks masterdata whether an item is a medicine

**Decision.** `POST /api/v1/inventory/items` calls `GET /api/v1/drugs/classify` on masterdata-service and
refuses the item when it matches the medicines master (422, `urn:hbmp:medicine-not-clinic-stock`, naming the
matched drug). Seam: `IMedicinesDirectory` in `services/inventory/Domain`; transport:
`HttpMedicinesDirectory` in `services/inventory/Api`, the same shape as `HttpBranchDirectory` beside it.

### What the gap actually risked, stated precisely

Worth being exact, because the honest version is narrower than "vaccines could leak" and more serious than it
first sounds. **The strict invariant held throughout**: inventory could not issue anything to a *named*
patient, because no patient identifier exists anywhere in it (D2, kept true by `NoPhiInInventoryTests`). What
was missing was the paperwork around *giving* it — no prescription, no eligibility check, no coverage limit,
no dispensing record. The vaccine gets given, and every control meant to surround giving it happens nowhere.
That is precisely the "second dispensing route" D2's rationale warns about, arriving through the catalogue
instead of through a patient column.

### Why masterdata answers, and not a list kept in inventory

"What counts as a medicine" is a clinical question and the medicines master is its home. A word list
maintained in a storekeeping service is a second answer to that question, and the two drift the first time a
drug is added to one and not the other. Cross-service and **by value** — inventory stores no reference to a
masterdata row; it asks a question and gets a verdict, which is the same posture as every other cross-service
read on this platform.

This is a **synchronous** call, and it passes the "caller cannot proceed without the result" test: the
endpoint's entire decision is whether to create the row. It is also a cold path — catalogue items are created
rarely, by administrators — so the round trip costs nothing that matters. Not cached, unlike
`HttpBranchDirectory`: a TTL here would mean a drug newly added to the master could still be admitted as
clinic stock for the length of it, and there is no traffic volume to justify buying that.

### Three choices inside it, each of which could reasonably have gone the other way

1. **Matching is bidirectional-containment, not equality.** The master's "Hepatitis B Vaccine" catches a typed
   "Hepatitis B Vaccine 20mcg/ml vial". Equality would have caught only the exactly-typed case, which is the
   one that never happens — protection in appearance only. Floored at six characters of drug name, because
   without a floor a master entry called "Water" refuses every consumable mentioning water, and a guard that
   fires on gauze gets switched off within a week.
2. **Unreachable ⇒ refuse (503), not admit.** The cheap implementation treats "could not ask" as "not a
   medicine", which leaves the gate open during exactly the window nobody is watching. Fail-closed matches
   `HttpBranchDirectory`'s posture in this same service, and the trade is easy here: a new gauze SKU waits a
   few minutes. `AN_UNREACHABLE_MEDICINES_MASTER_REFUSES_THE_ITEM_RATHER_THAN_ADMITTING_IT` is the test that
   holds it, and it is the most important one in the set.
3. **No override flag.** The pack says the classification is made once, centrally; an override is exactly how
   that becomes six clinics each ticking a box on a Tuesday — the same reasoning that made D1 a `CHECK`
   constraint rather than a setting. **The accepted cost:** a genuine consumable that happens to sit in the
   medicines master (saline, say) is refused, and the only remedy is to correct the master. That is a real
   friction and it is chosen deliberately, because the correction is then visible to the people accountable
   for the master rather than absorbed silently at one clinic.

### Also fixed, found on the way

`CreateItemRequest.Sku`, `NameEn`, `NameAr` and `UnitOfMeasure` are declared non-nullable, which stopped
nothing: a body with `"sku": null` deserialized to null and the subsequent `.Trim()` was an unhandled 500
waiting for the first client that sent one. Now a 400 naming the fields. Non-nullable reference types are a
compile-time convenience and never a validation of what arrived over the wire — worth remembering wherever
else a request record is trusted.

### Consequences

- **D5 is enforced by the platform**, and §4's table now describes five decisions with five mechanisms.
- **inventory-service gains a hard dependency on masterdata-service for catalogue writes.** Reads, movements,
  transfers and alerts are untouched — a masterdata outage cannot stop a clinic recording stock, only adding a
  new item type. That asymmetry is intended and is what makes fail-closed affordable here.
- **If clinic stock ever legitimately needs to include a medicine**, this is the constraint to revisit, and
  revisiting it means D5 itself — DPIA and Medical Director — not a flag on this check.

---

### Cross-references
Design: [42](../../HBMP-Design/42-branch-management.md) ·
Branch scope: [37](../../HBMP-Design/37-branch-scoping-and-clinical-sensitivity.md) ·
Authority vs reach: [40](../../HBMP-Design/40-user-access-model.md) ·
Roles: [10](../../HBMP-Design/10-role-matrix.md) · Min-necessary: [11](../../HBMP-Design/11-permission-matrix.md)

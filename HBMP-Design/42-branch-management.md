# 42 — Branch Management (coordinator & clinics manager, roster, licensing, clinic inventory)

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [37-branch-scoping-and-clinical-sensitivity.md](37-branch-scoping-and-clinical-sensitivity.md) · [40-user-access-model.md](40-user-access-model.md) · [10-role-matrix.md](10-role-matrix.md) · [11-permission-matrix.md](11-permission-matrix.md)
> Build prompt: [claude-code-prompts/phase-25-branch-management.md](claude-code-prompts/phase-25-branch-management.md)

**What this adds.** The people who *run* a Mersal clinic get a workspace: everything Reception can do, plus the practitioner roster, specialties and licences for their branch, the availability that feeds appointment slots, and the clinic's own stock — split into medical and non-medical. A **Branch Coordinator** does this for one clinic; a **Clinics Manager** does the same for all six.

---

## 0. What already exists (do not rebuild)

| Thing | Where | State |
|---|---|---|
| `provider.branch` + 6 seeded codes (ASW/ALX/OCT/MAA/DOK/NSR) | `provider/…/0005_branch.sql` | **Built.** No tenant/RLS by design — branches are shared, non-PHI |
| `provider.practitioner` incl. **`license_no` + `license_expiry`** | `…/0006_practitioner.sql:21-22` | **Built** — but see §3: nothing enforces expiry |
| `practitioner_specialty` (exactly-one-primary enforced) + 26 seeded specialties | `…/0006_practitioner.sql:32-40, 62-89` | **Built** |
| `practitioner_branch_assignment` (`valid_from/valid_to`, Active/Revoked) | `…/0006_practitioner.sql:42-49` | **Built** |
| `admin.user_branch_assignment` (Home/Additional) + `X-Active-Branch` resolution | `admin/…/0004`, `libs/auth/BranchContext.cs` | **Built** |
| `emr.provider_availability` (weekly rule) → `emr.appointment_slot` (materialized) | `emr/…/0002_appointments.sql` | **Built** — but no roster/leave concept, see §4 |
| `BranchScope` ABAC condition + `RowScope` branch sentinel | `libs/authz` | **Built** |
| `branch_manager` / `clinic_manager` named in `BranchScopedRoles` | `libs/authz/BranchScope.cs:24`, `apps/web/src/shell/useBranchContext.ts:11` | **Phantom** — referenced in code, never seeded as identity roles |
| Clinic inventory / stock / batch balances | — | **DOES NOT EXIST** anywhere. Pharmacy captures batch+expiry per dispense but derives no balance |

So this phase adds: two roles, a branch-scoped practitioner-admin capability, licence enforcement, a real roster, and inventory from scratch.

## 1. Two roles, one permission set — authority vs reach

**The decision that matters most.** Branch Coordinator and Clinics Manager must **share one permission set** and differ *only* in how many branches they reach. They are not two roles with two capability lists.

If they were, the lists would drift: someone adds "revoke specialty" to the coordinator, forgets the manager, and the manager who supervises six clinics can do less than the person in one of them. This is exactly the [doc 40](40-user-access-model.md) separation — *what may you do* (authority) is one question, *over which data* (reach) is another, and collapsing them is where these systems go wrong.

| | Branch Coordinator | Clinics Manager |
|---|---|---|
| Permission keys | identical | identical |
| Reach | one active branch (their assignment) | **all branches simultaneously** |
| Branch switcher | switches active branch | filters, does not restrict |

### The gap this exposes

Today `BranchScopeModes.ModeFor()` derives scope **from role names only**, and `RowScope.WithBranchScope` narrows a branch-scoped caller to **exactly one** active branch (`RowScope.cs:74-77`). There is no *multi-branch-simultaneous* mode. So "all six clinics at once" is currently expressible only by:

- (a) making the manager switch branches one at a time — wrong; they supervise across clinics, and
- (b) leaving the role out of `BranchScopedRoles` so it falls through to `MemberScoped` = unrestricted — wrong; that is an ungoverned "everything" with no grant behind it.

**Therefore:** add a third reach mode — `BranchSetScoped` — where the predicate is `branch_id ∈ PermittedBranchIds` rather than `= ActiveBranchId`. The permitted set still comes from real, auditable branch assignments; a clinics manager simply holds all six. Reach stays grant-derived, never role-derived. The `NoBranchSentinel` fail-closed behaviour is preserved: an unresolvable set matches nothing, never everything.

### Scope design (min-necessary)

Coordinator/manager inherit **reception's exact 12 scopes** — `reception:search`, `reception:read`, `eligibility:check`, `appointment:read/write`, `patient:read`, `practitioner:read`, `note:read`, `profile:read`, `callcentre:history:read`, `notification:read`, `claims:reimburse:submit` — and **no `emr:read`**. They run the clinic; they do not read clinical notes.

They must **not** be given `provider:write`. That scope is network-wide — it would let a clinic coordinator create branches and edit external labs and pharmacies, and it is also the scope that currently unmasks `license_no` (`Practitioners.cs:226`). Instead add branch-scoped scopes:

| New scope | Grants |
|---|---|
| `branch:practitioner:write` | assign/revoke practitioners **at branches in reach**, set specialties from the existing catalogue, maintain licence fields |
| `branch:roster:write` | roster, availability and exceptions at branches in reach |
| `branch:inventory:read` / `branch:inventory:write` | clinic stock at branches in reach |

**Specialties remain global master data.** A coordinator *assigns* specialties from the seeded 26; creating a new specialty stays with the network team. Same for branches themselves — a coordinator runs a clinic, they do not create one.

## 2. Practitioner administration, scoped to the clinic

The coordinator maintains, for their branch(es): which practitioners work here, from when to when, their specialties (with the existing exactly-one-primary rule), and their licence details.

**Practitioner identity is global; branch assignment is local.** A doctor working at Maadi and Dokki is *one* practitioner row with two assignments — not two records. Without that rule you get three "Dr Hala Fouad" rows and a roster that cannot be reasoned about.

To enforce it: add a **unique index on `license_no`** (where not deleted). A coordinator creating a practitioner whose licence already exists gets `409 practitioner-exists` with an offer to assign the existing one to their branch instead. This is the single cheapest defence against duplicate clinical identities.

## 3. Licence expiry is a safety gate, not a field

`license_no` and `license_expiry` already exist and **nothing reads them**. Today a doctor whose licence expired last year is still bookable: bookability checks practitioner status and branch assignment only (`Practitioners.cs:228,240-242`).

That is the most consequential finding in this design. Licensing becomes an enforced gate:

- **Slot generation and booking exclude a practitioner whose licence has expired *as at the slot date*.** Not as at today — booking three months out for a licence expiring next month must fail at generation, not surprise a patient on the day.
- **Existing future appointments are flagged, never silently cancelled.** Reuse `appointment.reassignment_needed_at` (already on the table, `0012`) and surface them in a coordinator worklist. A person decides who covers the clinic; the system does not cancel a refugee's appointment by itself.
- **Warnings at 90/60/30 days**, following the existing `ProviderCredentialExpiring` pattern, delivered to the coordinator of every branch the practitioner serves.
- **Never retroactive.** Past encounters stay valid; expiry affects future scheduling only.
- Licence numbers stay **field-masked** — visible to the licence-maintaining scopes, absent from the payload otherwise (the existing `canSeeLicense` projection, extended to the new scopes).

An expiry sweeper mirrors `ReportAccessExpirySweeper` (`orders/…/ReportAccessExpirySweeper.cs`) — the pattern is already in the codebase.

## 4. Roster → availability → slots: one source of truth

`emr.provider_availability` is a **weekly recurring rule** — day-of-week, start, end, slot minutes. There is no way to say "Dr Hala is on leave next Tuesday" or "the Aswan clinic closes for Eid". So today the only way to stop slots appearing is to delete the rule, which also erases the normal pattern.

The roster therefore adds an **exception layer** on top of the recurring rule:

- `roster_exception` — practitioner and/or branch, date range, kind (`Leave`, `PublicHoliday`, `ClinicClosed`, `AdHocClinic`), reason, author. Subtractive kinds remove availability; `AdHocClinic` adds it.
- Slot generation becomes: **recurring rule − exceptions ∩ active branch assignment ∩ valid licence ∩ practitioner Active**.

That intersection is the invariant. Availability must be computed in exactly one place, or the picker, the slot table and the booking validator will disagree — and the way that failure presents is a patient given an appointment with a doctor who is not there.

**Changing a roster affects appointments that already exist.** Cancelling a clinic day must produce an *impact preview* (how many booked appointments, which patients) before it applies, then flag them for reassignment — never bulk-cancel silently. Booked slots are held by a partial-unique index already; the roster must not be able to orphan them invisibly.

## 5. Clinic inventory — medical and non-medical

Greenfield. Two categories with genuinely different rules, one service.

| | **Medical** | **Non-medical** |
|---|---|---|
| Examples | syringes, gloves, sutures, dressings, IV sets, reagents | stationery, cleaning supplies, printer toner |
| Batch/lot | **required** | not tracked |
| Expiry | **required**; expired stock is blocked from issue | n/a |
| Storage condition | recorded (incl. cold-chain flag) | n/a |
| Write-off | requires reason + second approval | reason only |

### Stock is a ledger, not a number

`stock_movement` is **append-only**: receipt, issue, transfer, adjustment, write-off, return — each with quantity, reason, actor, timestamp, and batch where medical. **On-hand = sum of movements.** No mutable `quantity_on_hand` column that code can drift.

This is the same discipline as the audit chain and the outbox, for the same reason: a balance you can recompute is a balance you can reconcile, and a balance you cannot reconcile is a number people stop trusting. Physical stock-takes become a `Count` movement with a variance, not an overwrite.

### The boundary with pharmacy — non-negotiable

Clinic inventory covers **consumables used during care**. It is **not** a second dispensing path.

Anything that requires a prescription goes through `pharmacy-service`, against an `Rx`, with the authorization and benefit rules that entails. If clinic inventory could issue medication to a patient, it would be a route around eligibility, coverage limits, formulary and the dispense audit trail — every control the platform exists to enforce. So: **clinic inventory has no patient-dispensing endpoint at all.** Consumption is recorded by quantity against a branch (optionally an encounter, see §8), never as an issue *to a beneficiary* against a prescription.

Inventory is **not PHI** and should stay that way — it carries no beneficiary identifiers. Keeping it PHI-free is what lets a storekeeper use it without a clinical role.

### Operational essentials

Reorder level and lead time per item per branch, with a low-stock worklist; branch-to-branch transfer as two linked movements (out and in) so nothing is created or destroyed in transit; expiry alerts at 90/60/30 days; expired medical stock auto-quarantined — blocked from issue, requiring an explicit write-off.

## 6. Portal & screens

A **Branch Management** portal (base `branch`), used by both roles — the same screens, differing only in whether the branch control switches or filters.

Reception's sections verbatim (dashboard, eligibility check, appointments, book appointment, notifications) **plus**: Practitioners (roster of this clinic, assign/revoke, specialties, licence with expiry status), Roster & Availability (weekly pattern, exceptions calendar, impact preview), Licence Alerts (expiring/expired + affected appointments), Inventory (medical | non-medical tabs, movements ledger, low-stock, expiry), and for the clinics manager a **Branches overview** comparing the six.

Licence status uses the four-cue rule — Valid / Expiring / **Expired** must differ by hue *and* icon *and* shape *and* word. A grey chip that means "this doctor may not legally practise" is a design failure.

## 6b. What the 2026-08-11 audit of this portal found

Five findings, all fixed. Recorded here because each was a gap between what this document specifies and what
shipped, and the shape of the gap is the useful part.

| # | Finding | Why it survived |
|---|---|---|
| A1 | The branch **write** path (`ResolveBookingBranch`, `DenyIfOutsideBranchAsync` in emr) still asked `ActiveBranchId ==`. A set-scoped clinics manager has no active branch until they filter, so the guard fell through and accepted the branch id off the request body without checking `PermittedBranchIds` — breaking §7 rule 2 across eleven call sites. | `BranchQueryScope` fixed this for READS and documents the bug in its own header. Nobody asked whether the writes had been migrated too. The failure is invisible in the direction that hides it: the supervisor sees and does MORE, so no one reports it. |
| A2 | `provider_availability` had **no CRUD at all** — no GET, PUT or DELETE anywhere. Its only writer was `POST /appointment-slots`, which minted a fresh rule every call with no unique key, so rules accumulated. §4's "recurring rule" was unreadable and uneditable. | The existing idempotency test counts SLOTS, and slots have been deduplicated since 3.1. The rules behind them were not. Adding the unique index turned that test red immediately. |
| A3 | No capacity model existed. §4 never asked for one, and "how many patients will this clinician see in a day" turned out to be the question a clinic manager actually needed answered. | Not a regression — a gap in this document. Now §4b. |
| A4 | The only change history was the hash-chained audit store behind `audit:read`. The people who RUN a clinic could not ask who changed its roster. | §7 rule 10 says "every mutation is audited", and that was satisfied. It does not follow that anybody who needs the answer can read it. |
| A5 | The portal's five screens bypassed the SPA's API seam, so the whole portal errored in the demo bundle and could not be rendered in a test. | Its only test exercised a status chip in isolation, and the axe route sweep skipped these routes while reporting itself complete. |

Plus a client calling a route no service registers (`POST /roster-exceptions/{id}/withdraw`; emr maps
`DELETE`), which nothing called — so the broken client and the unreachable action hid each other.

**The pattern worth keeping.** Three of the five (A1, A2, A4) are cases where a control was built, documented
and correct, and then a second surface was added that did not inherit it. The question that found all three is
*"what enforces this, and does every path reach that enforcement?"* — the same question §8's mechanism column
exists to ask of the sponsor decisions.

## 4b. Capacity — maximum patients per practitioner per day

`slot_minutes × (end_time − start_time)` cannot express "Dr Hala takes twenty patients a day however long the
session runs". A six-hour day at fifteen minutes offers twenty-four appointments; a clinician who can safely
see twenty had no way to say so except by shortening their day, which also changes when they finish and tells
the desk something different from what was meant.

`provider_availability.max_per_day` is that number. **NULL means uncapped**, so every rule predating it keeps
its behaviour exactly.

It lives on the RULE, which makes it **per practitioner, per clinic, per weekday**. A doctor working Maadi
mornings and Dokki evenings holds two rules and two caps — deliberate, because the cap is administered by
whoever runs the clinic, and a coordinator who reaches one branch must be able to set the cap that applies
there without touching another clinic's. A network-wide cap across all clinics is a different control and is
not in scope.

**Enforced twice, and both are needed:**

- **Generation** — `SlotGeneration` emits at most `max_per_day` slots per date, across every window the day
  offers (the recurring one plus any `AdHocClinic`), applied *after* subtraction and filling from the
  earliest window. Inside the one function availability is computed, per §7 rule 5.
- **Booking** — the validator counts live appointments for (doctor, branch, Cairo date) under a
  per-(doctor, day) **advisory lock** and refuses `409 urn:hbmp:daily-capacity-reached`.

The lock is the load-bearing part. The existing `FOR UPDATE` slot lock serializes two people booking the
*same* slot and does nothing for two people booking *different* slots against the same doctor's last place:
both count nineteen, both see room, both commit. Counting inside a transaction is not counting under a lock.

## 7. Invariants

1. **Coordinator and manager share one permission set**; they differ only in reach. Any capability added to one is automatically held by the other.
2. **Reach is grant-derived, never role-derived.** `BranchSetScoped` resolves from real branch assignments; unresolvable reach matches **nothing** (sentinel), never everything.
3. **No `provider:write` for branch roles** — branch-scoped scopes only. A coordinator can never edit the external network or create a branch.
4. **One practitioner identity, many branch assignments**, enforced by a unique licence number.
5. **Availability is computed in exactly one place** — recurring rule − exceptions ∩ branch assignment ∩ valid licence ∩ Active status.
6. **An expired licence blocks future scheduling as at the slot date**, flags existing appointments for human reassignment, and never invalidates the past.
7. **Stock on-hand is derived from an append-only movement ledger.** No mutable quantity column.
8. **Clinic inventory never dispenses to a patient.** No endpoint accepts a beneficiary id for issue; prescribed items go through `pharmacy-service`.
9. Inventory carries **no PHI**.
10. Every practitioner, roster, licence and stock mutation is audited; no hard deletes.
11. **The branch WRITE path asks the same question as the branch READ path** — one implementation
    (`BranchWriteScope`), three modes, fail closed. A set-scoped caller who names no branch is refused, not
    defaulted: a write that could mean six clinics has to say which one.
12. **One availability rule per practitioner, per clinic, per weekday**, enforced by a partial unique index.
    Slot materialization READS a rule; it never creates one.
13. **A daily cap is enforced at generation and again at booking**, the second under a per-(doctor, day)
    advisory lock. Generation keeps the calendar honest; the booking check is what holds for a walk-in, an
    ad-hoc clinic, or any path that books without consuming a slot.
14. **Every administered change writes a domain history row in the same transaction as the change**, and the
    audit event as well. Neither substitutes for the other.
15. **Operational history is not the audit trail.** `audit:read` is never granted to a branch role — the
    clinic's own record answers "who changed this", and the hash-chained store stays with Security,
    Compliance and the DPO.

## 8. Decisions needed from the sponsor — **all five ratified as recommended, 2026-08-01**

Decision record: [`docs/decisions/phase-25-sponsor-pack.md`](../docs/decisions/phase-25-sponsor-pack.md)
(source of truth) · Implementation: [`docs/adr/0029-branch-management.md`](../docs/adr/0029-branch-management.md) §4.

No answer was changed, so no DPIA was triggered and neither the DPO nor the Medical Director was required.

| # | Question | Recommendation | Outcome | Enforced by |
|---|---|---|---|---|
| D1 | Do clinics hold **controlled substances**? | **Exclude from v1.** A controlled register needs dual-signature, a running balance per ampoule and regulator-facing reporting — a module of its own, not a category flag. Blocked by a CHECK constraint until designed | ✅ Ratified | `CHECK (is_controlled = false)` on `inventory.item` |
| D2 | Should consumption link to an **encounter**? | **No by default.** A patient link makes inventory PHI and drags RLS, min-necessary and retention into a storekeeping system. Record consumption by quantity per branch per day. Revisit only if per-patient costing is required | ✅ Ratified | Absence, held by `NoPhiInInventoryTests` (5 facts over schema, routes, model, runtime) |
| D3 | May a coordinator **create** a practitioner, or only assign existing ones? | **Create, with the licence-uniqueness guard** (§2). Central-only creation makes every new locum a ticket to head office | ✅ Ratified | Partial UNIQUE `ux_practitioner_license_no` |
| D4 | Does the clinics manager get **write** everywhere, or read-everywhere/write-own? | **Write everywhere.** They supervise the network of clinics; a supervisor who must raise a request to fix a roster is not a supervisor. Audited, and the branch filter makes the target explicit | ✅ Ratified | `BranchSetScoped` reach + a test asserting the two roles' scope sets are identical |
| D5 | Are vaccines/injectables clinic stock or pharmacy stock? | **Pharmacy**, wherever a prescription or authorization applies. Clinic stock is consumables. Ambiguous items get classified once, centrally, not per branch | ✅ Ratified | `GET /api/v1/drugs/classify` on masterdata, consulted by item creation; fail-closed (ADR-0029 §4.1) |

> **The column that matters is the last one, and it did not exist until sign-off.** Asking "what is each
> decision enforced BY?" — rather than "what did we decide?" — is what exposed that **D5 was enforced by
> nothing** for the whole of phase 25: no reference to vaccines, injectables or any medicine identifier
> existed anywhere in inventory-service, so "vaccines are pharmacy stock" was a rule people had to remember.
> A decision table without a mechanism column reads as though every row is equally real. They rarely are.

## 9. Acceptance criteria

- [ ] `branch_coordinator` and `clinics_manager` seeded with an **identical** scope set; a test asserts the two sets are equal and fails if they diverge.
- [ ] `BranchSetScoped` reach mode implemented; manager sees all six branches simultaneously; coordinator sees one; unresolvable reach returns **zero** rows (proved with the negation assertion).
- [ ] Neither role holds `provider:write`; a test proves a coordinator cannot create a branch or edit an external provider.
- [ ] Unique licence number enforced; duplicate create returns 409 with an assign-existing path.
- [ ] Licence expiry blocks slot generation and booking **as at the slot date**; existing future appointments are flagged not cancelled; 90/60/30-day warnings fire; past encounters unaffected.
- [ ] Roster exceptions (leave, holiday, closure, ad-hoc) subtract from or add to availability; slot generation uses the single intersection; a roster change shows an impact preview before applying.
- [ ] Inventory split medical/non-medical with batch+expiry mandatory on medical; on-hand derived from an append-only ledger; transfers are paired movements; expired medical stock is quarantined and cannot be issued.
- [ ] **No inventory endpoint accepts a beneficiary identifier** (asserted by a test over the route table).
- [ ] Branch Management portal carries reception's sections plus the five new ones; licence status uses four cues; bilingual AR/EN, WCAG 2.2 AA, axe clean.
- [ ] Every mutation audited; all pre-existing min-necessary, RLS, branch-scope and booking suites still green.

---

### Cross-references
Branch scope & sensitivity: [37](37-branch-scoping-and-clinical-sensitivity.md) · Authority vs reach: [40](40-user-access-model.md) · Roles: [10](10-role-matrix.md) · Min-necessary: [11](11-permission-matrix.md) · Appointments: [23-state-machines.md](23-state-machines.md) · Build: [claude-code-prompts/phase-25-branch-management.md](claude-code-prompts/phase-25-branch-management.md)

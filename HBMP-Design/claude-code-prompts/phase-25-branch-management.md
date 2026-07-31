# Phase 25 — Branch Management (coordinator & clinics manager, roster, licensing, clinic inventory)

**Goal:** Give the people who run a Mersal clinic a workspace — everything Reception can do, plus practitioner roster, specialties and **enforced licensing** for their branch, the availability that feeds appointment slots, and clinic stock split into **medical / non-medical**. A **Branch Coordinator** covers one clinic; a **Clinics Manager** covers all six with the *same* permissions.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> **Read [`../42-branch-management.md`](../42-branch-management.md) §0 first.** Most of the schema already exists — `provider.branch` with the six codes, `provider.practitioner` **including `license_no` and `license_expiry`**, `practitioner_specialty`, `practitioner_branch_assignment`, `admin.user_branch_assignment`, `emr.provider_availability` → `emr.appointment_slot`, `BranchScope` ABAC, and the `RowScope` branch sentinel. **Do not rebuild any of it.**
>
> **Three things are genuinely new:** a multi-branch reach mode, licence *enforcement* (the fields exist and nothing reads them), and inventory (nothing exists at all).

## Skills to activate
> `mersal-platform-architect`, `refugee-healthcare-management` (always-on), plus `provider-network-management`, `appointment-queue-management`, `healthcare-database-architect`, `healthcare-uiux-designer`.

## Context — read first
- [`../42-branch-management.md`](../42-branch-management.md) — **AUTHORITATIVE**, especially §1 (authority vs reach), §3 (licence gate), §7 (invariants), §8 (open decisions D1–D5).
- [`../37-branch-scoping-and-clinical-sensitivity.md`](../37-branch-scoping-and-clinical-sensitivity.md) §3 · [`../40-user-access-model.md`](../40-user-access-model.md) §2–§3 · [`../11-permission-matrix.md`](../11-permission-matrix.md) · [`../10-role-matrix.md`](../10-role-matrix.md).
- **Existing code:** `services/provider/{Api/Branches.cs,Api/Practitioners.cs,Infrastructure/Migrations/0005,0006,0007}`, `services/admin/Api/BranchAssignmentEndpoints.cs`, `services/emr/{Api/Appointments.cs,Domain/SlotGeneration.cs,Infrastructure/Migrations/0002,0006}`, `libs/authz/{BranchScope.cs,RowScope.cs,AbacConditions.cs,BranchScopeResolver.cs}`, `libs/auth/BranchContext.cs`, `services/identity/Domain/IdentityContract.cs`, `apps/web/src/{portals/catalog.ts,authz/permissions.ts,screens/PractitionerAdmin.tsx}`.
- `docs/HANDOFF.md` gotchas (`./dotnet.sh`, PG :55432, .NET 8 only, analyzers-as-errors, CPM, pnpm). **Run DB-gated tests with `./dotnet.sh test --with-db`** — a plain `dotnet test` skips ~100 integration/concurrency/RLS tests and still reports green.

## THE INVARIANTS (from ../42 §7)
1. Coordinator and manager **share one permission set**; they differ only in reach.
2. Reach is **grant-derived, never role-derived**; unresolvable reach matches **nothing**.
3. **No `provider:write`** for branch roles.
4. **One practitioner identity, many branch assignments** — enforced by unique licence number.
5. Availability computed in **exactly one place**.
6. Expired licence blocks **future** scheduling as at the slot date; flags existing appointments; never retroactive.
7. Stock on-hand is derived from an **append-only ledger**.
8. **Clinic inventory never dispenses to a patient.**
9. Inventory carries **no PHI**.

---

## Prompts

### 25.0 — ADR + the sponsor decisions (small commit, first)
```text
Write docs/adr/0025-branch-management.md recording:
- The authority-vs-reach decision (../42 §1): ONE permission set, two reach modes. Record the rejected
  alternative (two roles with two capability lists) and WHY — drift between the two lists means the
  supervisor of six clinics ends up able to do less than the coordinator of one.
- The new BranchSetScoped reach mode and why MemberScoped (unrestricted) was rejected for the manager:
  MemberScoped is an ungoverned "everything" with no grant behind it; reach must stay auditable.
- ../42 §8 decisions D1-D5 with the recommended answers as PROVISIONAL until sponsor sign-off:
  D1 controlled substances EXCLUDED from v1 (enforced by CHECK constraint, not by convention);
  D2 consumption does NOT link to an encounter (keeps inventory PHI-free);
  D3 coordinator MAY create a practitioner, guarded by licence uniqueness;
  D4 clinics manager has WRITE everywhere;
  D5 vaccines/injectables are PHARMACY stock, not clinic stock.
  Mark each "provisional — sponsor sign-off outstanding" in BUILD-STATUS, as done for ADR-0019/0020.
Acceptance: ADR merged; BUILD-STATUS lists the five provisional decisions.
```

### 25.1 — Roles & scopes: one set, two reaches
```text
Read ../42 §1. This sub-prompt is where the invariant is created; everything after it depends on it.

IDENTITY (services/identity)
- Add roles `branch_coordinator` and `clinics_manager` to IdentityContract.Roles and the seed migration.
  NOTE: `branch_manager` and `clinic_manager` are already hard-coded in libs/authz/BranchScope.cs:24 and
  apps/web/src/shell/useBranchContext.ts:11 as PHANTOM names that were never seeded. Decide ONE naming
  and fix all sites — do not leave two spellings of the same idea in the codebase.
- Add scopes: `branch:practitioner:write`, `branch:roster:write`, `branch:inventory:read`,
  `branch:inventory:write`.
- Grant BOTH new roles: reception's exact 12 scopes (reception:search, reception:read,
  eligibility:check, appointment:read, appointment:write, patient:read, practitioner:read, note:read,
  profile:read, callcentre:history:read, notification:read, claims:reimburse:submit) PLUS the four new
  branch scopes. NOT emr:read. NOT provider:write.
- THE TEST THAT PINS THE INVARIANT: assert the scope set of branch_coordinator EQUALS the scope set of
  clinics_manager, exactly, in both directions. It must fail loudly if anyone grants one a scope the
  other lacks. Register it in the phase-24 invariant registry.
- A second test asserts NEITHER role holds provider:write, and that a coordinator principal is refused
  by POST /branches and by the external-provider write endpoints.
- Update docs/security/token-contract.md ONLY if claim shapes change (they should not — new scopes are
  data, not new claims). Keep the byte-compat contract test green.

AUTHZ (libs/authz) — the new reach mode
- Add ScopeMode.BranchSetScoped alongside BranchScoped / MemberScoped / ProviderScoped.
- RowScope.WithBranchScope currently narrows to EXACTLY ONE active branch (RowScope.cs:74-77). Add the
  set form: predicate is `branch_id IN PermittedBranchIds`. Preserve the NoBranchSentinel behaviour —
  an unresolvable set injects the sentinel and matches ZERO rows, never all rows.
- AbacConditions.BranchScope gains the set case: resource.BranchId ∈ PermittedBranchIds, with
  ActiveBranchId acting as an optional FILTER (if set, narrow) rather than a restriction.
- BranchScopeModes.ModeFor currently derives mode from ROLE NAMES ONLY (BranchScope.cs:33-39). Keep that
  seam but make clinics_manager map to BranchSetScoped, and leave a documented note that phase 21
  moves reach to grants. Do NOT let clinics_manager fall through to MemberScoped — that is unrestricted
  and ungoverned.
- TESTS: coordinator sees exactly one branch's rows; manager sees all six; manager with a filter set
  sees one; a caller whose reach cannot be resolved gets ZERO rows AND the negation assertion (prove
  an unscoped query WOULD have returned N>0). Extend the existing RLS/branch-scope suites, do not fork.
ACCEPTANCE: the equality test passes; manager reads across six branches in one request; sentinel proven.
```

### 25.2 — Practitioner administration at branch level
```text
Read ../42 §2. Reuse services/provider/Api/Practitioners.cs — extend authorization, do not fork endpoints.

- Widen the write group from `provider:write` to AnyScope("provider:write","branch:practitioner:write"),
  then add a BRANCH-REACH CHECK: a caller holding only the branch scope may act ONLY on branches in
  their reach. A coordinator at Maadi assigning a practitioner to Dokki is 403 + audit.
- Extend the licence field-mask (`canSeeLicense`, Practitioners.cs:226) to include the new scope, so
  coordinators can maintain licences without holding network-wide provider:write.
- Specialties: coordinators ASSIGN from the seeded 26 and set primary (the existing exactly-one-primary
  partial index stays authoritative). They may NOT create/rename a specialty — that stays provider:write.
- MIGRATION: add UNIQUE index on provider.practitioner(license_no) WHERE is_deleted = false AND
  license_no IS NOT NULL. Backfill check first: if duplicates exist today, report them and STOP —
  merging clinical identities is a data decision, not a migration side-effect.
- POST /practitioners returns 409 `urn:hbmp:practitioner-exists` when the licence already exists, with
  the existing practitionerId in the problem detail so the UI can offer "assign to my branch instead".
- Branch assignment/revoke already emits PractitionerBranchRevoked to provider.events AND
  emr.practitioner-branch-revoked (Practitioners.cs:176,195) — keep that wiring.
ACCEPTANCE: coordinator can assign/revoke/specialty/licence at their branch and is 403 elsewhere;
duplicate licence is 409 with an assign path; specialty creation still requires provider:write.
TESTS: cross-branch denial, licence-mask per scope, duplicate-licence 409, specialty-create denial.
```

### 25.3 — Licence expiry as an enforced gate (the safety fix)
```text
Read ../42 §3. license_no/license_expiry EXIST and NOTHING reads them: bookability today checks
practitioner status + branch assignment only (Practitioners.cs:228,240-242). A doctor whose licence
expired last year is still bookable. Close that.

- Add IsLicenceValidAt(practitioner, date) to provider Domain. Extend GET /practitioners and
  GET /practitioners/{id}/serves-branch (the probe emr already calls) with an `asOf` date parameter,
  returning licence validity AS AT THAT DATE.
- SLOT GENERATION + BOOKING exclude a practitioner whose licence has expired as at the SLOT DATE — not
  as at today. Booking three months ahead for a licence expiring next month must fail at generation.
  Wire into emr: POST /appointment-slots (Appointments.cs:45) and POST /appointments (:260) gain a
  licence check beside the existing serves-branch gate (:61-65, :296-302), returning
  422 `urn:hbmp:practitioner-licence-expired` with the expiry date in the detail.
- EXISTING FUTURE APPOINTMENTS ARE FLAGGED, NEVER AUTO-CANCELLED. Reuse appointment.reassignment_needed_at
  (already on the table, migration 0012) and surface a coordinator worklist. A person decides cover;
  the system does not cancel a refugee's appointment by itself.
- EXPIRY SWEEPER: mirror services/orders/…/ReportAccessExpirySweeper.cs (the pattern exists). Emits
  PractitionerLicenceExpiring at 90/60/30 days and PractitionerLicenceExpired on the day, to the
  coordinators of every branch the practitioner serves. Follow the ProviderCredentialExpiring precedent.
- NEVER RETROACTIVE: past encounters, past appointments and historical records are untouched. Add the
  test that proves a completed encounter from before expiry is still readable and unflagged.
ACCEPTANCE (Given/When/Then)
- Given a licence expiring 30 Sep, When slots are generated for October, Then that practitioner has none.
- Given a booking attempt for a slot after expiry, Then 422 with the expiry date.
- Given a licence lapses with 12 future appointments, Then all 12 are flagged for reassignment, none
  cancelled, and the coordinator worklist lists them with patient contact.
- Given a past encounter, Then expiry changes nothing about it.
TESTS: boundary at the expiry date itself (inclusive/exclusive — decide and document), generation
exclusion, booking rejection, flag-not-cancel, sweeper thresholds, retroactivity test.
```

### 25.4 — Roster: exceptions, and ONE availability computation
```text
Read ../42 §4. emr.provider_availability is a WEEKLY RECURRING RULE with no way to express leave,
holidays or closures — so today the only way to stop slots is to delete the rule, which also erases
the normal pattern.

- MIGRATION (emr): roster_exception — exception_id, tenant_id, branch_id NULL, practitioner_id NULL,
  (at least one of the two NOT NULL), date_from, date_to, kind CHECK IN
  ('Leave','PublicHoliday','ClinicClosed','AdHocClinic'), start_time/end_time NULL (whole-day when
  null), reason varchar(300) NOT NULL, + audit columns, soft-delete, *_history twin, tenant RLS.
  Subtractive kinds remove availability; AdHocClinic ADDS it.
- SINGLE SOURCE OF TRUTH: extend services/emr/Domain/SlotGeneration.cs so availability =
  recurring rule − exceptions ∩ active branch assignment ∩ VALID LICENCE (25.3) ∩ practitioner Active.
  Every consumer — the doctor picker, GET /booking/doctor-availability, GET /appointment-days, slot
  materialization and the booking validator — calls THIS function. If you find a second place deciding
  availability, that is the bug. A patient given an appointment with a doctor who is on leave is how
  this failure presents.
- IMPACT PREVIEW BEFORE APPLY: POST /roster-exceptions?dryRun=true returns the affected booked
  appointments (count + list). The real POST requires acknowledging the count. Affected appointments are
  FLAGGED (reassignment_needed_at) — never bulk-cancelled.
- Regenerating slots must not orphan or double-book: booked slots are held by the existing partial-unique
  index; generation is idempotent (Appointments.cs:82-89 already skips existing starts) — keep it so.
ACCEPTANCE
- Given leave next Tuesday, Then no slots exist for that practitioner that day and the weekly pattern is
  intact the following Tuesday.
- Given ClinicClosed for a branch, Then no practitioner at that branch has slots that day.
- Given AdHocClinic on a Friday, Then slots appear for that date only.
- Given a closure over 8 booked appointments, Then dryRun reports 8 and the apply flags 8, cancels 0.
TESTS: each kind, whole-day vs part-day, overlapping exceptions, the intersection with licence and
branch assignment, dry-run parity with apply, idempotent regeneration.
```

### 25.5 — Clinic inventory: schema + ledger
```text
Read ../42 §5. NOTHING exists today — pharmacy captures batch/expiry per dispense but derives no balance.
New service `services/inventory` (schema `inventory`), house style: uuid v7, snake_case, audit columns,
soft-delete, *_history twins, tenant RLS, durable outbox + RELAY (do not repeat the document-service
omission where AddHbmpDurableOutbox shipped without AddHbmpOutboxRelay).

TABLES
- item: item_id, tenant_id, sku, name_en, name_ar, category CHECK IN ('Medical','NonMedical'),
  unit_of_measure, is_batch_tracked bool, requires_expiry bool, is_controlled bool DEFAULT false
  CHECK (is_controlled = false)  ← D1: controlled substances excluded from v1 BY CONSTRAINT, so enabling
  them is a deliberate migration, not a checkbox,
  storage_condition, cold_chain bool, status.
  RULE: category='Medical' ⇒ is_batch_tracked AND requires_expiry (CHECK).
- branch_item: (branch_id, item_id) reorder_level, lead_time_days, is_stocked.
- stock_batch: batch_id, item_id, batch_no, expiry_date, NULL only for non-medical.
- stock_movement (APPEND-ONLY, the heart of it): movement_id, tenant_id, branch_id, item_id,
  batch_id NULL, kind CHECK IN ('Receipt','Issue','TransferOut','TransferIn','Adjustment','WriteOff',
  'Return','Count'), quantity numeric (SIGNED by kind), reason varchar(300) NOT NULL for
  Adjustment/WriteOff/Count, transfer_ref uuid NULL (pairs Out/In), counterparty_branch_id NULL,
  actor, occurred_at, + audit. REVOKE UPDATE, DELETE — append-only enforced at the DB, like
  approvals' decision ledger (approvals/…/0001_approvals.sql:63-80).
- NO quantity_on_hand COLUMN ANYWHERE. On-hand = SUM(quantity) over movements, exposed by a view or a
  computed query. A balance you can recompute is a balance you can reconcile.
- **NO beneficiary_id / patient_id COLUMN IN ANY TABLE.** Add a test over the route table AND the schema
  asserting no inventory endpoint accepts, and no inventory column stores, a beneficiary identifier
  (../42 invariant 8/9). Inventory must never become a route around prescription, eligibility, coverage
  limits, formulary and the dispense audit trail.

RULES
- Expired medical stock is QUARANTINED: Issue against an expired batch is rejected 422
  `urn:hbmp:batch-expired`; clearing it requires an explicit WriteOff with reason.
- Transfers are TWO PAIRED MOVEMENTS sharing transfer_ref (TransferOut at source, TransferIn at
  destination) so nothing is created or destroyed in transit; a test asserts the pair sums to zero.
- Stock-take is a Count movement recording the variance — never an overwrite of history.
- Negative on-hand is impossible: Issue/TransferOut validated against current computed balance inside
  the transaction (SELECT ... FOR UPDATE on the batch/item rows), with a concurrency test proving two
  parallel issues of the last unit produce exactly one success. Reuse the consume/dispense harness.
ACCEPTANCE: on-hand always equals the ledger sum; expired batch cannot be issued; transfer pairs
balance; parallel issue of the last unit yields one success; no PHI column or parameter exists.
```

### 25.6 — Inventory API + worklists
```text
- Endpoints (scopes branch:inventory:read / branch:inventory:write, ALL branch-reach checked):
  GET/POST /api/v1/inventory/items (catalogue; create requires the write scope)
  GET /api/v1/inventory/stock?branchId&category&lowStock&expiringWithinDays  (computed on-hand)
  POST /api/v1/inventory/movements  (Idempotency-Key REQUIRED — a double-posted receipt is a phantom
    stock level; follow the consume/dispense idempotency pattern, stable key per intent, never per attempt)
  POST /api/v1/inventory/transfers  (creates the paired movements atomically)
  GET /api/v1/inventory/movements?…  (the ledger, paginated, filterable)
  GET /api/v1/inventory/alerts       (low stock + expiring 90/60/30 + expired-quarantined)
- A coordinator sees only their branch(es); a clinics manager sees all six in ONE response
  (BranchSetScoped from 25.1). Same endpoints, no separate "manager" routes.
- Expiry/low-stock alerts via the notification service using the existing routing; make sure the event
  names you publish are CONSUMED (phase 24 Gate 3.1 adds the symmetry gate — do not add new orphans).
- Kong routes + scope grants; route-coverage guard green.
ACCEPTANCE: manager gets six branches in one call, coordinator one; replayed movement applies once;
transfer is atomic; alerts fire at the thresholds.
```

### 25.7 — Branch Management portal
```text
Read ../42 §6, ../0B (+ §10b, and §10c for paired actions / reference modals), ../21, and the EXISTING
apps/web/src/{portals/catalog.ts, authz/permissions.ts, screens/PractitionerAdmin.tsx}.

- New portal base `branch`, used by BOTH roles — same screens; the branch control SWITCHES for a
  coordinator and FILTERS for a manager. Do not build two portals.
- Sections: reception's five verbatim (dashboard, eligibility, appointments, book, notifications) PLUS
  Practitioners · Roster & Availability · Licence Alerts · Inventory · (manager only) Branches overview.
- Practitioners: reuse PractitionerAdmin.tsx patterns — do not fork the screen; extract the shared
  parts. Add licence display with expiry status and the duplicate-licence 409 flow ("this licence
  belongs to Dr X — assign them to your clinic instead?").
- LICENCE STATUS USES FOUR CUES — Valid / Expiring / Expired differ by hue AND icon AND shape AND word.
  A grey chip meaning "may not legally practise" is a design failure.
- Roster & Availability: weekly pattern editor + exceptions calendar; creating an exception shows the
  IMPACT PREVIEW (n booked appointments, listed) and requires acknowledgement before applying.
- Licence Alerts: expiring/expired practitioners and the appointments flagged for reassignment, with a
  reassign action per appointment.
- Inventory: Medical | Non-medical tabs; on-hand table; movement ledger; receive/issue/transfer/adjust/
  write-off dialogs with reason required where the schema requires it; low-stock and expiry worklists.
  Batch + expiry fields are mandatory and visible for medical items, absent for non-medical.
- Branches overview (manager): the six clinics compared — today's appointments, no-shows, licence alerts,
  low-stock count. Charts (if any) keep the data table always in the DOM (../12 §7 rule).
- Bilingual AR/EN with full RTL; ≥44px targets; keyboard reachable; aria-live on async outcomes;
  axe clean in BOTH locales against a POPULATED state — add DevApiClient fixtures from the start so the
  a11y sweep is not vacuous (the mistake phase 24 Gate 4.1 is fixing for the policy screens).
ACCEPTANCE: one portal serves both roles; manager's branch control filters and coordinator's switches;
impact preview blocks a blind closure; licence chips carry four cues; axe green EN+AR on populated data.
```

### 25.8 — Docs, seeds, routes, status
```text
- ../10 gains branch_coordinator + clinics_manager with the shared-scope note; ../11 gains the new
  scopes and the branch-reach rule; ../14 gains the Branch Management portal + its sections;
  ../16 gains inventory-service; ../22 gains the new tables; ../23 gains roster_exception and the
  licence gate on the appointment state machine.
- 00-README-INDEX + HBMP-Design/README gain doc 42 (count -> 42); BUILD-STATUS gains 25.0-25.8 and the
  five provisional decisions from 25.0.
- Seed data for local dev: a practitioner with a licence expiring in 20 days (so the alert path is
  visible), one on leave next week, and a small item catalogue across both categories including one
  batch expiring inside 30 days.
- Register the new invariants in docs/quality/invariant-registry.yaml (phase 24 Gate 2): the
  scope-set-equality test, the no-PHI-in-inventory test, the licence-blocks-future-booking test, the
  ledger-sum test, and the zero-rows-on-unresolvable-reach test.
ACCEPTANCE: docs true; seeds make every alert path demonstrable; registry entries have named tests.
```

---

## Guardrails
- **One permission set, two reaches.** The equality test between `branch_coordinator` and `clinics_manager` is permanent — if a future phase grants one a scope, it grants both.
- **No `provider:write` for branch roles**, ever. A clinic coordinator must never be able to edit the external lab/pharmacy network or create a branch.
- **Availability is computed in exactly one function.** A second implementation is the bug, not an optimisation.
- **Licence expiry flags, never cancels.** No automated process cancels a beneficiary's appointment.
- **Inventory has no beneficiary identifier** — not in a column, not in a parameter, not "temporarily".
- **No `quantity_on_hand` column.** On-hand is derived, always.
- Movements carry a stable per-intent `Idempotency-Key`; a double-posted receipt is a phantom stock level.
- Every mutation audited; no hard deletes; `*_history` twins throughout.
- Full suite green after each sub-prompt (`./dotnet.sh test HbmpPlatform.sln -c Release --with-db` + `pnpm -r test`), **including the untouched min-necessary, RLS, branch-scope and booking suites**.

## Done when
- [ ] `branch_coordinator` and `clinics_manager` seeded with **identical** scope sets, pinned by an equality test; neither holds `provider:write`.
- [ ] `BranchSetScoped` implemented; manager reads six branches in one request, coordinator one; unresolvable reach returns **zero** rows with the negation assertion.
- [ ] Coordinators administer practitioners, specialties and licences **only at branches in reach**; duplicate licence returns 409 with an assign-existing path; specialty creation still needs `provider:write`.
- [ ] Licence expiry blocks slot generation and booking **as at the slot date**, flags existing appointments without cancelling, warns at 90/60/30, and changes nothing retroactively.
- [ ] `roster_exception` supports leave / holiday / closure / ad-hoc; availability is one function; roster changes show an impact preview before applying.
- [ ] Inventory split medical/non-medical with batch+expiry mandatory on medical; controlled substances blocked by constraint; on-hand derived from an append-only ledger; transfers paired; expired stock quarantined; parallel issue of the last unit yields exactly one success.
- [ ] **No inventory route or column carries a beneficiary identifier**, proven by test.
- [ ] One Branch Management portal serves both roles; licence status uses four cues; impact preview enforced; axe clean EN+AR against populated fixtures.
- [ ] Docs 10/11/14/16/22/23 updated, doc 42 indexed, BUILD-STATUS carries 25.x and the five provisional decisions, invariant registry updated.

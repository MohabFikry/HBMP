# Phase 14 — Branch Scoping, Practitioner Specialty & Clinical Sensitivity

**Goal:** Retrofit the running platform with (a) **multi-branch awareness** — six Mersal branches, staff assigned to a home branch and optionally others, an active-branch switcher, and server-side branch scoping for operational roles while approvals/managers/providers stay member-scoped; (b) **practitioner records with structured specialty** and one-or-many branch assignments; and (c) **examination types with sensitivity classification**, where sensitive results (mental health first) are content-restricted and only released through a **justified request** decided by the authoring doctor **or** a Medical Director, under a time-boxed, fully audited grant.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> ⚠️ **This is a cross-cutting RETROFIT of already-built services** (phases 0–6 are complete, 375 tests green). It touches `libs/authz`, identity, provider-service, emr-service, and orders-service. **Run it before Phase 7 (approvals)** — approvals must be built already knowing it is member-scoped and that sensitive results are restricted — and before Phase 9 (frontend), which needs the branch switcher.
>
> **Golden rule:** these services exist and are tested. Extend them; do not rewrite them. Every migration is additive/backward-compatible (expand/contract). If reality diverges from a design doc, flag it in the commit — don't silently deviate.

---

## Skills to activate
> Activate `appointment-queue-management`, `clinical-workflow-designer`, `healthcare-database-architect`, `ngo-healthcare-operations` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management` (the sensitivity work is squarely special-category refugee data). Add `healthcare-uiux-designer` for 14.7. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [`../37-branch-scoping-and-clinical-sensitivity.md`](../37-branch-scoping-and-clinical-sensitivity.md) — **AUTHORITATIVE** for this phase: branch model, scope modes, specialty, sensitivity levels, release-request workflow, acceptance criteria.
- [`../22-data-dictionary.md`](../22-data-dictionary.md) — schema conventions + the new branch/practitioner/examination/sensitivity tables and enums.
- [`../23-state-machines.md`](../23-state-machines.md) — the report-access-request lifecycle; existing appointment/order lifecycles you must not break.
- [`../10-role-matrix.md`](../10-role-matrix.md) — which roles are BranchScoped vs MemberScoped vs ProviderScoped.
- [`../11-permission-matrix.md`](../11-permission-matrix.md) — min-necessary field rules (unchanged) + the new sensitive-content rules.
- [`../18-security-model.md`](../18-security-model.md) §11 break-glass · [`../19-audit-strategy.md`](../19-audit-strategy.md) — every branch switch, denial, request, decision, and read-under-grant is audited.
- [`../20-compliance-checklist.md`](../20-compliance-checklist.md) — special-category data, PDPL, data-subject rights.
- [`../14-navigation-structure.md`](../14-navigation-structure.md) + [`../0B-DESIGN-SYSTEM-UI.md`](../0B-DESIGN-SYSTEM-UI.md) — branch switcher + restricted-result UI.

**Existing code you are extending** (read before editing):
- `libs/authz/` — `AbacConditions.cs`, `RowScope.cs`, `IAuthorizationEngine.cs`, `PolicyBundle.cs`, `BreakGlass.cs`, and the per-domain bundles (`EmrPolicies`, `OrdersPolicies`, `ProviderPolicies`, `ApprovalsPolicies`, `PharmacyPolicies`, `FinancePolicies`, `CasePolicies`, `AdminPolicies`, `ReportingPolicies`).
- `services/provider/` — `Domain/Entities.cs` (`Provider`, `ProviderLocation`), `Infrastructure/ProviderDbContext.cs`, RLS migrations `0003`/`0004` (**note the `NOBYPASSRLS` app-role finding in `docs/HANDOFF.md`**).
- `services/emr/` — `Domain/Appointment.cs` (`Appointment`, `ProviderAvailability`, `AppointmentSlot`, `WaitlistEntry` — all already carry `LocationId`), `Api/Appointments.cs`, `Api/Queue.cs`, `Domain/AppointmentQueue.cs`.
- `services/orders/` — `Domain/`, `Api/Consume.cs`, result upload/read endpoints from phase 5.3.
- `services/identity/` + `libs/auth` — token claims, user model.
- `docs/HANDOFF.md` + `docs/BUILD-STATUS.md` — current state, gotchas (`./dotnet.sh`, PG on **:55432**, .NET 8 not 9, analyzers-as-errors, central package management).

---

## THE INVARIANTS (read before writing any code)

1. **Branch scoping narrows; it never replaces.** A doctor still needs `TreatingRelationship`; reception still can't see EMR. Branch is an *additional* filter for BranchScoped roles — enforced **server-side**, not in the UI.
2. **Never trust `X-Active-Branch`.** Always validate it against the user's permitted set; out-of-set → `403` + audited `BranchScopeDenied`.
3. **Approvals, Medical Directors, managers, Case Managers, Finance/Claims are member-scoped across all branches.** External providers stay provider-scoped and are untouched by the branch dimension.
4. **Sensitive result content is default-deny for everyone except the authoring/ordering doctor** — including the approvals team. Others get existence metadata only.
5. **No release without purpose + justification.** Grants are time-boxed, single-result, non-transferable; **every read under a grant is separately audited** with the grant id.
6. All existing invariants still hold: atomic/idempotent consume-dispense, immutable hash-chained audit, soft-delete + history, min-necessary field projection.
7. Migrations are **additive and backward-compatible**; existing rows get sensible defaults (`sensitivity_level='Standard'`).

---

## Prompts

### 14.1 — `branch` entity + seed the six branches

```text
Add the internal Mersal branch entity to provider-service. Read ../37 §2 and ../22 (branch table) first.
provider-service's remit widens to "network & facilities": contracted providers AND internal branches, as SEPARATE tables. Do NOT reuse provider_location for branches.

SCHEMA (migration 0005_branch.sql, additive)
- branch: branch_id uuid PK (v7), branch_code varchar(8) UNIQUE, name_en, name_ar, city, address,
  timezone default 'Africa/Cairo', phone, opening_hours jsonb, status varchar(12) CHECK IN
  ('Active','Suspended','Closed'), + the standard audit columns (created_at/by, updated_at/by,
  row_version, is_deleted...). Index on (status), UNIQUE(branch_code) WHERE is_deleted=false.
- Seed the six branches idempotently (INSERT ... ON CONFLICT (branch_code) DO NOTHING):
  ASW Aswan/أسوان (Aswan), ALX Alexandria/الإسكندرية (Alexandria), OCT 6th of October/السادس من أكتوبر (Giza),
  MAA Maadi/المعادي (Cairo), DOK Dokki/الدقي (Giza), NSR Nasr City/مدينة نصر (Cairo).

API (scope provider:read / provider:admin, Kong + service validated)
- GET /api/v1/branches (list, filter by status) and GET /api/v1/branches/{id} — readable by any
  authenticated user (it is org reference data, no PHI); write restricted to Network/Org Admin.
- Emit BranchCreated/BranchUpdated/BranchStatusChanged via the outbox; audit every mutation.

ACCEPTANCE
- Given the migration runs, When I query branches, Then exactly the six seeded branches exist with EN+AR names and Active status.
- Given the seed runs twice, Then no duplicates are created (idempotent).
- Given a non-admin, When they POST /branches, Then 403 + audited.

TESTS: migration/seed idempotency, branch CRUD authz (admin vs non-admin), AR/EN name round-trip.
Update the service README + ../22 if the shipped schema differs from the doc.
```

### 14.2 — User↔branch assignment + active-branch context

```text
Add staff branch assignment and the active-branch context. Read ../37 §2.2–2.3 first. Work in
identity-service (assignment is an identity concern) and libs/auth (context plumbing).

SCHEMA (identity, additive migration)
- user_branch_assignment: assignment_id PK, user_id, branch_id (logical FK — value not FK, per
  cross-service rules), assignment_type varchar(10) CHECK IN ('Home','Additional'), valid_from date,
  valid_to date NULL, status varchar(10) CHECK IN ('Active','Revoked'), audit columns.
- CRITICAL INDEX: partial UNIQUE (user_id) WHERE assignment_type='Home' AND status='Active'
  — exactly one home branch per user.
- Index (user_id, status), (branch_id).

API (Org Admin / Network Team; SoD per ../10)
- POST/DELETE /api/v1/users/{id}/branches  { branchId, assignmentType, validFrom, validTo }
- GET /api/v1/users/{id}/branches ; GET /api/v1/me/branches -> { homeBranch, permittedBranches[] }
- Assigning a second Home must fail 409 with a clear reason. All changes audited; revocation takes
  effect on the next request (no stale caching beyond a short TTL).

ACTIVE-BRANCH CONTEXT (libs/auth)
- Read header X-Active-Branch. If absent -> resolve the user's Home branch. If present -> VALIDATE it is
  in the permitted set (Active + within validity window); if not -> 403 problem+json
  "branch-not-permitted" AND write an audited BranchScopeDenied event. NEVER trust the header.
- Expose the resolved context as IBranchContext { ActiveBranchId, PermittedBranchIds, IsBranchUnrestricted }
  registered per-request; cache the permitted set briefly (<=60s) keyed by user + a version stamp.
- Echo the resolved active branch on responses (header or envelope) so the UI shows the true context.
- POST /api/v1/me/active-branch {branchId} -> validates + emits ActiveBranchSwitched (audited: actor, from, to).

ACCEPTANCE
- Given a user with Home=Maadi and Additional=[Dokki], When they send X-Active-Branch=Dokki, Then it is accepted.
- Given the same user sends X-Active-Branch=Aswan, Then 403 + BranchScopeDenied audited.
- Given no header, Then the active branch resolves to Maadi.
- Given a second Home assignment is attempted, Then 409.
- Given an assignment is revoked, When the user next calls, Then that branch is no longer permitted.

TESTS: unit (permitted-set resolution incl. validity windows), integration (header accepted/denied/default),
authz test that a revoked assignment denies immediately, audit assertion on switch + denial.
```

### 14.3 — `BranchScope` ABAC condition + RowScope + policy bundle modes

```text
Teach the authorization engine about branches. Read ../37 §3 and the EXISTING libs/authz code
(AbacConditions.cs, RowScope.cs, IAuthorizationEngine.cs, PolicyBundle.cs) before changing anything —
follow the established shape used by ProviderOwnership and TreatingRelationship exactly.

libs/authz CHANGES
- Add AbacConditions.BranchScope + AbacConditions.InBranchScope(request): satisfied when
  resource.BranchId is in principal.PermittedBranchIds AND, for BranchScoped policies, equals
  principal.ActiveBranchId. Wire it into the engine's condition switch alongside the existing cases.
- Extend RowScope with BranchIds + BranchUnrestricted, mirroring the existing provider-scoping shape,
  so repositories can apply a branch predicate uniformly.
- Add a ScopeMode concept to PolicyBundle rules: BranchScoped | MemberScoped | ProviderScoped.

APPLY THE MODES (../37 §3 table — this is the heart of the change)
- BranchScoped: EmrPolicies appointment/slot/queue/encounter-worklist reads, reception search results
  that drive branch operations, branch-originated order worklists.
- MemberScoped (BranchUnrestricted=true): ApprovalsPolicies, FinancePolicies, CasePolicies,
  ReportingPolicies, AdminPolicies, and Medical Director / manager roles.
- ProviderScoped: ProviderPolicies, OrdersPolicies provider-queue, PharmacyPolicies — UNCHANGED
  (external providers are not branch-scoped).
- Bump each touched bundle's version constant.

BRANCH SCOPING NARROWS, NEVER REPLACES: a doctor still needs TreatingRelationship; reception still
cannot read EMR. Do not weaken any existing condition.

ACCEPTANCE
- Given a BranchScoped principal, When they request a resource in another branch, Then DENY with a
  branch reason (not an empty list) and an audit entry.
- Given a MemberScoped principal (approvals/director/finance/case), Then branch never restricts them.
- Given an external provider principal, Then behaviour is byte-for-byte unchanged from today.

TESTS: extend libs/authz/Tests — BranchScope satisfied/denied matrix, RowScope branch predicate,
a regression test per existing bundle proving no previously-allowed decision became denied
(run the FULL suite: ./dotnet.sh test HbmpPlatform.sln -c Release).
```

### 14.4 — Branch-scope the appointments, queue & orders worklists

```text
Apply branch scoping to the operational surfaces. Read ../37 §3, the EXISTING services/emr
(Api/Appointments.cs, Api/Queue.cs, Domain/Appointment.cs) and services/orders first.
appointment/appointment_slot/provider_availability/waitlist_entry ALREADY carry location_id — add
branch_id alongside it (a booking at a Mersal branch sets branch_id; a booking at an external
provider location leaves branch_id NULL and keeps location_id).

emr-service
- Additive migration: add branch_id uuid NULL to appointment, appointment_slot, provider_availability,
  waitlist_entry, encounter, and the queue ticket table; index (branch_id, scheduled_start) and
  (branch_id, status) for the worklists. Backfill existing rows where the location maps to a branch,
  else leave NULL and log a reconciliation report.
- Filter GET /appointments, GET /queues, and the clinician worklist by IBranchContext.ActiveBranchId
  for BranchScoped callers; MemberScoped callers see all branches with an OPTIONAL ?branchId= filter.
- Booking/rescheduling validates the target slot's branch == active branch for BranchScoped callers.
- PRESERVE the phase-3 no-double-book guarantee: the FOR UPDATE lock + ux_appointment_active_slot
  partial-unique index must remain exactly as-is. Adding branch_id must not weaken it — re-run
  AppointmentBookingConcurrencyTests.

orders-service
- Add ordering_branch_id to investigation_order (the branch where the order was raised); index it.
- Branch-scope the Mersal-side order worklists; the PROVIDER fulfillment queue (phase 5.1) stays
  provider-scoped and MUST NOT gain a branch filter.

ACCEPTANCE
- Given a receptionist with active branch Maadi, When they list appointments/queue, Then only Maadi
  rows return, and requesting a Dokki appointment by id returns 403 (not 404-empty).
- Given they switch active branch to Dokki (permitted), Then the lists change accordingly.
- Given an approvals reviewer or Medical Director, Then they see items across all six branches.
- Given a lab/imaging provider, Then their queue is unchanged (provider-scoped only).
- Given concurrent bookers on one slot, Then exactly one wins (phase-3 invariant intact).

TESTS: branch-filter integration tests per surface, cross-branch DENY test, member-scoped all-branch
test, provider-queue regression, and the existing concurrency suite green.
```

### 14.5 — Practitioner records, specialty & doctor↔branch assignment

```text
Add structured clinician identity. Read ../37 §4 first. Implement in provider-service (facilities +
practitioners) with the user link as a logical reference to identity.

SCHEMA (additive migration)
- specialty: specialty_code PK/UK, name_en, name_ar, parent_code NULL. Seed the ../37 §4 list
  (idempotent) — MUST include Psychiatry and Clinical Psychology (they drive sensitivity defaults).
- practitioner: practitioner_id PK, user_id (logical FK), practitioner_type CHECK IN ('Doctor','Nurse'),
  full_name_en, full_name_ar, license_no, license_expiry date, status; UNIQUE(user_id) WHERE is_deleted=false.
- practitioner_specialty: (practitioner_id, specialty_code) PK, is_primary boolean;
  partial UNIQUE (practitioner_id) WHERE is_primary — exactly one primary.
- practitioner_branch_assignment: (practitioner_id, branch_id) + valid_from/to, status — a doctor may
  serve ONE OR MANY branches.

API
- CRUD practitioners + specialty assignment + branch assignment (Network Team / Org Admin; audited).
- GET /api/v1/practitioners?branchId=&specialtyCode=&type= — the doctor picker feed, filtered by
  branch AND specialty; returns min-necessary fields only (no licence numbers to non-admin callers).
- Feed licence expiry into the EXISTING phase-2b credential-expiry reminder sweep.

ENFORCEMENT
- Creating provider_availability or booking an appointment for a doctor at a branch they are NOT
  assigned to -> 422 problem+json with a clear reason (validated in BOTH places, not just the UI).

ACCEPTANCE
- Given a doctor assigned to Maadi and Dokki, When availability is created at Aswan, Then 422.
- Given the picker is called with branchId=Maadi&specialtyCode=PSYCH, Then only psychiatrists assigned
  to Maadi return.
- Given a practitioner, Then exactly one primary specialty is enforced (409 on a second).

TESTS: assignment matrix, one-primary-specialty constraint, booking/availability rejection at an
unassigned branch, picker filtering, min-necessary projection on the picker response.
```

### 14.6 — Examination type + sensitivity classification on orders & results

```text
Introduce examination types and sensitivity. Read ../37 §5 first. Reference data lives in
masterdata-service; the classification is denormalized onto orders/results so read-time gating never
needs a cross-service join.

masterdata-service (additive)
- examination_type: examination_type_id PK, code UK, name_en, name_ar,
  category CHECK IN ('Lab','Imaging','Procedure','Consultation','Assessment'),
  default_code_system CHECK IN ('CPT','LOINC','LOCAL'), default_code,
  sensitivity_level CHECK IN ('Standard','Sensitive','HighlySensitive'),
  sensitive_category CHECK IN ('MentalHealth','HIV_STI','Genetic','SubstanceUse','ReproductiveHealth','GBV_Forensic','Other') NULL,
  status. Seed a starter set; classify MENTAL-HEALTH assessments/consultations as
  Sensitive + MentalHealth (the confirmed requirement). Mark the other special categories as
  configuration for the Medical Director + DPO to ratify — do not hard-code policy in code.
- GET /examination-types (filter by category/sensitivity) + GET /examination-types/{id} for validation.

orders-service (additive migration, backward-compatible)
- Add examination_type_id + sensitivity_level (denormalized, DEFAULT 'Standard') to investigation_order
  and order_line; add sensitivity_level to the result/report record from phase 5.3. Backfill existing
  rows to 'Standard'. Validate examination_type_id against masterdata FAIL-CLOSED (unknown -> 422).
- When an order is created, resolve and PIN the sensitivity from the examination type (so later
  reclassification cannot retroactively unlock already-restricted data — record the pinned value).
- Emit SensitiveResultRestricted when a result is uploaded against a non-Standard line.

ACCEPTANCE
- Given an order for a mental-health assessment, Then order/line/result carry sensitivity_level='Sensitive'
  and sensitive_category='MentalHealth'.
- Given an unknown examination_type_id, Then 422 (fail-closed).
- Given pre-existing orders, Then they default to 'Standard' and behave exactly as before.

TESTS: master-data validation, pinning behaviour, backfill/default regression, event emission.
```

### 14.7 — Sensitive-result gating + the release-request workflow

```text
THE PRIVACY HEART OF THIS PHASE. Read ../37 §6, ../11 (min-necessary), ../18 §11 (break-glass),
../19 (audit), ../20 (special-category data) first. Implement in orders-service (results) with the
request/grant tables alongside; reuse the EXISTING BreakGlass machinery from libs/authz — do not
invent a parallel one.

DEFAULT GATE (default-deny)
- For a result with sensitivity_level != 'Standard', FULL CONTENT (values + report document) is
  readable ONLY by the authoring/ordering doctor (with treating relationship).
- EVERYONE ELSE — including the medical approval team, other treating clinicians, case managers and
  reporting — receives EXISTENCE METADATA ONLY: examination category, date, status, ordering branch,
  and a RESTRICTED marker. Never values, never the report document, never a signed URL.
- This DELIBERATELY overrides the approval team's standing EMR oversight for sensitive results —
  encode it explicitly and test it.
- Field-level stripping happens SERVER-SIDE via the existing FieldProjector; a client must not be able
  to request the restricted fields by any query manipulation.

SCHEMA (additive)
- report_access_request: request_id PK, result_ref, document_id NULL, beneficiary_id, requested_by,
  requested_for_role, purpose_code CHECK IN ('ContinuityOfCare','AuthorizationDecision','ClinicalReview','Complaint','Legal','Other'),
  justification text NOT NULL, requested_ttl_hours int, status CHECK IN
  ('Requested','UnderReview','InfoRequested','Approved','Denied','Expired','Revoked'),
  decided_by, decided_by_role, decided_at, decision_reason.
- report_access_grant: grant_id PK, request_id FK, grantee_user_id, result_ref, purpose_code,
  granted_at, expires_at, revoked_at NULL, revoked_by NULL.
  Index (grantee_user_id, result_ref) WHERE revoked_at IS NULL.

API
- POST /api/v1/report-access-requests { resultRef, purposeCode, justification, requestedTtlHours }
  -> 422 if purpose or justification missing/blank. Routes to the authoring/ordering doctor; notifies.
- POST /api/v1/report-access-requests/{id}/decision { decision, ttlHours?, reason? }
  Deciders: the AUTHORING/ORDERING DOCTOR **or** a MEDICAL DIRECTOR (so care isn't blocked when the
  author is unavailable). A Medical Director decision sets decided_by_role='MedicalDirector' and is
  EXTRA-AUDITED. Deny requires a reason. RequestInfo returns it to the requester.
- Approve creates a TIME-BOXED grant: default 72h for 'Sensitive', 24h for 'HighlySensitive'
  (configurable), scoped to ONE result, NON-TRANSFERABLE.
- POST /report-access-grants/{id}/revoke (author, Medical Director, or DPO) — audited + notified.
- A background sweep expires grants; expiry is audited + notified.

READS UNDER A GRANT
- When a restricted result is read via an active grant, return the content AND write a DISTINCT audit
  event SensitiveResultReadUnderGrant carrying grant_id, purpose_code, actor, result_ref — separate
  from ordinary PHI-read audit.

BREAK-GLASS
- Emergency access to a sensitive result reuses libs/authz BreakGlass but is LOUD: extra justification,
  immediate notification to the authoring doctor AND Medical Director AND DPO, and it is flagged for
  MANDATORY retrospective review.

EVENTS (outbox): ReportAccessRequested, ReportAccessInfoRequested, ReportAccessApproved,
ReportAccessDenied, ReportAccessGrantExpired, ReportAccessGrantRevoked, SensitiveResultReadUnderGrant.

ACCEPTANCE (Given/When/Then)
- Given a mental-health result, When a NON-authoring clinician, an APPROVALS reviewer, or a case
  manager reads it, Then only existence metadata returns — no values, no report reference.
- Given the authoring doctor reads it, Then full content returns (and is audited as a PHI read).
- Given a request without justification or purpose, Then 422.
- Given a request, When the authoring doctor approves with ttl=24h, Then a single-result grant is
  created, the requester can read the content, and each such read writes SensitiveResultReadUnderGrant.
- Given the author is unavailable, When a Medical Director approves, Then it succeeds and is flagged
  decided_by_role='MedicalDirector' with extra audit.
- Given a grant passes expires_at, When the grantee reads again, Then access is denied and audited.
- Given a revoked grant, Then access is denied immediately.
- Given break-glass on a sensitive result, Then author + Medical Director + DPO are notified and the
  access is flagged for retrospective review.

TESTS (all REQUIRED)
- Authorization test matrix: authoring doctor vs other doctor vs approvals vs case manager vs finance
  vs admin — asserting restricted fields are ABSENT from the payload (reflection-based, like the
  existing QueueMinNecessaryTests / eligibility min-necessary tests).
- Request validation (missing purpose/justification), decision authz (only author or Medical Director),
  grant TTL expiry, revocation, non-transferability (grantee B cannot use grantee A's grant),
  single-result scoping (a grant for result X does not unlock result Y).
- Audit tests: request/decision/expiry/revocation and EVERY read-under-grant produce hash-chained events.
```

### 14.8 — Branch switcher + restricted-result UI (frontend)

```text
Add the UI surfaces. Read ../37 §7, ../0B-DESIGN-SYSTEM-UI.md (incl. §10b visual refinement v1.1),
../14-navigation-structure.md, ../21-accessibility-checklist.md first. If the phase-9 design system
is not yet built, deliver these as specified components to fold into phase 9.

BRANCH SWITCHER (app bar)
- BranchScoped roles: a switcher showing the ACTIVE branch and listing permitted branches (Home marked).
  Selecting one calls POST /me/active-branch, refreshes branch-scoped views, and announces the change
  via aria-live. Keyboard operable, >=44px target, visible 3px focus ring, RTL-mirrored, AR/EN labels.
- MemberScoped roles: show an "All branches" indicator plus an OPTIONAL branch filter — never a restriction.
- The active branch is visible on appointment/queue/order screens so a user cannot mistake their context.

RESTRICTED RESULT STATE
- A sensitive result the caller may not read renders as a LOCKED card: examination category + date +
  a RESTRICTED status chip using the four-cue system (neutral hue + lock icon + ghost pill + text —
  never colour alone), plus a "Request access" action.
- The request dialog captures purpose code + justification (both required, inline validation with
  aria-describedby) and requested duration.
- The decision screen (doctor / Medical Director) shows requester, role, purpose, justification and
  requested TTL, with Approve (TTL picker) / Deny (mandatory reason) / Request info.
- Never render a restricted value in the DOM "hidden" — the server must not have sent it.

ACCEPTANCE
- Given a multi-branch user, When they switch branch, Then lists refresh, the change is announced and audited.
- Given a restricted result, Then the UI shows the locked state with no values present in the payload or DOM.
- Given axe + keyboard + screen-reader + AR/RTL checks, Then all pass (a11y DoD).

TESTS: component tests for switcher + locked card + request dialog; axe in CI; an assertion that the
restricted payload contains no value fields.
```

---

## Guardrails

- **Do not rewrite built services.** Extend them; keep every existing test green (`./dotnet.sh test HbmpPlatform.sln -c Release` — 375 tests before you start).
- **Additive migrations only** (expand/contract); backfill defaults so existing data behaves exactly as before.
- Branch scoping is **server-side** and returns **denial, not silent emptiness**, on cross-branch access.
- **Never** let a client influence its own permitted branch set or read restricted content by query manipulation.
- Sensitive gating **overrides** approval-team EMR oversight — encode and test it explicitly.
- Every branch switch, scope denial, access request, decision, grant expiry/revocation, and read-under-grant is an **immutable hash-chained audit event**.
- Reuse the existing `BreakGlass`, `FieldProjector`, `RowScope`, outbox, and idempotency machinery — no parallel implementations.
- Respect the machine gotchas in `docs/HANDOFF.md` (`./dotnet.sh`, PG on :55432, .NET 8 APIs only, analyzers-as-errors, central package management, RLS needs the `hbmp_app` NOBYPASSRLS role).

## Done when

- [ ] Six branches seeded (EN/AR), distinct from `provider_location`.
- [ ] Users have one Home + optional Additional branches; `X-Active-Branch` validated server-side (403 + audit when out of set; Home as default).
- [ ] Reception/coordinator/nurse/doctor worklists, appointment lists and queues return **only the active branch**; cross-branch access is denied.
- [ ] Approvals, Medical Directors, managers, Case Managers, Finance/Claims see **all branches**; external provider queues are unchanged.
- [ ] Doctors have structured specialty (one primary) and one-or-many branch assignments; booking/availability at an unassigned branch fails 422; pickers filter by branch + specialty.
- [ ] Orders carry examination type with pinned sensitivity; existing rows default to `Standard`.
- [ ] A mental-health result returns **existence metadata only** to non-authoring clinicians, the approvals team and case managers — proven by authorization tests.
- [ ] Release requests require purpose + justification, route to the authoring doctor, and can be decided by a **Medical Director** (flagged + extra-audited).
- [ ] Grants are time-boxed, single-result, non-transferable, revocable, auto-expiring — and **every read under a grant is separately audited**.
- [ ] Break-glass on a sensitive result notifies author + Medical Director + DPO and is flagged for retrospective review.
- [ ] Branch switcher + locked-result UI meet the a11y DoD (keyboard, SR, AR/RTL, axe, ≥44px, non-colour status).
- [ ] Full suite green; `docs/BUILD-STATUS.md` ticked; service READMEs + ADRs updated; conventional commits per sub-prompt.

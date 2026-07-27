# Phase 19 — Policy & Member Administration (real PAS)

**Goal:** Rebuild Beneficiary Management into a genuine **Policy Administration System**: payers/sponsors → plans with **effective-dated, immutable versions** carrying the benefit configuration → policies → member groups → enrollment (with dependents, waiting periods, terminations, retro-effective changes) → **utilization for individual and group** → **policy query & member query** → full coverage and beneficiary detail → **signed, timestamped, cancellable notes on policy and member**.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> **Sequencing:** run **after phase 18 Gate A** — 18.A1 makes `coverage_limit.consumed_value` actually increment, and every utilization view in this phase reads that accumulator. Building utilization first would report zeros forever.
>
> **This is an EXTENSION of `services/policy`, not a rewrite.** The existing `Policy`/`BenefitCategory`/`Coverage`/`CoverageLimit` stay — `Coverage`/`CoverageLimit` become **generated** from a plan version instead of hand-entered, so the eligibility engine and the phase-18 accumulator keep working untouched.

## Skills to activate
> `policy-eligibility-engine`, `health-insurance-tpa-operations`, `beneficiary-lifecycle-management`, `healthcare-business-rules-engine`, `healthcare-database-architect`, `healthcare-uiux-designer`, `healthcare-reporting-kpis` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Index: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first
- [`../38-policy-member-administration.md`](../38-policy-member-administration.md) — **AUTHORITATIVE**: model, capabilities, notes spec (§5), invariants (§7), acceptance (§8).
- [`../22-data-dictionary.md`](../22-data-dictionary.md) (schema conventions) · [`../23-state-machines.md`](../23-state-machines.md) (policy/coverage lifecycles) · [`../11-permission-matrix.md`](../11-permission-matrix.md) (min-necessary, new roles) · [`../36-claims-management.md`](../36-claims-management.md) §5 (adjudication reads plan config) · [`../37-…-sensitivity.md`](../37-branch-scoping-and-clinical-sensitivity.md) §3/§6 (branch + payer scope, sensitive classes) · [`../0B-DESIGN-SYSTEM-UI.md`](../0B-DESIGN-SYSTEM-UI.md) + [`../21-accessibility-checklist.md`](../21-accessibility-checklist.md).
- **Existing code:** `services/policy/{Domain/Entities.cs,Domain/LimitReset.cs,Api/Program.cs,Infrastructure/*}`, `services/eligibility/Domain/EligibilityEngine.cs` (consumes coverage+limits), `services/patient` (beneficiary/identifiers/contacts/family), `libs/authz` (`FieldProjector`, `RowScope`, policy bundles), `libs/data` (RLS binder + tenant stamping), `apps/web/src/portals/catalog.ts` + `screens/registry.tsx`.
- `docs/HANDOFF.md` gotchas (`./dotnet.sh`, PG :55432, .NET 8 only, analyzers-as-errors, CPM, pnpm).

## THE INVARIANTS
1. **Resolve the plan version in force on the SERVICE DATE**, never "current" — eligibility, authorization and claims all do this.
2. **An `Active` plan version is immutable.** Amendments create a new version; the previous becomes `Superseded`. Never mutate.
3. **Enrollment GENERATES coverage + limits** from the plan version, recording `plan_version_id` so entitlement is explainable.
4. **This module never writes `consumed_value`** — phase 18 owns it; here we only read. (Do not re-introduce the X1 bug class.)
5. **No overlapping active enrollment** per (beneficiary, policy) — enforce with a GiST exclusion constraint on the date range.
6. **Retro-effective changes are append-only events**, never edits.
7. **Notes are append-only, signed, timestamped, cancellable — never edited or deleted.**
8. Existing invariants hold: min-necessary field projection, immutable audit on mutations + PHI reads, soft-delete, tenant RLS, additive migrations.

---

## Prompts

### 19.1 — Payer, plan, plan version + benefit configuration
```text
Extend services/policy. Read ../38 §3 and the existing Domain/Entities.cs first. Additive migration.

SCHEMA
- payer: payer_id, payer_code UK, name_en/ar, payer_type CHECK IN
  ('SelfFunded','Donor','Government','PartnerNGO','Insurer'), contact jsonb, status, audit cols.
- plan: plan_id, plan_code UK, name_en/ar, description, category, status.
- plan_version: plan_version_id, plan_id FK, version_no int, effective_from date NOT NULL,
  effective_to date NULL, status CHECK IN ('Draft','Active','Superseded','Retired'),
  activated_by/at, superseded_by_version_id. CONSTRAINTS: no overlapping Active ranges per plan
  (GiST exclusion on daterange); version_no unique per plan.
- benefit_rule: rule_id, plan_version_id FK, benefit_category_id FK, is_covered bool,
  limit_type CHECK IN ('Annual','PerEncounter','Lifetime','Count') NULL, limit_value numeric(14,2) NULL,
  reset_period CHECK IN ('None','Monthly','Quarterly','Yearly'), copay_fixed numeric(14,2) NULL,
  copay_percent numeric(5,2) NULL, deductible numeric(14,2) NULL, waiting_period_days int NOT NULL
  DEFAULT 0, requires_preauth bool, preauth_cost_threshold numeric(14,2) NULL, network_tier,
  exclusions jsonb, notes text. UNIQUE(plan_version_id, benefit_category_id).

BEHAVIOUR
- Draft versions are freely editable. POST /plan-versions/{id}/activate validates (≥1 covered category,
  no contradictory limits, dates sane, no Active overlap) then flips to Active and marks the previous
  Superseded — AFTER activation the version and its rules are IMMUTABLE (reject writes with 409).
- POST /plans/{id}/amend clones the Active version into a new Draft (version_no+1) for editing.
- Resolver: IPlanVersionResolver.ResolveAsync(planId, serviceDate) → the version whose range contains
  the date. Expose GET /plans/{id}/version-at?date= for other services.
- Scopes: policy:admin for writes, policy:read for reads. Audit every mutation; emit
  PayerCreated/PlanVersionActivated/PlanVersionSuperseded via the outbox.

ACCEPTANCE
- Given an Active version, When any field or rule is updated, Then 409 immutable.
- Given versions v1 [Jan–Jun] and v2 [Jul–], When resolving for 15 Jun, Then v1; for 15 Jul, Then v2.
- Given a second Active version overlapping an existing one, Then the DB constraint rejects it.
TESTS: activation validation matrix, immutability, resolver boundary (incl. the exact effective_from
day), overlap exclusion, authz (policy:admin required), audit assertions.
```

### 19.2 — Policy, groups, enrollment (+ coverage generation)
```text
Read ../38 §3–§4.2 and ../23 policy/coverage lifecycles. Extend the existing Policy entity; do not
break the eligibility engine's reads.

SCHEMA
- policy (extend): add payer_id FK, plan_version_id FK, previous_policy_id NULL (renewal chain),
  max_members int NULL. Keep policy_no, effective dates, status.
- member_group: group_id, policy_id FK, group_code, name_en/ar, group_type CHECK IN
  ('Programme','Cohort','BranchCaseload','Campaign'), effective_from/to, status.
  UNIQUE(policy_id, group_code).
- enrollment: enrollment_id, beneficiary_id (logical FK), policy_id FK, group_id NULL FK, member_no UK,
  relationship CHECK IN ('Principal','Spouse','Child','Dependent'), principal_enrollment_id NULL,
  effective_from date, effective_to date NULL, waiting_period_ends_on date NULL,
  status CHECK IN ('Pending','Active','Suspended','Terminated','Cancelled'), termination_reason,
  source_plan_version_id (provenance). EXCLUSION CONSTRAINT: no overlapping (beneficiary_id, policy_id)
  where status IN ('Active','Suspended') using daterange + GiST (btree_gist for the uuid equality).
- enrollment_event (append-only): event_id, enrollment_id FK, event_type CHECK IN
  ('Enrolled','GroupChanged','Suspended','Reinstated','Terminated','Corrected'), effective_date,
  reason, payload jsonb, actor_user_id, occurred_at.

BEHAVIOUR
- POST /policies (payer + plan version + dates) → issue; POST /policies/{id}/renew → new policy linked
  via previous_policy_id, optionally carrying members forward (explicit flag, reported count).
- POST /enrollments (Idempotency-Key) → validates the beneficiary is Active (patient-service), the
  policy is in force, no overlap; computes waiting_period_ends_on from the plan's benefit rules;
  **GENERATES the member's coverage + coverage_limit rows from plan_version.benefit_rule**, stamping
  source_plan_version_id. Dependents link to a principal.
- POST /enrollments/{id}/terminate {effectiveDate, reason} (reason MANDATORY), /reinstate,
  /change-group; each writes an enrollment_event. Retro-effective dates allowed for a supervisor scope
  and always recorded as an event, never an edit.
- Bulk enrol from CSV: staged → validated → committed with a reconciliation report (reuse the
  tools/migration patterns); partial failure never half-commits.
- Emit MemberEnrolled/MemberTerminated/MemberReinstated/CoverageGenerated via the outbox so eligibility
  reprojects. NEVER write consumed_value.

ACCEPTANCE
- Given an enrolment, Then coverage + limits exist matching the plan version's rules, with provenance.
- Given a second active enrolment overlapping the same beneficiary+policy, Then the DB rejects it.
- Given a service inside the waiting period, When eligibility is checked, Then Ineligible with a
  WAITING_PERIOD reason.
- Given a retro-effective termination, Then an enrollment_event records it and utilization recomputes.
TESTS: coverage generation fidelity, overlap exclusion, waiting-period eligibility, dependent linkage,
termination/reinstatement lifecycle, bulk enrol reconciliation, idempotent replay, audit.
```

### 19.3 — Notes on policy and member (signed · timestamped · cancellable)
```text
THE NOTES REQUIREMENT. Read ../38 §5 — it is the specification; follow it exactly.

SCHEMA (policy schema)
- note: note_id PK, scope CHECK IN ('Policy','Member'), scope_ref uuid NOT NULL,
  note_type CHECK IN ('General','Eligibility','Exception','Approval','Complaint','Financial',
  'Clinical','Administrative'), body text NOT NULL,
  visibility_class CHECK IN ('Administrative','Financial','Clinical','Restricted'),
  authored_by_user_id uuid NOT NULL, authored_by_username varchar(128) NOT NULL,
  authored_by_display varchar(200) NOT NULL, authored_at timestamptz NOT NULL,
  status CHECK IN ('Active','Cancelled') NOT NULL DEFAULT 'Active',
  cancelled_by_user_id NULL, cancelled_by_username varchar(128) NULL, cancelled_at timestamptz NULL,
  cancellation_reason text NULL, supersedes_note_id uuid NULL, pinned bool NOT NULL DEFAULT false,
  tenant_id + audit cols. Index (scope, scope_ref, authored_at DESC), (status), (pinned).
- CHECK: status='Cancelled' REQUIRES cancelled_by_user_id, cancelled_at AND cancellation_reason.

RULES (non-negotiable)
- APPEND-ONLY: body is NEVER updated and NEVER deleted. The only permitted mutation is Active→Cancelled.
  Reject any PATCH/PUT of body with 409. A correction is a NEW note (optionally supersedes_note_id).
- SIGNED: capture authored_by_username + display as a SNAPSHOT at write time (not a join) so the
  signature survives rename/de-provisioning. Take them from the token principal, never from the body.
- TIMESTAMPED: authored_at UTC; the API returns UTC and the UI renders Africa/Cairo (../38 §5.3).
- CANCELLATION: only the AUTHOR or a supervisor scope (policy:supervise / org admin) may cancel;
  cancellation_reason MANDATORY; cancelled notes remain VISIBLE (struck-through/dimmed), never hidden.
- MIN-NECESSARY: project the body server-side via libs/authz FieldProjector by visibility_class —
  Finance and Call Centre NEVER receive a Clinical/Restricted body (they receive existence: type,
  date, author, status). Restricted follows the ../37 §6 sensitive pattern.
- AUDIT: create + cancel always; READ audited when class is Clinical or Restricted.

API (scopes note:read / note:write / policy:supervise)
- POST /policies/{id}/notes , POST /enrollments/{id}/notes {noteType, body, visibilityClass, pinned}
- GET  /policies/{id}/notes , GET /enrollments/{id}/notes  (?status=&type=, newest-first, pinned first)
- POST /notes/{id}/cancel {reason}   -- author or supervisor only
- POST /notes/{id}/pin | /unpin
Emit NoteAdded / NoteCancelled via the outbox.

ACCEPTANCE (Given/When/Then)
- Given a note is created, Then it stores the author's username + display + UTC timestamp and returns
  status Active.
- Given anyone attempts to edit the body, Then 409 and the body is unchanged.
- Given the author cancels with a reason, Then status=Cancelled with who/when/why AND the original body
  is still returned (marked cancelled).
- Given a cancel without a reason, Then 422.
- Given a non-author, non-supervisor cancels, Then 403 + audited.
- Given a Finance or Call Centre principal reads a Clinical note, Then the body is ABSENT while type,
  date, author and status are present.
- Given the author is later renamed/disabled, Then the note still shows the original signed username.
TESTS: append-only enforcement, signature snapshot survival, cancellation authz + mandatory reason,
class-based projection matrix (reflection over the serialized payload), audit on create/cancel/
clinical-read, ordering (pinned then newest).
```

### 19.4 — Utilization (individual · group · policy · payer)
```text
Read ../38 §4.3. This is a READ MODEL over data the platform already records — it never writes
consumed_value (phase 18 owns that). Build in policy-service (or reporting-service if the projection
already lives there — choose, and state it in the ADR).

- Projection consuming CoverageLimitChanged / OrderLinesConsumed / RxLinesDispensed / claim events
  (idempotent, dedupe on event id) OR a direct query over coverage_limit + claims read models —
  prefer direct query first for correctness, add the projection only if latency demands it.
- GET /utilization/members/{beneficiaryId}?from=&to= → per benefit category: limit, consumed,
  remaining, % used, reset date; encounter counts; authorizations raised/approved/denied; claim value.
- GET /utilization/groups/{groupId}, /utilization/policies/{policyId}, /utilization/payers/{payerId}
  → aggregate totals + per-member table + distribution buckets + outliers (> X% of limit, configurable).
- Every response reconciles EXACTLY to the accumulator: assert Σ member consumption == the sum of
  coverage_limit.consumed_value for the scope (a test, not a comment).
- Audited CSV/XLSX export; exports carry NO clinical fields.
- MIN-NECESSARY: Finance/Claims see amounts + categories, never diagnoses; the response is
  FieldProjector-projected by role.

ACCEPTANCE
- Given consumption recorded via consume/dispense, Then member utilization reflects it exactly and the
  group/policy/payer aggregates sum correctly.
- Given a Finance principal, Then no clinical field is present in any utilization payload.
- Given an export, Then it is audited.
TESTS: reconciliation-to-accumulator, aggregation correctness across group/policy/payer, outlier
detection, projection matrix, export audit.
```

### 19.5 — Policy query, member query, coverage & beneficiary detail
```text
Read ../38 §4.4–§4.6.
- GET /policy-query: filter by payer, plan, status, effective window, group, member-count band,
  utilization band; sortable, paginated (page+pageSize with an explicit allow-list of sort fields),
  audited export.
- GET /member-query: filter by identifier (any type), name, member_no, policy, group, relationship,
  status, branch, enrollment window, waiting-period state, utilization band. Same pagination/sort/export.
- GET /enrollments/{id}/coverage-details → plan + version in force, every benefit category with
  covered/limit/consumed/remaining/reset/co-pay/waiting-period status/pre-auth requirement/exclusions,
  plus the effective-dated change history.
- GET /beneficiaries/{id}/administrative-360 → composes patient-service (identifiers, contacts, family,
  documents metadata) + enrollment history + policy/group membership + notes (class-projected).
  AGGREGATE, do not duplicate: call the owning services with the caller's token.
- ALL of the above: FieldProjector by role, PHI reads audited, payer scope + branch scope applied
  (payer-scoped users see only their payer; policy-admin roles are member-scoped/all-branches).

ACCEPTANCE
- Given a payer-scoped user, When they run policy query, Then only their payer's policies return
  (a cross-payer id returns 403, not an empty list).
- Given Reception runs member query, Then the payload carries eligibility-relevant fields only — no
  clinical content (reflection-asserted).
- Given coverage details for a member enrolled under v1 with a service date in v1's window, Then v1's
  rules are shown even though v2 is now Active.
TESTS: filter/sort/pagination correctness, payer-scope denial, projection matrix per role, version-in-
force correctness, export audit.
```

### 19.6 — Frontend: Beneficiary Management portal (+ Policy Administrator)
```text
Read ../38 §4/§6, ../0B (incl. §10b v1.1), ../14-navigation-structure.md, ../21, and the EXISTING
apps/web/src/portals/catalog.ts + screens/registry.tsx. Follow the PortalDef/Section shape exactly;
add the new permissions and gate every section.

SCREENS
- Policy Administration (new policy_admin role): payers list/detail; plans list; PLAN VERSION EDITOR
  (benefit rules grid: category × covered/limit/reset/co-pay/waiting/pre-auth/exclusions) with a clear
  Draft vs Active-immutable state, Validate + Activate actions, and an amend→new-version flow with a
  version timeline and a diff between versions.
- Policies: list + detail (payer, plan version in force, dates, groups, member count, utilization
  summary, notes panel), issue / amend / renew.
- Members: member query results table; member detail = identity + enrollment history + coverage details
  + utilization + notes panel; enrol / terminate / reinstate / change-group dialogs (mandatory reason
  on terminate); bulk-enrol upload with the reconciliation report.
- Groups: manage cohorts, membership, group utilization.
- Utilization: individual and group views — limit-vs-consumed bars, category breakdown, outliers,
  export. Charts MUST have an accessible data-table alternative always rendered (sr-only), per the
  R2 audit finding.
- NOTES PANEL (shared component, used on policy AND member):
  * Add note: type, visibility class, body, pin — submits with an Idempotency-Key.
  * List: newest-first, pinned first; each note shows body, note type chip, **author username**, and
    the timestamp in Africa/Cairo.
  * Cancelled notes render struck-through/dimmed with a four-cue status chip (neutral hue + icon +
    ghost pill + the word "Cancelled") and show who cancelled it, when, and why — never hidden.
  * Cancel action visible only to the author or a supervisor; opens a dialog with a MANDATORY reason.
  * A note whose body was withheld by projection renders a "Restricted — clinical note" locked state
    (existence + type + author + date), never an empty body.
- Every write path: typed bilingual error via the shared writeErrorMessage(ApiError), an
  Idempotency-Key minted once per form instance, and NO optimistic UI on server-invariant operations
  (phase 18 D1 rules).
- Bilingual AR/EN with full RTL, tokens only, ≥44px targets, visible focus, aria-live on outcomes.

ACCEPTANCE
- Given an Active plan version, Then the editor is read-only with an explicit "immutable — amend to
  change" affordance.
- Given a cancelled note, Then it is still visible, struck-through, with canceller + reason + timestamp.
- Given a Finance user, Then clinical notes render as restricted with no body in the payload or DOM.
- Given axe + keyboard + screen-reader checks in EN and AR, Then all pass.
TESTS: component tests for the notes panel (add, cancel-with-reason, cancelled rendering, restricted
rendering), plan-version immutability, member query table, utilization data-table alternative; axe.
```

### 19.7 — Roles, docs, migration & wiring
```text
- ROLES: add policy_admin (+ a policy:supervise scope for note cancellation and retro-effective
  changes) to identity's role/scope seed (0001_identity.sql), libs/authz PolicyPolicies bundle,
  apps/web permissions + ROLE_MAP + portal catalog. Update ../10-role-matrix.md and
  ../11-permission-matrix.md (new resources: payer, plan, plan_version, benefit_rule, member_group,
  enrollment, note; hard rules: Finance/Call-Centre never receive Clinical/Restricted note bodies;
  payer scope; note append-only).
- KONG: routes for /api/v1/{payers,plans,plan-versions,policies,member-groups,enrollments,notes,
  utilization,policy-query,member-query}. Verify with the route-coverage guard (phase 18 E1).
- DATA MIGRATION: backfill existing policies → create a default payer ("Mersal — self-funded") and a
  plan version reverse-engineered from current coverage rows; generate enrollments from existing
  coverage; write a reconciliation report. Reversible by batch (reuse tools/migration).
- DOCS: update ../22 (new tables/enums), ../23 (plan-version + enrollment + note lifecycles), ../07
  (FR-POL-* / FR-NOTE-*), ../16 (policy-service remit), ../14 (portal nav), 00-README-INDEX + README
  (doc 38), BUILD-STATUS (19.1–19.7). ADR-0017 "Effective-dated immutable plan versions" and
  ADR-0018 "Append-only signed notes".
ACCEPTANCE: policy_admin can log in and reach the new portal through Kong; the backfill reconciles;
the route guard passes; docs are true.
```

---

## Guardrails
- **Never write `consumed_value` from this module** — read-only against the phase-18 accumulator.
- **Never mutate an Active plan version** or a note body — both are immutable by design; corrections create new rows.
- Coverage/limits must remain shape-compatible with `EligibilityEngine` — run the eligibility suite after 19.2.
- Additive migrations only; the backfill is reversible and reconciled before anything is switched over.
- Min-necessary is server-side (`FieldProjector`), proven by reflection tests over serialized payloads — not by UI hiding.
- Every mutation and every Clinical/Restricted note read writes an immutable audit event.
- Full suite green after each sub-prompt (`./dotnet.sh test HbmpPlatform.sln -c Release` + `pnpm -r test`).

## Done when
- [ ] Payer → plan → **effective-dated immutable plan version** with full benefit configuration; amend/renew flows work; the resolver returns the version in force on a given **service date** (boundary-tested).
- [ ] Policies carry payer + plan version; groups exist; members enrol (with dependents, waiting periods, bulk import), terminate with mandatory reason, reinstate, change group — with **coverage generated** from the plan version and no overlapping active enrollment.
- [ ] **Notes on policy and member**: timestamped (UTC, rendered Africa/Cairo), **signed with username** (snapshot), status **Active/Cancelled** with mandatory cancellation reason, **append-only** (body never edited or deleted), cancelled notes still visible, class-projected so Finance/Call Centre never receive clinical bodies, fully audited.
- [ ] Utilization for **individual, group, policy and payer**, reconciling exactly to the accumulator, with accessible data-table alternatives and audited exports.
- [ ] **Policy query** and **member query** with the full criteria set, pagination, sort, audited export, payer + branch scope, role projection.
- [ ] Full coverage details (version-in-force correct) and the administrative beneficiary 360.
- [ ] Portal shipped for Beneficiary Management + the new Policy Administrator role, bilingual, WCAG 2.2 AA, no silent write failures.
- [ ] Authorization tests prove payer-scope isolation and note-class projection; docs, ADRs and BUILD-STATUS updated.

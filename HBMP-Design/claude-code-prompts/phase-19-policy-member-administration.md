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

### 19.1b — Network tiers (network administration) + tier-aware cost-share
```text
Network tiers are OWNED BY NETWORK ADMINISTRATION, not policy admin. Read ../38 §3 (network tiers)
and §4.1b first, plus the EXISTING services/provider (Provider, ProviderLocation, ProviderContract,
contract_service_line, Practitioners, OnboardingWorkflow) — you are extending that service.

SCHEMA A — provider-service (additive migration)
- network_tier: network_tier_id PK, tier_code UK (e.g. 'T1','T2','OON'), name_en/ar, rank int
  (1 = most preferred), description, is_out_of_network bool NOT NULL DEFAULT false, status
  CHECK IN ('Active','Retired'), audit cols. UNIQUE(tier_code) WHERE is_deleted=false; UNIQUE(rank)
  among Active tiers.
- provider_network_assignment: assignment_id PK, network_tier_id FK,
  scope CHECK IN ('Provider','Location','ContractServiceLine'), scope_ref uuid NOT NULL,
  effective_from date NOT NULL, effective_to date NULL, status, audit cols.
  EXCLUSION CONSTRAINT: no overlapping active assignment for the same (scope, scope_ref)
  — daterange + GiST (btree_gist for the equality parts). A location assignment OVERRIDES its parent
  provider's assignment for the same period (most-specific-wins).

API (provider-service; scopes provider:admin for writes — NETWORK TEAM only, provider:read for reads)
- CRUD /api/v1/network-tiers (Network Team / Org Admin only — policy admins must NOT be able to create
  or reassign tiers; assert this with an authz test).
- POST/DELETE /api/v1/network-tiers/{id}/assignments {scope, scopeRef, effectiveFrom, effectiveTo}
- GET /api/v1/network-tiers/resolve?providerId=&locationId=&serviceDate=&serviceCode=
  → the tier in force, applying most-specific-wins (contract line > location > provider) and returning
  a documented DEFAULT (the Active tier flagged is_out_of_network) when nothing matches — fail SAFE,
  never "covered by accident".
- Emit NetworkTierCreated/Updated/Retired, ProviderTierAssigned/Revoked via the outbox; audit all.

SCHEMA B — policy-service: tier-aware cost share
- benefit_rule: REMOVE the free-text network_tier column and the copay_* columns from the rule itself;
  keep covered/limit/reset/deductible/waiting/pre-auth/exclusions at the rule level.
- benefit_rule_tier: rule_tier_id PK, benefit_rule_id FK, network_tier_id (VALUE, not cross-service FK
  — per the cross-service rule), is_covered bool, copay_fixed numeric(14,2) NULL,
  copay_percent numeric(5,2) NULL, coinsurance_percent numeric(5,2) NULL,
  requires_preauth_override bool NULL, limit_multiplier numeric(5,2) NULL.
  UNIQUE(benefit_rule_id, network_tier_id). Validation at plan-version ACTIVATION: every Active tier
  must have a row for every covered category (or an explicit "not covered at this tier" row) — an
  unconfigured tier is a validation error, not a silent default.

CONSUMPTION (wire it, don't just store it)
- eligibility-service: the check accepts optional providerId/locationId; when present, resolve the tier
  (cache the tier resolution briefly — but key the cache on providerId+locationId+serviceDate, and
  remember the phase-18 X9 lesson: NEVER key a cache on fewer dimensions than the decision depends on)
  and return the applicable cost-share preview + requires_preauth for that tier.
- approvals-service: pre-auth triggers may be overridden per tier (requires_preauth_override).
- claims-service: adjudication step 6 (provider network status) resolves the tier AT THE SERVICE DATE
  and uses benefit_rule_tier for the payable/co-pay split; an unassigned/OON provider yields
  PROVIDER_OUT_OF_NETWORK or the OON cost-share, per configuration.

ACCEPTANCE (Given/When/Then)
- Given a provider in T1 and a location of that provider assigned to T2, When resolving for that
  location, Then T2 wins (most-specific).
- Given a provider whose tier changes on 1 Mar, When resolving for 15 Feb, Then the old tier; for
  15 Mar, Then the new one — and an ALREADY-ADJUDICATED February service is unaffected.
- Given a policy admin (not Network Team), When they attempt to create or reassign a tier, Then 403.
- Given a plan version activation where an Active tier has no row for a covered category, Then
  validation fails with a clear message.
- Given an eligibility check with an OON provider, Then the response shows the OON cost-share and any
  pre-auth requirement.
TESTS: most-specific-wins matrix, effective-date boundary (both directions), overlap exclusion,
authz separation (Network Team vs policy admin), activation validation completeness, eligibility
cost-share preview, claims tier-at-service-date adjudication.
```

### 19.2 — Policy, groups, enrollment (+ coverage generation)
```text
Read ../38 §3–§4.2 and ../23 policy/coverage lifecycles. Extend the existing Policy entity; do not
break the eligibility engine's reads.

SCHEMA
- policy (extend): add payer_id FK, previous_policy_id NULL (renewal chain), max_members int NULL.
  Keep policy_no, effective dates, status. NOTE: the policy does NOT carry a plan_version_id — plans
  hang off it via policy_plan (19.2b).
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

### 19.2b — Plans under a policy (`policy_plan`) + member plan election
```text
A policy offers ONE OR MORE plans; each member is elected onto exactly one. Read ../38 §3
(policy_plan, enrollment) and §7 invariants 1/1c first. This changes 19.2's shape — do 19.2 and 19.2b
in the same migration wave so enrollment is never written without a plan.

SCHEMA (policy schema, additive)
- policy_plan: policy_plan_id PK, policy_id FK, plan_version_id FK, plan_label varchar(80)
  (e.g. 'Standard','Oncology','Staff','Dependents'), effective_from date NOT NULL, effective_to NULL,
  is_default bool NOT NULL DEFAULT false, eligibility_rule jsonb NULL (declarative criteria:
  group_id in [...], relationship in [...], age band, branch — evaluated by the rules engine, NOT
  hard-coded), max_members int NULL, status CHECK IN ('Active','Closed'), audit cols.
  CONSTRAINTS: UNIQUE(policy_id, plan_label) where not deleted; partial UNIQUE (policy_id)
  WHERE is_default AND status='Active' — at most one default per policy; no overlapping active window
  for the same (policy_id, plan_version_id).
- enrollment (from 19.2): REPLACE any direct plan reference with policy_plan_id FK NOT NULL.

BEHAVIOUR
- POST /policies/{id}/plans — attach a plan version with label/window/default/eligibility rule.
  Reject if the plan version is not Active, or its window does not overlap the policy's window.
- GET /policies/{id}/plans — list with member counts per plan.
- Enrollment (19.2) now REQUIRES a policy_plan: either supplied explicitly or resolved from the
  policy's default; if an eligibility_rule exists it MUST be satisfied (else 422 with the failing
  criterion named). Coverage + limits generate from THAT policy_plan's plan_version.
- POST /enrollments/{id}/change-plan {policyPlanId, effectiveDate, reason} — reason MANDATORY.
  Writes an enrollment_event('PlanChanged'), NEVER an edit. On the effective date the member's coverage
  regenerates from the new plan version; consumption already accrued is PRESERVED (carry the
  accumulator forward per benefit category — do NOT reset a member's used amounts by moving plan;
  where the new plan has a different limit, remaining = new_limit − already_consumed, floored at 0).
  Document this rule in the ADR — it is a benefit-policy decision, and the sponsor must confirm it.
- Renewal (19.2) maps each member to the equivalent plan on the new policy by plan_label, reporting
  any unmapped members instead of silently defaulting.

ACCEPTANCE (Given/When/Then)
- Given a policy with plans 'Standard' (default) and 'Oncology', When a member enrols with no plan
  specified, Then they land on 'Standard'.
- Given an eligibility_rule restricting 'Oncology' to the Oncology group, When a non-group member is
  enrolled onto it, Then 422 naming the failing criterion.
- Given a second default plan is set, Then the partial unique index rejects it.
- Given a member on 'Standard' with 300 consumed of a 1000 Lab limit, When they change to a plan with
  a 500 Lab limit effective today, Then remaining = 200 (not 500), an enrollment_event records the
  change with its reason, and coverage rows point at the new plan version.
- Given renewal, Then members map by plan_label and unmapped members are REPORTED, not defaulted.
TESTS: default resolution, eligibility-rule enforcement, one-default constraint, plan-change event +
consumption carry-forward arithmetic, coverage regeneration provenance, renewal mapping report,
no-overlapping-window constraint.
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

### 19.3b — Documents on policy & member (classified · timestamped · signed)
```text
Attachments for policy paperwork AND member past-medical-history. Read ../38 §5 (the notes rules —
documents follow the same signature/visibility discipline), ../37 §6 (sensitive clinical material) and
../20 (retention) first.

REUSE, DO NOT REBUILD: services/document already owns the upload pipeline — MIME/size validation,
**ClamAV fail-closed scan**, checksum_sha256, MinIO storage, versioning. This sub-prompt adds the
POLICY/MEMBER LINKAGE + CLASSIFICATION on top; it must NOT introduce a second blob store or a second
scanner.

SCHEMA (policy schema — metadata + linkage; bytes stay in document-service/MinIO)
- policy_document: link_id PK, scope CHECK IN ('Policy','Member'), scope_ref uuid NOT NULL
  (policy_id | enrollment_id), document_id uuid (document-service ref), version_no int NOT NULL,
  supersedes_link_id uuid NULL,
  document_class CHECK IN (
    -- Policy scope
    'PolicyContract','BenefitSchedule','PayerAgreement','Endorsement','FinancialGuarantee',
    'PolicyCorrespondence',
    -- Member scope
    'IdentityDocument','ProofOfEligibility','EnrolmentForm','ConsentForm',
    'PastMedicalHistory','MedicalReport','LabResult','Prescription','DischargeSummary',
    'Referral','InvoiceReceipt','MemberCorrespondence','Other'),
  visibility_class CHECK IN ('Administrative','Financial','Clinical','Restricted'),
  title varchar(200), description text NULL,
  **document_date date NULL**            -- the date ON the document (e.g. when the report was issued)
  issuing_provider varchar(200) NULL,     -- who produced it (hospital/lab/authority)
  -- signature (snapshot, same discipline as notes)
  uploaded_by_user_id uuid NOT NULL, uploaded_by_username varchar(128) NOT NULL,
  uploaded_by_display varchar(200) NOT NULL, uploaded_at timestamptz NOT NULL,
  -- lifecycle
  status CHECK IN ('Active','Superseded','Withdrawn') NOT NULL DEFAULT 'Active',
  withdrawn_by_user_id/username NULL, withdrawn_at timestamptz NULL, withdrawal_reason text NULL,
  expires_on date NULL,                   -- ID cards, consents
  verified_by_user_id/username NULL, verified_at timestamptz NULL, verification_note text NULL,
  tenant_id + audit cols.
  Indexes (scope, scope_ref, uploaded_at DESC), (document_class), (status), (expires_on).
  CHECK: status='Withdrawn' REQUIRES withdrawn_by + withdrawn_at + withdrawal_reason.

CLASSIFICATION RULES (this is the point of the feature — get it right)
- **document_class drives a DEFAULT visibility_class**, which the uploader may raise but NEVER lower:
  PastMedicalHistory / MedicalReport / LabResult / Prescription / DischargeSummary / Referral →
  **Clinical**; anything mental-health, HIV/STI, genetic, substance-use, reproductive or GBV-related →
  **Restricted** (../37 §5 categories); InvoiceReceipt → Financial; everything else → Administrative.
- **document_date is distinct from uploaded_at** and matters clinically: past medical history is
  ordered by document_date, not upload order. Both are captured; the UI shows both.
- Re-upload of the same logical document creates a NEW VERSION (version_no+1, supersedes_link_id set);
  the prior version becomes Superseded and is NEVER deleted.
- Withdrawal (wrong member, wrong document) requires a MANDATORY reason, keeps the row and the bytes,
  and marks it Withdrawn — visible with the four-cue status treatment, never hidden.

ACCESS (min-necessary — the hard part)
- LIST is metadata-only and projected by role: everyone entitled to the record sees that a document
  EXISTS (class, title, document_date, uploader, status) — but **Clinical bodies are not downloadable**
  by Finance, Claims, Reception or Call Centre.
- **DOWNLOAD is a separate, scoped, always-audited action.** Serve via a short-TTL signed URL minted
  per request; never a permanent link. Every download writes an audit event with document_id, class,
  actor and purpose.
- **Restricted** documents follow the ../37 §6 pattern exactly: existence-only for everyone except the
  authoring/ordering clinician, released only through the existing report-access request/grant flow —
  do NOT invent a parallel mechanism.
- Uploading a Clinical/Restricted document requires a clinical or beneficiary-management scope; a
  Finance user may upload InvoiceReceipt but not PastMedicalHistory (authz-tested).

API (scopes document:read / document:write / policy:read / policy:write)
- POST /policies/{id}/documents , POST /enrollments/{id}/documents  (multipart; Idempotency-Key)
  {documentClass, title, description?, documentDate?, issuingProvider?, visibilityClass?, expiresOn?}
- GET  /policies/{id}/documents , GET /enrollments/{id}/documents  (?class=&status=&from=&to=,
  ordered by document_date DESC then uploaded_at DESC)
- GET  /documents/{linkId}/download   → signed short-TTL URL + audit
- POST /documents/{linkId}/withdraw {reason} ; POST /documents/{linkId}/verify {note}
- Emit DocumentAttached / DocumentSuperseded / DocumentWithdrawn / DocumentVerified via the outbox.
- An expiry sweep flags documents past expires_on and notifies the owning team.
- OCR seam: reuse the ../13 IDocumentOcrProvider interface for future extraction — wire the hook, do
  NOT implement OCR here.

ACCEPTANCE (Given/When/Then)
- Given a PastMedicalHistory upload, Then visibility defaults to Clinical, the uploader's username +
  UTC timestamp are stored, and document_date is captured separately from uploaded_at.
- Given an uploader tries to set visibility LOWER than the class default, Then 422.
- Given a Finance or Call Centre user lists a member's documents, Then clinical entries appear as
  metadata only and the download endpoint returns 403 + audit.
- Given a Restricted document, Then it is existence-only until a ../37 §6 grant exists.
- Given an infected file, Then the upload fails at scanning and nothing is linked.
- Given a re-upload, Then a new version exists and the prior one is Superseded, not deleted.
- Given a withdrawal without a reason, Then 422; with one, the document remains visible as Withdrawn.
- Given any download, Then an audit event records who, what, when.
TESTS: class→visibility default matrix + no-lowering rule, ClamAV fail-closed, versioning/supersede,
withdrawal authz + mandatory reason, download authz matrix per role (reflection over the payload +
403 on the download route), Restricted grant integration, audit on upload/download/withdraw,
document_date ordering, expiry sweep.
```

### 19.3c — Change timeline & history log (policy + member)
```text
A single chronological "what happened to this policy / this member, when, and who did it" view.
Read ../19-audit-strategy.md and ../38 §5/§7 first.

DESIGN RULE — ONE SOURCE OF TRUTH: the timeline is a **projection over the existing hash-chained
audit_event stream + domain events**, NOT a new hand-maintained log table. A second log would drift
from the audit trail and quietly become a lie. Build a read model; never ask a writer to remember to
append to it.

READ MODEL (policy schema or reporting-service — pick one, justify in the ADR)
- entity_timeline: entry_id PK, scope CHECK IN ('Policy','Member'), scope_ref uuid,
  occurred_at timestamptz NOT NULL, event_type varchar(64), event_category CHECK IN
  ('Lifecycle','Coverage','Plan','Enrolment','Note','Document','Utilization','Authorization',
   'Claim','Access','BulkOperation','Administrative'),
  actor_user_id uuid NULL, actor_username varchar(128) NULL, actor_display varchar(200) NULL,
  summary_en text, summary_ar text,           -- human-readable, bilingual
  change_diff jsonb NULL,                     -- MINIMIZED before/after (see below)
  visibility_class CHECK IN ('Administrative','Financial','Clinical','Restricted'),
  source_service varchar(40), correlation_id varchar(64), source_event_id uuid,
  target_ref uuid NULL, target_kind varchar(40) NULL,   -- deep-link target
  tenant_id. UNIQUE(source_event_id) — idempotent projection.
  Index (scope, scope_ref, occurred_at DESC), (event_category), (actor_user_id).

WHAT LANDS ON THE TIMELINE
Policy: created, plan attached/detached (19.2b), plan version amended/activated, endorsement, renewal,
status change, group created/closed, note added/cancelled (19.3), document attached/superseded/
withdrawn (19.3b), bulk job applied against it, payer change.
Member: enrolled, plan changed, group changed, suspended/reinstated/terminated (with reason),
coverage generated/regenerated, limit reset, **utilization threshold crossed (80% / 100%)**,
waiting period ended, note added/cancelled, document attached/withdrawn, authorization decided,
claim decided, **break-glass or sensitive-release access to their record**, contact/identity updated.

RULES
- **Append-only and immutable** — a timeline entry is never edited or deleted. A correction is a new
  entry referencing the original.
- **Diffs are minimized and class-projected**: `change_diff` stores only the changed fields, and a
  Clinical/Restricted-class entry's diff is withheld from operational roles — they see
  "Clinical record updated" with actor + timestamp, not the values. Reuse FieldProjector; do not
  hand-roll a second redaction path.
- **Actor is a snapshot** (username + display captured at projection time), so history stays readable
  after a user is renamed or de-provisioned — same discipline as notes and documents.
- Times stored UTC, rendered **Africa/Cairo**; grouped by day with relative labels ("Today",
  "Yesterday", then the date).
- **Access events are part of the story**: who VIEWED a restricted document or used break-glass on this
  member belongs on the member timeline (../19) — that is often the most important entry on it.
- The projection is idempotent (UNIQUE source_event_id) and REPLAYABLE from the audit stream, so a
  rebuild produces byte-identical history. Include a rebuild command + test.

API (scopes policy:read / member read as applicable)
- GET /policies/{id}/timeline , GET /enrollments/{id}/timeline
  ?from=&to=&category=&actor=&eventType=  — paginated (cursor by occurred_at), newest-first.
- GET /policies/{id}/timeline/export , /enrollments/{id}/timeline/export — audited, column-allow-listed
  (reuse the 19.5b extract engine; NEVER a clinical diff in an export to a non-clinical role).

FRONTEND (folds into 19.6)
- A vertical timeline component on both the policy and member screens: day groupings, category icon +
  four-cue chip, actor username, Africa/Cairo timestamp, one-line bilingual summary, expandable diff
  (when permitted), and a deep link to the target entity.
- Filter bar: date range, category, actor, event type. Filters URL-encoded (shareable).
- Withheld diffs render as an explicit "details restricted for your role" state — never a blank row.
- Accessible: an ordered list with real semantics (not a div soup), keyboard-navigable, aria-labels
  carrying the full timestamp, axe clean in EN + AR with full RTL mirroring.

ACCEPTANCE (Given/When/Then)
- Given a member is enrolled, has a plan change, a note, a document and a termination, Then the
  timeline shows all five in correct chronological order with the acting username and Cairo times.
- Given a Finance user views a member timeline, Then clinical-class diffs are withheld (summary +
  actor + timestamp remain) and nothing clinical appears in the payload.
- Given the projection is replayed from the audit stream, Then the timeline is identical (idempotent,
  no duplicates).
- Given someone uses break-glass on a member, Then that access appears on the member's timeline.
- Given a renamed/disabled user, Then their past entries still show the original username.
TESTS: projection idempotency + replay equality, ordering across sources, class-based diff withholding
(reflection over the payload), actor snapshot survival, filter correctness, export allow-list, deep-link
targets resolve, axe + keyboard on the timeline component.
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
- GET /utilization/groups/{groupId}, /utilization/plans/{policyPlanId}, /utilization/policies/{policyId},
  /utilization/payers/{payerId} → aggregate totals + per-member table + distribution buckets + outliers
  (> X% of limit, configurable). **Per-plan utilization matters**: a policy with several plans must be
  comparable plan-by-plan (which plan is consuming disproportionately).
- Add a **network-tier split** to every scope: consumption and cost-share by tier (in-network vs OON),
  since that is the primary lever the Network Team and Finance act on.
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
- GET /policy-query: filter by payer, plan, **plan label**, status, effective window, group,
  member-count band, utilization band; sortable, paginated (page+pageSize with an explicit allow-list
  of sort fields), audited export.
- GET /member-query: filter by identifier (any type), name, member_no, policy, **policy_plan**, group,
  relationship, status, branch, enrollment window, waiting-period state, utilization band. Same
  pagination/sort/export.
- GET /enrollments/{id}/coverage-details → **the member's plan (label) + plan version in force**, every
  benefit category with covered/limit/consumed/remaining/reset/waiting-period status/pre-auth
  requirement/exclusions, **plus the per-network-tier cost-share grid** (what the member pays in T1 vs
  T2 vs OON), plus the effective-dated change history including plan changes.
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

### 19.5b — Bulk upload & data extract engine
```text
One engine for getting data IN (bulk upload) and OUT (extracts), reusing the 19.5 filter model.
Read ../38 §4.4, the EXISTING tools/migration toolkit (staging→validate→map→load→reconcile,
batch_id provenance, rollback-by-batch, dedupe) and services/document (ClamAV scan, MinIO) first —
REUSE those patterns, do not invent a second importer.

=== A. BULK UPLOAD ===
JOB TYPES (each with a downloadable template): MemberEnrolment, MemberTermination, PlanChange,
GroupAssignment, ContactUpdate, ProviderTierAssignment (19.1b), BenefitRuleImport (populates a DRAFT
plan version only — never an Active one).

SCHEMA (policy schema; ProviderTierAssignment mirrors it in provider schema)
- bulk_job: job_id PK, job_type CHECK IN (...), file_name, file_document_id (document-service ref),
  status CHECK IN ('Uploaded','Scanning','Validating','Validated','Committing','Completed',
  'Failed','RolledBack'), total_rows, valid_rows, invalid_rows, applied_rows, batch_id,
  submitted_by_user_id/username, submitted_at, completed_at, tenant_id + audit cols.
- bulk_job_row (append-only): row_id PK, job_id FK, row_number int, raw jsonb, normalized jsonb,
  status CHECK IN ('Valid','Invalid','Applied','Skipped','Failed'), error_code, error_detail,
  target_ref uuid NULL (the created/updated entity). Index (job_id, status), (job_id, row_number).

PIPELINE (async job; NEVER blocks the request thread)
1. Upload → validate MIME/size → **ClamAV scan (fail closed)** → store in MinIO via document-service.
2. Parse (CSV + XLSX) with an explicit column contract from the template; unknown/missing columns fail
   the whole job with a clear message (do not guess).
3. VALIDATE every row independently: required fields, formats, referential existence (beneficiary,
   policy, policy_plan, group, tier, provider), business rules (no overlapping enrolment, plan
   eligibility_rule satisfied, effective dates within the policy/plan window, waiting period sane).
   Row errors carry row_number + machine error_code + human detail in BOTH locales.
4. DRY-RUN PREVIEW: return counts + the first N errors + a per-row diff of what WOULD change.
   A job is only committable from status Validated.
5. COMMIT: apply rows in ONE transaction per row (never one giant transaction — a 50k-row job must not
   hold a single lock), each with an Idempotency-Key derived from (job_id, row_number) so a resumed or
   retried job cannot double-apply. Partial failure marks the row Failed and CONTINUES; the job ends
   Completed-with-errors, never half-committed-and-silent.
6. RECONCILE: report submitted vs valid vs applied vs failed, downloadable; emit BulkJobCompleted.
7. ROLLBACK-BY-BATCH: reverse every Applied row of a job (soft-delete/compensating event), audited,
   available while the data is still reversible (guard: refuse if any downstream consumption exists —
   e.g. a member has already consumed benefit — and say so).

GUARDRAILS
- Row-level errors may reference identifiers → the error file is PHI-bearing: store it in MinIO,
  serve it only through an authorized, audited download, never inline in a log.
- Bulk operations are scope-checked per row (payer scope, branch scope) — a bulk file cannot be used
  to reach outside the submitter's scope.
- Every applied row writes an audit event carrying job_id + row_number (traceability from a member
  record back to the upload that created it).

=== B. DATA EXTRACT ===
- extract_definition: definition_id, name, description, entity CHECK IN ('Members','Policies','Plans',
  'Coverage','Utilization','NetworkTiers'), filter jsonb, columns jsonb (allow-listed), format
  CHECK IN ('CSV','XLSX','JSON'), owner_user_id, is_shared, schedule_cron NULL, tenant_id.
- extract_run: run_id, definition_id FK, requested_by, filter_snapshot jsonb (WHAT WAS ACTUALLY RUN),
  row_count, file_document_id, status, started_at, completed_at.
- FILTERS (shared vocabulary with 19.5 — implement ONCE and reuse): payer, policy, policy_plan /
  plan label, plan_version, **effective/as-of date**, group, network tier, branch, member status,
  relationship, enrollment window, waiting-period state, benefit category, utilization band.
- **AS-OF EXTRACTION is required**: "the member list as it stood on 1 March" — reconstruct from
  effective dating + enrollment_event, NOT from current rows. Test it against a member who changed
  plan and another who was terminated between the as-of date and today.
- COLUMN ALLOW-LIST PER ROLE: the requested column set is intersected with what the caller's role may
  see (FieldProjector classes). NO clinical columns in any policy/member extract, ever. Finance
  extracts carry no diagnosis. Silently dropping a column is not acceptable — return which columns
  were withheld and why.
- Large extracts run async → file in MinIO → **signed, short-TTL, audited download**; small ones stream.
- Every run writes an audit event with the filter snapshot, row count and column set.
- Scheduled extracts run under a service principal with an explicit scope, not the creator's ambient rights.

ACCEPTANCE (Given/When/Then)
- Given a 10k-row enrolment file with 37 invalid rows, When validated, Then the job reports 9,963 valid
  / 37 invalid with row numbers and bilingual reasons, and NOTHING is applied until commit.
- Given commit is run twice (same job), Then no row is applied twice (idempotency by job+row).
- Given a row that would breach the no-overlap or plan-eligibility rule, Then that row fails and the
  rest still apply.
- Given rollback of a job where one member has already consumed benefit, Then rollback is refused for
  that row with a clear reason and succeeds for the others.
- Given an infected file, Then the job fails at Scanning and nothing is parsed.
- Given an as-of extract for 1 March, Then a member terminated on 15 March still appears, and a member
  who changed plan on 10 March shows their 1-March plan.
- Given a Finance user requests a member extract with a clinical column, Then it is withheld and named.
TESTS: parser contract, per-row validation matrix, dry-run diff, idempotent commit, partial-failure
continuation, rollback guard, ClamAV fail-closed, as-of reconstruction (plan change + termination),
column allow-list per role, audit on apply + extract run, 50k-row performance smoke.
```

### 19.6 — Frontend: Beneficiary Management portal (+ Policy Administrator)
```text
Read ../38 §4/§6, ../0B (incl. §10b v1.1), ../14-navigation-structure.md, ../21, and the EXISTING
apps/web/src/portals/catalog.ts + screens/registry.tsx. Follow the PortalDef/Section shape exactly;
add the new permissions and gate every section.

SCREENS
- Policy Administration (new policy_admin role): payers list/detail; plans list; PLAN VERSION EDITOR —
  a **two-level grid**: rows = benefit category (covered / limit / reset / waiting / pre-auth /
  exclusions), and an expandable **cost-share matrix per category × network tier** (covered, co-pay,
  co-insurance) with tier columns rendered from the Active tiers. Clear Draft vs Active-immutable
  state, Validate + Activate actions, amend→new-version flow with a version timeline and a version diff.
- Policies: list + detail (payer, dates, **PLANS TAB listing the policy's plans with label, version,
  window, default badge, member count and per-plan utilization**, attach-plan action), groups, member
  count, utilization summary, notes panel; issue / amend / renew.
- Network administration (Network Team portal, extends the existing provider/network screens):
  network tiers list + editor (code, name, rank, OON flag), and a tier-assignment screen for
  providers / locations / contract lines with effective dates, showing most-specific-wins resolution
  and a "resolve tier at date" preview tool. Policy admins see tiers READ-ONLY.
- Members: member query results table; member detail = identity + enrollment history + **current plan
  (label + version)** + coverage details **incl. the per-tier cost-share grid** + utilization + notes
  panel + **documents panel (19.3b: classified list, upload, versioned, audited download)** +
  **change timeline (19.3c)**; enrol / terminate / reinstate / change-group / **change-plan** dialogs (mandatory reason on
  terminate and on change-plan, with a preview of how remaining limits carry forward); bulk-enrol
  upload with the reconciliation report.
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

### 19.6b — Analytical dashboard (multi-view, filtered, drillable)
```text
The analytical layer over everything 19.1–19.5b produced. Read ../38 §4.3, ../0B (incl. §10b v1.1),
../21, the EXISTING reporting-service (/dashboards/executive, KPI read models) and
apps/web ExecutiveDashboard.tsx first — EXTEND the reporting read-model pattern; do not query PHI
tables live from the dashboard.

READ MODELS (reporting-service; refreshed from domain events, dedupe on event id)
Pre-aggregate by the filter dimensions so a dashboard query never scans transactional PHI tables:
fact_enrolment (daily snapshot: active/new/terminated by payer, policy, policy_plan, group, branch,
relationship, status), fact_utilization (consumed/limit/remaining by member, benefit category,
network tier, plan, period), fact_cost (claimed/approved/adjusted/net by payer, plan, tier, category —
sourced from claims), dim_* for the labels. Every fact carries tenant_id and is RLS-protected.

VIEWS (tabs; each a distinct question a real administrator asks)
1. ENROLMENT — membership over time; new vs terminated (churn); split by payer / plan / group /
   branch / relationship; dependents-per-principal; waiting-period population.
2. UTILIZATION — consumed vs limit by benefit category; % of limit distribution; members crossing
   80/100% thresholds; per-plan comparison; **in-network vs OON split** (19.1b).
3. FINANCIAL — claimed / approved / adjusted / net payable by payer, plan, tier, category; cost per
   active member per month; top cost drivers. **No diagnoses anywhere in this view.**
4. NETWORK — tier mix of delivered services, OON leakage rate and its cost, top providers by volume
   and value, tier coverage gaps by branch.
5. PLAN COMPARISON — two or more plans side by side on enrolment, utilization %, cost/member,
   OON rate, approval rate — the view that answers "is Plan B actually cheaper?".
6. OUTLIERS & DATA QUALITY — members > X% of limit, members with no utilization, enrolments missing a
   group/plan, expiring policies/plan versions in the next N days, failed bulk rows awaiting fix.

FILTERS (one shared filter bar; the SAME vocabulary as 19.5/19.5b)
payer · policy · plan (policy_plan / label) · group · branch · network tier · benefit category ·
member status · relationship · **date range + as-of date** · utilization band.
- Filters are URL-encoded (shareable/bookmarkable), persisted per user, and cleared with one action.
- Every view honours payer scope and branch scope server-side — a payer-scoped user's dashboard cannot
  aggregate another payer's data (assert with a test, not a UI filter).

INTERACTION
- Drill-down: chart segment → filtered table → (permission-gated) member or policy detail, carrying
  the active filters through. The member step is a PHI read → audited.
- Compare mode: period-over-period (this month vs last, YTD vs prior YTD) with delta chips using the
  four-cue status treatment (never colour alone).
- Export: audited CSV/XLSX of the *currently filtered* dataset, reusing the 19.5b extract engine and
  its column allow-list (so the dashboard cannot become a PHI side-channel).

ACCESSIBILITY & PERFORMANCE (R2 audit findings U6/U10 apply directly)
- **Every chart renders an accessible data table ALWAYS** (sr-only wrapper, not behind a default-off
  toggle) plus a one-line text summary; charts are not aria-hidden.
- Series use pattern + direct label, never colour alone; full AR/RTL mirroring incl. axis direction
  and Arabic-Indic numeral option; keyboard-navigable filter bar and drill-down; axe clean in EN + AR.
- Dashboard p95 < 3s on the seeded volume; if a view cannot meet it, pre-aggregate further — do not
  ship a slow live query.

ACCEPTANCE
- Given a payer-scoped user, Then every view aggregates only their payer (server-enforced).
- Given a Finance user, Then no view or export contains a diagnosis or clinical field.
- Given filters set and shared via URL, Then the recipient (with rights) sees the same view.
- Given a drill-down to a member, Then the PHI read is audited.
- Given axe + keyboard + screen-reader checks in EN and AR, Then all pass and every chart has its data
  table present in the DOM.
TESTS: read-model projection correctness (reconcile to the accumulator, as 19.4), payer/branch scope
enforcement per view, filter round-trip via URL, drill-down audit, export column allow-list,
data-table presence assertion per chart, p95 performance smoke.
```

### 19.7 — Roles, docs, migration & wiring
```text
- ROLES: add policy_admin (+ a policy:supervise scope for note cancellation and retro-effective
  changes) to identity's role/scope seed (0001_identity.sql), libs/authz PolicyPolicies bundle,
  apps/web permissions + ROLE_MAP + portal catalog. Update ../10-role-matrix.md and
  ../11-permission-matrix.md (new resources: payer, plan, plan_version, benefit_rule, member_group,
  enrollment, note; hard rules: Finance/Call-Centre never receive Clinical/Restricted note bodies;
  payer scope; note append-only).
- KONG: routes for /api/v1/{payers,plans,plan-versions,policies,policy-plans,member-groups,enrollments,
  notes,documents,timeline,utilization,policy-query,member-query,bulk-jobs,extracts} ,
  /api/v1/network-tiers (provider-service) and the new /api/v1/dashboards/* views
  (reporting-service). Verify with the route-coverage guard (phase 18 E1).
- DATA MIGRATION: backfill existing policies → create a default payer ("Mersal — self-funded"), a plan
  version reverse-engineered from current coverage rows, **a single default `policy_plan` per policy**,
  and **a default network tier set (`T1` in-network + `OON`) with every existing contracted provider
  assigned to T1** from their contract start date; generate enrollments (pointing at the default
  policy_plan) from existing coverage; write a reconciliation report. Reversible by batch (reuse
  tools/migration).
- DOCS: update ../22 (new tables/enums incl. network_tier, provider_network_assignment,
  benefit_rule_tier, policy_plan), ../23 (plan-version + enrollment + plan-change + note lifecycles),
  ../07 (FR-POL-* / FR-NET-* / FR-NOTE-*), ../16 (policy-service remit + provider-service gains
  network tiers), ../14 (portal nav incl. the Network Team tier screens), 00-README-INDEX + README
  (doc 38), BUILD-STATUS (19.1–19.7 incl. 19.1b/19.2b). ADRs: 0017 "Effective-dated immutable plan
  versions", 0018 "Append-only signed notes", **0019 "Network tiers owned by network administration;
  cost-share resolved per tier at service date"**, **0020 "Multiple plans under a policy; consumption
  carries forward on plan change"** (the carry-forward rule needs sponsor confirmation — record it),
  **0021 "Document classification drives visibility; class default may be raised, never lowered"**,
  **0022 "Entity timeline is a replayable projection over the audit stream, not a second log"**.
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
- [ ] Payer → plan → **effective-dated immutable plan version** with full benefit configuration **and a per-network-tier cost-share grid**; amend/renew flows work; the resolver returns the version in force on a given **service date** (boundary-tested).
- [ ] **Network tiers** are created and assigned by the **Network Team** (policy admins get 403 on writes), assignment is effective-dated with **most-specific-wins** (contract line > location > provider), resolution is boundary-tested, and an already-adjudicated service is unaffected by a later tier move. Eligibility returns a tier-aware cost-share preview; claims adjudicate on the tier **at the service date**.
- [ ] **A policy carries multiple plans** via `policy_plan` (label, window, one default, eligibility rule); a member is elected onto exactly one; `change-plan` is an **event with a mandatory reason** and **consumption carries forward** (remaining = new limit − already consumed, floored at 0); renewal maps by plan label and **reports** unmapped members.
- [ ] Policies carry a payer; groups exist; members enrol (with dependents, waiting periods, bulk import), terminate with mandatory reason, reinstate, change group — with **coverage generated** from the elected plan's version and no overlapping active enrollment.
- [ ] **Notes on policy and member**: timestamped (UTC, rendered Africa/Cairo), **signed with username** (snapshot), status **Active/Cancelled** with mandatory cancellation reason, **append-only** (body never edited or deleted), cancelled notes still visible, class-projected so Finance/Call Centre never receive clinical bodies, fully audited.
- [ ] **Documents** on policy and member: classified (policy paperwork + **past medical history**), with **document_date distinct from upload date**, uploader username + UTC timestamp, class-driven visibility that can be raised but never lowered, versioning on re-upload, withdrawal with mandatory reason, ClamAV fail-closed, **download as a separate always-audited action behind a short-TTL signed URL**, and Restricted documents gated through the existing §37 grant flow.
- [ ] **Change timeline** on policy and member: a replayable, idempotent, append-only **projection over the audit stream** (never a second log), bilingual summaries, actor username snapshot, Africa/Cairo times, class-projected diffs (clinical values withheld from operational roles), access events included, filterable and audited on export.
- [ ] Utilization for **individual, group, plan, policy and payer**, with an **in-network vs OON tier split**, reconciling exactly to the accumulator, with accessible data-table alternatives and audited exports.
- [ ] **Policy query** and **member query** with the full criteria set, pagination, sort, audited export, payer + branch scope, role projection.
- [ ] **Bulk upload**: templated CSV/XLSX for enrolment, termination, plan change, group assignment, contact update and provider tier assignment — malware-scanned, row-validated with bilingual errors, **dry-run preview**, idempotent commit (no double-apply), partial-failure continuation, reconciliation report, and rollback-by-batch that refuses rows with downstream consumption.
- [ ] **Data extract** on the shared filter vocabulary (payer, policy, plan, effective/**as-of date**, group, tier, branch, status…), with per-role column allow-list (withheld columns named, never silently dropped), async large runs to signed short-TTL downloads, saved + scheduled definitions, and a full audit of every run's filter snapshot.
- [ ] **Analytical dashboard** with six views (Enrolment, Utilization, Financial, Network, Plan comparison, Outliers & data quality), one shared URL-encoded filter bar, drill-down to filtered tables and audited member detail, period-over-period compare, and exports that reuse the extract allow-list — payer/branch scope enforced server-side per view.
- [ ] Every dashboard chart ships an **always-rendered accessible data table** + text summary, pattern-not-colour series, AR/RTL parity, axe clean, p95 < 3s.
- [ ] Full coverage details (version-in-force correct) and the administrative beneficiary 360.
- [ ] Portal shipped for Beneficiary Management + the new Policy Administrator role, bilingual, WCAG 2.2 AA, no silent write failures.
- [ ] Authorization tests prove payer-scope isolation and note-class projection; docs, ADRs and BUILD-STATUS updated.

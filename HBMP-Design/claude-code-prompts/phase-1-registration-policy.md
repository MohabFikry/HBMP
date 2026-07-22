# Phase 1 — Registration, Identifiers, Documents & Policy/Coverage

**Goal:** Stand up `patient-service`, `policy-service`, and the `document-service` integration, then wire a registration workflow that takes a beneficiary from **Pending → Active**, issues a Member No, and emits `BeneficiaryActivated` so an eligibility snapshot can be built. (Release **R1**)

Back to [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md).

---

## Skills to activate
> Activate `beneficiary-lifecycle-management`, `policy-eligibility-engine`, `healthcare-database-architect` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

Open these before writing any code. If reality diverges from a doc, flag it in the PR — do not silently deviate.

- `../01-product-vision.md` — who the beneficiaries are (refugees with heterogeneous documents) and why any-identifier enrolment matters.
- `../04-patient-journey-maps.md` — the registration → approval → activation journey and hand-offs.
- `../07-functional-requirements.md` — R1 functional scope for registration, documents, policy.
- `../15-database-erd.md` §4 (Patient/Beneficiary), §5 (Policy/Coverage), §12 (Documents) — tables, keys, soft-delete/history.
- `../22-data-dictionary.md` — column types, nullability, PII/PHI classification, enum values.
- `../23-state-machines.md` §1 (Beneficiary/Member lifecycle) — legal transitions, guards, emitted events.
- `../17-api-specifications.md` §4 (Beneficiaries) — endpoint shapes, idempotency, ETag/concurrency.
- `../32-user-stories.md` — **US-001 … US-004** with Given/When/Then acceptance criteria.

Root `CLAUDE.md` already governs stack, naming, security, audit, a11y, testing, and Definition of Done — this file only adds phase scope, acceptance criteria, guardrails, and exit criteria.

---

## Prompts

### 1.1 — `patient-service`: beneficiary, identifiers, contacts, family/dependents

```text
Implement the patient-service (.NET 8) owning PostgreSQL schema `patient` with schema-per-service + RLS,
following ../15-database-erd.md §4 and ../22-data-dictionary.md.

Scope:
- Tables: beneficiary, beneficiary_identifier, contact, family_group, dependent_link, beneficiary_history.
  Use UUID v7 PKs, the standard audit columns (created_at/by, updated_at/by, row_version, is_deleted,
  deleted_at/by), and _history twin populated by trigger/outbox (never by app logic).
- beneficiary.status is TEXT with CHECK for exactly: Pending|Active|Suspended|Expired|Blocked|Inactive.
  New beneficiaries are created Pending (activation happens in prompt 1.4, not here).
- beneficiary_identifier.identifier_type ∈ {NationalID, Passport, RefugeeID, UNHCRNo, MemberNo}, each with
  identifier_value, issuing_country, valid_from/to, is_primary.
- contact.contact_type ∈ {Phone, Email, Address, EmergencyContact} with preferred_channel + is_primary.
- family_group + dependent_link model guardianship many-to-many (relationship ∈ {Child,Spouse,Parent,Other})
  per ../15-database-erd.md §4 notes — a person may be a dependent in one link and a guardian in another.

Duplicate detection:
- Enforce UNIQUE (identifier_type, identifier_value) as a PARTIAL index WHERE is_deleted = false.
- On create/add-identifier, run a pre-check: if an active identifier already exists, return RFC 7807
  409 problem `duplicate-identifier` with the existing beneficiaryId so the caller can open/merge instead
  of duplicating (US-001). Do NOT create a second row.

API (../17-api-specifications.md §4): POST /api/v1/beneficiaries (Idempotency-Key required, 201 + ETag),
GET /api/v1/beneficiaries (search by identifierType+identifierValue, name, status; cursor pagination),
GET/PATCH /api/v1/beneficiaries/{id} (PATCH uses If-Match → 412 on mismatch),
POST /api/v1/beneficiaries/{id}/identifiers. Return only min-necessary fields per caller role.

Acceptance (US-001):
- Given a new beneficiary, When I submit any one supported identifier with valid format, Then it is stored
  with its type and issuing authority and the record is created in status Pending.
- Given an identifier value that already exists (active), When I submit, Then I get a 409 duplicate warning
  naming the existing record — no duplicate row is written.
- Given required personal/contact fields are missing, When I submit, Then a 400 problem+json lists each
  missing field.

Tests: unit (identifier format validation, dedup), integration (duplicate blocked at DB + API), a concurrency
test proving two parallel creates of the same identifier cannot both succeed. Audit every mutation via
libs/audit-client. Emit `BeneficiaryRegistered` via outbox. Update the OpenAPI spec and the service README.
```

### 1.2 — `policy-service`: policy, coverage, coverage limits

```text
Implement the policy-service (.NET 8) owning PostgreSQL schema `policy`, per ../15-database-erd.md §5 and
../22-data-dictionary.md.

Scope:
- Tables: policy, coverage, benefit_category, coverage_limit (+ _history twins, standard audit columns).
- policy(policy_no UK, sponsor, effective_from/to, status ∈ {Active,Suspended,Expired}).
- benefit_category(code UK ∈ {LAB, IMAGING, PHARMACY, CONSULT, REFERRAL}, name).
- coverage(policy_id FK, beneficiary_id logical-FK, benefit_category_id FK, effective_from/to, status).
- coverage_limit(coverage_id FK, limit_type ∈ {Annual, PerEncounter, Lifetime, Count},
  limit_value NUMERIC(14,3), consumed_value NUMERIC(14,3) (authoritative accumulator, starts 0),
  currency_code, reset_period ∈ {None, Monthly, Quarterly, Yearly}).

Rules:
- Cross-service references (beneficiary_id) are stored as values, NOT enforced FKs across schemas.
- consumed_value is the source-of-truth accumulator for benefit usage; expose it read-only here
  (it is incremented transactionally by the consume/dispense sagas in later phases — do not mutate it here).
- Implement a reset job/spec for reset_period so limits roll over on the boundary (Monthly/Quarterly/Yearly);
  Lifetime and None never reset. Every reset writes a _history row + audit event.
- Publish `PolicyChanged` / `CoverageChanged` / `CoverageLimitChanged` domain events via outbox — phase 2
  consumes these to invalidate eligibility snapshots.

API: CRUD for policies/coverages/limits under /api/v1 (patient.write-class scope for now; refine to a
policy scope). Return min-necessary fields.

Acceptance:
- Given a policy with an Annual limit, When I create coverage for a beneficiary and benefit category, Then a
  coverage_limit is created with remaining = limit_value - consumed_value.
- Given a coverage_limit with reset_period Monthly, When the reset boundary passes, Then consumed_value resets
  to 0 and both a _history row and an audit event are written.
- Given a policy/coverage/limit change, When it commits, Then the corresponding domain event is published via
  the outbox in the same transaction.

Tests: unit (reset math per period/limit type), integration (event emitted on change), audit assertions.
Update OpenAPI + README.
```

### 1.3 — `document-service` integration: validated, malware-scanned upload attached to a beneficiary

```text
Wire document upload into registration using document-service (metadata schema `document`; blobs in Blob
Storage with CMK), per ../15-database-erd.md §12 and US-002.

Scope:
- Tables: document(doc_type ∈ {IDScan, Consent, Referral, LabResult, ImagingReport}, owner_beneficiary_id
  logical-FK, classification ∈ {PHI,PII,Internal}, blob_container, current_version_no, is_deleted) and
  document_version(version_no, blob_path, checksum_sha256, size_bytes, uploaded_at, uploaded_by).
- Upload endpoint validates BEFORE storing: allowed MIME types (pdf, jpeg, png; configurable) and a max size;
  reject disallowed type/oversize with a clear RFC 7807 400 problem naming the reason.
- On accept: compute checksum_sha256, run a malware scan (ClamAV, behind a pluggable scanner interface);
  quarantine + reject on positive. Only clean files are attached.
- On success: create/version the document, attach it to the beneficiary (owner_beneficiary_id), and stamp
  timestamp + uploader. Never store blob bytes in the RDBMS.

Acceptance (US-002):
- Given a file that exceeds size or is a disallowed type, When uploaded, Then it is rejected with a clear reason
  and nothing is persisted.
- Given an accepted file, When uploaded, Then it is malware-scanned, and on clean result attached to the
  beneficiary with timestamp + uploader recorded and an audit event written.
- Given a malware-positive file, When scanned, Then it is quarantined/rejected and the attempt is audited.

Tests: unit (validation matrix), integration (attach + version + checksum), a mocked malware-positive path.
Audit every upload/attach/reject. Update OpenAPI + README.
```

### 1.4 — Registration workflow: wizard-backed API, approval, Member No issuance, activation event

```text
Implement the registration workflow API in patient-service that drives a UI wizard and moves a registration
from Pending to Active, per ../04-patient-journey-maps.md, ../23-state-machines.md §1, and US-003/US-004.

Model a `registration` (application) aggregate distinct from the beneficiary lifecycle:
- registration.status ∈ {Pending, InfoRequested, Rejected, Active}. This is the APPLICATION sub-state; the
  underlying beneficiary.status stays Pending until activation, then becomes Active.
- Steps map to wizard pages: identity+identifiers (1.1) → contacts/family → documents (1.3) → review/submit.
- Endpoints: POST /api/v1/registrations (Idempotency-Key required), PATCH step data (If-Match),
  POST /api/v1/registrations/{id}/submit, and an approval decision endpoint
  POST /api/v1/registrations/{id}/decision { decision: Approve|RequestInfo|Reject, notes }.

Approval + activation (transactional):
- Approve: guard = documents verified AND a policy/coverage is bound. Then set beneficiary.status Active,
  issue a Member No business key `MRS-M-YYYY-NNNNNN` (monotonic per year), write a MemberNo identifier row,
  and emit `BeneficiaryActivated` via outbox → phase-2 eligibility builds the snapshot from it.
- RequestInfo: registration → InfoRequested, returned to the officer with mandatory notes; stays actionable.
- Reject: registration → Rejected; a reason is MANDATORY and recorded.
- Also expose the lifecycle transitions from ../23-state-machines.md §1 (suspend/expire/block/deactivate/
  reinstate/reactivate) with mandatory reasons where the table requires them; each writes history + audit
  and re-emits a status event (US-004). Reject illegal transitions with a 409 audited as TransitionDenied.

Acceptance:
- (US-003) Given a Pending registration, When an approver Approves, Then beneficiary.status becomes Active,
  a Member No is issued, an eligibility snapshot is triggered via BeneficiaryActivated, and all is audited.
- (US-003) Given incomplete info, When the approver chooses Request Info, Then it returns to the officer with
  notes and stays Pending/InfoRequested.
- (US-003) Given ineligibility, When Reject is chosen, Then a reason is mandatory and recorded.
- (US-004) Given an Active member, When suspended/blocked with a reason, Then eligibility reflects the new
  status (event emitted); Given a Suspended member, When reactivated, Then both transitions are in history.

Tests: integration for the full register→submit→approve→activate path (asserts Member No + BeneficiaryActivated),
a state-machine test rejecting illegal transitions, an idempotency test (replayed submit/decision is a no-op),
and audit assertions on every step. Update OpenAPI + README.
```

---

## Guardrails

- **Audit every step.** Register, upload, submit, approve/reject/request-info, and every status transition write an immutable, hash-chained `audit_event` via `libs/audit-client` (before/after minimized, correlation id).
- **Soft-delete + history only.** Never hard-delete beneficiary, policy, coverage, or document rows; `_history` twins are written by trigger/outbox, not app code.
- **Identifiers unique & verified.** `UNIQUE (identifier_type, identifier_value) WHERE is_deleted=false`; a duplicate is blocked with a 409, not silently merged.
- **Cross-schema links are values, not FKs.** `beneficiary_id` in `policy`/`document` is a logical reference kept consistent by events.
- **Idempotency on mutations.** `POST /registrations`, submit, decision, and uploads honor `Idempotency-Key`; replays return the stored result.
- **Minimum-necessary output.** DTOs expose only the fields the caller's role needs; registration/approval roles ≠ EMR.
- **Beneficiary vs registration state.** Keep the beneficiary lifecycle (Pending→Active→…) separate from the registration application sub-state (Pending/InfoRequested/Rejected/Active).

## Done when

- A beneficiary can be **registered with any one supported identifier**, and a duplicate identifier is **blocked with a 409** naming the existing record.
- Documents upload only after **type/size validation + malware scan**, and attach to the beneficiary with checksum, timestamp, and uploader.
- Policies/coverages/limits exist with working **reset periods** and emit change events via the outbox.
- The full path **register → submit → approve → activate** issues a `MRS-M-*` Member No and emits **`BeneficiaryActivated`**, and RequestInfo/Reject behave per US-003 (reason mandatory on reject).
- All mutations are audited, soft-delete/history holds, and unit/integration/concurrency/idempotency tests are green — meeting the root `CLAUDE.md` Definition of Done.

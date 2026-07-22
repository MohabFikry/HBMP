# Phase 7 — Medical Approvals (R4)

**Goal:** Build the `approvals-service` and Medical Approval worklist: review authorization requests with full clinical context (EMR, notes, supporting docs) under minimum-necessary field scoping, decide with **mandatory rationale**, and support break-glass paths (emergency approval, director override, manual authorization) — every decision immutably, hash-chain audited, with SLA/TAT tracking.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

---

## Skills to activate
> Activate `health-insurance-tpa-operations`, `healthcare-business-rules-engine`, `medical-claims-engine` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

Open these before coding:

- [../07-functional-requirements.md](../07-functional-requirements.md) — approval FRs (R4).
- [../11-permission-matrix.md](../11-permission-matrix.md) — §3.2 clinical zone: Medical Approval reads EMR/notes/results under purpose (`PUR`); §4 field-level rules. Finance ≠ diagnosis.
- [../13-ux-flows.md](../13-ux-flows.md) — review + decision flow.
- [../19-audit-strategy.md](../19-audit-strategy.md) — immutable hash-chained audit; break-glass special flagging; decision record fields.
- [../23-state-machines.md](../23-state-machines.md) §5 — **canonical Authorization lifecycle** and transition table (guards, events, actors).
- [../24-sequence-diagrams.md](../24-sequence-diagrams.md) — order/prescription → approval routing.
- [../32-user-stories.md](../32-user-stories.md) — **US-060, US-061, US-062**.
- Prototype: `../prototype-approvals-worklist.html` (the target UX for phase 9; backend contracts here must feed it).

**Canonical states (use exactly):** `Draft → Submitted → UnderReview → (Approved | PartiallyApproved | Rejected | InfoRequested)`; plus `Overridden`, `EmergencyApproved`, `Expired`.
**Canonical events:** `AuthSubmitted`, `AuthUnderReview`, `AuthApproved`, `AuthPartiallyApproved`, `AuthRejected`, `AuthInfoRequested`, `AuthInfoSupplied`, `AuthOverridden`, `AuthEmergencyApproved`, `AuthExpired`.

Depends on phase 4 (orders/prescriptions route to approval) and phase 0 (identity, ABAC, audit spine).

---

## Prompts

### 7.1 — `approvals-service` + worklist + clinical review view

```text
Build the approvals-service (.NET 8, bounded context `approvals`, schema `approvals`) that manages the Authorization lifecycle and the reviewer worklist. Read ../07, ../11 §3.2, ../19, ../23 §5, ../24, and US-060 first.

DOMAIN & DATA
- Table `authorization` (append-safe aggregate): id uuid v7, business key `AUTH-YYYY-XXXX`, beneficiary_id, source (order_line | prescription | manual), source_ref, requesting_provider_id (nullable for manual), service_codes[], requested_scope (jsonb), priority (routine|urgent|emergency), status (enum = canonical states), assigned_reviewer_id, sla_due_at, submitted_at, decided_at, created/updated audit columns. Status enum values EXACTLY as in ../23 §5.
- Table `authorization_decision` — APPEND-ONLY, never updated or deleted: id, authorization_id, decision (Approved|PartiallyApproved|Rejected|InfoRequested|Overridden|EmergencyApproved), reviewer_id, decided_at (timestamptz UTC), rationale (text), approved_scope (jsonb, for partial), break_glass (bool), justification (text, required when break_glass), correlation_id. Enforce append-only with a DB rule/trigger + no UPDATE/DELETE grants; corrections are new rows, not edits.
- Model the transition table from ../23 §5 as an explicit state machine with guards; reject illegal transitions with RFC7807 409.

WORKLIST API (/api/v1)
- GET /authorizations — reviewer inbox: filter by status, priority, sla breach, assigned/unassigned; sort by sla_due_at; cursor pagination. Return worklist projection ONLY (no clinical payload): key, beneficiary display-min, service codes, priority, status (with the non-color status attributes), sla_due_at, tat_elapsed.
- POST /authorizations/{id}/assign — pick up a request: Submitted → UnderReview, sets assigned_reviewer_id + starts SLA timer, emits AuthUnderReview.
- GET /authorizations/{id}/review — the review view. This is the ONLY endpoint that exposes clinical context (EMR summary, clinical notes, supporting documents/reports) and it MUST:
  * require scope auth:review + role Medical Approval + ABAC purpose PUR (../11 §3.2);
  * return a field-scoped clinical DTO assembled by calling emr/document services with the caller's purpose — approvals CAN see emr_note/diagnosis/lab_result/imaging_result, but the DTO is an explicit projection, not the raw record;
  * write a PHI-read audit event (actor, authorization_id, fields returned) via the shared audit client.

ACCEPTANCE (US-060)
- Given a Submitted request, When a Medical Approval reviewer opens /review, Then they see EMR summary, clinical notes, and supporting documents, and a PHI-read audit event is written.
- Given a user without the Medical Approval role, When they call /review, Then they get 403 (audited) and no clinical fields leak.
- Given an illegal transition (e.g., assign an already-Approved case), When attempted, Then 409 problem+json and no state change.

Ship: EF migrations, OpenAPI, unit + integration + authorization tests (prove finance/reception cannot call /review), README/ADR. Publish domain events via the outbox.
```

### 7.2 — Decisions with mandatory rationale + downstream effects

```text
Add decision endpoints to approvals-service. Read ../19, ../23 §5, ../24, and US-060 first. Each decision writes an authorization_decision row (append-only) and drives the state machine.

ENDPOINTS (/api/v1), all requiring scope auth:decide + Idempotency-Key
- POST /authorizations/{id}/approve — UnderReview → Approved. rationale optional-but-recorded.
- POST /authorizations/{id}/partially-approve — UnderReview → PartiallyApproved. Body MUST include approved_scope (the itemized approved subset) + rationale. Reject if approved_scope empty or equals full scope.
- POST /authorizations/{id}/reject — UnderReview → Rejected. **rejection reason (rationale) is MANDATORY** — 422 if missing/blank.
- POST /authorizations/{id}/request-info — UnderReview → InfoRequested. rationale = what is missing (mandatory).
- (InfoRequested → UnderReview via POST /authorizations/{id}/resupply, emits AuthInfoSupplied.)

EVERY decision MUST record: reviewer_id, decided_at (UTC), decision, rationale (per ../19). Persist the decision row in the SAME transaction as the status change and the outbox event.

DOWNSTREAM
- On Approved/PartiallyApproved/Rejected, emit the matching canonical event (AuthApproved / AuthPartiallyApproved / AuthRejected / AuthInfoRequested) and update the linked order_line / prescription gate (../24): Approved → releases fulfillment; PartiallyApproved → releases only approved_scope lines, leaves the rest gated; Rejected → blocks. Use a saga; the orders/pharmacy consumers are idempotent (dedupe on event id).
- Emit a notification trigger (consumed by notification-service, phase 8) to the requesting provider and beneficiary-facing channel.
- Capture TAT: on decision, compute tat = decided_at − submitted_at and persist for reporting; flag sla_breached if decided_at > sla_due_at.

ACCEPTANCE (US-060)
- Given a request UnderReview, When the reviewer Approves/Partially/Rejects/Requests-info, Then reviewer, timestamp, decision, and rationale are recorded and immutably audited; a rejection with no reason returns 422.
- Given a PartiallyApproved decision, When it commits, Then only the approved_scope order/prescription lines are released and the remainder stay gated.
- Given any decision, When it commits, Then the canonical domain event is published via the outbox and TAT is recorded.

Tests: unit (guards + mandatory-rationale), integration (state + outbox + downstream gate), contract (Pact) for the auth→orders/pharmacy events, concurrency (two reviewers deciding the same case → one wins, other 409).
```

### 7.3 — Break-glass: emergency approval, override, manual authorization + SLA/TAT

```text
Add the break-glass and manual paths to approvals-service. Read ../19, ../23 §5, US-061, US-062 first. These are the specially-audited exceptions — treat justification as non-optional and flag the audit trail.

EMERGENCY APPROVAL (US-061) — Submitted → EmergencyApproved
- POST /authorizations/{id}/emergency-approve, scope auth:emergency, role Medical Director (or delegated emergency authority per ../11).
- Body MUST include justification (non-blank) → 422 otherwise. Sets break_glass=true on the decision row.
- Emits AuthEmergencyApproved; flags "retrospective review required" (../23 §5) so it appears in a post-hoc review queue.

OVERRIDE (US-061) — Rejected → Overridden
- POST /authorizations/{id}/override, scope auth:override, role Medical Director.
- justification MANDATORY (director authority, ../23 §5). Sets break_glass=true. Emits AuthOverridden. Releases the linked order/prescription like an approval but tagged as override.

MANUAL AUTHORIZATION (US-062) — create without a provider submission
- POST /authorizations/manual, scope auth:manual, role Medical Approval / Director.
- Flow: search member (GET /beneficiaries?query=… min-necessary result), then create an authorization with source=manual, requesting_provider_id=null, requested_scope from the reviewer, and immediately decide it (Approved/PartiallyApproved). Requires justification.
- Result MUST be valid, linked to the member, and fully audited (US-062). Emits AuthApproved/AuthPartiallyApproved with source=manual.

BREAK-GLASS AUDIT (../19)
- Every emergency/override/manual decision writes a specially-flagged audit event (event_type includes `break_glass`, plus justification, actor, entity, correlation id) via the shared audit client, on the immutable hash chain. These events feed a dedicated break-glass audit report + retrospective-review queue.

SLA / TAT TRACKING
- SLA timer starts at UnderReview (assign); sla_due_at from policy (priority-based). Expose GET /authorizations/tat-summary (aggregate: count by status, avg/p95 TAT, breach count) for the reporting-service read-model (phase 8). Emergency/manual cases carry TAT from submitted/created → decided.

ACCEPTANCE
- US-061: Given an emergency, When a Director issues emergency-approve/override, Then extra justification is required (422 without) and a break-glass audit entry is written and flagged for retrospective review.
- US-062: Given a member search, When a reviewer creates a manual authorization, Then it is valid, linked to the member, decided with justification, and fully audited.
- Given any break-glass action, When audited, Then the audit event is specially flagged and appears in the break-glass report.

Tests: unit (justification mandatory on all three paths), integration (audit flag + retrospective queue), authz (only Director can override/emergency), E2E (manual auth end-to-end from member search).
```

---

## Guardrails

- **Immutable audit on every decision.** `authorization_decision` is append-only (no UPDATE/DELETE grants); the hash-chained `audit_event` is written for every create/state-change/decision/PHI-read/export via the shared client ([../19](../19-audit-strategy.md)). Corrections are new rows, never edits.
- **Break-glass is specially flagged.** Emergency, override, and manual authorization all require extra justification (422 without) and write a distinctly typed, retrospectively-reviewed audit event.
- **Minimum-necessary even where reads are allowed.** Approvals CAN see EMR/notes/reports, but only via the field-scoped `/review` DTO under ABAC purpose `PUR`; the decision APIs never echo raw clinical records. Finance never touches this service's clinical payloads.
- **Only legal transitions.** Enforce the ../23 §5 state machine; illegal transitions → 409. Mandatory rationale on reject; mandatory approved_scope on partial.
- **Idempotent + atomic.** Decisions carry `Idempotency-Key`; decision row + status change + outbox event commit in one transaction; downstream consumers dedupe on event id.

## Done when

- A submitted request can be picked up, reviewed **with EMR/clinical context** (field-scoped, PHI-read audited), and decided (Approve/Partial/Reject/Request-info) with rationale recorded and immutably audited — rejection reason enforced.
- PartiallyApproved releases only the approved scope; downstream order/prescription gates update via idempotent events.
- Emergency approval, director override, and **manual authorization** paths work, each requiring justification and writing a specially-flagged break-glass audit entry (retrospective-review queued).
- TAT is captured per case and a TAT/SLA aggregate is queryable for reporting.
- All acceptance criteria for US-060, US-061, US-062 pass; unit/integration/contract/authz/concurrency tests green; OpenAPI + README/ADR updated. Global Definition of Done met.

# case-service (Phase 10.1)

Care/benefit **coordination over an assigned case load**. A Case Manager works the cases they are **assigned** to,
sees a **beneficiary-360 coordination view** (minimum-necessary, field-scoped), opens/tracks coordination tasks,
and raises escalations — every action scoped to an active assignment and audited. Bounded context `case`, schema
`case` (a SQL reserved word → always double-quoted).

## The access model — `case-assignment` ABAC

The distinctive control is the **`case-assignment`** ABAC condition (10-role-matrix §3.11 *"access follows
assignment; unassignment revokes it"*), added to `libs/authz`:

- An **active** `case_assignment` row is the access anchor. `CaseGate` resolves the caller's active-assignment set
  from the DB and hands it to the shared authorization engine on the `ResourceRef` (`AssignedCaseIds`), so the
  check runs at the **policy layer**, not just in the controller.
- **Unassignment** (`active=false` + `unassigned_at`) removes the case from that set → the next authz evaluation is
  a `403` (audited). No cross-case-load reads.
- **Supervisory** roles (Manager / Medical Director) reach a case for oversight via the distinct
  `case:read-oversight` action (tenant-only), which the gate selects by role — mirroring `emr:read` /
  `emr:read-oversight`. They **cannot self-assign** (assign/unassign is `case:manage`, supervisory only — the
  access anchor can't be self-granted).

## Beneficiary-360 — a coordination summary, not a clinical record

`GET /api/v1/cases/{id}/beneficiary-360` assembles an **explicit, field-scoped** `Beneficiary360` DTO by calling
sibling services (eligibility/policy, appointments, approvals, and the emr coordination summary) under the caller's
bearer token and purpose `coordination`. Per 11-permission-matrix §4 a Case Manager gets:

- eligibility + **coverage** summary (remaining limits), **care-plan** status, **appointment** + open **approval
  STATUS**;
- a clinical **SUMMARY**: `diagnosis` is coord-visible (coded); `emr_note` / `prescription` / `lab_result` /
  `imaging_result` are represented **only as masked counts** (`MaskedSection` — presence, never the record body).
  The DTO graph has no property that can carry a raw note / result, so detail cannot leak (proven by a test).

Every assembly writes a **PHI-read audit event** naming the field classes returned (never the values). The assembly
is **fail-closed**: if the coverage spine can't be reached the endpoint returns `502`, never a partial leak.

`POST /cases/{id}/eligibility-override` (FR-ELG-007) initiates a **manual eligibility override** with a **mandatory
reason** (blank → `422`), audited high-severity here and delegated to eligibility-service via the outbox.

## APIs (`/api/v1`)

| Method | Route | Action | Notes |
|--------|-------|--------|-------|
| POST | `/cases` | `case:open` | Open a case (intake / supervisory). |
| GET | `/cases` | `case:read` | **My Cases** — caller's active assignments, cursor-paged, `status` filter. |
| GET | `/cases/{id}` | `case:read` / `case:read-oversight` | Assignment-scoped, or supervisory oversight. |
| PATCH | `/cases/{id}/status` | `case:write` | State machine Open→Active→OnHold↔Active→Resolved→Closed. |
| POST | `/cases/{id}/assign` · `/unassign` | `case:manage` | Supervisory; unassign **revokes** access. |
| POST | `/cases/{id}/escalate` | `case:write` | Raise to a role (Medical Approval / Director) — trackable, audited. |
| PATCH | `/cases/{id}/escalations/{eid}` | `case:write` | Acknowledge / resolve. |
| GET/POST | `/cases/{id}/tasks` · PATCH `/{tid}` | `case:read` / `case:write` | Coordination kanban Todo→InProgress→Done/Cancelled. |
| GET | `/cases/{id}/beneficiary-360` | `case:read` (+ `case:read-360` policy) | Field-scoped coordination view, PHI-read audited. |
| POST | `/cases/{id}/eligibility-override` | `case:write` | FR-ELG-007, mandatory reason. |

Events (outbox → `case.events`): `CaseOpened`, `CaseAssigned`, `CaseUnassigned`, `CaseEscalated`, `TaskCompleted`,
`EligibilityOverrideRequested`.

## Data & integrity

Schema `case` (migration `0001_case.sql`): `case_file`, `case_assignment` (one active row per case+manager, full
history — never deleted), `coordination_task`, `escalation`, `case_seq` (per-year `CASE-YYYY-NNNNNN`),
`processed_request`. Soft-delete + history on cases/tasks (`deleted` flag); the assignment history is auditable.
`xmin` optimistic concurrency on the case aggregate.

## Tests

- `CaseAuthzTests` — the ABAC proof: assigned → allow; **unassigned → deny (audited)**; unassignment revokes read
  **and** 360; supervisor oversight without assignment; Case Manager cannot self-assign; the 360 DTO cannot carry a
  raw clinical body.
- `CaseDomainTests` — case/task/escalation state machines, `CASE-YYYY-NNNNNN`, masked-section semantics.
- `CaseIntegrationTests` — env-gated `CASE_TEST_DB` (hbmp superuser conn): monotonic case number; assignment
  resolver grants then **unassignment revokes** at the datastore. Serialized via the `case-db` collection.

## ADR — why the coordination 360 is a projection, not an EMR read

The Case Manager needs *enough to coordinate benefits*, not the clinical record. Rather than granting a read against
emr/orders/pharmacy and filtering, case-service assembles an **explicit min-necessary DTO** from sibling summaries
under the caller's token (each sibling re-authorizes — defense in depth). The DTO is **structurally** incapable of
carrying raw clinical bodies, so "coordination summary only" is enforced by the type system, not by discipline. This
is the same posture as reporting's de-identified read-model and approvals' field-scoped review context.

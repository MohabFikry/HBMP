# approvals-service

Medical **approvals** (Release R4, Phase 7 — US-060/061/062). Owns the `approvals` schema. The Medical Approval
team and the Medical Director review authorization requests routed from gated investigation orders / prescriptions
(and manual seeds), see the clinical context under **minimum-necessary** field scoping, decide with **mandatory
rationale**, and use specially-audited **break-glass** paths. Every decision is immutable and hash-chain audited,
with SLA/TAT tracking.

> Phase 7 complete: 7.1 service foundation + reviewer worklist + clinical **review view**; 7.2 **decisions**
> (mandatory rationale) + downstream gate release + TAT/SLA; 7.3 **break-glass** (emergency / override / manual) +
> retrospective-review queue + TAT summary.

## Lifecycle (23-state-machines §5)

`Draft → Submitted → UnderReview → (Approved | PartiallyApproved | Rejected | InfoRequested)`; plus `Overridden`,
`EmergencyApproved`, `Expired`. Modelled as an explicit state machine (`AuthorizationWorkflow`) with guards; illegal
transitions are an RFC7807 **409**. The aggregate `Status` is the projection of the latest **append-only**
`authorization_decision` row.

## Ingestion (the routing seam)

`POST /api/v1/authorizations` (scope `auth:ingest`, `Idempotency-Key` required) creates a **Submitted**
authorization from a routed order-line / prescription (`source` = `OrderLine|Prescription|Manual`). This is the
system-to-system seam that the phase-4 routing saga / the `OrderPendingApproval`|`RxSubmitted` event consumer
targets; no clinical payload crosses it. Emits `AuthSubmitted` via the outbox. A non-manual request must name the
requesting provider (**422** otherwise; DB `CHECK` backstop).

## Worklist (US-060) — MIN-NECESSARY, no clinical payload

- `GET /api/v1/authorizations` (scope `auth:read`) — the reviewer inbox: filter by `status`, `priority`,
  `slaBreached`, `unassigned`; sorted by `sla_due_at`. Returns `WorklistItemView` ONLY (key, beneficiary id,
  service codes, priority, status, SLA due, elapsed TAT) — **never** diagnoses/notes/results.
- `GET /api/v1/authorizations/{id}` (scope `auth:read`) — worklist detail, same min-necessary projection.
- `POST /api/v1/authorizations/{id}/assign` (scope `auth:review`) — pick up a request: `Submitted → UnderReview`,
  sets the reviewer + starts the priority-based **SLA timer**, emits `AuthUnderReview`. Two reviewers racing → the
  `xmin` optimistic-concurrency guard means exactly one wins; the loser gets a **409** with no state change.

## Review view (US-060) — the ONLY clinical-context endpoint

`GET /api/v1/authorizations/{id}/review` (scope `auth:review` + role Medical Approval / Director, ABAC purpose
`PUR`). Returns a **field-scoped** clinical DTO (`ReviewView`: EMR summary + clinical notes + supporting documents)
assembled by `IClinicalContextProvider` — an **explicit projection**, never the raw record. Approvals read clinical
data for **oversight** (no treating relationship — 11-permission-matrix §3.2), so the gate is a tenant-scoped
role+scope check via the shared engine; **finance/reception have no rule and are default-denied (403, audited)**.
Every open writes a **PHI-read audit event** (actor, authorization id, purpose PUR, field classes returned).
`HttpClinicalContextClient` calls emr-service's oversight projection with the caller's bearer token (so emr enforces
its own `emr:read-oversight` rule — defense in depth) and is **fail-closed**: if the projection can't be assembled
the view shows "clinical context unavailable" rather than fabricating PHI. (The emr oversight endpoint is the
integration seam wired alongside phase 8.)

## Decisions (US-060) — mandatory rationale + downstream (phase 7.2)

All under scope `auth:decide` + `Idempotency-Key`. Each writes an **append-only** `authorization_decision` row,
drives the state machine, and emits the canonical event **in one transaction**, then audits (`Decision`); TAT is
captured (`tat_seconds = decided − submitted`) and `sla_breached` flagged.

- `POST /authorizations/{id}/approve` — `UnderReview → Approved` (rationale recorded).
- `POST /authorizations/{id}/partially-approve` — `→ PartiallyApproved`; `approved_scope` + rationale mandatory;
  `approved_scope` must be a **non-empty strict subset** of the requested codes (empty / not-subset / equals-full →
  **422**).
- `POST /authorizations/{id}/reject` — `→ Rejected`; **rejection reason mandatory (422 if blank)**.
- `POST /authorizations/{id}/request-info` — `→ InfoRequested`; rationale (what's missing) mandatory.
- `POST /authorizations/{id}/resupply` — `InfoRequested → UnderReview` (emits `AuthInfoSupplied`; state change, no
  decision row).

**Downstream:** emits `AuthApproved`/`AuthPartiallyApproved`/`AuthRejected`/`AuthInfoRequested` carrying
beneficiary/source/`sourceRef`/`approvedScope` + a `releasesDownstream` flag, so the orders/pharmacy gate consumers
(idempotent, dedupe on event id) release the approved lines and leave the rest gated; `Rejected` blocks.
**Concurrency:** two reviewers deciding the same case race on the authorization's `xmin` → exactly one wins, the
other **409**. The shared `Decide` helper updates the **parent first** (exclusive lock + xmin check) **then** inserts
the append-only child, avoiding the FK-share→exclusive deadlock under simultaneous deciders.

## Break-glass + SLA/TAT (US-061, US-062 — phase 7.3)

The specially-audited exceptions — each requires a **non-blank justification (422 otherwise)**, writes a
`break_glass` decision row (High-severity, flagged audit) and marks the case for **retrospective review**.

- `POST /authorizations/{id}/emergency-approve` (scope `auth:emergency`, **Director only**) — `Submitted →
  EmergencyApproved`.
- `POST /authorizations/{id}/override` (scope `auth:override`, **Director only**) — `Rejected → Overridden`;
  releases downstream like an approval, tagged as an override.
- `POST /authorizations/manual` (scope `auth:manual`) — **manual authorization** with no provider submission
  (`source = Manual`, `requesting_provider_id = null`): create + decide (Approved / PartiallyApproved) in one step,
  justification required; the beneficiary is resolved by the reviewer via the existing min-necessary member search.
  Emits `AuthApproved`/`AuthPartiallyApproved` with `source = Manual`.
- `GET /authorizations/retrospective-queue` (scope `auth:read`) — break-glass cases awaiting post-hoc review
  (min-necessary), newest first; drops once `retrospective_reviewed`.
- `GET /authorizations/tat-summary` (scope `auth:read`) — the TAT/SLA aggregate (count + avg + **p95** +
  breach count, overall and per status) for the phase-8 reporting read-model (`TatReporting`, Postgres
  `percentile_cont`).

## Authorization (`libs/authz/ApprovalsPolicies`, v7.0)

Roles `medical_approval` + `medical_director` on resource type `authorization`. Actions: `auth:list` /
`auth:assign` / `auth:review` (7.1), `auth:decide` (7.2), `auth:emergency` / `auth:override` (Director-only) /
`auth:manual` (7.3). `review`/`decide`/break-glass are flagged **Sensitive** → the engine audits the allow
(PHI-read / decision). All rules require tenant match.

## Domain & data

- `authorization` (`AUTH-YYYY-NNNNNN`; status/priority/source CHECK-constrained; `xmin` RowVersion; SLA/TAT columns;
  `service_codes`/`requested_scope` jsonb; `CHECK (source='Manual' OR requesting_provider_id IS NOT NULL)`).
- `authorization_decision` — **APPEND-ONLY** (23 §5, 19-audit-strategy): one immutable row per decision, carrying
  reviewer, timestamp, decision, rationale, `approved_scope` (jsonb, for partial), `break_glass` + mandatory
  `justification`. Immutability enforced by a trigger (`deny_decision_mutation`) **and** a revoked UPDATE/DELETE
  grant on `hbmp_app`; corrections are new rows, never edits.
- `Infrastructure/Migrations/0001_approvals.sql` — schema, `auth_seq`, both tables, the append-only trigger,
  `processed_request` idempotency ledger. `0002_breakglass.sql` — `retrospective_review_required` /
  `retrospective_reviewed` flags + the retrospective-queue partial index. Both applied to host PG (:55432).

## Tests

- `AuthorizationWorkflowTests` — the §5 transition table (legal + illegal), decision→state mapping, the
  release-downstream set, and priority-ordered SLA due. Pure, no DB.
- `ApprovalsAuthzTests` — against the real engine over `ApprovalsPolicies`: a reviewer/Director may open the
  clinical review (sensitive allow audited with purpose PUR); **finance and reception are default-denied** (deny
  audited); review needs the `auth:review` scope even for a reviewer; only the Director may emergency/override.
- `ApprovalsIntegrationTests` (env-gated `APPROVALS_TEST_DB`, live PG) — an authorization round-trips as Submitted
  with a monotonic `AUTH-` number; a manual authorization may omit the provider while a non-manual without one is
  DB-rejected; and the **append-only decision ledger rejects UPDATE and DELETE** via the trigger. Serialized via the
  `approvals-db` collection.
- `DecisionRulesTests` — mandatory-rationale blankness, the partial-approval strict-subset scope check, TAT and
  SLA-breach computation (pure).
- `DecisionConcurrencyTests` (env-gated, real 8-way parallel PG) — N reviewers deciding the same case → **exactly one
  commits**, one ledger row; parent-first ordering, no deadlock.
- `BreakGlassTests` (env-gated, live PG) — a break-glass decision without a justification is DB-rejected (CHECK); a
  manual break-glass authorization lands in the **retrospective queue** and drops once reviewed; the **TAT summary**
  aggregates avg/p95/breach over decided cases.

Total: 54 approvals tests; full solution 429 green.

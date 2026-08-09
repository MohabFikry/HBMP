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
system-to-system seam for an authorization raised from outside; no clinical payload crosses it. Emits
`AuthSubmitted` via the outbox. A non-manual request must name the requesting provider (**422** otherwise).

The seam carries two things the authorization cannot work out for itself. `orderedByUserId` is **who** is
waiting, so a decision notice has a human to reach (§11.3). `encounterId` is **which visit** it came out of
(ADR-0031, migration 0004), so the decision lands on that patient's care episode — an authorization is one of
the few artefacts that can hold a consultation open for days, and without it the appointment's timeline showed
the wait begin and never showed it end. Both are optional and both stay optional: a manual authorization is
raised by a reviewer with no encounter in hand, and a guessed one would put this member's authorization on
another member's timeline. The decision events carry `encounterId` and `tenantId` outward for the same reason —
emr's consumer binds its RLS session from that envelope and refuses a message it cannot attribute.

### The routing consumer (ADR-0041)

`RoutingConsumer` binds **`approvals.routing-events`**, approvals' own mirror of `OrderPendingApproval` and
`RxSubmitted` (`ApprovalRoutingFeed`), and raises the authorization through `RoutedAuthorizationIngestor` —
the same row this endpoint creates, in the same states, in the same `processed_request` ledger.

This is the caller the seam was written for and did not have. Until 2026-08-09 `auth:ingest` was held by
nobody: a gated order changed status, told the patient to wait, and reached no reviewer. Two differences from
the endpoint, both deliberate:

- **`RxSubmitted` is filtered on `requiresApproval`.** pharmacy emits it for every prescription; only the
  gated ones become authorizations, or a queue whose value is that everything in it needs a decision fills
  with a few hundred a day that do not.
- **A routed request may name no provider.** A doctor's token is practitioner-scoped and carries no
  `provider_id`, so a prescription has none to give. The DB `CHECK` (migration 0010) now requires a
  requesting provider **or** a `created_by` — attribution, which is what the rule was always reaching for.
  The endpoint's own 422 is unchanged, because there a missing provider means "this system cannot say who is
  asking".

Idempotency is keyed on the **event id**, never on `(source, sourceRef)`: an out-of-scope amendment
re-publishes the same event for the same order (design 46 §5) and that second request is a real one.

## Worklist (US-060) — MIN-NECESSARY, no clinical payload

- `GET /api/v1/authorizations` (scope `auth:read`) — the reviewer inbox: filter by `status`, `priority`,
  `slaBreached`, `unassigned`; sorted by `sla_due_at`. Returns `WorklistItemView` ONLY (key, beneficiary id,
  service codes, priority, status, SLA due, elapsed TAT) — **never** diagnoses/notes/results.
- `GET /api/v1/authorizations/{id}` (scope `auth:read`) — worklist detail, same min-necessary projection.
- `POST /api/v1/authorizations/{id}/assign` (scope `auth:review`) — pick up a request: `Submitted → UnderReview`,
  sets the reviewer + starts the priority-based **SLA timer**, emits `AuthUnderReview`. Two reviewers racing → the
  `xmin` optimistic-concurrency guard means exactly one wins; the loser gets a **409** with no state change.

## Fulfilment authorizations — the register (ADR-0034)

Dispensing a prescription line, or consuming an investigation-order line, **issues an authorization**: a
record of what was actually handed over, separate from the clinical instruction it was delivered against.
`Kind = Fulfilment`, `Status = Issued`, `SourceRef` = the prescription / order.

- **One authorization per prescription (per order)**, accumulating one `authorization_item` per fulfilment. A
  member collecting a fortnight's medication over two visits has one authorization with two items.
- **A substitution lands here and nowhere else.** The item stores `ordered_code` and `fulfilled_code` as two
  separate columns, so recording what was handed over cannot overwrite what the prescriber wrote — and this
  service has no client for the prescription at all.
- **`Issued` is terminal and unreachable.** No transition targets it and none leaves it, so settled work can
  never be assigned to a reviewer or start an SLA clock on a question nobody asked.

Issuance is **asynchronous, on approvals' own queue** (`approvals.fulfilments`). Not by an HTTP call from the
dispensing path: an authorization that cannot be issued must never be able to fail a dispense. Not by binding
`pharmacy.events` / `orders.events` either — that transport is point-to-point and policy-service already
consumes both, so a second consumer would COMPETE for messages and silently stop the benefit accumulator.
At-least-once delivery is guarded twice: `processed_event` for a redelivered message id, and UNIQUE
`(tenant_id, fulfilment_ref)` for a redelivery arriving under a new one.

- `GET /api/v1/authorizations?kind=Fulfilment|Review|All` (scope `auth:read`) — **`kind` defaults to
  `Review`.** The inbox is a work queue; a few hundred dispenses a day would drown the handful of requests
  that need a decision, and a queue that is mostly noise stops being read.
- `GET /api/v1/authorizations/{id}/items` (scope `auth:read`) — codes, labels, quantities and, only where the
  two codes differ, the substituting pharmacist's reason. Nothing clinical.
- `POST /api/v1/authorizations/substitution-requests` (scope `auth:request-substitution`, `lab_tech` /
  `imaging_tech`) — a bench asking whether another examination may stand in. A **request**, not a choice:
  master data records no equivalence between examinations, so a picker would have to be derived from the
  category, which would put "any radiology procedure" behind a button.

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
- `authorization_item` — what was delivered against a fulfilment authorization: `ordered_code` /
  `fulfilled_code` (two columns, deliberately), quantity, `substitution_reason` (DB-required when the codes
  differ), `fulfilment_ref` UNIQUE per tenant. RLS on `tenant_id`.
- `processed_event` — the fulfilment consumer's dedupe ledger. Intentionally RLS-free: event ids, no tenant
  data (mirrors `policy.processed_event`).
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

- `FulfilmentAuthorizationTests` (validation pure; issuance env-gated, live PG) — a dispense keeps **both**
  the written and the delivered drug; a substituted item with no reason is refused; a redelivered dispense
  cannot post twice; a second dispense **appends** rather than issuing again; a fulfilment is born `Issued`
  and a reviewer picking it up gets a 409; the reviewer inbox does not fill with dispenses while the register
  shows them; the items projection carries no clinical field; a technician's substitution question raises a
  **Review**, not a fulfilment, and is refused without a reason.

Invariants: `INV-SUBSTITUTION-DOES-NOT-EDIT-THE-PRESCRIPTION`, `INV-NO-INVENTED-EXAMINATION-EQUIVALENCE`
(`docs/quality/invariant-registry.yaml`).

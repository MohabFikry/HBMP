# reporting-service

Operational-KPI **read-model** + executive-dashboard contracts (Release R5, Phase 8.2 + 8.3 — US-073). Owns the
`reporting` schema. It projects domain events into query-optimized **aggregate, de-identified** views and serves KPI
+ dashboard APIs. It **never** writes to source domains and **never** re-derives PHI: every fact holds coded values,
counts, amounts and timings — no beneficiary identifiers, no free-text clinical notes, no row-level PHI. **Financial
views exclude diagnoses** (finance ≠ diagnosis), enforced in the schema and asserted by a test.

> Phase 8.2 complete: event-projected read-model (approval TAT + p95, pending-approvals snapshot, clinic workload,
> utilization, no-show, top diagnoses/medications, rejected requests, financial summary), zone-split KPI APIs, an
> audited CSV export, and an async job handle for long ranges. Phase 8.3 complete: the versioned executive-dashboard
> contract where **every widget carries an accessible dataTable + bilingual AR/EN labels**.

## Read-model / projections

`EventProjector.ProjectAsync(ReportingEvent)` maps a canonical, de-identified domain event → fact rows,
**idempotently** (dedupe on event id; a redelivery is a no-op). Mappings: `Auth*` decisions → `authorization_fact`
(+ `pending_authorization` snapshot, dropped on decision); `EncounterCreated` / `Appointment{Booked,Attended,NoShow}`
→ `encounter_fact`; `OrderLineConsumed` → `utilization_fact` (lab/radiology + provider); `RxDispensed` →
`utilization_fact` (drug) + `code_count` (medication); `DiagnosisRecorded` → `code_count` (diagnosis); `ServiceValued`
→ `financial_fact` (service code + amount only). Unmapped events are recorded processed and ignored.

`POST /api/v1/reports/projections` (scope `reporting:project`) is the system seam the projection consumer targets.

> **Deferred wiring (fanout bus):** dev uses a per-service in-memory outbox with no fanout exchange, so the live
> broker subscription that turns raw domain events into `projections` calls lands with the shared event bus (same
> seam as phases 5–8.1). The projector + queries are fully tested today without it.

## Minimization (enforced in the projection, not just the API)

Facts are aggregate + de-identified. `code_count` holds coded counts (ICD/ATC), never patient-linked rows.
`financial_fact` has service-line/code/amount columns and **deliberately no diagnosis/clinical column** — a test
(`Financial_fact_table_has_no_diagnosis_column_in_the_live_schema`) queries `information_schema` and fails if one
appears; the finance role is also default-denied the clinical-coded reports in authz (below).

## KPI APIs (`/api/v1/reports`) — zone-split, tenant-scoped

Operational (`reporting:read`): `approval-tat`, `pending-approvals`, `clinic-workload`,
`utilization?dimension=provider|drug|lab|radiology`, `no-show`, `rejected-requests`. Clinical-coded
(`reporting:read`, clinical zone): `top-diagnoses`, `top-medications`. Financial (`reporting:read-financial`):
`financial-summary`. Params: `from` / `to` (default trailing 30 days). Operational reports run inline (NFR-006
p95 ≤ 3 s); a range > 92 days returns a **202 + job handle** (`GET /reports/jobs/{id}` to poll). `GET
/reports/{report}/export` (scope `reporting:export`) returns CSV and writes an **Export audit event** (actor,
report, filter, row count).

## Executive dashboard (phase 8.3)

`GET /api/v1/dashboards/executive` — the versioned (`1.0`) composed contract. Each `DashboardWidget` carries chart
`series` **and** a mandatory accessible `dataTable` (labelled columns + rows) **and** bilingual AR/EN title + axis +
series labels (WCAG non-visual equivalent; Arabic RTL, authored). Widgets are zone-tagged; the endpoint includes the
clinical (top diagnoses/medications) and financial widgets **only** for a caller authorized for those zones —
financial widgets exclude diagnoses by construction. A widget without a complete dataTable + bilingual labels fails
`DashboardWidget.IsAccessible`, which the contract test asserts (CI gate).

> The canonical contract is the C# record set + OpenAPI (source of truth). The shared TS/zod mirror in
> `/libs/contracts` lands with the Phase 9 frontend.

## Authorization (`libs/authz/ReportingPolicies`, v8.2)

Access is split by data zone so the permission matrix is enforced in AUTHZ, not just the query:
`reporting:read-operational` + `reporting:read-clinical` (Medical Director / Manager; **not** finance) and
`reporting:read-financial` (finance + management). Because finance holds only the financial action, a
diagnosis-bearing report is **default-denied** to it. `reporting:project` is the system seam; `reporting:export` is
Sensitive → the handler writes the export audit.

## Domain & data

- Fact tables `authorization_fact` / `pending_authorization` / `encounter_fact` / `utilization_fact` / `code_count` /
  `financial_fact` (each with a unique `event_id` for idempotent projection), `processed_event` (dedupe), `report_job`
  (async handles). `financial_fact` has **no** diagnosis column.
- `Infrastructure/Migrations/0001_reporting.sql` — schema, fact tables, indexes, app-role grants. Applied to host PG
  (:55432).

## Tests

- `ReportModelsTests` (pure) — p95 nearest-rank + age bucketing.
- `ReportingAuthzTests` (pure, real engine) — finance may read financial but is **denied** top-diagnoses (finance ≠
  diagnosis); the Medical Director reads operational + clinical; the projection seam needs its scope; the financial
  fact type carries no diagnosis field.
- `ProjectionTests` (env-gated `REPORTING_TEST_DB`, live PG) — Auth decisions → TAT with p95 + breach counts; the
  pending snapshot tracks in-flight and drops on decision; projection is **idempotent**; workload + no-show from
  appointment/encounter events; financial summary from `ServiceValued` only; and the **financial fact table has no
  diagnosis column** in the live schema.
- `DashboardContractTests` (env-gated) — **every** widget has a dataTable + bilingual labels; clinical/financial
  widgets appear only when authorized; an incomplete bilingual label fails the accessibility gate.

Serialized via the `reporting-db` collection. Total: 20 reporting tests; full solution 476 green.

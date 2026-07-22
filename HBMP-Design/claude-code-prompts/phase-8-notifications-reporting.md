# Phase 8 — Notifications & Reporting (R5)

**Goal:** Build the `notification-service` (in-app + email, bilingual AR/EN templates, escalations, delivery tracking, future SMS/WhatsApp stubs) and the `reporting-service` (read-model/materialized views for operational KPIs — approval TAT, pending approvals, utilization, no-show, top diagnoses/medications, financials — plus accessible executive-dashboard data contracts). Reporting stays minimum-necessary: aggregates only, no PHI leakage, and finance views never expose diagnoses.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

---

## Skills to activate
> Activate `healthcare-reporting-kpis`, `executive-dashboard-designer` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [../07-functional-requirements.md](../07-functional-requirements.md) — notification + reporting FRs (R5).
- [../08-non-functional-requirements.md](../08-non-functional-requirements.md) — NFR-006 (operational report p95 ≤ 3 s; heavy analytics async with progress), NFR-005 (upload/export), NFR-082 (SLO dashboards).
- [../11-permission-matrix.md](../11-permission-matrix.md) — finance ≠ diagnosis; aggregate reads still obey field-level minimization.
- [../19-audit-strategy.md](../19-audit-strategy.md) — audit of exports/report reads.
- [../32-user-stories.md](../32-user-stories.md) — **US-072** (notifications & alerts), **US-073** (operational dashboard).
- Depends on domain events from phases 1–7 (orders, results, approvals, appointments). Consumes canonical events (e.g., `AuthApproved`, `AuthRejected`, `AuthInfoRequested`, `OrderLineAvailable`, `ResultReady`).

---

## Prompts

### 8.1 — `notification-service`: channels, bilingual templates, escalations, delivery tracking

```text
Build notification-service (.NET 8 or Node BFF-edge acceptable per CLAUDE.md, bounded context `notification`, schema `notification`). Read ../07, US-072 first. It is an event-driven fan-out engine, not a place business logic lives.

CHANNELS
- Ship two live channels: in-app (persisted, badge + notification center) and email (via provider abstraction). Define an INotificationChannel interface with send + delivery-status callback.
- Add SMS and WhatsApp as future-channel STUBS: implement the interface, register them behind a feature flag OFF by default, log "not yet enabled". No live sends. Document the extension point in the README.

EVENT INTAKE
- Subscribe (idempotent, dedupe on event id) to domain events that require a notification: approval decisions (AuthApproved / AuthPartiallyApproved / AuthRejected / AuthInfoRequested / AuthEmergencyApproved), order/result availability (OrderLineAvailable, ResultReady), prescription-ready, appointment reminders/no-show.
- Map each event → recipient role(s) + channel(s) via a routing config: e.g., approval decision → requesting provider (in-app + email) + beneficiary channel; result ready → ordering doctor (in-app); pending-approval SLA breach → reviewer + Medical Director.

BILINGUAL TEMPLATES (AR/EN)
- Templates are versioned records with `{key, locale, subject, body}` for both `ar` and `en`. Render with the recipient's preferred locale; Arabic content is RTL and never machine-translated at send time — both locales are authored. Interpolate only min-necessary, non-clinical fields (e.g., AUTH key, status text, provider name) — NEVER diagnoses or clinical detail in a notification body.
- Status text uses the canonical non-color status vocabulary so in-app items match the design system.

ESCALATIONS
- Support time-based escalation rules: if an actionable event (e.g., InfoRequested, SLA-breaching pending approval) is not acted on within a window, escalate to the next recipient (supervisor/Medical Director). Configurable per event type.

DELIVERY TRACKING
- Persist per-notification delivery state: queued → sent → delivered/failed (+ retry with backoff for email). Expose GET /notifications (in-app inbox, per-user, min-necessary) and GET /notifications/{id}/delivery (status). Write an audit event for sends of sensitive-context notifications.

ACCEPTANCE (US-072)
- Given a relevant event (order available, approval decision, result ready), When it occurs, Then the correct role receives an in-app AND email notification in their locale, with no clinical payload in the body.
- Given an unacted actionable notification past its window, When the timer fires, Then it escalates to the configured next recipient.
- Given a failed email, When retried, Then delivery state reflects the outcome.
- Given SMS/WhatsApp, When triggered, Then they are stubbed/flagged-off and no live send occurs.

Tests: unit (routing + template render both locales + escalation timer), integration (event → notification → delivery state), contract (Pact) for consumed events, idempotency (duplicate event → one notification). OpenAPI + README (channel extension points).
```

### 8.2 — `reporting-service`: read-model / materialized KPI views + dashboard data APIs

```text
Build reporting-service (bounded context `reporting`, schema `reporting`). Read ../07, ../08 (NFR-006), ../11, US-073 first. This is a READ-MODEL service: it projects domain events into query-optimized materialized views. It never writes to source domains and never re-derives PHI.

READ-MODEL / MATERIALIZED VIEWS (refresh via event projections or scheduled materialized-view refresh)
Build KPI projections for:
- Approval TAT — avg / p95 turnaround by priority + reviewer + period (from approvals-service TAT + AuthUnderReview/decision events).
- Pending approvals — count by status, priority, age bucket, SLA-breach count.
- Clinic workload — encounters/visits per clinic/day, queue depth.
- Provider / drug / lab / radiology utilization — counts + trends per service line.
- No-show rate — booked vs attended vs no-show, per clinic/period.
- Top diagnoses / top medications — ranked frequency (aggregate counts ONLY, coded, no patient identifiers).
- Rejected requests — count + reason breakdown (from AuthRejected rationale categories).
- Financial summaries — utilization value, claims/cost by service line (financials zone).

MINIMIZATION (../11) — enforce in the projection, not just the API:
- Views are AGGREGATE and de-identified: no beneficiary identifiers, no free-text clinical notes, no row-level PHI. "Top diagnoses" holds coded counts, not patient-linked rows.
- FINANCIAL views MUST NOT contain diagnosis codes or clinical detail (finance ≠ diagnosis). Build financial projections from service codes/amounts only; assert this with a test that fails if a diagnosis column appears in any finance view.

DATA APIs (/api/v1) — every endpoint scoped by role/permission:
- GET /reports/approval-tat, /reports/pending-approvals, /reports/clinic-workload, /reports/utilization?dimension=provider|drug|lab|radiology, /reports/no-show, /reports/top-diagnoses, /reports/top-medications, /reports/rejected-requests, /reports/financial-summary.
- Params: period range, clinic/provider filters (explicit allow-list), granularity. Operational reports p95 ≤ 3 s (NFR-006); heavy/long-range queries return an async job handle with progress.

AUDIT OF EXPORTS
- Any export (CSV/PDF) endpoint writes an audit event (actor, report, filter, row count) via the shared client (../19). Exports honor the same field minimization as the API.

ACCEPTANCE (US-073)
- Given data, When a Medical Director/Manager queries the KPIs, Then TAT, pending approvals, clinic workload, and utilization are returned within budget, aggregated and PHI-free.
- Given the finance role, When it queries any report, Then no diagnosis/clinical field is present (verified by test).
- Given an export, When performed, Then it is audited.

Tests: unit (projection correctness), integration (event → view refresh → API), authz (finance cannot retrieve diagnosis-bearing views; role scoping), the "no diagnosis in finance view" assertion, performance smoke (p95 budget).
```

### 8.3 — Executive dashboard data contracts + accessible chart data

```text
Define the executive-dashboard data contracts served by reporting-service and consumed by the phase-9 Executive dashboard screen. Read ../08 (NFR-006, NFR-082), US-073 first.

DATA CONTRACTS
- Provide a single GET /dashboards/executive endpoint (or composed per-widget endpoints) returning a typed contract per widget: TAT trend, pending-approvals gauge, clinic-workload bars, utilization by service line, no-show trend, top diagnoses/medications, rejected-request breakdown, financial summary. Version the contract; publish as OpenAPI + shared TS types (zod-validated) in /libs/contracts.
- Each widget payload includes: series data, units, period, last_refreshed_at, and a `dataTable` representation (rows + column headers) — NOT just chart-shaped arrays.

ACCESSIBLE CHART DATA (acceptance gate)
- EVERY chart contract MUST carry an equivalent data-table alternative (labelled rows/columns) so the UI can render an accessible table toggle for each chart (WCAG — non-visual equivalent). No chart may ship without its dataTable.
- Include human-readable labels in both AR and EN for axes/series so the front end renders localized, RTL-correct tables and charts.
- Values are aggregate/PHI-free and finance widgets exclude diagnoses (inherit 8.2 rules).

ACCEPTANCE (US-073)
- Given the executive dashboard contract, When a widget is requested, Then it returns both chart series and a data-table alternative with localized AR/EN labels.
- Given any chart contract, When validated in CI, Then a missing dataTable fails the build.

Tests: contract tests asserting every widget has a dataTable + bilingual labels; schema validation; authz scoping.
```

---

## Guardrails

- **Minimum-necessary reporting.** All views are aggregate and de-identified — no row-level PHI, no free-text clinical notes. Finance views exclude diagnoses (assert with a failing-on-violation test). Notifications carry no clinical payload in their body.
- **Audit of exports and sensitive sends.** Every export and every sensitive-context notification writes a hash-chained audit event ([../19](../19-audit-strategy.md)).
- **Bilingual by construction.** Templates and dashboard labels are authored in AR and EN; Arabic is RTL and never machine-translated at send/render time.
- **Read-model only.** reporting-service never mutates source domains; projections are idempotent (dedupe on event id). SMS/WhatsApp remain flagged-off stubs.
- **Performance budgets.** Operational reports meet NFR-006 (p95 ≤ 3 s); heavier analytics run async with progress.

## Done when

- Alerts fire on the relevant events (order available, approval decision, result ready) to the correct role in their locale, with delivery tracked, escalations working, and SMS/WhatsApp safely stubbed.
- Dashboard KPIs — including **approval TAT** and **utilization** — are queryable within budget, aggregated and PHI-free, with finance views proven free of diagnoses.
- Every executive-dashboard chart contract ships with an **accessible data-table alternative** and bilingual labels; CI fails a chart lacking one.
- US-072 and US-073 acceptance criteria pass; unit/integration/contract/authz tests green; exports audited; OpenAPI + shared contracts + README updated. Global Definition of Done met.

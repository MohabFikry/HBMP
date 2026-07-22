---
name: Healthcare Reporting & KPIs
description: Mersal's KPI catalog and the read-model, minimum-necessary, accessible approach to reporting and analytics for the HBMP. Use when designing or reviewing reports, KPIs, metrics, dashboards data, or analytics queries — to pick the right metric, aggregate without leaking PHI, and meet the accessible-chart requirement.
---

# Healthcare Reporting & KPIs

## Purpose
Define what Mersal measures and how reporting is built so metrics are useful, fast, privacy-safe,
and accessible. Reporting sits on the HBMP spine as a downstream consumer of domain events — it
never becomes a back door around role-based minimum-necessary access.

## When to use / when not to use
- **Use when** choosing which KPI answers a question; specifying a report/analytics view; deciding
  the aggregation level and whether a field may appear; reviewing a chart for accessibility; or
  designing the read-model/materialized-view that feeds a report.
- **Not for** the visual dashboard layout and chrome (use `executive-dashboard-designer`), general
  UI components (`healthcare-uiux-designer`), or privacy law detail (`refugee-healthcare-management`).

## Mersal domain knowledge & rules
**KPI catalog (canonical starting set):**
- **Approval TAT** — turnaround time from approval request submitted → decided (median + p95),
  sliced by service type, provider, reviewer, urgency.
- **Pending approvals** — count and aging of `Submitted`/`UnderReview` authorizations; oldest-first.
- **Clinic / reception workload** — encounters, arrivals, queue length, appointments per site/day.
- **Provider utilization** — volume by provider (clinic/doctor/lab/imaging/pharmacy) vs. contracted
  capacity; consumed order lines per provider.
- **Drug utilization** — top medications dispensed, quantities, generic-substitution rate,
  formulary adherence (PBM view).
- **Lab / radiology utilization** — order volume, TAT to result, top tests/studies.
- **No-show rate** — booked vs. arrived, by site/specialty/time slot.
- **Top diagnoses / top medications** — coded (ICD-10 / ATC) frequency for operational planning.
- **Rejected requests** — approval and prescription rejection counts + reasons (denial-reason mix).
- **Financial summaries** — claims volume/value, provider payables, benefit spend vs. limits,
  reconciliation status. Uses **coded/minimized** service references — never diagnoses.

**Read-model / materialized-view approach.** Reports are served from **read models
(materialized views / projections)** built from domain events (`OrderConsumed`,
`PrescriptionDispensed`, `AuthorizationApproved`, …), not by querying live OLTP transactional
tables. This keeps operational latency low, lets heavy analytics run async with progress, and
means the report layer only ever sees **pre-aggregated, minimized projections** — a privacy
boundary as much as a performance one. Targets: operational report **p95 ≤ 3 s**; heavy
analytics may be async.

**Minimum-necessary aggregation (the privacy contract for analytics):**
- Aggregates carry **no direct identifiers** unless the viewer is explicitly authorized —
  de-identified/pseudonymized by default (NFR-044).
- **No PHI leakage through reports.** The role zoning holds in analytics too: **Finance sees
  cost/claim metrics but never diagnosis**; a diagnosis KPI is available to clinical/medical
  roles, not to Finance or operations. Never join cost data to diagnosis for a Finance-facing view.
- Small-cell risk: avoid counts so small they re-identify an individual; suppress or bucket
  low-N cells, especially by nationality/condition (refugee-sensitivity).
- **0 PHI/PII in logs**; the same applies to exported report files.

## Key entities/tokens/rules & invariants
- Every KPI declares: **definition, unit, time grain, slice dimensions, source events, owning
  role/scope, sensitivity tier.**
- Financial metrics → **T2**; clinical/diagnosis metrics → **T3** with need-to-know; operational
  counts → T0/T1. The viewer's role + scope decide which slices render.
- **Accessible chart requirement (hard, WCAG 2.2 AA):** every chart ships with a **text
  alternative + an accessible data table** conveying the same data, plus a short text summary of
  the takeaway. Charts encode series by **pattern fill + direct labels**, never hue alone; status
  in charts uses the color + icon + shape + text system. Non-text-content (1.1.1) and use-of-color
  (1.4.1) are release-gating.

## How to apply
1. Restate the question, then pick the **single KPI** (with grain + slices) that answers it; don't
   invent an ad-hoc metric if a catalog KPI fits.
2. Confirm the **viewer's role/scope** — expose only slices their tier permits; strip identifiers.
3. Source it from a **read model / materialized view**, not live clinical tables; note if it must
   be async.
4. Check **small-cell / PHI-leak** risk before shipping a breakdown, especially cost×clinical or
   low-N demographic cuts.
5. Pair every chart with a **data table + text summary**; use pattern + direct labels.

## Canonical references
- `../../08-non-functional-requirements.md` §5 Privacy (NFR-040/041/042/044), §1 report latency
  (NFR-006), §6 accessibility (NFR-050/053)
- `../../11-permission-matrix.md` (role×resource field rules, reporting scope)
- `../../21-accessibility-checklist.md` (chart data-table alternative, 1.1.1 / 1.4.1)
- `../../10-role-matrix.md` (who may read which reports) · `../../0A-DESIGN-FOUNDATIONS.md` §5.2
- Executive dashboard example: `../../prototype-hbmp-multiscreen.html` (KPIs + accessible SVG chart)

## Guardrails
- Never let a report expose a field the viewer's role cannot see in the operational UI — reporting
  is not an exemption from minimum-necessary.
- Never join financial and diagnosis data in a Finance-facing metric.
- Never ship a chart without its data-table + text-summary alternative.
- Never publish breakdowns with re-identifying small cells; suppress or bucket.

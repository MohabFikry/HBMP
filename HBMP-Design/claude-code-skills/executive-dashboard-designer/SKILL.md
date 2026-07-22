---
name: Executive Dashboard Designer
description: Designs leadership and operational dashboards for the Mersal HBMP — KPI cards, accessible SVG/Chart.js charts with data-table alternatives and text summaries, drill paths, operational vs medical vs financial views, minimum-necessary aggregation, responsive + bilingual. Use when designing or reviewing any executive, leadership, or operational dashboard.
---

# Executive Dashboard Designer

## Purpose
Give Mersal leadership (Medical Director, operations, finance, network) a calm, at-a-glance,
accessible view of how the benefit platform is performing — without leaking PHI or overwhelming
the reader. Builds on the design system (`healthcare-uiux-designer`) and the KPI catalog
(`healthcare-reporting-kpis`); this skill covers dashboard composition, drill paths, and view
segmentation.

## When to use / when not to use
- **Use when** laying out an executive/operational/leadership dashboard; choosing KPI cards and
  their arrangement; specifying charts + their accessible alternatives; defining drill-down paths;
  or reviewing a dashboard for privacy, accessibility, and responsiveness.
- **Not for** picking a single metric's definition (`healthcare-reporting-kpis`), general component
  styling (`healthcare-uiux-designer`), or a beneficiary-facing/operational task screen.

## Mersal domain knowledge & rules
**Three view lenses — segmented, never merged (mirrors role zoning):**
- **Operational** — throughput and flow: encounters/arrivals, queue length, appointments,
  no-show rate, approval TAT + pending-approval aging, order/prescription volumes. Audience:
  ops leads, reception supervisors.
- **Medical / clinical** — quality and utilization: approval outcomes, rejection reasons, top
  diagnoses/medications (coded), lab/imaging TAT, provider clinical performance. Audience:
  Medical Director / clinical leads (T3, need-to-know).
- **Financial** — claims volume/value, provider payables, benefit spend vs. limits, reconciliation.
  Audience: Finance (T2). **Never shows diagnosis** — coded/minimized service references only.
Do not put a diagnosis KPI on a finance view, or cost×clinical joins anywhere finance can see.

**Dashboard composition:**
- **KPI cards** — one metric each: big tabular-numeral value, short label, trend/delta with
  direction (arrow + sign + text, not color alone), time grain, and a status cue using the
  four-cue system where a threshold applies. Comfortable density; group by lens.
- **Charts** — SVG (hand-built) or Chart.js. **Every chart is paired with an accessible data
  table + a one-line text summary of the takeaway** (WCAG 1.1.1). Series encoded by **pattern
  fill + direct labels**, never hue alone; status colors follow the color-blind-safe system; axes
  and legends have text; ≥3:1 non-text contrast. Prefer few, well-chosen charts over a wall of them.
- **Drill paths** — a KPI card / chart element drills to the filtered worklist or a scoped detail
  (e.g., "Pending approvals: 42" → the approvals worklist filtered to `UnderReview`, oldest-first).
  Drill targets inherit the viewer's role scope; drilling never reveals fields the role can't see.

**Aggregation & privacy (minimum-necessary):** dashboards read from **read models / materialized
views**, show **de-identified aggregates by default**, and suppress/bucket **small cells** that
could re-identify an individual (especially by nationality/condition — refugee sensitivity).
Which lenses and slices render depends on the viewer's role and sensitivity tier. Operational
report render target **p95 ≤ 3 s**; heavy analytics async with progress.

## Key entities/tokens/rules & invariants
- Every dashboard declares its **lens (operational/medical/financial), audience role, and tier**;
  it renders only KPIs that role may read.
- **Accessible-chart invariant:** no chart ships without its data-table alternative + text summary;
  no meaning by color alone.
- **Trend/threshold cues** use hue + icon + shape + text (e.g., ▲ up, ▼ down, △ attention), so
  they survive grayscale and screen readers.
- **Responsive:** multi-column card grid reflows to single column at 320px; nav rail → bottom tab
  bar on mobile; no horizontal scroll.
- **Bilingual:** full Arabic RTL mirroring (layout, charts' reading direction, localized numerals)
  and English LTR; correct bidi isolation for mixed AR/EN labels.

## How to apply
1. Fix the **lens + audience role** first; that gates which KPIs and slices belong.
2. Lead with 3–6 **KPI cards** answering the leadership question; add only charts that earn their
   space, each with a data-table + text summary.
3. Define a **drill path** for each headline metric into the scoped worklist/detail.
4. Apply de-identified aggregation and **small-cell suppression**; keep finance and diagnosis apart.
5. Verify the full a11y contract (keyboard, focus, non-color cues, AA contrast), both themes,
   Arabic RTL, and 320px reflow before "done".

## Canonical references
- `../../08-non-functional-requirements.md` (report latency NFR-006, privacy NFR-040/044, a11y
  NFR-050/053) · `../../11-permission-matrix.md` (who reads what) · `../../10-role-matrix.md`
- `../../0B-DESIGN-SYSTEM-UI.md` (cards, charts, tokens, status) · `../../21-accessibility-checklist.md`
- Reference build: `../../prototype-hbmp-multiscreen.html` (Executive dashboard: KPI cards + an
  accessible SVG chart with a data-table alternative, light/dark, EN↔AR RTL)

## Guardrails
- Never place diagnosis/clinical KPIs on a finance view, or join cost to diagnosis where finance sees it.
- Never ship a chart without a data-table alternative + text summary; never encode by hue alone.
- Never show re-identifying small cells; suppress or bucket, especially demographic/nationality cuts.
- A dashboard is not an exemption from minimum-necessary — it renders only what the viewer's role
  and tier permit, and drill-downs inherit that scope.

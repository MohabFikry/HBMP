import { z } from "zod";
import { zId, zLocalized, zStatus } from "./common";

/**
 * Executive dashboard contract (Phase 8.3) — the shared mirror the reporting-service emits and the web app
 * renders. THREE invariants are baked into the schema:
 *   1. PHI-free & aggregate — every widget is counts/rates over de-identified fact tables, never a patient row.
 *   2. Every chart carries an accessible `dataTable` (US-073) — the schema makes the table non-optional, so a
 *      chart literally cannot ship without its tabular alternative.
 *   3. Bilingual by construction — every human label is `{en, ar}`.
 * Finance-scoped dashboards reuse this shape but the server omits any diagnosis breakdown (finance ≠ diagnosis).
 */

/** A single categorical data point (bar/line/donut). `value` is numeric; `display` is the formatted string. */
export const zSeriesPoint = z.object({
  label: zLocalized,
  value: z.number(),
  display: z.string(),
});
export type SeriesPoint = z.infer<typeof zSeriesPoint>;

/** The accessible data-table that MUST accompany every chart (US-073). */
export const zDataTable = z.object({
  columns: z.array(zLocalized).min(1),
  rows: z.array(z.array(z.string())).min(0),
});
export type DashDataTable = z.infer<typeof zDataTable>;

export const zKpiWidget = z.object({
  kind: z.literal("kpi"),
  id: zId,
  title: zLocalized,
  value: z.string(),
  delta: z.string().optional(),
  direction: z.enum(["up", "down"]).optional(),
  /** Status pill (e.g. TAT within SLA) — four-cue safe. */
  status: zStatus.optional(),
  /**
   * The detail behind the headline, when the server sent one.
   *
   * Optional because not every KPI has a breakdown — but the two that DO were losing theirs. The server
   * marks pending-approvals and the financial summary as Gauge and Summary widgets, and the client mapped
   * both to a bare `{ title, value }`, discarding a table it had already computed, serialised and sent:
   * pending by status x priority x age x SLA breach, and cost by service line. Neither rendered anywhere in
   * the product. A KPI is a headline, and a headline with no article behind it is where a supervisor stops.
   */
  dataTable: zDataTable.optional(),
});
export type KpiWidget = z.infer<typeof zKpiWidget>;

export const zChartWidget = z.object({
  kind: z.literal("chart"),
  id: zId,
  title: zLocalized,
  chartType: z.enum(["bar", "line", "donut"]),
  series: z.array(zSeriesPoint),
  /** REQUIRED accessible alternative — not optional (US-073). */
  dataTable: zDataTable,
});
export type ChartWidget = z.infer<typeof zChartWidget>;

export const zWidget = z.discriminatedUnion("kind", [zKpiWidget, zChartWidget]);
export type Widget = z.infer<typeof zWidget>;

/**
 * The window a figure covers.
 *
 * Every reporting endpoint takes `from`/`to` and the portal sent neither, so two KPIs built from endpoints
 * with different server defaults (30 days and 90) sat in one row with nothing on screen saying so. A number
 * whose period is unstated is a number a supervisor cannot act on.
 */
export const zPeriod = z.object({ from: z.string(), to: z.string() });
export type Period = z.infer<typeof zPeriod>;

export const zExecutiveDashboard = z.object({
  /** Report version so the UI can pin to a contract (v1.0). */
  version: z.string(),
  generatedAt: z.string(),
  /** The resolved window, echoed back so the screen states what it is showing rather than assuming. */
  period: zPeriod.optional(),
  /** Zone label so the same shape can render an executive OR a finance-scoped board. */
  scope: z.enum(["executive", "finance", "director"]),
  kpis: z.array(zKpiWidget),
  charts: z.array(zChartWidget),
});
export type ExecutiveDashboard = z.infer<typeof zExecutiveDashboard>;

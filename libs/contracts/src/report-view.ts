import { z } from "zod";
import { zLocalized } from "./common";

/**
 * A generic, PHI-free report view (Phase 8.2/8.3 director oversight & quality). The reporting service emits
 * de-identified aggregates (counts, rates, coded tallies) — never a beneficiary. Screens render a set of KPI
 * headline figures plus one or more accessible data tables (bilingual headers; string cells for a11y parity
 * with the executive dashboard's dataTable requirement, US-073).
 */
export const zReportKpi = z.object({
  label: zLocalized,
  value: z.string(),
});
export type ReportKpi = z.infer<typeof zReportKpi>;

export const zReportTable = z.object({
  title: zLocalized,
  columns: z.array(zLocalized),
  rows: z.array(z.array(z.string())),
});
export type ReportTable = z.infer<typeof zReportTable>;

export const zReportView = z.object({
  kpis: z.array(zReportKpi),
  tables: z.array(zReportTable),
});
export type ReportView = z.infer<typeof zReportView>;

import { z } from "zod";
import { zId, zLocalized, zStatus } from "./common";

/**
 * Finance contracts (Phase 10.2/10.3). The Finance portal is minimum-necessary: **billing codes + quantities +
 * amounts + masked-min references only**. There is deliberately NO diagnosis / clinical field on any schema here —
 * finance ≠ diagnosis (11-permission-matrix §4). A contract test asserts none of these shapes name a clinical field.
 */

/** A utilization row — authorized-vs-delivered + spend for a billing service code. No clinical field. */
export const zUtilizationRow = z.object({
  serviceCode: z.string(),
  serviceLine: zLocalized,
  coverageCategory: zLocalized,
  providerRef: z.string().optional(), // masked reference, never a name
  authorizedQty: z.number().int().nonnegative(),
  deliveredQty: z.number().int().nonnegative(),
  spend: z.string(), // pre-formatted amount, e.g. "EGP 12,400"
});
export type UtilizationRow = z.infer<typeof zUtilizationRow>;

export const zUtilizationView = z.object({
  from: z.string(),
  to: z.string(),
  rows: z.array(zUtilizationRow),
  totalAuthorized: z.number().int().nonnegative(),
  totalDelivered: z.number().int().nonnegative(),
  totalSpend: z.string(),
});
export type UtilizationView = z.infer<typeof zUtilizationView>;

export const zSummaryBucket = z.object({
  key: zLocalized,
  deliveredQty: z.number().int().nonnegative(),
  spend: z.string(),
  sharePercent: z.number().min(0).max(100),
});
export type SummaryBucket = z.infer<typeof zSummaryBucket>;

/** A financial summary — spend/qty roll-up by a BILLING dimension (service-line / category / provider). */
export const zFinancialSummary = z.object({
  dimension: z.enum(["serviceline", "category", "provider"]),
  buckets: z.array(zSummaryBucket),
  totalSpend: z.string(),
});
export type FinancialSummary = z.infer<typeof zFinancialSummary>;

export const zSettlementStatus = z.enum(["draft", "submitted", "approved", "paid"]);
export type SettlementStatus = z.infer<typeof zSettlementStatus>;

export const zSettlementLine = z.object({
  serviceCode: z.string(),
  serviceLine: zLocalized,
  deliveredQty: z.number().int().nonnegative(),
  agreedUnitPrice: z.string(),
  lineTotal: z.string(),
});
export type SettlementLine = z.infer<typeof zSettlementLine>;

export const zSettlement = z.object({
  id: zId,
  settlementNo: z.string(),
  providerRef: z.string(),
  providerName: zLocalized,
  periodStart: z.string(),
  periodEnd: z.string(),
  currency: z.string(),
  total: z.string(),
  status: zStatus,
  state: zSettlementStatus,
  lines: z.array(zSettlementLine),
});
export type Settlement = z.infer<typeof zSettlement>;

/** Result of an export — the download plus the audited row count (a data.export event is written server-side). */
export const zExportResult = z.object({
  report: z.string(),
  format: z.enum(["csv", "xlsx"]),
  rowCount: z.number().int().nonnegative(),
  filename: z.string(),
  status: zStatus,
});
export type ExportResult = z.infer<typeof zExportResult>;

export const zExportRequest = z.object({
  report: z.enum(["utilization", "settlement", "summary"]),
  format: z.enum(["csv", "xlsx"]),
  from: z.string(),
  to: z.string(),
});
export type ExportRequest = z.infer<typeof zExportRequest>;

import { z } from "zod";
import { zDate, zId, zLocalized, zStatus } from "./common";

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
  /**
   * 18.D2 (audit R2 U7) — a RAW number, formatted at render.
   *
   * This was a pre-formatted string built with `toLocaleString("en-US")` and an "EGP " prefix, so the Arabic
   * UI showed Western digits and an English currency label inside an otherwise Arabic page. A formatted
   * string also cannot be re-localised, summed, or sorted numerically by any consumer. The number crosses
   * the wire; `useFormat().money()` turns it into EGP in the active locale at the point of display.
   */
  spend: z.number(),
});
export type UtilizationRow = z.infer<typeof zUtilizationRow>;

export const zUtilizationView = z.object({
  from: z.string(),
  to: z.string(),
  rows: z.array(zUtilizationRow),
  totalAuthorized: z.number().int().nonnegative(),
  totalDelivered: z.number().int().nonnegative(),
  totalSpend: z.number(),
});
export type UtilizationView = z.infer<typeof zUtilizationView>;

export const zSummaryBucket = z.object({
  key: zLocalized,
  deliveredQty: z.number().int().nonnegative(),
  spend: z.number(),
  sharePercent: z.number().min(0).max(100),
});
export type SummaryBucket = z.infer<typeof zSummaryBucket>;

/** A financial summary — spend/qty roll-up by a BILLING dimension (service-line / category / provider). */
export const zFinancialSummary = z.object({
  dimension: z.enum(["serviceline", "category", "provider"]),
  buckets: z.array(zSummaryBucket),
  totalSpend: z.number(),
});
export type FinancialSummary = z.infer<typeof zFinancialSummary>;

export const zSettlementStatus = z.enum(["draft", "submitted", "approved", "paid"]);
export type SettlementStatus = z.infer<typeof zSettlementStatus>;

/**
 * Where a settlement line's price came from.
 *
 * <p>`Contract` — the provider's agreed price book named this code. `ObservedFloor` — it did not, and the
 * line is priced at the LOWEST unit cost observed for the code in the period: a floor, pending a tariff,
 * which can only under-state.</p>
 */
export const zPriceSource = z.enum(["Contract", "ObservedFloor"]);
export type PriceSource = z.infer<typeof zPriceSource>;

export const zSettlementLine = z.object({
  serviceCode: z.string(),
  serviceLine: zLocalized,
  deliveredQty: z.number().int().nonnegative(),
  agreedUnitPrice: z.number(),   // 18.D2 (U7): raw; formatted at render
  lineTotal: z.number(),
  /**
   * Whether a tariff priced this line.
   *
   * <p>The server has always projected it, with a comment on the domain field saying exactly why: *a
   * reviewer issuing the draft has to be able to tell them apart*. The client dropped it. So at the moment
   * of authorising a payment, a column of "agreed prices" mixed the contract's tariff with a floor this
   * platform inferred because no tariff exists, rendered identically.</p>
   */
  priceSource: zPriceSource,
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
  total: z.number(),
  status: zStatus,
  state: zSettlementStatus,
  lines: z.array(zSettlementLine),
  /**
   * Who submitted this settlement, and who approved it. Staff subject ids.
   *
   * <p>Carried so segregation of duties can be honoured BEFORE the click. The service compares the
   * submitter against the approving principal and answers 409 `urn:hbmp:sod-violation` when they match;
   * without this the screen offers the submitter an Approve button and then refuses it, which is a control
   * working correctly and reading as a defect.</p>
   *
   * <p>The refusal stays — the client is not the authority on who may release a payment. It is merely no
   * longer the only place the rule becomes visible.</p>
   */
  submittedBy: z.string().nullish(),
  approvedBy: z.string().nullish(),
});
export type Settlement = z.infer<typeof zSettlement>;

/** Generate a draft settlement for one provider and one period. */
export const zGenerateSettlementRequest = z.object({
  providerId: zId,
  periodStart: zDate,
  periodEnd: zDate,
}).refine((v) => v.periodStart <= v.periodEnd, {
  path: ["periodEnd"],
  message: "period.reversed",
});
export type GenerateSettlementRequest = z.infer<typeof zGenerateSettlementRequest>;

/** The list plus how many there actually are — the endpoint caps at 100 and says so on `X-Total-Count`. */
export const zSettlementPage = z.object({
  rows: z.array(zSettlement),
  total: z.number().int().nonnegative(),
});
export type SettlementPage = z.infer<typeof zSettlementPage>;

/**
 * Result of an export — what was DELIVERED, plus the audited row count.
 *
 * <p>The file is the deliverable and the row count is the receipt. Previously this was the whole of it: the
 * server returned `text/csv` through `Results.File`, the client parsed the response as JSON, and the screen
 * showed a count while handing the operator nothing at all.</p>
 *
 * <p>`format` no longer offers `xlsx`. The endpoint has only ever produced CSV — and stored the *claimed*
 * format in the export ledger, so it asserted spreadsheets that were never generated. A CSV opens in Excel;
 * the gap is not worth a spreadsheet library in the one service whose security argument is that it cannot
 * express a clinical field.</p>
 */
export const zExportResult = z.object({
  report: z.string(),
  format: z.literal("csv"),
  rowCount: z.number().int().nonnegative(),
  filename: z.string(),
  status: zStatus,
});
export type ExportResult = z.infer<typeof zExportResult>;

export const zExportRequest = z.object({
  report: z.enum(["utilization", "settlement", "summary"]),
  format: z.literal("csv"),
  from: zDate,
  to: zDate,
  /** Narrows utilization and settlement exports. Both are accepted by the endpoint and neither was sent. */
  category: z.string().optional(),
  providerId: zId.optional(),
  /** Which dimension a `summary` export groups by — so the file matches the roll-up on screen. */
  dimension: z.enum(["serviceline", "category", "provider"]).optional(),
});
export type ExportRequest = z.infer<typeof zExportRequest>;

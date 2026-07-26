import { z } from "zod";
import { zId, zInstant, zStatus } from "./common";

/**
 * Claims-officer portal contracts (Phase 10b, design 36). These back the claims worklist, reconciliation
 * worklist and the PHI-free KPI dashboard. Min-necessary is structural: a claim carries CODES + AMOUNTS
 * only — never a diagnosis (finance-parity: the claims zone cannot read clinical narrative). Statuses are
 * pre-resolved to non-colour StatusChip kinds so the UI never depends on hue alone.
 */

/** A claim on the officer worklist (36 §4) — codes + amounts + lifecycle status, no clinical narrative. */
export const zClaimRow = z.object({
  id: zId,
  claimNo: z.string(),
  /** AutoDerived | Provider | Reimbursement — display verbatim. */
  origin: z.string(),
  status: zStatus,
  currency: z.string(),
  claimedAmount: z.number(),
  netPayable: z.number().nullable().optional(),
  /** ISO date (service-from) — display only. */
  serviceDateFrom: z.string(),
  submittedAt: zInstant.optional(),
});
export type ClaimRow = z.infer<typeof zClaimRow>;

/** A reconciliation worklist line (36 §7) — a delivered/billed/coded signal, bucketed by the classifier. */
export const zReconciliationRow = z.object({
  claimId: zId,
  claimNo: z.string(),
  origin: z.string(),
  code: z.string(),
  serviceDate: z.string(),
  billedAmount: z.number(),
  allowedAmount: z.number().nullable().optional(),
  /** Matched | BilledNotDelivered | DeliveredNotBilled | PriceVariance | … — the reconciliation bucket. */
  bucket: z.string(),
  status: zStatus,
});
export type ReconciliationRow = z.infer<typeof zReconciliationRow>;

/** One denial-reason tally for the KPI dashboard. */
export const zDenialReason = z.object({ reason: z.string(), count: z.number().int() });

/** Claims KPIs (36 §11) — PHI-free operational metrics. Rates are 0–1; TAT is submission→decision hours. */
export const zClaimsKpis = z.object({
  averageTatHours: z.number(),
  approvalRate: z.number(),
  denialRate: z.number(),
  ocrAutoMatchRate: z.number(),
  agedUnbilledCount: z.number().int(),
  agedUnbilledValue: z.number(),
  recoveryOutstanding: z.number(),
  topDenialReasons: z.array(zDenialReason),
});
export type ClaimsKpis = z.infer<typeof zClaimsKpis>;

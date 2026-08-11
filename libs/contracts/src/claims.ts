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
  /** The row's real identity. Keying on claimId + code collided for two lines of one claim carrying the same
   *  code on different quantities — which is precisely what the QuantityVariance bucket describes. */
  claimLineId: zId,
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

// ------------------------------------------------------------------------------------------------------------
// Adjudication (Phase 10b.4) — the officer's actual job, which had no interface until now.
//
// `claims_officer` holds claims:decide, :adjudicate, :adjust and :appeal, and the portal was three read
// screens. The types below back the line queue, the decision form and the adjustment ledger. Same boundary as
// everything above it: codes, amounts, reason codes and linkage. `resultExists` is a BOOLEAN derived from the
// fulfilment linkage so the officer can confirm the service was rendered without reading what it found.
// ------------------------------------------------------------------------------------------------------------

/** The 15 adjudication reason codes (`services/claims/Domain/ReasonCodes.cs`). Picked, never typed. */
export const CLAIM_REASON_CODES = [
  "NOT_ELIGIBLE", "POLICY_EXPIRED", "NOT_COVERED_CATEGORY", "NO_PRIOR_AUTH", "AUTH_EXPIRED",
  "EXCEEDS_AUTH_SCOPE", "NO_FULFILLMENT_RECORD", "DUPLICATE_CLAIM", "PROVIDER_OUT_OF_NETWORK",
  "CONTRACT_NOT_EFFECTIVE", "NO_TARIFF", "LIMIT_EXCEEDED", "NOT_MEDICALLY_NECESSARY",
  "ILLEGIBLE_DOCUMENT", "RECEIPT_MISMATCH",
] as const;
export type ClaimReasonCode = (typeof CLAIM_REASON_CODES)[number];

/** One line awaiting adjudication (`GET /api/v1/claims/worklist`) — one row per LINE, not per claim. */
export const zAdjudicationRow = z.object({
  claimId: zId,
  claimNo: z.string(),
  claimLineId: zId,
  serviceDate: z.string(),
  codeSystem: z.string(),
  code: z.string(),
  description: z.string().nullable().optional(),
  quantity: z.number(),
  billedAmount: z.number(),
  contractPrice: z.number().nullable().optional(),
  allowedAmount: z.number().nullable().optional(),
  status: zStatus,
  /** The engine's recommendation — Approve / Deny / RequiresManualReview / … — or null if not adjudicated. */
  systemRecommendation: z.string().nullable().optional(),
  reasonCodes: z.array(z.string()),
  authorizationId: zId.nullable().optional(),
  /** Whether a fulfilment record exists. The service was rendered — not what it found. */
  resultExists: z.boolean(),
});
export type AdjudicationRow = z.infer<typeof zAdjudicationRow>;

/** Decision kinds accepted by `POST /claims/{id}/lines/{lineId}/decisions`. */
export const CLAIM_DECISION_KINDS = ["Approve", "PartiallyApprove", "Deny", "Adjust", "RequestInfo", "RouteToClinical"] as const;
export type ClaimDecisionKind = (typeof CLAIM_DECISION_KINDS)[number];

export const zClaimDecisionRequest = z
  .object({
    claimId: zId,
    claimLineId: zId,
    decision: z.enum(CLAIM_DECISION_KINDS),
    allowedAmount: z.number().nonnegative().optional(),
    reasonCodes: z.array(z.string()),
    rationale: z.string(),
    /** The second approver's confirmation of a decision held for dual control. */
    confirmsDecisionId: zId.optional(),
  })
  // The same two rules the server enforces, checked here so the reviewer is told before the round trip rather
  // than after writing a rationale the form then throws away.
  .refine((d) => d.decision === "Approve" || d.rationale.trim().length > 0, {
    path: ["rationale"],
    message: "A rationale is required for anything other than a plain approval.",
  })
  .refine((d) => !["PartiallyApprove", "Adjust"].includes(d.decision) || d.allowedAmount !== undefined, {
    path: ["allowedAmount"],
    message: "An allowed amount is required for a partial approval or an adjustment.",
  });
export type ClaimDecisionRequest = z.infer<typeof zClaimDecisionRequest>;

/**
 * What came back.
 *
 * `PendingSecondApproval` is an OUTCOME, not a failure. The decision exceeded the dual-control threshold and is
 * held for a second, distinct approver; the server returns 202 and the line keeps its decision id. Rendering
 * that as an error would teach reviewers that the threshold is a malfunction.
 *
 * The three `SOD_*` reasons are likewise the control working. Each gets its own sentence in the UI, because a
 * 403 reading only "forbidden" on a segregation-of-duties refusal reads as a broken system.
 */
export const zClaimDecisionResult = z.object({
  outcome: z.enum(["Recorded", "Confirmed", "Replayed", "PendingSecondApproval"]),
  decisionId: zId,
  lineStatus: z.string().optional(),
  claimStatus: z.string().optional(),
  allowedAmount: z.number().nullable().optional(),
});
export type ClaimDecisionResult = z.infer<typeof zClaimDecisionResult>;

/** The nine adjustment kinds (`AdjustmentType`). An adjustment moves money on an already-decided claim. */
export const CLAIM_ADJUSTMENT_TYPES = [
  "PriceCorrection", "QuantityCorrection", "Deduction", "Recovery", "Clawback", "Writeoff",
  "Reversal", "Void", "Reallocation",
] as const;
export type ClaimAdjustmentType = (typeof CLAIM_ADJUSTMENT_TYPES)[number];

/** An adjustment already raised against a claim (`GET /claims/{id}/adjustments`). Append-only, before → after. */
export const zClaimAdjustment = z.object({
  adjustmentId: zId,
  claimLineId: zId.nullable().optional(),
  type: z.string(),
  amountDelta: z.number(),
  beforeAmount: z.number().nullable().optional(),
  afterAmount: z.number().nullable().optional(),
  reasonCode: z.string().nullable().optional(),
  adjustedAt: zInstant,
});
export type ClaimAdjustment = z.infer<typeof zClaimAdjustment>;

/** The full claim behind a worklist row — its lines and the adjustments raised against it. */
export const zClaimDetail = z.object({
  id: zId,
  claimNo: z.string(),
  origin: z.string(),
  status: zStatus,
  currency: z.string(),
  claimedAmount: z.number(),
  approvedAmount: z.number().nullable().optional(),
  adjustedAmount: z.number().nullable().optional(),
  netPayable: z.number().nullable().optional(),
  serviceDateFrom: z.string(),
  submittedAt: zInstant.optional(),
  lines: z.array(
    z.object({
      claimLineId: zId,
      codeSystem: z.string(),
      code: z.string(),
      description: z.string().nullable().optional(),
      quantity: z.number(),
      billedAmount: z.number(),
      contractPrice: z.number().nullable().optional(),
      allowedAmount: z.number().nullable().optional(),
      status: zStatus,
      reasonCodes: z.array(z.string()),
    }),
  ),
});
export type ClaimDetail = z.infer<typeof zClaimDetail>;

/**
 * The six reconciliation buckets.
 *
 * The portal offered three. The missing ones were `Duplicate` — the double-billing signal — `DeliveredNotBilled`,
 * which is money the platform is owed and never asked for, and `QuantityVariance`. All three were being
 * classified server-side and were unselectable, and invisible under "All", which is the absence of a filter
 * rather than a bucket.
 */
export const RECON_BUCKETS = [
  "Matched", "PriceVariance", "QuantityVariance", "BilledNotDelivered", "DeliveredNotBilled", "Duplicate",
] as const;
export type ReconBucket = (typeof RECON_BUCKETS)[number];

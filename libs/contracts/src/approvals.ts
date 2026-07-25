import { z } from "zod";
import { zCoded, zId, zInstant, zLocalized, zPatientRef, zPriority, zSla, zStatus } from "./common";

/**
 * Medical approvals (Phase 7, US-060). Worklist + a field-scoped review + a decision panel whose rationale
 * rules are enforced BOTH here (zod refine) and on the server. Reject/partial without a rationale is
 * structurally rejected by `zDecisionRequest`.
 */
export const zApprovalItem = z.object({
  id: zId,
  patient: zPatientRef,
  service: zCoded,
  requestedBy: zLocalized,
  priority: zPriority,
  sla: zSla,
  status: zStatus,
  submittedAt: zInstant,
  estimatedCost: z.string(),
});
export type ApprovalItem = z.infer<typeof zApprovalItem>;

/**
 * The review payload — a FIELD-SCOPED clinical excerpt (min-necessary): the coded reason + a short clinical
 * justification + attached document names. Not the full EMR. Reading it is audited server-side (purpose-of-use).
 */
export const zApprovalReview = z.object({
  id: zId,
  patient: zPatientRef,
  service: zCoded,
  clinicalJustification: z.string(),
  supportingCodes: z.array(zCoded),
  documents: z.array(z.object({ id: zId, name: z.string() })),
  requestedAmount: z.string(),
});
export type ApprovalReview = z.infer<typeof zApprovalReview>;

export const zDecisionKind = z.enum(["approve", "partial", "reject", "request_info"]);
export type DecisionKind = z.infer<typeof zDecisionKind>;

export const zBreakGlassKind = z.enum(["emergency", "override", "manual"]);
export type BreakGlassKind = z.infer<typeof zBreakGlassKind>;

/**
 * A decision. The refine encodes US-060: reject / partial / request-info REQUIRE a non-empty rationale, and a
 * break-glass decision REQUIRES an extra justification. Approve may carry an optional note.
 */
export const zDecisionRequest = z
  .object({
    approvalId: zId,
    idempotencyKey: z.string().uuid(),
    decision: zDecisionKind,
    rationale: z.string().default(""),
    /** For partial approvals — the approved amount/scope. */
    approvedAmount: z.string().optional(),
    breakGlass: z
      .object({ kind: zBreakGlassKind, justification: z.string() })
      .optional(),
  })
  .superRefine((v, ctx) => {
    const needsRationale = v.decision === "reject" || v.decision === "partial" || v.decision === "request_info";
    if (needsRationale && v.rationale.trim().length === 0) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["rationale"], message: "rationale.required" });
    }
    if (v.decision === "partial" && !v.approvedAmount) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["approvedAmount"], message: "approvedAmount.required" });
    }
    if (v.breakGlass && v.breakGlass.justification.trim().length === 0) {
      ctx.addIssue({ code: z.ZodIssueCode.custom, path: ["breakGlass", "justification"], message: "justification.required" });
    }
  });
export type DecisionRequest = z.infer<typeof zDecisionRequest>;

export const zDecisionResult = z.object({
  approvalId: zId,
  decisionId: zId,
  status: zStatus,
  replayed: z.boolean(),
});
export type DecisionResult = z.infer<typeof zDecisionResult>;

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
  /**
   * NULL on a fulfilment authorization, because there is no SLA on one.
   *
   * <p>Nothing waited on anybody: the medicine is already in the patient's hand. The worklist used to
   * fabricate a due date from the submission time when the server sent none, which put a countdown on
   * settled work — a clock ticking towards a deadline that does not exist.</p>
   */
  sla: zSla.nullish(),
  status: zStatus,
  submittedAt: zInstant,
  /** Every requested service code, not just the first. */
  serviceCodes: z.array(z.string()),
  /** Who is holding this, or null if nobody has picked it up. A staff id, not patient data. */
  assignedReviewerId: zId.nullish(),
  /** The provider that asked. Null on a manual authorization, which by definition has none. */
  requestingProviderId: zId.nullish(),
  /**
   * What KIND of request this is.
   *
   * <p>A validity extension is not a benefit authorization: it has no service code, no cost and no clinical
   * justification, and a reviewer who opens it expecting those has been misled by a queue that showed every
   * row the same way. This is the first thing anyone triaging needs, so it is on the row.</p>
   */
  source: z.enum(["OrderLine", "Prescription", "Manual", "ValidityExtension"]),
  /** The expired item's reference on an extension request — RX-2026-000312. Null otherwise. */
  itemReference: z.string().nullish(),
  /**
   * The requester's stated reason, on extension rows only.
   *
   * <p>It is the ENTIRE substance of that decision. Requiring a reviewer to open the PHI-audited clinical
   * review view to read one logistics sentence would add an audited access to the patient's record for a
   * question that is not about the patient.</p>
   */
  extensionReason: z.string().nullish(),
  /**
   * A question waiting for an answer, or a record of something already handed over (ADR-0034).
   *
   * <p>Both live in one aggregate and one AUTH- number space, because the approval team is accountable for
   * both. They are not both work: a reviewer triaging a row needs to know which of the two they are looking
   * at before anything else on it means something, and the inbox defaults to `Review` so a few hundred
   * dispenses a day cannot drown the twelve requests that need a decision.</p>
   */
  kind: z.enum(["Review", "Fulfilment"]),
});
export type ApprovalItem = z.infer<typeof zApprovalItem>;

/**
 * One delivered thing on a fulfilment authorization.
 *
 * <p><b>`orderedCode` and `fulfilledCode` are two fields, not one field plus a flag.</b> A substitution is
 * not an edit to what the prescriber decided: writing the delivered molecule into the field that held the
 * prescribed one would destroy the record of the clinical decision, which is the fact a reviewer most needs.
 * The prescription itself is never written to — this row is the only place the difference exists.</p>
 *
 * <p>Codes, labels, a quantity and — only when the two differ — the substituting pharmacist's reason. No
 * diagnosis, no note: this answers "what was delivered against RX-2026-000410", which is a benefit question
 * rather than a clinical one.</p>
 */
export const zAuthorizationItem = z.object({
  itemId: zId,
  sourceLineId: zId.nullish(),
  orderedCode: z.string(),
  orderedLabel: z.string().nullish(),
  fulfilledCode: z.string(),
  fulfilledLabel: z.string().nullish(),
  quantity: z.number(),
  substituted: z.boolean(),
  substitutionReason: z.string().nullish(),
  fulfilledAt: zInstant,
});
export type AuthorizationItem = z.infer<typeof zAuthorizationItem>;

/**
 * The review payload — a FIELD-SCOPED clinical excerpt (min-necessary): the coded reason + a short clinical
 * justification + attached document names. Not the full EMR. Reading it is audited server-side (purpose-of-use).
 */
export const zApprovalReview = z.object({
  id: zId,
  patient: zPatientRef,
  service: zCoded,
  clinicalJustification: z.string(),
  /**
   * Every requested service — NOT "supporting codes", which is what they were called.
   *
   * <p>They were never supporting anything. `serviceCodes[0]` was rendered as "the service" and
   * `slice(1)` as attachments to it, so a three-service request read as one service with two footnotes.</p>
   */
  requestedServices: z.array(zCoded),
  documents: z.array(z.object({ id: zId, name: z.string() })),
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

/**
 * The approvals engine's routing and SLA rules (ADR-0035 §5).
 *
 * <p>These two families change WHO decides and BY WHEN — never WHAT is decided. Nothing here can approve or
 * refuse anything, which is why they are the first families built: they prove the rule infrastructure without
 * putting a benefit decision behind it.</p>
 */
export const zRulePredicate = z.object({
  priority: z.enum(["Routine", "Urgent", "Emergency"]).nullish(),
  source: z.enum(["OrderLine", "Prescription", "Manual"]).nullish(),
  kind: z.enum(["Review", "Fulfilment"]).nullish(),
  /** Matches when the request carries ANY of these. The list is still ANDed with the other fields. */
  serviceCodes: z.array(z.string()).nullish(),
  requestingProviderId: z.string().nullish(),
  /** CONSULT / LAB / IMAGING / PHARMACY / REFERRAL — the closed vocabulary coverage is constrained to. */
  benefitCategory: z.string().nullish(),
  /** Matches at or above. An UNKNOWN amount does NOT clear the floor — absent is not small. */
  amountAtLeast: z.number().nullish(),
});
export type RulePredicate = z.infer<typeof zRulePredicate>;

export const zApprovalRule = z.object({
  id: zId,
  family: z.enum(["Routing", "Sla", "Preauth", "AutoApprove"]),
  /** Lower runs first. Ties break on id, so the order is total and the same request always resolves the same. */
  priority: z.number().int(),
  predicate: z.string(),
  action: z.string(),
  effectiveFrom: zInstant,
  /** Set once superseded. A closed window is still listed — "why did this go there last week" needs it. */
  effectiveTo: zInstant.nullish(),
  versionNo: z.number().int(),
  enabled: z.boolean(),
  authoredBy: z.string(),
  /** Mandatory. What somebody reads when asking why work went where it went. */
  rationale: z.string(),
});
export type ApprovalRule = z.infer<typeof zApprovalRule>;

export const zApprovalRuleList = z.object({
  rules: z.array(zApprovalRule),
  /**
   * The queues a routing rule may target.
   *
   * <p>A closed list because routing must never strand work: a rule pointing at a queue nobody watches sends
   * requests somewhere invisible, and the symptom is a queue that has gone quiet — which reads like a good
   * week. A typo would do it.</p>
   */
  queues: z.array(z.string()),
  /** Where a request that matched nothing goes. Never empty. */
  defaultQueue: z.string(),
});
export type ApprovalRuleList = z.infer<typeof zApprovalRuleList>;

export const zSaveApprovalRule = z.object({
  /**
   * `Preauth` is ADDITIVE ONLY, structurally: its action carries a reason and nothing else, so no rule can
   * remove a requirement the plan makes. The plan version's `requiresPreauth` is a contractual term between
   * the payer and Mersal — a local rule able to switch it off would silently override a contract.
   */
  family: z.enum(["Routing", "Sla", "Preauth", "AutoApprove"]),
  priority: z.number().int(),
  predicate: zRulePredicate,
  /** `{ queue }` for Routing, `{ hours }` for Sla, `{ reason }` for Preauth — validated per family. */
  action: z.record(z.string(), z.union([z.string(), z.number()])),
  rationale: z.string().min(1),
  enabled: z.boolean().default(true),
  /** Publishing a change closes this rule's window and opens a new one. Never an update in place. */
  supersedesRuleId: zId.optional(),
});
export type SaveApprovalRule = z.infer<typeof zSaveApprovalRule>;

/**
 * The tenant's auto-decision kill switch (ADR-0035 §5.3).
 *
 * <p><b>A tenant that has never touched it reads `enabled: false`</b> — not an error, not a 404. Auto-approval
 * is opt-in and stays opt-in: a new tenant, a restored database and a failed migration all produce "no row",
 * and every one of those must mean nobody is being paid without a human having looked.</p>
 *
 * <p>Turning it off does not edit any rule. That is the point — the control you reach for at 02:00 because a
 * rule is misbehaving must not require authoring the thing that is misbehaving.</p>
 */
export const zAutoDecisionSwitch = z.object({
  enabled: z.boolean(),
  /** Why it is in this state. Required in BOTH directions. */
  reason: z.string(),
  updatedBy: z.string().nullish(),
  updatedAt: zInstant.nullish(),
  /** The platform ceiling, which binds whatever a rule claims for itself. */
  hardMaximumEgp: z.number(),
});
export type AutoDecisionSwitch = z.infer<typeof zAutoDecisionSwitch>;

export const zSetAutoDecision = z.object({
  enabled: z.boolean(),
  reason: z.string().min(1),
});
export type SetAutoDecision = z.infer<typeof zSetAutoDecision>;

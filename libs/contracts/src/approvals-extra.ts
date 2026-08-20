import { z } from "zod";
import { zId, zStatus } from "./common";

/**
 * Approvals secondary-surface contracts (Phase 7.3). The SLA/TAT board (a PHI-free reporting read), the manual
 * authorization form (break-glass create, US-063), and the emergency-approve action (US-062). Durations are
 * pre-formatted to whole minutes for display; statuses render as non-color StatusChip kinds.
 */
export const zTatBucket = z.object({
  status: z.string(),
  count: z.number().int(),
  avgMinutes: z.number(),
  p95Minutes: z.number(),
  breaches: z.number().int(),
});
export type TatBucket = z.infer<typeof zTatBucket>;

export const zTatSummary = z.object({
  total: z.number().int(),
  avgMinutes: z.number(),
  p95Minutes: z.number(),
  breaches: z.number().int(),
  byStatus: z.array(zTatBucket),
});
export type TatSummary = z.infer<typeof zTatSummary>;

/** Manual authorization request (break-glass) — approved out-of-band, with a mandatory justification. */
export const zManualAuthInput = z.object({
  beneficiaryId: zId,
  serviceCodes: z.array(z.string()).min(1),
  justification: z.string().min(1),
  rationale: z.string().optional(),
});
export type ManualAuthInput = z.infer<typeof zManualAuthInput>;

export const zManualAuthResult = z.object({
  authorizationId: zId,
  authNo: z.string(),
  status: zStatus,
});
export type ManualAuthResult = z.infer<typeof zManualAuthResult>;

/** Result of an emergency approval on a pending authorization. */
export const zEmergencyResult = z.object({
  authorizationId: zId,
  status: zStatus,
});
export type EmergencyResult = z.infer<typeof zEmergencyResult>;

/**
 * A break-glass decision awaiting — or carrying — its post-hoc review.
 *
 * <p>The queue behind this has existed since 7.3 and nothing could ever empty it. `RetrospectiveReviewed`
 * appeared in exactly two places in the whole repository: its own declaration, and the `NOT` predicate that
 * read it. No endpoint, service or job ever assigned it, and no screen ever listed the queue. So the flag
 * recorded that a review was OWED and never that one happened.</p>
 *
 * <p>That is the control that makes break-glass defensible at all — an override is acceptable BECAUSE somebody
 * checks it afterwards. `ageDays` is on the row because the question asked of a compliance backlog is not how
 * many but how long the oldest has been sitting there.</p>
 */
export const zRetrospectiveItem = z.object({
  authorizationId: zId,
  authNo: z.string(),
  beneficiaryId: zId,
  serviceCodes: z.array(z.string()),
  source: z.string(),
  status: zStatus,
  decidedAt: z.string().nullish(),
  ageDays: z.number().int(),
  reviewed: z.boolean(),
  /** `Upheld` — the break-glass was warranted. `NotJustified` — it was not. */
  outcome: z.enum(["Upheld", "NotJustified"]).nullish(),
  reviewedAt: z.string().nullish(),
  /** The reviewer. A sign-off nobody is named on cannot be asked about. */
  reviewedBy: z.string().nullish(),
  rationale: z.string().nullish(),
});
export type RetrospectiveItem = z.infer<typeof zRetrospectiveItem>;

/**
 * Completing one.
 *
 * <p>`NotJustified` does not reverse the authorization, and there is deliberately no option that does. The care
 * was delivered under it; unwinding it retroactively would refuse a service that has already happened, to a
 * beneficiary who had no part in the decision. It is a finding — what an oversight report is built from.</p>
 */
export const zRetrospectiveReviewInput = z.object({
  authorizationId: zId,
  outcome: z.enum(["Upheld", "NotJustified"]),
  rationale: z.string().trim().min(1),
});
export type RetrospectiveReviewInput = z.infer<typeof zRetrospectiveReviewInput>;

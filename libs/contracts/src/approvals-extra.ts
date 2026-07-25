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

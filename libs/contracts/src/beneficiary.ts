import { z } from "zod";
import { zId, zStatus } from "./common";

/**
 * Beneficiary-management contracts (Phase 1, US-001..005). The Beneficiary-Management role administers the
 * beneficiary registry — register, search/manage, status & reactivation — a min-necessary identity projection
 * (name + member no + identifiers + status), never clinical data. Statuses render as non-color StatusChip kinds.
 */
export const zBeneficiaryIdentifier = z.object({
  type: z.string(),
  value: z.string(),
  isPrimary: z.boolean(),
});

export const zBeneficiaryRow = z.object({
  id: zId,
  memberNo: z.string().optional(),
  givenName: z.string(),
  familyName: z.string(),
  status: zStatus,
  /** Raw status enum name (Pending/Active/Suspended/…) for the status-change screen. */
  statusRaw: z.string(),
  identifiers: z.array(zBeneficiaryIdentifier),
});
export type BeneficiaryRow = z.infer<typeof zBeneficiaryRow>;

/** New-beneficiary registration (one primary identifier + one primary phone is the min viable record). */
export const zRegisterBeneficiaryInput = z.object({
  givenName: z.string().min(1),
  familyName: z.string().min(1),
  birthDate: z.string().optional(),
  sex: z.enum(["Male", "Female", "Other", "Unknown"]).optional(),
  identifierType: z.enum(["NationalID", "Passport", "RefugeeID", "UNHCRNo"]),
  identifierValue: z.string().min(1),
  phone: z.string().optional(),
});
export type RegisterBeneficiaryInput = z.infer<typeof zRegisterBeneficiaryInput>;

export const zRegisterResult = z.object({
  id: zId,
  memberNo: z.string().optional(),
  status: zStatus,
});
export type RegisterResult = z.infer<typeof zRegisterResult>;

/** Outcome of a status change / reactivation. */
export const zStatusChangeResult = z.object({
  id: zId,
  status: zStatus,
});
export type StatusChangeResult = z.infer<typeof zStatusChangeResult>;

/**
 * The registration APPLICATION riding a Pending beneficiary (US-003). Distinct from beneficiary status:
 * the person stays Pending until an approver activates them; these are the approval-workflow facts.
 */
export const zRegistrationInfo = z.object({
  id: zId,
  status: z.enum(["Pending", "InfoRequested", "Rejected", "Active"]),
  /** The two approval guards — the server refuses Approve until both are true. */
  documentsVerified: z.boolean(),
  coverageBound: z.boolean(),
  /** The approver's notes — what is missing (RequestInfo) or why refused (Reject). */
  notes: z.string().nullable(),
});
export type RegistrationInfo = z.infer<typeof zRegistrationInfo>;

/**
 * One row of the approver's worklist: a Pending beneficiary + its latest application, or null for people
 * registered before applications were auto-created (the queue must still show them — a person the queue
 * cannot show is a person nobody reviews).
 */
export const zRegistrationWorkItem = z.object({
  beneficiary: zBeneficiaryRow,
  registration: zRegistrationInfo.nullable(),
});
export type RegistrationWorkItem = z.infer<typeof zRegistrationWorkItem>;

/** Outcome of a registration decision. `memberNo` is present exactly when the decision was Approve. */
export const zRegistrationDecisionResult = z.object({
  status: z.string(),
  memberNo: z.string().optional(),
});
export type RegistrationDecisionResult = z.infer<typeof zRegistrationDecisionResult>;

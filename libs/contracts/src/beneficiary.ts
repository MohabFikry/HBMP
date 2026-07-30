import { z } from "zod";
import { zDate, zId, zStatus } from "./common";

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
  /**
   * The number printed on the physical card, entered by the officer at registration. Distinct from
   * `memberNo` (`MRS-M-YYYY-NNNNNN`), which the system issues at activation — the card exists in the
   * beneficiary's hand before anyone has approved their application, so the two cannot be one field.
   */
  cardNumber: z.string().optional(),
  givenName: z.string(),
  middleName: z.string().optional(),
  familyName: z.string(),
  status: zStatus,
  /** Raw status enum name (Pending/Active/Suspended/…) for the status-change screen. */
  statusRaw: z.string(),
  identifiers: z.array(zBeneficiaryIdentifier),
});
export type BeneficiaryRow = z.infer<typeof zBeneficiaryRow>;

/**
 * The coverage the officer is registering this person ONTO — captured at registration, applied at approval.
 *
 * Deliberately an INTENT rather than an enrollment. policy-service owns memberships, and the supervisor's
 * approval is what creates one (US-003's `coverageBound` guard is exactly this fact). Writing an enrollment
 * at registration would grant coverage before anybody had approved the application, and would need a
 * two-service saga to undo when the application is rejected.
 */
export const zEnrolmentIntent = z.object({
  /** The policy plan to elect. Mersal / UNCR Direct Billing / UNCR Cash Reimbursement in practice. */
  planId: zId,
  /** Mersal / UNCR / Comprehensive / Restricted network in practice. */
  networkTierId: zId,
  /** The member's share of the cost, as a percentage of the service price. */
  contributionPercent: z.number().min(0).max(100),
  /** Most beneficiaries are tied to one internal clinic; optional because some are not. */
  defaultBranchId: zId.optional(),
});
export type EnrolmentIntent = z.infer<typeof zEnrolmentIntent>;

/**
 * The six operational note slots, by position. The LABEL is fixed by the slot (slot 1 is always the known
 * diagnosis) so that a report can read slot 3 without parsing prose, while the value stays free text.
 *
 * `visibility` is not decoration. Slots 1 and 3 hold clinical facts — a diagnosis and a treatment — on a form
 * owned by an administrative role, and `18-security-model.md` makes minimum-necessary a matter of code rather
 * than of intent. Classifying them Clinical means the same projection that withholds a scanned lab result
 * from finance withholds these, while beneficiary management can still FILE them at registration.
 */
export const zRegistrationNote = z.object({
  slot: z.union([z.literal(1), z.literal(2), z.literal(3), z.literal(4), z.literal(5), z.literal(6)]),
  value: z.string(),
});
export type RegistrationNote = z.infer<typeof zRegistrationNote>;

/** New-beneficiary registration (one primary identifier + one primary phone is the min viable record). */
export const zRegisterBeneficiaryInput = z.object({
  /** Mandatory and unique among non-deleted records — a second person on one card is a benefit leak. */
  cardNumber: z.string().min(1),
  givenName: z.string().min(1),
  middleName: z.string().optional(),
  familyName: z.string().min(1),
  /**
   * `approximateBirthDate` marks a date transcribed from an incomplete refugee document. The date is still
   * stored — a rough date beats none for an age-banded eligibility rule — but nothing downstream may treat
   * it as exact, and a birthday-based report must be able to tell the difference.
   */
  birthDate: zDate.optional(),
  approximateBirthDate: z.boolean().optional(),
  sex: z.enum(["Male", "Female", "Other", "Unknown"]),
  /** ISO 3166-1 alpha-2. */
  nationalityCode: z.string().length(2),
  identifierType: z.enum(["NationalID", "Passport", "RefugeeID", "UNHCRNo"]),
  identifierValue: z.string().min(1),
  /** E.164, assembled in the UI from a dial code and a national number. */
  phone: z.string().min(1),
  individualNo: z.string().optional(),
  caseNo: z.string().optional(),
  enrolment: zEnrolmentIntent,
  notes: z.array(zRegistrationNote).max(6).optional(),
});
export type RegisterBeneficiaryInput = z.infer<typeof zRegisterBeneficiaryInput>;

/**
 * Age is DERIVED, never stored and never sent. A number written down today is wrong tomorrow, and a stored
 * age is how two screens come to disagree about whether someone is still a child. Both the form and the
 * profile compute it from the birth date at render time through this one function.
 */
export function ageInYears(birthDate: string | undefined, today: Date): number | undefined {
  if (!birthDate || !/^\d{4}-\d{2}-\d{2}$/.test(birthDate)) return undefined;
  const [y, m, d] = birthDate.split("-").map(Number);
  let age = today.getUTCFullYear() - y;
  // Not yet had this year's birthday — month is 0-based on the Date side, 1-based in the ISO string.
  if (today.getUTCMonth() + 1 < m || (today.getUTCMonth() + 1 === m && today.getUTCDate() < d)) age -= 1;
  return age >= 0 ? age : undefined;
}

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

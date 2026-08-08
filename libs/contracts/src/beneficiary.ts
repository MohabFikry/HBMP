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

  /**
   * The rest of the identity projection the server already discloses (`BeneficiaryReadGuard.Fields`), and
   * which every screen used to drop on the floor.
   *
   * All optional, because they are FIELD-PROJECTED: a caller whose role does not receive `contacts` gets a
   * response without the key, and a required field here would turn a correct minimum-necessary response into
   * a parse failure. Absent means "not disclosed to you", which is exactly what a screen should render as
   * withheld rather than as empty.
   */
  birthDate: z.string().optional(),
  birthDateIsApproximate: z.boolean().optional(),
  sex: z.string().optional(),
  nationalityCode: z.string().optional(),
  individualNo: z.string().optional(),
  caseNo: z.string().optional(),
  contacts: z.array(z.object({
    type: z.string(),
    value: z.string(),
    isPrimary: z.boolean(),
  })).optional(),
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

/**
 * A standing note slot as a READER receives it, already minimum-necessary projected.
 *
 * `value` is null exactly when `withheld` is true. The slot, its label and the fact that it is FILLED are
 * still disclosed, because "no diagnosis is on file" and "a diagnosis is on file that you may not read" are
 * different facts — and an approver who cannot tell them apart asks the beneficiary to repeat what the system
 * already holds. Beneficiary management types slots 1 and 3 and does not read them back: capture is not
 * disclosure (18-security-model).
 */
export const zStandingNote = z.object({
  slot: z.number().int().min(1).max(6),
  labelEn: z.string(),
  labelAr: z.string(),
  visibility: z.enum(["Administrative", "Clinical"]),
  value: z.string().nullable(),
  withheld: z.boolean(),
});
export type StandingNote = z.infer<typeof zStandingNote>;

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
  // The regex above already guarantees three numeric parts, so this guard is unreachable — but it is what
  // convinces `noUncheckedIndexedAccess` of that, and a guard is preferable to the `as [number, number, number]`
  // the alternative needs: a cast here would also silence a real mistake if the pattern above ever loosened.
  if (y === undefined || m === undefined || d === undefined) return undefined;
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
  /**
   * The CURRENT outstanding note — what is missing (RequestInfo), why it was refused (Reject), or the
   * officer's latest answer. The history is {@link zRegistrationThreadEntry}, fetched on demand: a worklist
   * that shipped every conversation would send twenty of them to render one column.
   */
  notes: z.string().nullable(),

  /** When the application was filed. The queue is worked oldest-first, and until now could not say how old. */
  createdAt: z.string(),
  /**
   * The officer who filed it — the subject, and their display name as it stood at the time.
   *
   * This is also the addressee of a RequestInfo decision. `null` on applications filed before the field
   * existed; a screen must render that as "unknown" rather than blank, because it is the difference between
   * "nobody filed this" and "we cannot route a question about it".
   */
  createdBy: z.string().nullable(),
  createdByName: z.string().nullable(),
  updatedAt: z.string().optional(),
  /** How many entries the thread holds, so the notes affordance can say whether opening it is worth a click. */
  threadCount: z.number().int().nonnegative(),

  /** The coverage elected at the desk — what the supervisor is actually approving. */
  enrolment: zEnrolmentIntent.nullable(),
  /** The six standing note slots, already minimum-necessary projected by the server. */
  standingNotes: z.array(zStandingNote),
});
export type RegistrationInfo = z.infer<typeof zRegistrationInfo>;

/**
 * One entry in the conversation about a registration.
 *
 * A `Decision` is a ruling (and names which one); a `Reply` is an answer to one. They are never rendered the
 * same way — a reply that reads as a decision is a reply somebody acts on as if it were.
 */
export const zRegistrationThreadEntry = z.object({
  id: zId,
  kind: z.enum(["Decision", "Reply"]),
  decision: z.enum(["Approve", "RequestInfo", "Reject"]).nullable(),
  body: z.string(),
  authorName: z.string().nullable(),
  authorRole: z.string().nullable(),
  createdAt: z.string(),
});
export type RegistrationThreadEntry = z.infer<typeof zRegistrationThreadEntry>;

/**
 * A document filed against the beneficiary, as document-service lists it.
 *
 * Metadata only — no bytes and no signed URL. The approval screen's job is to answer "is the paperwork
 * here?", and opening a scan is a separate, separately-audited disclosure that belongs on the member's
 * documents screen rather than behind a review modal.
 */
export const zBeneficiaryDocument = z.object({
  id: zId,
  docType: z.string(),
  classification: z.string(),
  uploadedAt: z.string().nullable(),
  uploadedBy: z.string().nullable(),
});
export type BeneficiaryDocument = z.infer<typeof zBeneficiaryDocument>;

/**
 * The identity fields an officer may CORRECT after registration.
 *
 * Every key is optional and only the keys present are written — a partial update, so a form that edits one
 * field cannot blank the eight it did not show. `null` is not accepted for the same reason: "leave alone" and
 * "clear this" have to be distinguishable, and this shape says only the first.
 *
 * Deliberately NOT here: `cardNumber` (uniquely indexed — moving a card between people is a benefit leak, and
 * it is a conflict for a human), `status` (its own endpoint, with its own legal-transition table), and the
 * identity DOCUMENT (adding or retiring one is a different act from fixing a typo in a name).
 */
export const zBeneficiaryEdit = z.object({
  givenName: z.string().min(1).optional(),
  middleName: z.string().optional(),
  familyName: z.string().min(1).optional(),
  birthDate: zDate.optional(),
  birthDateIsApproximate: z.boolean().optional(),
  sex: z.enum(["Male", "Female", "Other", "Unknown"]).optional(),
  nationalityCode: z.string().length(2).optional(),
  individualNo: z.string().optional(),
  caseNo: z.string().optional(),
});
export type BeneficiaryEdit = z.infer<typeof zBeneficiaryEdit>;

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

/**
 * A page of the approver's worklist.
 *
 * `total` is the size of the QUEUE, not of `items` — the two differ when the page is capped or when the
 * authorization engine drops a row the caller may not read. Reporting the loaded count as the total would
 * tell a supervisor they are nearly finished when they are not, which is precisely the number they manage
 * against.
 */
export const zRegistrationWorklistPage = z.object({
  items: z.array(zRegistrationWorkItem),
  total: z.number().int().nonnegative(),
});
export type RegistrationWorklistPage = z.infer<typeof zRegistrationWorklistPage>;

/**
 * The outcome of ONE registration in a bulk decision.
 *
 * A bulk decision is a loop of individually-audited, individually-idempotent decisions, not a single
 * transaction — the server refuses an Approve whose guards are not met, and that refusal belongs to its row.
 * Reporting a partial result honestly is the whole point: "8 approved, 2 refused because coverage is not
 * bound" is actionable, and "bulk decision failed" is not.
 */
export const zBulkDecisionOutcome = z.object({
  registrationId: zId,
  ok: z.boolean(),
  memberNo: z.string().optional(),
  /** Server-stated reason, already human-readable. Present exactly when `ok` is false. */
  error: z.string().optional(),
});
export type BulkDecisionOutcome = z.infer<typeof zBulkDecisionOutcome>;

/** Outcome of a registration decision. `memberNo` is present exactly when the decision was Approve. */
export const zRegistrationDecisionResult = z.object({
  status: z.string(),
  memberNo: z.string().optional(),
});
export type RegistrationDecisionResult = z.infer<typeof zRegistrationDecisionResult>;

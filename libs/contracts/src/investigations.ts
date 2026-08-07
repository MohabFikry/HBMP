import { z } from "zod";
import { zId, zInstant, zLocalized } from "./common";
import { zCheckState } from "./prescribing";

/**
 * Ordering investigations — the lab / imaging counterpart of `prescribing.ts`.
 *
 * <p>Kept as its own module rather than widening the prescribing contracts. The two workspaces share a
 * SHAPE — a typeahead, several lines, five per-line states, validate-then-submit — but not a vocabulary: a
 * drug interaction and a repeat blood test are not members of one enum, and forcing them into one would
 * give both engines a list of kinds neither can answer. `zCheckState` IS shared, deliberately: a clinician
 * reading both panels on one screen must not have to learn two meanings of "not checked".</p>
 */

/**
 * A procedure from the CPT catalogue — the code the order carries, and the text the doctor read.
 *
 * <p>Two fields, because two is what the catalogue is. It also carried the CPT `category` (Category I / II /
 * III / PLA / MAAA), which no screen has ever displayed: it says how a code was adopted into the book, not
 * what the procedure is, so there was nothing for a combobox to do with it.</p>
 */
export const zCptRef = z.object({
  code: z.string().min(1),
  description: z.string(),
});
export type CptRef = z.infer<typeof zCptRef>;

/**
 * The CPT SECTION being ordered from — what the Labs and Imaging tabs actually filter by.
 *
 * <p>Not to be confused with the CPT <i>category</i> the catalogue stores: that is the taxonomy (Category I /
 * II / III / PLA / MAAA) and says nothing about whether a code is a scan or a blood test. A section is the
 * code's own numeric range, which is how the book is organised and why CPT codes are assigned in blocks. The
 * ranges live in <c>masterdata</c>'s <c>CptSections</c>, verified against the published catalogue.</p>
 *
 * <p><b>A tab is not a section.</b> Imaging is one; Labs is <i>Laboratory and Pathology</i>, because a sample
 * run on an analyser and a specimen read by a pathologist are ordered from the same tab and are not the same
 * kind of work. That is why the search takes a list.</p>
 */
export const zCptSection = z.enum([
  "Anesthesia",
  "Surgery",
  "Imaging",
  "Laboratory",
  "Pathology",
  "Medicine",
  "EvaluationAndManagement",
  /** Category II / III / PLA / MAAA — the letter-suffixed codes, which sit outside the book's sections. */
  "Other",
]);
export type CptSection = z.infer<typeof zCptSection>;

/** Which queue the order goes to. One order is one type; the tab decides it, not the clinician. */
export const zInvestigationOrderType = z.enum(["Lab", "Imaging"]);
export type InvestigationOrderType = z.infer<typeof zInvestigationOrderType>;

/** One composed line, before it is an order. `lineId` is client-minted for correlation only. */
export const zInvestigationDraftLine = z.object({
  lineId: zId,
  test: zCptRef.nullable(),
  quantity: z.number().int().min(1),
  /** Free-text detail for the performing site — "left knee", "fasting". Never a substitute for the code. */
  note: z.string(),
});
export type InvestigationDraftLine = z.infer<typeof zInvestigationDraftLine>;

export const zOrderCheckKind = z.enum([
  "Code", "Section", "Duplicate", "PriorAuthorization", "Indication",
]);
export type OrderCheckKind = z.infer<typeof zOrderCheckKind>;

export const zOrderFinding = z.object({
  lineId: zId,
  kind: zOrderCheckKind,
  state: zCheckState,
  message: zLocalized,
  requiresAcknowledgement: z.boolean(),
  isBlocking: z.boolean(),
  /** Where the verdict came from. A warning a clinician cannot attribute is one they are right to ignore. */
  sourceName: z.string().nullish(),
  /** The source's own admission of what it does not cover. */
  caveat: z.string().nullish(),
});
export type OrderFinding = z.infer<typeof zOrderFinding>;

export const zOrderValidationResult = z.object({
  validationId: zId,
  overallState: zCheckState,
  findings: z.array(zOrderFinding),
  /** lineId → the worst state on that line. What its chip shows. */
  lineStates: z.record(z.string(), zCheckState),
});
export type OrderValidationResult = z.infer<typeof zOrderValidationResult>;

/** A clinician's recorded reason for proceeding past one warning on one line. */
export const zOrderAcknowledgement = z.object({
  lineId: zId,
  findingKind: zOrderCheckKind,
  reason: z.string(),
});
export type OrderAcknowledgement = z.infer<typeof zOrderAcknowledgement>;

export const zInvestigationOrderResult = z.object({
  orderId: zId,
  orderNo: z.string(),
  status: z.string(),
  requiresApproval: z.boolean(),
});
export type InvestigationOrderResult = z.infer<typeof zInvestigationOrderResult>;

// ---------------------------------------------------------------- validity extension

/**
 * Asking the approval team to revalidate something that has expired.
 *
 * <p>Raised by whoever is holding the lapsed item with the patient in front of them — a pharmacist at the
 * counter, a lab or imaging technician. It creates a request and nothing else: the scope behind it
 * (`auth:request-extension`) carries no decision authority, and the requester cannot decide their own.</p>
 */
export const zValidityExtensionRequest = z.object({
  itemType: z.enum(["Prescription", "InvestigationOrder"]),
  /** The expired item's id. */
  itemId: zId,
  /** Its human reference — RX-2026-000312. Shown to the approver so they read what the counter is holding. */
  itemReference: z.string().nullish(),
  beneficiaryId: zId,
  expiredAt: z.string().nullish(),
  /** Mandatory. An approver with an empty box is deciding on who asked, not on why. */
  reason: z.string(),
});
export type ValidityExtensionRequest = z.infer<typeof zValidityExtensionRequest>;

export const zValidityExtensionResult = z.object({
  authorizationId: zId,
  authNo: z.string(),
  status: z.string(),
});
export type ValidityExtensionResult = z.infer<typeof zValidityExtensionResult>;

// ---------------------------------------------------------------- validity periods (supervisor)

/** One artefact's configured validity, and whether anyone has actually chosen it. */
export const zValidityArtefactPolicy = z.object({
  artefact: z.enum(["Prescription", "LabOrder", "ImagingOrder", "ProcedureOrder"]),
  days: z.number().int(),
  /**
   * False = nobody has set this and `days` is the platform default.
   *
   * <p>Shown as its own state on the supervisor screen, because "10 because we chose 10" and "10 because
   * nobody has looked at this" are different facts, and only one of them is a decision.</p>
   */
  configured: z.boolean(),
  updatedAt: zInstant.nullish(),
});
export type ValidityArtefactPolicy = z.infer<typeof zValidityArtefactPolicy>;

export const zValidityPolicyView = z.object({
  defaultDays: z.number().int(),
  /** Clinical bounds, supplied by the server so the screen and the endpoint cannot disagree about them. */
  minDays: z.number().int(),
  maxDays: z.number().int(),
  items: z.array(zValidityArtefactPolicy),
});
export type ValidityPolicyView = z.infer<typeof zValidityPolicyView>;

import { z } from "zod";
import { zId, zLocalized } from "./common";

/**
 * Prescribing workspace contracts (phase 26, design doc 43 §6).
 *
 * Two rules from the design shape everything here:
 *   1. Benefit rules may BLOCK; clinical checks may only WARN, and are overridable with a recorded reason.
 *   2. "Check unavailable" is NEVER the same value as "OK" — hence five states, not four.
 */

/**
 * A drug as the prescribing combobox offers it.
 *
 * `drugId` is a REAL uuid. The modal this replaces sent the ATC code string where the API expects a Guid,
 * so the prescribing path could not work against real data at all.
 *
 * `activeIngredient` is a safety field, not decoration: two trade names holding the same molecule is the
 * commonest prescribing duplication, and the ingredient has to be visible at the moment of choosing.
 *
 * `hasIndicationData` distinguishes "this diagnosis is not a listed indication" from "nothing is recorded
 * for this drug" — 1,019 products in the Egyptian list are in the second case.
 */
export const zPrescribableDrug = z.object({
  drugId: zId,
  tradeName: zLocalized,
  activeIngredient: z.string().optional(),
  strength: z.string().optional(),
  form: z.string().optional(),
  priceEgp: z.number().optional(),
  atcCode: z.string().optional(),
  hasIndicationData: z.boolean(),
});
export type PrescribableDrug = z.infer<typeof zPrescribableDrug>;

/**
 * The five per-line states. FIVE, never four.
 *
 * `Ok`, `Warning` and `Blocked` are answers. `NotChecked` and `Unavailable` are NOT — the first means there
 * was no data to check against, the second that the source failed. Neither may ever render as `Ok`.
 */
export const zCheckState = z.enum(["Ok", "Warning", "Blocked", "NotChecked", "Unavailable"]);
export type CheckState = z.infer<typeof zCheckState>;

// 28.5 adds Duplication — two lines carrying the same molecule, including when one hides inside a
// combination product. Phase 26 skipped it in one line of the interaction pass.
// 28.9 adds Contraindication — "is this drug DANGEROUS IN this condition", which is a different question
// from Indication ("is it USED FOR it") and the one that carries the clinical value. Conflating them is why
// indication mismatch is noise: off-label is legitimate and common, so that warning fires constantly.
export const zCheckKind = z.enum([
  "Indication", "Interaction", "Allergy", "DoseDuration", "Benefit", "Duplication", "Contraindication",
]);
export type CheckKind = z.infer<typeof zCheckKind>;

/**
 * One check's verdict on one line.
 *
 * `sourceName` / `sourceVersion` / `checkedAt` are shown with the finding and stored with the prescription:
 * a warning a clinician cannot attribute is one they are right to ignore. `caveat` carries the source's own
 * statement of its limits — the indication map is ATC-level clinical judgement rather than a published
 * dataset, and the interaction list is internally curated with partial coverage.
 */
/**
 * How serious a clinical finding is (28.4, doc 44 §2).
 *
 * Only `Contraindicated` and `Major` interrupt the prescriber and gate submission; `Moderate` renders beside
 * the line and `Minor` collapses. The evidence for tiering is the best-documented failure mode in clinical
 * decision support: when a contraindicated combination and a trivial one demand the same click, clinicians
 * learn to dismiss both, and override rates above 90% are routinely reported.
 *
 * Ordered weakest-first so `SEVERITY_RANK` below can compare them.
 */
export const zClinicalSeverity = z.enum(["Minor", "Moderate", "Major", "Contraindicated"]);
export type ClinicalSeverity = z.infer<typeof zClinicalSeverity>;

/** Weakest to strongest. A line's chip shows the worst severity among its findings. */
export const SEVERITY_RANK: Record<ClinicalSeverity, number> = {
  Minor: 0,
  Moderate: 1,
  Major: 2,
  Contraindicated: 3,
};

export const zFinding = z.object({
  lineId: zId,
  drugId: zId.nullish(),
  kind: zCheckKind,
  state: zCheckState,
  messageEn: z.string(),
  messageAr: z.string(),
  // `.nullish()`, not `.optional()`, and the distinction is not cosmetic. These properties are nullable on
  // the server record, and System.Text.Json WRITES them as `null` rather than omitting them — so the wire
  // carries `"severity": null`. `.optional()` accepts `undefined` and REJECTS `null`, which made every
  // validation response fail contract parsing and surface to the prescriber as "validation could not run".
  sourceName: z.string().nullish(),
  sourceVersion: z.string().nullish(),
  checkedAt: z.string().nullish(),
  caveat: z.string().nullish(),
  /**
   * Verbatim source text supporting the finding — the manufacturer's own words, quoted rather than authored.
   *
   * Either the sentence in a drug label that named another drug on this prescription, or the label's dosing
   * section shown for the prescriber to read against what they typed. It is ENGLISH ONLY: that is the
   * language the label is published in, and machine-translating a regulatory document would be a worse
   * answer than saying which language it is in. Render it `dir="ltr" lang="en"` so it does not mirror into
   * the Arabic layout.
   */
  referenceText: z.string().nullish(),
  /**
   * How serious this finding is — a FIRST-CLASS element, not a word inside `messageEn` (28.4, doc 44 §2).
   *
   * Carried by every check kind since phase 28, not only interactions. Null is meaningful and is NOT
   * "harmless": a manufacturer label states an effect rather than a rank, so an ungraded finding still
   * interrupts. Read `requiresAcknowledgement` for the gating decision rather than re-deriving it here.
   */
  severity: zClinicalSeverity.nullish(),
  relatedLineId: zId.nullish(),
  requiresAcknowledgement: z.boolean(),
  /** Contraindicated only: the reason must be typed, and it is surfaced to the approver. */
  requiresTypedReason: z.boolean().nullish(),
  isBlocking: z.boolean(),
});
export type Finding = z.infer<typeof zFinding>;

export const zValidationResult = z.object({
  validationId: zId,
  ranAt: z.string(),
  engineVersion: z.string(),
  overallState: zCheckState,
  findings: z.array(zFinding),
  /** Per-line worst state, keyed by the client's line id. */
  lineStates: z.record(z.string(), zCheckState),
});
export type ValidationResult = z.infer<typeof zValidationResult>;

/** One composed prescription line, before submission. */
export const zPrescriptionDraftLine = z.object({
  /** Client-side identity, so findings and acknowledgements can name a line before the server does. */
  lineId: z.string(),
  drug: zPrescribableDrug.nullable(),
  dose: z.string(),
  durationDays: z.number().nullable(),
  quantity: z.number(),
});
export type PrescriptionDraftLine = z.infer<typeof zPrescriptionDraftLine>;

/** A prescriber's recorded reason for proceeding past one warning. */
export const zLineAcknowledgement = z.object({
  lineId: z.string(),
  findingKind: zCheckKind,
  reason: z.string(),
});
export type LineAcknowledgement = z.infer<typeof zLineAcknowledgement>;

export const zPrescriptionSubmitResult = z.object({
  prescriptionId: zId,
  rxNo: z.string(),
  status: z.string(),
});
export type PrescriptionSubmitResult = z.infer<typeof zPrescriptionSubmitResult>;

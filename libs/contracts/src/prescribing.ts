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

  // ---- 29.7 (design 45 §7) ------------------------------------------------------------------------------
  /**
   * Cheapest per PRESCRIBING UNIT within ingredient + strength + form. Derived server-side and never
   * authored — the client only renders it.
   */
  isLowestPrice: z.boolean().optional(),
  /** price ÷ pack size. Absent where pack size is unknown, and such a drug is never labelled: comparing
   *  PACK prices is the error §7 exists to prevent. */
  pricePerUnit: z.number().optional(),
  /**
   * Available / Unavailable / Unknown. THREE states, not a boolean.
   *
   * <p>`Unknown` is the default and renders NOTHING — no badge, no warning. A boolean defaulting to false
   * would show the entire catalogue as out of stock on day one, and prescribers would learn to ignore the
   * indicator before it ever carried real data.</p>
   */
  availability: z.enum(["Available", "Unavailable", "Unknown"]).optional(),

  // ---- 29.6 (design 45 §6) — the pack facts the composer shows and computes with --------------------------
  /**
   * What the dose and quantity are counted in — Tablet, ML, Puff.
   *
   * <p>Carried on the search row so the composer can label the dose field the instant a drug is chosen.
   * ABSENT is honest and renders as no unit: 838 catalogue rows have no derivable one, and a word invented
   * for them would sit beside the dose field reading as data.</p>
   */
  prescribingUnit: z.string().nullable().optional(),
  /** Prescribing units per pack. Absent where the catalogue does not record one. */
  packSize: z.number().nullable().optional(),
  /**
   * Whether fewer than a whole pack may be dispensed. ABSENT IS NOT FALSE — it means the catalogue does not
   * say, and the quantity is reported NotChecked naming the field rather than rounded to a pack.
   */
  isPackSplittable: z.boolean().nullable().optional(),
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
  // 29.6 (design 45 §6) — how much to dispense, from the drug's pack facts. Its reason for existing is the
  // NEGATIVE case: missing `is_pack_splittable` or `pack_size` reports NotChecked NAMING the field, never a
  // guessed quantity, because a silently wrong quantity is a dispensing error.
  "Quantity",
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
  /**
   * The sig as it is STORED and read back at the counter — "1 Tablet x 3/day". Derived from the three
   * numbers below rather than typed, so the text on the label and the arithmetic behind the quantity cannot
   * describe different prescriptions.
   */
  dose: z.string(),
  /**
   * 29.6 — the NUMERIC dose, in the drug's prescribing unit.
   *
   * <p>Its absence is the reason the Quantity check reported "not checked: this line has no numeric dose,
   * frequency and duration to compute a quantity from" on every prescription this platform had written. The
   * check was correct and complete; nothing sent it a number.</p>
   */
  doseAmount: z.number().nullable().default(null),
  /** Administrations per day. The second of the three numbers a quantity is computed from. */
  timesPerDay: z.number().int().nullable().default(null),
  durationDays: z.number().nullable(),
  quantity: z.number(),
  /**
   * True once the prescriber has typed a quantity of their own.
   *
   * <p>The computed figure is a STARTING POINT, not a verdict: a doctor who deliberately writes 90 because
   * the patient is travelling must not watch it snap back to 60 on the next keystroke. Client-side only —
   * it is never sent, because the server has no interest in how a number was arrived at.</p>
   */
  quantityEdited: z.boolean().default(false),
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

// ---- 29.5 — acute / chronic prescribing (design 45 §5) --------------------------------------------------

/**
 * <b>Acute</b> is today's behaviour, unchanged and the default. <b>Chronic</b> is one script dispensed in
 * dated windows, under ONE authorisation, with eligibility re-validated at each collection.
 */
export const zPrescriptionKind = z.enum(["Acute", "Chronic"]);
export type PrescriptionKind = z.infer<typeof zPrescriptionKind>;

/**
 * A refill cadence, as the Approval Supervisor administers it (`pharmacy.refill_frequency`).
 *
 * <p><b>A table, not an enum</b> — adding "every 6 months" must be a data change rather than a release, so
 * this describes a row. `months` is carried because it is what the window COUNT is derived from; a label
 * alone would leave the composer unable to explain the schedule it is showing.</p>
 */
export const zRefillFrequency = z.object({
  code: z.string(),
  months: z.number().int(),
  name: zLocalized,
});
export type RefillFrequency = z.infer<typeof zRefillFrequency>;

/** One dated collection window, as the composer previews it before submitting. */
export const zChronicWindow = z.object({
  windowNo: z.number().int(),
  /** The date the window is due. Fixed — collecting early never pulls the rest of the script forward. */
  scheduledOpen: z.string(),
  /** Scheduled minus the early tolerance. Window 1 gets none: it cannot open before the script existed. */
  opensAt: z.string(),
  closesAt: z.string(),
  allocatedQuantity: z.number(),
});

/**
 * The computed schedule, shown BEFORE submit so the doctor sees 34/33/33 and can adjust.
 *
 * <p><b>Computed by the server</b>, deliberately. Re-deriving largest-remainder here would fork the one
 * piece of arithmetic in this phase that must not be forked — the copies would drift, and the drift would
 * appear as a doctor being shown a schedule the pharmacy never honours.</p>
 */
export const zChronicPreview = z.object({
  /** The rounded total. The windows sum to it EXACTLY — round once, at the total (invariant 5). */
  total: z.number(),
  unit: z.string(),
  frequencyMonths: z.number().int(),
  windows: z.array(zChronicWindow),
});
export type ChronicPreview = z.infer<typeof zChronicPreview>;

/**
 * 29.6 — how much of a medicine a course needs, and how much is therefore dispensed (design 45 §6).
 *
 * <p><b>Computed by the SERVER.</b> `QuantityMath` is the one implementation of this arithmetic: the
 * validation check grades against it and the dispensing counter meters against it. A copy of the
 * multiplication in the browser would be a second answer to "how much medicine does this person get", and
 * the two would be discovered to disagree at a counter.</p>
 */
export const zQuantityPreview = z.object({
  /** Dose × times per day × days — what the patient consumes. */
  totalUnits: z.number(),
  /**
   * What is handed over. Equal to `totalUnits` for a splittable pack; rounded UP to whole packs for one that
   * cannot be broken, because half an inhaler is not a thing anyone can dispense.
   */
  dispenseQuantity: z.number(),
  /** Whole packs, when the pack cannot be split. Null when it can. */
  packs: z.number().nullable().optional(),
  /**
   * 31.2 — how many BOXES to hand over, which is what a pharmacy counts out.
   *
   * <p>NULL when the question has no answer, and it often has none: `pack_size` counts the catalogue's
   * MINOR UNITS, which is only the same thing the dose counts for forms like tablets and ampoules. A box of
   * 5 insulin pens dosed in IU divides to a box count wrong by the pen's contents — so it is withheld and
   * the composer says why, rather than printing a confident wrong number above a dispensing counter.</p>
   */
  boxes: z.number().nullable().optional(),
  packSize: z.number().nullable().optional(),
  /** The word the number is counted in, so the composer says "60 Tablet" and not a bare 60. */
  prescribingUnit: z.string().nullable().optional(),
  isPackSplittable: z.boolean().nullable().optional(),
});
export type QuantityPreview = z.infer<typeof zQuantityPreview>;

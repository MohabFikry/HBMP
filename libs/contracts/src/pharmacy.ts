import { z } from "zod";
import { zCoded, zId, zInstant, zPatientRef, zStatus } from "./common";

/**
 * Pharmacy dispensing (Phase 6). MIN-NECESSARY: the queue shows prescription lines + a MASKED patient ref.
 * There is NO lab/imaging result field anywhere in this schema — the Pharmacy zone structurally cannot see
 * results.
 */
export const zPrescriptionLine = z.object({
  id: zId,
  drug: zCoded,
  /** Prescribed quantity for this line. */
  quantity: z.number().int().positive(),
  /** Already dispensed against this line (supports partial dispensing across visits). */
  dispensed: z.number().int().nonnegative(),
  dose: z.string(),
  /** How it is taken and how often — "Oral", "BD". Display strings from the prescription, not codes. */
  route: z.string().nullish(),
  frequency: z.string().nullish(),
  /**
   * How long the course runs.
   *
   * <p>NULL means the prescriber did not record one, and the counter says exactly that. A missing duration
   * and a one-day course look identical in a blank cell, and only one of them is a reason to ring the
   * prescriber before handing anything over.</p>
   */
  durationDays: z.number().int().nullish(),
  /**
   * The active ingredient, resolved from master data.
   *
   * <p>Two trade names holding the same molecule is the commonest prescribing duplication there is, and a
   * pharmacist checking a packet against a prescription is checking the molecule. Null when the catalogue
   * records none — 2,786 of 31,651 products are in that state, and saying so beats showing the trade name
   * twice.</p>
   */
  activeIngredient: z.string().nullish(),
  /** What one unit costs, from the catalogue. Null when unpriced — never zero. */
  unitPriceEgp: z.number().nullish(),
  status: zStatus,
  /** True when the line is flagged out-of-stock (blocks dispense of this line, not the whole Rx). */
  outOfStock: z.boolean(),
});
export type PrescriptionLine = z.infer<typeof zPrescriptionLine>;

export const zPrescription = z.object({
  id: zId,
  /**
   * The prescription's own reference — RX-2026-000202.
   *
   * The dispensing screen titled itself with the internal uuid, which is not a thing a pharmacist can read
   * back to a patient, write on a bag, or quote down the phone. The server has always sent `rxNo`; nothing
   * read it.
   */
  rxNo: z.string(),
  patient: zPatientRef,
  prescriber: zCoded.pick({ label: true }), // display-only prescriber label
  submittedAt: zInstant,
  /** When it stops being dispensable. Absent only on rows written before expiry was stamped at all. */
  expiresAt: zInstant.nullish(),
  /**
   * Past its validity window.
   *
   * <p>Carried as its own flag rather than inferred from `status`, because the expiry sweeper runs on a
   * timer: between the moment a prescription lapses and the next sweep the row still says Approved. A
   * counter that read the status alone would offer to dispense something the server will refuse. The
   * service computes this against the clock; the screen never recomputes it.</p>
   */
  expired: z.boolean(),
  status: zStatus,
  /**
   * The encounter's recorded diagnoses AS AT prescribing time — a snapshot, not a join.
   *
   * <p>A medicine only makes sense against what it is FOR: a pharmacist checking a broad-spectrum antibiotic
   * against "acute sinusitis" is doing something a pharmacist handing it over blind is not. It is the same
   * snapshot the indication check ran on (26.4), so the screen and the warning cannot disagree about what was
   * known at the time — and a correction to the encounter next week does not rewrite it.</p>
   */
  diagnosisCodes: z.array(z.string()),
  primaryIcdCode: z.string().nullish(),
  lines: z.array(zPrescriptionLine),
});
export type Prescription = z.infer<typeof zPrescription>;

/** A per-line dispense decision. A substitution requires an explicit approved substitute drug. */
export const zDispenseLine = z.object({
  lineId: zId,
  /** Quantity to dispense now (≤ remaining). 0 = skip this line. */
  quantity: z.number().int().nonnegative(),
  /** Present only when substituting — the approved formulary alternative. */
  substitute: zCoded.optional(),
  /**
   * Why the substitution was made. Required by the server whenever `substitute` is present.
   *
   * A substitution is a pharmacist overriding what a doctor wrote, and the record of it is what lets the
   * prescriber — and anyone reviewing the episode later — see that the patient did not receive the molecule
   * on the prescription, and on whose judgement. Without the reason the dispense record shows a different
   * drug and no account of why.
   */
  substitutionReason: z.string().optional(),
  batchNumber: z.string().optional(),
  expiry: z.string().optional(),
});
export type DispenseLine = z.infer<typeof zDispenseLine>;

/**
 * Atomic idempotent dispense (Phase 6.2). Same Idempotency-Key contract as consume — a replay maps to the
 * same dispense_event and returns `replayed: true` rather than dispensing twice.
 */
export const zDispenseRequest = z.object({
  prescriptionId: zId,
  idempotencyKey: z.string().uuid(),
  lines: z.array(zDispenseLine).min(1),
  /**
   * What the pharmacist recorded about THIS handover — collection arrangements, a replaced lot, who
   * collected on the patient's behalf.
   *
   * <p>Not a clinical note and never read by the clinical checks. It rides on the dispense because it
   * describes that act at that counter, not the prescriber's decision; a pharmacist who needs to tell the
   * PRESCRIBER something has the out-of-stock notice, the substitution reason and the approval team. Capped
   * at 500 by the database, which is append-only — a field with no ceiling is one somebody eventually pastes
   * a clinical history into, permanently.</p>
   */
  note: z.string().max(500).optional(),
});
export type DispenseRequest = z.infer<typeof zDispenseRequest>;

export const zDispenseResult = z.object({
  prescriptionId: zId,
  dispenseEventId: zId,
  status: zStatus,
  replayed: z.boolean(),
  /** Lines left with an outstanding quantity after this dispense. */
  linesOutstanding: z.number().int().nonnegative(),
});
export type DispenseResult = z.infer<typeof zDispenseResult>;

/**
 * What a prescription costs, and how it splits between the member and the payer.
 *
 * <p>The split is NOT computed in the browser or in pharmacy-service. It comes from eligibility, which
 * composes it through the same `libs/benefit-pricing` path claims adjudicates with — so the figure a member
 * is told at the counter and the figure their claim is charged cannot diverge.</p>
 *
 * <p><b>`determinate: false` is why the nullable amounts are nullable.</b> When the plan does not price
 * pharmacy at this provider's tier, or a medicine has no list price, the member and payer figures are NULL
 * and `reason` says why. A screen must render that as "cannot be quoted" and never as 0 — at a dispensing
 * counter a zero reads as "free", and a beneficiary told their medication is free has been told something
 * their claim will later contradict.</p>
 */
export const zRxPriceLine = z.object({
  /**
   * `prescriptionLineId`, spelled as the SERVER spells it.
   *
   * <p>It was `lineId` here and `prescriptionLineId` on the wire, so every pricing response failed contract
   * validation and the counter showed "the cost could not be worked out" on every prescription — while the
   * endpoint had been returning a correct total the whole time. The dev fixture mirrored the CONTRACT rather
   * than the server, so the tests agreed with the bug.</p>
   */
  prescriptionLineId: zId,
  drugId: zId,
  drugName: z.string().nullish(),
  quantityPrescribed: z.number(),
  quantityDispensed: z.number(),
  /** Null when the catalogue holds no price for this product — not zero. */
  unitPriceEgp: z.number().nullish(),
  lineTotalEgp: z.number().nullish(),
});
export type RxPriceLine = z.infer<typeof zRxPriceLine>;

export const zRxPricing = z.object({
  lines: z.array(zRxPriceLine),
  currency: z.string(),
  totalEgp: z.number().nullish(),
  memberShareEgp: z.number().nullish(),
  payerShareEgp: z.number().nullish(),
  determinate: z.boolean(),
  reason: z.string().nullish(),
  tierCode: z.string().nullish(),
  isCovered: z.boolean().nullish(),
  /**
   * The amount the member/payer split was quoted on.
   *
   * <p>Equal to `totalEgp` when the counter asked about the whole prescription, and equal to the value of what
   * is about to be handed over once quantities have been entered. It is the denominator the percentage note
   * under each share is computed against — `totalEgp` is the wrong one as soon as a partial dispense is being
   * priced, and using it would report a 50% coinsurance as 25%.</p>
   */
  quotedOnEgp: z.number().nullish(),
  /**
   * Which question the two share figures answer: what the patient pays for the quantities entered at the
   * counter (`true`), or what they pay if they collect all of it (`false`).
   *
   * <p><b>The screen must say which.</b> A partial dispense is the ordinary case — stock is short, or the
   * member can only pay for part of a course today — and one label covering both readings would quietly
   * overstate what is owed at that moment. The share is RE-QUOTED on the server rather than scaled here,
   * because the split runs a deductible before a copay before coinsurance: half a prescription does not cost
   * half the share of the whole one, and a browser multiplying by a ratio would invent a figure the claim
   * later contradicts.</p>
   */
  quotedOnDispenseNow: z.boolean().nullish(),
});
export type RxPricing = z.infer<typeof zRxPricing>;

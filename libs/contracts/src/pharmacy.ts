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
  status: zStatus,
  /** True when the line is flagged out-of-stock (blocks dispense of this line, not the whole Rx). */
  outOfStock: z.boolean(),
});
export type PrescriptionLine = z.infer<typeof zPrescriptionLine>;

export const zPrescription = z.object({
  id: zId,
  patient: zPatientRef,
  prescriber: zCoded.pick({ label: true }), // display-only prescriber label
  submittedAt: zInstant,
  status: zStatus,
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

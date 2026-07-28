import { z } from "zod";
import { zCoded, zDate, zId, zInstant, zLocalized, zStatus } from "./common";

/**
 * Doctor consultation / EMR (Phase 4). Every read here is treating-relationship gated on the server; the UI
 * only ever lists patients the doctor is currently treating. This is the ONE zone that legitimately holds
 * diagnosis + clinical narrative — the others (reception/lab/pharmacy/finance) structurally cannot.
 */
export const zPatientListItem = z.object({
  /** The ENCOUNTER id — this list is a worklist of encounters, not of people. */
  id: zId,
  /**
   * The beneficiary this encounter belongs to. Carried because the unified patient profile (design 39) is
   * keyed on the BENEFICIARY, and a worklist row that cannot name one is a row you cannot open a file from
   * — which is why the profile was unreachable from every clinical worklist.
   */
  beneficiaryId: zId,
  name: zLocalized,
  mrn: z.string(),
  /** True only when a treating relationship is active (server-asserted). */
  treating: z.boolean(),
  lastVisit: zDate.nullable(),
  status: zStatus,
});
export type PatientListItem = z.infer<typeof zPatientListItem>;

export const zVitals = z.object({
  heightCm: z.number().nullable(),
  weightKg: z.number().nullable(),
  systolic: z.number().nullable(),
  diastolic: z.number().nullable(),
  heartRate: z.number().nullable(),
  tempC: z.number().nullable(),
});
export type Vitals = z.infer<typeof zVitals>;

export const zAllergy = z.object({ id: zId, substance: zLocalized, severity: z.enum(["mild", "moderate", "severe"]) });
export type Allergy = z.infer<typeof zAllergy>;

export const zSoap = z.object({
  subjective: z.string(),
  objective: z.string(),
  assessment: z.string(),
  plan: z.string(),
});
export type Soap = z.infer<typeof zSoap>;

export const zEncounter = z.object({
  id: zId,
  patientId: zId,
  patientName: zLocalized,
  openedAt: zInstant,
  signed: z.boolean(),
  soap: zSoap,
  vitals: zVitals,
  allergies: z.array(zAllergy),
  diagnoses: z.array(zCoded),
});
export type Encounter = z.infer<typeof zEncounter>;

/** Place an investigation order (routed to approval when high-cost — server decides). */
export const zPlaceOrderRequest = z.object({
  encounterId: zId,
  kind: z.enum(["lab", "imaging"]),
  test: zCoded,
  priority: z.enum(["routine", "urgent"]),
  notes: z.string().max(500).optional(),
});
export type PlaceOrderRequest = z.infer<typeof zPlaceOrderRequest>;

export const zPlaceOrderResult = z.object({
  orderId: zId,
  status: zStatus,
  /** True when the order tripped high-cost routing and now awaits medical approval. */
  requiresApproval: z.boolean(),
});
export type PlaceOrderResult = z.infer<typeof zPlaceOrderResult>;

/** Write an e-prescription line (interaction/allergy checks are advisory + server-side). */
export const zPrescribeRequest = z.object({
  encounterId: zId,
  drug: zCoded,
  dose: z.string().min(1),
  quantity: z.number().int().positive(),
  notes: z.string().max(500).optional(),
});
export type PrescribeRequest = z.infer<typeof zPrescribeRequest>;

export const zPrescribeResult = z.object({
  prescriptionId: zId,
  status: zStatus,
  /** Advisory alerts (interactions/allergies) — non-blocking; the doctor acknowledges. */
  advisories: z.array(zLocalized),
});
export type PrescribeResult = z.infer<typeof zPrescribeResult>;

import { z } from "zod";
import { zId, zInstant, zStatus, zPatientRef } from "./common";

/**
 * Clinician worklist contracts (Phase 4, US-032/033). These back the doctor portal's cross-encounter views:
 * "my orders" and "my prescriptions" — the things the signed-in clinician authored (server scopes by
 * CreatedBy == subject). Min-necessary: a masked beneficiary token + codes/status only, never another
 * clinician's work. Statuses are pre-resolved to non-color StatusChip kinds.
 */
export const zOrderRow = z.object({
  id: zId,
  orderNo: z.string(),
  beneficiary: zPatientRef,
  /** "Lab" | "Imaging" (emr order type) — display verbatim. */
  orderType: z.string(),
  /** The order's first line code (e.g. a CPT/LOINC) as a quick descriptor. */
  primaryCode: z.string(),
  lineCount: z.number().int(),
  status: zStatus,
  requestedAt: zInstant,
});
export type OrderRow = z.infer<typeof zOrderRow>;

export const zRxRow = z.object({
  id: zId,
  beneficiary: zPatientRef,
  lineCount: z.number().int(),
  status: zStatus,
  submittedAt: zInstant.optional(),
});
export type RxRow = z.infer<typeof zRxRow>;

/**
 * Vitals capture (Phase 4, US-030 nurse triage). The emr VitalType space; a single numeric reading per type
 * (BP is captured as systolic). Recording is treating-gated server-side (the nurse owns the encounter).
 */
export const zVitalType = z.enum(["BP", "HR", "Temp", "SpO2", "Weight", "Height", "BMI"]);
export type VitalType = z.infer<typeof zVitalType>;

export const zVitalInput = z.object({ type: zVitalType, value: z.number() });
export type VitalInput = z.infer<typeof zVitalInput>;

/** Outcome of a vitals-capture submission — how many readings were recorded on the encounter. */
export const zVitalsResult = z.object({ encounterId: zId, recorded: z.number().int() });
export type VitalsResult = z.infer<typeof zVitalsResult>;

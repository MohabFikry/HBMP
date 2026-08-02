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
  /** The order's first line id — the anchor the results inbox uses to open a result (14.6/14.7). Optional
   *  because not every order row carries a line (e.g. a rejected order); the "view result" action hides then. */
  firstLineId: zId.optional(),
});
export type OrderRow = z.infer<typeof zOrderRow>;

/**
 * A completed result the caller MAY read in full — Standard sensitivity, or a restricted result the caller
 * authored / holds an active grant for (14.6). `restricted:false` is the discriminant.
 */
export const zResultValue = z.object({
  restricted: z.literal(false),
  orderId: zId,
  lineId: zId,
  category: z.string(),
  code: z.string(),
  value: z.string(),
  status: z.string(),
  resultedAt: zInstant.optional(),
});
export type ResultValue = z.infer<typeof zResultValue>;

/**
 * Existence-only metadata for a sensitivity-restricted result (14.7). The server returns NO values here — only
 * that a result EXISTS, its category, ordering branch and sensitivity level — so no clinical value can reach the DOM.
 */
export const zRestrictedResult = z.object({
  restricted: z.literal(true),
  orderId: zId,
  lineId: zId,
  category: z.string(),
  status: z.string(),
  sensitivityLevel: z.string(),
  orderingBranch: z.string().nullable().optional(),
  date: z.string().optional(),
});
export type RestrictedResult = z.infer<typeof zRestrictedResult>;

/** A single result read — either full values or existence-only, discriminated on `restricted` (14.6/14.7). */
export const zResultDetail = z.discriminatedUnion("restricted", [zResultValue, zRestrictedResult]);
export type ResultDetail = z.infer<typeof zResultDetail>;

/** Input for a time-boxed report-access request (14.8) — purpose + justification are both mandatory (server 422s otherwise). */
export const zReportAccessInput = z.object({
  orderId: zId,
  lineId: zId,
  purposeCode: z.string().min(1),
  justification: z.string().min(1),
  requestedTtlHours: z.number().int().min(1).max(168),
});
export type ReportAccessInput = z.infer<typeof zReportAccessInput>;

/** Outcome of a report-access request (14.8) — the created request id + its lifecycle status (Pending/Granted). */
export const zReportAccessRequestResult = z.object({ requestId: zId, status: z.string() });
export type ReportAccessRequestResult = z.infer<typeof zReportAccessRequestResult>;

export const zRxRow = z.object({
  id: zId,
  beneficiary: zPatientRef,
  lineCount: z.number().int(),
  status: zStatus,
  submittedAt: zInstant.optional(),
});
export type RxRow = z.infer<typeof zRxRow>;

/**
 * Vitals capture (Phase 4, US-030 nurse triage). The emr VitalType space; one numeric reading per type, and
 * a blood pressure is therefore TWO readings — `BP` (systolic) and `BPDiastolic` — sent together. Recording
 * is treating-gated server-side (the nurse owns the encounter).
 */
export const zVitalType = z.enum(["BP", "BPDiastolic", "HR", "Temp", "SpO2", "Weight", "Height", "BMI"]);
export type VitalType = z.infer<typeof zVitalType>;

export const zVitalInput = z.object({ type: zVitalType, value: z.number() });
export type VitalInput = z.infer<typeof zVitalInput>;

/** Outcome of a vitals-capture submission — how many readings were recorded on the encounter. */
export const zVitalsResult = z.object({ encounterId: zId, recorded: z.number().int() });
export type VitalsResult = z.infer<typeof zVitalsResult>;

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

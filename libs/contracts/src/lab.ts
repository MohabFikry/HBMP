import { z } from "zod";
import { zCoded, zId, zInstant, zPatientRef, zPriority, zStatus } from "./common";

/**
 * Lab / Imaging fulfillment (Phase 5). MIN-NECESSARY: the queue shows the ordered test + a MASKED patient
 * ref only — never the ordering doctor's notes, never any prescription. There is no prescription field in
 * this schema by construction, so the Lab zone cannot leak Pharmacy data.
 */
export const zLabOrder = z.object({
  id: zId,
  kind: z.enum(["lab", "imaging"]),
  test: zCoded,
  patient: zPatientRef,
  priority: zPriority,
  status: zStatus,
  placedAt: zInstant,
  /** For partial fulfillment: total panels vs. already-completed. */
  panelsTotal: z.number().int().positive(),
  panelsDone: z.number().int().nonnegative(),
});
export type LabOrder = z.infer<typeof zLabOrder>;

/**
 * Atomic idempotent consume (Phase 5.2). `idempotencyKey` is sent as the `Idempotency-Key` header AND echoed
 * in the body so a retried/replayed request maps to the same fulfillment row. The server returns `replayed`
 * true when it recognised the key — the UI treats that as success, not a double-apply.
 */
export const zConsumeRequest = z.object({
  orderId: zId,
  idempotencyKey: z.string().uuid(),
  /** Panels fulfilled in THIS consume (enables partial fulfillment across visits). */
  panels: z.number().int().positive(),
});
export type ConsumeRequest = z.infer<typeof zConsumeRequest>;

export const zConsumeResult = z.object({
  orderId: zId,
  fulfillmentId: zId,
  status: zStatus,
  panelsDone: z.number().int().nonnegative(),
  panelsTotal: z.number().int().positive(),
  replayed: z.boolean(),
});
export type ConsumeResult = z.infer<typeof zConsumeResult>;

/** Result upload (Phase 5.3) — a document reference + the observation coding; routed back to the doctor. */
export const zResultUploadRequest = z.object({
  orderId: zId,
  idempotencyKey: z.string().uuid(),
  documentName: z.string().min(1),
  observation: zCoded.optional(),
  summary: z.string().max(1000).optional(),
});
export type ResultUploadRequest = z.infer<typeof zResultUploadRequest>;

export const zResultUploadResult = z.object({
  orderId: zId,
  documentId: zId,
  status: zStatus,
});
export type ResultUploadResult = z.infer<typeof zResultUploadResult>;

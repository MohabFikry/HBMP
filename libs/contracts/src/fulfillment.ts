import { z } from "zod";
import { zId, zInstant, zPatientRef } from "./common";

/**
 * Lab/imaging result-upload contracts (Phase 5.3, US-042). A result may only be attached to a line the
 * signed-in provider has consumed; the "awaiting result" worklist is those consumed-but-unreported lines.
 * Min-necessary: a masked beneficiary token + the line code — never a diagnosis or another provider's work.
 */
export const zResultTask = z.object({
  orderId: zId,
  lineId: zId,
  orderNo: z.string(),
  /** "Lab" | "Imaging". */
  orderType: z.string(),
  beneficiary: zPatientRef,
  code: z.string(),
  description: z.string().optional(),
  consumedAt: zInstant,
});
export type ResultTask = z.infer<typeof zResultTask>;

/** Outcome of uploading a result value/report for a consumed line. */
export const zResultUpload = z.object({
  orderId: zId,
  lineId: zId,
  uploaded: z.literal(true),
});
export type ResultUpload = z.infer<typeof zResultUpload>;

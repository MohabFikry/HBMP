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

// ── 32.5 Line notes (design 46 §7b) ─────────────────────────────────────────────────────────────────────

/**
 * Who a note is for.
 *
 * Three classes because the reader differs, and the middle one is why the vocabulary exists at all: an
 * external centre seeing a clinician's internal reasoning would widen the deliberately narrow projection
 * built for them in doc 45 §2b. The server filters BEFORE serialization, so a class a caller may not read
 * never reaches them — this enum types what may be WRITTEN, not what a screen is trusted to hide.
 *
 * <p>It lives in `fulfillment.ts` rather than beside one order kind because one panel serves all four:
 * labs, radiology, procedures and prescriptions. That is doc 46 §7b's requirement, and its reason — "a
 * second notes mechanism means two behaviours for 'cancel a note' and two answers to 'who can read this'".</p>
 */
export const zNoteVisibility = z.enum(["ToFulfiller", "Internal", "FromFulfiller"]);
export type NoteVisibility = z.infer<typeof zNoteVisibility>;

/** Which order kind a line belongs to. The panel is one component; the path differs. */
export const zLineNoteKind = z.enum(["investigation", "procedure", "prescription"]);
export type LineNoteKind = z.infer<typeof zLineNoteKind>;

/**
 * One operational note on an order or prescription line.
 *
 * <b>Cancelled notes are still returned</b>, deliberately: "there was a note here and it was withdrawn, by
 * X, because Z" is information, and a gap is not. The panel renders them struck through rather than
 * dropping them.
 */
export const zLineNote = z.object({
  noteId: zId,
  lineId: zId,
  visibility: zNoteVisibility,
  body: z.string(),
  authorDisplayName: z.string(),
  authoredAt: zInstant,
  status: z.enum(["Active", "Cancelled"]),
  cancelledAt: zInstant.nullish(),
  cancelReason: z.string().nullish(),
});
export type LineNote = z.infer<typeof zLineNote>;

import { z } from "zod";
import { zCoded, zId, zInstant, zPatientRef, zPriority, zStatus } from "./common";

/**
 * Lab / Imaging fulfillment (Phase 5). MIN-NECESSARY: the queue shows the ordered test + a MASKED patient
 * ref only — never the ordering doctor's notes, never any prescription. There is no prescription field in
 * this schema by construction, so the Lab zone cannot leak Pharmacy data.
 */
export const zLabOrder = z.object({
  id: zId,
  kind: z.enum(["lab", "radiology"]),
  test: zCoded,
  patient: zPatientRef,
  priority: zPriority,
  status: zStatus,
  placedAt: zInstant,
  /** For partial fulfillment: total panels vs. already-completed. */
  panelsTotal: z.number().int().positive(),
  panelsDone: z.number().int().nonnegative(),
  /** The order's own reference — ORD-2026-000900. What the patient is holding and the technician quotes. */
  orderNo: z.string(),
  /** When it stops being fulfillable. */
  expiresAt: zInstant.nullish(),
  /**
   * Past its validity window.
   *
   * <p>Its own flag rather than something inferred from `status`, for the reason the dispensing counter has
   * the same field: the expiry sweeper runs hourly, so between lapsing and being swept an order still reads
   * Active. A queue trusting the status would offer a technician an order that consume refuses.</p>
   */
  expired: z.boolean(),
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
  /**
   * Which lines, and how much of each.
   *
   * <p>OPTIONAL, and only the order page sends it. The queue collapses an order to its first line and one
   * panel count, which is enough for a single-test order and wrong for the three-line one the order page
   * shows — a technician who performed the second and third panels but not the first must be able to say so.
   * When absent the server is told about the order's first available line, exactly as before.</p>
   */
  lines: z.array(z.object({ lineId: zId, quantity: z.number().positive() })).optional(),
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

// ---------------------------------------------------------------- one order, on its own page (ADR-0034)

/**
 * One examination on an order.
 *
 * <p><b>Ordered, consumed and remaining are three separate figures and are never collapsed into one.</b>
 * "2 of 5 performed" and "3 remaining" answer different questions, and a bench that only shows the remainder
 * cannot tell a fresh order from one a patient has been working through over three visits.</p>
 */
export const zInvestigationOrderLine = z.object({
  id: zId,
  test: zCoded,
  quantityOrdered: z.number(),
  quantityConsumed: z.number(),
  status: zStatus,
});
export type InvestigationOrderLine = z.infer<typeof zInvestigationOrderLine>;

/** One investigation order with every line on it — what the order page is built from. */
export const zInvestigationOrder = z.object({
  id: zId,
  orderNo: z.string(),
  kind: z.enum(["lab", "radiology"]),
  patient: zPatientRef,
  status: zStatus,
  placedAt: zInstant,
  expiresAt: zInstant.nullish(),
  /** Its own flag, not inferred from `status`: the expiry sweeper runs hourly, so between lapsing and being
   *  swept an order still reads Active and consume would refuse it. */
  expired: z.boolean(),
  lines: z.array(zInvestigationOrderLine),
});
export type InvestigationOrder = z.infer<typeof zInvestigationOrder>;

/**
 * What an investigation order costs, and how it splits between the member and the payer.
 *
 * <p>Identical in shape to `zRxPricing`, deliberately: a bench and a dispensing counter are the same
 * situation — someone in front of a patient who is about to be told what they owe — and the two must not
 * answer differently. The split comes from eligibility through the same `libs/benefit-pricing` path a claim
 * is adjudicated by, so the figure quoted and the figure charged cannot diverge.</p>
 *
 * <p><b>`determinate: false` is why the amounts are nullable.</b> A screen must render a null as "cannot be
 * quoted" and NEVER as 0 — at a counter a zero reads as "free", and a beneficiary told their scan is free
 * has been told something their claim will later contradict.</p>
 */
export const zOrderPriceLine = z.object({
  orderLineId: zId,
  codeSystem: z.string(),
  code: z.string(),
  description: z.string().nullish(),
  quantityOrdered: z.number(),
  quantityConsumed: z.number(),
  /** Null when the catalogue holds no price for this examination — not zero. */
  unitPriceEgp: z.number().nullish(),
  lineTotalEgp: z.number().nullish(),
});
export type OrderPriceLine = z.infer<typeof zOrderPriceLine>;

export const zOrderPricing = z.object({
  lines: z.array(zOrderPriceLine),
  currency: z.string(),
  totalEgp: z.number().nullish(),
  memberShareEgp: z.number().nullish(),
  payerShareEgp: z.number().nullish(),
  determinate: z.boolean(),
  reason: z.string().nullish(),
  tierCode: z.string().nullish(),
  isCovered: z.boolean().nullish(),
  /** The amount the split was quoted on — the whole order, or what is about to be performed. It is the
   *  denominator for the percentage note under each share; `totalEgp` is the wrong one on a partial. */
  quotedOnEgp: z.number().nullish(),
  /** Which question the shares answer: what the patient pays for what is being performed now (`true`), or
   *  for the whole order (`false`). Re-quoted on the server, never scaled here — the split runs a deductible
   *  before a copay before coinsurance, so half an order is not half the share. */
  quotedOnPerformNow: z.boolean().nullish(),
});
export type OrderPricing = z.infer<typeof zOrderPricing>;

/**
 * Asking the approval team whether another examination may stand in for the one ordered.
 *
 * <p><b>A request, not a choice.</b> The pharmacy counter substitutes from the drug's ATC-5 class — a real
 * equivalence set in master data — and the server refuses anything outside it. Nothing equivalent exists for
 * examinations, so a picker here would have to be derived from the category, which would put "any radiology
 * procedure" behind a button. The honest version of "we do not know what is equivalent" is to ask someone
 * who does.</p>
 *
 * <p>`proposedCode` is optional on purpose: "we cannot run this one" is a complete and useful request, and
 * requiring a proposal would push a technician into naming a test they are not qualified to choose.</p>
 */
export const zSubstitutionRequest = z.object({
  orderId: zId,
  orderLineId: zId,
  orderReference: z.string(),
  beneficiaryId: zId,
  orderedCode: z.string(),
  orderedLabel: z.string().nullish(),
  proposedCode: z.string().optional(),
  reason: z.string().min(10),
});
export type SubstitutionRequest = z.infer<typeof zSubstitutionRequest>;

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

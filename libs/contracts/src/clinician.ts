import { z } from "zod";
import { zId, zInstant, zLocalized, zStatus, zPatientRef } from "./common";

/**
 * Clinician worklist contracts (Phase 4, US-032/033). These back the doctor portal's cross-encounter views:
 * "my orders" and "my prescriptions" — the things the signed-in clinician authored (server scopes by
 * CreatedBy == subject). Min-necessary: a masked beneficiary token + codes/status only, never another
 * clinician's work. Statuses are pre-resolved to non-color StatusChip kinds.
 */
/**
 * One examination on an investigation order, as its ORDERING clinician reads it back.
 *
 * <p>Deliberately not <c>zInvestigationOrderLine</c> (lab.ts): that shape is the fulfilling bench's — it
 * folds ordered-minus-remaining into a consumed figure for someone deciding what to perform next. A
 * prescriber opening their own order is asking what did I ask for, so the two quantities stay as the server
 * states them.</p>
 *
 * <p>These have always been on the wire. `/investigation-orders/mine` returns every line with the order, and
 * the client was reading `lines[0].code` for a "primary code" column and discarding the rest — so a doctor
 * could see that an order had four lines and never what any of them were.</p>
 */
export const zOrderRowLine = z.object({
  id: zId,
  code: z.string(),
  codeSystem: z.string(),
  /** The test's name. Null on a line whose code the catalogue could not describe — stated, never guessed. */
  description: z.string().nullable(),
  /** As raised. Not what remains — this view is the original, not the fulfilment state. */
  quantityOrdered: z.number(),
  /** Performed to date, shown apart from the ordered figure and never subtracted from it. */
  quantityConsumed: z.number(),
  status: zStatus,
});
export type OrderRowLine = z.infer<typeof zOrderRowLine>;

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
  /** Until when this order may be fulfilled. Absent = orders set no expiry on it. */
  expiresAt: zInstant.nullish(),
  /** The visit it was raised in — the key its care timeline is read by. Null on a row that predates it. */
  encounterId: zId.nullable(),
  /**
   * The lines as raised. They arrive with the row on the SAME response — `/investigation-orders/mine` has
   * always returned them — so reading an order back costs no second request and no second audited PHI read.
   * Exactly the arrangement `zRxRow.lines` already has for prescriptions.
   */
  lines: z.array(zOrderRowLine),
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

/**
 * One line of a prescription, as its AUTHOR reads it back.
 *
 * <p>Deliberately not <c>zPrescriptionLine</c> (pharmacy.ts): that shape is the dispensing counter's — it
 * carries `remaining` and `outOfStock` and folds dose/route/frequency into one string, because a pharmacist
 * is deciding what to hand over. A prescriber opening their own prescription is asking a different question
 * — what did I write — so the three sig fields stay separate and the quantity is the one PRESCRIBED.</p>
 *
 * <p>Every clinical field is nullable because the server can genuinely not hold it, and each null is an
 * absence the screen must SAY rather than smooth over. `drug` is the sharpest case: rows written before the
 * name snapshot (pharmacy migration 0006) carry only a drug uuid, and rendering the word "Medication" in the
 * gap — which is what the dispensing queue did — puts the name of the field where its value belongs.</p>
 */
export const zRxRowLine = z.object({
  id: zId,
  /**
   * 29.4 — the catalogue product, which is what a service HISTORY of this medicine is keyed on (design
   * 45 §4). The snapshotted `drug` name below is display text and changes with the catalogue; the uuid is
   * what "has this patient had this before?" can actually be asked about.
   */
  drugId: zId.nullable().optional(),
  /** Trade name + strength + form, snapshotted at prescribing. Null = not recorded on this row. */
  drug: zLocalized.nullable(),
  dose: z.string().nullable(),
  route: z.string().nullable(),
  frequency: z.string().nullable(),
  /** As written. Not what remains — this view is the original, not the fulfilment state. */
  quantityPrescribed: z.number(),
  /** Fulfilment to date, shown apart from the prescribed figure and never subtracted from it. */
  quantityDispensed: z.number(),
  /**
   * 31.3 — what those two figures COUNT: `boxes`, or the prescribing unit (`tabs`, `IU`, `ml`).
   *
   * <p>A quantity of 1 against a 24-tablet box and a quantity of 2250 against a box of insulin pens are both
   * correct and are counted in different things. Absent on lines written before 31.3, and on any line whose
   * unit the catalogue does not record — rendered as no unit, never as a guess.</p>
   */
  quantityUnit: z.string().nullish(),
  refillsAllowed: z.number().int(),
  status: zStatus,
});
export type RxRowLine = z.infer<typeof zRxRowLine>;

export const zRxRow = z.object({
  id: zId,
  /**
   * The prescription's own reference — RX-2026-000312.
   *
   * Required, not optional: pharmacy assigns it at creation (`rx_no varchar(20) NOT NULL UNIQUE`), so a row
   * without one is a broken response, not an unreferenced prescription. The encounter's Prescriptions table
   * rendered `id` under a "Reference" heading for want of this field — a uuid nobody can read back, quote on
   * the phone, or match against the paper in their hand. Same defect the dispensing screen had: the server
   * had always sent `rxNo` and nothing on the client asked for it.
   */
  rxNo: z.string(),
  beneficiary: zPatientRef,
  lineCount: z.number().int(),
  status: zStatus,
  submittedAt: zInstant.optional(),
  /** Until when this prescription may be dispensed. Absent = pharmacy set no expiry on it. */
  expiresAt: zInstant.optional(),
  /**
   * Who wrote it, snapshotted by pharmacy at submit. `null` = the prescription predates that snapshot
   * (pharmacy migration 0006) — an absence to be stated, never a name to be guessed at from an id.
   */
  prescriber: zLocalized.nullable(),
  /**
   * The lines as written. They arrive with the row on the SAME response — `/prescriptions/mine` has always
   * returned them — so reading a prescription back costs no second request and no second audited PHI read.
   */
  lines: z.array(zRxRowLine),
  /** The visit it was written in — the key its care timeline is read by. Null on a row that predates it. */
  encounterId: zId.nullable(),
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

/**
 * 30.6 — one entry in the CODED amendment/cancellation vocabulary (design 46 §7).
 *
 * <p>Served by the API rather than declared here, so adding a reason stays a data change. The code is what
 * makes "how often do we cancel, and why" answerable; the free text a clinician adds beside it answers "what
 * happened here", and neither substitutes for the other.</p>
 */
export const zAmendReasonOption = z.object({
  code: z.string(),
  nameEn: z.string(),
  nameAr: z.string(),
});
export type AmendReasonOption = z.infer<typeof zAmendReasonOption>;

/**
 * The outcome of withdrawing a WHOLE transaction — a prescription or an order — rather than one line.
 *
 * <p><b>Partial success is a first-class answer.</b> Design 46 §3: "if some lines are already consumed it
 * reports PARTIAL SUCCESS plainly rather than failing the lot or silently doing half." Both alternatives are
 * worse than the truth — failing the lot leaves a doctor unable to withdraw anything, and doing half leaves
 * them believing they have withdrawn everything.</p>
 *
 * <p>So the refusals are NAMED, per line, in the words the doctor needs. A count alone ("3 of 5 withdrawn")
 * tells them something went wrong and not which two are still live.</p>
 */
export const zWithdrawnLine = z.object({
  /** The service or medicine, as the doctor would name it — not the line uuid. */
  label: z.string(),
  withdrawn: z.boolean(),
  /** Why this line could not be withdrawn. Null on one that was. */
  refusal: z.string().nullable().optional(),
});
export type WithdrawnLine = z.infer<typeof zWithdrawnLine>;

export const zWithdrawResult = z.object({
  withdrawn: z.number().int(),
  total: z.number().int(),
  lines: z.array(zWithdrawnLine),
});
export type WithdrawResult = z.infer<typeof zWithdrawResult>;

import { z } from "zod";
import { zLocalized } from "./common";

/**
 * 29.2b — the EXTERNAL delivering provider's view of an order routed to it (design 45 §2b).
 *
 * <p><b>This schema is the client-side statement of the min-necessary projection.</b> The server decides what
 * crosses the wire — a withheld field is ABSENT from the JSON, never hidden here — but the schema being just
 * as narrow matters: a wider one invites somebody to populate the gap later, and `zod` would happily accept a
 * `diagnosis` the server started sending. There is no field here for a diagnosis, an encounter, a note, a
 * coverage amount or a cost-share, because a delivering centre sees none of them.</p>
 */
export const zProcedureQueueItem = z.object({
  orderId: z.string(),
  orderNo: z.string(),
  /** The LINE this row is about — required, because "record a session" has to name one (32.6). */
  orderLineId: z.string(),
  orderType: z.string(),
  status: z.string(),

  beneficiaryId: z.string(),
  /** NULL on the queue. A centre browsing a list of refugees' names is a disclosure nobody asked for — the
   * name appears only at the counter, behind two identifiers. */
  beneficiaryDisplayName: z.string().nullable().optional(),
  beneficiaryPhotoUrl: z.string().nullable().optional(),

  codeSystem: z.string(),
  code: z.string(),
  description: z.string().nullable().optional(),
  procedureTypeCode: z.string().nullable().optional(),

  /** From the APPROVED scope, never the requested one (design 45 §2). */
  sessionsAuthorised: z.number().int(),
  sessionsDelivered: z.number().int(),
  sessionsRemaining: z.number().int(),
  /** "4 of 6 sessions delivered" — the SAME sentence the ordering doctor's worklist shows. */
  progressLabel: z.string(),

  authorised: z.boolean(),
  validUntil: z.string().nullable().optional(),
  expired: z.boolean(),

  /** What the ordering doctor DELIBERATELY chose to share. Null means NOT DISCLOSED — never "no diagnosis". */
  sharedClinicalContext: z.string().nullable().optional(),

  /** When this centre closed the loop, or null while it is still open (32.6, design 45 §7). */
  completionReportedAt: z.string().nullable().optional(),
});

export type ProcedureQueueItem = z.infer<typeof zProcedureQueueItem>;

/** Progress after recording one session. */
export const zSessionProgress = z.object({
  orderId: z.string(),
  orderLineId: z.string(),
  sessionsDelivered: z.number().int(),
  sessionsAuthorised: z.number().int(),
  sessionsRemaining: z.number().int(),
  progressLabel: z.string(),
});

export type SessionProgress = z.infer<typeof zSessionProgress>;

/** Why a counter lookup could not answer. Four outcomes, and only one means "this person has nothing". */
export const zCounterOutcome = z.enum(["resolved", "notFound", "tooFewIdentifiers", "unavailable"]);
export type CounterOutcome = z.infer<typeof zCounterOutcome>;

export const zProcedureLabels = z.object({ label: zLocalized });

/**
 * 29.4 — one previous occurrence of a service (design 45 §4).
 *
 * <p>The schema is as narrow as the projection: a RESTRICTED row carries no `resultSummary` and no
 * `numericValue`, because the server never sent one. There is nothing here for a client to hide, which is the
 * difference between a gate and a display rule.</p>
 */
export const zServiceHistoryRow = z.object({
  orderId: z.string(),
  orderNo: z.string(),
  orderLineId: z.string(),
  serviceType: z.string(),
  codeSystem: z.string(),
  code: z.string(),
  description: z.string().nullable().optional(),
  occurredAt: z.string(),
  status: z.string(),
  actorUserId: z.string().nullable().optional(),
  branchId: z.string().nullable().optional(),
  /** True ⇒ existence only: date, service, actor, branch and this marker. */
  restricted: z.boolean(),
  sensitivityLevel: z.string().nullable().optional(),
  resultSummary: z.string().nullable().optional(),
  numericValue: z.number().nullable().optional(),
});
export type ServiceHistoryRow = z.infer<typeof zServiceHistoryRow>;

/**
 * 29.2 — an OP-Procedure KIND, as masterdata administers it (design 45 §2).
 *
 * <p><b>Master data, not an enum.</b> Adding "Hydrotherapy" must be a data change, not a release, so this
 * schema describes a row rather than a closed union.</p>
 *
 * <p><b>`isSessionBased` is the whole point.</b> The composer reveals its sessions field from this FLAG and
 * never from the code — physiotherapy is session-based today, and dialysis and rehabilitation obviously are
 * too. branching on the type's NAME would guarantee that conversation twice more.</p>
 */
export const zProcedureType = z.object({
  code: z.string(),
  name: zLocalized,
  isSessionBased: z.boolean(),
  /** What the sessions field starts at. Null on a non-session type, which has no such field. */
  defaultSessions: z.number().int().nullable().optional(),
  /** The composer stops the doctor here; orders-service refuses above it regardless. */
  maxSessions: z.number().int().nullable().optional(),
  /**
   * The CPT sections this type may accompany. A physiotherapy type on a minor-surgery code is a data error.
   * Held here so the composer can say so as the doctor picks — but the verdict that BINDS is the write
   * path's, because this one is display state.
   */
  allowedCptScopes: z.array(z.string()),
});
export type ProcedureType = z.infer<typeof zProcedureType>;

/**
 * 29.2 — which VEHICLE a CPT code creates (design 45 §2).
 *
 * <p>The doctor picks a service; the SYSTEM decides what it becomes. That distinction matters downstream: a
 * referral is not finished until a report comes back, and a procedure needs fulfilment and consumption. Get
 * it backwards and the loop is never opened, so it can never be found open — the classic outpatient
 * patient-safety failure.</p>
 */
export const zOrderableVehicle = z.enum([
  "ProcedureOrder", "Referral", "LabOrder", "RadiologyOrder", "NotOrderable",
]);
export type OrderableVehicle = z.infer<typeof zOrderableVehicle>;

/**
 * One code the doctor may choose, and what choosing it will actually create.
 *
 * <p>Read BEFORE the doctor commits — that is the whole reason `/orderable-services` exists. A composer
 * that discovers the vehicle only on submit cannot show anyone what is about to happen.</p>
 */
export const zOrderableService = z.object({
  code: z.string(),
  description: z.string(),
  section: z.string(),
  vehicle: zOrderableVehicle,
  /** False ⇒ the code exists but cannot be raised. `reason` says why. */
  orderable: z.boolean(),
  /**
   * Why a non-orderable code cannot be raised. Present so the option can be shown and EXPLAINED rather than
   * filtered out: a code that vanishes from a search reads as a typo to the doctor who typed it correctly.
   */
  reason: zLocalized.nullable().optional(),
});
export type OrderableService = z.infer<typeof zOrderableService>;

/** 29.2 — a raised referral, as the composer reports it back to the doctor. */
export const zReferralCreated = z.object({
  referralId: z.string(),
  referralNo: z.string(),
  status: z.string(),
  requestedServiceCode: z.string().nullable().optional(),
});
export type ReferralCreated = z.infer<typeof zReferralCreated>;

export const zTrendPoint = z.object({ at: z.string(), value: z.number() });

export const zServiceHistory = z.object({
  beneficiaryId: z.string(),
  serviceType: z.string().nullable().optional(),
  code: z.string().nullable().optional(),
  total: z.number().int(),
  page: z.number().int(),
  pageSize: z.number().int(),
  /** Built ONLY from rows the caller may see — a chart across restricted points leaks them by position. */
  trend: z.array(zTrendPoint),
  items: z.array(zServiceHistoryRow),
  /**
   * 29.4 — TRUE when the prescription half could not be loaded, so this history is INCOMPLETE rather than
   * complete-and-short (design 45 §4).
   *
   * <p>The three-state rule reaches inside a single response here: the orders half answered and the
   * pharmacy half did not, and a reader who is not told that will take a short list for the whole story.</p>
   */
  prescriptionsUnavailable: z.boolean().optional(),
});
export type ServiceHistory = z.infer<typeof zServiceHistory>;

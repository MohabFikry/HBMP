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
});
export type ServiceHistory = z.infer<typeof zServiceHistory>;

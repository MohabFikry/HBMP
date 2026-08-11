import { z } from "zod";
import { zLocalized } from "./common";
import { zPeriod } from "./dashboard";

/**
 * The Medical Director's oversight reads (2026-08-11 audit).
 *
 * Three views the portal could not ask for, all served from reporting-service's PHI-free zone:
 * utilization across every dimension it supports, the authorizations behind an SLA-breach count, and claim
 * outcomes with what they cost.
 *
 * <b>The shared property.</b> Every one carries the PERIOD it covers. The reporting endpoints have always
 * accepted `from`/`to` and the portal sent neither, so figures built from endpoints with different server
 * defaults sat in the same KPI row with nothing saying they covered different days.
 */

/**
 * The four axes `/reports/utilization` supports.
 *
 * Named `ServiceAxis` rather than `UtilizationDimension`: finance already exports a `UtilizationView` for the
 * member-benefit sense of the word — how much of a cap somebody has consumed — and two contracts sharing a
 * name across one barrel export is how a screen ends up importing the wrong one and type-checking anyway.
 *
 * The dashboard pinned this to `provider` and titled the widget "by service line", so three of the four were
 * reachable from no screen in the application and the one that rendered was labelled as a different axis.
 */
export const zServiceAxis = z.enum(["provider", "drug", "lab", "radiology"]);
export type ServiceAxis = z.infer<typeof zServiceAxis>;

export const zAxisUsageRow = z.object({
  /** Provider id, ATC class, or service code — whatever the dimension is keyed on. */
  code: z.string(),
  count: z.number(),
});
export type AxisUsageRow = z.infer<typeof zAxisUsageRow>;

export const zServiceUseView = z.object({
  dimension: zServiceAxis,
  period: zPeriod,
  rows: z.array(zAxisUsageRow),
});
export type ServiceUseView = z.infer<typeof zServiceUseView>;

/**
 * One breached authorization.
 *
 * NO BENEFICIARY, and the absence is the design rather than an omission. The Medical Director holds
 * `auth:read` and could open any of these, but a supervisor who opens individual files to check them is
 * doing the reviewer's job — so this list carries what is needed to act on a QUEUE (which request, how
 * urgent, how long, whose desk) and nothing that identifies a patient.
 */
export const zSlaBreachRow = z.object({
  authNo: z.string(),
  priority: z.string(),
  status: z.string(),
  ageBucket: z.string(),
  ageSeconds: z.number(),
  reviewerId: z.string().nullish(),
});
export type SlaBreachRow = z.infer<typeof zSlaBreachRow>;

export const zSlaBreachView = z.object({
  total: z.number(),
  rows: z.array(zSlaBreachRow),
});
export type SlaBreachView = z.infer<typeof zSlaBreachView>;

export const zClaimOutcomeRow = z.object({ outcome: z.string(), count: z.number() });
export const zCostRow = z.object({ serviceLine: z.string(), amount: z.number(), count: z.number() });
export const zDenialRow = z.object({ reasonCode: z.string(), count: z.number() });

/**
 * Claim outcomes and cost for the oversight portal.
 *
 * Served from reporting's financial zone, which the director already holds — NOT from claims-service, which
 * would have meant granting an operational claims scope to render a chart. A supervisor needs the shape of
 * what was claimed and denied; opening a claimant's file is the claims officer's authority.
 */
export const zClaimsCostView = z.object({
  period: zPeriod,
  decided: z.number(),
  totalAllowed: z.number(),
  byOutcome: z.array(zClaimOutcomeRow),
  byServiceLine: z.array(zCostRow),
  topDenialReasons: z.array(zDenialRow),
});
export type ClaimsCostView = z.infer<typeof zClaimsCostView>;

/** Bilingual labels for the utilization dimensions, so the picker is not four English words in an RTL page. */
export const SERVICE_AXIS_LABELS: Record<ServiceAxis, { en: string; ar: string }> = {
  provider: { en: "Provider", ar: "مقدم الخدمة" },
  drug: { en: "Medication", ar: "الدواء" },
  lab: { en: "Laboratory", ar: "المختبر" },
  radiology: { en: "Radiology", ar: "الأشعة" },
};

export type DirectorLabel = z.infer<typeof zLocalized>;

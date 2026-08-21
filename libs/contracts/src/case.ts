import { z } from "zod";
import { zId, zInstant, zLocalized, zStatus, zCoded, zPatientRef } from "./common";

/**
 * Case-management contracts (Phase 10.1/10.3). The Case Manager portal is assignment-scoped and minimum-necessary:
 * the **beneficiary-360 is a COORDINATION SUMMARY** — coverage/care-plan/appointments/approval STATUS plus a clinical
 * summary where diagnoses are coord-visible but notes/prescriptions/results are represented ONLY as masked counts.
 * There is deliberately no field on these schemas that can carry a raw clinical note / result body.
 */

export const zCaseCategory = z.enum(["complex", "chronic", "vulnerable", "escalation"]);
export type CaseCategory = z.infer<typeof zCaseCategory>;

export const zCaseStatus = z.enum(["open", "active", "on_hold", "resolved", "closed"]);
export type CaseStatus = z.infer<typeof zCaseStatus>;

export const zCasePriority = z.enum(["low", "normal", "high", "urgent"]);
export type CasePriority = z.infer<typeof zCasePriority>;

/** A row on the My-Cases worklist — no clinical content. */
/** The case lifecycle, as the caseworker moves a file through it. */
export const zCaseState = z.enum(["open", "active", "on_hold", "resolved", "closed"]);
export type CaseState = z.infer<typeof zCaseState>;

export const zCaseListItem = z.object({
  id: zId,
  caseNo: z.string(),
  beneficiary: zPatientRef,
  category: zCaseCategory,
  priority: zCasePriority,
  status: zStatus,
  /** The domain value behind `status`. See `zEscalationState` for why both travel. */
  state: zCaseState,
  openedAt: zInstant,
  summary: zLocalized.optional(),
});
export type CaseListItem = z.infer<typeof zCaseListItem>;

/** A masked clinical section on the 360: how many records exist (presence), never their content. */
export const zMaskedSection = z.object({
  count: z.number().int().nonnegative(),
  summaryOnly: z.literal(true), // always summary-only — the record body is never carried
});
export type MaskedSection = z.infer<typeof zMaskedSection>;

/** The coordination clinical SUMMARY: coord-visible diagnoses + masked note/rx/result counts. */
export const zClinicalSummary = z.object({
  activeDiagnoses: z.array(zCoded),
  notes: zMaskedSection,
  prescriptions: zMaskedSection,
  results: zMaskedSection,
});
export type ClinicalSummary = z.infer<typeof zClinicalSummary>;

export const zCoverageSummary = z.object({
  status: zStatus,
  planName: zLocalized,
  coverageCategory: zLocalized,
  // 18.D2 (U7): raw numbers; formatted at render in the active locale (see useFormat).
  annualCap: z.number().optional(),
  remaining: z.number().optional(),
});

export const zCarePlanSummary = z.object({
  status: zLocalized,
  goals: z.array(zLocalized),
  reviewDue: zInstant.optional(),
});

export const zAppointmentSummary = z.object({
  id: zId,
  clinic: zLocalized,
  when: zInstant,
  status: zStatus,
});

export const zApprovalStatusSummary = z.object({
  authNo: z.string(),
  status: zStatus,
  priority: zCasePriority,
  decidedAt: zInstant.optional(),
});

/** The field-scoped beneficiary-360 coordination view. */
export const zBeneficiary360 = z.object({
  caseId: zId,
  caseNo: z.string(),
  beneficiary: zPatientRef,
  coverage: zCoverageSummary,
  carePlan: zCarePlanSummary,
  appointments: z.array(zAppointmentSummary),
  openApprovals: z.array(zApprovalStatusSummary),
  clinical: zClinicalSummary,
});
export type Beneficiary360 = z.infer<typeof zBeneficiary360>;

export const zTaskState = z.enum(["todo", "in_progress", "done", "cancelled"]);
export type TaskState = z.infer<typeof zTaskState>;

export const zCoordinationTask = z.object({
  id: zId,
  caseId: zId,
  title: zLocalized,
  state: zTaskState,
  dueAt: zInstant.optional(),
  status: zStatus,
});
export type CoordinationTask = z.infer<typeof zCoordinationTask>;

/**
 * The escalation status vocabulary, alongside the rendered chip.
 *
 * `status` is a `{kind, label}` chip for display and `state` is the domain value the two transitions turn
 * on. Both, because deciding what to offer from a translated label is the defect the network roll-up was
 * (see `zNetworkMetrics`): "Raised" may be acknowledged, "Acknowledged" may be resolved, and "Resolved" is
 * terminal — none of which survives a chip.
 */
export const zEscalationState = z.enum(["raised", "acknowledged", "resolved"]);
export type EscalationState = z.infer<typeof zEscalationState>;

export const zEscalation = z.object({
  id: zId,
  caseId: zId,
  caseNo: z.string(),
  raisedToRole: zLocalized,
  reason: z.string(),
  state: zEscalationState,
  status: zStatus,
  raisedAt: zInstant,
  /** Set when the escalation was closed; the note is what closing it was FOR. */
  resolvedAt: zInstant.nullable(),
  resolutionNote: z.string().nullable(),
});
export type Escalation = z.infer<typeof zEscalation>;



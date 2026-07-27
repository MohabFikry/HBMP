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
export const zCaseListItem = z.object({
  id: zId,
  caseNo: z.string(),
  beneficiary: zPatientRef,
  category: zCaseCategory,
  priority: zCasePriority,
  status: zStatus,
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

export const zEscalation = z.object({
  id: zId,
  caseId: zId,
  caseNo: z.string(),
  raisedToRole: zLocalized,
  reason: z.string(),
  status: zStatus,
  raisedAt: zInstant,
});
export type Escalation = z.infer<typeof zEscalation>;

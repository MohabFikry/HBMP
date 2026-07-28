import { z } from "zod";
import { zId, zInstant, zDate } from "./common";

/**
 * Phase 20 — the unified patient profile (design 39).
 *
 * **These schemas are permissive on purpose, and that is the opposite of every other contract here.** Elsewhere
 * a strict schema is the min-necessary guarantee: a screen physically cannot read a field its zone forbids
 * because the type has no such property. The profile inverts that. The SERVER decides what each role receives
 * and omits the rest, so a section's payload legitimately differs between a treating doctor and a receptionist —
 * and a schema that demanded the doctor's shape would reject the receptionist's valid response.
 *
 * So the min-necessary guarantee for this feature lives entirely server-side (proved by reflection tests over
 * the serialized payload), and these schemas exist to parse what arrives, not to constrain it. Everything below
 * the section envelope is `.optional()` for that reason — not laziness.
 */

/** The four states, three of which are NOT the same thing (design 39 §6). */
export const zSectionState = z.enum(["Visible", "Restricted", "NotApplicable", "Unavailable"]);
export type SectionState = z.infer<typeof zSectionState>;

/** The offered way out of a Restricted state — rendered as the "Request access" control. */
export const zRequestAccessAction = z.object({
  kind: z.string(),
  href: z.string(),
  label: z.string().optional(),
});
export type RequestAccessAction = z.infer<typeof zRequestAccessAction>;

/** Non-colour status semantics: hue is never the only signal (21-accessibility). */
export const zStatusCue = z.object({
  label: z.string(),
  icon: z.string(),
  shape: z.string(),
  tone: z.string(),
});
export type StatusCue = z.infer<typeof zStatusCue>;

// ---------------------------------------------------------------- section payloads

export const zProfileHeader = z.object({
  beneficiaryId: zId,
  memberNo: z.string().optional(),
  displayName: z.string(),
  displayNameAr: z.string().optional(),
  ageBand: z.string().optional(),
  sex: z.string().optional(),
  status: z.string(),
  statusCue: zStatusCue,
  branchName: z.string().optional(),
  preferredLanguage: z.string().optional(),
  contact: z.object({ phone: z.string().optional(), preferredChannel: z.string().optional() }).optional(),
  /** Absent entirely for roles outside the design-39 §5 allow-list — the client renders initials. */
  photoUrl: z.string().optional(),
});
export type ProfileHeader = z.infer<typeof zProfileHeader>;

export const zProfileAlerts = z.object({
  allergies: z.array(z.object({
    allergen: z.string(),
    reaction: z.string().optional(),
    severity: z.string(),
  })),
  criticalFlags: z.array(z.object({ kind: z.string(), label: z.string(), tone: z.string() })).optional(),
  interactionWarnings: z.array(z.object({ kind: z.string(), label: z.string(), tone: z.string() })).optional(),
  operationalFlags: z.array(z.object({ kind: z.string(), label: z.string(), tone: z.string() })).optional(),
});
export type ProfileAlerts = z.infer<typeof zProfileAlerts>;

/** Direction, rendered with FOUR cues — hue AND arrow icon AND chip shape AND the word (design 39 §5b). */
export const zCallDirection = z.enum(["Inbound", "Outbound"]);
export type CallDirection = z.infer<typeof zCallDirection>;

export const zCallHistoryRow = z.object({
  callRef: z.string(),
  direction: zCallDirection,
  startedAt: zInstant,
  endedAt: zInstant.optional(),
  durationSeconds: z.number().int().optional(),
  branchCode: z.string().optional(),
  agentDisplayName: z.string().optional(),
  reasonCode: z.string().optional(),
  outcome: z.string().optional(),
  verification: z.object({ result: z.string(), identifierTypes: z.array(z.string()) }).optional(),
  /** Absent at Meta level. The client must not synthesise a placeholder — absence is the information. */
  summary: z.string().optional(),
  summaryEdited: z.boolean().default(false),
  linkedArtifacts: z.array(z.object({
    type: z.string(),
    ref: z.string(),
    action: z.string().optional(),
  })).optional(),
  /**
   * SERVER-GENERATED from the served projection. The client copies this string verbatim and never assembles
   * one from fields it holds, nor scrapes it from the DOM (design 39 §5b rule 1).
   */
  copyText: z.string(),
});
export type CallHistoryRow = z.infer<typeof zCallHistoryRow>;

export const zCallHistorySection = z.object({
  level: z.string(),
  items: z.array(zCallHistoryRow),
  nextCursor: z.string().optional(),
});
export type CallHistorySection = z.infer<typeof zCallHistorySection>;

// ---------------------------------------------------------------- the envelope

/**
 * One independently-gated section.
 *
 * `data` is ABSENT — not null, not empty — whenever the state is anything but Visible. The three non-visible
 * states are rendered differently on purpose, and collapsing them turns a permissions problem into a clinical
 * one: a user must never confuse "you may not see this", "it broke", and "there is nothing".
 */
export const zProfileSection = z.object({
  key: z.string(),
  state: zSectionState,
  reasonCode: z.string().optional(),
  requestAccessAction: zRequestAccessAction.optional(),
  data: z.unknown().optional(),
});
export type ProfileSection = z.infer<typeof zProfileSection>;

export const zPatientProfile = z.object({
  beneficiaryId: zId,
  servedAt: zInstant,
  /** In design-39 §3 render order. A section the caller may never see is not in this list at all. */
  sections: z.array(zProfileSection),
});
export type PatientProfile = z.infer<typeof zPatientProfile>;

/** The 15 section keys, in render order. Mirrors `ProfileSections.All` server-side. */
export const PROFILE_SECTION_KEYS = [
  "header", "alerts", "coverage", "pastMedicalHistory", "encounters", "investigations", "prescriptions",
  "authorizations", "referrals", "documents", "notes", "financial", "caseManagement", "timeline", "callHistory",
] as const;
export type ProfileSectionKey = (typeof PROFILE_SECTION_KEYS)[number];

/** The context bar asks for these two only — it is on every clinical screen and cannot be slow. */
export const CONTEXT_BAR_SECTIONS: ProfileSectionKey[] = ["header", "alerts"];

/**
 * The role-projected print/export summary. Composed SERVER-SIDE from the same projection the screen received
 * and audited as a PHI export — it can never contain a section the viewer could not see (design 39 §6).
 */
export const zExportWatermark = z.object({
  viewerSubject: z.string(),
  viewerRoles: z.string(),
  generatedAt: zInstant,
  purpose: z.string(),
});
export type ExportWatermark = z.infer<typeof zExportWatermark>;

export const zProfileExportSummary = z.object({
  profile: zPatientProfile,
  /** On the PAYLOAD, not decoration the client adds: an export printable without it leaves unattributed. */
  watermark: zExportWatermark,
});
export type ProfileExportSummary = z.infer<typeof zProfileExportSummary>;

/** The result of "copy all visible" — one server-generated block, one audit event. */
export const zCopySummariesResult = z.object({
  level: z.string(),
  callRefs: z.array(z.string()),
  copyText: z.string(),
});
export type CopySummariesResult = z.infer<typeof zCopySummariesResult>;

/** Generic row shapes for the list-style sections. Loose by design — see the file header. */
export const zProfileRows = z.object({ items: z.array(z.record(z.unknown())).optional() }).passthrough();

export const zProfileDate = zDate;

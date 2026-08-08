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
  /** Cover relationship — Principal, Spouse, Child, Dependent. */
  relationship: z.string().optional(),
  nationalityCode: z.string().optional(),
  /**
   * Exact birth date, so the header shows an AGE rather than a band. Stripped by `V(min)`: labs and
   * pharmacies get `ageBand`, which is all a specimen label or a dose check needs.
   */
  birthDate: zDate.optional(),
  /** Travels WITH the date. An estimate rendered as an exact age becomes a hard eligibility cutoff. */
  birthDateIsApproximate: z.boolean().optional(),
});
export type ProfileHeader = z.infer<typeof zProfileHeader>;

export const zProfileAlerts = z.object({
  allergies: z.array(z.object({
    allergen: z.string(),
    reaction: z.string().optional(),
    severity: z.string(),
  })),
  /**
   * ABO + Rh, or absent when nobody has recorded one.
   *
   * It arrives on ALERTS rather than on the header because that is where it comes from: emr, behind the
   * clinical gate, in the same call as the allergy list. The header is built from the administrative record
   * that reception and the call centre also read, and blood group does not belong there.
   *
   * `.nullish()` — the server sends `"bloodGroup": null` for a patient nobody has typed. `.optional()`
   * accepts `undefined` and REJECTS `null`, which is the exact mismatch that made every prescribing
   * validation response fail to parse in phase 26.
   */
  bloodGroup: z.string().nullish(),
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

/**
 * Sections 3–14, mirroring the records in `services/profile/Domain/Sections.cs`.
 *
 * **Every field below the section root is optional, including the row arrays, and that is deliberate.** Each of
 * these payloads has variant projections server-side (`V(meta)`, `V(admin)`, `V(amounts)`, `V(summary)`,
 * `V(pharmacy limit)` …) that null out whole fields, and the serializer omits nulls. A pharmacy's coverage
 * arrives with no `payerName`; a case manager's history arrives with no `narrative`; the Medical Director's
 * financial section arrives with no `claims`. Declaring any of those required would make the narrower — and
 * entirely valid — projection a type error, and would tempt a view into rendering a placeholder where the
 * honest answer is that the field was never served.
 *
 * So the views must treat every absence as "not served at this level" and render nothing for it, never an empty
 * cell. Absence is information here; see `39-patient-profile.md §4`.
 */

// ---------------------------------------------------------------- 3. coverage

export const zCoverageLimitLine = z.object({
  category: z.string(),
  annualLimit: z.number().optional(),
  consumed: z.number().optional(),
  remaining: z.number().optional(),
  costSharePercent: z.number().optional(),
  costShareTier: z.string().optional(),
});
export type CoverageLimitLine = z.infer<typeof zCoverageLimitLine>;

export const zProfileCoverage = z.object({
  payerName: z.string().optional(),
  policyNo: z.string().optional(),
  planLabel: z.string().optional(),
  planVersion: z.number().int().optional(),
  effectiveFrom: zDate.optional(),
  effectiveTo: zDate.optional(),
  waitingPeriodState: z.string().optional(),
  categories: z.array(zCoverageLimitLine).optional(),
});
export type ProfileCoverage = z.infer<typeof zProfileCoverage>;

// ---------------------------------------------------------------- 4. past medical history

export const zCodedCondition = z.object({
  system: z.string().optional(),
  code: z.string().optional(),
  display: z.string(),
  clinicalStatus: z.string().optional(),
  onsetOn: zDate.optional(),
});
export type CodedCondition = z.infer<typeof zCodedCondition>;

export const zHistoricalRecord = z.object({
  linkId: zId,
  documentClass: z.string().optional(),
  title: z.string(),
  documentDate: zDate.optional(),
});
export type HistoricalRecord = z.infer<typeof zHistoricalRecord>;

export const zProfilePastMedicalHistory = z.object({
  conditions: z.array(zCodedCondition).optional(),
  narrative: z.string().optional(),
  uploadedRecords: z.array(zHistoricalRecord).optional(),
});
export type ProfilePastMedicalHistory = z.infer<typeof zProfilePastMedicalHistory>;

// ---------------------------------------------------------------- 5. encounters

export const zEncounterRow = z.object({
  encounterRef: z.string(),
  occurredAt: zInstant,
  branchName: z.string().optional(),
  clinicianName: z.string().optional(),
  specialty: z.string().optional(),
  /** Dropped under `V(meta)`: "chest pain" is clinical content, and reception gets a visit's logistics only. */
  reason: z.string().optional(),
  status: z.string(),
  /**
   * The encounter's id, so the row can be OPENED. `encounterRef` is the human-readable number and addresses
   * nothing.
   *
   * Absent under `V(meta)` — reception, finance and beneficiary management have no encounter workspace, so
   * the handle is withheld rather than sent to a role that cannot use it. A view must therefore treat absence
   * as "not openable by you", never as a broken row.
   */
  encounterId: zId.optional(),
  /**
   * Branch and clinician as IDS. emr owns neither name — branch labels and a practitioner's name and
   * specialty belong to other services — so the view resolves them, the same join the day board makes for
   * branch labels and the booking picker makes for doctors.
   */
  branchId: zId.optional(),
  clinicianId: zId.optional(),
});
export type EncounterRow = z.infer<typeof zEncounterRow>;

export const zProfileEncounters = z.object({ items: z.array(zEncounterRow).optional() });
export type ProfileEncounters = z.infer<typeof zProfileEncounters>;

// ---------------------------------------------------------------- 6. investigations

export const zInvestigationRow = z.object({
  orderRef: z.string(),
  lineId: zId,
  category: z.string().optional(),
  orderedOn: zInstant,
  status: z.string(),
  providerName: z.string().optional(),
  /**
   * Absent whenever `restricted` is true — the owning service never sent a value, so there is nothing here to
   * withhold. A view must not treat this absence as "result pending" (design 37 §6).
   */
  resultSummary: z.string().optional(),
  restricted: z.boolean().optional(),
  sensitivityLevel: z.string().optional(),
  /**
   * 29.2 — Lab / Radiology / Procedure (design 45 §3). Lets the History tab be read by KIND of service
   * rather than as one flat list.
   *
   * <p>Optional, and absence must read as "unknown kind", never as one of the real ones: a row whose type
   * the upstream did not state must not appear in the Procedures pane, because that would tell a doctor a
   * procedure was ordered when nothing said so.</p>
   */
  orderType: z.string().optional(),
  /** The encounter this order was raised on — lets one visit's orders be told from the member's whole
   *  history. An id only; it discloses no clinical content of its own. */
  encounterId: zId.optional(),
});
export type InvestigationRow = z.infer<typeof zInvestigationRow>;

export const zProfileInvestigations = z.object({ items: z.array(zInvestigationRow).optional() });
export type ProfileInvestigations = z.infer<typeof zProfileInvestigations>;

// ---------------------------------------------------------------- 7. prescriptions

export const zProfileRxRow = z.object({
  rxRef: z.string(),
  drugDisplay: z.string(),
  status: z.string(),
  prescribedOn: zInstant,
  dispensedOn: zInstant.optional(),
  batchNo: z.string().optional(),
  expiryDate: zDate.optional(),
  substitutedWith: z.string().optional(),
  /** The encounter this prescription was written on — see the note on `zInvestigationRow`. */
  encounterId: zId.optional(),
});
export type ProfileRxRow = z.infer<typeof zProfileRxRow>;

export const zProfilePrescriptions = z.object({ items: z.array(zProfileRxRow).optional() });
export type ProfilePrescriptions = z.infer<typeof zProfilePrescriptions>;

// ---------------------------------------------------------------- 8. authorizations

export const zAuthorizationRow = z.object({
  authNo: z.string(),
  serviceCategory: z.string().optional(),
  status: z.string(),
  requestedAt: zInstant,
  decidedAt: zInstant.optional(),
  validUntil: zDate.optional(),
  /** Dropped for reception (`V(status)`) and finance (`V(cost)`) — clinical reasoning is neither one's zone. */
  rationale: z.string().optional(),
  /** Dropped for reception (`V(status)`). */
  approvedAmount: z.number().optional(),
});
export type AuthorizationRow = z.infer<typeof zAuthorizationRow>;

export const zProfileAuthorizations = z.object({ items: z.array(zAuthorizationRow).optional() });
export type ProfileAuthorizations = z.infer<typeof zProfileAuthorizations>;

// ---------------------------------------------------------------- 9. referrals

export const zReferralRow = z.object({
  referralRef: z.string(),
  status: z.string(),
  requestedSpecialty: z.string().optional(),
  createdAt: zInstant,
  /** Absent while the loop is still open — which is the thing a coordinator is looking for. */
  loopClosedAt: zInstant.optional(),
});
export type ReferralRow = z.infer<typeof zReferralRow>;

export const zProfileReferrals = z.object({ items: z.array(zReferralRow).optional() });
export type ProfileReferrals = z.infer<typeof zProfileReferrals>;

// ---------------------------------------------------------------- 10. documents

export const zDocumentRow = z.object({
  linkId: zId,
  documentClass: z.string().optional(),
  visibilityClass: z.string().optional(),
  title: z.string(),
  documentDate: zDate.optional(),
  uploadedAt: zInstant,
  status: z.string(),
  /** Metadata is always served; the CONTENT is gated separately (design 39 §3 row 10). */
  mayDownload: z.boolean().optional(),
});
export type DocumentRow = z.infer<typeof zDocumentRow>;

export const zProfileDocuments = z.object({ items: z.array(zDocumentRow).optional() });
export type ProfileDocuments = z.infer<typeof zProfileDocuments>;

// ---------------------------------------------------------------- 11. notes

export const zNoteRow = z.object({
  noteId: zId,
  noteType: z.string().optional(),
  visibilityClass: z.string().optional(),
  /** Absent when `withheld` is true. The note's EXISTENCE is not the secret, its content is (19.3). */
  body: z.string().optional(),
  authorDisplay: z.string().optional(),
  createdAt: zInstant,
  withheld: z.boolean().optional(),
  pinned: z.boolean().optional(),
});
export type NoteRow = z.infer<typeof zNoteRow>;

export const zProfileNotes = z.object({ items: z.array(zNoteRow).optional() });
export type ProfileNotes = z.infer<typeof zProfileNotes>;

// ---------------------------------------------------------------- 12. financial

export const zFinancialClaimRow = z.object({
  claimNo: z.string(),
  serviceDate: zDate.optional(),
  billedAmount: z.number().optional(),
  approvedAmount: z.number().optional(),
  memberShare: z.number().optional(),
  status: z.string(),
});
export type FinancialClaimRow = z.infer<typeof zFinancialClaimRow>;

/** Money only. There is no property here that can carry a diagnosis — the shape IS the rule. */
export const zProfileFinancial = z.object({
  currency: z.string().optional(),
  costShareOwed: z.number().optional(),
  settlementStatus: z.string().optional(),
  /** Dropped under `V(summary)` — the Medical Director sees the totals, not the claim ledger. */
  claims: z.array(zFinancialClaimRow).optional(),
});
export type ProfileFinancial = z.infer<typeof zProfileFinancial>;

// ---------------------------------------------------------------- 13. case management

export const zCaseRow = z.object({
  caseId: zId,
  caseNo: z.string(),
  status: z.string(),
  category: z.string().optional(),
  openedAt: zInstant,
});
export type CaseRow = z.infer<typeof zCaseRow>;

export const zCoordinationTaskRow = z.object({
  taskId: zId,
  title: z.string(),
  status: z.string(),
  dueOn: zDate.optional(),
});
export type CoordinationTaskRow = z.infer<typeof zCoordinationTaskRow>;

export const zEscalationRow = z.object({
  escalationId: zId,
  reason: z.string(),
  status: z.string(),
  raisedAt: zInstant,
});
export type EscalationRow = z.infer<typeof zEscalationRow>;

/**
 * Three sibling arrays and **no scalar field at all** — worth noting because a generic key/value renderer sees
 * nothing to print here and concludes the section is empty, whatever it actually holds.
 */
export const zProfileCaseManagement = z.object({
  cases: z.array(zCaseRow).optional(),
  tasks: z.array(zCoordinationTaskRow).optional(),
  escalations: z.array(zEscalationRow).optional(),
});
export type ProfileCaseManagement = z.infer<typeof zProfileCaseManagement>;

// ---------------------------------------------------------------- 14. timeline

export const zTimelineRow = z.object({
  at: zInstant,
  eventType: z.string(),
  visibilityClass: z.string().optional(),
  actorDisplay: z.string().optional(),
  summary: z.string().optional(),
  sourceService: z.string().optional(),
});
export type TimelineRow = z.infer<typeof zTimelineRow>;

export const zProfileTimeline = z.object({ items: z.array(zTimelineRow).optional() });
export type ProfileTimeline = z.infer<typeof zProfileTimeline>;

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

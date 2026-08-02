import { z } from "zod";
import {
  zApprovalItem,
  zApprovalReview,
  zConsumeResult,
  zDecisionResult,
  zDispenseResult,
  zEligibilityHit,
  zEligibilityResult,
  zEncounter,
  zExecutiveDashboard,
  zLabOrder,
  zPatientListItem,
  zPlaceOrderResult,
  zPrescribeResult,
  zPrescription,
  zBeneficiary360,
  zPatientProfile,
  zCopySummariesResult,
  zProfileExportSummary,
  type ProfileSectionKey,
  type ProfileSection,
  zCaseListItem,
  zCoordinationTask,
  zEscalation,
  zNotification,
  zMarkAllReadResult,
  zMarkReadResult,
  zRoleBinding,
  zTenantSummary,
  zSodConflict,
  zAccessReviewCampaign,
  zAppointmentRow,
  zBookableClinic,
  zTimelineStep,
  zBookableSlot,
  zBookingResult,
  zBreakGlassGrant,
  zMasterDataVersion,
  zSystemConfigEntry,
  zBeneficiaryDocument,
  zRegistrationThreadEntry,
  zRegistrationWorklistPage,
  zRegistrationDecisionResult,
  zMembershipRow,
  zMembershipDetail,
  zEffectiveAccess,
  zBranchScopeGrant,
  zAccessSession,
  zProgramEnablement,
  zProviderSummary,
  zProviderLocation,
  zProviderContract,
  type CreateProviderInput,
  zSpecialty,
  zBranchSummary,
  zPractitioner,
  zPractitionerCreated,
  zDoctorAvailability,
  zAppointmentDay,
  zAppointmentCounts,
  type CreatePractitionerInput,
  zBeneficiaryRow,
  zRegisterResult,
  zStatusChangeResult,
  type RegisterBeneficiaryInput,
  zCheckInResult,
  zOrderRow,
  zRxRow,
  zResultDetail,
  zReportAccessRequestResult,
  type ReportAccessInput,
  zClaimRow,
  zReconciliationRow,
  zClaimsKpis,
  zVitalsResult,
  zResultTask,
  zResultUpload,
  zDrugRef,
  zTatSummary,
  zManualAuthResult,
  zEmergencyResult,
  type ManualAuthInput,
  zReportView,
  type VitalInput,
  zExportResult,
  zFinancialSummary,
  zSettlement,
  zUtilizationView,
  type ConsumeRequest,
  type DecisionRequest,
  type DispenseRequest,
  type ExportRequest,
  type Localized,
  type PlaceOrderRequest,
  type PrescribeRequest,
  type IdentityUser,
  type RoleScopeGrant,
  type ReportAccessRequestRow,
} from "@mersal/contracts";
import type { BeneficiaryEdit, BookingRequest, BulkDecisionOutcome } from "@mersal/contracts";
import type { ApiClient, ApiScenario } from "./client";
import { ApiError } from "./http";

const loc = (en: string, ar: string): Localized => ({ en, ar });

/** The in-app inbox fixture. Module-level so the read-state overlay and the unread count read one source. */
const NOTIFICATION_FIXTURE = [
  {
    id: "NTF-1",
    subject: "Authorization awaiting review",
    body: "A new authorization is on your worklist and awaiting a decision.",
    status: { kind: "warn" as const, label: loc("Action needed", "إجراء مطلوب") },
    entityRef: "AUTH-2026-0001",
    sourceEventType: "AuthorizationSubmitted",
    actionable: true,
    read: false,
    createdAt: "2026-07-22T07:30:00Z",
  },
  {
    id: "NTF-2",
    subject: "Authorization approved",
    body: "An authorization you requested has been approved.",
    status: { kind: "ok" as const, label: loc("Approved", "معتمد") },
    entityRef: "AUTH-2026-0002",
    sourceEventType: "AuthorizationDecided",
    actionable: false,
    read: true,
    createdAt: "2026-07-21T12:00:00Z",
  },
];
const NOW = "2026-07-22T08:30:00Z";

/**
 * The dev beneficiary whose profile answers with the three withheld states (Restricted / Unavailable /
 * NotApplicable) instead of populated clinical sections. Every other id gets all fifteen sections.
 *
 * Open `/patients/BEN-3` to review how the three states look side by side; open any other beneficiary to
 * review the twelve designed section views. See `patientProfile` below.
 */
export const WITHHELD_STATE_DEMO_ID = "BEN-3";

/** Validate every fixture through its schema on the way out — a fixture that drifts from the contract fails loudly. */
/** Shared status chips for the mutable practitioner fixture, so a status change swaps one reference. */
const DEV_ACTIVE = { kind: "ok" as const, label: loc("Active", "نشط") };
const DEV_SUSPENDED = { kind: "warn" as const, label: loc("Suspended", "موقوف") };
const DEV_INACTIVE = { kind: "neu" as const, label: loc("Inactive", "غير نشط") };
/** Widened deliberately: the row literals below only mention two of the three, so without this TS narrows
 *  the field to those two and a status change to Inactive stops compiling. */
type DevStatusChip = typeof DEV_ACTIVE | typeof DEV_SUSPENDED | typeof DEV_INACTIVE;

// ---- Registration approval queue (US-003) ------------------------------------------------------------------
//
// Twelve rows, deliberately. The worklist grew search, a status filter, sortable columns and a pager, and every
// one of those is invisible on a fixture set of three: the pager never appears, the filter never removes
// anything, and "sort by oldest" reorders rows that were already in order. A fixture that cannot exercise a
// control is a fixture in which that control's bugs ship.
//
// Spread across four filing officers and six weeks so date and officer sorting both do something, and mixed
// across the three application states plus one legacy row with no application at all.
const REG_PENDING = { kind: "info" as const, label: loc("Pending", "قيد الانتظار") };

const REGISTRATION_QUEUE = [
  {
    beneficiary: {
      id: "BEN-1", cardNumber: "MF-04821", givenName: "Omar", familyName: "Khaled",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "NationalID", value: "•••2931", isPrimary: true }],
      birthDate: "1989-03-14", sex: "Male", nationalityCode: "SY", caseNo: "CASE-2211",
      contacts: [{ type: "Phone", value: "+20 100 ••• 4412", isPrimary: true }],
    },
    registration: {
      id: "REG-1", status: "Pending" as const, documentsVerified: true, coverageBound: true, notes: null,
      createdAt: "2026-06-18T08:05:00Z", createdBy: "u-layla", createdByName: "Layla Hassan",
      updatedAt: "2026-07-02T10:20:00Z", threadCount: 0,
      enrolment: { planId: "PLAN-MERSAL", networkTierId: "TIER-COMP", contributionPercent: 10, defaultBranchId: "BR-MAADI" },
      standingNotes: [
        { slot: 1, labelEn: "Known diagnosis", labelAr: "التشخيص المعروف", visibility: "Clinical" as const, value: null, withheld: true },
        { slot: 2, labelEn: "Forecasted case cost", labelAr: "التكلفة المتوقعة للحالة", visibility: "Administrative" as const, value: "EGP 4,000", withheld: false },
      ],
    },
  },
  {
    beneficiary: {
      id: "BEN-6", cardNumber: "MF-04833", givenName: "Rania", familyName: "Mostafa",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "RefugeeID", value: "R•••501", isPrimary: true }],
      birthDate: "1994-11-02", sex: "Female", nationalityCode: "SD", individualNo: "IND-7781",
      contacts: [{ type: "Phone", value: "+20 111 ••• 9087", isPrimary: true }],
    },
    registration: {
      id: "REG-2", status: "InfoRequested" as const, documentsVerified: false, coverageBound: false,
      notes: "UNHCR letter is expired — request a current one",
      createdAt: "2026-06-21T11:40:00Z", createdBy: "u-layla", createdByName: "Layla Hassan",
      updatedAt: "2026-07-14T09:00:00Z", threadCount: 2,
      enrolment: null, standingNotes: [],
    },
  },
  {
    beneficiary: {
      id: "BEN-7", cardNumber: "MF-04902", givenName: "Karim", familyName: "Fawzy",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "UNHCRNo", value: "803-•••12", isPrimary: true }],
      birthDate: "2001-07-30", sex: "Male", nationalityCode: "SY",
    },
    // The legacy row: a Pending person whose application predates auto-creation. The queue must still show
    // them — a person the queue cannot show is a person nobody reviews.
    registration: null,
  },
  {
    beneficiary: {
      id: "BEN-8", cardNumber: "MF-04915", givenName: "Nour", familyName: "Abdelrahman",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "NationalID", value: "•••7744", isPrimary: true }],
      birthDate: "1978-01-19", sex: "Female", nationalityCode: "EG", caseNo: "CASE-2240",
    },
    registration: {
      id: "REG-4", status: "Pending" as const, documentsVerified: true, coverageBound: false, notes: null,
      createdAt: "2026-06-29T07:15:00Z", createdBy: "u-tarek", createdByName: "Tarek Sabry",
      updatedAt: "2026-06-29T07:15:00Z", threadCount: 0,
      enrolment: { planId: "PLAN-UNCR-DB", networkTierId: "TIER-MERSAL", contributionPercent: 0 },
      standingNotes: [],
    },
  },
  {
    beneficiary: {
      id: "BEN-9", cardNumber: "MF-05001", givenName: "Hala", familyName: "Zaki",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "Passport", value: "P•••338", isPrimary: true }],
      birthDate: "1996-05-05", sex: "Female", nationalityCode: "SD",
    },
    registration: {
      id: "REG-5", status: "Pending" as const, documentsVerified: false, coverageBound: true, notes: null,
      createdAt: "2026-07-01T13:25:00Z", createdBy: "u-tarek", createdByName: "Tarek Sabry",
      updatedAt: "2026-07-01T13:25:00Z", threadCount: 0,
      enrolment: { planId: "PLAN-MERSAL", networkTierId: "TIER-RESTRICTED", contributionPercent: 20 },
      standingNotes: [],
    },
  },
  {
    beneficiary: {
      id: "BEN-10", cardNumber: "MF-05018", givenName: "Bassel", familyName: "Naim",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "RefugeeID", value: "R•••622", isPrimary: true }],
      birthDate: "1985-09-12", sex: "Male", nationalityCode: "SY",
    },
    registration: {
      id: "REG-6", status: "InfoRequested" as const, documentsVerified: true, coverageBound: false,
      notes: "Card copy is unreadable — rescan both sides",
      createdAt: "2026-07-03T09:50:00Z", createdBy: "u-mona", createdByName: "Mona Adel",
      updatedAt: "2026-07-19T15:30:00Z", threadCount: 3,
      enrolment: null, standingNotes: [],
    },
  },
  {
    beneficiary: {
      id: "BEN-11", cardNumber: "MF-05033", givenName: "Yara", familyName: "Selim",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "NationalID", value: "•••1180", isPrimary: true }],
      birthDate: "2010-02-28", sex: "Female", nationalityCode: "EG", caseNo: "CASE-2240",
    },
    registration: {
      id: "REG-7", status: "Pending" as const, documentsVerified: true, coverageBound: true, notes: null,
      createdAt: "2026-07-06T08:00:00Z", createdBy: "u-mona", createdByName: "Mona Adel",
      updatedAt: "2026-07-06T08:00:00Z", threadCount: 0,
      enrolment: { planId: "PLAN-MERSAL", networkTierId: "TIER-COMP", contributionPercent: 10, defaultBranchId: "BR-MAADI" },
      standingNotes: [],
    },
  },
  {
    beneficiary: {
      id: "BEN-12", cardNumber: "MF-05047", givenName: "Ismail", familyName: "Darwish",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "UNHCRNo", value: "803-•••55", isPrimary: true }],
      birthDate: "1962-12-01", sex: "Male", nationalityCode: "SY",
    },
    registration: {
      id: "REG-8", status: "Pending" as const, documentsVerified: false, coverageBound: false, notes: null,
      createdAt: "2026-07-09T14:10:00Z", createdBy: "u-fady", createdByName: "Fady Boutros",
      updatedAt: "2026-07-09T14:10:00Z", threadCount: 0,
      enrolment: null, standingNotes: [],
    },
  },
  {
    beneficiary: {
      id: "BEN-13", cardNumber: "MF-05060", givenName: "Sara", familyName: "Gamal",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "NationalID", value: "•••4407", isPrimary: true }],
      birthDate: "1999-08-21", sex: "Female", nationalityCode: "EG",
    },
    registration: {
      id: "REG-9", status: "Pending" as const, documentsVerified: true, coverageBound: true, notes: null,
      createdAt: "2026-07-13T10:35:00Z", createdBy: "u-fady", createdByName: "Fady Boutros",
      updatedAt: "2026-07-13T10:35:00Z", threadCount: 0,
      enrolment: { planId: "PLAN-UNCR-CR", networkTierId: "TIER-MERSAL", contributionPercent: 15 },
      standingNotes: [],
    },
  },
  {
    beneficiary: {
      id: "BEN-14", cardNumber: "MF-05072", givenName: "Ahmed", familyName: "Sherif",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "Passport", value: "P•••901", isPrimary: true }],
      birthDate: "1991-04-04", sex: "Male", nationalityCode: "SD",
    },
    registration: {
      id: "REG-10", status: "Rejected" as const, documentsVerified: false, coverageBound: false,
      notes: "Not eligible — resident outside the programme's governorates",
      createdAt: "2026-07-15T12:00:00Z", createdBy: "u-layla", createdByName: "Layla Hassan",
      updatedAt: "2026-07-20T08:45:00Z", threadCount: 1,
      enrolment: null, standingNotes: [],
    },
  },
  {
    beneficiary: {
      id: "BEN-15", cardNumber: "MF-05088", givenName: "Malak", familyName: "Riad",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "RefugeeID", value: "R•••733", isPrimary: true }],
      birthDate: "2015-06-17", sex: "Female", nationalityCode: "SY", caseNo: "CASE-2311",
    },
    registration: {
      id: "REG-11", status: "Pending" as const, documentsVerified: true, coverageBound: true, notes: null,
      createdAt: "2026-07-20T09:05:00Z", createdBy: "u-tarek", createdByName: "Tarek Sabry",
      updatedAt: "2026-07-20T09:05:00Z", threadCount: 0,
      enrolment: { planId: "PLAN-MERSAL", networkTierId: "TIER-COMP", contributionPercent: 0, defaultBranchId: "BR-SHOUBRA" },
      standingNotes: [],
    },
  },
  {
    beneficiary: {
      id: "BEN-16", cardNumber: "MF-05094", givenName: "Ziad", familyName: "Kamel",
      status: REG_PENDING, statusRaw: "Pending",
      identifiers: [{ type: "NationalID", value: "•••6620", isPrimary: true }],
      birthDate: "1973-10-09", sex: "Male", nationalityCode: "EG",
    },
    registration: {
      id: "REG-12", status: "Pending" as const, documentsVerified: false, coverageBound: true, notes: null,
      createdAt: "2026-07-27T07:45:00Z", createdBy: "u-mona", createdByName: "Mona Adel",
      updatedAt: "2026-07-27T07:45:00Z", threadCount: 0,
      enrolment: { planId: "PLAN-MERSAL", networkTierId: "TIER-RESTRICTED", contributionPercent: 25 },
      standingNotes: [],
    },
  },
];

/** Threads keyed by registration id. Mutable, so a reply posted in the dev app persists for the session. */
const REGISTRATION_THREADS: Record<string, Array<{
  id: string; kind: "Decision" | "Reply"; decision: "Approve" | "RequestInfo" | "Reject" | null;
  body: string; authorName: string | null; authorRole: string | null; createdAt: string;
}>> = {
  "REG-2": [
    {
      id: "THR-2-1", kind: "Decision", decision: "RequestInfo",
      body: "UNHCR letter is expired — request a current one",
      authorName: "Dina Farouk", authorRole: "beneficiary_mgmt_supervisor", createdAt: "2026-07-14T09:00:00Z",
    },
    {
      id: "THR-2-2", kind: "Reply", decision: null,
      body: "Beneficiary has an appointment at UNHCR on 5 August. I will upload the new letter the same day.",
      authorName: "Layla Hassan", authorRole: "beneficiary_mgmt", createdAt: "2026-07-15T11:20:00Z",
    },
  ],
  "REG-6": [
    {
      id: "THR-6-1", kind: "Decision", decision: "RequestInfo",
      body: "Card copy is unreadable — rescan both sides",
      authorName: "Dina Farouk", authorRole: "beneficiary_mgmt_supervisor", createdAt: "2026-07-16T13:05:00Z",
    },
    {
      id: "THR-6-2", kind: "Reply", decision: null,
      body: "Rescanned the front. The back is faded on the physical card; the member is applying for a reprint.",
      authorName: "Mona Adel", authorRole: "beneficiary_mgmt", createdAt: "2026-07-18T08:40:00Z",
    },
    {
      id: "THR-6-3", kind: "Reply", decision: null,
      body: "Reprint issued today, card number unchanged. New scan uploaded.",
      authorName: "Mona Adel", authorRole: "beneficiary_mgmt", createdAt: "2026-07-19T15:30:00Z",
    },
  ],
  "REG-10": [
    {
      id: "THR-10-1", kind: "Decision", decision: "Reject",
      body: "Not eligible — resident outside the programme's governorates",
      authorName: "Dina Farouk", authorRole: "beneficiary_mgmt_supervisor", createdAt: "2026-07-20T08:45:00Z",
    },
  ],
};

/** Documents filed against a beneficiary, keyed by beneficiary id. Metadata only — no bytes. */
const REGISTRATION_DOCUMENTS: Record<string, Array<{
  id: string; docType: string; classification: string; uploadedAt: string | null; uploadedBy: string | null;
}>> = {
  "BEN-1": [
    { id: "DOC-1", docType: "CardCopy", classification: "Administrative", uploadedAt: "2026-06-18T08:12:00Z", uploadedBy: "Layla Hassan" },
    { id: "DOC-2", docType: "IdentityDocument", classification: "Administrative", uploadedAt: "2026-06-18T08:14:00Z", uploadedBy: "Layla Hassan" },
  ],
  "BEN-6": [
    { id: "DOC-3", docType: "IdentityDocument", classification: "Administrative", uploadedAt: "2026-06-21T11:52:00Z", uploadedBy: "Layla Hassan" },
  ],
  "BEN-10": [
    { id: "DOC-4", docType: "CardCopy", classification: "Administrative", uploadedAt: "2026-07-19T15:28:00Z", uploadedBy: "Mona Adel" },
    // A clinical class an administrative role may see EXISTS but not open — the same locked state the
    // documents screen renders. It is here so the modal's withheld path is exercised, not theoretical.
    { id: "DOC-5", docType: "MedicalReport", classification: "Clinical", uploadedAt: "2026-07-03T10:00:00Z", uploadedBy: "Mona Adel" },
  ],
};

function ok<T>(schema: z.ZodType<T>, data: unknown): T {
  const r = schema.safeParse(data);
  if (!r.success) throw new ApiError("schema", `Dev fixture violates contract: ${r.error.issues[0]?.message}`);
  return r.data;
}

/**
 * In-memory fixture client — the same shape a real OIDC-gated HTTP client will have, backed by synthetic,
 * bilingual, contract-valid data (never real PHI). It drives the dev app and the tests: `latencyMs` exercises
 * the loading state, `fault` renders a screen straight into empty/error, and a repeated Idempotency-Key
 * returns `replayed: true` instead of double-applying (proving the consume/dispense/decide contract).
 */
export class DevApiClient implements ApiClient {
  private seenKeys = new Set<string>();
  private labProgress = new Map<string, number>(); // orderId → panelsDone
  private rxProgress = new Map<string, Map<string, number>>(); // rxId → lineId → dispensed
  private notificationsRead = new Set<string>(); // notification ids marked read in this session

  constructor(private scenario: ApiScenario = { latencyMs: 0, fault: "none" }) {}

  private async gate<T>(build: () => T, emptyValue?: T): Promise<T> {
    const { latencyMs = 0, fault = "none" } = this.scenario;
    if (latencyMs > 0) await new Promise((r) => setTimeout(r, latencyMs));
    if (fault === "error") throw new ApiError("http", "Simulated upstream failure", 503);
    if (fault === "empty" && emptyValue !== undefined) return emptyValue;
    return build();
  }

  // ---- Eligibility -------------------------------------------------------
  searchEligibility(query: string) {
    return this.gate<ReturnType<typeof this.buildHits>>(() => this.buildHits(query), []);
  }
  private buildHits(query: string) {
    // One ACTIVE and one SUSPENDED member, on purpose. The eligibility gate's whole job is to stop the second
    // one from being booked, and a fixture of nothing but active members is one where that path is never
    // exercised — which is how it shipped unenforced in the first place.
    const all = [
      {
        id: "MRS-M-10231", name: loc("Amal Hassan", "أمل حسن"), cardNumber: "•••• 4821",
        status: { kind: "ok" as const, label: loc("Active", "نشط") }, bookable: true,
      },
      {
        id: "MRS-M-10555", name: loc("Yusuf Haddad", "يوسف حداد"), cardNumber: "•••• 7702",
        status: { kind: "warn" as const, label: loc("Suspended", "موقوف") }, bookable: false,
      },
    ];
    const q = query.toLowerCase();
    return ok(
      z.array(zEligibilityHit),
      all.filter((h) => h.name.en.toLowerCase().includes(q) || h.id.toLowerCase().includes(q) || q.length >= 2),
    );
  }
  checkEligibility(beneficiaryId: string) {
    return this.gate(() =>
      ok(zEligibilityResult, {
        verdict: "eligible",
        status: { kind: "ok", label: loc("Eligible", "مؤهل") },
        beneficiary: {
          id: beneficiaryId,
          name: loc("Amal Hassan", "أمل حسن"),
          cardNumber: "MRS-CARD-4821",
          dateOfBirth: "1990-04-12",
          gender: "female",
        },
        coverage: {
          planName: loc("Mersal Essential", "مرسال الأساسية"),
          band: loc("Band B — Outpatient + Pharmacy", "الفئة ب — عيادات + صيدلية"),
          validUntil: "2026-12-31",
          copayPercent: 10,
          annualCapRemaining: 8400,
        },
        visitGate: { allowed: true },
      }),
    );
  }

  // ---- Reception day board -----------------------------------------------
  appointments(filter: "all" | "booked" | "checked-in" = "all", _mine = false, _range?: { from: string; to: string }, _branchId?: string) {
    void _mine; void _range; void _branchId;
    const rows = [
      // A note on the FIRST row only: the board must show the note affordance on rows that have one and
      // nothing at all on rows that do not, and a fixture where every row has a note never proves the second half.
      { id: "appt-1", token: "•••4821", type: "Consultation", ar: "كشف", st: "Booked", chip: { kind: "info" as const, label: loc("Booked", "محجوز") }, at: "2026-07-22T09:00:00Z", eligible: true, note: "Wheelchair access — ground-floor room. Sister attending as interpreter." },
      { id: "appt-2", token: "•••7710", type: "FollowUp", ar: "متابعة", st: "CheckedIn", chip: { kind: "ok" as const, label: loc("Checked in", "تم الوصول") }, at: "2026-07-22T09:30:00Z", eligible: false, name: "Amal Hassan", doctorId: "PRC-1" },
            // Its window has passed by more than the grace period, so the SERVER would allow a no-show here.
      { id: "appt-3", token: "•••2093", type: "Consultation", ar: "كشف", st: "Booked", chip: { kind: "info" as const, label: loc("Booked", "محجوز") }, at: "2026-07-22T10:00:00Z", eligible: true, noShowEligible: true, needsReassignment: true },
      { id: "appt-4", token: "•••5540", type: "Procedure", ar: "إجراء", st: "NoShow", chip: { kind: "warn" as const, label: loc("No-show", "لم يحضر") }, at: "2026-07-22T08:30:00Z", eligible: false },
    ].filter((r) => (filter === "booked" ? r.st === "Booked" : filter === "checked-in" ? r.st === "CheckedIn" : true));
    return this.gate(
      () =>
        ok(z.array(zAppointmentRow), rows.map((r) => ({
          id: r.id,
          beneficiary: { id: r.id, token: r.token },
          appointmentType: r.type,
          status: r.chip,
          scheduledStart: r.at,
          checkInEligible: r.eligible,
          checkedIn: r.st === "CheckedIn",
          noShowEligible: r.noShowEligible ?? false,
          startVisitEligible: r.st === "CheckedIn",
          branchId: "br-dokki",
          branchName: "Dokki",
          rowVersion: 1,
          note: r.note ?? null,
          noteBy: r.note ? "Nada Reception" : null,
          noteAt: r.note ? "2026-07-22T08:05:00Z" : null,
          // Only the CHECKED-IN row carries a name, exactly as the server behaves: the name is captured at
          // check-in, so a booked-but-not-arrived appointment genuinely has none.
          beneficiaryName: r.st === "CheckedIn" ? r.name ?? null : null,
          doctorId: r.doctorId ?? null,
          needsReassignment: r.needsReassignment ?? false,
          providerId: "prov-1",
          locationId: "loc-1",
        }))),
      [],
    );
  }
  appointmentCounts(_date?: string) {
    void _date;
    return this.gate(() => ok(zAppointmentCounts, { total: 4, checkedIn: 1, noShow: 1 }));
  }
  cancelAppointment(appointmentId: string, _reason: string) {
    void _reason;
    return this.gate(() => ok(zCheckInResult, {
      id: appointmentId,
      status: { kind: "neu", label: loc("Cancelled", "ملغى") },
    }));
  }
  async updateAppointmentNote(_appointmentId: string, _note: string) { void _appointmentId; void _note; }
  async rescheduleAppointment(_appointmentId: string, _slotId: string) { void _appointmentId; void _slotId; }
  appointmentTimeline(_appointmentId: string) {
    void _appointmentId;
    return this.gate(
      () =>
        ok(z.array(zTimelineStep), [
          { status: "Booked", at: "2026-07-22T08:00:00Z", by: "0cccc773-ce39-495c-bcac-0e67d746b7e9", byName: "Nada Reception" },
          // Deliberately unattributed: a step recorded before actor attribution existed.
          { status: "CheckedIn", at: "2026-07-22T08:55:00Z", by: null, byName: null },
          // An actor whose name could not be resolved (deactivated, or another tenant) — the id still shows.
          { status: "Completed", at: "2026-07-22T09:40:00Z", by: "129d2a05-8c27-43c7-aae2-f2cc4c7fda30", byName: null },
        ]),
      [],
    );
  }
  startVisit(_appointmentId: string, _beneficiaryId: string) {
    void _appointmentId; void _beneficiaryId;
    return this.gate(() => ({ encounterId: "enc-1" }));
  }
  noShow(appointmentId: string, _rowVersion?: number) {
    void _rowVersion;
    return this.gate(() => ok(zCheckInResult, { id: appointmentId, status: { kind: "warn", label: loc("No-show", "لم يحضر") } }));
  }
  checkIn(appointmentId: string, _rowVersion?: number) {
    void _rowVersion; // fixture path applies no concurrency guard; the live client echoes it as If-Match.
    return this.gate(() => ok(zCheckInResult, { id: appointmentId, status: { kind: "ok", label: loc("Checked in", "تم الوصول") } }));
  }

  // ---- Booking -----------------------------------------------------------
  bookableClinics(_branchId?: string) {
    void _branchId;
    return this.gate(
      () =>
        ok(z.array(zBookableClinic), [
          { providerId: "prov-1", locationId: "loc-1", branchId: "br-dokki", label: "Mersal Dokki · Dokki Clinic", openSlots: 2 },
        ]),
      [],
    );
  }
  // One slot is deliberately CLOSED: the desk must render availability from the server's answer, and a
  // fixture where everything is bookable would never exercise that.
  openSlots(_providerId: string, _locationId: string, _from?: string, _to?: string, _doctorId?: string) {
    void _providerId; void _locationId; void _from; void _to; void _doctorId;
    return this.gate(
      () =>
        // Spanning three DAYS, not one hour: the time picker groups by day and paginates, and a fixture that
        // fits in a single row never exercises either.
        ok(z.array(zBookableSlot), [
          { id: "slot-1", start: "2026-07-22T11:00:00Z", end: "2026-07-22T11:15:00Z", open: true },
          { id: "slot-2", start: "2026-07-22T11:15:00Z", end: "2026-07-22T11:30:00Z", open: false },
          { id: "slot-3", start: "2026-07-22T11:30:00Z", end: "2026-07-22T11:45:00Z", open: true },
          { id: "slot-4", start: "2026-07-23T08:00:00Z", end: "2026-07-23T08:15:00Z", open: true },
          { id: "slot-5", start: "2026-07-23T08:15:00Z", end: "2026-07-23T08:30:00Z", open: true },
          { id: "slot-6", start: "2026-07-26T09:00:00Z", end: "2026-07-26T09:15:00Z", open: true },
        ]),
      [],
    );
  }

  appointmentDays(_providerId: string, _locationId: string, _from: string, _to: string, _doctorId?: string) {
    void _providerId; void _locationId; void _from; void _to; void _doctorId;
    // Matches the slot fixture above — a calendar whose counts disagreed with the times beside it would look
    // broken in exactly the way this feature exists to avoid.
    return this.gate(
      () => ok(z.array(zAppointmentDay), [
        { day: "2026-07-22", openSlots: 2 },
        { day: "2026-07-23", openSlots: 2 },
        { day: "2026-07-26", openSlots: 1 },
      ]),
      [],
    );
  }
  bookAppointment(input: BookingRequest) {
    return this.gate(() =>
      ok(zBookingResult, {
        id: `appt-${input.slotId}`,
        status: { kind: "info", label: loc("Booked", "محجوز") },
        scheduledStart: "2026-07-22T11:00:00Z",
      }),
    );
  }

  // ---- EMR ---------------------------------------------------------------
  listPatients() {
    return this.gate(
      () =>
        ok(z.array(zPatientListItem), [
          {
            id: "MRS-M-10231",
            beneficiaryId: "aaaaaaaa-0000-0000-0000-000000000231",
            name: loc("Amal Hassan", "أمل حسن"),
            mrn: "MRN-10231",
            treating: true,
            lastVisit: "2026-07-01",
            status: { kind: "ok", label: loc("In consultation", "في الكشف") },
          },
          {
            id: "MRS-M-10555",
            beneficiaryId: "aaaaaaaa-0000-0000-0000-000000000555",
            name: loc("Yusuf Haddad", "يوسف حداد"),
            mrn: "MRN-10555",
            treating: true,
            lastVisit: "2026-06-20",
            status: { kind: "info", label: loc("Waiting", "بالانتظار") },
          },
        ]),
      [],
    );
  }
  getEncounter(patientId: string) {
    return this.gate(() =>
      ok(zEncounter, {
        id: "ENC-88120",
        patientId,
        patientName: loc("Amal Hassan", "أمل حسن"),
        openedAt: NOW,
        signed: false,
        soap: {
          subjective: "Persistent cough for 5 days, low-grade fever.",
          objective: "Temp 37.8°C, chest clear, no distress.",
          assessment: "Suspected upper respiratory infection.",
          plan: "Supportive care; CBC to rule out bacterial cause.",
        },
        vitals: { heightCm: 164, weightKg: 61, systolic: 118, diastolic: 76, heartRate: 82, tempC: 37.8 },
        allergies: [{ id: "AL-1", substance: loc("Penicillin", "بنسلين"), severity: "moderate" }],
        diagnoses: [{ system: "ICD-10", code: "J06.9", label: loc("Acute upper respiratory infection", "التهاب تنفسي علوي حاد") }],
      }),
    );
  }
  placeOrder(req: PlaceOrderRequest) {
    return this.gate(() =>
      ok(zPlaceOrderResult, {
        orderId: "ORD-55012",
        status: { kind: "info", label: loc("Order placed", "تم الطلب") },
        requiresApproval: req.priority === "routine" ? false : true,
      }),
    );
  }
  prescribe(req: PrescribeRequest) {
    return this.gate(() =>
      ok(zPrescribeResult, {
        prescriptionId: "RX-33110",
        status: { kind: "ok", label: loc("Prescription submitted", "تم إرسال الوصفة") },
        advisories:
          req.drug.code === "J01CA04"
            ? [loc("Patient has a moderate penicillin allergy — review.", "لدى المريض حساسية متوسطة من البنسلين — راجع.")]
            : [],
      }),
    );
  }

  ordersMine(status?: string) {
    const rows = [
      { id: "ord-1", line: "ln-1", no: "ORD-2026-000118", tok: "•••4821", type: "Lab", code: "80053", n: 1, st: { kind: "info" as const, label: loc("Active", "نشط") }, key: "Active", at: "2026-07-22T08:10:00Z" },
      // ord-2 is a psychiatry-panel result → sensitivity-restricted (14.7); resultDetail returns existence-only.
      { id: "ord-2", line: "ln-2", no: "ORD-2026-000119", tok: "•••7710", type: "Imaging", code: "71046", n: 1, st: { kind: "ok" as const, label: loc("Completed", "مكتمل") }, key: "Completed", at: "2026-07-21T14:00:00Z" },
      { id: "ord-3", line: "ln-3", no: "ORD-2026-000120", tok: "•••2093", type: "Lab", code: "85025", n: 2, st: { kind: "ok" as const, label: loc("Completed", "مكتمل") }, key: "Completed", at: "2026-07-20T09:30:00Z" },
    ].filter((r) => !status || r.key === status);
    return this.gate(
      () =>
        ok(z.array(zOrderRow), rows.map((r) => ({
          id: r.id, orderNo: r.no, beneficiary: { id: r.id, token: r.tok },
          orderType: r.type, primaryCode: r.code, lineCount: r.n, status: r.st, requestedAt: r.at,
          firstLineId: r.line,
        }))),
      [],
    );
  }

  /** 14.6/14.7 — a completed result read. Line `ln-2` is sensitivity-restricted → existence-only metadata (no values). */
  resultDetail(orderId: string, lineId: string) {
    return this.gate(() => {
      if (lineId === "ln-2")
        return ok(zResultDetail, {
          restricted: true as const, orderId, lineId, category: "Radiology — Psychiatry protocol",
          status: "Completed", sensitivityLevel: "Sensitive", orderingBranch: "Maadi", date: "2026-07-21",
        });
      return ok(zResultDetail, {
        restricted: false as const, orderId, lineId, category: "Laboratory — Chemistry panel",
        code: "80053", value: "Within reference range", status: "Completed", resultedAt: "2026-07-20T11:00:00Z",
      });
    });
  }

  /**
   * 18.C2 (W4) — fixture approver inbox. Two rows, deliberately: one Requested and one UnderReview, so the
   * screen's status handling and the "already picked up by someone" case are both exercised without a backend.
   */
  async reportAccessInbox(): Promise<ReportAccessRequestRow[]> {
    return [
      {
        requestId: "rar-1", orderId: "ord-77", orderLineId: "ol-77a", beneficiaryToken: "•••4821",
        requestedBy: "dr.hala", requestedForRole: "doctor", purposeCode: "TRT",
        justification: "Patient referred to me for follow-up; need the histology to plan treatment.",
        requestedTtlHours: 24,
        status: { kind: "warn", label: { en: "Awaiting decision", ar: "بانتظار القرار" } },
        createdAt: new Date(Date.now() - 3_600_000).toISOString(),
      },
      {
        requestId: "rar-2", orderId: "ord-91", orderLineId: "ol-91c", beneficiaryToken: "•••1903",
        requestedBy: "dr.omar", requestedForRole: "medical_approval", purposeCode: "PUR",
        justification: "Authorization review — medical necessity for the requested procedure.",
        requestedTtlHours: 8,
        status: { kind: "info", label: { en: "Under review", ar: "قيد المراجعة" } },
        createdAt: new Date(Date.now() - 7_200_000).toISOString(),
      },
    ];
  }

  async decideReportAccess(): Promise<void> {}
  async revokeReportAccessGrant(): Promise<void> {}

  /** 18.C2 (W5) — fixture identity users. One account WITHOUT a second factor, because that is the row the
   * screen exists to make visible. */
  async identityUsers(): Promise<IdentityUser[]> {
    return [
      { id: "u-1", username: "org.admin", displayName: "Org Admin", tenantId: "•••1111", isActive: true, twoFactorEnabled: true, roles: ["org_admin"] },
      { id: "u-2", username: "dr.hala", displayName: "Dr. Hala", tenantId: "•••1111", isActive: true, twoFactorEnabled: false, roles: ["doctor"] },
      { id: "u-3", username: "left.staff", displayName: "Former Staff", tenantId: "•••1111", isActive: false, twoFactorEnabled: true, roles: ["reception"] },
    ];
  }

  async identityRoleScopes(): Promise<RoleScopeGrant[]> {
    return [
      { role: "reception", scopes: ["reception:search", "reception:read", "patient:read", "eligibility:check"] },
      { role: "doctor", scopes: ["emr:read", "emr:write", "orders:write", "rx:write", "rx:read", "patient:read"] },
      { role: "claims_officer", scopes: ["claims:read", "claims:review", "claims:decide", "claims:batch"] },
    ];
  }

  /** 14.8 — a report-access request; the server would enqueue it for the author/MD to grant. */
  requestReportAccess(input: ReportAccessInput) {
    void input;
    return this.gate(() => ok(zReportAccessRequestResult, { requestId: "rar-dev-1", status: "Pending" }));
  }
  prescriptionsMine(status?: string) {
    void status;
    return this.gate(
      () =>
        ok(z.array(zRxRow), [
          { id: "rx-1", beneficiary: { id: "rx-1", token: "•••4821" }, lineCount: 2, status: { kind: "ok", label: loc("Approved", "معتمدة") }, submittedAt: "2026-07-22T08:15:00Z" },
          { id: "rx-2", beneficiary: { id: "rx-2", token: "•••2093" }, lineCount: 1, status: { kind: "part", label: loc("Partially dispensed", "صُرفت جزئياً") }, submittedAt: "2026-07-21T10:00:00Z" },
        ]),
      [],
    );
  }

  recordVitals(encounterId: string, readings: VitalInput[]) {
    return this.gate(() => ok(zVitalsResult, { encounterId, recorded: readings.length }));
  }

  // ---- Lab / imaging -----------------------------------------------------
  labQueue(kind: "lab" | "imaging") {
    return this.gate(() => {
      const base =
        kind === "lab"
          ? [
              {
                id: "ORD-55012",
                kind: "lab" as const,
                test: { system: "LOINC" as const, code: "58410-2", label: loc("Complete blood count", "تعداد دم كامل") },
                patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
                priority: "routine" as const,
                status: { kind: "info" as const, label: loc("Queued", "في الطابور") },
                placedAt: NOW,
                panelsTotal: 3,
                panelsDone: this.labProgress.get("ORD-55012") ?? 0,
              },
              {
                id: "ORD-55019",
                kind: "lab" as const,
                test: { system: "LOINC" as const, code: "2345-7", label: loc("Glucose", "سكر الدم") },
                patient: { id: "MRS-M-10555", token: "Y.H · •••7702" },
                priority: "urgent" as const,
                status: { kind: "warn" as const, label: loc("Urgent", "عاجل") },
                placedAt: NOW,
                panelsTotal: 1,
                panelsDone: this.labProgress.get("ORD-55019") ?? 0,
              },
            ]
          : [
              {
                id: "ORD-77003",
                kind: "imaging" as const,
                test: { system: "CPT" as const, code: "71046", label: loc("Chest X-ray", "أشعة صدر") },
                patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
                priority: "routine" as const,
                status: { kind: "info" as const, label: loc("Queued", "في الطابور") },
                placedAt: NOW,
                panelsTotal: 1,
                panelsDone: this.labProgress.get("ORD-77003") ?? 0,
              },
            ];
      return ok(z.array(zLabOrder), base);
    }, []);
  }
  consume(req: ConsumeRequest) {
    return this.gate(() => {
      const replayed = this.seenKeys.has(req.idempotencyKey);
      const totals: Record<string, number> = { "ORD-55012": 3, "ORD-55019": 1, "ORD-77003": 1 };
      const total = totals[req.orderId] ?? 1;
      let done = this.labProgress.get(req.orderId) ?? 0;
      if (!replayed) {
        this.seenKeys.add(req.idempotencyKey);
        done = Math.min(total, done + req.panels);
        this.labProgress.set(req.orderId, done);
      }
      return ok(zConsumeResult, {
        orderId: req.orderId,
        fulfillmentId: "FUL-" + req.orderId,
        status:
          done >= total
            ? { kind: "ok", label: loc("Fulfilled", "مكتمل") }
            : { kind: "part", label: loc("Partially fulfilled", "مكتمل جزئياً") },
        panelsDone: done,
        panelsTotal: total,
        replayed,
      });
    });
  }

  awaitingResult(kind: "lab" | "imaging") {
    const rows =
      kind === "lab"
        ? [{ orderId: "ORD-55012", lineId: "L-1", orderNo: "ORD-2026-000118", code: "80053", desc: "Comprehensive metabolic panel", tok: "•••4821" }]
        : [{ orderId: "ORD-77003", lineId: "L-9", orderNo: "ORD-2026-000119", code: "71046", desc: "Chest X-ray", tok: "•••7710" }];
    return this.gate(
      () =>
        ok(z.array(zResultTask), rows.map((r) => ({
          orderId: r.orderId, lineId: r.lineId, orderNo: r.orderNo, orderType: kind === "lab" ? "Lab" : "Imaging",
          beneficiary: { id: r.orderId, token: r.tok }, code: r.code, description: r.desc, consumedAt: NOW,
        }))),
      [],
    );
  }
  uploadResult(orderId: string, lineId: string, resultValue: string) {
    void resultValue;
    return this.gate(() => ok(zResultUpload, { orderId, lineId, uploaded: true }));
  }

  searchDrugs(query: string) {
    const all = [
      { drugId: "d-amox-500", name: loc("Amoxicillin 500mg caps", "أموكسيسيلين 500مجم"), atcCode: "J01CA04", form: "Capsule", strength: "500mg" },
      { drugId: "d-amox-250", name: loc("Amoxicillin 250mg caps", "أموكسيسيلين 250مجم"), atcCode: "J01CA04", form: "Capsule", strength: "250mg" },
      { drugId: "d-metf-500", name: loc("Metformin 500mg", "ميتفورمين 500مجم"), atcCode: "A10BA02", form: "Tablet", strength: "500mg" },
    ].filter((d) => d.name.en.toLowerCase().includes(query.toLowerCase()));
    return this.gate(() => ok(z.array(zDrugRef), all), []);
  }
  drugAlternatives(drugId: string) {
    const alts = drugId.startsWith("d-amox")
      ? [
          { drugId: "d-amox-250", name: loc("Amoxicillin 250mg caps", "أموكسيسيلين 250مجم"), atcCode: "J01CA04", form: "Capsule", strength: "250mg" },
          { drugId: "d-amox-susp", name: loc("Amoxicillin 125mg/5ml susp", "أموكسيسيلين شراب"), atcCode: "J01CA04", form: "Suspension", strength: "125mg/5ml" },
        ]
      : [];
    return this.gate(() => ok(z.array(zDrugRef), alts), []);
  }

  // ---- Pharmacy ----------------------------------------------------------
  pharmacyQueue() {
    return this.gate(() => {
      const disp = (rx: string, line: string) => this.rxProgress.get(rx)?.get(line) ?? 0;
      return ok(z.array(zPrescription), [
        {
          id: "RX-33110",
          patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
          prescriber: { label: loc("Dr. N. Fahmy", "د. ن. فهمي") },
          submittedAt: NOW,
          status: { kind: "info", label: loc("Submitted", "مُرسلة") },
          lines: [
            {
              id: "RXL-1",
              drug: { system: "ATC", code: "J01CA04", label: loc("Amoxicillin 500mg", "أموكسيسيلين ٥٠٠ملغ") },
              quantity: 21,
              dispensed: disp("RX-33110", "RXL-1"),
              dose: "1 cap × 3/day",
              status: { kind: "info", label: loc("Pending", "معلّقة") },
              outOfStock: false,
            },
            {
              id: "RXL-2",
              drug: { system: "ATC", code: "R05CB", label: loc("Guaifenesin syrup", "شراب جوايفينيسين") },
              quantity: 1,
              dispensed: disp("RX-33110", "RXL-2"),
              dose: "10 ml × 3/day",
              status: { kind: "warn", label: loc("Out of stock", "غير متوفر") },
              outOfStock: true,
            },
          ],
        },
      ]);
    }, []);
  }
  dispense(req: DispenseRequest) {
    return this.gate(() => {
      const replayed = this.seenKeys.has(req.idempotencyKey);
      if (!replayed) {
        this.seenKeys.add(req.idempotencyKey);
        const lines = this.rxProgress.get(req.prescriptionId) ?? new Map<string, number>();
        for (const l of req.lines) lines.set(l.lineId, (lines.get(l.lineId) ?? 0) + l.quantity);
        this.rxProgress.set(req.prescriptionId, lines);
      }
      const totals: Record<string, number> = { "RXL-1": 21, "RXL-2": 1 };
      const lines = this.rxProgress.get(req.prescriptionId) ?? new Map();
      const outstanding = Object.entries(totals).filter(([id, t]) => (lines.get(id) ?? 0) < t).length;
      return ok(zDispenseResult, {
        prescriptionId: req.prescriptionId,
        dispenseEventId: "DSP-" + req.prescriptionId,
        status: outstanding === 0
          ? { kind: "ok", label: loc("Fully dispensed", "تم الصرف بالكامل") }
          : { kind: "part", label: loc("Partially dispensed", "صرف جزئي") },
        replayed,
        linesOutstanding: outstanding,
      });
    });
  }

  // ---- Approvals ---------------------------------------------------------
  approvalWorklist() {
    return this.gate(
      () =>
        ok(z.array(zApprovalItem), [
          {
            id: "AUTH-9001",
            patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
            service: { system: "CPT", code: "70553", label: loc("MRI brain w/ contrast", "رنين مغناطيسي للمخ") },
            requestedBy: loc("Nile Imaging Center", "مركز النيل للأشعة"),
            priority: "urgent",
            sla: { dueAt: "2026-07-22T12:00:00Z", breached: false, minutesRemaining: 210 },
            status: { kind: "info", label: loc("Awaiting review", "بانتظار المراجعة") },
            submittedAt: NOW,
            estimatedCost: "EGP 6,500",
          },
          {
            id: "AUTH-9002",
            patient: { id: "MRS-M-10555", token: "Y.H · •••7702" },
            service: { system: "CPT", code: "29881", label: loc("Knee arthroscopy", "منظار الركبة") },
            requestedBy: loc("Cairo Ortho Clinic", "عيادة القاهرة للعظام"),
            priority: "routine",
            sla: { dueAt: "2026-07-22T06:00:00Z", breached: true, minutesRemaining: -150 },
            status: { kind: "warn", label: loc("SLA breached", "تجاوز المهلة") },
            submittedAt: "2026-07-21T09:00:00Z",
            estimatedCost: "EGP 18,000",
          },
        ]),
      [],
    );
  }
  approvalReview(approvalId: string) {
    return this.gate(() =>
      ok(zApprovalReview, {
        id: approvalId,
        patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
        service: { system: "CPT", code: "70553", label: loc("MRI brain w/ contrast", "رنين مغناطيسي للمخ") },
        clinicalJustification:
          "Progressive headache with focal neurological signs; rule out intracranial lesion.",
        supportingCodes: [
          { system: "ICD-10", code: "R51.9", label: loc("Headache, unspecified", "صداع غير محدد") },
        ],
        documents: [
          { id: "DOC-1", name: "neuro-exam.pdf" },
          { id: "DOC-2", name: "referral-letter.pdf" },
        ],
        requestedAmount: "EGP 6,500",
      }),
    );
  }
  decide(req: DecisionRequest) {
    return this.gate(() => {
      const replayed = this.seenKeys.has(req.idempotencyKey);
      if (!replayed) this.seenKeys.add(req.idempotencyKey);
      const label: Record<DecisionRequest["decision"], Localized> = {
        approve: loc("Approved", "تمت الموافقة"),
        partial: loc("Partially approved", "موافقة جزئية"),
        reject: loc("Rejected", "مرفوض"),
        request_info: loc("Information requested", "طُلبت معلومات"),
      };
      const kind = req.decision === "reject" ? "bad" : req.decision === "approve" ? "ok" : "part";
      return ok(zDecisionResult, {
        approvalId: req.approvalId,
        decisionId: "DEC-" + req.approvalId,
        status: { kind, label: label[req.decision] },
        replayed,
      });
    });
  }

  slaSummary() {
    return this.gate(() =>
      ok(zTatSummary, {
        total: 42, avgMinutes: 87.5, p95Minutes: 240, breaches: 5,
        byStatus: [
          { status: "Approved", count: 28, avgMinutes: 65, p95Minutes: 180, breaches: 1 },
          { status: "Rejected", count: 6, avgMinutes: 110, p95Minutes: 260, breaches: 2 },
          { status: "UnderReview", count: 8, avgMinutes: 130, p95Minutes: 300, breaches: 2 },
        ],
      }),
    );
  }
  createManualAuth(input: ManualAuthInput) {
    void input;
    return this.gate(() =>
      ok(zManualAuthResult, { authorizationId: "AUTH-MAN-0007", authNo: "AUTH-2026-0M07", status: { kind: "ok", label: loc("Approved", "معتمد") } }),
    );
  }
  emergencyApprove(authId: string, justification: string) {
    void justification;
    return this.gate(() => ok(zEmergencyResult, { authorizationId: authId, status: { kind: "ok", label: loc("Emergency approved", "اعتماد طارئ") } }));
  }

  directorReport(section: "oversight" | "quality" | "escalations") {
    const views: Record<string, unknown> = {
      oversight: {
        kpis: [
          { label: loc("Pending", "معلّقة"), value: "3" },
          { label: loc("SLA breaches", "تجاوزات"), value: "1" },
          { label: loc("Avg TAT (min)", "متوسط (د)"), value: "58" },
          { label: loc("P95 TAT (min)", "p95 (د)"), value: "120" },
        ],
        tables: [{
          title: loc("Pending by status", "المعلّقة حسب الحالة"),
          columns: [loc("Status", "الحالة"), loc("Priority", "الأولوية"), loc("Count", "العدد")],
          rows: [["Submitted", "Emergency", "1"], ["Submitted", "Urgent", "1"], ["UnderReview", "Routine", "1"]],
        }],
      },
      quality: {
        kpis: [
          { label: loc("Booked", "محجوزة"), value: "60" },
          { label: loc("No-shows", "تخلّف"), value: "9" },
          { label: loc("No-show rate", "نسبة التخلّف"), value: "15%" },
        ],
        tables: [{
          title: loc("Top diagnoses", "أكثر التشخيصات"),
          columns: [loc("ICD-10", "ICD-10"), loc("Count", "العدد")],
          rows: [["E11.9", "23"], ["I10", "19"], ["J06.9", "15"]],
        }],
      },
      escalations: {
        kpis: [{ label: loc("Rejected", "مرفوضة"), value: "2" }],
        tables: [{
          title: loc("Rejections by reason", "الرفض حسب السبب"),
          columns: [loc("Reason", "السبب"), loc("Count", "العدد")],
          rows: [["NOT_COVERED", "1"], ["INSUFFICIENT_DOCS", "1"]],
        }],
      },
    };
    return this.gate(() => ok(zReportView, views[section]));
  }

  // ---- Dashboard ---------------------------------------------------------
  executiveDashboard(scope: "executive" | "finance" | "director") {
    return this.gate(() => {
      const kpis = [
        {
          kind: "kpi" as const,
          id: "tat",
          title: loc("Approval TAT (p95)", "زمن الموافقة (p95)"),
          value: "4.2h",
          delta: "0.6h",
          direction: "down" as const,
          status: { kind: "ok" as const, label: loc("Within SLA", "ضمن المهلة") },
        },
        {
          kind: "kpi" as const,
          id: "pending",
          title: loc("Pending approvals", "موافقات معلّقة"),
          value: "37",
          delta: "5",
          direction: "up" as const,
        },
        {
          kind: "kpi" as const,
          id: "utilization",
          title: loc("Utilization", "معدل الاستخدام"),
          value: "82%",
        },
        {
          kind: "kpi" as const,
          id: "noshow",
          title: loc("No-show rate", "معدل عدم الحضور"),
          value: "6.1%",
          delta: "0.4%",
          direction: "down" as const,
        },
      ];
      const charts = [
        {
          kind: "chart" as const,
          id: "workload",
          title: loc("Clinic workload (visits/day)", "حِمل العيادة (زيارات/يوم)"),
          chartType: "bar" as const,
          series: [
            { label: loc("Mon", "الإثنين"), value: 120, display: "120" },
            { label: loc("Tue", "الثلاثاء"), value: 138, display: "138" },
            { label: loc("Wed", "الأربعاء"), value: 104, display: "104" },
            { label: loc("Thu", "الخميس"), value: 152, display: "152" },
          ],
          dataTable: {
            columns: [loc("Day", "اليوم"), loc("Visits", "زيارات")],
            rows: [["Mon", "120"], ["Tue", "138"], ["Wed", "104"], ["Thu", "152"]],
          },
        },
        // Finance zone: this is a MEDS/spend breakdown, never a diagnosis breakdown (finance ≠ diagnosis).
        {
          kind: "chart" as const,
          id: scope === "finance" ? "spend" : "topdx",
          title:
            scope === "finance"
              ? loc("Top spend categories", "أعلى فئات الإنفاق")
              : loc("Top diagnoses", "أكثر التشخيصات"),
          chartType: "donut" as const,
          series:
            scope === "finance"
              ? [
                  { label: loc("Pharmacy", "صيدلية"), value: 42, display: "42%" },
                  { label: loc("Imaging", "أشعة"), value: 31, display: "31%" },
                  { label: loc("Labs", "مختبر"), value: 27, display: "27%" },
                ]
              : [
                  { label: loc("URTI", "التهاب تنفسي"), value: 40, display: "40%" },
                  { label: loc("Hypertension", "ضغط الدم"), value: 33, display: "33%" },
                  { label: loc("Diabetes", "السكري"), value: 27, display: "27%" },
                ],
          dataTable:
            scope === "finance"
              ? {
                  columns: [loc("Category", "الفئة"), loc("Share", "النسبة")],
                  rows: [["Pharmacy", "42%"], ["Imaging", "31%"], ["Labs", "27%"]],
                }
              : {
                  columns: [loc("Diagnosis", "التشخيص"), loc("Share", "النسبة")],
                  rows: [["URTI", "40%"], ["Hypertension", "33%"], ["Diabetes", "27%"]],
                },
        },
      ];
      return ok(zExecutiveDashboard, {
        version: "1.0",
        generatedAt: NOW,
        scope,
        // Finance dashboards omit any diagnosis breakdown by construction.
        kpis,
        charts: scope === "finance" ? charts.filter((c) => c.id !== "topdx") : charts,
      });
    });
  }

  // ---- Case management (Phase 10.1) — assignment-scoped, coordination summary ----------------------------
  myCases() {
    return this.gate(
      () =>
        ok(z.array(zCaseListItem), [
          {
            id: "CASE-2026-000042",
            caseNo: "CASE-2026-000042",
            beneficiary: { id: "MRS-M-10231", token: "A.H · •••4821" },
            category: "chronic",
            priority: "high",
            status: { kind: "warn", label: loc("Active", "نشطة") },
            openedAt: "2026-07-10T09:00:00Z",
            summary: loc("Diabetes care coordination", "تنسيق رعاية السكري"),
          },
          {
            id: "CASE-2026-000051",
            caseNo: "CASE-2026-000051",
            beneficiary: { id: "MRS-M-10555", token: "Y.H · •••7702" },
            category: "vulnerable",
            priority: "urgent",
            status: { kind: "info", label: loc("Open", "مفتوحة") },
            openedAt: "2026-07-18T11:30:00Z",
            summary: loc("Post-surgery follow-up", "متابعة بعد الجراحة"),
          },
        ]),
      [],
    );
  }
  beneficiary360(caseId: string) {
    return this.gate(() =>
      ok(zBeneficiary360, {
        caseId,
        caseNo: caseId,
        beneficiary: { id: "MRS-M-10231", token: "A.H · •••4821" },
        coverage: {
          status: { kind: "ok", label: loc("Eligible", "مؤهل") },
          planName: loc("Mersal Essential", "مرسال الأساسية"),
          coverageCategory: loc("Band B — Outpatient + Pharmacy", "الفئة ب — عيادات + صيدلية"),
          annualCap: 20000,
          remaining: 8400,
        },
        carePlan: {
          status: loc("Active", "نشطة"),
          goals: [
            loc("HbA1c below 7% by Q4", "خفض السكر التراكمي دون ٧٪ بالربع الرابع"),
            loc("Quarterly retinal screening", "فحص الشبكية الفصلي"),
          ],
          reviewDue: "2026-09-30T00:00:00Z",
        },
        appointments: [
          { id: "APT-2201", clinic: loc("Endocrinology", "الغدد الصماء"), when: "2026-07-28T10:00:00Z", status: { kind: "info", label: loc("Booked", "محجوز") } },
        ],
        openApprovals: [
          { authNo: "AUTH-9001", status: { kind: "info", label: loc("Awaiting review", "بانتظار المراجعة") }, priority: "high", decidedAt: undefined },
        ],
        // Coordination clinical SUMMARY: diagnoses coord-visible; notes/rx/results MASKED (count only).
        clinical: {
          activeDiagnoses: [
            { system: "ICD-10", code: "E11.9", label: loc("Type 2 diabetes", "السكري من النوع ٢") },
            { system: "ICD-10", code: "I10", label: loc("Essential hypertension", "ارتفاع ضغط الدم") },
          ],
          notes: { count: 4, summaryOnly: true },
          prescriptions: { count: 3, summaryOnly: true },
          results: { count: 6, summaryOnly: true },
        },
      }),
    );
  }
  caseTasks(caseId: string) {
    return this.gate(
      () =>
        ok(z.array(zCoordinationTask), [
          { id: "TSK-1", caseId, title: loc("Book retinal screening", "حجز فحص الشبكية"), state: "todo", dueAt: "2026-07-30T00:00:00Z", status: { kind: "info", label: loc("To do", "للتنفيذ") } },
          { id: "TSK-2", caseId, title: loc("Confirm pharmacy refill", "تأكيد صرف الصيدلية"), state: "in_progress", status: { kind: "warn", label: loc("In progress", "قيد التنفيذ") } },
          { id: "TSK-3", caseId, title: loc("Call beneficiary re: diet plan", "الاتصال بالمستفيد بخصوص نظام الغذاء"), state: "done", status: { kind: "ok", label: loc("Done", "تم") } },
        ]),
      [],
    );
  }
  escalations() {
    return this.gate(
      () =>
        ok(z.array(zEscalation), [
          {
            id: "ESC-1",
            caseId: "CASE-2026-000051",
            caseNo: "CASE-2026-000051",
            raisedToRole: loc("Medical Approval", "الموافقة الطبية"),
            reason: "Urgent authorization for post-surgical imaging pending > 24h.",
            status: { kind: "warn", label: loc("Raised", "مُصعّدة") },
            raisedAt: "2026-07-20T08:00:00Z",
          },
        ]),
      [],
    );
  }

  // ---- Finance (Phase 10.2) — billing codes + amounts only, no diagnosis --------------------------------
  utilization() {
    return this.gate(() =>
      ok(zUtilizationView, {
        from: "2026-06-22",
        to: "2026-07-22",
        rows: [
          { serviceCode: "70553", serviceLine: loc("Imaging", "أشعة"), coverageCategory: loc("Outpatient", "عيادات خارجية"), providerRef: "PRV-•••301", authorizedQty: 12, deliveredQty: 9, spend: 58500 },
          { serviceCode: "J01CA04", serviceLine: loc("Pharmacy", "صيدلية"), coverageCategory: loc("Pharmacy", "صيدلية"), providerRef: "PRV-•••118", authorizedQty: 240, deliveredQty: 231, spend: 12400 },
          { serviceCode: "80053", serviceLine: loc("Lab", "مختبر"), coverageCategory: loc("Outpatient", "عيادات خارجية"), providerRef: "PRV-•••204", authorizedQty: 88, deliveredQty: 86, spend: 9120 },
        ],
        totalAuthorized: 340,
        totalDelivered: 326,
        totalSpend: 80020,
      }),
    );
  }
  settlements() {
    return this.gate(
      () =>
        ok(z.array(zSettlement), [
          {
            id: "STL-2026-000007",
            settlementNo: "STL-2026-000007",
            providerRef: "PRV-•••301",
            providerName: loc("Nile Imaging Center", "مركز النيل للأشعة"),
            periodStart: "2026-06-01",
            periodEnd: "2026-06-30",
            currency: "EGP",
            total: 58500,
            status: { kind: "info", label: loc("Submitted", "مُقدّمة") },
            state: "submitted",
            lines: [
              { serviceCode: "70553", serviceLine: loc("Imaging", "أشعة"), deliveredQty: 9, agreedUnitPrice: 6500, lineTotal: 58500 },
            ],
          },
          {
            id: "STL-2026-000006",
            settlementNo: "STL-2026-000006",
            providerRef: "PRV-•••118",
            providerName: loc("Cairo Community Pharmacy", "صيدلية القاهرة") ,
            periodStart: "2026-06-01",
            periodEnd: "2026-06-30",
            currency: "EGP",
            total: 12400,
            status: { kind: "ok", label: loc("Approved", "معتمدة") },
            state: "approved",
            lines: [
              { serviceCode: "J01CA04", serviceLine: loc("Pharmacy", "صيدلية"), deliveredQty: 231, agreedUnitPrice: 53.68, lineTotal: 12400 },
            ],
          },
        ]),
      [],
    );
  }
  financialSummary(dimension: "serviceline" | "category" | "provider") {
    return this.gate(() =>
      ok(zFinancialSummary, {
        dimension,
        buckets: [
          { key: loc("Imaging", "أشعة"), deliveredQty: 9, spend: 58500, sharePercent: 73 },
          { key: loc("Pharmacy", "صيدلية"), deliveredQty: 231, spend: 12400, sharePercent: 15 },
          { key: loc("Lab", "مختبر"), deliveredQty: 86, spend: 9120, sharePercent: 12 },
        ],
        totalSpend: 80020,
      }),
    );
  }
  exportReport(req: ExportRequest) {
    return this.gate(() =>
      ok(zExportResult, {
        report: req.report,
        format: req.format,
        rowCount: 3,
        filename: `${req.report}-${req.from}_${req.to}.${req.format}`,
        status: { kind: "ok", label: loc("Export ready (audited)", "التصدير جاهز (مُدقّق)") },
      }),
    );
  }

  // ---- Claims management (Phase 10b) — codes + amounts only, no diagnosis (finance-parity) --------------
  claimsWorklist(status?: string) {
    const rows = [
      { id: "clm-1", claimNo: "CLM-2026-004411", origin: "Provider", key: "Submitted", st: { kind: "info" as const, label: loc("Submitted", "مُقدّمة") }, claimed: 3200, net: null, from: "2026-07-18", at: "2026-07-19T09:00:00Z" },
      { id: "clm-2", claimNo: "CLM-2026-004412", origin: "AutoDerived", key: "Adjudicated", st: { kind: "ok" as const, label: loc("Adjudicated", "تمت المراجعة") }, claimed: 1450, net: 1305, from: "2026-07-15", at: "2026-07-16T11:30:00Z" },
      { id: "clm-3", claimNo: "CLM-2026-004413", origin: "Reimbursement", key: "Rejected", st: { kind: "bad" as const, label: loc("Rejected", "مرفوضة") }, claimed: 900, net: 0, from: "2026-07-12", at: "2026-07-13T08:15:00Z" },
    ].filter((r) => !status || r.key === status);
    return this.gate(
      () =>
        ok(z.array(zClaimRow), rows.map((r) => ({
          id: r.id, claimNo: r.claimNo, origin: r.origin, status: r.st, currency: "EGP",
          claimedAmount: r.claimed, netPayable: r.net, serviceDateFrom: r.from, submittedAt: r.at,
        }))),
      [],
    );
  }

  claimsReconciliation(bucket?: string) {
    const rows = [
      { claimId: "clm-1", claimNo: "CLM-2026-004411", origin: "Provider", code: "80053", date: "2026-07-18", billed: 320, allowed: 300, bucket: "PriceVariance", st: { kind: "warn" as const, label: loc("Price variance", "فرق سعر") } },
      { claimId: "clm-4", claimNo: "CLM-2026-004420", origin: "AutoDerived", code: "71046", date: "2026-07-17", billed: 800, allowed: null, bucket: "BilledNotDelivered", st: { kind: "bad" as const, label: loc("Billed, not delivered", "فوترة بلا تنفيذ") } },
      { claimId: "clm-5", claimNo: "CLM-2026-004421", origin: "Provider", code: "85025", date: "2026-07-16", billed: 150, allowed: 150, bucket: "Matched", st: { kind: "ok" as const, label: loc("Matched", "مطابقة") } },
    ].filter((r) => !bucket || r.bucket === bucket);
    return this.gate(
      () =>
        ok(z.array(zReconciliationRow), rows.map((r) => ({
          claimId: r.claimId, claimNo: r.claimNo, origin: r.origin, code: r.code, serviceDate: r.date,
          billedAmount: r.billed, allowedAmount: r.allowed, bucket: r.bucket, status: r.st,
        }))),
      [],
    );
  }

  claimsKpis() {
    return this.gate(() =>
      ok(zClaimsKpis, {
        averageTatHours: 34.5, approvalRate: 0.82, denialRate: 0.11, ocrAutoMatchRate: 0.74,
        agedUnbilledCount: 12, agedUnbilledValue: 41250, recoveryOutstanding: 8900,
        topDenialReasons: [
          { reason: "Missing authorization", count: 7 },
          { reason: "Non-covered service", count: 4 },
          { reason: "Duplicate claim", count: 3 },
        ],
      }),
    );
  }

  // ---- Notifications (Phase 8.1) — the caller's own in-app inbox, cross-portal --------------------------
  notifications(unreadOnly?: boolean) {
    // Reads that happened in this session are overlaid on the fixture, so a screen that reloads after
    // marking read sees the write — the same way it would against the service.
    const rows = NOTIFICATION_FIXTURE.map((n) =>
      this.notificationsRead.has(n.id) ? { ...n, read: true } : n,
    );
    return this.gate(
      () => ok(z.array(zNotification), unreadOnly ? rows.filter((n) => !n.read) : rows),
      [],
    );
  }
  private unreadNotificationIds() {
    return NOTIFICATION_FIXTURE.filter((n) => !n.read && !this.notificationsRead.has(n.id)).map((n) => n.id);
  }
  markNotificationRead(id: string) {
    this.notificationsRead.add(id);
    return this.gate(() => ok(zMarkReadResult, { id, read: true }));
  }
  markAllNotificationsRead() {
    const marked = this.unreadNotificationIds();
    marked.forEach((id) => this.notificationsRead.add(id));
    return this.gate(() => ok(zMarkAllReadResult, { marked: marked.length }));
  }

  // ---- Admin / platform governance (Phase 8b) — WHO can access, not content ------------------------------
  accessMatrix() {
    return this.gate(
      () =>
        ok(z.array(zRoleBinding), [
          { id: "RB-1", subjectToken: "•••8a91", role: "doctor", scope: "Tenant", tier: "T4", status: { kind: "ok", label: loc("Active", "نشط") }, grantedAt: "2026-06-25T09:00:00Z", reviewDueAt: "2026-09-25T09:00:00Z" },
          { id: "RB-2", subjectToken: "•••1c07", role: "finance", scope: "Tenant", tier: "T2", status: { kind: "ok", label: loc("Active", "نشط") }, grantedAt: "2026-06-25T09:00:00Z" },
        ]),
      [],
    );
  }
  adminTenants() {
    return this.gate(
      () => ok(z.array(zTenantSummary), [{ id: "T-1", name: "Mersal Foundation", status: { kind: "ok", label: loc("Active", "نشط") }, createdAt: "2026-01-01T00:00:00Z" }]),
      [],
    );
  }
  sodMatrix() {
    return this.gate(
      () =>
        ok(z.array(zSodConflict), [
          { roleA: "doctor", roleB: "medical_approval", reason: "Self-approval of own clinical request" },
          { roleA: "finance", roleB: "finance", reason: "Initiator must not release own payment" },
        ]),
      [],
    );
  }
  accessReviewCampaigns() {
    return this.gate(
      () => ok(z.array(zAccessReviewCampaign), [{ id: "CAMP-1", name: "Q3 2026 high-sensitivity access recertification", status: { kind: "info", label: loc("Open", "مفتوحة") }, minTier: "T3", dueAt: "2026-08-05T00:00:00Z" }]),
      [],
    );
  }
  breakGlassGrants() {
    return this.gate(
      () => ok(z.array(zBreakGlassGrant), [{ id: "BG-1", requesterToken: "•••8a91", reasonCode: "EmergencyCare", status: { kind: "neu", label: loc("Expired", "منتهٍ") }, requestedAt: "2026-07-20T02:00:00Z", expiresAt: "2026-07-20T03:00:00Z" }]),
      [],
    );
  }
  providerList() {
    return this.gate(
      () => ok(z.array(zProviderSummary), [
        { id: "PRV-1", code: "PRV-0001", legalName: "Nile Central Hospital", providerType: "Hospital", status: { kind: "ok", label: loc("Active", "نشط") }, onboardingState: "Activated" },
        { id: "PRV-2", code: "PRV-0002", legalName: "Cairo Care Clinic", providerType: "Clinic", status: { kind: "ok", label: loc("Active", "نشط") }, onboardingState: "Activated" },
        { id: "PRV-3", code: "PRV-0003", legalName: "Delta Diagnostics Lab", providerType: "Lab", status: { kind: "warn", label: loc("Suspended", "موقوف") }, onboardingState: "Credentialed" },
      ]),
      [],
    );
  }
  providerLocations(providerId: string) {
    void providerId;
    return this.gate(
      () => ok(z.array(zProviderLocation), [
        { id: "LOC-1", name: "Main Campus", governorate: "Cairo", address: "12 Nile Corniche", isPrimary: true },
        { id: "LOC-2", name: "East Annex", governorate: "Cairo", address: "4 Salah Salem St", isPrimary: false },
      ]),
      [],
    );
  }
  providerContracts(providerId: string) {
    void providerId;
    return this.gate(
      () => ok(z.array(zProviderContract), [
        { id: "CON-1", contractNo: "CON-2026-0001", status: { kind: "ok", label: loc("Active", "نشط") }, effectiveFrom: "2026-01-01", effectiveTo: "2026-12-31", serviceLines: 4 },
      ]),
      [],
    );
  }
  createProvider(input: CreateProviderInput) {
    return this.gate(() => ok(zProviderSummary, { id: "PRV-NEW", code: input.code, legalName: input.legalName, providerType: input.providerType, status: { kind: "warn", label: loc("Suspended", "موقوف") }, onboardingState: "Draft" }));
  }

  // ---- Practitioners (Phase 14.5) -------------------------------------------------------------------------
  /**
   * A subset of the specialty seed in provider migration 0006, with the codes and names copied EXACTLY.
   *
   * Fixtures that invent their own codes are worse than no fixtures: the first draft here used "OBG" and
   * "ORTH", which do not exist — the seed has OBGYN and ORTHO — so every screen built against it would have
   * looked right in dev and filtered to nothing in production, and the specialty filter is the one thing the
   * booking screen depends on. PSYCH and CPSY are present because they drive the 14.6 sensitivity defaults.
   */
  /** A handful of real ICD-10 titles — enough that the history table reads as conditions rather than codes
   *  in the dev harness. Anything unlisted is absent, which is exactly what a real miss looks like. */
  branchLabels(branchIds: readonly string[]) {
    const names: Record<string, string> = {
      "0190b100-0000-7000-8000-000000000005": "Dokki",
      "0190b100-0000-7000-8000-000000000004": "Maadi",
    };
    return Promise.resolve(new Map(branchIds.filter((b) => names[b]).map((b) => [b, names[b]] as const)));
  }

  icdTitles(codes: readonly string[]) {
    const catalogue: Record<string, string> = {
      "I10": "Essential (primary) hypertension",
      "E11.9": "Type 2 diabetes mellitus, Without complications",
      "K21.9": "Gastro-oesophageal reflux disease without oesophagitis",
      "J22": "Unspecified acute lower respiratory infection",
      "D50.9": "Iron deficiency anaemia, unspecified",
      "Z00.0": "General medical examination",
      "J02.9": "Acute pharyngitis, unspecified",
    };
    return Promise.resolve(
      new Map(codes.filter((c) => catalogue[c]).map((c) => [c, catalogue[c]] as const)));
  }

  specialties() {
    return this.gate(
      () => ok(z.array(zSpecialty), [
        { code: "GP", name: loc("General Practice", "الممارسة العامة") },
        { code: "IM", name: loc("Internal Medicine", "الباطنة") },
        { code: "PED", name: loc("Pediatrics", "طب الأطفال") },
        { code: "OBGYN", name: loc("Obstetrics & Gynaecology", "النساء والتوليد") },
        { code: "CARD", name: loc("Cardiology", "أمراض القلب") },
        { code: "DERM", name: loc("Dermatology", "الجلدية") },
        { code: "PSYCH", name: loc("Psychiatry", "الطب النفسي") },
        { code: "CPSY", name: loc("Clinical Psychology", "علم النفس الإكلينيكي") },
        { code: "ORTHO", name: loc("Orthopaedics", "العظام") },
        { code: "ENT", name: loc("ENT", "الأنف والأذن والحنجرة") },
      ]),
      [],
    );
  }

  /** The six internal clinics. These are the real branch list the booking screen filters on. */
  branches() {
    const active = { kind: "ok" as const, label: loc("Active", "نشط") };
    return this.gate(
      () => ok(z.array(zBranchSummary), [
        { id: "BR-OCT", code: "OCT", name: loc("October", "أكتوبر"), city: "6th of October", status: active },
        { id: "BR-DOK", code: "DOK", name: loc("Dokki", "الدقي"), city: "Giza", status: active },
        { id: "BR-MAA", code: "MAA", name: loc("Maadi", "المعادي"), city: "Cairo", status: active },
        { id: "BR-ASW", code: "ASW", name: loc("Aswan", "أسوان"), city: "Aswan", status: active },
        { id: "BR-ALX", code: "ALX", name: loc("Alexandria", "الإسكندرية"), city: "Alexandria", status: active },
        { id: "BR-NSR", code: "NSR", name: loc("Nasr City", "مدينة نصر"), city: "Cairo", status: active },
      ]),
      [],
    );
  }

  /**
   * The fixture deliberately includes a doctor with NO specialty and no branch (`PRC-4`). That is the state
   * the admin screen exists to make visible and fixable: such a record is invisible to the booking picker,
   * which filters on exactly those two fields, and a fixture of nothing but well-formed doctors is one where
   * the "incomplete" affordance never gets looked at.
   */
  /**
   * MUTABLE, unlike most fixtures here, because this screen's whole point is amending a record: a roster
   * that reset on every reload would make "promote to primary" look like it silently did nothing, which is
   * the exact failure the panel exists to rule out.
   */
  private practitionerRows = [
    { id: "PRC-1", practitionerType: "Doctor", name: loc("Hana Mansour", "هناء منصور"), primarySpecialty: "PED" as string | undefined, specialties: ["PED"], branches: ["BR-DOK", "BR-MAA"], status: DEV_ACTIVE as DevStatusChip },
    { id: "PRC-2", practitionerType: "Doctor", name: loc("Youssef Adel", "يوسف عادل"), primarySpecialty: "CARD" as string | undefined, specialties: ["CARD", "GP"], branches: ["BR-NSR"], status: DEV_ACTIVE as DevStatusChip },
    { id: "PRC-3", practitionerType: "Doctor", name: loc("Mona Saleh", "منى صالح"), primarySpecialty: "OBGYN" as string | undefined, specialties: ["OBGYN"], branches: ["BR-ALX", "BR-ASW"], status: DEV_ACTIVE as DevStatusChip },
    // No specialty, no clinic — the unbookable case the roster's "Bookable" column exists to surface, and
    // the record the edit panel exists to repair.
    { id: "PRC-4", practitionerType: "Doctor", name: loc("Karim Fouad", "كريم فؤاد"), primarySpecialty: undefined as string | undefined, specialties: [] as string[], branches: [] as string[], status: DEV_SUSPENDED as DevStatusChip },
    { id: "PRC-5", practitionerType: "Nurse", name: loc("Salma Nabil", "سلمى نبيل"), primarySpecialty: "GP" as string | undefined, specialties: ["GP"], branches: ["BR-OCT"], status: DEV_ACTIVE as DevStatusChip },
  ];

  private findPractitioner(id: string) {
    const row = this.practitionerRows.find((p) => p.id === id);
    if (!row) throw new ApiError("http", "Not Found", 404);
    return row;
  }

  practitioners(filter?: { branchId?: string; specialtyCode?: string; type?: string }) {
    // Filtered the same way the server filters, so a screen developed against fixtures behaves the same live.
    const rows = this.practitionerRows.filter((p) =>
      (!filter?.branchId || p.branches.includes(filter.branchId)) &&
      (!filter?.specialtyCode || p.specialties.includes(filter.specialtyCode)) &&
      (!filter?.type || p.practitionerType === filter.type));
    return this.gate(() => ok(z.array(zPractitioner), rows), []);
  }

  createPractitioner(input: CreatePractitionerInput) {
    return this.gate(() => {
      const row = {
        id: `PRC-${this.practitionerRows.length + 1}`,
        practitionerType: input.practitionerType,
        name: { en: input.fullNameEn, ar: input.fullNameAr },
        primarySpecialty: input.primarySpecialtyCode as string | undefined,
        specialties: [input.primarySpecialtyCode],
        branches: [...input.branchIds],
        status: DEV_ACTIVE as DevStatusChip,
      };
      this.practitionerRows.push(row);
      return ok(zPractitionerCreated, { practitioner: { ...row, licenseNo: input.licenseNo }, incomplete: [] });
    });
  }

  async assignSpecialty(practitionerId: string, specialtyCode: string) {
    const p = this.findPractitioner(practitionerId);
    if (!p.specialties.includes(specialtyCode)) p.specialties.push(specialtyCode);
  }

  async setPrimarySpecialty(practitionerId: string, specialtyCode: string) {
    const p = this.findPractitioner(practitionerId);
    if (!p.specialties.includes(specialtyCode)) p.specialties.push(specialtyCode);
    p.primarySpecialty = specialtyCode;
  }

  async revokeSpecialty(practitionerId: string, specialtyCode: string) {
    const p = this.findPractitioner(practitionerId);
    // Mirrors the server's 409: the primary cannot be removed, only replaced. A fixture that allowed it
    // would let the screen be built against a rule the real service does not have.
    if (p.primarySpecialty === specialtyCode) {
      throw new ApiError("http", "primary-specialty-cannot-be-revoked", 409, {
        title: "primary-specialty-cannot-be-revoked",
        type: "urn:hbmp:primary-specialty-required",
        detail: "Promote another specialty to primary first — a practitioner without one cannot be booked.",
      });
    }
    p.specialties = p.specialties.filter((s) => s !== specialtyCode);
  }

  async assignPractitionerBranch(practitionerId: string, branchId: string) {
    const p = this.findPractitioner(practitionerId);
    if (!p.branches.includes(branchId)) p.branches.push(branchId);
  }

  async revokePractitionerBranch(practitionerId: string, branchId: string) {
    const p = this.findPractitioner(practitionerId);
    p.branches = p.branches.filter((b) => b !== branchId);
  }

  async setPractitionerStatus(practitionerId: string, status: string, reason: string) {
    void reason;
    const p = this.findPractitioner(practitionerId);
    p.status = status === "Active" ? DEV_ACTIVE : status === "Suspended" ? DEV_SUSPENDED : DEV_INACTIVE;
  }

  /**
   * Availability is deliberately NOT given for every practitioner in the roster. PRC-3 (Mona Saleh) is a
   * fully-formed doctor with no open slot, so the join in `bookableDoctors` has a case where provider-service
   * says yes and emr says no — the case that decides whether the picker offers a dead end.
   */
  doctorAvailability(branchId?: string) {
    const all = [
      { doctorId: "PRC-1", branchId: "BR-DOK", openSlots: 6, nextSlotStart: "2026-07-30T09:00:00Z" },
      { doctorId: "PRC-1", branchId: "BR-MAA", openSlots: 3, nextSlotStart: "2026-07-31T11:30:00Z" },
      { doctorId: "PRC-2", branchId: "BR-NSR", openSlots: 2, nextSlotStart: "2026-07-30T13:15:00Z" },
      { doctorId: "PRC-5", branchId: "BR-OCT", openSlots: 4, nextSlotStart: "2026-07-30T08:30:00Z" },
    ];
    const rows = branchId ? all.filter((d) => d.branchId === branchId) : all;
    return this.gate(() => ok(z.array(zDoctorAvailability), rows), []);
  }

  // ---- Patient profile (Phase 20, design 39) --------------------------------------------------------------
  // The fixture deliberately carries ALL FOUR states at once — Visible, Restricted, Unavailable and
  // NotApplicable — because the three non-visible ones are the part of this screen most likely to be got
  // wrong, and a fixture that only shows happy-path sections is a fixture in which "restricted" and "broken"
  // and "empty" never get looked at side by side.
  patientProfile(beneficiaryId: string, sections?: ProfileSectionKey[]) {
    const all: ProfileSection[] = [
      {
        key: "header", state: "Visible" as const,
        data: {
          beneficiaryId, memberNo: "MRS-M-014882", displayName: "Amal Hassan", displayNameAr: "أمل حسن",
          ageBand: "30-39", sex: "F", status: "Active",
          statusCue: { label: "Active", icon: "check-circle", shape: "circle", tone: "positive" },
          branchName: "Nasr City", preferredLanguage: "ar",
          contact: { phone: "+20 100 000 0000", preferredChannel: "WhatsApp" },
          photoUrl: `/api/v1/patients/${beneficiaryId}/photo`,
        },
      },
      {
        key: "alerts", state: "Visible" as const,
        data: {
          allergies: [{ allergen: "Penicillin", reaction: "Rash", severity: "High" }],
          criticalFlags: [{ kind: "Critical", label: "Anticoagulated", tone: "critical" }],
        },
      },
      {
        key: "coverage", state: "Visible" as const,
        data: {
          payerName: "Mersal Foundation", policyNo: "POL-2026-0001", planLabel: "Gold", planVersion: 3,
          effectiveFrom: "2026-01-01", effectiveTo: "2026-12-31", waitingPeriodState: "Served",
          categories: [
            { category: "Pharmacy", annualLimit: 5000, consumed: 1200, remaining: 3800, costSharePercent: 10, costShareTier: "Tier1" },
            { category: "Dental", annualLimit: 2000, consumed: 0, remaining: 2000, costSharePercent: 20, costShareTier: "Tier2" },
          ],
        },
      },
      {
        key: "pastMedicalHistory", state: "Visible" as const,
        data: {
          conditions: [
            { system: "ICD-10", code: "E11.9", display: "Type 2 diabetes mellitus", clinicalStatus: "Active", onsetOn: "2021-03-14" },
            { system: "ICD-10", code: "I10", display: "Essential hypertension", clinicalStatus: "Active", onsetOn: "2022-11-02" },
          ],
          narrative: "Managed on metformin since 2021. Reports good adherence; last HbA1c 7.1% at the Nasr City clinic.",
          uploadedRecords: [
            { linkId: "doc-hist-1", documentClass: "Clinical", title: "Discharge summary — Al-Salam Hospital", documentDate: "2024-08-19" },
          ],
        },
      },
      {
        key: "encounters", state: "Visible" as const,
        data: {
          items: [
            { encounterRef: "ENC-2026-04412", occurredAt: "2026-07-02T09:00:00Z", branchName: "Nasr City", clinicianName: "Dr. S. Ibrahim", specialty: "Internal medicine", reason: "Diabetes follow-up", status: "Completed" },
            { encounterRef: "ENC-2026-04188", occurredAt: "2026-06-18T08:00:00Z", branchName: "Nasr City", clinicianName: "Dr. S. Ibrahim", specialty: "Internal medicine", reason: "Hypertension review", status: "Completed" },
            { encounterRef: "ENC-2026-04530", occurredAt: "2026-07-30T10:30:00Z", branchName: "Nasr City", clinicianName: "Dr. L. Aziz", specialty: "Endocrinology", reason: "Referral consultation", status: "Booked" },
          ],
        },
      },
      {
        key: "investigations", state: "Visible" as const,
        data: {
          items: [
            { orderRef: "ORD-2026-7741", lineId: "line-1", category: "Haematology", orderedOn: "2026-07-02T09:20:00Z", status: "Resulted", providerName: "Central Lab", resultSummary: "Hb 11.2 g/dL — mild anaemia", restricted: false },
            // Existence-only: the owning service never sent a value, and the row says why rather than looking
            // like a result that has not come back yet (design 37 §6).
            { orderRef: "ORD-2026-7802", lineId: "line-2", category: "Serology", orderedOn: "2026-07-22T11:00:00Z", status: "Resulted", providerName: "Central Lab", restricted: true, sensitivityLevel: "High" },
            { orderRef: "ORD-2026-7855", lineId: "line-3", category: "Chemistry", orderedOn: "2026-07-28T08:45:00Z", status: "Ordered", providerName: "Central Lab", restricted: false },
          ],
        },
      },
      {
        key: "prescriptions", state: "Visible" as const,
        data: {
          items: [
            { rxRef: "RX-2026-11204", drugDisplay: "Metformin 850mg tablet", status: "Dispensed", prescribedOn: "2026-07-02T09:10:00Z", dispensedOn: "2026-07-02T11:40:00Z", batchNo: "MTF-2291", expiryDate: "2027-04-30" },
            // A substitution AND an already-passed expiry: the two cells that need a cue rather than a bare value.
            { rxRef: "RX-2026-10877", drugDisplay: "Amlodipine 5mg tablet", status: "PartiallyDispensed", prescribedOn: "2026-06-18T08:05:00Z", dispensedOn: "2026-06-18T12:15:00Z", batchNo: "AML-1043", expiryDate: "2026-05-31", substitutedWith: "Amlodipine 5mg (generic, Tier 1)" },
            { rxRef: "RX-2026-11390", drugDisplay: "Insulin glargine 100 IU/mL", status: "Pending", prescribedOn: "2026-07-26T14:20:00Z" },
          ],
        },
      },
      {
        key: "authorizations", state: "Visible" as const,
        data: {
          items: [
            { authNo: "AUTH-2026-00841", serviceCategory: "Imaging", status: "Approved", requestedAt: "2026-07-20T10:00:00Z", decidedAt: "2026-07-21T08:30:00Z", validUntil: "2026-08-30", rationale: "Persistent lumbar pain with red-flag features; MRI indicated per protocol.", approvedAmount: 3200 },
            { authNo: "AUTH-2026-00902", serviceCategory: "Dental", status: "PendingInfo", requestedAt: "2026-07-27T09:15:00Z" },
          ],
        },
      },
      {
        key: "referrals", state: "Visible" as const,
        data: {
          items: [
            { referralRef: "REF-2026-0912", status: "Active", requestedSpecialty: "Endocrinology", createdAt: "2026-07-22T12:00:00Z" },
            { referralRef: "REF-2026-0744", status: "Completed", requestedSpecialty: "Ophthalmology", createdAt: "2026-05-11T09:30:00Z", loopClosedAt: "2026-06-04T14:10:00Z" },
          ],
        },
      },
      {
        key: "documents", state: "Visible" as const,
        data: {
          items: [
            { linkId: "doc-1", documentClass: "Identity", visibilityClass: "Administrative", title: "UNHCR registration card", documentDate: "2025-01-12", uploadedAt: "2025-01-13T09:00:00Z", status: "Verified", mayDownload: true },
            // Metadata visible, content gated — the row exists and offers no download control at all.
            { linkId: "doc-2", documentClass: "Clinical", visibilityClass: "Clinical", title: "Radiology report — lumbar MRI", documentDate: "2026-07-22", uploadedAt: "2026-07-22T16:40:00Z", status: "Active", mayDownload: false },
          ],
        },
      },
      {
        key: "notes", state: "Visible" as const,
        data: {
          items: [
            { noteId: "note-1", noteType: "Coordination", visibilityClass: "Administrative", body: "Member prefers afternoon appointments; transport arranged through the Nasr City branch.", authorDisplay: "H. Mostafa", createdAt: "2026-07-15T10:05:00Z", withheld: false, pinned: true },
            // Withheld: the note EXISTS and its content is not for this reader (19.3).
            { noteId: "note-2", noteType: "Clinical", visibilityClass: "Clinical", authorDisplay: "Dr. S. Ibrahim", createdAt: "2026-07-22T13:20:00Z", withheld: true, pinned: false },
          ],
        },
      },
      {
        key: "financial", state: "Visible" as const,
        data: {
          currency: "EGP", costShareOwed: 420, settlementStatus: "Pending",
          claims: [
            { claimNo: "CLM-2026-3391", serviceDate: "2026-07-02", billedAmount: 1800, approvedAmount: 1620, memberShare: 180, status: "Settled" },
            { claimNo: "CLM-2026-3502", serviceDate: "2026-07-22", billedAmount: 3200, approvedAmount: 2960, memberShare: 240, status: "Adjudicating" },
          ],
        },
      },
      {
        key: "caseManagement", state: "Visible" as const,
        data: {
          cases: [
            { caseId: "case-1", caseNo: "CASE-2026-0217", status: "Open", category: "ChronicCare", openedAt: "2026-05-04T08:00:00Z" },
          ],
          tasks: [
            { taskId: "task-1", title: "Confirm endocrinology follow-up booking", status: "Open", dueOn: "2026-07-10" },
            { taskId: "task-2", title: "Collect renewed UNHCR card scan", status: "Completed", dueOn: "2026-06-30" },
          ],
          escalations: [
            { escalationId: "esc-1", reason: "Insulin out of stock at branch pharmacy", status: "Escalated", raisedAt: "2026-07-26T15:00:00Z" },
          ],
        },
      },
      {
        key: "timeline", state: "Visible" as const,
        data: {
          items: [
            { at: "2026-07-02T11:40:00Z", eventType: "PrescriptionDispensed", visibilityClass: "Clinical", actorDisplay: "Pharmacy — Nasr City", summary: "RX-2026-11204 dispensed", sourceService: "pharmacy" },
            { at: "2026-07-26T09:12:00Z", eventType: "ProfileOpened", visibilityClass: "Access", actorDisplay: "R. Adel (reception)", summary: "Sections served: header, alerts, coverage", sourceService: "profile" },
            { at: "2026-07-21T08:30:00Z", eventType: "AuthorizationDecided", visibilityClass: "Clinical", actorDisplay: "Dr. S. Ibrahim", summary: "AUTH-2026-00841 approved", sourceService: "approvals" },
          ],
        },
      },
      {
        key: "callHistory", state: "Visible" as const,
        data: {
          level: "Full",
          items: [
            {
              callRef: "CALL-2026-004137", direction: "Outbound", startedAt: "2026-07-24T12:32:00Z",
              endedAt: "2026-07-24T12:38:12Z", durationSeconds: 372, branchCode: "Nasr City",
              agentDisplayName: "R. Adel", reasonCode: "RescheduleAppointment", outcome: "Resolved",
              summary: "Appointment APT-2026-8841 moved from 25 Jul to 30 Jul at the member's request; member confirmed the new slot on the call.",
              summaryEdited: false,
              linkedArtifacts: [{ type: "Appointment", ref: "APT-2026-8841", action: "Reschedule" }],
              copyText: "[Outbound] 2026-07-24 15:32 (6m 12s) · Nasr City · Agent: R. Adel\nMember: MRS-M-014882 · Ref: CALL-2026-004137\nReason: RescheduleAppointment · Outcome: Resolved\nAppointment APT-2026-8841 moved from 25 Jul to 30 Jul at the member's request; member confirmed the new slot on the call.",
            },
            {
              callRef: "CALL-2026-004102", direction: "Inbound", startedAt: "2026-07-11T08:05:00Z",
              endedAt: "2026-07-11T08:07:40Z", durationSeconds: 160, branchCode: "Nasr City",
              agentDisplayName: "M. Farid", reasonCode: "EligibilityEnquiry", outcome: "Resolved",
              summary: "Member asked whether dental is covered this year; confirmed remaining dental limit and how to book.",
              summaryEdited: true, linkedArtifacts: [],
              copyText: "[Inbound] 2026-07-11 11:05 (2m 40s) · Nasr City · Agent: M. Farid\nMember: MRS-M-014882 · Ref: CALL-2026-004102\nReason: EligibilityEnquiry · Outcome: Resolved\nMember asked whether dental is covered this year; confirmed remaining dental limit and how to book.",
            },
          ],
        },
      },
    ];

    /**
     * The three withheld states, on ONE beneficiary rather than permanently occupying three sections of
     * everyone's profile.
     *
     * They used to sit on investigations / encounters / referrals for every id, which demonstrated the states
     * beautifully and meant those three views could not be seen in the browser at all. Both things matter: the
     * states are a correctness requirement (design 39 §6) and eyeballing them is how a regression in their
     * treatment gets caught, but a view nobody can look at is a view nobody reviews. So `BEN-3` — Amina Yusuf,
     * the Suspended record in the search fixtures — answers with the withheld trio, and every other id answers
     * with all fifteen sections populated.
     */
    const withheld: Record<string, ProfileSection> = beneficiaryId !== WITHHELD_STATE_DEMO_ID ? {} : {
      // Restricted: the locked state, with the reason AND the way out.
      investigations: {
        key: "investigations", state: "Restricted", reasonCode: "sensitive-requires-grant",
        requestAccessAction: { kind: "report-access-request", href: `/api/v1/report-access-requests?beneficiaryId=${beneficiaryId}`, label: "Request access" },
      },
      // Unavailable: the owning service did not answer. NOT the same as empty — the user gets Retry.
      encounters: { key: "encounters", state: "Unavailable", reasonCode: "timeout" },
      // NotApplicable: nothing exists. A plain, calm "no records".
      referrals: { key: "referrals", state: "NotApplicable" },
    };

    const wanted = sections?.length ? new Set<string>(sections) : null;
    return this.gate(() =>
      ok(zPatientProfile, {
        beneficiaryId,
        servedAt: new Date().toISOString(),
        sections: all
          .map((s) => withheld[s.key] ?? s)
          .filter((s) => !wanted || wanted.has(s.key)),
      }),
    );
  }

  profileSummary(beneficiaryId: string) {
    return this.gate(() =>
      ok(zProfileExportSummary, {
        profile: {
          beneficiaryId,
          servedAt: new Date().toISOString(),
          sections: [{ key: "header", state: "Visible" as const, data: { beneficiaryId, displayName: "Amal Hassan", status: "Active", statusCue: { label: "Active", icon: "check-circle", shape: "circle", tone: "positive" } } }],
        },
        watermark: {
          viewerSubject: "dev-user",
          viewerRoles: "doctor",
          generatedAt: new Date().toISOString(),
          purpose: "profile-export",
        },
      }),
    );
  }

  copyCallSummaries(beneficiaryId: string, callRefs: string[]) {
    void beneficiaryId;
    return this.gate(() =>
      ok(zCopySummariesResult, {
        level: "Full",
        callRefs,
        copyText: callRefs
          .map((r) => `[Outbound] 2026-07-24 15:32 · Ref: ${r}`)
          .join("\n\n"),
      }),
    );
  }

  beneficiarySearch(query: { name?: string; status?: string }) {
    const all = [
      { id: "BEN-1", memberNo: "MRS-M-10231", givenName: "Omar", familyName: "Khaled", chip: { kind: "info" as const, label: loc("Pending", "قيد الانتظار") }, raw: "Pending", ids: [{ type: "NationalID", value: "•••2931", isPrimary: true }] },
      { id: "BEN-2", memberNo: "MRS-M-10555", givenName: "Salma", familyName: "Adel", chip: { kind: "ok" as const, label: loc("Active", "نشط") }, raw: "Active", ids: [{ type: "UNHCRNo", value: "801-•••45", isPrimary: true }] },
      { id: "BEN-3", memberNo: undefined, givenName: "Amina", familyName: "Yusuf", chip: { kind: "warn" as const, label: loc("Suspended", "موقوف") }, raw: "Suspended", ids: [{ type: "Passport", value: "A•••221", isPrimary: true }] },
      // The awkward states are the ones the status screen exists to make legible: a fraud-Blocked record
      // the desk must NOT be able to touch, and an Expired one whose only edge is renewal.
      { id: "BEN-4", memberNo: "MRS-M-10102", givenName: "Hassan", familyName: "Tariq", chip: { kind: "bad" as const, label: loc("Blocked", "محظور") }, raw: "Blocked", ids: [{ type: "UNHCRNo", value: "802-•••71", isPrimary: true }] },
      { id: "BEN-5", memberNo: "MRS-M-10077", givenName: "Layla", familyName: "Nasser", chip: { kind: "neu" as const, label: loc("Expired", "منتهٍ") }, raw: "Expired", ids: [{ type: "NationalID", value: "•••8843", isPrimary: true }] },
    ].filter((b) => (!query.name || (b.givenName + " " + b.familyName).toLowerCase().includes(query.name.toLowerCase())) && (!query.status || b.raw === query.status));
    return this.gate(
      () => ok(z.array(zBeneficiaryRow), all.map((b) => ({
        id: b.id, memberNo: b.memberNo, givenName: b.givenName, familyName: b.familyName,
        status: b.chip, statusRaw: b.raw, identifiers: b.ids,
      }))),
      [],
    );
  }
  registerBeneficiary(input: RegisterBeneficiaryInput) {
    void input;
    return this.gate(() => ok(zRegisterResult, { id: "BEN-NEW", memberNo: undefined, status: { kind: "info", label: loc("Pending", "قيد الانتظار") } }));
  }
  changeBeneficiaryStatus(id: string, toStatus: string, reason: string) {
    void reason;
    return this.gate(() => ok(zStatusChangeResult, { id, status: { kind: toStatus === "Active" ? "ok" : "warn", label: loc(toStatus, toStatus) } }));
  }

  // Registration approval worklist (US-003). The shapes the screen must make legible: an application ready to
  // approve, one mid-preparation, one bounced back for more information, and a legacy beneficiary with no
  // application at all. Enough rows, and enough SPREAD of date and officer, that search, the status filter,
  // sorting and the pager all do something visible in fixture mode — a fixture set of three makes every one of
  // those controls look broken.
  registrationWorklist() {
    return this.gate(() => ok(zRegistrationWorklistPage, { items: REGISTRATION_QUEUE, total: REGISTRATION_QUEUE.length }), {
      items: [],
      total: 0,
    });
  }
  registrationThread(id: string) {
    return this.gate(() => ok(z.array(zRegistrationThreadEntry), REGISTRATION_THREADS[id] ?? []), []);
  }
  replyToRegistration(id: string, body: string) {
    return this.gate(() => {
      const entry = {
        id: `THR-${id}-${(REGISTRATION_THREADS[id]?.length ?? 0) + 1}`,
        kind: "Reply" as const,
        decision: null,
        body,
        authorName: "Layla Hassan",
        authorRole: "beneficiary_mgmt",
        createdAt: "2026-07-31T09:15:00Z",
      };
      // Written back into the fixture so the modal shows the reply it just posted, exactly as a live thread
      // would — a reply that vanishes on reload is the bug this screen exists to avoid.
      REGISTRATION_THREADS[id] = [...(REGISTRATION_THREADS[id] ?? []), entry];
      return ok(zRegistrationThreadEntry, entry);
    });
  }
  beneficiary(id: string) {
    // Reuses the approval queue's people, so the detail and the worklist agree about who exists.
    const hit = REGISTRATION_QUEUE.find((r) => r.beneficiary.id === id)?.beneficiary;
    return this.gate(() => ok(zBeneficiaryRow, hit ?? {
      id, givenName: "Amina", familyName: "Yusuf",
      status: REG_PENDING, statusRaw: "Pending", identifiers: [],
      birthDate: "1992-04-11", sex: "Female", nationalityCode: "SY", caseNo: "CASE-2211",
      contacts: [{ type: "Phone", value: "+20 100 ••• 4412", isPrimary: true }],
    }));
  }
  updateBeneficiary(_id: string, edit: BeneficiaryEdit) {
    // Echoes the field names back, which is what the screen announces — "3 fields updated" is the confirmation
    // an operator needs, and inventing a fixed answer would hide a form that sent nothing.
    return this.gate(() => ({ changed: Object.keys(edit) }));
  }
  beneficiaryDocuments(beneficiaryId: string) {
    return this.gate(() => ok(z.array(zBeneficiaryDocument), REGISTRATION_DOCUMENTS[beneficiaryId] ?? []), []);
  }
  createRegistration() {
    return this.gate(() => undefined);
  }
  setRegistrationChecks() {
    return this.gate(() => undefined);
  }
  decideRegistration(_id: string, decision: "Approve" | "RequestInfo" | "Reject") {
    return this.gate(() =>
      ok(zRegistrationDecisionResult, decision === "Approve"
        ? { status: "Active", memberNo: "MRS-M-2026-000418" }
        : { status: decision === "Reject" ? "Rejected" : "InfoRequested" }),
    );
  }
  async decideRegistrations(ids: readonly string[], decision: "Approve" | "RequestInfo" | "Reject") {
    const outcomes: BulkDecisionOutcome[] = [];
    for (const id of ids) {
      // One row refuses, so the screen's partial-result path is exercised in fixture mode rather than only
      // against a live server. A bulk action that has never been seen to half-fail is a bulk action whose
      // failure branch has never been read.
      //
      // REG-9 deliberately: its guards BOTH hold, so the client lets it through and the server still says no.
      // That is the real shape of a bulk refusal — somebody unbound the coverage between the page loading and
      // the supervisor pressing confirm — and picking a row the client would have blocked anyway would
      // exercise nothing.
      const blocked = decision === "Approve" && id === "REG-9";
      outcomes.push(blocked
        ? { registrationId: id, ok: false, error: "cannot approve: no policy/coverage is bound" }
        : { registrationId: id, ok: true, memberNo: decision === "Approve" ? "MRS-M-2026-000418" : undefined });
    }
    return outcomes;
  }

  adminMasterData() {
    return this.gate(
      () => ok(z.array(zMasterDataVersion), [
        { id: "MDV-1", system: "ICD10", code: "E11.9", versionNo: 2, retired: false, effectiveFrom: "2026-01-01T00:00:00Z", rationale: "Annual ICD refresh" },
        { id: "MDV-2", system: "ATC", code: "A10BA02", versionNo: 1, retired: false, effectiveFrom: "2026-01-01T00:00:00Z", rationale: "Initial load" },
      ]),
      [],
    );
  }
  adminSystemConfig() {
    return this.gate(
      () => ok(z.array(zSystemConfigEntry), [
        { id: "CFG-1", tenantId: "*", key: "session.timeout_minutes", type: "Duration", value: "15", versionNo: 1 },
        { id: "CFG-2", tenantId: "11111111-1111-1111-1111-111111111111", key: "approvals.sla_hours", type: "Whole", value: "24", versionNo: 3 },
      ]),
      [],
    );
  }

  // ---- User & access model (Phase 21.6, design 40) -------------------------------------------------------
  //
  // The fixtures deliberately include the awkward states, because those are the ones the screens exist to
  // make legible: a suspended membership, a lapsed override, an open-ended branch grant, a cap already
  // exceeded, and a feature nobody has configured either way.

  memberships() {
    return this.gate(() => ok(z.array(zMembershipRow), DEV_MEMBERSHIPS), []);
  }

  membership(membershipId: string) {
    const row = DEV_MEMBERSHIPS.find((m) => m.membershipId === membershipId) ?? DEV_MEMBERSHIPS[0];
    return this.gate(() =>
      ok(zMembershipDetail, {
        ...row,
        providerId: null,
        homeBranchId: "b1000000-0000-0000-0000-000000000001",
        overrides: [
          {
            id: "OV-1", scope: "orders:read", effect: "Deny", reason: "Under investigation — access narrowed pending review",
            grantedBy: "admin@mersal", validUntil: null, expired: false,
          },
          {
            id: "OV-2", scope: "reports:export", effect: "Allow", reason: "Covering the monthly extract while N. is on leave",
            grantedBy: "admin@mersal", validUntil: "2026-08-31T00:00:00Z", expired: false,
          },
          // Lapsed on purpose: the screen must show it as expired rather than hide it, so an administrator
          // can explain why this person lost the key overnight.
          {
            id: "OV-3", scope: "claims:submit", effect: "Allow", reason: "Ramadan surge cover",
            grantedBy: "admin@mersal", validUntil: "2026-04-30T00:00:00Z", expired: true,
          },
        ],
      }),
    );
  }

  setMembershipOverride() {
    return this.gate(() => undefined);
  }

  effectiveAccess(membershipId: string) {
    return this.gate(() =>
      ok(zEffectiveAccess, {
        membershipId,
        keys: [
          { key: "encounters:read", source: "role", via: "doctor" },
          { key: "prescriptions:write", source: "role", via: "doctor" },
          { key: "reports:export", source: "override", via: "admin@mersal", reason: "Covering the monthly extract while N. is on leave" },
          { key: "labs:read", source: "role", via: "doctor", deprecated: true, replacedBy: "investigations:read" },
          { key: "orders:read", source: "denied", via: "admin@mersal", reason: "Under investigation — access narrowed pending review" },
        ],
      }),
    );
  }

  branchScopeGrants() {
    return this.gate(
      () =>
        ok(z.array(zBranchScopeGrant), [
          {
            grantId: "G-1", branchId: "b1000000-0000-0000-0000-000000000001", isHome: true,
            validFrom: "2026-01-01", validUntil: null, grantedBy: "admin@mersal", grantedReason: "Home branch",
          },
          {
            grantId: "G-2", branchId: "b1000000-0000-0000-0000-000000000002", isHome: false,
            validFrom: "2026-10-01", validUntil: "2026-10-31", grantedBy: "admin@mersal",
            grantedReason: "Covering Alexandria for October",
          },
        ]),
      [],
    );
  }

  accessSessions() {
    return this.gate(
      () =>
        ok(z.array(zAccessSession), [
          { sessionId: "S-1", device: "Chrome on Windows", createdAt: "2026-07-28T06:10:00Z", lastSeenAt: "2026-07-28T09:31:00Z", current: true },
          { sessionId: "S-2", device: "Safari on iPhone", createdAt: "2026-07-20T18:02:00Z", lastSeenAt: "2026-07-26T07:44:00Z", current: false },
        ]),
      [],
    );
  }

  revokeAccessSession() {
    return this.gate(() => undefined);
  }

  programEnablement(tenant: string) {
    return this.gate(() =>
      ok(zProgramEnablement, {
        tenantId: tenant,
        features: [
          { key: "approvals", enabled: true, configured: true, changedBy: "programme@mersal", changedAt: "2026-05-01T09:00:00Z" },
          { key: "callcentre", enabled: true, configured: true, changedBy: "programme@mersal", changedAt: "2026-06-11T09:00:00Z" },
          { key: "claims", enabled: false, configured: true, changedBy: "programme@mersal", changedAt: "2026-02-14T09:00:00Z" },
          // Never configured either way — shown as its own state, not folded into "off".
          { key: "interop", enabled: false, configured: false, changedBy: null, changedAt: null },
        ],
        limits: [
          { key: "active_users", maxValue: 50, currentUsage: 42, changedBy: "programme@mersal", changedAt: "2026-05-01T09:00:00Z" },
          // Already over its cap: legitimate after a cap is tightened, and the screen must say so plainly
          // rather than render a bar that silently overflows.
          { key: "active_provider_users", maxValue: 10, currentUsage: 12, changedBy: "programme@mersal", changedAt: "2026-07-01T09:00:00Z" },
          { key: "monthly_extracts", maxValue: 20, currentUsage: null, changedBy: null, changedAt: null },
          { key: "storage_mb", maxValue: null, currentUsage: null, changedBy: null, changedAt: null },
        ],
      }),
    );
  }

  setProgramFeature() {
    return this.gate(() => undefined);
  }

  setProgramLimit() {
    return this.gate(() => undefined);
  }
}

/** One identity holding two memberships with different authority — invariant 1, made visible. */
const DEV_MEMBERSHIPS = [
  {
    membershipId: "11111111-1111-1111-1111-111111111111",
    userId: "aaaaaaaa-1111-1111-1111-111111111111",
    username: "s.ibrahim", displayName: "Sara Ibrahim",
    tenantId: "mersal", status: { kind: "ok" as const, label: loc("Active", "نشِطة") },
    roles: [{ name: "doctor", level: 3 }], level: 3, isPlatformAdmin: false,
    overrideCount: 3, expiredOverrideCount: 1,
    activatedAt: "2026-01-15T08:00:00Z", endedAt: null,
  },
  {
    // Same person, different organisation, genuinely different authority — never a blended principal.
    membershipId: "22222222-2222-2222-2222-222222222222",
    userId: "aaaaaaaa-1111-1111-1111-111111111111",
    username: "s.ibrahim", displayName: "Sara Ibrahim",
    tenantId: "partner-ngo", status: { kind: "ok" as const, label: loc("Active", "نشِطة") },
    roles: [{ name: "provider_admin", level: 2 }], level: 2, isPlatformAdmin: false,
    overrideCount: 0, expiredOverrideCount: 0,
    activatedAt: "2026-03-02T08:00:00Z", endedAt: null,
  },
  {
    membershipId: "33333333-3333-3333-3333-333333333333",
    userId: "bbbbbbbb-2222-2222-2222-222222222222",
    username: "m.farouk", displayName: "Mohamed Farouk",
    tenantId: "mersal", status: { kind: "warn" as const, label: loc("Suspended", "موقوفة") },
    roles: [{ name: "pharmacist", level: 4 }], level: 4, isPlatformAdmin: false,
    overrideCount: 0, expiredOverrideCount: 0,
    activatedAt: "2026-02-01T08:00:00Z", endedAt: null,
  },
];

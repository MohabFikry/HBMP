import { z } from "zod";
import {
  zApprovalItem,
  zAdjudicationRow,
  zClaimDetail,
  zClaimAdjustment,
  zClaimDecisionResult,
  zRetrospectiveItem,
  zAuthorizationItem,
  zInvestigationOrder,
  zApprovalReview,
  zConsumeResult,
  zDecisionResult,
  zDispenseResult,
  zBeneficiaryVerification,
  zEligibilitySearch,
  zEligibilityResult,
  zEncounter,
  zEncounterDiagnosis,
  zIcdRef,
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
  type EscalationState,
  type CaseState,
  type TaskState,
  zNotification,
  zMarkAllReadResult,
  zMarkReadResult,
  zRoleBinding,
  zTenantSummary,
  zSodConflict,
  zSodViolation,
  zAccessReviewCampaign,
  zAccessReviewItem,
  zAppointmentRow,
  zBookableClinic,
  zTimelineStep,
  zBookableSlot,
  zBookingResult,
  zBreakGlassGrant,
  zMasterDataVersion,
  zMasterDataAsOf,
  zDocumentValidityView,
  zApprovalRuleList,
  zAutoDecisionSwitch,
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
  zNetworkMetrics,
  zProviderMetrics,
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
  zWaitingTicket,
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
  zPrescribableDrug,
  zCptRef,
  zOrderValidationResult,
  zValidityExtensionResult,
  zValidityPolicyView,
  zInvestigationOrderResult,
  zValidationResult,
  zPrescriptionSubmitResult,
  zAllergenOption,
  zAllergyRecord,
  zMedicationHistoryRow,
  zNoteAddendum,
  zLineNote,
  zChronicAmendPreview,
  zWithdrawResult,
  zMemberClinicalRecord,
  zTatSummary,
  zManualAuthResult,
  zEmergencyResult,
  type ManualAuthInput,
  zReportView,
  zServiceUseView,
  zSlaBreachView,
  zClaimsCostView,
  type VitalInput,
  zExportResult,
  zFinancialSummary,
  zSettlement,
  zSettlementPage,
  zOutOfStockResult,
  zUtilizationView,
  type ConsumeRequest,
  type DecisionRequest,
  type DispenseRequest,
  type RxPricing,
  type ExportRequest,
  type Localized,
  type PlaceOrderRequest,
  type PrescribeRequest,
  type IdentityUser,
  type RoleCatalogEntry,
  type ScopeCatalogEntry,
  type RoleScopeGrant,
  type ReportAccessRequestRow,
  zAmendReasonOption,
  zQuantityPreview,
  canonicaliseConfigValue,
} from "@mersal/contracts";
import type { BeneficiaryEdit, BookingRequest, BulkDecisionOutcome, DiagnosisRank, MasterDataEdit, SystemConfigEdit, SetDocumentValidity, SaveApprovalRule, ApprovalRule, SetAutoDecision} from "@mersal/contracts";
import type { CptSection, InvestigationDraftLine, InvestigationOrderType, OrderAcknowledgement, OrderFinding, ValidityExtensionRequest } from "@mersal/contracts";
import type { PrescriptionDraftLine, LineAcknowledgement, Finding, PrescriptionKind } from "@mersal/contracts";
import type { WithdrawResult } from "@mersal/contracts";
import type { AddAllergyRequest, AllergenOption, BloodGroup, MemberClinicalRecord } from "@mersal/contracts";
import type { AddMedicationHistoryRequest, MedicationHistoryRow, MedicationStatus } from "@mersal/contracts";
import type { NoteAddendum, LineNote, LineNoteKind, NoteVisibility } from "@mersal/contracts";
import type { InvestigationOrder, OrderPricing, SubstitutionRequest } from "@mersal/contracts";
import type { ApiClient, ApiScenario, ApprovalQueueFilter } from "./client";
import type { ApprovalItem, ClaimDecisionRequest, Period, RetrospectiveReviewInput } from "@mersal/contracts";
import type { GenerateSettlementRequest, Settlement } from "@mersal/contracts";
import { sectionStateFor } from "../dev/profileSectionMatrix";
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
 * The settlement lifecycle's four chips, and the two staff ids the SoD fixture needs.
 *
 * <p>Four DISTINCT chips. The HTTP client wrote the literal string `"ok"` into every settlement's status, so
 * Draft, Submitted, Approved and Paid rendered identically — and, because `zStatus` is an object and `"ok"`
 * is a string, threw on every call besides. `Approved` is `info` rather than `ok`: approval authorises a
 * payment, it does not complete one, and `Paid` is the only state that gets the affirmative chip.</p>
 */
const SETTLEMENT_CHIP: Record<string, { kind: "ok" | "info" | "part" | "warn" | "bad" | "neu"; label: Localized }> = {
  draft: { kind: "neu", label: loc("Draft", "مسودة") },
  submitted: { kind: "warn", label: loc("Submitted", "مُقدَّمة") },
  approved: { kind: "info", label: loc("Approved", "معتمدة") },
  paid: { kind: "ok", label: loc("Paid", "مدفوعة") },
};

/** The dev principal. `DEV_APPROVER` is somebody else, which is the entire point of the pair. */
// Matches `devAuthClient`'s `dev-${primaryRole}`, so the SoD sentence on the Settlements screen
// actually fires in development and in the tests. A fixture whose submitter is nobody the session
// could ever be cannot exercise the rule it exists to demonstrate.
const DEV_SUBJECT = "dev-finance";
const DEV_APPROVER = "dev-finance-manager";

const DEV_SETTLEMENTS = [
  {
    id: "stl-2026-000007",
    settlementNo: "STL-2026-000007",
    providerRef: "PRV-•••301",
    providerName: loc("Nile Imaging Center", "مركز النيل للأشعة"),
    periodStart: "2026-06-01",
    periodEnd: "2026-06-30",
    currency: "EGP",
    total: 58500,
    status: SETTLEMENT_CHIP.submitted,
    state: "submitted" as const,
    // Submitted BY THE DEV PRINCIPAL, so the Approve button on this row is the one the screen must withhold
    // and explain. A fixture in which nobody is ever the submitter cannot exercise the rule.
    submittedBy: DEV_SUBJECT,
    approvedBy: null,
    lines: [
      { serviceCode: "70553", serviceLine: loc("Imaging", "أشعة"), deliveredQty: 9, agreedUnitPrice: 6500, lineTotal: 58500, priceSource: "Contract" as const },
    ],
  },
  {
    id: "stl-2026-000006",
    settlementNo: "STL-2026-000006",
    providerRef: "PRV-•••118",
    providerName: loc("Cairo Community Pharmacy", "صيدلية القاهرة"),
    periodStart: "2026-06-01",
    periodEnd: "2026-06-30",
    currency: "EGP",
    total: 12400,
    status: SETTLEMENT_CHIP.approved,
    state: "approved" as const,
    submittedBy: DEV_APPROVER,
    approvedBy: DEV_SUBJECT,
    lines: [
      { serviceCode: "J01CA04", serviceLine: loc("Pharmacy", "صيدلية"), deliveredQty: 231, agreedUnitPrice: 53.68, lineTotal: 12400, priceSource: "Contract" as const },
    ],
  },
  {
    id: "stl-2026-000005",
    settlementNo: "STL-2026-000005",
    providerRef: "PRV-•••204",
    providerName: loc("Maadi Diagnostics Lab", "معمل المعادي للتحاليل"),
    periodStart: "2026-05-01",
    periodEnd: "2026-05-31",
    currency: "EGP",
    total: 9120,
    status: SETTLEMENT_CHIP.draft,
    state: "draft" as const,
    submittedBy: null,
    approvedBy: null,
    // A draft carrying an UNPRICED line. The reviewer about to submit this is the person the price-source
    // column exists for: two of these three numbers are the contract's and one is a floor this platform
    // inferred because no tariff names the code.
    lines: [
      { serviceCode: "80053", serviceLine: loc("Lab", "مختبر"), deliveredQty: 60, agreedUnitPrice: 106, lineTotal: 6360, priceSource: "Contract" as const },
      { serviceCode: "84443", serviceLine: loc("Lab", "مختبر"), deliveredQty: 26, agreedUnitPrice: 106.15, lineTotal: 2760, priceSource: "ObservedFloor" as const },
    ],
  },
];

/** The claim / line / bucket vocabularies, resolved once. Each key is a real enum member (`ClaimStatus`,
 *  `ClaimLineStatus`, `ReconBucket`) — the previous fixture invented three that were not. */
const CLAIM_STATUS_CHIP: Record<string, { kind: "ok" | "info" | "part" | "warn" | "bad" | "neu"; label: Localized }> = {
  Draft: { kind: "neu", label: loc("Draft", "مسودة") },
  Submitted: { kind: "info", label: loc("Submitted", "مُقدّمة") },
  UnderAdjudication: { kind: "info", label: loc("Under adjudication", "قيد البتّ") },
  PendingInfo: { kind: "warn", label: loc("Awaiting information", "بانتظار معلومات") },
  ClinicalReview: { kind: "warn", label: loc("Clinical review", "مراجعة سريرية") },
  Approved: { kind: "ok", label: loc("Approved", "معتمدة") },
  PartiallyApproved: { kind: "part", label: loc("Partially approved", "موافقة جزئية") },
  Denied: { kind: "bad", label: loc("Denied", "مرفوضة") },
  Settled: { kind: "ok", label: loc("Settled", "مُسوّاة") },
  Appealed: { kind: "warn", label: loc("Appealed", "مستأنَفة") },
  Void: { kind: "neu", label: loc("Void", "ملغاة") },
};
const CLAIM_LINE_CHIP: Record<string, { kind: "ok" | "info" | "part" | "warn" | "bad" | "neu"; label: Localized }> = {
  Pending: { kind: "info", label: loc("Pending", "معلّق") },
  Approved: { kind: "ok", label: loc("Approved", "معتمد") },
  PartiallyApproved: { kind: "part", label: loc("Partially approved", "موافقة جزئية") },
  Denied: { kind: "bad", label: loc("Denied", "مرفوض") },
  Adjusted: { kind: "warn", label: loc("Adjusted", "معدَّل") },
  Void: { kind: "neu", label: loc("Void", "ملغى") },
};
const RECON_CHIP: Record<string, { kind: "ok" | "info" | "part" | "warn" | "bad" | "neu"; label: Localized }> = {
  Matched: { kind: "ok", label: loc("Matched", "مطابقة") },
  PriceVariance: { kind: "warn", label: loc("Price variance", "فرق سعر") },
  QuantityVariance: { kind: "warn", label: loc("Quantity variance", "فرق كمية") },
  BilledNotDelivered: { kind: "bad", label: loc("Billed, not delivered", "فوترة بلا تنفيذ") },
  DeliveredNotBilled: { kind: "info", label: loc("Delivered, not billed", "تنفيذ بلا فوترة") },
  Duplicate: { kind: "bad", label: loc("Duplicate", "مكرّرة") },
};

/** The masterdata allergen seed (its migration 0002), abbreviated to the categories the picker groups by. */
/** 32.2 — the catalogue names searchDrugs answers with, keyed by id, so the fixture resolves a name the same
 *  way the server does rather than inventing one. */
const DEV_DRUG_NAMES: Record<string, string> = {
  "d-amox-500": "Amoxicillin 500mg caps",
  "d-amox-250": "Amoxicillin 250mg caps",
  "d-metf-500": "Metformin 500mg",
  "d-warf-5": "Warfarin 5mg",
  "d-sjw": "St John's Wort 300mg",
};

const DEV_ALLERGENS: AllergenOption[] = [
  { allergenId: "alg-penicillin", code: "ALG-PENICILLIN", name: "Penicillins", category: "Drug" },
  { allergenId: "alg-sulfa", code: "ALG-SULFA", name: "Sulfonamides", category: "Drug" },
  { allergenId: "alg-nsaid", code: "ALG-NSAID", name: "NSAIDs", category: "Drug" },
  { allergenId: "alg-cephalo", code: "ALG-CEPHALO", name: "Cephalosporins", category: "Drug" },
  { allergenId: "alg-iodine", code: "ALG-IODINE", name: "Iodine / Contrast media", category: "Drug" },
  { allergenId: "alg-peanut", code: "ALG-PEANUT", name: "Peanuts", category: "Food" },
  { allergenId: "alg-shellfish", code: "ALG-SHELLFISH", name: "Shellfish", category: "Food" },
  { allergenId: "alg-latex", code: "ALG-LATEX", name: "Latex", category: "Environmental" },
];

/**
 * A handful of real ICD-10 codes for the encounter workspace's assessment picker. Deliberately a short list
 * of PRESENTING complaints a general clinic actually sees, not a random slice of the catalogue: the demo is
 * there to show the search narrowing to something a doctor would plausibly pick.
 */
const DEV_ICD = [
  { code: "J01.90", title: "Acute sinusitis, unspecified" },
  { code: "J06.9", title: "Acute upper respiratory infection, unspecified" },
  { code: "J20.9", title: "Acute bronchitis, unspecified" },
  { code: "E11.9", title: "Type 2 diabetes mellitus without complications" },
  { code: "I10", title: "Essential (primary) hypertension" },
  { code: "K21.9", title: "Gastro-oesophageal reflux disease without oesophagitis" },
  { code: "M54.5", title: "Low back pain" },
  { code: "R51", title: "Headache" },
];

/**
 * Which CPT section a code belongs to — the dev mirror of masterdata's `CptSections`.
 *
 * Ranges only, no lookup table, because that is what the service does: a section is a pure function of the
 * code. Kept deliberately in step with the server's regexes; the fixture exists to behave like the thing it
 * stands in for, and a demo that sections codes differently teaches the wrong thing about the tabs.
 */
function devCptSection(code: string): CptSection {
  if (!/^\d{5}$/.test(code)) return "Other";
  const n = Number(code);
  if (n < 2000) return "Anesthesia";
  if (n < 70000) return "Surgery";
  if (n < 80000) return "Imaging";
  if (n < 88000) return "Laboratory";
  if (n < 89000) return "Pathology";
  if (n < 90000) return "Laboratory";
  if (n >= 99200 && n < 99500) return "EvaluationAndManagement";
  return "Medicine";
}

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
/**
 * The demo plan's cost share: an EGP 50 deductible still to meet, then 20% coinsurance.
 *
 * <p><b>The deductible is here on purpose.</b> A flat percentage would make the member's share linear in the
 * amount, and a screen that scaled the whole-prescription figure by "7 of 14" would then look correct in
 * development and be wrong against the real engine — `libs/money` runs a deductible before a copay before
 * coinsurance, so half a prescription does not cost half the share. The fixture models the same shape so the
 * dev counter reproduces the behaviour the server has rather than a friendlier version of it.</p>
 */
function devCostShare(amount: number): { member: number; payer: number } {
  const deductible = Math.min(amount, 50);
  const member = Number((deductible + (amount - deductible) * 0.2).toFixed(2));
  return { member, payer: Number((amount - member).toFixed(2)) };
}

/** The value of what is about to be handed over / performed, from a line-id → quantity basis. */
function devBasis(
  lines: { id: string; unit: number | null; whole: number }[],
  now: Record<string, number> | undefined,
): { amount: number; unpriced: boolean; onNow: boolean } {
  if (!now) return { amount: 0, unpriced: false, onNow: false };
  let amount = 0;
  let unpriced = false;
  for (const l of lines) {
    const q = now[l.id] ?? 0;
    if (q <= 0) continue;
    if (l.unit === null) unpriced = true;
    else amount += l.unit * q;
  }
  return { amount: Number(amount.toFixed(2)), unpriced, onNow: amount > 0 };
}

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
    return this.gate<ReturnType<typeof this.buildHits>>(
      () => this.buildHits(query),
      ok(zEligibilitySearch, { hits: [], truncated: false }),
    );
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
    return ok(zEligibilitySearch, {
      hits: all.filter((h) => h.name.en.toLowerCase().includes(q) || h.id.toLowerCase().includes(q) || q.length >= 2),
      // The fixture holds two members, so it can never fill a 25-row page. Pinned false rather than left
      // out: a fixture that reported `truncated` on a two-row list would exercise the warning banner and
      // teach the dev portal a behaviour the service does not have.
      truncated: false,
    });
  }
  /**
   * 33.9 — the fixture's members as the desk meets them: an identifier they carry, and a name.
   *
   * <p>`buildHits` above returns EVERYTHING for any two-character query, which is the defect this replaces
   * written down as a fixture — and it is left alone, because the search surface it feeds (booking, the call
   * centre) is a search and is meant to be broad. What the eligibility screen needed was never a search.</p>
   */
  private static readonly VERIFIABLE = [
    {
      id: "MRS-M-10231", given: "Amal", family: "Hassan", name: loc("Amal Hassan", "أمل حسن"),
      identifiers: ["MRS-M-10231", "29801011234567", "UNHCR-284-11902"], cardNumber: "MRS-M-10231",
      status: { kind: "ok" as const, label: loc("Active", "نشط") }, bookable: true,
    },
    {
      id: "MRS-M-10555", given: "Yusuf", family: "Haddad", name: loc("Yusuf Haddad", "يوسف حداد"),
      identifiers: ["MRS-M-10555", "29505200098765"], cardNumber: "MRS-M-10555",
      status: { kind: "warn" as const, label: loc("Suspended", "موقوف") }, bookable: false,
    },
  ];

  verifyBeneficiary(identifier: string, name: string) {
    return this.gate(() => {
      const id = identifier.trim();
      const terms = name.trim().toLowerCase().split(/[\s-]+/).filter(Boolean);
      // Mirrors the service: at least two characters per term, and EVERY term must prefix a name token.
      if (terms.length === 0 || terms.some((t) => t.length < 2))
        return ok(zBeneficiaryVerification, { verified: false, reason: "name-too-short" });

      const m = DevApiClient.VERIFIABLE.find((v) => v.identifiers.includes(id));
      if (!m) return ok(zBeneficiaryVerification, { verified: false, reason: "not-found" });

      const tokens = `${m.given} ${m.family}`.toLowerCase().split(/[\s-]+/).filter(Boolean);
      const agrees = terms.every((t) => tokens.some((k) => k.startsWith(t)));
      return agrees
        ? ok(zBeneficiaryVerification, {
            verified: true,
            hit: { id: m.id, name: m.name, cardNumber: m.cardNumber, status: m.status, bookable: m.bookable },
          })
        : ok(zBeneficiaryVerification, { verified: false, reason: "name-mismatch" });
    });
  }

  /**
   * 32.6 — the two scopes, both present in the fixture.
   *
   * <p>A fixture that always quoted a copay is part of why the real client's missing one went unnoticed for
   * so long: the dev portal showed "10%" on a code path that could not produce a number against the service.
   * So the no-category case here returns an explicitly UNKNOWN cost share carrying its reason, and only the
   * category case quotes.</p>
   */
  checkEligibility(beneficiaryId: string, benefitCategory?: string) {
    const category = benefitCategory?.trim() || undefined;
    return this.gate(() =>
      ok(zEligibilityResult, {
        verdict: "eligible",
        scope: category ? "benefit" : "membership",
        benefitCategory: category ?? null,
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
          annualCapRemaining: 8400,
        },
        costShare: category
          ? { known: true, tierCode: "IN", copayPercent: 10, copayFixed: null, coinsurancePercent: null }
          : {
              known: false,
              why: loc(
                "No benefit category was chosen, so no copay can be quoted. This is not a report that the "
                  + "member pays nothing.",
                "لم تُختَر فئة منفعة، لذا لا يمكن تحديد المساهمة. وهذا ليس تأكيداً بأن المستفيد لا يدفع شيئاً.",
              ),
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
          // A SUBJECT ID here, not a name — because that is what the server sends. This fixture used to put
          // "Nada Reception" in `noteBy`, so the note dialog looked correct in fixture mode and rendered a raw
          // uuid against the real API. A fixture that is kinder than the server hides exactly the defect it
          // ought to surface first.
          noteBy: r.note ? "c18b985c-cc5f-42eb-8b79-e41b7b84f975" : null,
          noteByName: r.note ? "Nada Reception" : null,
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
    // Four on the book, of which one arrived, one never did and one rang ahead — so the fixture exercises
    // all four cards, and `total` is visibly not the sum of the three named states.
    return this.gate(() => ok(zAppointmentCounts, { total: 4, checkedIn: 1, noShow: 1, cancelled: 1 }));
  }
  cancelAppointment(appointmentId: string, _reason: string) {
    void _reason;
    return this.gate(() => ok(zCheckInResult, {
      id: appointmentId,
      status: { kind: "neu", label: loc("Cancelled", "ملغى") },
    }));
  }
  async updateAppointmentNote(_appointmentId: string, _note: string) { void _appointmentId; void _note; }

  // ---- 30.6 amend / cancel a signed line (design 46 §1-§3) ----------------------------------------------
  //
  // The reason list mirrors the SEEDED vocabulary, filtered by applies_to exactly as the endpoint does — so
  // the demo picker offers a drug-specific reason on a prescription and never on a lab order. A dev fixture
  // that offered all eight everywhere would let a filtering bug reach production looking fine here.
  amendmentReasons(kind: "order" | "prescription") {
    const all = [
      { code: "PrescribingError", nameEn: "Prescribing error", nameAr: "خطأ في الوصف", scope: "All" },
      { code: "DoseCorrection", nameEn: "Dose correction", nameAr: "تصحيح الجرعة", scope: "Prescription" },
      { code: "PatientDeclined", nameEn: "Patient declined", nameAr: "رفض المريض", scope: "All" },
      { code: "ClinicalChange", nameEn: "Clinical change", nameAr: "تغير الحالة السريرية", scope: "All" },
      { code: "Duplicate", nameEn: "Duplicate", nameAr: "مكرر", scope: "All" },
      { code: "DrugUnavailable", nameEn: "Drug unavailable", nameAr: "الدواء غير متوفر", scope: "Prescription" },
      { code: "NotEligible", nameEn: "Patient not eligible", nameAr: "المريض غير مؤهل", scope: "All" },
      { code: "Other", nameEn: "Other", nameAr: "أخرى", scope: "All" },
    ];
    const scope = kind === "prescription" ? "Prescription" : "Order";
    return this.gate(() => ok(z.array(zAmendReasonOption),
      all.filter((r) => r.scope === "All" || r.scope === scope)
         .map(({ code, nameEn, nameAr }) => ({ code, nameEn, nameAr }))));
  }

  async cancelOrderLine(_o: string, _l: string, _c: string, _t?: string) { void _o; void _l; void _c; void _t; }
  async amendOrderLine(_o: string, _l: string, _q: number, _c: string, _t?: string) {
    void _o; void _l; void _q; void _c; void _t;
  }
  async cancelPrescriptionLine(_r: string, _l: string, _c: string, _t?: string) { void _r; void _l; void _c; void _t; }
  async amendPrescriptionLine(_r: string, _l: string, _q: number, _c: string, _t?: string) {
    void _r; void _l; void _q; void _c; void _t;
  }
  /**
   * 30.6 — the fixture answers PARTIAL success, not a clean one.
   *
   * A fixture that always reports "all withdrawn" is a fixture in which the branch the doctor most needs to
   * see — one line already dispensed and therefore still live — can never be rendered, in the demo build or
   * in any test that runs against it. That is exactly how the encounter tabs shipped a table nothing could
   * be withdrawn from: the fixture never disagreed with the happy path.
   */
  async withdrawPrescription(_r: string, _c: string, _t?: string): Promise<WithdrawResult> {
    void _c; void _t;
    const rx = (await this.prescriptionsMine()).find((x) => x.id === _r);
    const lines = (rx?.lines ?? []).map((l) => ({
      label: l.drug ? l.drug.en : "—",
      withdrawn: l.quantityDispensed === 0,
      refusal: l.quantityDispensed === 0 ? null : "Dispensed",
    }));
    return { withdrawn: lines.filter((l) => l.withdrawn).length, total: lines.length, lines };
  }
  async withdrawOrder(_o: string, _c: string, _t?: string): Promise<WithdrawResult> {
    void _c; void _t;
    const order = (await this.ordersMine()).find((x) => x.id === _o);
    const lines = (order?.lines ?? []).map((l) => ({
      label: l.description ?? l.code,
      withdrawn: l.quantityConsumed === 0,
      refusal: l.quantityConsumed === 0 ? null : "Delivered",
    }));
    return { withdrawn: lines.filter((l) => l.withdrawn).length, total: lines.length, lines };
  }
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
  /**
   * The visit's own care episode (ADR-0031). Every step carries the ORD-/RX- reference of the transaction it
   * belongs to, which is what lets one order's dialog show only its own history — so the fixture spans two
   * orders and a prescription rather than one of each, or the filtering would look like it worked when it
   * was simply showing everything.
   */
  encounterTimeline(_encounterId: string) {
    void _encounterId;
    return this.gate(
      () =>
        ok(z.array(zTimelineStep), [
          { status: "VisitStarted", at: "2026-07-22T09:00:00Z", by: "0cccc773-ce39-495c-bcac-0e67d746b7e9", byName: "Dr Karim Abdel-Latif", source: "emr", reference: "ENC-2026-000231" },
          { status: "VitalsRecorded", at: "2026-07-22T09:05:00Z", by: null, byName: null, source: "emr", reference: "ENC-2026-000231" },
          { status: "DiagnosisCoded", at: "2026-07-22T09:12:00Z", by: "0cccc773-ce39-495c-bcac-0e67d746b7e9", byName: "Dr Karim Abdel-Latif", source: "emr", reference: "ENC-2026-000231" },
          { status: "OrderPlaced", at: "2026-07-22T09:15:00Z", by: "0cccc773-ce39-495c-bcac-0e67d746b7e9", byName: "Dr Karim Abdel-Latif", source: "orders", reference: "ORD-2026-000118" },
          { status: "OrderSentForApproval", at: "2026-07-22T09:15:30Z", by: null, byName: null, source: "orders", reference: "ORD-2026-000118" },
          { status: "PrescriptionWritten", at: "2026-07-22T09:20:00Z", by: "0cccc773-ce39-495c-bcac-0e67d746b7e9", byName: "Dr Karim Abdel-Latif", source: "pharmacy", reference: "RX-2026-000202" },
          // A DIFFERENT order — present so a dialog filtered to ORD-2026-000118 has something to exclude.
          { status: "OrderPlaced", at: "2026-07-22T09:25:00Z", by: "0cccc773-ce39-495c-bcac-0e67d746b7e9", byName: "Dr Karim Abdel-Latif", source: "orders", reference: "ORD-2026-000120" },
          { status: "SampleConsumed", at: "2026-07-22T10:05:00Z", by: "129d2a05-8c27-43c7-aae2-f2cc4c7fda30", byName: null, source: "orders", reference: "ORD-2026-000118" },
          { status: "ResultReported", at: "2026-07-22T11:30:00Z", by: null, byName: null, source: "orders", reference: "ORD-2026-000118" },
          { status: "MedicineDispensed", at: "2026-07-22T12:10:00Z", by: null, byName: null, source: "pharmacy", reference: "RX-2026-000202" },
          { status: "VisitEnded", at: "2026-07-22T12:30:00Z", by: "0cccc773-ce39-495c-bcac-0e67d746b7e9", byName: "Dr Karim Abdel-Latif", source: "emr", reference: "ENC-2026-000231" },
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

  // ---- 32.6 — the waiting room ------------------------------------------
  //
  // The fixture carries a ticket with NO NAME on purpose. Check-in can be recorded without one, and a board
  // that quietly prints "Unknown" in that slot calls somebody who is not there; a fixture where every row is
  // named would never exercise the branch that has to say so.
  private waitingTickets = [
    { queueId: "q-1", appointmentId: "appt-2", position: 1, memberNo: "MRS-M-2026-000009", displayName: "Amal Hassan", appointmentType: "FollowUp", state: "Waiting", waitSeconds: 1140 },
    { queueId: "q-2", appointmentId: "appt-3", position: 2, memberNo: "MRS-M-2026-000031", displayName: null, appointmentType: "Consultation", state: "Waiting", waitSeconds: 420 },
  ];

  waitingRoom() {
    return this.gate(() => ok(z.array(zWaitingTicket), this.waitingTickets), []);
  }

  callNextWaiting() {
    return this.gate(() => {
      const head = this.waitingTickets[0];
      // An empty waiting room is an ANSWER. The service returns 204 and the client maps it to null; a
      // fixture that threw here would make the honest case look like a fault.
      if (!head) return null;
      this.waitingTickets = this.waitingTickets.slice(1).map((t, i) => ({ ...t, position: i + 1 }));
      return ok(zWaitingTicket, { ...head, state: "InConsultation", position: 0 });
    });
  }

  requeueWaiting(queueId: string) {
    return this.gate(() => { void queueId; return undefined as void; });
  }

  removeWaiting(queueId: string) {
    return this.gate(() => {
      this.waitingTickets = this.waitingTickets
        .filter((t) => t.queueId !== queueId)
        .map((t, i) => ({ ...t, position: i + 1 }));
      return undefined as void;
    });
  }

  completeWaiting(queueId: string) {
    return this.gate(() => { void queueId; return undefined as void; });
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
        // A worklist of ENCOUNTERS, which is why Amal appears three times: she has been seen three times by
        // this doctor, at two different branches. The fixture is deliberately shaped that way — "My Patients"
        // folds these to one row per person, and a fixture with one encounter each would have let the version
        // that listed every visit as a separate patient look correct.
        ok(z.array(zPatientListItem), [
          {
            id: "ENC-2026-000231",
            beneficiaryId: "aaaaaaaa-0000-0000-0000-000000000231",
            name: loc("Amal Hassan", "أمل حسن"),
            mrn: "ENC-2026-000231",
            treating: true,
            lastVisit: "2026-07-01",
            status: { kind: "ok", label: loc("In consultation", "في الكشف") },
            branchId: "bbbbbbbb-0000-0000-0000-000000000001",
            branchName: "Maadi",
          },
          {
            id: "ENC-2026-000198",
            beneficiaryId: "aaaaaaaa-0000-0000-0000-000000000231",
            name: loc("Amal Hassan", "أمل حسن"),
            mrn: "ENC-2026-000198",
            treating: true,
            lastVisit: "2026-05-14",
            status: { kind: "neu", label: loc("Completed", "مكتمل") },
            branchId: "bbbbbbbb-0000-0000-0000-000000000002",
            branchName: "Nasr City",
          },
          {
            id: "ENC-2026-000160",
            beneficiaryId: "aaaaaaaa-0000-0000-0000-000000000231",
            name: loc("Amal Hassan", "أمل حسن"),
            mrn: "ENC-2026-000160",
            treating: true,
            lastVisit: "2026-03-02",
            status: { kind: "neu", label: loc("Completed", "مكتمل") },
            branchId: "bbbbbbbb-0000-0000-0000-000000000001",
            branchName: "Maadi",
          },
          {
            id: "ENC-2026-000555",
            beneficiaryId: "aaaaaaaa-0000-0000-0000-000000000555",
            name: loc("Yusuf Haddad", "يوسف حداد"),
            mrn: "ENC-2026-000555",
            treating: true,
            lastVisit: "2026-06-20",
            status: { kind: "info", label: loc("Waiting", "بالانتظار") },
            branchId: "bbbbbbbb-0000-0000-0000-000000000002",
            branchName: "Nasr City",
          },
          {
            // A WALK-IN: no appointment, so no branch. The panel says so rather than inventing one.
            id: "ENC-2026-000601",
            beneficiaryId: "aaaaaaaa-0000-0000-0000-000000000601",
            name: loc("Mariam Fouad", "مريم فؤاد"),
            mrn: "ENC-2026-000601",
            treating: true,
            lastVisit: "2026-06-28",
            status: { kind: "neu", label: loc("Completed", "مكتمل") },
            branchId: null,
            branchName: null,
          },
        ]),
      [],
    );
  }
  /**
   * The argument is the ENCOUNTER id from the URL; `patientId` is the BENEFICIARY.
   *
   * It used to echo the encounter id straight back into `patientId`, which the live client never does — there
   * it is `e.beneficiaryId`. The encounter workspace's Prescriptions and Labs tabs filter the clinician's own
   * lists with `r.beneficiary.id === encounter.patientId`, so against this fixture that comparison was an
   * encounter id against a beneficiary id: never equal, both tabs permanently empty, and every column on
   * those two tables — including any newly added one — invisible in the demo build and in the route sweep.
   */
  getEncounter(encounterId: string) {
    return this.gate(() =>
      ok(zEncounter, {
        id: "ENC-88120",
        // Amal Hassan — the same beneficiary the orders and prescriptions fixtures are written against, so
        // the tabs actually hold rows.
        patientId: "aaaaaaaa-0000-0000-0000-000000000231",
        patientName: loc("Amal Hassan", "أمل حسن"),
        openedAt: NOW,
        signed: false,
        noteId: "NOTE-1",
        soap: {
          subjective: "Persistent cough for 5 days, low-grade fever.",
          objective: "Temp 37.8°C, chest clear, no distress.",
          assessment: "Suspected upper respiratory infection.",
          plan: "Supportive care; CBC to rule out bacterial cause.",
        },
        vitals: {
          heightCm: 164, weightKg: 61, systolic: 118, diastolic: 76,
          heartRate: 82, tempC: 37.8, spo2: 97, measuredAt: NOW,
        },
        allergies: [{ id: "AL-1", substance: loc("Penicillin", "بنسلين"), severity: "moderate" }],
        // 32.3 — corrections appended to this encounter's signed note, so the dev path can show the
        // original and its addendum together rather than only the empty case.
        addenda: this.addenda.get(encounterId) ?? [],
        diagnoses: [{
          id: "DX-1", system: "ICD-10", code: "J06.9", rank: "Primary",
          label: loc("Acute upper respiratory infection", "التهاب تنفسي علوي حاد"),
        }],
      }),
    );
  }
  saveEncounterNote(_encounterId: string, noteId: string | null) {
    return this.gate(() => ({ noteId: noteId ?? "NOTE-1" }));
  }
  signEncounterNote(): Promise<void> {
    return this.gate(() => undefined);
  }
  completeEncounter(): Promise<void> {
    return this.gate(() => undefined);
  }
  addEncounterDiagnosis(_encounterId: string, icdCode: string, rank: DiagnosisRank = "Secondary") {
    return this.gate(() =>
      ok(zEncounterDiagnosis, {
        id: `DX-${icdCode}`,
        system: "ICD-10",
        code: icdCode,
        label: loc(DEV_ICD.find((c) => c.code === icdCode)?.title ?? icdCode, icdCode),
        rank,
      }),
    );
  }
  removeEncounterDiagnosis(): Promise<void> {
    return this.gate(() => undefined);
  }
  searchIcd(query: string) {
    const q = query.trim().toLowerCase();
    // Same floor as the real client: a two-character minimum, so the demo behaves like the thing it stands in
    // for rather than dumping every code the moment the field is focused.
    return this.gate(() =>
      q.length < 2
        ? []
        : DEV_ICD.filter((c) => c.code.toLowerCase().startsWith(q) || c.title.toLowerCase().includes(q))
            .map((c) => zIcdRef.parse(c)),
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
    const ACTIVE = { kind: "info" as const, label: loc("Active", "نشط") };
    const DONE = { kind: "ok" as const, label: loc("Completed", "مكتمل") };
    // The beneficiary ids are the SAME ones `listPatients` returns, so the worklist's patient-name join has
    // something to resolve against — a fixture whose ids matched nothing would render the masked-token
    // fallback on every row and make the named column look broken in dev.
    //
    // ord-3 is deliberately MULTI-LINE with one undescribed code: the detail dialog's "test name not
    // recorded" state is then exercised in dev and in the a11y sweep rather than only in production.
    const rows = [
      {
        id: "ord-1", line: "ln-1", no: "ORD-2026-000118", ben: "aaaaaaaa-0000-0000-0000-000000000231",
        tok: "•••4821", type: "Lab", st: ACTIVE, key: "Active", at: "2026-07-22T08:10:00Z",
        exp: "2026-08-21T08:10:00Z",
        lines: [{ id: "ln-1", code: "80053", codeSystem: "CPT", description: "Comprehensive metabolic panel",
                  quantityOrdered: 1, quantityConsumed: 0, status: ACTIVE }],
      },
      // ord-2 is a psychiatry-panel result → sensitivity-restricted (14.7); resultDetail returns existence-only.
      {
        id: "ord-2", line: "ln-2", no: "ORD-2026-000119", ben: "aaaaaaaa-0000-0000-0000-000000000555",
        tok: "•••7710", type: "Imaging", st: DONE, key: "Completed", at: "2026-07-21T14:00:00Z", exp: null,
        lines: [{ id: "ln-2", code: "71046", codeSystem: "CPT", description: "Chest X-ray, 2 views",
                  quantityOrdered: 1, quantityConsumed: 1, status: DONE }],
      },
      {
        id: "ord-3", line: "ln-3", no: "ORD-2026-000120", ben: "aaaaaaaa-0000-0000-0000-000000000601",
        tok: "•••2093", type: "Lab", st: DONE, key: "Completed", at: "2026-07-20T09:30:00Z",
        exp: "2026-08-19T09:30:00Z",
        lines: [
          { id: "ln-3", code: "85025", codeSystem: "CPT", description: "Complete blood count with differential",
            quantityOrdered: 1, quantityConsumed: 1, status: DONE },
          { id: "ln-4", code: "84443", codeSystem: "CPT", description: null,
            quantityOrdered: 2, quantityConsumed: 1, status: { kind: "part" as const, label: loc("Partially used", "مُستخدم جزئياً") } },
        ],
      },
    ].filter((r) => !status || r.key === status);
    return this.gate(
      () =>
        ok(z.array(zOrderRow), rows.map((r) => ({
          id: r.id, orderNo: r.no, beneficiary: { id: r.ben, token: r.tok },
          orderType: r.type, primaryCode: r.lines[0].code, lineCount: r.lines.length,
          status: r.st, requestedAt: r.at, firstLineId: r.line, expiresAt: r.exp, lines: r.lines,
          encounterId: "ENC-2026-000231",
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
      // `hasReport: true` — the lab uploaded a signed PDF alongside the summary, which is the ordinary case
      // and the one that makes the download visible in the fixture app. The shape that motivated 33.8 is the
      // radiology one, where the report arrives with NO summary; it is not put on ln-2/ln-3 because those are
      // the fixture's restricted and standard cases and other suites assert on them.
      return ok(zResultDetail, {
        restricted: false as const, orderId, lineId, category: "Laboratory — Chemistry panel",
        code: "80053", value: "Within reference range", status: "Completed", hasReport: true,
        resultedAt: "2026-07-20T11:00:00Z",
      });
    });
  }
  resultReport(orderId: string, lineId: string) {
    void orderId; void lineId;
    return this.gate(
      () => new Blob(["%PDF-1.4 fixture report"], { type: "application/pdf" }),
      new Blob([], { type: "application/octet-stream" }),
    );
  }

  /**
   * 18.C2 (W4) — fixture approver inbox.
   *
   * <p>THREE rows since 32.4, and the third is the point: a request in InfoRequested that THIS caller
   * raised. Two rows exercised only the decider's half of the workflow, which is exactly the half that
   * worked — the requester's row was unreachable in production and equally unreachable in the fixture, so
   * no screen test could have found the missing exit.</p>
   */
  /**
   * 32.5 — the fixture line notes, keyed by line.
   *
   * <p>Seeded with one ToFulfiller instruction on the first prescription line, so the counter's panel has
   * something to render in the demo. An empty fixture would let both halves of this feature look finished
   * while the read path was never exercised — which is how the server-side model shipped in 30.5b and went
   * unseen for two months.</p>
   */
  private readonly lineNoteStore = new Map<string, LineNote[]>();

  /**
   * 32.6 — the fixture re-allocation.
   *
   * <p>Mirrors the real arithmetic's SHAPE rather than approximating its numbers: a 90-day script at 3/day
   * is 270 across three windows, so 60 days is 180 with 90 already collected. The three refusal outcomes
   * are reachable from the same inputs the live path refuses on, because a dev path that can only succeed
   * teaches a flow with no failure branch — which is how the dialog came to render a preview nobody had
   * seen.</p>
   */
  previewChronicAmendment(
    rxId: string, lineId: string,
    req: { durationDays: number; frequencyMonths: number; convertToAcute?: boolean },
  ) {
    void rxId;
    void lineId;
    return this.gate(() => {
      const perDay = 3;
      const alreadyDispensed = 90;
      const newTotal = req.durationDays * perDay;
      if (newTotal < alreadyDispensed) {
        return ok(zChronicAmendPreview, {
          outcome: "BelowDispensed", newTotal, alreadyDispensed, remainingWindows: [],
          unit: "PrescribingUnits", missingField: null,
        });
      }
      if (req.durationDays <= 30) {
        return ok(zChronicAmendPreview, {
          outcome: req.convertToAcute ? "ConvertedToAcute" : "NoLongerChronic",
          newTotal, alreadyDispensed,
          remainingWindows: req.convertToAcute ? [newTotal - alreadyDispensed] : [],
          unit: "PrescribingUnits", missingField: null,
        });
      }
      const remaining = newTotal - alreadyDispensed;
      const windows = Math.max(1, Math.round(req.durationDays / (30 * req.frequencyMonths)) - 1);
      const base = Math.floor(remaining / windows);
      const split = Array.from({ length: windows }, (_, i) =>
        i === 0 ? remaining - base * (windows - 1) : base);
      return ok(zChronicAmendPreview, {
        outcome: "Reallocated", newTotal, alreadyDispensed, remainingWindows: split,
        unit: "PrescribingUnits", missingField: null,
      });
    });
  }

  async amendChronicSchedule(
    rxId: string, lineId: string,
    req: { durationDays: number; frequencyMonths: number; reasonCode: string; reasonText?: string; convertToAcute?: boolean },
  ) {
    void rxId; void lineId; void req;
    await this.gate(() => undefined);
  }

  cancelPrescriptionLines(rxId: string, reasonCode: string, reasonText?: string) {
    void rxId; void reasonCode; void reasonText;
    return this.gate(() => ok(zWithdrawResult, {
      withdrawn: 2,
      total: 3,
      lines: [
        { label: "Amoxicillin 500mg", withdrawn: true, refusal: null },
        { label: "Paracetamol 500mg", withdrawn: true, refusal: null },
        // Partial success is the interesting case and the fixture shows it: a dispensed line cannot be
        // withdrawn, and "2 of 3" is the answer the prescriber has to read.
        { label: "Amlodipine 5mg", withdrawn: false, refusal: "Already dispensed" },
      ],
    }));
  }

  lineNotes(kind: LineNoteKind, orderId: string, lineId: string) {
    void orderId;
    return this.gate(() => {
      if (!this.lineNoteStore.has(lineId)) {
        this.lineNoteStore.set(lineId, kind === "prescription"
          ? [ok(zLineNote, {
              noteId: `note-${lineId}-1`,
              lineId,
              visibility: "ToFulfiller",
              body: "Patient cannot swallow tablets — syrup if available.",
              authorDisplayName: "Dr Karim Adel",
              authoredAt: "2026-08-19T09:20:00Z",
              status: "Active",
              cancelledAt: null,
              cancelReason: null,
            })]
          : []);
      }
      return [...this.lineNoteStore.get(lineId)!];
    }, []);
  }

  writeLineNote(
    kind: LineNoteKind, orderId: string, lineId: string, body: string, visibility?: NoteVisibility,
  ) {
    void kind;
    void orderId;
    return this.gate(() => {
      const existing = this.lineNoteStore.get(lineId) ?? [];
      const note = ok(zLineNote, {
        noteId: `note-${lineId}-${existing.length + 1}`,
        lineId,
        visibility: visibility ?? "ToFulfiller",
        body,
        authorDisplayName: "Dr Karim Adel",
        authoredAt: "2026-08-20T10:00:00Z",
        status: "Active",
        cancelledAt: null,
        cancelReason: null,
      });
      this.lineNoteStore.set(lineId, [note, ...existing]);
      return note;
    });
  }

  async cancelLineNote(kind: LineNoteKind, noteId: string, reason: string) {
    void kind;
    await this.gate(() => {
      for (const [lineId, notes] of this.lineNoteStore) {
        if (!notes.some((n) => n.noteId === noteId)) continue;
        // Marked, never removed — the same rule the server holds, so the dev path cannot teach a flow where
        // a withdrawn note disappears.
        this.lineNoteStore.set(lineId, notes.map((n) => n.noteId === noteId
          ? { ...n, status: "Cancelled" as const, cancelledAt: "2026-08-20T10:05:00Z", cancelReason: reason }
          : n));
      }
      return undefined;
    });
  }

  async takeReportAccessUnderReview(requestId: string) {
    void requestId;
    await this.gate(() => undefined);
  }

  async supplyReportAccessInfo(requestId: string, supplement: string) {
    void requestId;
    void supplement;
    await this.gate(() => undefined);
  }

  async reportAccessInbox(): Promise<ReportAccessRequestRow[]> {
    return [
      {
        requestId: "rar-1", orderId: "ord-77", orderLineId: "ol-77a", beneficiaryToken: "•••4821",
        requestedBy: "dr.hala", requestedForRole: "doctor", purposeCode: "TRT",
        justification: "Patient referred to me for follow-up; need the histology to plan treatment.",
        requestedTtlHours: 24,
        status: { kind: "warn", label: { en: "Awaiting decision", ar: "بانتظار القرار" } },
        statusCode: "Requested", canDecide: true, isRequester: false,
        createdAt: new Date(Date.now() - 3_600_000).toISOString(),
      },
      {
        requestId: "rar-2", orderId: "ord-91", orderLineId: "ol-91c", beneficiaryToken: "•••1903",
        requestedBy: "dr.omar", requestedForRole: "medical_approval", purposeCode: "PUR",
        justification: "Authorization review — medical necessity for the requested procedure.",
        requestedTtlHours: 8,
        status: { kind: "info", label: { en: "Under review", ar: "قيد المراجعة" } },
        statusCode: "UnderReview", canDecide: true, isRequester: false,
        createdAt: new Date(Date.now() - 7_200_000).toISOString(),
      },
      {
        requestId: "rar-3", orderId: "ord-64", orderLineId: "ol-64b", beneficiaryToken: "•••7734",
        requestedBy: "me", requestedForRole: "doctor", purposeCode: "TRT",
        justification: "Treating this patient since June; need the result to plan the follow-up.",
        requestedTtlHours: 12,
        status: { kind: "warn", label: { en: "More information needed", ar: "مطلوب إيضاح" } },
        // Mine, and waiting on ME. canDecide is false: I asked to see somebody else's result, so I am not
        // that order's author and have no authority over my own request.
        statusCode: "InfoRequested", canDecide: false, isRequester: true,
        createdAt: new Date(Date.now() - 10_800_000).toISOString(),
      },
    ];
  }

  async decideReportAccess(): Promise<void> {}
  async revokeReportAccessGrant(): Promise<void> {}

  /** 18.C2 (W5) — fixture identity users. One account WITHOUT a second factor, because that is the row the
   * screen exists to make visible. */
  /**
   * The fixture identity store. MUTABLE, unlike most of this file.
   *
   * Everything else here answers from a literal, because a screen that only reads cannot tell the difference.
   * User administration is the first surface in the app whose whole point is that the list CHANGES: "create
   * a user and see them in the table" is the behaviour under test, and a client that returned the same three
   * rows forever would pass a test that proved nothing.
   */
  private identityStore: IdentityUser[] = [
    // 28.13 — POSITIONS that deliberately do not track the roles beside them, because that is the whole
    // point of the column: `dr.hala` is a Consultant holding `doctor`, and `left.staff` is an Office
    // Administrator holding `reception`. A fixture where every title paraphrases its role would let a screen
    // that rendered the ROLE in this column look correct.
    { id: "u-1", username: "org.admin", displayName: "Org Admin", email: "org.admin@mersal.org", position: "Operations Director", tenantId: "•••1111", isActive: true, twoFactorEnabled: true, roles: ["org_admin"] },
    { id: "u-2", username: "dr.hala", displayName: "Dr. Hala", email: "hala@mersal.org", position: "Consultant Physician", tenantId: "•••1111", isActive: true, twoFactorEnabled: false, roles: ["doctor"] },
    { id: "u-3", username: "left.staff", displayName: "Former Staff", email: "former@mersal.org", position: "Office Administrator", tenantId: "•••1111", isActive: false, twoFactorEnabled: true, roles: ["reception"] },
    // Predates 28.8, which required an address on creation: it can neither sign in by address nor be sent a
    // reset link, and the console must say so rather than offer a button that fails.
    // No position, and that is a state the table has to render honestly rather than blank: a service account
    // has no job title because it is not a person.
    { id: "u-4", username: "svc.reporting", displayName: "Reporting Service", email: null, position: null, tenantId: "•••1111", isActive: true, twoFactorEnabled: false, roles: ["finance"] },
    /*
      28.16 — the two people the AUTHORITY fixtures are about, now that accounts and memberships are one
      table. They had no account rows at all while the two halves were separate tabs reading separate
      endpoints, which was invisible then and is the whole subject now: the merged row is an account JOINED to
      its memberships, and a membership whose account is missing would have quietly proved nothing.

      Sara holds TWO memberships in two organisations — invariant 1, and the reason the row does not blend
      them: the table lists her once because she is one person, and the detail makes you choose which
      membership you are configuring because authority is never the union.
    */
    { id: "u-5", username: "s.ibrahim", displayName: "Sara Ibrahim", email: "s.ibrahim@mersal.org", position: "Attending Physician", tenantId: "•••1111", isActive: true, twoFactorEnabled: true, roles: ["doctor", "provider_admin"] },
    { id: "u-6", username: "m.farouk", displayName: "Mohamed Farouk", email: "m.farouk@mersal.org", position: "Dispensing Pharmacist", tenantId: "•••1111", isActive: true, twoFactorEnabled: true, roles: ["pharmacist"] },
    /*
      Holds `network_team`, which is an ISSUER ALIAS for the provider-admin portal rather than its canonical
      name — and the seeded platform really does have an account exactly like this one.

      It is here as a regression fixture. The portals column used to resolve with
      `roles.includes(issuerRoleFor(portal))`, which only ever matches the CANONICAL spelling, so this
      account rendered as holding no portal at all — and the edit form, which requires at least one, could
      not be saved. Same defect for `imaging_tech` through the radiology rename's dual-accept window.
    */
    { id: "u-7", username: "n.habib", displayName: "Nour Habib", email: "n.habib@mersal.org", position: "Provider Network Analyst", tenantId: "•••1111", isActive: true, twoFactorEnabled: false, roles: ["network_team"] },
  ];

  async identityUsers(query?: string): Promise<IdentityUser[]> {
    if (!query) return [...this.identityStore];
    const q = query.toLowerCase();
    return this.identityStore.filter(
      (u) =>
        u.username.toLowerCase().includes(q) ||
        u.displayName.toLowerCase().includes(q) ||
        (u.email ?? "").toLowerCase().includes(q) ||
        (u.position ?? "").toLowerCase().includes(q),
    );
  }

  async createIdentityUser(input: {
    username: string; displayName: string; email: string; tenantId?: string; roles: string[]; position?: string;
  }) {
    // The duplicate check is here as well as on the server, because the fixture build is where the screen's
    // conflict path gets exercised — a 409 that only a live issuer can produce is a branch nothing tests.
    if (this.identityStore.some((u) => (u.email ?? "").toLowerCase() === input.email.toLowerCase())) {
      // A TYPED refusal, matching what the HTTP client raises. The screens branch on `status === 409` to
      // decide which field to blame, so a bare Error here would leave the conflict path untested — and it
      // is the branch most likely to be wrong.
      throw new ApiError("http", "email-taken", 409, { title: "email-taken" });
    }
    const id = `u-${this.identityStore.length + 1}-${input.username}`;
    this.identityStore.push({
      id,
      username: input.username,
      displayName: input.displayName,
      email: input.email,
      position: input.position?.trim() || null,
      tenantId: "•••1111",
      isActive: true,
      // A new account has no second factor until its owner enrols one — showing it as enrolled would hide
      // the single most important gap an administrator reviews this table for.
      twoFactorEnabled: false,
      roles: [...input.roles],
    });
    return { id, resetLinkSent: true };
  }

  async updateIdentityUser(id: string, input: { displayName?: string; email?: string; position?: string }) {
    const u = this.identityStore.find((x) => x.id === id);
    if (!u) throw new Error("not-found");
    if (input.displayName) u.displayName = input.displayName;
    if (input.email) u.email = input.email;
    // `undefined` leaves it alone; `""` CLEARS it — the server draws the same distinction, and a fixture
    // that could only ever set a title would leave the "remove it" path untested.
    if (input.position !== undefined) u.position = input.position.trim() || null;
  }

  myProfile() {
    // THROUGH `gate`, like every other method here. It originally returned directly, which made it the one
    // read in the fixture client that could not be slow and could not fail — so the app bar's loading path
    // was unreachable in the dev build and in tests, which is precisely where the caption flicker (28.14)
    // hid. A fixture that cannot be slow cannot exercise the states a real network produces.
    const me = this.identityStore[0];
    return this.gate(() => ({ displayName: me.displayName, position: me.position ?? null }));
  }

  async setIdentityUserRoles(id: string, roles: string[]) {
    const u = this.identityStore.find((x) => x.id === id);
    if (!u) throw new Error("not-found");
    u.roles = [...roles];
  }

  async deactivateIdentityUser(id: string) {
    const u = this.identityStore.find((x) => x.id === id);
    if (!u) throw new Error("not-found");
    u.isActive = false;
  }

  async reactivateIdentityUser(id: string) {
    const u = this.identityStore.find((x) => x.id === id);
    if (!u) throw new Error("not-found");
    u.isActive = true;
  }

  async sendPasswordResetLink(id: string) {
    if (!this.identityStore.some((x) => x.id === id)) throw new Error("not-found");
  }

  async changeMyPassword(currentPassword: string, newPassword: string) {
    // Modelled, not stubbed: the screen's job is to surface the refusal, and a fixture that always succeeds
    // leaves the only interesting branch untested.
    if (!currentPassword || !newPassword) throw new Error("missing-field");
    if (newPassword.length < 12) throw new Error("change-refused");
  }

  /**
   * A slice of the real catalogue — enough domains, and one of each flag, so the screen's own rules are
   * exercised: a service-only key must be unselectable, a deprecated one must be marked, and a
   * platform-administration key must say what it is.
   */
  async scopeCatalog(): Promise<ScopeCatalogEntry[]> {
    return [
      { name: "eligibility:check", domain: "eligibility", description: "Check whether a beneficiary is covered.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["reception", "call_center"] },
      { name: "patient:read", domain: "patient", description: "Read the beneficiary directory (identity, not clinical).", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["reception", "doctor", "nurse", "pharmacist"] },
      { name: "emr:read", domain: "emr", description: "Read the clinical record of a treated patient.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["doctor", "nurse"] },
      { name: "emr:write", domain: "emr", description: "Author an encounter.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["doctor"] },
      { name: "orders:write", domain: "orders", description: "Place an investigation order.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["doctor"] },
      { name: "orders:consume", domain: "orders", description: "Consume an order at a bench. Once only.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["lab_tech", "radiology_tech"] },
      { name: "rx:dispense", domain: "pharmacy", description: "Dispense against a prescription.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["pharmacist"] },
      { name: "finance:write", domain: "finance", description: "Raise a payment.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["finance"] },
      { name: "finance:approve", domain: "finance", description: "Release a payment.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["finance"] },
      { name: "claims:submit", domain: "claims", description: "Raise a claim.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: [] },
      { name: "claims:adjudicate", domain: "claims", description: "Decide a claim.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["claims_officer"] },
      { name: "admin:read", domain: "admin", description: "Read the administration surfaces.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: true, heldBy: ["org_admin", "super_admin"] },
      { name: "admin:write", domain: "admin", description: "Change users, roles and configuration.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: true, heldBy: ["org_admin", "super_admin"] },
      { name: "auth:ingest", domain: "auth", description: "Service-to-service audit ingest.", serviceOnly: true, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: [] },
      { name: "orders:read:all", domain: "orders", description: "Superseded by the narrower bench keys.", serviceOnly: false, deprecated: true, replacedBy: "orders:read", isPlatformAdminKey: false, heldBy: [] },
      { name: "notification:read", domain: "notification", description: "Read your own notification inbox.", serviceOnly: false, deprecated: false, replacedBy: null, isPlatformAdminKey: false, heldBy: ["reception", "doctor", "nurse", "org_admin"] },
    ];
  }

  private customRoles: RoleCatalogEntry[] = [];

  async roleCatalog(): Promise<RoleCatalogEntry[]> {
    return [
      { name: "reception", description: "The front desk.", sensitivityTier: "T2", level: 2, custom: false, builtIn: true, scopes: ["eligibility:check", "patient:read", "notification:read"] },
      { name: "doctor", description: "Treating clinician.", sensitivityTier: "T3", level: 1, custom: false, builtIn: true, scopes: ["emr:read", "emr:write", "orders:write", "patient:read", "notification:read"] },
      { name: "finance", description: "Money, never a diagnosis.", sensitivityTier: "T2", level: 2, custom: false, builtIn: true, scopes: ["finance:write", "finance:approve"] },
      { name: "org_admin", description: "Organisation administration.", sensitivityTier: "T4", level: 0, custom: false, builtIn: true, scopes: ["admin:read", "admin:write"] },
      ...this.customRoles,
    ];
  }

  async createRole(input: { name: string; scopes: string[]; description?: string; sensitivityTier?: string }) {
    if (this.customRoles.some((r) => r.name === input.name)) {
      throw new ApiError("http", "role-name-taken", 409, { title: "role-name-taken" });
    }
    // The SoD rule the server enforces over the SET, mirrored here for the same reason the duplicate check
    // is: it is the branch the screen exists to render, and only a live issuer would otherwise produce it.
    const sod = (a: string, b: string) => input.scopes.includes(a) && input.scopes.includes(b);
    if (sod("finance:write", "finance:approve") || sod("claims:submit", "claims:adjudicate")) {
      throw new ApiError("http", "sod-conflict", 409, { title: "sod-conflict" });
    }
    const tier = input.sensitivityTier ?? "T2";
    this.customRoles.push({
      name: input.name,
      description: input.description ?? null,
      sensitivityTier: tier,
      level: 4 - Number(tier.slice(1)),
      custom: true,
      builtIn: false,
      scopes: [...input.scopes],
    });
  }

  async setRoleScopes(role: string, scopes: string[]) {
    const r = this.customRoles.find((x) => x.name === role);
    if (r) r.scopes = [...scopes];
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
          {
            id: "rx-1", rxNo: "RX-2026-000202", beneficiary: { id: "aaaaaaaa-0000-0000-0000-000000000231", token: "•••4821" },
            lineCount: 2, status: { kind: "ok", label: loc("Approved", "معتمدة") },
            // 32.6 — one chronic row in the fixture, so the schedule control has somewhere to appear. With
            // every fixture row acute, the amend-schedule path would still be unreachable in the demo.
            kind: "Chronic", refillFrequencyCode: "Monthly", durationDays: 90,
            submittedAt: "2026-07-22T08:15:00Z", expiresAt: "2026-08-21T08:15:00Z",
            encounterId: "ENC-2026-000231",
            prescriber: loc("Dr Karim Abdel-Latif", "د. كريم عبد اللطيف"),
            lines: [
              {
                id: "rx-1-l1", drug: loc("Amoxicillin 500mg capsule", "أموكسيسيلين 500مجم كبسولة"),
                dose: "500 mg", route: "PO", frequency: "TDS",
                quantityPrescribed: 21, quantityDispensed: 0, refillsAllowed: 0,
                status: { kind: "info", label: loc("Active", "نشطة") },
              },
              // Written before the drug-name snapshot: the fixture carries the gap so the "not recorded"
              // rendering is exercised in dev and in tests, rather than only ever appearing in production.
              {
                id: "rx-1-l2", drug: null, dose: null, route: "PO", frequency: "OD",
                quantityPrescribed: 30, quantityDispensed: 0, refillsAllowed: 1,
                status: { kind: "info", label: loc("Active", "نشطة") },
              },
            ],
          },
          {
            id: "rx-2", rxNo: "RX-2026-000198", beneficiary: { id: "aaaaaaaa-0000-0000-0000-000000000555", token: "•••2093" },
            lineCount: 1, status: { kind: "part", label: loc("Partially dispensed", "صُرفت جزئياً") },
            kind: "Acute", refillFrequencyCode: null, durationDays: 7,
            submittedAt: "2026-07-21T10:00:00Z", prescriber: null, encounterId: "ENC-2026-000231",
            lines: [
              {
                id: "rx-2-l1", drug: loc("Metformin 500mg tablet", "ميتفورمين 500مجم قرص"),
                dose: "500 mg", route: "PO", frequency: "BD",
                quantityPrescribed: 60, quantityDispensed: 30, refillsAllowed: 2,
                status: { kind: "part", label: loc("Partially dispensed", "صُرفت جزئياً") },
              },
            ],
          },
        ]),
      [],
    );
  }

  recordVitals(encounterId: string, readings: VitalInput[]) {
    return this.gate(() => ok(zVitalsResult, { encounterId, recorded: readings.length }));
  }

  // ---- Standing clinical facts: blood group + allergies -------------------
  //
  // Held in a mutable map keyed by beneficiary, so a recorded allergy is VISIBLE on the next read. A dev
  // client that accepts a write and then returns the same fixture makes the round trip look broken in demo
  // and, worse, lets a test assert "the POST resolved" while the panel it is meant to prove still renders
  // the old list.
  //
  // The default entry is deliberately EMPTY. "No allergies recorded" is the state the UI most needs to get
  // right — it is not the same claim as "this patient has no allergies" — so it is the one the demo opens on
  // rather than a state a developer has to construct.
  private readonly clinical = new Map<string, MemberClinicalRecord>();

  private clinicalFor(beneficiaryId: string): MemberClinicalRecord {
    const existing = this.clinical.get(beneficiaryId);
    if (existing) return existing;
    const fresh: MemberClinicalRecord = {
      beneficiaryId, bloodGroup: null, bloodGroupRecordedAt: null, allergies: [],
    };
    this.clinical.set(beneficiaryId, fresh);
    return fresh;
  }

  memberClinicalRecord(beneficiaryId: string) {
    return this.gate(
      () => ok(zMemberClinicalRecord, this.clinicalFor(beneficiaryId)),
      ok(zMemberClinicalRecord, { beneficiaryId, bloodGroup: null, bloodGroupRecordedAt: null, allergies: [] }),
    );
  }

  allergenCatalogue() {
    // The masterdata seed (its migration 0002), abbreviated. Real uuids are not needed in dev, but the
    // SHAPE is: a Drug-category allergen is what prescribe-time screening resolves against ATC.
    return this.gate(
      () => DEV_ALLERGENS.map((a) => ok(zAllergenOption, a)),
      [],
    );
  }

  /**
   * 32.2 — the fixture medication list.
   *
   * <p>Two rows, and the pair is deliberate: one `Prescribed` (Mersal's own record, derivable from the
   * dispensing log) and one `SelfReported` (a medicine the patient mentioned, derivable from nowhere else).
   * A fixture with only the first would let a screen look right while never exercising the half of the union
   * that is the whole reason `medication_history` exists.</p>
   */
  private readonly medications = new Map<string, MedicationHistoryRow[]>();

  private medicationsFor(beneficiaryId: string): MedicationHistoryRow[] {
    if (!this.medications.has(beneficiaryId)) {
      this.medications.set(beneficiaryId, [
        ok(zMedicationHistoryRow, {
          medHistoryId: "MH-1", beneficiaryId, drugId: "d-warf-5", drugName: "Warfarin 5mg",
          source: "Prescribed", startDate: "2026-05-04", endDate: null, status: "Active",
        }),
        ok(zMedicationHistoryRow, {
          medHistoryId: "MH-2", beneficiaryId, drugId: "d-sjw", drugName: "St John's Wort 300mg",
          source: "SelfReported", startDate: "2026-07-01", endDate: null, status: "Active",
        }),
      ]);
    }
    return this.medications.get(beneficiaryId)!;
  }

  /** 32.3 — the corrections appended to a signed note, keyed by encounter. */
  private readonly addenda = new Map<string, NoteAddendum[]>();

  addNoteAddendum(
    encounterId: string,
    _noteId: string,
    soap: { subjective?: string; objective?: string; assessment?: string; plan?: string },
  ) {
    return this.gate(() => {
      const existing = this.addenda.get(encounterId) ?? [];
      const row = ok(zNoteAddendum, {
        id: `note-add-${existing.length + 1}`,
        authoredAt: "2026-08-20T10:00:00Z",
        // The signed-in clinician's name, not their id — the dev path renders what live renders, or the
        // uuid problem 0027 fixed reappears the moment somebody demos this screen.
        authoredByName: "Dr Karim Adel",
        soap: {
          subjective: soap.subjective ?? "",
          objective: soap.objective ?? "",
          assessment: soap.assessment ?? "",
          plan: soap.plan ?? "",
        },
      });
      this.addenda.set(encounterId, [...existing, row]);
      return row;
    });
  }

  medicationHistory(beneficiaryId: string, status?: MedicationStatus) {
    return this.gate(() => {
      const rows = this.medicationsFor(beneficiaryId);
      return status ? rows.filter((r) => r.status === status) : [...rows];
    });
  }

  async addMedicationHistory(beneficiaryId: string, req: AddMedicationHistoryRequest) {
    // The server snapshots the name from MASTER DATA and never trusts a client-supplied one (emr 0026), so
    // the fixture resolves it from the same catalogue the picker searched rather than echoing a label the
    // caller passed. An id the catalogue does not hold yields no name here either — a dev path kinder than
    // the live one teaches a flow that does not exist.
    const catalogue = await this.searchPrescribableDrugs("");
    const resolved = catalogue.find((d) => d.drugId === req.drugId);
    return this.gate(() => {
      const rows = this.medicationsFor(beneficiaryId);
      const row = ok(zMedicationHistoryRow, {
        medHistoryId: `MH-${rows.length + 1}`,
        beneficiaryId,
        drugId: req.drugId,
        drugName: resolved ? resolved.tradeName.en : (DEV_DRUG_NAMES[req.drugId] ?? null),
        source: req.source,
        startDate: req.startDate ?? null,
        endDate: req.endDate ?? null,
        status: req.status,
      });
      this.medications.set(beneficiaryId, [...rows, row]);
      return row;
    });
  }

  stopMedication(beneficiaryId: string, medHistoryId: string, endDate?: string) {
    return this.gate(() => {
      const rows = this.medicationsFor(beneficiaryId);
      const found = rows.find((r) => r.medHistoryId === medHistoryId);
      if (!found) throw new Error(`No medication ${medHistoryId}`);
      // The server refuses a second stop rather than restamping the end date, because "when did they stop"
      // is a recorded clinical fact. The fixture refuses it too, or the dev path teaches a flow the live one
      // rejects.
      if (found.status === "Stopped") throw new Error("This medication was already recorded as stopped.");
      const stopped = { ...found, status: "Stopped" as const, endDate: endDate ?? "2026-08-20" };
      this.medications.set(beneficiaryId, rows.map((r) => (r.medHistoryId === medHistoryId ? stopped : r)));
      return stopped;
    });
  }

  addAllergy(beneficiaryId: string, req: AddAllergyRequest) {
    return this.gate(() => {
      const option = DEV_ALLERGENS.find((a) => a.allergenId === req.allergenId);
      const record = ok(zAllergyRecord, {
        allergyId: `AL-${this.clinicalFor(beneficiaryId).allergies.length + 1}`,
        allergenId: req.allergenId,
        // The server resolves the name from master data and never trusts a client-supplied one. Mirrored
        // here so the dev path exercises the same rule: an unknown id yields no name, not a made-up one.
        allergen: option?.name ?? null,
        reaction: req.reaction?.trim() ? req.reaction.trim() : null,
        severity: req.severity,
        status: req.status,
      });
      const current = this.clinicalFor(beneficiaryId);
      this.clinical.set(beneficiaryId, { ...current, allergies: [...current.allergies, record] });
      return record;
    });
  }

  setBloodGroup(beneficiaryId: string, bloodGroup: BloodGroup): Promise<void> {
    return this.gate(() => {
      const current = this.clinicalFor(beneficiaryId);
      this.clinical.set(beneficiaryId, { ...current, bloodGroup, bloodGroupRecordedAt: NOW });
      return undefined;
    });
  }

  // ---- Lab / imaging -----------------------------------------------------
  /**
   * The bench's member search. Filters the same fixture the queue uses, so the two cannot disagree about an
   * order — and models the REFUSALS, which is where the real behaviour lives: one identifier is a 422, an
   * unreachable directory is a 503, and only "matched nobody" is an empty list.
   */
  async labSearch(kind: "lab" | "radiology", by: { orderNo?: string; cardNumber?: string; memberNo?: string; passport?: string }) {
    const rows = await this.labQueue(kind);
    const orderNo = (by.orderNo ?? "").trim();
    if (orderNo) return rows.filter((r) => r.orderNo.toLowerCase() === orderNo.toLowerCase());

    const supplied = [by.cardNumber, by.memberNo, by.passport].filter((v) => (v ?? "").trim() !== "");
    if (supplied.length < 2) throw new ApiError("http", "two-identifiers-required", 422, { type: "urn:hbmp:two-identifiers-required" });
    // The fixture has one member, so two identifiers resolve to all of their orders.
    return rows;
  }

  // ---- 29.2b — external delivering provider (design 45 §2b) --------------------------------------------
  //
  // The fixture models ONE centre, so everything here is already "ours" — the ownership rule it stands in for
  // is enforced server-side by assigned_provider_id and proved by the two-provider test in orders. What the
  // fixture DOES model faithfully is the part the UI has to get right: the queue carries no name, the counter
  // refuses a single identifier, and a replayed session key returns the same progress rather than a second one.
  private procedureSessions = new Map<string, number>();
  private procedureSeenKeys = new Set<string>();
  /** orderId → when the loop was closed. Dev-only, so the counter can show "reported" after it reports. */
  private procedureReports = new Map<string, string>();

  private procedureFixture() {
    return [
      {
        orderId: "ord-proc-1", orderLineId: "ord-proc-1-line-1",
        orderNo: "ORD-2026-000901", orderType: "Procedure", status: "Active",
        beneficiaryId: "ben-1", beneficiaryDisplayName: null, beneficiaryPhotoUrl: null,
        codeSystem: "CPT", code: "97110", description: "Therapeutic exercise",
        procedureTypeCode: "Physiotherapy",
        sessionsAuthorised: 6, sessionsDelivered: this.procedureSessions.get("ord-proc-1") ?? 0,
        authorised: true, validUntil: "2026-12-31T00:00:00Z", expired: false,
        sharedClinicalContext: "Post-op knee rehabilitation, ACL repair 12 Feb.",
      },
      {
        orderId: "ord-proc-2", orderLineId: "ord-proc-2-line-1",
        orderNo: "ORD-2026-000902", orderType: "Procedure", status: "Active",
        beneficiaryId: "ben-2", beneficiaryDisplayName: null, beneficiaryPhotoUrl: null,
        codeSystem: "CPT", code: "90935", description: "Haemodialysis",
        procedureTypeCode: "Dialysis",
        sessionsAuthorised: 12, sessionsDelivered: this.procedureSessions.get("ord-proc-2") ?? 0,
        // Deliberately NULL: the ordering doctor shared nothing. It must render as "not disclosed", never as
        // "no relevant history" — absence of data is never a clean result.
        authorised: true, validUntil: "2026-12-31T00:00:00Z", expired: false,
        sharedClinicalContext: null,
      },
    ].map((o) => ({
      ...o,
      sessionsRemaining: Math.max(0, o.sessionsAuthorised - o.sessionsDelivered),
      progressLabel: `${o.sessionsDelivered} of ${o.sessionsAuthorised} sessions delivered`,
      completionReportedAt: this.procedureReports.get(o.orderId) ?? null,
    }));
  }

  /**
   * 29.2 — the seeded OP-Procedure kinds (masterdata migration 0015 + 0017), mirrored here so the composer
   * behaves in dev as it does against the service.
   *
   * <p>Both session-based and non-session kinds are present deliberately: a fixture holding only
   * the physiotherapy row would let a check written against that NAME pass every test while the flag it
   * is supposed to read went unexercised.</p>
   */
  procedureTypes() {
    return this.gate(() => [
      { code: "Physiotherapy", name: loc("Physiotherapy", "العلاج الطبيعي"), isSessionBased: true, defaultSessions: 6, maxSessions: 30, allowedCptScopes: ["Medicine"] },
      { code: "MinorSurgery", name: loc("Minor Surgery", "جراحة صغرى"), isSessionBased: false, defaultSessions: null, maxSessions: null, allowedCptScopes: ["Surgery"] },
      { code: "InjectionInfusion", name: loc("Injection / Infusion", "حقن / تسريب"), isSessionBased: false, defaultSessions: null, maxSessions: null, allowedCptScopes: ["Medicine"] },
      { code: "Dialysis", name: loc("Dialysis", "غسيل كلوي"), isSessionBased: true, defaultSessions: 12, maxSessions: 156, allowedCptScopes: ["Medicine"] },
      { code: "WoundCare", name: loc("Wound Care", "العناية بالجروح"), isSessionBased: false, defaultSessions: null, maxSessions: null, allowedCptScopes: ["Surgery", "Medicine"] },
      { code: "Rehabilitation", name: loc("Rehabilitation", "إعادة التأهيل"), isSessionBased: true, defaultSessions: 10, maxSessions: 60, allowedCptScopes: ["Medicine"] },
      { code: "DiagnosticProcedure", name: loc("Diagnostic Procedure", "إجراء تشخيصي"), isSessionBased: false, defaultSessions: null, maxSessions: null, allowedCptScopes: ["Surgery", "Medicine"] },
      { code: "Other", name: loc("Other", "أخرى"), isSessionBased: false, defaultSessions: null, maxSessions: null, allowedCptScopes: ["Surgery", "Medicine"] },
    ]);
  }

  /**
   * 29.2 — the routing verdict, from the PUBLISHED ranges (design 45 §2).
   *
   * <p>Deliberately not an always-ProcedureOrder stub. The whole feature is that E/M goes somewhere else,
   * and a fixture that never produced a Referral would let the routing be deleted without a test noticing.
   * E/M is tested FIRST because 99202-99499 sits inside Medicine's 99xxx block — checking Medicine first
   * would swallow every office visit.</p>
   */
  orderableServices(query: string, kinds?: readonly string[]) {
    return this.gate(() => {
      const catalogue = [
        { code: "29881", description: "Knee arthroscopy, medial or lateral", section: "Surgery" },
        { code: "97110", description: "Therapeutic exercise", section: "Medicine" },
        { code: "90471", description: "Immunization administration", section: "Medicine" },
        { code: "99243", description: "Office consultation, moderate complexity", section: "EvaluationAndManagement" },
        { code: "99213", description: "Office visit, established patient", section: "EvaluationAndManagement" },
      ];
      const q = query.trim().toLowerCase();
      const rows = catalogue
        .filter((c) => c.code.startsWith(q) || c.description.toLowerCase().includes(q))
        .map((c) => {
          const n = Number(c.code);
          const vehicle = n >= 99202 && n <= 99499 ? "Referral" as const : "ProcedureOrder" as const;
          return { ...c, vehicle, orderable: true, reason: null };
        });
      return kinds?.length ? rows.filter((r) => kinds.includes(r.vehicle)) : rows;
    });
  }

  createReferral(req: {
    encounterId: string;
    targetSpecialty: string;
    reason?: string;
    requestedServiceCode: string;
    targetProviderId?: string | null;
  }) {
    return this.gate(() => ({
      referralId: crypto.randomUUID(),
      referralNo: "REF-2026-000001",
      status: "Requested",
      requestedServiceCode: req.requestedServiceCode,
    }));
  }

  procedureQueue() {
    return this.gate(() => this.procedureFixture());
  }

  procedureCounterSearch(by: { cardNumber?: string; memberNo?: string; passport?: string }) {
    return this.gate(() => {
      const supplied = [by.cardNumber, by.memberNo, by.passport].filter((v) => (v ?? "").trim() !== "");
      // A card number is a lookup key, not an authenticator — cards are shared and photographed.
      if (supplied.length < 2) {
        throw new ApiError("http", "second-identifier-required", 422, { type: "urn:hbmp:second-identifier-required" });
      }
      return this.procedureFixture().map((o) => ({ ...o, beneficiaryDisplayName: "Amal Hassan" }));
    });
  }

  recordProcedureSession(
    orderId: string, orderLineId: string, idempotencyKey: string,
    _by: { practitioner?: string; attended?: boolean; note?: string },
  ) {
    return this.gate(() => {
      const authorised = orderId === "ord-proc-2" ? 12 : 6;
      // REPLAY: the same key answers the same progress. Not a second session.
      if (!this.procedureSeenKeys.has(idempotencyKey)) {
        const done = this.procedureSessions.get(orderId) ?? 0;
        if (done >= authorised) {
          throw new ApiError("http", "no-sessions-remaining", 422, { type: "urn:hbmp:no-sessions-remaining" });
        }
        this.procedureSeenKeys.add(idempotencyKey);
        this.procedureSessions.set(orderId, done + 1);
      }
      const delivered = this.procedureSessions.get(orderId) ?? 0;
      return {
        orderId, orderLineId,
        sessionsDelivered: delivered, sessionsAuthorised: authorised,
        sessionsRemaining: Math.max(0, authorised - delivered),
        progressLabel: `${delivered} of ${authorised} sessions delivered`,
      };
    });
  }

  reportProcedureCompletion(orderId: string, findings: string) {
    return this.gate(() => {
      if (findings.trim() === "") {
        throw new ApiError("http", "report-required", 422, { type: "urn:hbmp:report-required" });
      }
      // 32.6 — recorded, so the counter can show the loop closed rather than offering to close it again.
      this.procedureReports.set(orderId, new Date().toISOString());
      return undefined as void;
    });
  }

  // 29.4 — the service-history fixture (design 45 §4).
  //
  // Carries all THREE states on purpose. A fixture that only ever has history leaves the two "nothing here"
  // branches — which are the ones that matter clinically — rendered by nobody, in the demo build and in the
  // route-level axe sweep alike.
  //   85025  → two numeric results, so the trend renders
  //   80048  → a RESTRICTED occurrence: existence only, no value anywhere in the payload
  //   99999  → no previous occurrences (a real, successful answer)
  //   ERR    → could not load (an error, and a different sentence)
  serviceHistory(
    beneficiaryId: string,
    q: { serviceType?: string; code: string; page?: number; pageSize?: number },
  ) {
    return this.gate(() => {
      if (q.code === "ERR") throw new ApiError("http", "service-history-unavailable", 503, {});

      const rows =
        q.code === "85025"
          ? [
              { orderId: "o1", orderNo: "ORD-2026-7741", orderLineId: "l1", serviceType: "Lab",
                codeSystem: "CPT", code: "85025", description: "Complete blood count",
                occurredAt: "2026-02-02T09:20:00Z", status: "Completed", actorUserId: "Dr Adel",
                branchId: null, restricted: false, sensitivityLevel: "Standard",
                resultSummary: "11.2", numericValue: 11.2 },
              { orderId: "o2", orderNo: "ORD-2026-7855", orderLineId: "l2", serviceType: "Lab",
                codeSystem: "CPT", code: "85025", description: "Complete blood count",
                occurredAt: "2026-07-02T09:20:00Z", status: "Completed", actorUserId: "Dr Adel",
                branchId: null, restricted: false, sensitivityLevel: "Standard",
                resultSummary: "12.8", numericValue: 12.8 },
            ]
          : q.code === "80048"
            ? [
                // EXISTENCE ONLY. No resultSummary and no numericValue — the server never sent them, so
                // there is nothing here for the modal to withhold.
                { orderId: "o3", orderNo: "ORD-2026-7802", orderLineId: "l3", serviceType: "Lab",
                  codeSystem: "CPT", code: "80048", description: "Basic metabolic panel",
                  occurredAt: "2026-05-11T11:00:00Z", status: "Completed", actorUserId: "Dr Salma",
                  branchId: null, restricted: true, sensitivityLevel: "HighlySensitive",
                  resultSummary: null, numericValue: null },
              ]
            : [];

      return {
        beneficiaryId, serviceType: q.serviceType ?? null, code: q.code,
        total: rows.length, page: 1, pageSize: 25,
        trend: rows.filter((r) => !r.restricted && r.numericValue !== null)
          .map((r) => ({ at: r.occurredAt, value: r.numericValue! })),
        items: rows,
      };
    });
  }

  labQueue(kind: "lab" | "radiology") {
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
                orderNo: "ORD-2026-055012",
                expiresAt: "2026-12-31T21:00:00.000Z",
                expired: false,
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
                orderNo: "ORD-2026-055019",
                expiresAt: "2026-12-31T21:00:00.000Z",
                expired: false,
              },
              // Lapsed. Present in the queue rather than filtered out — a technician with the patient in
              // front of them and an empty list has nothing to tell them, and the recovery is two minutes.
              {
                id: "ORD-55003",
                kind: "lab" as const,
                test: { system: "CPT" as const, code: "80053", label: loc("Comprehensive metabolic panel", "لوحة أيضية شاملة") },
                patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
                priority: "routine" as const,
                status: { kind: "bad" as const, label: loc("Expired", "منتهٍ") },
                placedAt: "2026-06-01T09:00:00.000Z",
                panelsTotal: 1,
                panelsDone: 0,
                orderNo: "ORD-2026-055003",
                expiresAt: "2026-06-11T21:00:00.000Z",
                expired: true,
              },
            ]
          : [
              {
                id: "ORD-77003",
                kind: "radiology" as const,
                test: { system: "CPT" as const, code: "71046", label: loc("Chest X-ray", "أشعة صدر") },
                patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
                priority: "routine" as const,
                status: { kind: "info" as const, label: loc("Queued", "في الطابور") },
                placedAt: NOW,
                panelsTotal: 1,
                panelsDone: this.labProgress.get("ORD-77003") ?? 0,
                orderNo: "ORD-2026-077003",
                expiresAt: "2026-12-31T21:00:00.000Z",
                expired: false,
              },
              // The imaging side has a lapsed one too — the two queues must not diverge in what they can show.
              {
                id: "ORD-77009",
                kind: "radiology" as const,
                test: { system: "CPT" as const, code: "70450", label: loc("CT head, without contrast", "أشعة مقطعية للرأس بدون صبغة") },
                patient: { id: "MRS-M-10555", token: "Y.H · •••7702" },
                priority: "routine" as const,
                status: { kind: "bad" as const, label: loc("Expired", "منتهٍ") },
                placedAt: "2026-06-02T09:00:00.000Z",
                panelsTotal: 1,
                panelsDone: 0,
                orderNo: "ORD-2026-077009",
                expiresAt: "2026-06-12T21:00:00.000Z",
                expired: true,
              },
            ];
      return ok(z.array(zLabOrder), base);
    }, []);
  }
  /**
   * The lines on one order (ADR-0034). The queue collapses an order to its first test and a panel count;
   * the order page has to show every line, because ordered / consumed / remaining are what a bench works
   * from and a single "3 panels" figure hides which three.
   */
  private static readonly ORDER_LINES: Record<
    string,
    { id: string; test: { system: "LOINC" | "CPT"; code: string; label: ReturnType<typeof loc> }; quantityOrdered: number }[]
  > = {
    "ORD-2026-055012": [
      { id: "OL-55012-1", test: { system: "LOINC", code: "58410-2", label: loc("Complete blood count", "تعداد دم كامل") }, quantityOrdered: 1 },
      { id: "OL-55012-2", test: { system: "LOINC", code: "4537-7", label: loc("Erythrocyte sedimentation rate", "سرعة الترسيب") }, quantityOrdered: 1 },
      { id: "OL-55012-3", test: { system: "LOINC", code: "1988-5", label: loc("C-reactive protein", "بروتين سي التفاعلي") }, quantityOrdered: 1 },
    ],
    "ORD-2026-055019": [
      { id: "OL-55019-1", test: { system: "LOINC", code: "2345-7", label: loc("Glucose", "سكر الدم") }, quantityOrdered: 1 },
    ],
    "ORD-2026-055003": [
      { id: "OL-55003-1", test: { system: "CPT", code: "80053", label: loc("Comprehensive metabolic panel", "لوحة أيضية شاملة") }, quantityOrdered: 1 },
    ],
    "ORD-2026-077003": [
      { id: "OL-77003-1", test: { system: "CPT", code: "71046", label: loc("Chest X-ray", "أشعة صدر") }, quantityOrdered: 1 },
    ],
    "ORD-2026-077009": [
      { id: "OL-77009-1", test: { system: "CPT", code: "70450", label: loc("CT head, without contrast", "أشعة مقطعية للرأس بدون صبغة") }, quantityOrdered: 1 },
    ],
  };

  async investigationOrder(orderNo: string): Promise<InvestigationOrder | null> {
    const rows = [...(await this.labQueue("lab")), ...(await this.labQueue("radiology"))];
    const head = rows.find((o) => o.orderNo === orderNo);
    if (!head) return null;

    // The panel counter is spent across the lines in order, which is how a bench actually works through one.
    let done = head.panelsDone;
    const lines = (DevApiClient.ORDER_LINES[orderNo] ?? []).map((l) => {
      const consumed = Math.min(l.quantityOrdered, done);
      done -= consumed;
      return {
        id: l.id,
        test: l.test,
        quantityOrdered: l.quantityOrdered,
        quantityConsumed: consumed,
        status: consumed >= l.quantityOrdered
          ? { kind: "ok" as const, label: loc("Performed", "تم التنفيذ") }
          : { kind: "info" as const, label: loc("Outstanding", "قيد التنفيذ") },
      };
    });

    return ok(zInvestigationOrder, {
      id: head.id,
      orderNo: head.orderNo,
      kind: head.kind,
      patient: head.patient,
      status: head.status,
      placedAt: head.placedAt,
      expiresAt: head.expiresAt,
      expired: head.expired,
      lines,
    });
  }

  /**
   * Three states, on purpose, because a bench meets all three.
   *
   * <p>ORD-…055012 prices completely. ORD-…055019 has a list price but no member split — the plan does not
   * price this category at this tier. ORD-…077009 has no catalogue price at all, so even the total is
   * unknown. None of the three is ever rendered as 0.00, which is the behaviour these fixtures exist to keep
   * a screen honest about.</p>
   */
  async orderPricing(orderId: string, performNow?: Record<string, number>): Promise<OrderPricing> {
    const rows = [...(await this.labQueue("lab")), ...(await this.labQueue("radiology"))];
    const head = rows.find((o) => o.id === orderId);
    const defs = DevApiClient.ORDER_LINES[head?.orderNo ?? ""] ?? [];
    const unpriced = head?.orderNo === "ORD-2026-077009";

    const lines = defs.map((l, i) => {
      const unit = unpriced ? null : [180, 95.5, 240][i % 3];
      return {
        orderLineId: l.id,
        codeSystem: l.test.system,
        code: l.test.code,
        description: l.test.label.en,
        quantityOrdered: l.quantityOrdered,
        quantityConsumed: 0,
        unitPriceEgp: unit,
        lineTotalEgp: unit === null ? null : Number((unit * l.quantityOrdered).toFixed(2)),
      };
    });

    if (unpriced) {
      return {
        lines, currency: "EGP", totalEgp: null, memberShareEgp: null, payerShareEgp: null,
        determinate: false,
        reason: "At least one examination on this order has no list price, so the total cannot be stated. "
          + "Quoting the priced lines alone would understate what the member owes.",
        tierCode: null, isCovered: null,
        quotedOnEgp: null, quotedOnPerformNow: false,
      };
    }

    const total = Number(lines.reduce((s, l) => s + (l.lineTotalEgp ?? 0), 0).toFixed(2));

    // What the split is quoted on: the quantities at the bench once any have been entered, the whole order
    // before that. A basis of nothing falls back rather than quoting a zero.
    const basis = devBasis(
      lines.map((l) => ({ id: l.orderLineId, unit: l.unitPriceEgp, whole: l.lineTotalEgp ?? 0 })),
      performNow,
    );
    const quotedOn = basis.onNow ? basis.amount : total;

    if (head?.orderNo === "ORD-2026-055019") {
      return {
        lines, currency: "EGP", totalEgp: total, memberShareEgp: null, payerShareEgp: null,
        determinate: false,
        reason: "The member's share could not be quoted — the plan does not price this examination category "
          + "at this provider's network tier. The total above is the full list price.",
        tierCode: null, isCovered: null,
        quotedOnEgp: quotedOn, quotedOnPerformNow: basis.onNow,
      };
    }

    const { member, payer } = devCostShare(quotedOn);
    return {
      lines, currency: "EGP", totalEgp: total,
      memberShareEgp: member, payerShareEgp: payer,
      determinate: true, reason: null, tierCode: "IN-NETWORK", isCovered: true,
      quotedOnEgp: quotedOn, quotedOnPerformNow: basis.onNow,
    };
  }

  requestSubstitution(req: SubstitutionRequest) {
    return this.gate(() => {
      void req;
      return { authNo: "AUTH-2026-000488" };
    });
  }

  consume(req: ConsumeRequest) {
    return this.gate(() => {
      const replayed = this.seenKeys.has(req.idempotencyKey);
      const totals: Record<string, number> = { "ORD-55012": 3, "ORD-55019": 1, "ORD-77003": 1 };
      const total = totals[req.orderId] ?? 1;
      let done = this.labProgress.get(req.orderId) ?? 0;
      if (!replayed) {
        this.seenKeys.add(req.idempotencyKey);
        // The order page names lines and quantities; the queue names a panel count. Both land on the same
        // counter, so a page and a queue reading the same order agree about how much is left.
        const added = req.lines?.length
          ? req.lines.reduce((s, l) => s + l.quantity, 0)
          : req.panels;
        done = Math.min(total, done + added);
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

  awaitingResult(kind: "lab" | "radiology") {
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
  uploadResult(orderId: string, lineId: string, result: { value?: string; report?: File }) {
    return this.gate(() => {
      // 32.6 — the service's rule, mirrored: a summary and/or a file, and refusing only when neither came.
      // A dev client that accepted an empty upload would make the one case the screen has to prevent
      // untestable against the fixture.
      if (!result.value?.trim() && !result.report) {
        throw new ApiError("http", "empty-result", 400, { type: "urn:hbmp:empty-result" });
      }
      return ok(zResultUpload, { orderId, lineId, uploaded: true });
    });
  }

  searchDrugs(query: string) {
    const all = [
      { drugId: "d-amox-500", name: loc("Amoxicillin 500mg caps", "أموكسيسيلين 500مجم"), atcCode: "J01CA04", form: "Capsule", strength: "500mg" },
      { drugId: "d-amox-250", name: loc("Amoxicillin 250mg caps", "أموكسيسيلين 250مجم"), atcCode: "J01CA04", form: "Capsule", strength: "250mg" },
      { drugId: "d-metf-500", name: loc("Metformin 500mg", "ميتفورمين 500مجم"), atcCode: "A10BA02", form: "Tablet", strength: "500mg" },
      // 32.2 — the two the current-medications fixture references. A medication list whose drugs are absent
      // from the catalogue would render as ids, which is what a screen looks like when nobody opened it.
      { drugId: "d-warf-5", name: loc("Warfarin 5mg", "وارفارين 5مجم"), atcCode: "B01AA03", form: "Tablet", strength: "5mg" },
      { drugId: "d-sjw", name: loc("St John's Wort 300mg", "عشبة سانت جون 300مجم"), atcCode: "N06AX25", form: "Capsule", strength: "300mg" },
    ].filter((d) => d.name.en.toLowerCase().includes(query.toLowerCase()));
    return this.gate(() => ok(z.array(zDrugRef), all), []);
  }
  /**
   * Approved alternatives for a drug — the same ATC-5 class the real formulary answers with.
   *
   * <p>Keyed on the ATC code as well as the internal id, because the dispensing fixture identifies its lines
   * by ATC ("J01CA04") while the Substitutions screen uses catalogue ids. Answering only the latter meant the
   * counter's substitute control found nothing for every prescription in the demo — a working feature that
   * looked like a broken one, and worse, one whose empty list was indistinguishable from "no alternative is
   * approved for this medicine".</p>
   */
  /** Ingredients for the fixture drugs. Two trade names sharing one molecule, because that is the
   *  duplication a pharmacist is actually checking for. */
  drugIngredients(drugIds: readonly string[]) {
    const byId: Record<string, string> = {
      "d-amox-500": "amoxicillin",
      "d-amox-250": "amoxicillin",
      "d-amox-susp": "amoxicillin",
      "d-metformin-500": "metformin",
      "d-metformin-850": "metformin",
      "d-metformin-xr": "metformin",
      "d-augmentin-1g": "amoxicillin + clavulanic acid",
      "d-paracetamol-500": "paracetamol",
    };
    const out = new Map<string, string>();
    for (const id of drugIds) {
      const found = byId[id];
      // Absent, not blank: an unrecorded ingredient is a state the screen names, and 2,786 real catalogue
      // products are in it.
      if (found) out.set(id, found);
    }
    return this.gate(() => out, new Map<string, string>());
  }

  drugAlternatives(drugId: string) {
    const byClass: Record<string, { drugId: string; name: ReturnType<typeof loc>; atcCode: string; form: string; strength: string }[]> = {
      J01CA04: [
        { drugId: "d-amox-250", name: loc("Amoxicillin 250mg caps", "أموكسيسيلين 250مجم"), atcCode: "J01CA04", form: "Capsule", strength: "250mg" },
        { drugId: "d-amox-susp", name: loc("Amoxicillin 125mg/5ml susp", "أموكسيسيلين شراب"), atcCode: "J01CA04", form: "Suspension", strength: "125mg/5ml" },
      ],
      A10BA02: [
        { drugId: "d-metformin-850", name: loc("Metformin 850mg tabs", "ميتفورمين 850مجم"), atcCode: "A10BA02", form: "Tablet", strength: "850mg" },
        { drugId: "d-metformin-xr", name: loc("Metformin XR 500mg", "ميتفورمين ممتد 500مجم"), atcCode: "A10BA02", form: "Tablet", strength: "500mg" },
      ],
    };
    const alts = drugId.startsWith("d-amox") ? byClass.J01CA04 : (byClass[drugId] ?? []);
    return this.gate(() => ok(z.array(zDrugRef), alts), []);
  }


  // ---- Prescribing workspace (phase 26) ----------------------------------
  //
  // POPULATED from the start, deliberately. An axe run against an empty screen proves the empty state is
  // accessible and nothing else — the combobox options, the five per-line status cues and the expanded
  // warning panels are exactly the surface that needs checking, and none of them render without data.
  //
  // Modelled on the real Egyptian drug list: trade name and active ingredient differ (Augmentin /
  // amoxicillin+clavulanic acid), which is the whole reason the search covers both, and one product carries
  // no indication data at all — 1,019 real products are in that state.
  private static readonly PRESCRIBABLE = [
    {
      drugId: "11111111-0000-4000-8000-000000000001",
      tradeName: loc("Augmentin 1g 14 f.c. tabs", "أوجمنتين 1جم"),
      activeIngredient: "amoxicillin + clavulanic acid",
      strength: "1g", form: "tablet", priceEgp: 210, atcCode: "J01CR02", hasIndicationData: true,
    },
    {
      drugId: "11111111-0000-4000-8000-000000000002",
      tradeName: loc("Amoxil 500mg caps", "أموكسيل 500مجم"),
      activeIngredient: "amoxicillin",
      strength: "500mg", form: "capsule", priceEgp: 43.5, atcCode: "J01CA04", hasIndicationData: true,
      // 29.7 — cheapest per CAPSULE in its group (43.5 / 20 = 2.175), and NOT the cheapest pack.
      isLowestPrice: true, pricePerUnit: 2.175, availability: "Available" as const,
    },
    {
      // The 29.7 correction made visible: a SMALLER pack at a LOWER pack price that is DEARER per capsule
      // (35 / 10 = 3.50). A chip driven by pack price would point the prescriber here.
      drugId: "11111111-0000-4000-8000-000000000005",
      tradeName: loc("Amoxicare 500mg 10 caps", "أموكسي كير 500مجم"),
      activeIngredient: "amoxicillin",
      strength: "500mg", form: "capsule", priceEgp: 35, atcCode: "J01CA04", hasIndicationData: true,
      isLowestPrice: false, pricePerUnit: 3.5, availability: "Unknown" as const,
    },
    {
      // Availability = Unavailable — the ONLY state that renders a badge.
      drugId: "11111111-0000-4000-8000-000000000006",
      tradeName: loc("Stockout 500mg caps", "ستوك أوت 500مجم"),
      activeIngredient: "amoxicillin",
      strength: "500mg", form: "capsule", priceEgp: 60, atcCode: "J01CA04", hasIndicationData: true,
      isLowestPrice: false, pricePerUnit: 3, availability: "Unavailable" as const,
    },
    {
      // No pack size upstream ⇒ no per-unit price ⇒ NEVER labelled, however cheap the pack looks. 12 EGP is
      // the lowest PACK price in this group, and that is precisely why it must not carry the chip.
      drugId: "11111111-0000-4000-8000-000000000007",
      tradeName: loc("Nopack 500mg caps", "نو باك 500مجم"),
      activeIngredient: "amoxicillin",
      strength: "500mg", form: "capsule", priceEgp: 12, atcCode: "J01CA04", hasIndicationData: true,
      isLowestPrice: false, availability: "Unknown" as const,
    },
    {
      drugId: "11111111-0000-4000-8000-000000000003",
      tradeName: loc("Glucophage 500mg", "جلوكوفاج 500مجم"),
      activeIngredient: "metformin",
      strength: "500mg", form: "tablet", priceEgp: 28, atcCode: "A10BA02", hasIndicationData: true,
    },
    {
      // No indication data — the check must report "not checked", never "OK".
      drugId: "11111111-0000-4000-8000-000000000004",
      tradeName: loc("Vero 4 30 tablets", "فيرو 4"),
      activeIngredient: "diosmin + hesperidin",
      strength: "300mg", form: "tablet", priceEgp: 90, hasIndicationData: false,
    },
  ];

  searchPrescribableDrugs(query: string) {
    const q = query.trim().toLowerCase();
    const hits = DevApiClient.PRESCRIBABLE.filter(
      (d) =>
        d.tradeName.en.toLowerCase().includes(q) ||
        d.tradeName.ar.includes(query.trim()) ||
        (d.activeIngredient ?? "").toLowerCase().includes(q),
    );
    return this.gate(() => ok(z.array(zPrescribableDrug), hits), []);
  }

  /**
   * A handful of real CPT codes per section, so the dev fixture exercises the section split rather than
   * returning the same list to both tabs.
   */
  private static readonly CPT: { code: string; description: string }[] = [
    { code: "71046", description: "Radiologic examination, chest; 2 views" },
    { code: "70450", description: "Computed tomography, head or brain; without contrast material" },
    { code: "76700", description: "Ultrasound, abdominal, real time; complete" },
    { code: "85025", description: "Blood count; complete (CBC), automated, with automated differential" },
    { code: "80053", description: "Comprehensive metabolic panel" },
    { code: "83036", description: "Hemoglobin; glycosylated (A1c)" },
    // A panel and one of its components, with the panel's REAL description — which cites the component codes,
    // as CPT panel descriptions do. That pairing is what makes the search ranking observable rather than
    // assumed: typing "82947" matches the glucose code AND the panel's text, and the panel sorts first by
    // code. A doctor reading a code off a request form should get the code they typed, not the panels that
    // mention it.
    { code: "82947", description: "Glucose; quantitative, blood (except reagent strip)" },
    {
      code: "80048",
      description:
        "Basic metabolic panel (Calcium, total) This panel must include the following: Calcium, total (82310) "
        + "Carbon dioxide (bicarbonate) (82374) Chloride (82435) Creatinine (82565) Glucose (82947) "
        + "Potassium (84132) Sodium (84295) Urea nitrogen (BUN) (84520)",
    },
    // Pathology (88xxx), which the Labs tab reaches and the Laboratory section alone does not. Without one
    // of these the fixture cannot tell "Labs asks for two sections" from "Labs asks for Laboratory".
    { code: "88305", description: "Level IV — Surgical pathology, gross and microscopic examination" },
    { code: "88175", description: "Cytopathology, cervical or vaginal, with screening by automated system" },
  ];

  validityPolicy() {
    return this.gate(() =>
      ok(zValidityPolicyView, {
        defaultDays: 10,
        minDays: 1,
        maxDays: 365,
        items: [
          // Deliberately mixed: two chosen, two still on the platform default, so the screen's distinction
          // between "set" and "nobody has looked at this" is exercised rather than assumed.
          { artefact: "Prescription" as const, days: 14, configured: true, updatedAt: "2026-07-20T09:00:00Z" },
          { artefact: "LabOrder" as const, days: 10, configured: false, updatedAt: null },
          { artefact: "ImagingOrder" as const, days: 30, configured: true, updatedAt: "2026-07-18T11:00:00Z" },
          { artefact: "ProcedureOrder" as const, days: 10, configured: false, updatedAt: null },
        ],
      }),
    );
  }

  async setValidityPolicy(artefact: string, days: number) {
    void artefact; void days;
    await this.gate(() => ok(z.object({}), {}));
  }

  requestValidityExtension(req: ValidityExtensionRequest) {
    void req;
    return this.gate(() =>
      ok(zValidityExtensionResult, {
        authorizationId: "auth-ext-1",
        authNo: "AUTH-2026-000271",
        status: "Submitted",
      }),
    );
  }

  searchCpt(query: string, sections: readonly CptSection[]) {
    const q = query.trim().toLowerCase();
    // The same rules masterdata applies, on the same shape of data — a fixture that searched more loosely
    // than the service would make the demo pass where the live catalogue fails, which is exactly how the
    // case-sensitive ICD code search survived: every portal test ran against a kinder client.
    const inSection = (code: string) => sections.some((s) => devCptSection(code) === s);
    const codeHit = (c: { code: string }) => c.code.toLowerCase().startsWith(q);
    const textHit = (c: { description: string }) => c.description.toLowerCase().includes(q);
    const hits = DevApiClient.CPT.filter((c) => inSection(c.code) && (codeHit(c) || textHit(c)));
    // Digit-led queries rank code matches first, worded queries rank descriptions first.
    const leads = /^\d/.test(q) ? codeHit : textHit;
    const ranked = [...hits].sort(
      (a, b) => Number(leads(b)) - Number(leads(a)) || a.code.localeCompare(b.code),
    );
    return this.gate(() => ok(z.array(zCptRef), ranked), []);
  }

  validateInvestigationOrder(req: {
    encounterId: string;
    orderType: InvestigationOrderType;
    lines: InvestigationDraftLine[];
    diagnosisIcdCodes: string[];
  }) {
    return this.gate(() => ok(zOrderValidationResult, devOrderValidation(req.orderType, req.lines, req.diagnosisIcdCodes)), {
      validationId: "ov-empty", overallState: "NotChecked" as const, findings: [], lineStates: {},
    });
  }

  submitInvestigationOrder(req: {
    encounterId: string;
    orderType: InvestigationOrderType;
    lines: InvestigationDraftLine[];
    acknowledgements: OrderAcknowledgement[];
    /**
     * 31.1 — the OP-Procedure COURSE: one kind and one session count for the whole order.
     *
     * <p>They were per-line, which let a two-item course carry two kinds and two session counts — not a
     * course any centre can deliver. Absent on Lab and Radiology orders, which have neither.</p>
     */
    procedureTypeCode?: string | null;
    sessions?: number | null;
  }) {
    void req.acknowledgements;
    return this.gate(() =>
      ok(zInvestigationOrderResult, {
        orderId: "ord-new",
        orderNo: "ORD-2026-000901",
        status: req.orderType === "Imaging" ? "PendingApproval" : "Active",
        requiresApproval: req.orderType === "Imaging",
      }),
    );
  }

  validatePrescription(req: {
    encounterId: string;
    lines: PrescriptionDraftLine[];
    diagnosisIcdCodes: string[];
  }) {
    return this.gate(() => ok(zValidationResult, devValidation(req.lines, req.diagnosisIcdCodes)), {
      validationId: "v-empty", ranAt: new Date(0).toISOString(), engineVersion: "26.4",
      overallState: "NotChecked" as const, findings: [], lineStates: {},
    });
  }

  /**
   * 29.5 — the seeded cadences (pharmacy migration 0012). `Every6Months` is seeded INACTIVE there and is
   * absent here for the same reason the endpoint excludes it: offering a cadence the write path refuses
   * would let a doctor compose a script that cannot be saved.
   */
  refillFrequencies() {
    return this.gate(() => [
      { code: "Monthly", months: 1, name: loc("Monthly", "شهرياً") },
      { code: "Every2Months", months: 2, name: loc("Every 2 months", "كل شهرين") },
      { code: "Every3Months", months: 3, name: loc("Every 3 months", "كل ٣ أشهر") },
    ]);
  }

  /**
   * 29.5 — the window schedule, as the server would compute it.
   *
   * <p>This fixture runs the SAME two rules the domain library does, because a fixture that returned tidy
   * equal windows would let the one thing this feature must not get wrong — <b>the windows sum exactly to
   * the total</b> — regress without a test noticing. Round ONCE at the total, then distribute integers by
   * largest remainder, highest first.</p>
   */
  /**
   * The fixture mirrors `QuantityMath` exactly, including the branch that REFUSES.
   *
   * A fixture that always returns a tidy number is a fixture in which the NotChecked path — the one
   * invariant 8 exists for, and the state 838 catalogue rows are actually in — can never be rendered. That
   * is how this phase kept shipping screens whose failure branch nobody had seen.
   */
  async prescribableDrugById(drugId: string) {
    const hit = (await this.searchPrescribableDrugs("")).find((d) => d.drugId === drugId);
    return hit ?? null;
  }

  quantityPreview(req: {
    drugId?: string;
    doseAmount?: number | null;
    timesPerDay?: number | null;
    durationDays?: number | null;
  }) {
    return this.gate(() => {
      const dose = req.doseAmount ?? 0;
      const perDay = req.timesPerDay ?? 0;
      const days = req.durationDays ?? 0;
      const missing = dose <= 0 ? "dose" : perDay <= 0 ? "frequency" : days <= 0 ? "duration" : null;
      if (missing) {
        throw Object.assign(new Error("422"), {
          problem: {
            title: "quantity-not-checked",
            detail: `'${missing}' is not recorded for this drug, so the quantity to dispense cannot be `
              + "computed. A silently wrong quantity is a dispensing error.",
          },
        });
      }
      const total = dose * perDay * days;
      return ok(zQuantityPreview, {
        totalUnits: total, dispenseQuantity: total, packs: null,
        // Tablets: a box of 30 holds 30 of the thing the dose counts, so a box count exists.
        boxes: Math.ceil(total / 30), packContent: 30,
        prescribingUnit: "Tablet", isPackSplittable: true,
      });
    });
  }

  chronicPreview(req: {
    durationDays: number;
    refillFrequencyCode: string;
    doseAmount?: number;
    timesPerDay?: number;
    drugId?: string;
    isPackSplittable?: boolean | null;
    packContent?: number | null;
  }) {
    return this.gate(() => {
      const months = { Monthly: 1, Every2Months: 2, Every3Months: 3 }[req.refillFrequencyCode] ?? 1;
      const raw = (req.doseAmount ?? 1) * (req.timesPerDay ?? 1) * req.durationDays;
      // Round once, at the TOTAL. A non-splittable pack rounds UP to whole packs.
      // 31.5 — divided by what the box HOLDS, like the server. See ChronicAllocation.
      const total = req.isPackSplittable === false && (req.packContent ?? 0) > 0
        ? Math.ceil(raw / req.packContent!) * req.packContent!
        : Math.round(raw);

      const count = Math.ceil(req.durationDays / (months * 30));
      const base = Math.floor(total / count);
      const remainder = total - base * count;
      // Largest remainder, HIGHEST FIRST — 100 over 3 is 34/33/33, and the sum is the total by construction.
      const windows = Array.from({ length: count }, (_, i) => base + (i < remainder ? 1 : 0));

      const start = new Date("2026-08-08T00:00:00Z");
      const day = (n: number) => new Date(start.getTime() + n * 86400000).toISOString().slice(0, 10);
      const period = months * 30;

      return {
        total,
        unit: req.isPackSplittable === false ? "WholeItem" : "SubUnit",
        frequencyMonths: months,
        windows: windows.map((q, i) => ({
          windowNo: i + 1,
          scheduledOpen: day(i * period),
          // Window 1 gets no tolerance: it cannot open before the script existed.
          opensAt: day(i === 0 ? 0 : i * period - 5),
          closesAt: day(i === count - 1 ? req.durationDays - 1 : (i + 1) * period - 1),
          allocatedQuantity: q,
        })),
      };
    });
  }

  submitPrescription(req: {
    encounterId: string;
    lines: PrescriptionDraftLine[];
    diagnosisIcdCodes: string[];
    acknowledgements: LineAcknowledgement[];
    kind?: PrescriptionKind;
    refillFrequencyCode?: string | null;
    durationDays?: number | null;
  }) {
    const result = devValidation(req.lines, req.diagnosisIcdCodes);
    // The fixture mirrors the server rule rather than rubber-stamping: a warning with no acknowledgement is
    // refused. A fixture that always succeeded would let the gating regress without a test noticing.
    const unacknowledged = result.findings.filter(
      (f) =>
        f.requiresAcknowledgement &&
        !req.acknowledgements.some((a) => a.lineId === f.lineId && a.findingKind === f.kind),
    );
    if (unacknowledged.length > 0) {
      return Promise.reject(new Error("unacknowledged-warning"));
    }
    if (result.findings.some((f) => f.isBlocking)) {
      return Promise.reject(new Error("blocked-by-benefit-rule"));
    }
    return this.gate(
      () => ok(zPrescriptionSubmitResult, { prescriptionId: "rx-dev-1", rxNo: "RX-2026-000001", status: "Submitted" }),
      { prescriptionId: "", rxNo: "", status: "" },
    );
  }

  // ---- Pharmacy ----------------------------------------------------------
  pharmacyQueue() {
    return this.gate(() => {
      const disp = (rx: string, line: string) => this.rxProgress.get(rx)?.get(line) ?? 0;
      return ok(z.array(zPrescription), [
        {
          id: "RX-33110",
          rxNo: "RX-2026-033110",
          patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
          prescriber: { label: loc("Dr. N. Fahmy", "د. ن. فهمي") },
          submittedAt: NOW,
          expiresAt: "2026-12-31T21:00:00.000Z",
          expired: false,
          // A real ICD code, because the counter resolves it to a title through master data — a fixture code
          // that resolves to nothing would test the fallback and never the join.
          diagnosisCodes: ["J01.0"],
          primaryIcdCode: "J01.0",
          status: { kind: "info", label: loc("Submitted", "مُرسلة") },
          lines: [
            {
              id: "RXL-1",
              drug: { system: "ATC", code: "d-amox-500", label: loc("Amoxicillin 500mg", "أموكسيسيلين ٥٠٠ملغ") },
              quantity: 21,
              dispensed: disp("RX-33110", "RXL-1"),
              quantityUnit: "caps",
              dose: "1 capsule",
              route: "Oral",
              frequency: "TDS",
              durationDays: 7,
              activeIngredient: "amoxicillin",
              // NOT set. The server leaves this null on the dispensing view and the counter joins the price
              // from /pricing; a fixture that fills it in models a payload no service sends and hides the
              // join it exists to exercise.
              unitPriceEgp: null,
              status: { kind: "info", label: loc("Pending", "معلّقة") },
              outOfStockAt: null,
              outOfStockNote: null,
              outOfStock: false,
            },
            {
              id: "RXL-2",
              drug: { system: "ATC", code: "d-guaifenesin", label: loc("Guaifenesin syrup", "شراب جوايفينيسين") },
              quantity: 1,
              dispensed: disp("RX-33110", "RXL-2"),
              quantityUnit: "caps",
              dose: "10 ml",
              route: "Oral",
              frequency: "TDS",
              // Not recorded, on purpose. The counter has to render an ABSENT duration as absent — a blank
              // cell reads as a one-day course, and only one of those is a reason to ring the prescriber.
              durationDays: null,
              activeIngredient: null,
              unitPriceEgp: null,
              status: { kind: "warn", label: loc("Out of stock", "غير متوفر") },
              // Flagged, WITH its provenance. Until 0020 this `true` was the only place in the entire
              // system the value could come from: the server's view did not carry the field and the HTTP
              // client wrote the literal `false`, so the chip rendered here and in the tests and could not
              // render in production. The two fields beside it are the ones that make the flag readable —
              // how long it has been that way, and what the pharmacist told the prescriber.
              outOfStockAt: "2026-07-21T07:40:00.000Z",
              outOfStockNote: "Supplier back-order; substitute discussed with the prescriber.",
              outOfStock: true,
            },
          ],
        },
        // Lapsed. It is HERE rather than filtered out, which is the whole point of the change that put it
        // on screen: a search that hides an expired prescription tells a pharmacist the member has nothing,
        // when in fact they have something that has run out of date and can be extended.
        {
          id: "RX-33044",
          rxNo: "RX-2026-033044",
          patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
          prescriber: { label: loc("Dr. N. Fahmy", "د. ن. فهمي") },
          submittedAt: "2026-06-01T09:00:00.000Z",
          expiresAt: "2026-06-11T21:00:00.000Z",
          expired: true,
          diagnosisCodes: ["E11.9"],
          primaryIcdCode: "E11.9",
          status: { kind: "bad", label: loc("Expired", "منتهية") },
          lines: [
            {
              id: "RXL-3",
              drug: { system: "ATC", code: "d-metformin-500", label: loc("Metformin 500mg", "ميتفورمين ٥٠٠ملغ") },
              quantity: 60,
              dispensed: 0,
              quantityUnit: "tabs",
              route: "Oral",
              frequency: "BD",
              durationDays: 30,
              activeIngredient: "metformin",
              unitPriceEgp: null,
              dose: "1 tab × 2/day",
              status: { kind: "neu", label: loc("Not dispensed", "لم تُصرف") },
              outOfStock: false,
            },
          ],
        },
      ]);
    }, []);
  }

  /**
   * The counter's lookup. The fixture answers on the Rx number, the member number or the card number, so the
   * demo exercises the real shape; the SERVER is what enforces the two-identifier rule, and a dev client
   * that enforced it too would hide a regression in the endpoint rather than reveal one.
   */
  async pharmacySearch(by: { rxNo?: string; cardNumber?: string; memberNo?: string; passport?: string }) {
    const all = await this.pharmacyQueue();
    // `??` does NOT fall through an empty string, and the screen sends all four fields with the unfilled
    // ones as "". So `by.rxNo ?? by.memberNo` was always "" whenever the pharmacist searched by member
    // number, and the fixture answered every identifier search with nothing — a demo that looked like a
    // working screen finding no prescriptions.
    const needle = [by.rxNo, by.memberNo, by.cardNumber, by.passport]
      .map((v) => (v ?? "").trim())
      .find((v) => v.length > 0)
      ?.toLowerCase() ?? "";
    if (!needle) return [];
    return all.filter((p) =>
      p.rxNo.toLowerCase().includes(needle) || p.patient.id.toLowerCase().includes(needle));
  }

  /**
   * A worked cost share for the demo counter.
   *
   * <p>One prescription is deliberately left INDETERMINATE. The tiles' hardest requirement is that they
   * never render an unknown split as 0.00 — at a counter a zero reads as "free" — so the demo has to be able
   * to show that state, not only the happy one.</p>
   */
  async prescriptionPricing(
    prescriptionId: string, dispenseNow?: Record<string, number>,
  ): Promise<RxPricing> {
    const all = await this.pharmacyQueue();
    const rx = all.find((p) => p.id === prescriptionId);
    const lines = (rx?.lines ?? []).map((l, i) => {
      const unit = [42.5, 128, 96.75][i % 3];
      return {
        prescriptionLineId: l.id,
        drugId: l.drug.code,
        drugName: l.drug.label.en,
        quantityPrescribed: l.quantity,
        quantityDispensed: l.dispensed,
        unitPriceEgp: unit,
        lineTotalEgp: Number((unit * l.quantity).toFixed(2)),
      };
    });
    const total = Number(lines.reduce((s, l) => s + (l.lineTotalEgp ?? 0), 0).toFixed(2));

    // What the split is quoted on: the quantities at the counter once any have been entered, the whole
    // prescription before that. A basis of nothing falls back rather than quoting a zero — see `devCostShare`.
    const basis = devBasis(
      lines.map((l) => ({ id: l.prescriptionLineId, unit: l.unitPriceEgp, whole: l.lineTotalEgp ?? 0 })),
      dispenseNow,
    );
    const quotedOn = basis.onNow ? basis.amount : total;

    if (rx?.rxNo?.endsWith("44")) {
      return {
        lines, currency: "EGP", totalEgp: total,
        memberShareEgp: null, payerShareEgp: null,
        determinate: false,
        reason: "The member's share could not be quoted — the plan does not price pharmacy at this "
          + "provider's network tier. The total above is the full list price.",
        tierCode: null, isCovered: null,
        quotedOnEgp: quotedOn, quotedOnDispenseNow: basis.onNow,
      };
    }

    const { member, payer } = devCostShare(quotedOn);
    return {
      lines, currency: "EGP", totalEgp: total,
      memberShareEgp: member,
      payerShareEgp: payer,
      determinate: true, reason: null, tierCode: "IN-NETWORK", isCovered: true,
      quotedOnEgp: quotedOn, quotedOnDispenseNow: basis.onNow,
    };
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

  /**
   * Lines this fixture has been told the counter cannot fill, keyed `rxId:lineId`.
   *
   * <p>State, not a constant, because the second report must be able to answer `replayed: true` — the whole
   * behaviour worth having a fixture for. A stub that always said `replayed: false` would let a screen test
   * assert the "reported just now" message on a line a colleague had already flagged.</p>
   */
  #outOfStock = new Map<string, string>();

  flagOutOfStock(req: { prescriptionId: string; lineId: string; quantity?: number; note?: string }) {
    return this.gate(() => {
      const key = `${req.prescriptionId}:${req.lineId}`;
      const existing = this.#outOfStock.get(key);
      const at = existing ?? new Date().toISOString();
      this.#outOfStock.set(key, at);
      return ok(zOutOfStockResult, {
        lineId: req.lineId,
        flagged: true,
        // The server does not notify the prescriber a second time, and this says which happened.
        replayed: existing !== undefined,
        outOfStockAt: at,
      });
    });
  }

  // ---- Approvals ---------------------------------------------------------
  /**
   * The fulfilment register (ADR-0034) — what counters and benches actually handed over.
   *
   * <p>Kept apart from the review fixtures rather than mixed in, because that is exactly how the server
   * treats them: `kind` defaults to Review so a few hundred dispenses a day cannot drown the handful of
   * requests that need a decision.</p>
   */
  /** The fixture's own signed-in reviewer, so "assigned to me" has something to be true of. */
  private static readonly ME = "usr-reviewer-1";

  private static readonly FULFILMENTS = [
    {
      id: "AUTH-7101",
      patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
      service: { system: "ATC", code: "J01CR02", label: loc("Augmentin 1g", "أوجمنتين 1جم") },
      requestedBy: loc("Prescription", "وصفة"),
      priority: "routine" as const,
      // No SLA on a fulfilment: nothing waited on anybody.
      sla: null,
      status: { kind: "ok" as const, label: loc("Issued", "صادرة") },
      submittedAt: NOW,
      serviceCodes: [],
      assignedReviewerId: null,
      requestingProviderId: null,
      source: "Prescription" as const,
      itemReference: "RX-2026-000410",
      extensionReason: null,
      kind: "Fulfilment" as const,
    },
    {
      id: "AUTH-7102",
      patient: { id: "MRS-M-10555", token: "Y.H · •••7702" },
      service: { system: "LOINC", code: "58410-2", label: loc("Complete blood count", "تعداد دم كامل") },
      requestedBy: loc("Clinician order", "طلب طبيب"),
      priority: "routine" as const,
      // No SLA on a fulfilment: nothing waited on anybody.
      sla: null,
      status: { kind: "ok" as const, label: loc("Issued", "صادرة") },
      submittedAt: NOW,
      serviceCodes: [],
      assignedReviewerId: null,
      requestingProviderId: null,
      source: "OrderLine" as const,
      itemReference: "ORD-2026-055012",
      extensionReason: null,
      kind: "Fulfilment" as const,
    },
  ];

  /**
   * What was delivered against one authorization.
   *
   * <p>AUTH-7101 carries a SUBSTITUTION, which is the case worth seeing: the prescriber wrote one product,
   * the counter handed over another, and both are on the row. The prescription itself still says what the
   * prescriber wrote — that is the whole point of the authorization being a separate document.</p>
   */
  authorizationItems(authorizationId: string) {
    const byAuth: Record<string, unknown[]> = {
      "AUTH-7101": [
        {
          itemId: "AI-7101-1",
          sourceLineId: "RXL-1",
          orderedCode: "d-augmentin-1g",
          orderedLabel: "Augmentin 1g 14 f.c. tabs",
          fulfilledCode: "d-amox-clav-generic",
          fulfilledLabel: "Amoxicillin+Clavulanic acid 1g tabs",
          quantity: 14,
          substituted: true,
          substitutionReason: "Prescribed brand is out of stock this morning; same active ingredient dispensed.",
          fulfilledAt: NOW,
        },
        {
          itemId: "AI-7101-2",
          sourceLineId: "RXL-2",
          orderedCode: "d-paracetamol-500",
          orderedLabel: "Paracetamol 500mg tabs",
          fulfilledCode: "d-paracetamol-500",
          fulfilledLabel: "Paracetamol 500mg tabs",
          quantity: 20,
          substituted: false,
          substitutionReason: null,
          fulfilledAt: NOW,
        },
      ],
      "AUTH-7102": [
        {
          itemId: "AI-7102-1",
          sourceLineId: "OL-55012-1",
          orderedCode: "58410-2",
          orderedLabel: "Complete blood count",
          fulfilledCode: "58410-2",
          fulfilledLabel: "Complete blood count",
          quantity: 1,
          substituted: false,
          substitutionReason: null,
          fulfilledAt: NOW,
        },
      ],
    };
    return this.gate(() => ok(z.array(zAuthorizationItem), byAuth[authorizationId] ?? []), []);
  }

  approvalWorklist(kind: "Review" | "Fulfilment" | "All" = "Review", filter?: ApprovalQueueFilter) {
    /*
      The fixture applies the SAME filters the server does, so a screen that reaches the server correctly is
      also the screen the fixtures exercise. A fixture that ignores a filter lets a broken query pass — which
      is how the claims worklist came to be calling an endpoint that has no `status` parameter.

      `filter.status` is the one exception, and it is a limitation rather than an omission: an ApprovalItem
      carries a resolved status CHIP, not the raw `AuthStatus` the server filters on, so there is nothing here
      to compare against. Every review row in this fixture is awaiting a decision, so `status=Submitted` — the
      only value any screen sends — is already true of all of them.
    */
    const narrow = (rows: ApprovalItem[]) =>
      rows.filter((r) =>
        (!filter?.priority || r.priority === filter.priority)
        && (!filter?.slaBreached || r.sla?.breached === true)
        && (filter?.assignedTo !== "unassigned" || !r.assignedReviewerId)
        && (filter?.assignedTo !== "me" || r.assignedReviewerId === DevApiClient.ME));
    const page = (rows: ApprovalItem[]) => ({ rows: narrow(rows), total: narrow(rows).length });

    if (kind === "Fulfilment") {
      return this.gate(() => page(ok(z.array(zApprovalItem), DevApiClient.FULFILMENTS)), { rows: [], total: 0 });
    }
    const extra = kind === "All" ? DevApiClient.FULFILMENTS : [];
    return this.gate(
      () =>
        page(ok(z.array(zApprovalItem), [
          ...extra,
          {
            id: "AUTH-9001",
            patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
            service: { system: "CPT", code: "70553", label: loc("MRI brain w/ contrast", "رنين مغناطيسي للمخ") },
            requestedBy: loc("Clinician order", "طلب طبيب"),
            priority: "urgent",
            sla: { dueAt: "2026-07-22T12:00:00Z", breached: false, minutesRemaining: 210 },
            status: { kind: "info", label: loc("Awaiting review", "بانتظار المراجعة") },
            submittedAt: NOW,
            // Three requested services on one authorization. The queue used to show only the first and call
            // the other two "supporting codes", so a reviewer decided on a third of what was asked.
            serviceCodes: ["70553", "70551", "76377"],
            assignedReviewerId: null,
            requestingProviderId: "prv-nile-imaging",
            source: "OrderLine" as const,
            itemReference: null,
            extensionReason: null,
            kind: "Review" as const,
          },
          {
            id: "AUTH-9002",
            patient: { id: "MRS-M-10555", token: "Y.H · •••7702" },
            service: { system: "CPT", code: "29881", label: loc("Knee arthroscopy", "منظار الركبة") },
            requestedBy: loc("Clinician order", "طلب طبيب"),
            priority: "routine",
            sla: { dueAt: "2026-07-22T06:00:00Z", breached: true, minutesRemaining: -150 },
            status: { kind: "warn", label: loc("SLA breached", "تجاوز المهلة") },
            submittedAt: "2026-07-21T09:00:00Z",
            serviceCodes: ["29881"],
            assignedReviewerId: "usr-reviewer-1",
            requestingProviderId: "prv-cairo-ortho",
            source: "OrderLine" as const,
            itemReference: null,
            extensionReason: null,
            kind: "Review" as const,
          },
          // A validity-extension request. It carries no service code and no cost — which is exactly why the
          // queue has to say what KIND it is, or a reviewer opens it looking for both.
          {
            id: "AUTH-9003",
            patient: { id: "MRS-M-10231", token: "A.H · •••4821" },
            service: { system: "CPT", code: "—", label: loc("Validity extension", "تمديد الصلاحية") },
            requestedBy: loc("Validity extension request", "طلب تمديد صلاحية"),
            priority: "routine",
            sla: { dueAt: "2026-07-22T16:00:00Z", breached: false, minutesRemaining: 420 },
            status: { kind: "info", label: loc("Awaiting review", "بانتظار المراجعة") },
            submittedAt: NOW,
            serviceCodes: [],
            assignedReviewerId: null,
            requestingProviderId: null,
            source: "ValidityExtension" as const,
            itemReference: "RX-2026-000312",
            extensionReason: "Patient is mid-course and could not travel before it lapsed.",
            kind: "Review" as const,
          },
        ])),
      { rows: [], total: 0 },
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
        requestedServices: [
          { system: "CPT", code: "70553", label: loc("MRI brain w/ contrast", "رنين مغناطيسي للمخ") },
          { system: "CPT", code: "70551", label: loc("MRI brain w/o contrast", "رنين مغناطيسي للمخ بدون صبغة") },
          { system: "CPT", code: "76377", label: loc("3D rendering", "معالجة ثلاثية الأبعاد") },
        ],
        documents: [
          { id: "DOC-1", name: "neuro-exam.pdf" },
          { id: "DOC-2", name: "referral-letter.pdf" },
        ],
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

  /*
   * ---- The oversight reads (2026-08-11 audit) ----------------------------------------------------------
   *
   * Demo data with the SHAPE of a real clinic's month, not round numbers: a no-show rate that varies by
   * clinic, denial reasons with a long tail, an SLA queue whose oldest case is the urgent one. A fixture
   * where every figure is tidy teaches nobody what the screen is for.
   */
  serviceUse(axis: "provider" | "drug" | "lab" | "radiology", period?: { from: string; to: string }) {
    const rows: Record<string, Array<{ code: string; count: number }>> = {
      provider: [
        { code: "Nile Central Hospital", count: 412 },
        { code: "Maadi Family Clinic", count: 388 },
        { code: "Shubra Polyclinic", count: 241 },
        { code: "Helwan Diagnostic Centre", count: 96 },
      ],
      drug: [
        { code: "A10BA02 metformin", count: 604 },
        { code: "C09AA05 ramipril", count: 331 },
        { code: "J01CA04 amoxicillin", count: 288 },
        { code: "N02BE01 paracetamol", count: 173 },
      ],
      lab: [
        { code: "80053 comprehensive metabolic", count: 517 },
        { code: "85025 full blood count", count: 494 },
        { code: "83036 HbA1c", count: 208 },
      ],
      radiology: [
        { code: "71046 chest x-ray, 2 views", count: 129 },
        { code: "76700 abdominal ultrasound", count: 74 },
        { code: "70450 CT head", count: 19 },
      ],
    };
    return this.gate(() => ok(zServiceUseView, {
      dimension: axis,
      period: period ?? { from: "", to: "" },
      rows: rows[axis] ?? [],
    }));
  }

  slaBreaches() {
    return this.gate(() => ok(zSlaBreachView, {
      total: 3,
      rows: [
        { authNo: "AUTH-2026-0418", priority: "Emergency", status: "Submitted", ageBucket: ">3d", ageSeconds: 291_600, reviewerId: null },
        { authNo: "AUTH-2026-0455", priority: "Urgent", status: "UnderReview", ageBucket: "1-3d", ageSeconds: 154_800, reviewerId: "Hala Mansour" },
        { authNo: "AUTH-2026-0491", priority: "Urgent", status: "InfoRequested", ageBucket: "4-24h", ageSeconds: 61_200, reviewerId: "Hala Mansour" },
      ],
    }));
  }

  claimsCost(period?: { from: string; to: string }) {
    return this.gate(() => ok(zClaimsCostView, {
      period: period ?? { from: "", to: "" },
      decided: 284,
      totalAllowed: 418_930.5,
      byOutcome: [
        { outcome: "Approved", count: 201 },
        { outcome: "PartiallyApproved", count: 58 },
        { outcome: "Denied", count: 25 },
      ],
      byServiceLine: [
        { serviceLine: "CPT", amount: 236_400, count: 152 },
        { serviceLine: "DRUG", amount: 121_180.5, count: 96 },
        { serviceLine: "LOINC", amount: 61_350, count: 36 },
      ],
      topDenialReasons: [
        { reasonCode: "NOT_COVERED", count: 9 },
        { reasonCode: "NO_PRIOR_AUTH", count: 7 },
        { reasonCode: "INSUFFICIENT_DOCS", count: 5 },
        { reasonCode: "DUPLICATE_CLAIM", count: 4 },
      ],
    }));
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
            state: "active",
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
            state: "open",
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
            state: "raised",
            status: { kind: "warn", label: loc("Escalated", "مُصعَّدة") },
            raisedAt: "2026-07-20T08:00:00Z",
            resolvedAt: null,
            resolutionNote: null,
          },
          {
            id: "ESC-2",
            caseId: "CASE-2026-000042",
            caseNo: "CASE-2026-000042",
            raisedToRole: loc("Medical Director", "المدير الطبي"),
            reason: "Repeat refusal of the same imaging request — needs a policy read.",
            state: "acknowledged",
            status: { kind: "info", label: loc("Acknowledged", "مُستلَمة") },
            raisedAt: "2026-07-19T13:20:00Z",
            resolvedAt: null,
            resolutionNote: null,
          },
          {
            // Three states, three chips. Every row used to render the same amber "Escalated", so a register
            // whose purpose is showing what is outstanding showed everything as outstanding forever.
            id: "ESC-3",
            caseId: "CASE-2026-000042",
            caseNo: "CASE-2026-000042",
            raisedToRole: loc("Medical Approval", "الموافقة الطبية"),
            reason: "Pharmacy substitution disputed by the prescriber.",
            state: "resolved",
            status: { kind: "ok", label: loc("Resolved", "مُغلقة") },
            raisedAt: "2026-07-11T10:05:00Z",
            resolvedAt: "2026-07-12T09:40:00Z",
            resolutionNote: "Prescriber accepted the substitution after the formulary note.",
          },
        ]),
      [],
    );
  }
  updateCaseTask(caseId: string, taskId: string, state: TaskState, outcomeNote?: string) {
    void caseId; void taskId; void state; void outcomeNote;
    return this.gate(() => undefined, undefined);
  }
  raiseEscalation(caseId: string, raisedToRole: string, reason: string, idempotencyKey?: string) {
    void caseId; void raisedToRole; void reason; void idempotencyKey;
    return this.gate(() => undefined, undefined);
  }
  updateEscalation(caseId: string, escalationId: string, state: EscalationState, resolutionNote?: string) {
    void caseId; void escalationId; void state; void resolutionNote;
    return this.gate(() => undefined, undefined);
  }
  setCaseState(caseId: string, state: CaseState) {
    void caseId; void state;
    return this.gate(() => undefined, undefined);
  }

  // ---- Finance (Phase 10.2) — billing codes + amounts only, no diagnosis --------------------------------
  utilization(_period?: Period) {
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
  /**
   * The settlement list.
   *
   * <p>Kept as MUTABLE STATE on the instance rather than a literal, because the three lifecycle writes now
   * exist and a fixture that ignored them would let a screen test "approve" a settlement and then assert
   * against a list that had never heard of it. A fixture speaks the service's vocabulary or it is a second
   * implementation of the bug — and a fixture that cannot move through a state machine the screen drives is
   * the same mistake one layer up.</p>
   */
  #settlements: Settlement[] = DEV_SETTLEMENTS.map((s) => ({ ...s, lines: s.lines.map((l) => ({ ...l })) }));

  settlements(filter?: { providerId?: string; status?: string }) {
    const rows = this.#settlements.filter(
      (s) => (!filter?.status || s.state === filter.status.toLowerCase())
        && (!filter?.providerId || s.providerRef === filter.providerId),
    );
    return this.gate(() => ok(zSettlementPage, { rows, total: rows.length }), { rows: [], total: 0 });
  }

  generateSettlement(req: GenerateSettlementRequest) {
    const seq = 8 + this.#settlements.length - DEV_SETTLEMENTS.length;
    const created = {
      id: `stl-dev-${seq}`,
      settlementNo: `STL-2026-${String(seq).padStart(6, "0")}`,
      providerRef: req.providerId,
      providerName: loc("Nile Imaging Center", "مركز النيل للأشعة"),
      periodStart: req.periodStart,
      periodEnd: req.periodEnd,
      currency: "EGP",
      total: 4300,
      status: SETTLEMENT_CHIP.draft,
      state: "draft" as const,
      submittedBy: null,
      approvedBy: null,
      // One contract-priced line and one floor-priced one, so the unpriced count on the screen is exercised
      // by something other than zero. A settlement in which every line is a guess and one in which none is
      // are the two cases a reviewer treats differently.
      lines: [
        { serviceCode: "70553", serviceLine: loc("Imaging", "أشعة"), deliveredQty: 4, agreedUnitPrice: 800, lineTotal: 3200, priceSource: "Contract" as const },
        { serviceCode: "70554", serviceLine: loc("Imaging", "أشعة"), deliveredQty: 2, agreedUnitPrice: 550, lineTotal: 1100, priceSource: "ObservedFloor" as const },
      ],
    };
    this.#settlements = [created, ...this.#settlements];
    return this.gate(() => ok(zSettlement, created));
  }

  submitSettlement(id: string) {
    return this.gate(() => ok(zSettlement, this.#move(id, "submitted", { submittedBy: DEV_SUBJECT })));
  }

  approveSettlement(id: string) {
    // SoD is the SERVER's rule and this fixture does not re-implement it — the screen withholds the button
    // from the submitter using `submittedBy`, and the test that proves it asserts on the screen, not here.
    return this.gate(() => ok(zSettlement, this.#move(id, "approved", { approvedBy: DEV_APPROVER })));
  }

  #move(id: string, state: "submitted" | "approved", who: { submittedBy?: string; approvedBy?: string }) {
    const next = this.#settlements.map((s) =>
      s.id === id ? { ...s, state, status: SETTLEMENT_CHIP[state], ...who } : s,
    );
    this.#settlements = next;
    const moved = next.find((s) => s.id === id);
    if (!moved) throw new ApiError("http", `No settlement ${id}`, 404);
    return moved;
  }

  financialSummary(dimension: "serviceline" | "category" | "provider", _period?: Period) {
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
        // CSV, always — the only thing the endpoint has ever produced. The XLSX segment is gone from the
        // screen and the schema; this fixture used to echo whatever was asked for, which is exactly how the
        // lie stayed invisible in every test.
        format: "csv",
        rowCount: 3,
        filename: `${req.report}-${req.from}_${req.to}.csv`,
        status: { kind: "ok", label: loc("Export ready (audited)", "التصدير جاهز (مُدقّق)") },
      }),
    );
  }

  // ---- Claims management (Phase 10b) — codes + amounts only, no diagnosis (finance-parity) --------------
  claimsWorklist(status?: string, take?: number) {
    // THE STATUSES ARE THE REAL ONES. This fixture served "Adjudicated" and "Rejected", which are not members
    // of ClaimStatus — the same wrong vocabulary the screen's filter and the chip map used, which is how a
    // worklist reading the wrong endpoint entirely went unnoticed: nothing in the client, the fixtures or the
    // tests ever spoke the service's language.
    const rows = [
      { id: "clm-1", claimNo: "CLM-2026-004411", origin: "ProviderSubmitted", key: "Submitted", claimed: 3200, net: null, from: "2026-07-18", at: "2026-07-19T09:00:00Z" },
      { id: "clm-2", claimNo: "CLM-2026-004412", origin: "AutoDerived", key: "UnderAdjudication", claimed: 1450, net: null, from: "2026-07-15", at: "2026-07-16T11:30:00Z" },
      { id: "clm-3", claimNo: "CLM-2026-004413", origin: "Reimbursement", key: "Denied", claimed: 900, net: 0, from: "2026-07-12", at: "2026-07-13T08:15:00Z" },
      { id: "clm-4", claimNo: "CLM-2026-004414", origin: "ProviderSubmitted", key: "PartiallyApproved", claimed: 5400, net: 4100, from: "2026-07-10", at: "2026-07-11T14:00:00Z" },
      { id: "clm-5", claimNo: "CLM-2026-004415", origin: "AutoDerived", key: "Settled", claimed: 780, net: 780, from: "2026-07-04", at: "2026-07-05T10:20:00Z" },
    ].filter((r) => !status || r.key === status).slice(0, take ?? 200);
    return this.gate(
      () =>
        ok(z.array(zClaimRow), rows.map((r) => ({
          id: r.id, claimNo: r.claimNo, origin: r.origin, status: CLAIM_STATUS_CHIP[r.key], currency: "EGP",
          claimedAmount: r.claimed, netPayable: r.net, serviceDateFrom: r.from, submittedAt: r.at,
        }))),
      [],
    );
  }

  claimDetail(claimId: string) {
    return this.gate(() =>
      ok(zClaimDetail, {
        id: claimId,
        claimNo: "CLM-2026-004412",
        origin: "AutoDerived",
        status: CLAIM_STATUS_CHIP.UnderAdjudication,
        currency: "EGP",
        claimedAmount: 1450,
        approvedAmount: null,
        adjustedAmount: null,
        netPayable: null,
        serviceDateFrom: "2026-07-15",
        submittedAt: "2026-07-16T11:30:00Z",
        lines: [
          {
            claimLineId: "cl-1", codeSystem: "CPT", code: "80053",
            description: "Comprehensive metabolic panel", quantity: 1,
            billedAmount: 320, contractPrice: 300, allowedAmount: null,
            status: CLAIM_LINE_CHIP.Pending, reasonCodes: [],
          },
          {
            claimLineId: "cl-2", codeSystem: "CPT", code: "71046",
            description: "Chest X-ray, 2 views", quantity: 1,
            billedAmount: 800, contractPrice: 640, allowedAmount: null,
            status: CLAIM_LINE_CHIP.Pending, reasonCodes: ["NO_PRIOR_AUTH"],
          },
          {
            claimLineId: "cl-3", codeSystem: "CPT", code: "85025",
            description: "Complete blood count", quantity: 2,
            billedAmount: 330, contractPrice: 150, allowedAmount: 150,
            status: CLAIM_LINE_CHIP.Adjusted, reasonCodes: ["LIMIT_EXCEEDED"],
          },
        ],
      }),
    );
  }

  claimAdjustments(_claimId: string) {
    return this.gate(
      () =>
        ok(z.array(zClaimAdjustment), [
          {
            adjustmentId: "adj-1", claimLineId: "cl-3", type: "QuantityCorrection",
            amountDelta: -180, beforeAmount: 330, afterAmount: 150,
            reasonCode: "LIMIT_EXCEEDED", adjustedAt: "2026-07-17T09:40:00Z",
          },
        ]),
      [],
    );
  }

  adjudicationQueue(filter?: { recommendation?: string; reasonCode?: string; minValue?: number; maxValue?: number }) {
    const rows = [
      { claimId: "clm-2", claimNo: "CLM-2026-004412", claimLineId: "cl-1", date: "2026-07-15", sys: "CPT", code: "80053", desc: "Comprehensive metabolic panel", qty: 1, billed: 320, contract: 300, rec: "Approve", reasons: [] as string[], res: true },
      { claimId: "clm-2", claimNo: "CLM-2026-004412", claimLineId: "cl-2", date: "2026-07-15", sys: "CPT", code: "71046", desc: "Chest X-ray, 2 views", qty: 1, billed: 800, contract: 640, rec: "RequiresManualReview", reasons: ["NO_PRIOR_AUTH"], res: true },
      // No fulfilment record: the officer can see the service was never rendered without reading anything
      // clinical, which is the whole point of `resultExists` being a boolean.
      { claimId: "clm-6", claimNo: "CLM-2026-004430", claimLineId: "cl-9", date: "2026-07-14", sys: "CPT", code: "93000", desc: "Electrocardiogram", qty: 1, billed: 450, contract: 400, rec: "Deny", reasons: ["NO_FULFILLMENT_RECORD"], res: false },
    ].filter((r) =>
      (!filter?.recommendation || r.rec === filter.recommendation)
      && (!filter?.reasonCode || r.reasons.includes(filter.reasonCode))
      && (filter?.minValue === undefined || r.billed >= filter.minValue)
      && (filter?.maxValue === undefined || r.billed <= filter.maxValue));
    return this.gate(
      () =>
        ok(z.array(zAdjudicationRow), rows.map((r) => ({
          claimId: r.claimId, claimNo: r.claimNo, claimLineId: r.claimLineId, serviceDate: r.date,
          codeSystem: r.sys, code: r.code, description: r.desc, quantity: r.qty,
          billedAmount: r.billed, contractPrice: r.contract, allowedAmount: null,
          status: CLAIM_LINE_CHIP.Pending, systemRecommendation: r.rec, reasonCodes: r.reasons,
          authorizationId: null, resultExists: r.res,
        }))),
      [],
    );
  }

  decideClaimLine(req: ClaimDecisionRequest, idempotencyKey?: string) {
    return this.gate(() => {
      const key = idempotencyKey ?? `${req.claimId}:${req.claimLineId}:${req.decision}`;
      if (this.seenKeys.has(key)) {
        return ok(zClaimDecisionResult, { outcome: "Replayed", decisionId: `dec-${req.claimLineId}` });
      }
      this.seenKeys.add(key);
      // Dual control above the threshold. Modelled here because it is an OUTCOME the screen has to render as
      // "waiting for a second approver" rather than as a failure — a fixture that never produces it would let
      // the screen ship treating a 202 as an error.
      const amount = req.allowedAmount ?? 0;
      if (amount > 5000 && !req.confirmsDecisionId) {
        return ok(zClaimDecisionResult, { outcome: "PendingSecondApproval", decisionId: `dec-${req.claimLineId}` });
      }
      return ok(zClaimDecisionResult, {
        outcome: req.confirmsDecisionId ? "Confirmed" : "Recorded",
        decisionId: `dec-${req.claimLineId}`,
        lineStatus: req.decision === "Deny" ? "Denied" : req.decision === "Approve" ? "Approved" : "PartiallyApproved",
        claimStatus: "UnderAdjudication",
        allowedAmount: req.allowedAmount ?? null,
      });
    });
  }

  raiseClaimAdjustment(
    input: { claimId: string; claimLineId: string; type: string; amountDelta: number; reasonCode?: string; rationale?: string },
    _idempotencyKey?: string,
  ) {
    return this.gate(() =>
      ok(zClaimAdjustment, {
        adjustmentId: `adj-${input.claimLineId}`,
        claimLineId: input.claimLineId,
        type: input.type,
        amountDelta: input.amountDelta,
        beforeAmount: null,
        afterAmount: null,
        reasonCode: input.reasonCode ?? null,
        adjustedAt: NOW,
      }),
    );
  }

  claimsReconciliation(bucket?: string, _period?: Period) {
    // ALL SIX BUCKETS have a row. Three of them — Duplicate, DeliveredNotBilled, QuantityVariance — were being
    // classified by the server and could not be selected in the portal or produced by this fixture, so the
    // double-billing signal and the revenue-leakage signal had never once been rendered.
    const rows = [
      { claimId: "clm-1", claimLineId: "cl-11", claimNo: "CLM-2026-004411", origin: "ProviderSubmitted", code: "80053", date: "2026-07-18", billed: 320, allowed: 300, bucket: "PriceVariance" },
      { claimId: "clm-4", claimLineId: "cl-12", claimNo: "CLM-2026-004420", origin: "AutoDerived", code: "71046", date: "2026-07-17", billed: 800, allowed: null, bucket: "BilledNotDelivered" },
      { claimId: "clm-5", claimLineId: "cl-13", claimNo: "CLM-2026-004421", origin: "ProviderSubmitted", code: "85025", date: "2026-07-16", billed: 150, allowed: 150, bucket: "Matched" },
      { claimId: "clm-7", claimLineId: "cl-14", claimNo: "CLM-2026-004425", origin: "ProviderSubmitted", code: "85025", date: "2026-07-16", billed: 330, allowed: 150, bucket: "QuantityVariance" },
      { claimId: "clm-8", claimLineId: "cl-15", claimNo: "CLM-2026-004428", origin: "AutoDerived", code: "93000", date: "2026-07-15", billed: 0, allowed: 400, bucket: "DeliveredNotBilled" },
      { claimId: "clm-9", claimLineId: "cl-16", claimNo: "CLM-2026-004429", origin: "ProviderSubmitted", code: "80053", date: "2026-07-18", billed: 320, allowed: null, bucket: "Duplicate" },
    ].filter((r) => !bucket || r.bucket === bucket);
    return this.gate(
      () =>
        ok(z.array(zReconciliationRow), rows.map((r) => ({
          claimId: r.claimId, claimLineId: r.claimLineId, claimNo: r.claimNo, origin: r.origin,
          code: r.code, serviceDate: r.date, billedAmount: r.billed, allowedAmount: r.allowed,
          bucket: r.bucket, status: RECON_CHIP[r.bucket],
        }))),
      [],
    );
  }

  claimsKpis(_period?: Period) {
    return this.gate(() =>
      ok(zClaimsKpis, {
        averageTatHours: 34.5, approvalRate: 0.82, denialRate: 0.11, ocrAutoMatchRate: 0.74,
        agedUnbilledCount: 12, agedUnbilledValue: 41250, recoveryOutstanding: 8900,
        topDenialReasons: [
          { reason: "NO_PRIOR_AUTH", count: 7 },
          { reason: "NOT_COVERED_CATEGORY", count: 4 },
          { reason: "DUPLICATE_CLAIM", count: 3 },
        ],
      }),
    );
  }

  // ---- Break-glass retrospective review ------------------------------------------------------------------
  //
  // The queue this backs has existed since 7.3 with no screen and no way to close a case: nothing anywhere
  // assigned `retrospectiveReviewed`. The fixture below deliberately includes one case FOUR MONTHS OLD,
  // because the failure this control had was not that the list was wrong — it was that nobody could see it
  // ageing.
  private retroReviewed = new Map<string, { outcome: "Upheld" | "NotJustified"; rationale: string }>();

  private static readonly RETRO_OPEN = [
    {
      authorizationId: "AUTH-8801", authNo: "AUTH-2026-000881",
      beneficiaryId: "MRS-M-10231", serviceCodes: ["70553"], source: "Manual",
      decidedAt: "2026-04-02T08:00:00Z", ageDays: 131,
    },
    {
      authorizationId: "AUTH-8802", authNo: "AUTH-2026-000902",
      beneficiaryId: "MRS-M-10555", serviceCodes: ["29881", "29880"], source: "OrderLine",
      decidedAt: "2026-08-04T15:20:00Z", ageDays: 7,
    },
    {
      authorizationId: "AUTH-8803", authNo: "AUTH-2026-000915",
      beneficiaryId: "MRS-M-10902", serviceCodes: ["93000"], source: "Manual",
      decidedAt: "2026-08-09T11:05:00Z", ageDays: 2,
    },
  ];

  retrospectiveQueue(closed?: boolean) {
    const chip = { kind: "warn" as const, label: loc("Emergency approved", "اعتماد طارئ") };
    const open = DevApiClient.RETRO_OPEN.filter((r) => !this.retroReviewed.has(r.authorizationId));
    const done = DevApiClient.RETRO_OPEN.filter((r) => this.retroReviewed.has(r.authorizationId));
    const rows = (closed ? done : open).map((r) => {
      const review = this.retroReviewed.get(r.authorizationId);
      return {
        ...r, status: chip, reviewed: !!review,
        outcome: review?.outcome ?? null,
        reviewedAt: review ? NOW : null,
        reviewedBy: review ? DevApiClient.ME : null,
        rationale: review?.rationale ?? null,
      };
    });
    return this.gate(() => ok(z.array(zRetrospectiveItem), rows), []);
  }

  completeRetrospectiveReview(input: RetrospectiveReviewInput, _idempotencyKey?: string) {
    return this.gate(() => {
      this.retroReviewed.set(input.authorizationId, { outcome: input.outcome, rationale: input.rationale });
      const row = DevApiClient.RETRO_OPEN.find((r) => r.authorizationId === input.authorizationId);
      return ok(zRetrospectiveItem, {
        authorizationId: input.authorizationId,
        authNo: row?.authNo ?? "",
        beneficiaryId: row?.beneficiaryId ?? "MRS-M-10231",
        serviceCodes: row?.serviceCodes ?? [],
        source: row?.source ?? "Manual",
        status: { kind: "warn" as const, label: loc("Emergency approved", "اعتماد طارئ") },
        decidedAt: row?.decidedAt ?? null,
        ageDays: row?.ageDays ?? 0,
        reviewed: true,
        outcome: input.outcome,
        reviewedAt: NOW,
        reviewedBy: DevApiClient.ME,
        rationale: input.rationale,
      });
    });
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
      () => ok(z.array(zAccessReviewCampaign), [
        {
          id: "CAMP-1", name: "Q3 2026 high-sensitivity access recertification",
          status: { kind: "info", label: loc("Open", "مفتوحة") }, minTier: "T3", dueAt: "2026-08-05T00:00:00Z",
          // Three still pending on a campaign that is already due: the state the sweep exists for, and the
          // one the row could not show while the counts were dropped in the mapping.
          total: 5, pending: 3, recertified: 1, revoked: 1, autoExpired: 0,
        },
        {
          id: "CAMP-2", name: "Q2 2026 high-sensitivity access recertification",
          status: { kind: "neu", label: loc("Closed", "مغلقة") }, minTier: "T3", dueAt: "2026-05-05T00:00:00Z",
          // Closed — and three of the four were removed by the deadline passing rather than by anybody
          // deciding. One person's access was actually reviewed.
          total: 4, pending: 0, recertified: 1, revoked: 0, autoExpired: 3,
        },
      ]),
      [],
    );
  }
  accessReviewItems(campaignId: string) {
    return this.gate(
      () => ok(z.array(zAccessReviewItem), campaignId === "CAMP-1"
        ? [
            { id: "ITEM-1", bindingId: "RB-1", subjectUserId: "u-5", subjectName: "Sara Ibrahim", role: "doctor", decision: "pending", decidedBy: null, decidedAt: null, note: null },
            { id: "ITEM-2", bindingId: "RB-2", subjectUserId: "u-2", subjectName: "Mona Adel", role: "medical_approval", decision: "pending", decidedBy: null, decidedAt: null, note: null },
            // The name deliberately absent on one row: identity could not label this subject, and the
            // worklist still has to render it rather than dropping a grant nobody would then review.
            { id: "ITEM-3", bindingId: "RB-3", subjectUserId: "u-9", subjectName: null, role: "super_admin", decision: "pending", decidedBy: null, decidedAt: null, note: null },
            { id: "ITEM-4", bindingId: "RB-4", subjectUserId: "u-1", subjectName: "Org Admin", role: "org_admin", decision: "recertified", decidedBy: "u-1", decidedAt: "2026-08-01T09:00:00Z", note: "Confirmed with operations." },
            { id: "ITEM-5", bindingId: "RB-5", subjectUserId: "u-7", subjectName: "Hana Youssef", role: "finance", decision: "revoked", decidedBy: "u-1", decidedAt: "2026-08-01T09:05:00Z", note: "Left the finance team in June." },
          ]
        : []),
      [],
    );
  }
  recertifyAccessItem(itemId: string, note?: string) {
    void itemId; void note;
    return this.gate(() => undefined, undefined);
  }
  revokeAccessItem(itemId: string, note?: string) {
    void itemId; void note;
    return this.gate(() => undefined, undefined);
  }
  sweepAccessCampaign(campaignId: string) {
    void campaignId;
    return this.gate(() => ({ autoExpired: 3 }), { autoExpired: 0 });
  }
  breakGlassGrants() {
    return this.gate(
      () => ok(z.array(zBreakGlassGrant), [
        {
          id: "BG-1", requesterToken: "•••8a91", approverToken: "•••1111", reasonCode: "EmergencyCare",
          status: { kind: "neu", label: loc("Expired", "منتهٍ") },
          requestedAt: "2026-07-20T02:00:00Z", expiresAt: "2026-07-20T03:00:00Z",
          // Eleven uses, four of them outside what the grant covered, and nobody has reviewed it since it
          // lapsed. This is the row the dashboard exists to surface and could not previously describe.
          accessCount: 11, outOfScopeCount: 4, postReviewDone: false,
        },
        {
          id: "BG-2", requesterToken: "•••4f2a", approverToken: null, reasonCode: "UnconsciousPatient",
          status: { kind: "info", label: loc("Requested", "مطلوب") },
          requestedAt: "2026-08-21T06:40:00Z", expiresAt: undefined,
          accessCount: 0, outOfScopeCount: 0, postReviewDone: false,
        },
      ]),
      [],
    );
  }
  approveBreakGlass(grantId: string) {
    void grantId;
    return this.gate(() => undefined, undefined);
  }
  rejectBreakGlass(grantId: string, reason: string) {
    void grantId; void reason;
    return this.gate(() => undefined, undefined);
  }
  sodViolations() {
    return this.gate(
      () => ok(z.array(zSodViolation), [
        {
          subjectUserId: "u-5", subjectName: "Sara Ibrahim", heldRole: "doctor", conflictingRole: "medical_approval",
          reason: "Self-approval of own clinical request",
        },
      ]),
      [],
    );
  }
  networkMetrics() {
    return this.gate(
      // The same three providers the directory fixture carries, counted by the SERVICE's own definition:
      // one suspended, none terminated. Kept in step deliberately — a roll-up that disagreed with the
      // directory beside it would be a fixture that hides exactly the drift this endpoint exists to end.
      () => ok(zNetworkMetrics, { total: 3, active: 2, suspended: 1, terminated: 0 }),
      { total: 0, active: 0, suspended: 0, terminated: 0 },
    );
  }
  providerMetrics(providerId: string) {
    return this.gate(
      () => ok(zProviderMetrics, {
        providerId,
        status: "Active",
        activeContracts: 2,
        servicesOffered: 41,
        credentials: { valid: 5, expiringSoon: 1, expired: 0 },
        // Null, not zero — the fulfillment events that populate it land in phases 5/6.
        ordersFulfilled: 0,
        avgTurnaroundHours: null,
      }),
      {
        providerId, status: "", activeContracts: 0, servicesOffered: 0,
        credentials: { valid: 0, expiringSoon: 0, expired: 0 },
        ordersFulfilled: 0, avgTurnaroundHours: null,
      },
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
            { orderRef: "ORD-2026-7741", lineId: "line-1", category: "Haematology", orderedOn: "2026-07-02T09:20:00Z", status: "Resulted", providerName: "Central Lab", resultSummary: "Hb 11.2 g/dL — mild anaemia", restricted: false, orderType: "Lab" },
            // Existence-only: the owning service never sent a value, and the row says why rather than looking
            // like a result that has not come back yet (design 37 §6).
            { orderRef: "ORD-2026-7802", lineId: "line-2", category: "Serology", orderedOn: "2026-07-22T11:00:00Z", status: "Resulted", providerName: "Central Lab", restricted: true, sensitivityLevel: "High", orderType: "Lab" },
            // 29.2 — an OP procedure in the SAME section, so the History tab's Procedures pane has something
            // to show and the axe sweep has something to look at. A fixture that omits it makes the pane
            // render its empty state in every screenshot and every accessibility run.
            { orderRef: "ORD-2026-7901", lineId: "line-4", category: "Therapeutic exercise", orderedOn: "2026-07-25T10:00:00Z", status: "PartiallyUsed", providerName: "Cairo Physiotherapy Centre", resultSummary: "4 of 6 sessions delivered", restricted: false, orderType: "Procedure" },
            { orderRef: "ORD-2026-7855", lineId: "line-3", category: "Chemistry", orderedOn: "2026-07-28T08:45:00Z", status: "Ordered", providerName: "Central Lab", restricted: false, orderType: "Lab" },
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

    /**
     * The ROLE PROJECTION, mirrored from the server (`libs/authz/ProfilePolicies.cs`, drift-checked by
     * `test/profile-fixture-matrix.test.ts`).
     *
     * Without this the fixture answered every caller with all fifteen sections, so the dev build showed a
     * receptionist the prescriptions and investigation results production withholds from them — and, worse,
     * the screen's behaviour under a real projection was never exercised anywhere a test could see it.
     *
     * A section with NO CELL for the caller's roles is omitted entirely rather than sent withheld, because
     * that is what the server does: `DecideAll` drops it, and the profile screen's tab grouping is written
     * against that (a tab whose sections are all absent does not render at all). A `res` cell is sent as
     * Restricted, so the "you may ask for access" state stays reachable in dev.
     *
     * No roles known — a client constructed without a session, which is most unit tests — means no
     * projection, not an empty profile. See `ApiScenario.roles`.
     */
    const roles = this.scenario.roles?.() ?? [];
    const project = (s: ProfileSection): ProfileSection | null => {
      if (roles.length === 0) return s;
      const state = sectionStateFor(roles, s.key);
      if (state === null) return null;
      if (state === "Restricted" && s.state !== "Restricted") {
        return { key: s.key, state: "Restricted", reasonCode: "role-not-permitted" };
      }
      return s;
    };

    return this.gate(() =>
      ok(zPatientProfile, {
        beneficiaryId,
        servedAt: new Date().toISOString(),
        sections: all
          .map((s) => withheld[s.key] ?? s)
          .map(project)
          .filter((s): s is ProfileSection => s !== null)
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
    return this.gate(() => ok(z.array(zMasterDataVersion), this.mdVersions), []);
  }
  /**
   * Append a version, in memory.
   *
   * <p>Models the real semantics rather than a success stub: the code's version number goes UP and the prior
   * entry stays in `mdVersions`. A dev client that returned `{ ok: true }` would let an editor that quietly
   * overwrote history look correct here and be wrong against the server — which is the trap the `lineId`
   * pricing bug fell into, where the fixture agreed with the client instead of with the wire.</p>
   */
  private mdVersions: { id: string; system: string; code: string; versionNo: number; retired: boolean; effectiveFrom: string; rationale?: string }[] = [
    { id: "MDV-1", system: "Icd10", code: "E11.9", versionNo: 2, retired: false, effectiveFrom: "2026-01-01T00:00:00Z", rationale: "Annual ICD refresh" },
    { id: "MDV-2", system: "Atc", code: "A10BA02", versionNo: 1, retired: false, effectiveFrom: "2026-01-01T00:00:00Z", rationale: "Initial load" },
  ];

  adminMasterDataUpsert(edit: MasterDataEdit) {
    return this.gate(() => {
      const prior = this.mdVersions.find((v) => v.system === edit.system && v.code === edit.code);
      const versionNo = (prior?.versionNo ?? 0) + 1;
      const id = `MDV-${this.mdVersions.length + 1}`;
      this.mdVersions = [
        ...this.mdVersions.filter((v) => !(v.system === edit.system && v.code === edit.code)),
        { id, system: edit.system, code: edit.code, versionNo, retired: edit.retired,
          effectiveFrom: new Date().toISOString(), rationale: edit.rationale },
      ];
      return { id, code: edit.code, versionNo };
    });
  }

  adminMasterDataAsOf(system: string, code: string, at: string) {
    // `at` is unread here on purpose: the fixture holds one version per code, so there is nothing to resolve
    // against a date. The SERVER resolves properly — pretending to here would invent a history the dev client
    // does not have and hide the difference.
    void at;
    return this.gate(() =>
      ok(zMasterDataAsOf, {
        id: this.mdVersions.find((v) => v.system === system && v.code === code)?.id ?? "MDV-0",
        versionNo: this.mdVersions.find((v) => v.system === system && v.code === code)?.versionNo ?? 1,
        // A plausible attribute set so the editor's diff has two sides to compare.
        attributes: { title: "Type 2 diabetes mellitus without complications", chronic: true, billable: true },
        effectiveFrom: "2026-01-01T00:00:00Z",
        effectiveTo: null,
      }),
    );
  }

  /**
   * The document policy, in memory. Mixed configured/unconfigured on purpose: "365 because we chose 365" and
   * "365 because nobody has looked" are different states, and a fixture where everything is configured would
   * let a screen that never renders the distinction look finished.
   */
  private docValidity = [
    { kind: "RefugeeId", key: "document-validity.refugee-id.days", days: 730, warnDays: [90, 60, 30], configured: true, warnConfigured: false, identity: true, updatedAt: "2026-07-01T00:00:00Z" },
    { kind: "NationalId", key: "document-validity.national-id.days", days: 365, warnDays: [90, 60, 30], configured: false, warnConfigured: false, identity: true, updatedAt: null },
    { kind: "Passport", key: "document-validity.passport.days", days: 3650, warnDays: [180, 90], configured: true, warnConfigured: true, identity: true, updatedAt: "2026-06-12T00:00:00Z" },
    { kind: "UnhcrNo", key: "document-validity.unhcr-no.days", days: 365, warnDays: [90, 60, 30], configured: false, warnConfigured: false, identity: true, updatedAt: null },
    { kind: "PractitionerLicence", key: "document-validity.practitioner-licence.days", days: 365, warnDays: [90, 60, 30], configured: false, warnConfigured: false, identity: false, updatedAt: null },
    { kind: "FacilityAccreditation", key: "document-validity.facility-accreditation.days", days: 1095, warnDays: [90, 60, 30], configured: true, warnConfigured: false, identity: false, updatedAt: "2026-05-02T00:00:00Z" },
    { kind: "ProviderContract", key: "document-validity.provider-contract.days", days: 365, warnDays: [90, 60, 30], configured: false, warnConfigured: false, identity: false, updatedAt: null },
  ];

  /**
   * The engine's rules, in memory.
   *
   * <p>The fixture carries a SUPERSEDED version on purpose (`effectiveTo` set). A screen that only ever saw
   * live rules would have no way to show "why did this go there last week", which is most of what the
   * effective dating is for.</p>
   */
  private rules: ApprovalRule[] = [
    { id: "R-1", family: "Routing", priority: 10,
      predicate: JSON.stringify({ priority: "Emergency" }), action: JSON.stringify({ queue: "escalation" }),
      effectiveFrom: "2026-07-01T00:00:00Z", effectiveTo: null, versionNo: 2, enabled: true,
      authoredBy: "medical_director", rationale: "Emergencies go to the on-call desk, not the general queue." },
    { id: "R-0", family: "Routing", priority: 10,
      predicate: JSON.stringify({ priority: "Emergency" }), action: JSON.stringify({ queue: "clinical" }),
      effectiveFrom: "2026-05-01T00:00:00Z", effectiveTo: "2026-07-01T00:00:00Z", versionNo: 1, enabled: true,
      authoredBy: "medical_director", rationale: "Initial routing." },
    { id: "R-2", family: "Routing", priority: 50,
      predicate: JSON.stringify({ source: "Prescription" }), action: JSON.stringify({ queue: "pharmacy" }),
      effectiveFrom: "2026-07-01T00:00:00Z", effectiveTo: null, versionNo: 1, enabled: true,
      authoredBy: "medical_director", rationale: "Pharmacy questions are answered by the pharmacy reviewers." },
    { id: "R-3", family: "Sla", priority: 10,
      predicate: JSON.stringify({ priority: "Emergency" }), action: JSON.stringify({ hours: 1 }),
      effectiveFrom: "2026-07-01T00:00:00Z", effectiveTo: null, versionNo: 1, enabled: true,
      authoredBy: "medical_director", rationale: "An emergency that waits four hours is not being treated as one." },
    // A pre-auth trigger. Narrow on purpose — a catch-all here would gate every act of care on the platform,
    // and the server refuses one.
    { id: "R-4", family: "Preauth", priority: 10,
      predicate: JSON.stringify({ benefitCategory: "IMAGING", amountAtLeast: 5000 }),
      action: JSON.stringify({ reason: "Imaging over EGP 5,000 is reviewed before it is performed." }),
      effectiveFrom: "2026-07-01T00:00:00Z", effectiveTo: null, versionNo: 1, enabled: true,
      authoredBy: "medical_director", rationale: "High-cost imaging was the largest source of retrospective denials." },
  ];

  /** OFF by default, like a tenant that has never touched it — which is the state that matters most. */
  private autoSwitch = {
    enabled: false,
    reason: "Auto-decision has never been turned on for this tenant.",
    updatedBy: null as string | null,
    updatedAt: null as string | null,
    hardMaximumEgp: 5000,
  };

  autoDecisionSwitch() {
    return this.gate(() => ok(zAutoDecisionSwitch, this.autoSwitch));
  }

  setAutoDecision(req: SetAutoDecision) {
    return this.gate(() => {
      this.autoSwitch = {
        ...this.autoSwitch,
        enabled: req.enabled,
        reason: req.reason,
        updatedBy: "medical_director",
        updatedAt: new Date().toISOString(),
      };
      return ok(zAutoDecisionSwitch, this.autoSwitch);
    });
  }

  approvalRules(family?: "Routing" | "Sla" | "Preauth" | "AutoApprove") {
    return this.gate(() => ok(zApprovalRuleList, {
      rules: family ? this.rules.filter((r) => r.family === family) : this.rules,
      queues: ["clinical", "default", "escalation", "high-cost", "imaging", "pharmacy"],
      defaultQueue: "default",
    }));
  }

  saveApprovalRule(req: SaveApprovalRule) {
    return this.gate(() => {
      const now = new Date().toISOString();
      const prior = req.supersedesRuleId
        ? this.rules.find((r) => r.id === req.supersedesRuleId && r.effectiveTo === null)
        : undefined;
      // Supersede, never overwrite — the same semantics the server has, so a screen that quietly edited
      // history would look correct here and be wrong against the wire.
      if (prior) prior.effectiveTo = now as never;
      const id = `R-${this.rules.length + 1}`;
      this.rules = [...this.rules, {
        id, family: req.family, priority: req.priority,
        predicate: JSON.stringify(req.predicate), action: JSON.stringify(req.action),
        effectiveFrom: now, effectiveTo: null as never, versionNo: (prior?.versionNo ?? 0) + 1,
        enabled: req.enabled, authoredBy: "medical_director", rationale: req.rationale,
      }];
      return { id, versionNo: (prior?.versionNo ?? 0) + 1 };
    });
  }

  adminDocumentValidity() {
    return this.gate(() => ok(zDocumentValidityView, {
      tenant: "11111111-1111-1111-1111-111111111111",
      defaultDays: 365, minDays: 1, maxDays: 3650, defaultWarnDays: [90, 60, 30],
      items: this.docValidity,
    }));
  }

  adminSetDocumentValidity(req: SetDocumentValidity) {
    return this.gate(() => {
      this.docValidity = this.docValidity.map((d) =>
        d.kind !== req.kind ? d : {
          ...d,
          days: req.days ?? d.days,
          warnDays: req.warnDays ?? d.warnDays,
          // Setting it makes it CONFIGURED, which is the state the screen distinguishes.
          configured: req.days !== undefined ? true : d.configured,
          warnConfigured: req.warnDays !== undefined ? true : d.warnConfigured,
          updatedAt: new Date().toISOString(),
        });
      return undefined as void;
    });
  }

  /**
   * MUTABLE, like `mdVersions` above and for the same reason: the config editor's whole point is that a
   * value CHANGES and the change survives a reload of the table. A fixture that answered the same two rows
   * after every write would let an editor that silently did nothing look correct here.
   *
   * <p>`session.timeout_minutes` is deliberately `Whole` and not `Duration`. The fixture used to say
   * Duration with a value of "15", which the server would read as a .NET `TimeSpan` — fifteen DAYS. A
   * fixture that would be rejected (or worse, accepted with the wrong meaning) by the real endpoint teaches
   * the wrong shape to every screen written against it.</p>
   */
  private configs: { id: string; tenantId: string; key: string; type: string; value: string; versionNo: number }[] = [
    { id: "CFG-1", tenantId: "*", key: "session.timeout_minutes", type: "Whole", value: "15", versionNo: 1 },
    { id: "CFG-2", tenantId: "11111111-1111-1111-1111-111111111111", key: "approvals.sla_hours", type: "Whole", value: "24", versionNo: 3 },
    { id: "CFG-3", tenantId: "11111111-1111-1111-1111-111111111111", key: "notifications.sms_enabled", type: "Boolean", value: "true", versionNo: 1 },
    { id: "CFG-4", tenantId: "*", key: "documents.retention", type: "Duration", value: "3650.00:00:00", versionNo: 2 },
  ];

  adminSystemConfig() {
    return this.gate(() => ok(z.array(zSystemConfigEntry), this.configs), []);
  }

  adminSystemConfigSet(edit: SystemConfigEdit) {
    return this.gate(() => {
      // The SERVER's validation, not a looser copy of it. A dev client that accepted "abc" as a Whole would
      // let the editor ship without the error path anyone will actually hit.
      const canonical = canonicaliseConfigValue(edit.type, edit.value);
      if (canonical === null) {
        throw new ApiError("http", `not-a-${edit.type.toLowerCase()}`, 422, { title: `not-a-${edit.type.toLowerCase()}` });
      }
      const tenantId = edit.tenantId ?? "11111111-1111-1111-1111-111111111111";
      const prior = this.configs.find((c) => c.tenantId === tenantId && c.key === edit.key);
      const row = {
        id: prior?.id ?? `CFG-${this.configs.length + 1}`,
        tenantId,
        key: edit.key,
        type: edit.type,
        value: canonical,
        versionNo: (prior?.versionNo ?? 0) + 1,
      };
      this.configs = [...this.configs.filter((c) => !(c.tenantId === tenantId && c.key === edit.key)), row];
      return ok(zSystemConfigEntry, row);
    });
  }

  // ---- User & access model (Phase 21.6, design 40) -------------------------------------------------------
  //
  // The fixtures deliberately include the awkward states, because those are the ones the screens exist to
  // make legible: a suspended membership, a lapsed override, an open-ended branch grant, a cap already
  // exceeded, and a feature nobody has configured either way.

  /** The exception rows, per membership. See {@link devOverrides}. */
  private overrideStore = devOverrides();

  memberships() {
    return this.gate(
      () =>
        ok(
          z.array(zMembershipRow),
          // Counted from the live store rather than carried as literals: the roster's "3 exceptions, 1
          // lapsed" and the detail's three rows have to be one fact, or granting one from the detail leaves
          // the list behind it saying something else.
          DEV_MEMBERSHIPS.map((m) => ({
            ...m,
            overrideCount: (this.overrideStore.get(m.membershipId) ?? []).length,
            expiredOverrideCount: (this.overrideStore.get(m.membershipId) ?? []).filter((o) => o.expired).length,
          })),
        ),
      [],
    );
  }

  membership(membershipId: string) {
    const row = DEV_MEMBERSHIPS.find((m) => m.membershipId === membershipId) ?? DEV_MEMBERSHIPS[0];
    return this.gate(() =>
      ok(zMembershipDetail, {
        ...row,
        // Read back from the mutable map, so the counts on the roster and the rows in the detail are the same
        // fact rather than two literals that agree until somebody grants an exception.
        overrideCount: (this.overrideStore.get(row.membershipId) ?? []).length,
        expiredOverrideCount: (this.overrideStore.get(row.membershipId) ?? []).filter((o) => o.expired).length,
        providerId: null,
        homeBranchId: "b1000000-0000-0000-0000-000000000001",
        overrides: [...(this.overrideStore.get(row.membershipId) ?? [])],
      }),
    );
  }

  setMembershipOverride(
    membershipId: string,
    input: { scopeKey: string; effect: "Allow" | "Deny"; reason: string; validUntil: string | null },
  ) {
    return this.gate(() => {
      const list = this.overrideStore.get(membershipId) ?? [];
      // SET, not add: the endpoint is idempotent per (membership, scope), and a fixture that appended would
      // let a screen with a duplicate-key bug look correct.
      const rest = list.filter((o) => o.scope !== input.scopeKey);
      this.overrideStore.set(membershipId, [...rest, {
        id: `OV-${membershipId.slice(0, 4)}-${input.scopeKey}`,
        scope: input.scopeKey,
        effect: input.effect,
        reason: input.reason,
        grantedBy: "u-1",
        validUntil: input.validUntil,
        expired: false,
      }]);
      return undefined;
    });
  }

  removeMembershipOverride(membershipId: string, scopeKey: string) {
    return this.gate(() => {
      const list = this.overrideStore.get(membershipId) ?? [];
      this.overrideStore.set(membershipId, list.filter((o) => o.scope !== scopeKey));
      return undefined;
    });
  }

  /**
   * Mode 2, DERIVED from the membership's roles and its live overrides.
   *
   * <p>It used to be five literals returned for every membership id, which meant granting an exception in the
   * dev build changed nothing on the preview that exists to show what an exception does. The algebra here is
   * the fixture's own — the real one runs on the server and this client never talks to it — but it has to
   * MOVE, or the screen's most important panel is a picture.</p>
   */
  effectiveAccess(membershipId: string) {
    return this.gate(() => {
      const row = DEV_MEMBERSHIPS.find((m) => m.membershipId === membershipId) ?? DEV_MEMBERSHIPS[0];
      const byRole: Record<string, string[]> = {
        doctor: ["encounters:read", "prescriptions:write", "labs:read", "orders:read"],
        provider_admin: ["provider:read", "provider:write"],
        network_team: ["provider:read"],
        pharmacist: ["pharmacy:read", "pharmacy:dispense"],
        org_admin: ["admin:read", "admin:write"],
        reception: ["reception:search", "appointment:write"],
      };
      const deprecated: Record<string, string | null> = { "labs:read": "investigations:read" };
      const overrides = (this.overrideStore.get(row.membershipId) ?? []).filter((o) => !o.expired);
      const denied = new Map(overrides.filter((o) => o.effect === "Deny").map((o) => [o.scope, o]));
      const allowed = overrides.filter((o) => o.effect === "Allow");

      const fromRoles = row.roles.flatMap((r) => (byRole[r.name] ?? []).map((key) => ({ key, via: r.name })));
      const keys = [
        ...fromRoles.map((k) => {
          const deny = denied.get(k.key);
          return deny
            // Listed as DENIED rather than dropped: an absence that looks like a broken role definition is
            // what sends an administrator re-granting the role.
            ? { key: k.key, source: "denied" as const, via: deny.grantedBy ?? undefined, reason: deny.reason }
            : {
                key: k.key, source: "role" as const, via: k.via,
                deprecated: k.key in deprecated ? true : undefined,
                replacedBy: deprecated[k.key] ?? undefined,
              };
        }),
        ...allowed.map((o) => ({ key: o.scope, source: "override" as const, via: o.grantedBy ?? undefined, reason: o.reason })),
      ];
      return ok(zEffectiveAccess, { membershipId, keys });
    });
  }

  branchScopeGrants() {
    return this.gate(
      () =>
        ok(z.array(zBranchScopeGrant), [
          /*
            28.10 — the branch ids MATCH `branches()` above.

            They used to be `b1000000-0000-…-01/02`, which appear in no other fixture. That was invisible
            while the column rendered `branchId.slice(0, 8)` — eight hex characters look the same whether or
            not they identify anything. Now that the column resolves a NAME, an id matching nothing renders
            as "Branch no longer listed", so the fixture would have shown every dev run a review finding that
            does not exist. `BR-ALX` for the second one because its own reason already says Alexandria.
          */
          {
            grantId: "G-1", branchId: "BR-MAA", isHome: true,
            validFrom: "2026-01-01", validUntil: null, grantedBy: "admin@mersal", grantedReason: "Home branch",
          },
          {
            grantId: "G-2", branchId: "BR-ALX", isHome: false,
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

/**
 * The membership roster — keyed to the fixture ACCOUNTS above by `userId`.
 *
 * <p>One identity holding two memberships with different authority (Sara) is invariant 1 made visible, and
 * `svc.reporting` deliberately has NO membership: an account that exists and holds authority nowhere is a
 * real state, and the merged table has to render it as a fact rather than as a blank.</p>
 */
const DEV_MEMBERSHIPS = [
  {
    membershipId: "11111111-1111-1111-1111-111111111111",
    userId: "u-5",
    username: "s.ibrahim", displayName: "Sara Ibrahim",
    tenantId: "mersal", status: { kind: "ok" as const, label: loc("Active", "نشِطة") },
    roles: [{ name: "doctor", level: 3 }], level: 3, isPlatformAdmin: false,
    activatedAt: "2026-01-15T08:00:00Z", endedAt: null,
  },
  {
    // Same person, different organisation, genuinely different authority — never a blended principal.
    membershipId: "22222222-2222-2222-2222-222222222222",
    userId: "u-5",
    username: "s.ibrahim", displayName: "Sara Ibrahim",
    tenantId: "partner-ngo", status: { kind: "ok" as const, label: loc("Active", "نشِطة") },
    roles: [{ name: "provider_admin", level: 2 }], level: 2, isPlatformAdmin: false,
    activatedAt: "2026-03-02T08:00:00Z", endedAt: null,
  },
  {
    membershipId: "33333333-3333-3333-3333-333333333333",
    userId: "u-6",
    username: "m.farouk", displayName: "Mohamed Farouk",
    tenantId: "mersal", status: { kind: "warn" as const, label: loc("Suspended", "موقوفة") },
    roles: [{ name: "pharmacist", level: 4 }], level: 4, isPlatformAdmin: false,
    activatedAt: "2026-02-01T08:00:00Z", endedAt: null,
  },
  {
    membershipId: "44444444-4444-4444-4444-444444444444",
    userId: "u-1",
    username: "org.admin", displayName: "Org Admin",
    tenantId: "mersal", status: { kind: "ok" as const, label: loc("Active", "نشِطة") },
    roles: [{ name: "org_admin", level: 1 }], level: 1, isPlatformAdmin: false,
    activatedAt: "2025-11-01T08:00:00Z", endedAt: null,
  },
  {
    membershipId: "55555555-5555-5555-5555-555555555555",
    userId: "u-2",
    username: "dr.hala", displayName: "Dr. Hala",
    tenantId: "mersal", status: { kind: "ok" as const, label: loc("Active", "نشِطة") },
    roles: [{ name: "doctor", level: 3 }], level: 3, isPlatformAdmin: false,
    activatedAt: "2026-01-04T08:00:00Z", endedAt: null,
  },
  {
    // Ended, not merely inactive: the account is de-provisioned AND the membership is over, and the two are
    // separate facts that happen to agree here.
    membershipId: "66666666-6666-6666-6666-666666666666",
    userId: "u-3",
    username: "left.staff", displayName: "Former Staff",
    tenantId: "mersal", status: { kind: "neu" as const, label: loc("Ended", "منتهية") },
    roles: [{ name: "reception", level: 4 }], level: 4, isPlatformAdmin: false,
    activatedAt: "2025-06-01T08:00:00Z", endedAt: "2026-05-30T08:00:00Z",
  },
  {
    membershipId: "77777777-7777-7777-7777-777777777777",
    userId: "u-7",
    username: "n.habib", displayName: "Nour Habib",
    tenantId: "mersal", status: { kind: "ok" as const, label: loc("Active", "نشِطة") },
    roles: [{ name: "network_team", level: 2 }], level: 2, isPlatformAdmin: false,
    activatedAt: "2026-04-12T08:00:00Z", endedAt: null,
  },
];

/**
 * The overrides, MUTABLE and per membership — 28.16.
 *
 * <p>`setMembershipOverride` was a no-op returning `undefined` and `membership()` answered the same three
 * literals for every id, so the exception path could be driven in the dev build and in tests without any of
 * it being observable. That is the shape of stub this session has already been caught by twice: a fixture
 * that cannot disagree with a broken screen cannot test one. Granting an exception now adds a row, and
 * withdrawing one removes it.</p>
 */
interface DevOverride {
  id: string; scope: string; effect: "Allow" | "Deny"; reason: string;
  grantedBy: string | null; validUntil: string | null; expired: boolean;
}

/** A FRESH map per client, like the identity store: a module-level one would carry one test's grant into
 *  the next test's assertions. */
function devOverrides(): Map<string, DevOverride[]> {
  return new Map<string, DevOverride[]>([
  ["11111111-1111-1111-1111-111111111111", [
    {
      id: "OV-1", scope: "orders:read", effect: "Deny", reason: "Under investigation — access narrowed pending review",
      grantedBy: "u-1", validUntil: null, expired: false,
    },
    {
      id: "OV-2", scope: "reports:export", effect: "Allow", reason: "Covering the monthly extract while N. is on leave",
      grantedBy: "u-1", validUntil: "2026-08-31T00:00:00Z", expired: false,
    },
    // Lapsed on purpose: the screen must show it as expired rather than hide it, so an administrator
    // can explain why this person lost the key overnight.
    {
      id: "OV-3", scope: "claims:submit", effect: "Allow", reason: "Ramadan surge cover",
      grantedBy: "u-1", validUntil: "2026-04-30T00:00:00Z", expired: true,
    },
  ]],
  ]);
}

/**
 * A fixture validation engine that mirrors the SERVER's five-state semantics (phase 26).
 *
 * It exists so the workspace can be exercised — and axe-checked — against every state the real engine can
 * produce, including the two that are not answers. A fixture that returned "Ok" for everything would make
 * the whole point of the phase untestable in the UI.
 *
 * The rules it reproduces:
 *   - no diagnosis recorded            -> NotChecked, never Ok
 *   - drug carries no indication data  -> NotChecked, never a mismatch
 *   - indication matched at CATEGORY level ("E11.9" satisfies "E11")
 *   - off-label                        -> Warning, never Blocked
 *   - two lines sharing an ingredient  -> Warning on both (the duplication the ingredient line guards)
 *   - allergy source down for one drug -> Unavailable
 *   - benefit                          -> NotChecked in phase 26 (the seam), Blocked for the demo exclusion
 */
function devValidation(lines: PrescriptionDraftLine[], diagnoses: string[]) {
  const findings: Finding[] = [];
  const provenance = {
    sourceName: "Drug indication list (ATC + drug class)",
    sourceVersion: "egyptian-drug-list_5",
    checkedAt: new Date(0).toISOString(),
    caveat: "Indications are mapped at ATC level 4 by clinical review, not from a published dataset.",
  };
  const categories = diagnoses.map((d) => d.trim().toUpperCase().slice(0, 3));
  const indicated: Record<string, string[]> = {
    "11111111-0000-4000-8000-000000000001": ["J01", "J15", "J18", "N39"],
    "11111111-0000-4000-8000-000000000002": ["J01", "J02", "J03"],
    "11111111-0000-4000-8000-000000000003": ["E11"],
  };

  /**
   * Which demo drug's label names which other demo drug, standing in for the openFDA text scan.
   *
   * Deliberately NOT symmetric, because real labels are not: manufacturers document interactions from their
   * own product's point of view, and often only the older drug's label mentions the newer one. The live
   * check reads both labels for exactly this reason, and a fixture that paired them symmetrically would
   * hide the case the two-direction scan exists to catch.
   */
  const LABEL_INTERACTIONS: Record<string, string[]> = {
    "11111111-0000-4000-8000-000000000001": ["11111111-0000-4000-8000-000000000003"],
  };

  const add = (
    lineId: string, drugId: string | undefined, kind: Finding["kind"], state: Finding["state"],
    en: string, ar: string, extra: Partial<Finding> = {},
  ) => {
    // NULL, not undefined, for every absent optional — because that is what the real service sends.
    // System.Text.Json WRITES nullable properties as `null` rather than omitting them, and a fixture that
    // used `undefined` instead was the reason the whole suite passed green while the live screen failed
    // contract parsing on the first response. A fixture whose SHAPE differs from the server is a fixture
    // that tests the wrong thing.
    findings.push({
      lineId, drugId, kind, state, messageEn: en, messageAr: ar,
      severity: null, relatedLineId: null,
      requiresAcknowledgement: state === "Warning", isBlocking: state === "Blocked",
      ...provenance, ...extra,
    });
  };

  for (const line of lines) {
    const drug = line.drug;
    if (!drug) continue;

    // --- indication ---
    if (diagnoses.length === 0) {
      add(line.lineId, drug.drugId, "Indication", "NotChecked",
        "Not checked — no diagnosis recorded on this encounter.",
        "لم يتم التحقق — لا يوجد تشخيص مسجل في هذه الزيارة.");
    } else if (!drug.hasIndicationData) {
      add(line.lineId, drug.drugId, "Indication", "NotChecked",
        "Not checked — no indication data is recorded for this medicine.",
        "لم يتم التحقق — لا توجد بيانات دواعي استعمال مسجلة لهذا الدواء.");
    } else if ((indicated[drug.drugId] ?? []).some((c) => categories.includes(c))) {
      add(line.lineId, drug.drugId, "Indication", "Ok",
        "Listed indication.", "من دواعي الاستعمال المسجلة.");
    } else {
      add(line.lineId, drug.drugId, "Indication", "Warning",
        "Not a listed indication for the recorded diagnosis. Off-label use may be appropriate; give a reason to proceed.",
        "ليس من دواعي الاستعمال المسجلة للتشخيص المدوَّن. يرجى ذكر السبب للمتابعة.");
    }

    // --- interaction: two lines sharing an active ingredient ---
    const twin = lines.find(
      (o) =>
        o.lineId !== line.lineId && o.drug &&
        (o.drug.activeIngredient ?? "?").split(" + ")[0] === (drug.activeIngredient ?? "!").split(" + ")[0],
    );
    if (twin) {
      add(line.lineId, drug.drugId, "Interaction", "Warning",
        `Duplicate active ingredient with ${twin.drug!.tradeName.en}.`,
        `تكرار للمادة الفعالة مع ${twin.drug!.tradeName.ar}.`,
        { severity: "Major", relatedLineId: twin.lineId, sourceName: "Mersal interaction list" });
    } else {
      add(line.lineId, drug.drugId, "Interaction", "Ok",
        "No interaction found (checked against 512 known pairs).",
        "لم يتم العثور على تداخلات (تم التحقق مقابل 512 زوجًا معروفًا).",
        { sourceName: "Mersal interaction list", caveat: "Checked against Mersal's own interaction list; coverage is partial." });
    }

    // --- allergy: this product's source is deliberately down, to exercise Unavailable ---
    if (drug.drugId.endsWith("0004")) {
      add(line.lineId, drug.drugId, "Allergy", "Unavailable",
        "Allergy check unavailable — the allergy record could not be reached.",
        "تعذّر التحقق من الحساسية — تعذر الوصول إلى سجل الحساسية.",
        { sourceName: null, sourceVersion: null, checkedAt: null, caveat: null });
    } else {
      add(line.lineId, drug.drugId, "Allergy", "Ok",
        "No conflict with the recorded allergies.", "لا يوجد تعارض مع الحساسية المسجلة.",
        { sourceName: "EMR allergy record" });
    }

    // --- interaction, second source: manufacturer label text, live from openFDA ---
    //
    // Additive, not a replacement. It answers with different authority and different provenance, and it may
    // WARN but never reassure — a label's interactions section is prose, not a complete list, so a silence
    // from it is not a negative result.
    const otherDrugs = lines.filter((o) => o.lineId !== line.lineId && o.drug);
    if (otherDrugs.length > 0) {
      const named = otherDrugs.find((o) => LABEL_INTERACTIONS[drug.drugId]?.includes(o.drug!.drugId));
      if (named) {
        add(line.lineId, drug.drugId, "Interaction", "Warning",
          `The ${(drug.activeIngredient ?? drug.tradeName.en).toUpperCase()} label names `
          + `${named.drug!.activeIngredient ?? named.drug!.tradeName.en} in its interactions section, and `
          + `${named.drug!.tradeName.en} is on this prescription. Read the manufacturer's wording below and `
          + "give a reason to proceed.",
          `تذكر نشرة ${drug.activeIngredient ?? drug.tradeName.ar} المادة `
          + `${named.drug!.activeIngredient ?? named.drug!.tradeName.ar} ضمن قسم التداخلات الدوائية، `
          + `و${named.drug!.tradeName.ar} موجود في هذه الوصفة. يرجى قراءة نص الشركة المصنِّعة أدناه `
          + "(بالإنجليزية) وذكر السبب للمتابعة.",
          {
            severity: null, relatedLineId: named.lineId,
            sourceName: "openFDA drug label (U.S. FDA)", sourceVersion: "live",
            referenceText: "Concomitant use of drugs that increase bleeding risk, antibiotics, antifungals, "
              + "and inhibitors and inducers of CYP2C9, 1A2, or 3A4 may increase the INR and the risk of "
              + "bleeding.",
            caveat: "U.S. FDA product labelling, matched by active ingredient. Labels are narrative, not a "
              + "complete interaction list, and describe U.S. products.",
          });
      } else {
        add(line.lineId, drug.drugId, "Interaction", "NotChecked",
          "No interaction named in the manufacturer labels for the medicines on this prescription. This is "
          + "not an all-clear: a label's interactions section is written as prose, not as a complete list, "
          + "so an interaction can exist without being named.",
          "لم يُذكر أي تداخل في نشرات الشركات المصنِّعة للأدوية في هذه الوصفة. وهذا لا يعني الخلو من "
          + "التداخلات: فقسم التداخلات في النشرة مكتوب كنص وصفي وليس قائمة كاملة، وقد يوجد تداخل دون ذكره.",
          { sourceName: "openFDA drug label (U.S. FDA)", sourceVersion: "live" });
      }
    }

    // --- dose: no structured rules exist, so the label's own dosing is shown for reference only ---
    add(line.lineId, drug.drugId, "DoseDuration", "NotChecked",
      "Dose not checked — no dosing rule is configured for this medicine. The manufacturer's labelled dosing "
      + `for ${(drug.activeIngredient ?? drug.tradeName.en).toUpperCase()} is shown below for reference — it `
      + "has NOT been compared with what you prescribed.",
      "لم يتم التحقق من الجرعة — لا توجد قاعدة جرعات مُهيأة لهذا الدواء. فيما يلي جرعات النشرة المعتمدة "
      + "للاطلاع فقط (بالإنجليزية) — ولم تتم مقارنتها بما وصفته.",
      {
        sourceName: "openFDA drug label (U.S. FDA)", sourceVersion: "live",
        referenceText: "Individualize the dosing regimen for each patient and adjust based on response. "
          + "The usual adult dose is one tablet every 8 hours; do not exceed the maximum daily dose.",
      });

    // --- benefit: the seam. Blocked only for the demo exclusion, to exercise the state. ---
    add(line.lineId, drug.drugId, "Benefit", "NotChecked",
      "Benefit rules are evaluated on submission, not while prescribing.",
      "يتم تقييم قواعد التغطية عند الإرسال وليس أثناء وصف الدواء.",
      { sourceName: "Mersal benefit rules", sourceVersion: "not-yet-configured" });
  }

  const rank: Finding["state"][] = ["Blocked", "Unavailable", "Warning", "NotChecked", "Ok"];
  const worst = (subset: Finding[]) =>
    rank.find((r) => subset.some((f) => f.state === r)) ?? "NotChecked";

  const lineStates: Record<string, Finding["state"]> = {};
  for (const line of lines) {
    lineStates[line.lineId] = worst(findings.filter((f) => f.lineId === line.lineId));
  }

  return {
    validationId: "v-dev-1",
    ranAt: new Date(0).toISOString(),
    engineVersion: "26.4",
    overallState: worst(findings),
    findings,
    lineStates,
  };
}

/**
 * Dev-fixture checks for an investigation order — the same five states the server produces.
 *
 * <p>It mirrors the real engine's SHAPE rather than guessing at its verdicts: an unknown code blocks, a
 * wrong-section code blocks, a repeated code within the same draft warns, and the indication check reports
 * NotChecked because no procedure-indication reference exists. A fixture that returned a clean pass for
 * everything would let the workspace's own "unanswered" rendering go untested — which is the part most worth
 * testing, since a check that did not run must never look like one that passed.</p>
 */
function devOrderValidation(
  orderType: InvestigationOrderType,
  lines: InvestigationDraftLine[],
  diagnoses: string[],
) {
  const findings: OrderFinding[] = [];
  const seen = new Map<string, number>();
  for (const l of lines) {
    const code = l.test?.code ?? "";
    if (!code) {
      findings.push({
        lineId: l.lineId, kind: "Code", state: "Blocked",
        message: loc("No test has been chosen for this line.", "لم يتم اختيار فحص لهذا السطر."),
        requiresAcknowledgement: false, isBlocking: true, sourceName: null, caveat: null,
      });
      continue;
    }
    findings.push({
      lineId: l.lineId, kind: "Code", state: "Ok",
      message: loc("In the procedure catalogue.", "موجود في كتالوج الإجراءات."),
      requiresAcknowledgement: false, isBlocking: false, sourceName: "masterdata:cpt", caveat: null,
    });

    const radiology = /^7\d{4}$/.test(code);
    const laboratory = /^8\d{4}$/.test(code);
    if ((orderType === "Lab" && radiology) || (orderType === "Imaging" && laboratory)) {
      findings.push({
        lineId: l.lineId, kind: "Section", state: "Blocked",
        message: loc(
          `'${code}' belongs to another section. It would reach a queue that cannot perform it.`,
          `الكود '${code}' من قسم آخر، وسيصل إلى قائمة عمل لا يمكنها تنفيذه.`),
        requiresAcknowledgement: false, isBlocking: true, sourceName: null, caveat: null,
      });
    }

    const count = (seen.get(code) ?? 0) + 1;
    seen.set(code, count);
    if (count > 1) {
      findings.push({
        lineId: l.lineId, kind: "Duplicate", state: "Warning",
        message: loc(
          "This test is already on this order.", "هذا الفحص مطلوب بالفعل ضمن هذا الطلب."),
        requiresAcknowledgement: true, isBlocking: false, sourceName: null, caveat: null,
      });
    }

    findings.push({
      lineId: l.lineId, kind: "Indication", state: "NotChecked",
      message: diagnoses.length === 0
        ? loc("No diagnosis is recorded on this encounter, so nothing can be checked against.",
              "لا يوجد تشخيص مسجل في هذه الزيارة، لذلك لا يوجد ما يمكن التحقق مقابله.")
        : loc("No procedure-indication reference is loaded, so this test has not been checked against the recorded diagnoses.",
              "لا يوجد مرجع لدواعي إجراء الفحوصات، لذلك لم يتم التحقق من هذا الفحص مقابل التشخيصات المسجلة."),
      requiresAcknowledgement: false, isBlocking: false, sourceName: null, caveat: null,
    });
  }

  const worst = (states: string[]) =>
    states.includes("Blocked") ? "Blocked"
      : states.includes("Warning") ? "Warning"
      : states.includes("Unavailable") ? "Unavailable"
      : states.includes("NotChecked") ? "NotChecked"
      : "Ok";

  const lineStates: Record<string, string> = {};
  for (const l of lines) {
    lineStates[l.lineId] = worst(findings.filter((f) => f.lineId === l.lineId).map((f) => f.state));
  }

  return {
    validationId: "ov-1",
    overallState: worst(findings.map((f) => f.state)),
    findings,
    lineStates,
  };
}

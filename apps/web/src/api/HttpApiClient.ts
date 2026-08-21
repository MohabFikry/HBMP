import {
  zAccessReviewCampaign,
  zAppointmentRow,
  zWaitingTicket,
  zBookableClinic,
  zTimelineStep,
  zBookableSlot,
  zBookingResult,
  zApprovalItem,
  zAdjudicationRow,
  zClaimDetail,
  zClaimAdjustment,
  zClaimDecisionResult,
  zRetrospectiveItem,
  zApprovalReview,
  zBreakGlassGrant,
  zMasterDataVersion,
  zMasterDataAsOf,
  zDocumentValidityView,
  zApprovalRuleList,
  zAutoDecisionSwitch,
  zSystemConfigEntry,
  zProviderSummary,
  zProviderLocation,
  zProviderContract,
  type CreateProviderInput,
  zBeneficiaryRow,
  zPatientProfile,
  zCopySummariesResult,
  zProfileExportSummary,
  type ProfileSectionKey,
  zRegisterResult,
  zStatusChangeResult,
  type RegisterBeneficiaryInput,
  zCheckInResult,
  zRoleBinding,
  zSodConflict,
  zTenantSummary,
  zConsumeResult,
  zDecisionResult,
  zDispenseResult,
  zRxPricing,
  zAuthorizationItem,
  zInvestigationOrder,
  zOrderPricing,
  zEligibilityHit,
  zEligibilityResult,
  zEncounter,
  zEncounterDiagnosis,
  zIcdRef,
  zExecutiveDashboard,
  zKpiWidget,
  zChartWidget,
  zLabOrder,
  zMarkAllReadResult,
  zMarkReadResult,
  zNotification,
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
  zValidationResult,
  zCptRef,
  zOrderValidationResult,
  zValidityExtensionResult,
  zValidityPolicyView,
  zInvestigationOrderResult,
  zPrescriptionSubmitResult,
  zTatSummary,
  zManualAuthResult,
  zEmergencyResult,
  type ManualAuthInput,
  zReportView,
  zPatientListItem,
  zPlaceOrderResult,
  zPrescribeResult,
  zPrescription,
  zBeneficiary360,
  zCaseListItem,
  zCoordinationTask,
  zEscalation,
  zExportResult,
  zFinancialSummary,
  zSettlement,
  zSettlementPage,
  zOutOfStockResult,
  zUtilizationView,
  type ConsumeRequest,
  type DecisionRequest,
  type DispenseRequest,
  type ExportRequest,
  type PlaceOrderRequest,
  type PrescribeRequest,
  type VitalInput,
  type Soap,
  type DiagnosisRank,
  zIdentityUser,
  zRoleCatalogEntry,
  zScopeCatalogEntry,
  zRoleScopeGrant,
  zReportAccessRequestRow,
  zBeneficiaryDocument,
  zRegistrationThreadEntry,
  zRegistrationWorkItem,
  zRegistrationWorklistPage,
  zRegistrationDecisionResult,
  zMembershipRow,
  zMembershipDetail,
  zEffectiveAccess,
  zBranchScopeGrant,
  zAccessSession,
  zProgramEnablement,
  zSpecialty,
  zBranchSummary,
  zPractitioner,
  zPractitionerCreated,
  zDoctorAvailability,
  zAppointmentDay,
  zAppointmentCounts,
  zAmendReasonOption,
  // 2026-08-11 audit — the director oversight reads. VALUE imports: the payload is validated at the seam.
  zServiceUseView,
  zSlaBreachView,
  zClaimsCostView,
  zMedicationHistoryRow,
  zNoteAddendum,
  zLineNote,
  zChronicAmendPreview,
} from "@mersal/contracts";
import type { Localized, Period, ServiceAxis, LineNoteKind, NoteVisibility } from "@mersal/contracts";
import type { ClaimDecisionRequest, RetrospectiveReviewInput } from "@mersal/contracts";
import type {
  BeneficiaryEdit, BookingRequest, BulkDecisionOutcome, CreatePractitionerInput, PractitionerAttachFailure,
  MasterDataEdit,
  SystemConfigEdit,
  SetDocumentValidity,
  SaveApprovalRule,
  SetAutoDecision,
  GenerateSettlementRequest,
} from "@mersal/contracts";
import type {
  PrescriptionDraftLine, LineAcknowledgement, AddAllergyRequest, BloodGroup, PrescriptionKind,
  AddMedicationHistoryRequest, MedicationStatus,
} from "@mersal/contracts";
import { zChronicPreview, zQuantityPreview, zRefillFrequency } from "@mersal/contracts";
// 29.2b — VALUE imports (the schemas), not types: the payload is validated at the seam.
import {
  zOrderableService, zProcedureQueueItem, zProcedureType, zReferralCreated, zSessionProgress, zServiceHistory,
} from "@mersal/contracts";
import type { CptSection, InvestigationDraftLine, InvestigationOrderType, OrderAcknowledgement, ValidityExtensionRequest } from "@mersal/contracts";
import type { SubstitutionRequest, WithdrawResult } from "@mersal/contracts";
import { zAllergenOption, zAllergyRecord, zMemberClinicalRecord } from "@mersal/contracts";
import type { ApiClient } from "./client";
import { ApiError, getRaw, getRawCounted, postRaw, putRaw, patchRaw, deleteRaw, postForm, postForFile, parseOr, getAbsolute, postAbsolute, deleteAbsolute } from "./http";
import { newIdempotencyKey } from "./http";
import type { ApprovalQueueFilter } from "./client";
import { GATEWAY_BASE } from "../config";

/* This client is a deliberate adapter between loosely-typed service JSON and the strict portal contracts;
   it maps `any` service payloads then zod-validates the mapping, so `any` is intentional file-wide. */
/* eslint-disable @typescript-eslint/no-explicit-any */

/**
 * Wrap a LANGUAGE-NEUTRAL service value as the bilingual shape the portal contracts use.
 *
 * 24.4 — `neutral()` used to do double duty and the second job was a bug. Same text in both languages is
 * correct for a value that has no language: an ICD or CPT code, a masked identifier, a drug name as the
 * formulary records it, a chart label that is already a code. It is NOT correct for English UI text —
 * "Under review", "Prescriber", "Awaiting decision" — where copying English into `ar` puts English in
 * front of an Arabic-reading user and, worse, makes the payload LOOK translated to anything checking that
 * both fields are populated. Ten such literals had reached the portal contracts that way.
 *
 * Renamed so the two cases cannot be confused: `neutral()` says the sameness is deliberate, `t()` below
 * carries a real translation. HttpApiClientI18nTests forbids a hardcoded English literal reaching
 * `neutral()`.
 */
const neutral = (s: unknown) => ({ en: String(s ?? ""), ar: String(s ?? "") });

/** A translated UI string the API layer has to supply because the service sends no label at all. */
const t = (en: string, ar: string) => ({ en, ar });

/** masterdata-service serialises `AllergenCategory` as an ordinal — mapped by position, per Entities.cs. */
const ALLERGEN_CATEGORY = ["Drug", "Food", "Environmental"] as const;

/**
 * emr's AllergyResponse → the portal's AllergyRecord.
 *
 * `allergenDisplay` is left NULL when emr has none (a row recorded before its migration 0020) rather than
 * substituted with the uuid or the reaction. The component renders "(unspecified)" in the reader's own
 * language; inventing a substance name in the API layer would put a word in a clinician's mouth.
 */
/**
 * 32.2 — one medication-history row.
 *
 * <p>Nothing is defaulted into existence here. A row with no source would be a row whose warning cannot say
 * where it came from, and `parseOr` is meant to reject that rather than have this mapper invent
 * "SelfReported" to keep a screen quiet — which is the shape of defect this whole pass is closing.</p>
 */
/**
 * The path prefix for a line note, by order kind.
 *
 * <p>orders-service serves investigation and procedure notes; pharmacy serves prescription notes (32.5).
 * Three prefixes and one shape, which is the whole point of the shared panel.</p>
 */
const lineNoteBase = (kind: LineNoteKind, orderId: string) => {
  const root = kind === "prescription" ? "/prescriptions"
    : kind === "procedure" ? "/procedure-orders"
    : "/investigation-orders";
  return orderId ? `${root}/${encodeURIComponent(orderId)}` : root;
};

const toLineNote = (n: any, lineId: string) => ({
  noteId: n?.noteId,
  // orders names it orderLineId, pharmacy prescriptionLineId. Neither is guessed: the caller already knows
  // which line it asked about, so the fallback is the id it asked with rather than a fabricated one.
  lineId: n?.orderLineId ?? n?.prescriptionLineId ?? lineId,
  visibility: n?.visibility,
  body: n?.body ?? "",
  authorDisplayName: n?.authorDisplayName ?? "",
  authoredAt: n?.authoredAt,
  status: n?.status,
  cancelledAt: n?.cancelledAt ?? null,
  cancelReason: n?.cancelReason ?? null,
});

const toMedicationRow = (m: any) => ({
  medHistoryId: m?.medHistoryId,
  beneficiaryId: m?.beneficiaryId,
  drugId: m?.drugId,
  drugName: m?.drugName ?? null,
  source: m?.source,
  startDate: m?.startDate ?? null,
  endDate: m?.endDate ?? null,
  status: m?.status,
});

/**
 * 32.4 — one chip per report-access state, because they say different things to different readers.
 *
 * `InfoRequested` is the one that mattered: it is not "awaiting a decision", it is awaiting the REQUESTER,
 * and rendering the two identically is what made a request that needed an answer look like one that needed
 * patience.
 */
const REPORT_ACCESS_CHIP: Record<string, { kind: "info" | "warn" | "ok" | "bad"; label: Localized }> = {
  Requested: { kind: "warn", label: t("Awaiting decision", "في انتظار القرار") },
  UnderReview: { kind: "info", label: t("Under review", "قيد المراجعة") },
  InfoRequested: { kind: "warn", label: t("More information needed", "مطلوب إيضاح") },
  Approved: { kind: "ok", label: t("Approved", "تمت الموافقة") },
  Denied: { kind: "bad", label: t("Denied", "مرفوض") },
  Expired: { kind: "info", label: t("Expired", "منتهٍ") },
  Revoked: { kind: "bad", label: t("Revoked", "أُلغي") },
};

const toAllergyRecord = (a: any) => ({
  allergyId: a?.allergyId,
  allergenId: a?.allergenId,
  allergen: a?.allergenDisplay ?? null,
  reaction: a?.reaction ?? null,
  severity: a?.severity ?? "Mild",
  status: a?.status ?? "Active",
});
/** Pre-format a numeric amount as the contract's display string, e.g. 12400 -> "EGP 12,400". */
/**
 * 18.D2 (audit R2 U7) — the API layer now returns a NUMBER; formatting happens at render.
 *
 * This used to build "EGP 12,400" with a hardcoded en-US locale, so the Arabic UI showed Western digits and
 * an English currency prefix. A pre-formatted string also cannot be summed, sorted numerically, or
 * re-localised when the user switches language mid-session.
 */
const money = (n: unknown, field: string): number => {
  if (typeof n === "number" && Number.isFinite(n)) return n;
  // A decimal serialised as text is tolerated when it parses — that is a serialisation choice, not a missing
  // field, and only the second is what this refuses. `""`, `null` and `undefined` are not amounts.
  if (typeof n === "string" && n.trim() !== "") {
    const v = Number(n);
    if (Number.isFinite(v)) return v;
  }
  throw new ApiError("schema", `${field}: expected an amount, got ${JSON.stringify(n)}`);
};

/**
 * ============================================================================================================
 * THE ADAPTER RESHAPES. IT DOES NOT INVENT.
 * ============================================================================================================
 * `money()` above used to read `Number(n ?? 0)` and fall back to `0` on anything unparseable, and required
 * identifiers were written `id: r?.orderId ?? ""`. Both defaults are applied BEFORE `parseOr`, so the zod
 * contract then validates a well-formed object and passes. Contract drift — a field the server renamed or
 * stopped sending — became plausible wrong data instead of a loud failure, which is the one outcome the
 * schema layer exists to prevent.
 *
 * What the two defaults actually assert is worth saying plainly. `?? 0` on an amount asserts that this
 * provider is owed nothing; it appears on the finance settlement and utilization screens. `?? ""` on an id
 * asserts an entity whose identifier is the empty string: it becomes a React key, a route parameter, and the
 * body of the next write. `POST /orders//consume` is the shape of that mistake.
 *
 * These two refuse instead, and name the field. NOT a blanket rule against `??` in this file — most of the
 * defaults here are legitimate adaptation (an absent label, an optional filter, a status the contract gives a
 * default for) or a min-necessary null the screens deliberately render as "withheld". The rule is narrower
 * and about consequence: where the substituted value would be BELIEVED, substitute nothing.
 */
const required = <T>(value: T | null | undefined, field: string): T => {
  if (value === null || value === undefined || value === "") {
    throw new ApiError("schema", `${field}: the service returned no value for a required identifier`);
  }
  return value;
};
/** Map a service case status (Open/Active/OnHold/Resolved/Closed) to the contract's snake_case enum. */
const caseStatus = (s: unknown) =>
  ({ open: "open", active: "active", onhold: "on_hold", resolved: "resolved", closed: "closed" })[
    String(s ?? "open").toLowerCase()
  ] ?? "open";
/** A masked, min-necessary display token for a case row (never a beneficiary name). */
const caseToken = (c: any) => `•••${String(c.beneficiaryId ?? c.caseId ?? "").slice(-4)}`;

/** Map the reception card's accessible status tone to the design-system StatusKind. */
const toneToKind = (tone: unknown): "ok" | "warn" | "bad" | "neu" | "info" =>
  ({ positive: "ok", caution: "warn", critical: "bad", neutral: "neu" })[String(tone ?? "neutral")] as any ?? "neu";
/**
 * 32.6 — the SERVICE's decision → the verdict the result card renders.
 *
 * <p>`NeedsAuthorization` is "review", not "ineligible": the engine's own words are that it is "a soft No
 * that routes to the approval team, not a denial". An unrecognised value also lands on "review" rather than
 * "eligible" — a decision this client cannot read is not permission to proceed.</p>
 */
const decisionToVerdict = (decision: unknown): "eligible" | "ineligible" | "review" => {
  const d = String(decision ?? "").toLowerCase();
  if (d === "eligible") return "eligible";
  if (d === "ineligible") return "ineligible";
  return "review";
};

/**
 * 32.6 — the service's cost-share preview → the typed union the screen renders.
 *
 * <p>The service's `reason` is English prose. It is NOT passed through: an Arabic-reading receptionist must
 * not be shown an English sentence about what a beneficiary owes (ADR-0042). The three cases it can report
 * are mapped to typed pairs here, and an unrecognised one falls to a sentence that says the share is unknown
 * rather than to a number.</p>
 */
const toCostShare = (preview: any, category: string | undefined): any => {
  if (!category) {
    return {
      known: false,
      why: {
        en: "No benefit category was chosen, so no copay can be quoted. This is not a report that the "
          + "member pays nothing.",
        ar: "لم تُختَر فئة منفعة، لذا لا يمكن تحديد المساهمة. وهذا ليس تأكيداً بأن المستفيد لا يدفع شيئاً.",
      },
    };
  }
  if (preview?.determinate === true) {
    return {
      known: true,
      tierCode: preview.tierCode ?? null,
      copayPercent: preview.copayPercent ?? null,
      copayFixed: preview.copayFixed ?? null,
      coinsurancePercent: preview.coinsurancePercent ?? null,
    };
  }
  const reason = String(preview?.reason ?? "");
  if (/tier could be resolved/i.test(reason)) {
    return {
      known: false,
      why: {
        en: "No network tier could be resolved for this provider on this date, so the member's share is unknown.",
        ar: "تعذّر تحديد شريحة الشبكة لهذا المقدّم في هذا التاريخ، لذا فإن حصة المستفيد غير معروفة.",
      },
    };
  }
  if (/could not be read/i.test(reason)) {
    return {
      known: false,
      why: {
        en: "The plan's cost share could not be read, so the member's share is unknown. This is not a "
          + "report that the service is free.",
        ar: "تعذّرت قراءة المساهمة في الخطة، لذا فإن حصة المستفيد غير معروفة. وهذا ليس تأكيداً بأن الخدمة مجانية.",
      },
    };
  }
  return {
    known: false,
    why: {
      en: "This plan does not price this benefit category, so no copay can be quoted.",
      ar: "لا تُسعّر هذه الخطة فئة المنفعة هذه، لذا لا يمكن تحديد المساهمة.",
    },
  };
};

// 32.6 — `statusToVerdict` lived here and is gone. It mapped a cached member-status STRING to an eligibility
// verdict in the browser, which is how the reception desk came to answer a question it had never asked
// eligibility-service. Deleted rather than left unused: the next screen that needed a verdict in a hurry
// would have found it.
/** Map a beneficiary status (Pending/Active/Suspended/Expired/Blocked/Inactive) → a non-color StatusKind chip. */
const beneficiaryStatusChip = (s: unknown): { kind: "ok" | "info" | "warn" | "bad" | "neu"; label: { en: string; ar: string } } => {
  const k = String(s ?? "Pending");
  const map: Record<string, { kind: "ok" | "info" | "warn" | "bad" | "neu"; label: { en: string; ar: string } }> = {
    Pending: { kind: "info", label: { en: "Pending", ar: "قيد الانتظار" } },
    Active: { kind: "ok", label: { en: "Active", ar: "نشط" } },
    Suspended: { kind: "warn", label: { en: "Suspended", ar: "موقوف" } },
    Expired: { kind: "neu", label: { en: "Expired", ar: "منتهٍ" } },
    Blocked: { kind: "bad", label: { en: "Blocked", ar: "محظور" } },
    Inactive: { kind: "neu", label: { en: "Inactive", ar: "غير نشط" } },
  };
  return map[k] ?? map.Pending;
};
/** Map an emr appointment status (Booked/CheckedIn/Completed/NoShow/Cancelled) → a non-color StatusKind chip. */
/** Code → title, or "" for a code masterdata does not carry. Module-level: the ICD catalogue is immutable
 *  within a deployment, and the same codes recur across sections and across patients. */
const icdTitleCache = new Map<string, string>();

const apptStatusChip = (s: unknown): { kind: "ok" | "info" | "warn" | "neu"; label: { en: string; ar: string } } => {
  const k = String(s ?? "Booked");
  const map: Record<string, { kind: "ok" | "info" | "warn" | "neu"; label: { en: string; ar: string } }> = {
    Booked: { kind: "info", label: { en: "Booked", ar: "محجوز" } },
    CheckedIn: { kind: "ok", label: { en: "Checked in", ar: "تم الوصول" } },
    Completed: { kind: "neu", label: { en: "Completed", ar: "مكتمل" } },
    NoShow: { kind: "warn", label: { en: "No-show", ar: "لم يحضر" } },
    Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغى" } },
  };
  return map[k] ?? map.Booked;
};
/** Map a provider/contract status → a non-color StatusKind chip (Active=ok, Suspended/Draft=warn, Terminated/Expired=neu). */
const providerStatusChip = (s: unknown): { kind: "ok" | "warn" | "neu" | "info"; label: { en: string; ar: string } } => {
  const k = String(s ?? "");
  const map: Record<string, { kind: "ok" | "warn" | "neu" | "info"; label: { en: string; ar: string } }> = {
    Active: { kind: "ok", label: { en: "Active", ar: "نشط" } },
    Suspended: { kind: "warn", label: { en: "Suspended", ar: "موقوف" } },
    Terminated: { kind: "neu", label: { en: "Terminated", ar: "منتهٍ" } },
    Draft: { kind: "warn", label: { en: "Draft", ar: "مسودة" } },
    Expired: { kind: "neu", label: { en: "Expired", ar: "منتهٍ" } },
    // Practitioner vocabulary (14.5) shares Active/Suspended with providers and adds this third state.
    Inactive: { kind: "neu", label: { en: "Inactive", ar: "غير نشط" } },
  };
  return map[k] ?? { kind: "info", label: { en: k || "—", ar: k || "—" } };
};

/**
 * Today's CIVIL date in Cairo, as `YYYY-MM-DD`, for a `DateOnly` the server compares against ITS today.
 *
 * `new Date().toISOString().slice(0,10)` is the UTC date, which between midnight and 02:00/03:00 Cairo is
 * still YESTERDAY. For a branch assignment's `validFrom` that direction happens to be harmless — the server
 * tests `valid_from <= today` — but relying on which way an off-by-one-day error falls is not a rule anyone
 * can maintain, and the same expression copied to a field that must not be backdated would be a live bug.
 */
function cairoToday(): string {
  // en-CA gives ISO-ordered YYYY-MM-DD, which is the format the wire wants.
  return new Intl.DateTimeFormat("en-CA", {
    timeZone: "Africa/Cairo", year: "numeric", month: "2-digit", day: "2-digit",
  }).format(new Date());
}

/**
 * `?from=&to=` for the reporting reads, plus whatever else the caller needs on the same query.
 *
 * <p>Returns an EMPTY string when there is no period, rather than `?from=&to=`. An empty `from` is not the
 * same request as an absent one: the server parses what it is given and falls back to its own default only
 * when the parameter is missing, so sending blanks would have every director screen silently asking for
 * whatever `DateOnly.TryParse("")` decided.</p>
 *
 * <p>The period reaches the server at all because it never used to. Every reporting endpoint has accepted
 * `from`/`to` since phase 8.2 and the director portal sent neither, so two KPIs built from two endpoints
 * with different server defaults — thirty days and ninety — sat in the same row with nothing on screen
 * saying they covered different windows.</p>
 */
/**
 * Hand a downloaded file to the user.
 *
 * <p>An object URL and an anchor click — the only way a browser saves bytes it already holds. The URL is
 * revoked immediately afterwards: it pins the blob in memory for as long as it lives, and a finance export
 * is an audited extract of what the organisation spent, which should not outlive the click that produced
 * it.</p>
 *
 * <p>Guarded on `document` so a non-DOM environment (a contract test driving this client over a stubbed
 * `fetch`) exercises the mapping without needing a document to click into.</p>
 */
function saveBlob(blob: Blob, filename: string): void {
  if (typeof document === "undefined" || typeof URL?.createObjectURL !== "function") return;
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = filename;
  a.rel = "noopener";
  document.body.appendChild(a);
  a.click();
  a.remove();
  URL.revokeObjectURL(url);
}

function periodQuery(period?: Period, extra?: Record<string, string | undefined>): string {
  const params = new URLSearchParams();
  if (period?.from) params.set("from", period.from);
  if (period?.to) params.set("to", period.to);
  for (const [k, v] of Object.entries(extra ?? {})) if (v) params.set(k, v);
  const q = params.toString();
  return q ? `?${q}` : "";
}

/**
 * `?dispense=<lineId>:<qty>` / `?perform=<lineId>:<qty>`, repeated — the basis a cost share is quoted on.
 *
 * <p>Zero and negative entries are dropped rather than sent. A line the counter has not touched is not part
 * of what is being handed over, and sending it as `:0` would be indistinguishable on the wire from a line
 * someone deliberately zeroed — which the server would have to treat the same way regardless. Dropping it
 * here keeps the query as short as the answer is.</p>
 *
 * <p>An empty basis produces an empty string, so the caller asks the whole-prescription question rather than
 * asking for a quote on nothing. That distinction is the reason the tiles never read "Patient pays EGP 0.00"
 * on a screen where nothing has been entered yet.</p>
 */
function basisQuery(param: string, basis?: Record<string, number>): string {
  const parts = Object.entries(basis ?? {})
    .filter(([id, q]) => id && Number.isFinite(q) && q > 0)
    .map(([id, q]) => `${param}=${encodeURIComponent(`${id}:${q}`)}`);
  return parts.length === 0 ? "" : `?${parts.join("&")}`;
}

/** One practitioner row → the contract shape. Shared by the list and the create path. */
function toPractitioner(p: any) {
  return parseOr(zPractitioner, {
    id: required(p?.practitionerId, "practitioner.practitionerId"),
    practitionerType: String(p?.practitionerType ?? ""),
    name: { en: String(p?.fullNameEn ?? ""), ar: String(p?.fullNameAr ?? p?.fullNameEn ?? "") },
    primarySpecialty: p?.primarySpecialty ?? undefined,
    specialties: Array.isArray(p?.specialties) ? p.specialties.map(String) : [],
    branches: Array.isArray(p?.branches) ? p.branches.map(String) : [],
    status: providerStatusChip(p?.status),
    // Absent, not blank, for a caller without provider:write — the server omits it entirely.
    licenseNo: p?.licenseNo ?? undefined,
  });
}

/**
 * The server's own reason for a failed attachment, kept as a STRING rather than run through
 * `writeErrorMessage`.
 *
 * That classifier answers "what should the operator do about this whole submission?" — retry, reload, stop —
 * and the answer here is none of those: the submission partly succeeded and the remedy is to finish the one
 * assignment that did not land. What the operator needs is which specialty or clinic failed and what the
 * service said about it ("specialty already assigned or a primary specialty already exists"), which is
 * exactly `ApiError.reason`.
 */
function attachReason(e: unknown): string {
  return e instanceof ApiError ? e.reason : String(e);
}
type Chip = { kind: "ok" | "info" | "part" | "warn" | "bad" | "neu"; label: { en: string; ar: string } };

/**
 * A claim lifecycle status → a non-colour StatusKind chip.
 *
 * FOUR OF THE ELEVEN REAL STATUSES WERE MISSING AND FOUR OF THE EIGHT ENTRIES NAMED NOTHING. The map listed
 * `UnderReview`, `Adjudicated`, `Rejected` and `Cancelled`; `ClaimStatus` has none of those. Meanwhile
 * `UnderAdjudication`, `Approved`, `Denied`, `PendingInfo`, `ClinicalReview`, `Appealed` and `Void` — the
 * statuses claims actually hold — all fell through to the neutral fallback, which puts the raw English token
 * in the Arabic slot. An Arabic reader saw "Denied" in Latin script on a denied claim.
 */
const claimStatusChip = (s: unknown): Chip => {
  const k = String(s ?? "");
  const map: Record<string, Chip> = {
    Draft: { kind: "neu", label: { en: "Draft", ar: "مسودة" } },
    Submitted: { kind: "info", label: { en: "Submitted", ar: "مُقدّمة" } },
    UnderAdjudication: { kind: "info", label: { en: "Under adjudication", ar: "قيد البتّ" } },
    PendingInfo: { kind: "warn", label: { en: "Awaiting information", ar: "بانتظار معلومات" } },
    ClinicalReview: { kind: "warn", label: { en: "Clinical review", ar: "مراجعة سريرية" } },
    Approved: { kind: "ok", label: { en: "Approved", ar: "معتمدة" } },
    PartiallyApproved: { kind: "part", label: { en: "Partially approved", ar: "موافقة جزئية" } },
    Denied: { kind: "bad", label: { en: "Denied", ar: "مرفوضة" } },
    Settled: { kind: "ok", label: { en: "Settled", ar: "مُسوّاة" } },
    Appealed: { kind: "warn", label: { en: "Appealed", ar: "مستأنَفة" } },
    Void: { kind: "neu", label: { en: "Void", ar: "ملغاة" } },
  };
  return map[k] ?? { kind: "neu", label: { en: k || "—", ar: k || "—" } };
};

/** A claim LINE status (`ClaimLineStatus`) → a chip. Distinct vocabulary from the claim's own. */
const claimLineStatusChip = (s: unknown): Chip => {
  const k = String(s ?? "");
  const map: Record<string, Chip> = {
    Pending: { kind: "info", label: { en: "Pending", ar: "معلّق" } },
    Approved: { kind: "ok", label: { en: "Approved", ar: "معتمد" } },
    PartiallyApproved: { kind: "part", label: { en: "Partially approved", ar: "موافقة جزئية" } },
    Denied: { kind: "bad", label: { en: "Denied", ar: "مرفوض" } },
    Adjusted: { kind: "warn", label: { en: "Adjusted", ar: "معدَّل" } },
    Void: { kind: "neu", label: { en: "Void", ar: "ملغى" } },
  };
  return map[k] ?? { kind: "neu", label: { en: k || "—", ar: k || "—" } };
};

/**
 * A reconciliation bucket → a chip. All SIX, including the two that carry the money.
 *
 * `Duplicate` is the double-billing signal and `QuantityVariance` is a provider billing more units than were
 * delivered. Both were classified server-side, neither had a chip, so both rendered as their raw English token
 * in both languages — and neither could be filtered to.
 */
const reconBucketChip = (s: unknown): Chip => {
  const k = String(s ?? "");
  const map: Record<string, Chip> = {
    Matched: { kind: "ok", label: { en: "Matched", ar: "مطابقة" } },
    PriceVariance: { kind: "warn", label: { en: "Price variance", ar: "فرق سعر" } },
    QuantityVariance: { kind: "warn", label: { en: "Quantity variance", ar: "فرق كمية" } },
    BilledNotDelivered: { kind: "bad", label: { en: "Billed, not delivered", ar: "فوترة بلا تنفيذ" } },
    DeliveredNotBilled: { kind: "info", label: { en: "Delivered, not billed", ar: "تنفيذ بلا فوترة" } },
    Duplicate: { kind: "bad", label: { en: "Duplicate", ar: "مكرّرة" } },
  };
  return map[k] ?? { kind: "neu", label: { en: k || "—", ar: k || "—" } };
};

/**
 * Who asked, from the request's SOURCE.
 *
 * This was the literal string "Provider" on every row of the approval queue — including manual
 * authorizations, which the approval team raises itself and which carry no requesting provider at all. A
 * constant that is true of some rows and false of others is worse than an empty column: it cannot be
 * questioned, because it never looks missing.
 */
const requesterLabel = (source: unknown): { en: string; ar: string } => {
  const map: Record<string, { en: string; ar: string }> = {
    OrderLine: { en: "Clinician order", ar: "طلب طبيب" },
    Prescription: { en: "Prescription", ar: "وصفة" },
    Manual: { en: "Raised by the approval team", ar: "من فريق الموافقات" },
    ValidityExtension: { en: "Validity extension request", ar: "طلب تمديد صلاحية" },
  };
  return map[String(source ?? "")] ?? { en: "—", ar: "—" };
};

/** Map an emr encounter status → a resolved bilingual StatusKind for the doctor worklist chip. */
const encounterStatus = (s: unknown) => {
  const k = String(s ?? "InProgress");
  const map: Record<string, { kind: "ok" | "info" | "neu"; label: { en: string; ar: string } }> = {
    InProgress: { kind: "info", label: { en: "In progress", ar: "جارٍ" } },
    Completed: { kind: "ok", label: { en: "Completed", ar: "مكتمل" } },
    Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغى" } },
  };
  return map[k] ?? map.InProgress;
};
/** Map a coverage/eligibility status string → a resolved bilingual StatusKind chip for coordination views. */
const coverageChip = (s: unknown): { kind: "ok" | "warn" | "bad" | "neu"; label: { en: string; ar: string } } => {
  const k = String(s ?? "").toLowerCase();
  if (k === "active") return { kind: "ok", label: { en: "Active", ar: "نشط" } };
  if (k === "blocked" || k === "expired" || k === "lapsed") return { kind: "bad", label: { en: k[0].toUpperCase() + k.slice(1), ar: "منتهٍ" } };
  if (k === "none") return { kind: "neu", label: { en: "None", ar: "لا يوجد" } };
  return { kind: "warn", label: { en: "Review", ar: "قيد المراجعة" } };
};
/** Map a notification's canonical status vocabulary → a non-color StatusKind chip (accessibility: hue+text). */
const notificationChip = (s: unknown): { kind: "ok" | "info" | "warn" | "bad" | "neu"; label: { en: string; ar: string } } => {
  const raw = String(s ?? "Informational");
  const k = raw.toLowerCase();
  if (k.includes("action")) return { kind: "warn", label: { en: raw, ar: "إجراء مطلوب" } };
  if (k.includes("escalat")) return { kind: "bad", label: { en: raw, ar: "تصعيد" } };
  if (k.includes("approv")) return { kind: "ok", label: { en: raw, ar: "معتمد" } };
  if (k.includes("reject") || k.includes("fail")) return { kind: "bad", label: { en: raw, ar: "مرفوض" } };
  return { kind: "info", label: { en: raw, ar: "معلومة" } };
};
/** Map a break-glass grant status → a non-color StatusKind chip (active = attention, expired/revoked = neutral). */
const breakGlassChip = (s: unknown): { kind: "ok" | "info" | "warn" | "bad" | "neu"; label: { en: string; ar: string } } => {
  const raw = String(s ?? "");
  const k = raw.toLowerCase();
  if (k === "active") return { kind: "warn", label: { en: "Active", ar: "نشط" } };
  if (k === "requested") return { kind: "info", label: { en: "Requested", ar: "مطلوب" } };
  if (k === "approved") return { kind: "info", label: { en: "Approved", ar: "معتمد" } };
  if (k === "rejected" || k === "revoked") return { kind: "bad", label: { en: raw, ar: "ملغى" } };
  return { kind: "neu", label: { en: raw || "Expired", ar: "منتهٍ" } };
};
/**
 * A settlement's lifecycle state → its chip.
 *
 * <p>All four states used to render an identical green "ok" chip, because this client wrote the literal
 * string `"ok"` into the `status` field. Two consequences, and the second is the reason the Settlements
 * screen had never worked at all:</p>
 *
 * <ol>
 *   <li>Draft, Submitted, Approved and Paid were visually indistinguishable — the four-cue rule broken at
 *       its root, not by a missing icon but by the same cue for every value. The real state travelled in
 *       `state` and was never displayed.</li>
 *   <li><b>`"ok"` is a string and `zStatus` is `{ kind, label }`, so `parseOr` threw on every call.</b>
 *       Every settlement read failed with `ApiError("schema")` against a real gateway, and no test noticed
 *       because the tests construct `DevApiClient`. Design 49 §1.</li>
 * </ol>
 *
 * <p>`Approved` is `info` rather than `ok`: approval authorises a payment, it does not complete one. `Paid`
 * is the settled state and is the only one that gets the affirmative chip — a distinction a finance clerk
 * answering "has this provider been paid" depends on.</p>
 */
const settlementChip = (s: unknown): { kind: "ok" | "info" | "warn" | "neu"; label: { en: string; ar: string } } => {
  switch (String(s ?? "").toLowerCase()) {
    case "submitted": return { kind: "warn", label: { en: "Submitted", ar: "مُقدَّمة" } };
    case "approved":  return { kind: "info", label: { en: "Approved", ar: "معتمدة" } };
    case "paid":      return { kind: "ok",   label: { en: "Paid", ar: "مدفوعة" } };
    default:          return { kind: "neu",  label: { en: "Draft", ar: "مسودة" } };
  }
};

/**
 * A coordination task's state → its chip. Same literal-`"ok"` crash as {@link settlementChip} described.
 *
 * <p>Carried along from a different portal, deliberately and knowingly: `caseTasks` and `escalations` are
 * case management, outside the finance/pharmacy audit that found this. Leaving a proven, one-line,
 * always-throwing crash in place on the grounds that it belongs to another portal is scope discipline
 * applied until it stops making sense. They are noted in design 49 §6 so the next reader takes them as two
 * lines carried, not two screens audited.</p>
 */
const taskChip = (s: unknown): { kind: "ok" | "info" | "warn" | "neu"; label: { en: string; ar: string } } => {
  switch (String(s ?? "").toLowerCase().replace(/[^a-z]/g, "")) {
    case "done":       return { kind: "ok",   label: { en: "Done", ar: "منجزة" } };
    case "inprogress": return { kind: "info", label: { en: "In progress", ar: "جارية" } };
    case "blocked":    return { kind: "warn", label: { en: "Blocked", ar: "معطّلة" } };
    default:           return { kind: "neu",  label: { en: "To do", ar: "قيد الانتظار" } };
  }
};

/** Map a case/approval priority string → the zCasePriority enum. */
const casePriority = (p: unknown): "low" | "normal" | "high" | "urgent" =>
  ({ low: "low", normal: "normal", routine: "normal", high: "high", urgent: "urgent", emergency: "urgent" })[
    String(p ?? "normal").toLowerCase()
  ] as any ?? "normal";
/** Map an orders CodeSystem to the zCoded system enum (LOCAL has no clinical code space → fall back to LOINC). */
const codeSystem = (s: unknown): "CPT" | "LOINC" | "ICD-10" | "ATC" | "RxNorm" =>
  ({ CPT: "CPT", LOINC: "LOINC", LOCAL: "LOINC" })[String(s ?? "LOINC")] as any ?? "LOINC";
/** Map an orders/order-line status → a resolved bilingual StatusKind for the fulfillment queue. */
const orderStatus = (s: unknown) => {
  const k = String(s ?? "Active");
  const map: Record<string, { kind: "ok" | "info" | "part" | "neu"; label: { en: string; ar: string } }> = {
    Active: { kind: "info", label: { en: "Active", ar: "نشط" } },
    PartiallyUsed: { kind: "part", label: { en: "Partially used", ar: "مُستخدم جزئياً" } },
    Completed: { kind: "ok", label: { en: "Completed", ar: "مكتمل" } },
    Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغى" } },
  };
  return map[k] ?? map.Active;
};
/** orderId → first available order-line id, cached from the queue so consume can target a concrete line. */
const orderLineByOrderId = new Map<string, string>();
/** encounterId → raw beneficiaryId, cached from getEncounter so doctor write actions can address orders/pharmacy. */
const encounterBeneficiary = new Map<string, string>();
/**
 * Map a pharmacy prescription/line status → a resolved bilingual StatusKind.
 *
 * ============================================================================================================
 * "APPROVED" MEANS A PERSON APPROVED IT
 * ============================================================================================================
 * `RxStatus.Approved` is reached two ways (doc 23 §3, "approve / no-gate", actor "Approval Team / auto"):
 *
 *   1. The approval team decided it, and an authorization records that decision.
 *   2. `RxRoutingPolicy` found no gate, and the SUBMIT path set it outright — `if (!route.RequiresApproval)
 *      rx.Status = RxStatus.Approved` (pharmacy Prescriptions.cs). Nobody reviewed anything.
 *
 * Both used to render the same chip. A prescriber reading their own worklist was told a reviewer had passed
 * a prescription that no reviewer had ever seen — and "approved" is not a decorative word in a benefit
 * platform: it is the claim that a clinical and financial gate was cleared by someone accountable for it.
 *
 * So the two are separated by the thing that actually distinguishes them — whether an authorization exists:
 *
 *   Submitted            → "Awaiting approval"   the gate is open and the team has not decided yet
 *   Approved, no auth    → "Verified"            the system checked it; it is dispensable; nobody approved it
 *   Approved, with auth  → "Approved"            the approval team decided it
 *   PartiallyDispensed   → "Partially dispensed"
 *   Dispensed            → "Dispensed"           the pharmacy handed it over
 *
 * The STATE MACHINE is untouched — `Approved` is still `Approved` on the wire and in the database, and doc 23
 * still governs the transitions. This is a labelling fix, which is the right size for the defect: the states
 * were correct and their names were not.
 */
const rxStatus = (s: unknown, authorizationId?: unknown) => {
  const k = String(s ?? "Approved");
  const map: Record<string, { kind: "ok" | "info" | "part" | "neu" | "warn"; label: { en: string; ar: string } }> = {
    Draft: { kind: "neu", label: { en: "Draft", ar: "مسودة" } },
    // The gated state. `warn` rather than `info`: this one is WAITING ON SOMEONE, and a prescriber scanning
    // for what is stuck needs it to stand out from the rows that are simply progressing.
    Submitted: { kind: "warn", label: { en: "Awaiting approval", ar: "بانتظار الموافقة" } },
    Approved: { kind: "info", label: { en: "Verified", ar: "تم التحقق" } },
    Rejected: { kind: "neu", label: { en: "Rejected", ar: "مرفوضة" } },
    Active: { kind: "info", label: { en: "Active", ar: "نشطة" } },
    PartiallyDispensed: { kind: "part", label: { en: "Partially dispensed", ar: "صُرفت جزئياً" } },
    Dispensed: { kind: "ok", label: { en: "Dispensed", ar: "صُرفت" } },
    Expired: { kind: "neu", label: { en: "Expired", ar: "منتهية" } },
    Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغاة" } },
  };
  // A real decision by the approval team — the ONLY thing entitled to the word "approved".
  if (k === "Approved" && authorizationId)
    return { kind: "ok" as const, label: { en: "Approved", ar: "معتمدة" } };
  return map[k] ?? map.Approved;
};

/**
 * Demo drug labels for the seeded prescription lines. The pharmacy dispensing projection is min-necessary and
 * carries only the masterdata drug_id (name resolution is a separate masterdata read, not part of the queue);
 * for the seeded rows we map those ids to their real ATC + name so the queue renders a meaningful medication.
 * Unmapped ids fall back to a token — no fabricated names.
 */
const DEMO_DRUG_LABELS: Record<string, { atc: string; en: string; ar: string }> = {
  "40d46bd1-0200-4404-b424-d9cdd05391b4": { atc: "A10BA02", en: "Metformin 500mg", ar: "ميتفورمين 500مجم" },
  "26d41d0b-2046-4e20-89f3-3a4a951570b7": { atc: "C08CA01", en: "Amlodipine 10mg", ar: "أملوديبين 10مجم" },
  "3aa10944-02db-44b2-89c6-95100b09d372": { atc: "N02BE01", en: "Paracetamol 500mg", ar: "باراسيتامول 500مجم" },
};
const drugCoded = (drugId: unknown) => {
  const d = DEMO_DRUG_LABELS[String(drugId)];
  return d
    ? { system: "ATC" as const, code: d.atc, label: { en: d.en, ar: d.ar } }
    // "Medication" was the label here, which is the name of the field standing where its value belongs — the
    // exact defect the prescriber column had. A pharmacist reading it cannot tell it from a product name.
    : { system: "ATC" as const, code: String(drugId ?? "").slice(0, 8), label: t("Medication not recorded", "الدواء غير مسجّل") };
};
/** prescriptionId → its line ids (in order), cached from the queue so dispense can target concrete lines. */
const rxLineIds = new Map<string, string[]>();
/** Map an authorization status → a resolved bilingual StatusKind for the approvals worklist. */
const authStatus = (s: unknown) => {
  const k = String(s ?? "Submitted");
  const map: Record<string, { kind: "ok" | "info" | "part" | "warn" | "bad" | "neu"; label: { en: string; ar: string } }> = {
    Submitted: { kind: "info", label: { en: "Submitted", ar: "مُقدَّم" } },
    UnderReview: { kind: "part", label: { en: "Under review", ar: "قيد المراجعة" } },
    Approved: { kind: "ok", label: { en: "Approved", ar: "معتمد" } },
    PartiallyApproved: { kind: "part", label: { en: "Partially approved", ar: "معتمد جزئياً" } },
    Rejected: { kind: "bad", label: { en: "Rejected", ar: "مرفوض" } },
    InfoRequested: { kind: "warn", label: { en: "Info requested", ar: "طُلبت معلومات" } },
    EmergencyApproved: { kind: "ok", label: { en: "Emergency approved", ar: "اعتماد طارئ" } },
    Overridden: { kind: "warn", label: { en: "Overridden", ar: "تجاوز" } },
    Expired: { kind: "neu", label: { en: "Expired", ar: "منتهٍ" } },
  };
  return map[k] ?? map.Submitted;
};
/** Decision kind → the approvals-service endpoint segment (decisions are per-type, not a single /decision). */
const decisionPath: Record<string, string> = {
  approve: "approve",
  reject: "reject",
  partial: "partially-approve",
  request_info: "request-info",
};

/**
 * Last reception search cards, keyed by beneficiaryId. The reception service returns ONE min-necessary card that
 * already carries identity + coverage + remaining limits; the fixture-era client split this into search+check, so
 * we cache the card from the search and let {@link HttpApiClient.checkEligibility} map it — no second round-trip
 * and no fabricated PHI (the card is the single source of truth reception is allowed to see).
 */
const receptionCards = new Map<string, any>();

/**
 * The production API client — talks to the phase services through the gateway (`/api/v1`), zod-validating
 * every response against the shared contract, and sending `Idempotency-Key` on consume/dispense/decide.
 *
 * It is fully wired but not exercised by the dev app (which uses `DevApiClient` fixtures) nor the tests; it is
 * the drop-in the app uses once the services are reachable behind Kong — exactly the AuthClient→OIDC pattern.
 */
export class HttpApiClient implements ApiClient {
  // Reception (Phase 2, US-010) — the eligibility service exposes ONE min-necessary reception card at
  // `/reception/search`; there is deliberately no full-demographic "get by id" for reception (Reception≠EMR).
  // We adapt the card into the search-hit + result-card contract, caching the card so the check step needs no
  // second call (and never fabricates DOB/gender the card intentionally omits).
  async searchEligibility(query: string, signal?: AbortSignal) {
    const r = (await getRaw(`/reception/search?q=${encodeURIComponent(query)}`, signal)) as any;
    const cards: any[] = r?.results ?? [];
    receptionCards.clear();
    for (const c of cards) receptionCards.set(String(c.identity?.beneficiaryId), c);
    return cards.map((c: any) => {
      // The card has always carried this; the mapping used to drop it, so a search result could not tell a
      // suspended member from an active one and the desk discovered it only when the booking was refused.
      const raw = c.identity?.status;
      return parseOr(zEligibilityHit, {
        id: c.identity?.beneficiaryId,
        name: neutral(c.identity?.displayName),
        cardNumber: c.identity?.memberNo ?? "",
        // The server's own resolved semantics where present (label + non-colour tone), so the chip here says
        // exactly what the eligibility card says rather than a second opinion derived from the raw string.
        status: raw
          ? { kind: toneToKind(c.identity?.statusSemantics?.tone), label: neutral(c.identity?.statusSemantics?.label ?? raw) }
          : undefined,
        // Default-DENY on an absent or unrecognised status: "not stated" must not render as bookable.
        bookable: String(raw ?? "") === "Active",
      });
    });
  }
  /**
   * 32.6 — THE eligibility check, asked of eligibility-service.
   *
   * <p>This method used to make no network call at all. It read the reception search cache, compared
   * `identity.status` to the string "active", and returned that as the verdict. So none of the properties
   * eligibility-service exists to apply reached the desk: not the network tier, not the plan version in force
   * on the service date, not the waiting period, not the remaining limits, and not the audit event — the
   * question "who checked this beneficiary's eligibility, and what were they told?" had no answer on the
   * chain, because as far as the platform was concerned nobody had checked anything. The screen promised a
   * copay in its own idle text and there was no code path that could ever produce one.</p>
   *
   * <p><b>Two questions, each asked of the service that owns it.</b> The membership check (no category) gives
   * the visit gate: may this person be admitted today. The benefit check (with a category) gives cover and
   * cost share for the care they came for. They are separate because a `NeedsAuthorization` verdict on a
   * benefit is a soft No that routes to approvals — it does not turn the person away at the door, and
   * collapsing the two would do exactly that.</p>
   *
   * <p>The identity block still comes from the reception card, which is the min-necessary projection the desk
   * is entitled to. That was never the defect; deciding eligibility from it was.</p>
   */
  async checkEligibility(beneficiaryId: string, benefitCategory?: string) {
    const c = receptionCards.get(String(beneficiaryId));
    const identity = c?.identity ?? {};
    const categories: string[] = c?.coverage ?? [];
    const limits: any[] = c?.remainingLimits ?? [];
    // Pick a monetary remaining-limit (annual cap) for the coverage summary, if the card carries one.
    const cap = limits.find((l) => /amount|annual/i.test(String(l.limitType)));

    const membership = (await postRaw(`/eligibility/check`, { beneficiaryId })) as any;
    const category = benefitCategory?.trim() || undefined;
    const benefit = category
      ? ((await postRaw(`/eligibility/check`, { beneficiaryId, benefitCategory: category })) as any)
      : null;

    // The verdict on screen is the one about the question that was actually asked.
    const answering = benefit ?? membership;
    const decision = String(answering?.decision ?? "");

    return parseOr(zEligibilityResult, {
      verdict: decisionToVerdict(decision),
      // From the SERVER's own label, not from whether a category happened to be passed here: a response
      // whose scope disagrees with what we asked for is a contract breach we want to see, not paper over.
      scope: String(membership?.decisionScope ?? "") === "Membership" && !benefit ? "membership" : "benefit",
      benefitCategory: category ?? null,
      status: { kind: toneToKind(identity.statusSemantics?.tone), label: neutral(identity.statusSemantics?.label ?? identity.status) },
      beneficiary: {
        id: identity.beneficiaryId ?? beneficiaryId,
        name: neutral(identity.displayName),
        cardNumber: identity.memberNo ?? "",
      },
      coverage: categories.length
        ? {
            planName: { en: "Benefit coverage", ar: "التغطية التأمينية" },
            band: neutral(categories.join(" · ")),
            annualCapRemaining: cap ? money(cap.remaining, "coverage.annualCapRemaining") : undefined,
          }
        : null,
      costShare: toCostShare(benefit?.costShare, category),
      // THE MEMBERSHIP answer, always. Whether the person may be seen today is a question about their
      // standing, and a benefit that needs authorisation is not a closed door.
      visitGate: String(membership?.decision ?? "") === "Eligible"
        ? { allowed: true }
        : {
            allowed: false,
            reason: { en: "Coverage not active — refer to eligibility desk.", ar: "التغطية غير فعّالة — يُرجى مراجعة مكتب الأهلية." },
          },
    });
  }

  // Reception day board (Phase 3, US-020) — the emr appointments list is date/status scoped (no clinic params
  // required, unlike the clinical walk-in queue). Reception sees a masked beneficiary token + type/time/status
  // only. `checkIn` posts a minimal body (normal priority); member details on the queue ticket are optional.
  async appointments(
    filter: "all" | "booked" | "checked-in" = "all",
    mine = false,
    range?: { from: string; to: string },
    branchId?: string,
  ) {
    const status = filter === "booked" ? "Booked" : filter === "checked-in" ? "CheckedIn" : "";
    const qs = new URLSearchParams();
    if (status) qs.set("status", status);
    // Sent as from/to rather than a single date: the server expands each end to its own Cairo civil day, so
    // the last day of the range includes its evening clinic.
    if (range) { qs.set("from", range.from); qs.set("to", range.to); }
    // Cross-branch callers only. A branch-scoped desk's active branch is resolved server-side and naming
    // another is refused, so sending one from reception could only ever produce a 403.
    if (branchId) qs.set("branchId", branchId);
    // ?mine is resolved from the TOKEN's subject server-side — the caller cannot ask for another doctor's list.
    if (mine) qs.set("mine", "true");
    const r = (await getRaw(`/appointments${qs.toString() ? `?${qs}` : ""}`)) as any[];

    // Put names to the branches on THIS page — one request for the distinct ids, and a failure leaves the name
    // null rather than failing the board. Branch names live behind provider:read, which the desks and the call
    // centre do not hold, so they come from the label-only lookup (see /branch-labels).
    const branchIds = [...new Set((r ?? []).map((a: any) => a.branchId).filter(Boolean).map(String))];
    const branchNames = new Map<string, string>();
    if (branchIds.length > 0) {
      try {
        const rows = (await getRaw(`/branch-labels?branchIds=${encodeURIComponent(branchIds.join(","))}`)) as any[];
        for (const row of rows ?? []) {
          if (row?.branchId && row?.nameEn) branchNames.set(String(row.branchId), String(row.nameEn));
        }
      } catch {
        // Unnamed is better than no board.
      }
    }

    return (r ?? []).map((a: any) =>
      parseOr(zAppointmentRow, {
        id: a.appointmentId,
        beneficiary: { id: a.beneficiaryId, token: caseToken({ beneficiaryId: a.beneficiaryId }) },
        appointmentType: String(a.appointmentType ?? ""),
        status: apptStatusChip(a.status),
        scheduledStart: a.scheduledStart ?? new Date().toISOString(),
        checkInEligible: String(a.status ?? "") === "Booked",
        checkedIn: String(a.status ?? "") === "CheckedIn",
        // Straight from the server — the grace period is its rule, evaluated against its clock.
        noShowEligible: a.noShowEligible === true,
        // Derived from the server's own status + doctor assignment, echoed back on the row.
        startVisitEligible: String(a.status ?? "") === "CheckedIn",
        branchId: a.branchId ?? null,
        branchName: a.branchId ? branchNames.get(String(a.branchId)) ?? null : null,
        rowVersion: typeof a.rowVersion === "number" ? a.rowVersion : undefined,
        // Null, not "", when absent — the board renders a note affordance only when there is a note to open.
        note: a.note ?? null,
        noteBy: a.noteBy ?? null,
        noteByName: a.noteByName ?? null,
        noteAt: a.noteAt ?? null,
        beneficiaryName: a.beneficiaryName ?? null,
        doctorId: a.doctorId ?? null,
        needsReassignment: a.needsReassignment === true,
        providerId: a.providerId ?? null,
        locationId: a.locationId ?? null,
      }),
    );
  }
  // ---- 32.6 — the waiting room (emr /queues, phase 3.3) ------------------------------------------------
  //
  // Five endpoints that had no caller anywhere in the product for four phases, while the WRITE half of the
  // same subsystem ran on every check-in. Nothing read the tickets and nothing cleared them.
  //
  // NO ARGUMENTS on the reads. The branch comes from the caller's validated active-branch claim server-side;
  // passing one from here would be a filter the server has to re-check anyway, and a filter that looks like
  // a permission is how a client-side narrowing gets mistaken for one.

  async waitingRoom() {
    const r = (await getRaw(`/queues`)) as any[];
    return (r ?? []).map((t) => parseOr(zWaitingTicket, {
      queueId: t.queueId,
      appointmentId: t.appointmentId,
      position: Number(t.position ?? 0),
      // NOT defaulted to a placeholder. Check-in can be recorded without them, and a board that prints
      // "Unknown" calls somebody who is not there.
      memberNo: t.memberNo ?? null,
      displayName: t.displayName ?? null,
      appointmentType: String(t.appointmentType ?? ""),
      state: String(t.state ?? ""),
      waitSeconds: Number(t.waitSeconds ?? 0),
    }));
  }

  async callNextWaiting() {
    // 204 when nobody is waiting — an empty waiting room is an answer, not a failure, and `getRaw`/`postRaw`
    // give back undefined for a no-content body.
    const r = (await postRaw(`/queues/call-next`, {})) as any;
    if (!r?.queueId) return null;
    return parseOr(zWaitingTicket, {
      queueId: r.queueId,
      appointmentId: r.appointmentId,
      position: Number(r.position ?? 0),
      memberNo: r.memberNo ?? null,
      displayName: r.displayName ?? null,
      appointmentType: String(r.appointmentType ?? ""),
      state: String(r.state ?? ""),
      waitSeconds: Number(r.waitSeconds ?? 0),
    });
  }

  async requeueWaiting(queueId: string) {
    await postRaw(`/queues/${encodeURIComponent(queueId)}/requeue`, {});
  }

  async removeWaiting(queueId: string) {
    await postRaw(`/queues/${encodeURIComponent(queueId)}/remove`, {});
  }

  async completeWaiting(queueId: string) {
    await postRaw(`/queues/${encodeURIComponent(queueId)}/complete`, {});
  }

  async checkIn(appointmentId: string, rowVersion?: number) {
    // Opt-in optimistic concurrency: echo the row version we read as If-Match; a stale board loses to a
    // concurrent transition with 412 (surfaced as an ApiError the desk shows) instead of double check-in.
    const r = (await postRaw(
      `/appointments/${encodeURIComponent(appointmentId)}/check-in`,
      { priority: 1 },
      undefined,
      rowVersion !== undefined ? { ifMatch: rowVersion } : undefined,
    )) as any;
    return parseOr(zCheckInResult, { id: r?.appointmentId ?? appointmentId, status: apptStatusChip(r?.status ?? "CheckedIn") });
  }

  /**
   * The clinics this caller may book into. Two calls on purpose:
   *
   * emr answers "which clinics have bookable slots in my branch?" from the slot table under appointment:read —
   * the scope the desk already holds. provider-service then puts NAMES to those ids via a label-only lookup
   * that returns nothing but names. Reception never gains provider:read, and neither call can enumerate the
   * network: the labels require explicit ids, and the ids come only from slots the caller may already see.
   *
   * A failed label lookup degrades to the id rather than failing the booking — an unlabelled clinic is worse
   * than no booking screen, but a broken booking screen is worse than both.
   */
  async bookableClinics(branchId?: string) {
    const qs = branchId ? `?branchId=${encodeURIComponent(branchId)}` : "";
    const clinics = (await getRaw(`/branch-clinics${qs}`)) as any[];
    if (!clinics?.length) return [];

    const ids = clinics.map((c) => c.locationId).filter(Boolean);
    let labels = new Map<string, string>();
    try {
      const rows = (await getRaw(`/clinic-labels?locationIds=${encodeURIComponent(ids.join(","))}`)) as any[];
      labels = new Map((rows ?? []).map((r: any) => [String(r.locationId), `${r.providerName} · ${r.locationName}`]));
    } catch {
      // Names are a nicety; the booking itself needs only the ids.
    }

    return clinics.map((c: any) =>
      parseOr(zBookableClinic, {
        providerId: c.providerId,
        locationId: c.locationId,
        branchId: c.branchId ?? null,
        label: labels.get(String(c.locationId)) ?? String(c.locationId).slice(0, 8),
        openSlots: typeof c.openSlots === "number" ? c.openSlots : 0,
      }),
    );
  }

  async noShow(appointmentId: string, rowVersion?: number) {
    const r = (await postRaw(
      `/appointments/${encodeURIComponent(appointmentId)}/no-show`,
      {},
      crypto.randomUUID(),
      rowVersion !== undefined ? { ifMatch: rowVersion } : undefined,
    )) as any;
    return parseOr(zCheckInResult, { id: r?.appointmentId ?? appointmentId, status: apptStatusChip(r?.status ?? "NoShow") });
  }

  // ---- 30.6 amend / cancel a signed line (design 46 §1-§3) ----------------------------------------------
  //
  // Every mutation here carries an Idempotency-Key generated ONCE per call, which is the platform's rule and
  // is load-bearing on this path: a double-tapped withdraw must write one amendment record, because "how
  // often do we cancel and why" is a clinical-quality metric and one nervous double-click would inflate it.

  async amendmentReasons(kind: "order" | "prescription") {
    const path = kind === "order"
      ? "/investigation-orders/amendment-reasons"
      : "/prescriptions/amendment-reasons";
    const rows = ((await getRaw(path)) as any[]) ?? [];
    return rows.map((r: any) =>
      parseOr(zAmendReasonOption, { code: r.code, nameEn: r.nameEn, nameAr: r.nameAr }));
  }

  async cancelOrderLine(orderId: string, lineId: string, reasonCode: string, reasonText?: string) {
    await postRaw(
      `/investigation-orders/${encodeURIComponent(orderId)}/lines/${encodeURIComponent(lineId)}/cancel`,
      { reasonCode, reasonText },
      crypto.randomUUID(),
    );
  }

  async amendOrderLine(
    orderId: string, lineId: string, quantityOrdered: number, reasonCode: string, reasonText?: string,
  ) {
    await postRaw(
      `/investigation-orders/${encodeURIComponent(orderId)}/lines/${encodeURIComponent(lineId)}/amend`,
      { quantityOrdered, reasonCode, reasonText },
      crypto.randomUUID(),
    );
  }

  async cancelPrescriptionLine(rxId: string, lineId: string, reasonCode: string, reasonText?: string) {
    await postRaw(
      `/prescriptions/${encodeURIComponent(rxId)}/lines/${encodeURIComponent(lineId)}/cancel`,
      { reasonCode, reasonText },
      crypto.randomUUID(),
    );
  }

  async amendPrescriptionLine(
    rxId: string, lineId: string, quantityPrescribed: number, reasonCode: string, reasonText?: string,
  ) {
    await postRaw(
      `/prescriptions/${encodeURIComponent(rxId)}/lines/${encodeURIComponent(lineId)}/amend`,
      { quantityPrescribed, reasonCode, reasonText },
      crypto.randomUUID(),
    );
  }

  /**
   * 30.6 — withdraw a WHOLE prescription (pharmacy `POST /prescriptions/{id}/cancel`).
   *
   * <p>The endpoint cancels every still-active line and leaves a dispensed one alone, so the report is built
   * from the prescription it returns rather than assumed: "withdrawn" for the lines that moved, and the
   * refusal named for the ones that did not. Reporting a blanket success here is precisely the "silently
   * doing half" failure design 46 §3 rules out.</p>
   */
  async withdrawPrescription(rxId: string, reasonCode: string, reasonText?: string): Promise<WithdrawResult> {
    const r = (await postRaw(
      `/prescriptions/${encodeURIComponent(rxId)}/cancel`,
      { reasonCode, reason: reasonText ?? reasonCode },
      crypto.randomUUID(),
    )) as { lines?: { drugName?: string | null; drugId?: string | null; status?: string }[] };

    const lines = (r.lines ?? []).map((l) => ({
      label: l.drugName ?? l.drugId ?? "—",
      withdrawn: l.status === "Cancelled",
      refusal: l.status === "Cancelled" ? null : l.status ?? null,
    }));
    return { withdrawn: lines.filter((l) => l.withdrawn).length, total: lines.length, lines };
  }

  /**
   * 30.6 — withdraw a WHOLE order (orders `POST /{id}/cancel-lines`).
   *
   * <p>The endpoint answers 200, 207 or 409 by how much of it succeeded and names every refusal per line. All
   * three are read the same way here, because "three of five withdrawn" is a real answer that the doctor has
   * to see rather than an error to swallow.</p>
   */
  async withdrawOrder(orderId: string, reasonCode: string, reasonText?: string): Promise<WithdrawResult> {
    const r = (await postRaw(
      `/investigation-orders/${encodeURIComponent(orderId)}/cancel-lines`,
      { reasonCode, reasonText },
      crypto.randomUUID(),
    )) as { cancelled?: number; lines?: { code: string; cancelled: boolean; refusal?: string | null }[] };

    const lines = (r.lines ?? []).map((l) => ({
      label: l.code,
      withdrawn: l.cancelled,
      refusal: l.refusal ?? null,
    }));
    return { withdrawn: r.cancelled ?? lines.filter((l) => l.withdrawn).length, total: lines.length, lines };
  }

  async cancelAppointment(appointmentId: string, reason: string, rowVersion?: number) {
    const r = (await postRaw(
      `/appointments/${encodeURIComponent(appointmentId)}/cancel`,
      { reason },
      crypto.randomUUID(),
      rowVersion !== undefined ? { ifMatch: rowVersion } : undefined,
    )) as any;
    return parseOr(zCheckInResult, { id: r?.appointmentId ?? appointmentId, status: apptStatusChip(r?.status ?? "Cancelled") });
  }

  async updateAppointmentNote(appointmentId: string, note: string) {
    await postRaw(`/appointments/${encodeURIComponent(appointmentId)}/note`, { note });
  }
  async rescheduleAppointment(appointmentId: string, newSlotId: string, rowVersion?: number) {
    await postRaw(
      `/appointments/${encodeURIComponent(appointmentId)}/reschedule`,
      { newSlotId },
      crypto.randomUUID(),
      rowVersion !== undefined ? { ifMatch: rowVersion } : undefined,
    );
  }

  async appointmentTimeline(appointmentId: string) {
    return this.readTimeline(`/appointments/${encodeURIComponent(appointmentId)}/timeline`);
  }

  /**
   * The care episode of ONE visit (ADR-0031) — what the encounter workspace, and every order and prescription
   * raised in it, shows as its history.
   *
   * Identical in shape to the appointment timeline, and read through the same helper, because they are the
   * same list seen from two ends: the appointment's own view reaches the visit's steps from the booking down,
   * this one reads them directly. A doctor inside the workspace has no appointment id to hand — a walk-in has
   * no appointment at all — which is why reaching them the long way round was not an option.
   */
  async encounterTimeline(encounterId: string) {
    return this.readTimeline(`/encounters/${encodeURIComponent(encounterId)}/timeline`);
  }

  /** Fetch a timeline and put NAMES to its actor ids. Shared so both timelines resolve actors identically. */
  private async readTimeline(path: string) {
    // 30.5c — the ENCOUNTER timeline now answers `{ steps, opening }` so it can carry the check-in and the
    // waiting time derived from it (design 46 §7c); the APPOINTMENT timeline is still a bare array. Both
    // shapes are accepted here rather than in two helpers, because they are the same list seen from two ends
    // and splitting them is how the actor-name resolution would drift between the two. The `opening` half is
    // not surfaced yet — that is Gate 6 UI work — but accepting the envelope keeps the client working today.
    const r = (await getRaw(path)) as any;
    const steps: any[] = Array.isArray(r) ? r : (r?.steps ?? []);

    // Put names to the actor ids. One request for the DISTINCT ids on this timeline, not one per step — a
    // rebooked appointment repeats the same actor several times. A failure here degrades to the id rather than
    // failing the timeline: knowing when a no-show was marked is worth more than knowing nobody's name.
    const ids = [...new Set(steps.map((x: any) => x.by).filter(Boolean).map(String))];
    const names = new Map<string, string>();
    if (ids.length > 0) {
      try {
        const rows = (await getAbsolute(
          `${GATEWAY_BASE}/identity/user-labels?subjectIds=${encodeURIComponent(ids.join(","))}`,
        )) as any[];
        for (const row of rows ?? []) {
          if (row?.subjectId && row?.displayName) names.set(String(row.subjectId), String(row.displayName));
        }
      } catch {
        // Left unresolved on purpose — see above.
      }
    }

    return steps.map((x: any) =>
      parseOr(zTimelineStep, {
        status: x.status,
        at: x.at,
        by: x.by ?? null,
        byName: x.by ? names.get(String(x.by)) ?? null : null,
        source: x.source ?? null,
        reference: x.reference ?? null,
      }),
    );
  }

  async startVisit(appointmentId: string, beneficiaryId: string) {
    // POST /encounters is where the CheckedIn + assigned-doctor rules are enforced, so starting a visit goes
    // through it rather than through a UI-only shortcut.
    const r = (await postRaw("/encounters", { beneficiaryId, appointmentId }, crypto.randomUUID())) as any;
    return { encounterId: String(required(r?.encounterId, "startEncounter.encounterId")) };
  }

  // Booking (Phase 3.1, US-020). Slot availability is the SERVER's answer — it holds the no-double-book
  // invariant and can see slots held by bookings this desk is not allowed to read, so `open` is never
  // re-derived here from times.
  async openSlots(providerId: string, locationId: string, from?: string, to?: string, doctorId?: string) {
    const qs = new URLSearchParams({ providerId, locationId, onlyOpen: "true" });
    if (from) qs.set("from", from);
    if (to) qs.set("to", to);
    // Narrowed server-side once the booking screen has picked a doctor — shipping the whole clinic's
    // fortnight and hiding most of it would be slower and would put one clinician's calendar in front of a
    // client that asked about another.
    if (doctorId) qs.set("doctorId", doctorId);
    const r = (await getRaw(`/appointment-slots?${qs.toString()}`)) as any[];
    return (r ?? []).map((s: any) =>
      parseOr(zBookableSlot, {
        id: s.slotId,
        start: s.slotStart,
        end: s.slotEnd,
        open: s.open !== false,
        doctorId: s.doctorId ?? undefined,
      }),
    );
  }

  async bookAppointment(input: BookingRequest) {
    // Idempotency-Key is REQUIRED by the endpoint: a retried booking must not hold two slots for one patient.
    const r = (await postRaw(
      "/appointments",
      {
        beneficiaryId: input.beneficiaryId,
        providerId: input.providerId,
        locationId: input.locationId,
        slotId: input.slotId,
        appointmentType: input.appointmentType,
        // Omitted by a branch-scoped desk — the server stamps its active branch and refuses a mismatch.
        ...(input.branchId ? { branchId: input.branchId } : {}),
        // Both omitted rather than sent as null when unset: the slot is authoritative for the doctor when
        // there is one, and an explicit null would overwrite that with "no doctor".
        ...(input.doctorId ? { doctorId: input.doctorId } : {}),
        ...(input.note ? { note: input.note } : {}),
        ...(input.beneficiaryName ? { beneficiaryName: input.beneficiaryName } : {}),
        joinWaitlistIfFull: false,
      },
      crypto.randomUUID(),
    )) as any;
    return parseOr(zBookingResult, {
      id: required(r?.appointmentId, "booking.appointmentId"),
      status: apptStatusChip(r?.status ?? "Booked"),
      scheduledStart: r?.scheduledStart ?? new Date().toISOString(),
    });
  }

  // Doctor / EMR (Phase 4, US-030) — the emr service is encounter-centric and treating-relationship gated: the
  // "my patients" worklist is the caller's own encounters (/encounters/mine), and a patient row's id IS its
  // encounter id, so getEncounter maps straight to /encounters/{id}/clinical. emr stores the beneficiary id but
  // not the name (that lives in patient-service), so we render a masked token — the doctor's zone still shows
  // full clinical detail (diagnoses/SOAP/vitals) that no other zone may see.
  async listPatients() {
    const r = (await getRaw(`/encounters/mine`)) as any[];

    // Put names to the branches on this worklist — one request for the distinct ids, and a failure leaves the
    // name null rather than failing the list. Identical to the day board's lookup and for the same reason:
    // branch names sit behind provider:read, which a doctor does not hold, so they come from the label-only
    // endpoint instead of from provider-service.
    const branchIds = [...new Set((r ?? []).map((e: any) => e.branchId).filter(Boolean).map(String))];
    const branchNames = new Map<string, string>();
    if (branchIds.length > 0) {
      try {
        const rows = (await getRaw(`/branch-labels?branchIds=${encodeURIComponent(branchIds.join(","))}`)) as any[];
        for (const row of rows ?? []) {
          if (row?.branchId && row?.nameEn) branchNames.set(String(row.branchId), String(row.nameEn));
        }
      } catch {
        // Unnamed is better than no worklist.
      }
    }

    return (r ?? []).map((e: any) =>
      parseOr(zPatientListItem, {
        id: e.encounterId,
        beneficiaryId: String(required(e.beneficiaryId, "encounter.beneficiaryId")),
        // The name when emr has one — this is the TREATING clinician's own worklist, and they read the full
        // record behind every row. The masked token stays as the fallback for a walk-in that was never
        // booked, where no name was ever captured; blank would read as data loss.
        name: e.beneficiaryName
          ? neutral(String(e.beneficiaryName))
          : neutral(`Beneficiary •••${String(e.beneficiaryId ?? "").slice(-4)}`),
        mrn: e.encounterNo ?? "",
        treating: true,
        lastVisit: e.startedAt ? String(e.startedAt).slice(0, 10) : null,
        status: encounterStatus(e.status),
        branchId: e.branchId ?? null,
        branchName: e.branchId ? branchNames.get(String(e.branchId)) ?? null : null,
      }),
    );
  }
  async getEncounter(encounterId: string) {
    const r = (await getRaw(`/encounters/${encodeURIComponent(encounterId)}/clinical`)) as any;
    const e = r?.encounter ?? {};
    // Cache the raw beneficiaryId so downstream write actions (place order / prescribe) can address the
    // orders/pharmacy services, which key on the beneficiary — the doctor UI itself only ever shows the mask.
    if (e.beneficiaryId) encounterBeneficiary.set(encounterId, String(e.beneficiaryId));

    // The encounter's WORKING note — the one the workspace edits. Addenda are separate notes that point at
    // their original, and a signed note is immutable, so "the note to edit" is the newest note that is
    // neither. Taking `notes[0]` picked whatever the database returned first, which after a single addendum
    // was as likely to be the locked original as the live draft.
    const allNotes: any[] = r?.notes ?? [];
    const notes: any[] = allNotes.filter((n: any) => !n.addendumOfNoteId);
    notes.sort((a, b) => String(b.authoredAt ?? "").localeCompare(String(a.authoredAt ?? "")));
    const note = notes.find((n: any) => !n.isSigned) ?? notes[0] ?? {};

    // 32.3 — the corrections appended to THIS note. They were filtered out above and then discarded, which
    // is why an addendum written by anyone was invisible to every reader: the mechanism the workspace tells
    // a doctor to use produced a record nothing displayed. Oldest first, because they are read in the order
    // they were made.
    const addenda = allNotes
      .filter((n: any) => n.addendumOfNoteId && String(n.addendumOfNoteId) === String(note.noteId ?? ""))
      .sort((a, b) => String(a.authoredAt ?? "").localeCompare(String(b.authoredAt ?? "")))
      .map((n: any) => ({
        id: n.noteId,
        authoredAt: n.authoredAt,
        authoredByName: n.authoredByName ?? null,
        soap: {
          subjective: n.subjective ?? "",
          objective: n.objective ?? "",
          assessment: n.assessment ?? "",
          plan: n.plan ?? "",
        },
      }));

    const vitals: any[] = r?.vitals ?? [];
    // Newest first, so `v()` answers with the CURRENT reading. Unsorted, a vitals panel showed whichever row
    // came back first — on an encounter where a nurse re-took the temperature, that is the reading the
    // doctor already knows is stale.
    vitals.sort((a, b) => String(b.measuredAt ?? "").localeCompare(String(a.measuredAt ?? "")));
    const v = (type: string) => {
      const hit = vitals.find((x) => String(x.vitalType) === type);
      return hit?.valueNum === undefined || hit?.valueNum === null ? null : Number(hit.valueNum);
    };

    // Resolve the ICD titles so the assessment reads as conditions rather than as codes. Cached per session
    // and failure-tolerant (see `icdTitles`) — a reference lookup must never take the clinical record down.
    const codes = [...new Set((r?.diagnoses ?? []).map((d: any) => String(d.icdCode ?? "")).filter(Boolean))];
    const titles = await this.icdTitles(codes as string[]);

    return parseOr(zEncounter, {
      id: e.encounterId ?? encounterId,
      patientId: required(e.beneficiaryId, "encounter.beneficiaryId"),
      patientName: neutral(`Beneficiary •••${String(e.beneficiaryId ?? "").slice(-4)}`),
      openedAt: e.startedAt ?? new Date().toISOString(),
      signed: Boolean(note.isSigned),
      noteId: note.noteId ?? null,
      soap: {
        subjective: note.subjective ?? "",
        objective: note.objective ?? "",
        assessment: note.assessment ?? "",
        plan: note.plan ?? "",
      },
      vitals: {
        heightCm: v("Height"),
        weightKg: v("Weight"),
        systolic: v("BP"),
        diastolic: v("BPDiastolic"),
        heartRate: v("HR"),
        tempC: v("Temp"),
        spo2: v("SpO2"),
        measuredAt: vitals[0]?.measuredAt ?? null,
      },
      addenda,
      // The SUBSTANCE, which is what an allergy chip is for. This read `a.reaction ?? "Allergen"` — so a
      // penicillin allergy recorded with a reaction of "rash" displayed as "rash", and one recorded without
      // a reaction displayed as the literal word "Allergen". emr has carried `allergenDisplay` since its
      // migration 0020; `reaction` stays as the fallback for rows recorded before it.
      allergies: (r?.allergies ?? []).map((a: any) => ({
        id: a.allergyId,
        substance: neutral(a.allergenDisplay ?? a.reaction ?? "Allergen"),
        severity: String(a.severity ?? "mild").toLowerCase(),
      })),
      diagnoses: (r?.diagnoses ?? []).map((d: any) => ({
        id: d.diagnosisId ?? null,
        system: "ICD-10",
        code: d.icdCode,
        label: neutral(titles.get(String(d.icdCode)) ?? d.icdCode),
        rank: d.diagnosisRank === "Primary" ? "Primary" : "Secondary",
      })),
    });
  }

  // ---- Clinical documentation writes (US-031) --------------------------------------------------------
  // The workspace's Save Draft / Save & finalize. Three endpoints, deliberately not collapsed into one
  // "save" call: creating a note, amending it and SIGNING it are three different clinical acts with three
  // different audit events and three different refusal reasons, and the doctor is entitled to see which one
  // failed.
  async saveEncounterNote(encounterId: string, noteId: string | null, soap: Soap) {
    const enc = encodeURIComponent(encounterId);
    const body = {
      subjective: soap.subjective || null,
      objective: soap.objective || null,
      assessment: soap.assessment || null,
      plan: soap.plan || null,
    };
    const r = noteId
      ? ((await putRaw(`/encounters/${enc}/notes/${encodeURIComponent(noteId)}`, body)) as any)
      : ((await postRaw(`/encounters/${enc}/notes`, { noteType: "SOAP", ...body })) as any);
    return { noteId: String(r?.noteId ?? noteId ?? "") };
  }
  async signEncounterNote(encounterId: string, noteId: string) {
    await postRaw(
      `/encounters/${encodeURIComponent(encounterId)}/notes/${encodeURIComponent(noteId)}/sign`,
      {},
    );
  }
  async completeEncounter(encounterId: string) {
    await postRaw(`/encounters/${encodeURIComponent(encounterId)}/complete`, {});
  }
  async addEncounterDiagnosis(encounterId: string, icdCode: string, rank: DiagnosisRank = "Secondary") {
    const r = (await postRaw(`/encounters/${encodeURIComponent(encounterId)}/diagnoses`, {
      // emr requires a rank, and the doctor chooses it: which condition the visit was chiefly about is a
      // clinical judgement, not something derivable from the order the codes were typed in.
      icdCode,
      diagnosisRank: rank,
      clinicalStatus: "Active",
    })) as any;
    const titles = await this.icdTitles([icdCode]);
    return parseOr(zEncounterDiagnosis, {
      id: r?.diagnosisId ?? null,
      system: "ICD-10",
      code: r?.icdCode ?? icdCode,
      label: neutral(titles.get(icdCode) ?? icdCode),
      rank: r?.diagnosisRank === "Primary" ? "Primary" : rank,
    });
  }
  async removeEncounterDiagnosis(encounterId: string, diagnosisId: string) {
    await deleteRaw(
      `/encounters/${encodeURIComponent(encounterId)}/diagnoses/${encodeURIComponent(diagnosisId)}`,
    );
  }
  async searchIcd(query: string, signal?: AbortSignal) {
    const q = query.trim();
    // An empty query means "show me nothing yet", not "show me the ICD-10 catalogue". masterdata would
    // happily return the first page of tens of thousands of codes, which is a list nobody picks from.
    if (q.length < 2) return [];
    const r = (await getRaw(`/search?domain=icd&q=${encodeURIComponent(q)}`, signal)) as any[];
    return (Array.isArray(r) ? r : []).map((x: any) =>
      parseOr(zIcdRef, { code: String(x?.code ?? ""), title: String(x?.title ?? "") }),
    );
  }
  // Place an investigation order (US-032). The real endpoint is /investigation-orders and it (a) requires an
  // Idempotency-Key and (b) keys on the beneficiary — which the doctor UI never shows, so we recover it from the
  // encounter cache populated by getEncounter. Order lines validate their CPT/LOINC code against master data.
  async placeOrder(req: PlaceOrderRequest) {
    const beneficiaryId = encounterBeneficiary.get(req.encounterId) ?? req.encounterId;
    const idem = `ord:${req.encounterId}:${req.test.system}:${req.test.code}`;
    const body = {
      beneficiaryId,
      encounterId: req.encounterId,
      // 29.1 (design 45 §1) — the SWITCH: the SPA now writes only the new value. orders-service accepted
      // both from its EXPAND migration onwards, so this is safe to flip on its own deploy.
      orderType: req.kind === "radiology" ? "Radiology" : "Lab",
      expiresAt: new Date(Date.now() + 30 * 864e5).toISOString(),
      lines: [{
        codeSystem: req.test.system === "CPT" ? "CPT" : "LOINC",
        code: req.test.code,
        description: req.test.label?.en ?? req.test.code,
        quantityOrdered: 1,
      }],
    };
    const r = (await postRaw(`/investigation-orders`, body, idem)) as any;
    return parseOr(zPlaceOrderResult, {
      orderId: r?.orderId ?? r?.OrderId ?? "",
      status: orderStatus(r?.status),
      requiresApproval: String(r?.status ?? "").toLowerCase() === "pendingapproval",
    });
  }
  /**
   * Ask the approval team to revalidate an expired prescription or investigation order.
   *
   * Raises a request and nothing else — the scope behind it carries no decision authority. A 409 means one
   * is already open for this item, which is an ANSWER (someone already asked) and not a failure.
   */
  async requestValidityExtension(req: ValidityExtensionRequest) {
    const r = (await postRaw(`/authorizations/validity-extensions`, req, crypto.randomUUID())) as any;
    return parseOr(zValidityExtensionResult, {
      authorizationId: required(r?.authorizationId, "validityExtension.authorizationId"),
      authNo: String(r?.authNo ?? ""),
      status: String(r?.status ?? ""),
    });
  }

  // ---- Validity periods (Medical Director) -------------------------------------------------------------

  /**
   * The tenant's four validity periods.
   *
   * The endpoint answers for EVERY artefact whether or not a row exists, so this client never has to decide
   * what a missing key means — that decision is made once, server-side, and is the platform default.
   */
  async validityPolicy() {
    const r = (await getRaw(`/admin/validity-policy`)) as any;
    return parseOr(zValidityPolicyView, {
      defaultDays: Number(r?.defaultDays ?? 10),
      minDays: Number(r?.minDays ?? 1),
      maxDays: Number(r?.maxDays ?? 365),
      items: ((r?.items ?? []) as any[]).map((i: any) => ({
        artefact: i.artefact,
        days: Number(i.days ?? 10),
        configured: !!i.configured,
        updatedAt: i.updatedAt ?? null,
      })),
    });
  }

  async setValidityPolicy(artefact: string, days: number) {
    await putRaw(`/admin/validity-policy`, { artefact, days });
  }

  // ---- Investigation ordering workspace: CPT typeahead + validate + multi-line submit ------------------
  //
  // The counterpart of the prescribing trio below. What it replaces was a modal with two text inputs
  // pre-filled with a hard-coded LOINC code and the words "Complete blood count" — one line, no catalogue,
  // no checks, and a 422 the first time anyone changed the code to something real.

  /**
   * CPT typeahead, narrowed to the SECTION the tab is ordering from.
   *
   * `section` is a code-range fact resolved by masterdata (70000-79999 radiology, 80000-89999 laboratory),
   * not the stored `category` — which is the CPT taxonomy and would put a chest x-ray and a blood count in
   * the same bucket.
   */
  async searchCpt(query: string, sections: readonly CptSection[], signal?: AbortSignal) {
    const q = query.trim();
    if (q.length < 2) return [];
    // A comma-separated list, because the Labs tab is two sections. The ORDER of the 20 rows is decided
    // server-side — code-led for a query that starts with a digit, description-led otherwise — and re-sorting
    // here would throw that away, since a page of 20 is already the top 20 of a much longer ranked list.
    const r = (await getRaw(
      `/cpt-codes?section=${encodeURIComponent(sections.join(","))}&q=${encodeURIComponent(q)}&pageSize=20`,
      signal,
    )) as any;
    return ((r?.items ?? []) as any[]).map((c: any) =>
      parseOr(zCptRef, { code: String(c.code ?? ""), description: String(c.description ?? "") }),
    );
  }

  /** Step 1 — advisory. Display state only; the create path re-derives everything and reads none of it. */
  /**
   * 29.2 — the OP-Procedure kinds (design 45 §2). MASTER DATA: administered like refill frequency, so
   * adding "Hydrotherapy" is a data change rather than a release.
   *
   * <p>Inactive types are excluded by the server. A retired type must not be offerable, but an order
   * already carrying one keeps it — the row is not deleted.</p>
   */
  /**
   * 29.2 — what each code will actually create (design 45 §2).
   *
   * <p>Gate 2's stated purpose, in as many words: "so the UI can show the doctor what will happen before
   * they commit". The endpoint existed and had no caller, which is why an E/M code could be composed as a
   * procedure order and nothing anywhere raised a referral.</p>
   *
   * @param kinds Restrict to particular vehicles — the OP Procedures tab shows the two it can create.
   */
  async orderableServices(query: string, kinds?: readonly string[]) {
    const q = query.trim();
    if (q.length < 2) return [];
    const kindParam = kinds?.length ? `&kind=${encodeURIComponent(kinds.join(","))}` : "";
    const r = (await getRaw(
      `/orderable-services?q=${encodeURIComponent(q)}&pageSize=20${kindParam}`,
    )) as any;
    return ((r?.items ?? []) as any[]).map((s: any) =>
      parseOr(zOrderableService, {
        code: String(s.code ?? ""),
        description: String(s.description ?? ""),
        section: String(s.section ?? ""),
        vehicle: s.vehicle ?? "NotOrderable",
        orderable: Boolean(s.orderable),
        // A refusal reason the service authored in both languages. Absent is null, never an empty string
        // masquerading as an explanation.
        reason: s.reasonEn
          ? { en: String(s.reasonEn), ar: String(s.reasonAr ?? s.reasonEn) }
          : null,
      }),
    );
  }

  /**
   * 29.2 — raise a REFERRAL for an E/M code (design 45 §2, invariant 3).
   *
   * <p>The vehicle is decided by the SERVER: this call sends the CPT code and pharmacy refuses it with
   * `not-a-referral-service` if the routing map sends that code somewhere else. The composer's verdict is
   * display state, exactly as it is for the procedure type.</p>
   */
  async createReferral(req: {
    encounterId: string;
    targetSpecialty: string;
    reason?: string;
    requestedServiceCode: string;
    targetProviderId?: string | null;
  }) {
    const beneficiaryId = encounterBeneficiary.get(req.encounterId) ?? req.encounterId;
    // Keyed on the encounter, the specialty and the code — so a double-tapped "refer" is ONE referral, and
    // a genuinely different second referral in the same encounter is not swallowed as a replay.
    const idem = `ref:${req.encounterId}:${req.targetSpecialty}:${req.requestedServiceCode}`;
    const r = (await postRaw(`/referrals`, {
      beneficiaryId,
      encounterId: req.encounterId,
      targetSpecialty: req.targetSpecialty,
      targetProviderId: req.targetProviderId ?? null,
      reason: req.reason ?? null,
      requestedServiceCode: req.requestedServiceCode,
      // Named rather than assumed. A bare code with no system is what becomes ambiguous the first time a
      // second coding system arrives.
      requestedServiceCodeSystem: "CPT",
    }, idem)) as any;
    return parseOr(zReferralCreated, {
      referralId: String(required(r?.referralId, "referral.referralId")),
      referralNo: String(r?.referralNo ?? ""),
      status: String(r?.status ?? ""),
      requestedServiceCode: r?.requestedServiceCode ?? null,
    });
  }

  async procedureTypes() {
    const r = (await getRaw(`/procedure-types`)) as any[];
    return ((r ?? []) as any[]).map((p: any) =>
      parseOr(zProcedureType, {
        code: String(p.code ?? ""),
        // The service authors both languages for this vocabulary, so neither side is a copy of the other.
        name: { en: String(p.nameEn ?? p.code ?? ""), ar: String(p.nameAr ?? p.nameEn ?? "") },
        isSessionBased: Boolean(p.isSessionBased),
        defaultSessions: typeof p.defaultSessions === "number" ? p.defaultSessions : null,
        maxSessions: typeof p.maxSessions === "number" ? p.maxSessions : null,
        allowedCptScopes: Array.isArray(p.allowedCptScopes) ? p.allowedCptScopes.map(String) : [],
      }),
    );
  }

  async validateInvestigationOrder(req: {
    encounterId: string;
    orderType: InvestigationOrderType;
    lines: InvestigationDraftLine[];
    diagnosisIcdCodes: string[];
  }) {
    const beneficiaryId = encounterBeneficiary.get(req.encounterId) ?? req.encounterId;
    const r = (await postRaw(`/investigation-orders/validate`, {
      beneficiaryId,
      encounterId: req.encounterId,
      orderType: req.orderType,
      diagnosisIcdCodes: req.diagnosisIcdCodes,
      lines: req.lines.map((l) => ({
        lineId: l.lineId, code: l.test?.code ?? null, description: l.test?.description ?? null, quantity: l.quantity,
      })),
    })) as any;
    return parseOr(zOrderValidationResult, {
      validationId: r?.validationId ?? crypto.randomUUID(),
      overallState: r?.overallState ?? "NotChecked",
      findings: ((r?.findings ?? []) as any[]).map((f: any) => ({
        lineId: f.lineId,
        kind: f.kind,
        state: f.state,
        // The service sends both languages per finding; neither is machine-translated at render.
        message: { en: String(f.messageEn ?? ""), ar: String(f.messageAr ?? f.messageEn ?? "") },
        requiresAcknowledgement: !!f.requiresAcknowledgement,
        isBlocking: !!f.isBlocking,
        sourceName: f.sourceName ?? null,
        caveat: f.caveat ?? null,
      })),
      lineStates: r?.lineStates ?? {},
    });
  }

  /** Step 2 — one order, every line, one Idempotency-Key. */
  async submitInvestigationOrder(req: {
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
    const beneficiaryId = encounterBeneficiary.get(req.encounterId) ?? req.encounterId;
    // Keyed on the composed CONTENT, so a double-click is one order while a genuinely different second
    // order for the same encounter is not silently swallowed as a replay.
    const idem = `ord:${req.encounterId}:${req.orderType}:${req.lines.map((l) => `${l.test?.code}x${l.quantity}`).join(",")}`;
    const r = (await postRaw(`/investigation-orders`, {
      beneficiaryId,
      encounterId: req.encounterId,
      orderType: req.orderType,
      // 31.1 — the COURSE, at the level it is decided. One kind, one session count, for the whole order.
      procedureTypeCode: req.procedureTypeCode ?? null,
      sessions: req.sessions ?? null,
      lines: req.lines.map((l) => ({
        codeSystem: "CPT",
        code: l.test?.code ?? "",
        description: [l.test?.description, l.note.trim()].filter(Boolean).join(" — "),
        // 31.1 — the line's quantity is now PER SESSION. The server derives the metered total
        // (sessions x this), so `quantity_ordered` keeps its meaning and consume, partial approval and the
        // delivering centre's queue are all untouched.
        quantityPerSession: l.quantity,
        // Sent for a pre-31.1 server, which reads the line-level figure as the whole quantity. Harmless on
        // a 31.1 server, which prefers `quantityPerSession`.
        quantityOrdered: l.quantity,
      })),
    }, idem)) as any;
    return parseOr(zInvestigationOrderResult, {
      orderId: required(r?.orderId, "order.orderId"),
      orderNo: String(r?.orderNo ?? ""),
      status: String(r?.status ?? ""),
      requiresApproval: String(r?.status ?? "").toLowerCase() === "pendingapproval",
    });
  }

  // Write an e-prescription (US-033). Keyed on beneficiary (from the encounter cache) + Idempotency-Key. The
  // prescription line references a master-data drug id; the coded drug's `code` carries that reference.
  async prescribe(req: PrescribeRequest) {
    const beneficiaryId = encounterBeneficiary.get(req.encounterId) ?? req.encounterId;
    const idem = `rx:${req.encounterId}:${req.drug.code}`;
    const body = {
      beneficiaryId,
      encounterId: req.encounterId,
      lines: [{
        drugId: req.drug.code,
        dose: req.dose,
        route: "Oral",
        frequency: "Daily",
        quantityPrescribed: req.quantity,
        refillsAllowed: 0,
      }],
    };
    const r = (await postRaw(`/prescriptions`, body, idem)) as any;
    return parseOr(zPrescribeResult, {
      prescriptionId: r?.prescriptionId ?? r?.PrescriptionId ?? "",
      status: rxStatus(r?.status),
    });
  }

  // Clinician worklists (Phase 4, US-032/033) — "my orders" / "my prescriptions" are scoped server-side by
  // CreatedBy == subject, so no beneficiary is leaked cross-clinician. We flatten each to a min-necessary row
  // (masked beneficiary token + first-line code + status). The results inbox is just ordersMine("Completed").
  async ordersMine(status?: string) {
    const r = (await getRaw(`/investigation-orders/mine${status ? `?status=${encodeURIComponent(status)}` : ""}`)) as any[];
    return (r ?? []).map((o: any) => {
      const lines: any[] = o.lines ?? [];
      return parseOr(zOrderRow, {
        id: o.orderId,
        orderNo: o.orderNo ?? "",
        beneficiary: { id: o.beneficiaryId, token: caseToken({ beneficiaryId: o.beneficiaryId }) },
        orderType: String(o.orderType ?? ""),
        primaryCode: lines[0]?.code ?? "—",
        lineCount: lines.length,
        status: orderStatus(o.status),
        requestedAt: o.requestedAt ?? new Date().toISOString(),
        firstLineId: lines[0]?.orderLineId ?? lines[0]?.lineId ?? undefined,
        expiresAt: o.expiresAt ?? null,
        // Already on the wire (`OrderResponse.EncounterId`) and previously discarded. It is the key the care
        // timeline is read by, so an order can show what has happened to it.
        encounterId: o.encounterId ?? null,
        // Carried through rather than reduced to a count. The server has always sent them; keeping only
        // `lines[0].code` is what left the worklist able to say an order had four tests and not which four.
        lines: lines.map((l: any) => ({
          id: l.orderLineId ?? l.lineId,
          code: String(l.code ?? "—"),
          codeSystem: String(l.codeSystem ?? ""),
          // Null, not "": an undescribed code is a stated absence, and an empty string renders as a blank
          // cell that reads like a fault.
          description: l.description ?? null,
          quantityOrdered: Number(l.quantityOrdered ?? 1),
          quantityConsumed: Number(l.quantityConsumed ?? 0),
          status: orderStatus(l.status),
        })),
      });
    });
  }

  /** 14.6/14.7 — read one result. The orders service applies the sensitivity gate and returns either the value
   *  or `{ restricted: true, … }` existence-only metadata; the discriminated union parses both. */
  async resultDetail(orderId: string, lineId: string) {
    const r = (await getRaw(`/investigation-orders/${orderId}/lines/${lineId}/result`)) as any;
    if (r?.restricted === true)
      return parseOr(zResultDetail, {
        restricted: true, orderId, lineId,
        category: r.category ?? r.orderType ?? "Result",
        status: r.status ?? "Completed",
        sensitivityLevel: r.sensitivityLevel ?? "Sensitive",
        orderingBranch: r.orderingBranch ?? null,
        date: r.date ?? r.resultUploadedAt ?? undefined,
      });
    return parseOr(zResultDetail, {
      restricted: false, orderId, lineId,
      category: r?.category ?? r?.orderType ?? "Result",
      code: r?.code ?? "—",
      value: r?.resultValue ?? r?.value ?? "—",
      status: r?.status ?? "Completed",
      resultedAt: r?.resultUploadedAt ?? r?.resultedAt ?? undefined,
    });
  }

  /** 14.8 — request time-boxed access to a restricted result (POST /report-access-requests). */
  async requestReportAccess(input: ReportAccessInput) {
    const r = (await postRaw(`/report-access-requests`, {
      orderId: input.orderId, orderLineId: input.lineId, purposeCode: input.purposeCode,
      justification: input.justification, requestedTtlHours: input.requestedTtlHours,
    })) as any;
    return parseOr(zReportAccessRequestResult, { requestId: r?.requestId ?? r?.id ?? "unknown", status: r?.status ?? "Pending" });
  }
  /**
   * 18.C2 (audit R2 W4) — the approver inbox. Clinical-free by construction: the row carries who asked, for
   * which line and why, and a MASKED beneficiary token. An approver decides whether the requester may see the
   * result; showing them the result to make that decision would disclose the thing being gated.
   */
  async reportAccessInbox() {
    const r = (await getRaw(`/report-access-requests`)) as any[];
    return (Array.isArray(r) ? r : []).map((q: any) =>
      parseOr(zReportAccessRequestRow, {
        requestId: q.requestId,
        orderId: q.orderId,
        orderLineId: q.orderLineId,
        beneficiaryToken: `•••${String(q.beneficiaryId ?? "").replace(/-/g, "").slice(-4)}`,
        requestedBy: String(q.requestedBy ?? ""),
        requestedForRole: q.requestedForRole ?? undefined,
        purposeCode: String(q.purposeCode ?? ""),
        justification: String(q.justification ?? ""),
        requestedTtlHours: typeof q.requestedTtlHours === "number" ? q.requestedTtlHours : undefined,
        // 32.4 — every state gets its own chip. This was a two-branch conditional, so an InfoRequested
        // request rendered as "Awaiting decision": the queue told the decider it was waiting on THEM while
        // it was in fact waiting on the requester, and told the requester nothing at all because the row
        // was not in any list they could see.
        status: REPORT_ACCESS_CHIP[String(q.status)] ?? { kind: "warn" as const, label: t(String(q.status), String(q.status)) },
        statusCode: q.status,
        canDecide: Boolean(q.canDecide),
        isRequester: Boolean(q.isRequester),
        createdAt: q.createdAt ?? new Date().toISOString(),
      }),
    );
  }

  async takeReportAccessUnderReview(requestId: string) {
    await postRaw(`/report-access-requests/${encodeURIComponent(requestId)}/review`, {});
  }

  /**
   * Answer the question a reviewer asked (32.4).
   *
   * <p>The supplement is APPENDED server-side; the original justification is never overwritten. This is the
   * only exit from InfoRequested, and until now no client called it — so "Ask for more" was a one-way door
   * the product itself offered.</p>
   */
  async supplyReportAccessInfo(requestId: string, supplement: string) {
    await postRaw(`/report-access-requests/${encodeURIComponent(requestId)}/supply-info`, { supplement });
  }

  async decideReportAccess(requestId: string, decision: "approve" | "deny" | "requestinfo", reason: string, ttlHours?: number) {
    await postRaw(`/report-access-requests/${encodeURIComponent(requestId)}/decision`, { decision, reason, ttlHours });
  }

  async revokeReportAccessGrant(grantId: string) {
    await postRaw(`/report-access-grants/${encodeURIComponent(grantId)}/revoke`, {});
  }

  async prescriptionsMine(status?: string) {
    const r = (await getRaw(`/prescriptions/mine${status ? `?status=${encodeURIComponent(status)}` : ""}`)) as any[];
    return (r ?? []).map((p: any) =>
      parseOr(zRxRow, {
        id: p.prescriptionId,
        rxNo: p.rxNo,
        beneficiary: { id: p.beneficiaryId, token: caseToken({ beneficiaryId: p.beneficiaryId }) },
        lineCount: (p.lines ?? []).length,
        // 32.6 — Acute vs Chronic, which decides which amendment control a row may offer at all.
        kind: p.kind ?? null,
        refillFrequencyCode: p.refillFrequencyCode ?? null,
        durationDays: typeof p.durationDays === "number" ? p.durationDays : null,
        // The authorization decides whether this reads "Approved" or "Verified" — see `rxStatus`.
        status: rxStatus(p.status, p.authorizationId),
        submittedAt: p.submittedAt ?? undefined,
        expiresAt: p.expiresAt ?? undefined,
        // Already on the wire (`PrescriptionResponse.EncounterId`) and previously discarded. It is the key
        // the care timeline is read by, so a prescription can show what has happened to it.
        encounterId: p.encounterId ?? null,
        // `neutral()`, not a translation — a person's name is not translated. Null stays null all the way to
        // the screen, which words the absence itself; the mapper does not get to decide how it reads.
        prescriber: p.prescriberName ? neutral(p.prescriberName) : null,
        // Carried through from the SAME response rather than fetched when the reader opens one. The lines
        // were always in this payload and were being thrown away at `lineCount`.
        lines: ((p.lines ?? []) as any[]).map((l: any) => ({
          id: l.prescriptionLineId,
          // 29.4 — the catalogue product, so the service-history modal can be opened on this medicine.
          drugId: l.drugId ?? null,
          drug: l.drugName ? neutral(l.drugName) : null,
          dose: l.dose ?? null,
          route: l.route ?? null,
          frequency: l.frequency ?? null,
          quantityPrescribed: Number(l.quantityPrescribed ?? 0),
          quantityDispensed: Number(l.quantityDispensed ?? 0),
          // 31.3 — read strictly. An absent unit renders as no unit; it is never filled in with a plausible
          // one, because "1" meaning one box and "1" meaning one tablet are the same character.
          quantityUnit: typeof l.quantityUnit === "string" && l.quantityUnit.length > 0
            ? l.quantityUnit
            : null,
          // 31.5 — read strictly. A line written before the numbers were kept carries none, and 0 or 1 in
          // their place would be a dose and a frequency nobody wrote.
          doseAmount: typeof l.doseAmount === "number" ? l.doseAmount : null,
          timesPerDay: typeof l.timesPerDay === "number" ? l.timesPerDay : null,
          durationDays: typeof l.durationDays === "number" ? l.durationDays : null,
          refillsAllowed: Math.trunc(Number(l.refillsAllowed ?? 0)),
          status: rxStatus(l.status),
        })),
      }),
    );
  }

  // Vitals capture (Phase 4, US-030) — one POST /encounters/{id}/vitals per reading (treating-gated: the nurse
  // owns the encounter). emr accepts enum NAMES (JsonStringEnumConverter), so we send the readable vitalType.
  async recordVitals(encounterId: string, readings: VitalInput[]) {
    let recorded = 0;
    for (const r of readings) {
      await postRaw(`/encounters/${encodeURIComponent(encounterId)}/vitals`, { vitalType: r.type, valueNum: r.value });
      recorded += 1;
    }
    return parseOr(zVitalsResult, { encounterId, recorded });
  }

  // ---- Standing clinical facts: blood group + allergies (emr migrations 0020/0021) --------------------
  //
  // These live on the MEMBER, not on the visit, so every path here is /beneficiaries/{id}/… . emr gates each
  // one on a treating relationship and audits it as a PHI read or a clinical write; nothing below re-checks
  // that, because a client-side permission check is a display hint and never a control.

  async memberClinicalRecord(beneficiaryId: string) {
    const r = (await getRaw(`/beneficiaries/${encodeURIComponent(beneficiaryId)}/clinical-record`)) as any;
    return parseOr(zMemberClinicalRecord, {
      beneficiaryId: r?.beneficiaryId ?? beneficiaryId,
      bloodGroup: r?.bloodGroup ?? null,
      bloodGroupRecordedAt: r?.bloodGroupRecordedAt ?? null,
      allergies: (r?.allergies ?? []).map(toAllergyRecord),
    });
  }

  async allergenCatalogue() {
    const r = (await getRaw(`/allergens`)) as any[];
    return (r ?? []).map((a: any) =>
      parseOr(zAllergenOption, {
        allergenId: a.allergenId,
        code: String(a.code ?? ""),
        name: String(a.name ?? ""),
        // masterdata-service is the one service that never registered a JsonStringEnumConverter, so its
        // enums come over as ORDINALS. Mapped by position against Domain/Entities.cs AllergenCategory
        // rather than left as a number, because "category: 2" is not something a UI can group by.
        category: ALLERGEN_CATEGORY[Number(a.category)] ?? "Drug",
      }),
    );
  }

  /**
   * What the patient is already taking (32.2).
   *
   * <p>This read exists because the prescribing interaction check needs it, not only because a screen wants
   * to show it. Until 32.1 the check compared a prescription against nothing at all and reported "no
   * interaction found" — so an empty answer here is rendered as "nothing recorded", never as "takes
   * nothing", and the two sentences are not interchangeable.</p>
   */
  /**
   * Append a correction to a signed note (32.3).
   *
   * <p>The only way to change a signed clinical record on this platform — emr refuses an edit with a 409
   * naming this path, the workspace tells the doctor so twice, and until now no client could take it.</p>
   */
  async addNoteAddendum(
    encounterId: string,
    noteId: string,
    soap: { subjective?: string; objective?: string; assessment?: string; plan?: string },
  ) {
    const r = (await postRaw(
      `/encounters/${encodeURIComponent(encounterId)}/notes/${encodeURIComponent(noteId)}/addendum`,
      {
        noteType: "SOAP",
        subjective: soap.subjective ?? null,
        objective: soap.objective ?? null,
        assessment: soap.assessment ?? null,
        plan: soap.plan ?? null,
      },
    )) as any;
    return parseOr(zNoteAddendum, {
      id: r?.noteId,
      authoredAt: r?.authoredAt,
      authoredByName: r?.authoredByName ?? null,
      soap: {
        subjective: r?.subjective ?? "",
        objective: r?.objective ?? "",
        assessment: r?.assessment ?? "",
        plan: r?.plan ?? "",
      },
    });
  }

  /**
   * 32.5 — notes on an order or prescription line (design 46 §7b).
   *
   * <p>One method, three paths, because one PANEL serves all four order kinds. The alternative — a method
   * per kind — is how a platform ends up with two behaviours for "cancel a note", which is the outcome doc
   * 46 §7b names when it insists on a single mechanism.</p>
   *
   * <p>A cancelled note still comes back. The screen strikes it through; dropping it would turn "withdrawn
   * by X because Z" into a gap.</p>
   */
  /**
   * 32.6 — what a chronic amendment would do, before it does it (design 46 §10).
   *
   * <p>Computed by the SERVER, by the same pure function the write path calls. Re-deriving largest-remainder
   * here would fork the one piece of arithmetic `zChronicPreview` forbids forking — the copies drift, and
   * the drift appears as a doctor shown a schedule the pharmacy never honours.</p>
   */
  async previewChronicAmendment(
    rxId: string, lineId: string, req: { durationDays: number; frequencyMonths: number; convertToAcute?: boolean },
  ) {
    const r = (await postRaw(
      `/prescriptions/${encodeURIComponent(rxId)}/lines/${encodeURIComponent(lineId)}/amend-schedule/preview`,
      { durationDays: req.durationDays, frequencyMonths: req.frequencyMonths, convertToAcute: req.convertToAcute ?? false },
    )) as any;
    return parseOr(zChronicAmendPreview, {
      outcome: r?.outcome,
      newTotal: Number(r?.newTotal ?? 0),
      alreadyDispensed: Number(r?.alreadyDispensed ?? 0),
      remainingWindows: (r?.remainingWindows ?? []).map(Number),
      unit: String(r?.unit ?? ""),
      missingField: r?.missingField ?? null,
    });
  }

  /** 32.6 — apply it. Idempotency-Key per user action, not per retry. */
  async amendChronicSchedule(
    rxId: string, lineId: string,
    req: { durationDays: number; frequencyMonths: number; reasonCode: string; reasonText?: string; convertToAcute?: boolean },
  ) {
    await postRaw(
      `/prescriptions/${encodeURIComponent(rxId)}/lines/${encodeURIComponent(lineId)}/amend-schedule`,
      {
        durationDays: req.durationDays, frequencyMonths: req.frequencyMonths,
        reasonCode: req.reasonCode, reasonText: req.reasonText ?? null,
        convertToAcute: req.convertToAcute ?? false,
      },
      crypto.randomUUID(),
    );
  }

  /**
   * 32.6 — withdraw every still-cancellable line of a prescription.
   *
   * <p>The medication twin of `withdrawOrder`, and it answers the same way: 200, 207 or 409 by how much
   * succeeded, with a per-line refusal. All three are read identically, because "three of five withdrawn" is
   * an answer the prescriber has to see rather than an error to swallow.</p>
   */
  async cancelPrescriptionLines(rxId: string, reasonCode: string, reasonText?: string): Promise<WithdrawResult> {
    const r = (await postRaw(
      `/prescriptions/${encodeURIComponent(rxId)}/cancel-lines`,
      { reasonCode, reasonText },
      crypto.randomUUID(),
    )) as { cancelled?: number; lines?: { drugName?: string | null; cancelled: boolean; refusal?: string | null }[] };

    const lines = (r.lines ?? []).map((l) => ({
      label: l.drugName ?? "—",
      withdrawn: l.cancelled,
      refusal: l.refusal ?? null,
    }));
    // `total` is the count the prescriber ASKED about, not the count that succeeded — "3 of 5 withdrawn"
    // needs both numbers, and deriving the denominator from the successes would always read "5 of 5".
    return { withdrawn: r.cancelled ?? lines.filter((l) => l.withdrawn).length, total: lines.length, lines };
  }

  async lineNotes(kind: LineNoteKind, orderId: string, lineId: string) {
    const r = (await getRaw(`${lineNoteBase(kind, orderId)}/lines/${encodeURIComponent(lineId)}/notes`)) as any[];
    return (r ?? []).map((n) => parseOr(zLineNote, toLineNote(n, lineId)));
  }

  async writeLineNote(
    kind: LineNoteKind, orderId: string, lineId: string, body: string, visibility?: NoteVisibility,
  ) {
    const r = (await postRaw(
      `${lineNoteBase(kind, orderId)}/lines/${encodeURIComponent(lineId)}/notes`,
      { body, visibility: visibility ?? null },
    )) as any;
    return parseOr(zLineNote, toLineNote(r, lineId));
  }

  async cancelLineNote(kind: LineNoteKind, noteId: string, reason: string) {
    await postRaw(`${lineNoteBase(kind, "")}/notes/${encodeURIComponent(noteId)}/cancel`, { reason });
  }

  async medicationHistory(beneficiaryId: string, status?: MedicationStatus) {
    const q = status ? `?status=${encodeURIComponent(status)}` : "";
    const r = (await getRaw(
      `/beneficiaries/${encodeURIComponent(beneficiaryId)}/medication-history${q}`,
    )) as any[];
    return (r ?? []).map((m) => parseOr(zMedicationHistoryRow, toMedicationRow(m)));
  }

  async addMedicationHistory(beneficiaryId: string, req: AddMedicationHistoryRequest) {
    const r = (await postRaw(`/beneficiaries/${encodeURIComponent(beneficiaryId)}/medication-history`, {
      drugId: req.drugId,
      source: req.source,
      startDate: req.startDate ?? null,
      endDate: req.endDate ?? null,
      status: req.status,
    })) as any;
    return parseOr(zMedicationHistoryRow, toMedicationRow(r));
  }

  /**
   * The patient stopped taking it.
   *
   * <p>Not a delete: what someone WAS taking is part of the clinical picture. The row keeps its place and
   * gains an end date, and it leaves the active list the interaction check reads.</p>
   */
  async stopMedication(beneficiaryId: string, medHistoryId: string, endDate?: string) {
    const r = (await postRaw(
      `/beneficiaries/${encodeURIComponent(beneficiaryId)}/medication-history/${encodeURIComponent(medHistoryId)}/stop`,
      { endDate: endDate ?? null },
    )) as any;
    return parseOr(zMedicationHistoryRow, toMedicationRow(r));
  }

  async addAllergy(beneficiaryId: string, req: AddAllergyRequest) {
    const r = (await postRaw(`/beneficiaries/${encodeURIComponent(beneficiaryId)}/allergies`, {
      allergenId: req.allergenId,
      // Empty string and "no reaction recorded" are different claims; only the second is true of a blank box.
      reaction: req.reaction?.trim() ? req.reaction.trim() : null,
      severity: req.severity,
      status: req.status,
    })) as any;
    return parseOr(zAllergyRecord, toAllergyRecord(r));
  }

  async setBloodGroup(beneficiaryId: string, bloodGroup: BloodGroup) {
    await putRaw(`/beneficiaries/${encodeURIComponent(beneficiaryId)}/blood-group`, { bloodGroup });
  }

  // Lab / Radiology (Phase 5, US-040) — the orders service exposes ONE capability-filtered provider queue at
  // /investigation-orders/queue (a lab_tech sees Lab orders, a radiology_tech Radiology — by role, not URL). We
  // flatten each order to one row using its first available line as the `test`, cache that line id so consume
  // can target it, and default priority to routine (the fulfillment queue does not carry a clinical priority).
  /**
   * Find a patient's investigation orders (27.8).
   *
   * <p>Search-first, exactly as the dispensing counter is. The bench's real question is "what do I have for
   * THIS patient", and browsing the tenant's queue to reach one order puts other patients' orders on screen
   * to get there.</p>
   *
   * <p>An ORDER NUMBER identifies the order on its own — it is the reference on the paper in the patient's
   * hand. A CARD NUMBER does not identify a person, so it takes a second identifier alongside it; the server
   * enforces that and the screen explains it.</p>
   */
  async labSearch(kind: "lab" | "radiology", by: { orderNo?: string; cardNumber?: string; memberNo?: string; passport?: string }) {
    const q = Object.entries(by)
      .filter(([, v]) => (v ?? "").trim() !== "")
      .map(([k, v]) => `${k}=${encodeURIComponent(String(v).trim())}`)
      .join("&");
    const r = (await getRaw(`/investigation-orders/search?${q}`)) as any[];
    return (r ?? [])
      .filter((o: any) => String(o.orderType ?? "").toLowerCase() === kind)
      .map((o: any) => this.toLabOrder(o, kind));
  }

  // ---- 29.2b — external delivering provider (design 45 §2b) --------------------------------------------
  //
  // NO CLIENT-SIDE OWNERSHIP FILTER anywhere in this block, deliberately. The queue is scoped by
  // `assigned_provider_id` server-side and proved by the two-provider test in orders; a `.filter()` here would
  // read like defence and would in fact be the opposite — it would make a server that started returning other
  // centres' rows look correct in the UI, which is precisely how audit R3's network-wide pharmacy queue went
  // unnoticed.

  async procedureQueue() {
    const r = (await getRaw("/procedure-orders/queue?page=1&pageSize=50")) as unknown[];
    return (r ?? []).map((o) => parseOr(zProcedureQueueItem, o));
  }

  async procedureCounterSearch(by: { cardNumber?: string; memberNo?: string; passport?: string }) {
    const qs = new URLSearchParams();
    if (by.cardNumber?.trim()) qs.set("cardNumber", by.cardNumber.trim());
    if (by.memberNo?.trim()) qs.set("memberNo", by.memberNo.trim());
    if (by.passport?.trim()) qs.set("passport", by.passport.trim());
    const r = (await getRaw(`/procedure-orders/search?${qs}`)) as unknown[];
    return (r ?? []).map((o) => parseOr(zProcedureQueueItem, o));
  }

  async recordProcedureSession(
    orderId: string, orderLineId: string, idempotencyKey: string,
    by: { practitioner?: string; attended?: boolean; note?: string },
  ) {
    // The key is passed through, never generated here: generating one per call would make every retry a new
    // session, which is the exact opposite of the guarantee it exists to provide.
    const r = await postRaw(
      `/procedure-orders/${encodeURIComponent(orderId)}/sessions`,
      { orderLineId, deliveringPractitioner: by.practitioner ?? null, attended: by.attended ?? true, note: by.note ?? null },
      idempotencyKey,
    );
    return parseOr(zSessionProgress, r);
  }

  async reportProcedureCompletion(orderId: string, findings: string) {
    await postRaw(`/procedure-orders/${encodeURIComponent(orderId)}/report`, { findings });
  }

  async serviceHistory(
    beneficiaryId: string,
    q: { serviceType?: string; code: string; page?: number; pageSize?: number },
  ) {
    const qs = new URLSearchParams({ code: q.code });
    if (q.serviceType) qs.set("serviceType", q.serviceType);
    if (q.page) qs.set("page", String(q.page));
    if (q.pageSize) qs.set("pageSize", String(q.pageSize));
    // NOT wrapped in a try/catch that returns an empty list. A failed load must reach the caller as an error,
    // because the modal renders "could not load" and "no previous occurrences" as different sentences and a
    // swallowed failure would collapse them into the reassuring one.
    const r = await getRaw(`/patients/${encodeURIComponent(beneficiaryId)}/service-history?${qs}`);
    return parseOr(zServiceHistory, r);
  }

  async labQueue(kind: "lab" | "radiology") {
    const r = (await getRaw(`/investigation-orders/queue?page=1&pageSize=50`)) as any[];
    return (r ?? [])
      .filter((o: any) => String(o.orderType ?? "").toLowerCase() === kind)
      .map((o: any) => this.toLabOrder(o, kind));
  }

  /** One queue/search row → the contract shape. Shared so the two reads cannot describe an order differently. */
  private toLabOrder(o: any, kind: "lab" | "radiology") {
    {
      {
        const lines: any[] = o.lines ?? [];
        const line = lines[0] ?? {};
        if (line.orderLineId) orderLineByOrderId.set(String(o.orderId), String(line.orderLineId));
        const remaining = lines.reduce((acc: number, l: any) => acc + Math.max(0, Math.round(Number(l.quantityRemaining ?? 1))), 0);
        return parseOr(zLabOrder, {
          id: o.orderId,
          kind,
          test: { system: codeSystem(line.codeSystem), code: line.code ?? "—", label: neutral(line.description ?? line.code ?? "") },
          patient: { id: o.beneficiaryId, token: caseToken({ beneficiaryId: o.beneficiaryId }) },
          priority: "routine",
          // Same rule as the dispensing counter: the service computes `expired` against the clock, and the
          // displayed state follows the FLAG, not the status the sweeper has yet to catch up with.
          status: o.expired
            ? { kind: "bad" as const, label: t("Expired", "منتهي") }
            : orderStatus(o.status),
          placedAt: o.requestedAt ?? new Date().toISOString(),
          panelsTotal: Math.max(1, remaining),
          panelsDone: 0,
          orderNo: String(o.orderNo ?? ""),
          expiresAt: o.expiresAt ?? null,
          expired: !!o.expired,
        });
      }
    }
  }
  /**
   * One order with every line on it (ADR-0034).
   *
   * <p>Found by ORDER NUMBER, because that is the reference printed on the paper in the patient's hand and
   * the one a technician can read back. It searches the fulfilment queue the caller is already entitled to,
   * so this discloses nothing the queue does not — it just stops collapsing the order to its first line.</p>
   */
  async investigationOrder(orderNo: string) {
    const rows = (await getRaw(`/investigation-orders/search?orderNo=${encodeURIComponent(orderNo)}`)) as any[];
    const o = (rows ?? []).find((x: any) => String(x.orderNo ?? "") === orderNo) ?? (rows ?? [])[0];
    if (!o) return null;

    // 29.1 — READS accept BOTH spellings. Belt and braces, not a live dependency: orders 0009 rewrote every
    // stored `Imaging` to `Radiology` in place and asserts none remain, so nothing on the wire should carry
    // the old value today. It stays because narrowing it would silently reclassify any that did as a LAB
    // order, sending it to a bench that cannot perform it — a failure with no error attached.
    //
    // 32.6 — this comment used to claim pre-switch orders keep `Imaging` "for the life of the order", which
    // the backfill contradicts. Left uncorrected it invites the opposite mistake: a reader adding a
    // dual-accept filter elsewhere to fix a problem that does not exist.
    const rawType = String(o.orderType ?? "").toLowerCase();
    const kind = rawType === "radiology" || rawType === "imaging" ? "radiology" : "lab";
    return parseOr(zInvestigationOrder, {
      id: o.orderId,
      orderNo: String(o.orderNo ?? orderNo),
      kind,
      patient: { id: o.beneficiaryId, token: caseToken({ beneficiaryId: o.beneficiaryId }) },
      // The FLAG, not the status — the expiry sweeper runs hourly, so a lapsed order still reads Active
      // between lapsing and being swept, and consume would refuse what the page had offered.
      status: o.expired ? { kind: "bad" as const, label: t("Expired", "منتهي") } : orderStatus(o.status),
      placedAt: o.requestedAt ?? new Date().toISOString(),
      expiresAt: o.expiresAt ?? null,
      expired: !!o.expired,
      lines: (o.lines ?? []).map((l: any) => {
        const ordered = Number(l.quantityOrdered ?? l.quantity ?? 1);
        const remaining = Number(l.quantityRemaining ?? ordered);
        return {
          id: l.orderLineId,
          test: { system: codeSystem(l.codeSystem), code: l.code ?? "—", label: neutral(l.description ?? l.code ?? "") },
          quantityOrdered: ordered,
          // Derived, because the queue projection reports what is LEFT rather than what is done. Clamped at
          // zero: a negative "consumed" on screen is worse than an approximate one.
          quantityConsumed: Math.max(0, ordered - remaining),
          status: orderStatus(l.status),
        };
      }),
    });
  }

  async orderPricing(orderId: string, performNow?: Record<string, number>) {
    return parseOr(
      zOrderPricing,
      await getRaw(
        `/investigation-orders/${encodeURIComponent(orderId)}/pricing${basisQuery("perform", performNow)}`,
      ),
    );
  }

  async requestSubstitution(req: SubstitutionRequest, idempotencyKey?: string) {
    const r = (await postRaw(
      `/authorizations/substitution-requests`,
      {
        orderId: req.orderId,
        orderLineId: req.orderLineId,
        orderReference: req.orderReference,
        beneficiaryId: req.beneficiaryId,
        orderedCode: req.orderedCode,
        orderedLabel: req.orderedLabel ?? null,
        proposedCode: req.proposedCode ?? null,
        reason: req.reason,
      },
      idempotencyKey ?? crypto.randomUUID(),
    )) as any;
    return { authNo: String(r?.authNo ?? "") };
  }

  // Result upload (Phase 5.3, US-042) — the "awaiting result" worklist is the provider's consumed-but-unreported
  // lines; a result posts as multipart form (resultValue and/or a report file — this screen sends the value).
  async awaitingResult(kind: "lab" | "radiology") {
    const r = (await getRaw(`/investigation-orders/awaiting-result`)) as any[];
    return (r ?? [])
      .filter((x: any) => String(x.orderType ?? "").toLowerCase() === kind)
      .map((x: any) =>
        parseOr(zResultTask, {
          orderId: x.orderId,
          lineId: x.lineId,
          orderNo: x.orderNo ?? "",
          orderType: String(x.orderType ?? ""),
          beneficiary: { id: x.beneficiaryId, token: caseToken({ beneficiaryId: x.beneficiaryId }) },
          code: x.code ?? "—",
          description: x.description ?? undefined,
          consumedAt: x.consumedAt ?? new Date().toISOString(),
        }),
      );
  }
  async uploadResult(
    orderId: string, lineId: string,
    result: { value?: string; report?: File },
    idempotencyKey?: string,
  ) {
    // Only the parts that were actually given. Sending `resultValue: ""` alongside a file would overwrite a
    // summary a previous upload had recorded — the server writes the value only when it is non-blank, and a
    // client that always sends the field is relying on that rather than saying what it means.
    const fields: Record<string, string | Blob> = {};
    if (result.value?.trim()) fields.resultValue = result.value.trim();
    // `report` is the field name the service reads (Results.cs). The filename rides along with the Blob.
    if (result.report) fields.report = result.report;
    await postForm(`/investigation-orders/${encodeURIComponent(orderId)}/lines/${encodeURIComponent(lineId)}/result`, fields, idempotencyKey);
    return parseOr(zResultUpload, { orderId, lineId, uploaded: true });
  }
  async consume(req: ConsumeRequest) {
    // Per-line when the caller named lines (the order page), else the queue's cached first line. A page that
    // shows three panels separately must be able to consume the two that were actually performed.
    const orderLineId = orderLineByOrderId.get(String(req.orderId));
    const body = {
      lines: req.lines?.length
        ? req.lines.map((l) => ({ orderLineId: l.lineId, quantity: l.quantity }))
        : orderLineId ? [{ orderLineId, quantity: req.panels }] : [],
    };
    const r = (await postRaw(`/investigation-orders/${encodeURIComponent(req.orderId)}/consume`, body, req.idempotencyKey)) as any;
    const lines: any[] = r?.lines ?? [];
    const total = lines.reduce((acc, l) => acc + Math.round(Number(l.quantityOrdered ?? l.quantityRemaining ?? 1)), 0);
    const done = lines.reduce((acc, l) => acc + Math.round(Number(l.quantityConsumed ?? 0)), 0);
    return parseOr(zConsumeResult, {
      orderId: r?.orderId ?? req.orderId,
      fulfillmentId: (r?.fulfillments ?? [])[0]?.fulfillmentId ?? req.idempotencyKey,
      status: orderStatus(r?.orderStatus),
      panelsDone: done,
      panelsTotal: Math.max(1, total),
      replayed: !!r?.replayed,
    });
  }

  // Pharmacy (Phase 6, US-050) — the pharmacy service exposes a browse-all dispensable queue at
  // /prescriptions/queue (min-necessary: quantities + dose, never diagnosis). The contract's single-request
  // multi-line dispense maps to the service's per-line dispense endpoint (one atomic idempotent call per line);
  // batch/expiry are required by the service but not collected by this screen, so we supply a dev batch + a
  // one-year expiry per line. Line ids are cached from the queue so dispense can target them.
  async pharmacyQueue() {
    return this.toPrescriptions((await getRaw(`/prescriptions/queue`)) as any[]);
  }

  /**
   * The dispensing counter's lookup: one member's dispensable prescriptions.
   *
   * By Rx NUMBER on its own — the prescription's own reference identifies it — or by TWO of the member's
   * identifiers. A card number alone resolves nobody, deliberately: it is printed on something that gets
   * shared, photographed and reused, so it is a lookup key and not proof of identity (doc 43 §7 D5). The
   * server enforces that; sending one identifier answers 422 rather than a silent empty list.
   */
  async pharmacySearch(by: { rxNo?: string; cardNumber?: string; memberNo?: string; passport?: string }) {
    const q = new URLSearchParams();
    if (by.rxNo?.trim()) q.set("rxNo", by.rxNo.trim());
    if (by.cardNumber?.trim()) q.set("cardNumber", by.cardNumber.trim());
    if (by.memberNo?.trim()) q.set("memberNo", by.memberNo.trim());
    if (by.passport?.trim()) q.set("passport", by.passport.trim());
    return this.toPrescriptions((await getRaw(`/prescriptions/search?${q.toString()}`)) as any[]);
  }

  /**
   * One mapping for the queue and the search, because they return the same view.
   *
   * Everything displayed here now comes from the SERVER. It used to invent three of them: the prescriber was
   * the literal word "Prescriber", the drug fell back to the literal word "Medication", and the title used
   * the internal uuid because nothing read `rxNo`. A screen that prints the name of a field where its value
   * belongs is not a placeholder — it reads as data, and a pharmacist cannot tell it apart from one.
   */
  private toPrescriptions(rows: any[]) {
    return (rows ?? []).map((p: any) => {
      const lines: any[] = p.lines ?? [];
      rxLineIds.set(String(p.prescriptionId), lines.map((l) => String(l.prescriptionLineId)));
      return parseOr(zPrescription, {
        id: p.prescriptionId,
        rxNo: String(p.rxNo ?? ""),
        patient: { id: p.beneficiaryId, token: caseToken({ beneficiaryId: p.beneficiaryId }) },
        // `neutral()`, not a translation: a person's name is not translated, and neither is a drug's trade
        // name. Null means the row predates the snapshot (pharmacy migration 0006) — said in words rather
        // than back-filled with a uuid.
        prescriber: {
          label: p.prescriberName
            ? neutral(p.prescriberName)
            : t("Prescriber not recorded", "الطبيب الواصف غير مسجّل"),
        },
        submittedAt: p.submittedAt ?? new Date().toISOString(),
        expiresAt: p.expiresAt ?? null,
        // Straight from the service, which computes it against the clock. Not re-derived here: the sweeper
        // runs hourly, so a lapsed prescription's STATUS still reads Approved until it catches up, and a
        // screen that trusted the status would offer to dispense something the server refuses.
        expired: !!p.expired,
        diagnosisCodes: (p.diagnosisCodes ?? []).map(String),
        primaryIcdCode: p.primaryIcdCode ?? null,
        status: p.expired
          ? { kind: "bad" as const, label: t("Expired", "منتهية") }
          : rxStatus(p.status),
        lines: lines.map((l) => ({
          id: l.prescriptionLineId,
          // The FULL drug id. It was sliced to eight characters — a display shortening applied to a field
          // nothing displays and three things use as an identity: the ingredient join, the approved-
          // alternatives lookup behind the substitute control, and the drugId sent on submission. So the
          // counter reported "active ingredient not recorded" for every medicine in the catalogue, and the
          // substitute modal asked master data about a drug whose id was a prefix of a real one.
          drug: l.drugName
            ? { system: "ATC" as const, code: String(l.drugId ?? ""), label: neutral(l.drugName) }
            : drugCoded(l.drugId),
          quantity: Math.max(1, Math.round(Number(l.quantityPrescribed ?? 1))),
          dispensed: Math.round(Number(l.quantityDispensed ?? 0)),
          // 31.3 — read strictly. An absent unit shows as no unit; the one thing this screen must never do
          // is put a plausible word next to a number that means something else.
          quantityUnit: typeof l.quantityUnit === "string" && l.quantityUnit.length > 0
            ? l.quantityUnit
            : null,
          // The dose ALONE now. Route, frequency and duration travel as their own fields so the counter can
          // lay them out as the distinct facts they are; joining them into one string here left the screen
          // unable to say "duration not recorded" without parsing its own display text back apart.
          dose: String(l.dose ?? ""),
          route: l.route ?? null,
          frequency: l.frequency ?? null,
          durationDays: l.durationDays ?? null,
          // Filled by the caller's joins — the ingredient from master data, the price from the pricing
          // endpoint. Null here rather than absent, so a screen that skips the joins still parses.
          activeIngredient: null,
          unitPriceEgp: null,
          status: rxStatus(l.status),
          // READ, not asserted. This was the literal `false` — the server's `DispensableLineView` did not
          // carry the field, so the one client that talks to a real gateway could never produce anything
          // else, while the dev fixture supplied `true` on one row. The chip, the "out of stock" exclusion
          // from the fillable set, and the tests covering both were all exercising a value production could
          // not reach. Design 49 §5, pharmacy migration 0020.
          outOfStock: Boolean(l.outOfStock),
          outOfStockAt: l.outOfStockAt ?? null,
          outOfStockNote: l.outOfStockNote ?? null,
        })),
      });
    });
  }
  /**
   * Active ingredient by drug id, from master data.
   *
   * <p>A CLIENT-SIDE JOIN, deliberately — the same shape as `icdTitles` and `branchLabels`, and for the same
   * reason. The molecule a product contains is master data's fact, and teaching pharmacy-service to answer it
   * would make pharmacy a second place that says what a drug is. The browser already holds an authorised read
   * of both, so it joins them.</p>
   *
   * <p>Missing ids are simply absent from the map: 2,786 of 31,651 catalogue products record no ingredient,
   * and the caller falls back to saying so rather than repeating the trade name.</p>
   */
  async drugIngredients(drugIds: readonly string[]) {
    const ids = [...new Set(drugIds.filter(Boolean))];
    if (ids.length === 0) return new Map<string, string>();
    // NOT caught here any more. A swallowed failure returned an empty map, and an empty map is
    // indistinguishable from "the catalogue records no ingredient for these" — so a masterdata outage
    // rendered as a negative finding about every medicine on the prescription. The caller decides what to
    // show, and it can only decide if it is told the read failed.
    const r = (await postRaw(`/drugs/ingredients/by-ids`, { drugIds: ids })) as any;
    const out = new Map<string, string>();
    for (const item of (r?.items ?? []) as any[]) {
      const name = item.scientificName ?? item.activeIngredient ?? null;
      if (item.drugId && name) out.set(String(item.drugId), String(name));
    }
    return out;
  }

  // Formulary substitutions (Phase 6.3, US-052) — master data is reference-only (auth, no scope). Search drugs
  // by name, then list a drug's policy-approved alternatives (same ATC-5 substance). Bilingual name from AR
  // where master data has it, else the EN name echoed (no machine translation).
  async searchDrugs(query: string, signal?: AbortSignal) {
    const r = (await getRaw(`/drugs?q=${encodeURIComponent(query)}&pageSize=20`, signal)) as any;
    return ((r?.items ?? []) as any[]).map((d: any) =>
      parseOr(zDrugRef, {
        drugId: d.drugId,
        name: { en: String(d.name ?? ""), ar: String(d.nameAr ?? d.name ?? "") },
        atcCode: d.atcCode ?? undefined,
        form: d.form ?? undefined,
        strength: d.strength ?? undefined,
      }),
    );
  }

  // ---- Prescribing workspace (phase 26, design 43 §6) ----------------------------------------------
  //
  // One field over trade name AND active ingredient: a prescriber searches by whichever name they know.
  // The uuid is carried from here all the way to submission — `prescribe()` above sent `req.drug.code`,
  // the ATC STRING, where the API expects a Guid, so that path could never work against real data.
  async searchPrescribableDrugs(query: string, signal?: AbortSignal) {
    const r = (await getRaw(`/drugs/search?q=${encodeURIComponent(query)}&pageSize=20`, signal)) as any;
    return ((r?.items ?? []) as any[]).map((d: any) =>
      parseOr(zPrescribableDrug, {
        drugId: d.drugId,
        tradeName: {
          en: String(d.tradeName ?? ""),
          // No Arabic trade name exists in the Egyptian drug list, so the English name is echoed rather
          // than rendering an empty option. Not machine-translated.
          ar: String(d.tradeNameAr ?? d.tradeName ?? ""),
        },
        activeIngredient: d.activeIngredient ?? undefined,
        strength: d.strength ?? undefined,
        form: d.form ?? undefined,
        priceEgp: typeof d.priceEgp === "number" ? d.priceEgp : undefined,
        atcCode: d.atcCode ?? undefined,
        hasIndicationData: Boolean(d.hasIndicationData),

        // ---- 29.7 (design 45 §7) --------------------------------------------------------------------
        //
        // Rendered by DrugCombobox, DERIVED by masterdata, and never authored here. Omitting these three
        // from this list is what made the whole feature invisible against a real backend: the contract
        // declares them `.optional()`, so zod parsed the gap silently and the chips simply never rendered
        // while every fixture-driven test stayed green.
        //
        // `isLowestPrice` is read strictly — a missing field is NOT a label. A drug with no pack size has
        // no per-unit price, and falling back to the pack price is the exact comparison §7 exists to
        // prevent: a 20-tab pack at 100 EGP is dearer per tablet than a 30-tab pack at 120.
        isLowestPrice: d.isLowestPrice === true,
        pricePerUnit: typeof d.pricePerUnit === "number" ? d.pricePerUnit : undefined,
        // THREE states, and absence resolves to the explicit third one rather than to `undefined`.
        // `Unknown` is the catalogue-wide default and renders NOTHING; making that explicit here means a
        // reader can tell the default was chosen rather than left over.
        availability: d.availability === "Available" || d.availability === "Unavailable"
          ? d.availability
          : "Unknown",

        // ---- 29.6 (design 45 §6) --------------------------------------------------------------------
        //
        // The pack facts. Read STRICTLY: `isPackSplittable` absent is NOT false, because false means
        // "dispense whole packs" and absent means "the catalogue does not say" — and the second is
        // reported as NotChecked naming the field rather than rounded to a pack. Same distinction the
        // whole five-state model turns on, at the one place a wrong answer becomes a dispensing error.
        prescribingUnit: typeof d.prescribingUnit === "string" && d.prescribingUnit.length > 0
          ? d.prescribingUnit
          : null,
        // 31.3 — the SERVER's short form, never one reconstructed here. "Tablet" → "tabs" is a fact about
        // the vocabulary the drug table owns, and a second copy of it beside a dose field is a second
        // answer to what a medicine is counted in.
        prescribingUnitShort: typeof d.prescribingUnitShort === "string" && d.prescribingUnitShort.length > 0
          ? d.prescribingUnitShort
          : null,
        packSize: typeof d.packSize === "number" ? d.packSize : null,
        isPackSplittable: typeof d.isPackSplittable === "boolean" ? d.isPackSplittable : null,
      }),
    );
  }

  async validatePrescription(req: {
    encounterId: string;
    lines: PrescriptionDraftLine[];
    diagnosisIcdCodes: string[];
  }) {
    const beneficiaryId = encounterBeneficiary.get(req.encounterId) ?? req.encounterId;
    const r = (await postRaw(`/prescriptions/validate`, {
      beneficiaryId,
      encounterId: req.encounterId,
      lines: rxLines(req.lines),
      diagnosisIcdCodes: req.diagnosisIcdCodes,
    })) as any;
    return parseOr(zValidationResult, {
      validationId: required(r?.validationId, "validation.validationId"),
      ranAt: String(r?.ranAt ?? ""),
      engineVersion: String(r?.engineVersion ?? ""),
      overallState: r?.overallState ?? "NotChecked",
      findings: (r?.findings ?? []) as unknown[],
      lineStates: (r?.lineStates ?? {}) as Record<string, string>,
    });
  }

  /**
   * 29.5 — the supervisor-configurable refill cadences (design 45 §5). Only ACTIVE rows: the server
   * refuses an inactive one, and a composer offering a vocabulary the server rejects produces failures
   * nobody can explain from the screen.
   */
  async refillFrequencies() {
    const r = (await getRaw(`/refill-frequencies`)) as any[];
    return ((r ?? []) as any[]).map((f: any) =>
      parseOr(zRefillFrequency, {
        code: String(f.code ?? ""),
        months: Math.trunc(Number(f.months ?? 0)),
        name: { en: String(f.nameEn ?? f.code ?? ""), ar: String(f.nameAr ?? f.nameEn ?? "") },
      }),
    );
  }

  /**
   * 29.5 — the window schedule, BEFORE submit (design 45 §5).
   *
   * <p>The arithmetic stays on the server: this is the same `ChronicAllocation.Plan` the write path runs,
   * so what the doctor is shown and what the pharmacy honours cannot disagree. A refusal here — a duration
   * that is not chronic, an unknown frequency, missing pack data — is the SAME refusal submit would give,
   * which is the point of previewing at all.</p>
   */
  async prescribableDrugById(drugId: string) {
    try {
      const d = (await getRaw(`/drugs/by-id/${encodeURIComponent(drugId)}`)) as any;
      if (!d?.drugId) return null;
      return parseOr(zPrescribableDrug, {
        drugId: d.drugId,
        tradeName: { en: String(d.name ?? ""), ar: String(d.nameAr ?? d.name ?? "") },
        activeIngredient: d.scientificName ?? undefined,
        strength: d.strength ?? undefined,
        form: d.form ?? undefined,
        priceEgp: typeof d.priceEgp === "number" ? d.priceEgp : undefined,
        atcCode: d.atcCode ?? undefined,
        // The by-id row carries no indication join; the flag is left as the search set it rather than
        // being asserted false, which would render an unchecked medicine as one with no indications.
        hasIndicationData: true,
        isLowestPrice: d.isLowestPrice === true,
        pricePerUnit: typeof d.pricePerUnit === "number" ? d.pricePerUnit : undefined,
        availability: d.availability === "Available" || d.availability === "Unavailable"
          ? d.availability
          : "Unknown",
        prescribingUnit: typeof d.prescribingUnit === "string" && d.prescribingUnit.length > 0
          ? d.prescribingUnit
          : null,
        // 31.3 — the SERVER's short form, never one reconstructed here. "Tablet" → "tabs" is a fact about
        // the vocabulary the drug table owns, and a second copy of it beside a dose field is a second
        // answer to what a medicine is counted in.
        prescribingUnitShort: typeof d.prescribingUnitShort === "string" && d.prescribingUnitShort.length > 0
          ? d.prescribingUnitShort
          : null,
        packSize: typeof d.packSize === "number" ? d.packSize : null,
        isPackSplittable: typeof d.isPackSplittable === "boolean" ? d.isPackSplittable : null,
      });
    } catch {
      // An enrichment, not a requirement. The composer keeps the snapshot it restored.
      return null;
    }
  }

  async quantityPreview(req: {
    drugId?: string;
    doseAmount?: number | null;
    timesPerDay?: number | null;
    durationDays?: number | null;
  }) {
    // The DRUG, and the doctor's own three numbers. NOT pack facts — see the interface note. A 422
    // `quantity-not-checked` travels up as an ApiError carrying the problem, and the composer renders which
    // field is missing rather than a quantity.
    const r = (await postRaw(`/prescriptions/quantity-preview`, req)) as any;
    return parseOr(zQuantityPreview, {
      totalUnits: Number(r?.totalUnits ?? 0),
      dispenseQuantity: Number(r?.dispenseQuantity ?? 0),
      packs: r?.packs ?? null,
      // Read strictly: an absent box count is NOT zero and NOT one, it is "this cannot be counted in boxes".
      boxes: typeof r?.boxes === "number" ? r.boxes : null,
      packContent: r?.packContent ?? null,
      prescribingUnit: r?.prescribingUnit ?? null,
      isPackSplittable: r?.isPackSplittable ?? null,
    });
  }

  async chronicPreview(req: {
    durationDays: number;
    refillFrequencyCode: string;
    doseAmount?: number;
    timesPerDay?: number;
    // The DRUG, so the server resolves its pack facts itself. The composer does not hold them and must not
    // fetch them to hand back — a second reader of the catalogue is a second thing that can disagree with it.
    drugId?: string;
    isPackSplittable?: boolean | null;
    /** 31.5 — what one box HOLDS, renamed from `packSize`: the pack size counts CONTAINERS for every
     *  measured product and is the wrong divisor for all of them (31.3). Normally omitted — the server
     *  resolves it from `drugId`. */
    packContent?: number | null;
  }) {
    const r = (await postRaw(`/prescriptions/chronic-preview`, req)) as any;
    return parseOr(zChronicPreview, {
      total: Number(r?.total ?? 0),
      unit: String(r?.unit ?? ""),
      frequencyMonths: Math.trunc(Number(r?.frequencyMonths ?? 0)),
      windows: ((r?.windows ?? []) as any[]).map((w: any) => ({
        windowNo: Math.trunc(Number(w.windowNo ?? 0)),
        scheduledOpen: String(w.scheduledOpen ?? ""),
        opensAt: String(w.opensAt ?? ""),
        closesAt: String(w.closesAt ?? ""),
        allocatedQuantity: Number(w.allocatedQuantity ?? 0),
      })),
    });
  }

  async submitPrescription(req: {
    encounterId: string;
    lines: PrescriptionDraftLine[];
    diagnosisIcdCodes: string[];
    acknowledgements: LineAcknowledgement[];
    // 29.5 — additive and defaulted, so every existing caller is unaffected (design 45 §5).
    kind?: PrescriptionKind;
    refillFrequencyCode?: string | null;
    durationDays?: number | null;
  }) {
    const beneficiaryId = encounterBeneficiary.get(req.encounterId) ?? req.encounterId;
    // Keyed on the composed line set, so a retry of the SAME prescription replays rather than duplicating,
    // while an edited one is a new submission.
    const idem = `rx:${req.encounterId}:${req.lines.map((l) => `${l.drug?.drugId}x${l.quantity}`).join(",")}`;
    const r = (await postRaw(`/prescriptions`, {
      beneficiaryId,
      encounterId: req.encounterId,
      lines: rxLines(req.lines),
      diagnosisIcdCodes: req.diagnosisIcdCodes,
      acknowledgements: req.acknowledgements.map((a) => ({
        clientLineId: a.lineId,
        findingKind: a.findingKind,
        reason: a.reason,
      })),
      // 29.5 — the script's own shape (design 45 §5). Sent only for a CHRONIC script: an acute one carries
      // no schedule at all, and the server refuses `acute-has-no-schedule` if one arrives, because "is this
      // chronic?" must have exactly one answer.
      ...(req.kind === "Chronic"
        ? {
            kind: "Chronic",
            refillFrequencyCode: req.refillFrequencyCode ?? null,
            durationDays: req.durationDays ?? null,
          }
        : {}),
    }, idem)) as any;
    return parseOr(zPrescriptionSubmitResult, {
      prescriptionId: required(r?.prescriptionId, "prescription.prescriptionId"),
      rxNo: String(r?.rxNo ?? ""),
      status: String(r?.status ?? ""),
    });
  }

  async drugAlternatives(drugId: string) {
    const r = (await getRaw(`/drugs/by-id/${encodeURIComponent(drugId)}/alternatives`)) as any;
    return ((r?.drugs ?? []) as any[]).map((d: any) =>
      parseOr(zDrugRef, {
        drugId: d.drugId,
        name: { en: String(d.name ?? ""), ar: String(d.nameAr ?? d.name ?? "") },
        atcCode: d.atcCode ?? undefined,
        form: d.form ?? undefined,
        strength: d.strength ?? undefined,
      }),
    );
  }
  async dispense(req: DispenseRequest) {
    const expiry = new Date(Date.now() + 365 * 24 * 3600 * 1000).toISOString().slice(0, 10);
    let last: any = null;
    for (const line of req.lines) {
      last = await postRaw(
        `/prescriptions/${encodeURIComponent(req.prescriptionId)}/lines/${encodeURIComponent(line.lineId)}/dispense`,
        {
          quantity: line.quantity,
          batchNo: `DEV-${String(req.prescriptionId).slice(0, 8)}`,
          expiryDate: expiry,
          // The server has accepted these since phase 6.3 and nothing sent them, so a substitution chosen at
          // the counter was recorded as a straight dispense of the ORIGINAL drug — the patient went home with
          // one molecule and the record said another.
          substitutedDrugId: line.substitute?.code,
          substitutionReason: line.substitutionReason,
          // The counter's note travels with EVERY line of this dispense. It describes the handover, not one
          // medicine, and a note recorded against only the first line would be lost the moment somebody read
          // the second.
          note: req.note,
        },
        `${req.idempotencyKey}:${line.lineId}`,
      );
    }
    const rx = last?.prescription ?? {};
    const outstanding = (rx.lines ?? []).filter((l: any) => Number(l.quantityRemaining ?? 0) > 0).length;
    return parseOr(zDispenseResult, {
      prescriptionId: req.prescriptionId,
      dispenseEventId: last?.dispense?.dispenseEventId ?? req.idempotencyKey,
      status: rxStatus(last?.rxStatus ?? rx.status),
      replayed: !!last?.replayed,
      linesOutstanding: outstanding,
    });
  }

  /**
   * Report that the counter cannot fill a line.
   *
   * <p>The endpoint has been complete since phase 6.3 — it consumes nothing, so the unfilled quantity stays
   * available for a later visit; it notifies the PRESCRIBER on a route that escalates to the pharmacy
   * supervisor after eight hours; it audits. Nothing in this application called it, so a pharmacist facing an
   * empty shelf had no control at all: the prescriber was never told, the escalation never fired, and a
   * refugee beneficiary made a second journey for a medicine nobody recorded as missing.</p>
   *
   * <p>Quantity and note are both optional. "We have none at all" is the common case and needs neither, and
   * a required field on the one control a busy counter reaches for under pressure is a control that gets
   * skipped.</p>
   *
   * <p>Raising it twice is safe: the server returns what it already recorded and does <b>not</b> notify
   * again. Two pharmacists reporting the same empty shelf must not put two escalating notifications in front
   * of one prescriber.</p>
   */
  async flagOutOfStock(req: { prescriptionId: string; lineId: string; quantity?: number; note?: string }) {
    const r = (await postRaw(
      `/prescriptions/${encodeURIComponent(req.prescriptionId)}/lines/${encodeURIComponent(req.lineId)}/out-of-stock`,
      { prescriptionLineId: req.lineId, quantity: req.quantity, note: req.note },
    )) as any;
    return parseOr(zOutOfStockResult, {
      lineId: r?.prescriptionLineId ?? req.lineId,
      flagged: r?.flagged !== false,
      // TRUE when the line was already flagged and this call notified nobody. The screen says so, because
      // "already reported by a colleague" and "reported just now" are different things to a pharmacist
      // deciding whether the prescriber has heard about it.
      replayed: !!r?.replayed,
      outOfStockAt: r?.outOfStockAt ?? null,
    });
  }

  async prescriptionPricing(prescriptionId: string, dispenseNow?: Record<string, number>) {
    return parseOr(
      zRxPricing,
      await getRaw(
        `/prescriptions/${encodeURIComponent(prescriptionId)}/pricing${basisQuery("dispense", dispenseNow)}`,
      ),
    );
  }

  // Approvals (Phase 7, US-060) — the worklist is GET /authorizations/ (min-necessary: codes + SLA, NO clinical
  // payload — that is /review only, audited as a PHI read). Decisions are per-type endpoints, not one /decision;
  // a decision needs the request UnderReview, so decide assigns first (idempotent-ish) then routes by kind.
  async approvalWorklist(
    kind: "Review" | "Fulfilment" | "All" = "Review",
    filter?: ApprovalQueueFilter,
  ) {
    // Defaulting to Review matches the server's default and is the same argument (ADR-0034): the inbox is a
    // work queue, and a few hundred dispenses a day landing in it would drown the requests that need a
    // decision. The register is asked for deliberately.
    //
    // THE FILTERS NOW REACH THE SERVER. `status`, `priority`, `slaBreached` and `unassigned` have always been
    // accepted by this endpoint and none of them was ever sent: the client took the server's 200-row page and
    // filtered it in the browser, so a tenant with three hundred pending requests narrowing to "breached" was
    // narrowing a truncated list and was told nothing. `assignedTo` is new on both sides — the queue had no
    // notion of ownership at all, which is the first question a queue worked by several people is read with.
    const q = new URLSearchParams({ kind });
    if (filter?.status) q.set("status", filter.status);
    if (filter?.priority) q.set("priority", filter.priority);
    if (filter?.slaBreached) q.set("slaBreached", "true");
    if (filter?.assignedTo === "unassigned") q.set("unassigned", "true");
    else if (filter?.assignedTo === "me") q.set("assignedTo", "me");

    const { body, total } = await getRawCounted(`/authorizations/?${q.toString()}`);
    const r = (body ?? []) as any[];
    const now = Date.now();
    const rows = (r ?? []).map((a: any) => {
      const dueMs = a.slaDueAt ? Date.parse(a.slaDueAt) : now;
      const codes: string[] = Array.isArray(a.serviceCodes) ? a.serviceCodes : [];
      const code = codes[0] ?? "—";
      return parseOr(zApprovalItem, {
        id: a.authorizationId,
        patient: { id: a.beneficiaryId, token: caseToken({ beneficiaryId: a.beneficiaryId }) },
        // `service` stays the first code because a table cell holds one thing; `serviceCodes` below carries
        // all of them, and the review panel lists the whole set rather than calling the tail "supporting".
        service: { system: "CPT", code, label: neutral(code) },
        serviceCodes: codes,
        // The SOURCE, not the literal string "Provider" this used to be on every row — including the manual
        // authorizations, which by definition have no requesting provider at all.
        requestedBy: requesterLabel(a.source),
        requestingProviderId: a.requestingProviderId ?? null,
        assignedReviewerId: a.assignedReviewerId ?? null,
        priority: String(a.priority ?? "routine").toLowerCase(),
        /*
          NO DUE DATE MEANS NO SLA, whatever kind of row this is.

          A fulfilment authorization never had one — nothing waited on anybody, the medicine is already in the
          patient's hand. A review request that has not been picked up has not started its clock either: the
          timer is set by `assign`, so `slaDueAt` is null until somebody takes it.

          Previously only the fulfilment case answered null, and every other row with no due date got one
          fabricated from the submission time — a countdown towards a deadline that does not exist, on a
          request nobody has accepted. The screen renders null as an em-dash and the "breached" filter counts
          it as neither breached nor in time, which is what it is.
        */
        sla: a.slaDueAt
          ? { dueAt: a.slaDueAt, breached: !!a.slaBreached, minutesRemaining: Math.round((dueMs - now) / 60000) }
          : null,
        status: authStatus(a.status),
        // The server's own timestamp. This was `now - tatElapsedSeconds`, recomputed on every render, so a
        // row's submission time crept forward while the page sat open.
        submittedAt: a.submittedAt ?? new Date(now - Number(a.tatElapsedSeconds ?? 0) * 1000).toISOString(),
        source: a.source ?? "Manual",
        itemReference: a.itemReference ?? null,
        extensionReason: a.extensionReason ?? null,
        // A server that predates ADR-0034 sends no `kind`, and everything it can send IS a review request —
        // so the fallback is the truth for that server rather than a guess.
        kind: a.kind === "Fulfilment" ? "Fulfilment" : "Review",
      });
    });
    return { rows, total: total ?? rows.length };
  }

  async retrospectiveQueue(closed?: boolean) {
    const r = (await getRaw(`/authorizations/retrospective-queue${closed ? "?closed=true" : ""}`)) as any[];
    return (r ?? []).map((a: any) =>
      parseOr(zRetrospectiveItem, {
        authorizationId: a.authorizationId,
        authNo: a.authNo ?? "",
        beneficiaryId: a.beneficiaryId,
        serviceCodes: Array.isArray(a.serviceCodes) ? a.serviceCodes : [],
        source: String(a.source ?? "Manual"),
        status: authStatus(a.status),
        decidedAt: a.decidedAt ?? null,
        ageDays: Number(a.ageDays ?? 0),
        reviewed: !!a.reviewed,
        outcome: a.outcome ?? null,
        reviewedAt: a.reviewedAt ?? null,
        reviewedBy: a.reviewedBy ?? null,
        rationale: a.rationale ?? null,
      }),
    );
  }

  async completeRetrospectiveReview(input: RetrospectiveReviewInput, idempotencyKey?: string) {
    const r = (await postRaw(
      `/authorizations/${encodeURIComponent(input.authorizationId)}/retrospective-review`,
      { outcome: input.outcome, rationale: input.rationale },
      idempotencyKey ?? `retro:${input.authorizationId}`,
    )) as any;
    return parseOr(zRetrospectiveItem, {
      authorizationId: r?.authorizationId ?? input.authorizationId,
      authNo: r?.authNo ?? "",
      beneficiaryId: r?.beneficiaryId,
      serviceCodes: Array.isArray(r?.serviceCodes) ? r.serviceCodes : [],
      source: String(r?.source ?? "Manual"),
      status: authStatus(r?.status),
      decidedAt: r?.decidedAt ?? null,
      ageDays: Number(r?.ageDays ?? 0),
      reviewed: true,
      outcome: r?.outcome ?? input.outcome,
      reviewedAt: r?.reviewedAt ?? null,
      reviewedBy: r?.reviewedBy ?? null,
      rationale: r?.rationale ?? input.rationale,
    });
  }

  async authorizationItems(authorizationId: string) {
    const r = (await getRaw(`/authorizations/${encodeURIComponent(authorizationId)}/items`)) as any[];
    return (r ?? []).map((i: any) =>
      parseOr(zAuthorizationItem, {
        itemId: i.itemId,
        sourceLineId: i.sourceLineId ?? null,
        orderedCode: i.orderedCode ?? "—",
        orderedLabel: i.orderedLabel ?? null,
        fulfilledCode: i.fulfilledCode ?? i.orderedCode ?? "—",
        fulfilledLabel: i.fulfilledLabel ?? null,
        quantity: Number(i.quantity ?? 0),
        // Trusted from the server, which derives it from the two codes rather than storing it — so the flag
        // cannot disagree with the codes beside it.
        substituted: !!i.substituted,
        substitutionReason: i.substitutionReason ?? null,
        fulfilledAt: i.fulfilledAt ?? new Date().toISOString(),
      }),
    );
  }

  async approvalReview(approvalId: string) {
    const a = (await getRaw(`/authorizations/${encodeURIComponent(approvalId)}/review`)) as any;
    const codes: string[] = a?.serviceCodes ?? [];
    return parseOr(zApprovalReview, {
      id: a?.authorizationId ?? approvalId,
      patient: { id: required(a?.beneficiaryId, "approval.beneficiaryId"), token: caseToken({ beneficiaryId: a?.beneficiaryId }) },
      service: { system: "CPT", code: codes[0] ?? "—", label: neutral(codes[0] ?? "") },
      clinicalJustification: a?.emrSummary ?? "clinical context unavailable",
      // EVERY requested service, including the first. `slice(1)` labelled the rest of the request
      // "supporting codes", which they never were — a three-service request read as one service with two
      // attachments, and a reviewer deciding on it was deciding on a third of what was asked.
      requestedServices: codes.map((c) => ({ system: "CPT" as const, code: c, label: neutral(c) })),
      documents: (a?.documents ?? []).map((d: any) => ({ id: d.id ?? d.documentId ?? "", name: d.name ?? d.title ?? "document" })),
    });
  }
  async decide(req: DecisionRequest) {
    const seg = decisionPath[req.decision] ?? "approve";
    const base = `/authorizations/${encodeURIComponent(req.approvalId)}`;
    // Move Submitted → UnderReview so the decision is legal; ignore if already assigned/underway.
    try {
      await postRaw(`${base}/assign`, {}, `${req.idempotencyKey}:assign`);
    } catch {
      /* already assigned or not assignable — proceed to the decision, which will report any real conflict */
    }
    const body =
      req.decision === "partial"
        ? { approvedScope: req.approvedAmount ? [req.approvedAmount] : [], rationale: req.rationale }
        : { rationale: req.rationale };
    const r = (await postRaw(`${base}/${seg}`, body, req.idempotencyKey)) as any;
    return parseOr(zDecisionResult, {
      approvalId: r?.authorizationId ?? req.approvalId,
      decisionId: r?.decisionId ?? r?.id ?? req.idempotencyKey,
      status: authStatus(r?.status),
      replayed: !!r?.replayed,
    });
  }

  // Approvals break-glass + SLA (Phase 7.3). The TAT board is a PHI-free reporting read (durations converted to
  // whole minutes). A manual authorization is a break-glass create (Idempotency-Key + mandatory justification,
  // always Approved from this form). Emergency-approve acts on a pending authorization from the worklist.
  async slaSummary() {
    const r = (await getRaw(`/authorizations/tat-summary`)) as any;
    const min = (s: unknown) => Math.round((Number(s ?? 0) / 60) * 10) / 10;
    return parseOr(zTatSummary, {
      total: Number(r?.total ?? 0),
      avgMinutes: min(r?.avgTatSeconds),
      p95Minutes: min(r?.p95TatSeconds),
      breaches: Number(r?.slaBreaches ?? 0),
      byStatus: (r?.byStatus ?? []).map((b: any) => ({
        status: String(b.status ?? ""),
        count: Number(b.count ?? 0),
        avgMinutes: min(b.avgTatSeconds),
        p95Minutes: min(b.p95TatSeconds),
        breaches: Number(b.slaBreaches ?? 0),
      })),
    });
  }
  async createManualAuth(input: ManualAuthInput, idempotencyKey?: string) {
    // 18.D1: prefer the FORM's key. The content-derived fallback treats two genuinely separate manual
    // authorizations for the same beneficiary + codes as one — which is wrong when a second is deliberate.
    const idem = idempotencyKey ?? `manual:${input.beneficiaryId}:${input.serviceCodes.join(",")}`;
    const body = {
      beneficiaryId: input.beneficiaryId,
      serviceCodes: input.serviceCodes,
      decision: "Approved",
      justification: input.justification,
      rationale: input.rationale ?? null,
    };
    const r = (await postRaw(`/authorizations/manual`, body, idem)) as any;
    return parseOr(zManualAuthResult, {
      authorizationId: r?.authorizationId ?? r?.AuthorizationId ?? "",
      authNo: r?.authNo ?? r?.AuthNo ?? "",
      status: authStatus(r?.status),
    });
  }
  async emergencyApprove(authId: string, justification: string) {
    const r = (await postRaw(`/authorizations/${encodeURIComponent(authId)}/emergency-approve`, { justification }, `emg:${authId}`)) as any;
    return parseOr(zEmergencyResult, { authorizationId: r?.authorizationId ?? authId, status: authStatus(r?.status ?? "EmergencyApproved") });
  }

  // Director / Reporting (Phase 8.3) — the reporting service emits one executive dashboard at
  // /dashboards/executive (zone-tagged widgets, each with chart series + a mandatory accessible dataTable +
  // bilingual labels, PHI-free). The zod contract splits widgets into kpis + charts, so we map gauge/summary
  // widgets to KPI cards and the rest to charts (every chart keeps its required dataTable — US-073).
  async executiveDashboard(scope: "executive" | "finance" | "director", period?: Period) {
    // `scope` and the period REACH THE SERVER now. Both used to stop here: the scope picked a page heading
    // and the period did not exist, so three portals rendered the same payload over whatever window the
    // server happened to default to.
    const d = (await getRaw(`/dashboards/executive${periodQuery(period, { scope })}`)) as any;
    const widgets: any[] = d?.widgets ?? [];
    const bi = (x: any) => ({ en: String(x?.en ?? ""), ar: String(x?.ar ?? "") });
    const points = (w: any) => (w.series?.[0]?.points ?? []) as any[];
    const chartTypeByKind: Record<number, "bar" | "line" | "donut"> = { 0: "line", 1: "bar", 2: "donut", 3: "bar", 4: "donut", 5: "bar" };
    const isKpi = (w: any) => w.kind === 2 || w.kind === 5; // Gauge | Summary → KPI card

    const kpis = widgets.filter(isKpi).map((w) =>
      parseOr(zKpiWidget, {
        kind: "kpi",
        id: w.key,
        title: bi(w.title),
        // MONEY IS MONEY. This was `String(sum)` for every KPI, and one of the two KPI widgets is the
        // financial summary — so the director's only cost figure rendered as a bare decimal with no currency,
        // no locale grouping and whatever float artefact the sum produced, in an application that formats
        // every other amount through `useFormat().money` precisely because `ar-EG` must not read as `en-US`.
        // The raw total travels as `value`; the screen formats it, because only the screen knows the locale.
        value: String(points(w).reduce((acc: number, p: any) => acc + Number(p.value ?? 0), 0)),
        // The breakdown the server already computed and this client used to throw away. Pending-approvals
        // ships status x priority x age x SLA breach; the financial summary ships cost by service line.
        // Neither rendered anywhere in the product.
        dataTable: {
          columns: (w.dataTable?.columns ?? []).map(bi),
          rows: (w.dataTable?.rows ?? []).map((row: any[]) => row.map((c) => String(c))),
        },
      }),
    );
    const charts = widgets.filter((w) => !isKpi(w)).map((w) =>
      parseOr(zChartWidget, {
        kind: "chart",
        id: w.key,
        title: bi(w.title),
        chartType: chartTypeByKind[w.kind as number] ?? "bar",
        series: points(w).map((p: any) => ({ label: neutral(p.label), value: Number(p.value ?? 0), display: String(p.value ?? 0) })),
        dataTable: {
          columns: (w.dataTable?.columns ?? []).map(bi),
          rows: (w.dataTable?.rows ?? []).map((row: any[]) => row.map((c) => String(c))),
        },
      }),
    );
    return parseOr(zExecutiveDashboard, {
      version: d?.contractVersion ?? "1.0",
      generatedAt: d?.generatedAt ?? new Date().toISOString(),
      scope,
      // Echoed from the widgets rather than from what we asked for: the server resolves the window on the
      // Cairo calendar and may not have used our dates at all. Stating the request back would be stating a
      // question, and what the screen has to show is the answer.
      period: widgets[0]?.from && widgets[0]?.to
        ? { from: String(widgets[0].from), to: String(widgets[0].to) }
        : period,
      kpis,
      charts,
    });
  }

  // Director oversight / quality / escalations (Phase 8.3) — de-identified reporting aggregates (no PHI). Each
  // section fetches the relevant /reports/* endpoints and normalises them to KPI headlines + accessible tables.
  async directorReport(section: "oversight" | "quality" | "escalations", period?: Period) {
    const q = periodQuery(period);
    const min = (s: unknown) => `${Math.round(Number(s ?? 0) / 60)}`;
    const pct = (n: unknown) => `${Math.round(Number(n ?? 0) * 100)}%`;
    if (section === "oversight") {
      const pend = (await getRaw(`/reports/pending-approvals`)) as any;
      const tat = (await getRaw(`/reports/approval-tat${q}`)) as any;
      return parseOr(zReportView, {
        kpis: [
          { label: { en: "Pending", ar: "معلّقة" }, value: String(pend?.total ?? 0) },
          { label: { en: "SLA breaches", ar: "تجاوزات" }, value: String(pend?.slaBreaches ?? 0) },
          { label: { en: "Avg TAT (min)", ar: "متوسط الاستجابة (د)" }, value: min(tat?.avgTatSeconds) },
          { label: { en: "P95 TAT (min)", ar: "الاستجابة p95 (د)" }, value: min(tat?.p95TatSeconds) },
        ],
        tables: [
          {
            title: { en: "Pending by status", ar: "المعلّقة حسب الحالة" },
            columns: [{ en: "Status", ar: "الحالة" }, { en: "Priority", ar: "الأولوية" }, { en: "Age", ar: "العمر" }, { en: "Count", ar: "العدد" }, { en: "Breaches", ar: "تجاوزات" }],
            rows: (pend?.rows ?? []).map((r: any) => [String(r.status), String(r.priority), String(r.ageBucket), String(r.count), String(r.slaBreaches)]),
          },
          {
            title: { en: "Turnaround by priority", ar: "الاستجابة حسب الأولوية" },
            columns: [{ en: "Priority", ar: "الأولوية" }, { en: "Count", ar: "العدد" }, { en: "Avg (min)", ar: "متوسط (د)" }, { en: "P95 (min)", ar: "p95 (د)" }],
            rows: (tat?.byPriority ?? []).map((r: any) => [String(r.dimension), String(r.count), min(r.avgTatSeconds), min(r.p95TatSeconds)]),
          },
        ],
      });
    }
    if (section === "quality") {
      const dx = (await getRaw(`/reports/top-diagnoses${q}`)) as any;
      const rx = (await getRaw(`/reports/top-medications${q}`)) as any;
      const ns = (await getRaw(`/reports/no-show${q}`)) as any;
      return parseOr(zReportView, {
        kpis: [
          { label: { en: "Booked", ar: "محجوزة" }, value: String(ns?.booked ?? 0) },
          { label: { en: "Attended", ar: "حضر" }, value: String(ns?.attended ?? 0) },
          { label: { en: "No-shows", ar: "تخلّف" }, value: String(ns?.noShow ?? 0) },
          { label: { en: "No-show rate", ar: "نسبة التخلّف" }, value: pct(ns?.noShowRate) },
        ],
        tables: [
          {
            title: { en: "Top diagnoses", ar: "أكثر التشخيصات" },
            columns: [{ en: "ICD-10", ar: "ICD-10" }, { en: "Count", ar: "العدد" }],
            rows: (dx?.rows ?? []).map((r: any) => [String(r.code), String(r.count)]),
          },
          {
            title: { en: "Top medications", ar: "أكثر الأدوية" },
            columns: [{ en: "ATC", ar: "ATC" }, { en: "Count", ar: "العدد" }],
            rows: (rx?.rows ?? []).map((r: any) => [String(r.code), String(r.count)]),
          },
          {
            title: { en: "No-show by clinic", ar: "التخلّف حسب العيادة" },
            columns: [{ en: "Clinic", ar: "العيادة" }, { en: "Booked", ar: "محجوزة" }, { en: "No-show", ar: "تخلّف" }, { en: "Rate", ar: "النسبة" }],
            rows: (ns?.byClinic ?? []).map((r: any) => [String(r.clinicId), String(r.booked), String(r.noShow), pct(r.noShowRate)]),
          },
        ],
      });
    }
    // escalations → rejected/flagged authorization requests by reason (de-identified).
    const rej = (await getRaw(`/reports/rejected-requests${q}`)) as any;
    return parseOr(zReportView, {
      kpis: [{ label: { en: "Rejected", ar: "مرفوضة" }, value: String(rej?.total ?? 0) }],
      tables: [
        {
          title: { en: "Rejections by reason", ar: "الرفض حسب السبب" },
          columns: [{ en: "Reason", ar: "السبب" }, { en: "Count", ar: "العدد" }],
          rows: (rej?.byReason ?? []).map((r: any) => [String(r.reasonCode), String(r.count)]),
        },
      ],
    });
  }

  /*
   * ── The 2026-08-11 oversight reads ──────────────────────────────────────────────────────────────────
   *
   * All three sit in reporting-service's PHI-free zone. The claims one is deliberately NOT a claims-service
   * call: the Medical Director holds `reporting:read-financial` and holds neither `claims:read` nor
   * `claims:reconcile`, and reaching a chart by widening an operational scope is how an analytical need
   * quietly becomes an operational authority.
   */
  /*
   * Named `serviceUse`, not `utilization`.
   *
   * `utilization()` is already taken on this client, by finance's member-benefit sense of the word: how much
   * of a cap somebody has consumed. This is the other sense — which services the network is using and how
   * often. Two methods with one name, distinguished only by which overload the caller picked, is how a
   * screen ends up rendering the wrong report and type-checking on the way.
   */
  async serviceUse(axis: ServiceAxis, period?: Period) {
    const r = (await getRaw(`/reports/utilization${periodQuery(period, { dimension: axis })}`)) as any;
    return parseOr(zServiceUseView, {
      dimension: axis,
      period: period ?? { from: "", to: "" },
      rows: (r?.rows ?? []).map((row: any) => ({ code: String(row.code ?? ""), count: Number(row.count ?? 0) })),
    });
  }

  async slaBreaches() {
    const r = (await getRaw(`/reports/sla-breaches`)) as any;
    return parseOr(zSlaBreachView, {
      total: Number(r?.total ?? 0),
      rows: (r?.rows ?? []).map((row: any) => ({
        authNo: String(row.authNo ?? ""),
        priority: String(row.priority ?? "Routine"),
        status: String(row.status ?? ""),
        ageBucket: String(row.ageBucket ?? ""),
        ageSeconds: Number(row.ageSeconds ?? 0),
        reviewerId: row.reviewerId ?? null,
      })),
    });
  }

  async claimsCost(period?: Period) {
    const r = (await getRaw(`/reports/claims-summary${periodQuery(period)}`)) as any;
    return parseOr(zClaimsCostView, {
      period: period ?? { from: "", to: "" },
      decided: Number(r?.decided ?? 0),
      // `money()` rather than Number(): it is the one place in this client that refuses a value it cannot
      // read as an amount, so a malformed total fails loudly instead of rendering as NaN in a currency field.
      totalAllowed: money(r?.totalAllowed ?? 0, "claimsCost.totalAllowed"),
      byOutcome: (r?.byOutcome ?? []).map((x: any) => ({ outcome: String(x.outcome ?? ""), count: Number(x.count ?? 0) })),
      byServiceLine: (r?.byServiceLine ?? []).map((x: any) => ({
        serviceLine: String(x.serviceLine ?? ""),
        amount: money(x.amount ?? 0, "claimsCost.amount"),
        count: Number(x.count ?? 0),
      })),
      topDenialReasons: (r?.topDenialReasons ?? []).map((x: any) => ({
        reasonCode: String(x.reasonCode ?? ""), count: Number(x.count ?? 0),
      })),
    });
  }

  // Case management (Phase 10.1) — assignment-scoped; the server re-authorizes every call (case-assignment ABAC).
  // The service returns { items } with PascalCase enums + a plain summary; adapt to the array + lowercase-enum
  // + bilingual contract shape.
  async myCases() {
    const r = (await getRaw(`/cases`)) as any;
    const items: any[] = Array.isArray(r) ? r : (r?.items ?? []);
    return items.map((c: any) =>
      parseOr(zCaseListItem, {
        id: c.caseId ?? c.id,
        caseNo: c.caseNo,
        beneficiary: { id: c.beneficiaryId ?? c.beneficiary?.id ?? c.caseId, token: caseToken(c) },
        category: String(c.category ?? "complex").toLowerCase(),
        priority: String(c.priority ?? "normal").toLowerCase(),
        status: caseStatus(c.status),
        openedAt: c.openedAt ?? new Date().toISOString(),
        summary: c.summary ? neutral(c.summary) : undefined,
      }),
    );
  }
  // The case-service assembles a coordination view (coverage/care-plan/appointment+approval STATUS + a clinical
  // SUMMARY where diagnoses are coord-visible but notes/rx/results are masked counts). Adapt its DTO — plain
  // strings + numeric limits — to the bilingual + StatusKind contract; the masked counts pass through unchanged.
  async beneficiary360(caseId: string) {
    const b = (await getRaw(`/cases/${encodeURIComponent(caseId)}/beneficiary-360`)) as any;
    const maskedCount = (m: any) => ({ count: Number(m?.count ?? 0), summaryOnly: true as const });
    return parseOr(zBeneficiary360, {
      caseId: b.caseId ?? caseId,
      caseNo: b.caseNo ?? "",
      beneficiary: {
        id: b.beneficiary?.beneficiaryId ?? b.beneficiary?.id ?? caseId,
        token: b.beneficiary?.maskedMemberId ?? b.beneficiary?.displayName ?? "•••",
      },
      coverage: {
        status: coverageChip(b.coverage?.status),
        planName: neutral(b.coverage?.policyName ?? "—"),
        coverageCategory: neutral(b.coverage?.coverageCategory ?? "—"),
        annualCap: b.coverage?.annualLimit != null ? money(b.coverage.annualLimit, "coverage.annualLimit") : undefined,
        remaining: b.coverage?.remainingLimit != null ? money(b.coverage.remainingLimit, "coverage.remainingLimit") : undefined,
      },
      carePlan: {
        status: neutral(b.carePlan?.status ?? "None"),
        goals: (b.carePlan?.goals ?? []).map((g: unknown) => neutral(g)),
        reviewDue: b.carePlan?.reviewDue ?? undefined,
      },
      appointments: (b.appointments ?? []).map((a: any) => ({
        id: a.appointmentId ?? a.id,
        clinic: neutral(a.clinic ?? "—"),
        when: a.when,
        status: coverageChip(a.status),
      })),
      openApprovals: (b.openApprovals ?? []).map((a: any) => ({
        authNo: a.authNo ?? "—",
        status: coverageChip(a.status),
        priority: casePriority(a.priority),
        decidedAt: a.decidedAt ?? undefined,
      })),
      clinical: {
        activeDiagnoses: (b.clinical?.activeDiagnoses ?? []).map((d: any) => ({
          system: (["ICD-10", "CPT", "LOINC", "ATC", "RxNorm"].includes(d.system) ? d.system : "ICD-10") as
            | "ICD-10" | "CPT" | "LOINC" | "ATC" | "RxNorm",
          code: d.code ?? "",
          label: neutral(d.display ?? d.label ?? d.code ?? ""),
        })),
        notes: maskedCount(b.clinical?.notes),
        prescriptions: maskedCount(b.clinical?.prescriptions),
        results: maskedCount(b.clinical?.results),
      },
    });
  }
  async caseTasks(caseId: string) {
    const r = (await getRaw(`/cases/${encodeURIComponent(caseId)}/tasks`)) as any;
    const items: any[] = Array.isArray(r) ? r : (r?.items ?? []);
    return items.map((t: any) =>
      parseOr(zCoordinationTask, {
        id: t.taskId ?? t.id,
        caseId: t.caseId ?? caseId,
        title: neutral(t.title ?? t.description ?? ""),
        state: String(t.state ?? "todo").toLowerCase().replace(/inprogress/, "in_progress"),
        dueAt: t.dueAt ?? undefined,
        status: taskChip(t.state),
      }),
    );
  }
  async escalations() {
    const r = (await getRaw(`/cases/escalations`)) as any;
    const items: any[] = Array.isArray(r) ? r : (r?.items ?? []);
    return items.map((e: any) =>
      parseOr(zEscalation, {
        id: e.escalationId ?? e.id,
        caseId: required(e.caseId, "caseEvent.caseId"),
        caseNo: e.caseNo ?? "",
        raisedToRole: neutral(e.raisedToRole ?? e.targetRole ?? ""),
        reason: String(e.reason ?? ""),
        // An escalation is by definition something that needed raising. `warn`, never the green chip the
        // literal produced — and, as above, that literal threw before it could mislead anyone.
        status: { kind: "warn" as const, label: { en: "Escalated", ar: "مُصعَّدة" } },
        raisedAt: e.raisedAt ?? e.createdAt ?? new Date().toISOString(),
      }),
    );
  }

  // Finance (Phase 10.2) — billing codes + amounts only; the finance service denies any clinical read.
  // The service emits plain strings + numeric amounts; these adapters map to the bilingual + pre-formatted
  // contract shape (and compute share%), then validate the mapping.
  //
  // ============================================================================================================
  // THE MAPPINGS BELOW ARE NOW TESTED (design 49 §1)
  // ============================================================================================================
  // Two of them used to write the literal string "ok" into a `status` field whose schema is `{ kind, label }`,
  // so `parseOr` threw and EVERY settlement read and EVERY export failed against a real gateway. Nothing
  // caught it because nothing ran it: the web tests construct `DevApiClient`, and this class only exists when
  // there is a gateway on the other end. The Provider Settlements screen and the Exports screen were
  // permanently in their error state in production and permanently green in CI.
  //
  // `test/http-client-contract.test.ts` now drives these methods over a stubbed `fetch` and validates what
  // comes out against the real schemas. The HTTP adapter is code, and untested code is code that does not work.
  async utilization(period?: Period) {
    // `from`/`to` reach the server at last. The endpoint has accepted them since phase 10.2 and this screen
    // sent neither, so finance saw the trailing month forever and could not close a prior one — the single
    // most routine thing a finance function does. The screen even RENDERED the window it was given, which is
    // the period rule honoured in the reading and broken in the asking.
    const r = (await getRaw(`/finance/utilization${periodQuery(period)}`)) as any;
    return parseOr(zUtilizationView, {
      from: r?.from ?? "",
      to: r?.to ?? "",
      rows: (r?.rows ?? []).map((x: any) => ({
        serviceCode: x.serviceCode,
        serviceLine: neutral(x.serviceLine),
        coverageCategory: neutral(x.coverageCategory),
        providerRef: x.providerRef ?? undefined,
        authorizedQty: x.authorizedQty ?? 0,
        deliveredQty: x.deliveredQty ?? 0,
        spend: money(x.spend, "utilization.rows[].spend"),
      })),
      totalAuthorized: r?.totalAuthorized ?? 0,
      totalDelivered: r?.totalDelivered ?? 0,
      totalSpend: money(r?.totalSpend, "utilization.totalSpend"),
    });
  }

  /**
   * The settlement list, with how many there ACTUALLY are.
   *
   * <p>The endpoint caps at 100 and reports the true count on `X-Total-Count`. It also accepts `providerId`
   * and `status`, neither of which was ever sent — the screen pulled every settlement and filtered in the
   * browser, which cannot see past the cap and so filtered a truncated set while presenting it as complete.</p>
   */
  async settlements(filter?: { providerId?: string; status?: string }) {
    const params = new URLSearchParams();
    if (filter?.providerId) params.set("providerId", filter.providerId);
    if (filter?.status) params.set("status", filter.status);
    const q = params.toString();
    const { body, total } = await getRawCounted(`/finance/settlements${q ? `?${q}` : ""}`);
    const rows = ((body as any[]) ?? []).map((s: any) => this.#settlement(s));
    return parseOr(zSettlementPage, { rows, total: total ?? rows.length });
  }

  /**
   * One settlement, from the service's `SettlementView`.
   *
   * <p>Private because four call sites need it — the list and the three lifecycle writes, each of which
   * returns the settlement it just moved. A generate that returned a shape the list could not parse would be
   * a screen that writes successfully and then cannot show what it wrote.</p>
   */
  #settlement(s: any) {
    return {
      id: s.settlementId ?? s.id,
      settlementNo: s.settlementNo,
      providerRef: s.providerRef ?? s.providerId ?? "",
      providerName: neutral(s.providerName ?? s.providerRef ?? ""),
      periodStart: s.periodStart ?? "",
      periodEnd: s.periodEnd ?? "",
      currency: s.currencyCode ?? s.currency ?? "EGP",
      total: money(s.total, "settlement.total"),
      // The REAL state, four ways. This was the literal "ok" — see `settlementChip`.
      status: settlementChip(s.status ?? s.state),
      state: String(s.status ?? s.state ?? "draft").toLowerCase(),
      submittedBy: s.submittedBy ?? undefined,
      approvedBy: s.approvedBy ?? undefined,
      lines: (s.lines ?? []).map((l: any) => ({
        serviceCode: l.serviceCode,
        serviceLine: neutral(l.serviceLine),
        deliveredQty: l.deliveredQty ?? 0,
        agreedUnitPrice: money(l.agreedUnitPrice, "settlement.lines[].agreedUnitPrice"),
        lineTotal: money(l.lineTotal, "settlement.lines[].lineTotal"),
        // Projected by the service since phase 10.2 and dropped here, so a reviewer authorising a payment
        // saw a contract tariff and an inferred floor rendered identically. `Contract` is the default only
        // for a row written before the column existed; a value the enum does not know is not silently
        // widened into one it does.
        priceSource: l.priceSource === "ObservedFloor" ? "ObservedFloor" : "Contract",
      })),
    };
  }

  /**
   * Generate a draft settlement for a provider and period.
   *
   * <p>The first of three writes the finance role has held the scopes for since phase 10.2 with no screen to
   * use them. Idempotency-keyed because generating one mints a financial artifact and a retried request must
   * return the settlement produced the first time rather than a second one.</p>
   */
  async generateSettlement(req: GenerateSettlementRequest) {
    const r = (await postRaw(`/finance/settlements`, req, newIdempotencyKey())) as any;
    return parseOr(zSettlement, this.#settlement(r));
  }

  /** Submit a draft for approval — the initiator half of the SoD split. */
  async submitSettlement(id: string) {
    const r = (await postRaw(`/finance/settlements/${encodeURIComponent(id)}/submit`, {})) as any;
    return parseOr(zSettlement, this.#settlement(r));
  }

  /**
   * Approve a submitted settlement — the release half.
   *
   * <p>The service refuses when the approver is the submitter (409 `urn:hbmp:sod-violation`), and that
   * refusal stays: the client is not the authority on who may release a payment. The screen reads
   * `submittedBy` and declines to offer the button in the first place, so the ordinary path no longer runs
   * through a refusal.</p>
   */
  async approveSettlement(id: string) {
    const r = (await postRaw(`/finance/settlements/${encodeURIComponent(id)}/approve`, {})) as any;
    return parseOr(zSettlement, this.#settlement(r));
  }

  async financialSummary(dimension: "serviceline" | "category" | "provider", period?: Period) {
    const r = (await getRaw(`/finance/summaries${periodQuery(period, { dimension })}`)) as any;
    const buckets: any[] = r?.buckets ?? [];
    const total = buckets.reduce((acc, b) => acc + Number(b.spend ?? 0), 0) || 1;
    return parseOr(zFinancialSummary, {
      dimension: r?.dimension ?? dimension,
      buckets: buckets.map((b: any) => ({
        key: neutral(b.key),
        deliveredQty: b.deliveredQty ?? 0,
        spend: money(b.spend, "financialSummary.buckets[].spend"),
        sharePercent: Math.round((Number(b.spend ?? 0) / total) * 100),
      })),
      totalSpend: money(r?.totalSpend ?? total, "financialSummary.totalSpend"),
    });
  }

  /**
   * Run an export and HAND OVER THE FILE.
   *
   * <p>This method used to `postRaw` — parse a `text/csv` response as JSON — and return a row count. There
   * was no download anywhere in the application, so the one thing the Exports screen exists to do did not
   * happen; and the filename it reported was built locally from the requested format, which is how it managed
   * to name a file `.xlsx` that the server had always produced as CSV.</p>
   *
   * <p>The blob is saved through an object URL and an anchor click, and the URL is revoked afterwards —
   * a blob held by a live URL is a copy of an audited financial extract kept alive in the page.</p>
   */
  async exportReport(req: ExportRequest) {
    const { blob, filename, rowCount } = await postForFile(`/finance/exports`, req);
    // The server's name if it gave one — it knows what it produced. The local fallback is CSV-suffixed
    // unconditionally, because CSV is the only thing this endpoint returns.
    const name = filename ?? `${req.report}-${req.from}_${req.to}.csv`;
    saveBlob(blob, name);
    return parseOr(zExportResult, {
      report: req.report,
      format: "csv",
      // Null means the gateway has not been told to expose `X-Row-Count`, which is not the same fact as
      // zero rows — but the schema wants a number and the file is already downloaded, so the honest
      // fallback is the one the operator can check for themselves by opening it.
      rowCount: rowCount ?? 0,
      filename: name,
      status: { kind: "ok" as const, label: { en: "Exported", ar: "تم التصدير" } },
    });
  }

  // Claims management (Phase 10b) — codes + amounts only, never a diagnosis. The service isolates provider
  // users to their own claims and audits every read; the portal maps status/bucket → non-color StatusKind chips.
  async claimsWorklist(status?: string, take?: number) {
    /*
      `/claims`, NOT `/claims/worklist`.

      The old call went to the per-LINE adjudication queue: hard-filtered to UnderAdjudication + Pending, with
      no `status` query parameter at all. ASP.NET bound nothing and answered 200, so all four segments of the
      screen's status control returned identical rows — none of them in any of the statuses named. And the
      payload is a LINE, so `origin`, `claimedAmount`, `netPayable` and `submittedAt` were all absent: every
      money column on the claims worklist rendered zero or blank, on every row, always.

      `GET /api/v1/claims` is the claim-level list. It parses `status` into a real ClaimStatus and returns the
      amounts. The line queue still has a screen — `adjudicationQueue` below — because that is what it is for.
    */
    const q = new URLSearchParams();
    if (status) q.set("status", status);
    q.set("take", String(take ?? 200));
    const r = (await getRaw(`/claims?${q.toString()}`)) as any[];
    return (r ?? []).map((c: any) =>
      parseOr(zClaimRow, {
        id: c.claimId ?? c.id,
        claimNo: c.claimNo ?? "",
        origin: String(c.origin ?? ""),
        status: claimStatusChip(c.status),
        currency: c.currencyCode ?? c.currency ?? "EGP",
        claimedAmount: Number(c.claimedAmount ?? 0),
        netPayable: c.netPayable ?? null,
        serviceDateFrom: String(c.serviceDateFrom ?? ""),
        submittedAt: c.submittedAt ?? undefined,
      }),
    );
  }

  async claimDetail(claimId: string) {
    const c = (await getRaw(`/claims/${encodeURIComponent(claimId)}`)) as any;
    return parseOr(zClaimDetail, {
      id: c?.claimId ?? claimId,
      claimNo: c?.claimNo ?? "",
      origin: String(c?.origin ?? ""),
      status: claimStatusChip(c?.status),
      currency: c?.currencyCode ?? "EGP",
      claimedAmount: Number(c?.claimedAmount ?? 0),
      approvedAmount: c?.approvedAmount ?? null,
      adjustedAmount: c?.adjustedAmount ?? null,
      netPayable: c?.netPayable ?? null,
      serviceDateFrom: String(c?.serviceDateFrom ?? ""),
      submittedAt: c?.submittedAt ?? undefined,
      lines: (c?.lines ?? []).map((l: any) => ({
        claimLineId: l.claimLineId,
        codeSystem: String(l.codeSystem ?? ""),
        code: String(l.code ?? "—"),
        description: l.description ?? null,
        quantity: Number(l.quantity ?? 0),
        billedAmount: Number(l.billedAmount ?? 0),
        contractPrice: l.contractPrice ?? null,
        allowedAmount: l.allowedAmount ?? null,
        status: claimLineStatusChip(l.status),
        reasonCodes: Array.isArray(l.reasonCodes) ? l.reasonCodes : [],
      })),
    });
  }

  async claimAdjustments(claimId: string) {
    const r = (await getRaw(`/claims/${encodeURIComponent(claimId)}/adjustments`)) as any[];
    return (r ?? []).map((a: any) =>
      parseOr(zClaimAdjustment, {
        adjustmentId: a.adjustmentId,
        claimLineId: a.claimLineId ?? null,
        type: String(a.type ?? a.adjustmentType ?? ""),
        amountDelta: Number(a.amountDelta ?? 0),
        beforeAmount: a.beforeAmount ?? null,
        afterAmount: a.afterAmount ?? null,
        reasonCode: a.reasonCode ?? null,
        adjustedAt: a.adjustedAt ?? new Date().toISOString(),
      }),
    );
  }

  async adjudicationQueue(filter?: { recommendation?: string; reasonCode?: string; minValue?: number; maxValue?: number }) {
    // The line queue, called with the parameters it actually accepts. Every one of these was served and
    // unreachable: the only caller this endpoint ever had was passing it a `status` it does not take.
    const q = new URLSearchParams();
    if (filter?.recommendation) q.set("recommendation", filter.recommendation);
    if (filter?.reasonCode) q.set("reasonCode", filter.reasonCode);
    if (filter?.minValue !== undefined) q.set("minValue", String(filter.minValue));
    if (filter?.maxValue !== undefined) q.set("maxValue", String(filter.maxValue));
    const r = (await getRaw(`/claims/worklist${q.toString() ? `?${q.toString()}` : ""}`)) as any[];
    return (r ?? []).map((l: any) =>
      parseOr(zAdjudicationRow, {
        claimId: l.claimId,
        claimNo: l.claimNo ?? "",
        claimLineId: l.claimLineId,
        serviceDate: String(l.serviceDate ?? ""),
        codeSystem: String(l.codeSystem ?? ""),
        code: String(l.code ?? "—"),
        description: l.description ?? null,
        quantity: Number(l.quantity ?? 0),
        billedAmount: Number(l.billedAmount ?? 0),
        contractPrice: l.contractPrice ?? null,
        allowedAmount: l.allowedAmount ?? null,
        status: claimLineStatusChip(l.status),
        systemRecommendation: l.systemRecommendation ?? null,
        reasonCodes: Array.isArray(l.reasonCodes) ? l.reasonCodes : [],
        authorizationId: l.authorizationId ?? null,
        // A BOOLEAN, derived server-side from the fulfilment linkage. The officer confirms the service was
        // rendered without reading what it found — the min-necessary boundary this whole portal rests on.
        resultExists: !!l.resultExists,
      }),
    );
  }

  async decideClaimLine(req: ClaimDecisionRequest, idempotencyKey?: string) {
    const r = (await postRaw(
      `/claims/${encodeURIComponent(req.claimId)}/lines/${encodeURIComponent(req.claimLineId)}/decisions`,
      {
        decision: req.decision,
        allowedAmount: req.allowedAmount ?? null,
        reasonCodes: req.reasonCodes,
        rationale: req.rationale,
        confirmsDecisionId: req.confirmsDecisionId ?? null,
      },
      idempotencyKey ?? newIdempotencyKey(),
    )) as any;
    // 202 PendingSecondApproval comes back through the same success path — it is an OUTCOME, not a failure.
    // The decision exceeded the dual-control threshold and waits for a second, distinct approver; showing it
    // as an error would teach reviewers that the threshold is a malfunction.
    return parseOr(zClaimDecisionResult, {
      outcome: String(r?.outcome ?? "Recorded"),
      decisionId: r?.decisionId ?? "",
      lineStatus: r?.lineStatus ?? undefined,
      claimStatus: r?.claimStatus ?? undefined,
      allowedAmount: r?.allowedAmount ?? null,
    });
  }

  async raiseClaimAdjustment(
    input: { claimId: string; claimLineId: string; type: string; amountDelta: number; reasonCode?: string; rationale?: string },
    idempotencyKey?: string,
  ) {
    const r = (await postRaw(
      `/claims/${encodeURIComponent(input.claimId)}/lines/${encodeURIComponent(input.claimLineId)}/adjustments`,
      {
        type: input.type,
        amountDelta: input.amountDelta,
        reasonCode: input.reasonCode ?? null,
        rationale: input.rationale ?? null,
      },
      idempotencyKey ?? newIdempotencyKey(),
    )) as any;
    return parseOr(zClaimAdjustment, {
      adjustmentId: r?.adjustmentId ?? "",
      claimLineId: input.claimLineId,
      type: input.type,
      amountDelta: input.amountDelta,
      beforeAmount: r?.beforeAmount ?? null,
      afterAmount: r?.afterAmount ?? null,
      reasonCode: input.reasonCode ?? null,
      adjustedAt: new Date().toISOString(),
    });
  }

  async claimsReconciliation(bucket?: string, period?: Period) {
    // The window now travels. This endpoint has always defaulted to the last 90 CAIRO days and the screen sent
    // nothing and displayed nothing, so a reconciliation list silently ended 90 days back with no indication
    // that anything preceded it.
    const q = new URLSearchParams();
    if (bucket) q.set("bucket", bucket);
    if (period?.from) q.set("from", period.from);
    if (period?.to) q.set("to", period.to);
    const r = (await getRaw(`/reconciliation${q.toString() ? `?${q.toString()}` : ""}`)) as any[];
    return (r ?? []).map((l: any) =>
      parseOr(zReconciliationRow, {
        claimId: l.claimId,
        // The row's real identity, which the server always sent and this mapper dropped. Keying on
        // claimId + code collided for two lines of one claim on the same code — the QuantityVariance case.
        claimLineId: l.claimLineId,
        claimNo: l.claimNo ?? "",
        origin: String(l.origin ?? ""),
        code: l.code ?? "—",
        serviceDate: String(l.serviceDate ?? ""),
        billedAmount: Number(l.billedAmount ?? 0),
        allowedAmount: l.allowedAmount ?? l.contractPrice ?? null,
        bucket: String(l.bucket ?? ""),
        status: reconBucketChip(l.bucket),
      }),
    );
  }

  async claimsKpis(period?: Period) {
    const q = new URLSearchParams();
    if (period?.from) q.set("from", period.from);
    if (period?.to) q.set("to", period.to);
    const r = (await getRaw(`/claims/kpis${q.toString() ? `?${q.toString()}` : ""}`)) as any;
    return parseOr(zClaimsKpis, {
      averageTatHours: Number(r?.averageTatHours ?? 0),
      approvalRate: Number(r?.approvalRate ?? 0),
      denialRate: Number(r?.denialRate ?? 0),
      ocrAutoMatchRate: Number(r?.ocrAutoMatchRate ?? 0),
      agedUnbilledCount: Number(r?.agedUnbilledCount ?? 0),
      agedUnbilledValue: Number(r?.agedUnbilledValue ?? 0),
      recoveryOutstanding: Number(r?.recoveryOutstanding ?? 0),
      topDenialReasons: (r?.topDenialReasons ?? []).map((d: any) => ({ reason: d.reason ?? d.code ?? "—", count: Number(d.count ?? 0) })),
    });
  }

  // Notifications (Phase 8.1) — the caller's own in-app inbox. The service row-filters by recipient == caller,
  // so this is inherently min-necessary. Map the service's status vocabulary → a non-color StatusKind chip.
  async notifications(unreadOnly?: boolean) {
    const r = (await getRaw(`/notifications/${unreadOnly ? "?unreadOnly=true" : ""}`)) as any[];
    return (Array.isArray(r) ? r : []).map((n: any) =>
      parseOr(zNotification, {
        id: n.notificationId ?? n.id,
        subject: String(n.subject ?? ""),
        body: String(n.body ?? ""),
        status: notificationChip(n.statusText),
        entityRef: n.entityRef ?? undefined,
        sourceEventType: String(n.sourceEventType ?? ""),
        actionable: Boolean(n.actionable ?? (String(n.statusText ?? "").toLowerCase().includes("action"))),
        read: Boolean(n.read),
        createdAt: n.createdAt ?? new Date().toISOString(),
      }),
    );
  }
  async markNotificationRead(id: string) {
    const r = (await postRaw(`/notifications/${encodeURIComponent(id)}/read`, {})) as any;
    return parseOr(zMarkReadResult, { id: r?.notificationId ?? id, read: true });
  }
  async markAllNotificationsRead() {
    const r = (await postRaw(`/notifications/read-all`, {})) as any;
    return parseOr(zMarkAllReadResult, { marked: Number(r?.marked ?? 0) });
  }

  // Admin / platform governance (Phase 8b). Every read is admin-role gated + audited server-side. Subject ids are
  // masked to a short token here (the admin manages access, not identities). Statuses render as non-color chips.
  async accessMatrix() {
    const r = (await getRaw(`/admin/access-matrix`)) as any[];
    return (Array.isArray(r) ? r : []).map((b: any) =>
      parseOr(zRoleBinding, {
        id: b.bindingId ?? b.id,
        subjectToken: `•••${String(b.subjectUserId ?? "").replace(/-/g, "").slice(-4)}`,
        role: String(b.role ?? ""),
        scope: String(b.scope ?? "Tenant"),
        tier: String(b.tier ?? ""),
        status: { kind: "ok", label: t("Active", "نشط") },
        grantedAt: b.grantedAt ?? new Date().toISOString(),
        reviewDueAt: b.reviewDueAt ?? undefined,
      }),
    );
  }
  /**
   * 18.C2 (audit R2 W5) — users from the IDENTITY STORE. The console read admin-service's access-matrix
   * PROJECTION, which knows role bindings and nothing about the account, so it could not show whether an
   * account was active or carried a second factor — the control gating every admin scope on the platform.
   * `/identity` sits outside `/api/v1` (it is the issuer's own surface), hence the absolute gateway path.
   */
  async identityUsers(query?: string) {
    const url = `${GATEWAY_BASE}/identity/admin/users${query ? `?query=${encodeURIComponent(query)}` : ""}`;
    const r = (await getAbsolute(url)) as any[];
    return (Array.isArray(r) ? r : []).map((u: any) =>
      parseOr(zIdentityUser, {
        id: String(u.id),
        username: String(u.username ?? ""),
        displayName: String(u.displayName ?? u.username ?? ""),
        // NOT masked, unlike the tenant beside it. The address is the sign-in credential and the destination
        // of every reset link, so an administrator who cannot read it cannot tell whether the button they
        // are about to press will reach the person — which is the whole question that button asks.
        email: u.email ?? null,
        // Absent normalised to null, never "": the table branches on "is there a title", and a blank string
        // would render an empty cell where the honest answer is "none recorded".
        position: u.position ? String(u.position) : null,
        tenantId: u.tenantId ? `•••${String(u.tenantId).replace(/-/g, "").slice(-4)}` : undefined,
        isActive: u.isActive !== false,
        twoFactorEnabled: u.twoFactorEnabled === true,
        roles: Array.isArray(u.roles) ? u.roles.map(String) : [],
      }),
    );
  }

  // ---- 28.8/28.9 — administering people ------------------------------------------------------------------
  //
  // Thin by design. Each of these is one call to an endpoint that has enforced its own rules since 17.4 —
  // the SoD engine, the MFA gate, the audit write, the membership mirror. Anything clever here would be a
  // second opinion about a decision the server has already made correctly.

  async createIdentityUser(input: {
    username: string; displayName: string; email: string; tenantId?: string; roles: string[]; lang?: "en" | "ar";
    position?: string;
  }) {
    const r = (await postAbsolute(`${GATEWAY_BASE}/identity/admin/users`, input)) as any;
    return { id: String(r?.id ?? ""), resetLinkSent: r?.resetLinkSent === true };
  }

  async updateIdentityUser(id: string, input: { displayName?: string; email?: string; position?: string }) {
    await postAbsolute(`${GATEWAY_BASE}/identity/admin/users/${encodeURIComponent(id)}`, input);
  }

  async myProfile() {
    const r = (await getAbsolute(`${GATEWAY_BASE}/identity/me/profile`)) as any;
    return {
      displayName: String(r?.displayName ?? ""),
      // Normalised to null rather than left as undefined: the app bar branches on "is there a title", and
      // two falsy shapes for one absence is how that branch eventually gets written wrong.
      position: r?.position ? String(r.position) : null,
    };
  }

  async setIdentityUserRoles(id: string, roles: string[]) {
    await postAbsolute(`${GATEWAY_BASE}/identity/admin/users/${encodeURIComponent(id)}/roles`, { roles });
  }

  async deactivateIdentityUser(id: string) {
    await postAbsolute(`${GATEWAY_BASE}/identity/admin/users/${encodeURIComponent(id)}/deactivate`, {});
  }

  async reactivateIdentityUser(id: string) {
    await postAbsolute(`${GATEWAY_BASE}/identity/admin/users/${encodeURIComponent(id)}/reactivate`, {});
  }

  async sendPasswordResetLink(id: string, lang?: "en" | "ar") {
    await postAbsolute(`${GATEWAY_BASE}/identity/admin/users/${encodeURIComponent(id)}/reset-password`, { lang });
  }

  async changeMyPassword(currentPassword: string, newPassword: string) {
    // `/identity/me`, not `/identity/admin`: changing your own password needs no administrative scope, and
    // putting it behind one would mean the people most likely to need it could not.
    await postAbsolute(`${GATEWAY_BASE}/identity/me/password`, { currentPassword, newPassword });
  }

  async scopeCatalog() {
    const r = (await getAbsolute(`${GATEWAY_BASE}/identity/admin/scopes`)) as any[];
    return (Array.isArray(r) ? r : []).map((s: any) =>
      parseOr(zScopeCatalogEntry, {
        name: String(s.name ?? ""),
        domain: String(s.domain ?? ""),
        description: s.description ?? null,
        serviceOnly: s.serviceOnly === true,
        deprecated: s.deprecated === true,
        replacedBy: s.replacedBy ?? null,
        isPlatformAdminKey: s.isPlatformAdminKey === true,
        heldBy: Array.isArray(s.heldBy) ? s.heldBy.map(String) : [],
      }),
    );
  }

  async roleCatalog() {
    const r = (await getAbsolute(`${GATEWAY_BASE}/identity/admin/roles`)) as any[];
    return (Array.isArray(r) ? r : []).map((x: any) =>
      parseOr(zRoleCatalogEntry, {
        name: String(x.name ?? ""),
        description: x.description ?? null,
        sensitivityTier: String(x.sensitivityTier ?? "T1"),
        level: typeof x.level === "number" ? x.level : null,
        custom: x.custom === true,
        builtIn: x.builtIn === true,
        scopes: Array.isArray(x.scopes) ? x.scopes.map(String) : [],
      }),
    );
  }

  async createRole(input: { name: string; scopes: string[]; description?: string; sensitivityTier?: string }) {
    await postAbsolute(`${GATEWAY_BASE}/identity/admin/roles`, input);
  }

  async setRoleScopes(role: string, scopes: string[]) {
    await postAbsolute(`${GATEWAY_BASE}/identity/admin/roles/${encodeURIComponent(role)}/scopes`, { scopes });
  }

  /** 18.C2 (W5) — the live role→scope matrix, read from the issuer's own catalog rather than inferred. */
  async identityRoleScopes() {
    const roles = (await getAbsolute(`${GATEWAY_BASE}/identity/roles`)) as any[];
    const names = (Array.isArray(roles) ? roles : []).map((r: any) => String(r.name));
    // One call per role: /effective-scopes is the exact seam the issuer uses to build the `scope` claim, so
    // what the screen shows is what a token would actually carry — not a second copy of the mapping.
    const rows = await Promise.all(
      names.map(async (role) => {
        const scopes = (await getAbsolute(`${GATEWAY_BASE}/identity/effective-scopes?role=${encodeURIComponent(role)}`)) as string[];
        return parseOr(zRoleScopeGrant, { role, scopes: Array.isArray(scopes) ? scopes.map(String) : [] });
      }),
    );
    return rows;
  }

  async adminTenants() {
    const r = (await getRaw(`/admin/tenants`)) as any[];
    return (Array.isArray(r) ? r : []).map((tn: any) =>
      parseOr(zTenantSummary, {
        id: tn.tenantId ?? tn.id,
        name: String(tn.name ?? ""),
        status: tn.active === false
          ? { kind: "neu" as const, label: t("Inactive", "غير نشط") }
          : { kind: "ok" as const, label: t("Active", "نشط") },
        createdAt: tn.createdAt ?? undefined,
      }),
    );
  }
  async sodMatrix() {
    const r = (await getRaw(`/admin/sod-matrix`)) as any[];
    return (Array.isArray(r) ? r : []).map((c: any) =>
      parseOr(zSodConflict, { roleA: String(c.tokenA ?? ""), roleB: String(c.tokenB ?? ""), reason: String(c.reason ?? "") }),
    );
  }
  async accessReviewCampaigns() {
    const r = (await getRaw(`/admin/dashboards/access-review`)) as any[];
    return (Array.isArray(r) ? r : []).map((c: any) =>
      parseOr(zAccessReviewCampaign, {
        id: c.campaignId ?? c.id,
        name: String(c.name ?? ""),
        status: String(c.status ?? "").toLowerCase() === "open"
          ? { kind: "info" as const, label: t("Open", "مفتوح") }
          : { kind: "neu" as const, label: t("Closed", "مغلق") },
        minTier: c.minTier ?? undefined,
        dueAt: c.dueAt ?? undefined,
      }),
    );
  }
  async breakGlassGrants() {
    const r = (await getRaw(`/admin/dashboards/break-glass`)) as any[];
    return (Array.isArray(r) ? r : []).map((g: any) =>
      parseOr(zBreakGlassGrant, {
        id: g.grantId ?? g.id,
        requesterToken: `•••${String(g.requester ?? g.requesterUserId ?? "").replace(/-/g, "").slice(-4)}`,
        reasonCode: String(g.reasonCode ?? ""),
        status: breakGlassChip(g.status),
        requestedAt: g.requestedAt ?? new Date().toISOString(),
        expiresAt: g.expiresAt ?? undefined,
      }),
    );
  }
  // Provider network (Phase 2b, US-018..021) — the Network Team's tenant-scoped directory (never provider-scoped
  // ABAC, so it sees the whole tenant network). Locations/contracts are per-provider reads; performance is
  // derived client-side from the directory (the network roll-up /metrics is not routed at the gateway).
  async providerList() {
    const r = (await getRaw(`/providers`)) as any[];
    return (Array.isArray(r) ? r : []).map((p: any) =>
      parseOr(zProviderSummary, {
        id: p.providerId,
        code: String(p.providerCode ?? ""),
        legalName: String(p.legalName ?? ""),
        providerType: String(p.providerTypeLabel ?? p.providerType ?? ""),
        status: providerStatusChip(p.status),
        onboardingState: String(p.onboardingState ?? ""),
      }),
    );
  }
  async providerLocations(providerId: string) {
    const r = (await getRaw(`/providers/${encodeURIComponent(providerId)}/locations`)) as any[];
    return (Array.isArray(r) ? r : []).map((l: any) =>
      parseOr(zProviderLocation, {
        id: l.locationId,
        name: String(l.name ?? ""),
        governorate: l.governorate ?? undefined,
        address: l.address ?? undefined,
        isPrimary: Boolean(l.isPrimary),
      }),
    );
  }
  async providerContracts(providerId: string) {
    const r = (await getRaw(`/providers/${encodeURIComponent(providerId)}/contracts`)) as any[];
    return (Array.isArray(r) ? r : []).map((c: any) =>
      parseOr(zProviderContract, {
        id: c.contractId,
        contractNo: String(c.contractNo ?? ""),
        status: providerStatusChip(c.status),
        effectiveFrom: String(c.effectiveFrom ?? ""),
        effectiveTo: c.effectiveTo ?? undefined,
        serviceLines: Number(c.serviceLines ?? 0),
      }),
    );
  }
  async createProvider(input: CreateProviderInput, idempotencyKey?: string) {
    const r = (await postRaw(`/providers`, { providerCode: input.code, legalName: input.legalName, providerType: input.providerType }, idempotencyKey)) as any;
    return parseOr(zProviderSummary, {
      id: required(r?.providerId, "provider.providerId"),
      code: String(r?.providerCode ?? input.code),
      legalName: String(r?.legalName ?? input.legalName),
      providerType: String(r?.providerTypeLabel ?? r?.providerType ?? input.providerType),
      status: providerStatusChip(r?.status ?? "Suspended"),
      onboardingState: String(r?.onboardingState ?? "Draft"),
    });
  }

  // ---- Practitioners (Phase 14.5, design 37 §4) -----------------------------------------------------------
  async branchLabels(branchIds: readonly string[]) {
    const wanted = [...new Set(branchIds.filter(Boolean))];
    const out = new Map<string, string>();
    if (wanted.length === 0) return out;
    try {
      const rows = (await getRaw(`/branch-labels?branchIds=${encodeURIComponent(wanted.join(","))}`)) as any[];
      for (const row of rows ?? []) {
        if (row?.branchId && row?.nameEn) out.set(String(row.branchId), String(row.nameEn));
      }
    } catch {
      // Unnamed is better than no table — the caller falls back to showing nothing for the branch.
    }
    return out;
  }

  async icdTitles(codes: readonly string[]) {
    const wanted = [...new Set(codes.filter(Boolean))];
    const out = new Map<string, string>();
    await Promise.all(wanted.map(async (code) => {
      const cached = icdTitleCache.get(code);
      if (cached !== undefined) {
        if (cached) out.set(code, cached);
        return;
      }
      try {
        const r = (await getRaw(`/icd-codes/${encodeURIComponent(code)}`)) as any;
        const title = typeof r?.title === "string" ? r.title : "";
        // Cached either way. A code masterdata does not carry is a stable fact for this session, and
        // re-asking for it on every render of every section is a request per row per paint.
        icdTitleCache.set(code, title);
        if (title) out.set(code, title);
      } catch {
        // A reference lookup must never take a clinical section down with it. The caller shows the code,
        // which is what it showed before this method existed.
        icdTitleCache.set(code, "");
      }
    }));
    return out;
  }

  async specialties() {
    const r = (await getRaw(`/specialties`)) as any[];
    return (Array.isArray(r) ? r : []).map((s: any) =>
      parseOr(zSpecialty, {
        code: String(s?.specialtyCode ?? ""),
        // Both sides are authored in the master data; falling back to the English name is better than an
        // empty Arabic label, which would render as a blank option in an Arabic session.
        name: { en: String(s?.nameEn ?? s?.specialtyCode ?? ""), ar: String(s?.nameAr ?? s?.nameEn ?? "") },
        parentCode: s?.parentCode ?? undefined,
      }),
    );
  }

  async branches() {
    const r = (await getRaw(`/branches`)) as any[];
    return (Array.isArray(r) ? r : []).map((b: any) =>
      parseOr(zBranchSummary, {
        id: required(b?.branchId, "branch.branchId"),
        code: String(b?.branchCode ?? ""),
        name: { en: String(b?.nameEn ?? b?.branchCode ?? ""), ar: String(b?.nameAr ?? b?.nameEn ?? "") },
        city: b?.city ?? undefined,
        status: providerStatusChip(b?.status),
      }),
    );
  }

  async practitioners(filter?: { branchId?: string; specialtyCode?: string; type?: string }) {
    const qs = new URLSearchParams();
    if (filter?.branchId) qs.set("branchId", filter.branchId);
    if (filter?.specialtyCode) qs.set("specialtyCode", filter.specialtyCode);
    if (filter?.type) qs.set("type", filter.type);
    const r = (await getRaw(`/practitioners${qs.toString() ? `?${qs}` : ""}`)) as any[];
    return (Array.isArray(r) ? r : []).map(toPractitioner);
  }

  /**
   * Create a doctor across the THREE endpoints 14.5 exposes — practitioner, specialty, one call per clinic.
   *
   * These are not one transaction and there is no endpoint that makes them one. So the failure modes are
   * split deliberately:
   *
   *   • the practitioner POST failing REJECTS — nothing was created, the form keeps its contents, and the
   *     idempotency key makes the operator's retry safe.
   *   • an attachment failing RESOLVES, with the failure named in `incomplete`. The practitioner exists at
   *     that point; rejecting would tell the operator nothing was saved and invite a retry that 409s on the
   *     unique user_id index, and swallowing it would report a bookable doctor who has no specialty and
   *     therefore appears in no booking picker.
   *
   * The attachments run SEQUENTIALLY rather than in parallel: they are audited writes against one aggregate,
   * and a partial result is far easier to act on when the order it was attempted in is the order it is
   * reported in.
   */
  async createPractitioner(input: CreatePractitionerInput, idempotencyKey?: string) {
    const created = (await postRaw(`/practitioners`, {
      userId: input.userId,
      practitionerType: input.practitionerType,
      fullNameEn: input.fullNameEn,
      fullNameAr: input.fullNameAr,
      licenseNo: input.licenseNo ?? null,
      licenseExpiry: input.licenseExpiry ?? null,
    }, idempotencyKey)) as any;

    const id = String(created?.practitionerId ?? "");
    const incomplete: PractitionerAttachFailure[] = [];
    const attachedSpecialties: string[] = [];
    const attachedBranches: string[] = [];

    try {
      await postRaw(`/practitioners/${encodeURIComponent(id)}/specialties`,
        { specialtyCode: input.primarySpecialtyCode, isPrimary: true });
      attachedSpecialties.push(input.primarySpecialtyCode);
    } catch (e) {
      incomplete.push({ step: "specialty", ref: input.primarySpecialtyCode, reason: attachReason(e) });
    }

    const validFrom = cairoToday();
    for (const branchId of input.branchIds) {
      try {
        await postRaw(`/practitioners/${encodeURIComponent(id)}/branches`,
          { branchId, validFrom, validTo: null });
        attachedBranches.push(branchId);
      } catch (e) {
        incomplete.push({ step: "branch", ref: branchId, reason: attachReason(e) });
      }
    }

    // Built from what ACTUALLY attached, not from the request. The create response was written before any of
    // the calls above ran, so it reports empty specialty/branch lists for every practitioner; echoing the
    // input instead would draw a complete record on screen for one that is missing half its assignments.
    return parseOr(zPractitionerCreated, {
      practitioner: {
        ...toPractitioner(created),
        specialties: attachedSpecialties,
        primarySpecialty: attachedSpecialties[0],
        branches: attachedBranches,
      },
      incomplete,
    });
  }

  async assignSpecialty(practitionerId: string, specialtyCode: string) {
    await postRaw(`/practitioners/${encodeURIComponent(practitionerId)}/specialties`, { specialtyCode, isPrimary: false });
  }
  async setPrimarySpecialty(practitionerId: string, specialtyCode: string) {
    await postRaw(`/practitioners/${encodeURIComponent(practitionerId)}/specialties/primary`, { specialtyCode, isPrimary: true });
  }
  async revokeSpecialty(practitionerId: string, specialtyCode: string) {
    await postRaw(`/practitioners/${encodeURIComponent(practitionerId)}/specialties/revoke`, { specialtyCode });
  }
  async assignPractitionerBranch(practitionerId: string, branchId: string) {
    await postRaw(`/practitioners/${encodeURIComponent(practitionerId)}/branches`,
      { branchId, validFrom: cairoToday(), validTo: null });
  }
  async revokePractitionerBranch(practitionerId: string, branchId: string) {
    await postRaw(`/practitioners/${encodeURIComponent(practitionerId)}/branches/revoke`, { branchId });
  }
  async setPractitionerStatus(practitionerId: string, status: string, reason: string) {
    await postRaw(`/practitioners/${encodeURIComponent(practitionerId)}/status`, { status, reason });
  }

  async appointmentDays(providerId: string, locationId: string, from: string, to: string, doctorId?: string) {
    const qs = new URLSearchParams({ providerId, locationId, from, to });
    if (doctorId) qs.set("doctorId", doctorId);
    const r = (await getRaw(`/appointment-days?${qs}`)) as any[];
    return (Array.isArray(r) ? r : []).map((d: any) =>
      parseOr(zAppointmentDay, { day: String(d?.day ?? ""), openSlots: Number(d?.openSlots ?? 0) }),
    );
  }

  async appointmentCounts(date?: string) {
    const qs = date ? `?date=${encodeURIComponent(date)}` : "";
    const r = (await getRaw(`/appointments/summary${qs}`)) as any;
    return parseOr(zAppointmentCounts, {
      total: Number(r?.total ?? 0),
      checkedIn: Number(r?.checkedIn ?? 0),
      noShow: Number(r?.noShow ?? 0),
    });
  }

  async doctorAvailability(branchId?: string) {
    const qs = branchId ? `?branchId=${encodeURIComponent(branchId)}` : "";
    const r = (await getRaw(`/booking/doctor-availability${qs}`)) as any[];
    return (Array.isArray(r) ? r : []).map((d: any) =>
      parseOr(zDoctorAvailability, {
        doctorId: required(d?.doctorId, "doctorAvailability.doctorId"),
        branchId: d?.branchId ?? undefined,
        openSlots: Number(d?.openSlots ?? 0),
        nextSlotStart: String(d?.nextSlotStart ?? ""),
      }),
    );
  }

  // Beneficiary management (Phase 1, US-001..005) — the registry: register, search/manage, status/reactivation.
  // Min-necessary identity projection (name + member no + identifiers + status), never clinical data.
  // Patient profile (Phase 20, design 39). NOTE what this method does NOT do: it applies no filtering, maps no
  // fields and drops nothing. The payload arrives already projected to the caller's role, and re-shaping it
  // here would be the client-side filtering the whole feature exists to avoid — so the response is parsed and
  // handed on exactly as received.
  async patientProfile(beneficiaryId: string, sections?: ProfileSectionKey[]) {
    const qs = sections?.length ? `?sections=${encodeURIComponent(sections.join(","))}` : "";
    const r = await getRaw(`/patients/${encodeURIComponent(beneficiaryId)}/profile${qs}`);
    return parseOr(zPatientProfile, r);
  }

  // The clipboard block is generated SERVER-SIDE from the served projection and this call is what writes the
  // CallSummaryCopied audit event. Assembling the text in the browser would both bypass the audit and risk
  // including a field the projection dropped.
  async copyCallSummaries(beneficiaryId: string, callRefs: string[]) {
    const r = await postRaw(
      `/beneficiaries/${encodeURIComponent(beneficiaryId)}/call-interactions/copy`, { callRefs });
    return parseOr(zCopySummariesResult, r);
  }

  async profileSummary(beneficiaryId: string) {
    const r = await getRaw(`/patients/${encodeURIComponent(beneficiaryId)}/profile/summary`);
    return parseOr(zProfileExportSummary, r);
  }

  async beneficiarySearch(query: { name?: string; status?: string }) {
    const qs = new URLSearchParams();
    if (query.name) qs.set("name", query.name);
    if (query.status) qs.set("status", query.status);
    const r = (await getRaw(`/beneficiaries${qs.toString() ? `?${qs}` : ""}`)) as any;
    const items: any[] = r?.items ?? (Array.isArray(r) ? r : []);
    return items.map((b: any) =>
      parseOr(zBeneficiaryRow, {
        id: b.beneficiaryId,
        memberNo: b.memberNo ?? undefined,
        givenName: String(b.givenName ?? ""),
        familyName: String(b.familyName ?? ""),
        status: beneficiaryStatusChip(b.status),
        statusRaw: String(b.status ?? ""),
        identifiers: (b.identifiers ?? []).map((i: any) => ({ type: String(i.type ?? ""), value: String(i.value ?? ""), isPrimary: Boolean(i.isPrimary) })),
      }),
    );
  }
  async registerBeneficiary(input: RegisterBeneficiaryInput, idempotencyKey?: string) {
    // 18.D1: the form's per-instance key wins. The content-derived fallback stays for callers that do not
    // supply one — the CARD is what makes two submissions the same registration, and it is mandatory, so it
    // is a better fallback key than the identifier the old form keyed on.
    const idem = idempotencyKey ?? `reg:card:${input.cardNumber}`;
    // Flat and form-shaped, matching patient-service's RegisterRequest. The domain's collection shape
    // (identifiers[], contacts[]) is deliberately NOT exposed here: the form has one of each, and offering
    // the client a list only invites it to send two.
    const body = {
      cardNumber: input.cardNumber,
      givenName: input.givenName,
      middleName: input.middleName || null,
      familyName: input.familyName,
      birthDate: input.birthDate || null,
      birthDateIsApproximate: input.approximateBirthDate ?? false,
      sex: input.sex,
      nationalityCode: input.nationalityCode,
      identifierType: input.identifierType,
      identifierValue: input.identifierValue,
      phone: input.phone,
      individualNo: input.individualNo || null,
      caseNo: input.caseNo || null,
      enrolment: {
        planId: input.enrolment.planId,
        networkTierId: input.enrolment.networkTierId,
        contributionPercent: input.enrolment.contributionPercent,
        defaultBranchId: input.enrolment.defaultBranchId || null,
      },
      notes: (input.notes ?? []).map((n) => ({ slot: n.slot, value: n.value })),
    };
    const r = (await postRaw(`/beneficiaries`, body, idem)) as any;
    return parseOr(zRegisterResult, {
      id: required(r?.beneficiaryId, "beneficiary.beneficiaryId"),
      memberNo: r?.memberNo ?? undefined,
      status: beneficiaryStatusChip(r?.status ?? "Pending"),
    });
  }
  async changeBeneficiaryStatus(id: string, toStatus: string, reason: string) {
    const r = (await postRaw(`/beneficiaries/${encodeURIComponent(id)}/status`, { toStatus, reason })) as any;
    return parseOr(zStatusChangeResult, { id: r?.beneficiaryId ?? id, status: beneficiaryStatusChip(r?.status ?? toStatus) });
  }

  // Registration approval workflow (US-003). The worklist row carries the beneficiary through the same
  // field-projected disclosure the directory search uses, so the mapping tolerates classes the caller's
  // role cannot read.
  async registrationWorklist(pageSize = 100) {
    // The server clamps pageSize to 100. Asking for the maximum in one request loads the OLDEST 100
    // applications, which — the queue being ordered oldest-first — is exactly the work at the front of it.
    // `total` comes back regardless, so the screen can say when there is more behind this page rather than
    // implying the queue is 100 long. Paging the server in a loop was the alternative and it buys nothing: a
    // supervisor does not work application 400 before application 4.
    const r = (await getRaw(`/registrations?pageSize=${pageSize}`)) as any;
    const items: any[] = r?.items ?? [];
    const mapped = items.map((it: any) => {
      const b = it?.beneficiary ?? {};
      const reg = it?.registration;
      return parseOr(zRegistrationWorkItem, {
        beneficiary: {
          id: b.beneficiaryId,
          memberNo: b.memberNo ?? undefined,
          cardNumber: b.cardNumber ?? undefined,
          givenName: String(b.givenName ?? ""),
          middleName: b.middleName ?? undefined,
          familyName: String(b.familyName ?? ""),
          status: beneficiaryStatusChip(b.status),
          statusRaw: String(b.status ?? ""),
          identifiers: (b.identifiers ?? []).map((i: any) => ({ type: String(i.type ?? ""), value: String(i.value ?? ""), isPrimary: Boolean(i.isPrimary) })),
          // Field-projected on the server: a key that is absent was withheld from this role, and stays
          // undefined here rather than being defaulted into a value nobody disclosed.
          birthDate: b.birthDate ?? undefined,
          birthDateIsApproximate: b.birthDateIsApproximate ?? undefined,
          sex: b.sex ?? undefined,
          nationalityCode: b.nationalityCode ?? undefined,
          individualNo: b.individualNo ?? undefined,
          caseNo: b.caseNo ?? undefined,
          contacts: b.contacts ?? undefined,
        },
        registration: reg
          ? {
              id: reg.registrationId,
              status: String(reg.status ?? "Pending"),
              documentsVerified: reg.documentsVerified === true,
              coverageBound: reg.coverageBound === true,
              notes: reg.notes ?? null,
              createdAt: String(reg.createdAt ?? ""),
              createdBy: reg.createdBy ?? null,
              createdByName: reg.createdByName ?? null,
              updatedAt: reg.updatedAt ?? undefined,
              threadCount: Number(reg.threadCount ?? 0),
              enrolment: reg.enrolment
                ? {
                    planId: reg.enrolment.planId,
                    networkTierId: reg.enrolment.networkTierId,
                    contributionPercent: Number(reg.enrolment.contributionPercent ?? 0),
                    defaultBranchId: reg.enrolment.defaultBranchId ?? undefined,
                  }
                : null,
              standingNotes: (reg.standingNotes ?? []).map((n: any) => ({
                slot: Number(n.slot),
                labelEn: String(n.labelEn ?? ""),
                labelAr: String(n.labelAr ?? ""),
                visibility: n.visibility === "Clinical" ? "Clinical" : "Administrative",
                value: n.value ?? null,
                withheld: n.withheld === true,
              })),
            }
          : null,
      });
    });
    return parseOr(zRegistrationWorklistPage, {
      items: mapped,
      // Falls back to the loaded count rather than 0: a server that predates the field would otherwise make
      // the pager claim an empty queue underneath a full table.
      total: Number.isFinite(r?.total) ? Number(r.total) : mapped.length,
    });
  }
  async registrationThread(id: string) {
    const r = (await getRaw(`/registrations/${encodeURIComponent(id)}/thread`)) as any[];
    return (r ?? []).map((e: any) =>
      parseOr(zRegistrationThreadEntry, {
        id: e.entryId,
        kind: e.kind === "Reply" ? "Reply" : "Decision",
        decision: e.decision ?? null,
        body: String(e.body ?? ""),
        authorName: e.authorName ?? null,
        authorRole: e.authorRole ?? null,
        createdAt: String(e.createdAt ?? ""),
      }));
  }
  async replyToRegistration(id: string, body: string) {
    const e = (await postRaw(`/registrations/${encodeURIComponent(id)}/thread`, { body })) as any;
    return parseOr(zRegistrationThreadEntry, {
      id: e?.entryId,
      kind: "Reply",
      decision: null,
      body: String(e?.body ?? body),
      authorName: e?.authorName ?? null,
      authorRole: e?.authorRole ?? null,
      createdAt: String(e?.createdAt ?? new Date().toISOString()),
    });
  }
  async beneficiary(id: string) {
    const b = (await getRaw(`/beneficiaries/${encodeURIComponent(id)}`)) as any;
    // The SAME field-projected shape the worklist rows are built from, mapped once — an absent key means the
    // server withheld it from this role, and stays undefined rather than being defaulted into a value nobody
    // disclosed.
    return parseOr(zBeneficiaryRow, {
      id: b?.beneficiaryId ?? id,
      memberNo: b?.memberNo ?? undefined,
      cardNumber: b?.cardNumber ?? undefined,
      givenName: String(b?.givenName ?? ""),
      middleName: b?.middleName ?? undefined,
      familyName: String(b?.familyName ?? ""),
      status: beneficiaryStatusChip(b?.status),
      statusRaw: String(b?.status ?? ""),
      identifiers: (b?.identifiers ?? []).map((i: any) => ({ type: String(i.type ?? ""), value: String(i.value ?? ""), isPrimary: Boolean(i.isPrimary) })),
      birthDate: b?.birthDate ?? undefined,
      birthDateIsApproximate: b?.birthDateIsApproximate ?? undefined,
      sex: b?.sex ?? undefined,
      nationalityCode: b?.nationalityCode ?? undefined,
      individualNo: b?.individualNo ?? undefined,
      caseNo: b?.caseNo ?? undefined,
      contacts: b?.contacts ?? undefined,
    });
  }
  async updateBeneficiary(id: string, edit: BeneficiaryEdit) {
    const r = (await patchRaw(`/beneficiaries/${encodeURIComponent(id)}`, edit)) as any;
    return { changed: Array.isArray(r?.changed) ? (r.changed as string[]) : [] };
  }
  async beneficiaryDocuments(beneficiaryId: string) {
    const r = (await getRaw(`/beneficiaries/${encodeURIComponent(beneficiaryId)}/documents`)) as any[];
    return (r ?? []).map((d: any) => {
      // document-service returns every VERSION; the review only needs the current one's provenance.
      const versions: any[] = d?.versions ?? [];
      const current = versions.find((v) => v.versionNo === d.currentVersionNo) ?? versions[versions.length - 1];
      return parseOr(zBeneficiaryDocument, {
        id: d.documentId,
        docType: String(d.docType ?? ""),
        classification: String(d.classification ?? ""),
        uploadedAt: current?.uploadedAt ?? null,
        uploadedBy: current?.uploadedBy ?? null,
      });
    });
  }
  async createRegistration(beneficiaryId: string, idempotencyKey?: string) {
    await postRaw(`/registrations`, { beneficiaryId }, idempotencyKey ?? `regwf:${beneficiaryId}`);
  }
  async setRegistrationChecks(id: string, checks: { documentsVerified?: boolean; coverageBound?: boolean }) {
    await patchRaw(`/registrations/${encodeURIComponent(id)}`, checks);
  }
  async decideRegistration(id: string, decision: "Approve" | "RequestInfo" | "Reject", notes?: string) {
    const r = (await postRaw(`/registrations/${encodeURIComponent(id)}/decision`, { decision, notes: notes ?? null })) as any;
    return parseOr(zRegistrationDecisionResult, { status: String(r?.status ?? ""), memberNo: r?.memberNo ?? undefined });
  }
  async decideRegistrations(ids: readonly string[], decision: "Approve" | "RequestInfo" | "Reject", notes?: string) {
    const outcomes: BulkDecisionOutcome[] = [];
    // SEQUENTIAL, not Promise.all. Each decision that approves issues a member number from a shared counter
    // and enrols the member through the outbox; firing twenty at once turns a queue the server serialises
    // anyway into twenty simultaneous transactions, and makes the failure that matters — one row refused —
    // arrive interleaved with nineteen successes in no particular order.
    for (const id of ids) {
      try {
        const r = await this.decideRegistration(id, decision, notes);
        outcomes.push({ registrationId: id, ok: true, memberNo: r.memberNo });
      } catch (e) {
        // The server's own reason, kept per row. "cannot approve: no policy/coverage is bound" tells the
        // supervisor what to do next; "bulk decision failed" tells them nothing.
        outcomes.push({
          registrationId: id,
          ok: false,
          error: e instanceof ApiError ? e.reason : String((e as Error)?.message ?? e),
        });
      }
    }
    return outcomes;
  }

  // Governance reads (Phase 8b.2) — the master-data versions + typed system-config currently in force. Reference
  // configuration, not PHI; every admin read is audited server-side.
  /**
   * Append a new effective-dated version of a master-data code.
   *
   * <p>A POST, not a PUT, because it CREATES a version rather than replacing one — the prior version's window
   * closes and history stays resolvable. The 403 for a non-clinical system is not handled here: it carries a
   * problem type the screen renders, and swallowing it would leave a supervisor staring at a form that did
   * nothing.</p>
   */
  async adminMasterDataUpsert(edit: MasterDataEdit) {
    const r = (await postRaw(`/admin/master-data`, {
      system: edit.system,
      code: edit.code,
      attributes: edit.attributes,
      rationale: edit.rationale,
      retired: edit.retired,
    })) as any;
    return {
      id: String(required(r?.versionId, "masterDataVersion.versionId")),
      code: String(r?.code ?? edit.code),
      versionNo: Number(r?.versionNo ?? 0),
    };
  }

  /** The version in force at an instant — the "what did this code mean then" read behind the diff. */
  async adminMasterDataAsOf(system: string, code: string, at: string) {
    const r = (await getRaw(
      `/admin/master-data/${encodeURIComponent(system)}/${encodeURIComponent(code)}/as-of?at=${encodeURIComponent(at)}`,
    )) as any;
    // `attributesJson` is a STRING on the wire — the server stores the snapshot as JSON text. Parsed here so
    // the screen never has to know that, and defaulted to {} rather than throwing: a malformed snapshot is a
    // version that cannot be diffed, not a page that cannot load.
    let attributes: Record<string, unknown> = {};
    try {
      attributes = JSON.parse(String(r?.attributesJson ?? "{}"));
    } catch { attributes = {}; }
    return parseOr(zMasterDataAsOf, {
      id: required(r?.versionId, "masterDataVersion.versionId"),
      versionNo: Number(r?.versionNo ?? 0),
      attributes,
      effectiveFrom: r?.effectiveFrom ?? new Date().toISOString(),
      effectiveTo: r?.effectiveTo ?? null,
    });
  }

  /** The tenant's document validity policy — every kind answered, configured or not (ADR-0035 §6). */
  async adminDocumentValidity() {
    return parseOr(zDocumentValidityView, await getRaw(`/admin/document-validity`));
  }

  /**
   * Set a cadence, thresholds, or both.
   *
   * <p>A PUT with only the field being changed: omitting one leaves it untouched. Sending both every time
   * would make an untouched threshold list a fresh write with the supervisor's name on it, which is a
   * decision they did not make.</p>
   */
  async adminSetDocumentValidity(req: SetDocumentValidity) {
    await putRaw(`/admin/document-validity`, {
      kind: req.kind,
      ...(req.days === undefined ? {} : { days: req.days }),
      ...(req.warnDays === undefined ? {} : { warnDays: req.warnDays }),
    });
  }

  /** The engine's rules, plus the queues a routing rule may target (ADR-0035 §5). */
  async approvalRules(family?: "Routing" | "Sla" | "Preauth" | "AutoApprove") {
    const r = (await getRaw(`/approval-rules/${family ? `?family=${family}` : ""}`)) as any;
    return parseOr(zApprovalRuleList, {
      rules: (r?.rules ?? []).map((x: any) => ({
        id: x.ruleId ?? x.id ?? "",
        family: x.family,
        priority: Number(x.priority ?? 0),
        predicate: String(x.predicate ?? "{}"),
        action: String(x.action ?? "{}"),
        effectiveFrom: x.effectiveFrom,
        effectiveTo: x.effectiveTo ?? null,
        versionNo: Number(x.versionNo ?? 1),
        enabled: Boolean(x.enabled),
        authoredBy: String(x.authoredBy ?? ""),
        rationale: String(x.rationale ?? ""),
      })),
      queues: r?.queues ?? [],
      defaultQueue: String(r?.defaultQueue ?? "default"),
    });
  }

  /**
   * Publish a rule.
   *
   * <p>A POST because it CREATES a version. Supplying `supersedesRuleId` closes the prior version's window
   * rather than editing it, so a request routed last Tuesday stays explainable against last Tuesday's rules.</p>
   */
  async saveApprovalRule(req: SaveApprovalRule) {
    const r = (await postRaw(`/approval-rules/`, req)) as any;
    return { id: String(required(r?.ruleId, "approvalRule.ruleId")), versionNo: Number(r?.versionNo ?? 1) };
  }

  /** The tenant's auto-decision kill switch. A tenant that never touched it reads `enabled: false`. */
  async autoDecisionSwitch() {
    return parseOr(zAutoDecisionSwitch, await getRaw(`/approval-rules/auto-decision`));
  }

  /** Turn auto-decision on or off. A reason is required in BOTH directions. */
  async setAutoDecision(req: SetAutoDecision) {
    return parseOr(zAutoDecisionSwitch, await putRaw(`/approval-rules/auto-decision`, req));
  }

  async adminMasterData() {
    const r = (await getRaw(`/admin/master-data`)) as any[];
    return (Array.isArray(r) ? r : []).map((v: any) =>
      parseOr(zMasterDataVersion, {
        id: v.versionId ?? v.id,
        system: String(v.system ?? ""),
        code: String(v.code ?? ""),
        versionNo: Number(v.versionNo ?? 0),
        retired: Boolean(v.retired),
        effectiveFrom: v.effectiveFrom ?? new Date().toISOString(),
        rationale: v.rationale ?? undefined,
      }),
    );
  }
  async adminSystemConfig() {
    const r = (await getRaw(`/admin/system-config`)) as any[];
    return (Array.isArray(r) ? r : []).map((c: any) =>
      parseOr(zSystemConfigEntry, {
        id: c.configId ?? c.id,
        tenantId: String(c.tenantId ?? "*"),
        key: String(c.key ?? ""),
        type: String(c.type ?? ""),
        value: String(c.value ?? ""),
        versionNo: Number(c.versionNo ?? 0),
      }),
    );
  }

  /**
   * Set one configuration value. The server validates the value AGAINST the declared type and answers 422
   * with the reason (`not-an-integer`, `not-a-duration`) when it does not parse, which the editor renders
   * beside the field rather than as a toast.
   */
  async adminSystemConfigSet(edit: SystemConfigEdit) {
    const r = (await putRaw(`/admin/system-config`, {
      key: edit.key,
      valueType: edit.type,
      value: edit.value,
      tenant: edit.tenantId ?? null,
    })) as any;
    return parseOr(zSystemConfigEntry, {
      id: r?.configId ?? r?.id,
      // The response carries no tenant — the server pinned it from the token, so the value we sent (or the
      // caller's own tenant when we sent none) is the only honest thing to report back.
      tenantId: edit.tenantId ?? "",
      key: String(r?.key ?? edit.key),
      type: String(r?.valueType ?? r?.type ?? edit.type),
      // The CANONICAL value, not the one typed: the server normalises `TRUE` to `true` and `1.50` to `1.5`,
      // and echoing the input back would show the administrator a value the platform is not using.
      value: String(r?.value ?? edit.value),
      versionNo: Number(r?.versionNo ?? 0),
    });
  }

  // ---- User & access model (Phase 21.6, design 40) -------------------------------------------------------
  //
  // Authority (memberships, overrides, effective set, sessions) comes from identity-service on the absolute
  // `/identity` path; reach (branch grants) and programme enablement come from admin-service under
  // `/api/v1`. The split is the service boundary, not an accident of routing — see design 40 §3.

  async memberships(tenant?: string, status?: string, query?: string) {
    const qs = new URLSearchParams();
    if (tenant) qs.set("tenant", tenant);
    if (status) qs.set("status", status);
    if (query) qs.set("query", query);
    const suffix = qs.toString() ? `?${qs}` : "";
    const r = (await getAbsolute(`${GATEWAY_BASE}/identity/admin/memberships${suffix}`)) as any[];
    return (Array.isArray(r) ? r : []).map((m: any) => parseOr(zMembershipRow, membershipRowOf(m)));
  }

  async membership(membershipId: string) {
    const m = (await getAbsolute(
      `${GATEWAY_BASE}/identity/admin/memberships/${encodeURIComponent(membershipId)}`,
    )) as any;
    return parseOr(zMembershipDetail, {
      ...membershipRowOf(m),
      providerId: m?.providerId ?? null,
      homeBranchId: m?.homeBranchId ?? null,
      overrides: (Array.isArray(m?.overrides) ? m.overrides : []).map((o: any) => ({
        id: String(o.id),
        scope: String(o.scope ?? ""),
        effect: o.effect === "Deny" ? "Deny" : "Allow",
        reason: String(o.reason ?? ""),
        grantedBy: o.grantedBy ?? null,
        validUntil: o.validUntil ?? null,
        expired: o.expired === true,
      })),
    });
  }

  async setMembershipOverride(
    membershipId: string,
    input: { scopeKey: string; effect: "Allow" | "Deny"; reason: string; validUntil: string | null },
  ) {
    await postAbsolute(
      `${GATEWAY_BASE}/identity/admin/memberships/${encodeURIComponent(membershipId)}/overrides`,
      input,
    );
  }

  async removeMembershipOverride(membershipId: string, scopeKey: string) {
    // The key is a path SEGMENT and it contains colons (`orders:read`), so the encode is load-bearing rather
    // than defensive — an unencoded key is a different URL from the one the route matches.
    await deleteAbsolute(
      `${GATEWAY_BASE}/identity/admin/memberships/${encodeURIComponent(membershipId)}/overrides/${encodeURIComponent(scopeKey)}`,
    );
  }

  /**
   * Mode 2, rendered verbatim.
   *
   * The server returns the granted set plus the deprecation pointers; the DENIED keys are the ones that make
   * the screen useful ("orders:read — denied by override, because X" explains an absence that otherwise
   * looks like a broken role), so they are composed here from the membership's Deny overrides rather than
   * recomputed: this maps two server answers together, it does not re-run the algebra.
   */
  async effectiveAccess(membershipId: string) {
    const [set, detail] = await Promise.all([
      getAbsolute(`${GATEWAY_BASE}/identity/admin/memberships/${encodeURIComponent(membershipId)}/effective`) as Promise<any>,
      this.membership(membershipId),
    ]);

    const deprecated = new Map<string, string | null>(
      (Array.isArray(set?.deprecated) ? set.deprecated : []).map((d: any) => [String(d.key), d.replacedBy ?? null]),
    );
    const allows = new Map(detail.overrides.filter((o) => o.effect === "Allow" && !o.expired).map((o) => [o.scope, o]));
    const denies = detail.overrides.filter((o) => o.effect === "Deny" && !o.expired);
    // Which keys the A1 short-circuit accounts for, as the SERVER marks them. Without this a key the
    // platform-admin flag granted would render "from role", which is the one provenance error that matters
    // on this screen: A1's boundary is exactly what an administrator opens it to see.
    const platformAdmin = new Set<string>(
      (Array.isArray(set?.platformAdminKeys) ? set.platformAdminKeys : []).map(String),
    );

    const granted = (Array.isArray(set?.scopes) ? set.scopes : []).map((k: any) => {
      const key = String(k);
      const allow = allows.get(key);
      // An explicit Allow override outranks the flag as an EXPLANATION: someone wrote it down, with a
      // reason, and that is the more useful thing to show a reviewer.
      const source = allow ? "override" : platformAdmin.has(key) && detail.isPlatformAdmin ? "platform-admin" : "role";
      return {
        key,
        source,
        via: allow ? (allow.grantedBy ?? undefined) : undefined,
        deprecated: deprecated.has(key) || undefined,
        replacedBy: deprecated.get(key) ?? undefined,
        reason: allow?.reason,
      };
    });

    // Listed, not filtered — an absence with a reason attached is the single most useful line here.
    const removed = denies.map((o) => ({
      key: o.scope,
      source: "denied" as const,
      via: o.grantedBy ?? undefined,
      reason: o.reason,
    }));

    return parseOr(zEffectiveAccess, { membershipId, keys: [...granted, ...removed] });
  }

  async branchScopeGrants(subject: string, tenant?: string) {
    const r = (await getRaw(
      `/admin/users/${encodeURIComponent(subject)}/branches${tenant ? `?tenant=${encodeURIComponent(tenant)}` : ""}`,
    )) as any[];
    return (Array.isArray(r) ? r : []).map((g: any) =>
      parseOr(zBranchScopeGrant, {
        grantId: String(g.assignmentId ?? g.grantId ?? g.id),
        branchId: String(required(g.branchId, "branchScopeGrant.branchId")),
        isHome: g.isHome === true || g.assignmentType === "Home",
        validFrom: String(g.validFrom ?? "").slice(0, 10),
        validUntil: g.validTo || g.validUntil ? String(g.validTo ?? g.validUntil).slice(0, 10) : null,
        grantedBy: g.grantedBy ?? null,
        grantedReason: g.grantedReason ?? null,
      }),
    );
  }

  async accessSessions(userId: string) {
    const r = (await getAbsolute(`${GATEWAY_BASE}/identity/admin/users/${encodeURIComponent(userId)}/sessions`)) as any[];
    return (Array.isArray(r) ? r : []).map((s: any) =>
      parseOr(zAccessSession, {
        sessionId: String(s.sessionId),
        // Enough to RECOGNISE a device, which is all the server sends (min-necessary session view).
        device: String(s.userAgent ?? s.device ?? "—"),
        createdAt: s.createdAt ?? new Date().toISOString(),
        lastSeenAt: s.lastSeenAt ?? null,
        current: s.current === true,
      }),
    );
  }

  async revokeAccessSession(userId: string, sessionId: string) {
    await deleteAbsolute(
      `${GATEWAY_BASE}/identity/admin/users/${encodeURIComponent(userId)}/sessions/${encodeURIComponent(sessionId)}`,
    );
  }

  async programEnablement(tenant: string) {
    const r = (await getRaw(`/admin/programs/${encodeURIComponent(tenant)}`)) as any;
    return parseOr(zProgramEnablement, {
      tenantId: String(r?.tenantId ?? tenant),
      features: (Array.isArray(r?.features) ? r.features : []).map((f: any) => ({
        key: String(f.key ?? ""),
        enabled: f.enabled === true,
        configured: f.configured === true,
        changedBy: f.changedBy ?? null,
        changedAt: f.changedAt ?? null,
      })),
      limits: (Array.isArray(r?.limits) ? r.limits : []).map((l: any) => ({
        key: String(l.key ?? ""),
        // `?? null` and NOT `?? 0`: null means unlimited for the cap and "not counted here" for the usage.
        // Coercing either to zero would state a fact the server never asserted.
        maxValue: l.maxValue ?? null,
        currentUsage: l.currentUsage ?? null,
        changedBy: l.changedBy ?? null,
        changedAt: l.changedAt ?? null,
      })),
    });
  }

  async setProgramFeature(tenant: string, key: string, enabled: boolean, reason: string) {
    await putRaw(`/admin/programs/${encodeURIComponent(tenant)}/features/${encodeURIComponent(key)}`, { enabled, reason });
  }

  async setProgramLimit(tenant: string, key: string, maxValue: number, reason: string) {
    await putRaw(`/admin/programs/${encodeURIComponent(tenant)}/limits/${encodeURIComponent(key)}`, { maxValue, reason });
  }
}

/** Shared mapping for the roster row and the detail's base, so the two cannot drift apart. */
function membershipRowOf(m: any) {
  const status = String(m?.status ?? "");
  return {
    membershipId: String(m?.membershipId),
    userId: String(m?.userId),
    username: String(m?.username ?? ""),
    displayName: String(m?.displayName ?? m?.username ?? ""),
    tenantId: String(required(m?.tenantId, "membership.tenantId")),
    status: membershipStatusChip(status),
    roles: (Array.isArray(m?.roles) ? m.roles : []).map((r: any) => ({
      name: String(r.name ?? ""),
      level: r.level ?? null,
    })),
    level: Number(m?.level ?? 0),
    isPlatformAdmin: m?.isPlatformAdmin === true,
    overrideCount: Number(m?.overrideCount ?? 0),
    expiredOverrideCount: Number(m?.expiredOverrideCount ?? 0),
    activatedAt: m?.activatedAt ?? null,
    endedAt: m?.endedAt ?? null,
  };
}

/**
 * Membership status as a four-cue chip.
 *
 * Only Active is a usable principal (design 40 §1) and the other three are all "not right now" — but they
 * are shown as three DIFFERENT states rather than one "inactive", because the remedy differs: an Invited
 * membership needs the person to accept, a Suspended one needs an administrator, and an Ended one needs a
 * new membership entirely.
 */
function membershipStatusChip(status: string) {
  switch (status) {
    case "Active":
      return { kind: "ok" as const, label: { en: "Active", ar: "نشِطة" } };
    case "Invited":
      return { kind: "info" as const, label: { en: "Invited", ar: "مدعوّة" } };
    case "Suspended":
      return { kind: "warn" as const, label: { en: "Suspended", ar: "موقوفة" } };
    case "Ended":
      return { kind: "neu" as const, label: { en: "Ended", ar: "منتهية" } };
    default:
      return { kind: "neu" as const, label: { en: status || "Unknown", ar: status || "غير معروفة" } };
  }
}

/**
 * Draft lines → the wire shape the pharmacy API expects.
 *
 * `clientLineId` is how a finding and its acknowledgement name a line before the server has minted one.
 * `drugId` is the REAL uuid; sending anything else here is the defect this workspace was built to fix.
 */
function rxLines(lines: PrescriptionDraftLine[]) {
  return lines
    .filter((l) => l.drug !== null)
    .map((l) => ({
      drugId: l.drug!.drugId,
      dose: l.dose,
      route: "Oral",
      frequency: "Daily",
      quantityPrescribed: l.quantity,
      // 31.3 — what that number counts, snapshotted with it. The composer knows because it is what the
      // Quantity field is labelled with; the counter cannot know, and renders the figure alone.
      // `|| null`, not `?? null`: the draft carries "" for "nothing computed", and an empty string stored
      // against a quantity is a unit that exists and says nothing. Absent is the honest wire value.
      quantityUnit: l.quantityUnit || null,
      refillsAllowed: 0,
      durationDays: l.durationDays,
      clientLineId: l.lineId,
      // 29.6 — THE NUMBERS THE CHECKS RUN ON.
      //
      // `CreateRxLine` and `ValidateLine` have accepted these since 26.4 and nothing sent them, so the
      // Quantity check reported "no numeric dose, frequency and duration to compute a quantity from" and
      // the daily-dose rule had nothing to compare against — on every prescription this platform had
      // written. Two correct checks, unfed.
      doseAmount: l.doseAmount,
      doseUnit: l.drug!.prescribingUnit ?? null,
      timesPerDay: l.timesPerDay,
    }));
}

import {
  zAccessReviewCampaign,
  zAppointmentRow,
  zBookableClinic,
  zTimelineStep,
  zBookableSlot,
  zBookingResult,
  zApprovalItem,
  zApprovalReview,
  zBreakGlassGrant,
  zMasterDataVersion,
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
  zEligibilityHit,
  zEligibilityResult,
  zEncounter,
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
  zUtilizationView,
  type ConsumeRequest,
  type DecisionRequest,
  type DispenseRequest,
  type ExportRequest,
  type PlaceOrderRequest,
  type PrescribeRequest,
  type VitalInput,
  zIdentityUser,
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
} from "@mersal/contracts";
import type { BeneficiaryEdit, BookingRequest, BulkDecisionOutcome, CreatePractitionerInput, PractitionerAttachFailure } from "@mersal/contracts";
import type { ApiClient } from "./client";
import { ApiError, getRaw, postRaw, putRaw, patchRaw, postForm, parseOr, getAbsolute, postAbsolute, deleteAbsolute } from "./http";
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
/** Pre-format a numeric amount as the contract's display string, e.g. 12400 -> "EGP 12,400". */
/**
 * 18.D2 (audit R2 U7) — the API layer now returns a NUMBER; formatting happens at render.
 *
 * This used to build "EGP 12,400" with a hardcoded en-US locale, so the Arabic UI showed Western digits and
 * an English currency prefix. A pre-formatted string also cannot be summed, sorted numerically, or
 * re-localised when the user switches language mid-session.
 */
const money = (n: unknown) => { const v = Number(n ?? 0); return Number.isFinite(v) ? v : 0; };
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
/** Map member coverage status → the eligibility verdict the result card renders. */
const statusToVerdict = (status: unknown): "eligible" | "ineligible" | "review" => {
  const s = String(status ?? "").toLowerCase();
  if (s === "active") return "eligible";
  if (s === "blocked" || s === "expired") return "ineligible";
  return "review";
};
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

/** One practitioner row → the contract shape. Shared by the list and the create path. */
function toPractitioner(p: any) {
  return parseOr(zPractitioner, {
    id: p?.practitionerId ?? "",
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
/** Map a claim lifecycle status (36 §3) → a non-color StatusKind chip. */
const claimStatusChip = (s: unknown): { kind: "ok" | "info" | "part" | "warn" | "bad" | "neu"; label: { en: string; ar: string } } => {
  const k = String(s ?? "");
  const map: Record<string, { kind: "ok" | "info" | "part" | "warn" | "bad" | "neu"; label: { en: string; ar: string } }> = {
    Draft: { kind: "neu", label: { en: "Draft", ar: "مسودة" } },
    Submitted: { kind: "info", label: { en: "Submitted", ar: "مُقدّمة" } },
    UnderReview: { kind: "info", label: { en: "Under review", ar: "قيد المراجعة" } },
    Adjudicated: { kind: "ok", label: { en: "Adjudicated", ar: "تمت المراجعة" } },
    PartiallyApproved: { kind: "part", label: { en: "Partially approved", ar: "موافقة جزئية" } },
    Rejected: { kind: "bad", label: { en: "Rejected", ar: "مرفوضة" } },
    Settled: { kind: "ok", label: { en: "Settled", ar: "مُسوّاة" } },
    Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغاة" } },
  };
  return map[k] ?? { kind: "neu", label: { en: k || "—", ar: k || "—" } };
};
/** Map a reconciliation bucket (36 §7) → a non-color StatusKind chip. */
const reconBucketChip = (s: unknown): { kind: "ok" | "info" | "warn" | "bad" | "neu"; label: { en: string; ar: string } } => {
  const k = String(s ?? "");
  const map: Record<string, { kind: "ok" | "info" | "warn" | "bad" | "neu"; label: { en: string; ar: string } }> = {
    Matched: { kind: "ok", label: { en: "Matched", ar: "مطابقة" } },
    PriceVariance: { kind: "warn", label: { en: "Price variance", ar: "فرق سعر" } },
    BilledNotDelivered: { kind: "bad", label: { en: "Billed, not delivered", ar: "فوترة بلا تنفيذ" } },
    DeliveredNotBilled: { kind: "info", label: { en: "Delivered, not billed", ar: "تنفيذ بلا فوترة" } },
  };
  return map[k] ?? { kind: "neu", label: { en: k || "—", ar: k || "—" } };
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
/** Map a pharmacy prescription/line status → a resolved bilingual StatusKind for the dispensing queue. */
const rxStatus = (s: unknown) => {
  const k = String(s ?? "Approved");
  const map: Record<string, { kind: "ok" | "info" | "part" | "neu"; label: { en: string; ar: string } }> = {
    Approved: { kind: "info", label: { en: "Approved", ar: "معتمدة" } },
    Active: { kind: "info", label: { en: "Active", ar: "نشطة" } },
    PartiallyDispensed: { kind: "part", label: { en: "Partially dispensed", ar: "صُرفت جزئياً" } },
    Dispensed: { kind: "ok", label: { en: "Dispensed", ar: "صُرفت" } },
    Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغاة" } },
  };
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
    : { system: "ATC" as const, code: String(drugId ?? "").slice(0, 8), label: t("Medication", "دواء") };
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
  async searchEligibility(query: string) {
    const r = (await getRaw(`/reception/search?q=${encodeURIComponent(query)}`)) as any;
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
  async checkEligibility(beneficiaryId: string) {
    const c = receptionCards.get(String(beneficiaryId));
    const identity = c?.identity ?? {};
    const categories: string[] = c?.coverage ?? [];
    const limits: any[] = c?.remainingLimits ?? [];
    // Pick a monetary remaining-limit (annual cap) for the coverage summary, if the card carries one.
    const cap = limits.find((l) => /amount|annual/i.test(String(l.limitType)));
    const active = String(identity.status ?? "").toLowerCase() === "active";
    return parseOr(zEligibilityResult, {
      verdict: statusToVerdict(identity.status),
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
            annualCapRemaining: cap ? money(cap.remaining) : undefined,
          }
        : null,
      visitGate: active
        ? { allowed: true }
        : { allowed: false, reason: { en: "Coverage not active — refer to eligibility desk.", ar: "التغطية غير فعّالة — يُرجى مراجعة مكتب الأهلية." } },
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
        noteAt: a.noteAt ?? null,
        beneficiaryName: a.beneficiaryName ?? null,
        doctorId: a.doctorId ?? null,
        needsReassignment: a.needsReassignment === true,
        providerId: a.providerId ?? null,
        locationId: a.locationId ?? null,
      }),
    );
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
    const r = (await getRaw(`/appointments/${encodeURIComponent(appointmentId)}/timeline`)) as any[];
    const steps = r ?? [];

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
      }),
    );
  }

  async startVisit(appointmentId: string, beneficiaryId: string) {
    // POST /encounters is where the CheckedIn + assigned-doctor rules are enforced, so starting a visit goes
    // through it rather than through a UI-only shortcut.
    const r = (await postRaw("/encounters", { beneficiaryId, appointmentId }, crypto.randomUUID())) as any;
    return { encounterId: String(r?.encounterId ?? "") };
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
      id: r?.appointmentId ?? "",
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
    return (r ?? []).map((e: any) =>
      parseOr(zPatientListItem, {
        id: e.encounterId,
        beneficiaryId: String(e.beneficiaryId ?? ""),
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
      }),
    );
  }
  async getEncounter(encounterId: string) {
    const r = (await getRaw(`/encounters/${encodeURIComponent(encounterId)}/clinical`)) as any;
    const e = r?.encounter ?? {};
    // Cache the raw beneficiaryId so downstream write actions (place order / prescribe) can address the
    // orders/pharmacy services, which key on the beneficiary — the doctor UI itself only ever shows the mask.
    if (e.beneficiaryId) encounterBeneficiary.set(encounterId, String(e.beneficiaryId));
    const note = (r?.notes ?? [])[0] ?? {};
    const vitals: any[] = r?.vitals ?? [];
    const v = (type: string) => vitals.find((x) => String(x.vitalType) === type)?.valueNum ?? null;
    return parseOr(zEncounter, {
      id: e.encounterId ?? encounterId,
      patientId: e.beneficiaryId ?? "",
      patientName: neutral(`Beneficiary •••${String(e.beneficiaryId ?? "").slice(-4)}`),
      openedAt: e.startedAt ?? new Date().toISOString(),
      signed: (r?.notes ?? []).some((n: any) => n.isSigned),
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
        diastolic: null,
        heartRate: v("HR"),
        tempC: v("Temp"),
      },
      allergies: (r?.allergies ?? []).map((a: any) => ({
        id: a.allergyId,
        substance: neutral(a.reaction ?? "Allergen"),
        severity: String(a.severity ?? "mild").toLowerCase(),
      })),
      diagnoses: (r?.diagnoses ?? []).map((d: any) => ({
        system: "ICD-10",
        code: d.icdCode,
        label: neutral(d.icdCode),
      })),
    });
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
      orderType: req.kind === "imaging" ? "Imaging" : "Lab",
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
        status: q.status === "UnderReview"
          ? { kind: "info" as const, label: t("Under review", "قيد المراجعة") }
          : { kind: "warn" as const, label: t("Awaiting decision", "في انتظار القرار") },
        createdAt: q.createdAt ?? new Date().toISOString(),
      }),
    );
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
        beneficiary: { id: p.beneficiaryId, token: caseToken({ beneficiaryId: p.beneficiaryId }) },
        lineCount: (p.lines ?? []).length,
        status: rxStatus(p.status),
        submittedAt: p.submittedAt ?? undefined,
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

  // Lab / Imaging (Phase 5, US-040) — the orders service exposes ONE capability-filtered provider queue at
  // /investigation-orders/queue (a lab_tech sees Lab orders, an imaging_tech Imaging — by role, not URL). We
  // flatten each order to one row using its first available line as the `test`, cache that line id so consume
  // can target it, and default priority to routine (the fulfillment queue does not carry a clinical priority).
  async labQueue(kind: "lab" | "imaging") {
    const r = (await getRaw(`/investigation-orders/queue?page=1&pageSize=50`)) as any[];
    return (r ?? [])
      .filter((o: any) => String(o.orderType ?? "").toLowerCase() === kind)
      .map((o: any) => {
        const lines: any[] = o.lines ?? [];
        const line = lines[0] ?? {};
        if (line.orderLineId) orderLineByOrderId.set(String(o.orderId), String(line.orderLineId));
        const remaining = lines.reduce((acc, l) => acc + Math.max(0, Math.round(Number(l.quantityRemaining ?? 1))), 0);
        return parseOr(zLabOrder, {
          id: o.orderId,
          kind,
          test: { system: codeSystem(line.codeSystem), code: line.code ?? "—", label: neutral(line.description ?? line.code ?? "") },
          patient: { id: o.beneficiaryId, token: caseToken({ beneficiaryId: o.beneficiaryId }) },
          priority: "routine",
          status: orderStatus(o.status),
          placedAt: o.requestedAt ?? new Date().toISOString(),
          panelsTotal: Math.max(1, remaining),
          panelsDone: 0,
        });
      });
  }
  // Result upload (Phase 5.3, US-042) — the "awaiting result" worklist is the provider's consumed-but-unreported
  // lines; a result posts as multipart form (resultValue and/or a report file — this screen sends the value).
  async awaitingResult(kind: "lab" | "imaging") {
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
  async uploadResult(orderId: string, lineId: string, resultValue: string, idempotencyKey?: string) {
    await postForm(`/investigation-orders/${encodeURIComponent(orderId)}/lines/${encodeURIComponent(lineId)}/result`, { resultValue }, idempotencyKey);
    return parseOr(zResultUpload, { orderId, lineId, uploaded: true });
  }
  async consume(req: ConsumeRequest) {
    const orderLineId = orderLineByOrderId.get(String(req.orderId));
    const body = { lines: orderLineId ? [{ orderLineId, quantity: req.panels }] : [] };
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
    const r = (await getRaw(`/prescriptions/queue`)) as any[];
    return (r ?? []).map((p: any) => {
      const lines: any[] = p.lines ?? [];
      rxLineIds.set(String(p.prescriptionId), lines.map((l) => String(l.prescriptionLineId)));
      return parseOr(zPrescription, {
        id: p.prescriptionId,
        patient: { id: p.beneficiaryId, token: caseToken({ beneficiaryId: p.beneficiaryId }) },
        prescriber: { label: t("Prescriber", "الطبيب الواصف") },
        submittedAt: p.submittedAt ?? new Date().toISOString(),
        status: rxStatus(p.status),
        lines: lines.map((l) => ({
          id: l.prescriptionLineId,
          drug: drugCoded(l.drugId),
          quantity: Math.max(1, Math.round(Number(l.quantityPrescribed ?? 1))),
          dispensed: Math.round(Number(l.quantityDispensed ?? 0)),
          dose: [l.dose, l.route, l.frequency].filter(Boolean).join(" · "),
          status: rxStatus(l.status),
          outOfStock: false,
        })),
      });
    });
  }
  // Formulary substitutions (Phase 6.3, US-052) — master data is reference-only (auth, no scope). Search drugs
  // by name, then list a drug's policy-approved alternatives (same ATC-5 substance). Bilingual name from AR
  // where master data has it, else the EN name echoed (no machine translation).
  async searchDrugs(query: string) {
    const r = (await getRaw(`/drugs?q=${encodeURIComponent(query)}&pageSize=20`)) as any;
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
        { quantity: line.quantity, batchNo: `DEV-${String(req.prescriptionId).slice(0, 8)}`, expiryDate: expiry },
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

  // Approvals (Phase 7, US-060) — the worklist is GET /authorizations/ (min-necessary: codes + SLA, NO clinical
  // payload — that is /review only, audited as a PHI read). Decisions are per-type endpoints, not one /decision;
  // a decision needs the request UnderReview, so decide assigns first (idempotent-ish) then routes by kind.
  async approvalWorklist() {
    const r = (await getRaw(`/authorizations/`)) as any[];
    const now = Date.now();
    return (r ?? []).map((a: any) => {
      const dueMs = a.slaDueAt ? Date.parse(a.slaDueAt) : now;
      const submittedAt = new Date(now - Number(a.tatElapsedSeconds ?? 0) * 1000).toISOString();
      const code = (a.serviceCodes ?? [])[0] ?? "—";
      return parseOr(zApprovalItem, {
        id: a.authorizationId,
        patient: { id: a.beneficiaryId, token: caseToken({ beneficiaryId: a.beneficiaryId }) },
        service: { system: "CPT", code, label: neutral(code) },
        requestedBy: t("Provider", "مقدم الخدمة"),
        priority: String(a.priority ?? "routine").toLowerCase(),
        sla: {
          dueAt: a.slaDueAt ?? submittedAt,
          breached: !!a.slaBreached,
          minutesRemaining: Math.round((dueMs - now) / 60000),
        },
        status: authStatus(a.status),
        submittedAt,
        estimatedCost: "—",
      });
    });
  }
  async approvalReview(approvalId: string) {
    const a = (await getRaw(`/authorizations/${encodeURIComponent(approvalId)}/review`)) as any;
    const codes: string[] = a?.serviceCodes ?? [];
    return parseOr(zApprovalReview, {
      id: a?.authorizationId ?? approvalId,
      patient: { id: a?.beneficiaryId ?? "", token: caseToken({ beneficiaryId: a?.beneficiaryId }) },
      service: { system: "CPT", code: codes[0] ?? "—", label: neutral(codes[0] ?? "") },
      clinicalJustification: a?.emrSummary ?? "clinical context unavailable",
      supportingCodes: codes.slice(1).map((c) => ({ system: "CPT" as const, code: c, label: neutral(c) })),
      documents: (a?.documents ?? []).map((d: any) => ({ id: d.id ?? d.documentId ?? "", name: d.name ?? d.title ?? "document" })),
      requestedAmount: "—",
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
  async executiveDashboard(scope: "executive" | "finance" | "director") {
    const d = (await getRaw(`/dashboards/executive`)) as any;
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
        value: String(points(w).reduce((acc: number, p: any) => acc + Number(p.value ?? 0), 0)),
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
      kpis,
      charts,
    });
  }

  // Director oversight / quality / escalations (Phase 8.3) — de-identified reporting aggregates (no PHI). Each
  // section fetches the relevant /reports/* endpoints and normalises them to KPI headlines + accessible tables.
  async directorReport(section: "oversight" | "quality" | "escalations") {
    const min = (s: unknown) => `${Math.round(Number(s ?? 0) / 60)}`;
    const pct = (n: unknown) => `${Math.round(Number(n ?? 0) * 100)}%`;
    if (section === "oversight") {
      const pend = (await getRaw(`/reports/pending-approvals`)) as any;
      const tat = (await getRaw(`/reports/approval-tat`)) as any;
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
      const dx = (await getRaw(`/reports/top-diagnoses`)) as any;
      const rx = (await getRaw(`/reports/top-medications`)) as any;
      const ns = (await getRaw(`/reports/no-show`)) as any;
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
    const rej = (await getRaw(`/reports/rejected-requests`)) as any;
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
        annualCap: b.coverage?.annualLimit != null ? money(b.coverage.annualLimit) : undefined,
        remaining: b.coverage?.remainingLimit != null ? money(b.coverage.remainingLimit) : undefined,
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
        status: "ok",
      }),
    );
  }
  async escalations() {
    const r = (await getRaw(`/cases/escalations`)) as any;
    const items: any[] = Array.isArray(r) ? r : (r?.items ?? []);
    return items.map((e: any) =>
      parseOr(zEscalation, {
        id: e.escalationId ?? e.id,
        caseId: e.caseId ?? "",
        caseNo: e.caseNo ?? "",
        raisedToRole: neutral(e.raisedToRole ?? e.targetRole ?? ""),
        reason: String(e.reason ?? ""),
        status: "ok",
        raisedAt: e.raisedAt ?? e.createdAt ?? new Date().toISOString(),
      }),
    );
  }

  // Finance (Phase 10.2) — billing codes + amounts only; the finance service denies any clinical read.
  // The service emits plain strings + numeric amounts; these adapters map to the bilingual + pre-formatted
  // contract shape (and compute share%), then validate the mapping.
  async utilization() {
    const r = (await getRaw(`/finance/utilization`)) as any;
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
        spend: money(x.spend),
      })),
      totalAuthorized: r?.totalAuthorized ?? 0,
      totalDelivered: r?.totalDelivered ?? 0,
      totalSpend: money(r?.totalSpend),
    });
  }
  async settlements() {
    const r = (await getRaw(`/finance/settlements`)) as any[];
    return (r ?? []).map((s: any) =>
      parseOr(zSettlement, {
        id: s.id,
        settlementNo: s.settlementNo,
        providerRef: s.providerRef ?? s.providerId ?? "",
        providerName: neutral(s.providerName ?? s.providerRef ?? ""),
        periodStart: s.periodStart ?? "",
        periodEnd: s.periodEnd ?? "",
        currency: s.currency ?? "EGP",
        total: money(s.total),
        status: "ok",
        state: String(s.state ?? s.status ?? "draft").toLowerCase(),
        lines: (s.lines ?? []).map((l: any) => ({
          serviceCode: l.serviceCode,
          serviceLine: neutral(l.serviceLine),
          deliveredQty: l.deliveredQty ?? 0,
          agreedUnitPrice: money(l.agreedUnitPrice),
          lineTotal: money(l.lineTotal),
        })),
      }),
    );
  }
  async financialSummary(dimension: "serviceline" | "category" | "provider") {
    const r = (await getRaw(`/finance/summaries?dimension=${dimension}`)) as any;
    const buckets: any[] = r?.buckets ?? [];
    const total = buckets.reduce((acc, b) => acc + Number(b.spend ?? 0), 0) || 1;
    return parseOr(zFinancialSummary, {
      dimension: r?.dimension ?? dimension,
      buckets: buckets.map((b: any) => ({
        key: neutral(b.key),
        deliveredQty: b.deliveredQty ?? 0,
        spend: money(b.spend),
        sharePercent: Math.round((Number(b.spend ?? 0) / total) * 100),
      })),
      totalSpend: money(r?.totalSpend ?? total),
    });
  }
  async exportReport(req: ExportRequest) {
    const r = (await postRaw(`/finance/exports`, req)) as any;
    return parseOr(zExportResult, {
      report: r?.report ?? req.report,
      format: r?.format ?? req.format,
      rowCount: r?.rowCount ?? r?.rows ?? 0,
      filename: r?.filename ?? `${req.report}-${req.from}_${req.to}.${req.format}`,
      status: "ok",
    });
  }

  // Claims management (Phase 10b) — codes + amounts only, never a diagnosis. The service isolates provider
  // users to their own claims and audits every read; the portal maps status/bucket → non-color StatusKind chips.
  async claimsWorklist(status?: string) {
    const r = (await getRaw(`/claims/worklist${status ? `?status=${encodeURIComponent(status)}` : ""}`)) as any[];
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

  async claimsReconciliation(bucket?: string) {
    const r = (await getRaw(`/reconciliation${bucket ? `?bucket=${encodeURIComponent(bucket)}` : ""}`)) as any[];
    return (r ?? []).map((l: any) =>
      parseOr(zReconciliationRow, {
        claimId: l.claimId,
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

  async claimsKpis() {
    const r = (await getRaw(`/claims/kpis`)) as any;
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
        tenantId: u.tenantId ? `•••${String(u.tenantId).replace(/-/g, "").slice(-4)}` : undefined,
        isActive: u.isActive !== false,
        twoFactorEnabled: u.twoFactorEnabled === true,
        roles: Array.isArray(u.roles) ? u.roles.map(String) : [],
      }),
    );
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
      id: r?.providerId ?? "",
      code: String(r?.providerCode ?? input.code),
      legalName: String(r?.legalName ?? input.legalName),
      providerType: String(r?.providerTypeLabel ?? r?.providerType ?? input.providerType),
      status: providerStatusChip(r?.status ?? "Suspended"),
      onboardingState: String(r?.onboardingState ?? "Draft"),
    });
  }

  // ---- Practitioners (Phase 14.5, design 37 §4) -----------------------------------------------------------
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
        id: b?.branchId ?? "",
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
        doctorId: d?.doctorId ?? "",
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
      id: r?.beneficiaryId ?? "",
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
        branchId: String(g.branchId ?? ""),
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
    tenantId: String(m?.tenantId ?? ""),
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

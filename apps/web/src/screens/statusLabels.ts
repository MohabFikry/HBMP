import type { Localized } from "@mersal/contracts";
import type { StatusKind } from "@mersal/design-system";

/**
 * Phase 18.D2 (audit R2 U3) — one place that turns a raw server enum into a truthful chip + a bilingual label.
 *
 * The Call Centre 360 rendered `<StatusChip kind="ok" label={summary.identity.status} />`: the LABEL came
 * from the server and the COLOUR was hardcoded green. So a Suspended member, an Expired member and a Blocked
 * member all displayed as eligible-looking green, with the real word in small text beside it. An agent
 * scanning a screen under call pressure reads the colour. The consequence is an agent telling a suspended
 * member their coverage is fine, and booking them an appointment that will be refused at the desk.
 *
 * It also rendered the raw enum — "Suspended", never "موقوف" — so the Arabic UI showed English state names.
 *
 * The rule this file exists to enforce: a status chip's KIND must be derived from the same value as its
 * label. Hardcoding one while binding the other is what makes a chip lie. Unknown values map to `neu` with
 * the raw value shown, which is honest — "I do not know what this means" — rather than a confident green.
 */

/** Beneficiary / member eligibility state (22-data-dictionary §11, BeneficiaryStatus). */
const MEMBER: Record<string, { kind: StatusKind; label: Localized }> = {
  Active: { kind: "ok", label: { en: "Active", ar: "نشط" } },
  Pending: { kind: "info", label: { en: "Pending", ar: "قيد الانتظار" } },
  // Everything below is a member who must NOT be treated as covered.
  Suspended: { kind: "warn", label: { en: "Suspended", ar: "موقوف" } },
  Expired: { kind: "bad", label: { en: "Expired", ar: "منتهي" } },
  Blocked: { kind: "bad", label: { en: "Blocked", ar: "محظور" } },
  Inactive: { kind: "neu", label: { en: "Inactive", ar: "غير نشط" } },
  Deceased: { kind: "neu", label: { en: "Deceased", ar: "متوفى" } },
};

/** Call outcome (37 / callcentre 0001 CHECK constraint). */
const CALL_OUTCOME: Record<string, Localized> = {
  Resolved: { en: "Resolved", ar: "تم الحل" },
  FollowUpRequired: { en: "Follow-up required", ar: "يتطلب متابعة" },
  Transferred: { en: "Transferred", ar: "تم التحويل" },
  Abandoned: { en: "Abandoned", ar: "مهجورة" },
  NoAction: { en: "No action", ar: "لا إجراء" },
};

/** Identifier types shown to an agent verifying a caller. */
const IDENTIFIER_TYPE: Record<string, Localized> = {
  MemberNo: { en: "Member number", ar: "رقم العضوية" },
  NationalId: { en: "National ID", ar: "الرقم القومي" },
  UNHCRNo: { en: "UNHCR number", ar: "رقم المفوضية" },
  Passport: { en: "Passport", ar: "جواز السفر" },
  DateOfBirth: { en: "Date of birth", ar: "تاريخ الميلاد" },
  Phone: { en: "Phone", ar: "الهاتف" },
  // Both spellings of the UNHCR number occur on the wire: the identifier catalogue writes `UNHCRNo`, the
  // eligibility projection column is `UnhcrNo`. An unmapped key falls through to the raw literal, so an
  // Arabic-portal agent would be challenged on the English word "UnhcrNo".
  UnhcrNo: { en: "UNHCR number", ar: "رقم المفوضية" },
  RefugeeId: { en: "Refugee ID", ar: "رقم اللاجئ" },
  FullName: { en: "Full name", ar: "الاسم الكامل" },
};

/** Appointment types (23-state-machines §6). */
const APPOINTMENT_TYPE: Record<string, Localized> = {
  Consultation: { en: "Consultation", ar: "كشف" },
  FollowUp: { en: "Follow-up", ar: "متابعة" },
  Procedure: { en: "Procedure", ar: "إجراء" },
  Referral: { en: "Referral", ar: "إحالة" },
  WalkIn: { en: "Walk-in", ar: "بدون موعد" },
};

/** Referral status. */
const REFERRAL_STATUS: Record<string, { kind: StatusKind; label: Localized }> = {
  Requested: { kind: "info", label: { en: "Requested", ar: "مطلوبة" } },
  Scheduled: { kind: "info", label: { en: "Scheduled", ar: "مجدولة" } },
  Completed: { kind: "ok", label: { en: "Completed", ar: "مكتملة" } },
  Cancelled: { kind: "neu", label: { en: "Cancelled", ar: "ملغاة" } },
};

/** Approval priority (7.x worklist). */
const PRIORITY: Record<string, { kind: StatusKind; label: Localized }> = {
  Emergency: { kind: "bad", label: { en: "Emergency", ar: "طارئ" } },
  Urgent: { kind: "warn", label: { en: "Urgent", ar: "عاجل" } },
  Routine: { kind: "neu", label: { en: "Routine", ar: "عادي" } },
};

/** A raw value we have no mapping for: neutral, and shown verbatim so it is visibly unrecognised. */
function unknown(raw: string): { kind: StatusKind; label: Localized } {
  return { kind: "neu", label: { en: raw, ar: raw } };
}

export function memberStatus(raw: string | null | undefined) {
  if (!raw) return unknown("—");
  return MEMBER[raw] ?? unknown(raw);
}

export function referralStatus(raw: string | null | undefined) {
  if (!raw) return unknown("—");
  return REFERRAL_STATUS[raw] ?? unknown(raw);
}

export function priority(raw: string | null | undefined) {
  if (!raw) return unknown("—");
  return PRIORITY[raw] ?? unknown(raw);
}

/** Label-only lookups (no chip): the value is rendered as text, not as a status. */
/**
 * Why a call happened. Lives here rather than in a screen because BOTH call-centre screens render it now —
 * the workspace's call bar and the booking journey's call-record step — and a per-screen copy is exactly how
 * one of them ends up showing an Arabic-portal agent "FollowUpRequired".
 */
const CALL_REASON: Record<string, Localized> = {
  BookAppointment: { en: "Book appointment", ar: "حجز موعد" },
  RescheduleAppointment: { en: "Reschedule appointment", ar: "إعادة جدولة موعد" },
  CancelAppointment: { en: "Cancel appointment", ar: "إلغاء موعد" },
  AppointmentEnquiry: { en: "Appointment enquiry", ar: "استفسار عن موعد" },
  EligibilityEnquiry: { en: "Eligibility enquiry", ar: "استفسار عن الأهلية" },
  UpdateContact: { en: "Update contact", ar: "تحديث بيانات الاتصال" },
  Complaint: { en: "Complaint", ar: "شكوى" },
  Other: { en: "Other", ar: "أخرى" },
};

export function callReasonLabel(raw: string | null | undefined): Localized {
  return raw ? CALL_REASON[raw] ?? { en: raw, ar: raw } : { en: "—", ar: "—" };
}

export function callOutcomeLabel(raw: string | null | undefined): Localized {
  return raw ? CALL_OUTCOME[raw] ?? { en: raw, ar: raw } : { en: "—", ar: "—" };
}

export function identifierTypeLabel(raw: string | null | undefined): Localized {
  return raw ? IDENTIFIER_TYPE[raw] ?? { en: raw, ar: raw } : { en: "—", ar: "—" };
}

export function appointmentTypeLabel(raw: string | null | undefined): Localized {
  return raw ? APPOINTMENT_TYPE[raw] ?? { en: raw, ar: raw } : { en: "—", ar: "—" };
}

/** Exposed for the test that asserts every member state has a truthful, non-green mapping. */
export const MEMBER_STATUSES = MEMBER;

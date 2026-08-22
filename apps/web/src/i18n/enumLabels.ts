import type { Localized } from "@mersal/contracts";
import { useLoc } from "../screens/_shared";

/**
 * Server enum members, in both languages.
 *
 * ============================================================================================================
 * WHY THIS EXISTS, AND WHY IT IS SHARED
 * ============================================================================================================
 * Policy-service returns statuses, relationships, limit types and job states as plain strings — they are C#
 * enum members on the wire (`api/policyApi.ts` types them as `string`), and screens rendered them straight
 * through. So an Arabic operator read a fully Arabic table whose Status column said "Active", whose Limit type
 * dropdown offered "PerEncounter", and whose job chip said "Validating". A locale that is right everywhere
 * except in the columns carrying the decision is not a translated screen.
 *
 * The portals use a typed `Localized` (`{en, ar}`) precisely so a missing translation is a COMPILE error
 * (ADR-0042). A `string` off the API was never a `Localized`, so it walks straight past the one mechanism
 * built to catch this — which is why it has to be caught here instead.
 *
 * This map lived inside `MemberAdmin` and covered the twelve members that screen renders. Six other screens
 * in the same portal needed the same treatment and did not have it, and a translation table that only one
 * screen can reach is a translation table the next screen will duplicate slightly differently. One module,
 * one vocabulary.
 *
 * ============================================================================================================
 * THE FALLBACK IS THE RAW VALUE, ON PURPOSE
 * ============================================================================================================
 * A member the server adds later shows up as itself rather than disappearing. Rendering blank — or, worse,
 * a placeholder — would hide a member's state, which is the failure mode that actually matters here. An
 * untranslated word is a cosmetic defect; a missing status is a clinical-adjacent one.
 */
export const ENUM_LABELS: Record<string, Localized> = {
  // ── Lifecycle: member, policy, coverage, catalogue ────────────────────────────────────────────────────
  Active: { en: "Active", ar: "نشط" },
  Inactive: { en: "Inactive", ar: "موقوف" },
  Pending: { en: "Pending", ar: "قيد الانتظار" },
  Suspended: { en: "Suspended", ar: "معلّق" },
  Terminated: { en: "Terminated", ar: "منتهٍ" },
  Cancelled: { en: "Cancelled", ar: "ملغى" },
  Expired: { en: "Expired", ar: "منتهي الصلاحية" },

  // ── Plan version (design 38 §3) ───────────────────────────────────────────────────────────────────────
  // `Superseded` and `Retired` both still resolve for service dates inside their own window, so neither may
  // read as "gone" — the Arabic says replaced and withdrawn, not deleted.
  Draft: { en: "Draft", ar: "مسودة" },
  Superseded: { en: "Superseded", ar: "مُستبدَل" },
  Retired: { en: "Retired", ar: "مسحوب" },

  // ── Relationship ──────────────────────────────────────────────────────────────────────────────────────
  Principal: { en: "Principal", ar: "المشترك الرئيسي" },
  Spouse: { en: "Spouse", ar: "الزوج/الزوجة" },
  Child: { en: "Child", ar: "ابن/ابنة" },
  Dependent: { en: "Dependent", ar: "معال" },

  // ── Waiting period ────────────────────────────────────────────────────────────────────────────────────
  Serving: { en: "Serving", ar: "جارية" },
  Served: { en: "Served", ar: "منتهية" },
  None: { en: "None", ar: "لا يوجد" },

  // ── Benefit limits ────────────────────────────────────────────────────────────────────────────────────
  // `PerEncounter` is not English prose — it is an identifier, and it was being shown to operators as one.
  Annual: { en: "Annual", ar: "سنوي" },
  PerEncounter: { en: "Per encounter", ar: "لكل زيارة" },
  Lifetime: { en: "Lifetime", ar: "مدى الحياة" },
  Count: { en: "Count", ar: "عدد" },

  // ── Reset period ──────────────────────────────────────────────────────────────────────────────────────
  Monthly: { en: "Monthly", ar: "شهري" },
  Quarterly: { en: "Quarterly", ar: "ربع سنوي" },
  Yearly: { en: "Yearly", ar: "سنوي" },

  // ── Bulk job (design 38 §5b) ──────────────────────────────────────────────────────────────────────────
  Uploaded: { en: "Uploaded", ar: "تم الرفع" },
  Scanning: { en: "Scanning", ar: "جارٍ الفحص" },
  Validating: { en: "Validating", ar: "جارٍ التحقق" },
  Validated: { en: "Validated", ar: "تم التحقق" },
  Committing: { en: "Committing", ar: "جارٍ التطبيق" },
  Completed: { en: "Completed", ar: "مكتمل" },
  Failed: { en: "Failed", ar: "فشل" },
  RolledBack: { en: "Rolled back", ar: "تم التراجع" },
  // Row-level outcomes. `Skipped` is deliberately distinct from `Failed`: a row an earlier run already
  // applied was skipped on purpose, and collapsing them makes an idempotent re-commit read as a failure.
  Valid: { en: "Valid", ar: "صالح" },
  Invalid: { en: "Invalid", ar: "غير صالح" },
  Applied: { en: "Applied", ar: "مُطبَّق" },
  Skipped: { en: "Skipped", ar: "متخطّى" },

  // ── Member group ──────────────────────────────────────────────────────────────────────────────────────
  Programme: { en: "Programme", ar: "برنامج" },
  Cohort: { en: "Cohort", ar: "مجموعة" },
  BranchCaseload: { en: "Branch caseload", ar: "حِمل الفرع" },
  Campaign: { en: "Campaign", ar: "حملة" },

  // ── Identity ──────────────────────────────────────────────────────────────────────────────────────────
  Male: { en: "Male", ar: "ذكر" },
  Female: { en: "Female", ar: "أنثى" },
  Other: { en: "Other", ar: "آخر" },
  Unknown: { en: "Unknown", ar: "غير معروف" },
  NationalID: { en: "National ID", ar: "الرقم القومي" },
  Passport: { en: "Passport", ar: "جواز السفر" },
  RefugeeID: { en: "Refugee ID", ar: "بطاقة اللاجئ" },
  UNHCRNo: { en: "UNHCR number", ar: "رقم المفوضية" },
  MemberNo: { en: "Member number", ar: "رقم العضوية" },
};

/** Resolve a server enum to the active language, falling back to the raw value. */
export function useEnumLabel(): (value: string) => string {
  const t = useLoc();
  return (value: string) => (ENUM_LABELS[value] ? t(ENUM_LABELS[value]) : value);
}

import type { IconName } from "@mersal/design-system";
import type { Permission, Role } from "../authz/permissions";

/** Bilingual label authored inline (both locales; no machine translation). */
export interface Localized {
  en: string;
  ar: string;
}

export interface Section {
  key: string;
  /** Route path relative to the portal base (the full path is `/${portalBase}/${path}`). */
  path: string;
  label: Localized;
  /** Nav group heading (bilingual). */
  group: Localized;
  icon: IconName;
  /** Permission required to see + open this section. */
  permission: Permission;
}

export interface PortalDef {
  role: Role;
  /** URL base segment for the portal, e.g. `reception`. */
  base: string;
  title: Localized;
  /** Short role eyebrow shown in the page header. */
  eyebrow: Localized;
  sections: Section[];
}

const G = {
  access: { en: "Patient access", ar: "وصول المستفيد" },
  clinical: { en: "Clinical", ar: "سريري" },
  fulfillment: { en: "Fulfillment", ar: "التنفيذ" },
  dispensing: { en: "Dispensing", ar: "الصرف" },
  approvals: { en: "Approvals", ar: "الموافقات" },
  registration: { en: "Registration", ar: "التسجيل" },
  cases: { en: "Cases", ar: "الحالات" },
  finance: { en: "Finance", ar: "المالية" },
  network: { en: "Network", ar: "الشبكة" },
  admin: { en: "Administration", ar: "الإدارة" },
  oversight: { en: "Oversight", ar: "الإشراف" },
  insights: { en: "Insights", ar: "المؤشرات" },
} satisfies Record<string, Localized>;

/**
 * The full portal catalog (14-navigation-structure §2). Each role gets a distinct portal; sections are
 * permission-gated so the router mounts only usable routes and the nav renders only usable items.
 * Min-necessary is structural: Reception has no EMR section, Finance has no clinical section, Pharmacy no
 * results, Lab no prescriptions.
 */
export const PORTALS: PortalDef[] = [
  {
    role: "reception",
    base: "reception",
    title: { en: "Reception", ar: "الاستقبال" },
    eyebrow: { en: "Reception", ar: "الاستقبال" },
    sections: [
      { key: "eligibility", path: "eligibility", label: { en: "Eligibility search", ar: "التحقق من الأهلية" }, group: G.access, icon: "user", permission: "eligibility.check" },
      { key: "queue", path: "queue", label: { en: "Today's visits", ar: "زيارات اليوم" }, group: G.access, icon: "check2", permission: "queue.reception" },
      { key: "appointments", path: "appointments", label: { en: "Appointments", ar: "المواعيد" }, group: G.access, icon: "clock", permission: "appointments.read" },
      { key: "checkin", path: "check-in", label: { en: "Check-in", ar: "تسجيل الوصول" }, group: G.access, icon: "ok", permission: "checkin.write" },
    ],
  },
  {
    role: "doctor",
    base: "clinician",
    title: { en: "Consultation", ar: "الكشف" },
    eyebrow: { en: "Doctor", ar: "الطبيب" },
    sections: [
      { key: "patients", path: "patients", label: { en: "My patients", ar: "مرضاي" }, group: G.clinical, icon: "user", permission: "emr.read" },
      { key: "encounter", path: "encounter", label: { en: "Encounter workspace", ar: "مساحة اللقاء" }, group: G.clinical, icon: "doc", permission: "emr.write" },
      { key: "orders", path: "orders", label: { en: "Orders", ar: "الطلبات" }, group: G.clinical, icon: "flask", permission: "orders.place" },
      { key: "prescriptions", path: "prescriptions", label: { en: "Prescriptions", ar: "الوصفات" }, group: G.clinical, icon: "pill", permission: "prescriptions.write" },
      { key: "results", path: "results", label: { en: "Results inbox", ar: "صندوق النتائج" }, group: G.clinical, icon: "chart", permission: "results.inbox" },
    ],
  },
  {
    role: "nurse",
    base: "nurse",
    title: { en: "Nursing", ar: "التمريض" },
    eyebrow: { en: "Nurse", ar: "الممرض/ة" },
    sections: [
      { key: "patients", path: "patients", label: { en: "My patients", ar: "مرضاي" }, group: G.clinical, icon: "user", permission: "emr.read" },
      { key: "vitals", path: "vitals", label: { en: "Vitals & triage", ar: "العلامات والفرز" }, group: G.clinical, icon: "chart", permission: "vitals.write" },
      { key: "results", path: "results", label: { en: "Results inbox", ar: "صندوق النتائج" }, group: G.clinical, icon: "doc", permission: "results.inbox" },
    ],
  },
  {
    role: "lab",
    base: "lab",
    title: { en: "Laboratory", ar: "المختبر" },
    eyebrow: { en: "Laboratory", ar: "المختبر" },
    sections: [
      { key: "queue", path: "queue", label: { en: "Order queue", ar: "قائمة الطلبات" }, group: G.fulfillment, icon: "flask", permission: "lab.queue" },
      { key: "consume", path: "consume", label: { en: "Consume order", ar: "تنفيذ الطلب" }, group: G.fulfillment, icon: "ok", permission: "lab.consume" },
      { key: "result", path: "result", label: { en: "Upload result", ar: "رفع النتيجة" }, group: G.fulfillment, icon: "doc", permission: "lab.result.upload" },
    ],
  },
  {
    role: "imaging",
    base: "imaging",
    title: { en: "Imaging", ar: "الأشعة" },
    eyebrow: { en: "Imaging", ar: "الأشعة" },
    sections: [
      { key: "queue", path: "queue", label: { en: "Order queue", ar: "قائمة الطلبات" }, group: G.fulfillment, icon: "flask", permission: "imaging.queue" },
      { key: "consume", path: "consume", label: { en: "Consume order", ar: "تنفيذ الطلب" }, group: G.fulfillment, icon: "ok", permission: "imaging.consume" },
      { key: "result", path: "result", label: { en: "Upload result", ar: "رفع النتيجة" }, group: G.fulfillment, icon: "doc", permission: "imaging.result.upload" },
    ],
  },
  {
    role: "pharmacy",
    base: "pharmacy",
    title: { en: "Pharmacy", ar: "الصيدلية" },
    eyebrow: { en: "Pharmacy", ar: "الصيدلية" },
    sections: [
      { key: "queue", path: "queue", label: { en: "Prescription queue", ar: "قائمة الوصفات" }, group: G.dispensing, icon: "pill", permission: "pharmacy.queue" },
      { key: "dispense", path: "dispense", label: { en: "Dispense", ar: "الصرف" }, group: G.dispensing, icon: "ok", permission: "pharmacy.dispense" },
      { key: "substitutions", path: "substitutions", label: { en: "Substitutions", ar: "البدائل" }, group: G.dispensing, icon: "refer", permission: "pharmacy.substitution" },
    ],
  },
  {
    role: "medical_approval",
    base: "approvals",
    title: { en: "Approval worklist", ar: "قائمة الموافقات" },
    eyebrow: { en: "Medical approval", ar: "الموافقة الطبية" },
    sections: [
      { key: "worklist", path: "worklist", label: { en: "Worklist", ar: "قائمة العمل" }, group: G.approvals, icon: "check2", permission: "approvals.worklist" },
      { key: "manual", path: "manual", label: { en: "Manual authorization", ar: "تفويض يدوي" }, group: G.approvals, icon: "plus", permission: "approvals.manual" },
      { key: "emergency", path: "emergency", label: { en: "Emergency / override", ar: "طارئ / تجاوز" }, group: G.approvals, icon: "triangle", permission: "approvals.emergency" },
      { key: "sla", path: "sla", label: { en: "SLA / TAT board", ar: "لوحة الاستجابة" }, group: G.insights, icon: "chart", permission: "approvals.sla" },
    ],
  },
  {
    role: "beneficiary_mgmt",
    base: "beneficiaries",
    title: { en: "Beneficiary management", ar: "إدارة المستفيدين" },
    eyebrow: { en: "Beneficiary mgmt", ar: "إدارة المستفيدين" },
    sections: [
      { key: "register", path: "register", label: { en: "Register new", ar: "تسجيل جديد" }, group: G.registration, icon: "plus", permission: "beneficiary.register" },
      { key: "manage", path: "manage", label: { en: "Search / manage", ar: "بحث / إدارة" }, group: G.registration, icon: "user", permission: "beneficiary.manage" },
      { key: "status", path: "status", label: { en: "Status & reactivation", ar: "الحالة وإعادة التفعيل" }, group: G.registration, icon: "clock", permission: "beneficiary.status" },
      { key: "eligibility", path: "eligibility", label: { en: "Eligibility check", ar: "التحقق من الأهلية" }, group: G.access, icon: "check2", permission: "eligibility.check" },
    ],
  },
  {
    role: "case_manager",
    base: "cases",
    title: { en: "Case management", ar: "إدارة الحالات" },
    eyebrow: { en: "Case manager", ar: "مدير الحالة" },
    sections: [
      { key: "cases", path: "my-cases", label: { en: "My cases", ar: "حالاتي" }, group: G.cases, icon: "doc", permission: "case.read" },
      { key: "beneficiary360", path: "beneficiary-360", label: { en: "Beneficiary 360", ar: "المستفيد 360" }, group: G.cases, icon: "user", permission: "case.beneficiary360" },
      { key: "escalations", path: "escalations", label: { en: "Escalations", ar: "التصعيدات" }, group: G.cases, icon: "triangle", permission: "case.escalations" },
    ],
  },
  {
    role: "finance",
    base: "finance",
    title: { en: "Finance", ar: "المالية" },
    eyebrow: { en: "Finance", ar: "المالية" },
    // No clinical/diagnosis routes exist here — min-necessary (Finance cannot view diagnoses).
    sections: [
      { key: "utilization", path: "utilization", label: { en: "Utilization", ar: "الاستخدام" }, group: G.finance, icon: "chart", permission: "finance.utilization" },
      { key: "settlements", path: "settlements", label: { en: "Provider settlements", ar: "تسويات مقدمي الخدمة" }, group: G.finance, icon: "doc", permission: "finance.settlements" },
      { key: "summaries", path: "summaries", label: { en: "Financial summaries", ar: "ملخصات مالية" }, group: G.finance, icon: "check2", permission: "finance.summaries" },
      { key: "exports", path: "exports", label: { en: "Exports", ar: "التصدير" }, group: G.finance, icon: "refer", permission: "finance.export" },
    ],
  },
  {
    role: "provider_admin",
    base: "network",
    title: { en: "Provider network", ar: "شبكة مقدمي الخدمة" },
    eyebrow: { en: "Network admin", ar: "إدارة الشبكة" },
    sections: [
      { key: "directory", path: "directory", label: { en: "Providers directory", ar: "دليل مقدمي الخدمة" }, group: G.network, icon: "user", permission: "provider.directory" },
      { key: "onboarding", path: "onboarding", label: { en: "Onboarding", ar: "الانضمام" }, group: G.network, icon: "plus", permission: "provider.onboarding" },
      { key: "contracts", path: "contracts", label: { en: "Contracts & coverage", ar: "العقود والتغطية" }, group: G.network, icon: "doc", permission: "provider.contracts" },
      { key: "locations", path: "locations", label: { en: "Locations & users", ar: "المواقع والمستخدمون" }, group: G.network, icon: "check2", permission: "provider.locations" },
      { key: "performance", path: "performance", label: { en: "Performance", ar: "الأداء" }, group: G.insights, icon: "chart", permission: "provider.performance" },
    ],
  },
  {
    role: "org_admin",
    base: "admin",
    title: { en: "Administration", ar: "الإدارة" },
    eyebrow: { en: "Org admin", ar: "مدير المؤسسة" },
    sections: [
      { key: "users", path: "users", label: { en: "Users & roles", ar: "المستخدمون والأدوار" }, group: G.admin, icon: "user", permission: "admin.users" },
      { key: "policies", path: "policies", label: { en: "Permissions / policies", ar: "الصلاحيات / السياسات" }, group: G.admin, icon: "check2", permission: "admin.policies" },
      { key: "masterdata", path: "master-data", label: { en: "Master data", ar: "البيانات المرجعية" }, group: G.admin, icon: "doc", permission: "admin.masterdata" },
      { key: "tenants", path: "tenants", label: { en: "Tenants / providers", ar: "المستأجرون / مقدمو الخدمة" }, group: G.admin, icon: "refer", permission: "admin.tenants" },
      { key: "audit", path: "audit", label: { en: "Audit & access reviews", ar: "التدقيق والمراجعات" }, group: G.oversight, icon: "clock", permission: "admin.audit" },
      { key: "config", path: "config", label: { en: "System config", ar: "إعدادات النظام" }, group: G.admin, icon: "info", permission: "admin.config" },
    ],
  },
  {
    role: "super_admin",
    base: "platform",
    title: { en: "Platform administration", ar: "إدارة المنصة" },
    eyebrow: { en: "Super admin", ar: "مدير المنصة" },
    sections: [
      { key: "users", path: "users", label: { en: "Users & roles", ar: "المستخدمون والأدوار" }, group: G.admin, icon: "user", permission: "admin.users" },
      { key: "policies", path: "policies", label: { en: "Permissions / policies", ar: "الصلاحيات / السياسات" }, group: G.admin, icon: "check2", permission: "admin.policies" },
      { key: "masterdata", path: "master-data", label: { en: "Master data", ar: "البيانات المرجعية" }, group: G.admin, icon: "doc", permission: "admin.masterdata" },
      { key: "tenants", path: "tenants", label: { en: "Tenants / providers", ar: "المستأجرون / مقدمو الخدمة" }, group: G.admin, icon: "refer", permission: "admin.tenants" },
      { key: "audit", path: "audit", label: { en: "Audit & access reviews", ar: "التدقيق والمراجعات" }, group: G.oversight, icon: "clock", permission: "admin.audit" },
      { key: "config", path: "config", label: { en: "System config", ar: "إعدادات النظام" }, group: G.admin, icon: "info", permission: "admin.config" },
    ],
  },
  {
    role: "medical_director",
    base: "director",
    title: { en: "Medical director", ar: "المدير الطبي" },
    eyebrow: { en: "Medical director", ar: "المدير الطبي" },
    sections: [
      { key: "dashboards", path: "dashboards", label: { en: "Clinical dashboards", ar: "لوحات سريرية" }, group: G.insights, icon: "chart", permission: "director.dashboards" },
      { key: "oversight", path: "oversight", label: { en: "Approval oversight / TAT", ar: "الإشراف على الموافقات" }, group: G.oversight, icon: "check2", permission: "director.oversight" },
      { key: "quality", path: "quality", label: { en: "Quality & outcomes", ar: "الجودة والنتائج" }, group: G.oversight, icon: "doc", permission: "director.quality" },
      { key: "escalations", path: "escalations", label: { en: "Escalations", ar: "التصعيدات" }, group: G.oversight, icon: "triangle", permission: "director.escalations" },
    ],
  },
];

export function portalForRole(role: Role): PortalDef {
  const p = PORTALS.find((x) => x.role === role);
  if (!p) throw new Error(`No portal defined for role ${role}`);
  return p;
}

/** Flat map of every fully-qualified route path → owning section (for deep-link resolution). */
export interface RouteEntry {
  portal: PortalDef;
  section: Section;
  fullPath: string;
}
export const ALL_ROUTES: RouteEntry[] = PORTALS.flatMap((portal) =>
  portal.sections.map((section) => ({ portal, section, fullPath: `/${portal.base}/${section.path}` })),
);

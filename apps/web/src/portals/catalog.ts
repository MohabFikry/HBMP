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
  contact: { en: "Contact centre", ar: "مركز الاتصال" },
  claims: { en: "Claims", ar: "المطالبات" },
  finance: { en: "Finance", ar: "المالية" },
  network: { en: "Network", ar: "الشبكة" },
  admin: { en: "Administration", ar: "الإدارة" },
  oversight: { en: "Oversight", ar: "الإشراف" },
  insights: { en: "Insights", ar: "المؤشرات" },
  product: { en: "Benefit product", ar: "منتج المنافع" },
  membership: { en: "Membership", ar: "العضوية" },
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
      // 18.C2 (W4): requests to release a sensitive result the doctor authored. Same permission as the
      // results inbox — deciding who may see your result is part of owning it (37 §6).
      { key: "result-access", path: "result-access", label: { en: "Result access requests", ar: "طلبات الوصول للنتائج" }, group: G.clinical, icon: "clock", permission: "results.inbox" },
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
      // US-003 — the officer PREPARES the application here (documents verified, coverage bound); the
      // decision buttons belong to the supervisor's portal below and the server enforces the split.
      { key: "approvals", path: "approvals", label: { en: "Registration approvals", ar: "اعتماد التسجيلات" }, group: G.registration, icon: "check2", permission: "beneficiary.approvals" },
      { key: "manage", path: "manage", label: { en: "Search / manage", ar: "بحث / إدارة" }, group: G.registration, icon: "user", permission: "beneficiary.manage" },
      { key: "status", path: "status", label: { en: "Status & reactivation", ar: "الحالة وإعادة التفعيل" }, group: G.registration, icon: "clock", permission: "beneficiary.status" },
      { key: "eligibility", path: "eligibility", label: { en: "Eligibility check", ar: "التحقق من الأهلية" }, group: G.access, icon: "check2", permission: "eligibility.check" },
      // Phase 19.6 — the membership book. Registration answers "who is this person"; these answer "what are
      // they entitled to, under whose policy, and what have they used".
      // Bulk sits WITH its membership siblings: the rail groups consecutive runs, so an out-of-order entry
      // renders a second "MEMBERSHIP" heading after INSIGHTS (QA P1-9).
      { key: "members", path: "members", label: { en: "Members", ar: "الأعضاء" }, group: G.membership, icon: "user", permission: "policy.members" },
      { key: "groups", path: "groups", label: { en: "Groups", ar: "المجموعات" }, group: G.membership, icon: "refer", permission: "policy.groups" },
      { key: "bulk", path: "bulk", label: { en: "Bulk & imports", ar: "الرفع الجماعي" }, group: G.membership, icon: "doc", permission: "policy.bulk" },
      { key: "utilization", path: "utilization", label: { en: "Utilization", ar: "الاستخدام" }, group: G.insights, icon: "chart", permission: "policy.utilization" },
      { key: "analytics", path: "analytics", label: { en: "Analytics", ar: "التحليلات" }, group: G.insights, icon: "chart", permission: "policy.analytics" },
    ],
  },
  {
    // 19.7's approver persona (US-003). Same base as the officer's portal — one person holds one role at a
    // time, so the paths never collide — but a deliberately narrower section list: the supervisor DECIDES
    // registrations and must not hold the register pen (the SoD split the server enforces on the decision
    // endpoint starts in the navigation).
    role: "beneficiary_mgmt_supervisor",
    base: "beneficiaries",
    title: { en: "Registration review", ar: "مراجعة التسجيلات" },
    eyebrow: { en: "Registration supervisor", ar: "مشرف التسجيل" },
    sections: [
      { key: "approvals", path: "approvals", label: { en: "Registration approvals", ar: "اعتماد التسجيلات" }, group: G.registration, icon: "check2", permission: "beneficiary.approvals" },
      { key: "manage", path: "manage", label: { en: "Search / manage", ar: "بحث / إدارة" }, group: G.registration, icon: "user", permission: "beneficiary.manage" },
      { key: "status", path: "status", label: { en: "Status & reactivation", ar: "الحالة وإعادة التفعيل" }, group: G.registration, icon: "clock", permission: "beneficiary.status" },
      { key: "eligibility", path: "eligibility", label: { en: "Eligibility check", ar: "التحقق من الأهلية" }, group: G.access, icon: "check2", permission: "eligibility.check" },
      { key: "members", path: "members", label: { en: "Members", ar: "الأعضاء" }, group: G.membership, icon: "user", permission: "policy.members" },
      { key: "groups", path: "groups", label: { en: "Groups", ar: "المجموعات" }, group: G.membership, icon: "refer", permission: "policy.groups" },
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
    role: "call_center",
    base: "call-centre",
    title: { en: "Call Centre", ar: "مركز الاتصال" },
    eyebrow: { en: "Call Centre", ar: "مركز الاتصال" },
    // No clinical routes exist here — min-necessary (the call centre gets no clinical data).
    sections: [
      { key: "workspace", path: "workspace", label: { en: "Call workspace", ar: "مساحة المكالمة" }, group: G.contact, icon: "user", permission: "callcentre.workspace" },
      { key: "history", path: "history", label: { en: "Call history", ar: "سجل المكالمات" }, group: G.contact, icon: "clock", permission: "callcentre.history" },
    ],
  },
  {
    role: "claims_officer",
    base: "claims",
    title: { en: "Claims management", ar: "إدارة المطالبات" },
    eyebrow: { en: "Claims officer", ar: "موظف المطالبات" },
    // No clinical/diagnosis routes exist here — min-necessary (claims sees codes + amounts, never a diagnosis).
    sections: [
      { key: "worklist", path: "worklist", label: { en: "Claims worklist", ar: "قائمة المطالبات" }, group: G.claims, icon: "doc", permission: "claims.worklist" },
      { key: "reconciliation", path: "reconciliation", label: { en: "Reconciliation", ar: "التسوية" }, group: G.claims, icon: "check2", permission: "claims.reconciliation" },
      { key: "insights", path: "insights", label: { en: "Claims insights", ar: "مؤشرات المطالبات" }, group: G.insights, icon: "chart", permission: "claims.insights" },
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
      // 19.6b — the financial and network views are the money questions this role exists to answer. Still no
      // clinical route: the dashboard's fact tables carry no diagnosis column at all.
      { key: "analytics", path: "analytics", label: { en: "Analytics", ar: "التحليلات" }, group: G.insights, icon: "chart", permission: "policy.analytics" },
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
      // Phase 19.6 (19.1b) — the Network Team owns the tier structure and who sits in it.
      { key: "tiers", path: "tiers", label: { en: "Network tiers", ar: "شرائح الشبكة" }, group: G.network, icon: "half", permission: "network.tiers" },
    ],
  },
  {
    role: "policy_admin",
    base: "policy",
    title: { en: "Policy administration", ar: "إدارة الوثائق التأمينية" },
    eyebrow: { en: "Policy admin", ar: "مدير الوثائق" },
    // No clinical route exists here — policy administration reads entitlement and money, never a diagnosis.
    sections: [
      { key: "payers", path: "payers", label: { en: "Payers", ar: "الجهات الممولة" }, group: G.product, icon: "user", permission: "policy.payers" },
      { key: "plans", path: "plans", label: { en: "Plans & versions", ar: "الخطط والإصدارات" }, group: G.product, icon: "doc", permission: "policy.plans" },
      { key: "policies", path: "policies", label: { en: "Policies", ar: "الوثائق" }, group: G.product, icon: "check2", permission: "policy.policies" },
      { key: "members", path: "members", label: { en: "Members", ar: "الأعضاء" }, group: G.membership, icon: "user", permission: "policy.members" },
      { key: "groups", path: "groups", label: { en: "Groups", ar: "المجموعات" }, group: G.membership, icon: "refer", permission: "policy.groups" },
      { key: "utilization", path: "utilization", label: { en: "Utilization", ar: "الاستخدام" }, group: G.insights, icon: "chart", permission: "policy.utilization" },
      // 19.6b — the analytical layer over 19.1–19.5b. Served by reporting-service from a pre-aggregated read
      // model, never by querying the benefit spine the reception desk is using.
      { key: "analytics", path: "analytics", label: { en: "Analytics", ar: "التحليلات" }, group: G.insights, icon: "chart", permission: "policy.analytics" },
      { key: "bulk", path: "bulk", label: { en: "Bulk & imports", ar: "الرفع الجماعي" }, group: G.membership, icon: "plus", permission: "policy.bulk" },
      // Read-only here: policy administration prices benefits AT a tier; the Network Team decides which tier
      // a provider sits in. Same section, different capability (see `mayAdministerTiers`).
      { key: "tiers", path: "tiers", label: { en: "Network tiers", ar: "شرائح الشبكة" }, group: G.network, icon: "half", permission: "network.tiers" },
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
      // 21.6 — memberships, exceptions, branch reach and the effective-access preview (design 40).
      { key: "access", path: "access", label: { en: "Users & access", ar: "المستخدمون والصلاحيات" }, group: G.admin, icon: "user", permission: "admin.access" },
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
      { key: "access", path: "access", label: { en: "Users & access", ar: "المستخدمون والصلاحيات" }, group: G.admin, icon: "user", permission: "admin.access" },
      // Platform administration only — programme enablement is set by Mersal, never by the tenant, so it
      // appears on this portal alone. The hiding is cosmetic; the API requires the platform-admin role.
      { key: "programs", path: "programs", label: { en: "Programme enablement", ar: "تفعيل البرامج" }, group: G.admin, icon: "check2", permission: "admin.programs" },
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
      // 18.C2 (W4): the ESCALATION path for sensitive-result release — 37 §6 lets the Medical Director decide
      // when the authoring doctor is unavailable, which is the case the whole mechanism exists to cover.
      { key: "result-access", path: "result-access", label: { en: "Result access requests", ar: "طلبات الوصول للنتائج" }, group: G.oversight, icon: "clock", permission: "director.escalations" },
    ],
  },
];

// Cross-cutting inbox: every portal gets a Notifications section (self-service; the notification-service row-filters
// by recipient == caller, so it is inherently min-necessary). Appended here so it renders last in the nav for all
// roles without repeating it in every portal literal above.
const NOTIFICATIONS_SECTION: Section = {
  key: "notifications",
  path: "notifications",
  label: { en: "Notifications", ar: "الإشعارات" },
  group: { en: "Inbox", ar: "الوارد" },
  icon: "clock",
  permission: "notification.read",
};
for (const portal of PORTALS) {
  if (!portal.sections.some((s) => s.key === "notifications")) portal.sections.push(NOTIFICATIONS_SECTION);
}

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

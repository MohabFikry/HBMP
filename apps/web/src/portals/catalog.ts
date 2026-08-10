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
  /**
   * 25.7 — show this section only to a caller whose REACH spans more than one branch (design 42 §1/§6).
   *
   * The Branches overview compares the six clinics. For a coordinator who runs one, that comparison has a
   * single row and no meaning. Hiding it is therefore a REACH decision, not an authority one — which is why
   * it is a flag here and NOT a permission the manager holds and the coordinator does not. Making it a
   * permission would have broken the one-permission-set invariant this whole phase rests on, to hide a table.
   */
  reachScoped?: boolean;
}

/**
 * The three bands the portal picker groups by (design 14 §1). A zone answers "what kind of work is this",
 * which is the only question a person with several portals is asking when they land on the picker.
 *
 * NOT a permission and NOT a scope — nothing is authorized by zone. It exists so twenty-one cards read as
 * three short lists rather than one long one.
 */
export type ZoneKey = "operations" | "clinical" | "fulfillment";

export interface ZoneDef {
  key: ZoneKey;
  label: Localized;
  /**
   * The zone's dot on a portal card, named as an EXISTING token. No new colour enters the system for this —
   * and the dot is decorative (`aria-hidden`) with the zone heading directly above it carrying the meaning,
   * so the grouping survives greyscale, colour blindness and a forced-colours mode.
   */
  dot: string;
}

/** Render order on the picker. Zones the caller holds no portal in are not rendered at all. */
export const ZONES: readonly ZoneDef[] = [
  {
    key: "operations",
    label: { en: "Operations & administration", ar: "التشغيل والإدارة" },
    dot: "var(--accent)",
  },
  {
    key: "clinical",
    label: { en: "Clinical & approvals", ar: "الرعاية السريرية والموافقات" },
    dot: "var(--st-info-fg)",
  },
  {
    key: "fulfillment",
    label: { en: "Fulfillment — lab, imaging & pharmacy", ar: "التنفيذ — المختبر والأشعة والصيدلية" },
    dot: "var(--st-part-fg)",
  },
];

export interface PortalDef {
  role: Role;
  /** URL base segment for the portal, e.g. `reception`. */
  base: string;
  title: Localized;
  /** Short role eyebrow shown in the page header. */
  eyebrow: Localized;
  /** Which band of the picker this portal sits in. */
  zone: ZoneKey;
  /** Glyph for the picker card's tile and the in-app switcher. */
  icon: IconName;
  /**
   * What this portal is FOR, in two lines, for somebody choosing between several. Written from the section
   * list below rather than from the role's title: "Reception" tells a receptionist nothing they did not
   * already know, and tells a clinics manager holding four portals nothing at all.
   */
  description: Localized;
  sections: Section[];
}

const G = {
  access: { en: "Patient Access", ar: "وصول المستفيد" },
  clinical: { en: "Clinical", ar: "سريري" },
  fulfillment: { en: "Fulfillment", ar: "التنفيذ" },
  dispensing: { en: "Dispensing", ar: "الصرف" },
  approvals: { en: "Approvals", ar: "الموافقات" },
  registration: { en: "Registration", ar: "التسجيل" },
  cases: { en: "Cases", ar: "الحالات" },
  contact: { en: "Contact Centre", ar: "مركز الاتصال" },
  claims: { en: "Claims", ar: "المطالبات" },
  finance: { en: "Finance", ar: "المالية" },
  network: { en: "Network", ar: "الشبكة" },
  admin: { en: "Administration", ar: "الإدارة" },
  oversight: { en: "Oversight", ar: "الإشراف" },
  insights: { en: "Insights", ar: "المؤشرات" },
  product: { en: "Benefit Product", ar: "منتج المنافع" },
  membership: { en: "Membership", ar: "العضوية" },
  branch: { en: "Clinic Management", ar: "إدارة العيادة" },
  // ADR-0035 — the parameters a supervisor sets that generate their own workload. Its own group rather than
  // folded into Oversight: overseeing the queue and setting the rules that fill it are different acts, and a
  // supervisor looking for "where do I change this" should not have to read past their dashboards.
  governance: { en: "Governance", ar: "الحوكمة" },
} satisfies Record<string, Localized>;

/**
 * 25.7 — THE BRANCH MANAGEMENT SECTIONS, declared ONCE and shared by both branch roles (design 42 §6).
 *
 * "Do not build two portals." Both PortalDef entries below reference this same array, so there is literally
 * one section list: a screen added for the coordinator is a screen the manager has, because it is the same
 * object. The two roles differ in exactly one visible way — the branch control SWITCHES for a coordinator
 * and FILTERS for a manager — and that is decided by reach in `useBranchContext`, not here.
 *
 * Reception's five verbatim, then the five that make this a clinic-management workspace.
 */
const BRANCH_SECTIONS: Section[] = [
  { key: "dashboard", path: "dashboard", label: { en: "Dashboard", ar: "لوحة المتابعة" }, group: G.access, icon: "chart", permission: "queue.reception" },
  { key: "eligibility", path: "eligibility", label: { en: "Eligibility Check", ar: "التحقق من الأهلية" }, group: G.access, icon: "user", permission: "eligibility.check" },
  { key: "appointments", path: "appointments", label: { en: "Appointments", ar: "المواعيد" }, group: G.access, icon: "clock", permission: "appointments.read" },
  { key: "book", path: "book", label: { en: "Book Appointment", ar: "حجز موعد" }, group: G.access, icon: "plus", permission: "appointments.book" },

  { key: "practitioners", path: "practitioners", label: { en: "Practitioners", ar: "الممارسون" }, group: G.branch, icon: "user", permission: "branch.practitioners" },
  { key: "roster", path: "roster", label: { en: "Roster & Availability", ar: "الجدول والإتاحة" }, group: G.branch, icon: "clock", permission: "branch.roster" },
  { key: "licence-alerts", path: "licence-alerts", label: { en: "Licence Alerts", ar: "تنبيهات التراخيص" }, group: G.branch, icon: "triangle", permission: "branch.licences" },
  { key: "inventory", path: "inventory", label: { en: "Inventory", ar: "المخزون" }, group: G.branch, icon: "flask", permission: "branch.inventory" },
  // Reach-scoped, not permission-scoped — see Section.reachScoped.
  { key: "branches", path: "branches", label: { en: "Branches Overview", ar: "نظرة عامة على الفروع" }, group: G.oversight, icon: "chart", permission: "queue.reception", reachScoped: true },
];

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
    zone: "operations",
    icon: "user",
    description: {
      en: "The front desk: check eligibility, work today's board and book appointments for the branch you are in.",
      ar: "مكتب الاستقبال: التحقق من الأهلية، ومتابعة قائمة اليوم، وحجز المواعيد للفرع الذي تعمل فيه.",
    },
    sections: [
      // 14.5 — the desk's landing page: how the day is going, who is in the building, what is still to come.
      { key: "dashboard", path: "dashboard", label: { en: "Dashboard", ar: "لوحة المتابعة" }, group: G.access, icon: "chart", permission: "queue.reception" },
      { key: "eligibility", path: "eligibility", label: { en: "Eligibility Check", ar: "التحقق من الأهلية" }, group: G.access, icon: "user", permission: "eligibility.check" },
      // "Today's Visits" was its own section until 14.5. It is now the dashboard's middle band, beside the
      // counts and the schedule that give it context — which is how the desk actually reads it.
      // Check-in was a separate section until 14.5. It was always the same server call against a filtered
      // view of THIS board, so the second screen only added somewhere for the two to disagree and a decision
      // about where to click before doing the work. It is now an action in the table, and the desk still
      // needs `checkin.write` to perform it — the server enforces that regardless of what the nav shows.
      { key: "appointments", path: "appointments", label: { en: "Appointments", ar: "المواعيد" }, group: G.access, icon: "clock", permission: "appointments.read" },
      { key: "book", path: "book", label: { en: "Book Appointment", ar: "حجز موعد" }, group: G.access, icon: "plus", permission: "appointments.book" },
    ],
  },
  {
    // 25.7 — ONE portal, two roles. Both entries share BRANCH_SECTIONS by reference (design 42 §6).
    role: "branch_coordinator",
    base: "branch",
    title: { en: "Clinic Management", ar: "إدارة العيادة" },
    eyebrow: { en: "Branch Coordinator", ar: "منسق الفرع" },
    zone: "operations",
    icon: "branch",
    description: {
      en: "Run one clinic: its front desk, its practitioners and roster, its licence alerts and its stock.",
      ar: "إدارة عيادة واحدة: استقبالها، وممارسوها وجدولهم، وتنبيهات تراخيصها، ومخزونها.",
    },
    sections: BRANCH_SECTIONS,
  },
  {
    role: "clinics_manager",
    base: "branch",
    title: { en: "Clinic Management", ar: "إدارة العيادة" },
    eyebrow: { en: "Clinics Manager", ar: "مدير العيادات" },
    zone: "operations",
    icon: "branch",
    description: {
      en: "The same clinic workspace across all six branches, with the overview that compares them.",
      ar: "نفس مساحة إدارة العيادة عبر الفروع الستة، مع النظرة العامة التي تقارن بينها.",
    },
    sections: BRANCH_SECTIONS,
  },
  {
    role: "doctor",
    base: "clinician",
    title: { en: "Consultation", ar: "الكشف" },
    eyebrow: { en: "Doctor", ar: "الطبيب" },
    zone: "clinical",
    icon: "stethoscope",
    description: {
      en: "See your patients, write the encounter, place orders and prescriptions, and read the results back.",
      ar: "متابعة مرضاك، وتوثيق اللقاء، وإصدار الطلبات والوصفات، ومراجعة النتائج.",
    },
    sections: [
      { key: "visits", path: "visits", label: { en: "My Visits", ar: "زياراتي" }, group: G.clinical, icon: "clock", permission: "appointments.read" },
      { key: "patients", path: "patients", label: { en: "My Patients", ar: "مرضاي" }, group: G.clinical, icon: "user", permission: "emr.read" },
      { key: "encounter", path: "encounter", label: { en: "Encounter Workspace", ar: "مساحة اللقاء" }, group: G.clinical, icon: "doc", permission: "emr.write" },
      { key: "orders", path: "orders", label: { en: "Orders", ar: "الطلبات" }, group: G.clinical, icon: "flask", permission: "orders.place" },
      { key: "prescriptions", path: "prescriptions", label: { en: "Prescriptions", ar: "الوصفات" }, group: G.clinical, icon: "pill", permission: "prescriptions.write" },
      { key: "results", path: "results", label: { en: "Results Inbox", ar: "صندوق النتائج" }, group: G.clinical, icon: "chart", permission: "results.inbox" },
      // 18.C2 (W4): requests to release a sensitive result the doctor authored. Same permission as the
      // results inbox — deciding who may see your result is part of owning it (37 §6).
      { key: "result-access", path: "result-access", label: { en: "Result Access Requests", ar: "طلبات الوصول للنتائج" }, group: G.clinical, icon: "clock", permission: "results.inbox" },
    ],
  },
  {
    role: "nurse",
    base: "nurse",
    title: { en: "Nursing", ar: "التمريض" },
    eyebrow: { en: "Nurse", ar: "الممرض/ة" },
    zone: "clinical",
    icon: "heart",
    description: {
      en: "Triage and vitals for the patients in front of you, with the results inbox that follows them.",
      ar: "الفرز والعلامات الحيوية للمرضى أمامك، مع صندوق النتائج المرتبط بهم.",
    },
    sections: [
      { key: "patients", path: "patients", label: { en: "My Patients", ar: "مرضاي" }, group: G.clinical, icon: "user", permission: "emr.read" },
      { key: "vitals", path: "vitals", label: { en: "Vitals & Triage", ar: "العلامات والفرز" }, group: G.clinical, icon: "chart", permission: "vitals.write" },
      { key: "results", path: "results", label: { en: "Results Inbox", ar: "صندوق النتائج" }, group: G.clinical, icon: "doc", permission: "results.inbox" },
    ],
  },
  {
    role: "lab",
    base: "lab",
    title: { en: "Laboratory", ar: "المختبر" },
    eyebrow: { en: "Laboratory", ar: "المختبر" },
    zone: "fulfillment",
    icon: "flask",
    description: {
      en: "The bench: find a patient's investigation order, consume it once, and upload the result.",
      ar: "المعمل: البحث عن طلب فحص المريض، وتنفيذه مرة واحدة، ورفع النتيجة.",
    },
    sections: [
      // 27.8 — "Order Queue" was removed, not renamed: it and "Consume Order" both routed to the SAME
      // component, so the rail offered one screen twice under two names. The same duplication the pharmacy
      // rail had. What is left is the bench, which now opens on a search for one patient rather than on a
      // browse of every patient's orders.
      { key: "consume", path: "consume", label: { en: "Perform Order", ar: "تنفيذ الطلب" }, group: G.fulfillment, icon: "flask", permission: "lab.consume" },
      { key: "result", path: "result", label: { en: "Upload Result", ar: "رفع النتيجة" }, group: G.fulfillment, icon: "doc", permission: "lab.result.upload" },
    ],
  },
  {
    role: "radiology",
    base: "radiology",
    title: { en: "Radiology", ar: "الأشعة" },
    eyebrow: { en: "Radiology", ar: "الأشعة" },
    zone: "fulfillment",
    icon: "eye",
    description: {
      en: "The imaging bench: find a patient's imaging order, consume it once, and upload the report.",
      ar: "قسم الأشعة: البحث عن طلب التصوير، وتنفيذه مرة واحدة، ورفع التقرير.",
    },
    sections: [
      // 27.8 — "Order Queue" was removed, not renamed: it and "Consume Order" both routed to the SAME
      // component, so the rail offered one screen twice under two names. The same duplication the pharmacy
      // rail had. What is left is the bench, which now opens on a search for one patient rather than on a
      // browse of every patient's orders.
      { key: "consume", path: "consume", label: { en: "Perform Order", ar: "تنفيذ الطلب" }, group: G.fulfillment, icon: "flask", permission: "radiology.consume" },
      { key: "result", path: "result", label: { en: "Upload Result", ar: "رفع النتيجة" }, group: G.fulfillment, icon: "doc", permission: "radiology.result.upload" },
    ],
  },
  {
    // 29.2b (design 45 §2b) — the EXTERNAL delivering provider's portal. Two entries only: the queue of work
    // routed to THIS centre, and the counter where the person present is verified behind two identifiers.
    // There is no "browse patients" and no result upload — a centre delivering physiotherapy needs neither.
    role: "procedure_provider",
    base: "procedure",
    title: { en: "Procedures", ar: "الإجراءات" },
    eyebrow: { en: "Delivery Centre", ar: "مركز التنفيذ" },
    zone: "fulfillment",
    icon: "check2",
    description: {
      en: "Your centre's queue of routed procedures, and the counter that verifies the person before delivery.",
      ar: "قائمة الإجراءات الموجَّهة إلى مركزك، ومنضدة التحقق من هوية الشخص قبل التنفيذ.",
    },
    sections: [
      { key: "queue", path: "queue", label: { en: "Our Queue", ar: "قائمة أعمالنا" }, group: G.fulfillment, icon: "flask", permission: "procedure.queue" },
      { key: "counter", path: "counter", label: { en: "Verify & Deliver", ar: "التحقق والتنفيذ" }, group: G.fulfillment, icon: "doc", permission: "procedure.deliver" },
    ],
  },
  {
    role: "pharmacy",
    base: "pharmacy",
    title: { en: "Pharmacy", ar: "الصيدلية" },
    eyebrow: { en: "Pharmacy", ar: "الصيدلية" },
    zone: "fulfillment",
    icon: "pill",
    description: {
      en: "The dispensing counter: find a prescription, dispense against it, and record any substitution.",
      ar: "منضدة الصرف: البحث عن الوصفة، وصرفها، وتسجيل أي بديل.",
    },
    sections: [
      // "Prescription Queue" was removed, not renamed: it and "Dispense" both routed to the SAME component,
      // so the rail offered one screen twice under two names. The remaining entry is the dispensing counter,
      // which now opens on a search rather than on a browse of every patient's prescriptions.
      { key: "dispense", path: "dispense", label: { en: "Dispense", ar: "الصرف" }, group: G.dispensing, icon: "pill", permission: "pharmacy.dispense" },
      { key: "substitutions", path: "substitutions", label: { en: "Substitutions", ar: "البدائل" }, group: G.dispensing, icon: "refer", permission: "pharmacy.substitution" },
    ],
  },
  {
    role: "medical_approval",
    base: "approvals",
    title: { en: "Approval Worklist", ar: "قائمة الموافقات" },
    eyebrow: { en: "Medical Approval", ar: "الموافقة الطبية" },
    zone: "clinical",
    icon: "check2",
    description: {
      en: "Decide what needs authorizing, with the register of every decision and the board that tracks turnaround.",
      ar: "البتّ فيما يحتاج تفويضًا، مع سجل كل قرار ولوحة متابعة زمن الاستجابة.",
    },
    sections: [
      { key: "worklist", path: "worklist", label: { en: "Worklist", ar: "قائمة العمل" }, group: G.approvals, icon: "check2", permission: "approvals.worklist" },
      // The register, alongside the queue but never inside it: one says "decide this", the other says "this
      // happened". ADR-0034 — a few hundred dispenses a day in the inbox would drown the decisions.
      { key: "authorizations", path: "authorizations", label: { en: "Authorizations", ar: "التفويضات" }, group: G.approvals, icon: "doc", permission: "approvals.register" },
      { key: "manual", path: "manual", label: { en: "Manual Authorization", ar: "تفويض يدوي" }, group: G.approvals, icon: "plus", permission: "approvals.manual" },
      { key: "emergency", path: "emergency", label: { en: "Emergency / Override", ar: "طارئ / تجاوز" }, group: G.approvals, icon: "triangle", permission: "approvals.emergency" },
      { key: "sla", path: "sla", label: { en: "SLA / TAT Board", ar: "لوحة الاستجابة" }, group: G.insights, icon: "chart", permission: "approvals.sla" },
    ],
  },
  {
    role: "beneficiary_mgmt",
    base: "beneficiaries",
    title: { en: "Beneficiary Management", ar: "إدارة المستفيدين" },
    eyebrow: { en: "Beneficiary Mgmt", ar: "إدارة المستفيدين" },
    zone: "operations",
    icon: "users",
    description: {
      en: "The beneficiary book: register new people, manage membership and groups, and run the bulk imports.",
      ar: "سجل المستفيدين: تسجيل أشخاص جدد، وإدارة العضوية والمجموعات، وتنفيذ الرفع الجماعي.",
    },
    /*
     * MEMBERSHIP FIRST, and therefore Beneficiaries is where a sign-in lands.
     *
     * The landing page is `accessible[0]` (AppShell), so section ORDER here is the landing decision — there is
     * no second place to set it, which is deliberate: a "default page" configured apart from the menu is a
     * default that drifts from the menu. The beneficiary book is what this role opens all day; registration is
     * an occasional errand by comparison, and it was on top only because it was built first.
     *
     * "Beneficiaries", not "Members": the organisation says beneficiary everywhere else in the product, and
     * this list is the same people the rest of the portal calls beneficiaries.
     *
     * Three sections were REMOVED rather than moved, each because it duplicated something better:
     *   · Search / Manage — a second, weaker search over the same registry this list already searches.
     *   · Status & Reactivation — its own screen for one action on one person. It is now a `Status change`
     *     button in the member's detail, beside Change plan, where the person is already on screen.
     *   · Utilization — now a tab in Analytics, which is where every other figure about a cohort lives.
     * The rail groups CONSECUTIVE runs, so the order below is also the group order; an out-of-place entry
     * renders a second heading with the same name (QA P1-9).
     */
    sections: [
      { key: "members", path: "members", label: { en: "Beneficiaries", ar: "المستفيدون" }, group: G.membership, icon: "user", permission: "policy.members" },
      { key: "groups", path: "groups", label: { en: "Groups", ar: "المجموعات" }, group: G.membership, icon: "refer", permission: "policy.groups" },
      { key: "bulk", path: "bulk", label: { en: "Bulk & Imports", ar: "الرفع الجماعي" }, group: G.membership, icon: "doc", permission: "policy.bulk" },
      { key: "register", path: "register", label: { en: "Register New", ar: "تسجيل جديد" }, group: G.registration, icon: "plus", permission: "beneficiary.register" },
      // US-003 — the officer PREPARES the application here (documents verified, coverage bound); the
      // decision buttons belong to the supervisor's portal below and the server enforces the split.
      { key: "approvals", path: "approvals", label: { en: "Registration Approvals", ar: "اعتماد التسجيلات" }, group: G.registration, icon: "check2", permission: "beneficiary.approvals" },
      { key: "eligibility", path: "eligibility", label: { en: "Eligibility Check", ar: "التحقق من الأهلية" }, group: G.access, icon: "check2", permission: "eligibility.check" },
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
    title: { en: "Registration Review", ar: "مراجعة التسجيلات" },
    eyebrow: { en: "Registration Supervisor", ar: "مشرف التسجيل" },
    zone: "operations",
    icon: "users",
    description: {
      en: "The officer's workspace plus the decision: approve or refuse the registrations the desk has prepared.",
      ar: "مساحة عمل الموظف بالإضافة إلى القرار: اعتماد أو رفض التسجيلات التي أعدّها المكتب.",
    },
    /*
     * THE SAME LIST as the officer's, in the same order. The supervisor's portal is the officer's plus the
     * decision, not a subset of it — a supervisor who cannot open the bulk import they are asked about, or
     * the analytics they report from, ends up borrowing an officer's screen, which is a worse audit trail
     * than giving them their own.
     *
     * What used to be withheld here was the register pen, as a separation of duties. That is still the rule;
     * it is simply enforced where it can actually hold: patient-service refuses a decision on a registration
     * the ACTOR filed (`urn:hbmp:self-approval`). A missing menu item never enforced it — the API was
     * reachable regardless — and it cost the supervisor half their job to pretend otherwise.
     */
    sections: [
      { key: "members", path: "members", label: { en: "Beneficiaries", ar: "المستفيدون" }, group: G.membership, icon: "user", permission: "policy.members" },
      { key: "groups", path: "groups", label: { en: "Groups", ar: "المجموعات" }, group: G.membership, icon: "refer", permission: "policy.groups" },
      { key: "bulk", path: "bulk", label: { en: "Bulk & Imports", ar: "الرفع الجماعي" }, group: G.membership, icon: "doc", permission: "policy.bulk" },
      { key: "register", path: "register", label: { en: "Register New", ar: "تسجيل جديد" }, group: G.registration, icon: "plus", permission: "beneficiary.register" },
      { key: "approvals", path: "approvals", label: { en: "Registration Approvals", ar: "اعتماد التسجيلات" }, group: G.registration, icon: "check2", permission: "beneficiary.approvals" },
      { key: "eligibility", path: "eligibility", label: { en: "Eligibility Check", ar: "التحقق من الأهلية" }, group: G.access, icon: "check2", permission: "eligibility.check" },
      { key: "analytics", path: "analytics", label: { en: "Analytics", ar: "التحليلات" }, group: G.insights, icon: "chart", permission: "policy.analytics" },
    ],
  },
  {
    role: "case_manager",
    base: "cases",
    title: { en: "Case Management", ar: "إدارة الحالات" },
    eyebrow: { en: "Case Manager", ar: "مدير الحالة" },
    zone: "operations",
    icon: "folder",
    description: {
      en: "Carry a caseload end to end: your cases, the whole picture of one beneficiary, and what has escalated.",
      ar: "متابعة الحالات من البداية للنهاية: حالاتك، والصورة الكاملة لمستفيد واحد، وما تم تصعيده.",
    },
    sections: [
      { key: "cases", path: "my-cases", label: { en: "My Cases", ar: "حالاتي" }, group: G.cases, icon: "doc", permission: "case.read" },
      { key: "beneficiary360", path: "beneficiary-360", label: { en: "Beneficiary 360", ar: "المستفيد 360" }, group: G.cases, icon: "user", permission: "case.beneficiary360" },
      { key: "escalations", path: "escalations", label: { en: "Escalations", ar: "التصعيدات" }, group: G.cases, icon: "triangle", permission: "case.escalations" },
    ],
  },
  {
    role: "call_center",
    base: "call-centre",
    title: { en: "Call Centre", ar: "مركز الاتصال" },
    eyebrow: { en: "Call Centre", ar: "مركز الاتصال" },
    zone: "operations",
    icon: "phone",
    description: {
      en: "Take the call, verify the caller, and book across any branch. No clinical data reaches this portal.",
      ar: "استقبال المكالمة، والتحقق من المتصل، والحجز في أي فرع. لا تصل أي بيانات سريرية إلى هذه البوابة.",
    },
    // No clinical routes exist here — min-necessary (the call centre gets no clinical data).
    sections: [
      { key: "workspace", path: "workspace", label: { en: "Call Workspace", ar: "مساحة المكالمة" }, group: G.contact, icon: "user", permission: "callcentre.workspace" },
      // Booking is the single most common reason a member rings, and in the workspace it is the fifth step of
      // a general-purpose call. Its own item makes the journey the agent actually has in front of them. It
      // does NOT skip verification: the screen opens its own call record and verifies inside itself, because
      // every reserve path in callcentre-service demands an interaction with a recorded PASS.
      { key: "book", path: "book", label: { en: "Book Appointment", ar: "حجز موعد" }, group: G.contact, icon: "plus", permission: "appointments.book" },
      // The cross-branch appointment board. Read-only: every reserve path needs a VERIFIED interaction, which
      // only exists inside a call, so the board points at the workspace rather than offering dead buttons.
      { key: "appointments", path: "appointments", label: { en: "Appointments", ar: "المواعيد" }, group: G.contact, icon: "clock", permission: "appointments.read" },
      { key: "history", path: "history", label: { en: "Call History", ar: "سجل المكالمات" }, group: G.contact, icon: "clock", permission: "callcentre.history" },
    ],
  },
  {
    role: "claims_officer",
    base: "claims",
    title: { en: "Claims Management", ar: "إدارة المطالبات" },
    eyebrow: { en: "Claims Officer", ar: "موظف المطالبات" },
    zone: "operations",
    icon: "scale",
    description: {
      en: "Work the claims queue and reconcile what providers billed. Codes and amounts only — never a diagnosis.",
      ar: "معالجة قائمة المطالبات وتسوية ما فوتره مقدمو الخدمة. الأكواد والمبالغ فقط — دون أي تشخيص.",
    },
    // No clinical/diagnosis routes exist here — min-necessary (claims sees codes + amounts, never a diagnosis).
    sections: [
      { key: "worklist", path: "worklist", label: { en: "Claims Worklist", ar: "قائمة المطالبات" }, group: G.claims, icon: "doc", permission: "claims.worklist" },
      { key: "reconciliation", path: "reconciliation", label: { en: "Reconciliation", ar: "التسوية" }, group: G.claims, icon: "check2", permission: "claims.reconciliation" },
      { key: "insights", path: "insights", label: { en: "Claims Insights", ar: "مؤشرات المطالبات" }, group: G.insights, icon: "chart", permission: "claims.insights" },
    ],
  },
  {
    role: "finance",
    base: "finance",
    title: { en: "Finance", ar: "المالية" },
    eyebrow: { en: "Finance", ar: "المالية" },
    zone: "operations",
    icon: "chart",
    description: {
      en: "Utilization, provider settlements, summaries and exports. This portal cannot reach a diagnosis at all.",
      ar: "الاستخدام، وتسويات مقدمي الخدمة، والملخصات، والتصدير. لا تصل هذه البوابة إلى أي تشخيص.",
    },
    // No clinical/diagnosis routes exist here — min-necessary (Finance cannot view diagnoses).
    sections: [
      { key: "utilization", path: "utilization", label: { en: "Utilization", ar: "الاستخدام" }, group: G.finance, icon: "chart", permission: "finance.utilization" },
      { key: "settlements", path: "settlements", label: { en: "Provider Settlements", ar: "تسويات مقدمي الخدمة" }, group: G.finance, icon: "doc", permission: "finance.settlements" },
      { key: "summaries", path: "summaries", label: { en: "Financial Summaries", ar: "ملخصات مالية" }, group: G.finance, icon: "check2", permission: "finance.summaries" },
      { key: "exports", path: "exports", label: { en: "Exports", ar: "التصدير" }, group: G.finance, icon: "refer", permission: "finance.export" },
      // 19.6b — the financial and network views are the money questions this role exists to answer. Still no
      // clinical route: the dashboard's fact tables carry no diagnosis column at all.
      { key: "analytics", path: "analytics", label: { en: "Analytics", ar: "التحليلات" }, group: G.insights, icon: "chart", permission: "policy.analytics" },
    ],
  },
  {
    role: "provider_admin",
    base: "network",
    title: { en: "Provider Network", ar: "شبكة مقدمي الخدمة" },
    eyebrow: { en: "Network Admin", ar: "إدارة الشبكة" },
    zone: "operations",
    icon: "globe",
    description: {
      en: "The provider directory: onboard centres, hold their contracts and tiers, and watch how they perform.",
      ar: "دليل مقدمي الخدمة: ضم المراكز، وإدارة عقودهم وشرائحهم، ومتابعة أدائهم.",
    },
    sections: [
      { key: "directory", path: "directory", label: { en: "Providers Directory", ar: "دليل مقدمي الخدمة" }, group: G.network, icon: "user", permission: "provider.directory" },
      { key: "onboarding", path: "onboarding", label: { en: "Onboarding", ar: "الانضمام" }, group: G.network, icon: "plus", permission: "provider.onboarding" },
      { key: "contracts", path: "contracts", label: { en: "Contracts & Coverage", ar: "العقود والتغطية" }, group: G.network, icon: "doc", permission: "provider.contracts" },
      { key: "locations", path: "locations", label: { en: "Locations & Users", ar: "المواقع والمستخدمون" }, group: G.network, icon: "check2", permission: "provider.locations" },
      // 14.5 — Mersal's own clinicians. Placed next to Locations & Users because it answers the same kind of
      // question (who works where), and immediately before Performance so the network group reads
      // directory → onboarding → contracts → places → people → how they are doing.
      { key: "practitioners", path: "practitioners", label: { en: "Doctors & Clinicians", ar: "الأطباء والإكلينيكيون" }, group: G.network, icon: "user", permission: "provider.practitioners" },
      { key: "performance", path: "performance", label: { en: "Performance", ar: "الأداء" }, group: G.insights, icon: "chart", permission: "provider.performance" },
      // Phase 19.6 (19.1b) — the Network Team owns the tier structure and who sits in it.
      { key: "tiers", path: "tiers", label: { en: "Network Tiers", ar: "شرائح الشبكة" }, group: G.network, icon: "half", permission: "network.tiers" },
    ],
  },
  {
    role: "policy_admin",
    base: "policy",
    title: { en: "Policy Administration", ar: "إدارة الوثائق التأمينية" },
    eyebrow: { en: "Policy Admin", ar: "مدير الوثائق" },
    zone: "operations",
    icon: "doc",
    description: {
      en: "Author the benefit product: payers, plans and their versions, policies, and who is covered by them.",
      ar: "تصميم منتج المنافع: الجهات الممولة، والخطط وإصداراتها، والوثائق، ومن تشمله التغطية.",
    },
    // No clinical route exists here — policy administration reads entitlement and money, never a diagnosis.
    sections: [
      { key: "payers", path: "payers", label: { en: "Payers", ar: "الجهات الممولة" }, group: G.product, icon: "user", permission: "policy.payers" },
      { key: "plans", path: "plans", label: { en: "Plans & Versions", ar: "الخطط والإصدارات" }, group: G.product, icon: "doc", permission: "policy.plans" },
      { key: "policies", path: "policies", label: { en: "Policies", ar: "الوثائق" }, group: G.product, icon: "check2", permission: "policy.policies" },
      { key: "members", path: "members", label: { en: "Members", ar: "الأعضاء" }, group: G.membership, icon: "user", permission: "policy.members" },
      { key: "groups", path: "groups", label: { en: "Groups", ar: "المجموعات" }, group: G.membership, icon: "refer", permission: "policy.groups" },
      { key: "utilization", path: "utilization", label: { en: "Utilization", ar: "الاستخدام" }, group: G.insights, icon: "chart", permission: "policy.utilization" },
      // 19.6b — the analytical layer over 19.1–19.5b. Served by reporting-service from a pre-aggregated read
      // model, never by querying the benefit spine the reception desk is using.
      { key: "analytics", path: "analytics", label: { en: "Analytics", ar: "التحليلات" }, group: G.insights, icon: "chart", permission: "policy.analytics" },
      { key: "bulk", path: "bulk", label: { en: "Bulk & Imports", ar: "الرفع الجماعي" }, group: G.membership, icon: "plus", permission: "policy.bulk" },
      // Read-only here: policy administration prices benefits AT a tier; the Network Team decides which tier
      // a provider sits in. Same section, different capability (see `mayAdministerTiers`).
      { key: "tiers", path: "tiers", label: { en: "Network Tiers", ar: "شرائح الشبكة" }, group: G.network, icon: "half", permission: "network.tiers" },
    ],
  },
  {
    role: "org_admin",
    base: "admin",
    title: { en: "Administration", ar: "الإدارة" },
    eyebrow: { en: "Org Admin", ar: "مدير المؤسسة" },
    zone: "operations",
    icon: "lock",
    description: {
      en: "Your organization's people and their access, plus master data, tenants, config and the audit trail.",
      ar: "أفراد مؤسستك وصلاحياتهم، إضافة إلى البيانات المرجعية والمستأجرين والإعدادات وسجل التدقيق.",
    },
    sections: [
      { key: "users", path: "users", label: { en: "Users & Roles", ar: "المستخدمون والأدوار" }, group: G.admin, icon: "user", permission: "admin.users" },
      { key: "policies", path: "policies", label: { en: "Permissions / Policies", ar: "الصلاحيات / السياسات" }, group: G.admin, icon: "check2", permission: "admin.policies" },
      { key: "masterdata", path: "master-data", label: { en: "Master Data", ar: "البيانات المرجعية" }, group: G.admin, icon: "doc", permission: "admin.masterdata" },
      { key: "tenants", path: "tenants", label: { en: "Tenants / Providers", ar: "المستأجرون / مقدمو الخدمة" }, group: G.admin, icon: "refer", permission: "admin.tenants" },
      { key: "audit", path: "audit", label: { en: "Audit & Access Reviews", ar: "التدقيق والمراجعات" }, group: G.oversight, icon: "clock", permission: "admin.audit" },
      { key: "config", path: "config", label: { en: "System Config", ar: "إعدادات النظام" }, group: G.admin, icon: "info", permission: "admin.config" },
      // 21.6 — memberships, exceptions, branch reach and the effective-access preview (design 40).
      { key: "access", path: "access", label: { en: "Users & Access", ar: "المستخدمون والصلاحيات" }, group: G.admin, icon: "user", permission: "admin.access" },
    ],
  },
  {
    role: "super_admin",
    base: "platform",
    title: { en: "Platform Administration", ar: "إدارة المنصة" },
    eyebrow: { en: "Super Admin", ar: "مدير المنصة" },
    zone: "operations",
    icon: "lock",
    description: {
      en: "The platform itself: every tenant's people and access, and the programme enablement Mersal alone sets.",
      ar: "المنصة نفسها: أفراد كل مستأجر وصلاحياتهم، وتفعيل البرامج الذي تحدده مرسال وحدها.",
    },
    sections: [
      { key: "users", path: "users", label: { en: "Users & Roles", ar: "المستخدمون والأدوار" }, group: G.admin, icon: "user", permission: "admin.users" },
      { key: "policies", path: "policies", label: { en: "Permissions / Policies", ar: "الصلاحيات / السياسات" }, group: G.admin, icon: "check2", permission: "admin.policies" },
      { key: "masterdata", path: "master-data", label: { en: "Master Data", ar: "البيانات المرجعية" }, group: G.admin, icon: "doc", permission: "admin.masterdata" },
      { key: "tenants", path: "tenants", label: { en: "Tenants / Providers", ar: "المستأجرون / مقدمو الخدمة" }, group: G.admin, icon: "refer", permission: "admin.tenants" },
      { key: "audit", path: "audit", label: { en: "Audit & Access Reviews", ar: "التدقيق والمراجعات" }, group: G.oversight, icon: "clock", permission: "admin.audit" },
      { key: "config", path: "config", label: { en: "System Config", ar: "إعدادات النظام" }, group: G.admin, icon: "info", permission: "admin.config" },
      { key: "access", path: "access", label: { en: "Users & Access", ar: "المستخدمون والصلاحيات" }, group: G.admin, icon: "user", permission: "admin.access" },
      // Platform administration only — programme enablement is set by Mersal, never by the tenant, so it
      // appears on this portal alone. The hiding is cosmetic; the API requires the platform-admin role.
      { key: "programs", path: "programs", label: { en: "Programme Enablement", ar: "تفعيل البرامج" }, group: G.admin, icon: "check2", permission: "admin.programs" },
    ],
  },
  {
    role: "medical_director",
    base: "director",
    title: { en: "Medical Director", ar: "المدير الطبي" },
    eyebrow: { en: "Medical Director", ar: "المدير الطبي" },
    zone: "clinical",
    icon: "gauge",
    description: {
      en: "Clinical oversight: dashboards, approval turnaround, quality and outcomes, and the rules that shape them.",
      ar: "الإشراف السريري: اللوحات، وزمن استجابة الموافقات، والجودة والنتائج، والقواعد التي تحكمها.",
    },
    sections: [
      { key: "dashboards", path: "dashboards", label: { en: "Clinical Dashboards", ar: "لوحات سريرية" }, group: G.insights, icon: "chart", permission: "director.dashboards" },
      { key: "oversight", path: "oversight", label: { en: "Approval Oversight / TAT", ar: "الإشراف على الموافقات" }, group: G.oversight, icon: "check2", permission: "director.oversight" },
      { key: "quality", path: "quality", label: { en: "Quality & Outcomes", ar: "الجودة والنتائج" }, group: G.oversight, icon: "doc", permission: "director.quality" },
      { key: "escalations", path: "escalations", label: { en: "Escalations", ar: "التصعيدات" }, group: G.oversight, icon: "triangle", permission: "director.escalations" },
      // 18.C2 (W4): the ESCALATION path for sensitive-result release — 37 §6 lets the Medical Director decide
      // when the authoring doctor is unavailable, which is the case the whole mechanism exists to cover.
      { key: "result-access", path: "result-access", label: { en: "Result Access Requests", ar: "طلبات الوصول للنتائج" }, group: G.oversight, icon: "clock", permission: "director.escalations" },
      // How long a prescription or an order stays actionable. It sits under oversight rather than with the
      // platform settings because it is a clinical safety judgement whose consequence — every extension
      // request a short window produces — lands in this same person's approval queue.
      { key: "validity", path: "validity", label: { en: "Validity Periods", ar: "مدد الصلاحية" }, group: G.oversight, icon: "clock", permission: "director.oversight" },
      // ADR-0035 §3 — governance. The supervisor sets the parameters that generate their own workload, and
      // a mis-mapped clinical code is one of them: it misroutes a diagnosis into this same approval queue.
      { key: "master-lists", path: "master-lists", label: { en: "Master Lists", ar: "القوائم المرجعية" }, group: G.governance, icon: "folder", permission: "director.masterlists" },
      // ADR-0035 §6 — beside Validity Periods rather than under it: one is how long a clinical DECISION stays
      // actionable, the other how long a DOCUMENT stays current. Different judgements, different consequences.
      { key: "document-validity", path: "document-validity", label: { en: "Document Validity", ar: "صلاحية المستندات" }, group: G.governance, icon: "doc", permission: "director.oversight" },
      // ADR-0035 §5 — routing and SLA rules. Its own permission because authoring the rule that shapes a
      // thousand cases is a different power from deciding one; a reviewer holds neither key.
      { key: "engine", path: "engine", label: { en: "Approvals Engine", ar: "محرك الموافقات" }, group: G.governance, icon: "toggle", permission: "director.engine" },
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

/**
 * Every portal the caller is entitled to, in catalog order.
 *
 * Deduped by BASE and not by role, which is the whole subtlety: `branch_coordinator` and `clinics_manager`
 * are two roles over one portal (`/branch`), and somebody promoted from the first to the second holds both
 * for as long as the grant takes to be tidied. Keying on role would offer them the same clinic workspace
 * twice under two eyebrows, and the second card would go to the same URL as the first.
 *
 * The FIRST match wins, and `PORTALS` is ordered by the same priority `ROLE_MAP` uses, so the pair resolves
 * to the coordinator's entry... which is wrong for a manager. Hence the explicit ordering below: a caller
 * holding both gets the wider reach, matching `BranchScope.ModeFor` on the server and `ROLE_MAP`'s own
 * comment about why set-before-single is load-bearing.
 */
export function portalsForRoles(roles: readonly Role[]): PortalDef[] {
  const held = new Set(roles);
  const out: PortalDef[] = [];
  const seenBase = new Set<string>();
  for (const portal of PORTALS) {
    if (!held.has(portal.role)) continue;
    // Two roles, one base: prefer the wider one. `clinics_manager` is declared after `branch_coordinator`,
    // so without this the narrower entry would claim the base and the manager would be shown as a
    // coordinator — the exact narrowing ROLE_MAP orders itself to avoid.
    if (seenBase.has(portal.base)) {
      if (portal.role === "clinics_manager" && held.has("branch_coordinator")) {
        const i = out.findIndex((p) => p.base === portal.base);
        if (i >= 0) out[i] = portal;
      }
      continue;
    }
    seenBase.add(portal.base);
    out.push(portal);
  }
  return out;
}

/**
 * The portal that OWNS a URL base segment, e.g. `admin` → the org-admin portal.
 *
 * Ambiguous by construction — `beneficiaries` is two portals and `branch` is two — so the caller's held
 * roles decide. Without them this returns the first declared, which is only ever used for a label.
 */
export function portalForBase(base: string, roles?: readonly Role[]): PortalDef | undefined {
  if (roles) {
    const mine = portalsForRoles(roles).find((p) => p.base === base);
    if (mine) return mine;
  }
  return PORTALS.find((p) => p.base === base);
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

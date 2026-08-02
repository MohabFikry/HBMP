import { useCallback, useMemo, useState, type ReactNode } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import {
  Button,
  DataTable,
  Icon,
  InlineAlert,
  Modal,
  StatusChip,
  Tabs,
  type Column,
} from "@mersal/design-system";
import type {
  AuthorizationRow,
  CaseRow,
  CodedCondition,
  CoordinationTaskRow,
  CoverageLimitLine,
  DocumentRow,
  Encounter,
  EncounterRow,
  EscalationRow,
  FinancialClaimRow,
  HistoricalRecord,
  InvestigationRow,
  Localized,
  NoteRow,
  ProfileAuthorizations,
  ProfileCaseManagement,
  ProfileCoverage,
  ProfileDocuments,
  ProfileEncounters,
  ProfileFinancial,
  ProfileInvestigations,
  PatientProfile,
  ProfileNotes,
  ProfilePastMedicalHistory,
  ProfilePrescriptions,
  ProfileReferrals,
  ProfileSection,
  ProfileTimeline,
  ReferralRow,
  ProfileRxRow,
  TimelineRow,
} from "@mersal/contracts";
import type { PolicyApi } from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";
import { useFormat } from "../i18n/useFormat";
import { useLoc } from "./_shared";

/**
 * The audited signed-URL endpoint lives on `PolicyApi`, not the portal `ApiClient` — same instance every other
 * document surface uses (`BeneficiaryDocuments`, `PolicyBulk`), constructed once at module scope per the house
 * pattern so a test can still inject a fake.
 */
const httpPolicyApi = createHttpPolicyApi();

/**
 * Phase 20.4 — the designed views for profile sections 3–14 (design 39 §3).
 *
 * <b>Why this file exists.</b> The profile screen shipped with bespoke renderers for three sections — identity,
 * alerts and call history — and sent the other twelve through a generic key/value dumper. That dumper printed
 * raw camelCase JSON keys as labels (so an Arabic user read <i>costSharePercent</i>), flattened list rows into
 * one run-on string, and — the part that mattered — filtered out every value whose <c>typeof</c> was "object".
 * Coverage's per-category limits, a prescription's lines, a case's tasks: all nested, all silently dropped. A
 * section whose payload was <em>entirely</em> nested had nothing left to print and rendered as "No records",
 * which is the one thing design 39 §6 forbids — real data wearing the costume of absence.
 *
 * <b>The rule every view here follows: an absent field renders as nothing, never as a blank.</b> Each section
 * has variant projections server-side that null out whole fields per role, and the serializer omits nulls. So
 * absence means "not served at your access level", and the honest rendering of that is silence. A dash or an
 * empty cell says "recorded as nothing", which is a different and untrue claim — and for a reception user
 * looking at an encounter with its clinical reason stripped, it is the difference between "not your zone" and
 * "the doctor wrote nothing down".
 *
 * That principle has a structural consequence worth naming: <b>columns are built from the rows actually
 * served</b> (see {@link anyHas}). A Reason column that is empty on every row is not a column, it is a
 * question the screen cannot answer, so it is not rendered at all.
 *
 * <b>No role logic lives here.</b> Same reason as the parent screen: the server decided what this caller may
 * see, and a second decision made in the browser is the one an attacker gets to influence. These views render
 * what arrived and nothing else.
 */

// ---------------------------------------------------------------- strings

/** Field labels. Every `<dt>` and every column header comes from here — never from a JSON key. */
const L = {
  // shared
  status: { en: "Status", ar: "الحالة" },
  empty: { en: "No records", ar: "لا توجد سجلات" },
  ref: { en: "Reference", ar: "المرجع" },
  date: { en: "Date", ar: "التاريخ" },
  notServed: { en: "Not available at your access level", ar: "غير متاح لمستوى وصولك" },

  // coverage
  payer: { en: "Payer", ar: "الجهة الممولة" },
  policyNo: { en: "Policy no.", ar: "رقم البوليصة" },
  plan: { en: "Plan", ar: "الخطة" },
  effectiveFrom: { en: "Effective from", ar: "سارية من" },
  effectiveTo: { en: "Effective to", ar: "سارية إلى" },
  waitingPeriod: { en: "Waiting period", ar: "فترة الانتظار" },
  limitsCaption: { en: "Benefit limits by category", ar: "حدود المزايا حسب الفئة" },
  category: { en: "Category", ar: "الفئة" },
  annualLimit: { en: "Annual limit", ar: "الحد السنوي" },
  consumed: { en: "Consumed", ar: "المستهلك" },
  remaining: { en: "Remaining", ar: "المتبقي" },
  costShare: { en: "Cost share", ar: "مشاركة التكلفة" },
  exhausted: { en: "Exhausted", ar: "مستنفد" },
  low: { en: "Low", ar: "منخفض" },

  // past medical history
  conditionsCaption: { en: "Recorded conditions", ar: "الحالات المسجّلة" },
  condition: { en: "Condition", ar: "الحالة المرضية" },
  code: { en: "Code", ar: "الترميز" },
  onset: { en: "Onset", ar: "تاريخ البدء" },
  narrative: { en: "History narrative", ar: "سرد التاريخ المرضي" },
  uploadedRecords: { en: "Historical records", ar: "سجلات سابقة" },

  // encounters
  encountersCaption: { en: "Encounters", ar: "الزيارات" },
  occurredAt: { en: "When", ar: "التاريخ والوقت" },
  branch: { en: "Branch", ar: "الفرع" },
  clinician: { en: "Clinician", ar: "الطبيب" },
  specialty: { en: "Specialty", ar: "التخصص" },
  reason: { en: "Reason", ar: "سبب الزيارة" },
  view: { en: "View", ar: "عرض" },
  viewEncounter: { en: "View visit details", ar: "عرض تفاصيل الزيارة" },
  encounterDetails: { en: "Visit details", ar: "تفاصيل الزيارة" },
  openEncounter: { en: "Open encounter", ar: "فتح الزيارة" },
  close: { en: "Close", ar: "إغلاق" },
  // the visit-details modal's tabs and its three clinical panes
  tabVisit: { en: "Visit", ar: "الزيارة" },
  tabNote: { en: "Note", ar: "الملاحظة" },
  tabDiagnoses: { en: "Diagnoses", ar: "التشخيصات" },
  tabVitals: { en: "Vitals", ar: "العلامات الحيوية" },
  tabOrders: { en: "Orders", ar: "الطلبات" },
  investigationsOn: { en: "Investigations ordered on this visit", ar: "الفحوصات المطلوبة في هذه الزيارة" },
  rxOn: { en: "Prescriptions written on this visit", ar: "الوصفات المكتوبة في هذه الزيارة" },
  noOrdersOnVisit: { en: "No investigation was ordered on this visit.", ar: "لم يُطلب أي فحص في هذه الزيارة." },
  noRxOnVisit: { en: "No prescription was written on this visit.", ar: "لم تُكتب أي وصفة في هذه الزيارة." },
  ordersUnavailable: {
    en: "Orders for a single visit cannot be listed here — the visit's reference was not included in your view of this record.",
    ar: "لا يمكن عرض طلبات زيارة واحدة هنا — لم يُدرج مرجع الزيارة في عرضك لهذا السجل.",
  },
  restrictedSection: {
    en: "This part of the record is not available at your access level.",
    ar: "هذا الجزء من السجل غير متاح لمستوى وصولك.",
  },
  restrictedResult: { en: "Result restricted", ar: "النتيجة مقيّدة" },
  loading: { en: "Loading…", ar: "جارٍ التحميل…" },
  // "Restricted", not "empty". The record exists and this caller may not read it; showing a blank note
  // instead would tell a clinician the visit was never documented.
  encounterRestricted: {
    en: "The clinical record for this visit is available to the treating clinician and the approval team.",
    ar: "السجل السريري لهذه الزيارة متاح للطبيب المعالج وفريق الموافقات.",
  },
  encounterUnavailable: {
    en: "The clinical record could not be loaded.",
    ar: "تعذّر تحميل السجل السريري.",
  },
  subjective: { en: "Subjective", ar: "الشكوى" },
  objective: { en: "Objective", ar: "الفحص" },
  assessment: { en: "Assessment", ar: "التقييم" },
  // `soapPlan`, not `plan` — `plan` above is the COVERAGE plan. The two happen to share a word in both
  // languages and mean entirely different things, and one key serving both is how a benefit plan name ends
  // up labelling a treatment plan the first time either wording is improved.
  soapPlan: { en: "Plan", ar: "الخطة" },
  noNote: { en: "No note was written on this visit.", ar: "لم تُكتب ملاحظة في هذه الزيارة." },
  noDiagnoses: { en: "No diagnosis was coded on this visit.", ar: "لم يُسجَّل تشخيص في هذه الزيارة." },
  noVitals: { en: "No vitals were recorded on this visit.", ar: "لم تُسجَّل علامات حيوية في هذه الزيارة." },
  measuredAt: { en: "Measured", ar: "وقت القياس" },
  bp: { en: "Blood pressure", ar: "ضغط الدم" },
  hr: { en: "Heart rate", ar: "النبض" },
  temp: { en: "Temperature", ar: "الحرارة" },
  spo2: { en: "Oxygen saturation", ar: "تشبع الأكسجين" },
  height: { en: "Height", ar: "الطول" },
  weight: { en: "Weight", ar: "الوزن" },

  // investigations
  investigationsCaption: { en: "Investigation orders and results", ar: "طلبات الفحوصات والنتائج" },
  orderedOn: { en: "Ordered", ar: "تاريخ الطلب" },
  provider: { en: "Provider", ar: "مقدم الخدمة" },
  result: { en: "Result", ar: "النتيجة" },
  resultRestricted: { en: "Sensitivity-restricted", ar: "مقيّدة الحساسية" },
  awaitingResult: { en: "Awaiting result", ar: "بانتظار النتيجة" },

  // prescriptions
  prescriptionsCaption: { en: "Prescriptions and dispensing", ar: "الوصفات والصرف" },
  drug: { en: "Medicine", ar: "الدواء" },
  prescribedOn: { en: "Prescribed", ar: "تاريخ الوصف" },
  dispensedOn: { en: "Dispensed", ar: "تاريخ الصرف" },
  batchNo: { en: "Batch", ar: "التشغيلة" },
  expiry: { en: "Expiry", ar: "تاريخ الانتهاء" },
  expired: { en: "Expired", ar: "منتهي الصلاحية" },
  substituted: { en: "Substituted with", ar: "مستبدل بـ" },

  // authorizations
  authorizationsCaption: { en: "Authorization requests and decisions", ar: "طلبات وقرارات الموافقة" },
  authNo: { en: "Authorization no.", ar: "رقم الموافقة" },
  serviceCategory: { en: "Service", ar: "الخدمة" },
  requestedAt: { en: "Requested", ar: "تاريخ الطلب" },
  decidedAt: { en: "Decided", ar: "تاريخ القرار" },
  validUntil: { en: "Valid until", ar: "صالحة حتى" },
  approvedAmount: { en: "Approved amount", ar: "المبلغ المعتمد" },
  rationale: { en: "Rationale", ar: "المبرر" },

  // referrals
  referralsCaption: { en: "Referrals", ar: "الإحالات" },
  requestedSpecialty: { en: "To specialty", ar: "إلى تخصص" },
  createdAt: { en: "Raised", ar: "تاريخ الإنشاء" },
  loop: { en: "Referral loop", ar: "دورة الإحالة" },
  loopClosed: { en: "Closed", ar: "مغلقة" },
  loopOpen: { en: "Open", ar: "مفتوحة" },

  // documents
  documentsCaption: { en: "Documents", ar: "المستندات" },
  title: { en: "Title", ar: "العنوان" },
  documentClass: { en: "Class", ar: "التصنيف" },
  visibility: { en: "Visibility", ar: "نطاق الظهور" },
  uploadedAt: { en: "Uploaded", ar: "تاريخ الرفع" },
  download: { en: "Download", ar: "تنزيل" },
  downloadFailed: { en: "The document could not be opened.", ar: "تعذّر فتح المستند." },

  // notes
  notesCaption: { en: "Notes", ar: "الملاحظات" },
  noteType: { en: "Type", ar: "النوع" },
  author: { en: "Author", ar: "الكاتب" },
  pinned: { en: "Pinned", ar: "مثبّتة" },
  noteWithheld: {
    en: "This note exists. Its content is outside your visibility class.",
    ar: "هذه الملاحظة موجودة. محتواها خارج نطاق ظهورك.",
  },

  // financial
  costShareOwed: { en: "Cost share owed", ar: "مشاركة التكلفة المستحقة" },
  settlement: { en: "Settlement", ar: "التسوية" },
  claimsCaption: { en: "Claims", ar: "المطالبات" },
  claimNo: { en: "Claim no.", ar: "رقم المطالبة" },
  serviceDate: { en: "Service date", ar: "تاريخ الخدمة" },
  billed: { en: "Billed", ar: "المفوتر" },
  approved: { en: "Approved", ar: "المعتمد" },
  memberShare: { en: "Member share", ar: "حصة المستفيد" },

  // case management
  cases: { en: "Cases", ar: "الحالات" },
  casesCaption: { en: "Assigned cases", ar: "الحالات المكلّف بها" },
  caseNo: { en: "Case no.", ar: "رقم الحالة" },
  openedAt: { en: "Opened", ar: "تاريخ الفتح" },
  tasks: { en: "Coordination tasks", ar: "مهام التنسيق" },
  tasksCaption: { en: "Coordination tasks", ar: "مهام التنسيق" },
  task: { en: "Task", ar: "المهمة" },
  dueOn: { en: "Due", ar: "تاريخ الاستحقاق" },
  overdue: { en: "Overdue", ar: "متأخرة" },
  escalations: { en: "Escalations", ar: "التصعيدات" },
  escalationsCaption: { en: "Escalations", ar: "التصعيدات" },

  // timeline
  timelineCaption: { en: "Change and access history", ar: "سجل التغييرات والوصول" },
  event: { en: "Event", ar: "الحدث" },
  actor: { en: "By", ar: "بواسطة" },
  summary: { en: "Detail", ar: "التفصيل" },
  source: { en: "Source", ar: "المصدر" },
} satisfies Record<string, Localized>;

/**
 * Status vocabulary → Arabic.
 *
 * <b>Unknown statuses pass through in English rather than being hidden or guessed at.</b> The vocabularies come
 * from `23-state-machines.md` and the owning services, and they grow; a lexicon that swallowed anything it did
 * not recognise would turn a new state into a blank chip. Passing the raw token through is visibly imperfect,
 * which is the correct failure mode for a translation gap — someone notices and adds the word.
 *
 * Deliberately NOT mapped through the API layer like every other screen's chips: this payload reaches the
 * screen exactly as the server projected it, and reshaping statuses in `HttpApiClient` would put a second
 * opinion about section content between the server's decision and the render.
 */
const STATUS_AR: Record<string, string> = {
  // membership / coverage
  active: "نشط",
  inactive: "غير نشط",
  suspended: "موقوف",
  blocked: "محظور",
  expired: "منتهٍ",
  pending: "قيد الانتظار",
  served: "مستوفاة",
  waived: "مُعفاة",
  // encounters / appointments
  booked: "محجوز",
  scheduled: "مُجدول",
  checkedin: "تم الوصول",
  inconsultation: "في الاستشارة",
  completed: "مكتمل",
  cancelled: "ملغى",
  noshow: "لم يحضر",
  waitlisted: "قائمة انتظار",
  // orders / results
  ordered: "تم الطلب",
  collected: "تم السحب",
  resulted: "صدرت النتيجة",
  fulfilled: "مُنفَّذ",
  verified: "تم التحقق",
  // prescriptions
  dispensed: "مصروف",
  partiallydispensed: "مصروف جزئيًا",
  // approvals
  draft: "مسودة",
  submitted: "مُرسَل",
  requested: "مطلوب",
  underreview: "قيد المراجعة",
  clinicalreview: "مراجعة طبية",
  manualassessment: "تقييم يدوي",
  inforequested: "طُلبت معلومات",
  pendinginfo: "بانتظار معلومات",
  pendingapproval: "بانتظار الموافقة",
  decided: "تم القرار",
  approved: "موافَق عليه",
  partiallyapproved: "موافقة جزئية",
  accepted: "مقبول",
  rejected: "مرفوض",
  denied: "غير موافَق عليه",
  // referrals / cases
  open: "مفتوحة",
  closed: "مغلقة",
  resolved: "تم الحل",
  escalated: "تم التصعيد",
  archived: "مؤرشف",
  // claims
  adjudicating: "قيد التسوية",
  underadjudication: "قيد التسوية",
  appealed: "مُستأنف",
  settlementissued: "صدرت التسوية",
  settled: "مسوّى",
  paid: "مدفوع",
  automatched: "مطابقة تلقائية",
  ocrprocessing: "معالجة ضوئية",
};

// ---------------------------------------------------------------- shared primitives

type StatusKind = "ok" | "info" | "part" | "warn" | "bad" | "neu";

const STATUS_KIND: Record<string, StatusKind> = {
  active: "ok", completed: "ok", approved: "ok", accepted: "ok", dispensed: "ok", fulfilled: "ok",
  resolved: "ok", closed: "ok", verified: "ok", settled: "ok", paid: "ok", served: "ok", resulted: "ok",
  waived: "ok",

  pending: "info", draft: "info", submitted: "info", requested: "info", ordered: "info", booked: "info",
  scheduled: "info", checkedin: "info", inconsultation: "info", underreview: "info", clinicalreview: "info",
  manualassessment: "info", adjudicating: "info", underadjudication: "info", open: "info", collected: "info",
  ocrprocessing: "info", automatched: "info", pendingapproval: "info",

  partiallyapproved: "part", partiallydispensed: "part", settlementissued: "part",

  suspended: "warn", expired: "warn", noshow: "warn", inforequested: "warn", pendinginfo: "warn",
  escalated: "warn", appealed: "warn", waitlisted: "warn",

  cancelled: "bad", rejected: "bad", denied: "bad", blocked: "bad",

  inactive: "neu", archived: "neu",
};

/** `PartiallyApproved` → `partiallyapproved`, so one lookup serves every casing the services use. */
const norm = (s: string) => s.toLowerCase().replace(/[^a-z]/g, "");

function statusKind(status: string): StatusKind {
  return STATUS_KIND[norm(status)] ?? "neu";
}

/**
 * A status chip with all four cues, its label translated where the vocabulary is known.
 *
 * The chip is the only place a raw server token is allowed to surface, and only as a fallback — see
 * {@link STATUS_AR}.
 */
function Status({ status }: { status: string }) {
  const t = useLoc();
  const ar = STATUS_AR[norm(status)];
  const label = t({ en: status, ar: ar ?? status });
  return <StatusChip kind={statusKind(status)} label={label} />;
}

/** Is this field worth a column? True only if at least one served row actually carries a value. */
function anyHas<Row>(rows: Row[], pick: (row: Row) => unknown): boolean {
  return rows.some((row) => {
    const v = pick(row);
    return v !== null && v !== undefined && v !== "";
  });
}

type MaybeColumn<Row> = Column<Row> | false | null | undefined;

/**
 * Drop the columns whose field no served row carries.
 *
 * This is the projection story made visible: a receptionist's encounter rows arrive with `reason` stripped, so
 * the Reason column does not exist on their screen — rather than existing and being empty down its whole
 * length, which reads as "no reason was recorded" (design 39 §4's `V(meta)`).
 */
function columns<Row>(...list: MaybeColumn<Row>[]): Column<Row>[] {
  return list.filter((c): c is Column<Row> => Boolean(c));
}

interface Fact {
  label: Localized;
  value: ReactNode;
}

type MaybeFact = Fact | false | null | undefined;

/**
 * A translated definition list. Entries whose value is absent are <b>omitted</b>, not blanked: the field was
 * never served, and an empty `<dd>` is an assertion that it was served empty.
 */
function Facts({ facts }: { facts: MaybeFact[] }) {
  const t = useLoc();
  const kept = facts.filter((f): f is Fact => {
    if (!f) return false;
    return f.value !== null && f.value !== undefined && f.value !== "";
  });
  if (kept.length === 0) return null;

  return (
    <dl className="profile-facts">
      {kept.map((f) => (
        <div key={f.label.en}>
          <dt>{t(f.label)}</dt>
          <dd>{f.value}</dd>
        </div>
      ))}
    </dl>
  );
}

/** A labelled sub-group inside a section — case management's three lists, history's narrative and records. */
function Group({ heading, children }: { heading: Localized; children: ReactNode }) {
  const t = useLoc();
  return (
    <section className="profile-group">
      <h3 className="profile-group-head">{t(heading)}</h3>
      {children}
    </section>
  );
}

function Empty() {
  const t = useLoc();
  return <p className="profile-empty">{t(L.empty)}</p>;
}

/**
 * Money in the section's own currency.
 *
 * `useFormat().money` is pinned to EGP, which is right for every screen that deals in Mersal's own ledger. A
 * coverage or financial section carries an explicit `currency`, and formatting a USD balance with an EGP symbol
 * would misstate an amount rather than merely mis-style it. `fmt.locale` is exposed for exactly this case.
 */
function useMoney(currency?: string) {
  const fmt = useFormat();
  return useMemo(() => {
    if (!currency || currency.toUpperCase() === "EGP") return fmt.money;
    let custom: Intl.NumberFormat;
    try {
      custom = new Intl.NumberFormat(fmt.locale, { style: "currency", currency });
    } catch {
      // An unrecognised currency code is the server's bug, not a reason to render nothing. Fall back to the
      // app default rather than throwing inside a table cell.
      return fmt.money;
    }
    return (v: number | null | undefined) =>
      typeof v === "number" && Number.isFinite(v) ? custom.format(v) : "—";
  }, [currency, fmt]);
}

// ---------------------------------------------------------------- 3. coverage

/**
 * Coverage & eligibility (design 39 §3 row 3).
 *
 * The per-category limits are the whole point of this section — "how much dental is left" is the question a
 * receptionist, a pharmacist and the member all arrive with — and they were the part the generic renderer
 * dropped on the floor for being nested.
 */
function CoverageView({ data }: { data: ProfileCoverage }) {
  const t = useLoc();
  const fmt = useFormat();
  const money = useMoney();
  const rows = data.categories ?? [];

  const plan =
    data.planLabel === undefined
      ? undefined
      : data.planVersion === undefined
        ? data.planLabel
        : `${data.planLabel} · v${fmt.number(data.planVersion)}`;

  const cols = columns<CoverageLimitLine>(
    { key: "category", header: t(L.category), cell: (r) => r.category,
      sortable: true, sortValue: (r) => r.category },
    anyHas(rows, (r) => r.annualLimit) && {
      key: "limit", header: t(L.annualLimit), cell: (r) => money(r.annualLimit),
      sortable: true, sortValue: (r) => r.annualLimit,
    },
    anyHas(rows, (r) => r.consumed) && {
      key: "consumed", header: t(L.consumed), cell: (r) => money(r.consumed),
      sortable: true, sortValue: (r) => r.consumed,
    },
    anyHas(rows, (r) => r.remaining) && {
      key: "remaining", header: t(L.remaining),
      // The number carries the fact; the chip beside it carries the urgency, with hue + icon + shape + word.
      cell: (r) => (
        <span className="profile-remaining">
          <span>{money(r.remaining)}</span>
          <LimitCue line={r} />
        </span>
      ),
      sortable: true, sortValue: (r) => r.remaining,
    },
    anyHas(rows, (r) => r.costSharePercent ?? r.costShareTier) && {
      key: "share", header: t(L.costShare), cell: (r) => costShare(r, fmt.number),
      sortable: true, sortValue: (r) => r.costSharePercent,
    },
  );

  return (
    <div className="profile-stack">
      <Facts
        facts={[
          { label: L.payer, value: data.payerName },
          { label: L.policyNo, value: data.policyNo },
          { label: L.plan, value: plan },
          data.effectiveFrom !== undefined && { label: L.effectiveFrom, value: fmt.date(data.effectiveFrom) },
          data.effectiveTo !== undefined && { label: L.effectiveTo, value: fmt.date(data.effectiveTo) },
          data.waitingPeriodState !== undefined && {
            label: L.waitingPeriod,
            // "None" is not a state, so it does not get a chip — the same rule the membership roster now
            // follows. A chip on every profile for the members who have NO waiting period spends the one
            // loud element in this row on the answer "nothing applies here".
            value:
              norm(data.waitingPeriodState) === "none" ? (
                "—"
              ) : (
                <Status status={data.waitingPeriodState} />
              ),
          },
        ]}
      />
      {rows.length > 0 ? (
        <DataTable
          caption={t(L.limitsCaption)}
          columns={cols}
          rows={rows}
          rowKey={(r) => r.category}
          density="compact"
        />
      ) : null}
      {rows.length === 0 && !hasAnyValue(data) ? <Empty /> : null}
    </div>
  );
}

/** `10% · Tier1`, or whichever half was served. */
function costShare(line: CoverageLimitLine, num: (v: number | null | undefined) => string): string {
  const parts = [
    line.costSharePercent === undefined ? null : `${num(line.costSharePercent)}%`,
    line.costShareTier ?? null,
  ].filter(Boolean);
  return parts.join(" · ");
}

/**
 * Exhausted / running low, as a chip rather than a colour.
 *
 * Only rendered when there is a limit to be a proportion OF. "Low" against an unknown ceiling is a guess, and a
 * guess about whether a member can afford their next visit is not one this screen gets to make.
 */
function LimitCue({ line }: { line: CoverageLimitLine }) {
  const t = useLoc();
  const { annualLimit: limit, remaining } = line;
  if (typeof remaining !== "number" || typeof limit !== "number" || limit <= 0) return null;
  if (remaining <= 0) return <StatusChip kind="bad" label={t(L.exhausted)} />;
  if (remaining / limit <= 0.2) return <StatusChip kind="warn" label={t(L.low)} />;
  return null;
}

/** Did the server send anything at all for this section? Distinguishes "empty" from "narrowed to nothing". */
function hasAnyValue(data: object): boolean {
  return Object.values(data).some((v) => {
    if (v === null || v === undefined || v === "") return false;
    return Array.isArray(v) ? v.length > 0 : true;
  });
}

// ---------------------------------------------------------------- 4. past medical history

function PastMedicalHistoryView({ data }: { data: ProfilePastMedicalHistory }) {
  const t = useLoc();
  const fmt = useFormat();
  const api = useApi();
  const conditions = data.conditions ?? [];
  const records = data.uploadedRecords ?? [];

  /**
   * The CONDITION, resolved from the code.
   *
   * emr stores a diagnosis as a bare ICD-10 code and sends that code as the display too — so both columns
   * read "K21.9" and the table said the same thing twice while naming no condition at all. Resolving a code
   * to its meaning is masterdata-service's job (emr's own comment says so, and being a second answerer is
   * what it declines to be), so the browser joins the two reads it already holds.
   *
   * Falls back to the code. A condition masterdata does not carry still has to appear on the patient's
   * history — a blank cell would read as "no diagnosis recorded".
   */
  const codes = useMemo(
    () => [...new Set(conditions.map((c) => c.code).filter((c): c is string => Boolean(c)))],
    [conditions]);
  const titles = useAsync(
    useCallback(() => api.icdTitles(codes), [api, codes.join(",")]),  // eslint-disable-line react-hooks/exhaustive-deps
    [codes.join(",")]);
  /**
   * The record's OWN description wins where it has one; the catalogue only fills the gap.
   *
   * emr's display is the code itself, which is the case this lookup exists for. But other providers do send a
   * real description, and overriding it with the catalogue's wording would replace what was recorded about
   * this patient with a generic title — "Type 2 diabetes mellitus" becoming "Type 2 diabetes mellitus,
   * Without complications", which is a different clinical claim from the one in the record.
   */
  const titleFor = (r: CodedCondition) => {
    const recorded = r.display && r.display !== r.code ? r.display : null;
    return recorded ?? (r.code ? titles.data?.get(r.code) : undefined) ?? r.display;
  };

  const cols = columns<CodedCondition>(
    anyHas(conditions, (r) => r.code) && {
      // The CODE alone. It carried "ICD-10 K21.9" on the argument that a code means nothing without its
      // system — true in a payload, not in a column headed "Code" in a clinical table where every row is
      // ICD-10. The prefix repeated on every line and cost the width the condition needed.
      key: "code", header: t(L.code), cell: (r) => <span className="tnum">{r.code}</span>,
      sortable: true, sortValue: (r) => r.code,
    },
    { key: "display", header: t(L.condition), cell: (r) => titleFor(r),
      sortable: true, sortValue: (r) => titleFor(r) },
    anyHas(conditions, (r) => r.clinicalStatus) && {
      key: "clinicalStatus", header: t(L.status),
      cell: (r) => (r.clinicalStatus ? <Status status={r.clinicalStatus} /> : null),
      sortable: true, sortValue: (r) => r.clinicalStatus,
    },
    anyHas(conditions, (r) => r.onsetOn) && {
      key: "onset", header: t(L.onset), cell: (r) => (r.onsetOn ? fmt.date(r.onsetOn) : null),
      sortable: true, sortValue: (r) => r.onsetOn,
    },
  );

  if (conditions.length === 0 && records.length === 0 && !data.narrative) return <Empty />;

  return (
    <div className="profile-stack">
      {conditions.length > 0 ? (
        <DataTable
          caption={t(L.conditionsCaption)}
          columns={cols}
          rows={conditions}
          rowKey={(r) => `${r.system ?? ""}-${r.code ?? r.display}`}
          density="compact"
        />
      ) : null}

      {/* Dropped under `V(summary)` for case managers — so its absence is a projection, not an empty field. */}
      {data.narrative ? (
        <Group heading={L.narrative}>
          <p className="profile-prose">{data.narrative}</p>
        </Group>
      ) : null}

      {records.length > 0 ? (
        <Group heading={L.uploadedRecords}>
          <ul className="profile-rows">
            {records.map((r: HistoricalRecord) => (
              <li key={r.linkId}>
                <span className="profile-row-title">{r.title}</span>
                {r.documentClass ? <span className="profile-row-meta">{r.documentClass}</span> : null}
                {r.documentDate ? <span className="profile-row-meta">{fmt.date(r.documentDate)}</span> : null}
              </li>
            ))}
          </ul>
        </Group>
      ) : null}
    </div>
  );
}

// ---------------------------------------------------------------- 5. encounters

/**
 * Encounters — and the way INTO one.
 *
 * <b>The reference is a control, not a caption.</b> The section listed a clinician's own visits with no way to
 * open any of them: the number is human-readable and addresses nothing, so reading a past visit meant leaving
 * the profile and finding it again from a worklist. The row opens the encounter workspace and records where it
 * came from, so Back returns to this profile — scrolled and filtered as it was.
 *
 * Rows arriving without an `encounterId` render as plain text. That is the `V(meta)` projection: reception,
 * finance and beneficiary management have no encounter workspace, so the handle was never sent. Absence means
 * "not openable by you", and a dead button would say the opposite.
 */
function EncountersView({ data, beneficiaryId }: { data: ProfileEncounters; beneficiaryId?: string }) {
  const t = useLoc();
  const fmt = useFormat();
  const api = useApi();
  const navigate = useNavigate();
  const location = useLocation();
  const rows = data.items ?? [];

  /**
   * Branch and clinician NAMES, and the clinician's specialty.
   *
   * The payload carries ids: emr owns no branch label and no practitioner record, so it sends what it has and
   * the browser joins — the same shape the day board uses for branch labels and the booking picker for
   * doctors. Two independent lookups, each degrading on its own: a branch that cannot be named leaves that
   * cell blank rather than taking the table with it.
   */
  const branchIds = useMemo(
    () => [...new Set(rows.map((r) => r.branchId).filter((b): b is string => Boolean(b)))], [rows]);
  const branches = useAsync(
    useCallback(() => api.branchLabels(branchIds), [api, branchIds.join(",")]),  // eslint-disable-line react-hooks/exhaustive-deps
    [branchIds.join(",")]);

  const hasClinicians = rows.some((r) => r.clinicianId);
  const practitioners = useAsync(
    useCallback(
      () => (hasClinicians ? api.practitioners() : Promise.resolve([])),
      [api, hasClinicians]),
    [hasClinicians]);
  const byPractitioner = useMemo(
    () => new Map((practitioners.data ?? []).map((p) => [p.id, p])), [practitioners.data]);

  const branchOf = (r: EncounterRow) =>
    r.branchName ?? (r.branchId ? branches.data?.get(r.branchId) ?? null : null);
  const clinicianOf = (r: EncounterRow) =>
    r.clinicianName ?? (r.clinicianId ? t(byPractitioner.get(r.clinicianId)?.name ?? EMPTY_NAME) || null : null);
  const specialtyOf = (r: EncounterRow) =>
    r.specialty ?? (r.clinicianId ? byPractitioner.get(r.clinicianId)?.primarySpecialty ?? null : null);

  const [detail, setDetail] = useState<EncounterRow | null>(null);

  const openEncounter = (encounterId: string) =>
    navigate(`/clinician/encounter?encounter=${encodeURIComponent(encounterId)}`, {
      state: { from: `${location.pathname}${location.search}` },
    });

  const cols = columns<EncounterRow>(
    { key: "occurredAt", header: t(L.occurredAt), cell: (r) => fmt.dateTime(r.occurredAt),
      sortable: true, sortValue: (r) => r.occurredAt },
    { key: "encounterRef", header: t(L.ref), cell: (r) => <span className="tnum">{r.encounterRef}</span>,
      sortable: true, sortValue: (r) => r.encounterRef },
    anyHas(rows, (r) => branchOf(r)) && {
      key: "branch", header: t(L.branch), cell: (r) => branchOf(r),
      sortable: true, sortValue: (r) => branchOf(r),
    },
    anyHas(rows, (r) => clinicianOf(r)) && {
      key: "clinician", header: t(L.clinician), cell: (r) => clinicianOf(r),
      sortable: true, sortValue: (r) => clinicianOf(r),
    },
    anyHas(rows, (r) => specialtyOf(r)) && {
      key: "specialty", header: t(L.specialty), cell: (r) => specialtyOf(r),
      sortable: true, sortValue: (r) => specialtyOf(r),
    },
    // Absent for every administrative role (`V(meta)`) — the column disappears rather than standing empty.
    anyHas(rows, (r) => r.reason) && {
      key: "reason", header: t(L.reason), cell: (r) => r.reason,
    },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
    {
      // Last column, on every row. The row itself navigates AWAY to the workspace; this reads the visit
      // where you are, which is the commoner intent when scanning a history — and it is the only affordance
      // for a role whose projection carries no encounterId to navigate with.
      key: "view",
      header: t(L.view),
      // Pinned to the trailing edge. This table is seven columns wide and overflows its card on a laptop, so
      // the column that ends up past the fold is the last one — which is where the control lives. Without
      // this, reading a visit meant scrolling sideways first, on every row.
      stickyEnd: true,
      cell: (r) => (
        <Button
          variant="ghost"
          size="sm"
          // Icon-only, so it needs a name — and the name says WHICH visit, because a column of identical
          // "View" buttons is unusable with a screen reader.
          aria-label={`${t(L.viewEncounter)} — ${r.encounterRef}`}
          title={t(L.viewEncounter)}
          leadingIcon={<Icon name="eye" />}
          // The row is a click target too; without this, opening the modal would also navigate away from it.
          onClick={(e) => { e.stopPropagation(); setDetail(r); }}
        />
      ),
    },
  );

  if (rows.length === 0) return <Empty />;

  /*
    THE WHOLE ROW opens the encounter, not the reference alone.
    ============================================================================================================
    A link on one cell makes a 6-column row a target the width of "ENC-2026-000074" — the smallest thing in it,
    and the one a clinician is least likely to aim at when what they want is "that visit". `interactive` makes
    DataTable a grid with roving focus and Enter/Space per row (18.D3), so the keyboard path is the row too
    rather than a tab stop buried in a cell.

    Rows with no `encounterId` stay inert: that is the `V(meta)` projection, where the handle was never sent
    because the role has no encounter workspace to open.
  */
  return (
    <>
      <DataTable
        caption={t(L.encountersCaption)}
        columns={cols}
        rows={rows}
        rowKey={(r) => r.encounterRef}
        density="compact"
        interactive={rows.some((r) => r.encounterId)}
        onSelect={(r) => r.encounterId && openEncounter(r.encounterId)}
      />
      {/*
        The visit, read where you are.

        The row's own facts are the FRAME; the clinical record inside it comes from emr, behind emr's own
        treating gate. That is why the modal fetches rather than rendering the row alone: a clinician
        scanning a history wants the note and the diagnoses, and sending them to the workspace and back for
        every visit they glance at is the long way round. Nothing here widens what a role may read — a
        caller without a treating relationship gets the same 403 the workspace would give them, and the
        modal says so instead of showing an empty note.
      */}
      {detail && (
        <EncounterDetailModal
          row={detail}
          branch={branchOf(detail)}
          clinician={clinicianOf(detail)}
          specialty={specialtyOf(detail)}
          beneficiaryId={beneficiaryId}
          onClose={() => setDetail(null)}
          onOpenEncounter={openEncounter}
        />
      )}
    </>
  );
}

/**
 * One visit, read in place — identity and context first, then the record, split across tabs.
 *
 * <b>Tabs, not one long column.</b> A consultation is four unrelated things (what happened, what was
 * written, what was coded, what was measured) and stacking them makes a dialog you scroll rather than read;
 * the vitals end up below the fold of a note whose length nobody controls.
 */
function EncounterDetailModal({
  row,
  branch,
  clinician,
  specialty,
  beneficiaryId,
  onClose,
  onOpenEncounter,
}: {
  row: EncounterRow;
  branch: string | null;
  clinician: string | null;
  specialty: string | null;
  /** Needed for the orders tab, which reads the member's own investigation and prescription sections. */
  beneficiaryId?: string;
  onClose: () => void;
  onOpenEncounter: (encounterId: string) => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const api = useApi();
  const [tab, setTab] = useState("visit");

  // Only when the projection carried a handle. A `V(meta)` role (reception, finance) is given the visit's
  // existence and not its id, and asking emr for a record we were deliberately not given the key to would
  // be the client trying a door the server already closed.
  const encounterId = row.encounterId ?? null;
  const record = useAsync(
    useCallback(
      () => (encounterId ? api.getEncounter(encounterId) : Promise.resolve(null)),
      [api, encounterId],
    ),
    [encounterId],
  );

  /**
   * What this visit ORDERED — investigations and prescriptions, scoped to this encounter.
   *
   * Fetched from the member's own profile sections rather than from orders/pharmacy directly, so the
   * design-39 §4 matrix decides what comes back exactly as it does on the file itself: a role that may not
   * read prescriptions gets the section withheld here too, and this modal has no separate opinion. Loaded
   * only when the orders tab is opened — a dialog that reads three services to show a date is a dialog that
   * costs three PHI accesses per glance.
   */
  const wantOrders = tab === "orders" && Boolean(beneficiaryId) && Boolean(row.encounterId);
  const orders = useAsync(
    useCallback(
      () => (wantOrders
        ? api.patientProfile(beneficiaryId!, ["investigations", "prescriptions"])
        : Promise.resolve(null)),
      [api, beneficiaryId, wantOrders],
    ),
    [wantOrders, beneficiaryId],
  );

  const denied = record.status === "error" && record.error?.status === 403;
  const e = record.data;
  const soapFilled = e ? Object.values(e.soap).some((v) => v.trim().length > 0) : false;

  const visitPane = (
    <dl className="profile-facts">
      <Fact label={t(L.occurredAt)} value={fmt.dateTime(row.occurredAt)} />
      <Fact label={t(L.ref)} value={row.encounterRef} />
      <Fact label={t(L.branch)} value={branch} />
      <Fact label={t(L.clinician)} value={clinician} />
      <Fact label={t(L.specialty)} value={specialty} />
      <Fact label={t(L.reason)} value={row.reason} />
      <Fact label={t(L.status)} value={row.status} />
    </dl>
  );

  // The three clinical panes share one story: withheld, still loading, empty, or here. Kept in a helper so
  // all three tell it the same way — an empty note and a withheld note must never render alike.
  const clinical = (body: (enc: NonNullable<typeof e>) => ReactNode, emptyLabel: Localized) => {
    if (denied) return <InlineAlert tone="info">{t(L.encounterRestricted)}</InlineAlert>;
    if (record.status === "error") return <InlineAlert tone="bad">{t(L.encounterUnavailable)}</InlineAlert>;
    if (record.status === "loading") return <p className="profile-empty">{t(L.loading)}</p>;
    if (!e) return <p className="profile-empty">{t(emptyLabel)}</p>;
    return body(e);
  };

  return (
    <Modal
      open
      onOpenChange={(open: boolean) => !open && onClose()}
      title={`${t(L.encounterDetails)} — ${row.encounterRef}`}
      closeLabel={t(L.close)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(L.close)}</Button>
          {encounterId ? (
            <Button
              variant="primary"
              leadingIcon={<Icon name="doc" />}
              onClick={() => onOpenEncounter(encounterId)}
            >
              {t(L.openEncounter)}
            </Button>
          ) : null}
        </>
      }
    >
      <Tabs
        aria-label={t(L.encounterDetails)}
        value={tab}
        onValueChange={setTab}
        items={[
          { value: "visit", label: t(L.tabVisit), content: visitPane },
          {
            value: "note",
            label: t(L.tabNote),
            content: clinical(
              (enc) =>
                soapFilled ? (
                  <dl className="soap">
                    <SoapPart label={t(L.subjective)} value={enc.soap.subjective} />
                    <SoapPart label={t(L.objective)} value={enc.soap.objective} />
                    <SoapPart label={t(L.assessment)} value={enc.soap.assessment} />
                    <SoapPart label={t(L.soapPlan)} value={enc.soap.plan} />
                  </dl>
                ) : (
                  <p className="profile-empty">{t(L.noNote)}</p>
                ),
              L.noNote,
            ),
          },
          {
            value: "diagnoses",
            label: t(L.tabDiagnoses),
            content: clinical(
              (enc) =>
                enc.diagnoses.length === 0 ? (
                  <p className="profile-empty">{t(L.noDiagnoses)}</p>
                ) : (
                  <ul className="chip-list dx-list">
                    {enc.diagnoses.map((d) => (
                      <li key={d.id ?? d.code} className="dx-chip">
                        <span className="dx-code tnum">{d.code}</span>
                        <span className="dx-label">{t(d.label)}</span>
                      </li>
                    ))}
                  </ul>
                ),
              L.noDiagnoses,
            ),
          },
          {
            value: "vitals",
            label: t(L.tabVitals),
            content: clinical((enc) => <VitalsFacts vitals={enc.vitals} />, L.noVitals),
          },
          {
            value: "orders",
            label: t(L.tabOrders),
            content: <EncounterOrders state={orders} encounterId={row.encounterId ?? null} />,
          },
        ]}
      />
    </Modal>
  );
}

/**
 * What this visit ordered.
 *
 * <b>Scoped by encounter id, and honest when it cannot be.</b> A role whose encounter projection carries no
 * id (`V(meta)` — reception, finance) cannot have this list narrowed to one visit, and showing them the
 * member's WHOLE order history under a heading that says "on this visit" would be a plain untruth. They get
 * a sentence saying why instead.
 *
 * The two sections come back withheld or absent exactly as they do on the patient file — this reads the same
 * profile response and applies no rules of its own.
 */
function EncounterOrders({
  state,
  encounterId,
}: {
  state: ReturnType<typeof useAsync<PatientProfile | null>>;
  encounterId: string | null;
}) {
  const t = useLoc();
  const fmt = useFormat();

  if (!encounterId) return <InlineAlert tone="info">{t(L.ordersUnavailable)}</InlineAlert>;
  if (state.status === "loading") return <p className="profile-empty">{t(L.loading)}</p>;
  if (state.status === "error") return <InlineAlert tone="bad">{t(L.encounterUnavailable)}</InlineAlert>;

  const section = (key: string): ProfileSection | null =>
    state.data?.sections.find((x: ProfileSection) => x.key === key) ?? null;
  const inv = section("investigations");
  const rx = section("prescriptions");

  const invRows = inv?.state === "Visible"
    ? ((inv.data as ProfileInvestigations).items ?? []).filter((r) => r.encounterId === encounterId)
    : null;
  const rxRows = rx?.state === "Visible"
    ? ((rx.data as ProfilePrescriptions).items ?? []).filter((r) => r.encounterId === encounterId)
    : null;

  return (
    <div className="stack-3">
      <section>
        <h4 className="section-h">{t(L.investigationsOn)}</h4>
        {/* A withheld section and an empty one are different answers, and only one of them says anything
            about what the doctor ordered. */}
        {invRows === null ? (
          <InlineAlert tone="info">{t(L.restrictedSection)}</InlineAlert>
        ) : invRows.length === 0 ? (
          <p className="profile-empty">{t(L.noOrdersOnVisit)}</p>
        ) : (
          <ul className="profile-rows">
            {invRows.map((r) => (
              <li key={r.lineId} className="enc-order-row">
                <span className="tnum">{r.orderRef}</span>
                <span>{r.category ?? "—"}</span>
                <span className="muted tnum">{fmt.dateTime(r.orderedOn)}</span>
                <Status status={r.status} />
                {/* A restricted result is restricted, never "pending" — the same rule the full
                    investigations section keeps (design 37 §6). */}
                {r.restricted ? (
                  <span className="muted">{t(L.restrictedResult)}</span>
                ) : r.resultSummary ? (
                  <span>{r.resultSummary}</span>
                ) : null}
              </li>
            ))}
          </ul>
        )}
      </section>
      <section>
        <h4 className="section-h">{t(L.rxOn)}</h4>
        {rxRows === null ? (
          <InlineAlert tone="info">{t(L.restrictedSection)}</InlineAlert>
        ) : rxRows.length === 0 ? (
          <p className="profile-empty">{t(L.noRxOnVisit)}</p>
        ) : (
          <ul className="profile-rows">
            {rxRows.map((r) => (
              <li key={`${r.rxRef}-${r.drugDisplay}`} className="enc-order-row">
                <span className="tnum">{r.rxRef}</span>
                <span>{r.drugDisplay}</span>
                <span className="muted tnum">{fmt.dateTime(r.prescribedOn)}</span>
                <Status status={r.status} />
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

/** One SOAP section in the read-only view. Absent sections are omitted — a heading over nothing reads as a
 *  clinician who examined the patient and wrote no findings. */
function SoapPart({ label, value }: { label: string; value: string }) {
  if (!value.trim()) return null;
  return (
    <div>
      <dt>{label}</dt>
      <dd style={{ whiteSpace: "pre-wrap" }}>{value}</dd>
    </div>
  );
}

function VitalsFacts({ vitals }: { vitals: Encounter["vitals"] }) {
  const t = useLoc();
  const fmt = useFormat();
  const bp = vitals.systolic === null && vitals.diastolic === null
    ? null
    : `${vitals.systolic ?? "—"} / ${vitals.diastolic ?? "—"} mmHg`;
  const rows: [string, string | null][] = [
    [t(L.bp), bp],
    [t(L.hr), vitals.heartRate === null ? null : `${vitals.heartRate} bpm`],
    [t(L.temp), vitals.tempC === null ? null : `${vitals.tempC} °C`],
    [t(L.spo2), vitals.spo2 === null ? null : `${vitals.spo2} %`],
    [t(L.height), vitals.heightCm === null ? null : `${vitals.heightCm} cm`],
    [t(L.weight), vitals.weightKg === null ? null : `${vitals.weightKg} kg`],
  ];
  if (rows.every(([, v]) => v === null)) return <p className="profile-empty">{t(L.noVitals)}</p>;
  return (
    <>
      {vitals.measuredAt && (
        <p className="profile-sub">{t(L.measuredAt)} {fmt.dateTime(vitals.measuredAt)}</p>
      )}
      <dl className="profile-facts">
        {rows.map(([label, value]) => <Fact key={label} label={label} value={value} />)}
      </dl>
    </>
  );
}

/** One labelled value in the detail modal. Renders NOTHING when the field is absent — a dash would claim the
 *  visit has no branch when the truth is that this role's projection does not carry one. */
function Fact({ label, value }: { label: string; value?: string | null }) {
  if (!value) return null;
  return (
    <div>
      <dt>{label}</dt>
      <dd>{value}</dd>
    </div>
  );
}

/** A blank bilingual label, so an unresolved practitioner renders empty rather than "undefined". */
const EMPTY_NAME = { en: "", ar: "" };

// ---------------------------------------------------------------- 6. investigations

/**
 * Investigations & results (design 39 §3 row 6), sensitivity-gated per design 37 §6.
 *
 * <b>A restricted row is rendered as restricted, not as pending.</b> The owning service never sent a value, so
 * there is nothing here to hide — but the two absences mean opposite things: "the lab has not reported yet" is
 * a wait, and "this result is sensitivity-restricted" is a locked door with a request-access path. A clinician
 * who reads the second as the first waits for a result that will never arrive on its own.
 */
function InvestigationsView({ data }: { data: ProfileInvestigations }) {
  const t = useLoc();
  const fmt = useFormat();
  const rows = data.items ?? [];
  if (rows.length === 0) return <Empty />;

  const cols = columns<InvestigationRow>(
    { key: "orderedOn", header: t(L.orderedOn), cell: (r) => fmt.dateTime(r.orderedOn),
      sortable: true, sortValue: (r) => r.orderedOn },
    { key: "orderRef", header: t(L.ref), cell: (r) => r.orderRef,
      sortable: true, sortValue: (r) => r.orderRef },
    anyHas(rows, (r) => r.category) && {
      key: "category", header: t(L.category), cell: (r) => r.category,
      sortable: true, sortValue: (r) => r.category,
    },
    anyHas(rows, (r) => r.providerName) && {
      key: "provider", header: t(L.provider), cell: (r) => r.providerName,
      sortable: true, sortValue: (r) => r.providerName,
    },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
    { key: "result", header: t(L.result), cell: (r) => <InvestigationResult row={r} /> },
  );

  return (
    <DataTable
      caption={t(L.investigationsCaption)}
      columns={cols}
      rows={rows}
      rowKey={(r) => r.lineId}
      density="compact"
    />
  );
}

function InvestigationResult({ row }: { row: InvestigationRow }) {
  const t = useLoc();

  if (row.restricted) {
    return (
      <span className="profile-locked" data-restricted="true">
        <span aria-hidden="true">🔒</span>
        <span>{t(L.resultRestricted)}</span>
        {row.sensitivityLevel ? <span className="profile-row-meta">{row.sensitivityLevel}</span> : null}
      </span>
    );
  }
  if (row.resultSummary) return <>{row.resultSummary}</>;
  // Not restricted and no value: the ordinary wait. Said in words, because an empty cell is ambiguous
  // precisely where this section cannot afford ambiguity.
  return <span className="profile-row-meta">{t(L.awaitingResult)}</span>;
}

// ---------------------------------------------------------------- 7. prescriptions

function PrescriptionsView({ data }: { data: ProfilePrescriptions }) {
  const t = useLoc();
  const fmt = useFormat();
  const rows = data.items ?? [];
  if (rows.length === 0) return <Empty />;

  const cols = columns<ProfileRxRow>(
    { key: "prescribedOn", header: t(L.prescribedOn), cell: (r) => fmt.dateTime(r.prescribedOn),
      sortable: true, sortValue: (r) => r.prescribedOn },
    { key: "rxRef", header: t(L.ref), cell: (r) => r.rxRef, sortable: true, sortValue: (r) => r.rxRef },
    { key: "drug", header: t(L.drug), cell: (r) => r.drugDisplay,
      sortable: true, sortValue: (r) => r.drugDisplay },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
    anyHas(rows, (r) => r.dispensedOn) && {
      key: "dispensedOn", header: t(L.dispensedOn),
      cell: (r) => (r.dispensedOn ? fmt.dateTime(r.dispensedOn) : null),
      sortable: true, sortValue: (r) => r.dispensedOn,
    },
    anyHas(rows, (r) => r.batchNo) && {
      key: "batchNo", header: t(L.batchNo), cell: (r) => r.batchNo,
    },
    anyHas(rows, (r) => r.expiryDate) && {
      key: "expiry", header: t(L.expiry), cell: (r) => <Expiry date={r.expiryDate} />,
      sortable: true, sortValue: (r) => r.expiryDate,
    },
    // Only when a substitution actually happened — an always-empty column invites "why is nothing substituted?"
    anyHas(rows, (r) => r.substitutedWith) && {
      key: "substituted", header: t(L.substituted), cell: (r) => r.substitutedWith,
    },
  );

  return (
    <DataTable
      caption={t(L.prescriptionsCaption)}
      columns={cols}
      rows={rows}
      rowKey={(r) => r.rxRef}
      density="compact"
    />
  );
}

/** An expiry that has passed is flagged in words and an icon, never by turning the date red. */
function Expiry({ date }: { date?: string }) {
  const t = useLoc();
  const fmt = useFormat();
  if (!date) return null;
  // Date-only comparison: a batch expiring today is not yet expired, and comparing against `now` would
  // silently expire it at 00:00 Cairo.
  const past = date < new Date().toISOString().slice(0, 10);
  return (
    <span className="profile-expiry">
      <span>{fmt.date(date)}</span>
      {past ? <StatusChip kind="warn" label={t(L.expired)} /> : null}
    </span>
  );
}

// ---------------------------------------------------------------- 8. authorizations

function AuthorizationsView({ data }: { data: ProfileAuthorizations }) {
  const t = useLoc();
  const fmt = useFormat();
  const money = useMoney();
  const rows = data.items ?? [];
  if (rows.length === 0) return <Empty />;

  const cols = columns<AuthorizationRow>(
    { key: "requestedAt", header: t(L.requestedAt), cell: (r) => fmt.dateTime(r.requestedAt),
      sortable: true, sortValue: (r) => r.requestedAt },
    { key: "authNo", header: t(L.authNo), cell: (r) => r.authNo,
      sortable: true, sortValue: (r) => r.authNo },
    anyHas(rows, (r) => r.serviceCategory) && {
      key: "serviceCategory", header: t(L.serviceCategory), cell: (r) => r.serviceCategory,
      sortable: true, sortValue: (r) => r.serviceCategory,
    },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
    anyHas(rows, (r) => r.decidedAt) && {
      key: "decidedAt", header: t(L.decidedAt), cell: (r) => (r.decidedAt ? fmt.dateTime(r.decidedAt) : null),
      sortable: true, sortValue: (r) => r.decidedAt,
    },
    anyHas(rows, (r) => r.validUntil) && {
      key: "validUntil", header: t(L.validUntil), cell: (r) => (r.validUntil ? fmt.date(r.validUntil) : null),
      sortable: true, sortValue: (r) => r.validUntil,
    },
    // Stripped for reception (`V(status)`): they tell a member "approved until the 30th", not what it cost.
    anyHas(rows, (r) => r.approvedAmount) && {
      key: "approvedAmount", header: t(L.approvedAmount), cell: (r) => money(r.approvedAmount),
      sortable: true, sortValue: (r) => r.approvedAmount,
    },
    // Stripped for reception AND finance: the clinical reasoning is neither one's zone.
    anyHas(rows, (r) => r.rationale) && {
      key: "rationale", header: t(L.rationale), cell: (r) => r.rationale,
    },
  );

  return (
    <DataTable
      caption={t(L.authorizationsCaption)}
      columns={cols}
      rows={rows}
      rowKey={(r) => r.authNo}
      density="compact"
    />
  );
}

// ---------------------------------------------------------------- 9. referrals

function ReferralsView({ data }: { data: ProfileReferrals }) {
  const t = useLoc();
  const fmt = useFormat();
  const rows = data.items ?? [];
  if (rows.length === 0) return <Empty />;

  const cols = columns<ReferralRow>(
    { key: "createdAt", header: t(L.createdAt), cell: (r) => fmt.dateTime(r.createdAt),
      sortable: true, sortValue: (r) => r.createdAt },
    { key: "referralRef", header: t(L.ref), cell: (r) => r.referralRef,
      sortable: true, sortValue: (r) => r.referralRef },
    anyHas(rows, (r) => r.requestedSpecialty) && {
      key: "specialty", header: t(L.requestedSpecialty), cell: (r) => r.requestedSpecialty,
      sortable: true, sortValue: (r) => r.requestedSpecialty,
    },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
    // The loop is the coordination question — "did anything come back?" — so it is stated as open or closed
    // with four cues, not left as a timestamp the reader has to interpret by its presence.
    { key: "loop", header: t(L.loop),
      cell: (r) =>
        r.loopClosedAt ? (
          <span className="profile-loop">
            <StatusChip kind="ok" label={t(L.loopClosed)} />
            <span className="profile-row-meta">{fmt.dateTime(r.loopClosedAt)}</span>
          </span>
        ) : (
          <StatusChip kind="info" label={t(L.loopOpen)} />
        ),
      sortable: true, sortValue: (r) => r.loopClosedAt ?? "",
    },
  );

  return (
    <DataTable
      caption={t(L.referralsCaption)}
      columns={cols}
      rows={rows}
      rowKey={(r) => r.referralRef}
      density="compact"
    />
  );
}

// ---------------------------------------------------------------- 10. documents

function DocumentsView({ data }: { data: ProfileDocuments }) {
  const t = useLoc();
  const fmt = useFormat();
  const rows = data.items ?? [];
  if (rows.length === 0) return <Empty />;

  const cols = columns<DocumentRow>(
    { key: "title", header: t(L.title), cell: (r) => r.title, sortable: true, sortValue: (r) => r.title },
    anyHas(rows, (r) => r.documentClass) && {
      key: "class", header: t(L.documentClass), cell: (r) => r.documentClass,
      sortable: true, sortValue: (r) => r.documentClass,
    },
    anyHas(rows, (r) => r.visibilityClass) && {
      key: "visibility", header: t(L.visibility), cell: (r) => r.visibilityClass,
      sortable: true, sortValue: (r) => r.visibilityClass,
    },
    anyHas(rows, (r) => r.documentDate) && {
      key: "documentDate", header: t(L.date), cell: (r) => (r.documentDate ? fmt.date(r.documentDate) : null),
      sortable: true, sortValue: (r) => r.documentDate,
    },
    { key: "uploadedAt", header: t(L.uploadedAt), cell: (r) => fmt.dateTime(r.uploadedAt),
      sortable: true, sortValue: (r) => r.uploadedAt },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
    // Metadata is always served; CONTENT is gated separately (design 39 §3 row 10). The control is absent
    // rather than disabled — a greyed download advertises a file this caller will never open.
    anyHas(rows, (r) => r.mayDownload) && {
      key: "download", header: t(L.download),
      cell: (r) => (r.mayDownload ? <DownloadControl linkId={r.linkId} title={r.title} /> : null),
    },
  );

  return (
    <DataTable
      caption={t(L.documentsCaption)}
      columns={cols}
      rows={rows}
      rowKey={(r) => r.linkId}
      density="compact"
    />
  );
}

/**
 * Resolve a short-TTL signed URL through the audited endpoint, then hand it to the browser.
 *
 * Never a direct object link: the signed-URL round trip IS the audit event for a PHI document read, and a
 * plain `<a href>` to storage would be a download nobody recorded.
 */
function DownloadControl({
  linkId,
  title,
  api = httpPolicyApi,
}: {
  linkId: string;
  title: string;
  api?: PolicyApi;
}) {
  const t = useLoc();
  const [failed, setFailed] = useState(false);

  const open = useCallback(async () => {
    setFailed(false);
    try {
      const { url } = await api.documentDownloadUrl(linkId, "download");
      window.open(url, "_blank", "noopener,noreferrer");
    } catch {
      setFailed(true);
    }
  }, [api, linkId]);

  return (
    <>
      <Button
        variant="ghost"
        size="sm"
        // "Download" repeated down a column tells a screen-reader user nothing about which file they are on.
        aria-label={`${t(L.download)} — ${title}`}
        onClick={() => void open()}
      >
        <Icon name="download" aria-hidden />
      </Button>
      {failed ? (
        <span role="alert" className="profile-row-meta">
          {t(L.downloadFailed)}
        </span>
      ) : null}
    </>
  );
}

// ---------------------------------------------------------------- 11. notes

/**
 * Notes (design 39 §3 row 11), class-projected.
 *
 * A note the caller may not read still appears, with a lock and no body: <b>its existence is not the secret,
 * its content is</b> (19.3). Hiding the row entirely would let a user conclude nothing was written, and the
 * whole point of showing withheld things is that people request access instead of assuming absence.
 */
function NotesView({ data }: { data: ProfileNotes }) {
  const t = useLoc();
  const fmt = useFormat();
  const rows = data.items ?? [];
  if (rows.length === 0) return <Empty />;

  // Pinned first, then the server's order within each group. A pin is an instruction from whoever left it.
  const ordered = [...rows].sort((a, b) => Number(b.pinned ?? false) - Number(a.pinned ?? false));

  return (
    <ul className="profile-notes">
      {ordered.map((note: NoteRow) => (
        <li key={note.noteId} className="profile-note" data-withheld={note.withheld ? "true" : undefined}>
          <div className="profile-note-head">
            {note.pinned ? <StatusChip kind="info" label={t(L.pinned)} /> : null}
            {note.noteType ? <span className="profile-row-title">{note.noteType}</span> : null}
            {note.visibilityClass ? <span className="profile-row-meta">{note.visibilityClass}</span> : null}
            <span className="profile-row-meta">{fmt.dateTime(note.createdAt)}</span>
            {note.authorDisplay ? <span className="profile-row-meta">{note.authorDisplay}</span> : null}
          </div>
          {note.withheld ? (
            <p className="profile-note-withheld">
              <span aria-hidden="true">🔒</span> {t(L.noteWithheld)}
            </p>
          ) : note.body ? (
            <p className="profile-prose">{note.body}</p>
          ) : null}
        </li>
      ))}
    </ul>
  );
}

// ---------------------------------------------------------------- 12. financial

function FinancialView({ data }: { data: ProfileFinancial }) {
  const t = useLoc();
  const fmt = useFormat();
  const money = useMoney(data.currency);
  const claims = data.claims ?? [];

  const cols = columns<FinancialClaimRow>(
    anyHas(claims, (r) => r.serviceDate) && {
      key: "serviceDate", header: t(L.serviceDate), cell: (r) => (r.serviceDate ? fmt.date(r.serviceDate) : null),
      sortable: true, sortValue: (r) => r.serviceDate,
    },
    { key: "claimNo", header: t(L.claimNo), cell: (r) => r.claimNo,
      sortable: true, sortValue: (r) => r.claimNo },
    anyHas(claims, (r) => r.billedAmount) && {
      key: "billed", header: t(L.billed), cell: (r) => money(r.billedAmount),
      sortable: true, sortValue: (r) => r.billedAmount,
    },
    anyHas(claims, (r) => r.approvedAmount) && {
      key: "approved", header: t(L.approved), cell: (r) => money(r.approvedAmount),
      sortable: true, sortValue: (r) => r.approvedAmount,
    },
    anyHas(claims, (r) => r.memberShare) && {
      key: "memberShare", header: t(L.memberShare), cell: (r) => money(r.memberShare),
      sortable: true, sortValue: (r) => r.memberShare,
    },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
  );

  if (!hasAnyValue(data)) return <Empty />;

  return (
    <div className="profile-stack">
      <Facts
        facts={[
          data.costShareOwed !== undefined && {
            label: L.costShareOwed,
            value: <strong className="profile-amount">{money(data.costShareOwed)}</strong>,
          },
          data.settlementStatus !== undefined && {
            label: L.settlement,
            value: <Status status={data.settlementStatus} />,
          },
        ]}
      />
      {/* Absent under `V(summary)` — the Medical Director gets the totals, not the ledger. */}
      {claims.length > 0 ? (
        <DataTable
          caption={t(L.claimsCaption)}
          columns={cols}
          rows={claims}
          rowKey={(r) => r.claimNo}
          density="compact"
        />
      ) : null}
    </div>
  );
}

// ---------------------------------------------------------------- 13. case management

/**
 * Case management (design 39 §3 row 13).
 *
 * Three sibling arrays and not one scalar field — which is exactly why the generic renderer reported this
 * section as "No records" no matter how many open cases a beneficiary had.
 */
function CaseManagementView({ data }: { data: ProfileCaseManagement }) {
  const t = useLoc();
  const fmt = useFormat();
  const cases = data.cases ?? [];
  const tasks = data.tasks ?? [];
  const escalations = data.escalations ?? [];

  if (cases.length === 0 && tasks.length === 0 && escalations.length === 0) return <Empty />;

  const caseCols = columns<CaseRow>(
    { key: "openedAt", header: t(L.openedAt), cell: (r) => fmt.dateTime(r.openedAt),
      sortable: true, sortValue: (r) => r.openedAt },
    { key: "caseNo", header: t(L.caseNo), cell: (r) => r.caseNo, sortable: true, sortValue: (r) => r.caseNo },
    anyHas(cases, (r) => r.category) && {
      key: "category", header: t(L.category), cell: (r) => r.category,
      sortable: true, sortValue: (r) => r.category,
    },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
  );

  const taskCols = columns<CoordinationTaskRow>(
    { key: "title", header: t(L.task), cell: (r) => r.title, sortable: true, sortValue: (r) => r.title },
    anyHas(tasks, (r) => r.dueOn) && {
      key: "dueOn", header: t(L.dueOn), cell: (r) => <Due date={r.dueOn} status={r.status} />,
      sortable: true, sortValue: (r) => r.dueOn,
    },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
  );

  const escalationCols = columns<EscalationRow>(
    { key: "raisedAt", header: t(L.createdAt), cell: (r) => fmt.dateTime(r.raisedAt),
      sortable: true, sortValue: (r) => r.raisedAt },
    { key: "reason", header: t(L.reason), cell: (r) => r.reason, sortable: true, sortValue: (r) => r.reason },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
  );

  return (
    <div className="profile-stack">
      {cases.length > 0 ? (
        <Group heading={L.cases}>
          <DataTable caption={t(L.casesCaption)} columns={caseCols} rows={cases}
            rowKey={(r) => r.caseId} density="compact" />
        </Group>
      ) : null}
      {tasks.length > 0 ? (
        <Group heading={L.tasks}>
          <DataTable caption={t(L.tasksCaption)} columns={taskCols} rows={tasks}
            rowKey={(r) => r.taskId} density="compact" />
        </Group>
      ) : null}
      {escalations.length > 0 ? (
        <Group heading={L.escalations}>
          <DataTable caption={t(L.escalationsCaption)} columns={escalationCols} rows={escalations}
            rowKey={(r) => r.escalationId} density="compact" />
        </Group>
      ) : null}
    </div>
  );
}

/** A due date, flagged overdue in words — and only while the task is still open to BE overdue. */
function Due({ date, status }: { date?: string; status: string }) {
  const t = useLoc();
  const fmt = useFormat();
  if (!date) return null;
  const settled = ["completed", "closed", "cancelled", "resolved"].includes(norm(status));
  const late = !settled && date < new Date().toISOString().slice(0, 10);
  return (
    <span className="profile-due">
      <span>{fmt.date(date)}</span>
      {late ? <StatusChip kind="warn" label={t(L.overdue)} /> : null}
    </span>
  );
}

// ---------------------------------------------------------------- 14. timeline

function TimelineView({ data }: { data: ProfileTimeline }) {
  const t = useLoc();
  const fmt = useFormat();
  const rows = data.items ?? [];
  if (rows.length === 0) return <Empty />;

  // Newest first, matching every other timeline in the app (commit ed8dbe9). A copy — `rows` belongs to the
  // caller's props and sorting in place would reorder the parent's state.
  const ordered = [...rows].sort((a, b) => (a.at < b.at ? 1 : a.at > b.at ? -1 : 0));

  const cols = columns<TimelineRow>(
    { key: "at", header: t(L.date), cell: (r) => fmt.dateTime(r.at), sortable: true, sortValue: (r) => r.at },
    { key: "eventType", header: t(L.event), cell: (r) => r.eventType,
      sortable: true, sortValue: (r) => r.eventType },
    anyHas(ordered, (r) => r.actorDisplay) && {
      key: "actor", header: t(L.actor), cell: (r) => r.actorDisplay,
      sortable: true, sortValue: (r) => r.actorDisplay,
    },
    anyHas(ordered, (r) => r.summary) && {
      key: "summary", header: t(L.summary), cell: (r) => r.summary,
    },
    anyHas(ordered, (r) => r.sourceService) && {
      key: "source", header: t(L.source), cell: (r) => r.sourceService,
      sortable: true, sortValue: (r) => r.sourceService,
    },
  );

  return (
    <DataTable
      caption={t(L.timelineCaption)}
      columns={cols}
      rows={ordered}
      rowKey={(r) => `${r.at}-${r.eventType}-${r.sourceService ?? ""}`}
      density="compact"
    />
  );
}

// ---------------------------------------------------------------- the fallback

/**
 * The renderer for a section key this build does not know — a server ahead of this client, nothing more.
 *
 * <b>It never claims emptiness.</b> The renderer it replaces filtered values by `typeof v !== "object"`, which
 * dropped every nested array and object, and then reported "No records" when that left nothing — so a coverage
 * payload full of limits, or a case list with three open cases, rendered as absence. Here, nested content is
 * counted and summarised rather than discarded, and a payload that holds anything says so.
 */
export function FallbackView({ data }: { data: unknown }) {
  const t = useLoc();
  if (data === null || data === undefined) return <Empty />;
  if (typeof data !== "object") return <p className="profile-prose">{String(data)}</p>;

  const entries = Object.entries(data as Record<string, unknown>);
  const scalars = entries.filter(([, v]) => v !== null && v !== undefined && typeof v !== "object");
  const nested = entries.filter(([, v]) => v !== null && typeof v === "object");

  if (scalars.length === 0 && nested.length === 0) return <Empty />;

  return (
    <div className="profile-stack">
      <InlineAlert tone="info">{t(L.notServed)}</InlineAlert>
      {scalars.length > 0 ? (
        <dl className="profile-facts">
          {scalars.map(([k, v]) => (
            <div key={k}>
              <dt>{humanise(k)}</dt>
              <dd>{String(v)}</dd>
            </div>
          ))}
        </dl>
      ) : null}
      {/* Nested content is SHOWN, not filtered away. Unstyled and unlabelled, but present — the alternative
          is a screen that quietly asserts data does not exist. */}
      {nested.map(([k, v]) => (
        <Group key={k} heading={{ en: humanise(k), ar: humanise(k) }}>
          <pre className="profile-raw">{JSON.stringify(v, null, 2)}</pre>
        </Group>
      ))}
    </div>
  );
}

/** `costSharePercent` → `Cost share percent`. Not a translation — a last resort that beats a camelCase key. */
function humanise(key: string): string {
  const spaced = key.replace(/([a-z0-9])([A-Z])/g, "$1 $2").replace(/[_-]+/g, " ");
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}

// ---------------------------------------------------------------- dispatch

/**
 * The section keys this module renders. The parent screen keeps identity, alerts and call history — they were
 * always bespoke — and everything else routes here.
 */
export const DESIGNED_SECTION_KEYS = new Set([
  "coverage", "pastMedicalHistory", "encounters", "investigations", "prescriptions", "authorizations",
  "referrals", "documents", "notes", "financial", "caseManagement", "timeline",
]);

/**
 * Render one section's visible content.
 *
 * The cast is unavoidable and safe: `data` is `unknown` on the wire because its shape depends on the caller's
 * role, and every field these views read is optional. A field the server withheld is simply not there, and the
 * views are written to render nothing for it.
 */
export function SectionView({ section, beneficiaryId }: { section: ProfileSection; beneficiaryId?: string }) {
  const data = section.data;
  if (data === null || data === undefined) return <Empty />;

  switch (section.key) {
    case "coverage":
      return <CoverageView data={data as ProfileCoverage} />;
    case "pastMedicalHistory":
      return <PastMedicalHistoryView data={data as ProfilePastMedicalHistory} />;
    case "encounters":
      // `beneficiaryId` was accepted here and dropped on the floor. The encounters view needs it to scope
      // the visit-details modal's orders tab to this member.
      return <EncountersView data={data as ProfileEncounters} beneficiaryId={beneficiaryId} />;
    case "investigations":
      return <InvestigationsView data={data as ProfileInvestigations} />;
    case "prescriptions":
      return <PrescriptionsView data={data as ProfilePrescriptions} />;
    case "authorizations":
      return <AuthorizationsView data={data as ProfileAuthorizations} />;
    case "referrals":
      return <ReferralsView data={data as ProfileReferrals} />;
    case "documents":
      return <DocumentsView data={data as ProfileDocuments} />;
    case "notes":
      return <NotesView data={data as ProfileNotes} />;
    case "financial":
      return <FinancialView data={data as ProfileFinancial} />;
    case "caseManagement":
      return <CaseManagementView data={data as ProfileCaseManagement} />;
    case "timeline":
      return <TimelineView data={data as ProfileTimeline} />;
    default:
      return <FallbackView data={data} />;
  }
}

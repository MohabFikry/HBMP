import { useCallback, useMemo, useState, type ReactNode } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import {
  Button,
  DataTable,
  Icon,
  InlineAlert,
  StatusChip,
  type Column,
} from "@mersal/design-system";
import type {
  AuthorizationRow,
  CaseRow,
  CodedCondition,
  CoordinationTaskRow,
  CoverageLimitLine,
  DocumentRow,
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
  const conditions = data.conditions ?? [];
  const records = data.uploadedRecords ?? [];

  const cols = columns<CodedCondition>(
    { key: "display", header: t(L.condition), cell: (r) => r.display,
      sortable: true, sortValue: (r) => r.display },
    anyHas(conditions, (r) => r.code) && {
      key: "code", header: t(L.code),
      // System and code belong together: "E11" means nothing without the ICD-10 it belongs to.
      cell: (r) => [r.system, r.code].filter(Boolean).join(" "),
    },
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
function EncountersView({ data }: { data: ProfileEncounters }) {
  const t = useLoc();
  const fmt = useFormat();
  const navigate = useNavigate();
  const location = useLocation();
  const rows = data.items ?? [];
  if (rows.length === 0) return <Empty />;

  const openEncounter = (encounterId: string) =>
    navigate(`/clinician/encounter?encounter=${encodeURIComponent(encounterId)}`, {
      state: { from: `${location.pathname}${location.search}` },
    });

  const cols = columns<EncounterRow>(
    { key: "occurredAt", header: t(L.occurredAt), cell: (r) => fmt.dateTime(r.occurredAt),
      sortable: true, sortValue: (r) => r.occurredAt },
    { key: "encounterRef", header: t(L.ref),
      cell: (r) => (r.encounterId
        ? <button type="button" className="linklike tnum" onClick={() => openEncounter(r.encounterId!)}>
            {r.encounterRef}
          </button>
        : <span className="tnum">{r.encounterRef}</span>),
      sortable: true, sortValue: (r) => r.encounterRef },
    anyHas(rows, (r) => r.branchName) && {
      key: "branch", header: t(L.branch), cell: (r) => r.branchName,
      sortable: true, sortValue: (r) => r.branchName,
    },
    anyHas(rows, (r) => r.clinicianName) && {
      key: "clinician", header: t(L.clinician), cell: (r) => r.clinicianName,
      sortable: true, sortValue: (r) => r.clinicianName,
    },
    anyHas(rows, (r) => r.specialty) && {
      key: "specialty", header: t(L.specialty), cell: (r) => r.specialty,
      sortable: true, sortValue: (r) => r.specialty,
    },
    // Absent for every administrative role (`V(meta)`) — the column disappears rather than standing empty.
    anyHas(rows, (r) => r.reason) && {
      key: "reason", header: t(L.reason), cell: (r) => r.reason,
    },
    { key: "status", header: t(L.status), cell: (r) => <Status status={r.status} />,
      sortable: true, sortValue: (r) => r.status },
  );

  return (
    <DataTable
      caption={t(L.encountersCaption)}
      columns={cols}
      rows={rows}
      rowKey={(r) => r.encounterRef}
      density="compact"
    />
  );
}

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
export function SectionView({ section }: { section: ProfileSection; beneficiaryId?: string }) {
  const data = section.data;
  if (data === null || data === undefined) return <Empty />;

  switch (section.key) {
    case "coverage":
      return <CoverageView data={data as ProfileCoverage} />;
    case "pastMedicalHistory":
      return <PastMedicalHistoryView data={data as ProfilePastMedicalHistory} />;
    case "encounters":
      return <EncountersView data={data as ProfileEncounters} />;
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

import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Button,
  Card,
  DataTable,
  Icon,
  InlineAlert,
  InputField,
  Modal,
  Pagination,
  SelectField,
  StatusChip,
  Tabs,
  TextareaField,
  useTheme,
} from "@mersal/design-system";
import type { BeneficiaryEdit, BeneficiaryRow, Localized } from "@mersal/contracts";
import type {
  CategoryCoverageDetail,
  CoveredFamilyMember,
  FamilyView,
  MemberCoverageDetail,
  MemberGroupView,
  MemberQueryRow,
  MemberUtilizationView,
  PlanChangeView,
  QueryPage,
  PlanChangePreviewView,
  PolicyApi,
  PolicyPlanView,
} from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";
import { useApi } from "../api/ApiProvider";
import { useWrite } from "../api/useWrite";
import { StatusChangeModal } from "./BeneficiaryStatusDialog";
import { MemberAvatar } from "./MemberAvatar";
// Lazy: the unified profile is the heaviest screen in the app and most member lookups never open it.
import { lazy, Suspense } from "react";
const PatientProfile = lazy(() => import("./PatientProfile").then((m) => ({ default: m.PatientProfile })));

/** ONE client for the module, not one per render: a default parameter re-evaluates on every call,
 *  and screens key their load effects on the api instance — a fresh instance per render turned the
 *  first failing (or even succeeding) fetch into an unbounded request loop (QA P0-1: ~400 req/s).*/
const httpPolicyApi = createHttpPolicyApi();
import { writeErrorMessage } from "../api/writeError";
import { PageHeader, useLoc, readErrorMessage } from "./_shared";
import { ChangeTimeline, LimitMeters, NotesPanel, useIdempotencyKey } from "./PolicyPanels";
import { BeneficiaryDocuments } from "./BeneficiaryDocuments";
import { useFormat } from "../i18n/useFormat";
import { useToast } from "@mersal/design-system";

/**
 * Phase 19.6 — member query and the member detail (design 38 §4.2 / §4.5 / §6).
 *
 * The detail is where every other module's output meets one person: the plan they are on and the VERSION of
 * it, the coverage that plan generated, the per-tier cost share they will actually be charged, what they have
 * consumed, the documents on file, the notes, and the change timeline. It is deliberately one screen — an
 * officer answering "why was this claim rejected" should not have to reconcile four.
 *
 * Every membership change is a dialog with an explicit effective date, because "when" is the field that
 * decides whether a member was covered on the day they were treated, and a change that silently means "today"
 * is how retroactive cover gets granted by accident.
 */

const S = {
  // "Beneficiaries", matching the nav and the word the rest of the product uses for these people. The screen
  // said "Members" while the menu item that opened it said "Beneficiaries", which is one name too many for
  // one list.
  title: { en: "Beneficiaries", ar: "المستفيدون" },
  search: { en: "Name, member number or identifier", ar: "الاسم أو رقم العضوية أو المعرّف" },
  find: { en: "Search", ar: "بحث" },

  // ---- advanced search ----
  advanced: { en: "Advanced search", ar: "بحث متقدم" },
  advancedHide: { en: "Hide advanced search", ar: "إخفاء البحث المتقدم" },
  advancedHint: {
    en: "Every field narrows the result together with the others. Leave a field empty to ignore it.",
    ar: "كل حقل يضيّق النتيجة مع بقية الحقول. اترك الحقل فارغًا لتجاهله.",
  },
  fName: { en: "Name", ar: "الاسم" },
  fMemberNo: { en: "Member number", ar: "رقم العضوية" },
  fIdType: { en: "Identity document", ar: "مستند الهوية" },
  fIdValue: { en: "Document number", ar: "رقم المستند" },
  fStatus: { en: "Membership status", ar: "حالة العضوية" },
  fRelationship: { en: "Relationship", ar: "صلة القرابة" },
  fWaiting: { en: "Waiting period", ar: "فترة الانتظار" },
  fBand: { en: "Utilization band", ar: "شريحة الاستخدام" },
  fEnrolledFrom: { en: "Enrolled on or after", ar: "مسجّل من تاريخ" },
  fEnrolledTo: { en: "Enrolled on or before", ar: "مسجّل حتى تاريخ" },
  anyValue: { en: "Any", ar: "الكل" },
  applyFilters: { en: "Apply", ar: "تطبيق" },
  clearFilters: { en: "Clear", ar: "مسح" },
  activeFilters: { en: "{n} filters applied", ar: "{n} عوامل تصفية مطبقة" },
  idTypeNeedsValue: {
    en: "Choose a document type and type its number — one without the other cannot narrow anything.",
    ar: "اختر نوع المستند واكتب رقمه — أحدهما دون الآخر لا يضيّق شيئًا.",
  },
  memberNo: { en: "Member no.", ar: "رقم العضوية" },
  cardNo: { en: "Card no.", ar: "رقم البطاقة" },
  name: { en: "Name", ar: "الاسم" },
  plan: { en: "Plan", ar: "الخطة" },
  relationship: { en: "Relationship", ar: "صلة القرابة" },
  status: { en: "Status", ar: "الحالة" },
  from: { en: "From", ar: "من" },
  waiting: { en: "Waiting period", ar: "فترة الانتظار" },
  used: { en: "% used", ar: "٪ مستخدم" },
  // Covered, with no accumulating ceiling — not "nothing used", and not "no cover".
  unlimited: { en: "Unlimited", ar: "بلا حد" },
  noCover: { en: "No cover", ar: "لا تغطية" },
  noMembers: { en: "No members match this search.", ar: "لا يوجد أعضاء مطابقون." },
  select: { en: "Select a member to open their record.", ar: "اختر عضوًا لفتح سجله." },
  truncated: {
    en: "More people matched than could be resolved — this page is a subset, narrow the search.",
    ar: "تطابق عدد أكبر مما يمكن حصره — هذه الصفحة جزء من النتائج، ضيّق البحث.",
  },
  payerScoped: {
    en: "Narrowed to the payers you are assigned to — this is not the whole book.",
    ar: "تم التضييق على الجهات الممولة المسندة إليك — هذه ليست كل السجلات.",
  },
  tabPerson: { en: "Details", ar: "البيانات" },
  tabCoverage: { en: "Coverage", ar: "التغطية" },
  tabUtilization: { en: "Utilization", ar: "الاستخدام" },
  tabNotes: { en: "Notes", ar: "الملاحظات" },
  tabDocuments: { en: "Documents", ar: "المستندات" },
  // "Logs", not "Timeline". The panel is the audited record of what was changed, by whom and when — an
  // operator looking for "who edited this" searches for a log, and "timeline" reads as a clinical narrative
  // of the member's journey, which is a different thing this product also has.
  tabTimeline: { en: "Logs", ar: "السجل" },
  planVersion: { en: "Plan version", ar: "إصدار الخطة" },
  enrolledUnder: { en: "Enrolled under version", ar: "مسجّل تحت الإصدار" },
  versionDrift: {
    en: "This member's entitlements were generated under an older version of the plan. That is legitimate — it is why two members of the same plan can have different ceilings.",
    ar: "أُنشئت استحقاقات هذا العضو تحت إصدار أقدم من الخطة. هذا سليم — ولهذا قد يختلف سقف عضوين في الخطة نفسها.",
  },
  category: { en: "Category", ar: "الفئة" },
  limit: { en: "Limit", ar: "الحد" },
  consumed: { en: "Consumed", ar: "المستهلك" },
  remaining: { en: "Remaining", ar: "المتبقي" },
  resets: { en: "Resets", ar: "إعادة التعيين" },
  costShare: { en: "Cost share by network tier", ar: "مشاركة التكلفة حسب شريحة الشبكة" },
  tier: { en: "Tier", ar: "الشريحة" },
  copay: { en: "Co-pay", ar: "المشاركة الثابتة" },
  coinsurance: { en: "Co-insurance", ar: "نسبة المشاركة" },
  preauth: { en: "Pre-auth", ar: "موافقة مسبقة" },
  covered: { en: "Covered", ar: "مغطّى" },
  notCovered: { en: "Not covered", ar: "غير مغطّى" },
  limitDiffers: {
    en: "The ceiling on this member's coverage differs from what the plan in force would grant today.",
    ar: "يختلف سقف تغطية هذا العضو عمّا تمنحه الخطة السارية اليوم.",
  },
  exclusions: { en: "Exclusions", ar: "الاستثناءات" },
  terminate: { en: "Terminate", ar: "إنهاء" },
  openProfile: { en: "Open full profile", ar: "فتح الملف الكامل" },
  reinstate: { en: "Reinstate", ar: "إعادة تفعيل" },
  changeGroup: { en: "Change group", ar: "تغيير المجموعة" },
  changePlan: { en: "Change plan", ar: "تغيير الخطة" },
  statusChange: { en: "Status change", ar: "تغيير الحالة" },
  editDetails: { en: "Edit details", ar: "تعديل البيانات" },
  family: { en: "Family", ar: "الأسرة" },
  familyTitle: { en: "Covered family", ar: "الأسرة المغطاة" },
  familyIntro: {
    en: "Everyone enrolled under the same principal. Cover is per person — a relative on this list has their own plan, their own limits and their own status.",
    ar: "كل المسجّلين تحت المشترك الرئيسي نفسه. التغطية لكل فرد — لكل قريب في هذه القائمة خطته وحدوده وحالته.",
  },
  familyAlone: {
    en: "Nobody else is enrolled under this cover.",
    ar: "لا يوجد أحد آخر مسجّل تحت هذه التغطية.",
  },
  familyWithheld: {
    en: "{n} more household member(s) are covered by a payer outside your scope and are not shown.",
    ar: "يوجد {n} من أفراد الأسرة تحت جهة ممولة خارج نطاقك ولا يظهرون هنا.",
  },
  familyNamesUnavailable: {
    en: "Names could not be looked up just now, so some rows show a member number only.",
    ar: "تعذّر جلب الأسماء الآن، لذا تظهر بعض الصفوف برقم العضوية فقط.",
  },
  principal: { en: "Principal", ar: "المشترك الرئيسي" },
  thisMember: { en: "This member", ar: "هذا العضو" },
  openMember: { en: "Open", ar: "فتح" },
  age: { en: "Age", ar: "العمر" },
  years: { en: "{n} yrs", ar: "{n} سنة" },
  ageApprox: { en: "approx.", ar: "تقريبي" },
  nationality: { en: "Nationality", ar: "الجنسية" },
  phone: { en: "Phone", ar: "الهاتف" },
  sex: { en: "Sex", ar: "النوع" },
  notDisclosedShort: { en: "Not shown to your role", ar: "غير متاح لدورك" },
  nameUnavailable: { en: "Name unavailable", ar: "الاسم غير متاح" },
  close: { en: "Close", ar: "إغلاق" },
  profileTitle: { en: "Full profile", ar: "الملف الكامل" },
  effectiveDate: { en: "Effective date", ar: "تاريخ السريان" },
  reason: { en: "Reason", ar: "السبب" },
  reasonRequired: { en: "A reason is required.", ar: "السبب مطلوب." },
  reasonOptional: { en: "Reason (optional)", ar: "السبب (اختياري)" },
  confirm: { en: "Confirm", ar: "تأكيد" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  group: { en: "Group", ar: "المجموعة" },
  none: { en: "No group", ar: "بدون مجموعة" },
  targetPlan: { en: "Move to plan", ar: "الانتقال إلى خطة" },
  carryPreview: { en: "What carries forward", ar: "ما ينتقل مع العضو" },
  carryHint: {
    en: "Consumption carries with the member. A member who has used 300 of 1,000 moving to a 500 plan has 200 left, not 500.",
    ar: "ينتقل الاستهلاك مع العضو. من استهلك ٣٠٠ من ١٠٠٠ وانتقل إلى خطة ٥٠٠ يتبقى له ٢٠٠ لا ٥٠٠.",
  },
  // The two readings of ADR-0020. Which one is in force is a SERVER setting, so the hint is chosen from the
  // preview's answer rather than hard-coded here — a screen that always claims consumption carries would be
  // stating the wrong rule outright on any deployment configured the other way.
  resetHint: {
    en: "Each plan carries its own ceiling here: the member starts the new plan at zero consumed.",
    ar: "لكل خطة سقفها الخاص هنا: يبدأ العضو الخطة الجديدة باستهلاك صفر.",
  },
  selectPlanFirst: { en: "Choose a plan to see what would change.", ar: "اختر خطة لعرض ما سيتغيّر." },
  currentLimit: { en: "Limit now", ar: "الحد الحالي" },
  newLimit: { en: "Limit after", ar: "الحد بعد التغيير" },
  notHeldToday: { en: "New benefit", ar: "منفعة جديدة" },
  dropped: { en: "Benefits that would be withdrawn", ar: "منافع ستُسحب" },
  droppedHint: {
    en: "The new plan does not cover these at all. The member holds them today.",
    ar: "الخطة الجديدة لا تغطي هذه إطلاقًا. العضو يملكها اليوم.",
  },
  previewing: { en: "Calculating…", ar: "جارٍ الحساب…" },
  carryResult: { en: "Applied — new balances", ar: "تم التطبيق — الأرصدة الجديدة" },
  exhausted: { en: "Exhausted", ar: "مستنفد" },
  backdated: {
    en: "This date is in the past. A back-dated membership change needs supervisory rights and will be refused without them.",
    ar: "هذا التاريخ في الماضي. يتطلب التغيير بأثر رجعي صلاحية إشرافية وسيُرفض بدونها.",
  },
  done: { en: "Change applied.", ar: "تم تطبيق التغيير." },
  utilizationCaption: { en: "Consumption against limit, by benefit category", ar: "الاستهلاك مقابل الحد، حسب فئة المنفعة" },
  reconcileBad: {
    en: "The accumulator and the reported total disagree. Treat these figures as provisional and raise it.",
    ar: "لا يتطابق المُراكِم مع الإجمالي المُبلَّغ. اعتبر هذه الأرقام مبدئية وأبلغ عنها.",
  },
  unavailable: { en: "Some figures could not be composed:", ar: "تعذّر تجميع بعض الأرقام:" },
  encounters: { en: "Encounters", ar: "الزيارات" },
  authorizations: { en: "Authorizations", ar: "التفويضات" },
  terminationReason: { en: "Termination reason", ar: "سبب الإنهاء" },
} satisfies Record<string, Localized>;

function statusKind(status: string): "ok" | "warn" | "bad" | "neu" | "info" {
  switch (status) {
    case "Active": return "ok";
    case "Suspended": return "warn";
    case "Terminated": case "Cancelled": return "bad";
    case "Pending": return "info";
    default: return "neu";
  }
}

/**
 * Bilingual labels for the membership enums the server sends as bare strings.
 *
 * policy-service types `status`, `relationship` and `waitingPeriodState` as `string` (see
 * `api/policyApi.ts`), and the table rendered them straight through — so an Arabic operator read a fully
 * Arabic table whose Status column said "Active", whose Relationship column said "Principal", and whose
 * Waiting period column said "None". A locale that is right everywhere except in the columns that carry the
 * decision is not a translated screen.
 *
 * The fallback is the raw value on purpose: a value the server adds later shows up as itself rather than
 * disappearing, which is the failure mode that would actually hide a member's state.
 */
const ENUM_LABELS: Record<string, Localized> = {
  Active: { en: "Active", ar: "نشط" },
  Pending: { en: "Pending", ar: "قيد الانتظار" },
  Suspended: { en: "Suspended", ar: "موقوف" },
  Terminated: { en: "Terminated", ar: "منتهٍ" },
  Cancelled: { en: "Cancelled", ar: "ملغى" },
  Principal: { en: "Principal", ar: "المشترك الرئيسي" },
  Spouse: { en: "Spouse", ar: "الزوج/الزوجة" },
  Child: { en: "Child", ar: "ابن/ابنة" },
  Dependent: { en: "Dependent", ar: "معال" },
  Serving: { en: "Serving", ar: "جارية" },
  Served: { en: "Served", ar: "منتهية" },
  None: { en: "None", ar: "لا يوجد" },
};

/** Resolve a server enum to the active language, falling back to the raw value. */
function useEnumLabel(): (value: string) => string {
  const t = useLoc();
  return (value: string) => (ENUM_LABELS[value] ? t(ENUM_LABELS[value]) : value);
}

// ── Member query ────────────────────────────────────────────────────────────────────────────────────────

export function MemberSearch({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const fmt = useFormat();
  const enumLabel = useEnumLabel();
  const [query, setQuery] = useState("");
  const [page, setPage] = useState<QueryPage<MemberQueryRow> | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [selected, setSelected] = useState<MemberQueryRow | null>(null);

  /*
   * ============================================================================================================
   * SEARCH, SORT AND PAGING ARE THE SERVER'S — unlike the approval queue, which does all three in the browser.
   * ============================================================================================================
   * The difference is the size of the thing. An approval queue is the hundred applications at the front of it;
   * this is the whole membership book, which is tens of thousands of rows and cannot be shipped to a browser to
   * be filtered there. `GET /member-query` already accepts every one of these filters plus `page`, `pageSize`
   * and `sort` — the screen was sending `name` and `pageSize: 50` and using one field of a query surface that
   * was fully built. So this is wiring, not new capability.
   *
   * One state object rather than a `useState` per field: the query is ONE thing, every change resets to page 1,
   * and a request has to be fired from exactly one place or two fields will race each other's results.
   */
  const [criteria, setCriteria] = useState<Criteria>(EMPTY_CRITERIA);
  const [draft, setDraft] = useState<Criteria>(EMPTY_CRITERIA);
  const [advancedOpen, setAdvancedOpen] = useState(false);
  const [pageNo, setPageNo] = useState(1);
  // Five. The roster is a LOOKUP, not a list to read: an operator searches for a person, opens them, and
  // works in the detail below — which is a tall panel with six tabs, so a long table just pushes the thing
  // they came for off the screen. Five rows is enough to tell "I found them" from "I need to narrow this",
  // and the pager's own size picker is there for the rare read-the-book case.
  const [pageSize, setPageSize] = useState(5);
  const [sort, setSort] = useState<{ field: string; dir: "ascending" | "descending" }>(
    { field: "memberno", dir: "ascending" });

  const run = useCallback(
    async (c: Criteria, p: number, size: number, sortBy: { field: string; dir: "ascending" | "descending" }) => {
      setError(null);
      try {
        setPage(await api.memberQuery({
          ...toQuery(c),
          page: p,
          pageSize: size,
          // The server's vocabulary: a leading "-" means descending (SortRequest.TryParse).
          sort: (sortBy.dir === "descending" ? "-" : "") + sortBy.field,
        }));
      } catch (e) {
        setError(readErrorMessage(e));
      }
    },
    [api],
  );

  // ONE effect drives every fetch, keyed on the whole query. A per-control handler that also fetched would
  // race: change the page size while a sort is in flight and the older response can land last.
  useEffect(() => {
    void run(criteria, pageNo, pageSize, sort);
  }, [run, criteria, pageNo, pageSize, sort]);

  // Narrowing moves the operator to the front of the result — staying on page 7 of a result that now has two
  // renders an empty table under a pager insisting there are matches.
  const applyCriteria = (next: Criteria) => {
    setCriteria(next);
    setDraft(next);
    setPageNo(1);
    setSelected(null);
  };

  const activeCount = countActive(criteria);

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.title)} />
      <Card>
        {/* The app's one search-bar vocabulary (QA follow-up): a labeled standard control + the solid
            primary action, exactly as Reception's eligibility search and the register form render theirs.
            The pill SearchField belongs to the app bar; borrowing it in-page paired a 999px-radius input
            with a differently-shaped secondary button and matched nothing else on screen. */}
        <form
          className="pol-searchbar"
          onSubmit={(e) => {
            e.preventDefault();
            // The quick box is the NAME field of the same query. It replaces the criteria rather than adding
            // to them, because an operator typing a name into the top box expects that to be the search — not
            // to be silently intersected with a filter they set ten minutes ago and cannot see.
            applyCriteria({ ...EMPTY_CRITERIA, name: query.trim() });
          }}
        >
          <InputField
            label={t(S.search)}
            value={query}
            onChange={(e) => setQuery(e.currentTarget.value)}
            autoComplete="off"
          />
          <Button type="submit" variant="primary">
            {t(S.find)}
          </Button>
          <Button
            type="button"
            variant="secondary"
            aria-expanded={advancedOpen}
            onClick={() => setAdvancedOpen((v) => !v)}
          >
            {t(advancedOpen ? S.advancedHide : S.advanced)}
            {activeCount > 0 ? ` (${activeCount})` : ""}
          </Button>
        </form>

        {/*
          * The advanced panel edits a DRAFT and applies it on submit, rather than firing a query per keystroke
          * across ten fields. Ten live filters over a table this size is ten requests for one intention, and
          * the results arrive out of order.
          */}
        {advancedOpen && (
          <form
            className="pol-advanced"
            aria-label={t(S.advanced)}
            onSubmit={(e) => { e.preventDefault(); applyCriteria(draft); }}
          >
            <p className="pol-muted">{t(S.advancedHint)}</p>
            <div className="pol-advanced-grid">
              <InputField label={t(S.fName)} value={draft.name} autoComplete="off"
                onChange={(e) => setDraft({ ...draft, name: e.currentTarget.value })} />
              <InputField label={t(S.fMemberNo)} value={draft.memberNo} autoComplete="off"
                onChange={(e) => setDraft({ ...draft, memberNo: e.currentTarget.value })} />

              <FilterSelect label={t(S.fIdType)} value={draft.identifierType}
                onChange={(v) => setDraft({ ...draft, identifierType: v })}
                options={[{ value: "", label: t(S.anyValue) }, ...ID_TYPES.map((v) => ({ value: v, label: v }))]} />
              <InputField
                label={t(S.fIdValue)} value={draft.identifierValue} autoComplete="off"
                // Stated at the pair, not after a fruitless search: a type with no number narrows nothing, and
                // the server would answer with a page that looks like "no matches".
                error={draft.identifierType !== "" && draft.identifierValue.trim() === "" ? t(S.idTypeNeedsValue) : undefined}
                onChange={(e) => setDraft({ ...draft, identifierValue: e.currentTarget.value })} />

              <FilterSelect label={t(S.fStatus)} value={draft.status}
                onChange={(v) => setDraft({ ...draft, status: v })}
                options={[{ value: "", label: t(S.anyValue) }, ...MEMBER_STATUSES.map((v) => ({ value: v, label: enumLabel(v) }))]} />
              <FilterSelect label={t(S.fRelationship)} value={draft.relationship}
                onChange={(v) => setDraft({ ...draft, relationship: v })}
                options={[{ value: "", label: t(S.anyValue) }, ...RELATIONSHIPS.map((v) => ({ value: v, label: enumLabel(v) }))]} />
              <FilterSelect label={t(S.fWaiting)} value={draft.waitingPeriod}
                onChange={(v) => setDraft({ ...draft, waitingPeriod: v })}
                options={[{ value: "", label: t(S.anyValue) }, ...WAITING_STATES.map((v) => ({ value: v, label: enumLabel(v) }))]} />
              <FilterSelect label={t(S.fBand)} value={draft.utilizationBand}
                onChange={(v) => setDraft({ ...draft, utilizationBand: v })}
                options={[{ value: "", label: t(S.anyValue) }, ...BANDS.map((v) => ({ value: v, label: enumLabel(v) }))]} />

              <InputField type="date" label={t(S.fEnrolledFrom)} value={draft.enrolledFromAfter}
                onChange={(e) => setDraft({ ...draft, enrolledFromAfter: e.currentTarget.value })} />
              <InputField type="date" label={t(S.fEnrolledTo)} value={draft.enrolledToBefore}
                onChange={(e) => setDraft({ ...draft, enrolledToBefore: e.currentTarget.value })} />
            </div>
            <div className="pol-advanced-actions">
              <Button type="submit" variant="primary">{t(S.applyFilters)}</Button>
              <Button type="button" variant="ghost" onClick={() => { setQuery(""); applyCriteria(EMPTY_CRITERIA); }}>
                {t(S.clearFilters)}
              </Button>
              {activeCount > 0 && (
                <span className="pol-muted tnum" aria-live="polite">
                  {t(S.activeFilters).replace("{n}", String(activeCount))}
                </span>
              )}
            </div>
          </form>
        )}
      </Card>

      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {page?.payerScopeApplied && <InlineAlert tone="info">{t(S.payerScoped)}</InlineAlert>}
      {/* A truncated identity match makes the page a SUBSET. Saying so is the difference between a search
          and a wrong answer. */}
      {page?.identityMatchTruncated && <InlineAlert tone="warn">{t(S.truncated)}</InlineAlert>}

      <Card>
        <DataTable
          caption={t(S.title)}
          rows={page?.items ?? []}
          rowKey={(r) => r.enrollmentId}
          interactive
          selectedKey={selected?.enrollmentId ?? null}
          onSelect={(r) => setSelected(r)}
          loading={page === null && !error}
          emptyLabel={t(S.noMembers)}
          /*
            CONTROLLED sort, because the SERVER sorts. The table's own sort would order the 25 rows it was
            handed and leave the actual first member several pages away — the same trap `useTableQuery`
            documents, arriving here through a different door. Only the columns in `MemberSortFields.Allowed`
            are marked sortable: a header that offers an order the server rejects answers with a 400.
          */
          sortKey={sort.field}
          sortDir={sort.dir}
          onSort={(key) => {
            setSort((prev) => (prev.field === key
              ? { field: key, dir: prev.dir === "ascending" ? "descending" : "ascending" }
              : { field: key, dir: "ascending" }));
            setPageNo(1);
          }}
          columns={[
            { key: "memberno", header: t(S.memberNo), cell: (r) => r.memberNo, sortable: true },
            {
              key: "name",
              header: t(S.name),
              /*
               * A blank name is legible; a wrong one is not. patient-service could not be asked → null.
               *
               * But an em dash is the table's word for "this field is empty", and that is not what happened
               * here: the membership exists and the PERSON RECORD behind it could not be read (bulk-imported
               * enrolments reference beneficiary ids patient-service does not hold — §12.6). Rendering the
               * same dash the card-number column uses invited "this member has no name on file", which is a
               * data-quality accusation against the wrong record. The member's own detail card has said
               * "Name unavailable" since 19.5; the roster now says it too, so one person does not have two
               * different explanations depending on which screen you opened.
               */
              cell: (r) =>
                [r.givenName, r.familyName].filter(Boolean).join(" ") || (
                  <span className="muted">{t(S.nameUnavailable)}</span>
                ),
            },
            {
              // The card number the beneficiary is holding. Not sortable — the server does not offer it as a
              // sort field, and a header that promises an order the server rejects answers with a 400.
              key: "cardNumber",
              header: t(S.cardNo),
              cell: (r) => <span className="tnum">{r.cardNumber ?? "—"}</span>,
            },
            { key: "plan", header: t(S.plan), cell: (r) => r.planLabel ?? "—" },
            { key: "relationship", header: t(S.relationship), cell: (r) => enumLabel(r.relationship), sortable: true },
            { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={statusKind(r.status)} label={enumLabel(r.status)} />, sortable: true },
            { key: "effectivefrom", header: t(S.from), cell: (r) => fmt.date(r.effectiveFrom), sortable: true },
            {
              // A chip is for a state that changes what the operator does. "None" is not one: it was
              // rendered on every row of every page, so the column that exists to flag the handful of
              // members still serving a waiting period was drawing the eye to the ones who are not. The
              // dash is the same "nothing here" the rest of the table already uses, which leaves the chip
              // meaning what it should — this member's cover has a condition on it today.
              key: "waiting",
              header: t(S.waiting),
              cell: (r) =>
                r.waitingPeriodState === "Serving" ? (
                  <StatusChip kind="warn" label={enumLabel(r.waitingPeriodState)} />
                ) : r.waitingPeriodState === "Served" ? (
                  <StatusChip kind="ok" label={enumLabel(r.waitingPeriodState)} />
                ) : (
                  "—"
                ),
            },
            {
              /*
               * Three different facts used to render as two symbols.
               *
               * `percentUsed` is null in TWO unrelated cases, and the server distinguishes them on the same
               * row: `utilizationBand` is `Unlimited` when the member is covered with no accumulating
               * ceiling, and `Zero` with a null percentage when there is no coverage to meter at all. Both
               * arrived as "—", next to rows reading "0%" — so a member whose benefit was never metered and
               * a member with no cover looked identical, and both looked like a member who simply has not
               * claimed yet. `libs/benefit-pricing` is explicit that Unlimited is NOT Zero: "an unlimited
               * benefit reported as 0% invites 'plenty left' on something that was never metered."
               *
               * Each fact now says itself. 0% keeps meaning what it means — metered, nothing used.
               */
              key: "percentused",
              header: t(S.used),
              cell: (r) =>
                r.percentUsed != null ? (
                  `${Math.round(r.percentUsed)}%`
                ) : r.utilizationBand === "Unlimited" ? (
                  <span className="muted">{t(S.unlimited)}</span>
                ) : (
                  <span className="muted">{t(S.noCover)}</span>
                ),
              sortable: true,
            },
          ]}
        />
        {/*
          * Server-paged, and shown ALWAYS — deliberately unlike the approval queue, which hides its pager
          * when one page holds everything.
          *
          * A queue is work to get through; a pager that cannot be pressed is noise on top of it. This is the
          * membership BOOK, and its size is the answer to a question operators actually ask — "how many
          * members match this?" is why the advanced search exists. "Showing 1–25 of 25" is information even
          * when there is one page, and the size picker is the only way to ask for 50 or 100 rows, which
          * cannot be reached if the control appears only once the result is already large.
          */}
        {page && (
          <Pagination
            page={pageNo}
            pageSize={pageSize}
            total={page.totalCount}
            onPageChange={(p) => { setPageNo(p); setSelected(null); }}
            onPageSizeChange={(n) => { setPageSize(n); setPageNo(1); setSelected(null); }}
            // The default HAS to be in this list. A <select> whose value matches no option renders the first
            // one instead, so the picker would have shown "10" while the table was serving 5 — a control
            // silently disagreeing with what is on screen.
            pageSizeOptions={[5, 10, 25, 50, 100]}
          />
        )}
      </Card>

      {!selected && <InlineAlert tone="info">{t(S.select)}</InlineAlert>}
      {selected && (
        <MemberDetail
          api={api}
          row={selected}
          // Re-run the SAME query, on the same page, with the same sort — a change to one member must not
          // reset the operator to the top of an unfiltered list.
          onChanged={() => void run(criteria, pageNo, pageSize, sort)}
        />
      )}
    </div>
  );
}

/*
 * ============================================================================================================
 * THE ADVANCED SEARCH CRITERIA
 * ============================================================================================================
 * One record rather than nine `useState`s. Three things fall out of that and all three were bugs waiting to
 * happen otherwise: the whole query is one value so a fetch effect can depend on it, "how many filters are
 * applied" is countable, and Clear is `EMPTY_CRITERIA` rather than nine setters somebody will eventually
 * forget to extend.
 *
 * The field names are the SERVER'S query parameters, deliberately. A local vocabulary mapped at the edge is
 * one more place for "status" to mean the enrollment's here and the beneficiary's there.
 */
interface Criteria {
  name: string;
  memberNo: string;
  identifierType: string;
  identifierValue: string;
  status: string;
  relationship: string;
  waitingPeriod: string;
  utilizationBand: string;
  enrolledFromAfter: string;
  enrolledToBefore: string;
}

const EMPTY_CRITERIA: Criteria = {
  name: "", memberNo: "", identifierType: "", identifierValue: "",
  status: "", relationship: "", waitingPeriod: "", utilizationBand: "",
  enrolledFromAfter: "", enrolledToBefore: "",
};

/**
 * Criteria → query string parameters, dropping everything empty.
 *
 * An identifier type WITHOUT a value is dropped as a pair: the server searches the directory when
 * `identifierValue` is present, and sending a lone type would either be ignored or — worse — read as "all
 * passports", which is not what the operator asked and would silently return the wrong page.
 */
function toQuery(c: Criteria): Record<string, string | undefined> {
  const v = (x: string) => (x.trim() === "" ? undefined : x.trim());
  const hasIdentifier = v(c.identifierValue) !== undefined;
  return {
    name: v(c.name),
    memberNo: v(c.memberNo),
    identifierType: hasIdentifier ? v(c.identifierType) : undefined,
    identifierValue: hasIdentifier ? v(c.identifierValue) : undefined,
    status: v(c.status),
    relationship: v(c.relationship),
    waitingPeriod: v(c.waitingPeriod),
    utilizationBand: v(c.utilizationBand),
    enrolledFromAfter: v(c.enrolledFromAfter),
    enrolledToBefore: v(c.enrolledToBefore),
  };
}

const countActive = (c: Criteria): number =>
  Object.values(toQuery(c)).filter((x) => x !== undefined).length;

/** The closed vocabularies the server validates against (`TryParseMemberFacets`, `IdentifierType`). */
const ID_TYPES = ["NationalID", "Passport", "RefugeeID", "UNHCRNo", "MemberNo"] as const;
const MEMBER_STATUSES = ["Active", "Terminated", "Cancelled"] as const;
const RELATIONSHIPS = ["Principal", "Spouse", "Child", "Dependent"] as const;
const WAITING_STATES = ["Serving", "Served", "None"] as const;
const BANDS = ["Low", "Medium", "High", "Exhausted"] as const;

/**
 * A labelled select for the filter grid.
 *
 * ============================================================================================================
 * NOW THE DESIGN SYSTEM'S, NOT A LOCAL LOOKALIKE
 * ============================================================================================================
 * This used to wrap a NATIVE `<select>` in the field markup by hand, with a note saying the design system's
 * `Select` was "wrong here" because ten filters need visible labels bound to their control. The objection was
 * fair and is now answered: `SelectField` is that control with that label. A native select cannot style its
 * own option list — the popup is drawn by the OS — so every one of these opened a system-blue list with square
 * corners, and sat at a slightly different height from the `InputField`s beside it.
 *
 * Kept as a named wrapper rather than replaced at ~15 call sites: the filter grid's contract is
 * `value: string` where `""` means "Any", and `SelectField`'s is `string | null` where null means "nothing
 * chosen". Those are different ideas and the translation belongs in one place.
 */
function FilterSelect({
  label,
  value,
  onChange,
  options,
}: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  options: ReadonlyArray<{ value: string; label: string }>;
}) {
  return (
    <SelectField
      label={label}
      value={value}
      onChange={onChange}
      options={[...options]}
    />
  );
}

// ── Member detail ───────────────────────────────────────────────────────────────────────────────────────

type Dialog = "terminate" | "reinstate" | "changeGroup" | "changePlan" | null;

/*
 * `status` and `profile` are NOT in `Dialog` on purpose. `Dialog` is the set of MEMBERSHIP operations
 * `MembershipDialog` performs against policy-service with one shared shape — effective date, reason,
 * idempotency key. A beneficiary status change is a different aggregate in a different service, and the full
 * profile is a read. Folding all three into one union would have meant one component with three unrelated
 * bodies, which is how a dialog ends up posting an effective date to an endpoint that has no use for one.
 */

export function MemberDetail({
  api,
  row,
  onChanged,
}: {
  api: PolicyApi;
  row: MemberQueryRow;
  onChanged: () => void;
}) {
  const t = useLoc();
  const patientApi = useApi();
  const enumLabel = useEnumLabel();
  const fmt = useFormat();
  const { lang } = useTheme();
  const [tab, setTab] = useState("coverage");
  const [dialog, setDialog] = useState<Dialog>(null);
  const [statusOpen, setStatusOpen] = useState(false);
  const [profileOpen, setProfileOpen] = useState(false);
  const [familyOpen, setFamilyOpen] = useState(false);
  const [editOpen, setEditOpen] = useState(false);
  const [announce, setAnnounce] = useState("");
  /**
   * Bumped after every write against this membership.
   *
   * The tabs below the card stay mounted while the card's actions are used above them, so a plan change, a
   * termination, a status change or a correction left the Logs tab showing the history as it was when the tab
   * was opened — and the only way to see your own change was to reload the application. Now the write tells
   * the panel, and the panel re-reads.
   */
  const [changeSeq, setChangeSeq] = useState(0);
  const recordChanged = useCallback(() => setChangeSeq((n) => n + 1), []);
  const [coverage, setCoverage] = useState<MemberCoverageDetail | null>(null);
  const [person, setPerson] = useState<BeneficiaryRow | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  const loadCoverage = useCallback(async () => {
    try {
      setCoverage(await api.coverageDetails(row.enrollmentId));
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }, [api, row.enrollmentId]);

  /*
   * THE IDENTITY RECORD IS READ ONCE, HERE — not by the header and again by the Details tab.
   *
   * `GET /beneficiaries/{id}` is an audited PHI read that projects by role. Two components fetching it
   * independently would write two disclosure entries every time somebody opened a member, and the audit trail
   * would say a record was read twice when it was looked at once. So the parent holds it and passes it down;
   * a correction reloads it in one place and the header and the tab cannot disagree.
   *
   * A failure here is NOT surfaced as the screen's error. The membership is what this screen is about and it
   * renders without the identity record; the fields that depend on it simply say nothing, which is the same
   * thing they say when the caller's role was not given them.
   */
  const loadPerson = useCallback(async () => {
    try {
      setPerson(await patientApi.beneficiary(row.beneficiaryId));
    } catch {
      setPerson(null);
    }
  }, [patientApi, row.beneficiaryId]);

  useEffect(() => {
    setCoverage(null);
    void loadCoverage();
  }, [loadCoverage]);

  useEffect(() => {
    setPerson(null);
    void loadPerson();
  }, [loadPerson]);

  const fullName = [row.givenName, row.familyName].filter(Boolean).join(" ");

  return (
    <div className="pol-detail" data-testid="member-detail">
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>

      {/*
        * ============================================================================================================
        * THE MEMBER CARD — three bands, in the order the questions get asked
        * ============================================================================================================
        * WHO is this (photo, name, status, and the four facts a desk uses to recognise somebody: age, sex,
        * nationality, phone) · WHAT do they hold (member number, plan, relationship, dates) · WHAT can I do.
        *
        * It was one flat row of four labelled facts above six same-sized buttons, which gave the destructive
        * action the same weight as the reads and put "who is this person" nowhere at all. The bands are
        * separated by a rule rather than by spacing alone, because the third one contains Terminate.
        */}
      <Card>
        <div className="mem-card">
          <div className="mem-identity">
            <MemberAvatar beneficiaryId={row.beneficiaryId} name={fullName || row.memberNo} />

            <div className="mem-identity-text">
              {/*
                * THE PERSON'S NAME is the title. It always was — but the fallback was `row.memberNo`, so
                * whenever the name was unavailable the heading printed the member number and the `Member no.`
                * field directly beneath it printed the same string again. Two identical lines, one of them
                * pretending to be a name.
                *
                * The fallback now SAYS the name is missing. That is a different claim from "this person is
                * called MEM-fe743906fbcb", and it is the one an operator can act on — names ride on
                * policy-service's per-page lookup to patient-service, so a blank one means that lookup found
                * nothing, not that the person is nameless.
                */}
              <div className="mem-nameline">
                <h2>{fullName || <span className="muted">{t(S.nameUnavailable)}</span>}</h2>
                <StatusChip kind={statusKind(row.status)} label={enumLabel(row.status)} />
              </div>
              <p className="mem-sub tnum">
                {row.memberNo}
                <span aria-hidden="true"> · </span>
                {enumLabel(row.relationship)}
              </p>
              <GeneralInformation person={person} />
            </div>
          </div>

          <dl className="mem-facts">
            <div>
              <dt>{t(S.plan)}</dt>
              <dd>
                {row.planLabel ?? "—"}
                {coverage?.planVersionNo != null && ` · ${t(S.planVersion)} ${coverage.planVersionNo}`}
              </dd>
            </div>
            <div>
              <dt>{t(S.from)}</dt>
              <dd className="tnum">
                {fmt.date(row.effectiveFrom)} → {row.effectiveTo ? fmt.date(row.effectiveTo) : "—"}
              </dd>
            </div>
            {/* Projected only for case-handling roles; a null here means the caller is not entitled to it,
                not that the membership ended for no reason. */}
            {row.terminationReason && (
              <div>
                <dt>{t(S.terminationReason)}</dt>
                <dd>{row.terminationReason}</dd>
              </div>
            )}
          </dl>

          {/*
            * EVERY action here opens a modal, including the profile.
            *
            * It was a mixed set: four buttons that revealed an inline panel further down the page, and one
            * anchor that navigated away entirely. Two different things happening to the same-looking row of
            * controls is a row an operator cannot predict — and the navigation was the worse half, because
            * looking someone up, opening their file and coming back lost the search, the selected member and
            * the tab they were on. A member lookup is a glance; it should not cost the page you were on.
            *
            * The profile keeps its own route (`/patients/{id}`) for deep links from notifications and
            * worklists — this only changes how it opens FROM HERE.
            *
            * ORDER IS BY CONSEQUENCE, not by how the code grew: the three that only READ or correct come
            * first, the three that move the membership follow, and Terminate is pushed to the far end behind
            * its own separator. It used to sit second, one button away from the primary — the two most likely
            * mis-clicks on the card were "open the profile" and "end this person's cover".
            */}
          <div className="mem-actions">
            <Button
              size="sm"
              variant="primary"
              leadingIcon={<Icon name="user" />}
              onClick={() => setProfileOpen(true)}
              aria-haspopup="dialog"
            >
              {t(S.openProfile)}
            </Button>
            {/* The household. A read, so it sits with the reads — and it answers the question an officer asks
                before any of the changes below: is this cover only about the person in front of me. */}
            <Button
              size="sm"
              variant="secondary"
              leadingIcon={<Icon name="users" />}
              onClick={() => setFamilyOpen(true)}
              aria-haspopup="dialog"
              data-testid="open-family"
            >
              {t(S.family)}
            </Button>
            {/*
              * Correcting the record, from the card rather than only from the Details tab.
              *
              * The edit has lived behind the Details tab since 19.6c, which is the right home for the full
              * twelve-field record — but the reason an officer opens a member is usually that one of those
              * fields is wrong, and finding it meant knowing that a tab labelled "Details" was where writing
              * happened. Same modal, same PATCH, same log: the affordance is what moved.
              */}
            <Button
              size="sm"
              variant="secondary"
              leadingIcon={<Icon name="pen" />}
              onClick={() => setEditOpen(true)}
              aria-haspopup="dialog"
              disabled={person === null}
              data-testid="edit-details"
            >
              {t(S.editDetails)}
            </Button>

            <span className="mem-actions-split" aria-hidden="true" />

            <Button
              size="sm"
              variant="secondary"
              leadingIcon={<Icon name="swap" />}
              onClick={() => setDialog("changePlan")}
              aria-haspopup="dialog"
            >
              {t(S.changePlan)}
            </Button>
            <Button
              size="sm"
              variant="secondary"
              leadingIcon={<Icon name="folder" />}
              onClick={() => setDialog("changeGroup")}
              aria-haspopup="dialog"
            >
              {t(S.changeGroup)}
            </Button>
            {/*
              * The former "Status & Reactivation" screen, as an action on the record it acts on.
              *
              * It sits beside Change plan because that is the neighbour it belongs to: both change what this
              * person is entitled to, one at the membership level and one at the beneficiary level.
              *
              * ALWAYS ENABLED, and the dialog explains itself. It was briefly disabled when there was no legal
              * move, which produced a permanently grey button whenever the beneficiary's status had not been
              * disclosed to the caller — indistinguishable, from the outside, from a broken control. The two
              * dead ends have different answers ("a director unlocks a blocked record" / "your role was not
              * told this person's status") and only the dialog can say which, so it does.
              */}
            <Button
              size="sm"
              variant="secondary"
              leadingIcon={<Icon name="toggle" />}
              onClick={() => setStatusOpen(true)}
              aria-haspopup="dialog"
            >
              {t(S.statusChange)}
            </Button>
            <Button
              size="sm"
              variant="secondary"
              leadingIcon={<Icon name="undo" />}
              onClick={() => setDialog("reinstate")}
              aria-haspopup="dialog"
            >
              {t(S.reinstate)}
            </Button>

            <span className="mem-actions-split" aria-hidden="true" />

            {/* Ending someone's cover is the one action here that cannot be undone by the next dialog, so it
                carries the danger variant the design system already defines for exactly this (0B §6), and its
                own separator to keep it out of the run of secondaries. */}
            <Button
              size="sm"
              className="mem-actions-end"
              variant="danger"
              leadingIcon={<Icon name="cross" />}
              onClick={() => setDialog("terminate")}
              aria-haspopup="dialog"
            >
              {t(S.terminate)}
            </Button>
          </div>
        </div>
      </Card>

      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {familyOpen && (
        <FamilyModal
          api={api}
          enrollmentId={row.enrollmentId}
          onClose={() => setFamilyOpen(false)}
        />
      )}

      {/* The Details tab's modal, opened from the card. One component, so the validation, the diff-on-save and
          the log entry cannot differ depending on which button you pressed. */}
      {editOpen && person && (
        <EditDetailsModal
          person={person}
          onClose={() => setEditOpen(false)}
          onSaved={async (changed) => {
            setEditOpen(false);
            setAnnounce(changed.length === 0 ? t(P.noChanges) : t(P.saved).replace("{n}", String(changed.length)));
            await loadPerson();
            // The roster carries the name and the card number, so a correction to either has to reach it.
            onChanged();
            recordChanged();
          }}
        />
      )}

      {statusOpen && (
        <StatusChangeModal
          beneficiaryId={row.beneficiaryId}
          name={fullName || row.memberNo}
          // The BENEFICIARY status, not `row.status` — that one is the ENROLLMENT's (Active/Terminated/
          // Cancelled), and feeding it to the transition table would offer moves from a state the person is
          // not in. Two different lifecycles on one row is exactly the confusion this comment exists for.
          statusRaw={row.beneficiaryStatus}
          onClose={() => setStatusOpen(false)}
          onChanged={() => {
            setStatusOpen(false);
            onChanged();
            recordChanged();
          }}
        />
      )}

      {profileOpen && (
        <Modal
          open
          onOpenChange={(o) => !o && setProfileOpen(false)}
          title={t(S.profileTitle)}
          closeLabel={t(S.close)}
          wide
          footer={<Button variant="ghost" onClick={() => setProfileOpen(false)}>{t(S.close)}</Button>}
        >
          <Suspense fallback={<div className="async-loading" role="status" aria-live="polite"><span className="mrs-spin" aria-hidden="true" /></div>}>
            <PatientProfile beneficiaryId={row.beneficiaryId} />
          </Suspense>
        </Modal>
      )}

      {dialog && (
        <MembershipDialog
          api={api}
          kind={dialog}
          row={row}
          onClose={() => setDialog(null)}
          onDone={async (msg) => {
            setAnnounce(msg);
            setDialog(null);
            await loadCoverage();
            onChanged();
            recordChanged();
          }}
        />
      )}

      <Tabs
        aria-label={t(S.title)}
        value={tab}
        onValueChange={setTab}
        items={[
          // The IDENTITY record, first — "who is this" precedes "what are they entitled to", and it is the
          // half the registration form fills in and nothing since could correct.
          {
            value: "person",
            label: t(S.tabPerson),
            content: tab === "person"
              ? <BeneficiaryPanel person={person} onReload={loadPerson} onChanged={onChanged} />
              : null,
          },
          { value: "coverage", label: t(S.tabCoverage), content: <CoverageTab coverage={coverage} /> },
          { value: "utilization", label: t(S.tabUtilization), content: tab === "utilization" ? <MemberUtilizationTab api={api} beneficiaryId={row.beneficiaryId} /> : null },
          { value: "notes", label: t(S.tabNotes), content: tab === "notes" ? <NotesPanel api={api} scope="enrollments" scopeRef={row.enrollmentId} /> : null },
          // The member's documents get the richer panel: a typed upload, and the two affordances an officer
          // actually needs on a filed document — see it in place, or take a copy. The generic DocumentsPanel
          // stays for the POLICY scope, where nothing is uploaded from the screen and there is no photo.
          { value: "documents", label: t(S.tabDocuments), content: tab === "documents" ? <BeneficiaryDocuments api={api} enrollmentId={row.enrollmentId} /> : null },
          { value: "timeline", label: t(S.tabTimeline), content: tab === "timeline" ? <ChangeTimeline api={api} scope="enrollments" scopeRef={row.enrollmentId} lang={lang} reloadToken={changeSeq} /> : null },
        ]}
      />
    </div>
  );
}

/**
 * The four facts a desk uses to tell one person from another: age, sex, nationality, phone.
 *
 * ============================================================================================================
 * WHY THIS RENDERS NOTHING RATHER THAN PLACEHOLDERS
 * ============================================================================================================
 * Every field here is FIELD-PROJECTED by patient-service: `undefined` means the caller's role was not given
 * it, `null` means the record does not hold it. A dash for both would tell an officer the system has no phone
 * number for a person whose number they are simply not entitled to see — and they would then ask the
 * beneficiary to repeat something already on file. So a withheld field is absent, an empty one shows a dash,
 * and the whole strip disappears when the role receives none of it.
 *
 * The icon labels the field and the text carries the value. Neither stands alone: each chip has a `title` and
 * a visually-hidden label, because an icon is not a word and a screen reader must be told which is which.
 */
function GeneralInformation({ person }: { person: BeneficiaryRow | null }) {
  const t = useLoc();
  if (!person) return null;

  const phone = person.contacts?.find((c) => c.type === "Phone" && c.isPrimary)
    ?? person.contacts?.find((c) => c.type === "Phone");

  // Whole years, from a date the caller was allowed to read. Not a band: this role reads the exact birth date
  // one tab away, so banding it here would be a privacy gesture rather than a privacy measure.
  const age = person.birthDate ? yearsSince(person.birthDate) : null;

  const chips: { key: string; icon: "calendar" | "sex" | "globe" | "phone"; label: string; value: string }[] = [];
  if (age !== null) {
    chips.push({
      key: "age",
      icon: "calendar",
      label: t(S.age),
      // The approximate flag travels WITH the date, here as everywhere: an estimated birth date shown as an
      // exact age is how an estimate becomes a hard eligibility cutoff.
      value: t(S.years).replace("{n}", String(age)) + (person.birthDateIsApproximate ? ` (${t(S.ageApprox)})` : ""),
    });
  }
  if (person.sex) chips.push({ key: "sex", icon: "sex", label: t(S.sex), value: person.sex });
  if (person.nationalityCode) {
    chips.push({ key: "nat", icon: "globe", label: t(S.nationality), value: person.nationalityCode });
  }
  if (phone) chips.push({ key: "phone", icon: "phone", label: t(S.phone), value: phone.value });

  if (chips.length === 0) return null;

  return (
    <ul className="mem-info" data-testid="member-general-info">
      {chips.map((chip) => (
        <li key={chip.key} title={`${chip.label}: ${chip.value}`}>
          <Icon name={chip.icon} width={16} height={16} aria-hidden="true" />
          <span className="sr-only">{chip.label}: </span>
          <span>{chip.value}</span>
        </li>
      ))}
    </ul>
  );
}

/** Whole years between an ISO date and today. Returns null for anything unparseable rather than a negative
 *  age, which is what a malformed date would otherwise render as. */
function yearsSince(isoDate: string): number | null {
  const born = new Date(isoDate);
  if (Number.isNaN(born.getTime())) return null;
  const now = new Date();
  let years = now.getFullYear() - born.getFullYear();
  const monthDelta = now.getMonth() - born.getMonth();
  if (monthDelta < 0 || (monthDelta === 0 && now.getDate() < born.getDate())) years -= 1;
  return years >= 0 && years < 130 ? years : null;
}

/**
 * The covered household.
 *
 * ============================================================================================================
 * A READ, AND A NARROW ONE
 * ============================================================================================================
 * `GET /enrollments/{id}/family` returns the principal and every dependant under them — this member included
 * and marked, because a family list that silently omits the person you opened reads as a list with somebody
 * missing. Names come from patient-service through the caller's own token, so a role that may not read them
 * gets member numbers rather than somebody else's answer.
 *
 * It does NOT open the other members: switching the roster's selection out from under an operator mid-task is
 * how you lose the reason they came. The row carries the member number, which is what they search with.
 */
function FamilyModal({
  api,
  enrollmentId,
  onClose,
}: {
  api: PolicyApi;
  enrollmentId: string;
  onClose: () => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const enumLabel = useEnumLabel();
  const [view, setView] = useState<FamilyView | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    void (async () => {
      try {
        const result = await api.family(enrollmentId);
        if (live) setView(result);
      } catch (e) {
        if (live) setError(readErrorMessage(e));
      }
    })();
    return () => { live = false; };
  }, [api, enrollmentId]);

  // The subject is always in the household, so "one row" means "nobody else is on this cover".
  const alone = view !== null && view.members.filter((m) => !m.isSubject).length === 0;

  return (
    <Modal
      open
      onOpenChange={(o) => !o && onClose()}
      title={t(S.familyTitle)}
      closeLabel={t(S.close)}
      wide
      footer={<Button variant="ghost" onClick={onClose}>{t(S.close)}</Button>}
    >
      <p className="pol-muted">{t(S.familyIntro)}</p>

      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {view === null && !error && (
        <div className="async-loading" role="status" aria-live="polite">
          <span className="mrs-spin" aria-hidden="true" />
        </div>
      )}

      {view !== null && alone && <InlineAlert tone="info">{t(S.familyAlone)}</InlineAlert>}

      {view !== null && !alone && (
        <div className="pol-tablewrap">
          <table className="pol-grid" data-testid="family-table">
            <caption className="sr-only">{t(S.familyTitle)}</caption>
            <thead>
              <tr>
                <th scope="col">{t(S.name)}</th>
                <th scope="col">{t(S.memberNo)}</th>
                <th scope="col">{t(S.relationship)}</th>
                <th scope="col">{t(S.plan)}</th>
                <th scope="col">{t(S.from)}</th>
                <th scope="col">{t(S.status)}</th>
              </tr>
            </thead>
            <tbody>
              {view.members.map((member) => (
                <FamilyRow key={member.enrollmentId} member={member} fmt={fmt} t={t} enumLabel={enumLabel} />
              ))}
            </tbody>
          </table>
        </div>
      )}

      {/* Both of these are said out loud rather than left to be inferred from a short list. */}
      {view !== null && view.withheld > 0 && (
        <InlineAlert tone="warn">{t(S.familyWithheld).replace("{n}", String(view.withheld))}</InlineAlert>
      )}
      {view !== null && view.unavailable.length > 0 && (
        <InlineAlert tone="warn">{t(S.familyNamesUnavailable)}</InlineAlert>
      )}
    </Modal>
  );
}

function FamilyRow({
  member,
  fmt,
  t,
  enumLabel,
}: {
  member: CoveredFamilyMember;
  fmt: ReturnType<typeof useFormat>;
  t: (value: Localized) => string;
  enumLabel: (value: string) => string;
}) {
  const name = [member.givenName, member.familyName].filter(Boolean).join(" ");
  return (
    <tr data-testid="family-row" data-subject={member.isSubject || undefined}>
      <th scope="row">
        {name || <span className="muted">{t(S.nameUnavailable)}</span>}
        {/* Two different facts, and a row can carry both: who the cover belongs to, and which row you came
            from. Words, not styling — a bold row says nothing to a screen reader. */}
        {member.isPrincipal && <StatusChip kind="info" label={t(S.principal)} />}
        {member.isSubject && <StatusChip kind="neu" label={t(S.thisMember)} />}
      </th>
      <td className="tnum">{member.memberNo}</td>
      <td>{enumLabel(member.relationship)}</td>
      <td>{member.planLabel ?? "—"}</td>
      <td className="tnum">
        {member.effectiveFrom ? fmt.date(member.effectiveFrom) : "—"}
        {" → "}
        {member.effectiveTo ? fmt.date(member.effectiveTo) : "—"}
      </td>
      <td><StatusChip kind={statusKind(member.status)} label={enumLabel(member.status)} /></td>
    </tr>
  );
}

// ── The identity record ─────────────────────────────────────────────────────────────────────────────────

const P = {
  title: { en: "Registration details", ar: "بيانات التسجيل" },
  intro: {
    en: "The record as the registration form captured it. Corrections are logged with what changed, by whom and when.",
    ar: "السجل كما التقطه نموذج التسجيل. تُسجَّل التصحيحات مع بيان ما تغيّر ومن غيّره ومتى.",
  },
  edit: { en: "Edit details", ar: "تعديل البيانات" },
  save: { en: "Save changes", ar: "حفظ التغييرات" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  close: { en: "Close", ar: "إغلاق" },
  saved: { en: "{n} field(s) updated.", ar: "تم تحديث {n} حقل." },
  noChanges: { en: "Nothing was changed.", ar: "لم يتغيّر شيء." },

  givenName: { en: "Given name", ar: "الاسم الأول" },
  middleName: { en: "Middle name", ar: "الاسم الأوسط" },
  familyName: { en: "Family name", ar: "اسم العائلة" },
  birthDate: { en: "Date of birth", ar: "تاريخ الميلاد" },
  approximate: { en: "The date of birth is approximate", ar: "تاريخ الميلاد تقريبي" },
  approxTag: { en: "approximate", ar: "تقريبي" },
  sex: { en: "Sex", ar: "النوع" },
  nationality: { en: "Nationality", ar: "الجنسية" },
  individualNo: { en: "Individual no.", ar: "رقم الفرد" },
  caseNo: { en: "Case no.", ar: "رقم الحالة" },
  cardNo: { en: "Card no.", ar: "رقم البطاقة" },
  identifier: { en: "Identity document", ar: "مستند الهوية" },
  phone: { en: "Phone", ar: "الهاتف" },
  statusLabel: { en: "Beneficiary status", ar: "حالة المستفيد" },
  notDisclosed: { en: "Not disclosed to your role", ar: "غير متاح لدورك" },
  lockedHint: {
    en: "The card number, the identity document and the status are changed elsewhere — each has its own rules and its own record.",
    ar: "رقم البطاقة ومستند الهوية والحالة تُغيَّر من مواضع أخرى — لكلٍّ قواعده وسجله.",
  },
  required: { en: "This field cannot be emptied.", ar: "لا يمكن ترك هذا الحقل فارغًا." },
  futureDate: { en: "A date of birth cannot be in the future.", ar: "لا يمكن أن يكون تاريخ الميلاد في المستقبل." },
} satisfies Record<string, Localized>;

const SEXES = ["Male", "Female", "Other", "Unknown"] as const;

/**
 * The registration record, and the one place it can be corrected.
 *
 * ============================================================================================================
 * WHY THIS IS A TAB AND NOT MORE COLUMNS
 * ============================================================================================================
 * The roster resolves names through a per-page batch that patient-service deliberately keeps to name, status
 * and card number: a list is the highest-volume disclosure the platform makes, and a date of birth in a
 * fifty-row table is fifty disclosures nobody asked for. Here it is ONE person, read through
 * `GET /beneficiaries/{id}`, which projects by role and audits the read — so the fields appear for the
 * operator who opened a record and nowhere else.
 *
 * ============================================================================================================
 * WHAT IS DELIBERATELY NOT EDITABLE
 * ============================================================================================================
 * The card number is uniquely indexed among live rows — moving a card between people is a benefit leak, and a
 * collision is a conflict for a human. The identity document carries the duplicate check the registrar owns.
 * The status has a legal-transition table and its own dialog. All three are SHOWN, with one sentence saying
 * where they are changed, because a field that is simply absent reads as data the system does not hold.
 */
function BeneficiaryPanel({
  person,
  onReload,
  onChanged,
}: {
  /** Fetched ONCE by the parent and shared with the card above — see `loadPerson`. Null while it is in
   *  flight, and also when the read failed, which this panel renders as nothing rather than as an error:
   *  the membership screen around it is intact and the operator can still work. */
  person: BeneficiaryRow | null;
  onReload: () => Promise<void>;
  onChanged: () => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const { toast } = useToast();
  const [editing, setEditing] = useState(false);

  // Undefined means the server withheld the field from this role; empty means it holds nothing. An operator
  // who cannot tell them apart asks the beneficiary to repeat what the system already has.
  const value = (v: string | undefined | null) =>
    v === undefined ? <span className="muted">{t(P.notDisclosed)}</span> : v === null || v === "" ? "—" : v;

  if (!person) return null;

  const identifier = person.identifiers.find((i) => i.isPrimary) ?? person.identifiers[0];
  const phone = person.contacts?.find((c) => c.type === "Phone" && c.isPrimary)
    ?? person.contacts?.find((c) => c.type === "Phone");

  return (
    <Card>
      <div className="pol-panel-head">
        <div>
          <h3>{t(P.title)}</h3>
          <p className="pol-muted">{t(P.intro)}</p>
        </div>
        <Button variant="secondary" onClick={() => setEditing(true)} aria-haspopup="dialog">{t(P.edit)}</Button>
      </div>

      <dl className="reg-kv">
        <div><dt>{t(P.givenName)}</dt><dd>{value(person.givenName)}</dd></div>
        <div><dt>{t(P.middleName)}</dt><dd>{value(person.middleName ?? null)}</dd></div>
        <div><dt>{t(P.familyName)}</dt><dd>{value(person.familyName)}</dd></div>
        <div>
          <dt>{t(P.birthDate)}</dt>
          <dd className="tnum">
            {person.birthDate === undefined ? value(undefined) : fmt.date(person.birthDate)}
            {person.birthDateIsApproximate ? <span className="muted"> ({t(P.approxTag)})</span> : null}
          </dd>
        </div>
        <div><dt>{t(P.sex)}</dt><dd>{value(person.sex)}</dd></div>
        <div><dt>{t(P.nationality)}</dt><dd>{value(person.nationalityCode)}</dd></div>
        <div><dt>{t(P.individualNo)}</dt><dd className="tnum">{value(person.individualNo ?? null)}</dd></div>
        <div><dt>{t(P.caseNo)}</dt><dd className="tnum">{value(person.caseNo ?? null)}</dd></div>
        <div><dt>{t(P.cardNo)}</dt><dd className="tnum">{value(person.cardNumber ?? null)}</dd></div>
        <div>
          <dt>{t(P.identifier)}</dt>
          <dd className="tnum">{identifier ? `${identifier.type}: ${identifier.value}` : value(person.identifiers.length === 0 ? null : undefined)}</dd>
        </div>
        <div><dt>{t(P.phone)}</dt><dd className="tnum">{phone ? phone.value : value(person.contacts === undefined ? undefined : null)}</dd></div>
        <div><dt>{t(P.statusLabel)}</dt><dd><StatusChip kind={person.status.kind} label={t(person.status.label)} /></dd></div>
      </dl>

      <p className="pol-muted pol-lockednote">{t(P.lockedHint)}</p>

      {editing && (
        <EditDetailsModal
          person={person}
          onClose={() => setEditing(false)}
          onSaved={async (changed) => {
            setEditing(false);
            toast(changed.length === 0 ? t(P.noChanges) : t(P.saved).replace("{n}", String(changed.length)), "ok");
            await onReload();
            // The roster carries the name and the card number, so a correction to either has to reach it —
            // otherwise the list and the record it opened disagree until the next search.
            onChanged();
          }}
        />
      )}
    </Card>
  );
}

function EditDetailsModal({
  person,
  onClose,
  onSaved,
}: {
  person: BeneficiaryRow;
  onClose: () => void;
  onSaved: (changed: string[]) => void | Promise<void>;
}) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();
  const [form, setForm] = useState({
    givenName: person.givenName,
    middleName: person.middleName ?? "",
    familyName: person.familyName,
    birthDate: person.birthDate ?? "",
    birthDateIsApproximate: person.birthDateIsApproximate ?? false,
    sex: person.sex ?? "Unknown",
    nationalityCode: person.nationalityCode ?? "",
    individualNo: person.individualNo ?? "",
    caseNo: person.caseNo ?? "",
  });
  const [touched, setTouched] = useState(false);

  const today = new Date().toISOString().slice(0, 10);
  const nameError = (v: string) => (touched && v.trim() === "" ? t(P.required) : undefined);
  const dateError = touched && form.birthDate !== "" && form.birthDate > today ? t(P.futureDate) : undefined;
  const invalid = form.givenName.trim() === "" || form.familyName.trim() === "" || Boolean(dateError);

  const submit = async () => {
    setTouched(true);
    if (invalid) return;
    // Only what MOVED is sent. A form that posts all nine fields makes every save look like nine corrections
    // in the log, which is how a real one becomes impossible to find. The server refuses to record unchanged
    // values too, so this is belt and braces — but the belt is here, where the diff is cheapest to compute.
    const edit: BeneficiaryEdit = {};
    const set = <K extends keyof typeof form>(key: K, current: unknown, next: unknown) => {
      if (next !== current) (edit as Record<string, unknown>)[key as string] = next;
    };
    set("givenName", person.givenName, form.givenName.trim());
    set("middleName", person.middleName ?? "", form.middleName.trim());
    set("familyName", person.familyName, form.familyName.trim());
    set("birthDate", person.birthDate ?? "", form.birthDate);
    set("birthDateIsApproximate", person.birthDateIsApproximate ?? false, form.birthDateIsApproximate);
    set("sex", person.sex ?? "Unknown", form.sex);
    set("nationalityCode", person.nationalityCode ?? "", form.nationalityCode.trim().toUpperCase());
    set("individualNo", person.individualNo ?? "", form.individualNo.trim());
    set("caseNo", person.caseNo ?? "", form.caseNo.trim());
    // An empty birth date means "not recorded", and the contract has no way to say "clear it" — sending "" as
    // a date would be a validation error rather than the erasure somebody meant.
    if (edit.birthDate === "") delete edit.birthDate;

    let changed: string[] = [];
    const ok = await write.run(async () => {
      const r = await api.updateBeneficiary(person.id, edit);
      changed = r.changed;
      return r;
    });
    if (ok) await onSaved(changed);
  };

  return (
    <Modal
      open
      onOpenChange={(o) => !o && !write.busy && onClose()}
      title={`${t(P.edit)} — ${[person.givenName, person.familyName].filter(Boolean).join(" ")}`}
      closeLabel={t(P.cancel)}
      wide
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(P.cancel)}</Button>
          <Button variant="primary" onClick={submit} loading={write.busy} disabled={write.busy}>{t(P.save)}</Button>
        </>
      }
    >
      {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}

      <div className="pol-advanced-grid">
        <InputField label={t(P.givenName)} value={form.givenName} error={nameError(form.givenName)} autoComplete="off"
          onChange={(e) => setForm({ ...form, givenName: e.currentTarget.value })} />
        <InputField label={t(P.middleName)} value={form.middleName} autoComplete="off"
          onChange={(e) => setForm({ ...form, middleName: e.currentTarget.value })} />
        <InputField label={t(P.familyName)} value={form.familyName} error={nameError(form.familyName)} autoComplete="off"
          onChange={(e) => setForm({ ...form, familyName: e.currentTarget.value })} />
        <InputField type="date" label={t(P.birthDate)} value={form.birthDate} error={dateError} max={today}
          onChange={(e) => setForm({ ...form, birthDate: e.currentTarget.value })} />
        <FilterSelect label={t(P.sex)} value={form.sex} onChange={(v) => setForm({ ...form, sex: v })}
          options={SEXES.map((v) => ({ value: v, label: v }))} />
        <InputField label={t(P.nationality)} value={form.nationalityCode} maxLength={2} autoComplete="off"
          onChange={(e) => setForm({ ...form, nationalityCode: e.currentTarget.value })} />
        <InputField label={t(P.individualNo)} value={form.individualNo} autoComplete="off"
          onChange={(e) => setForm({ ...form, individualNo: e.currentTarget.value })} />
        <InputField label={t(P.caseNo)} value={form.caseNo} autoComplete="off"
          onChange={(e) => setForm({ ...form, caseNo: e.currentTarget.value })} />
      </div>

      {/* Travels WITH the date, always. A consumer that gets the date without the flag has no way to know it
          is an estimate, which is precisely how an estimated date becomes a hard eligibility cutoff. */}
      <label className="ben-checkbox">
        <input
          type="checkbox"
          className="mrs-checkbox"
          checked={form.birthDateIsApproximate}
          onChange={(e) => setForm({ ...form, birthDateIsApproximate: e.currentTarget.checked })}
        />
        <span>{t(P.approximate)}</span>
      </label>
    </Modal>
  );
}

// ── Coverage + cost-share grid ──────────────────────────────────────────────────────────────────────────

function CoverageTab({ coverage }: { coverage: MemberCoverageDetail | null }) {
  const t = useLoc();
  const fmt = useFormat();
  const [open, setOpen] = useState<string | null>(null);
  if (!coverage) return null;

  return (
    <Card data-testid="coverage-tab">
      {coverage.planVersionChangedSinceEnrolment && (
        <InlineAlert tone="info" data-testid="version-drift">
          {t(S.versionDrift)} ({t(S.enrolledUnder)} {coverage.enrolledUnderPlanVersionId?.slice(0, 8)})
        </InlineAlert>
      )}
      <div className="pol-tablewrap">
        <table className="pol-grid">
          <caption className="sr-only">{t(S.tabCoverage)}</caption>
          <thead>
            <tr>
              <th scope="col">{t(S.category)}</th>
              <th scope="col">{t(S.covered)}</th>
              <th scope="col">{t(S.limit)}</th>
              <th scope="col">{t(S.consumed)}</th>
              <th scope="col">{t(S.remaining)}</th>
              <th scope="col">{t(S.resets)}</th>
              <th scope="col">{t(S.costShare)}</th>
            </tr>
          </thead>
          <tbody>
            {coverage.categories.map((c) => [
              <tr key={c.benefitCategoryCode}>
                <th scope="row">
                  {c.benefitCategoryCode}
                  {c.limitDiffersFromPlan && <StatusChip kind="info" label="≠" />}
                </th>
                <td>
                  <StatusChip kind={c.isCovered ? "ok" : "neu"} label={c.isCovered ? t(S.covered) : t(S.notCovered)} />
                </td>
                <td>{c.limit != null ? fmt.money(c.limit) : "∞"}</td>
                <td>{fmt.money(c.consumed)}</td>
                <td>{c.remaining != null ? fmt.money(c.remaining) : "∞"}</td>
                <td>{c.resetsOn ? fmt.date(c.resetsOn) : c.resetPeriod}</td>
                <td>
                  <Button
                    variant="ghost"
                    aria-expanded={open === c.benefitCategoryCode}
                    onClick={() => setOpen(open === c.benefitCategoryCode ? null : c.benefitCategoryCode)}
                  >
                    {t(S.costShare)}
                  </Button>
                </td>
              </tr>,
              open === c.benefitCategoryCode ? (
                <tr key={`${c.benefitCategoryCode}-cs`} className="pol-grid-sub">
                  <td colSpan={7}>
                    <CostShareGrid category={c} />
                  </td>
                </tr>
              ) : null,
            ])}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

function CostShareGrid({ category }: { category: CategoryCoverageDetail }) {
  const t = useLoc();
  const fmt = useFormat();
  return (
    <div>
      {category.limitDiffersFromPlan && <InlineAlert tone="info">{t(S.limitDiffers)}</InlineAlert>}
      <div className="pol-tablewrap">
        <table className="pol-costshare">
          <caption>
            {t(S.costShare)} — {category.benefitCategoryCode}
          </caption>
          <thead>
            <tr>
              <th scope="col">{t(S.tier)}</th>
              <th scope="col">{t(S.covered)}</th>
              <th scope="col">{t(S.copay)}</th>
              <th scope="col">{t(S.coinsurance)}</th>
              <th scope="col">{t(S.preauth)}</th>
              <th scope="col">{t(S.limit)}</th>
            </tr>
          </thead>
          <tbody>
            {category.costShareByTier.map((x) => (
              <tr key={x.networkTierId}>
                <th scope="row">{x.tierCode}</th>
                <td>
                  <StatusChip kind={x.isCovered ? "ok" : "neu"} label={x.isCovered ? t(S.covered) : t(S.notCovered)} />
                </td>
                <td>{x.copayFixed != null ? fmt.money(x.copayFixed) : x.copayPercent != null ? `${x.copayPercent}%` : "—"}</td>
                <td>{x.coinsurancePercent != null ? `${x.coinsurancePercent}%` : "—"}</td>
                <td>
                  <StatusChip kind={x.requiresPreauth ? "warn" : "neu"} label={x.requiresPreauth ? t(S.preauth) : "—"} />
                </td>
                <td>{x.limitAtTier != null ? fmt.money(x.limitAtTier) : "∞"}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
      {category.exclusions.length > 0 && (
        <p>
          <strong>{t(S.exclusions)}:</strong> {category.exclusions.join(", ")}
        </p>
      )}
    </div>
  );
}

// ── Member utilization ──────────────────────────────────────────────────────────────────────────────────

function MemberUtilizationTab({ api, beneficiaryId }: { api: PolicyApi; beneficiaryId: string }) {
  const t = useLoc();
  const fmt = useFormat();
  const [view, setView] = useState<MemberUtilizationView | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    api
      .memberUtilization(beneficiaryId)
      .then((v) => live && setView(v))
      .catch((e) => live && setError(writeErrorMessage(e).message));
    return () => { live = false; };
  }, [api, beneficiaryId]);

  const meters = useMemo(
    () =>
      (view?.categories ?? []).map((c) => ({
        label: c.benefitCategory,
        consumed: c.consumed,
        limit: c.unlimited ? null : c.limit,
        valueText: fmt.money(c.consumed),
        limitText: c.unlimited ? "∞" : fmt.money(c.limit),
      })),
    [view, fmt],
  );

  return (
    <Card data-testid="member-utilization">
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {view && (
        <>
          {!view.reconciliation.reconciled && <InlineAlert tone="bad">{t(S.reconcileBad)}</InlineAlert>}
          <LimitMeters caption={t(S.utilizationCaption)} rows={meters} />
          <dl className="pol-kpis">
            <div>
              <dt>{t(S.encounters)}</dt>
              {/* null means "could not ask", never "zero" — an em dash says so. */}
              <dd>{fmt.number(view.external.encounters ?? undefined)}</dd>
            </div>
            <div>
              <dt>{t(S.authorizations)}</dt>
              <dd>{fmt.number(view.external.authorizationsRaised ?? undefined)}</dd>
            </div>
          </dl>
          {view.external.unavailable.length > 0 && (
            <InlineAlert tone="warn">
              {t(S.unavailable)} {view.external.unavailable.join(", ")}
            </InlineAlert>
          )}
        </>
      )}
    </Card>
  );
}

// ── Membership dialogs ──────────────────────────────────────────────────────────────────────────────────

const todayIso = () => new Date().toISOString().slice(0, 10);

function MembershipDialog({
  api,
  kind,
  row,
  onClose,
  onDone,
}: {
  api: PolicyApi;
  kind: Exclude<Dialog, null>;
  row: MemberQueryRow;
  onClose: () => void;
  onDone: (announcement: string) => Promise<void>;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const [effectiveDate, setEffectiveDate] = useState(todayIso());
  const [reason, setReason] = useState("");
  const [groupId, setGroupId] = useState("");
  const [policyPlanId, setPolicyPlanId] = useState("");
  const [groups, setGroups] = useState<MemberGroupView[]>([]);
  const [plans, setPlans] = useState<PolicyPlanView[]>([]);
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  const [applied, setApplied] = useState<PlanChangeView | null>(null);
  const [preview, setPreview] = useState<PlanChangePreviewView | null>(null);
  const [previewError, setPreviewError] = useState<Localized | null>(null);
  const [previewing, setPreviewing] = useState(false);
  const [key, rotateKey] = useIdempotencyKey();

  const reasonMandatory = kind === "terminate" || kind === "changePlan";
  const backdated = effectiveDate < todayIso();

  useEffect(() => {
    if (kind === "changeGroup") api.policyGroups(row.policyId).then(setGroups).catch(() => setGroups([]));
    if (kind === "changePlan") api.policyPlans(row.policyId).then(setPlans).catch(() => setPlans([]));
  }, [api, kind, row.policyId]);

  // The dry run. Re-asked whenever the target plan or the effective date changes, because both are inputs to
  // the answer — a plan not yet in force on the chosen date fails HERE, in a preview, rather than after the
  // officer has written a justification for a change that was never going to be accepted.
  useEffect(() => {
    if (kind !== "changePlan" || !policyPlanId) {
      setPreview(null);
      setPreviewError(null);
      return;
    }
    let live = true;
    setPreviewing(true);
    setPreviewError(null);
    api
      .previewPlanChange(row.enrollmentId, policyPlanId, effectiveDate)
      .then((p) => {
        if (!live) return;
        setPreview(p);
      })
      .catch((e: unknown) => {
        if (!live) return;
        // A failed preview clears the previous one. Leaving the last successful answer on screen beside a newly
        // chosen plan is how somebody confirms a change against arithmetic for a different plan.
        setPreview(null);
        setPreviewError(writeErrorMessage(e).message);
      })
      .finally(() => {
        if (live) setPreviewing(false);
      });
    return () => {
      live = false;
    };
  }, [api, kind, policyPlanId, effectiveDate, row.enrollmentId]);

  const title = { terminate: S.terminate, reinstate: S.reinstate, changeGroup: S.changeGroup, changePlan: S.changePlan }[kind];

  async function submit() {
    if (reasonMandatory && !reason.trim()) {
      setError(S.reasonRequired);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      switch (kind) {
        case "terminate":
          await api.terminate(row.enrollmentId, effectiveDate, reason.trim(), key);
          break;
        case "reinstate":
          await api.reinstate(row.enrollmentId, effectiveDate, reason.trim() || null, key);
          break;
        case "changeGroup":
          await api.changeGroup(row.enrollmentId, groupId || null, effectiveDate, reason.trim() || null, key);
          break;
        case "changePlan": {
          const result = await api.changePlan(row.enrollmentId, policyPlanId, effectiveDate, reason.trim(), key);
          // The authoritative arithmetic comes back from the server. Shown before the dialog closes, because
          // "what is this member's ceiling now" is the question the change was made to answer.
          setApplied(result);
          rotateKey();
          setBusy(false);
          return;
        }
      }
      rotateKey();
      await onDone(t(S.done));
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    /*
     * A REAL modal, not a card that claims to be one.
     *
     * This used to be `<Card role="dialog" aria-modal="true">` rendered inline under the member's identity
     * block. `aria-modal="true"` is an assertion the markup could not keep: nothing trapped focus, Escape did
     * nothing, the page behind stayed in the tab order, and a screen reader was told the rest of the page was
     * inert while it was still fully reachable. It also scrolled away — an officer who had scrolled down to
     * the coverage grid pressed Terminate and the form opened above their viewport.
     *
     * `Modal` is the design system's Radix dialog: focus trap, Escape, scrim, restore-focus-on-close, and a
     * labelled close control. `wide` because the plan-change dry run is a five-column table (0B §10c).
     */
    <Modal
      open
      onOpenChange={(o) => !o && !busy && onClose()}
      title={t(title)}
      closeLabel={t(S.cancel)}
      wide
      footer={
        <div className="pol-dialog-actions">
          {!applied && (
            // A plan change cannot be confirmed until the dry run has answered. Not defensiveness about the
            // network: the preview runs the same resolution the change does, so a preview that failed is a
            // change that would have failed — and the point of the dialog is that nobody moves a member's
            // entitlement without having been shown what it does to them.
            <Button
              variant={kind === "terminate" ? "danger" : "primary"}
              onClick={submit}
              loading={busy}
              disabled={busy || (kind === "changePlan" && !preview)}
            >
              {t(S.confirm)}
            </Button>
          )}
          <Button variant="ghost" onClick={() => (applied ? void onDone(t(S.done)) : onClose())}>
            {applied ? t(S.confirm) : t(S.cancel)}
          </Button>
        </div>
      }
    >
      <div className="pol-dialog" data-testid={`dialog-${kind}`}>
      <InputField
        type="date"
        label={t(S.effectiveDate)}
        value={effectiveDate}
        onChange={(e) => setEffectiveDate(e.target.value)}
      />
      {/* Stated up front rather than discovered as a 403. The server decides; this only removes the surprise. */}
      {backdated && <InlineAlert tone="warn">{t(S.backdated)}</InlineAlert>}

      {/*
        * The design system's Select, not a native one. A bare <select> is drawn by the OS: it sat a few pixels
        * shorter than the date field directly above it, kept square corners against the app's radius, and
        * opened a system-blue list. In a modal whose other two controls are Mersal fields, that does not read
        * as plain — it reads as a control somebody forgot to finish.
        */}
      {kind === "changeGroup" && (
        <SelectField
          id="dlg-group"
          label={t(S.group)}
          value={groupId === "" ? null : groupId}
          onChange={setGroupId}
          // "No group" is a real choice here, not an absence — a member can legitimately belong to none.
          placeholder={t(S.none)}
          options={[
            { value: "", label: t(S.none) },
            ...groups.map((g) => ({ value: g.groupId, label: `${g.groupCode} — ${g.nameEn}` })),
          ]}
        />
      )}

      {kind === "changePlan" && (
        <>
          <SelectField
            id="dlg-plan"
            label={t(S.targetPlan)}
            value={policyPlanId === "" ? null : policyPlanId}
            onChange={setPolicyPlanId}
            placeholder="—"
            options={plans.map((p) => ({ value: p.policyPlanId, label: p.planLabel }))}
          />

          {/* The carry-forward preview — the server's own dry run, not an estimate assembled here. Same
              resolution and same arithmetic as the change itself, so what this shows is what will happen. */}
          {!policyPlanId && <p className="pol-muted">{t(S.selectPlanFirst)}</p>}
          {previewing && <p className="pol-muted" aria-live="polite">{t(S.previewing)}</p>}
          {previewError && <InlineAlert tone="bad" data-testid="preview-error">{t(previewError)}</InlineAlert>}

          {preview && (
            <div data-testid="carry-preview">
              <h4>{t(S.carryPreview)}</h4>
              <InlineAlert tone="info">
                {t(preview.consumptionPolicy === "CarryForward" ? S.carryHint : S.resetHint)}
              </InlineAlert>
              <div className="pol-tablewrap">
                <table className="pol-costshare">
                  <caption className="sr-only">{t(S.carryPreview)}</caption>
                  <thead>
                    <tr>
                      <th scope="col">{t(S.category)}</th>
                      <th scope="col">{t(S.currentLimit)}</th>
                      <th scope="col">{t(S.consumed)}</th>
                      <th scope="col">{t(S.newLimit)}</th>
                      <th scope="col">{t(S.remaining)}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {preview.rows.map((r) => (
                      <tr key={r.benefitCategoryId}>
                        <th scope="row">{r.benefitCategoryCode ?? r.benefitCategoryId.slice(0, 8)}</th>
                        {/* "Not covered today" and "unbounded today" are different facts and a dash cannot say
                            both — `held` is what separates them. */}
                        <td>{!r.held ? t(S.notHeldToday) : r.currentLimitValue != null ? fmt.money(r.currentLimitValue) : "∞"}</td>
                        <td>{fmt.money(r.consumedValue)}</td>
                        <td>{r.newLimitValue != null ? fmt.money(r.newLimitValue) : "∞"}</td>
                        <td>
                          {r.remaining != null ? fmt.money(r.remaining) : "∞"}
                          {r.exhausted && <StatusChip kind="bad" label={t(S.exhausted)} />}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>

              {/* The half no client-side estimate could have recovered: a benefit the new plan does not cover
                  produces no row in the outcome at all, so without this it simply disappears. */}
              {preview.droppedCategories.length > 0 && (
                <div data-testid="carry-dropped">
                  <h4>{t(S.dropped)}</h4>
                  <InlineAlert tone="warn">{t(S.droppedHint)}</InlineAlert>
                  <div className="pol-tablewrap">
                    <table className="pol-costshare">
                      <caption className="sr-only">{t(S.dropped)}</caption>
                      <thead>
                        <tr>
                          <th scope="col">{t(S.category)}</th>
                          <th scope="col">{t(S.currentLimit)}</th>
                          <th scope="col">{t(S.consumed)}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {preview.droppedCategories.map((d) => (
                          <tr key={d.benefitCategoryId}>
                            <th scope="row">{d.benefitCategoryCode ?? d.benefitCategoryId.slice(0, 8)}</th>
                            <td>{d.currentLimitValue != null ? fmt.money(d.currentLimitValue) : "∞"}</td>
                            <td>{fmt.money(d.consumedValue)}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </div>
              )}
            </div>
          )}
        </>
      )}

      <TextareaField
        label={reasonMandatory ? t(S.reason) : t(S.reasonOptional)}
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        rows={2}
        required={reasonMandatory}
      />

      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {applied && (
        <div data-testid="carry-result" aria-live="polite">
          <h4>{t(S.carryResult)}</h4>
          <div className="pol-tablewrap">
            <table className="pol-costshare">
              <caption className="sr-only">{t(S.carryResult)}</caption>
              <thead>
                <tr>
                  <th scope="col">{t(S.category)}</th>
                  <th scope="col">{t(S.limit)}</th>
                  <th scope="col">{t(S.consumed)}</th>
                  <th scope="col">{t(S.remaining)}</th>
                </tr>
              </thead>
              <tbody>
                {applied.carriedLimits.map((c) => (
                  <tr key={c.benefitCategoryId}>
                    <th scope="row">{c.benefitCategoryCode ?? c.benefitCategoryId.slice(0, 8)}</th>
                    <td>{c.limitValue != null ? fmt.money(c.limitValue) : "∞"}</td>
                    <td>{fmt.money(c.consumedValue)}</td>
                    <td>
                      {c.remaining != null ? fmt.money(c.remaining) : "∞"}
                      {c.exhausted && <StatusChip kind="bad" label={t(S.exhausted)} />}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      </div>
    </Modal>
  );
}

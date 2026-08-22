import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Button, Card, ComboboxField, DataTable, DataTableView, Icon, InlineAlert, InputField, KpiList, Modal,
  Pagination, StatusChip, Tabs, TextareaField, useTableQuery,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type {
  PayerView,
  PolicyAdminView,
  PolicyBook,
  PolicyDetail,
  MemberGroupView,
  PolicyApi,
  PolicyPlanView,
  PolicyQueryRow,
  QueryPage,
  ScopeUtilizationView,
} from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";

/** ONE client for the module, not one per render: a default parameter re-evaluates on every call,
 *  and screens key their load effects on the api instance — a fresh instance per render turned the
 *  first failing (or even succeeding) fetch into an unbounded request loop (QA P0-1: ~400 req/s).*/
const httpPolicyApi = createHttpPolicyApi();
import { writeErrorMessage } from "../api/writeError";
import { PageHeader, fillLocalized, useLoc, readErrorMessage } from "./_shared";
import { useAuth } from "../auth/AuthProvider";
import { mayAdministerMembership } from "../authz/permissions";
import { Fact, HistoryModal, ReasonDialog, RecordActions } from "./AdminRecordControls";
import { useIdempotencyKey } from "./PolicyPanels";
import { ChangeTimeline, DocumentsPanel, LimitMeters, NotesPanel } from "./PolicyPanels";
import { useFormat } from "../i18n/useFormat";
import { useEnumLabel } from "../i18n/enumLabels";
import { useTheme } from "@mersal/design-system";

/**
 * Phase 19.6 — the policy book: which contracts exist, what plans sit under them, who is grouped how, and
 * what the whole thing has consumed (design 38 §4.2 / §4.5).
 *
 * The PLANS TAB is the part that earns its place. A policy with three plans behaves nothing like a policy
 * with one — members are on different ceilings, the default decides where a new enrolment lands, and the
 * per-plan member count is the first number anyone asks for when a policy looks over-consumed. Presenting
 * plans as a tab rather than a nested drill-down keeps that comparison on one screen.
 */

/** The three the server accepts on `status` (PolicyStatus). */
const POLICY_STATUSES = ["Active", "Suspended", "Expired"] as const;

const S = {
  title: { en: "Policies", ar: "الوثائق" },
  groupsTitle: { en: "Groups", ar: "المجموعات" },
  policyNo: { en: "Policy no.", ar: "رقم الوثيقة" },
  status: { en: "Status", ar: "الحالة" },
  window: { en: "In force", ar: "سارية" },
  members: { en: "Members", ar: "الأعضاء" },
  plans: { en: "Plans", ar: "الخطط" },
  used: { en: "% used", ar: "٪ مستخدم" },
  noPolicies: { en: "No policies match these filters.", ar: "لا توجد وثائق مطابقة." },
  findPolicy: { en: "Policy number", ar: "رقم الوثيقة" },
  findPolicyHint: { en: "Any part of it — the server matches on a fragment.", ar: "أي جزء منه — يطابق الخادم جزءًا من الرقم." },
  anyStatus: { en: "Any status", ar: "أي حالة" },
  anyPayer: { en: "Any payer", ar: "أي جهة ممولة" },
  payer: { en: "Payer", ar: "الجهة الممولة" },
  search: { en: "Search", ar: "بحث" },
  clearFilters: { en: "Clear", ar: "مسح" },
  // ── 19.8: the contract as an administrable record ──────────────────────────────────────────────────────
  newPolicy: { en: "New policy", ar: "وثيقة جديدة" },
  editPolicy: { en: "Edit this policy", ar: "تعديل هذه الوثيقة" },
  suspendPolicy: { en: "Suspend this policy", ar: "تعليق هذه الوثيقة" },
  resumePolicy: { en: "Resume this policy", ar: "استئناف هذه الوثيقة" },
  expirePolicy: { en: "End this policy", ar: "إنهاء هذه الوثيقة" },
  policyHistory: { en: "Change history", ar: "سجل التغييرات" },
  policyNoLabel: { en: "Policy number", ar: "رقم الوثيقة" },
  policyNoLocked: {
    en: "The number can never be changed. Claims, extracts and the payer's own systems key on it. To replace one, issue the right policy and move its members deliberately.",
    ar: "لا يمكن تغيير الرقم أبدًا. فالمطالبات والمستخرجات وأنظمة الجهة الممولة ترتبط به. لاستبداله، أصدر الوثيقة الصحيحة وانقل أعضاءها عمدًا.",
  },
  from: { en: "In force from", ar: "سارية من" },
  until: { en: "Until (inclusive)", ar: "حتى (شامل)" },
  untilHint: {
    en: "The last day covered. Leave it empty for open-ended.",
    ar: "آخر يوم مغطى. اتركه فارغًا لغير محدد.",
  },
  cap: { en: "Member cap", ar: "حد الأعضاء" },
  capHint: {
    en: "Leave it empty for uncapped. It cannot be set below the members already active.",
    ar: "اتركه فارغًا لغير محدود. لا يمكن ضبطه أقل من الأعضاء النشطين بالفعل.",
  },
  policyNotes: { en: "Notes", ar: "ملاحظات" },
  needPolicyNo: { en: "A policy number is required.", ar: "رقم الوثيقة مطلوب." },
  needPayer: { en: "Choose the payer this contract is with.", ar: "اختر الجهة الممولة لهذا العقد." },
  needForwardWindow: { en: "The policy must end on or after it starts.", ar: "يجب أن تنتهي الوثيقة في أو بعد تاريخ بدايتها." },
  needPositiveCap: {
    en: "A cap of zero is not 'uncapped', it is 'closed to enrolment'. Leave it empty instead.",
    ar: "حد بقيمة صفر ليس «غير محدود» بل «مغلق للتسجيل». اتركه فارغًا بدلًا من ذلك.",
  },
  policyCreated: { en: "Policy issued.", ar: "تم إصدار الوثيقة." },
  policyUpdated: { en: "Policy updated.", ar: "تم تحديث الوثيقة." },
  policySuspended: { en: "Policy suspended.", ar: "تم تعليق الوثيقة." },
  policyResumed: { en: "Policy resumed.", ar: "تم استئناف الوثيقة." },
  policyExpired: { en: "Policy ended.", ar: "تم إنهاء الوثيقة." },
  formCreatePolicy: { en: "New policy", ar: "وثيقة جديدة" },
  formEditPolicy: { en: "Edit policy", ar: "تعديل الوثيقة" },
  save: { en: "Save", ar: "حفظ" },
  createPolicy: { en: "Issue policy", ar: "إصدار الوثيقة" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  // ── window state ───────────────────────────────────────────────────────────────────────────────────────
  windowNotYetStarted: { en: "Starts later", ar: "تبدأ لاحقًا" },
  windowInForce: { en: "In force", ar: "سارية" },
  windowEnded: { en: "Window closed", ar: "انتهت المدة" },
  activeButEnded: {
    en: "This policy is Active and its own effective window has closed. Renew it, or end it once its members are moved.",
    ar: "هذه الوثيقة نشطة وقد انتهت مدة سريانها. جدّدها أو أنهِها بعد نقل أعضائها.",
  },
  // ── the book ───────────────────────────────────────────────────────────────────────────────────────────
  membersTotal: { en: "Members", ar: "الأعضاء" },
  activeMembers: { en: "Active members", ar: "الأعضاء النشطون" },
  plansOn: { en: "Plans", ar: "الخطط" },
  committed: { en: "Committed", ar: "الملتزم به" },
  restricted: { en: "Restricted for your role", ar: "مقيّد حسب دورك" },
  ofCap: { en: "{0}% of the member cap", ar: "{0}٪ من حد الأعضاء" },
  overCap: { en: "Active members exceed this policy's cap.", ar: "عدد الأعضاء النشطين يتجاوز حد الوثيقة." },
  // ── status moves ───────────────────────────────────────────────────────────────────────────────────────
  suspendTitle: { en: "Suspend {0}?", ar: "تعليق {0}؟" },
  suspendBody: {
    en: "Cover under {0} stops being honoured until it is resumed. Nothing is terminated and no member is removed.",
    ar: "تتوقف التغطية بموجب {0} حتى يتم استئنافها. لا يُنهى أي تسجيل ولا يُزال أي عضو.",
  },
  resumeTitle: { en: "Resume {0}?", ar: "استئناف {0}؟" },
  resumeBody: { en: "Cover under {0} is honoured again from now.", ar: "تُعتمد التغطية بموجب {0} مجددًا من الآن." },
  expireTitle: { en: "End {0}?", ar: "إنهاء {0}؟" },
  expireBody: {
    en: "{0} stops covering anybody. An ended policy is not resumed — reopening cover means issuing a renewal, which is a new contract linked to this one.",
    ar: "تتوقف {0} عن تغطية أي شخص. الوثيقة المنتهية لا تُستأنف — فإعادة فتح التغطية تعني إصدار تجديد، وهو عقد جديد مرتبط بهذا.",
  },
  reversible: { en: "It can be resumed at any time.", ar: "يمكن استئنافها في أي وقت." },
  notReversible: { en: "This cannot be undone — the way back is a renewal.", ar: "لا يمكن التراجع عن هذا — والسبيل للعودة هو التجديد." },
  impact: {
    en: "{0} members are active on this policy right now.",
    ar: "{0} عضوًا نشطًا على هذه الوثيقة الآن.",
  },
  policyHistoryTitle: { en: "Change history — {0}", ar: "سجل التغييرات — {0}" },
  lastChanged: { en: "Last changed", ar: "آخر تعديل" },
  by: { en: "by {0}", ar: "بواسطة {0}" },
  select: { en: "Select a policy to see its plans, groups, utilization and notes.", ar: "اختر وثيقة لعرض خططها ومجموعاتها واستخدامها وملاحظاتها." },
  tabPlans: { en: "Plans", ar: "الخطط" },
  tabGroups: { en: "Groups", ar: "المجموعات" },
  tabUtilization: { en: "Utilization", ar: "الاستخدام" },
  tabNotes: { en: "Notes", ar: "الملاحظات" },
  tabDocuments: { en: "Documents", ar: "المستندات" },
  tabTimeline: { en: "Timeline", ar: "السجل" },
  planLabel: { en: "Label", ar: "التسمية" },
  planVersion: { en: "Version", ar: "الإصدار" },
  default: { en: "Default", ar: "الافتراضية" },
  noPlansUnder: { en: "No plans attached to this policy.", ar: "لا توجد خطط مرتبطة بهذه الوثيقة." },
  noGroups: { en: "No groups defined.", ar: "لا توجد مجموعات." },
  searchGroups: { en: "Search groups", ar: "بحث في المجموعات" },
  searchGroupsHint: { en: "Code or name", ar: "الرمز أو الاسم" },
  noGroupMatches: { en: "No group matches your search.", ar: "لا توجد مجموعة مطابقة لبحثك." },
  groupTypeFilter: { en: "Type", ar: "النوع" },
  groupCode: { en: "Code", ar: "الرمز" },
  groupName: { en: "Name", ar: "الاسم" },
  groupType: { en: "Type", ar: "النوع" },
  utilizationCaption: { en: "Consumption against limit, by member", ar: "الاستهلاك مقابل الحد، لكل عضو" },
  totalLimit: { en: "Total limit", ar: "إجمالي الحد" },
  totalConsumed: { en: "Consumed", ar: "المستهلك" },
  remaining: { en: "Remaining", ar: "المتبقي" },
  outliers: { en: "Members over threshold", ar: "الأعضاء فوق الحد" },
  reconcileBad: {
    en: "The accumulator and the reported total disagree. Treat these figures as provisional and raise it.",
    ar: "لا يتطابق المُراكِم مع الإجمالي المُبلَّغ. اعتبر هذه الأرقام مبدئية وأبلغ عنها.",
  },
  payerScoped: {
    en: "Narrowed to the payers you are assigned to — this is not the whole book.",
    ar: "تم التضييق على الجهات الممولة المسندة إليك — هذه ليست كل الوثائق.",
  },
  truncated: {
    en: "More policies matched than could be resolved — this page is a subset, narrow the search.",
    ar: "تطابق عدد من الوثائق أكبر مما يمكن حصره — هذه الصفحة جزء من النتائج، ضيّق البحث.",
  },
  unavailable: { en: "Some figures could not be composed:", ar: "تعذّر تجميع بعض الأرقام:" },
  memberNo: { en: "Member no.", ar: "رقم العضو" },
  export: { en: "Export (audited)", ar: "تصدير (مُدقَّق)" },
  exported: { en: "Export downloaded. The request was audited with its row count.", ar: "تم تنزيل التصدير. سُجّل الطلب مع عدد الصفوف." },
} satisfies Record<string, Localized>;

function bandKind(band: string): "ok" | "warn" | "bad" | "neu" {
  if (band === "Over" || band === "Exhausted") return "bad";
  if (band === "High" || band === "Approaching") return "warn";
  if (band === "None" || band === "Unknown") return "neu";
  return "ok";
}

export function PolicyList({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const enumLabel = useEnumLabel();
  const fmt = useFormat();
  const { lang } = useTheme();
  /*
    ============================================================================================================
    THE WHOLE PAGE, NOT THE FIRST 50 OF IT
    ============================================================================================================
    This screen asked for `pageSize: 50`, rendered `items`, and dropped `totalCount`, `totalPages` and
    `identityMatchTruncated` on the floor — so policy 51 was unreachable and nothing on screen said so. An
    operator searching for a policy that sorts 51st was told, in effect, that it does not exist.

    `MemberAdmin` consumes the identical `QueryPage` envelope off the identical query surface and already does
    all three things; this is the same wiring, not new capability. As there, SEARCH, SORT AND PAGING ARE THE
    SERVER'S: `GET /policy-query` accepts `page`, `pageSize` and `sort`, and the book is too big to filter in a
    browser. The table is therefore driven in CONTROLLED sort mode — its own sort would order the 25 rows it was
    handed and leave the true first policy several pages away.
  */
  const [page, setPage] = useState<QueryPage<PolicyQueryRow> | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [selected, setSelected] = useState<PolicyQueryRow | null>(null);
  const [tab, setTab] = useState("plans");
  const [pageNo, setPageNo] = useState(1);
  const [pageSize, setPageSize] = useState(25);
  // The server's own default, so the first render's order matches `sortedBy` rather than claiming an order the
  // response was not sorted in (PolicySortFields.Default).
  const [sort, setSort] = useState<{ field: string; dir: "ascending" | "descending" }>(
    { field: "policyno", dir: "ascending" });

  /*
    THE REGISTER HAD NO WAY IN.

    This screen sent `page`, `pageSize` and `sort` and nothing else, while `GET /policy-query` accepts eleven
    filters — payer, plan, plan label, status, policy number, three dates, group, and two bands. So finding one
    policy meant paging through the book. `MemberAdmin` consumes the identical envelope off the identical query
    surface and has had a criteria bar since 19.5b; this is that wiring, not new capability.

    Three of the eleven, deliberately. Policy number is what somebody has in hand; status and payer are what
    they narrow by. The other eight are report parameters, and putting all eleven in a permanent bar is how the
    analytics screen ended up with thirteen controls above its content.

    `applied` is separate from what is typed: a filter takes effect on Search, not on every keystroke, because
    each change is a server round trip over the whole book.
  */
  const [payers, setPayers] = useState<PayerView[]>([]);
  // 19.8 — the contract's own record, loaded on selection, beside the tabs that describe what hangs off it.
  const [detail, setDetail] = useState<PolicyDetail | null>(null);
  const [form, setForm] = useState<{ mode: "create" } | { mode: "edit"; policy: PolicyAdminView } | null>(null);
  const [move, setMove] = useState<"suspend" | "resume" | "expire" | null>(null);
  const [historyOpen, setHistoryOpen] = useState(false);
  const [announce, setAnnounce] = useState("");
  const { session } = useAuth();
  const mayWrite = mayAdministerMembership(session?.role ?? undefined);
  const [draft, setDraft] = useState<{ policyNo: string; status: string; payerId: string }>(
    { policyNo: "", status: "", payerId: "" });
  const [applied, setApplied] = useState<{ policyNo: string; status: string; payerId: string }>(
    { policyNo: "", status: "", payerId: "" });

  // Reference data for the payer picker. A failure here leaves the picker empty rather than the screen
  // broken: the register still lists, and it still filters by number and status.
  useEffect(() => {
    let live = true;
    api.payers().then((r) => live && setPayers(r)).catch(() => undefined);
    return () => { live = false; };
  }, [api]);

  const run = useCallback(
    async (
      p: number, size: number, sortBy: { field: string; dir: "ascending" | "descending" },
      f: { policyNo: string; status: string; payerId: string },
    ) => {
      setError(null);
      try {
        setPage(await api.policyQuery({
          page: p,
          pageSize: size,
          // The server's vocabulary: a leading "-" means descending (SortRequest.TryParse).
          sort: (sortBy.dir === "descending" ? "-" : "") + sortBy.field,
          // `q()` drops empty values, so an unset filter is absent from the query string rather than sent
          // as "" — which the server would read as a filter matching nothing.
          policyNo: f.policyNo.trim() || undefined,
          status: f.status || undefined,
          payerId: f.payerId || undefined,
        }));
      } catch (e) {
        setError(readErrorMessage(e));
      }
    },
    [api],
  );

  useEffect(() => {
    if (!selected) { setDetail(null); return; }
    let live = true;
    setDetail(null);
    api.policy(selected.policyId)
      .then((d) => { if (live) setDetail(d); })
      .catch((e) => { if (live) setError(readErrorMessage(e)); });
    return () => { live = false; };
  }, [api, selected]);

  const reloadPolicy = useCallback(async (id: string) => {
    try {
      setDetail(await api.policy(id));
      await run(pageNo, pageSize, sort, applied);
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }, [api, run, pageNo, pageSize, sort, applied]);

  // ONE effect drives every fetch, keyed on the whole query — a per-control handler that also fetched would
  // race, and the older response can land last.
  useEffect(() => {
    void run(pageNo, pageSize, sort, applied);
  }, [run, pageNo, pageSize, sort, applied]);

  const narrowed = Boolean(applied.policyNo || applied.status || applied.payerId);

  /* Narrowing has to reset to page 1 and drop the selection: the operator is very likely no longer looking
     at a page the selected policy is on, and leaving the detail open under a table it left is the fault the
     sort handler below already guards against. */
  const applyFilters = () => { setApplied(draft); setPageNo(1); setSelected(null); };
  const clearFilters = () => {
    const empty = { policyNo: "", status: "", payerId: "" };
    setDraft(empty); setApplied(empty); setPageNo(1); setSelected(null);
  };

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.title)} />
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {mayWrite && (
        <div className="screen-toolbar">
          <span />
          <Button variant="primary" leadingIcon={<Icon name="plus" />} onClick={() => setForm({ mode: "create" })}>
            {t(S.newPolicy)}
          </Button>
        </div>
      )}

      <Card>
        <div className="pol-searchbar">
          <InputField
            label={t(S.findPolicy)}
            help={t(S.findPolicyHint)}
            value={draft.policyNo}
            onChange={(e) => setDraft({ ...draft, policyNo: e.currentTarget.value })}
            onKeyDown={(e) => { if (e.key === "Enter") applyFilters(); }}
          />
          <ComboboxField
            label={t(S.status)}
            style={{ maxWidth: "var(--field-max)" }}
            value={draft.status || null}
            placeholder={t(S.anyStatus)}
            onChange={(v) => setDraft({ ...draft, status: v ?? "" })}
            options={POLICY_STATUSES.map((v) => ({ value: v, label: enumLabel(v) }))}
          />
          <ComboboxField
            label={t(S.payer)}
            style={{ maxWidth: "var(--field-max)" }}
            value={draft.payerId || null}
            placeholder={t(S.anyPayer)}
            onChange={(v) => setDraft({ ...draft, payerId: v ?? "" })}
            options={payers.map((p) => ({ value: p.payerId, label: lang === "ar" ? p.nameAr : p.nameEn }))}
          />
          <Button variant="primary" leadingIcon={<Icon name="search" />} onClick={applyFilters}>
            {t(S.search)}
          </Button>
          {narrowed && (
            <Button variant="ghost" onClick={clearFilters}>{t(S.clearFilters)}</Button>
          )}
        </div>
      </Card>

      {/* A payer-scoped user must not read "12 policies" as "the organisation has 12 policies". */}
      {page?.payerScopeApplied && <InlineAlert tone="info">{t(S.payerScoped)}</InlineAlert>}
      {page && page.unavailable.length > 0 && (
        <InlineAlert tone="warn">
          {t(S.unavailable)} {page.unavailable.join(", ")}
        </InlineAlert>
      )}
      {/* A truncated identity match makes the page a SUBSET. Saying so is the difference between a search and
          a wrong answer — the same disclosure the member roster already makes. */}
      {page?.identityMatchTruncated && <InlineAlert tone="warn">{t(S.truncated)}</InlineAlert>}

      <Card>
        <DataTable
          caption={t(S.title)}
          rows={page?.items ?? []}
          rowKey={(r) => r.policyId}
          interactive
          selectedKey={selected?.policyId ?? null}
          onSelect={(r) => setSelected(r)}
          loading={page === null && !error}
          emptyLabel={t(S.noPolicies)}
          sortKey={sort.field}
          sortDir={sort.dir}
          onSort={(key) => {
            setSort((prev) => (prev.field === key
              ? { field: key, dir: prev.dir === "ascending" ? "descending" : "ascending" }
              : { field: key, dir: "ascending" }));
            // A new order makes the current page meaningless, and the selected row is very likely no longer on
            // it — leaving the detail panel open under a table it no longer belongs to.
            setPageNo(1);
            setSelected(null);
          }}
          /*
            Each key IS the server's sort field (PolicySortFields.Allowed), because that is what `onSort` hands
            back. Only the six the server accepts are marked sortable: a header that promises an order the
            server rejects answers with a 400 and an UNKNOWN_SORT_FIELD problem.
          */
          columns={[
            { key: "policyno", header: t(S.policyNo), cell: (r) => r.policyNo, sortable: true },
            {
              key: "status",
              header: t(S.status),
              cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={enumLabel(r.status)} />,
              sortable: true,
            },
            {
              // Sorts by the START of the window — the end date is not the question anyone asks of a policy
              // list, and `effectiveto` is nullable, so an open-ended policy has nothing to order by.
              key: "effectivefrom",
              header: t(S.window),
              cell: (r) => `${fmt.date(r.effectiveFrom)} → ${r.effectiveTo ? fmt.date(r.effectiveTo) : "—"}`,
              sortable: true,
            },
            { key: "membercount", header: t(S.members), cell: (r) => fmt.number(r.memberCount), numeric: true, sortable: true },
            // NOT sortable: the server offers no `plancount`, and offering it here would 400.
            { key: "plans", header: t(S.plans), cell: (r) => fmt.number(r.planCount), numeric: true },
            {
              key: "percentused",
              header: t(S.used),
              cell: (r) => (
                <StatusChip
                  kind={bandKind(r.utilizationBand)}
                  label={r.percentUsed != null ? `${Math.round(r.percentUsed)}% · ${r.utilizationBand}` : r.utilizationBand}
                />
              ),
              sortable: true,
            },
          ]}
        />
        {/* Shown always, for the reason the membership book gives: "Showing 1–25 of 25" answers "how big is
            this book?", which is a question operators ask of a register even when it fits on one page. */}
        {page && (
          <Pagination
            page={pageNo}
            pageSize={pageSize}
            total={page.totalCount}
            onPageChange={(p) => { setPageNo(p); setSelected(null); }}
            onPageSizeChange={(n) => { setPageSize(n); setPageNo(1); setSelected(null); }}
            pageSizeOptions={[10, 25, 50, 100]}
          />
        )}
      </Card>

      {!selected && <InlineAlert tone="info">{t(S.select)}</InlineAlert>}

      {selected && (
        <PolicyDetailPane
          policy={detail?.policy ?? null}
          book={detail?.book ?? null}
          policyNo={selected.policyNo}
          mayWrite={mayWrite}
          onEdit={() => detail && setForm({ mode: "edit", policy: detail.policy })}
          onMove={setMove}
          onHistory={() => setHistoryOpen(true)}
        />
      )}

      {selected && (
        // `Tabs` force-mounts every panel (so a hidden pane is still in the DOM for assistive tech), which
        // means the CONTENT must gate its own fetching — otherwise selecting a policy would fire six
        // requests, including a PHI-adjacent notes read the operator never asked for.
        <Tabs
          aria-label={t(S.title)}
          value={tab}
          onValueChange={setTab}
          items={[
            { value: "plans", label: t(S.tabPlans), content: tab === "plans" ? <PolicyPlansTab api={api} policyId={selected.policyId} /> : null },
            { value: "groups", label: t(S.tabGroups), content: tab === "groups" ? <PolicyGroupsTab api={api} policyId={selected.policyId} /> : null },
            { value: "utilization", label: t(S.tabUtilization), content: tab === "utilization" ? <ScopeUtilizationPanel api={api} scope="policies" id={selected.policyId} /> : null },
            { value: "notes", label: t(S.tabNotes), content: tab === "notes" ? <NotesPanel api={api} scope="policies" scopeRef={selected.policyId} /> : null },
            { value: "documents", label: t(S.tabDocuments), content: tab === "documents" ? <DocumentsPanel api={api} scope="policies" scopeRef={selected.policyId} /> : null },
            { value: "timeline", label: t(S.tabTimeline), content: tab === "timeline" ? <ChangeTimeline api={api} scope="policies" scopeRef={selected.policyId} lang={lang} /> : null },
          ]}
        />
      )}

      {form && (
        <PolicyForm
          api={api}
          mode={form.mode}
          policy={form.mode === "edit" ? form.policy : null}
          policyNo={form.mode === "edit" ? selected?.policyNo ?? "" : ""}
          payers={payers}
          onClose={() => setForm(null)}
          onSaved={async (id, wasCreate) => {
            setForm(null);
            setAnnounce(t(wasCreate ? S.policyCreated : S.policyUpdated));
            if (wasCreate) await run(1, pageSize, sort, applied);
            else await reloadPolicy(id);
          }}
        />
      )}

      {selected && move && (
        <ReasonDialog
          title={fillLocalized(MOVE_TITLE[move], selected.policyNo)}
          body={fillLocalized(MOVE_BODY[move], selected.policyNo)}
          /* Suspending and resuming are reversible; ending is not, and the line has to say which — a dialog
             that overstates on the reversible cases is one nobody reads on the irreversible one. */
          description={move === "expire" ? S.notReversible : S.reversible}
          confirmLabel={MOVE_CONFIRM[move]}
          onConfirm={async (reason, key) => { await api.changePolicyStatus(selected.policyId, move, reason, key); }}
          onClose={() => setMove(null)}
          onDone={async () => {
            setMove(null);
            setAnnounce(t(MOVE_DONE[move]));
            await reloadPolicy(selected.policyId);
          }}
        >
          {/* The blast radius, stated BEFORE the button rather than discovered after it. Suspending is not
              refused for having members — it is the operation — so the count is context, not a barrier. */}
          {detail && detail.book.activeMemberCount > 0 && (
            <InlineAlert tone={move === "expire" ? "warn" : "info"}>
              {t(fillLocalized(S.impact, String(detail.book.activeMemberCount)))}
            </InlineAlert>
          )}
        </ReasonDialog>
      )}

      {selected && historyOpen && (
        <HistoryModal
          title={fillLocalized(S.policyHistoryTitle, selected.policyNo)}
          load={() => api.policyHistory(selected.policyId)}
          facts={(e) => (
            <>
              <Fact label={t(S.status)} value={enumLabel(e.status)} />
              <Fact
                label={t(S.window)}
                value={`${fmt.date(e.effectiveFrom)} → ${e.effectiveTo ? fmt.date(e.effectiveTo) : "—"}`}
              />
              {typeof e.maxMembers === "number" && <Fact label={t(S.cap)} value={fmt.number(e.maxMembers)} />}
            </>
          )}
          onClose={() => setHistoryOpen(false)}
        />
      )}
    </div>
  );
}

/** The three moves, named once. A `Record` per axis rather than a switch in five places: the labels, the
 *  bodies and the announcements have to stay in step, and three parallel switches is how they stop. */
const MOVE_TITLE: Record<"suspend" | "resume" | "expire", Localized> =
  { suspend: S.suspendTitle, resume: S.resumeTitle, expire: S.expireTitle };
const MOVE_BODY: Record<"suspend" | "resume" | "expire", Localized> =
  { suspend: S.suspendBody, resume: S.resumeBody, expire: S.expireBody };
const MOVE_CONFIRM: Record<"suspend" | "resume" | "expire", Localized> =
  { suspend: S.suspendPolicy, resume: S.resumePolicy, expire: S.expirePolicy };
const MOVE_DONE: Record<"suspend" | "resume" | "expire", Localized> =
  { suspend: S.policySuspended, resume: S.policyResumed, expire: S.policyExpired };

const WINDOW_LABEL: Record<string, Localized> = {
  NotYetStarted: S.windowNotYetStarted, InForce: S.windowInForce, Ended: S.windowEnded,
};
const WINDOW_KIND: Record<string, "ok" | "info" | "warn" | "neu"> = {
  NotYetStarted: "info", InForce: "ok", Ended: "warn",
};

// ── The contract's own record ───────────────────────────────────────────────────────────────────────────

function PolicyDetailPane({
  policy, book, policyNo, mayWrite, onEdit, onMove, onHistory,
}: {
  policy: PolicyAdminView | null;
  book: PolicyBook | null;
  policyNo: string;
  mayWrite: boolean;
  onEdit: () => void;
  onMove: (m: "suspend" | "resume" | "expire") => void;
  onHistory: () => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const enumLabel = useEnumLabel();
  const active = policy?.status === "Active";
  const suspended = policy?.status === "Suspended";
  const ended = policy?.status === "Expired";

  return (
    <Card>
      <div className="screen-toolbar">
        <div className="pay-head">
          <h3><span className="tnum">{policy?.policyNo ?? policyNo}</span></h3>
          <div className="pay-chips">
            {policy && <StatusChip kind={statusKind(policy.status)} label={enumLabel(policy.status)} />}
            {policy && (
              <StatusChip
                kind={WINDOW_KIND[policy.windowState] ?? "neu"}
                label={t(WINDOW_LABEL[policy.windowState] ?? { en: policy.windowState, ar: policy.windowState })}
              />
            )}
            {policy && (
              <span className="pol-muted">
                {fmt.date(policy.effectiveFrom)} → {policy.effectiveTo ? fmt.date(policy.effectiveTo) : "—"}
              </span>
            )}
          </div>
        </div>
        <RecordActions
          onHistory={onHistory}
          onEdit={mayWrite && policy && !ended ? onEdit : undefined}
          editLabel={S.editPolicy}
          status={mayWrite && policy && !ended
            ? {
                label: suspended ? S.resumePolicy : S.suspendPolicy,
                icon: suspended ? "undo" : "lock",
                onClick: () => onMove(suspended ? "resume" : "suspend"),
              }
            : undefined}
        >
          {/* Ending is separated from the suspend toggle: it is the one move on this record that cannot be
              taken back, and putting it under the same control as a reversible one invites the wrong click. */}
          {mayWrite && policy && !ended && (
            <Button variant="ghost" aria-label={t(S.expirePolicy)} title={t(S.expirePolicy)} onClick={() => onMove("expire")}>
              <Icon name="bin" />
            </Button>
          )}
        </RecordActions>
      </div>

      {/* The combination somebody has to act on, said in words — the same treatment the payer's expired
          agreement gets, because it is the same shape of fact. */}
      {policy && active && policy.windowState === "Ended" && (
        <InlineAlert tone="warn">{t(S.activeButEnded)}</InlineAlert>
      )}
      {policy && policy.status !== "Active" && policy.statusReason && (
        <InlineAlert tone="info">
          {policy.statusReason}
          {policy.statusChangedAt ? ` — ${fmt.dateTime(policy.statusChangedAt)}` : ""}
        </InlineAlert>
      )}

      {book && (
        <>
          <KpiList
            items={[
              { label: t(S.membersTotal), value: fmt.number(book.memberCount) },
              { label: t(S.activeMembers), value: fmt.number(book.activeMemberCount) },
              { label: t(S.plansOn), value: fmt.number(book.planCount) },
              {
                label: t(S.committed),
                // null is "withheld", 0 is zero — rendering both as an em dash would tell a role with no
                // amount access that a policy with a book of business has none.
                value: book.committedLimit === null || book.committedLimit === undefined
                  ? t(S.restricted)
                  : fmt.money(book.committedLimit),
              },
            ]}
          />
          {typeof book.percentOfCap === "number" && (
            <p className={book.percentOfCap > 100 ? "" : "pol-muted"}>
              {t(fillLocalized(S.ofCap, fmt.number(book.percentOfCap, { maximumFractionDigits: 1 })))}
            </p>
          )}
          {typeof book.percentOfCap === "number" && book.percentOfCap > 100 && (
            <InlineAlert tone="warn">{t(S.overCap)}</InlineAlert>
          )}
        </>
      )}

      {policy?.terms?.notes && <p>{policy.terms.notes}</p>}
      {policy?.updatedByName && (
        <p className="pol-muted">
          {t(S.lastChanged)}: {fmt.dateTime(policy.updatedAt)} {t(fillLocalized(S.by, policy.updatedByName))}
        </p>
      )}
    </Card>
  );
}

function statusKind(status: string): "ok" | "warn" | "neu" {
  return status === "Active" ? "ok" : status === "Suspended" ? "warn" : "neu";
}

// ── Issue / amend ───────────────────────────────────────────────────────────────────────────────────────

function PolicyForm({
  api, mode, policy, policyNo, payers, onClose, onSaved,
}: {
  api: PolicyApi;
  mode: "create" | "edit";
  policy: PolicyAdminView | null;
  policyNo: string;
  payers: PayerView[];
  onClose: () => void;
  onSaved: (policyId: string, wasCreate: boolean) => void | Promise<void>;
}) {
  const t = useLoc();
  const { lang } = useTheme();
  const [key, rotate] = useIdempotencyKey();
  const [no, setNo] = useState("");
  const [from, setFrom] = useState(policy?.effectiveFrom ?? "");
  const [until, setUntil] = useState(policy?.effectiveTo ?? "");
  const [cap, setCap] = useState(
    typeof policy?.terms?.maxMembers === "number" ? String(policy.terms.maxMembers) : "");
  const [payerId, setPayerId] = useState(policy?.terms?.payerId ?? "");
  const [notes, setNotes] = useState(policy?.terms?.notes ?? "");
  const [problem, setProblem] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    const capValue = cap.trim() === "" ? null : Number(cap);
    if (mode === "create" && !no.trim()) { setProblem(S.needPolicyNo); return; }
    if (mode === "create" && !payerId) { setProblem(S.needPayer); return; }
    if (!from) { setProblem(S.needForwardWindow); return; }
    if (until && until < from) { setProblem(S.needForwardWindow); return; }
    if (capValue !== null && !(capValue > 0)) { setProblem(S.needPositiveCap); return; }

    const body = {
      effectiveFrom: from,
      effectiveTo: until || null,
      maxMembers: capValue,
      payerId: payerId || null,
      notes: notes.trim() || null,
    };

    setBusy(true);
    setProblem(null);
    try {
      const id = mode === "create"
        ? (await api.createPolicy({ ...body, policyNo: no.trim(), payerId }, key)).policyId
        : (await api.updatePolicy(policy!.policyId, body)).policyId;
      await onSaved(id, mode === "create");
    } catch (e) {
      rotate();
      setProblem(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={t(mode === "create" ? S.formCreatePolicy : S.formEditPolicy)}
      closeLabel={t(S.cancel)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button variant="primary" onClick={() => void submit()} loading={busy}>
            {t(mode === "create" ? S.createPolicy : S.save)}
          </Button>
        </>
      }
    >
      {problem && <InlineAlert tone="bad">{t(problem)}</InlineAlert>}
      <div className="pay-form-grid">
        {mode === "create" ? (
          <InputField
            label={t(S.policyNoLabel)} value={no} onChange={(e) => setNo(e.currentTarget.value)}
            help={t(S.policyNoLocked)} required
          />
        ) : (
          <InputField label={t(S.policyNoLabel)} value={policy?.policyNo ?? policyNo} readOnly help={t(S.policyNoLocked)} />
        )}
        <ComboboxField
          label={t(S.payer)}
          value={payerId || null}
          placeholder={t(S.anyPayer)}
          onChange={(v) => setPayerId(v ?? "")}
          options={payers.map((p) => ({ value: p.payerId, label: lang === "ar" ? p.nameAr : p.nameEn }))}
        />
        <InputField label={t(S.from)} type="date" value={from} onChange={(e) => setFrom(e.currentTarget.value)} required />
        <InputField label={t(S.until)} type="date" value={until} onChange={(e) => setUntil(e.currentTarget.value)} help={t(S.untilHint)} />
        <InputField
          label={t(S.cap)} type="number" min={1} inputMode="numeric"
          value={cap} onChange={(e) => setCap(e.currentTarget.value)} help={t(S.capHint)}
        />
      </div>
      <TextareaField label={t(S.policyNotes)} rows={3} value={notes} onChange={(e) => setNotes(e.currentTarget.value)} />
    </Modal>
  );
}

function PolicyPlansTab({ api, policyId }: { api: PolicyApi; policyId: string }) {
  const t = useLoc();
  const enumLabel = useEnumLabel();
  const fmt = useFormat();
  const [rows, setRows] = useState<PolicyPlanView[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    setRows(null);
    api.policyPlans(policyId).then((r) => live && setRows(r)).catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api, policyId]);

  return (
    <Card data-testid="policy-plans-tab">
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      <DataTable
        caption={t(S.tabPlans)}
        rows={rows ?? []}
        rowKey={(r) => r.policyPlanId}
        loading={rows === null && !error}
        emptyLabel={t(S.noPlansUnder)}
        columns={[
          {
            key: "label",
            header: t(S.planLabel),
            cell: (r) => (
              <>
                {r.planLabel} {r.isDefault && <StatusChip kind="info" label={t(S.default)} />}
              </>
            ),
          },
          { key: "window", header: t(S.window), cell: (r) => `${fmt.date(r.effectiveFrom)} → ${r.effectiveTo ? fmt.date(r.effectiveTo) : "—"}`, sortable: true, sortValue: (r) => r.effectiveFrom },
          { key: "members", header: t(S.members), cell: (r) => fmt.number(r.memberCount), numeric: true, sortable: true, sortValue: (r) => r.memberCount },
          { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={enumLabel(r.status)} />, sortable: true, sortValue: (r) => r.status },
        ]}
      />
    </Card>
  );
}

function PolicyGroupsTab({ api, policyId }: { api: PolicyApi; policyId: string }) {
  const t = useLoc();
  const enumLabel = useEnumLabel();
  const fmt = useFormat();
  const [rows, setRows] = useState<MemberGroupView[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    setRows(null);
    api.policyGroups(policyId).then((r) => live && setRows(r)).catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api, policyId]);

  return (
    <Card data-testid="policy-groups-tab">
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      <DataTable
        caption={t(S.groupsTitle)}
        rows={rows ?? []}
        rowKey={(r) => r.groupId}
        loading={rows === null && !error}
        emptyLabel={t(S.noGroups)}
        columns={[
          { key: "code", header: t(S.groupCode), cell: (r) => r.groupCode, sortable: true, sortValue: (r) => r.groupCode },
          { key: "name", header: t(S.groupName), cell: (r) => r.nameEn, sortable: true, sortValue: (r) => r.nameEn },
          { key: "type", header: t(S.groupType), cell: (r) => enumLabel(r.groupType), sortable: true, sortValue: (r) => r.groupType },
          { key: "window", header: t(S.window), cell: (r) => `${fmt.date(r.effectiveFrom)} → ${r.effectiveTo ? fmt.date(r.effectiveTo) : "—"}`, sortable: true, sortValue: (r) => r.effectiveFrom },
          { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={enumLabel(r.status)} />, sortable: true, sortValue: (r) => r.status },
        ]}
      />
    </Card>
  );
}

/**
 * A group / plan / policy / payer utilization aggregate. Shared by the policy detail and the standalone
 * Utilization section, because "how much has this cohort used" is one question with one answer.
 */
export function ScopeUtilizationPanel({
  api,
  scope,
  id,
}: {
  api: PolicyApi;
  scope: "groups" | "plans" | "policies" | "payers";
  id: string;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const [view, setView] = useState<ScopeUtilizationView | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    setView(null);
    api
      .scopeUtilization(scope, id)
      .then((v) => live && setView(v))
      .catch((e) => live && setError(writeErrorMessage(e).message));
    return () => { live = false; };
  }, [api, scope, id]);

  const meters = useMemo(
    () =>
      (view?.members ?? []).slice(0, 25).map((m) => ({
        label: m.memberNo,
        consumed: m.totalConsumed,
        limit: m.totalLimit,
        valueText: fmt.money(m.totalConsumed),
        limitText: m.anyUnlimited ? "∞" : fmt.money(m.totalLimit),
      })),
    [view, fmt],
  );

  return (
    // `Card` is a SURFACE — background, border, radius, shadow, and no padding; every screen that uses one
    // supplies its own (the filter card above this does). This one supplied none, so the KPI tiles sat flush
    // against the card's own border with their labels reading as clipped, and the member meters ran edge to
    // edge and collided with the tiles above them. The gap matters as much as the padding: four stacked
    // children (alerts, KPIs, meters, outlier table) with nothing between them is what made it look crammed
    // rather than merely tight.
    <Card
      data-testid="scope-utilization"
      className="pol-stack-lg"
    >
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {view && (
        <>
          {/* The reconciliation statement is carried in every response and rendered on every report — a
              disagreement between the accumulator and the reported total must be visible to whoever acts on
              the number, not discovered afterwards. */}
          {!view.reconciliation.reconciled && <InlineAlert tone="bad">{t(S.reconcileBad)}</InlineAlert>}
          {/*
            * The four headline figures, in the KPI treatment 0B §10b specifies (audit §5.2).
            *
            * They were `<dl class="pol-kpis">` — dt at 0.82rem, dd at 1.25rem, four pairs in a flex row —
            * which is to say plain text with a size difference. On a screen whose entire purpose is "how much
            * of this cohort's entitlement is gone", the answer read as a caption. `KpiList` keeps the
            * definition-list semantics (four terms describing one subject, announced as pairs) and takes the
            * hairline, the uppercase micro-label and the 34px tabular numerals from the same classes
            * `KpiCard` uses, so the two cannot drift into two different-looking KPIs.
            */}
          <KpiList
            items={[
              { label: t(S.members), value: fmt.number(view.memberCount) },
              { label: t(S.totalLimit), value: fmt.money(view.totalLimit) },
              { label: t(S.totalConsumed), value: fmt.money(view.totalConsumed) },
              { label: t(S.remaining), value: fmt.money(view.totalRemaining) },
            ]}
          />

          <LimitMeters caption={t(S.utilizationCaption)} rows={meters} />

          {view.outliers.length > 0 && (
            <>
              <h4>
                {t(S.outliers)} ({Math.round(view.outlierThresholdPercent)}%)
              </h4>
              <DataTable
                caption={t(S.outliers)}
                rows={view.outliers}
                rowKey={(r) => r.enrollmentId}
                density="compact"
                columns={[
                  { key: "member", header: t(S.memberNo), cell: (r) => r.memberNo, sortable: true, sortValue: (r) => r.memberNo },
                  { key: "consumed", header: t(S.totalConsumed), cell: (r) => fmt.money(r.totalConsumed), numeric: true, sortable: true, sortValue: (r) => r.totalConsumed },
                  { key: "limit", header: t(S.totalLimit), cell: (r) => (r.anyUnlimited ? "∞" : fmt.money(r.totalLimit)), numeric: true },
                  { key: "pct", header: t(S.used), cell: (r) => (r.percentUsed != null ? `${Math.round(r.percentUsed)}%` : "—"), sortable: true, sortValue: (r) => r.percentUsed },
                ]}
              />
            </>
          )}

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

/**
 * The standalone Utilization section — the same aggregate as the policy detail's tab, over any scope, with
 * the audited CSV export.
 *
 * The export goes through the service's own `/utilization/export`, which writes an audit event carrying the
 * row count. Building the CSV client-side from data already on screen would have been easier and would have
 * produced a file nobody could later account for.
 */
export function UtilizationScreen({
  api = httpPolicyApi,
  embedded = false,
}: {
  api?: PolicyApi;
  /**
   * Rendered as a PANEL inside another screen rather than as a screen of its own — which is how the
   * beneficiary portal reaches it now that Utilization is a tab in Analytics rather than a nav section.
   *
   * All it suppresses is the page header. A tab panel that prints its own <h1> gives the page two titles and
   * two competing answers to "where am I"; policy administration still opens this at `/policy/utilization`,
   * where the header is the only thing naming the screen.
   */
  embedded?: boolean;
}) {
  const t = useLoc();
  const [policies, setPolicies] = useState<PolicyQueryRow[]>([]);
  const [scope, setScope] = useState<"policies" | "groups" | "plans" | "payers">("policies");
  const [scopeId, setScopeId] = useState<string>("");
  const [error, setError] = useState<Localized | null>(null);
  const [announce, setAnnounce] = useState("");

  useEffect(() => {
    let live = true;
    api
      .policyQuery({ pageSize: 50 })
      .then((p) => {
        if (!live) return;
        setPolicies(p.items);
        setScopeId(p.items[0]?.policyId ?? "");
      })
      .catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api]);

  async function exportCsv() {
    try {
      const csv = await api.exportUtilization(scopeMap[scope], scopeId);
      const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `utilization-${scope}-${scopeId.slice(0, 8)}.csv`;
      a.click();
      URL.revokeObjectURL(url);
      setAnnounce(t(S.exported));
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }

  return (
    <div className="pol-screen">
      {!embedded && <PageHeader title={t(S.tabUtilization)} />}
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      <Card className="pol-scopebar">
        {/* QA P1-11 fixed the label running into a zero-width bare select by hand-building the field markup
            around it. `ComboboxField` IS that markup, so the wrapper goes and the width stays.
            Searchable, and this is the case the audit was loudest about: the list is every policy on the
            book, and first-letter typeahead over policy numbers means arrowing to find one. The empty
            `<option>` that had to exist so an empty control looked empty is now a placeholder — "nothing
            chosen" stops being a selectable row reading "—". */}
        <ComboboxField
          id="util-policy"
          label={t(S.policyNo)}
          style={{ minWidth: "var(--field-max)" }}
          placeholder="—"
          value={scopeId || null}
          onChange={(v) => {
            setScope("policies");
            setScopeId(v);
          }}
          options={policies.map((p) => ({ value: p.policyId, label: p.policyNo }))}
        />
        <Button leadingIcon={<Icon name="download" />} variant="secondary" onClick={exportCsv} disabled={!scopeId}>
          {t(S.export)}
        </Button>
      </Card>
      {scopeId && <ScopeUtilizationPanel api={api} scope={scope} id={scopeId} />}
    </div>
  );
}

/** The export endpoint names its scopes in the singular domain vocabulary, not the REST path segment. */
const scopeMap: Record<"policies" | "groups" | "plans" | "payers", string> = {
  policies: "Policy",
  groups: "Group",
  plans: "Plan",
  payers: "Payer",
};

/** The standalone Groups section: every group across the policies the caller can see, with its utilization. */
export function GroupsScreen({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const enumLabel = useEnumLabel();
  const fmt = useFormat();
  const [policies, setPolicies] = useState<PolicyQueryRow[]>([]);
  const [policyId, setPolicyId] = useState<string | null>(null);
  const [groups, setGroups] = useState<MemberGroupView[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    api
      .policyQuery({ pageSize: 50 })
      .then((p) => {
        if (!live) return;
        setPolicies(p.items);
        setPolicyId(p.items[0]?.policyId ?? null);
      })
      .catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api]);

  const load = useCallback(async () => {
    if (!policyId) return;
    try {
      setGroups(await api.policyGroups(policyId));
      setSelected(null);
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }, [api, policyId]);

  const groupColumns: Column<MemberGroupView>[] = [
    { key: "code", header: t(S.groupCode), cell: (r) => <span className="tnum">{r.groupCode}</span>, sortable: true, sortValue: (r) => r.groupCode },
    { key: "name", header: t(S.groupName), cell: (r) => r.nameEn, sortable: true, sortValue: (r) => r.nameEn },
    { key: "type", header: t(S.groupType), cell: (r) => enumLabel(r.groupType), sortable: true, sortValue: (r) => r.groupType },
    { key: "window", header: t(S.window), cell: (r) => `${fmt.date(r.effectiveFrom)} → ${r.effectiveTo ? fmt.date(r.effectiveTo) : "—"}`, sortable: true, sortValue: (r) => r.effectiveFrom },
  ];

  /* One policy's groups come down whole, so the search and the type chips run in the browser. The POLICY
     picker above is a different kind of control and stays where it is: it changes what the server returns. */
  const groupQuery = useTableQuery<MemberGroupView>({
    rows: groups,
    columns: groupColumns,
    searchText: (r) => `${r.groupCode} ${r.nameEn}`,
    searchLabel: t(S.searchGroups),
    searchPlaceholder: t(S.searchGroupsHint),
    filters: [
      {
        key: "type",
        label: t(S.groupTypeFilter),
        options: [...new Set(groups.map((g) => g.groupType))].sort().map((v) => ({ value: v, label: enumLabel(v) })),
        match: (r, v) => r.groupType === v,
      },
    ],
    initialSortKey: "code",
    persistKey: "policy-groups",
  });

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.groupsTitle)} />
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      <Card>
        <ComboboxField
          id="grp-policy"
          label={t(S.policyNo)}
          style={{ maxWidth: "var(--field-max)" }}
          placeholder="—"
          value={policyId}
          onChange={setPolicyId}
          options={policies.map((p) => ({ value: p.policyId, label: p.policyNo }))}
        />
      </Card>
      <Card>
        <DataTableView
          query={groupQuery}
          columns={groupColumns}
          rowKey={(r) => r.groupId}
          caption={t(S.groupsTitle)}
          interactive
          selectedKey={selected}
          onSelect={(r) => setSelected(r.groupId)}
          emptyLabel={t(S.noGroups)}
          noMatchesLabel={t(S.noGroupMatches)}
        />
      </Card>
      {selected && <ScopeUtilizationPanel api={api} scope="groups" id={selected} />}
    </div>
  );
}

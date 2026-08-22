import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Button, Card, Combobox, DataTableView, Icon, InlineAlert, InputField, KpiList, Modal, StatusChip,
  TextareaField, useTableQuery,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type {
  ActivationProblem,
  BenefitCategoryView,
  BenefitRuleInput,
  BenefitRuleTierInput,
  BenefitRuleView,
  NetworkTierView,
  PlanAdminView,
  PlanBook,
  PlanDetail,
  PlanVersionView,
  PlanView,
  PolicyApi,
} from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";

/** ONE client for the module, not one per render: a default parameter re-evaluates on every call,
 *  and screens key their load effects on the api instance — a fresh instance per render turned the
 *  first failing (or even succeeding) fetch into an unbounded request loop (QA P0-1: ~400 req/s).*/
const httpPolicyApi = createHttpPolicyApi();
import { writeErrorMessage } from "../api/writeError";
import { PageHeader, fillLocalized, useLoc, readErrorMessage } from "./_shared";
import { useAuth } from "../auth/AuthProvider";
import { mayAdministerBenefitProduct } from "../authz/permissions";
import { Fact, HistoryModal, ReasonDialog, RecordActions } from "./AdminRecordControls";
import { useIdempotencyKey } from "./PolicyPanels";
import { useFormat } from "../i18n/useFormat";
import { useEnumLabel } from "../i18n/enumLabels";

/**
 * Phase 19.6 — payers, plans, and THE PLAN VERSION EDITOR (design 38 §4.1 / §4.1b).
 *
 * The editor is the screen this whole portal exists for, and its central claim is structural: a Draft is
 * freely editable and an Active version is immutable, so the UI must not offer an edit it will be refused.
 * `editable` is projected by the service rather than derived here from the status — deriving the same rule in
 * two places is how a read-only screen ends up with a working save button.
 *
 * The grid is TWO LEVELS because a benefit has two kinds of fact. What the plan covers (limit, reset,
 * waiting period, pre-authorisation, exclusions) is one row per benefit category. What the MEMBER PAYS
 * depends additionally on where they were treated — so each category expands into a cost-share matrix with
 * one column per Active network tier. Flattening those into a single grid would ask an administrator to hold
 * "category × tier" in their head while typing; keeping them separate makes an unpriced tier visible as an
 * empty cell, which is exactly what activation refuses.
 */

const S = {
  plans: { en: "Plans & Versions", ar: "الخطط والإصدارات" },
  payerCode: { en: "Code", ar: "الرمز" },
  name: { en: "Name", ar: "الاسم" },
  type: { en: "Type", ar: "النوع" },
  status: { en: "Status", ar: "الحالة" },
  category: { en: "Category", ar: "الفئة" },
  noPlans: { en: "No plans configured.", ar: "لا توجد خطط." },
  searchPlans: { en: "Search plans", ar: "بحث في الخطط" },
  searchPlansHint: { en: "Code, name, or category", ar: "الرمز أو الاسم أو الفئة" },
  noPlanMatches: { en: "No plan matches your search.", ar: "لا توجد خطة مطابقة لبحثك." },
  filterStatus: { en: "Status", ar: "الحالة" },
  filterCategory: { en: "Category", ar: "الفئة" },
  versions: { en: "Versions", ar: "الإصدارات" },
  version: { en: "Version", ar: "الإصدار" },
  window: { en: "In force", ar: "سارٍ" },
  draft: { en: "Draft — editable", ar: "مسودة — قابلة للتعديل" },
  immutable: { en: "Active — immutable", ar: "سارٍ — غير قابل للتعديل" },
  immutableHint: {
    en: "An active version is the benefit configuration claims are judged against, so it can never be edited. Amend it to create a new draft.",
    ar: "الإصدار الساري هو تكوين المنافع الذي تُقيَّم عليه المطالبات، ولذلك لا يمكن تعديله. عدّله لإنشاء مسودة جديدة.",
  },
  amend: { en: "Amend — create a new draft", ar: "تعديل — إنشاء مسودة جديدة" },
  validate: { en: "Validate", ar: "التحقق" },
  activate: { en: "Activate", ar: "التفعيل" },
  saveRules: { en: "Save benefit configuration", ar: "حفظ تكوين المنافع" },
  valid: { en: "This draft passes validation and can be activated.", ar: "اجتازت هذه المسودة التحقق ويمكن تفعيلها." },
  covered: { en: "Covered", ar: "مغطّى" },
  limit: { en: "Limit", ar: "الحد" },
  limitType: { en: "Limit type", ar: "نوع الحد" },
  reset: { en: "Reset", ar: "إعادة التعيين" },
  waiting: { en: "Waiting (days)", ar: "فترة الانتظار (أيام)" },
  preauth: { en: "Pre-auth", ar: "موافقة مسبقة" },
  exclusions: { en: "Exclusions", ar: "الاستثناءات" },
  costShare: { en: "Cost share by network tier", ar: "مشاركة التكلفة حسب شريحة الشبكة" },
  copay: { en: "Co-pay", ar: "المشاركة الثابتة" },
  coinsurance: { en: "Co-insurance %", ar: "نسبة المشاركة ٪" },
  expand: { en: "Cost share", ar: "مشاركة التكلفة" },
  noTiers: {
    en: "No active network tiers. Cost share cannot be authored until the Network Team defines one.",
    ar: "لا توجد شرائح شبكة نشطة. لا يمكن تحديد مشاركة التكلفة حتى تنشئ الشبكة شريحة.",
  },
  unpriced: { en: "Unpriced", ar: "غير مسعّر" },
  unpricedHint: {
    en: "A covered category that leaves an active tier unpriced cannot be activated.",
    ar: "لا يمكن تفعيل فئة مغطّاة تترك شريحة نشطة دون تسعير.",
  },
  diff: { en: "What changed from the previous version", ar: "ما تغيّر عن الإصدار السابق" },
  noDiff: { en: "No previous version to compare against.", ar: "لا يوجد إصدار سابق للمقارنة." },
  unchanged: { en: "No differences in benefit configuration.", ar: "لا توجد فروق في تكوين المنافع." },
  added: { en: "Added", ar: "مضاف" },
  removed: { en: "Removed", ar: "محذوف" },
  changed: { en: "Changed", ar: "معدّل" },
  saved: { en: "Benefit configuration saved.", ar: "تم حفظ تكوين المنافع." },
  activated: { en: "Version activated. It is now immutable.", ar: "تم تفعيل الإصدار. أصبح غير قابل للتعديل." },
  amended: { en: "New draft created from the active version.", ar: "تم إنشاء مسودة جديدة من الإصدار الساري." },
  selectPlan: { en: "Select a plan to see its versions.", ar: "اختر خطة لعرض إصداراتها." },
  members: { en: "Members", ar: "الأعضاء" },
  // ── 19.8: the plan as an administrable record ──────────────────────────────────────────────────────────
  newPlan: { en: "New plan", ar: "خطة جديدة" },
  editPlan: { en: "Edit this plan", ar: "تعديل هذه الخطة" },
  deactivatePlan: { en: "Withdraw this plan", ar: "سحب هذه الخطة" },
  reactivatePlan: { en: "Return this plan to the catalogue", ar: "إعادة الخطة إلى الكتالوج" },
  planHistory: { en: "Change history", ar: "سجل التغييرات" },
  planIdentity: { en: "Plan", ar: "الخطة" },
  planCode: { en: "Plan code", ar: "رمز الخطة" },
  planCodeLocked: {
    en: "The code can never be changed. Extracts, reconciliation files and the payer's own systems join on it. To replace a code, create the right plan and move its policies deliberately.",
    ar: "لا يمكن تغيير الرمز أبدًا. فالمستخرجات وملفات التسوية وأنظمة الجهة الممولة ترتبط به. لاستبدال الرمز، أنشئ الخطة الصحيحة وانقل وثائقها عمدًا.",
  },
  nameEn: { en: "Name (English)", ar: "الاسم (إنجليزي)" },
  nameAr: { en: "Name (Arabic)", ar: "الاسم (عربي)" },
  planDescription: { en: "Description", ar: "الوصف" },
  needPlanCode: { en: "A plan code is required.", ar: "رمز الخطة مطلوب." },
  needPlanNames: {
    en: "A plan needs a name in both languages: half the platform renders in Arabic.",
    ar: "تحتاج الخطة إلى اسم بكلتا اللغتين: نصف المنصة يُعرض بالعربية.",
  },
  needCategory: { en: "A plan needs a category.", ar: "تحتاج الخطة إلى فئة." },
  planCreated: { en: "Plan created.", ar: "تم إنشاء الخطة." },
  planUpdated: { en: "Plan updated.", ar: "تم تحديث الخطة." },
  planWithdrawn: { en: "Plan withdrawn from the catalogue.", ar: "تم سحب الخطة من الكتالوج." },
  planReturned: { en: "Plan returned to the catalogue.", ar: "أُعيدت الخطة إلى الكتالوج." },
  save: { en: "Save", ar: "حفظ" },
  create: { en: "Create plan", ar: "إنشاء خطة" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  formCreate: { en: "New plan", ar: "خطة جديدة" },
  formEdit: { en: "Edit plan", ar: "تعديل الخطة" },
  // ── the book of business ───────────────────────────────────────────────────────────────────────────────
  versionsTotal: { en: "Versions", ar: "الإصدارات" },
  drafts: { en: "Drafts", ar: "المسودات" },
  inForceNow: { en: "Active version", ar: "الإصدار الساري" },
  policiesSelling: { en: "Policies", ar: "الوثائق" },
  activePolicies: { en: "Active policies", ar: "الوثائق النشطة" },
  membersOnPlan: { en: "Members", ar: "الأعضاء" },
  sellableWindow: { en: "Sellable", ar: "متاح للبيع" },
  openEnded: { en: "open-ended", ar: "غير محدد" },
  // ── withdrawal ─────────────────────────────────────────────────────────────────────────────────────────
  withdrawTitle: { en: "Withdraw {0} from the catalogue?", ar: "سحب {0} من الكتالوج؟" },
  withdrawBody: {
    en: "{0} stops being offered for new policies. Its versions stay resolvable forever — a claim for care given last March is still judged by March's rules — and nothing already enrolled changes.",
    ar: "لن تُعرض {0} للوثائق الجديدة. وتبقى إصداراتها قابلة للتحديد دائمًا — فالمطالبة عن رعاية قُدّمت في مارس تُقيَّم وفق قواعد مارس — ولا يتغيّر أي تسجيل قائم.",
  },
  returnTitle: { en: "Return {0} to the catalogue?", ar: "إعادة {0} إلى الكتالوج؟" },
  returnBody: {
    en: "{0} becomes available again for new policies.",
    ar: "تصبح {0} متاحة مجددًا للوثائق الجديدة.",
  },
  reversible: { en: "It can be reversed at any time.", ar: "يمكن التراجع عنه في أي وقت." },
  historyTitle: { en: "Change history — {0}", ar: "سجل التغييرات — {0}" },
  selectPlanFirst: { en: "Select a plan to see its versions and what is sold against them.", ar: "اختر خطة لعرض إصداراتها وما يُباع عليها." },
  lastChanged: { en: "Last changed", ar: "آخر تعديل" },
  by: { en: "by {0}", ar: "بواسطة {0}" },
} satisfies Record<string, Localized>;

const LIMIT_TYPES = ["", "Annual", "PerEncounter", "Lifetime", "Count"];
const RESET_PERIODS = ["None", "Monthly", "Quarterly", "Yearly"];

// Payers moved to PolicyPayerAdmin.tsx in 19.7, when the section grew a detail pane and three writes.

function BiName({ en, ar }: { en: string; ar: string }) {
  const t = useLoc();
  return <>{t({ en, ar })}</>;
}

// ── Plans + the version editor ──────────────────────────────────────────────────────────────────────────

export function PolicyPlans({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const enumLabel = useEnumLabel();
  const fmt = useFormat();
  const [plans, setPlans] = useState<PlanView[] | null>(null);
  const [categories, setCategories] = useState<BenefitCategoryView[]>([]);
  const [tiers, setTiers] = useState<NetworkTierView[]>([]);
  const [selectedPlan, setSelectedPlan] = useState<string | null>(null);
  const [versions, setVersions] = useState<PlanVersionView[]>([]);
  const [selectedVersion, setSelectedVersion] = useState<string | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [announce, setAnnounce] = useState("");
  // 19.8 — the plan's own detail, loaded on selection. A second read rather than a slice of the list,
  // because the book of business is a set of aggregates the list cannot afford per row.
  const [detail, setDetail] = useState<PlanDetail | null>(null);
  const [form, setForm] = useState<{ mode: "create" } | { mode: "edit"; plan: PlanAdminView } | null>(null);
  const [statusChange, setStatusChange] = useState<"deactivate" | "reactivate" | null>(null);
  const [historyOpen, setHistoryOpen] = useState(false);

  const { session } = useAuth();
  const mayWrite = mayAdministerBenefitProduct(session?.role ?? undefined);

  useEffect(() => {
    let live = true;
    Promise.all([api.plans(), api.benefitCategories(), api.networkTiers()])
      .then(([p, c, tr]) => {
        if (!live) return;
        setPlans(p);
        setCategories(c);
        setTiers(tr);
      })
      .catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api]);

  useEffect(() => {
    if (!selectedPlan) { setDetail(null); return; }
    let live = true;
    setDetail(null);
    api.plan(selectedPlan)
      .then((d) => { if (live) setDetail(d); })
      .catch((e) => { if (live) setError(readErrorMessage(e)); });
    return () => { live = false; };
  }, [api, selectedPlan]);

  const reloadPlans = useCallback(async (id: string) => {
    try {
      setPlans(await api.plans());
      setDetail(await api.plan(id));
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }, [api]);

  const loadVersions = useCallback(
    async (planId: string) => {
      try {
        const v = await api.planVersions(planId);
        setVersions(v);
        setSelectedVersion(v[0]?.planVersionId ?? null);
      } catch (e) {
        setError(readErrorMessage(e));
      }
    },
    [api],
  );

  useEffect(() => {
    if (selectedPlan) void loadVersions(selectedPlan);
  }, [selectedPlan, loadVersions]);

  /** Only ACTIVE tiers get a column: pricing against a retired tier authors a number nothing will ever read. */
  const activeTiers = useMemo(
    () => tiers.filter((x) => x.status === "Active").sort((a, b) => a.rank - b.rank),
    [tiers],
  );

  const current = plans?.find((p) => p.planId === selectedPlan) ?? null;
  const version = versions.find((v) => v.planVersionId === selectedVersion) ?? null;
  const previous = version
    ? versions.filter((v) => v.versionNo < version.versionNo).sort((a, b) => b.versionNo - a.versionNo)[0] ?? null
    : null;

  const planColumns: Column<PlanView>[] = [
    { key: "code", header: t(S.payerCode), cell: (r) => <span className="tnum">{r.planCode}</span>, sortable: true, sortValue: (r) => r.planCode },
    { key: "name", header: t(S.name), cell: (r) => <BiName en={r.nameEn} ar={r.nameAr} /> },
    { key: "category", header: t(S.category), cell: (r) => r.category, sortable: true, sortValue: (r) => r.category },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={enumLabel(r.status)} />, sortable: true, sortValue: (r) => r.status },
  ];

  const planQuery = useTableQuery<PlanView>({
    rows: plans ?? [],
    columns: planColumns,
    searchText: (r) => `${r.planCode} ${r.nameEn} ${r.nameAr} ${r.category}`,
    searchLabel: t(S.searchPlans),
    searchPlaceholder: t(S.searchPlansHint),
    filters: [
      {
        key: "status",
        label: t(S.filterStatus),
        options: [
          { value: "Active", label: enumLabel("Active") },
          { value: "Inactive", label: enumLabel("Inactive") },
        ],
        match: (r, v) => r.status === v,
      },
      {
        // The category vocabulary is whatever the catalogue holds, so the chips are derived from the rows
        // rather than hardcoded — a category added on the server appears here without a code change.
        key: "category",
        label: t(S.filterCategory),
        options: [...new Set((plans ?? []).map((p) => p.category))].sort().map((c) => ({ value: c, label: c })),
        match: (r, v) => r.category === v,
      },
    ],
    initialSortKey: "code",
    persistKey: "policy-plans",
  });

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.plans)} />
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {mayWrite && (
        <div className="screen-toolbar">
          <span />
          <Button variant="primary" leadingIcon={<Icon name="plus" />} onClick={() => setForm({ mode: "create" })}>
            {t(S.newPlan)}
          </Button>
        </div>
      )}

      {/* The house table — search, filters, sortable columns, pager — rather than the bare `DataTable` this
          was. Payers sits directly above this in the same nav group and has had all four since 19.7; a
          catalogue list beside another catalogue list, one searchable and one not, is the inconsistency an
          operator actually trips over. The plan catalogue is small and comes down whole, so the client-side
          `useTableQuery` engine is the right one here (unlike the policy and member registers, which page on
          the server). */}
      <Card>
        <DataTableView
          query={planQuery}
          columns={planColumns}
          rowKey={(r) => r.planId}
          caption={t(S.plans)}
          interactive
          selectedKey={selectedPlan}
          onSelect={(r) => setSelectedPlan(r.planId)}
          loading={plans === null && !error}
          emptyLabel={t(S.noPlans)}
          noMatchesLabel={t(S.noPlanMatches)}
        />
      </Card>

      {!selectedPlan && <InlineAlert tone="info">{t(S.selectPlanFirst)}</InlineAlert>}

      {/* 19.8 — the plan's own record, above its versions. What the plan IS and what is riding on it, before
          the editor that changes what it covers. */}
      {selectedPlan && current && (
        <PlanDetailPane
          plan={detail?.plan ?? PlanAsAdminView(current)}
          book={detail?.book ?? null}
          mayWrite={mayWrite}
          onEdit={() => setForm({ mode: "edit", plan: detail?.plan ?? PlanAsAdminView(current) })}
          onStatus={() => setStatusChange(current.status === "Active" ? "deactivate" : "reactivate")}
          onHistory={() => setHistoryOpen(true)}
        />
      )}

      {selectedPlan && (
        <Card>
          <h3>{t(S.versions)}</h3>
          {/* The version timeline. Superseded versions stay listed and resolvable forever, because a claim
              for care given last March must still be judged by March's rules. */}
          <ol className="pol-versions">
            {versions.map((v) => (
              <li key={v.planVersionId}>
                <Button
                  variant={v.planVersionId === selectedVersion ? "primary" : "ghost"}
                  onClick={() => setSelectedVersion(v.planVersionId)}
                >
                  {t(S.version)} {v.versionNo}
                </Button>
                <StatusChip
                  kind={v.status === "Active" ? "ok" : v.status === "Draft" ? "info" : "neu"}
                  label={enumLabel(v.status)}
                />
                <span>
                  {t(S.window)}: {fmt.date(v.effectiveFrom)} → {v.effectiveTo ? fmt.date(v.effectiveTo) : "—"}
                </span>
              </li>
            ))}
          </ol>
          <Button
            variant="secondary"
            onClick={async () => {
              try {
                await api.amendPlan(selectedPlan, crypto.randomUUID());
                setAnnounce(t(S.amended));
                await loadVersions(selectedPlan);
              } catch (e) {
                setError(writeErrorMessage(e).message);
              }
            }}
          >
            {t(S.amend)}
          </Button>
        </Card>
      )}

      {version && (
        <PlanVersionEditor
          api={api}
          version={version}
          previous={previous}
          categories={categories}
          tiers={activeTiers}
          onChanged={async () => {
            if (selectedPlan) await loadVersions(selectedPlan);
          }}
          onAnnounce={setAnnounce}
        />
      )}

      {form && (
        <PlanForm
          api={api}
          mode={form.mode}
          plan={form.mode === "edit" ? form.plan : null}
          onClose={() => setForm(null)}
          onSaved={async (id, wasCreate) => {
            setForm(null);
            setSelectedPlan(id);
            setAnnounce(t(wasCreate ? S.planCreated : S.planUpdated));
            await reloadPlans(id);
          }}
        />
      )}

      {selectedPlan && current && statusChange && (
        <ReasonDialog
          title={fillLocalized(statusChange === "deactivate" ? S.withdrawTitle : S.returnTitle, current.planCode)}
          body={fillLocalized(statusChange === "deactivate" ? S.withdrawBody : S.returnBody, current.planCode)}
          description={S.reversible}
          confirmLabel={statusChange === "deactivate" ? S.deactivatePlan : S.reactivatePlan}
          onConfirm={async (reason, key) => {
            if (statusChange === "deactivate") await api.deactivatePlan(selectedPlan, reason, key);
            else await api.reactivatePlan(selectedPlan, reason, key);
          }}
          onClose={() => setStatusChange(null)}
          onDone={async () => {
            setStatusChange(null);
            setAnnounce(t(statusChange === "deactivate" ? S.planWithdrawn : S.planReturned));
            await reloadPlans(selectedPlan);
          }}
        />
      )}

      {selectedPlan && current && historyOpen && (
        <HistoryModal
          title={fillLocalized(S.historyTitle, current.planCode)}
          load={() => api.planHistory(selectedPlan)}
          facts={(e) => (
            <>
              <Fact label={t(S.name)} value={e.nameEn} />
              <Fact label={t(S.category)} value={e.category} />
              <Fact label={t(S.status)} value={enumLabel(e.status)} />
            </>
          )}
          onClose={() => setHistoryOpen(false)}
        />
      )}
    </div>
  );
}

/** The list row as the detail shape, for the moment before the detail read lands — so the pane renders
 *  immediately with what the table already knows rather than flashing empty. */
function PlanAsAdminView(r: PlanView): PlanAdminView {
  return {
    planId: r.planId, planCode: r.planCode, nameEn: r.nameEn, nameAr: r.nameAr,
    description: r.description ?? null, category: r.category, status: r.status,
    statusReason: null, statusChangedAt: null, updatedAt: new Date(0).toISOString(), updatedByName: null,
  };
}

// ── The plan's own record ───────────────────────────────────────────────────────────────────────────────

function PlanDetailPane({
  plan, book, mayWrite, onEdit, onStatus, onHistory,
}: {
  plan: PlanAdminView;
  book: PlanBook | null;
  mayWrite: boolean;
  onEdit: () => void;
  onStatus: () => void;
  onHistory: () => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const enumLabel = useEnumLabel();
  const active = plan.status === "Active";

  return (
    <Card>
      <div className="screen-toolbar">
        <div className="pay-head">
          <h3>
            <BiName en={plan.nameEn} ar={plan.nameAr} />{" "}
            <span className="tnum pol-muted">{plan.planCode}</span>
          </h3>
          <div className="pay-chips">
            <StatusChip kind={active ? "ok" : "neu"} label={enumLabel(plan.status)} />
            <span className="pol-muted">{plan.category}</span>
          </div>
        </div>
        <RecordActions
          onHistory={onHistory}
          onEdit={mayWrite ? onEdit : undefined}
          editLabel={S.editPlan}
          status={mayWrite
            ? {
                label: active ? S.deactivatePlan : S.reactivatePlan,
                icon: active ? "lock" : "undo",
                onClick: onStatus,
              }
            : undefined}
        />
      </div>

      {/* A withdrawn plan says WHY on the record, because that is the first thing anybody opening it wants. */}
      {!active && plan.statusReason && (
        <InlineAlert tone="info">
          {plan.statusReason}
          {plan.statusChangedAt ? ` — ${fmt.dateTime(plan.statusChangedAt)}` : ""}
        </InlineAlert>
      )}

      {plan.description && <p>{plan.description}</p>}

      {book && (
        <>
          <KpiList
            items={[
              { label: t(S.versionsTotal), value: fmt.number(book.versionCount) },
              { label: t(S.drafts), value: fmt.number(book.draftCount) },
              { label: t(S.inForceNow), value: fmt.number(book.activeCount) },
              { label: t(S.policiesSelling), value: fmt.number(book.policyCount) },
              { label: t(S.activePolicies), value: fmt.number(book.activePolicyCount) },
              { label: t(S.membersOnPlan), value: fmt.number(book.activeMemberCount) },
            ]}
          />
          <dl className="pol-identity-list">
            <Fact
              label={t(S.sellableWindow)}
              value={book.firstEffectiveFrom
                ? `${fmt.date(book.firstEffectiveFrom)} → ${book.lastEffectiveTo ? fmt.date(book.lastEffectiveTo) : t(S.openEnded)}`
                : "—"}
            />
          </dl>
        </>
      )}

      {plan.updatedByName && (
        <p className="pol-muted">
          {t(S.lastChanged)}: {fmt.dateTime(plan.updatedAt)} {t(fillLocalized(S.by, plan.updatedByName))}
        </p>
      )}
    </Card>
  );
}

// ── Create / edit ───────────────────────────────────────────────────────────────────────────────────────

function PlanForm({
  api, mode, plan, onClose, onSaved,
}: {
  api: PolicyApi;
  mode: "create" | "edit";
  plan: PlanAdminView | null;
  onClose: () => void;
  onSaved: (planId: string, wasCreate: boolean) => void | Promise<void>;
}) {
  const t = useLoc();
  const [key, rotate] = useIdempotencyKey();
  const [planCode, setPlanCode] = useState("");
  const [nameEn, setNameEn] = useState(plan?.nameEn ?? "");
  const [nameAr, setNameAr] = useState(plan?.nameAr ?? "");
  const [description, setDescription] = useState(plan?.description ?? "");
  const [category, setCategory] = useState(plan?.category ?? "Standard");
  const [problem, setProblem] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    if (mode === "create" && !planCode.trim()) { setProblem(S.needPlanCode); return; }
    if (!nameEn.trim() || !nameAr.trim()) { setProblem(S.needPlanNames); return; }
    if (!category.trim()) { setProblem(S.needCategory); return; }

    const body = {
      nameEn: nameEn.trim(),
      nameAr: nameAr.trim(),
      description: description.trim() || null,
      category: category.trim(),
    };

    setBusy(true);
    setProblem(null);
    try {
      const id = mode === "create"
        ? (await api.createPlan({ ...body, planCode: planCode.trim() }, key)).planId
        : (await api.updatePlan(plan!.planId, body)).planId;
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
      title={t(mode === "create" ? S.formCreate : S.formEdit)}
      closeLabel={t(S.cancel)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
          <Button variant="primary" onClick={() => void submit()} loading={busy}>
            {t(mode === "create" ? S.create : S.save)}
          </Button>
        </>
      }
    >
      {problem && <InlineAlert tone="bad">{t(problem)}</InlineAlert>}
      <div className="pay-form-grid">
        {mode === "create" ? (
          <InputField
            label={t(S.planCode)}
            value={planCode}
            onChange={(e) => setPlanCode(e.currentTarget.value)}
            help={t(S.planCodeLocked)}
            required
          />
        ) : (
          <InputField label={t(S.planCode)} value={plan?.planCode ?? ""} readOnly help={t(S.planCodeLocked)} />
        )}
        <InputField label={t(S.category)} value={category} onChange={(e) => setCategory(e.currentTarget.value)} required />
        <InputField label={t(S.nameEn)} value={nameEn} onChange={(e) => setNameEn(e.currentTarget.value)} required />
        <InputField label={t(S.nameAr)} value={nameAr} onChange={(e) => setNameAr(e.currentTarget.value)} required dir="rtl" />
      </div>
      <TextareaField
        label={t(S.planDescription)}
        rows={3}
        value={description}
        onChange={(e) => setDescription(e.currentTarget.value)}
      />
    </Modal>
  );
}

// ── The two-level grid ──────────────────────────────────────────────────────────────────────────────────

interface EditableTier extends BenefitRuleTierInput {
  tierCode: string;
}

interface EditableRule {
  benefitCategoryCode: string;
  isCovered: boolean;
  limitType: string;
  limitValue: string;
  resetPeriod: string;
  waitingPeriodDays: string;
  requiresPreauth: boolean;
  exclusions: string;
  deductible: string;
  deductibleWaived: boolean;
  preauthCostThreshold: string;
  tiers: EditableTier[];
}

function toEditable(
  rules: BenefitRuleView[],
  categories: BenefitCategoryView[],
  tiers: NetworkTierView[],
): EditableRule[] {
  const byCode = new Map(
    rules
      .filter((r) => r.benefitCategoryCode)
      .map((r) => [r.benefitCategoryCode as string, r]),
  );
  // One row per CATEGORY, not per configured rule: the category nobody has priced yet is precisely the row
  // an administrator opens the editor to fill in, and it cannot appear if the rows come from the rule set.
  return categories.map((c) => {
    const r = byCode.get(c.code);
    return {
      benefitCategoryCode: c.code,
      isCovered: r?.isCovered ?? false,
      limitType: r?.limitType ?? "",
      limitValue: r?.limitValue != null ? String(r.limitValue) : "",
      resetPeriod: r?.resetPeriod ?? "None",
      waitingPeriodDays: String(r?.waitingPeriodDays ?? 0),
      requiresPreauth: r?.requiresPreauth ?? false,
      exclusions: r?.exclusions ?? "[]",
      deductible: r?.deductible != null ? String(r.deductible) : "",
      deductibleWaived: r?.deductibleWaived ?? false,
      preauthCostThreshold: r?.preauthCostThreshold != null ? String(r.preauthCostThreshold) : "",
      tiers: tiers.map((tier) => {
        const existing = r?.tiers.find((x) => x.networkTierId === tier.networkTierId);
        return {
          tierCode: tier.tierCode,
          networkTierId: tier.networkTierId,
          isCovered: existing?.isCovered ?? false,
          copayFixed: existing?.copayFixed ?? null,
          copayPercent: existing?.copayPercent ?? null,
          coinsurancePercent: existing?.coinsurancePercent ?? null,
          copayCountsTowardDeductible: existing?.copayCountsTowardDeductible ?? false,
          requiresPreauthOverride: existing?.requiresPreauthOverride ?? null,
          limitMultiplier: existing?.limitMultiplier ?? null,
        };
      }),
    };
  });
}

const num = (s: string): number | null => (s.trim() === "" ? null : Number(s));

function toInput(rules: EditableRule[]): BenefitRuleInput[] {
  return rules.map((r) => ({
    benefitCategoryCode: r.benefitCategoryCode,
    isCovered: r.isCovered,
    limitType: r.limitType || null,
    limitValue: num(r.limitValue),
    resetPeriod: r.resetPeriod,
    deductible: num(r.deductible),
    deductibleWaived: r.deductibleWaived,
    waitingPeriodDays: Number(r.waitingPeriodDays || "0"),
    requiresPreauth: r.requiresPreauth,
    preauthCostThreshold: num(r.preauthCostThreshold),
    exclusions: r.exclusions,
    notes: null,
    tiers: r.tiers.map(({ tierCode: _tierCode, ...rest }) => rest),
  }));
}

function PlanVersionEditor({
  api,
  version,
  previous,
  categories,
  tiers,
  onChanged,
  onAnnounce,
}: {
  api: PolicyApi;
  version: PlanVersionView;
  previous: PlanVersionView | null;
  categories: BenefitCategoryView[];
  tiers: NetworkTierView[];
  onChanged: () => Promise<void>;
  onAnnounce: (s: string) => void;
}) {
  const t = useLoc();
  const enumLabel = useEnumLabel();
  const [rules, setRules] = useState<EditableRule[]>(() => toEditable(version.rules, categories, tiers));
  const [expanded, setExpanded] = useState<string | null>(null);
  const [problems, setProblems] = useState<ActivationProblem[] | null>(null);
  const [valid, setValid] = useState(false);
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  const [saveKey, rotateSaveKey] = useIdempotencyKey();
  const [activateKey, rotateActivateKey] = useIdempotencyKey();

  useEffect(() => {
    setRules(toEditable(version.rules, categories, tiers));
    setProblems(null);
    setValid(false);
  }, [version, categories, tiers]);

  const editable = version.editable;

  function patch(code: string, change: Partial<EditableRule>) {
    setRules((prev) => prev.map((r) => (r.benefitCategoryCode === code ? { ...r, ...change } : r)));
  }
  function patchTier(code: string, tierId: string, change: Partial<EditableTier>) {
    setRules((prev) =>
      prev.map((r) =>
        r.benefitCategoryCode === code
          ? { ...r, tiers: r.tiers.map((x) => (x.networkTierId === tierId ? { ...x, ...change } : x)) }
          : r,
      ),
    );
  }

  async function save() {
    setBusy(true);
    setError(null);
    try {
      await api.setPlanRules(version.planVersionId, toInput(rules), saveKey);
      rotateSaveKey();
      onAnnounce(t(S.saved));
      await onChanged();
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  async function validate() {
    setBusy(true);
    setError(null);
    try {
      const r = await api.validatePlanVersion(version.planVersionId);
      setProblems(r.problems);
      setValid(r.valid);
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  async function activate() {
    setBusy(true);
    setError(null);
    try {
      await api.activatePlanVersion(version.planVersionId, activateKey);
      rotateActivateKey();
      onAnnounce(t(S.activated));
      await onChanged();
    } catch (e) {
      setError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card data-testid="plan-version-editor">
      <div className="pol-editor-head">
        <h3>
          {t(S.version)} {version.versionNo}
        </h3>
        {editable ? (
          <StatusChip kind="info" label={t(S.draft)} />
        ) : (
          <StatusChip kind="neu" label={t(S.immutable)} />
        )}
      </div>

      {!editable && (
        // The explicit "immutable — amend to change" affordance the acceptance criterion asks for. It says
        // WHY, and it offers the only legitimate way forward rather than leaving a disabled form.
        <InlineAlert tone="info" data-testid="immutable-notice">
          {t(S.immutableHint)}
        </InlineAlert>
      )}

      {tiers.length === 0 && <InlineAlert tone="warn">{t(S.noTiers)}</InlineAlert>}
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {/*
        WRAPPED, and the wrapper is not decoration.
        `.pol-grid` is `display: block; overflow-x: auto` — a table that is its own scrollport. That buys the
        horizontal scroll and costs the table its layout: the browser wraps the rows in an anonymous
        shrink-to-fit table, so `width: 100%` applies to the block box while the columns size to their own
        content. `.pol-tablewrap > .pol-grid` resets it to a real table and takes the scroll onto the wrapper,
        which is what the other 11 call sites of these two classes already do — these two were the only ones
        that never got one.
        The wrapper is also what makes the pane reachable: a region a pointer can scroll and a keyboard
        cannot is WCAG 2.1.1, and `tabIndex` + `.mrs-scroll-focusable` is the treatment every other scrolling
        pane in the product carries. `.mrs-scroll` brings the house scrollbar with it.
        It is a prerequisite for the pickers inside this table becoming design-system comboboxes: an ancestor
        with `overflow-x: auto` is a clipping context on BOTH axes (CSS Overflow §3 — `visible` computes to
        `auto` when the other axis is not `visible`), so an option list opened in here would have been cut off.
      */}
      <div className="pol-tablewrap mrs-scroll mrs-scroll-focusable" tabIndex={0}>
      <table className="pol-grid">
        <caption className="sr-only">{t(S.plans)}</caption>
        <thead>
          <tr>
            <th scope="col">{t(S.category)}</th>
            <th scope="col">{t(S.covered)}</th>
            <th scope="col">{t(S.limitType)}</th>
            <th scope="col">{t(S.limit)}</th>
            <th scope="col">{t(S.reset)}</th>
            <th scope="col">{t(S.waiting)}</th>
            <th scope="col">{t(S.preauth)}</th>
            <th scope="col">{t(S.exclusions)}</th>
            <th scope="col">{t(S.expand)}</th>
          </tr>
        </thead>
        <tbody>
          {rules.map((r) => {
            const unpriced = r.isCovered && r.tiers.some((x) => !x.isCovered && x.copayFixed == null && x.coinsurancePercent == null);
            const open = expanded === r.benefitCategoryCode;
            return [
              <tr key={r.benefitCategoryCode}>
                <th scope="row">
                  {r.benefitCategoryCode}
                  {unpriced && <StatusChip kind="warn" label={t(S.unpriced)} />}
                </th>
                <td>
                  <input
                    type="checkbox"
                    className="mrs-checkbox"
                    aria-label={`${t(S.covered)} — ${r.benefitCategoryCode}`}
                    checked={r.isCovered}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { isCovered: e.target.checked })}
                  />
                </td>
                <td>
                  {/* These two carried NO class at all — the browser's untouched default control, in a table
                      of Mersal-styled inputs. Safe to convert only after step 7 wrapped the table: an
                      ancestor with `overflow-x: auto` clips on both axes, and a native popup was escaping it
                      only because the OS draws it outside the page. */}
                  <Combobox
                    aria-label={`${t(S.limitType)} — ${r.benefitCategoryCode}`}
                    value={r.limitType || null}
                    disabled={!editable}
                    placeholder="—"
                    onChange={(v) => patch(r.benefitCategoryCode, { limitType: v })}
                    options={LIMIT_TYPES.filter(Boolean).map((x) => ({ value: x, label: enumLabel(x) }))}
                  />
                </td>
                <td>
                  <input
                    className="mrs-control"
                    inputMode="decimal"
                    aria-label={`${t(S.limit)} — ${r.benefitCategoryCode}`}
                    value={r.limitValue}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { limitValue: e.target.value })}
                  />
                </td>
                <td>
                  <Combobox
                    aria-label={`${t(S.reset)} — ${r.benefitCategoryCode}`}
                    value={r.resetPeriod}
                    disabled={!editable}
                    onChange={(v) => patch(r.benefitCategoryCode, { resetPeriod: v })}
                    options={RESET_PERIODS.map((x) => ({ value: x, label: enumLabel(x) }))}
                  />
                </td>
                <td>
                  <input
                    className="mrs-control"
                    inputMode="numeric"
                    aria-label={`${t(S.waiting)} — ${r.benefitCategoryCode}`}
                    value={r.waitingPeriodDays}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { waitingPeriodDays: e.target.value })}
                  />
                </td>
                <td>
                  <input
                    type="checkbox"
                    className="mrs-checkbox"
                    aria-label={`${t(S.preauth)} — ${r.benefitCategoryCode}`}
                    checked={r.requiresPreauth}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { requiresPreauth: e.target.checked })}
                  />
                </td>
                <td>
                  <input
                    className="mrs-control"
                    aria-label={`${t(S.exclusions)} — ${r.benefitCategoryCode}`}
                    value={r.exclusions}
                    disabled={!editable}
                    onChange={(e) => patch(r.benefitCategoryCode, { exclusions: e.target.value })}
                  />
                </td>
                <td>
                  <Button
                    variant="ghost"
                    aria-expanded={open}
                    onClick={() => setExpanded(open ? null : r.benefitCategoryCode)}
                  >
                    {t(S.expand)}
                  </Button>
                </td>
              </tr>,
              open ? (
                <tr key={`${r.benefitCategoryCode}-tiers`} className="pol-grid-sub">
                  <td colSpan={9}>
                    <fieldset>
                      <legend>
                        {t(S.costShare)} — {r.benefitCategoryCode}
                      </legend>
                      {unpriced && <InlineAlert tone="warn">{t(S.unpricedHint)}</InlineAlert>}
                      {/* Same treatment, same reasons — one column per tier, so this is the wider of the two. */}
                      <div className="pol-tablewrap mrs-scroll mrs-scroll-focusable" tabIndex={0}>
                      <table className="pol-costshare">
                        <thead>
                          <tr>
                            <th scope="col">{t(S.type)}</th>
                            {r.tiers.map((x) => (
                              <th key={x.networkTierId} scope="col">
                                {x.tierCode}
                              </th>
                            ))}
                          </tr>
                        </thead>
                        <tbody>
                          <tr>
                            <th scope="row">{t(S.covered)}</th>
                            {r.tiers.map((x) => (
                              <td key={x.networkTierId}>
                                <input
                                  type="checkbox"
                                  className="mrs-checkbox"
                                  aria-label={`${t(S.covered)} — ${r.benefitCategoryCode} — ${x.tierCode}`}
                                  checked={x.isCovered}
                                  disabled={!editable}
                                  onChange={(e) =>
                                    patchTier(r.benefitCategoryCode, x.networkTierId, { isCovered: e.target.checked })
                                  }
                                />
                              </td>
                            ))}
                          </tr>
                          <tr>
                            <th scope="row">{t(S.copay)}</th>
                            {r.tiers.map((x) => (
                              <td key={x.networkTierId}>
                                <input
                                  className="mrs-control"
                                  inputMode="decimal"
                                  aria-label={`${t(S.copay)} — ${r.benefitCategoryCode} — ${x.tierCode}`}
                                  value={x.copayFixed ?? ""}
                                  disabled={!editable}
                                  onChange={(e) =>
                                    patchTier(r.benefitCategoryCode, x.networkTierId, {
                                      copayFixed: e.target.value === "" ? null : Number(e.target.value),
                                    })
                                  }
                                />
                              </td>
                            ))}
                          </tr>
                          <tr>
                            <th scope="row">{t(S.coinsurance)}</th>
                            {r.tiers.map((x) => (
                              <td key={x.networkTierId}>
                                <input
                                  className="mrs-control"
                                  inputMode="decimal"
                                  aria-label={`${t(S.coinsurance)} — ${r.benefitCategoryCode} — ${x.tierCode}`}
                                  value={x.coinsurancePercent ?? ""}
                                  disabled={!editable}
                                  onChange={(e) =>
                                    patchTier(r.benefitCategoryCode, x.networkTierId, {
                                      coinsurancePercent: e.target.value === "" ? null : Number(e.target.value),
                                    })
                                  }
                                />
                              </td>
                            ))}
                          </tr>
                        </tbody>
                      </table>
                      </div>
                    </fieldset>
                  </td>
                </tr>
              ) : null,
            ];
          })}
        </tbody>
      </table>
      </div>

      <div className="pol-editor-actions">
        {editable && (
          <Button variant="primary"
              leadingIcon={<Icon name="check2" />} onClick={save} disabled={busy}>
            {t(S.saveRules)}
          </Button>
        )}
        <Button variant="secondary" onClick={validate} disabled={busy}>
          {t(S.validate)}
        </Button>
        {editable && (
          <Button variant="primary"
              leadingIcon={<Icon name="toggle" />} onClick={activate} disabled={busy || !valid}>
            {t(S.activate)}
          </Button>
        )}
      </div>

      {problems !== null && (
        <div aria-live="polite" data-testid="validation-result">
          {valid ? (
            <InlineAlert tone="ok">{t(S.valid)}</InlineAlert>
          ) : (
            <ul className="pol-problems">
              {problems.map((p) => (
                <li key={`${p.code}-${p.detail}`}>
                  <StatusChip kind="bad" label={p.code} /> {p.detail}
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      <VersionDiff current={version} previous={previous} />
    </Card>
  );
}

// ── Version diff ────────────────────────────────────────────────────────────────────────────────────────

interface DiffLine {
  category: string;
  kind: "added" | "removed" | "changed";
  detail: string;
}

/** Compare two rule sets field by field. Rendered rather than left to the reader because "what did this
 *  amendment actually change" is the question every plan amendment is reviewed on, and diffing two screens
 *  by eye is how a changed waiting period ships unnoticed. */
export function diffRules(current: BenefitRuleView[], previous: BenefitRuleView[]): DiffLine[] {
  const key = (r: BenefitRuleView) => r.benefitCategoryCode ?? r.benefitCategoryId;
  const prev = new Map(previous.map((r) => [key(r), r]));
  const lines: DiffLine[] = [];

  for (const r of current) {
    const k = key(r);
    const p = prev.get(k);
    if (!p) {
      lines.push({ category: k, kind: "added", detail: r.isCovered ? "covered" : "not covered" });
      continue;
    }
    prev.delete(k);
    const changes: string[] = [];
    if (p.isCovered !== r.isCovered) changes.push(`covered ${p.isCovered} → ${r.isCovered}`);
    if (p.limitValue !== r.limitValue) changes.push(`limit ${p.limitValue ?? "—"} → ${r.limitValue ?? "—"}`);
    if (p.limitType !== r.limitType) changes.push(`limit type ${p.limitType ?? "—"} → ${r.limitType ?? "—"}`);
    if (p.resetPeriod !== r.resetPeriod) changes.push(`reset ${p.resetPeriod} → ${r.resetPeriod}`);
    if (p.waitingPeriodDays !== r.waitingPeriodDays)
      changes.push(`waiting ${p.waitingPeriodDays}d → ${r.waitingPeriodDays}d`);
    if (p.requiresPreauth !== r.requiresPreauth)
      changes.push(`pre-auth ${p.requiresPreauth} → ${r.requiresPreauth}`);
    if (p.deductible !== r.deductible) changes.push(`deductible ${p.deductible ?? "—"} → ${r.deductible ?? "—"}`);
    for (const tier of r.tiers) {
      const pt = p.tiers.find((x) => x.networkTierId === tier.networkTierId);
      if (!pt) {
        changes.push(`tier ${tier.tierCode} added`);
        continue;
      }
      if (pt.copayFixed !== tier.copayFixed)
        changes.push(`${tier.tierCode} co-pay ${pt.copayFixed ?? "—"} → ${tier.copayFixed ?? "—"}`);
      if (pt.coinsurancePercent !== tier.coinsurancePercent)
        changes.push(`${tier.tierCode} co-insurance ${pt.coinsurancePercent ?? "—"} → ${tier.coinsurancePercent ?? "—"}`);
      if (pt.isCovered !== tier.isCovered) changes.push(`${tier.tierCode} covered ${pt.isCovered} → ${tier.isCovered}`);
    }
    if (changes.length > 0) lines.push({ category: k, kind: "changed", detail: changes.join("; ") });
  }
  for (const [k, p] of prev) {
    lines.push({ category: k, kind: "removed", detail: p.isCovered ? "was covered" : "was not covered" });
  }
  return lines;
}

function VersionDiff({ current, previous }: { current: PlanVersionView; previous: PlanVersionView | null }) {
  const t = useLoc();
  const lines = useMemo(() => (previous ? diffRules(current.rules, previous.rules) : []), [current, previous]);
  const label = { added: S.added, removed: S.removed, changed: S.changed } as const;

  return (
    <section data-testid="version-diff">
      <h4>{t(S.diff)}</h4>
      {!previous && <InlineAlert tone="info">{t(S.noDiff)}</InlineAlert>}
      {previous && lines.length === 0 && <InlineAlert tone="info">{t(S.unchanged)}</InlineAlert>}
      <ul className="pol-diff">
        {lines.map((l) => (
          <li key={`${l.category}-${l.kind}-${l.detail}`}>
            <StatusChip kind={l.kind === "removed" ? "bad" : l.kind === "added" ? "ok" : "info"} label={t(label[l.kind])} />
            <strong>{l.category}</strong> — {l.detail}
          </li>
        ))}
      </ul>
    </section>
  );
}

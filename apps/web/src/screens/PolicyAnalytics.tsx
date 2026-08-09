import { useCallback, useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Button, Card, DataTable, InlineAlert, InputField, SelectField, StatusChip, Tabs, useTheme } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type {
  AnalyticsDelta,
  AnalyticsFilters,
  AnalyticsSeries,
  AnalyticsViewResult,
  OutlierRow,
  PolicyApi,
} from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";
import { writeErrorMessage } from "../api/writeError";
import { PageHeader, useLoc, readErrorMessage } from "./_shared";
import { useApi } from "../api/ApiProvider";
import { useFormat } from "../i18n/useFormat";
// 19.7 — the scope explorer moved in here when its own nav item was retired. Lazy, because it pulls the
// policy list and the CSV export path, and most visits to Analytics never open this tab.
import { UtilizationScreen } from "./PolicyBook";

/**
 * Phase 19.6b — the analytical layer over everything 19.1–19.5b produced.
 *
 * SIX VIEWS, ONE FILTER BAR, AND THE FILTERS LIVE IN THE URL
 * ---------------------------------------------------------
 * Not a convenience. "Look at this" is how a finding gets escalated, and a link that drops its filters sends
 * the recipient to a different number under the same title — which is worse than sending them nothing. So the
 * filter bar reads and writes `useSearchParams`, and the URL is the single source of truth for what is being
 * shown. Switching tabs keeps the filters; sharing the link carries them.
 *
 * EVERY CHART SHIPS ITS DATA TABLE, ALWAYS
 * ----------------------------------------
 * The R2 audit finding (U6) is that a table behind a default-off toggle is not an alternative — it is a
 * feature nobody finds. The bars here are `aria-hidden` decoration; the table is the content, unconditionally
 * in the DOM, with the server's own one-line summary above it. There is no toggle to leave switched off.
 *
 * THE NUMBERS COME FROM A READ MODEL, NOT FROM THE BENEFIT SPINE
 * -------------------------------------------------------------
 * Every figure here is served by reporting-service from pre-aggregated facts. The dashboard never queries the
 * transactional tables a reception desk is checking eligibility against, and it never receives a name — a
 * drill-down returns ids, and resolving a person is a separate, audited call.
 */

const S = {
  title: { en: "Analytics", ar: "التحليلات" },
  tabEnrolment: { en: "Enrolment", ar: "التسجيل" },
  tabUtilization: { en: "Utilization", ar: "الاستهلاك" },
  tabFinancial: { en: "Financial", ar: "المالية" },
  tabNetwork: { en: "Network", ar: "الشبكة" },
  tabPlans: { en: "Plan comparison", ar: "مقارنة الخطط" },
  tabOutliers: { en: "Outliers & data quality", ar: "الحالات الشاذة وجودة البيانات" },
  // Distinct from `tabUtilization`, which is the ANALYTICAL cut. This one is the operational explorer:
  // pick a policy, group, plan or payer and take the numbers away as a CSV.
  tabScope: { en: "Utilization by scope", ar: "الاستخدام حسب النطاق" },

  filters: { en: "Filters", ar: "عوامل التصفية" },
  from: { en: "From", ar: "من" },
  to: { en: "To", ar: "إلى" },
  asOf: { en: "As of", ar: "كما في تاريخ" },
  asOfHint: {
    en: "A range asks what happened during it; an as-of date asks what the book looked like on that day.",
    ar: "النطاق يسأل عمّا حدث خلاله؛ وتاريخ «كما في» يسأل عن حالة السجل في ذلك اليوم.",
  },
  payer: { en: "Payer", ar: "الجهة الممولة" },
  policy: { en: "Policy", ar: "الوثيقة" },
  plan: { en: "Plan", ar: "الخطة" },
  anyValue: { en: "Any", ar: "الكل" },
  pickPolicyFirst: { en: "Choose a policy first", ar: "اختر وثيقة أولًا" },
  referenceFailed: {
    en: "Some filter lists could not be loaded. The figures below are unaffected — only the narrowing you can apply here.",
    ar: "تعذّر تحميل بعض قوائم التصفية. الأرقام أدناه غير متأثرة — التأثير على ما يمكنك تضييقه هنا فقط.",
  },
  group: { en: "Group", ar: "المجموعة" },
  branch: { en: "Branch", ar: "الفرع" },
  tier: { en: "Network tier", ar: "شريحة الشبكة" },
  category: { en: "Benefit category", ar: "فئة المنفعة" },
  status: { en: "Member status", ar: "حالة العضو" },
  relationship: { en: "Relationship", ar: "صلة القرابة" },
  band: { en: "Utilization band", ar: "شريحة الاستهلاك" },
  clear: { en: "Clear filters", ar: "مسح عوامل التصفية" },
  compare: { en: "Compare with previous period", ar: "قارن بالفترة السابقة" },
  comparing: { en: "Comparing with the preceding period of the same length.", ar: "المقارنة مع الفترة السابقة بنفس الطول." },
  export: { en: "Export this view", ar: "تصدير هذا العرض" },
  exported: { en: "Export downloaded and recorded in the audit log.", ar: "تم تنزيل التصدير وتسجيله في سجل التدقيق." },

  loading: { en: "Loading…", ar: "جارٍ التحميل…" },
  empty: { en: "No data for the selected filters.", ar: "لا توجد بيانات ضمن عوامل التصفية المحددة." },
  payerScoped: {
    en: "These figures cover only the payers you are assigned to — they are not the whole programme.",
    ar: "تغطي هذه الأرقام الجهات الممولة المسندة إليك فقط — وليست البرنامج بأكمله.",
  },
  unavailable: { en: "Some figures could not be composed:", ar: "تعذّر تجميع بعض الأرقام:" },

  plansToCompare: { en: "Plans to compare (comma-separated ids)", ar: "الخطط المراد مقارنتها (معرّفات مفصولة بفواصل)" },
  plansHint: {
    en: "Pick the shortlist. Cost per member is the only honest comparison — a plan with 12 expensive members and one with 4,000 cheap ones have similar totals and nothing else in common.",
    ar: "اختر القائمة المختصرة. التكلفة لكل عضو هي المقارنة الوحيدة الصادقة — خطة بها ١٢ عضوًا مرتفع التكلفة وأخرى بها ٤٠٠٠ عضو منخفض التكلفة قد تتشابه إجمالياتهما ولا تتشابه في شيء آخر.",
  },

  drillTitle: { en: "Members in this band", ar: "الأعضاء ضمن هذه الشريحة" },
  drillHint: {
    en: "Member numbers are resolved separately, and that read is recorded in the audit log.",
    ar: "تُستخرج أرقام الأعضاء بشكل منفصل، ويُسجَّل ذلك الاطلاع في سجل التدقيق.",
  },
  drillClose: { en: "Close", ar: "إغلاق" },
  memberRef: { en: "Membership", ar: "العضوية" },
  limit: { en: "Limit", ar: "الحد" },
  consumed: { en: "Consumed", ar: "المستهلك" },

  deltaUp: { en: "Up", ar: "ارتفاع" },
  deltaDown: { en: "Down", ar: "انخفاض" },
  deltaFlat: { en: "No change", ar: "بدون تغيير" },
  vsPrevious: { en: "vs previous", ar: "مقارنة بالسابق" },
} satisfies Record<string, Localized>;

const VIEWS = [
  { key: "enrolment", label: S.tabEnrolment },
  { key: "utilization", label: S.tabUtilization },
  { key: "financial", label: S.tabFinancial },
  { key: "network", label: S.tabNetwork },
  { key: "plancomparison", label: S.tabPlans },
  { key: "outliers", label: S.tabOutliers },
  // Was its own nav section until the beneficiary portal was reordered. It belongs beside the other
  // figures about a cohort rather than in a menu of its own, and nothing about it was lost in the move.
  { key: "scope", label: S.tabScope },
] as const;

/** The filter keys carried in the URL. Listed once so "clear" and "read" cannot drift apart — a clear that
 *  missed a key would leave an invisible narrowing applied to every subsequent view. */
const FILTER_KEYS = [
  "payerId", "policyId", "policyPlanId", "groupId", "branchId",
  "tier", "category", "status", "relationship", "band",
  "from", "to", "asOf", "plans", "compare",
] as const;

export function PolicyAnalytics({ api }: { api?: PolicyApi } = {}) {
  const client = useMemo(() => api ?? createHttpPolicyApi(), [api]);
  const t = useLoc();
  const [params, setParams] = useSearchParams();
  const [view, setView] = useState<string>(VIEWS[0].key);

  const filters = useMemo<AnalyticsFilters>(() => {
    const out: Record<string, string> = {};
    for (const key of FILTER_KEYS) {
      const value = params.get(key);
      if (value) out[key] = value;
    }
    return out as AnalyticsFilters;
  }, [params]);

  const setFilter = useCallback(
    (key: string, value: string) => {
      const next = new URLSearchParams(params);
      if (value) next.set(key, value);
      else next.delete(key);
      setParams(next, { replace: true });
    },
    [params, setParams],
  );

  const clearFilters = useCallback(() => {
    const next = new URLSearchParams(params);
    for (const key of FILTER_KEYS) next.delete(key);
    setParams(next, { replace: true });
  }, [params, setParams]);

  return (
    <div className="pol-screen">
      {/*
        * The subtitle is gone. It read "Aggregates over the policy and membership book. No clinical data
        * appears in any view." — a true sentence that nobody needed twice: the first half restates the page
        * title, and the second is a guarantee about the SERVER's projection which no operator can act on and
        * which reads, to the one person who might worry about it, as a claim rather than a control. It cost a
        * full row above the filters on every visit. The space goes to the filters and the first view.
        */}
      <PageHeader title={t(S.title)} />

      <FilterBar api={client} filters={filters} onChange={setFilter} onClear={clearFilters} />

      <Tabs
        aria-label={t(S.title)}
        value={view}
        onValueChange={setView}
        items={VIEWS.map((v) => ({
          value: v.key,
          label: t(v.label),
          // Each panel gates its own fetch: `Tabs` force-mounts every panel so hidden content stays available
          // to assistive tech, which means six views would otherwise fire six requests on first paint.
          content: v.key === "scope"
            // Mounted only when open. `Tabs` force-mounts every panel so hidden content stays available to
            // assistive tech, and this one fetches the policy list on mount — six views would otherwise
            // fire six requests on first paint.
            ? (view === "scope" ? <UtilizationScreen api={client} embedded /> : null)
            : <ViewPanel api={client} view={v.key} active={view === v.key} filters={filters} onFilter={setFilter} />,
        }))}
      />
    </div>
  );
}

// ── Filter bar ──────────────────────────────────────────────────────────────────────────────────────────

/** The enum vocabularies the dashboard narrows by. The same tokens the server's `AnalyticsFilter` parses —
 *  `MemberStatus`, `Relationship`, `UtilizationBand` — so an option can never be one the query rejects. */
const MEMBER_STATUSES: Record<string, Localized> = {
  Active: { en: "Active", ar: "نشط" },
  Terminated: { en: "Terminated", ar: "منتهٍ" },
  Cancelled: { en: "Cancelled", ar: "ملغى" },
};

const RELATIONSHIPS: Record<string, Localized> = {
  Principal: { en: "Principal", ar: "المشترك الرئيسي" },
  Spouse: { en: "Spouse", ar: "الزوج/الزوجة" },
  Child: { en: "Child", ar: "ابن/ابنة" },
  Dependent: { en: "Dependent", ar: "معال" },
};

/**
 * All SIX bands, not the four the roster filters by.
 *
 * `Zero` and `Unlimited` are the two the domain warns about most (`libs/benefit-pricing`: "an unlimited
 * benefit reported as 0% invites 'plenty left' on something that was never metered"), and "who has used
 * NOTHING all year" is a question this dashboard exists to answer — a member in that band is healthy,
 * unaware of their entitlement, or wrongly enrolled, and only the third is findable this way.
 */
const BANDS = ["Zero", "Low", "Medium", "High", "Exhausted", "Unlimited"] as const;

const BAND_LABELS: Record<(typeof BANDS)[number], Localized> = {
  Zero: { en: "Nothing used", ar: "لم يُستخدم شيء" },
  Low: { en: "Low (under 50%)", ar: "منخفض (أقل من ٥٠٪)" },
  Medium: { en: "Medium (50–80%)", ar: "متوسط (٥٠–٨٠٪)" },
  High: { en: "High (80–100%)", ar: "مرتفع (٨٠–١٠٠٪)" },
  Exhausted: { en: "At or over the limit", ar: "بلغ الحد أو تجاوزه" },
  Unlimited: { en: "Unlimited", ar: "بلا حد" },
};

/**
 * One reference read, reduced to "the list, or nothing".
 *
 * A thunk rather than a promise so a SYNCHRONOUS throw is caught too. `Promise.all([f().catch(…)])` only
 * handles rejection — if the call itself throws before returning a promise, `.catch` is never reached and the
 * whole batch rejects, so one broken lookup empties five working pickers.
 */
async function safe<T>(read: () => Promise<T>): Promise<T | null> {
  try {
    return await read();
  } catch {
    return null;
  }
}

interface ReferenceLists {
  payers: { value: string; label: string }[];
  policies: { value: string; label: string }[];
  plans: { value: string; label: string }[];
  groups: { value: string; label: string }[];
  branches: { value: string; label: string }[];
  tiers: { value: string; label: string }[];
  categories: { value: string; label: string }[];
  failed: boolean;
}

const NO_REFERENCE: ReferenceLists = {
  payers: [], policies: [], plans: [], groups: [], branches: [], tiers: [], categories: [], failed: false,
};

/**
 * The lists behind the pickers.
 *
 * Fetched once for the flat sets and again per policy for the two that hang off one. `failed` is a single
 * flag rather than a per-list error: from the operator's side the consequence is identical — a filter they
 * cannot use — and six separate warnings above one bar is not six times as useful.
 *
 * A failure leaves the lists EMPTY rather than falling back to free text. A picker that silently becomes a
 * uuid box is the worse of the two states: it looks like it works.
 */
function useAnalyticsReference(api: PolicyApi, policyId?: string): ReferenceLists {
  const { lang } = useTheme();
  const core = useApi();
  const [flat, setFlat] = useState<Omit<ReferenceLists, "plans" | "groups">>(NO_REFERENCE);
  const [scoped, setScoped] = useState<Pick<ReferenceLists, "plans" | "groups">>({ plans: [], groups: [] });

  const bi = useCallback(
    (en: string, ar: string) => (lang === "ar" ? ar || en : en || ar),
    [lang],
  );

  useEffect(() => {
    let live = true;
    void (async () => {
      const [payers, policies, branches, tiers, categories] = await Promise.all([
        safe(() => api.payers()),
        safe(() => api.policyQuery({ pageSize: 200 })),
        safe(() => core.branches()),
        safe(() => api.networkTiers()),
        safe(() => api.benefitCategories()),
      ]);
      if (!live) return;
      setFlat({
        payers: (payers ?? []).map((p) => ({ value: p.payerId, label: bi(p.nameEn, p.nameAr) })),
        // A policy is known by its NUMBER, which is what appears on the paperwork and in every other screen.
        policies: (policies?.items ?? []).map((p) => ({ value: p.policyId, label: p.policyNo })),
        branches: (branches ?? []).map((b) => ({ value: b.id, label: bi(b.name.en, b.name.ar) })),
        tiers: (tiers ?? []).map((x) => ({ value: x.tierCode, label: bi(x.nameEn, x.nameAr) })),
        categories: (categories ?? []).map((c) => ({ value: c.code, label: c.name })),
        failed: [payers, policies, branches, tiers, categories].some((r) => r === null),
      });
    })();
    return () => { live = false; };
  }, [api, core, bi]);

  useEffect(() => {
    if (!policyId) { setScoped({ plans: [], groups: [] }); return; }
    let live = true;
    void (async () => {
      const [plans, groups] = await Promise.all([
        safe(() => api.policyPlans(policyId)),
        safe(() => api.policyGroups(policyId)),
      ]);
      if (!live) return;
      setScoped({
        plans: (plans ?? []).map((p) => ({ value: p.policyPlanId, label: p.planLabel })),
        groups: (groups ?? []).map((g) => ({ value: g.groupId, label: bi(g.nameEn, g.nameAr) })),
      });
    })();
    return () => { live = false; };
  }, [api, policyId, bi]);

  return useMemo(() => ({ ...flat, ...scoped }), [flat, scoped]);
}

/**
 * Nine of the twelve filters used to be free-text boxes.
 *
 * Four of them (`payerId`, `policyId`, `policyPlanId`, `groupId`, `branchId`) are UUID columns, so using the
 * dashboard's own narrowing meant typing a v7 uuid from memory — nobody can, and the audit (§5.1) recorded
 * the whole bar as "blank bordered boxes ... nothing saying they are pickers". The other five take exact
 * enum tokens (`High`, `Principal`, `Terminated`), where a near miss is not an error but an empty chart that
 * reads as "no data for this period".
 *
 * So every filter over a KNOWN set is now a picker, and the sets come from the API — the same payer, plan,
 * group, tier and category lists the rest of the portal is built from, resolved for this caller. A bundled
 * catalogue would show a payer somebody is not assigned to, which is both wrong and a small disclosure.
 *
 * The three dates stay native: a date has no list.
 */
function FilterBar({
  api,
  filters,
  onChange,
  onClear,
}: {
  api: PolicyApi;
  filters: AnalyticsFilters;
  onChange: (key: string, value: string) => void;
  onClear: () => void;
}) {
  const t = useLoc();
  const reference = useAnalyticsReference(api, filters.policyId);

  const date = (key: keyof AnalyticsFilters, label: Localized) => (
    <InputField
      type="date"
      label={t(label)}
      value={filters[key] ?? ""}
      onChange={(e) => onChange(key, e.target.value)}
    />
  );

  /** A filter over a known set. `any` is an option, not a blank — "no narrowing" is a choice, not an absence. */
  const pick = (
    key: keyof AnalyticsFilters,
    label: Localized,
    options: ReadonlyArray<{ value: string; label: string }>,
    opts: { disabled?: boolean; help?: string } = {},
  ) => (
    <SelectField
      label={t(label)}
      value={filters[key] ?? ""}
      onChange={(v) => onChange(key, v)}
      disabled={opts.disabled}
      help={opts.help}
      options={[{ value: "", label: t(S.anyValue) }, ...options]}
    />
  );

  const enumOptions = (labels: Record<string, Localized>) =>
    Object.entries(labels).map(([value, label]) => ({ value, label: t(label) }));

  // The plan and the group both belong TO a policy, so neither list exists until one is chosen. Rendering
  // them enabled-and-empty would offer a control that can only disappoint; disabled with the reason is the
  // honest state, and it also explains the ordering of the bar.
  const policyChosen = Boolean(filters.policyId);
  const needsPolicy = policyChosen ? undefined : t(S.pickPolicyFirst);

  return (
    <Card className="pol-filterbar" aria-label={t(S.filters)}>
      <h2 className="panel-h">{t(S.filters)}</h2>
      <div className="pol-filtergrid">
        {date("from", S.from)}
        {date("to", S.to)}
        {date("asOf", S.asOf)}
        {pick("payerId", S.payer, reference.payers)}
        {pick("policyId", S.policy, reference.policies)}
        {pick("policyPlanId", S.plan, reference.plans, { disabled: !policyChosen, help: needsPolicy })}
        {pick("groupId", S.group, reference.groups, { disabled: !policyChosen, help: needsPolicy })}
        {pick("branchId", S.branch, reference.branches)}
        {pick("tier", S.tier, reference.tiers)}
        {pick("category", S.category, reference.categories)}
        {pick("status", S.status, enumOptions(MEMBER_STATUSES))}
        {pick("relationship", S.relationship, enumOptions(RELATIONSHIPS))}
        {pick("band", S.band, BANDS.map((v) => ({ value: v, label: t(BAND_LABELS[v]) })))}
      </div>
      {reference.failed && <InlineAlert tone="warn">{t(S.referenceFailed)}</InlineAlert>}
      <InlineAlert tone="info">{t(S.asOfHint)}</InlineAlert>
      <div className="pol-filteractions">
        <label className="pol-check">
          <input
            type="checkbox"
            checked={filters.compare === "1"}
            onChange={(e) => onChange("compare", e.target.checked ? "1" : "")}
          />
          {t(S.compare)}
        </label>
        <Button variant="ghost" onClick={onClear}>
          {t(S.clear)}
        </Button>
      </div>
    </Card>
  );
}

// ── One view ────────────────────────────────────────────────────────────────────────────────────────────

function ViewPanel({
  api,
  view,
  active,
  filters,
  onFilter,
}: {
  api: PolicyApi;
  view: string;
  active: boolean;
  filters: AnalyticsFilters;
  onFilter: (key: string, value: string) => void;
}) {
  const t = useLoc();
  const [result, setResult] = useState<AnalyticsViewResult | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  const [announce, setAnnounce] = useState("");
  const [drill, setDrill] = useState<{ band: string; rows: OutlierRow[] } | null>(null);

  useEffect(() => {
    if (!active) return;
    let live = true;
    setBusy(true);
    setError(null);
    api
      .analytics(view, filters)
      .then((r) => live && setResult(r))
      .catch((e: unknown) => {
        if (!live) return;
        setResult(null);
        setError(writeErrorMessage(e).message);
      })
      .finally(() => live && setBusy(false));
    return () => {
      live = false;
    };
  }, [api, view, active, filters]);

  async function exportView() {
    try {
      const csv = await api.analyticsExport(view, filters);
      download(`${view}.csv`, csv);
      setAnnounce(t(S.exported));
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }

  async function openDrill(band: string) {
    try {
      setDrill({ band, rows: await api.analyticsOutlierMembers(band, filters, 50) });
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }

  return (
    <div className="pol-view">
      <div aria-live="polite" className="sr-only">
        {announce}
      </div>

      {view === "plancomparison" && (
        <Card>
          <InputField
            label={t(S.plansToCompare)}
            value={filters.plans ?? ""}
            onChange={(e) => onFilter("plans", e.target.value)}
          />
          <InlineAlert tone="info">{t(S.plansHint)}</InlineAlert>
        </Card>
      )}

      {busy && <p className="pol-muted">{t(S.loading)}</p>}
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      {/* A scoped total is a different fact from an unscoped one, and it looks identical. Saying so is the
          difference between "your payers" and "the programme". */}
      {result?.payerScopeApplied && <InlineAlert tone="warn" data-testid="payer-scoped">{t(S.payerScoped)}</InlineAlert>}
      {result && result.unavailable.length > 0 && (
        <InlineAlert tone="warn">
          {t(S.unavailable)} {result.unavailable.join(", ")}
        </InlineAlert>
      )}

      {result && result.deltas.length > 0 && <DeltaStrip deltas={result.deltas} />}

      {result?.series.map((s) => (
        <SeriesCard key={s.key} series={s} onDrill={view === "outliers" ? openDrill : undefined} />
      ))}

      {result && result.series.length === 0 && !busy && <p className="pol-muted">{t(S.empty)}</p>}

      {result && (
        <div className="pol-view-actions">
          <Button variant="secondary" onClick={exportView}>
            {t(S.export)}
          </Button>
        </div>
      )}

      {drill && <DrillPanel drill={drill} onClose={() => setDrill(null)} />}
    </div>
  );
}

// ── A series: bars for sighted users, a table for everyone ───────────────────────────────────────────────

function SeriesCard({ series, onDrill }: { series: AnalyticsSeries; onDrill?: (band: string) => void }) {
  const { lang } = useTheme();
  const fmt = useFormat();
  const title = lang === "ar" ? series.titleAr : series.titleEn;
  const summary = lang === "ar" ? series.summaryAr : series.summaryEn;
  const max = Math.max(1, ...series.points.map((p) => Math.abs(p.value)));

  /*
   * Every figure goes through the app's formatter — including the two that used to bypass it.
   *
   * `fmt.money` resolves `ar-EG`, which renders Arabic-Indic digits; `` `${value}%` `` and `String(value)`
   * are JavaScript's own number-to-string and are ALWAYS Latin. So an Arabic cost table printed ١٬٢٥٠٫٠٠ in
   * its currency column and 120 in the count column beside it, under a server-composed summary sentence that
   * says ١٦٠ — three numeral systems in one card, for the same kind of quantity. Nothing errored, because
   * both spellings are readable; they just are not the same language.
   *
   * A percentage goes through `Intl` as a percentage rather than a number with a "%" glued on, so the sign
   * lands where Arabic puts it (٪ leads) instead of trailing an Arabic-Indic number in Latin punctuation.
   */
  const render = (value: number) =>
    series.unit === "currency" ? fmt.money(value)
      : series.unit === "percent" ? fmt.number(value / 100, { style: "percent", maximumFractionDigits: 1 })
      : fmt.number(value);

  return (
    <Card className="pol-series" data-testid={`series-${series.key}`}>
      <h2 className="panel-h">{title}</h2>
      {/* The text summary is read BEFORE the table, so a screen-reader user can decide whether to read the
          rows at all. It is composed server-side from the plotted data — a caption written here drifts. */}
      <p className="pol-series-summary">{summary}</p>

      {/* Decoration only. The bars carry no information the table lacks, so they are hidden from assistive
          tech rather than duplicated into it. */}
      <div className="pol-bars" aria-hidden="true">
        {series.points.map((p) => (
          <div key={p.key} className="pol-bar-row">
            <span className="pol-bar-label">{lang === "ar" ? p.labelAr : p.labelEn}</span>
            <span className="pol-bar-track">
              {/* Pattern + direct value, never colour alone: the width and the printed number both carry it. */}
              <span className={`pol-bar-fill p-${hash(p.key) % 4}`} style={{ width: `${(Math.abs(p.value) / max) * 100}%` }} />
            </span>
            <span className="pol-bar-value">{render(p.value)}</span>
          </div>
        ))}
      </div>

      {/* ALWAYS in the DOM. Not sr-only here — it is the content of the card, and a sighted user reading the
          exact figures should not have to hunt for them either. */}
      <div className="pol-tablewrap mrs-scroll mrs-scroll-focusable" tabIndex={0}>
        <table className="pol-costshare">
          <caption className="sr-only">{title}</caption>
          <thead>
            <tr>
              {series.columns.map((c) => (
                <th key={c.en} scope="col">
                  {lang === "ar" ? c.ar : c.en}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {series.points.map((p) => (
              <tr key={p.key}>
                <th scope="row">
                  {onDrill ? (
                    <Button variant="ghost" onClick={() => onDrill(bandOf(p.key))}>
                      {lang === "ar" ? p.labelAr : p.labelEn}
                    </Button>
                  ) : (
                    (lang === "ar" ? p.labelAr : p.labelEn)
                  )}
                </th>
                <td>{render(p.value)}</td>
                {series.columns.length > 2 && <td>{p.secondary != null ? render(p.secondary) : "—"}</td>}
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </Card>
  );
}

// ── Compare mode ────────────────────────────────────────────────────────────────────────────────────────

function DeltaStrip({ deltas }: { deltas: AnalyticsDelta[] }) {
  const t = useLoc();
  const { lang } = useTheme();

  return (
    <Card className="pol-deltas" data-testid="delta-strip">
      <p className="pol-muted">{t(S.comparing)}</p>
      <div className="pol-deltagrid">
        {deltas.map((d) => {
          // Four cues: hue + icon (from StatusChip) + the pill shape + the WORD. `better` decides the hue and
          // is a server judgement, because direction alone does not say whether a movement is good news.
          const word = d.direction === "Up" ? t(S.deltaUp) : d.direction === "Down" ? t(S.deltaDown) : t(S.deltaFlat);
          const kind = d.better === true ? "ok" : d.better === false ? "bad" : "neu";
          return (
            <div key={d.key} className="pol-delta">
              <span className="pol-delta-label">{lang === "ar" ? d.labelAr : d.labelEn}</span>
              <StatusChip kind={kind} label={`${word}${d.percentChange != null ? ` ${d.percentChange}%` : ""}`} />
              <span className="pol-delta-values">
                {d.current} · {t(S.vsPrevious)} {d.previous}
              </span>
            </div>
          );
        })}
      </div>
    </Card>
  );
}

// ── Drill-down ──────────────────────────────────────────────────────────────────────────────────────────

function DrillPanel({ drill, onClose }: { drill: { band: string; rows: OutlierRow[] }; onClose: () => void }) {
  const t = useLoc();
  const fmt = useFormat();

  return (
    <Card className="pol-drill" data-testid="drill-panel" aria-label={t(S.drillTitle)}>
      <h2 className="panel-h">
        {t(S.drillTitle)} — {drill.band}
      </h2>
      {/* Said plainly: the list is ids, and turning one into a person is a separate, recorded act. */}
      <InlineAlert tone="info">{t(S.drillHint)}</InlineAlert>
      <DataTable
        caption={t(S.drillTitle)}
        rows={drill.rows}
        rowKey={(r) => r.enrollmentId}
        emptyLabel={t(S.empty)}
        columns={[
          { key: "member", header: t(S.memberRef), cell: (r) => r.enrollmentId.slice(0, 8) },
          { key: "limit", header: t(S.limit), cell: (r) => fmt.money(r.limit), numeric: true },
          { key: "consumed", header: t(S.consumed), cell: (r) => fmt.money(r.consumed), numeric: true },
        ]}
      />
      <Button variant="ghost" onClick={onClose}>
        {t(S.drillClose)}
      </Button>
    </Card>
  );
}

// ── helpers ─────────────────────────────────────────────────────────────────────────────────────────────

/** The outlier series' point keys map onto utilization bands; anything else drills the exhausted band, which
 *  is the one an operator is looking for when they click a bar labelled "over the limit". */
function bandOf(key: string): string {
  if (key === "near-limit") return "High";
  if (key === "no-utilization") return "Zero";
  return "Exhausted";
}

/** Stable pattern index per series key, so a category keeps its hatch across renders and between views —
 *  a pattern that reshuffles is worse than no pattern, because it reads as a change in the data. */
function hash(s: string): number {
  let h = 0;
  for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0;
  return h;
}

function download(name: string, csv: string) {
  const url = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}

import { useCallback, useEffect, useMemo, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Button, Card, DataTable, InlineAlert, InputField, StatusChip, Tabs, useTheme } from "@mersal/design-system";
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
import { useFormat } from "../i18n/useFormat";

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
  subtitle: {
    en: "Aggregates over the policy and membership book. No clinical data appears in any view.",
    ar: "تجميعات على سجل الوثائق والعضوية. لا تظهر أي بيانات سريرية في أي عرض.",
  },
  tabEnrolment: { en: "Enrolment", ar: "التسجيل" },
  tabUtilization: { en: "Utilization", ar: "الاستهلاك" },
  tabFinancial: { en: "Financial", ar: "المالية" },
  tabNetwork: { en: "Network", ar: "الشبكة" },
  tabPlans: { en: "Plan comparison", ar: "مقارنة الخطط" },
  tabOutliers: { en: "Outliers & data quality", ar: "الحالات الشاذة وجودة البيانات" },

  filters: { en: "Filters", ar: "عوامل التصفية" },
  from: { en: "From", ar: "من" },
  to: { en: "To", ar: "إلى" },
  asOf: { en: "As of", ar: "كما في تاريخ" },
  asOfHint: {
    en: "A range asks what happened during it; an as-of date asks what the book looked like on that day.",
    ar: "النطاق يسأل عمّا حدث خلاله؛ وتاريخ «كما في» يسأل عن حالة السجل في ذلك اليوم.",
  },
  payer: { en: "Payer", ar: "الجهة الممولة" },
  plan: { en: "Plan", ar: "الخطة" },
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
      <PageHeader title={t(S.title)} />
      <p className="pol-muted">{t(S.subtitle)}</p>

      <FilterBar filters={filters} onChange={setFilter} onClear={clearFilters} />

      <Tabs
        aria-label={t(S.title)}
        value={view}
        onValueChange={setView}
        items={VIEWS.map((v) => ({
          value: v.key,
          label: t(v.label),
          // Each panel gates its own fetch: `Tabs` force-mounts every panel so hidden content stays available
          // to assistive tech, which means six views would otherwise fire six requests on first paint.
          content: <ViewPanel api={client} view={v.key} active={view === v.key} filters={filters} onFilter={setFilter} />,
        }))}
      />
    </div>
  );
}

// ── Filter bar ──────────────────────────────────────────────────────────────────────────────────────────

function FilterBar({
  filters,
  onChange,
  onClear,
}: {
  filters: AnalyticsFilters;
  onChange: (key: string, value: string) => void;
  onClear: () => void;
}) {
  const t = useLoc();
  const text = (key: keyof AnalyticsFilters, label: Localized, type = "text") => (
    <InputField
      type={type}
      label={t(label)}
      value={filters[key] ?? ""}
      onChange={(e) => onChange(key, e.target.value)}
    />
  );

  return (
    <Card className="pol-filterbar" aria-label={t(S.filters)}>
      <h3>{t(S.filters)}</h3>
      <div className="pol-filtergrid">
        {text("from", S.from, "date")}
        {text("to", S.to, "date")}
        {text("asOf", S.asOf, "date")}
        {text("payerId", S.payer)}
        {text("policyPlanId", S.plan)}
        {text("groupId", S.group)}
        {text("branchId", S.branch)}
        {text("tier", S.tier)}
        {text("category", S.category)}
        {text("status", S.status)}
        {text("relationship", S.relationship)}
        {text("band", S.band)}
      </div>
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

  const render = (value: number) =>
    series.unit === "currency" ? fmt.money(value) : series.unit === "percent" ? `${value}%` : String(value);

  return (
    <Card className="pol-series" data-testid={`series-${series.key}`}>
      <h3>{title}</h3>
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
      <div className="pol-tablewrap">
        <table className="pol-costshare">
          <caption className="sr-only">{title}</caption>
          <thead>
            <tr>
              {series.columns.map((c) => (
                <th key={c} scope="col">
                  {c}
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
      <h3>
        {t(S.drillTitle)} — {drill.band}
      </h3>
      {/* Said plainly: the list is ids, and turning one into a person is a separate, recorded act. */}
      <InlineAlert tone="info">{t(S.drillHint)}</InlineAlert>
      <DataTable
        caption={t(S.drillTitle)}
        rows={drill.rows}
        rowKey={(r) => r.enrollmentId}
        emptyLabel={t(S.empty)}
        columns={[
          { key: "member", header: t(S.memberRef), cell: (r) => r.enrollmentId.slice(0, 8) },
          { key: "limit", header: t(S.limit), cell: (r) => fmt.money(r.limit) },
          { key: "consumed", header: t(S.consumed), cell: (r) => fmt.money(r.consumed) },
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

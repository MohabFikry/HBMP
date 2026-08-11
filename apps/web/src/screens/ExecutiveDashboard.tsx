import { useState } from "react";
import { Button, Card, KpiCard, StatusChip } from "@mersal/design-system";
import type { ChartWidget, DashDataTable, ExecutiveDashboard as Dashboard, KpiWidget, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { PeriodControl, usePeriod } from "./director/PeriodControl";

const S = {
  title: { en: "Executive Dashboard", ar: "لوحة القيادة" },
  financeTitle: { en: "Financial Dashboard", ar: "اللوحة المالية" },
  empty: { en: "No data for this period.", ar: "لا توجد بيانات لهذه الفترة." },
  showTable: { en: "Show data table", ar: "عرض الجدول" },
  showChart: { en: "Show chart", ar: "عرض الرسم" },
  chartOf: { en: "Chart", ar: "رسم بياني" },
  version: { en: "Report version", ar: "إصدار التقرير" },
  breakdown: { en: "Show the breakdown", ar: "عرض التفصيل" },
  hideBreakdown: { en: "Hide the breakdown", ar: "إخفاء التفصيل" },
} satisfies Record<string, Localized>;

export function ExecutiveDashboard({ scope = "executive" }: { scope?: "executive" | "finance" | "director" }) {
  const api = useApi();
  const t = useLoc();
  // The period now reaches the server, and so does the scope. Both used to stop in the browser: the scope
  // chose a heading, and no screen in this portal ever sent a window at all.
  const [preset, period, setPreset] = usePeriod();
  const state = useAsync<Dashboard>(() => api.executiveDashboard(scope, period), [scope, period.from, period.to]);

  return (
    <>
      <PageHeader
        title={t(scope === "finance" ? S.financeTitle : S.title)}
        actions={state.data ? <span className="muted tnum">{t(S.version)} {state.data.version}</span> : undefined}
      />
      <PeriodControl preset={preset} period={period} onChange={setPreset} />
      <AsyncSection state={state} isEmpty={(d) => d.kpis.length === 0 && d.charts.length === 0} emptyLabel={S.empty}>
        {(d) => (
          <div className="stack" style={{ gap: "var(--sp5)" }}>
            <div className="kpi-row">
              {d.kpis.map((k) => (
                <KpiCell key={k.id} kpi={k} t={t} />
              ))}
            </div>
            <div className="chart-grid">
              {d.charts.map((c) => (
                <ChartCard key={c.id} chart={c} t={t} />
              ))}
            </div>
          </div>
        )}
      </AsyncSection>
    </>
  );
}

/**
 * A KPI headline, and the breakdown behind it when the server sent one.
 *
 * <b>The breakdown used to be thrown away.</b> The server marks pending-approvals and the financial summary
 * as Gauge and Summary widgets, each carrying a full `dataTable` — pending by status x priority x age x SLA
 * breach, and cost by service line. The client mapped both to a bare `{ title, value }` and dropped the
 * table on the floor: computed, serialised, sent, and rendered nowhere in the product. A supervisor reading
 * "37 pending" had no way in the application to ask which 37.
 *
 * <b>Collapsed by default, and that is not the accessibility trade the chart cards make.</b> A chart's table
 * IS its accessible alternative, so it must always be in the tree; this table is supplementary detail behind
 * a headline that is itself fully readable, so a disclosure is honest here. The button states which it is
 * doing, and `aria-expanded` carries the state to assistive tech rather than leaving it to the caret.
 */
function KpiCell({ kpi, t }: { kpi: KpiWidget; t: (l: Localized) => string }) {
  const [open, setOpen] = useState(false);
  const hasDetail = (kpi.dataTable?.rows.length ?? 0) > 0;
  const detailId = `kpi-detail-${kpi.id}`;

  return (
    <div className="kpi-cell">
      <KpiCard label={t(kpi.title)} value={kpi.value} delta={kpi.delta} direction={kpi.direction} />
      {kpi.status && (
        <div style={{ marginTop: "var(--sp2)" }}>
          <StatusChip kind={kpi.status.kind} label={t(kpi.status.label)} />
        </div>
      )}
      {hasDetail && (
        <>
          <Button
            size="sm"
            variant="ghost"
            aria-expanded={open}
            aria-controls={detailId}
            onClick={() => setOpen((v) => !v)}
            style={{ marginTop: "var(--sp2)" }}
          >
            {open ? t(S.hideBreakdown) : t(S.breakdown)}
          </Button>
          {open && (
            <div id={detailId} style={{ marginTop: "var(--sp2)" }}>
              <MiniTable table={kpi.dataTable!} caption={t(kpi.title)} t={t} />
            </div>
          )}
        </>
      )}
    </div>
  );
}

/**
 * The tabular form of a widget's data. Shared so a KPI's breakdown and a chart's alternative cannot drift.
 *
 * Takes `srOnly` rather than a className, so `mini-table` stays a literal on the tag. `table-design.test.ts`
 * reads the JSX line to check a table joins one of the five documented vocabularies rather than inventing a
 * sixth, and a class passed in as a variable is invisible to it — which is exactly how the fifth vocabulary
 * arrived. Constraining the prop is also the honest interface: a caller here has one choice, whether the
 * table is visible, not which design it uses.
 */
function MiniTable({ table, caption, t, srOnly = false }:
  { table: DashDataTable; caption: string; t: (l: Localized) => string; srOnly?: boolean }) {
  return (
    <table className={srOnly ? "mini-table sr-only" : "mini-table"}>
      <caption className="sr-only">{caption}</caption>
      <thead>
        <tr>{table.columns.map((col, i) => <th key={i} scope="col">{t(col)}</th>)}</tr>
      </thead>
      <tbody>
        {table.rows.map((row, ri) => (
          <tr key={ri}>{row.map((cell, ci) => <td key={ci} className={ci > 0 ? "tnum" : undefined}>{cell}</td>)}</tr>
        ))}
      </tbody>
    </table>
  );
}

function ChartCard({ chart, t }: { chart: ChartWidget; t: (l: Localized) => string }) {
  const [showTable, setShowTable] = useState(false);
  const max = Math.max(1, ...chart.series.map((p) => p.value));

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }}>
      <div className="result-head">
        <h2 className="section-h" style={{ margin: 0 }}>{t(chart.title)}</h2>
        <Button
          size="sm"
          variant="ghost"
          aria-pressed={showTable}
          onClick={() => setShowTable((v) => !v)}
        >
          {showTable ? t(S.showChart) : t(S.showTable)}
        </Button>
      </div>

      {/*
        18.D3 (audit R2 U6) — the data table is ALWAYS in the accessibility tree.
        It used to render only when `showTable` was on, and the default was OFF, while the bar chart carried
        aria-hidden="true". So the default state of every dashboard chart was: sighted users see bars, screen
        reader users encounter NOTHING — the region was empty. "There is an accessible alternative behind a
        toggle" is not an accessible alternative if the toggle starts closed and a screen-reader user has no
        way to know it exists. The table now renders unconditionally; the toggle only controls whether it is
        also VISIBLE, and the chart stays aria-hidden because it is genuinely decorative.
      */}
      <MiniTable
        table={chart.dataTable}
        caption={t(chart.title)}
        t={t}
        srOnly={!showTable}
      />
      {!showTable && (
        // Decorative: the table above carries the same numbers for assistive tech (US-073).
        <ul className="bars" aria-hidden="true">
          {chart.series.map((p, i) => (
            <li key={i}>
              <span className="bar-label">{t(p.label)}</span>
              <span className="bar-track"><span className="bar-fill" style={{ inlineSize: `${(p.value / max) * 100}%` }} /></span>
              <span className="bar-val tnum">{p.display}</span>
            </li>
          ))}
        </ul>
      )}
    </Card>
  );
}

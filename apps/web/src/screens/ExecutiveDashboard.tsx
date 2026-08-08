import { useState } from "react";
import { Button, Card, KpiCard, StatusChip } from "@mersal/design-system";
import type { ChartWidget, ExecutiveDashboard as Dashboard, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Executive Dashboard", ar: "لوحة القيادة" },
  financeTitle: { en: "Financial Dashboard", ar: "اللوحة المالية" },
  empty: { en: "No data for this period.", ar: "لا توجد بيانات لهذه الفترة." },
  showTable: { en: "Show data table", ar: "عرض الجدول" },
  showChart: { en: "Show chart", ar: "عرض الرسم" },
  chartOf: { en: "Chart", ar: "رسم بياني" },
  version: { en: "Report version", ar: "إصدار التقرير" },
} satisfies Record<string, Localized>;

export function ExecutiveDashboard({ scope = "executive" }: { scope?: "executive" | "finance" | "director" }) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<Dashboard>(() => api.executiveDashboard(scope), [scope]);

  return (
    <>
      <PageHeader
        title={t(scope === "finance" ? S.financeTitle : S.title)}
        actions={state.data ? <span className="muted tnum">{t(S.version)} {state.data.version}</span> : undefined}
      />
      <AsyncSection state={state} isEmpty={(d) => d.kpis.length === 0 && d.charts.length === 0} emptyLabel={S.empty}>
        {(d) => (
          <div className="stack" style={{ gap: "var(--sp5)" }}>
            <div className="kpi-row">
              {d.kpis.map((k) => (
                <div key={k.id} className="kpi-cell">
                  <KpiCard label={t(k.title)} value={k.value} delta={k.delta} direction={k.direction} />
                  {k.status && (
                    <div style={{ marginTop: "var(--sp2)" }}>
                      <StatusChip kind={k.status.kind} label={t(k.status.label)} />
                    </div>
                  )}
                </div>
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
      <table className={showTable ? "mini-table" : "mini-table sr-only"}>
        <caption className="sr-only">{t(chart.title)}</caption>
        <thead>
          <tr>{chart.dataTable.columns.map((col, i) => <th key={i} scope="col">{t(col)}</th>)}</tr>
        </thead>
        <tbody>
          {chart.dataTable.rows.map((row, ri) => (
            <tr key={ri}>{row.map((cell, ci) => <td key={ci} className={ci > 0 ? "tnum" : undefined}>{cell}</td>)}</tr>
          ))}
        </tbody>
      </table>
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

import { Card, DataTable, KpiCard } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, ReportView as ReportViewData } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

type Section = "oversight" | "quality" | "escalations";

const TITLES: Record<Section, Localized> = {
  oversight: { en: "Approval oversight / TAT", ar: "الإشراف على الموافقات" },
  quality: { en: "Quality & outcomes", ar: "الجودة والنتائج" },
  escalations: { en: "Escalations", ar: "التصعيدات" },
};
const EMPTY: Localized = { en: "No data for this period.", ar: "لا توجد بيانات لهذه الفترة." };

type Row = readonly string[];

/** Generic director report — KPI headlines + accessible data tables (de-identified reporting aggregates). */
export function DirectorReport({ section }: { section: Section }) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<ReportViewData>(() => api.directorReport(section), [section]);
  return (
    <>
      <PageHeader title={t(TITLES[section])} />
      <AsyncSection state={state} isEmpty={(d) => d.kpis.length === 0 && d.tables.length === 0} emptyLabel={EMPTY}>
        {(view) => (
          <div className="stack" style={{ gap: "var(--sp4)" }}>
            {view.kpis.length > 0 && (
              <div className="kpi-row">
                {view.kpis.map((k, i) => <KpiCard key={i} label={t(k.label)} value={k.value} />)}
              </div>
            )}
            {view.tables.map((table, ti) => {
              const cols: Column<Row>[] = table.columns.map((c, ci) => ({
                key: String(ci),
                header: t(c),
                cell: (row: Row) => <span className={ci === 0 ? "" : "tnum"}>{row[ci]}</span>,
              }));
              return (
                <Card key={ti} as="section" style={{ padding: "var(--sp3)" }}>
                  <h2 className="section-h" style={{ margin: "0 0 var(--sp2)", paddingInline: "var(--sp2)" }}>{t(table.title)}</h2>
                  {table.rows.length === 0 ? (
                    <p className="muted" style={{ paddingInline: "var(--sp2)" }}>{t(EMPTY)}</p>
                  ) : (
                    <DataTable columns={cols} rows={table.rows} rowKey={(r) => r.join("|")} caption={t(table.title)} />
                  )}
                </Card>
              );
            })}
          </div>
        )}
      </AsyncSection>
    </>
  );
}

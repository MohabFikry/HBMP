import { Card, DataTable, DataTableView, KpiCard, StatusChip, useTableQuery } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, ReportView as ReportViewData, SlaBreachRow } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { PeriodControl, usePeriod } from "./director/PeriodControl";

type Section = "oversight" | "quality" | "escalations";

const TITLES: Record<Section, Localized> = {
  oversight: { en: "Approval Oversight / TAT", ar: "الإشراف على الموافقات" },
  quality: { en: "Quality & Outcomes", ar: "الجودة والنتائج" },
  escalations: { en: "Escalations", ar: "التصعيدات" },
};
const EMPTY: Localized = { en: "No data for this period.", ar: "لا توجد بيانات لهذه الفترة." };

const B = {
  title: { en: "Past their SLA, still waiting", ar: "تجاوزت المهلة ولا تزال معلّقة" },
  none: {
    en: "Nothing is past its SLA right now.",
    ar: "لا يوجد حاليًا ما تجاوز المهلة المحددة.",
  },
  authNo: { en: "Request", ar: "الطلب" },
  priority: { en: "Priority", ar: "الأولوية" },
  status: { en: "Status", ar: "الحالة" },
  waiting: { en: "Waiting", ar: "مدة الانتظار" },
  reviewer: { en: "With", ar: "لدى" },
  unassigned: { en: "Unassigned", ar: "غير مُسند" },
  note: {
    en: "The clinic's own view of its queue. It names the request, not the patient — open the case in Approvals to act on it.",
    ar: "عرض العيادة لقائمتها. يذكر الطلب لا المريض — افتح الحالة في الموافقات لاتخاذ إجراء.",
  },
  search: { en: "Search", ar: "بحث" },
  searchHint: { en: "Request number, reviewer or priority", ar: "رقم الطلب أو المراجع أو الأولوية" },
  noMatches: {
    en: "No rows match. Change the search or clear it above.",
    ar: "لا توجد صفوف مطابقة. عدّل البحث أو أزله أعلاه.",
  },
} satisfies Record<string, Localized>;

/** Emergency reads differently from Routine, and colour alone must not be what says so (four-cue rule). */
function priorityChip(priority: string): { kind: "bad" | "warn" | "neu"; label: Localized } {
  switch (priority) {
    case "Emergency": return { kind: "bad", label: { en: "Emergency", ar: "طارئ" } };
    case "Urgent": return { kind: "warn", label: { en: "Urgent", ar: "عاجل" } };
    default: return { kind: "neu", label: { en: priority, ar: priority } };
  }
}

/**
 * The authorizations behind the breach count.
 *
 * <b>Why a supervisor needed this.</b> Approval Oversight reported "SLA breaches: 12" and the portal offered
 * no way to ask which twelve — a number to be trusted rather than acted on. This is the same PHI-free
 * reporting plane the count comes from, so it carries the request number, its priority, how long it has
 * waited and whose desk it is on, and NO beneficiary. The director holds `auth:read` and could open any of
 * these; a supervisor who opens files to check them is doing the reviewer's job, so the path to a case is a
 * deliberate step into Approvals rather than a click on this table.
 */
function SlaBreaches() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const state = useAsync(() => api.slaBreaches(), []);

  const hours = (seconds: number) => {
    const h = Math.round(seconds / 3600);
    return h < 48 ? `${fmt.number(h)}h` : `${fmt.number(Math.round(h / 24))}d`;
  };

  const cols: Column<SlaBreachRow>[] = [
    { key: "authNo", header: t(B.authNo), cell: (r) => <span className="tnum">{r.authNo}</span>, sortable: true, sortValue: (r) => r.authNo },
    {
      key: "priority", header: t(B.priority), sortable: true, sortValue: (r) => r.priority,
      cell: (r) => { const c = priorityChip(r.priority); return <StatusChip kind={c.kind} label={t(c.label)} />; },
    },
    { key: "status", header: t(B.status), cell: (r) => r.status, sortable: true, sortValue: (r) => r.status },
    {
      key: "waiting", header: t(B.waiting), numeric: true, sortable: true, sortValue: (r) => r.ageSeconds,
      cell: (r) => <span className="tnum">{hours(r.ageSeconds)}</span>,
    },
    {
      key: "reviewer", header: t(B.reviewer),
      cell: (r) => (r.reviewerId ? r.reviewerId : <span className="muted">{t(B.unassigned)}</span>),
    },
  ];

  /*
   * A `DataTableView`, not a bare table, and the distinction is not cosmetic.
   *
   * The server caps this at a hundred rows by default and will return five hundred if asked. A breach queue
   * that long is something a supervisor works — "has anyone picked up the emergency one", "what is Hala
   * holding" — and an unbroken, unsearchable list of a hundred rows answers neither. It is also the reason
   * the count alone was not enough in the first place.
   *
   * Read OUTSIDE the AsyncSection render prop: a hook called in there would be conditional on the load.
   */
  const query = useTableQuery<SlaBreachRow>({
    rows: state.data?.rows ?? [],
    columns: cols,
    searchText: (r) => [r.authNo, r.priority, r.status, r.reviewerId ?? ""].join(" "),
    searchLabel: t(B.search),
    searchPlaceholder: t(B.searchHint),
    pageSize: 25,
    persistKey: "director-sla-breaches",
  });

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <h2 className="section-h" style={{ margin: "0 0 var(--sp2)", paddingInline: "var(--sp2)" }}>{t(B.title)}</h2>
      <p className="muted" style={{ margin: "0 0 var(--sp3)", paddingInline: "var(--sp2)" }}>{t(B.note)}</p>
      <AsyncSection state={state} isEmpty={(d) => d.rows.length === 0} emptyLabel={B.none}>
        {() => (
          <DataTableView
            query={query}
            columns={cols}
            rowKey={(r) => r.authNo}
            caption={t(B.title)}
            emptyLabel={t(B.none)}
            noMatchesLabel={t(B.noMatches)}
          />
        )}
      </AsyncSection>
    </Card>
  );
}

type Row = readonly string[];

/** Generic director report — KPI headlines + accessible data tables (de-identified reporting aggregates). */
export function DirectorReport({ section }: { section: Section }) {
  const api = useApi();
  const t = useLoc();
  const [preset, period, setPreset] = usePeriod();
  const state = useAsync<ReportViewData>(() => api.directorReport(section, period), [section, period.from, period.to]);
  return (
    <>
      <PageHeader title={t(TITLES[section])} />
      <PeriodControl preset={preset} period={period} onChange={setPreset} />
      <AsyncSection state={state} isEmpty={(d) => d.kpis.length === 0 && d.tables.length === 0} emptyLabel={EMPTY}>
        {(view) => (
          <div className="stack" style={{ gap: "var(--sp4)" }}>
            {view.kpis.length > 0 && (
              <div className="kpi-row">
                {view.kpis.map((k, i) => <KpiCard key={i} label={t(k.label)} value={k.value} />)}
              </div>
            )}
            {/* The drill-down belongs to oversight alone: quality and escalations report on different
                things, and a breach list under either would be an answer to a question they do not ask. */}
            {section === "oversight" && <SlaBreaches />}
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

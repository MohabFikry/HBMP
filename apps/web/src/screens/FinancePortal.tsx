import { useState } from "react";
import { useFormat } from "../i18n/useFormat";
import { Button, Card, DataTable, SegmentedControl, StatusChip, useToast } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type {
  ExportRequest,
  ExportResult,
  FinancialSummary,
  Localized,
  Settlement,
  SettlementLine,
  UtilizationRow,
  UtilizationView,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

// The Finance portal is minimum-necessary: billing codes + amounts + masked refs only. There is deliberately no
// screen, column, or control here that reaches a diagnosis or clinical note (finance ≠ diagnosis).
const S = {
  utilTitle: { en: "Utilization", ar: "الاستخدام" },
  utilEmpty: { en: "No utilization for this period.", ar: "لا يوجد استخدام لهذه الفترة." },
  code: { en: "Service code", ar: "رمز الخدمة" },
  line: { en: "Service line", ar: "بند الخدمة" },
  category: { en: "Category", ar: "الفئة" },
  provider: { en: "Provider", ar: "مقدّم الخدمة" },
  authorized: { en: "Authorized", ar: "مُصرّح" },
  delivered: { en: "Delivered", ar: "مُقدّم" },
  spend: { en: "Spend", ar: "الإنفاق" },
  totals: { en: "Totals", ar: "الإجماليات" },

  setTitle: { en: "Provider Settlements", ar: "تسويات مقدّمي الخدمة" },
  setEmpty: { en: "No settlements yet.", ar: "لا توجد تسويات بعد." },
  settlement: { en: "Settlement", ar: "التسوية" },
  period: { en: "Period", ar: "الفترة" },
  total: { en: "Total", ar: "الإجمالي" },
  status: { en: "Status", ar: "الحالة" },
  view: { en: "View lines", ar: "عرض البنود" },
  pickSettlement: { en: "Select a settlement to see its priced lines.", ar: "اختر تسوية لعرض بنودها المُسعّرة." },
  agreedPrice: { en: "Agreed price", ar: "السعر المتفق" },
  lineTotal: { en: "Line total", ar: "إجمالي البند" },

  sumTitle: { en: "Financial Summaries", ar: "الملخصات المالية" },
  sumEmpty: { en: "No summary data.", ar: "لا توجد بيانات ملخص." },
  dimension: { en: "Group by", ar: "التجميع حسب" },
  byLine: { en: "Service line", ar: "بند الخدمة" },
  byCategory: { en: "Category", ar: "الفئة" },
  byProvider: { en: "Provider", ar: "مقدّم الخدمة" },
  showTable: { en: "Show data table", ar: "عرض الجدول" },
  showChart: { en: "Show chart", ar: "عرض الرسم" },
  share: { en: "Share", ar: "النسبة" },

  expTitle: { en: "Exports", ar: "التصدير" },
  report: { en: "Report", ar: "التقرير" },
  format: { en: "Format", ar: "الصيغة" },
  runExport: { en: "Export (masked, audited)", ar: "تصدير (مُقنّع ومُدقّق)" },
  confirm: { en: "Exports are masked and recorded in the audit trail. Continue?", ar: "التصدير مُقنّع ومُسجّل في سجل التدقيق. المتابعة؟" },
  exported: { en: "Export ready — a data.export audit event was recorded.", ar: "التصدير جاهز — تم تسجيل حدث تدقيق." },
  expFail: { en: "Export failed.", ar: "فشل التصدير." },
  rows: { en: "rows", ar: "صفوف" },
  dateFrom: { en: "From", ar: "من" },
  dateTo: { en: "To", ar: "إلى" },
  badRange: { en: "The From date must be on or before the To date.", ar: "يجب أن يكون تاريخ (من) مساويًا أو قبل تاريخ (إلى)." },
} satisfies Record<string, Localized>;

/** Utilization — authorized-vs-delivered + spend by billing code. A table (no chart needed); totals footer. */
export function FinanceUtilization() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const state = useAsync<UtilizationView>(() => api.utilization(), []);
  const cols: Column<UtilizationRow>[] = [
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.serviceCode}</span> },
    { key: "line", header: t(S.line), cell: (r) => t(r.serviceLine) },
    { key: "category", header: t(S.category), cell: (r) => t(r.coverageCategory) },
    { key: "provider", header: t(S.provider), cell: (r) => <span className="tnum">{r.providerRef ?? "—"}</span> },
    { key: "authorized", header: t(S.authorized), cell: (r) => r.authorizedQty, numeric: true },
    { key: "delivered", header: t(S.delivered), cell: (r) => r.deliveredQty, numeric: true },
    { key: "spend", header: t(S.spend), cell: (r) => fmt.money(r.spend), numeric: true },
  ];
  return (
    <>
      <PageHeader title={t(S.utilTitle)} actions={state.data ? <span className="muted tnum">{state.data.from} → {state.data.to}</span> : undefined} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.rows.length === 0} emptyLabel={S.utilEmpty}>
          {(d) => (
            <div className="stack" style={{ gap: "var(--sp3)" }}>
              <DataTable columns={cols} rows={d.rows} rowKey={(r) => r.serviceCode + r.providerRef} caption={t(S.utilTitle)} />
              <div className="result-head" style={{ paddingInline: "var(--sp2)" }}>
                <strong>{t(S.totals)}</strong>
                <span className="tnum">
                  {t(S.authorized)} {d.totalAuthorized} · {t(S.delivered)} {d.totalDelivered} · {t(S.spend)} {fmt.money(d.totalSpend)}
                </span>
              </div>
            </div>
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Provider settlements — list → priced line detail. Prices are the agreed contract prices (read from provider). */
export function FinanceSettlements() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const state = useAsync<Settlement[]>(() => api.settlements(), []);
  const [selected, setSelected] = useState<string | null>(null);

  const cols: Column<Settlement>[] = [
    { key: "settlement", header: t(S.settlement), cell: (r) => <span className="tnum">{r.settlementNo}</span> },
    { key: "provider", header: t(S.provider), cell: (r) => t(r.providerName) },
    { key: "period", header: t(S.period), cell: (r) => <span className="tnum">{r.periodStart} → {r.periodEnd}</span> },
    { key: "total", header: t(S.total), cell: (r) => fmt.money(r.total), numeric: true },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    {
      key: "view",
      header: t(S.view),
      cell: (r) => (
        <Button size="sm" variant={selected === r.id ? "primary" : "secondary"} onClick={() => setSelected(r.id)}>
          {t(S.view)}
        </Button>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.setTitle)} />
      <div className="split split-wide">
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.setEmpty}>
            {(rows) => (
              <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.setTitle)} interactive
                onSelect={(r) => setSelected(r.id)} selectedKey={selected} />
            )}
          </AsyncSection>
        </Card>
        <div>
          {selected && state.data ? (
            <SettlementLines lines={state.data.find((s) => s.id === selected)?.lines ?? []} t={t} />
          ) : (
            <Card style={{ padding: "var(--sp6)" }}><p className="muted">{t(S.pickSettlement)}</p></Card>
          )}
        </div>
      </div>
    </>
  );
}

function SettlementLines({ lines, t }: { lines: SettlementLine[]; t: (l: Localized) => string }) {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const cols: Column<SettlementLine>[] = [
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.serviceCode}</span> },
    { key: "line", header: t(S.line), cell: (r) => t(r.serviceLine) },
    { key: "delivered", header: t(S.delivered), cell: (r) => r.deliveredQty, numeric: true },
    { key: "agreed", header: t(S.agreedPrice), cell: (r) => fmt.money(r.agreedUnitPrice), numeric: true },
    { key: "total", header: t(S.lineTotal), cell: (r) => fmt.money(r.lineTotal), numeric: true },
  ];
  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <DataTable columns={cols} rows={lines} rowKey={(r) => r.serviceCode} caption={t(S.setTitle)} density="compact" />
    </Card>
  );
}

/** Financial summaries — a roll-up with a chart + accessible data-table toggle (US-073). Billing dimensions only. */
export function FinanceSummaries() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const [dimension, setDimension] = useState<FinancialSummary["dimension"]>("serviceline");
  const [showTable, setShowTable] = useState(false);
  const state = useAsync<FinancialSummary>(() => api.financialSummary(dimension), [dimension]);
  const max = Math.max(1, ...(state.data?.buckets.map((b) => b.sharePercent) ?? [1]));

  return (
    <>
      <PageHeader
        title={t(S.sumTitle)}
        actions={<span className="muted tnum">{t(S.total)}: {fmt.money(state.data?.totalSpend)}</span>}
      />
      <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
        <div className="result-head">
          <SegmentedControl<FinancialSummary["dimension"]>
            aria-label={t(S.dimension)}
            value={dimension}
            onChange={setDimension}
            segments={[
              { value: "serviceline", label: t(S.byLine) },
              { value: "category", label: t(S.byCategory) },
              { value: "provider", label: t(S.byProvider) },
            ]}
          />
          <Button size="sm" variant="ghost" aria-pressed={showTable} onClick={() => setShowTable((v) => !v)}>
            {showTable ? t(S.showChart) : t(S.showTable)}
          </Button>
        </div>
        <AsyncSection state={state} isEmpty={(d) => d.buckets.length === 0} emptyLabel={S.sumEmpty}>
          {(d) =>
            showTable ? (
              <table className="mini-table">
                <caption className="sr-only">{t(S.sumTitle)}</caption>
                <thead>
                  <tr>
                    <th scope="col">{t(S.category)}</th>
                    <th scope="col">{t(S.delivered)}</th>
                    <th scope="col">{t(S.spend)}</th>
                    <th scope="col">{t(S.share)}</th>
                  </tr>
                </thead>
                <tbody>
                  {d.buckets.map((b, i) => (
                    <tr key={i}>
                      <td>{t(b.key)}</td>
                      <td className="tnum">{b.deliveredQty}</td>
                      <td className="mrs-num">{fmt.money(b.spend)}</td>
                      <td className="tnum">{b.sharePercent}%</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            ) : (
              // Decorative — the data-table above is the accessible source of truth (US-073).
              <ul className="bars" aria-hidden="true">
                {d.buckets.map((b, i) => (
                  <li key={i}>
                    <span className="bar-label">{t(b.key)}</span>
                    <span className="bar-track"><span className="bar-fill" style={{ inlineSize: `${(b.sharePercent / max) * 100}%` }} /></span>
                    <span className="bar-val tnum">{fmt.money(b.spend)}</span>
                  </li>
                ))}
              </ul>
            )
          }
        </AsyncSection>
      </Card>
    </>
  );
}

/** Exports — confirm + run; masked, audited server-side (data.export). Shows the audited row count on success. */
export function FinanceExports() {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [report, setReport] = useState<ExportRequest["report"]>("utilization");
  const [format, setFormat] = useState<ExportRequest["format"]>("csv");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<ExportResult | null>(null);
  // Default to the trailing 30 days (ISO yyyy-MM-dd), operator-adjustable below.
  const today = new Date();
  const iso = (d: Date) => d.toISOString().slice(0, 10);
  const [from, setFrom] = useState(iso(new Date(today.getTime() - 30 * 864e5)));
  const [to, setTo] = useState(iso(today));
  const badRange = from > to;

  async function run() {
    if (badRange) return;
    if (!window.confirm(t(S.confirm))) return;
    setBusy(true);
    try {
      const res = await api.exportReport({ report, format, from, to });
      setResult(res);
      toast(t(S.exported), "ok");
    } catch {
      toast(t(S.expFail), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <PageHeader title={t(S.expTitle)} />
      <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)", maxInlineSize: "42rem" }}>
        <fieldset className="fieldset">
          <legend>{t(S.report)}</legend>
          <SegmentedControl<ExportRequest["report"]>
            aria-label={t(S.report)}
            value={report}
            onChange={setReport}
            segments={[
              { value: "utilization", label: t(S.utilTitle) },
              { value: "settlement", label: t(S.settlement) },
              { value: "summary", label: t(S.sumTitle) },
            ]}
          />
        </fieldset>
        <fieldset className="fieldset">
          <legend>{t(S.format)}</legend>
          <SegmentedControl<ExportRequest["format"]>
            aria-label={t(S.format)}
            value={format}
            onChange={setFormat}
            segments={[{ value: "csv", label: "CSV" }, { value: "xlsx", label: "XLSX" }]}
          />
        </fieldset>
        <fieldset className="fieldset">
          <legend>{t(S.period)}</legend>
          <div style={{ display: "flex", gap: "var(--sp4)", flexWrap: "wrap" }}>
            <label style={{ display: "grid", gap: "var(--sp2)" }}>
              {t(S.dateFrom)}
              <input type="date" value={from} max={to} onChange={(e) => setFrom(e.target.value)} style={{ minHeight: 44 }} />
            </label>
            <label style={{ display: "grid", gap: "var(--sp2)" }}>
              {t(S.dateTo)}
              <input type="date" value={to} min={from} onChange={(e) => setTo(e.target.value)} style={{ minHeight: 44 }} />
            </label>
          </div>
          {badRange && <p role="alert" style={{ color: "var(--st-bad-fg)" }}>{t(S.badRange)}</p>}
        </fieldset>
        <div>
          <Button variant="primary" loading={busy} disabled={badRange} onClick={run}>{t(S.runExport)}</Button>
        </div>
        {result && (
          <div aria-live="polite">
            <StatusChip kind={result.status.kind} label={t(result.status.label)} />{" "}
            <span className="tnum muted">{result.filename} · {result.rowCount} {t(S.rows)}</span>
          </div>
        )}
      </Card>
    </>
  );
}

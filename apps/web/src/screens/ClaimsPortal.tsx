import { Card, DataTable, KpiCard, SegmentedControl, StatusChip } from "@mersal/design-system";
import { useFormat } from "../i18n/useFormat";
import type { Column } from "@mersal/design-system";
import type { ClaimRow, ClaimsKpis, Localized, ReconciliationRow } from "@mersal/contracts";
import { useState } from "react";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

// The Claims portal is minimum-necessary: claim CODES + AMOUNTS + lifecycle status only. Like Finance, there is
// deliberately no screen, column, or control here that reaches a diagnosis or clinical note (claims ≠ diagnosis).
const S = {
  wlTitle: { en: "Claims Worklist", ar: "قائمة المطالبات" },
  wlEmpty: { en: "No claims match this filter.", ar: "لا توجد مطالبات مطابقة." },
  claimNo: { en: "Claim", ar: "المطالبة" },
  origin: { en: "Origin", ar: "المصدر" },
  status: { en: "Status", ar: "الحالة" },
  claimed: { en: "Claimed", ar: "المطالَب" },
  net: { en: "Net payable", ar: "الصافي المستحق" },
  serviceFrom: { en: "Service date", ar: "تاريخ الخدمة" },
  submitted: { en: "Submitted", ar: "تاريخ التقديم" },
  all: { en: "All", ar: "الكل" },
  submittedF: { en: "Submitted", ar: "مُقدّمة" },
  adjudicated: { en: "Adjudicated", ar: "تمت المراجعة" },
  rejected: { en: "Rejected", ar: "مرفوضة" },

  recTitle: { en: "Reconciliation", ar: "التسوية" },
  recEmpty: { en: "Nothing to reconcile in this bucket.", ar: "لا يوجد ما يُسوّى في هذه الفئة." },
  code: { en: "Code", ar: "الرمز" },
  serviceDate: { en: "Service date", ar: "تاريخ الخدمة" },
  billed: { en: "Billed", ar: "المفوتر" },
  allowed: { en: "Allowed", ar: "المسموح" },
  bucket: { en: "Bucket", ar: "الفئة" },
  bAll: { en: "All", ar: "الكل" },
  bMatched: { en: "Matched", ar: "مطابقة" },
  bVariance: { en: "Price variance", ar: "فرق سعر" },
  bBilledNot: { en: "Billed, not delivered", ar: "فوترة بلا تنفيذ" },

  insTitle: { en: "Claims Insights", ar: "مؤشرات المطالبات" },
  insEmpty: { en: "No KPI data for this period.", ar: "لا توجد بيانات مؤشرات لهذه الفترة." },
  tat: { en: "Avg TAT (hrs)", ar: "متوسط زمن المعالجة (س)" },
  approval: { en: "Approval rate", ar: "معدل الاعتماد" },
  denial: { en: "Denial rate", ar: "معدل الرفض" },
  ocr: { en: "OCR auto-match", ar: "مطابقة آلية" },
  agedCount: { en: "Aged unbilled", ar: "غير مفوتر متقادم" },
  agedValue: { en: "Aged value", ar: "قيمة متقادمة" },
  recovery: { en: "Recovery outstanding", ar: "مستحقات الاسترداد" },
  denialsTitle: { en: "Top denial reasons", ar: "أهم أسباب الرفض" },
  reason: { en: "Reason", ar: "السبب" },
  count: { en: "Count", ar: "العدد" },
} satisfies Record<string, Localized>;

// 18.D2 (U7): money is formatted at render by useFormat — EGP in the ACTIVE locale, not en-US.
const pct = (n: number) => `${Math.round(n * 100)}%`;
// 18.D2 (U7): see useFormat — Africa/Cairo + the app locale, never the browser's.

/** Claims worklist (36 §4) — the officer's queue of claims, filterable by lifecycle status. Codes + amounts only. */
export function ClaimsWorklist() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const [status, setStatus] = useState<string>("");
  const state = useAsync<ClaimRow[]>(() => api.claimsWorklist(status || undefined), [status]);
  const cols: Column<ClaimRow>[] = [
    { key: "claimNo", header: t(S.claimNo), cell: (r) => <span className="tnum">{r.claimNo}</span> },
    { key: "origin", header: t(S.origin), cell: (r) => r.origin },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "claimed", header: t(S.claimed), cell: (r) => fmt.money(r.claimedAmount), numeric: true },
    { key: "net", header: t(S.net), cell: (r) => fmt.money(r.netPayable), numeric: true },
    { key: "serviceFrom", header: t(S.serviceFrom), cell: (r) => <span className="tnum">{fmt.date(r.serviceDateFrom)}</span> },
    { key: "submitted", header: t(S.submitted), cell: (r) => <span className="tnum">{fmt.date(r.submittedAt)}</span> },
  ];
  return (
    <>
      <PageHeader title={t(S.wlTitle)} />
      <div style={{ marginBottom: "var(--sp3)" }}>
        <SegmentedControl
          aria-label={t(S.status)}
          value={status}
          onChange={setStatus}
          segments={[
            { value: "", label: t(S.all) },
            { value: "Submitted", label: t(S.submittedF) },
            { value: "Adjudicated", label: t(S.adjudicated) },
            { value: "Rejected", label: t(S.rejected) },
          ]}
        />
      </div>
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.wlEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.wlTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Reconciliation worklist (36 §7) — delivered/billed/coded signals bucketed by the classifier. */
export function ClaimsReconciliation() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const [bucket, setBucket] = useState<string>("");
  const state = useAsync<ReconciliationRow[]>(() => api.claimsReconciliation(bucket || undefined), [bucket]);
  const cols: Column<ReconciliationRow>[] = [
    { key: "claimNo", header: t(S.claimNo), cell: (r) => <span className="tnum">{r.claimNo}</span> },
    { key: "origin", header: t(S.origin), cell: (r) => r.origin },
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.code}</span> },
    { key: "serviceDate", header: t(S.serviceDate), cell: (r) => <span className="tnum">{fmt.date(r.serviceDate)}</span> },
    { key: "billed", header: t(S.billed), cell: (r) => fmt.money(r.billedAmount), numeric: true },
    { key: "allowed", header: t(S.allowed), cell: (r) => fmt.money(r.allowedAmount), numeric: true },
    { key: "bucket", header: t(S.bucket), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];
  return (
    <>
      <PageHeader title={t(S.recTitle)} />
      <div style={{ marginBottom: "var(--sp3)" }}>
        <SegmentedControl
          aria-label={t(S.bucket)}
          value={bucket}
          onChange={setBucket}
          segments={[
            { value: "", label: t(S.bAll) },
            { value: "Matched", label: t(S.bMatched) },
            { value: "PriceVariance", label: t(S.bVariance) },
            { value: "BilledNotDelivered", label: t(S.bBilledNot) },
          ]}
        />
      </div>
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.recEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => `${r.claimId}-${r.code}`} caption={t(S.recTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Claims insights (36 §11) — PHI-free operational KPIs + the top denial reasons. */
export function ClaimsInsights() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const state = useAsync<ClaimsKpis>(() => api.claimsKpis(), []);
  const reasonCols: Column<{ reason: string; count: number }>[] = [
    { key: "reason", header: t(S.reason), cell: (r) => r.reason },
    { key: "count", header: t(S.count), cell: (r) => <span className="tnum">{r.count}</span> },
  ];
  return (
    <>
      <PageHeader title={t(S.insTitle)} />
      <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.insEmpty}>
        {(k) => (
          <>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(160px, 1fr))", gap: "var(--sp3)", marginBottom: "var(--sp4)" }}>
              <KpiCard label={t(S.tat)} value={k.averageTatHours.toFixed(1)} />
              <KpiCard label={t(S.approval)} value={pct(k.approvalRate)} />
              <KpiCard label={t(S.denial)} value={pct(k.denialRate)} />
              <KpiCard label={t(S.ocr)} value={pct(k.ocrAutoMatchRate)} />
              <KpiCard label={t(S.agedCount)} value={String(k.agedUnbilledCount)} />
              <KpiCard label={t(S.agedValue)} value={fmt.money(k.agedUnbilledValue)} />
              <KpiCard label={t(S.recovery)} value={fmt.money(k.recoveryOutstanding)} />
            </div>
            <Card as="section" style={{ padding: "var(--sp3)" }}>
              <h2 style={{ fontSize: "var(--fs-title-3)", marginTop: 0 }}>{t(S.denialsTitle)}</h2>
              <DataTable columns={reasonCols} rows={k.topDenialReasons} rowKey={(r) => r.reason} caption={t(S.denialsTitle)} />
            </Card>
          </>
        )}
      </AsyncSection>
    </>
  );
}

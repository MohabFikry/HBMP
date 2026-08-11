import { Card, DataTable, DataTableView, InlineAlert, KpiCard, SegmentedControl, StatusChip, useTableQuery } from "@mersal/design-system";
import { useFormat } from "../i18n/useFormat";
import type { Column } from "@mersal/design-system";
import type { ClaimDetail, ClaimRow, ClaimsKpis, Localized, ReconciliationRow } from "@mersal/contracts";
import { RECON_BUCKETS } from "@mersal/contracts";
import { useState } from "react";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { PeriodControl, usePeriod } from "./director/PeriodControl";
import { AsyncSection, CodeList, PageHeader, useLoc } from "./_shared";

// The Claims portal is minimum-necessary: claim CODES + AMOUNTS + lifecycle status only. Like Finance, there is
// deliberately no screen, column, or control here that reaches a diagnosis or clinical note (claims ≠ diagnosis).
const S = {
  wlTitle: { en: "Claims", ar: "المطالبات" },
  wlEmpty: { en: "No claims match this filter.", ar: "لا توجد مطالبات مطابقة." },
  claimNo: { en: "Claim", ar: "المطالبة" },
  search: { en: "Search", ar: "بحث" },
  wlSearchHint: { en: "Claim number or origin", ar: "رقم المطالبة أو المصدر" },
  recSearchHint: { en: "Claim number or service code", ar: "رقم المطالبة أو رمز الخدمة" },
  noMatches: {
    en: "No rows match. Change the search or clear the filter above.",
    ar: "لا توجد صفوف مطابقة. عدّل البحث أو أزل التصفية أعلاه.",
  },
  origin: { en: "Origin", ar: "المصدر" },
  status: { en: "Status", ar: "الحالة" },
  claimed: { en: "Claimed", ar: "المطالَب" },
  net: { en: "Net payable", ar: "الصافي المستحق" },
  serviceFrom: { en: "Service date", ar: "تاريخ الخدمة" },
  submitted: { en: "Submitted", ar: "تاريخ التقديم" },

  // The status vocabulary is `ClaimStatus`, exactly. The three segments this screen used to offer named two
  // states that do not exist in it, and were sent to an endpoint that has no status parameter at all.
  all: { en: "All", ar: "الكل" },
  fSubmitted: { en: "Submitted", ar: "مُقدّمة" },
  fUnderAdjudication: { en: "Under adjudication", ar: "قيد البتّ" },
  fPendingInfo: { en: "Awaiting info", ar: "بانتظار معلومات" },
  fApproved: { en: "Approved", ar: "معتمدة" },
  fPartiallyApproved: { en: "Partial", ar: "جزئية" },
  fDenied: { en: "Denied", ar: "مرفوضة" },
  fSettled: { en: "Settled", ar: "مُسوّاة" },

  // ---- detail ----
  pick: { en: "Select a claim to see its lines and any adjustments raised against it.", ar: "اختر مطالبة لعرض بنودها وأي تسويات أُجريت عليها." },
  lines: { en: "Lines", ar: "البنود" },
  code: { en: "Code", ar: "الرمز" },
  qty: { en: "Qty", ar: "الكمية" },
  billed: { en: "Billed", ar: "المفوتر" },
  contract: { en: "Contract", ar: "التعاقدي" },
  allowed: { en: "Allowed", ar: "المسموح" },
  reasons: { en: "Reason codes", ar: "رموز الأسباب" },
  adjustments: { en: "Adjustments", ar: "التسويات" },
  adjType: { en: "Type", ar: "النوع" },
  adjDelta: { en: "Change", ar: "التغيير" },
  adjBefore: { en: "Before", ar: "قبل" },
  adjAfter: { en: "After", ar: "بعد" },
  adjWhen: { en: "When", ar: "التاريخ" },
  noAdjustments: { en: "No adjustments have been raised against this claim.", ar: "لم تُجرَ أي تسويات على هذه المطالبة." },
  approved: { en: "Approved", ar: "المعتمد" },

  recTitle: { en: "Reconciliation", ar: "التسوية" },
  recEmpty: { en: "Nothing to reconcile in this bucket.", ar: "لا يوجد ما يُسوّى في هذه الفئة." },
  serviceDate: { en: "Service date", ar: "تاريخ الخدمة" },
  bucket: { en: "Bucket", ar: "الفئة" },
  bAll: { en: "All", ar: "الكل" },
  bMatched: { en: "Matched", ar: "مطابقة" },
  bVariance: { en: "Price variance", ar: "فرق سعر" },
  bQuantity: { en: "Quantity variance", ar: "فرق كمية" },
  bBilledNot: { en: "Billed, not delivered", ar: "فوترة بلا تنفيذ" },
  bDeliveredNot: { en: "Delivered, not billed", ar: "تنفيذ بلا فوترة" },
  bDuplicate: { en: "Duplicate", ar: "مكرّرة" },

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

const BUCKET_LABEL: Record<(typeof RECON_BUCKETS)[number], Localized> = {
  Matched: S.bMatched,
  PriceVariance: S.bVariance,
  QuantityVariance: S.bQuantity,
  BilledNotDelivered: S.bBilledNot,
  DeliveredNotBilled: S.bDeliveredNot,
  Duplicate: S.bDuplicate,
};

/**
 * The claim-level worklist.
 *
 * <b>It was reading the wrong endpoint.</b> The client called `/claims/worklist`, which is the per-LINE
 * adjudication queue: hard-filtered to UnderAdjudication + Pending, and carrying no `status` query parameter
 * at all. ASP.NET bound nothing and answered 200, so every segment of the status control returned the same
 * rows — none of them in any of the statuses the control named, two of which are not members of `ClaimStatus`.
 *
 * And the payload is a LINE. `origin`, `claimedAmount`, `netPayable` and `submittedAt` are not on it, so those
 * four columns rendered empty, `0.00`, blank and blank on every row, always — while `rowKey` was the claim id,
 * which collides across the lines of one claim.
 *
 * `GET /api/v1/claims` is the claim-level list: it parses `status` into a real `ClaimStatus` and returns the
 * amounts. The line queue keeps its own screen, {@link ClaimsAdjudication}, because that is what it is for.
 */
export function ClaimsWorklist() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const [status, setStatus] = useState<string>("");
  const [selected, setSelected] = useState<string | null>(null);
  const state = useAsync<ClaimRow[]>(() => api.claimsWorklist(status || undefined), [status]);
  const cols: Column<ClaimRow>[] = [
    { key: "claimNo", header: t(S.claimNo), cell: (r) => <span className="tnum">{r.claimNo}</span>, sortable: true, sortValue: (r) => r.claimNo },
    { key: "origin", header: t(S.origin), cell: (r) => r.origin, sortable: true, sortValue: (r) => r.origin },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "claimed", header: t(S.claimed), cell: (r) => fmt.money(r.claimedAmount), numeric: true, sortable: true, sortValue: (r) => r.claimedAmount },
    { key: "net", header: t(S.net), cell: (r) => (r.netPayable === null || r.netPayable === undefined ? <span className="muted">—</span> : fmt.money(r.netPayable)), numeric: true, sortable: true, sortValue: (r) => r.netPayable ?? -1 },
    { key: "serviceFrom", header: t(S.serviceFrom), cell: (r) => <span className="tnum">{fmt.date(r.serviceDateFrom)}</span>, sortable: true, sortValue: (r) => r.serviceDateFrom },
    { key: "submitted", header: t(S.submitted), cell: (r) => <span className="tnum">{r.submittedAt ? fmt.date(r.submittedAt) : "—"}</span>, sortable: true, sortValue: (r) => r.submittedAt ?? "" },
  ];

  /*
    Search and a pager, but NO filter group: the status segmented control above already narrows this, and it
    does it on the SERVER — `claimsWorklist(status)` refetches. Mirroring it into `useTableQuery` would give
    the screen two controls for one question that could disagree with each other.

    Read outside AsyncSection's render prop: a hook called in there would be conditional on the load.
  */
  const query = useTableQuery<ClaimRow>({
    rows: state.data ?? [],
    columns: cols,
    // What a finance clerk arrives holding: the claim number off a provider's statement, or the code.
    searchText: (r) => [r.claimNo, r.origin, t(r.status.label)].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.wlSearchHint),
    pageSize: 25,
    persistKey: "claims-worklist",
  });

  return (
    <>
      <PageHeader title={t(S.wlTitle)} />
      <div style={{ marginBottom: "var(--sp3)" }}>
        <SegmentedControl
          aria-label={t(S.status)}
          value={status}
          onChange={(v) => { setStatus(v); setSelected(null); }}
          segments={[
            { value: "", label: t(S.all) },
            { value: "Submitted", label: t(S.fSubmitted) },
            { value: "UnderAdjudication", label: t(S.fUnderAdjudication) },
            { value: "PendingInfo", label: t(S.fPendingInfo) },
            { value: "Approved", label: t(S.fApproved) },
            { value: "PartiallyApproved", label: t(S.fPartiallyApproved) },
            { value: "Denied", label: t(S.fDenied) },
            { value: "Settled", label: t(S.fSettled) },
          ]}
        />
      </div>
      <div className="split split-wide">
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.wlEmpty}>
            {() => (
              <DataTableView
                query={query}
                columns={cols}
                rowKey={(r) => r.id}
                caption={t(S.wlTitle)}
                interactive
                selectedKey={selected ?? undefined}
                onSelect={(r) => setSelected(r.id)}
                emptyLabel={t(S.wlEmpty)}
                noMatchesLabel={t(S.noMatches)}
              />
            )}
          </AsyncSection>
        </Card>
        <div>
          {selected ? (
            <ClaimDetailPanel key={selected} claimId={selected} />
          ) : (
            <Card style={{ padding: "var(--sp6)" }}>
              <p className="muted">{t(S.pick)}</p>
            </Card>
          )}
        </div>
      </div>
    </>
  );
}

/** A claim's lines and the adjustments raised against it. Codes, quantities and amounts — never a diagnosis. */
function ClaimDetailPanel({ claimId }: { claimId: string }) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const detail = useAsync<ClaimDetail>(() => api.claimDetail(claimId), [claimId]);
  const adjustments = useAsync(() => api.claimAdjustments(claimId), [claimId]);

  const lineCols: Column<ClaimDetail["lines"][number]>[] = [
    { key: "code", header: t(S.code), cell: (l) => <span className="tnum">{l.code}</span> },
    { key: "qty", header: t(S.qty), cell: (l) => fmt.number(l.quantity), numeric: true },
    { key: "billed", header: t(S.billed), cell: (l) => fmt.money(l.billedAmount), numeric: true },
    { key: "contract", header: t(S.contract), cell: (l) => (l.contractPrice == null ? <span className="muted">—</span> : fmt.money(l.contractPrice)), numeric: true },
    { key: "allowed", header: t(S.allowed), cell: (l) => (l.allowedAmount == null ? <span className="muted">—</span> : fmt.money(l.allowedAmount)), numeric: true },
    { key: "status", header: t(S.status), cell: (l) => <StatusChip kind={l.status.kind} label={t(l.status.label)} /> },
    {
      key: "reasons",
      header: t(S.reasons),
      // The codes as written. They are the vocabulary the server validates against and the one an appeal
      // quotes back, so translating them here would make the two conversations use different words.
      cell: (l) => <CodeList codes={l.reasonCodes} />,
    },
  ];

  const adjCols: Column<{ adjustmentId: string; type: string; amountDelta: number; beforeAmount?: number | null; afterAmount?: number | null; adjustedAt: string }>[] = [
    { key: "type", header: t(S.adjType), cell: (a) => a.type },
    { key: "delta", header: t(S.adjDelta), cell: (a) => fmt.money(a.amountDelta), numeric: true },
    { key: "before", header: t(S.adjBefore), cell: (a) => (a.beforeAmount == null ? <span className="muted">—</span> : fmt.money(a.beforeAmount)), numeric: true },
    { key: "after", header: t(S.adjAfter), cell: (a) => (a.afterAmount == null ? <span className="muted">—</span> : fmt.money(a.afterAmount)), numeric: true },
    { key: "when", header: t(S.adjWhen), cell: (a) => <span className="tnum">{fmt.date(a.adjustedAt)}</span> },
  ];

  return (
    <div className="stack" style={{ gap: "var(--sp4)" }}>
      <AsyncSection state={detail} emptyLabel={S.pick}>
        {(c) => (
          <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
            <div>
              <h2 className="section-h" style={{ marginBlockStart: 0 }}>{c.claimNo}</h2>
              <dl className="rxv-meta">
                <dt>{t(S.origin)}</dt>
                <dd>{c.origin}</dd>
                <dt>{t(S.status)}</dt>
                <dd><StatusChip kind={c.status.kind} label={t(c.status.label)} /></dd>
                <dt>{t(S.claimed)}</dt>
                <dd className="tnum">{fmt.money(c.claimedAmount)}</dd>
                <dt>{t(S.approved)}</dt>
                <dd className="tnum">{c.approvedAmount == null ? "—" : fmt.money(c.approvedAmount)}</dd>
                <dt>{t(S.net)}</dt>
                <dd className="tnum">{c.netPayable == null ? "—" : fmt.money(c.netPayable)}</dd>
              </dl>
            </div>
            <div>
              <h3 className="rxv-h">{t(S.lines)}</h3>
              <DataTable columns={lineCols} rows={c.lines} rowKey={(l) => l.claimLineId} caption={t(S.lines)} />
            </div>
          </Card>
        )}
      </AsyncSection>

      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <h3 className="rxv-h" style={{ marginBlockStart: 0 }}>{t(S.adjustments)}</h3>
        <AsyncSection state={adjustments} isEmpty={(d) => d.length === 0} emptyLabel={S.noAdjustments}>
          {(rows) => (
            <DataTable columns={adjCols} rows={rows} rowKey={(a) => a.adjustmentId} caption={t(S.adjustments)} />
          )}
        </AsyncSection>
      </Card>
    </div>
  );
}

/**
 * Reconciliation (36 §7) — delivered/billed/coded signals bucketed by the classifier.
 *
 * <b>All six buckets, and a stated window.</b> Three were unselectable: `Duplicate`, which is the
 * double-billing signal; `DeliveredNotBilled`, which is money the platform is owed and never asked for; and
 * `QuantityVariance`. The server has always classified them. They were also invisible under "All", which is
 * the absence of a filter rather than a bucket, and had no status chip, so they rendered as their raw English
 * token in both languages.
 */
export function ClaimsReconciliation() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const [bucket, setBucket] = useState<string>("");
  const [preset, period, setPreset] = usePeriod("mersal.claims.period");
  const state = useAsync<ReconciliationRow[]>(() => api.claimsReconciliation(bucket || undefined, period), [bucket, period]);
  const cols: Column<ReconciliationRow>[] = [
    { key: "claimNo", header: t(S.claimNo), cell: (r) => <span className="tnum">{r.claimNo}</span>, sortable: true, sortValue: (r) => r.claimNo },
    { key: "origin", header: t(S.origin), cell: (r) => r.origin, sortable: true, sortValue: (r) => r.origin },
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.code}</span>, sortable: true, sortValue: (r) => r.code },
    { key: "serviceDate", header: t(S.serviceDate), cell: (r) => <span className="tnum">{fmt.date(r.serviceDate)}</span>, sortable: true, sortValue: (r) => r.serviceDate },
    { key: "billed", header: t(S.billed), cell: (r) => fmt.money(r.billedAmount), numeric: true, sortable: true, sortValue: (r) => r.billedAmount },
    { key: "allowed", header: t(S.allowed), cell: (r) => (r.allowedAmount == null ? <span className="muted">—</span> : fmt.money(r.allowedAmount)), numeric: true, sortable: true, sortValue: (r) => r.allowedAmount ?? -1 },
    { key: "bucket", header: t(S.bucket), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
  ];

  /** Same shape as the worklist above: the bucket control is the server's filter, so this adds only the
   *  search and the pager. A reconciliation row is found by claim number or by the service code on it. */
  const query = useTableQuery<ReconciliationRow>({
    rows: state.data ?? [],
    columns: cols,
    searchText: (r) => [r.claimNo, r.code, r.origin, t(r.status.label)].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.recSearchHint),
    pageSize: 25,
    persistKey: "claims-reconciliation",
  });

  return (
    <>
      <PageHeader title={t(S.recTitle)} />
      {/* The window now travels and is stated. This endpoint has always defaulted to the last ninety CAIRO
          days; the screen sent nothing and displayed nothing, so the list silently ended ninety days back
          with no indication that anything preceded it. */}
      <PeriodControl preset={preset} period={period} onChange={setPreset} />
      <div style={{ marginBottom: "var(--sp3)" }}>
        <SegmentedControl
          aria-label={t(S.bucket)}
          value={bucket}
          onChange={setBucket}
          segments={[
            { value: "", label: t(S.bAll) },
            ...RECON_BUCKETS.map((b) => ({ value: b, label: t(BUCKET_LABEL[b]) })),
          ]}
        />
      </div>
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.recEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              // The line's own id, which the server always sent and the client dropped. `claimId-code`
              // collided for two lines of one claim on the same code — the QuantityVariance case exactly.
              rowKey={(r) => r.claimLineId}
              caption={t(S.recTitle)}
              emptyLabel={t(S.recEmpty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Claims insights (36 §11) — PHI-free operational KPIs + the top denial reasons, over a stated period. */
export function ClaimsInsights() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const [preset, period, setPreset] = usePeriod("mersal.claims.period");
  const state = useAsync<ClaimsKpis>(() => api.claimsKpis(period), [period]);
  const reasonCols: Column<{ reason: string; count: number }>[] = [
    { key: "reason", header: t(S.reason), cell: (r) => <span className="tnum">{r.reason}</span>, sortable: true, sortValue: (r) => r.reason },
    { key: "count", header: t(S.count), cell: (r) => r.count, numeric: true, sortable: true, sortValue: (r) => r.count },
  ];
  return (
    <>
      <PageHeader title={t(S.insTitle)} />
      {/* A rate with no period is not a figure. "Denial rate 12%" did not say twelve percent of WHAT SPAN,
          and the endpoint's own default is ninety Cairo days — a different window from the thirty the
          director's dashboards default to, which is how two numbers covering different spans end up beside
          each other in one conversation. */}
      <PeriodControl preset={preset} period={period} onChange={setPreset} />
      <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.insEmpty}>
        {(k) => (
          <>
            {/* `mrs-kpigrid`, not a hand-written grid: this was `minmax(160px, 1fr)` with a --sp3 gap, which
                is narrower than the design system's own KPI row and too narrow for the money values two of
                these seven tiles carry — they clipped. One class now sizes both KPI layouts. */}
            <div className="mrs-kpigrid" style={{ marginBottom: "var(--sp4)" }}>
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
              {k.topDenialReasons.length === 0 ? (
                <InlineAlert tone="info">{t(S.insEmpty)}</InlineAlert>
              ) : (
                <DataTable columns={reasonCols} rows={k.topDenialReasons} rowKey={(r) => r.reason} caption={t(S.denialsTitle)} />
              )}
            </Card>
          </>
        )}
      </AsyncSection>
    </>
  );
}

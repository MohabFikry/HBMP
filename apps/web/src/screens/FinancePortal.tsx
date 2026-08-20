import { useCallback, useMemo, useState } from "react";
import { useFormat } from "../i18n/useFormat";
import {
  Button, Card, DataTable, DataTableView, Icon, InlineAlert, InputField, SegmentedControl, StatusChip,
  useTableQuery, useToast,
} from "@mersal/design-system";
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
import { ApiError } from "../api/http";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { PeriodControl, usePeriod } from "./director/PeriodControl";
import { useAuth } from "../auth/AuthProvider";

// The Finance portal is minimum-necessary: billing codes + amounts + masked refs only. There is deliberately no
// screen, column, or control here that reaches a diagnosis or clinical note (finance ≠ diagnosis).
//
// ==============================================================================================================
// THE PERIOD (design 49 §4)
// ==============================================================================================================
// `/finance/utilization` and `/finance/summaries` have accepted `from`/`to` since phase 10.2 and neither screen
// sent either, so finance saw the trailing month forever and could not close a prior one. Utilization even
// RENDERED the window it had been given, which is the period rule honoured in the reading and broken in the
// asking. One shared `PeriodControl` under its own storage key drives all three screens that take a window,
// so they cannot disagree about which month is on the page.
//
// Provider Settlements deliberately does NOT take it. A settlement carries its own period as columns, and
// filtering period-stamped rows by a global period means either containment or overlap — which differ exactly
// on the boundary-spanning settlements a finance question is most often about. Picking one silently would be
// a filter that means something the operator did not ask for. It filters on provider and status instead,
// which is what the endpoint declares.
const PERIOD_KEY = "finance-period";

const S = {
  utilTitle: { en: "Utilization", ar: "الاستخدام" },
  utilEmpty: { en: "No utilization for this period.", ar: "لا يوجد استخدام لهذه الفترة." },
  code: { en: "Service code", ar: "رمز الخدمة" },
  search: { en: "Search", ar: "بحث" },
  utilSearchHint: { en: "Service code, line or provider", ar: "رمز الخدمة أو البند أو مقدم الخدمة" },
  setSearchHint: { en: "Settlement number or provider", ar: "رقم التسوية أو مقدم الخدمة" },
  noMatches: {
    en: "No rows match. Change the search or clear the filters.",
    ar: "لا توجد صفوف مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },
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

  // ---- the lifecycle ----
  allStates: { en: "All", ar: "الكل" },
  draft: { en: "Draft", ar: "مسودة" },
  submitted: { en: "Submitted", ar: "مُقدَّمة" },
  approved: { en: "Approved", ar: "معتمدة" },
  paid: { en: "Paid", ar: "مدفوعة" },
  generate: { en: "Generate a settlement", ar: "إنشاء تسوية" },
  providerId: { en: "Provider", ar: "مقدّم الخدمة" },
  providerHint: {
    en: "The provider's id, as it appears on the settlement rows below.",
    ar: "معرّف مقدّم الخدمة كما يظهر في صفوف التسوية أدناه.",
  },
  create: { en: "Generate draft", ar: "إنشاء مسودة" },
  submit: { en: "Submit for approval", ar: "إرسال للاعتماد" },
  approve: { en: "Approve", ar: "اعتماد" },
  generated: { en: "Draft {no} generated.", ar: "تم إنشاء المسودة {no}." },
  submitted_ok: { en: "{no} submitted for approval.", ar: "تم إرسال {no} للاعتماد." },
  approved_ok: { en: "{no} approved. Payment is authorised, not executed.", ar: "تم اعتماد {no}. تم تفويض الدفع دون تنفيذه." },
  writeFailed: { en: "That could not be saved. Nothing changed.", ar: "تعذّر الحفظ. لم يتغيّر شيء." },
  /*
    SEGREGATION OF DUTIES, SAID BEFORE THE CLICK (design 49 §3.1).

    The service compares the submitter against the approving principal and answers 409
    `urn:hbmp:sod-violation`. That refusal stays — the client is not the authority on who may release a
    payment. But a screen that offers the submitter an Approve button and then refuses it is a control
    working correctly and reading as a defect in the software. `submittedBy` is on the view now, so the
    button is withheld and the rule is written out instead of discovered by breaking it.
  */
  sodOwn: {
    en: "You submitted this settlement, so somebody else has to approve it.",
    ar: "أنت من قدّم هذه التسوية، لذا يجب أن يعتمدها شخص آخر.",
  },
  sodRefused: {
    en: "Approval was refused: the approver must be a different person than the submitter.",
    ar: "رُفض الاعتماد: يجب أن يكون المعتمِد شخصاً غير مقدّم التسوية.",
  },
  /*
    THE PRICE SOURCE (design 49 §3.2).

    `SettlementLine.PriceSource` is `Contract` or `ObservedFloor`, and the domain comment on it says what it
    is for: an unpriced code is settled at the LOWEST unit cost observed for it in the period — a floor,
    pending a tariff, which can only under-state — and "a reviewer issuing the draft has to be able to tell
    them apart". The service projects it; this screen dropped it. So at the moment of authorising a payment,
    a column of "agreed prices" mixed the contract's tariff with a number this platform inferred, rendered
    identically.
  */
  priceSource: { en: "Priced by", ar: "أساس التسعير" },
  contract: { en: "Contract", ar: "العقد" },
  observedFloor: { en: "Observed floor", ar: "أدنى سعر ملحوظ" },
  unpricedWarn: {
    en: "{n} of these lines have no contract tariff. They are priced at the lowest unit cost seen in the "
      + "period, which can only under-state what is owed.",
    ar: "{n} من هذه البنود بلا تعريفة تعاقدية. سُعِّرت بأدنى تكلفة وحدة لوحظت في الفترة، وهو ما لا يمكن إلا "
      + "أن يقلّ عن المستحق.",
  },
  truncated: {
    en: "Showing the {shown} most recent of {total} settlements. Narrow by provider or status to see the rest.",
    ar: "يعرض أحدث {shown} من أصل {total} تسوية. ضيّق حسب مقدّم الخدمة أو الحالة لعرض الباقي.",
  },

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
  runExport: { en: "Export (masked, audited)", ar: "تصدير (مُقنّع ومُدقّق)" },
  confirm: { en: "Exports are masked and recorded in the audit trail. Continue?", ar: "التصدير مُقنّع ومُسجّل في سجل التدقيق. المتابعة؟" },
  exported: { en: "Downloaded — a data.export audit event was recorded.", ar: "تم التنزيل — وسُجّل حدث تدقيق." },
  expFail: { en: "Export failed. No file was produced.", ar: "فشل التصدير. لم يُنتج أي ملف." },
  rows: { en: "rows", ar: "صفوف" },
  /*
    CSV, and only CSV (design 49 §2).

    The format control offered XLSX and the endpoint has never produced one: it always returned `text/csv`,
    and stored the CLAIMED format in the export ledger, so the ledger asserted spreadsheets that were never
    generated. A CSV opens in Excel; the gap is not worth a spreadsheet library in the one service whose
    security argument is that it cannot express a clinical field. The control is gone rather than disabled —
    a disabled option still advertises something that is not coming.
  */
  csvOnly: { en: "CSV — opens in Excel.", ar: "CSV — يُفتح في Excel." },
} satisfies Record<string, Localized>;

/** Utilization — authorized-vs-delivered + spend by billing code. A table (no chart needed); totals footer. */
export function FinanceUtilization() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const [preset, period, setPreset] = usePeriod(PERIOD_KEY);
  // The period is a DEPENDENCY, so changing it re-asks the server rather than re-filtering a month the
  // browser happens to be holding.
  const state = useAsync<UtilizationView>(() => api.utilization(period), [period.from, period.to]);
  const cols: Column<UtilizationRow>[] = [
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.serviceCode}</span>, sortable: true, sortValue: (r) => r.serviceCode },
    { key: "line", header: t(S.line), cell: (r) => t(r.serviceLine), sortable: true, sortValue: (r) => t(r.serviceLine) },
    { key: "category", header: t(S.category), cell: (r) => t(r.coverageCategory), sortable: true, sortValue: (r) => t(r.coverageCategory) },
    { key: "provider", header: t(S.provider), cell: (r) => <span className="tnum">{r.providerRef ?? "—"}</span> },
    { key: "authorized", header: t(S.authorized), cell: (r) => r.authorizedQty, numeric: true, sortable: true, sortValue: (r) => r.authorizedQty },
    { key: "delivered", header: t(S.delivered), cell: (r) => r.deliveredQty, numeric: true, sortable: true, sortValue: (r) => r.deliveredQty },
    { key: "spend", header: t(S.spend), cell: (r) => fmt.money(r.spend), numeric: true, sortable: true, sortValue: (r) => r.spend },
  ];
  /*
    A period's utilization by billing code. It grows with the period and had no search, so answering "what
    did we spend on this code" meant scanning. The service LINE is the filter rather than the code: a
    finance analyst groups by line and reads codes within it, and the vocabulary is derived from the rows
    because the lines present depend on what was actually delivered.

    Read outside AsyncSection's render prop: a hook in there would be conditional on the load finishing.
  */
  const rows = useMemo(() => state.data?.rows ?? [], [state.data]);
  const filters = useMemo(() => {
    const lines = [...new Map(rows.map((r) => [t(r.serviceLine), r.serviceLine])).entries()]
      .sort((a, b) => a[0].localeCompare(b[0]));
    if (lines.length < 2) return [];
    return [{
      key: "line",
      label: t(S.line),
      options: lines.map(([label]) => ({ value: label, label })),
      match: (r: UtilizationRow, value: string) => t(r.serviceLine) === value,
    }];
  }, [rows, t]);

  const query = useTableQuery<UtilizationRow>({
    rows,
    columns: cols,
    searchText: (r) => [r.serviceCode, t(r.serviceLine), t(r.coverageCategory), r.providerRef]
      .filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.utilSearchHint),
    filters,
    pageSize: 25,
    // Biggest spend first — this table is opened to find where the money went.
    initialSortKey: "spend",
    initialSortDir: "descending",
    persistKey: "finance-utilization",
  });

  return (
    <>
      <PageHeader title={t(S.utilTitle)} actions={state.data ? <span className="muted tnum">{state.data.from} → {state.data.to}</span> : undefined} />
      <PeriodControl preset={preset} period={period} onChange={setPreset} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.rows.length === 0} emptyLabel={S.utilEmpty}>
          {(d) => (
            <div className="stack" style={{ gap: "var(--sp3)" }}>
              <DataTableView
                query={query}
                columns={cols}
                rowKey={(r) => r.serviceCode + r.providerRef}
                caption={t(S.utilTitle)}
                emptyLabel={t(S.utilEmpty)}
                noMatchesLabel={t(S.noMatches)}
              />
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

/** The four lifecycle states, plus "all". Real `SettlementStatus` members — the server parses them. */
const STATES = ["", "Draft", "Submitted", "Approved", "Paid"] as const;
type StateFilter = (typeof STATES)[number];

/**
 * Provider settlements — the list, the lifecycle, and the priced line detail.
 *
 * <p><b>This screen had no write control of any kind.</b> `finance` holds `finance:write` and
 * `finance:approve`; the service implements generate → submit → approve with segregation of duties on the
 * last step and a `SettlementApproved` event emitted inside the approving transaction. The portal had a table
 * and a "View lines" button, so a settlement could only exist if something outside the product put it there
 * and the SoD control the permission matrix requires had never been exercised by a person. Design 49 §3.</p>
 */
export function FinanceSettlements() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const { session } = useAuth();
  const me = session?.userId ?? null;

  const [stateFilter, setStateFilter] = useState<StateFilter>("");
  const [selected, setSelected] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<Localized | null>(null);
  // Bumped after every write, so the list re-reads from the server rather than being patched locally. A
  // settlement's total and lines are the service's arithmetic; re-deriving them here would be a second
  // implementation of the pricing rules on the screen that authorises the payment.
  const [version, setVersion] = useState(0);

  // SERVER-SIDE. `status` is a parameter the endpoint declares and this screen never sent — it pulled every
  // settlement and filtered in the browser, which cannot see past the endpoint's 100-row cap and so filtered
  // a truncated set while presenting it as complete.
  const state = useAsync(
    () => api.settlements(stateFilter ? { status: stateFilter } : undefined),
    [stateFilter, version],
  );

  const reload = useCallback(() => setVersion((v) => v + 1), []);

  const run = useCallback(async (id: string, kind: "submit" | "approve") => {
    setBusy(id);
    setRefusal(null);
    try {
      const s = kind === "submit" ? await api.submitSettlement(id) : await api.approveSettlement(id);
      toast(t(kind === "submit" ? S.submitted_ok : S.approved_ok).replace("{no}", s.settlementNo), "ok");
      reload();
    } catch (e) {
      // 409 `urn:hbmp:sod-violation` is the SoD refusal, and it is not a defect — it is the control doing
      // its job on a path the screen tries to keep the operator off in the first place. It gets its own
      // sentence; anything else is a save that did not happen.
      const problem = e instanceof ApiError ? e.problem : null;
      setRefusal(problem?.type === "urn:hbmp:sod-violation" ? S.sodRefused : S.writeFailed);
    } finally {
      setBusy(null);
    }
  }, [api, reload, t, toast]);

  const rows = state.data?.rows ?? [];
  const total = state.data?.total ?? rows.length;

  const cols: Column<Settlement>[] = [
    { key: "settlement", header: t(S.settlement), cell: (r) => <span className="tnum">{r.settlementNo}</span>, sortable: true, sortValue: (r) => r.settlementNo },
    { key: "provider", header: t(S.provider), cell: (r) => t(r.providerName), sortable: true, sortValue: (r) => t(r.providerName) },
    { key: "period", header: t(S.period), cell: (r) => <span className="tnum">{r.periodStart} → {r.periodEnd}</span> },
    { key: "total", header: t(S.total), cell: (r) => fmt.money(r.total), numeric: true, sortable: true, sortValue: (r) => r.total },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    {
      key: "act",
      header: t(S.status),
      cell: (r) => <LifecycleAction row={r} me={me} busy={busy === r.id} onRun={run} t={t} />,
    },
    {
      key: "view",
      header: t(S.view),
      cell: (r) => (
        <Button size="sm" variant="secondary" onClick={() => setSelected(r.id)}>
          {t(S.view)}
        </Button>
      ),
    },
  ];

  /** Provider settlements accumulate every period. Searched by settlement number or provider — the two
   *  things a finance clerk has in front of them when a provider queries a payment. */
  const query = useTableQuery<Settlement>({
    rows,
    columns: cols,
    searchText: (r) => [r.settlementNo, t(r.providerName), t(r.status.label)].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.setSearchHint),
    pageSize: 25,
    initialSortKey: "period",
    initialSortDir: "descending",
    persistKey: "finance-settlements",
  });

  const active = rows.find((s) => s.id === selected) ?? null;

  return (
    <>
      <PageHeader title={t(S.setTitle)} />
      <GenerateSettlement onDone={reload} />
      <div className="stack" style={{ gap: "var(--sp3)", marginBottom: "var(--sp3)" }}>
        <SegmentedControl<StateFilter>
          aria-label={t(S.status)}
          value={stateFilter}
          onChange={setStateFilter}
          segments={[
            { value: "", label: t(S.allStates) },
            { value: "Draft", label: t(S.draft) },
            { value: "Submitted", label: t(S.submitted) },
            { value: "Approved", label: t(S.approved) },
            { value: "Paid", label: t(S.paid) },
          ]}
        />
        {/* Invariant 31 — a list that caps its page says so, in the count. */}
        {total > rows.length && (
          <InlineAlert tone="info">
            {t(S.truncated).replace("{shown}", String(rows.length)).replace("{total}", String(total))}
          </InlineAlert>
        )}
        <div aria-live="polite">{refusal && <InlineAlert tone="bad">{t(refusal)}</InlineAlert>}</div>
      </div>
      <div className="split split-wide">
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <AsyncSection state={state} isEmpty={(d) => d.rows.length === 0} emptyLabel={S.setEmpty}>
            {() => (
              <DataTableView
                query={query}
                columns={cols}
                rowKey={(r) => r.id}
                caption={t(S.setTitle)}
                interactive
                onSelect={(r) => setSelected(r.id)}
                selectedKey={selected}
                emptyLabel={t(S.setEmpty)}
                noMatchesLabel={t(S.noMatches)}
              />
            )}
          </AsyncSection>
        </Card>
        <div>
          {active ? (
            <SettlementLines lines={active.lines} t={t} />
          ) : (
            <Card style={{ padding: "var(--sp6)" }}><p className="muted">{t(S.pickSettlement)}</p></Card>
          )}
        </div>
      </div>
    </>
  );
}

/**
 * The one control a settlement's current state permits, and nothing else.
 *
 * <p>A Draft submits. A Submitted approves — unless the caller submitted it, in which case the button is not
 * offered and the reason is written where the button would have been. An Approved or Paid settlement has no
 * next step here: payment execution is out of scope for finance-service by design, and `Paid` is a recorded
 * outcome rather than a money movement.</p>
 */
function LifecycleAction({
  row, me, busy, onRun, t,
}: {
  row: Settlement;
  me: string | null;
  busy: boolean;
  onRun: (id: string, kind: "submit" | "approve") => void;
  t: (l: Localized) => string;
}) {
  if (row.state === "draft") {
    return (
      <Button size="sm" variant="primary" leadingIcon={<Icon name="check2" />} loading={busy}
        onClick={() => onRun(row.id, "submit")}>
        {t(S.submit)}
      </Button>
    );
  }
  if (row.state !== "submitted") return <span className="muted">—</span>;
  // The SoD rule, before the click rather than in the refusal. See S.sodOwn.
  if (me !== null && row.submittedBy === me) {
    return <span className="muted" style={{ maxInlineSize: "18rem", display: "inline-block" }}>{t(S.sodOwn)}</span>;
  }
  return (
    <Button size="sm" variant="primary" loading={busy} onClick={() => onRun(row.id, "approve")}>
      {t(S.approve)}
    </Button>
  );
}

/** Generate a draft for one provider and one period — the entry point the portal did not have. */
function GenerateSettlement({ onDone }: { onDone: () => void }) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [providerId, setProviderId] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  const ready = providerId.trim() !== "" && from !== "" && to !== "" && from <= to;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!ready) return;
    setBusy(true);
    setError(null);
    try {
      const s = await api.generateSettlement({ providerId: providerId.trim(), periodStart: from, periodEnd: to });
      toast(t(S.generated).replace("{no}", s.settlementNo), "ok");
      setProviderId("");
      onDone();
    } catch {
      setError(S.writeFailed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", marginBottom: "var(--sp4)" }}>
      <h2 className="section-h" style={{ marginBlockStart: 0 }}>{t(S.generate)}</h2>
      <form onSubmit={submit} className="stack" style={{ gap: "var(--sp3)" }} aria-label={t(S.generate)}>
        <InputField
          label={t(S.providerId)}
          help={t(S.providerHint)}
          value={providerId}
          onChange={(e) => setProviderId(e.currentTarget.value)}
          autoComplete="off"
        />
        <div style={{ display: "flex", gap: "var(--sp4)", flexWrap: "wrap" }}>
          <label style={{ display: "grid", gap: "var(--sp2)" }}>
            {t(S.period)} — {t({ en: "from", ar: "من" })}
            <input type="date" value={from} max={to || undefined} onChange={(e) => setFrom(e.target.value)} style={{ minHeight: 44 }} />
          </label>
          <label style={{ display: "grid", gap: "var(--sp2)" }}>
            {t(S.period)} — {t({ en: "to", ar: "إلى" })}
            <input type="date" value={to} min={from || undefined} onChange={(e) => setTo(e.target.value)} style={{ minHeight: 44 }} />
          </label>
        </div>
        <div aria-live="polite">{error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}</div>
        <div>
          <Button type="submit" variant="primary" leadingIcon={<Icon name="plus" />} loading={busy} disabled={!ready}>
            {t(S.create)}
          </Button>
        </div>
      </form>
    </Card>
  );
}

function SettlementLines({ lines, t }: { lines: SettlementLine[]; t: (l: Localized) => string }) {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  // How many of these numbers are not a contract tariff. Stated above the table rather than left for the
  // reviewer to count down a column — the whole point of surfacing the price source is that it changes
  // whether this settlement should be approved as it stands.
  const unpriced = lines.filter((l) => l.priceSource === "ObservedFloor").length;
  const cols: Column<SettlementLine>[] = [
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.serviceCode}</span>, sortable: true, sortValue: (r) => r.serviceCode },
    { key: "line", header: t(S.line), cell: (r) => t(r.serviceLine), sortable: true, sortValue: (r) => t(r.serviceLine) },
    { key: "delivered", header: t(S.delivered), cell: (r) => r.deliveredQty, numeric: true, sortable: true, sortValue: (r) => r.deliveredQty },
    { key: "agreed", header: t(S.agreedPrice), cell: (r) => fmt.money(r.agreedUnitPrice), numeric: true, sortable: true, sortValue: (r) => r.agreedUnitPrice },
    {
      key: "source",
      header: t(S.priceSource),
      // A chip, not a word: `warn` is the second cue that a price nobody agreed to is in this row.
      cell: (r) => r.priceSource === "Contract"
        ? <StatusChip kind="ok" label={t(S.contract)} />
        : <StatusChip kind="warn" label={t(S.observedFloor)} />,
      sortable: true,
      sortValue: (r) => r.priceSource,
    },
    { key: "total", header: t(S.lineTotal), cell: (r) => fmt.money(r.lineTotal), numeric: true, sortable: true, sortValue: (r) => r.lineTotal },
  ];
  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      {unpriced > 0 && (
        <div style={{ marginBottom: "var(--sp3)" }}>
          <InlineAlert tone="warn">{t(S.unpricedWarn).replace("{n}", String(unpriced))}</InlineAlert>
        </div>
      )}
      <DataTable columns={cols} rows={lines} rowKey={(r) => r.serviceCode} caption={t(S.setTitle)} density="compact" />
    </Card>
  );
}

/** Financial summaries — a roll-up with a chart + accessible data-table toggle (US-073). Billing dimensions only. */
export function FinanceSummaries() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const [preset, period, setPreset] = usePeriod(PERIOD_KEY);
  const [dimension, setDimension] = useState<FinancialSummary["dimension"]>("serviceline");
  const [showTable, setShowTable] = useState(false);
  const state = useAsync<FinancialSummary>(
    () => api.financialSummary(dimension, period),
    [dimension, period.from, period.to],
  );
  const max = Math.max(1, ...(state.data?.buckets.map((b) => b.sharePercent) ?? [1]));

  return (
    <>
      <PageHeader
        title={t(S.sumTitle)}
        actions={<span className="muted tnum">{t(S.total)}: {fmt.money(state.data?.totalSpend)}</span>}
      />
      <PeriodControl preset={preset} period={period} onChange={setPreset} />
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

/**
 * Exports — confirm, run, and HAND OVER THE FILE.
 *
 * <p>Three controls on this screen used to do nothing (design 49 §2). The report selector was ignored by the
 * server, which ran the utilization query whatever was asked for and used `report` only to name the file and
 * the `data.export` audit event — so choosing "Provider Settlements" wrote an audit record asserting an
 * export nobody performed. The format selector offered XLSX and the server always returned CSV. And the
 * button produced no file at all: the response is `text/csv` and the client parsed it as JSON, so the screen
 * reported a row count and handed the operator nothing.</p>
 *
 * <p>The period comes from the portal's shared control, so an export matches the figures on the screen the
 * operator was just reading rather than a window they have to remember to re-enter.</p>
 */
export function FinanceExports() {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [preset, period, setPreset] = usePeriod(PERIOD_KEY);
  const [report, setReport] = useState<ExportRequest["report"]>("utilization");
  const [busy, setBusy] = useState(false);
  const [result, setResult] = useState<ExportResult | null>(null);

  async function run() {
    if (!window.confirm(t(S.confirm))) return;
    setBusy(true);
    try {
      const res = await api.exportReport({
        report,
        format: "csv",
        from: period.from,
        to: period.to,
      });
      setResult(res);
      toast(t(S.exported), "ok");
    } catch {
      setResult(null);
      toast(t(S.expFail), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <PageHeader title={t(S.expTitle)} />
      <PeriodControl preset={preset} period={period} onChange={setPreset} />
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
        {/* No format control. The endpoint produces CSV and has never produced anything else; a segment
            offering XLSX was a choice the operator believed they had made. */}
        <p className="muted" style={{ margin: 0 }}>{t(S.csvOnly)}</p>
        <div>
          <Button variant="primary" loading={busy} onClick={run}>{t(S.runExport)}</Button>
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

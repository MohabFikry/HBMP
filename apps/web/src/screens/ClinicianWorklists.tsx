import { useMemo, useState } from "react";
import { useFormat, type Formatters } from "../i18n/useFormat";
import { Button, Card, DataTable, DataTableView, Modal, StatusChip, useTableQuery } from "@mersal/design-system";
import type { Column, TableFilterSpec } from "@mersal/design-system";
import type { Localized, OrderRow, PatientListItem, ResultDetail, RxRow } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc, useOpenProfile } from "./_shared";
import { RestrictedResultCard, RequestAccessDialog } from "./RestrictedResultCard";

const S = {
  patientsTitle: { en: "My Patients", ar: "مرضاي" },
  patientsEmpty: { en: "No patients on your worklist.", ar: "لا يوجد مرضى في قائمتك." },
  patientsNoMatches: {
    en: "No patients match. Change the search or clear the filters.",
    ar: "لا يوجد مرضى مطابقون. عدّل البحث أو أزل عوامل التصفية.",
  },
  search: { en: "Search", ar: "بحث" },
  patientsSearchHint: { en: "Name, MRN or status", ar: "الاسم أو الرقم الطبي أو الحالة" },
  // The three encounter states, worded exactly as the chips in the Status column (`encounterStatus`).
  encInProgress: { en: "In progress", ar: "جارٍ" },
  encCompleted: { en: "Completed", ar: "مكتمل" },
  encCancelled: { en: "Cancelled", ar: "ملغى" },
  ordersTitle: { en: "Orders", ar: "الطلبات" },
  ordersEmpty: { en: "You haven't placed any orders.", ar: "لم تقم بطلب أي فحوصات." },
  rxTitle: { en: "Prescriptions", ar: "الوصفات" },
  rxEmpty: { en: "You haven't written any prescriptions.", ar: "لم تكتب أي وصفات." },
  resultsTitle: { en: "Results Inbox", ar: "صندوق النتائج" },
  resultsEmpty: { en: "No completed results yet.", ar: "لا توجد نتائج مكتملة بعد." },
  openFile: { en: "Patient file", ar: "ملف المريض" },
  patient: { en: "Patient", ar: "المريض" },
  mrn: { en: "MRN", ar: "الرقم الطبي" },
  lastVisit: { en: "Last visit", ar: "آخر زيارة" },
  status: { en: "Status", ar: "الحالة" },
  orderNo: { en: "Order", ar: "الطلب" },
  type: { en: "Type", ar: "النوع" },
  code: { en: "Code", ar: "الرمز" },
  lines: { en: "Lines", ar: "البنود" },
  placed: { en: "Placed", ar: "تاريخ الطلب" },
  submitted: { en: "Submitted", ar: "تاريخ الإرسال" },
  result: { en: "Result", ar: "النتيجة" },
  viewResult: { en: "View result", ar: "عرض النتيجة" },
  resultTitle: { en: "Result", ar: "النتيجة" },
  value: { en: "Value", ar: "القيمة" },
  accessRequested: { en: "Access request submitted — pending author / medical-director grant.", ar: "تم إرسال طلب الوصول — بانتظار موافقة الطبيب المُحرّر / المدير الطبي." },
  close: { en: "Close", ar: "إغلاق" },
} satisfies Record<string, Localized>;

// 18.D2 (U7): dates come from useFormat — pinned to Africa/Cairo and the APP locale. A bare
// toLocaleDateString uses the MACHINE zone, so a UTC-set clinic PC renders the wrong day near midnight.

/** My patients — the caller's own encounters (treating-relationship gated server-side). */
export function DoctorPatients() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  // Carries where we came from, so the profile's Back control returns to this worklist rather than guessing.
  const openProfile = useOpenProfile();
  const state = useAsync<PatientListItem[]>(() => api.listPatients(), []);
  const cols: Column<PatientListItem>[] = [
    // The patient's NAME. emr supplies it on this endpoint from the appointment the visit was started from;
    // a walk-in that was never booked still falls back to the masked token, which the client builds.
    { key: "patient", header: t(S.patient), cell: (r) => <strong>{t(r.name)}</strong>,
      sortable: true, sortValue: (r) => t(r.name) },
    { key: "mrn", header: t(S.mrn), cell: (r) => <span className="tnum">{r.mrn}</span>,
      sortable: true, sortValue: (r) => r.mrn },
    // Sorts on the ISO date, not the rendered one: `fmt.date` renders Arabic-Indic digits under the Arabic
    // locale, and sorting those orders the worklist by glyph.
    { key: "lastVisit", header: t(S.lastVisit), cell: (r) => <span className="tnum">{fmt.date(r.lastVisit)}</span>,
      sortable: true, sortValue: (r) => r.lastVisit ?? "" },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />,
      sortable: true, sortValue: (r) => r.status.label.en },
    {
      // design 39 §6's "search → profile" entry, from the list a clinician actually starts their day in.
      // The whole unified profile was unreachable from every clinical worklist without this.
      key: "file",
      // A real header, not "": an empty <th> has no accessible name, so a screen-reader user hears nothing
      // for the column their cursor is in (axe empty-table-header).
      header: t(S.openFile),
      cell: (r) => (
        <Button variant="secondary" size="sm" onClick={() => openProfile(r.beneficiaryId)}>
          {t(S.openFile)}
        </Button>
      ),
    },
  ];

  // Matched on the ENGLISH label: the row carries its status only as a pre-resolved `{kind, label}` chip,
  // and matching the localized half would break the filter the moment the portal is switched to Arabic.
  const filters: TableFilterSpec<PatientListItem>[] = useMemo(() => [
    {
      key: "status",
      label: t(S.status),
      options: [
        { value: S.encInProgress.en, label: t(S.encInProgress) },
        { value: S.encCompleted.en,  label: t(S.encCompleted) },
        { value: S.encCancelled.en,  label: t(S.encCancelled) },
      ],
      match: (r, value) => r.status.label.en === value,
    },
  ], [t]);

  // Read outside AsyncSection's render prop: a hook called there would be conditional on the load finishing.
  const query = useTableQuery<PatientListItem>({
    rows: state.data ?? [],
    columns: cols,
    // Both languages of the name and the status, because the portal switches and a haystack in one language
    // goes quiet in the other.
    searchText: (r) => [r.name.en, r.name.ar, r.mrn, r.status.label.en, r.status.label.ar].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.patientsSearchHint),
    filters,
    pageSize: 10,
    // Most recent visit first — this is a panel a clinician scans for who they have just seen, and emr
    // already returns it newest-first.
    initialSortKey: "lastVisit",
    initialSortDir: "descending",
    persistKey: "doctor-patients",
  });

  return (
    <>
      <PageHeader title={t(S.patientsTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.patientsEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.patientsTitle)}
              emptyLabel={t(S.patientsEmpty)}
              noMatchesLabel={t(S.patientsNoMatches)}
            />
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

// 18.D2 (U7): the formatter is PASSED IN rather than hooked here — this is a plain helper, not a component,
// so calling a hook inside it violates the rules of hooks (and would break if it were ever called twice).
function orderColumns(t: (l: Localized) => string, fmt: Formatters): Column<OrderRow>[] {
  return [
    { key: "orderNo", header: t(S.orderNo), cell: (r) => <span className="tnum">{r.orderNo}</span> },
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.beneficiary.token}</span> },
    { key: "type", header: t(S.type), cell: (r) => r.orderType },
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.primaryCode}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "placed", header: t(S.placed), cell: (r) => <span className="tnum">{fmt.date(r.requestedAt)}</span> },
  ];
}

/** Orders — everything I've ordered (investigation orders I authored). */
export function DoctorOrders() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const state = useAsync<OrderRow[]>(() => api.ordersMine(), []);
  const cols = orderColumns(t, fmt);
  return (
    <>
      <PageHeader title={t(S.ordersTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.ordersEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.ordersTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Results inbox — my orders that are Completed (results are back). */
/**
 * Results inbox (Phase 4 US-032 + Phase 14.6/14.8). Opening a completed result fetches it through the sensitivity
 * gate: a Standard result (or one the caller authored / holds a grant for) shows its value; a restricted result
 * returns EXISTENCE-ONLY metadata, which renders the locked `RestrictedResultCard` + a "Request access" flow. The
 * server sends no values for a restricted result, so no clinical value can ever reach this DOM.
 */
export function DoctorResults() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const state = useAsync<OrderRow[]>(() => api.ordersMine("Completed"), []);
  const [open, setOpen] = useState(false);
  const [detail, setDetail] = useState<ResultDetail | null>(null);
  const [busy, setBusy] = useState(false);
  const [requesting, setRequesting] = useState(false);
  const [requested, setRequested] = useState(false);

  async function openResult(row: OrderRow) {
    if (!row.firstLineId) return;
    setBusy(true);
    setDetail(null);
    setRequesting(false);
    setRequested(false);
    setOpen(true);
    try {
      setDetail(await api.resultDetail(row.id, row.firstLineId));
    } finally {
      setBusy(false);
    }
  }

  async function submitRequest(input: { purposeCode: string; justification: string; requestedTtlHours: number }) {
    if (!detail) return;
    await api.requestReportAccess({ orderId: detail.orderId, lineId: detail.lineId, ...input });
    setRequesting(false);
    setRequested(true);
  }

  const cols: Column<OrderRow>[] = [
    ...orderColumns(t, fmt),
    {
      key: "result",
      header: t(S.result),
      cell: (r) =>
        r.firstLineId ? (
          <Button variant="secondary" onClick={() => openResult(r)}>
            {t(S.viewResult)}
          </Button>
        ) : (
          <span className="muted">—</span>
        ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.resultsTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.resultsEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.resultsTitle)} />}
        </AsyncSection>
      </Card>

      <Modal open={open} onOpenChange={setOpen} title={t(S.resultTitle)}>
        {busy && (
          <div role="status" aria-live="polite" style={{ padding: "var(--sp4)" }}>
            <span className="mrs-spin" aria-hidden="true" />
          </div>
        )}
        {!busy && detail && detail.restricted && !requesting && (
          <RestrictedResultCard result={detail} onRequestAccess={() => setRequesting(true)} />
        )}
        {!busy && detail && detail.restricted && requesting && (
          <RequestAccessDialog onSubmit={submitRequest} onCancel={() => setRequesting(false)} />
        )}
        {!busy && requested && (
          <p role="status" aria-live="polite" style={{ marginTop: "var(--sp3)" }}>
            {t(S.accessRequested)}
          </p>
        )}
        {!busy && detail && !detail.restricted && (
          <dl style={{ display: "grid", gridTemplateColumns: "auto 1fr", gap: "4px 12px" }}>
            <dt style={{ opacity: 0.7 }}>{t(S.type)}</dt>
            <dd>{detail.category}</dd>
            <dt style={{ opacity: 0.7 }}>{t(S.code)}</dt>
            <dd className="tnum">{detail.code}</dd>
            <dt style={{ opacity: 0.7 }}>{t(S.value)}</dt>
            <dd>{detail.value}</dd>
            <dt style={{ opacity: 0.7 }}>{t(S.status)}</dt>
            <dd>{detail.status}</dd>
          </dl>
        )}
      </Modal>
    </>
  );
}

/** Prescriptions — e-prescriptions I authored. */
export function DoctorPrescriptions() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const state = useAsync<RxRow[]>(() => api.prescriptionsMine(), []);
  const cols: Column<RxRow>[] = [
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.beneficiary.token}</span> },
    { key: "lines", header: t(S.lines), cell: (r) => <span className="tnum">{r.lineCount}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "submitted", header: t(S.submitted), cell: (r) => <span className="tnum">{fmt.date(r.submittedAt)}</span> },
  ];
  return (
    <>
      <PageHeader title={t(S.rxTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.rxEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.rxTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

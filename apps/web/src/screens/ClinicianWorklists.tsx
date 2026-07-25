import { Card, DataTable, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, OrderRow, PatientListItem, RxRow } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  patientsTitle: { en: "My patients", ar: "مرضاي" },
  patientsEmpty: { en: "No patients on your worklist.", ar: "لا يوجد مرضى في قائمتك." },
  ordersTitle: { en: "Orders", ar: "الطلبات" },
  ordersEmpty: { en: "You haven't placed any orders.", ar: "لم تقم بطلب أي فحوصات." },
  rxTitle: { en: "Prescriptions", ar: "الوصفات" },
  rxEmpty: { en: "You haven't written any prescriptions.", ar: "لم تكتب أي وصفات." },
  resultsTitle: { en: "Results inbox", ar: "صندوق النتائج" },
  resultsEmpty: { en: "No completed results yet.", ar: "لا توجد نتائج مكتملة بعد." },
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
} satisfies Record<string, Localized>;

const dt = (s?: string | null) => (s ? new Date(s).toLocaleDateString() : "—");

/** My patients — the caller's own encounters (treating-relationship gated server-side). */
export function DoctorPatients() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<PatientListItem[]>(() => api.listPatients(), []);
  const cols: Column<PatientListItem>[] = [
    { key: "patient", header: t(S.patient), cell: (r) => t(r.name) },
    { key: "mrn", header: t(S.mrn), cell: (r) => <span className="tnum">{r.mrn}</span> },
    { key: "lastVisit", header: t(S.lastVisit), cell: (r) => <span className="tnum">{dt(r.lastVisit)}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];
  return (
    <>
      <PageHeader title={t(S.patientsTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.patientsEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.patientsTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

function orderColumns(t: (l: Localized) => string): Column<OrderRow>[] {
  return [
    { key: "orderNo", header: t(S.orderNo), cell: (r) => <span className="tnum">{r.orderNo}</span> },
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.beneficiary.token}</span> },
    { key: "type", header: t(S.type), cell: (r) => r.orderType },
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.primaryCode}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "placed", header: t(S.placed), cell: (r) => <span className="tnum">{dt(r.requestedAt)}</span> },
  ];
}

/** Orders — everything I've ordered (investigation orders I authored). */
export function DoctorOrders() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<OrderRow[]>(() => api.ordersMine(), []);
  const cols = orderColumns(t);
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
export function DoctorResults() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<OrderRow[]>(() => api.ordersMine("Completed"), []);
  const cols = orderColumns(t);
  return (
    <>
      <PageHeader title={t(S.resultsTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.resultsEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.resultsTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Prescriptions — e-prescriptions I authored. */
export function DoctorPrescriptions() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<RxRow[]>(() => api.prescriptionsMine(), []);
  const cols: Column<RxRow>[] = [
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.beneficiary.token}</span> },
    { key: "lines", header: t(S.lines), cell: (r) => <span className="tnum">{r.lineCount}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "submitted", header: t(S.submitted), cell: (r) => <span className="tnum">{dt(r.submittedAt)}</span> },
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

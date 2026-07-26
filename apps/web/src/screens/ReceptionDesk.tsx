import { useState } from "react";
import { Button, Card, DataTable, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AppointmentRow, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { ApiError } from "../api/http";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  visitsTitle: { en: "Today's visits", ar: "زيارات اليوم" },
  visitsEmpty: { en: "No one is checked in yet.", ar: "لا يوجد أحد قد سجّل وصوله بعد." },
  apptTitle: { en: "Appointments", ar: "المواعيد" },
  apptEmpty: { en: "No appointments booked for today.", ar: "لا توجد مواعيد محجوزة اليوم." },
  checkinTitle: { en: "Check-in", ar: "تسجيل الوصول" },
  checkinEmpty: { en: "No arrivals waiting to be checked in.", ar: "لا يوجد وافدون بانتظار تسجيل الوصول." },
  beneficiary: { en: "Beneficiary", ar: "المستفيد" },
  type: { en: "Type", ar: "النوع" },
  time: { en: "Time", ar: "الوقت" },
  status: { en: "Status", ar: "الحالة" },
  action: { en: "Action", ar: "إجراء" },
  checkIn: { en: "Check in", ar: "تسجيل الوصول" },
  checkedIn: { en: "Checked in", ar: "تم الوصول" },
  stale: {
    en: "This appointment changed since the board loaded — refreshing.",
    ar: "تغيّر هذا الموعد منذ تحميل اللوحة — يجري التحديث.",
  },
} satisfies Record<string, Localized>;

const timeOf = (iso: string) =>
  new Date(iso).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });

/** Shared columns for a read-only appointment board (masked beneficiary token + type/time/status). */
function boardColumns(t: (l: Localized) => string): Column<AppointmentRow>[] {
  return [
    { key: "beneficiary", header: t(S.beneficiary), cell: (r) => <span className="tnum">{r.beneficiary.token}</span> },
    { key: "type", header: t(S.type), cell: (r) => r.appointmentType },
    { key: "time", header: t(S.time), cell: (r) => <span className="tnum">{timeOf(r.scheduledStart)}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];
}

/** Today's visits — everyone who has arrived and is waiting (CheckedIn). */
export function ReceptionVisits() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<AppointmentRow[]>(() => api.appointments("checked-in"), []);
  const cols = boardColumns(t);
  return (
    <>
      <PageHeader title={t(S.visitsTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.visitsEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.visitsTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Full day board — every appointment scheduled for today, any status. */
export function ReceptionAppointments() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<AppointmentRow[]>(() => api.appointments("all"), []);
  const cols = boardColumns(t);
  return (
    <>
      <PageHeader title={t(S.apptTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.apptEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.apptTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Arrivals desk — Booked appointments with a check-in action (Booked → CheckedIn, enqueues a walk-in ticket). */
export function ReceptionCheckIn() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<AppointmentRow[]>(() => api.appointments("booked"), []);
  const [busy, setBusy] = useState<string | null>(null);
  const [done, setDone] = useState<Set<string>>(new Set());
  const [stale, setStale] = useState(false);

  async function doCheckIn(row: AppointmentRow) {
    setBusy(row.id);
    setStale(false);
    try {
      // Echo the version we read (opt-in If-Match): a concurrent transition invalidates our board → 412.
      await api.checkIn(row.id, row.rowVersion);
      setDone((prev) => new Set(prev).add(row.id));
    } catch (e) {
      // 412 = the row moved under us (already checked in / rescheduled elsewhere). Re-load the board rather
      // than double-acting; any other failure re-throws for the generic handler.
      if (e instanceof ApiError && e.status === 412) {
        setStale(true);
        state.reload();
      } else {
        throw e;
      }
    } finally {
      setBusy(null);
    }
  }

  const cols: Column<AppointmentRow>[] = [
    ...boardColumns(t),
    {
      key: "action",
      header: t(S.action),
      cell: (r) =>
        done.has(r.id) ? (
          <StatusChip kind="ok" label={t(S.checkedIn)} />
        ) : (
          <Button variant="primary" size="sm" loading={busy === r.id} disabled={!r.checkInEligible} onClick={() => void doCheckIn(r)}>
            {t(S.checkIn)}
          </Button>
        ),
    },
  ];
  return (
    <>
      <PageHeader title={t(S.checkinTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.checkinEmpty}>
          {(rows) => (
            <div aria-live="polite">
              {stale && <StatusChip kind="warn" label={t(S.stale)} />}
              <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.checkinTitle)} />
            </div>
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

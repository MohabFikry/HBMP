import { useState } from "react";
import { useFormat, type Formatters } from "../i18n/useFormat";
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

/**
 * Shared columns for a read-only appointment board (masked beneficiary token + type/time/status).
 *
 * 18.D2 (audit R2 U7) — the appointment TIME is the headline case. This used to be
 * `toLocaleTimeString(undefined, …)`, which formats in the MACHINE's time zone: a clinic PC set to UTC —
 * the default on a fresh Linux image and in every container — rendered a 09:00 Cairo appointment as 07:00.
 * Nothing errored. The receptionist read 07:00, told the patient 07:00, and the patient missed their slot
 * or arrived two hours early. DST made the error change size mid-year. The formatter is now passed in,
 * pinned to Africa/Cairo and the app's own locale.
 */
function boardColumns(t: (l: Localized) => string, fmt: Formatters): Column<AppointmentRow>[] {
  return [
    { key: "beneficiary", header: t(S.beneficiary), cell: (r) => <span className="tnum">{r.beneficiary.token}</span> },
    { key: "type", header: t(S.type), cell: (r) => r.appointmentType },
    { key: "time", header: t(S.time), cell: (r) => <span className="tnum">{fmt.time(r.scheduledStart)}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];
}

/** Today's visits — everyone who has arrived and is waiting (CheckedIn). */
export function ReceptionVisits() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Cairo appointment times, app locale
  const state = useAsync<AppointmentRow[]>(() => api.appointments("checked-in"), []);
  const cols = boardColumns(t, fmt);
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
  const fmt = useFormat();   // 18.D2 (U7) — Cairo appointment times, app locale
  const state = useAsync<AppointmentRow[]>(() => api.appointments("all"), []);
  const cols = boardColumns(t, fmt);
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
  const fmt = useFormat();   // 18.D2 (U7) — Cairo appointment times, app locale
  const state = useAsync<AppointmentRow[]>(() => api.appointments("booked"), []);
  const [busy, setBusy] = useState<string | null>(null);
  const [stale, setStale] = useState(false);

  /**
   * 18.D1 (audit R2 E3) — check-in renders SERVER-CONFIRMED state only.
   *
   * The rule: a read may be optimistic; a server-invariant operation (book, consume, dispense, decide,
   * check-in, cancel) may not. This screen kept a local `done` set and painted a green "Checked in" chip from
   * it. The chip was therefore a record of the request having been SENT, not of the patient having been
   * checked in — and after a partial failure, a reload, or a concurrent transition elsewhere, the board and
   * the truth disagreed with no way for the receptionist to tell. Now the call is followed by a reload and
   * the chip is derived from the row's own status.
   */
  async function doCheckIn(row: AppointmentRow) {
    setBusy(row.id);
    setStale(false);
    try {
      // Echo the version we read (opt-in If-Match): a concurrent transition invalidates our board → 412.
      await api.checkIn(row.id, row.rowVersion);
      state.reload();
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
    ...boardColumns(t, fmt),
    {
      key: "action",
      header: t(S.action),
      cell: (r) =>
        // Derived from the row the SERVER returned, never from a local "we sent it" flag.
        r.checkedIn ? (
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

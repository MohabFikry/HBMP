import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useFormat, type Formatters } from "../i18n/useFormat";
import { Button, Card, DataTable, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AppointmentRow, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { ApiError } from "../api/http";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { VisitTimelineButton } from "./VisitTimeline";

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
  openFile: { en: "Patient file", ar: "ملف المريض" },
  noShow: { en: "No-show", ar: "لم يحضر" },
  noShowHint: {
    en: "Available once the appointment window has passed.",
    ar: "يتاح بعد انقضاء وقت الموعد.",
  },
  actions: { en: "Actions", ar: "الإجراءات" },
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

/**
 * The patient-file action (design 39 §6 — the profile is opened FOR someone from a worklist, never from a
 * menu). Reception's boards are the list the desk actually works from, and every row already names a
 * beneficiary; without this the unified profile had no entry point on this side of the building at all.
 * The SERVER decides which sections reception may see, so the same route serves every portal.
 */
function patientFileColumn(t: (l: Localized) => string, go: (to: string) => void): Column<AppointmentRow> {
  return {
    key: "file",
    // A real header, not "": an empty <th> has no accessible name (axe empty-table-header). The fixture routes
    // render no rows, which is why the route-level sweep never surfaced this.
    header: t(S.openFile),
    cell: (r) => (
      <Button variant="secondary" size="sm" onClick={() => go(`/patients/${encodeURIComponent(r.beneficiary.id)}`)}>
        {t(S.openFile)}
      </Button>
    ),
  };
}

/** Today's visits — everyone who has arrived and is waiting (CheckedIn). */
export function ReceptionVisits() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Cairo appointment times, app locale
  const navigate = useNavigate();
  const state = useAsync<AppointmentRow[]>(() => api.appointments("checked-in"), []);
  const cols = [...boardColumns(t, fmt), patientFileColumn(t, navigate)];
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

/**
 * The day board — every appointment today in any status, and the two decisions the desk makes about each one:
 * the patient arrived (check-in) or they did not (no-show).
 *
 * Both actions come from SERVER flags, never from re-reading the row's status or the clock here.
 * `noShowEligible` in particular is a grace period after the scheduled end that only emr knows: offering the
 * button early produces a 409 the receptionist cannot explain, offering it late leaves someone who never
 * arrived sitting Booked all day, and a clinic PC with a drifting clock would be wrong either way.
 */
export function ReceptionAppointments() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Cairo appointment times, app locale
  const navigate = useNavigate();
  const state = useAsync<AppointmentRow[]>(() => api.appointments("all"), []);
  const desk = useDeskTransitions(state.reload);

  const cols: Column<AppointmentRow>[] = [
    ...boardColumns(t, fmt),
    patientFileColumn(t, navigate),
    {
      key: "actions",
      header: t(S.actions),
      cell: (r) => (
        <span className="row-actions">
          <VisitTimelineButton row={r} />
          {r.checkInEligible && (
            <Button variant="primary" size="sm" loading={desk.busy === `in:${r.id}`}
                    onClick={() => void desk.run(`in:${r.id}`, () => api.checkIn(r.id, r.rowVersion))}>
              {t(S.checkIn)}
            </Button>
          )}
          {/* Shown only while the server says it is allowed — the desk is never offered a refusal. */}
          {r.noShowEligible && (
            <Button variant="secondary" size="sm" loading={desk.busy === `ns:${r.id}`}
                    onClick={() => void desk.run(`ns:${r.id}`, () => api.noShow(r.id, r.rowVersion))}>
              {t(S.noShow)}
            </Button>
          )}
          {/* A Booked row whose window has not passed: say WHY there is no no-show button rather than
              leaving an empty cell the receptionist reads as a broken screen. */}
          {r.checkInEligible && !r.noShowEligible && <span className="muted">{t(S.noShowHint)}</span>}
        </span>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.apptTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.apptEmpty}>
          {(rows) => (
            <div aria-live="polite">
              {desk.stale && <StatusChip kind="warn" label={t(S.stale)} />}
              <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.apptTitle)} />
            </div>
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

/**
 * Shared mechanics for a desk transition: one in flight at a time, a 412 re-reads the board instead of
 * double-acting, and the row's own status — reloaded from the server — is the only thing that paints the
 * result. 18.D1 (E3): a chip driven by "we sent the request" is a chip that lies after a partial failure.
 */
function useDeskTransitions(reload: () => void) {
  const [busy, setBusy] = useState<string | null>(null);
  const [stale, setStale] = useState(false);

  async function run(key: string, action: () => Promise<unknown>) {
    setBusy(key);
    setStale(false);
    try {
      await action();
      reload();
    } catch (e) {
      // 412 = the row moved under us (checked in at another desk, cancelled, already no-showed).
      if (e instanceof ApiError && e.status === 412) {
        setStale(true);
        reload();
      } else {
        throw e;
      }
    } finally {
      setBusy(null);
    }
  }

  return { busy, stale, run };
}

/** Arrivals desk — Booked appointments with a check-in action (Booked → CheckedIn, enqueues a walk-in ticket). */
export function ReceptionCheckIn() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Cairo appointment times, app locale
  const navigate = useNavigate();
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
    patientFileColumn(t, navigate),
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

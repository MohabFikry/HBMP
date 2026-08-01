import { useMemo, useState } from "react";
import { useFormat } from "../i18n/useFormat";
import { Button, Card, DataTable, Icon, InlineAlert, InputField, StatusChip, TableToolbar } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AppointmentRow, Localized, Practitioner, Specialty } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { ApiError } from "../api/http";
import { AsyncSection, PageHeader, useLoc, useOpenProfile } from "./_shared";
import { useRestorableState } from "./useRestorableState";
import { VisitTimelineButton } from "./VisitTimeline";
import {
  CancelAppointmentButton, doctorColumns, noteColumn, patientColumn, timeAndStatusColumns,
} from "./booking/appointmentColumns";
import { EditAppointmentButton } from "./booking/EditAppointment";

const S = {
  visitsTitle: { en: "Today's Visits", ar: "زيارات اليوم" },
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
  needsReassign: { en: "Doctor unavailable", ar: "الطبيب غير متاح" },
  needsReassignWhy: {
    en: "The assigned doctor no longer works at this clinic — call the patient to reassign or rebook.",
    ar: "الطبيب المعيَّن لم يعد يعمل في هذه العيادة — اتصل بالمريض لإعادة التعيين أو الحجز.",
  },
  search: { en: "Search", ar: "بحث" },
  searchHint: { en: "Patient token or type", ar: "رمز المريض أو النوع" },
  when: { en: "When", ar: "المدة" },
  today: { en: "Today", ar: "اليوم" },
  customRange: { en: "Custom range", ar: "مدة مخصصة" },
  from: { en: "From", ar: "من" },
  to: { en: "To", ar: "إلى" },
  rangeIncomplete: {
    en: "Pick both dates to apply the custom range — showing today until then.",
    ar: "اختر التاريخين لتطبيق المدة المخصصة — يتم عرض اليوم حتى ذلك الحين.",
  },
  fBooked: { en: "Booked", ar: "محجوز" },
  fCheckedIn: { en: "Checked in", ar: "تم الوصول" },
  fNoShow: { en: "No-show", ar: "لم يحضر" },
  noneMatch: {
    en: "No appointments match these filters. Clear a filter to see more.",
    ar: "لا توجد مواعيد مطابقة لعوامل التصفية. أزل أحد العوامل لعرض المزيد.",
  },
  stale: {
    en: "This appointment changed since the board loaded — refreshing.",
    ar: "تغيّر هذا الموعد منذ تحميل اللوحة — يجري التحديث.",
  },
} satisfies Record<string, Localized>;

/** Stable empties: `?? []` mints a new array each render and defeats the memo keyed on it. */
const NO_PRACTITIONERS: Practitioner[] = [];
const NO_SPECIALTIES: Specialty[] = [];

/**
 * The board's identity + scheduling columns, now shared verbatim with the call centre
 * (`booking/appointmentColumns`).
 *
 * 18.D2 (audit R2 U7) — the appointment TIME remains the headline case there. It used to be
 * `toLocaleTimeString(undefined, …)`, which formats in the MACHINE's time zone: a clinic PC set to UTC —
 * the default on a fresh Linux image and in every container — rendered a 09:00 Cairo appointment as 07:00.
 * Nothing errored. The receptionist read 07:00, told the patient 07:00, and the patient missed their slot or
 * arrived two hours early. The formatter is passed in, pinned to Africa/Cairo and the app's own locale.
 */
/**
 * The patient-file action (design 39 §6 — the profile is opened FOR someone from a worklist, never from a
 * menu). Reception's boards are the list the desk actually works from, and every row already names a
 * beneficiary; without this the unified profile had no entry point on this side of the building at all.
 * The SERVER decides which sections reception may see, so the same route serves every portal.
 */
function patientFileColumn(
  t: (l: Localized) => string,
  openProfile: (beneficiaryId: string) => void,
): Column<AppointmentRow> {
  return {
    key: "file",
    // A real header, not "": an empty <th> has no accessible name (axe empty-table-header). The fixture routes
    // render no rows, which is why the route-level sweep never surfaced this.
    header: t(S.openFile),
    // Icon + a stronger variant: this is the action reception reaches for most on the board, and as a plain
    // secondary button among several it read as the least important thing in the row.
    cell: (r) => (
      <Button
        variant="primary"
        size="sm"
        leadingIcon={<Icon name="user" />}
        onClick={() => openProfile(r.beneficiary.id)}
      >
        {t(S.openFile)}
      </Button>
    ),
  };
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
  // Carries where we came from, so the profile's Back control returns to this board rather than guessing.
  const openProfile = useOpenProfile();

  // ---- filters (14.5) --------------------------------------------------------------------------------
  // `when` and the custom range are SERVER-side, because they change which rows exist; `status` and the
  // search are client-side over what came back, because they narrow rows already in hand. Mixing the two
  // freely would mean a status filter that silently missed appointments outside today.
  //
  // RESTORED across a visit to a patient's file. Narrowing this board is real work — a custom date range and
  // a status, typed once and applied to a day's list — and the desk opens a patient file FROM a row, so the
  // round trip used to throw all of it away and drop the receptionist back on an unfiltered "today".
  const [when, setWhen] = useRestorableState<string | null>("reception-appts.when", "today");
  const [from, setFrom] = useRestorableState("reception-appts.from", "");
  const [to, setTo] = useRestorableState("reception-appts.to", "");
  const [status, setStatus] = useRestorableState<string | null>("reception-appts.status", null);
  const [query, setQuery] = useRestorableState("reception-appts.query", "");

  const customActive = when === "custom" && from !== "" && to !== "";
  const range = customActive ? { from, to } : undefined;
  const state = useAsync<AppointmentRow[]>(
    () => api.appointments("all", false, range),
    // Re-fetch only when the SERVER-side inputs change. Typing in the search box must not re-hit the API on
    // every keystroke — that is the whole reason the two kinds of filter are split.
    [range?.from, range?.to],
  );
  const desk = useDeskTransitions(state.reload);

  // Doctor + specialty are provider-service's to disclose; reception reads them directly under
  // `practitioner:read` and joins here. emr returns only a doctorId — see `doctorColumns`.
  const practitioners = useAsync<Practitioner[]>(() => api.practitioners({ type: "Doctor" }), []);
  const specialties = useAsync<Specialty[]>(() => api.specialties(), []);
  const doctorById = useMemo(
    () => new Map((practitioners.data ?? NO_PRACTITIONERS).map((d) => [d.id, d])),
    [practitioners.data],
  );

  const visible = (rows: AppointmentRow[]) => {
    const q = query.trim().toLowerCase();
    return rows.filter((r) => {
      if (status === "booked" && !r.checkInEligible) return false;
      if (status === "checked-in" && !r.checkedIn) return false;
      if (status === "no-show" && r.status.label.en !== "No-show") return false;
      if (!q) return true;
      // The NAME is what the desk reads and what a patient gives at the counter, so it is what the search
      // has to match. The token stays in the haystack for rows booked before names were captured.
      const doctor = r.doctorId ? doctorById.get(r.doctorId)?.name.en ?? "" : "";
      return `${r.beneficiaryName ?? ""} ${r.beneficiary.token} ${r.appointmentType} ${doctor}`
        .toLowerCase().includes(q);
    });
  };

  const deps = { t, fmt, doctorById, specialties: specialties.data ?? NO_SPECIALTIES };
  const cols: Column<AppointmentRow>[] = [
    patientColumn(deps),
    ...doctorColumns(deps),
    ...timeAndStatusColumns(deps),
    noteColumn(deps),
    patientFileColumn(t, openProfile),
    {
      key: "actions",
      header: t(S.actions),
      cell: (r) => (
        <span className="row-actions">
          {/* 14.5 — the doctor stopped serving this branch after the booking was made. Nothing was changed
              automatically, so this row is asking the desk for a decision rather than reporting one. The
              reason is spelled out because "Doctor unavailable" alone does not tell anyone what to do. */}
          {r.needsReassignment && (
            <>
              <StatusChip kind="warn" label={t(S.needsReassign)} />
              <span className="muted">{t(S.needsReassignWhy)}</span>
            </>
          )}
          <VisitTimelineButton row={r} />
          {/* Check-in lives HERE now, and the separate Check-in screen is gone. It was always the same
              server call against a filtered view of this same board, so the second screen only added a
              place for the two to disagree — and a decision about where to click before doing the work. */}
          {r.checkInEligible && (
            <Button variant="primary" size="sm" leadingIcon={<Icon name="ok" />}
                    loading={desk.busy === `in:${r.id}`}
                    onClick={() => void desk.run(`in:${r.id}`, () => api.checkIn(r.id, r.rowVersion))}>
              {t(S.checkIn)}
            </Button>
          )}
          {/* Shown only while the server says it is allowed — the desk is never offered a refusal. */}
          {r.noShowEligible && (
            <Button variant="secondary" size="sm" leadingIcon={<Icon name="cross" />}
                    loading={desk.busy === `ns:${r.id}`}
                    onClick={() => void desk.run(`ns:${r.id}`, () => api.noShow(r.id, r.rowVersion))}>
              {t(S.noShow)}
            </Button>
          )}
          {/* A Booked row whose window has not passed: say WHY there is no no-show button rather than
              leaving an empty cell the receptionist reads as a broken screen. */}
          {r.checkInEligible && !r.noShowEligible && <span className="muted">{t(S.noShowHint)}</span>}
          {/* Last in the row, and a confirmation away: cancelling releases the slot and may hand it straight
              to the waitlist, so a single mis-click in a dense table must not be able to do it. */}
          {/* Edit sits beside cancel: they are the two amendments to an appointment that is still going to
              happen, and both are recorded on its timeline. */}
          <EditAppointmentButton row={r} t={t} onSaved={state.reload} />
          <CancelAppointmentButton
            row={r}
            t={t}
            onCancel={(reason) => desk.run(`cx:${r.id}`, () => api.cancelAppointment(r.id, reason, r.rowVersion))}
          />
        </span>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.apptTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <TableToolbar
          search={{ label: t(S.search), value: query, onChange: setQuery, placeholder: t(S.searchHint) }}
          filters={[
            {
              key: "when", label: t(S.when), value: when, onChange: setWhen,
              options: [{ value: "today", label: t(S.today) }, { value: "custom", label: t(S.customRange) }],
              // Beside the chip that reveals them, not at the far end of the bar. They appear only once
              // "Custom" is chosen: two empty date boxes sitting permanently next to a "Today" chip invite
              // the desk to wonder which of the two is actually in force.
              extra: when === "custom" ? (
                <>
                  <InputField label={t(S.from)} type="date" value={from} onChange={(e) => setFrom(e.currentTarget.value)} />
                  <InputField label={t(S.to)} type="date" value={to} onChange={(e) => setTo(e.currentTarget.value)} />
                </>
              ) : undefined,
            },
            {
              key: "status", label: t(S.status), value: status, onChange: setStatus,
              options: [
                { value: "booked", label: t(S.fBooked) },
                { value: "checked-in", label: t(S.fCheckedIn) },
                { value: "no-show", label: t(S.fNoShow) },
              ],
            },
          ]}
        />

        {/* Chosen "Custom" but not yet finished filling it in — say what is still showing rather than
            leaving the board looking filtered when it is not. */}
        {when === "custom" && !customActive && <InlineAlert tone="info">{t(S.rangeIncomplete)}</InlineAlert>}

        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.apptEmpty}>
          {(rows) => {
            const shown = visible(rows);
            return (
              <div aria-live="polite">
                {desk.stale && <StatusChip kind="warn" label={t(S.stale)} />}
                {shown.length === 0 ? (
                  // Distinct from the empty board above: rows EXIST, the filters hid them. Telling the desk
                  // "no appointments today" when they have simply filtered to a status with none is how
                  // someone concludes the system lost their bookings.
                  <InlineAlert tone="info">{t(S.noneMatch)}</InlineAlert>
                ) : (
                  <DataTable columns={cols} rows={shown} rowKey={(r) => r.id} caption={t(S.apptTitle)} />
                )}
              </div>
            );
          }}
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

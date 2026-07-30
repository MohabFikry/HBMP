import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, DataTable, Icon, InlineAlert, KpiCard, Modal, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AppointmentCounts, AppointmentRow, Localized, Practitioner, Specialty } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { AppointmentNoteButton } from "./AppointmentNote";
import { patientColumn } from "./booking/appointmentColumns";

const S = {
  title: { en: "Dashboard", ar: "لوحة المتابعة" },

  cardTotal: { en: "Appointments", ar: "المواعيد" },
  today: { en: "Today", ar: "اليوم" },
  prevDay: { en: "Previous day", ar: "اليوم السابق" },
  nextDay: { en: "Next day", ar: "اليوم التالي" },
  jumpToDay: { en: "Choose a day", ar: "اختر يومًا" },
  goToday: { en: "Today", ar: "اليوم" },
  prevMonth: { en: "Previous month", ar: "الشهر السابق" },
  nextMonth: { en: "Next month", ar: "الشهر التالي" },
  close: { en: "Close", ar: "إغلاق" },
  cardCheckedIn: { en: "Checked in", ar: "تم الوصول" },
  cardNoShow: { en: "No-shows", ar: "لم يحضروا" },
  countsFailed: {
    en: "Couldn't load today's figures — the cards below are not current.",
    ar: "تعذّر تحميل أرقام اليوم — البطاقات أدناه ليست محدّثة.",
  },
  retry: { en: "Retry", ar: "إعادة المحاولة" },

  visitsHeading: { en: "Visits", ar: "الزيارات" },
  visitsEmpty: { en: "No one has checked in on this day.", ar: "لم يسجّل أحد وصوله في هذا اليوم." },
  patient: { en: "Patient", ar: "المريض" },
  doctor: { en: "Doctor", ar: "الطبيب" },
  specialty: { en: "Specialty", ar: "التخصص" },
  time: { en: "Time", ar: "الوقت" },
  note: { en: "Note", ar: "ملاحظة" },
  openFile: { en: "Patient file", ar: "ملف المريض" },
  unnamedDoctor: { en: "No named doctor", ar: "بدون طبيب محدد" },

  calendarHeading: { en: "Schedule", ar: "الجدول" },
  calendarEmpty: { en: "No appointments booked for this day.", ar: "لا توجد مواعيد محجوزة في هذا اليوم." },
} satisfies Record<string, Localized>;

const ZONE = "Africa/Cairo";

/** `YYYY-MM-DD` in Cairo — the key the day nav and the server both speak. */
const cairoDayKey = (d: Date) =>
  new Intl.DateTimeFormat("en-CA", { timeZone: ZONE, year: "numeric", month: "2-digit", day: "2-digit" }).format(d);

const cairoToday = () => cairoDayKey(new Date());

/** Noon UTC for a day key — the same calendar day in Cairo under either offset, and on either side of DST. */
const dayInstant = (key: string) => `${key}T12:00:00Z`;

/**
 * The span the schedule shows when nothing forces it wider — ordinary clinic hours.
 *
 * It is a FLOOR, not a fixed list. An appointment outside it used to fall into a trailing "Other" band, so a
 * 21:00 booking and a 01:33 one sat together at the bottom, out of sequence and detached from the timeline
 * they belong to — which is precisely where an unusual appointment most needs to be seen in context.
 */
const DEFAULT_FIRST_HOUR = 8;
const DEFAULT_LAST_HOUR = 18;

/**
 * The reception dashboard (14.5).
 *
 * Three things the desk needs at a glance and previously had to assemble by eye across two screens: how the
 * day is going, who is in the building right now, and what is still to come.
 *
 * <b>On showing patient NAMES here.</b> Reception's other boards render a masked token, and that is right for
 * them. This section is different and the difference was signed off: the desk greets the person, walks them
 * to a room and arranges the rest of their journey, and none of that is possible against "•••4821". The name
 * is only present for patients who have ARRIVED — it is captured at check-in — so this list is exactly the
 * set of people physically in the building, which is also the narrowest set that answers the need.
 *
 * <b>Doctor and specialty are a client-side join.</b> emr holds a `doctorId`; who that is belongs to
 * provider-service, which reception reads directly under `practitioner:read`. Neither service composes the
 * other's data on the caller's behalf — see `bookableDoctors` for the same shape on the booking screen.
 */
export function ReceptionDashboard() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const navigate = useNavigate();

  /**
   * The day the whole dashboard is showing. Today by default — that is what the desk opens the screen for —
   * but a receptionist is constantly asked "who is in tomorrow?" and had no way to answer without leaving
   * this screen. One piece of state drives the cards, the visits table AND the schedule, so the three can
   * never disagree about which day they are describing.
   */
  const [day, setDay] = useState(() => cairoToday());
  const isToday = day === cairoToday();

  const counts = useAsync<AppointmentCounts>(() => api.appointmentCounts(dayInstant(day)), [day]);
  const board = useAsync<AppointmentRow[]>(
    // A single day expressed as a one-day range: the server expands each end to its own Cairo civil day, so
    // this picks up that day's evening clinic rather than stopping at midnight.
    () => api.appointments("all", false, { from: dayInstant(day), to: dayInstant(day) }),
    [day],
  );

  const [pickerOpen, setPickerOpen] = useState(false);
  const [pickerMonth, setPickerMonth] = useState(() => cairoToday().slice(0, 7));

  function stepDay(delta: number) {
    const d = new Date(`${day}T12:00:00Z`);
    d.setUTCDate(d.getUTCDate() + delta);
    setDay(cairoDayKey(d));
  }
  const doctors = useAsync<Practitioner[]>(() => api.practitioners({ type: "Doctor" }), []);
  const specialties = useAsync<Specialty[]>(() => api.specialties(), []);

  const doctorById = useMemo(
    () => new Map((doctors.data ?? []).map((d) => [d.id, d])),
    [doctors.data],
  );
  const specialtyName = useMemo(() => {
    const m = new Map((specialties.data ?? []).map((s) => [s.code, s.name]));
    // The code is the honest fallback while the reference list loads — a dash would claim the doctor has no
    // specialty, which is a different and worse statement.
    return (code?: string) => (code ? (m.get(code) ? t(m.get(code)!) : code) : null);
  }, [specialties.data, t]);

  const visits = useMemo(() => (board.data ?? []).filter((r) => r.checkedIn), [board.data]);

  /**
   * What a card shows. Zero is a NUMBER, not a dash — "0 appointments today" is a real and useful answer,
   * and rendering it as "—" would tell the desk the figure is unknown when it is in fact known and quiet.
   */
  const cardValue = (n?: number) =>
    counts.status === "loading" ? "…" : counts.status === "error" ? "—" : String(n ?? 0);

  const visitCols: Column<AppointmentRow>[] = [
    // The same patient column the boards use, so the three reception surfaces cannot drift on how a person
    // is identified.
    patientColumn({ t }),
    {
      key: "doctor", header: t(S.doctor), sortable: true,
      cell: (r) => {
        const d = r.doctorId ? doctorById.get(r.doctorId) : undefined;
        return d ? t(d.name) : <span className="muted">{t(S.unnamedDoctor)}</span>;
      },
      sortValue: (r) => (r.doctorId ? doctorById.get(r.doctorId)?.name.en : undefined),
    },
    {
      key: "specialty", header: t(S.specialty), sortable: true,
      cell: (r) => {
        const name = specialtyName(r.doctorId ? doctorById.get(r.doctorId)?.primarySpecialty : undefined);
        return name ?? <span className="muted">—</span>;
      },
      sortValue: (r) => specialtyName(r.doctorId ? doctorById.get(r.doctorId)?.primarySpecialty : undefined),
    },
    {
      key: "time", header: t(S.time), sortable: true,
      cell: (r) => <span className="tnum">{fmt.time(r.scheduledStart)}</span>,
      sortValue: (r) => r.scheduledStart,
    },
    { key: "note", header: t(S.note), cell: (r) => <AppointmentNoteButton note={r.note} /> },
    {
      key: "file",
      header: t(S.openFile),
      // Icon + primary: this is the action the desk reaches for most, and as a plain secondary among
      // several it read as the least important thing in the row.
      cell: (r) => (
        <Button
          variant="primary"
          size="sm"
          leadingIcon={<Icon name="user" />}
          onClick={() => navigate(`/patients/${encodeURIComponent(r.beneficiary.id)}`)}
        >
          {t(S.openFile)}
        </Button>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.title)} />

      {/*
        The day selector. Centred label between two arrows, and it says "Today" rather than a date when it is
        today — that is how the desk refers to it, and a date where "Today" belongs makes the reader stop and
        work out whether it is the current one.
      */}
      <div className="dash-daynav">
        <Button
          variant="ghost" aria-label={t(S.prevDay)} title={t(S.prevDay)}
          leadingIcon={<Icon name="chevron" style={{ transform: "rotate(90deg)" }} />}
          onClick={() => stepDay(-1)}
        />
        {/* The LABEL is the control. A separate "Back to today" button only appeared once you had navigated
            away, so the one moment you needed it was the one moment its position was unfamiliar — and it did
            nothing else. Making the label open a month puts jumping anywhere, including back to today, in the
            place you are already looking.
            aria-live: the arrows replace every figure on the page, and a keyboard user would otherwise hear
            nothing about which day they had moved to. */}
        <button
          type="button"
          className="dash-daynav-label"
          aria-haspopup="dialog"
          aria-label={`${isToday ? t(S.today) : fmt.date(dayInstant(day))} — ${t(S.jumpToDay)}`}
          onClick={() => { setPickerMonth(day.slice(0, 7)); setPickerOpen(true); }}
        >
          <span aria-live="polite">{isToday ? t(S.today) : fmt.date(dayInstant(day))}</span>
          <Icon name="chevron" aria-hidden="true" />
        </button>
        <Button
          variant="ghost" aria-label={t(S.nextDay)} title={t(S.nextDay)}
          leadingIcon={<Icon name="chevron" style={{ transform: "rotate(-90deg)" }} />}
          onClick={() => stepDay(1)}
        />
      </div>

      <DayPickerModal
        open={pickerOpen}
        month={pickerMonth}
        selected={day}
        t={t}
        fmt={fmt}
        onMonth={setPickerMonth}
        onPick={(picked) => { setDay(picked); setPickerOpen(false); }}
        onClose={() => setPickerOpen(false)}
      />

      {/* ── Cards ──────────────────────────────────────────────────────
          Counted server-side. Tallying the board here would be capped at its 200-row page and would
          undercount a busy day, in the direction nobody checks. */}
      <div className="dash-kpis">
        <KpiCard label={t(S.cardTotal)} value={cardValue(counts.data?.total)} />
        <KpiCard label={t(S.cardCheckedIn)} value={cardValue(counts.data?.checkedIn)} />
        <KpiCard label={t(S.cardNoShow)} value={cardValue(counts.data?.noShow)} />
      </div>
      {/*
        A failed count is SAID, not implied by a dash.

        These three cards previously rendered `?? "—"`, which collapsed three different situations into one
        glyph: still loading, failed to load, and a genuinely quiet morning with zero appointments. A desk
        reading "—" cannot tell "we don't know" from "there are none", and the second reading is the
        dangerous one — it invites someone to conclude the day is empty when the figure simply never arrived.
        Zero now renders as 0, loading renders as an ellipsis, and a failure says so and offers a retry.
      */}
      {counts.status === "error" && (
        <div role="alert" style={{ marginTop: "var(--sp2)" }}>
          <InlineAlert tone="bad">
            <span>{t(S.countsFailed)}</span>{" "}
            <Button variant="secondary" size="sm" onClick={counts.reload}>{t(S.retry)}</Button>
          </InlineAlert>
        </div>
      )}

      {/* ── Today's visits ─────────────────────────────────────────── */}
      <Card as="section" style={{ padding: "var(--sp3)", marginTop: "var(--sp4)" }}>
        <h2 className="section-h">{t(S.visitsHeading)}</h2>
        <AsyncSection<AppointmentRow[]> state={board} isEmpty={() => visits.length === 0} emptyLabel={S.visitsEmpty}>
          {() => (
            <DataTable columns={visitCols} rows={visits} rowKey={(r) => r.id} caption={t(S.visitsHeading)} />
          )}
        </AsyncSection>
      </Card>

      {/* ── The day, laid out ──────────────────────────────────────── */}
      <Card as="section" style={{ padding: "var(--sp3)", marginTop: "var(--sp4)" }}>
        <h2 className="section-h">{t(S.calendarHeading)}</h2>
        <AsyncSection<AppointmentRow[]> state={board} isEmpty={(d) => d.length === 0} emptyLabel={S.calendarEmpty}>
          {(rows) => <DaySchedule rows={rows} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/**
 * The clinic's day as hour bands.
 *
 * A calendar rather than a third table because the question it answers is shaped differently: not "what is
 * this appointment" but "when is the desk about to be busy". Bands are laid out from a FIXED hour list, so an
 * empty 11:00 renders as a visible gap — which is the answer to "can we fit a walk-in in?" and is exactly
 * what a list of only-the-booked-hours cannot show.
 */
function DaySchedule({ rows }: { rows: AppointmentRow[] }) {
  const t = useLoc();
  const fmt = useFormat();

  const { byHour, hours } = useMemo(() => {
    const m = new Map<number, AppointmentRow[]>();
    for (const r of rows) {
      // The CAIRO hour, not the browser's: a clinic PC on UTC would file a 09:00 appointment under 07:00 and
      // draw a schedule two bands out of step with every time printed on it.
      const hour = Number(
        new Intl.DateTimeFormat("en-GB", { timeZone: "Africa/Cairo", hour: "2-digit", hour12: false })
          .format(new Date(r.scheduledStart)),
      );
      m.set(hour, [...(m.get(hour) ?? []), r]);
    }
    // The range STRETCHES to cover whatever exists, rather than pushing outliers into a bucket at the end.
    // An early or late appointment is exactly the one the desk needs to see in its place in the day.
    const present = [...m.keys()];
    const first = Math.min(DEFAULT_FIRST_HOUR, ...present);
    const last = Math.max(DEFAULT_LAST_HOUR, ...present);
    return {
      byHour: m,
      hours: Array.from({ length: last - first + 1 }, (_, i) => first + i),
    };
  }, [rows]);

  return (
    // Fixed height + scroll: a stretched range can be twenty-odd rows, and letting the schedule grow
    // unbounded pushes everything below it off the page. The band heights stay constant so an empty hour is
    // still visibly empty — that gap is the answer to "can we fit a walk-in in?".
    <ol className="dash-day">
      {hours.map((h) => {
        const at = byHour.get(h) ?? [];
        return (
          <li key={h} className="dash-hour">
            <span className="dash-hour-label tnum">{String(h).padStart(2, "0")}:00</span>
            <div className="dash-hour-items">
              {at.map((r) => (
                <span key={r.id} className="dash-appt">
                  <span className="tnum">{fmt.time(r.scheduledStart)}</span>
                  <span>{r.beneficiaryName ?? r.beneficiary.token}</span>
                  <StatusChip kind={r.status.kind} label={t(r.status.label)} />
                </span>
              ))}
            </div>
          </li>
        );
      })}
    </ol>
  );
}

/**
 * The month picker behind the day label.
 *
 * A modal rather than a popover: it is focus-trapped, dismissible with Escape and returns focus on close for
 * free, and this is a deliberate jump rather than a hover-weight affordance. "Today" sits inside it, which is
 * where someone looking for it will already be — the old standalone button only existed once you had
 * navigated away, so it was unfamiliar at exactly the moment you wanted it.
 */
function DayPickerModal({
  open, month, selected, t, fmt, onMonth, onPick, onClose,
}: {
  open: boolean;
  month: string;
  selected: string;
  t: (l: Localized) => string;
  fmt: ReturnType<typeof useFormat>;
  onMonth: (m: string) => void;
  onPick: (day: string) => void;
  onClose: () => void;
}) {
  const [y, m] = month.split("-").map(Number);
  const monthIndex = m - 1;

  // Noon UTC anchors: Cairo is UTC+2/+3, so noon is the same calendar day under either offset and across a
  // DST change. Midnight anchoring is what makes a month grid render the 1st twice or skip it.
  const at = (yy: number, mm: number, dd: number) => new Date(Date.UTC(yy, mm, dd, 12));
  const total = new Date(Date.UTC(y, monthIndex + 1, 0)).getUTCDate();
  const days = Array.from({ length: total }, (_, i) => at(y, monthIndex, i + 1));
  // Egypt's week starts SATURDAY; getUTCDay() counts from Sunday.
  const lead = (days[0].getUTCDay() + 1) % 7;

  const monthLabel = new Intl.DateTimeFormat(fmt.locale, {
    timeZone: "Africa/Cairo", month: "long", year: "numeric",
  }).format(at(y, monthIndex, 1));

  const step = (delta: number) => onMonth(cairoDayKey(at(y, monthIndex + delta, 1)).slice(0, 7));

  return (
    <Modal
      open={open}
      onOpenChange={(o: boolean) => { if (!o) onClose(); }}
      title={t(S.jumpToDay)}
      footer={
        <>
          <Button variant="secondary" onClick={onClose}>{t(S.close)}</Button>
          <Button variant="primary" onClick={() => onPick(cairoToday())}>{t(S.goToday)}</Button>
        </>
      }
    >
      <div className="bk-cal bk-cal-sm">
        <div className="bk-cal-head">
          <Button
            variant="ghost" size="sm" aria-label={t(S.prevMonth)} title={t(S.prevMonth)}
            leadingIcon={<Icon name="chevron" style={{ transform: "rotate(90deg)" }} />}
            onClick={() => step(-1)}
          />
          <strong className="bk-cal-month" aria-live="polite">{monthLabel}</strong>
          <Button
            variant="ghost" size="sm" aria-label={t(S.nextMonth)} title={t(S.nextMonth)}
            leadingIcon={<Icon name="chevron" style={{ transform: "rotate(-90deg)" }} />}
            onClick={() => step(1)}
          />
        </div>
        {/* Weekday headings, from the same locale as the month name — a Latin header row over Arabic dates
            would be worse than none. 1 Aug 2026 is a Saturday, which is where Egypt's week starts. */}
        <div className="bk-cal-weekdays" aria-hidden="true">
          {Array.from({ length: 7 }, (_, i) => (
            <span key={i}>
              {new Intl.DateTimeFormat(fmt.locale, { timeZone: "Africa/Cairo", weekday: "short" })
                .format(at(2026, 7, 1 + i))}
            </span>
          ))}
        </div>
        <div className="bk-cal-grid" role="radiogroup" aria-label={t(S.jumpToDay)}>
          {Array.from({ length: lead }, (_, i) => <span key={`pad-${i}`} className="bk-cal-pad" aria-hidden="true" />)}
          {days.map((d) => {
            const key = cairoDayKey(d);
            return (
              <button
                key={key}
                type="button"
                role="radio"
                aria-checked={key === selected}
                className="bk-cal-day"
                aria-label={fmt.date(dayInstant(key))}
                onClick={() => onPick(key)}
              >
                <span className="bk-cal-num tnum">{d.getUTCDate()}</span>
              </button>
            );
          })}
        </div>
      </div>
    </Modal>
  );
}

import { useEffect, useMemo, useRef, useState } from "react";
import { Button, Card, DataTableView, Icon, InlineAlert, KpiCard, useTableQuery } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AppointmentCounts, AppointmentRow, Localized, Practitioner, Specialty } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc, useOpenProfile } from "./_shared";
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
  search: { en: "Search", ar: "بحث" },
  visitsSearchHint: { en: "Patient or doctor", ar: "المريض أو الطبيب" },
  noMatches: { en: "No visits match your search.", ar: "لا توجد زيارات مطابقة لبحثك." },
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
  // Records where the profile was opened FROM, so its Back control returns to this board rather than
  // guessing from history. A bare navigate() leaves `state.from` unset, and Back then falls back to -1 —
  // wrong after any in-page redirect and absent entirely on a fresh tab.
  const openProfile = useOpenProfile();

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
  /**
   * A doctor id → display name, or null when emr recorded none.
   *
   * <p>Lifted out of the visits column so the table and the schedule resolve a doctor the same way. They did
   * it separately before — the schedule not at all — and two joins over one map is how the two halves of a
   * screen start naming the same person differently.</p>
   */
  const doctorName = useMemo(
    () => (doctorId?: string | null) => {
      const d = doctorId ? doctorById.get(doctorId) : undefined;
      return d ? t(d.name) : null;
    },
    [doctorById, t],
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
      cell: (r) => doctorName(r.doctorId) ?? <span className="muted">{t(S.unnamedDoctor)}</span>,
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
          onClick={() => openProfile(r.beneficiary.id)}
        >
          {t(S.openFile)}
        </Button>
      ),
    },
  ];

  /** Today's checked-in visits. A busy desk's board grows all morning; the search is the patient's name. */
  const visitsQuery = useTableQuery({
    rows: visits,
    columns: visitCols,
    // The doctor's NAME, resolved through the same lookup the column uses — searching the raw id would
    // match nothing anyone at the desk can see or type.
    searchText: (r) => [r.beneficiaryName, doctorName(r.doctorId)].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.visitsSearchHint),
    pageSize: 25,
    persistKey: "reception-visits",
  });

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
        <div className="dash-daynav-anchor">
        <button
          type="button"
          className="dash-daynav-label"
          aria-haspopup="dialog"
          aria-expanded={pickerOpen}
          aria-label={`${isToday ? t(S.today) : fmt.date(dayInstant(day))} — ${t(S.jumpToDay)}`}
          onClick={() => { setPickerMonth(day.slice(0, 7)); setPickerOpen(true); }}
        >
          <span aria-live="polite">{isToday ? t(S.today) : fmt.date(dayInstant(day))}</span>
          <Icon name="chevron" aria-hidden="true" />
        </button>
        {/* Anchored to the DATE, not centred over the page. Choosing a day is a small adjustment to what is
            already on screen; a full modal dims everything the operator is comparing against and makes a
            two-second decision feel like leaving the page. */}
        {pickerOpen && (
          <DayPickerPopover
            month={pickerMonth}
            selected={day}
            t={t}
            fmt={fmt}
            onMonth={setPickerMonth}
            onPick={(picked) => { setDay(picked); setPickerOpen(false); }}
            onClose={() => setPickerOpen(false)}
          />
        )}
        </div>
        <Button
          variant="ghost" aria-label={t(S.nextDay)} title={t(S.nextDay)}
          leadingIcon={<Icon name="chevron" style={{ transform: "rotate(-90deg)" }} />}
          onClick={() => stepDay(1)}
        />
      </div>


      {/* ── Cards ──────────────────────────────────────────────────────
          Counted server-side. Tallying the board here would be capped at its 200-row page and would
          undercount a busy day, in the direction nobody checks. */}
      {/* The glyphs are decorative and marked so: the label names each figure in words, and the icon is what
          lets the desk find the right tile by shape on a board of three identical white cards. */}
      <div className="dash-kpis">
        <KpiCard
          label={t(S.cardTotal)} value={cardValue(counts.data?.total)}
          icon={<Icon name="calendar" />}
        />
        <KpiCard
          label={t(S.cardCheckedIn)} value={cardValue(counts.data?.checkedIn)}
          icon={<Icon name="check2" />}
        />
        {/* The only one of the three that counts something going WRONG, and it looked exactly like the other
            two. The tone marks the subject, not the figure — it stays red on a morning that reads 0, because
            the desk finds this tile by colour and a card that changes identity with its value cannot be
            found that way. The number itself stays in body colour for the same reason. */}
        <KpiCard
          label={t(S.cardNoShow)} value={cardValue(counts.data?.noShow)}
          icon={<Icon name="cross" />} tone="bad"
        />
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
            <DataTableView
              query={visitsQuery}
              columns={visitCols}
              rowKey={(r) => r.id}
              caption={t(S.visitsHeading)}
              emptyLabel={t(S.visitsEmpty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>

      {/* ── The day, laid out ──────────────────────────────────────── */}
      <Card as="section" style={{ padding: "var(--sp3)", marginTop: "var(--sp4)" }}>
        <h2 className="section-h">{t(S.calendarHeading)}</h2>
        <AsyncSection<AppointmentRow[]> state={board} isEmpty={(d) => d.length === 0} emptyLabel={S.calendarEmpty}>
          {(rows) => <DaySchedule rows={rows} doctorName={doctorName} />}
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
function DaySchedule({
  rows, doctorName,
}: {
  rows: AppointmentRow[];
  /** The client-side doctor join, passed in rather than refetched — see the screen's header note. */
  doctorName: (doctorId?: string | null) => string | null;
}) {
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
    // aria-label so the schedule is addressable as a landmark in its own right — a bare <ol> among the
    // page's other lists is "list" with nothing to tell it apart, by keyboard or by test.
    <ol className="dash-day mrs-scroll mrs-scroll-focusable" tabIndex={0} aria-label={t(S.calendarHeading)}>
      {hours.map((h) => {
        const at = byHour.get(h) ?? [];
        return (
          <li key={h} className="dash-hour">
            <span className="dash-hour-label tnum">{String(h).padStart(2, "0")}:00</span>
            <div className="dash-hour-items">
              {at.map((r) => {
                const doctor = doctorName(r.doctorId);
                return (
                  /*
                    One appointment.

                    The accent bar carries the status as a second, non-textual channel — the status word is
                    still written out beside it, because 21-accessibility-checklist forbids hue as the only
                    carrier. Reading a band of eight chips for "who has arrived" is a scan down the left edge,
                    which a pill in the middle of each chip cannot give you.

                    The NAME leads, at full weight. It was the same size as the time and the status and sat
                    third in the reading order behind both; the desk is looking for a person, and the time is
                    already implied by the band the chip is sitting in.
                  */
                  <span key={r.id} className={`dash-appt dash-appt--${r.status.kind}`}>
                    <span className="dash-appt-body">
                      <span className="dash-appt-top">
                        {/* `title` so a name too long for the chip is still readable on hover; the full
                            string stays in the DOM either way, so assistive technology is never truncated. */}
                        <span className="dash-appt-name" title={r.beneficiaryName ?? r.beneficiary.token}>
                          {r.beneficiaryName ?? r.beneficiary.token}
                        </span>
                        {/* The status rides WITH the name rather than on the line below it, because the two
                            are read as one answer — "Tarek Selim, booked". The bullet is a second, non-textual
                            channel at the point of reading; the word stays, per 21-accessibility §non-color
                            status, and the bullet is aria-hidden so it is never announced as a stray glyph. */}
                        <span className="dash-appt-status">
                          <span className="dash-appt-dot" aria-hidden="true" />
                          {t(r.status.label)}
                        </span>
                      </span>
                      {/*
                        The two facts underneath, each behind the glyph for what it is. They were a status word
                        and a name separated by a dot, which made the doctor's name look like a continuation of
                        the status; the icons say which KIND of thing each one is before it is read.
                      */}
                      <span className="dash-appt-meta">
                        <span className="dash-appt-fact">
                          <Icon name="clock" className="dash-appt-ico" width={13} height={13} />
                          <span className="tnum">{fmt.time(r.scheduledStart)}</span>
                        </span>
                        {/* The doctor answers "which room is this person going to", which is the question the
                            desk asks straight after "who". Absent is said in words rather than left blank —
                            an empty slot reads as a rendering fault. */}
                        <span className="dash-appt-fact dash-appt-doctor">
                          <Icon name="stethoscope" className="dash-appt-ico" width={13} height={13} />
                          {/* Wrapped rather than left as a bare text node: `text-overflow` needs an element
                              of its own to clip, and a loose text node inside a flex row is an anonymous box
                              no rule can reach — so a long name would push the chip wide instead of
                              ellipsing. */}
                          <span className={`dash-appt-doctor-name${doctor ? "" : " muted"}`}>
                            {doctor ?? t(S.unnamedDoctor)}
                          </span>
                        </span>
                      </span>
                    </span>
                  </span>
                );
              })}
            </div>
          </li>
        );
      })}
    </ol>
  );
}

/**
 * The month picker behind the day label — a popover anchored to the date, not a modal.
 *
 * Choosing a day is a small adjustment to what is already on screen. A centred modal dims the very cards and
 * table the operator is comparing against, and turns a two-second decision into something that feels like
 * leaving the page. It also has to be dismissible the way a popover is: Escape, a click outside, or picking a
 * day — all three, because an operator who opens it by accident should not have to find a Close button.
 *
 * Focus returns to the trigger on close, which a bare `position: absolute` panel does not give you for free.
 */
function DayPickerPopover({
  month, selected, t, fmt, onMonth, onPick, onClose,
}: {
  month: string;
  selected: string;
  t: (l: Localized) => string;
  fmt: ReturnType<typeof useFormat>;
  onMonth: (m: string) => void;
  onPick: (day: string) => void;
  onClose: () => void;
}) {
  const ref = useRef<HTMLDivElement>(null);

  useEffect(() => {
    // Escape closes, and a pointer down anywhere outside closes — the two gestures people already use on a
    // popover. Bound on `pointerdown` rather than `click` so a drag that starts outside also dismisses.
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    const onDown = (e: PointerEvent) => {
      if (ref.current && !ref.current.contains(e.target as Node)) onClose();
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("pointerdown", onDown);
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("pointerdown", onDown);
    };
  }, [onClose]);

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
    <div ref={ref} className="dash-daypop" role="dialog" aria-label={t(S.jumpToDay)}>
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

      <div className="dash-daypop-foot">
        <Button variant="secondary" size="sm" onClick={() => onPick(cairoToday())}>{t(S.goToday)}</Button>
      </div>
    </div>
  );
}

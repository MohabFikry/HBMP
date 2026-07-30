import { useMemo, useState } from "react";
import { Button, Icon } from "@mersal/design-system";
import type { AppointmentDay, BookableSlot, Localized } from "@mersal/contracts";
import { useFormat } from "../../i18n/useFormat";
import { useLoc } from "../_shared";

const S = {
  heading: { en: "Time", ar: "الوقت" },
  pickDay: { en: "Choose a day", ar: "اختر يومًا" },
  times: { en: "Available times", ar: "الأوقات المتاحة" },
  noneOnDay: { en: "No open times on this day.", ar: "لا توجد أوقات متاحة في هذا اليوم." },
  noneThisMonth: {
    en: "No open times this month. Try another month, doctor or clinic.",
    ar: "لا توجد أوقات متاحة هذا الشهر. جرّب شهرًا أو طبيبًا أو عيادة أخرى.",
  },
  prevMonth: { en: "Previous month", ar: "الشهر السابق" },
  nextMonth: { en: "Next month", ar: "الشهر التالي" },
  prev: { en: "Previous", ar: "السابق" },
  next: { en: "Next", ar: "التالي" },
  page: { en: "Page", ar: "صفحة" },
  of: { en: "of", ar: "من" },
  taken: { en: "Taken", ar: "محجوز" },
  open: { en: "open", ar: "متاح" },
  noneShort: { en: "no times", ar: "لا أوقات" },
} satisfies Record<string, Localized>;

/** Times per page. Enough to scan at a glance; more than this and the desk is reading rather than choosing. */
const PAGE_SIZE = 12;

const ZONE = "Africa/Cairo";

/**
 * Every date built here is anchored at 12:00 UTC.
 *
 * Cairo is UTC+2/+3, so noon UTC is early afternoon Cairo — the same calendar day under either offset and on
 * either side of a DST change. Anchoring at midnight is what makes a month grid render the 1st twice or skip
 * it depending on the season, and the bug only appears for part of the year.
 */
const at = (y: number, m: number, d: number) => new Date(Date.UTC(y, m, d, 12));

/** `YYYY-MM-DD` in Cairo — the same key the server groups its day counts by. */
const dayKey = (d: Date) =>
  new Intl.DateTimeFormat("en-CA", { timeZone: ZONE, year: "numeric", month: "2-digit", day: "2-digit" }).format(d);

/** `YYYY-MM` for the month a date falls in, in Cairo. */
export const monthKey = (d: Date) => dayKey(d).slice(0, 7);

function parseMonth(key: string): { year: number; month: number } {
  const [y, m] = key.split("-").map(Number);
  return { year: y, month: m - 1 };
}

/**
 * Egypt's week starts on SATURDAY. `getUTCDay()` counts from Sunday, so this shifts the column index — a grid
 * built on the JS default puts Saturday last and splits the weekend across two rows, which is not how a month
 * is read here.
 */
const columnOf = (d: Date) => (d.getUTCDay() + 1) % 7;

export interface BookingTimePickerProps {
  /** Per-day open counts for the VISIBLE month, from the server. */
  days: AppointmentDay[];
  /** Slots for the visible month; the picker filters to the chosen day itself. */
  slots: BookableSlot[];
  selectedSlotId: string | null;
  onSelectSlot: (slotId: string) => void;
  busy?: boolean;
  /**
   * The visible month as `YYYY-MM`, controlled by the parent — because changing month has to RE-FETCH.
   * A calendar showing a month whose availability was never loaded draws every day as empty and quietly
   * tells the operator there is nothing there.
   */
  month?: string;
  onMonthChange?: (month: string) => void;
}

/**
 * The time step: a MONTH calendar with the chosen day's times beside it, paginated.
 *
 * ============================================================================================================
 * WHY A NAVIGABLE MONTH, AND WHY IT IS ALWAYS ON SCREEN
 * ============================================================================================================
 * This began as a step you had to reach, then a two-week strip that vanished entirely when a doctor had
 * nothing free. Both fail the same way: the desk is told "no times" without being shown what they are
 * choosing between, and with nothing to click to look further out. A caller asking for "sometime after the
 * 20th" cannot be served by a control that only offers the next fortnight.
 *
 * So it is a real month, always rendered — before a doctor is chosen, and when the month is empty — and the
 * month is navigable. Counts come from the server for the month in view; a day the server did not name is
 * drawn with none rather than omitted, so the shape of the month survives either way.
 */
export function BookingTimePicker({
  days, slots, selectedSlotId, onSelectSlot, busy = false, month, onMonthChange,
}: BookingTimePickerProps) {
  const t = useLoc();
  const fmt = useFormat();

  // Uncontrolled fallback so the component stands alone (and in tests) without duplicating the rule that the
  // parent owns fetching.
  const [ownMonth, setOwnMonth] = useState(() => monthKey(new Date()));
  const visibleMonth = month ?? ownMonth;

  const [day, setDay] = useState<string | null>(null);
  const [page, setPage] = useState(0);

  const { year, month: monthIndex } = parseMonth(visibleMonth);

  /** The month's days, with the server's counts laid over them. */
  const grid = useMemo(() => {
    const counts = new Map(days.map((d) => [d.day, d.openSlots]));
    const total = new Date(Date.UTC(year, monthIndex + 1, 0)).getUTCDate();
    return Array.from({ length: total }, (_, i) => {
      const date = at(year, monthIndex, i + 1);
      const key = dayKey(date);
      return { key, date, dayOfMonth: i + 1, openSlots: counts.get(key) ?? 0 };
    });
  }, [days, year, monthIndex]);

  const openDays = useMemo(() => grid.filter((d) => d.openSlots > 0), [grid]);

  // Default to the first day that HAS availability rather than to the 1st: opening on an empty day makes a
  // month with plenty of free time look fully booked. A day chosen in July is not a day in August, so a
  // selection from another month is discarded rather than carried across.
  const activeDay = day && day.startsWith(visibleMonth) ? day : openDays[0]?.key ?? null;

  const dayTimes = useMemo(
    () => (activeDay ? slots.filter((s) => dayKey(new Date(s.start)) === activeDay) : []),
    [slots, activeDay],
  );

  const pageCount = Math.max(1, Math.ceil(dayTimes.length / PAGE_SIZE));
  // Clamped rather than stored blindly: switching to a day with fewer times must not leave the view on a page
  // that no longer exists, showing nothing and looking broken.
  const safePage = Math.min(page, pageCount - 1);
  const shown = dayTimes.slice(safePage * PAGE_SIZE, safePage * PAGE_SIZE + PAGE_SIZE);

  function stepMonth(delta: number) {
    setOwnMonth(monthKey(at(year, monthIndex + delta, 1)));
    onMonthChange?.(monthKey(at(year, monthIndex + delta, 1)));
    setDay(null);
    setPage(0);
  }

  const monthLabel = new Intl.DateTimeFormat(fmt.locale, { timeZone: ZONE, month: "long", year: "numeric" })
    .format(at(year, monthIndex, 1));

  // Weekday headings from the SAME locale as the month name, starting Saturday. Hard-coded English initials
  // would leave an Arabic user with a Latin header row over Arabic dates. 1 Aug 2026 is a Saturday, so this
  // walks one real week from the week's first day.
  const weekdayNames = useMemo(() => {
    const f = new Intl.DateTimeFormat(fmt.locale, { timeZone: ZONE, weekday: "short" });
    return Array.from({ length: 7 }, (_, i) => f.format(at(2026, 7, 1 + i)));
  }, [fmt.locale]);

  return (
    <section aria-labelledby="bk-time-h" className="bk-time">
      <h3 className="section-h" id="bk-time-h">{t(S.heading)}</h3>

      <div className="bk-time-grid">
        <div className="bk-cal">
          <div className="bk-cal-head">
            <Button
              variant="ghost" size="sm" aria-label={t(S.prevMonth)} title={t(S.prevMonth)}
              leadingIcon={<Icon name="chevron" style={{ transform: "rotate(90deg)" }} />}
              onClick={() => stepMonth(-1)}
            />
            {/* aria-live: stepping the month replaces the whole grid, and a keyboard user pressing the arrow
                would otherwise hear nothing about where they had landed. */}
            <strong className="bk-cal-month" aria-live="polite">{monthLabel}</strong>
            <Button
              variant="ghost" size="sm" aria-label={t(S.nextMonth)} title={t(S.nextMonth)}
              leadingIcon={<Icon name="chevron" style={{ transform: "rotate(-90deg)" }} />}
              onClick={() => stepMonth(1)}
            />
          </div>

          <div className="bk-cal-weekdays" aria-hidden="true">
            {weekdayNames.map((w, i) => <span key={i}>{w}</span>)}
          </div>

          <div className="bk-cal-grid" role="radiogroup" aria-label={t(S.pickDay)}>
            {/* Leading blanks so the 1st lands in its real column. Presentational and aria-hidden, so a
                screen reader is not read a run of empty cells before the month begins. */}
            {Array.from({ length: columnOf(at(year, monthIndex, 1)) }, (_, i) => (
              <span key={`pad-${i}`} className="bk-cal-pad" aria-hidden="true" />
            ))}
            {grid.map((d) => {
              const empty = d.openSlots === 0;
              return (
                <button
                  key={d.key}
                  type="button"
                  role="radio"
                  aria-checked={d.key === activeDay}
                  disabled={empty}
                  className="bk-cal-day"
                  // The count is IN the accessible name: a screen-reader user choosing a day needs to know
                  // whether it holds anything before selecting it, not after.
                  aria-label={`${fmt.date(`${d.key}T12:00:00Z`)} — ${empty ? t(S.noneShort) : `${d.openSlots} ${t(S.open)}`}`}
                  onClick={() => { setDay(d.key); setPage(0); }}
                >
                  <span className="bk-cal-num tnum">{d.dayOfMonth}</span>
                  {/* A count, not a dot: "how many" is what the desk weighs when a caller asks for the
                      soonest appointment. Hidden from the reader — the label above already says it. */}
                  {!empty && <span className="bk-cal-count tnum" aria-hidden="true">{d.openSlots}</span>}
                </button>
              );
            })}
          </div>

          {!busy && openDays.length === 0 && (
            <p role="status" className="muted bk-cal-empty">{t(S.noneThisMonth)}</p>
          )}
        </div>

        <div className="bk-times">
          <div className="bk-times-head">
            <h4 className="section-h" id="bk-times-h">{t(S.times)}</h4>
            {pageCount > 1 && (
              <span className="bk-pager">
                <Button
                  variant="ghost" size="sm" disabled={safePage === 0}
                  leadingIcon={<Icon name="chevron" style={{ transform: "rotate(90deg)" }} />}
                  onClick={() => setPage(safePage - 1)}
                >
                  {t(S.prev)}
                </Button>
                <span className="tnum" aria-live="polite">
                  {t(S.page)} {safePage + 1} {t(S.of)} {pageCount}
                </span>
                <Button
                  variant="ghost" size="sm" disabled={safePage >= pageCount - 1}
                  leadingIcon={<Icon name="chevron" style={{ transform: "rotate(-90deg)" }} />}
                  onClick={() => setPage(safePage + 1)}
                >
                  {t(S.next)}
                </Button>
              </span>
            )}
          </div>

          {shown.length === 0 ? (
            // Only when OTHER days have something. With nothing bookable this month the calendar already
            // says so, and repeating it here is two sentences telling the operator the same thing twice.
            openDays.length > 0 ? <p role="status" className="muted">{t(S.noneOnDay)}</p> : null
          ) : (
            <div className="book-slots" role="radiogroup" aria-labelledby="bk-times-h">
              {shown.map((s) => (
                <button
                  key={s.id}
                  type="button"
                  role="radio"
                  aria-checked={selectedSlotId === s.id}
                  // The SERVER's flag. It holds the no-double-book invariant and can see slots held by
                  // bookings this desk may not read, so a time comparison here would be a second opinion.
                  disabled={!s.open}
                  className="book-slot"
                  // The day is in the accessible name too: without it a screen-reader user is offered a
                  // dozen indistinguishable "09:40"s.
                  aria-label={`${fmt.date(s.start)} ${fmt.time(s.start)}`}
                  onClick={() => onSelectSlot(s.id)}
                >
                  <span className="tnum">{fmt.time(s.start)}</span>
                  {!s.open && <span className="muted"> · {t(S.taken)}</span>}
                </button>
              ))}
            </div>
          )}
        </div>
      </div>
    </section>
  );
}

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
  noneAtAll: {
    en: "No open times. Try another doctor, clinic or month.",
    ar: "لا توجد أوقات متاحة. جرّب طبيبًا أو عيادة أو شهرًا آخر.",
  },
  prev: { en: "Previous", ar: "السابق" },
  next: { en: "Next", ar: "التالي" },
  page: { en: "Page", ar: "صفحة" },
  of: { en: "of", ar: "من" },
  taken: { en: "Taken", ar: "محجوز" },
  slotsAvailable: { en: "open", ar: "متاح" },
} satisfies Record<string, Localized>;

/** Times per page. Enough to scan at a glance; more than this and the desk is reading rather than choosing. */
const PAGE_SIZE = 12;

/** `YYYY-MM-DD` for an instant, in Cairo — the same key the server groups its day counts by. */
function cairoDayKey(iso: string): string {
  return new Intl.DateTimeFormat("en-CA", {
    timeZone: "Africa/Cairo", year: "numeric", month: "2-digit", day: "2-digit",
  }).format(new Date(iso));
}

export interface BookingTimePickerProps {
  /** Per-day open counts from the server, driving the calendar strip. */
  days: AppointmentDay[];
  /** Slots for the whole loaded window; the picker filters to the chosen day itself. */
  slots: BookableSlot[];
  selectedSlotId: string | null;
  onSelectSlot: (slotId: string) => void;
  busy?: boolean;
}

/**
 * The time step: a day strip with open-slot counts, and the chosen day's times beside it, paginated.
 *
 * ============================================================================================================
 * WHY IT IS ALWAYS VISIBLE, NOT A STEP
 * ============================================================================================================
 * Booking used to be a numbered sequence where times appeared only after a clinic was chosen. The desk's
 * actual job is the reverse: a patient says "any time Thursday morning", and the operator needs to see what
 * exists before committing to a doctor. A hidden step forces them to choose first and discover second, then
 * back out — which is why the same three-step form kept being abandoned halfway.
 *
 * ============================================================================================================
 * WHY A DAY STRIP RATHER THAN A FLAT LIST OF TIMES
 * ============================================================================================================
 * Availability spans weeks. A flat list repeats "09:40" once per day with nothing to tell them apart, so the
 * operator cannot book a specific day — which is most of what a caller rings to do. The counts on each day
 * come from the SERVER (`/appointment-days`), not from counting the slots on screen: the slot list is one
 * page of one day, and a count derived from it would be wrong for every other day in the strip.
 */
export function BookingTimePicker({
  days, slots, selectedSlotId, onSelectSlot, busy = false,
}: BookingTimePickerProps) {
  const t = useLoc();
  const fmt = useFormat();

  const [day, setDay] = useState<string | null>(null);
  const [page, setPage] = useState(0);

  // Default to the first day that HAS availability rather than to today: opening on an empty day makes a
  // clinic with plenty of free time look fully booked.
  const openDays = useMemo(() => days.filter((d) => d.openSlots > 0), [days]);
  const activeDay = day ?? openDays[0]?.day ?? null;

  const dayTimes = useMemo(
    () => (activeDay ? slots.filter((s) => cairoDayKey(s.start) === activeDay) : []),
    [slots, activeDay],
  );

  const pageCount = Math.max(1, Math.ceil(dayTimes.length / PAGE_SIZE));
  // Clamped rather than stored blindly: switching to a day with fewer times must not leave the view on a
  // page that no longer exists, showing nothing and looking broken.
  const safePage = Math.min(page, pageCount - 1);
  const shown = dayTimes.slice(safePage * PAGE_SIZE, safePage * PAGE_SIZE + PAGE_SIZE);

  function pickDay(next: string) {
    setDay(next);
    setPage(0);
  }

  if (openDays.length === 0) {
    return (
      <section aria-labelledby="bk-time-h" className="bk-time">
        <h3 className="section-h" id="bk-time-h">{t(S.heading)}</h3>
        <p role="status" className="muted">{t(busy ? S.times : S.noneAtAll)}</p>
      </section>
    );
  }

  return (
    <section aria-labelledby="bk-time-h" className="bk-time">
      <h3 className="section-h" id="bk-time-h">{t(S.heading)}</h3>

      <div className="bk-time-grid">
        {/* The day strip. Radios rather than buttons: this is a single choice from a set, and a screen reader
            should announce it as "3 of 14" rather than as fourteen unrelated buttons. */}
        <div className="bk-days" role="radiogroup" aria-label={t(S.pickDay)}>
          {days.map((d) => {
            const disabled = d.openSlots === 0;
            const active = d.day === activeDay;
            return (
              <button
                key={d.day}
                type="button"
                role="radio"
                aria-checked={active}
                disabled={disabled}
                className="bk-day"
                // The count is IN the accessible name: a screen-reader user choosing a day needs to know
                // whether it has anything in it before they select it, not after.
                aria-label={`${fmt.date(`${d.day}T12:00:00Z`)} — ${d.openSlots} ${t(S.slotsAvailable)}`}
                onClick={() => pickDay(d.day)}
              >
                <span className="bk-day-date">{fmt.date(`${d.day}T12:00:00Z`)}</span>
                <span className="bk-day-count tnum">{d.openSlots}</span>
              </button>
            );
          })}
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
                {/* Announced politely: paging with the keyboard otherwise moves focus and changes content
                    with nothing said about where you now are. */}
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
            <p role="status" className="muted">{t(S.noneOnDay)}</p>
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

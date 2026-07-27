import { useMemo } from "react";
import { useTheme } from "@mersal/design-system";

/**
 * Phase 18.D2 (audit R2 U7) — one hook for every date, time, number and money the user sees.
 *
 * Bare `toLocaleDateString()` / `toLocaleString()` formats in the BROWSER's locale and the MACHINE's time
 * zone. Both are wrong here, and the time zone is the dangerous one: a clinic PC set to UTC — the default on
 * a freshly imaged Linux box, and on every container — renders a 09:00 Cairo appointment as 07:00. Nothing
 * errors. The receptionist reads 07:00, the patient is told 07:00, and they arrive two hours early or miss
 * the slot. During DST the offset changes and the same screen is wrong by a different amount.
 *
 * The locale is equally wrong in the other direction: an Arabic-speaking user on an en-US browser gets
 * English month names inside an otherwise Arabic page, because the app's language and the browser's are
 * unrelated settings.
 *
 * So: the zone is pinned to Africa/Cairo (CLAUDE.md — timestamps are UTC, display is Cairo) and the locale
 * follows the APP's language, not the browser's. Currency is EGP and is formatted at RENDER from a raw
 * number, so the API layer never ships a pre-formatted string that cannot be re-localised.
 */
const ZONE = "Africa/Cairo";

export interface Formatters {
  /** 26 Jul 2026 — no time. For dates where the hour is meaningless (birth date, service date). */
  date: (value: string | number | Date | null | undefined) => string;
  /** 09:00 — no date. For a slot time within a day already established by its heading. */
  time: (value: string | number | Date | null | undefined) => string;
  /** 26 Jul 2026, 09:00 — both, for audit trails and timestamps. */
  dateTime: (value: string | number | Date | null | undefined) => string;
  /** EGP 1,250.00 in the active locale. Takes a NUMBER — never a pre-formatted string. */
  money: (value: number | null | undefined) => string;
  /** Plain number with locale digit grouping. */
  number: (value: number | null | undefined, opts?: Intl.NumberFormatOptions) => string;
}

/** Arabic uses ar-EG specifically: it carries Egyptian month names and the right week start. */
function localeFor(lang: string): string {
  return lang === "ar" ? "ar-EG" : "en-GB";
}

function toDate(value: string | number | Date | null | undefined): Date | null {
  if (value === null || value === undefined || value === "") return null;
  const d = value instanceof Date ? value : new Date(value);
  return Number.isNaN(d.getTime()) ? null : d;
}

export function useFormat(): Formatters {
  const { lang } = useTheme();

  return useMemo(() => {
    const locale = localeFor(lang);
    // Constructed once per locale change: Intl formatters are expensive and these run per table cell.
    const dateFmt = new Intl.DateTimeFormat(locale, { timeZone: ZONE, day: "2-digit", month: "short", year: "numeric" });
    const timeFmt = new Intl.DateTimeFormat(locale, { timeZone: ZONE, hour: "2-digit", minute: "2-digit", hour12: false });
    const dateTimeFmt = new Intl.DateTimeFormat(locale, {
      timeZone: ZONE, day: "2-digit", month: "short", year: "numeric", hour: "2-digit", minute: "2-digit", hour12: false,
    });
    const moneyFmt = new Intl.NumberFormat(locale, { style: "currency", currency: "EGP" });

    // An em dash rather than an empty string: a blank cell reads as a rendering bug, "—" reads as "no value".
    const dash = "—";
    return {
      date: (v) => { const d = toDate(v); return d ? dateFmt.format(d) : dash; },
      time: (v) => { const d = toDate(v); return d ? timeFmt.format(d) : dash; },
      dateTime: (v) => { const d = toDate(v); return d ? dateTimeFmt.format(d) : dash; },
      money: (v) => (typeof v === "number" && Number.isFinite(v) ? moneyFmt.format(v) : dash),
      number: (v, opts) =>
        typeof v === "number" && Number.isFinite(v) ? new Intl.NumberFormat(locale, opts).format(v) : dash,
    };
  }, [lang]);
}

/** The zone, exported so a test can assert the app never formats in the machine's local time. */
export const DISPLAY_TIME_ZONE = ZONE;

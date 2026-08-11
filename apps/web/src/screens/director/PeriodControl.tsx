import { useCallback, useMemo, useState } from "react";
import { SegmentedControl } from "@mersal/design-system";
import type { Localized, Period } from "@mersal/contracts";
import { useFormat } from "../../i18n/useFormat";
import { useLoc } from "../_shared";

/**
 * The window every analytical screen on the oversight portal is showing.
 *
 * <b>Why this exists.</b> Every reporting endpoint has accepted `from`/`to` since phase 8.2, and the Medical
 * Director's portal sent neither from any screen. Two consequences, and the second is the worse one: the
 * director could not ask about last quarter, and — because `/reports/*` defaults to thirty days while the
 * claims KPI endpoint defaults to ninety — two figures covering different spans could sit in one KPI row with
 * nothing on screen saying so.
 *
 * <b>Presets, not a date picker.</b> A supervisory question is almost always "this month", "last month",
 * "the quarter" — and a free date range invites the one thing this control exists to prevent, which is two
 * screens quietly disagreeing about the window. The chosen preset is shared across the portal by
 * {@link usePeriod} and stated in words beside the control, so the answer on screen always names its question.
 */

const PRESETS = ["30d", "90d", "month", "quarter"] as const;
export type PresetKey = (typeof PRESETS)[number];

const LABELS: Record<PresetKey, Localized> = {
  "30d": { en: "Last 30 days", ar: "آخر ٣٠ يومًا" },
  "90d": { en: "Last 90 days", ar: "آخر ٩٠ يومًا" },
  month: { en: "This month", ar: "هذا الشهر" },
  quarter: { en: "This quarter", ar: "هذا الربع" },
};

const S = {
  legend: { en: "Period", ar: "الفترة" },
  showing: { en: "Showing", ar: "المعروض" },
  to: { en: "to", ar: "إلى" },
} satisfies Record<string, Localized>;

/**
 * Today on the CLINIC's calendar, not the reader's.
 *
 * `en-CA` because it formats ISO-ordered `YYYY-MM-DD`, which is what the wire wants; `Africa/Cairo` because
 * a director opening the portal from another timezone is still asking about the clinic's day. The same trap
 * `Period.Parse` documents on the server, on the other side of the request.
 */
function cairoToday(): Date {
  const iso = new Intl.DateTimeFormat("en-CA", {
    timeZone: "Africa/Cairo", year: "numeric", month: "2-digit", day: "2-digit",
  }).format(new Date());
  return new Date(`${iso}T00:00:00Z`);
}

const iso = (d: Date) => d.toISOString().slice(0, 10);

/** The resolved window for a preset, on Cairo dates. */
export function periodFor(preset: PresetKey): Period {
  const today = cairoToday();
  const to = iso(today);
  if (preset === "30d" || preset === "90d") {
    const days = preset === "30d" ? 30 : 90;
    const from = new Date(today);
    from.setUTCDate(from.getUTCDate() - days);
    return { from: iso(from), to };
  }
  if (preset === "month") {
    return { from: iso(new Date(Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), 1))), to };
  }
  // Calendar quarter, not "the last ninety days": a director comparing against a board report is comparing
  // against quarters, and the two are only the same number by coincidence.
  const quarterStartMonth = Math.floor(today.getUTCMonth() / 3) * 3;
  return { from: iso(new Date(Date.UTC(today.getUTCFullYear(), quarterStartMonth, 1))), to };
}

const STORAGE_KEY = "director-period";

/**
 * The portal's shared period.
 *
 * Persisted for the session so moving between Oversight and Utilization does not silently change the
 * question — a supervisor who narrowed to this month and then clicked a different section used to get
 * thirty days again, with no indication that the comparison they were about to make was invalid.
 */
export function usePeriod(): [PresetKey, Period, (p: PresetKey) => void] {
  const [preset, setPresetState] = useState<PresetKey>(() => {
    const saved = typeof sessionStorage !== "undefined" ? sessionStorage.getItem(STORAGE_KEY) : null;
    return (PRESETS as readonly string[]).includes(saved ?? "") ? (saved as PresetKey) : "30d";
  });
  const setPreset = useCallback((p: PresetKey) => {
    setPresetState(p);
    try { sessionStorage.setItem(STORAGE_KEY, p); } catch { /* private mode — the default is fine */ }
  }, []);
  const period = useMemo(() => periodFor(preset), [preset]);
  return [preset, period, setPreset];
}

/**
 * The control, and the sentence that says what it resolved to.
 *
 * The sentence is not decoration. A preset name is a promise ("this quarter"); the dates are the promise
 * kept, and they are what a supervisor writes down when they take a figure into a meeting.
 */
export function PeriodControl({
  preset, period, onChange,
}: { preset: PresetKey; period: Period; onChange: (p: PresetKey) => void }) {
  const t = useLoc();
  const fmt = useFormat();
  return (
    <div className="stack" style={{ gap: "var(--sp2)", marginBottom: "var(--sp4)" }}>
      <SegmentedControl
        aria-label={t(S.legend)}
        value={preset}
        onChange={(v) => onChange(v as PresetKey)}
        segments={PRESETS.map((p) => ({ value: p, label: t(LABELS[p]) }))}
      />
      <p className="muted" style={{ margin: 0 }}>
        {t(S.showing)} <span className="tnum">{fmt.date(period.from)}</span>{" "}
        {t(S.to)} <span className="tnum">{fmt.date(period.to)}</span>
      </p>
    </div>
  );
}

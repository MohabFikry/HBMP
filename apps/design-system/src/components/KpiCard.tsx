import type { ReactNode } from "react";
import { cx } from "../lib/cx";
import { useTheme } from "../theme/ThemeProvider";

export interface KpiCardProps {
  /** Uppercase micro-label. */
  label: string;
  /** Big tabular-numeral value. */
  value: string;
  delta?: string;
  /** Direction of the delta — drives icon + accessible text, not color alone. */
  direction?: "up" | "down";
  /**
   * A glyph for the subject the figure counts.
   *
   * <p><b>Decorative, and marked as such.</b> The label already names the figure in words; the icon lets a
   * desk find the right card by shape at a glance, on a board of three or seven identical white tiles. It is
   * therefore rendered `aria-hidden` — announcing "calendar, Appointments, 8" adds a word that carries no
   * information the label does not already give, and status is never encoded by the icon alone.</p>
   */
  icon?: ReactNode;
  /**
   * Recolours the hairline and the icon tile to say what KIND of figure this is.
   *
   * <p><b>It marks the subject, never the reading.</b> A no-show card is `bad` whether it says 0 or 40 —
   * the tone identifies the category so a desk can find the one card it dreads on a row of identical white
   * tiles. Wiring it to the VALUE instead would paint a card red for a quiet morning, which is the platform's
   * own forbidden pattern in miniature: a colour claiming something the number does not say.</p>
   *
   * <p>That is also why the big value stays in body colour in every tone. Nothing here carries state by hue
   * alone — the uppercase label names the figure in words, and the tone only reinforces it.</p>
   */
  tone?: "brand" | "ok" | "warn" | "bad";
  className?: string;
}

/**
 * KPI card (0B §10b): brand→accent gradient top hairline, uppercase micro-label, 34px tabular numerals,
 * delta as a bordered pill with a ▲/▼ glyph (direction encoded by glyph + text, not hue alone).
 */
export function KpiCard({ label, value, delta, direction, icon, tone, className }: KpiCardProps) {
  const { lang } = useTheme();
  return (
    <div className={cx("mrs-card", "mrs-kpi", tone && tone !== "brand" && `mrs-kpi--${tone}`, className)}>
      <div className="mrs-kpi-lab">
        {icon && <span className="mrs-kpi-icon" aria-hidden="true">{icon}</span>}
        {label}
      </div>
      <div className="mrs-kpi-val">{value}</div>
      {delta && (
        <div className={cx("mrs-kpi-delta", direction)}>
          <span aria-hidden="true">{direction === "down" ? "▼" : "▲"}</span>
          <span>{delta}</span>
          {/* 18.D3 (U6): the direction word follows the app language. It was hardcoded English, so an
              Arabic user heard "١٢٪ increase" — the only part of the number that carries MEANING, in the
              wrong language. */}
          {direction && (
            <span className="sr-only">
              {lang === "ar" ? (direction === "down" ? " انخفاض" : " ارتفاع") : direction === "down" ? " decrease" : " increase"}
            </span>
          )}
        </div>
      )}
    </div>
  );
}

export interface KpiListItem {
  label: string;
  value: string;
}

/**
 * A row of KPIs as a definition list.
 *
 * Same 0B §10b treatment as {@link KpiCard} — hairline, uppercase micro-label, 34px tabular numerals — over
 * `<dl>/<dt>/<dd>` instead of nested `<div>`s. The screens that render a FIXED set of figures about one thing
 * ("members / limit / consumed / remaining" for a policy) are describing one subject with four terms, which
 * is what a definition list is; a screen reader then announces "Total consumed, EGP 41,200" as a pair rather
 * than as two unrelated lines of text.
 *
 * `KpiCard` stays for the dashboards, where each tile is an independent headline with its own delta and the
 * grouping is a layout choice rather than a claim about the content. Both draw from the same classes, so the
 * two cannot drift into two different-looking KPIs.
 */
export function KpiList({ items, className }: { items: KpiListItem[]; className?: string }) {
  return (
    <dl className={cx("mrs-kpilist", className)}>
      {items.map((k) => (
        <div key={k.label} className="mrs-card mrs-kpi">
          <dt className="mrs-kpi-lab">{k.label}</dt>
          <dd className="mrs-kpi-val">{k.value}</dd>
        </div>
      ))}
    </dl>
  );
}

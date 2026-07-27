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
  className?: string;
}

/**
 * KPI card (0B §10b): brand→accent gradient top hairline, uppercase micro-label, 34px tabular numerals,
 * delta as a bordered pill with a ▲/▼ glyph (direction encoded by glyph + text, not hue alone).
 */
export function KpiCard({ label, value, delta, direction, className }: KpiCardProps) {
  const { lang } = useTheme();
  return (
    <div className={cx("mrs-card", "mrs-kpi", className)}>
      <div className="mrs-kpi-lab">{label}</div>
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

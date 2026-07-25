import { cx } from "../lib/cx";

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
  return (
    <div className={cx("mrs-card", "mrs-kpi", className)}>
      <div className="mrs-kpi-lab">{label}</div>
      <div className="mrs-kpi-val">{value}</div>
      {delta && (
        <div className={cx("mrs-kpi-delta", direction)}>
          <span aria-hidden="true">{direction === "down" ? "▼" : "▲"}</span>
          <span>{delta}</span>
          {direction && <span className="sr-only">{direction === "down" ? " decrease" : " increase"}</span>}
        </div>
      )}
    </div>
  );
}

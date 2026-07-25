import { statusMeta, type StatusKind } from "../tokens/tokens";
import { Icon, type IconName } from "./Icon";
import { cx } from "../lib/cx";

const kindIcon: Record<StatusKind, IconName> = {
  ok: "ok",
  info: "clock",
  part: "half",
  warn: "triangle",
  bad: "cross",
  neu: "info",
};

export interface StatusChipProps {
  kind: StatusKind;
  /** Human label — REQUIRED. Status is never color-only: hue + icon + shape + this text (0A §5.2). */
  label: string;
  className?: string;
}

/**
 * StatusChip — the color-blind-safe status primitive. Every status renders four redundant cues:
 * hue + icon + shape (pill/dashed/half/outline/square/ghost) + text label. Never color alone.
 */
export function StatusChip({ kind, label, className }: StatusChipProps) {
  return (
    <span className={cx("mrs-chip", kind, className)} data-shape={statusMeta[kind].shape}>
      <Icon name={kindIcon[kind]} />
      {label}
    </span>
  );
}

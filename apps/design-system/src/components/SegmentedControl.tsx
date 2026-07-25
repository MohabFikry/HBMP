import { useRef } from "react";
import { cx } from "../lib/cx";

export interface Segment<T extends string> {
  value: T;
  label: string;
}

export interface SegmentedControlProps<T extends string> {
  segments: Segment<T>[];
  value: T;
  onChange: (value: T) => void;
  "aria-label": string;
  className?: string;
}

/**
 * Segmented control (HIG signature) — single-select filter switch. `role=radiogroup`, arrow-key navigable,
 * selected segment gets a solid pill on the tinted track. Accessible name required.
 */
export function SegmentedControl<T extends string>({
  segments,
  value,
  onChange,
  className,
  ...aria
}: SegmentedControlProps<T>) {
  const refs = useRef<Array<HTMLButtonElement | null>>([]);

  function onKeyDown(e: React.KeyboardEvent, index: number) {
    let next = index;
    if (e.key === "ArrowRight" || e.key === "ArrowDown") next = (index + 1) % segments.length;
    else if (e.key === "ArrowLeft" || e.key === "ArrowUp") next = (index - 1 + segments.length) % segments.length;
    else return;
    e.preventDefault();
    onChange(segments[next].value);
    refs.current[next]?.focus();
  }

  return (
    <div className={cx("mrs-seg", className)} role="radiogroup" aria-label={aria["aria-label"]}>
      {segments.map((s, i) => (
        <button
          key={s.value}
          ref={(el) => {
            refs.current[i] = el;
          }}
          type="button"
          role="radio"
          aria-checked={s.value === value}
          tabIndex={s.value === value ? 0 : -1}
          onClick={() => onChange(s.value)}
          onKeyDown={(e) => onKeyDown(e, i)}
        >
          {s.label}
        </button>
      ))}
    </div>
  );
}

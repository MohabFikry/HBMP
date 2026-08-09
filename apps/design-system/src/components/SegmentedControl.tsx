import { useRef } from "react";
import { cx } from "../lib/cx";
import { useDirection } from "../lib/useDirection";

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
  const dir = useDirection();

  function onKeyDown(e: React.KeyboardEvent, index: number) {
    /*
     * ARROW KEYS ARE SPATIAL, AND ARABIC RUNS THE OTHER WAY.
     *
     * The control mirrors itself through logical CSS, so in Arabic the FIRST segment is drawn on the right.
     * `ArrowRight` therefore has to move BACKWARD through the list to move rightward on screen. It did not,
     * and the effect was not subtle: on the design's signature filter control, arrowing toward the segment
     * you are looking at moved focus away from it.
     *
     * Up/Down are unaffected. Vertical order does not mirror, and a reader who reaches for ArrowDown means
     * "the next one" in both languages.
     */
    const forward = dir === "rtl" ? "ArrowLeft" : "ArrowRight";
    const back = dir === "rtl" ? "ArrowRight" : "ArrowLeft";

    let next = index;
    if (e.key === forward || e.key === "ArrowDown") next = (index + 1) % segments.length;
    else if (e.key === back || e.key === "ArrowUp") next = (index - 1 + segments.length) % segments.length;
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

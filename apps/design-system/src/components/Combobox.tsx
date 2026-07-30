import { useEffect, useId, useMemo, useRef, useState } from "react";
import type { ReactNode } from "react";
import { cx } from "../lib/cx";
import { Icon } from "./Icon";

export interface ComboboxOption {
  value: string;
  label: string;
  /** Secondary text shown after the label (e.g. a country code or a plan code). */
  hint?: string;
  /** Rendered before the label in both the list and the closed control — a flag, a status dot, an icon. */
  leading?: ReactNode;
  /** Extra text matched when filtering but never displayed. Lets "Egypt" be found by typing "EG" or "+20". */
  keywords?: string;
}

export interface ComboboxProps {
  options: ComboboxOption[];
  value: string | null;
  onChange: (value: string) => void;
  "aria-label"?: string;
  "aria-labelledby"?: string;
  id?: string;
  placeholder?: string;
  disabled?: boolean;
  className?: string;
  /** Rendered on the error path by the caller; only used to set aria-invalid here. */
  invalid?: boolean;
  "aria-describedby"?: string;
}

/**
 * Editable combobox (APG "combobox with list autocomplete"). The user TYPES TO FILTER.
 *
 * ============================================================================================================
 * WHY THIS EXISTS BESIDE `Select`
 * ============================================================================================================
 * `Select` is the select-only pattern: a button that opens a list, with first-letter typeahead. That is right
 * for a closed vocabulary of five — Male/Female/Other/Unknown — where every option is on screen at once.
 *
 * It is the wrong control for a hundred nationalities. First-letter typeahead means an operator looking for
 * "South Sudan" presses S and lands on "Saudi Arabia", then presses S again and moves to "Senegal", and has
 * to keep pressing to walk the whole S range. Typing the word and seeing the list narrow is how everyone
 * expects a long list to behave, and it is the difference between a field an operator uses and one they fight.
 *
 * ============================================================================================================
 * WHAT IT WILL NOT DO
 * ============================================================================================================
 * It never keeps free text. The input is a QUERY, not the value: on blur or Escape it reverts to the label of
 * whatever is actually selected. A combobox that let a half-typed "Sud" survive as the nationality would be a
 * text field wearing a droplist's clothes, and the value would fail validation somewhere far from here.
 */
export function Combobox({
  options,
  value,
  onChange,
  id,
  placeholder,
  disabled,
  className,
  invalid,
  ...aria
}: ComboboxProps) {
  const autoId = useId();
  const inputId = id ?? `${autoId}-input`;
  const listId = `${autoId}-list`;
  const optionId = (i: number) => `${autoId}-opt-${i}`;

  const selected = options.find((o) => o.value === value) ?? null;
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const rootRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLUListElement>(null);

  // Closed, the input SHOWS the selection; open, it holds whatever is being typed. One input doing both jobs
  // is what makes this feel like a droplist you can type into rather than a text box with suggestions.
  const display = open ? query : (selected?.label ?? "");

  const matches = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!open || q === "") return options;
    // Prefix matches first: typing "sud" should offer Sudan before it offers South Sudan.
    const scored = options
      .map((o) => {
        const hay = `${o.label} ${o.hint ?? ""} ${o.keywords ?? ""}`.toLowerCase();
        const label = o.label.toLowerCase();
        if (label.startsWith(q)) return { o, rank: 0 };
        if (hay.includes(q)) return { o, rank: 1 };
        return null;
      })
      .filter((x): x is { o: ComboboxOption; rank: number } => x !== null);
    return scored.sort((a, b) => a.rank - b.rank).map((x) => x.o);
  }, [options, query, open]);

  useEffect(() => {
    if (open) setActive(0);
  }, [open, query]);

  // Keep the active option in view when arrowing past the edge of the popup.
  useEffect(() => {
    if (!open) return;
    listRef.current
      ?.querySelector<HTMLElement>(`#${CSS.escape(optionId(active))}`)
      ?.scrollIntoView?.({ block: "nearest" });
  }, [open, active]);

  useEffect(() => {
    if (!open) return;
    // pointerdown, not click: a drag that starts inside and ends outside must not close, and pointerdown
    // fires before the browser moves focus.
    function onPointerDown(e: PointerEvent) {
      if (!rootRef.current?.contains(e.target as Node)) close();
    }
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  });

  function close() {
    setOpen(false);
    setQuery("");
  }

  function commit(i: number) {
    const opt = matches[i];
    if (opt) onChange(opt.value);
    close();
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (disabled) return;
    const last = matches.length - 1;

    if (e.key === "Escape") {
      if (open) {
        e.preventDefault();
        close();          // reverts to the selection — the typed query is discarded, never kept
      }
      return;
    }
    if (e.key === "Tab") {
      close();
      return;
    }
    if (e.key === "Enter") {
      if (open) {
        e.preventDefault();
        commit(active);
      }
      return;
    }
    if (e.key === "ArrowDown" || e.key === "ArrowUp" || e.key === "Home" || e.key === "End") {
      e.preventDefault();
      if (!open) {
        setOpen(true);
        return;
      }
      if (e.key === "ArrowDown") setActive((i) => Math.min(last, i + 1));
      else if (e.key === "ArrowUp") setActive((i) => Math.max(0, i - 1));
      else if (e.key === "Home") setActive(0);
      else setActive(last);
    }
  }

  return (
    <div ref={rootRef} className={cx("mrs-combo", className)}>
      <div className="mrs-combo-control">
        {/* The selected option's flag stays visible while the field is closed — for nationality that is the
            fastest way to confirm the right country at a glance, which is the whole reason it is there. */}
        {!open && selected?.leading && <span className="mrs-combo-leading">{selected.leading}</span>}
        <input
          id={inputId}
          role="combobox"
          type="text"
          className="mrs-control mrs-combo-input"
          autoComplete="off"
          spellCheck={false}
          aria-expanded={open}
          aria-controls={listId}
          aria-autocomplete="list"
          aria-haspopup="listbox"
          aria-activedescendant={open && matches.length > 0 ? optionId(active) : undefined}
          aria-invalid={invalid || undefined}
          aria-label={aria["aria-label"]}
          aria-labelledby={aria["aria-labelledby"]}
          aria-describedby={aria["aria-describedby"]}
          disabled={disabled}
          placeholder={selected ? undefined : placeholder}
          value={display}
          onChange={(e) => {
            setQuery(e.currentTarget.value);
            setOpen(true);
          }}
          onFocus={() => setOpen(true)}
          // The value is whatever is SELECTED; a half-typed query never survives losing focus.
          onBlur={() => window.setTimeout(() => setOpen(false), 0)}
          onKeyDown={onKeyDown}
        />
        <Icon name="chevron" className="mrs-combo-chevron" aria-hidden />
      </div>

      {/* Rendered only while open, so the closed control contributes nothing to the a11y tree or hit-testing. */}
      {open && (
        <ul ref={listRef} id={listId} role="listbox" className="mrs-combo-list"
            aria-label={aria["aria-label"]} aria-labelledby={aria["aria-labelledby"]}>
          {matches.length === 0 && (
            // A silent empty popup reads as a broken control. Not an option, so it is never selectable.
            <li className="mrs-combo-empty" role="presentation">No match</li>
          )}
          {matches.map((o, i) => (
            <li
              key={o.value}
              id={optionId(i)}
              role="option"
              aria-selected={o.value === value}
              className={cx("mrs-combo-option", i === active && "is-active")}
              // The input must keep focus for aria-activedescendant to mean anything, and mousedown is where
              // the browser would otherwise take it away.
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => commit(i)}
              onMouseEnter={() => setActive(i)}
            >
              <Icon name="ok" className="mrs-combo-check" aria-hidden />
              {o.leading && <span className="mrs-combo-leading">{o.leading}</span>}
              <span className="mrs-combo-label">{o.label}</span>
              {o.hint && <span className="mrs-combo-hint">{o.hint}</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

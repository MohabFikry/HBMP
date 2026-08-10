import { useEffect, useId, useRef, useState } from "react";
import type { ReactNode } from "react";
import { cx } from "../lib/cx";
import { Icon } from "./Icon";
import { PopupPortal, useAnchoredPopup } from "./Popup";

export interface SelectOption {
  value: string;
  label: string;
  /** Optional secondary text shown after the label (e.g. "Home"). */
  hint?: string;
}

export interface SelectProps {
  options: SelectOption[];
  /** The selected value, or null for "nothing chosen" (renders `placeholder`). */
  value: string | null;
  onChange: (value: string) => void;
  /** Accessible name. Supply this or `aria-labelledby`. */
  "aria-label"?: string;
  "aria-labelledby"?: string;
  id?: string;
  /** Rendered inside the trigger, before the value — sized by CSS, so pass a bare <Icon />. */
  leadingIcon?: ReactNode;
  placeholder?: string;
  disabled?: boolean;
  className?: string;
  /** Pill silhouette to match the app-bar search; default is the field radius. */
  shape?: "pill" | "field";
}

/**
 * Accessible select (APG "select-only combobox"). A native <select> cannot style its own option list — the
 * popup is drawn by the OS, so it arrived with a system-blue highlight and square corners no matter what CSS
 * we wrote. This renders the list ourselves so it can wear the Mersal surface, radius and accent.
 *
 * Focus never leaves the trigger: the active option is communicated with `aria-activedescendant`, which is
 * what lets a screen reader announce the option while keystrokes keep arriving at the combobox. Keyboard:
 * Arrow/Home/End move, Enter/Space commit, Escape reverts, printable characters jump (typeahead), Tab closes.
 */
export function Select({
  options,
  value,
  onChange,
  id,
  leadingIcon,
  placeholder,
  disabled,
  className,
  shape = "field",
  ...aria
}: SelectProps) {
  const autoId = useId();
  const triggerId = id ?? `${autoId}-trigger`;
  const listId = `${autoId}-list`;
  const optionId = (i: number) => `${autoId}-opt-${i}`;

  const [open, setOpen] = useState(false);
  const selectedIndex = options.findIndex((o) => o.value === value);
  // Which option the keyboard is "on". Distinct from the selection until the user commits.
  const [active, setActive] = useState(selectedIndex < 0 ? 0 : selectedIndex);
  const rootRef = useRef<HTMLDivElement>(null);
  const listRef = useRef<HTMLUListElement>(null);
  const typed = useRef({ buffer: "", at: 0 });

  // Reopening should start from the current selection, not from wherever the last visit ended.
  useEffect(() => {
    if (open) setActive(selectedIndex < 0 ? 0 : selectedIndex);
  }, [open, selectedIndex]);

  // Keep the active option in view when arrowing through a list taller than the popup.
  useEffect(() => {
    if (!open) return;
    const el = listRef.current?.querySelector<HTMLElement>(`#${CSS.escape(optionId(active))}`);
    // Guard the method itself, not just the element: jsdom leaves scrollIntoView undefined.
    el?.scrollIntoView?.({ block: "nearest" });
  }, [open, active]);

  useEffect(() => {
    if (!open) return;
    // pointerdown, not click: a click that starts inside and ends outside should not close, and pointerdown
    // fires before the browser moves focus.
    function onPointerDown(e: PointerEvent) {
      const t = e.target as Node;
      // BOTH refs. The list is portalled to <body>, so it is no longer inside `rootRef` — checking only that
      // one would treat every click on an option as an outside click and close the popup before it commits.
      if (!rootRef.current?.contains(t) && !listRef.current?.contains(t)) setOpen(false);
    }
    document.addEventListener("pointerdown", onPointerDown);
    return () => document.removeEventListener("pointerdown", onPointerDown);
  }, [open]);

  function commit(i: number) {
    const opt = options[i];
    if (opt) onChange(opt.value);
    setOpen(false);
  }

  function typeahead(char: string) {
    const now = Date.now();
    typed.current.buffer = now - typed.current.at > 700 ? char : typed.current.buffer + char;
    typed.current.at = now;
    const q = typed.current.buffer.toLowerCase();
    const from = typed.current.buffer.length === 1 ? active + 1 : active;
    for (let n = 0; n < options.length; n++) {
      const i = (from + n) % options.length;
      if (options[i].label.toLowerCase().startsWith(q)) {
        setActive(i);
        if (!open) commit(i);
        return;
      }
    }
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (disabled) return;
    const last = options.length - 1;

    if (e.key === "Escape") {
      if (open) {
        e.preventDefault();
        setOpen(false);
      }
      return;
    }
    if (e.key === "Tab") {
      setOpen(false);
      return;
    }
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      if (open) commit(active);
      else setOpen(true);
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
      return;
    }
    // Printable, non-modified characters drive typeahead — the behaviour a native select has and users expect.
    if (e.key.length === 1 && !e.ctrlKey && !e.metaKey && !e.altKey) {
      e.preventDefault();
      typeahead(e.key);
    }
  }

  const selected = selectedIndex < 0 ? null : options[selectedIndex];
  const popup = useAnchoredPopup(rootRef, listRef, open);

  return (
    <div ref={rootRef} className={cx("mrs-select", `mrs-select--${shape}`, className)}>
      <button
        type="button"
        id={triggerId}
        role="combobox"
        aria-expanded={open}
        aria-controls={listId}
        aria-haspopup="listbox"
        aria-activedescendant={open && options.length > 0 ? optionId(active) : undefined}
        aria-label={aria["aria-label"]}
        aria-labelledby={aria["aria-labelledby"]}
        disabled={disabled}
        className="mrs-select-trigger"
        onClick={() => setOpen((o) => !o)}
        onKeyDown={onKeyDown}
      >
        {leadingIcon}
        <span className={cx("mrs-select-value", !selected && "is-placeholder")}>
          {selected ? selected.label : (placeholder ?? "")}
          {selected?.hint && <span className="mrs-select-hint"> · {selected.hint}</span>}
        </span>
        <Icon name="chevron" className="mrs-select-chevron" aria-hidden />
      </button>

      {/*
        Rendered only while open so the closed control contributes nothing to the a11y tree or hit-testing,
        and PORTALLED out of this control so no scrolling ancestor can clip it — see `Popup.tsx` for why a
        modal's `overflow: auto` cut seven of eight options off the bottom of this list. `aria-controls` on
        the trigger is what keeps the two halves associated across the portal.
      */}
      {open && (
        <PopupPortal>
        <ul
          ref={listRef}
          id={listId}
          role="listbox"
          className="mrs-select-list mrs-popup mrs-scroll"
          data-flipped={popup.flipped || undefined}
          style={popup.style}
          aria-label={aria["aria-label"]}
          aria-labelledby={aria["aria-labelledby"]}
        >
          {options.map((o, i) => (
            <li
              key={o.value}
              id={optionId(i)}
              role="option"
              aria-selected={o.value === value}
              className={cx("mrs-select-option", i === active && "is-active")}
              // The trigger must keep focus for aria-activedescendant to mean anything, and mousedown is
              // where the browser would otherwise move it.
              onMouseDown={(e) => e.preventDefault()}
              onClick={() => commit(i)}
              onMouseEnter={() => setActive(i)}
            >
              <Icon name="ok" className="mrs-select-check" aria-hidden />
              <span className="mrs-select-option-label">
                {o.label}
                {o.hint && <span className="mrs-select-hint"> · {o.hint}</span>}
              </span>
            </li>
          ))}
        </ul>
        </PopupPortal>
      )}
    </div>
  );
}

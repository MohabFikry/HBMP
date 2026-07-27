import { useEffect, useMemo, useRef, useState } from "react";
import { useTheme } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type { Section } from "../portals/catalog";

/**
 * Phase 18.F2 — the command palette (⌘K / Ctrl+K).
 *
 * This is what the dead app-bar search should have been, and it is why 18.D2 deleted that field rather than
 * leaving it as a placeholder: a control that does nothing teaches people not to reach for it, and by the
 * time the real thing arrives they have stopped looking.
 *
 * It matters more here than in a typical app because of the shape of this platform: sixteen role portals,
 * each with five to nine sections. The nav rail shows one portal's sections at a time, so "where is the
 * settlement export" is a question the UI cannot currently answer — you either know or you hunt. A palette
 * is the only navigation that scales across that many surfaces without adding a second menu.
 *
 * PERMISSION-SCOPED BY CONSTRUCTION. It is handed the caller's already-filtered section list — the same
 * array the nav rail renders — so it cannot surface a destination the user may not open. That is deliberate:
 * a palette that lists everything and 403s on selection is an enumeration oracle for the whole platform, and
 * it is exactly the mistake that makes "search" a security finding rather than a feature.
 *
 * No PHI, no records, no free-text server search. Sections only, for now: those are safe to list, need no
 * round trip, and cover the navigation problem. Searching RECORDS means sending a query with a beneficiary
 * name in it, which needs the min-necessary rules and a PHI-read audit on the server side — a real feature,
 * not a palette detail, and not something to bolt on quietly.
 */
export interface CommandPaletteProps {
  open: boolean;
  onClose: () => void;
  /** Already permission-filtered — the palette never filters for authorization itself. */
  sections: Section[];
  portalBase: string;
  onNavigate: (path: string) => void;
}

const S = {
  title: { en: "Go to…", ar: "الانتقال إلى…" },
  placeholder: { en: "Search sections", ar: "ابحث في الأقسام" },
  empty: { en: "No matching section.", ar: "لا يوجد قسم مطابق." },
  hint: { en: "↑↓ to move · Enter to open · Esc to close", ar: "↑↓ للتنقل · Enter للفتح · Esc للإغلاق" },
  count: { en: "results", ar: "نتيجة" },
} satisfies Record<string, Localized>;

/**
 * Subsequence match, not substring: "fset" finds "Finance · Settlements".
 *
 * Chosen over exact substring because a user reaching for the palette is recalling a destination, not typing
 * its name — they know it is the settlements one under finance. Ranking prefers earlier and tighter matches
 * so the obvious answer stays first; without the tightness term, a long label containing the letters far
 * apart outranks the short label that actually starts with them.
 */
function score(haystack: string, needle: string): number | null {
  if (needle.length === 0) return 0;
  const h = haystack.toLowerCase();
  const n = needle.toLowerCase();
  let hi = 0, first = -1, last = 0;
  for (const ch of n) {
    const found = h.indexOf(ch, hi);
    if (found === -1) return null;
    if (first === -1) first = found;
    last = found;
    hi = found + 1;
  }
  return first * 2 + (last - first);   // earlier start and tighter span rank higher (lower is better)
}

export function CommandPalette({ open, onClose, sections, portalBase, onNavigate }: CommandPaletteProps) {
  const { lang } = useTheme();
  const t = (l: Localized) => (lang === "ar" ? l.ar : l.en);
  const [query, setQuery] = useState("");
  const [active, setActive] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listId = "cmdk-list";

  const results = useMemo(() => {
    const scored = sections
      .map((s) => ({ section: s, rank: score(`${t(s.group)} ${t(s.label)}`, query) }))
      .filter((r): r is { section: Section; rank: number } => r.rank !== null);
    scored.sort((a, b) => a.rank - b.rank);
    return scored.slice(0, 8).map((r) => r.section);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [sections, query, lang]);

  // Reset on each open: a palette that remembers the last query makes the second use slower than the first.
  useEffect(() => {
    if (open) {
      setQuery("");
      setActive(0);
      // Focus directly rather than via requestAnimationFrame: the effect already runs after the dialog is
      // in the DOM, and deferring a frame leaves a window in which the first keystroke goes to the page
      // behind the palette — which for ⌘K-then-type (the normal usage) loses the first character.
      inputRef.current?.focus();
    }
  }, [open]);

  useEffect(() => setActive(0), [query]);

  if (!open) return null;

  function choose(section: Section) {
    onNavigate(`/${portalBase}/${section.path}`);
    onClose();
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (e.key === "Escape") { e.preventDefault(); onClose(); return; }
    if (e.key === "ArrowDown") { e.preventDefault(); setActive((i) => (i + 1) % Math.max(results.length, 1)); return; }
    if (e.key === "ArrowUp") { e.preventDefault(); setActive((i) => (i - 1 + results.length) % Math.max(results.length, 1)); return; }
    if (e.key === "Enter" && results[active]) { e.preventDefault(); choose(results[active]); }
  }

  return (
    // The scrim closes on an outside click; the dialog stops propagation so a click inside does not.
    <div className="cmdk-overlay" onMouseDown={onClose}>
      <div
        className="cmdk"
        role="dialog"
        aria-modal="true"
        aria-label={t(S.title)}
        onMouseDown={(e) => e.stopPropagation()}
        onKeyDown={onKeyDown}
      >
        {/*
          combobox + listbox rather than a bare input: without the roles a screen-reader user gets a text
          field that does nothing visible, because the results appear in a region they are never told about.
          aria-activedescendant keeps focus IN the input (so typing keeps working) while announcing the
          highlighted option — the pattern roving tabindex cannot achieve here.
        */}
        <input
          ref={inputRef}
          className="cmdk-input"
          role="combobox"
          aria-expanded="true"
          aria-controls={listId}
          aria-autocomplete="list"
          aria-activedescendant={results[active] ? `cmdk-opt-${results[active].key}` : undefined}
          aria-label={t(S.placeholder)}
          placeholder={t(S.placeholder)}
          value={query}
          onChange={(e) => setQuery(e.currentTarget.value)}
        />
        <ul id={listId} role="listbox" aria-label={t(S.title)} className="cmdk-list">
          {results.map((s, i) => (
            <li
              key={s.key}
              id={`cmdk-opt-${s.key}`}
              role="option"
              aria-selected={i === active}
              className={i === active ? "cmdk-opt cmdk-opt-active" : "cmdk-opt"}
              onMouseEnter={() => setActive(i)}
              onMouseDown={() => choose(s)}
            >
              <span className="cmdk-group">{t(s.group)}</span>
              <span className="cmdk-label">{t(s.label)}</span>
            </li>
          ))}
          {results.length === 0 && <li className="cmdk-empty">{t(S.empty)}</li>}
        </ul>
        {/* Announced to assistive tech as the list changes; visually it is the keyboard hint. */}
        <p className="cmdk-hint" aria-live="polite">
          <span className="sr-only">{results.length} {t(S.count)}. </span>
          {t(S.hint)}
        </p>
      </div>
    </div>
  );
}

import { useEffect, useId, useRef, useState } from "react";
import type { CptRef, CptSection } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useLoc } from "../_shared";

const S = {
  labelLab: { en: "Test", ar: "الفحص" },
  labelImaging: { en: "Study", ar: "الفحص التصويري" },
  placeholder: { en: "Search by CPT code or name…", ar: "ابحث بكود CPT أو الاسم…" },
  results: { en: "results", ar: "نتيجة" },
  none: { en: "No procedure in this section matches that search.", ar: "لا يوجد إجراء في هذا القسم مطابق لهذا البحث." },
  searching: { en: "Searching…", ar: "جارٍ البحث…" },
  hint: { en: "Type at least 2 characters", ar: "اكتب حرفين على الأقل" },
  change: { en: "Change", ar: "تغيير" },
};

/**
 * The ordering combobox — ONE field over CPT code and procedure name.
 *
 * <b>Why one field.</b> The same reasoning as <c>DrugCombobox</c>: a doctor reaches for whichever they know.
 * Some type "71046" because it is on the request form in front of them; most type "chest x-ray". Splitting
 * that into a code box and a name box makes them decide which kind of thing they are about to type before
 * they have typed it.
 *
 * <b>Why the code stays visible after choosing.</b> It is what travels to the performing site, appears on
 * the worklist and gets quoted in a claim. A screen that shows only the description after selection hides
 * the one string everyone downstream actually works from.
 *
 * <b>Scoped to its tab's sections.</b> Imaging searches the Imaging section; Labs searches Laboratory AND
 * Pathology — a list, because one tab is not one section. Resolved server-side against the published CPT
 * ranges. Ordering a scan from the lab tab is therefore not something a doctor has to notice and avoid; it
 * is not offered. (The server still refuses it on submit — a filtered list is a convenience, not a rule.)
 *
 * <b>The order of the results is the server's.</b> Type a digit and codes lead; type a word and descriptions
 * lead. Twenty rows is the top of a ranked list, not a sample of it, so re-sorting them here would discard
 * the ranking and reorder a page whose membership already depended on it.
 */
export function CptCombobox({
  value,
  sections,
  onChange,
  disabled,
}: {
  value: CptRef | null;
  sections: readonly CptSection[];
  onChange: (test: CptRef | null) => void;
  disabled?: boolean;
}) {
  const api = useApi();
  const t = useLoc();
  const listId = useId();
  const inputId = useId();
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<CptRef[]>([]);
  const [searching, setSearching] = useState(false);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  const label = sections.includes("Imaging") ? S.labelImaging : S.labelLab;

  // Debounced with a 2-character floor — a request per keystroke asks the catalogue about "c" and "ch" on
  // the way to a search nobody wanted the first two of.
  useEffect(() => {
    let live = true;
    const q = query.trim();
    if (q.length < 2) {
      setResults([]);
      setSearching(false);
      return;
    }
    setSearching(true);
    const controller = new AbortController();
    const timer = setTimeout(() => {
      // The signal, not just the `live` flag. `live` stops a superseded answer from being RENDERED; the
      // signal stops it from being FETCHED. On a 250ms debounce a considered search leaves several requests
      // running against the catalogue that nobody will ever look at.
      api.searchCpt(q, sections, controller.signal).then(
        (rows) => { if (live) { setResults(rows); setSearching(false); setActive(0); } },
        () => { if (live) { setResults([]); setSearching(false); } },
      );
    }, 250);
    return () => { live = false; clearTimeout(timer); controller.abort(); };
    // `sections.join()` and not `sections`: the caller passes an array literal, so a new identity every
    // render would re-run this effect on every keystroke elsewhere on the screen. What matters is WHICH
    // sections, not which array.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, query, sections.join(",")]);

  function choose(test: CptRef) {
    onChange(test);
    setOpen(false);
    setQuery("");
    setResults([]);
  }

  function onKeyDown(e: React.KeyboardEvent) {
    if (!open || results.length === 0) {
      if (e.key === "ArrowDown" && results.length > 0) { setOpen(true); e.preventDefault(); }
      return;
    }
    if (e.key === "ArrowDown") { setActive((i) => (i + 1) % results.length); e.preventDefault(); }
    else if (e.key === "ArrowUp") { setActive((i) => (i - 1 + results.length) % results.length); e.preventDefault(); }
    else if (e.key === "Enter") { choose(results[active]); e.preventDefault(); }
    else if (e.key === "Escape") { setOpen(false); e.preventDefault(); }
  }

  if (value && !open) {
    return (
      <div className="rx-drug-chosen">
        <span className="rx-drug-trade">{value.description}</span>
        <span className="rx-drug-sub tnum">CPT {value.code}</span>
        <button
          type="button"
          className="rx-drug-change"
          disabled={disabled}
          onClick={() => { setOpen(true); queueMicrotask(() => inputRef.current?.focus()); }}
        >
          {t(S.change)}
        </button>
      </div>
    );
  }

  return (
    <div className="rx-combobox">
      <label className="rx-combobox-label" htmlFor={inputId}>{t(label)}</label>
      <input
        id={inputId}
        ref={inputRef}
        className="rx-combobox-input"
        role="combobox"
        aria-expanded={open && results.length > 0}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={open && results[active] ? `${listId}-opt-${results[active].code}` : undefined}
        placeholder={t(S.placeholder)}
        disabled={disabled}
        value={query}
        onChange={(e) => { setQuery(e.currentTarget.value); setOpen(true); }}
        onKeyDown={onKeyDown}
      />
      <p className="rx-combobox-hint" aria-live="polite">
        {query.trim().length < 2
          ? t(S.hint)
          : searching
            ? t(S.searching)
            : `${results.length} ${t(S.results)}`}
      </p>
      {open && results.length > 0 && (
        <ul id={listId} role="listbox" aria-label={t(label)} className="rx-combobox-list mrs-scroll">
          {results.map((c, i) => (
            <li
              key={c.code}
              id={`${listId}-opt-${c.code}`}
              role="option"
              aria-selected={i === active}
              className={i === active ? "rx-combobox-opt rx-combobox-opt--active" : "rx-combobox-opt"}
              onMouseEnter={() => setActive(i)}
              onMouseDown={() => choose(c)}
            >
              <span className="rx-combobox-trade">{c.description}</span>
              <span className="rx-combobox-sub tnum">CPT {c.code}</span>
            </li>
          ))}
        </ul>
      )}
      {open && !searching && query.trim().length >= 2 && results.length === 0 && (
        <p className="rx-combobox-empty">{t(S.none)}</p>
      )}
    </div>
  );
}

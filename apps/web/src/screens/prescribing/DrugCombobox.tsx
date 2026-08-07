import { useEffect, useId, useRef, useState } from "react";
import type { PrescribableDrug } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useLoc } from "../_shared";

const S = {
  // 29.7 — design 45 §7.
  lowestPrice: { en: "Lowest price", ar: "الأقل سعراً" },
  unavailable: { en: "Unavailable", ar: "غير متوفر" },
  unavailableHint: {
    en: "This product is out of stock. Alternatives with the same active ingredient are listed below.",
    ar: "هذا المنتج غير متوفر. البدائل بنفس المادة الفعالة مدرجة أدناه.",
  },
  label: { en: "Medicine", ar: "الدواء" },
  placeholder: { en: "Search by trade name or ingredient…", ar: "ابحث بالاسم التجاري أو المادة الفعالة…" },
  results: { en: "results", ar: "نتيجة" },
  none: { en: "No medicine matches that search.", ar: "لا يوجد دواء مطابق لهذا البحث." },
  searching: { en: "Searching…", ar: "جارٍ البحث…" },
  hint: { en: "Type at least 2 characters", ar: "اكتب حرفين على الأقل" },
  change: { en: "Change medicine", ar: "تغيير الدواء" },
  noIndicationData: { en: "No indication data", ar: "لا توجد بيانات دواعي استعمال" },
};

/**
 * The prescribing combobox — ONE field over trade name and active ingredient.
 *
 * <b>Why one field.</b> A prescriber searches by whichever name they know: "augmentin" and "amoxicillin"
 * must both reach the same product (doc 43 §6). Splitting them into two fields makes the doctor decide, up
 * front, which kind of name they are about to type.
 *
 * <b>Why the ingredient is rendered under the trade name.</b> It is a safety feature, not decoration. Two
 * boxes with different trade names holding the same molecule is the commonest prescribing duplication, and
 * showing the ingredient at the moment of choosing is the cheapest defence against it.
 *
 * A real ARIA 1.2 combobox — roles, `aria-activedescendant`, `aria-expanded` — following the pattern
 * `CommandPalette` already establishes here rather than inventing a second one. A styled div would leave a
 * screen-reader user with a text field whose results they are never told about.
 */
export function DrugCombobox({
  value,
  onChange,
  disabled,
}: {
  value: PrescribableDrug | null;
  onChange: (drug: PrescribableDrug | null) => void;
  disabled?: boolean;
}) {
  const api = useApi();
  const t = useLoc();
  const listId = useId();
  const inputId = useId();
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<PrescribableDrug[]>([]);
  const [searching, setSearching] = useState(false);
  const [open, setOpen] = useState(false);
  const [active, setActive] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);

  // Debounced, and a floor of 2 characters. A request per keystroke asks the catalogue about "a", "au" and
  // "aug" on the way to a search nobody wanted the first two of — the same reasoning DiagnosisPicker records.
  useEffect(() => {
    let live = true;
    const q = query.trim();
    if (q.length < 2) {
      setResults([]);
      setSearching(false);
      return;
    }
    setSearching(true);
    const timer = setTimeout(() => {
      api.searchPrescribableDrugs(q).then(
        (rows) => { if (live) { setResults(rows); setSearching(false); setActive(0); } },
        () => { if (live) { setResults([]); setSearching(false); } },
      );
    }, 250);
    return () => { live = false; clearTimeout(timer); };
  }, [api, query]);

  function choose(drug: PrescribableDrug) {
    onChange(drug);
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

  // Chosen: show what was picked, with the ingredient still visible. A combobox that hides the ingredient
  // after selection gives the safety cue away at exactly the moment the line is being reviewed.
  if (value && !open) {
    return (
      <div className="rx-drug-chosen">
        <span className="rx-drug-trade">{t(value.tradeName)}</span>
        <span className="rx-drug-sub">
          {value.activeIngredient ?? "—"}
          {value.strength ? ` · ${value.strength}` : ""}
          {typeof value.priceEgp === "number" ? ` · ${value.priceEgp} EGP` : ""}
          {!value.hasIndicationData && (
            <span className="rx-drug-flag"> · {t(S.noIndicationData)}</span>
          )}
        </span>
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
      <label className="rx-combobox-label" htmlFor={inputId}>{t(S.label)}</label>
      <input
        id={inputId}
        ref={inputRef}
        className="rx-combobox-input"
        role="combobox"
        aria-expanded={open && results.length > 0}
        aria-controls={listId}
        aria-autocomplete="list"
        aria-activedescendant={open && results[active] ? `${listId}-opt-${results[active].drugId}` : undefined}
        placeholder={t(S.placeholder)}
        disabled={disabled}
        value={query}
        onChange={(e) => { setQuery(e.currentTarget.value); setOpen(true); }}
        onKeyDown={onKeyDown}
      />
      {/* Always rendered so assistive tech has a stable live region to announce into. */}
      <p className="rx-combobox-hint" aria-live="polite">
        {query.trim().length < 2
          ? t(S.hint)
          : searching
            ? t(S.searching)
            : `${results.length} ${t(S.results)}`}
      </p>
      {open && results.length > 0 && (
        <ul id={listId} role="listbox" aria-label={t(S.label)} className="rx-combobox-list mrs-scroll">
          {results.map((d, i) => (
            <li
              key={d.drugId}
              id={`${listId}-opt-${d.drugId}`}
              role="option"
              aria-selected={i === active}
              className={i === active ? "rx-combobox-opt rx-combobox-opt--active" : "rx-combobox-opt"}
              onMouseEnter={() => setActive(i)}
              onMouseDown={() => choose(d)}
            >
              {/* Trade name carries the title weight; ingredient, strength and price sit beneath it,
                  smaller and muted — the layout doc 43 §6 specifies, for the duplication reason above. */}
              <span className="rx-combobox-trade">{t(d.tradeName)}</span>
              <span className="rx-combobox-sub">
                {d.activeIngredient ?? "—"}
                {d.strength ? ` · ${d.strength}` : ""}
                {typeof d.priceEgp === "number" ? ` · ${d.priceEgp} EGP` : ""}
              </span>
              {/*
                29.7 — the lowest-price chip, beside the price already shown (design 45 §7). The comparison
                behind it is per PRESCRIBING UNIT within ingredient + strength + form: a 20-tablet pack at
                100 EGP is MORE expensive per tablet than a 30-tablet pack at 120 EGP, so a chip driven by
                pack price would point a prescriber at the dearer box.
              */}
              {d.isLowestPrice && (
                <span className="rx-combobox-chip" data-kind="lowest-price">{t(S.lowestPrice)}</span>
              )}
              {/*
                AVAILABILITY: only a POSITIVE "Unavailable" renders. `Unknown` — the default for all 31,651
                drugs until stock data exists — renders NOTHING at all: not a warning, not a neutral chip,
                nothing. An indicator that fires on every row is an indicator prescribers stop seeing.
              */}
              {d.availability === "Unavailable" && (
                <span className="rx-combobox-chip" data-kind="unavailable" title={t(S.unavailableHint)}>
                  {t(S.unavailable)}
                </span>
              )}
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

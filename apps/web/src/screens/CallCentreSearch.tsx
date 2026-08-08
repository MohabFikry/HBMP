import { Button, InputField, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";
import type { CcMatch } from "./CallCentre";

/**
 * ONE search box for the whole call centre.
 *
 * <b>Why there is no "search by" picker any more.</b> There used to be one on the booking journey, offering
 * Phone / MemberNo / NationalId / Passport / RefugeeId / UnhcrNo / FullName. It never narrowed anything: the
 * reception index behind this matches every one of those columns on every query, and multi-word input is
 * treated as a name. The picker only set the on-screen keypad and the example — which its own help text
 * admitted — while costing the agent a decision on every call and implying that guessing wrong would lose the
 * member.
 *
 * <b>Why it looks like reception's.</b> Same markup and the same `book-search` / `book-hits` classes the
 * reception desk uses, because it is the same job: find a person, pick them. The call centre had grown its own
 * `cc-search` / `cc-results` pair that drifted into a different shape — a whole-row button with the name and
 * number pushed to opposite edges — so the two front-of-house screens taught two different gestures for one
 * task. A row states who was found; a Choose button picks them.
 *
 * The Choose button is labelled with the MEMBER'S NAME, not just "Choose": a list of identical "Choose"
 * buttons has no usable accessible name, and a screen-reader user arrowing the list hears the same word for
 * every row.
 */
export function MemberSearch({
  query, onQueryChange, onSearch, results, onSelect, busy,
}: {
  query: string;
  onQueryChange: (v: string) => void;
  onSearch: () => void;
  /** `null` means "no search has been run" — distinct from `[]`, which means "searched, found nobody". */
  results: CcMatch[] | null;
  onSelect: (m: CcMatch) => void;
  busy?: boolean;
}) {
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang];

  return (
    <>
      <form
        className="book-search"
        noValidate
        onSubmit={(e) => { e.preventDefault(); onSearch(); }}
      >
        <InputField
          label={t(L.ccSearchLabel)}
          help={t(L.ccSearchHelp)}
          value={query}
          onChange={(e) => onQueryChange(e.target.value)}
        />
        <Button type="submit" variant="secondary" loading={busy}>{t(L.ccSearch)}</Button>
      </form>

      {results && results.length === 0 && <p role="status">{t(L.ccNoResults)}</p>}
      {results && results.length > 0 && (
        <ul className="book-hits">
          {results.map((m) => (
            <li key={m.beneficiaryId}>
              <span>
                <strong>{m.displayName}</strong>{" "}
                {/* The real member number. It arrived masked (•••001) while MemberNo was an identifier the
                    agent could be challenged on; with the challenge gone, a mask is pure cost — this list is
                    exactly where an agent tells two people with the same name apart. */}
                {m.memberNo && <span className="tnum muted">{m.memberNo}</span>}
              </span>
              <Button
                variant="secondary"
                size="sm"
                aria-label={`${t(L.ccChoose)} — ${m.displayName}`}
                onClick={() => onSelect(m)}
              >
                {t(L.ccChoose)}
              </Button>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

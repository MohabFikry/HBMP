import { Button, InputField, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";
import type { CcMatch } from "./CallCentre";

/**
 * ONE search box for the whole call centre.
 *
 * <b>Why there is no "search by" picker any more.</b> There used to be one on the booking journey, offering
 * Phone / MemberNo / NationalId / Passport / RefugeeId / UnhcrNo / FullName. It never narrowed anything: the
 * reception index behind this matches every one of those columns on every query, and multi-word input is treated
 * as a name. The picker only set the on-screen keypad and the example — which its own help text admitted — while
 * costing the agent a decision on every call and implying that guessing wrong would lose the member.
 *
 * A caller says "it's Hana Mansour" or reads a number off their card. The agent types it. That is the whole
 * interaction, and one field is the honest shape of it.
 *
 * Rendered by both the workspace and the standalone booking journey so the two cannot drift apart — the picker
 * existing on one screen and not the other is how they drifted in the first place.
 */
export function MemberSearch({
  query, onQueryChange, onSearch, results, selectedId, onSelect, disabled,
}: {
  query: string;
  onQueryChange: (v: string) => void;
  onSearch: () => void;
  /** `null` means "no search has been run" — distinct from `[]`, which means "searched, found nobody". */
  results: CcMatch[] | null;
  selectedId?: string | null;
  onSelect: (m: CcMatch) => void;
  disabled?: boolean;
}) {
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang];

  return (
    <>
      <div className="cc-search">
        <InputField
          label={t(L.ccSearchLabel)}
          help={t(L.ccSearchHelp)}
          value={query}
          onChange={(e) => onQueryChange(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") onSearch(); }}
          disabled={disabled}
        />
        <Button variant="secondary" onClick={onSearch} disabled={disabled}>{t(L.ccSearch)}</Button>
      </div>

      {results && results.length === 0 && <p role="status">{t(L.ccNoResults)}</p>}
      {results && results.length > 0 && (
        <ul className="cc-results">
          {results.map((m) => (
            <li key={m.beneficiaryId}>
              <button
                type="button"
                className="cc-result"
                onClick={() => onSelect(m)}
                aria-pressed={selectedId === m.beneficiaryId}
                disabled={disabled}
              >
                <span>{m.displayName}</span>
                {/* The real member number. It arrived masked (•••001) while MemberNo was an identifier the agent
                    could be asked to challenge on; with the challenge gone, a mask is pure cost — this list is
                    exactly where an agent tells two people with the same name apart. */}
                {m.memberNo && <span className="cc-muted">{m.memberNo}</span>}
              </button>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

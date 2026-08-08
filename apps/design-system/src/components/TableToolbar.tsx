import { useId, type ReactNode } from "react";
import { Icon } from "./Icon";
import { cx } from "../lib/cx";
import { useTheme } from "../theme/ThemeProvider";

export interface FilterChip {
  value: string;
  label: string;
  /** Optional count shown after the label — "Booked · 12". Omit when a count would be noise or misleading. */
  count?: number;
}

export interface FilterGroup {
  /** Stable id, used for the group's accessible name and to key the selection. */
  key: string;
  /** Visible legend, e.g. "Status" or "When". */
  label: string;
  options: FilterChip[];
  /** Currently selected value. `null` means "no filter" and renders no chip pressed. */
  value: string | null;
  onChange: (value: string | null) => void;
  /**
   * Extra controls belonging to THIS group, rendered immediately after its chips.
   *
   * The appointments board's custom date range is the case that needed it: those two fields exist only
   * because the "Custom range" chip was pressed, and living in the toolbar's trailing slot left them at the
   * far end of the bar, visually detached from the control that revealed them. A follow-up belongs next to
   * what it follows.
   */
  extra?: ReactNode;
}

export interface TableToolbarProps {
  /** Search box. Omit entirely for a table with nothing worth searching. */
  search?: {
    label: string;
    value: string;
    onChange: (value: string) => void;
    placeholder?: string;
  };
  filters?: FilterGroup[];
  /** Anything else — a date-range pair, an export button. Rendered at the end of the bar. */
  children?: ReactNode;
  className?: string;
}

/**
 * The toolbar above a worklist: search, plus one or more single-select filter groups.
 *
 * ============================================================================================================
 * WHY THIS IS A COMPONENT AND NOT A PATTERN EACH SCREEN REPEATS
 * ============================================================================================================
 * "Sortable columns, search, filters" was agreed as the house standard for tables generally. A standard that
 * lives in a document is one every screen implements slightly differently: one puts filters above the search,
 * another uses a dropdown where its neighbour uses chips, a third forgets that a pressed chip needs a
 * non-colour cue and ships a filter a colour-blind operator cannot see is active. The way to make a standard
 * hold is to make it the path of least resistance, which means shipping it.
 *
 * ============================================================================================================
 * WHY CHIPS RATHER THAN A DROPDOWN
 * ============================================================================================================
 * These filters are small, closed sets — Today / This week / Custom; Booked / Checked in / No-show — and the
 * operator switches between them constantly at a busy desk. A dropdown hides the current value behind a click
 * and costs two interactions to change; chips show every option and the active one at a glance, and cost one.
 * A long or open-ended vocabulary would want `Combobox` instead.
 *
 * Selection is conveyed by `aria-pressed` AND a filled style AND a check glyph — never by colour alone
 * (21-accessibility). Clicking the active chip clears it, which is the behaviour people try first.
 */
export function TableToolbar({ search, filters = [], children, className }: TableToolbarProps) {
  const { lang } = useTheme();
  const searchId = useId();
  const ar = lang === "ar";

  return (
    <div className={cx("mrs-toolbar", className)}>
      {search && (
        <div className="mrs-toolbar-search">
          <label className="mrs-label" htmlFor={searchId}>{search.label}</label>
          <div className="mrs-toolbar-searchbox">
            <Icon name="search" width={16} height={16} aria-hidden="true" />
            <input
              id={searchId}
              type="search"
              className="mrs-control"
              value={search.value}
              placeholder={search.placeholder}
              onChange={(e) => search.onChange(e.currentTarget.value)}
              autoComplete="off"
            />
          </div>
        </div>
      )}

      {filters.map((group) => (
        // A fieldset + legend, so a screen-reader user hears "Status: Booked, pressed" rather than an
        // unattributed row of buttons whose meaning was only ever conveyed by their position on screen.
        <fieldset key={group.key} className="mrs-toolbar-group">
          {/*
            The legend carries the accessible name and NOTHING visual.

            A rendered `<legend>` is laid out at the top of its fieldset's box, not in normal flow — so the
            moment a group's `extra` made the fieldset taller (the date range is a label-over-control pair,
            roughly 30px taller than a chip), "WHEN" floated to the top of that taller box while SEARCH, FROM,
            TO and STATUS stayed on the label line below it. Nothing was misaligned by accident; the legend
            was simply measuring a different box from every other label in the bar.

            Splitting the two — legend for the screen reader, a span for the eye — is what makes the visible
            label an ordinary flow item that can sit directly above its chips. The span is aria-hidden so the
            group's name is announced once, not twice.
          */}
          <legend className="sr-only">{group.label}</legend>
          <div className="mrs-chipset">
            {/* Label over chips, so the pair is the same label-over-control shape as the search box and as
                anything a caller passes in `extra` — which is what puts every label in the bar on one line. */}
            <div className="mrs-toolbar-chipcol">
              <span className="mrs-toolbar-grouplabel" aria-hidden="true">{group.label}</span>
              <div className="mrs-toolbar-chips">
                {group.options.map((o) => {
                  const active = group.value === o.value;
                  return (
                    <button
                      key={o.value}
                      type="button"
                      className={cx("mrs-filterchip", active && "mrs-on")}
                      aria-pressed={active}
                      // Pressing the active chip clears the filter — the first thing anyone tries, and without
                      // it a single-select group is a trap you cannot get out of once you have chosen.
                      onClick={() => group.onChange(active ? null : o.value)}
                    >
                      {active && <Icon name="ok" width={12} height={12} aria-hidden="true" />}
                      <span>{o.label}</span>
                      {o.count !== undefined && <span className="mrs-chipcount tnum">{o.count}</span>}
                    </button>
                  );
                })}
              </div>
            </div>
            {/* Inside the chipset so it wraps with the chips and shares their baseline. */}
            {group.extra}
          </div>
        </fieldset>
      ))}

      {children && <div className="mrs-toolbar-extra">{children}</div>}

      {/* The live region names the ACTIVE filters after every change. Without it, a screen-reader user who
          clears a chip hears nothing and cannot tell whether the table is filtered or simply empty. */}
      <div className="sr-only" aria-live="polite">
        {filters
          .filter((g) => g.value)
          .map((g) => `${g.label}: ${g.options.find((o) => o.value === g.value)?.label ?? g.value}`)
          .join(", ") || (ar ? "لا توجد عوامل تصفية" : "No filters applied")}
      </div>
    </div>
  );
}

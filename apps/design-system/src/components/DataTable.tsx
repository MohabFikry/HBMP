import { useMemo, useRef, useState, type ReactNode } from "react";
import { Icon } from "./Icon";
import { cx } from "../lib/cx";
import { sortRows } from "../lib/sortRows";
import { useTheme } from "../theme/ThemeProvider";

export interface Column<Row> {
  key: string;
  header: string;
  /** Cell renderer. */
  cell: (row: Row) => ReactNode;
  sortable?: boolean;
  /**
   * The value to sort this column BY, for the built-in (uncontrolled) sort.
   *
   * Required for a `sortable` column in uncontrolled mode, because `cell` returns a ReactNode and there is no
   * honest way to order rendered JSX — a status chip sorts by its status, not by the markup around it, and a
   * date cell must sort chronologically rather than by the string "26 Jul 2026". Strings compare with
   * `localeCompare` so Arabic labels order correctly rather than by code point.
   */
  sortValue?: (row: Row) => string | number | null | undefined;
  /**
   * Pin this column to the trailing edge while the rest of the table scroll under it.
   *
   * For the ACTIONS column, and effectively only for it. A wide worklist overflows its card — that is what
   * `.mrs-wl-scroll` is for — but the column that ends up past the fold is the last one, which is where the
   * buttons are. An operator then has to scroll sideways to reach the control they came for, on every row.
   * Pinning it means the columns that can be read at a glance are the ones that scroll.
   */
  stickyEnd?: boolean;
  /**
   * This column holds a number — money, a quantity, a count, a percentage.
   *
   * <p>Aligns the cell AND its header to the end and sets tabular figures, so the digits stack. A money
   * column is read by scanning DOWN it, and that scan only works when `9.50` and `12,400.00` end in the same
   * place; left-aligned they start together and finish apart, so the eye has to measure each string instead
   * of reading a shape.</p>
   *
   * <p>A flag rather than a job for each `cell` renderer, because that is how it went wrong the first time:
   * thirteen of the app's fifty-six money renders wrapped their value in `.tnum`, which sets the figure width
   * and nothing else, on a `<span>` inside a cell still aligned to the start. The fix was applied and the
   * column stayed ragged. Alignment belongs to the cell, and the cell belongs to the table.</p>
   *
   * <p>The HEADER moves with it. A right-aligned column under a left-aligned heading reads as a mistake, and
   * on a narrow column the heading ends up nowhere near the figures it names.</p>
   *
   * <p><b>Not simply "contains digits".</b> A case number, an MRN, an order reference and a date are all made
   * of numerals and none of them belongs here: they are read left-to-right like words, and pushing them to
   * the right edge would break the alignment of the column they sit beside. Those columns want `.tnum` on the
   * value — equal-width figures so a list of IDs scans cleanly — and nothing else. `numeric` is for a
   * quantity you would COMPARE down the column.</p>
   */
  numeric?: boolean;
  /**
   * This column NAMES the row — render it as `<th scope="row">` rather than `<td>`.
   *
   * <p>For the one column that says which thing each row is: the person's name, the benefit category code,
   * the item. A screen reader reading across a wide row announces the row header with every cell, so the
   * listener hears "Nour Ali, plan, Standard" instead of six unattributed values; without it the only way to
   * know whose plan is being read is to remember which row you arrowed into.</p>
   *
   * <p>At most one column should set it — two row headers name the row twice and the announcement gets
   * longer rather than clearer.</p>
   */
  rowHeader?: boolean;
}

export type SortDir = "ascending" | "descending" | "none";

export interface DataTableProps<Row> {
  columns: Column<Row>[];
  rows: Row[];
  rowKey: (row: Row) => string;
  /** Caption for screen readers (required). */
  caption: string;
  selectedKey?: string | null;
  onSelect?: (row: Row) => void;
  /** Roving-tabindex keyboard nav across rows when true (worklist mode). */
  interactive?: boolean;
  density?: "comfortable" | "compact";
  /**
   * CONTROLLED sort. Supply all three and the caller owns ordering — needed when the server sorts, or when
   * sort state is shared with something outside the table.
   *
   * Omit `onSort` and the table sorts ITSELF from `column.sortValue`. That default exists because every
   * caller was otherwise reimplementing the same comparator, useState and click handler, and "sortable
   * columns" as a house standard cannot rest on each screen remembering to do it. See `sortValue`.
   */
  sortKey?: string;
  sortDir?: SortDir;
  onSort?: (key: string) => void;
  loading?: boolean;
  error?: string;
  /** Overrides the default localized "No results". */
  emptyLabel?: string;
  /**
   * MULTI-select, for tables where one decision is taken over many rows at once. Distinct from
   * `selectedKey`/`onSelect`, which mark the ONE row a detail pane is showing — a worklist can have both, and
   * conflating them would make opening a row for review silently enlist it in the next bulk action.
   */
  selection?: RowSelection<Row>;
}

/** Multi-select state for {@link DataTableProps.selection}. */
export interface RowSelection<Row> {
  /** The selected row keys. Owned by the caller, because a bulk action needs them outside the table. */
  keys: ReadonlySet<string>;
  onChange: (keys: Set<string>) => void;
  /**
   * Rows this action cannot apply to. Their checkbox renders DISABLED rather than absent: a missing control
   * reads as a rendering fault, whereas a disabled one says "not this row" — and the header count stays
   * honest because select-all only ever takes the selectable rows.
   */
  isSelectable?: (row: Row) => boolean;
  /** Accessible name for a row's checkbox, e.g. `(row) => "Select " + row.name`. */
  rowLabel: (row: Row) => string;
  /** Accessible name for the select-all checkbox in the header. */
  allLabel: string;
}

/**
 * Data table / worklist — sticky micro-label header, tabular numerals, sortable (aria-sort), rows as
 * focusable buttons with roving tabindex + arrow-key nav, selected row = 4px accent left-bar + tint +
 * aria-selected. Explicit loading / empty / error states. Density toggle (comfortable/compact). 0B §6.
 */
export function DataTable<Row>({
  columns,
  rows,
  rowKey,
  caption,
  selectedKey,
  onSelect,
  interactive = false,
  density = "comfortable",
  sortKey,
  sortDir = "none",
  onSort,
  loading = false,
  error,
  emptyLabel,
  selection,
}: DataTableProps<Row>) {
  const rowRefs = useRef<Array<HTMLTableRowElement | null>>([]);
  const selectAllRef = useRef<HTMLInputElement | null>(null);

  // ---- sort: controlled when the caller supplies onSort, otherwise the table's own ------------------------
  const [ownSort, setOwnSort] = useState<{ key: string; dir: Exclude<SortDir, "none"> } | null>(null);
  const controlled = onSort !== undefined;

  const activeKey = controlled ? sortKey : ownSort?.key;
  const activeDir: SortDir = controlled ? sortDir : (ownSort?.dir ?? "none");

  const handleSort = (key: string) => {
    if (controlled) { onSort(key); return; }
    // Toggle asc → desc → asc on the same column; a fresh column always starts ascending, because landing on
    // a descending sort you did not ask for reads as the table having reordered itself.
    setOwnSort((prev) =>
      prev?.key === key
        ? { key, dir: prev.dir === "ascending" ? "descending" : "ascending" }
        : { key, dir: "ascending" });
  };

  const sortedRows = useMemo(() => {
    if (controlled || !ownSort) return rows;
    const col = columns.find((c) => c.key === ownSort.key);
    if (!col?.sortValue) return rows;
    // The comparator is shared with `useTableQuery` (lib/sortRows) so that turning on pagination cannot
    // change the order — see the note there.
    return sortRows(rows, col.sortValue, ownSort.dir);
  }, [controlled, ownSort, rows, columns]);

  // 18.D3 (U6) — the DS shipped hardcoded English "Loading…" / "No results". An Arabic user saw English
  // inside an otherwise Arabic table, and the strings were unreachable from the app's own i18n because they
  // live in the component. They follow the app language now; a caller may still override emptyLabel with
  // something specific ("No authorizations awaiting your review").
  const { lang } = useTheme();
  const ar = lang === "ar";
  const loadingText = ar ? "جارٍ التحميل…" : "Loading…";
  const emptyText = emptyLabel ?? (ar ? "لا توجد نتائج" : "No results");

  function onRowKeyDown(e: React.KeyboardEvent, index: number, row: Row) {
    // A control inside the row owns its own keys. The row handler calls preventDefault on Space, so without
    // this the select checkbox in a bulk-decision worklist could not be ticked from the keyboard at all — the
    // keypress opened the row for review instead, which is the opposite of what was pressed.
    if (e.target !== e.currentTarget) return;
    if (e.key === "Enter" || e.key === " ") {
      e.preventDefault();
      onSelect?.(row);
    } else if (e.key === "ArrowDown") {
      e.preventDefault();
      rowRefs.current[(index + 1) % sortedRows.length]?.focus();
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      rowRefs.current[(index - 1 + sortedRows.length) % sortedRows.length]?.focus();
    }
  }

  // ---- multi-select ---------------------------------------------------------------------------------------
  //
  // Select-all covers THE ROWS ON SCREEN, not every row that exists. With a pager, "all" is ambiguous and the
  // dangerous reading is the invisible one: a supervisor ticking the header box and approving would decide 210
  // applications having seen 25. Scoping it to the page makes what was selected exactly what was displayed.
  const selectableRows = selection ? sortedRows.filter((r) => selection.isSelectable?.(r) ?? true) : [];
  const selectedOnPage = selection
    ? selectableRows.filter((r) => selection.keys.has(rowKey(r))).length
    : 0;
  const allOnPageSelected = selectableRows.length > 0 && selectedOnPage === selectableRows.length;
  // `indeterminate` is a DOM property with no HTML attribute, so React cannot set it declaratively. Without
  // it a partial selection renders as an empty box, which says "nothing is selected" while rows are.
  if (selectAllRef.current) {
    selectAllRef.current.indeterminate = selectedOnPage > 0 && !allOnPageSelected;
  }

  const toggleRow = (row: Row) => {
    if (!selection) return;
    const next = new Set(selection.keys);
    const key = rowKey(row);
    if (next.has(key)) next.delete(key);
    else next.add(key);
    selection.onChange(next);
  };

  const toggleAllOnPage = () => {
    if (!selection) return;
    const next = new Set(selection.keys);
    // Clearing removes only THIS page's keys, so a selection made on page 1 survives a look at page 2 —
    // otherwise paging silently discards work the operator has already done.
    for (const r of selectableRows) {
      if (allOnPageSelected) next.delete(rowKey(r));
      else next.add(rowKey(r));
    }
    selection.onChange(next);
  };

  const colCount = columns.length + (selection ? 1 : 0);

  return (
    /*
      FOCUSABLE, because it scrolls. A pane that overflows horizontally can be dragged with a mouse and,
      without a tab stop, reached by nobody else: the columns past the fold are simply unavailable to a
      keyboard-only user. That is WCAG 2.1.1, and axe has a rule for it (scrollable-region-focusable) which
      our suite cannot fire — the a11y tests run in jsdom, which has no layout engine and therefore never
      computes an overflow. So four locale x theme axe sweeps pass over tables no keyboard can scroll.

      Always focusable rather than only-when-overflowing: whether a table overflows depends on the viewport
      and the data, so it changes after render and on resize. Measuring it would mean a ResizeObserver per
      table to decide a tab stop, and the failure mode of guessing wrong is unreachable clinical data. The
      cost of the other choice is one extra tab stop on tables that happen to fit, which is the same trade
      GOV.UK and Bootstrap make for responsive tables.

      `group`, not `region`: a dozen section tables on the patient profile would otherwise add a dozen
      landmarks to the page and drown the ones that mean something.
    */
    <div className="mrs-wl-scroll mrs-scroll mrs-scroll-focusable" tabIndex={0} role="group" aria-label={caption}>
      {/*
        18.D3 (U6) — an interactive worklist is a GRID, not a table.
        `aria-selected` on a <tr> inside an implicit role="table" is invalid ARIA: the attribute is simply
        ignored, so a screen-reader user navigating a worklist is never told which row is current. role="grid"
        (with gridcell children) is the role that supports selection and two-dimensional arrow navigation —
        which this component already implements in onRowKeyDown. A non-interactive table stays a plain table,
        because a grid role on static data adds navigation semantics that are not there.
      */}
      <table
        className={cx("mrs-wl", density === "compact" && "mrs-compact")}
        role={interactive ? "grid" : undefined}
      >
      <caption className="sr-only">{caption}</caption>
      <thead>
        <tr>
          {selection && (
            <th scope="col" className="mrs-selcell">
              <input
                ref={selectAllRef}
                type="checkbox"
                className="mrs-checkbox"
                checked={allOnPageSelected}
                disabled={selectableRows.length === 0}
                onChange={toggleAllOnPage}
                aria-label={selection.allLabel}
              />
            </th>
          )}
          {columns.map((c) => {
            const isSorted = activeKey === c.key;
            return (
              <th
                key={c.key}
                aria-sort={c.sortable ? (isSorted ? activeDir : "none") : undefined}
                scope="col"
                className={cx(c.stickyEnd && "mrs-stickyend", c.numeric && "mrs-num")}
              >
                {/* Sortable now needs only `sortable` — not `sortable && onSort`. Requiring a handler meant a
                    column marked sortable rendered as inert text whenever the caller had not wired one, so
                    the header said "you can sort by this" and nothing happened. */}
                {c.sortable ? (
                  <button type="button" className="mrs-sort" onClick={() => handleSort(c.key)}>
                    {c.header}
                    <Icon name="chevron" width={12} height={12} />
                  </button>
                ) : (
                  c.header
                )}
              </th>
            );
          })}
        </tr>
      </thead>
      <tbody>
        {loading && (
          <tr>
            <td colSpan={colCount} className="mrs-tablestate" aria-live="polite">
              {loadingText}
            </td>
          </tr>
        )}
        {!loading && error && (
          <tr>
            <td colSpan={colCount} className="mrs-tablestate" role="alert" style={{ color: "var(--st-bad-fg)" }}>
              {error}
            </td>
          </tr>
        )}
        {!loading && !error && sortedRows.length === 0 && (
          <tr>
            <td colSpan={colCount} className="mrs-tablestate">
              {emptyText}
            </td>
          </tr>
        )}
        {!loading &&
          !error &&
          sortedRows.map((row, i) => {
            const key = rowKey(row);
            const selected = key === selectedKey;
            return (
              <tr
                key={key}
                ref={(el) => {
                  rowRefs.current[i] = el;
                }}
                className={cx(interactive && "mrs-row")}
                tabIndex={interactive ? (selected || (!selectedKey && i === 0) ? 0 : -1) : undefined}
                aria-selected={interactive ? selected : undefined}
                onClick={interactive ? () => onSelect?.(row) : undefined}
                onKeyDown={interactive ? (e) => onRowKeyDown(e, i, row) : undefined}
              >
                {selection && (
                  <td className="mrs-selcell" role={interactive ? "gridcell" : undefined}>
                    <input
                      type="checkbox"
                      className="mrs-checkbox"
                      checked={selection.keys.has(key)}
                      disabled={!(selection.isSelectable?.(row) ?? true)}
                      onChange={() => toggleRow(row)}
                      // An interactive worklist opens a row on click. Without this, ticking the box for a bulk
                      // decision ALSO swaps the review pane to that row — two different intentions on one
                      // gesture, and the one that happens is the one the operator did not ask for.
                      onClick={(e) => e.stopPropagation()}
                      aria-label={selection.rowLabel(row)}
                    />
                  </td>
                )}
                {columns.map((c) => {
                  // `rowHeader` swaps the element, not the styling: a row header is still a cell to look at
                  // and only a different thing to LISTEN to.
                  const Cell = c.rowHeader ? "th" : "td";
                  return (
                    <Cell
                      key={c.key}
                      scope={c.rowHeader ? "row" : undefined}
                      role={interactive ? "gridcell" : undefined}
                      className={cx(c.stickyEnd && "mrs-stickyend", c.numeric && "mrs-num")}
                    >
                      {c.cell(row)}
                    </Cell>
                  );
                })}
              </tr>
            );
          })}
      </tbody>
      </table>
    </div>
  );
}

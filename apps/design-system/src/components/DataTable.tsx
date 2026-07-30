import { useMemo, useRef, useState, type ReactNode } from "react";
import { Icon } from "./Icon";
import { cx } from "../lib/cx";
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
}: DataTableProps<Row>) {
  const rowRefs = useRef<Array<HTMLTableRowElement | null>>([]);

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
    const pick = col.sortValue;
    const sign = ownSort.dir === "ascending" ? 1 : -1;
    // Copied before sorting: `rows` belongs to the caller and mutating it in place would reorder their state.
    return [...rows].sort((a, b) => {
      const x = pick(a);
      const y = pick(b);
      // Absent values sink to the bottom in BOTH directions. Reversing them with the sort would put "no
      // value" at the top of a descending list, which reads as data rather than as its absence.
      if (x === null || x === undefined) return y === null || y === undefined ? 0 : 1;
      if (y === null || y === undefined) return -1;
      if (typeof x === "number" && typeof y === "number") return (x - y) * sign;
      return String(x).localeCompare(String(y)) * sign;
    });
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

  const colCount = columns.length;

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
    <div className="mrs-wl-scroll" tabIndex={0} role="group" aria-label={caption}>
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
          {columns.map((c) => {
            const isSorted = activeKey === c.key;
            return (
              <th key={c.key} aria-sort={c.sortable ? (isSorted ? activeDir : "none") : undefined} scope="col">
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
                {columns.map((c) => (
                  <td key={c.key} role={interactive ? "gridcell" : undefined}>{c.cell(row)}</td>
                ))}
              </tr>
            );
          })}
      </tbody>
      </table>
    </div>
  );
}

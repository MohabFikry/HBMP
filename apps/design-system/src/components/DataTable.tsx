import { useRef, type ReactNode } from "react";
import { Icon } from "./Icon";
import { cx } from "../lib/cx";
import { useTheme } from "../theme/ThemeProvider";

export interface Column<Row> {
  key: string;
  header: string;
  /** Cell renderer. */
  cell: (row: Row) => ReactNode;
  sortable?: boolean;
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
      rowRefs.current[(index + 1) % rows.length]?.focus();
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      rowRefs.current[(index - 1 + rows.length) % rows.length]?.focus();
    }
  }

  const colCount = columns.length;

  return (
    <div className="mrs-wl-scroll">
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
            const isSorted = sortKey === c.key;
            return (
              <th key={c.key} aria-sort={c.sortable ? (isSorted ? sortDir : "none") : undefined} scope="col">
                {c.sortable && onSort ? (
                  <button type="button" className="mrs-sort" onClick={() => onSort(c.key)}>
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
        {!loading && !error && rows.length === 0 && (
          <tr>
            <td colSpan={colCount} className="mrs-tablestate">
              {emptyText}
            </td>
          </tr>
        )}
        {!loading &&
          !error &&
          rows.map((row, i) => {
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

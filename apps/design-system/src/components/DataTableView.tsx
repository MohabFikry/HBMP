import type { ReactNode } from "react";
import { DataTable, type Column, type RowSelection } from "./DataTable";
import { Pagination } from "./Pagination";
import { TableToolbar, type FilterGroup } from "./TableToolbar";
import { cx } from "../lib/cx";
import { useTheme } from "../theme/ThemeProvider";
import type { TableQuery } from "../lib/useTableQuery";

export interface DataTableViewProps<Row> {
  /** The state from `useTableQuery` — it owns search, filters, sort and paging. */
  query: TableQuery<Row>;
  columns: ReadonlyArray<Column<Row>>;
  rowKey: (row: Row) => string;
  /** Caption for screen readers (required, as on `DataTable`). */
  caption: string;
  /** Extra toolbar controls — an export button, a bulk-action bar. Rendered at the end of the bar. */
  toolbarExtra?: ReactNode;
  /**
   * Filter groups the CALLER owns because they narrow on the server, rendered before the query's own.
   *
   * <p>`useTableQuery` is a client-side engine: its `match` runs over rows already in hand, and its faceted
   * counts mean "if you picked this instead, you would get N". A filter that changes what the SERVER returns
   * satisfies neither — its `match` would have to return true for every row, and every option would then
   * report the whole set as its count. Putting one in there does not narrow anything; it just lies about the
   * numbers.</p>
   *
   * <p>The appointments boards are the case: their date range is a query parameter, so choosing "Custom"
   * refetches. Their status chips and their search are ordinary client-side narrowing and belong to the
   * query. The two kinds sit in one bar because an operator does not care which side of the wire a control
   * acts on — but they are wired differently, and pretending otherwise is what makes the counts wrong.</p>
   *
   * <p>These carry no counts for the same reason: the component cannot compute a count for rows it has not
   * been given. Omit `count` on their options.</p>
   */
  serverFilters?: FilterGroup[];
  /** Shown when the table is empty and NOTHING is filtering it. */
  emptyLabel?: string;
  /** Shown when search or a filter has excluded everything. Falls back to a localized default. */
  noMatchesLabel?: string;
  selection?: RowSelection<Row>;
  interactive?: boolean;
  selectedKey?: string | null;
  onSelect?: (row: Row) => void;
  density?: "comfortable" | "compact";
  loading?: boolean;
  error?: string;
  className?: string;
}

/**
 * THE portal table: toolbar, table, pager — in that order, wired to one query object.
 *
 * ============================================================================================================
 * WHY THIS EXISTS RATHER THAN THREE COMPONENTS EACH SCREEN ASSEMBLES
 * ============================================================================================================
 * `TableToolbar` already argues this case for filters, and the argument generalises: a house standard that
 * lives in a document is one every screen implements slightly differently. Assembled by hand, one screen puts
 * the pager above the table, another sorts the page instead of the result (see `useTableQuery` on why that
 * silently produces the wrong order), a third forgets that narrowing a filter has to reset the page and leaves
 * the operator staring at an empty page 4. Shipping the assembly makes the correct version the easy one.
 *
 * ============================================================================================================
 * "NO RESULTS" AND "NOTHING HERE" ARE DIFFERENT SCREENS
 * ============================================================================================================
 * An empty queue is good news and needs no action. An empty queue *because you typed something* needs the
 * search cleared, and telling that operator "No registrations waiting for review" is a lie that sends them
 * looking for a bug. So the empty state follows `query.narrowed`.
 *
 * The pager is hidden when a single page holds everything: a control that can only be pressed to no effect is
 * noise, and on a five-row table it is most of the chrome.
 */
export function DataTableView<Row>({
  query,
  columns,
  rowKey,
  caption,
  toolbarExtra,
  serverFilters,
  emptyLabel,
  noMatchesLabel,
  selection,
  interactive,
  selectedKey,
  onSelect,
  density,
  loading,
  error,
  className,
}: DataTableViewProps<Row>) {
  const { lang } = useTheme();
  const ar = lang === "ar";
  const noMatches = noMatchesLabel ?? (ar
    ? "لا توجد صفوف مطابقة. عدّل البحث أو أزل عوامل التصفية."
    : "No rows match. Change the search or clear the filters.");

  const groups = serverFilters ? [...serverFilters, ...query.toolbarProps.filters] : query.toolbarProps.filters;
  const hasToolbar = Boolean(query.toolbarProps.search) || groups.length > 0 || Boolean(toolbarExtra);

  return (
    <div className={cx("mrs-tableview", className)}>
      {hasToolbar && (
        <TableToolbar search={query.toolbarProps.search} filters={groups}>
          {toolbarExtra}
        </TableToolbar>
      )}

      <DataTable
        columns={columns as Column<Row>[]}
        rows={query.rows}
        rowKey={rowKey}
        caption={caption}
        selection={selection}
        interactive={interactive}
        selectedKey={selectedKey}
        onSelect={onSelect}
        density={density}
        loading={loading}
        error={error}
        emptyLabel={query.narrowed ? noMatches : emptyLabel}
        // CONTROLLED sort. The query sorts the whole result before paging it; letting the table sort as well
        // would order the 25 rows it can see and leave the row that should be first on page 4.
        sortKey={query.sortKey}
        sortDir={query.sortDir}
        onSort={query.onSort}
      />

      {query.total > query.pageSize && <Pagination {...query.pagerProps} />}
    </div>
  );
}

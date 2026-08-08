import type { ReactNode } from "react";
import { DataTable, type Column, type RowSelection } from "./DataTable";
import { Pagination } from "./Pagination";
import { TableToolbar } from "./TableToolbar";
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

  const hasToolbar = Boolean(query.toolbarProps.search) || query.toolbarProps.filters.length > 0 || Boolean(toolbarExtra);

  return (
    <div className={cx("mrs-tableview", className)}>
      {hasToolbar && (
        <TableToolbar search={query.toolbarProps.search} filters={query.toolbarProps.filters}>
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

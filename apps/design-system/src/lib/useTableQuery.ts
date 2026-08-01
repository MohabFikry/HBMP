import { useEffect, useMemo, useState } from "react";
import type { Column, SortDir } from "../components/DataTable";
import type { FilterGroup } from "../components/TableToolbar";
import { sortRows, type SortValue } from "./sortRows";

/**
 * One filter group, described once.
 *
 * `match` rather than a field name, because the interesting filters are not equality on a column: "unassigned"
 * is an absent value, "overdue" is a comparison against the clock, and "needs both checks" is two booleans. A
 * field name would cover the easy third of the cases and push the rest back into every screen.
 */
export interface TableFilterSpec<Row> {
  key: string;
  label: string;
  options: ReadonlyArray<{ value: string; label: string }>;
  match: (row: Row, value: string) => boolean;
  /** Initial selection. `null` (the default) means the group starts unfiltered. */
  initial?: string | null;
  /** Suppress the per-option counts. Default is to show them. */
  hideCounts?: boolean;
}

export interface UseTableQueryOptions<Row> {
  rows: readonly Row[];
  /** The columns, so the hook can sort by `column.sortValue` exactly as the table would. */
  columns: ReadonlyArray<Column<Row>>;
  /**
   * The haystack free-text search runs against. Return every field an operator would plausibly type — a
   * search that only matches the name is a search that fails on the one identifier they have in hand.
   */
  searchText?: (row: Row) => string;
  searchLabel?: string;
  searchPlaceholder?: string;
  filters?: ReadonlyArray<TableFilterSpec<Row>>;
  pageSize?: number;
  /** Column key to sort by initially. */
  initialSortKey?: string;
  initialSortDir?: Exclude<SortDir, "none">;
}

export interface TableQuery<Row> {
  /** The rows on the current page — what goes into `DataTable`. */
  rows: Row[];
  /** Every row passing search + filters, across all pages. What a "select all matching" would act on. */
  matched: Row[];
  /** `matched.length`, for the pager. */
  total: number;
  /** True when filters or search are narrowing the set — lets a screen distinguish "empty" from "no matches". */
  narrowed: boolean;
  search: string;
  setSearch: (value: string) => void;
  filterValues: Readonly<Record<string, string | null>>;
  setFilter: (key: string, value: string | null) => void;
  clear: () => void;
  page: number;
  setPage: (page: number) => void;
  pageSize: number;
  setPageSize: (size: number) => void;
  sortKey: string | undefined;
  sortDir: SortDir;
  onSort: (key: string) => void;
  /** Spread straight into `TableToolbar`. */
  toolbarProps: { search?: { label: string; value: string; onChange: (v: string) => void; placeholder?: string }; filters: FilterGroup[] };
  /** Spread straight into `Pagination`. */
  pagerProps: { page: number; pageSize: number; total: number; onPageChange: (p: number) => void; onPageSizeChange: (n: number) => void };
}

/**
 * Search + filter + sort + paginate, in that order, over a list the caller already holds.
 *
 * ============================================================================================================
 * WHY THE ORDER IS LOAD-BEARING
 * ============================================================================================================
 * Sorting must happen BEFORE paging. A table that sorts its own rows (which `DataTable` does by default) sorts
 * the page it was handed — so with paging on, "sort by oldest" reorders 25 rows within page 1 and the actual
 * oldest application, sitting on page 4, never moves. It looks like it worked. That is why this hook owns the
 * sort and drives `DataTable` in controlled mode.
 *
 * ============================================================================================================
 * WHY CLIENT-SIDE
 * ============================================================================================================
 * These are queues an operator works through — hundreds of rows, not millions — and the server already pages
 * them. Filtering in the browser means a filter responds instantly and cannot disagree with what is on screen.
 * A dataset that outgrows this wants a server-side query, and the shape here (a query object with a total)
 * is deliberately the shape a server-backed version would also return.
 *
 * ============================================================================================================
 * WHY THE COUNTS ARE FACETED
 * ============================================================================================================
 * Each group's counts are computed over the rows passing SEARCH AND EVERY OTHER GROUP, but not itself. So the
 * numbers say "if you picked this instead, you would get N" — which is what a count beside an option is read
 * as. Counting the whole set would show options that lead to an empty table; counting the fully filtered set
 * would show every unselected option as zero.
 */
export function useTableQuery<Row>({
  rows,
  columns,
  searchText,
  searchLabel,
  searchPlaceholder,
  filters = [],
  pageSize: initialPageSize = 25,
  initialSortKey,
  initialSortDir = "ascending",
}: UseTableQueryOptions<Row>): TableQuery<Row> {
  const [search, setSearchRaw] = useState("");
  const [filterValues, setFilterValues] = useState<Record<string, string | null>>(() =>
    Object.fromEntries(filters.map((f) => [f.key, f.initial ?? null])));
  const [page, setPage] = useState(1);
  const [pageSize, setPageSizeRaw] = useState(initialPageSize);
  const [sort, setSort] = useState<{ key: string; dir: Exclude<SortDir, "none"> } | null>(
    initialSortKey ? { key: initialSortKey, dir: initialSortDir } : null);

  // Narrowing the set moves the operator to the front of it. Staying on page 4 of a result that now has one
  // page renders an empty table under a pager insisting there are matches.
  const setSearch = (value: string) => { setSearchRaw(value); setPage(1); };
  const setFilter = (key: string, value: string | null) => {
    setFilterValues((prev) => ({ ...prev, [key]: value }));
    setPage(1);
  };
  const setPageSize = (size: number) => { setPageSizeRaw(size); setPage(1); };
  const clear = () => {
    setSearchRaw("");
    setFilterValues(Object.fromEntries(filters.map((f) => [f.key, null])));
    setPage(1);
  };

  const needle = search.trim().toLowerCase();
  const bySearch = useMemo(
    () => (needle === "" || !searchText ? [...rows] : rows.filter((r) => searchText(r).toLowerCase().includes(needle))),
    [rows, needle, searchText]);

  // Rows passing every group EXCEPT `skip` — the faceting base, and with `skip` undefined, the final set.
  const passing = useMemo(() => {
    const apply = (skip?: string) =>
      bySearch.filter((row) =>
        filters.every((f) => {
          if (f.key === skip) return true;
          const value = filterValues[f.key];
          return value === null || value === undefined || f.match(row, value);
        }));
    return { all: apply(), without: (key: string) => apply(key) };
    // `filters` is rebuilt every render by callers that inline it, so the identity of the ARRAY is not a
    // useful dependency; what matters is the values selected and the rows underneath.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bySearch, filterValues, filters.length]);

  const matched = useMemo(() => {
    if (!sort) return passing.all;
    const col = columns.find((c) => c.key === sort.key);
    if (!col?.sortValue) return passing.all;
    const pick: (row: Row) => SortValue = col.sortValue;
    return sortRows(passing.all, pick, sort.dir);
  }, [passing, sort, columns]);

  // A reload that shrinks the queue (the last two applications were decided) can leave the pager past the end.
  const pageCount = Math.max(1, Math.ceil(matched.length / pageSize));
  useEffect(() => {
    if (page > pageCount) setPage(pageCount);
  }, [page, pageCount]);

  const current = Math.min(page, pageCount);
  const paged = useMemo(
    () => matched.slice((current - 1) * pageSize, current * pageSize),
    [matched, current, pageSize]);

  const toolbarFilters: FilterGroup[] = filters.map((f) => {
    const base = passing.without(f.key);
    return {
      key: f.key,
      label: f.label,
      value: filterValues[f.key] ?? null,
      onChange: (value) => setFilter(f.key, value),
      options: f.options.map((o) => ({
        value: o.value,
        label: o.label,
        count: f.hideCounts ? undefined : base.filter((r) => f.match(r, o.value)).length,
      })),
    };
  });

  return {
    rows: paged,
    matched,
    total: matched.length,
    narrowed: needle !== "" || Object.values(filterValues).some((v) => v !== null && v !== undefined),
    search,
    setSearch,
    filterValues,
    setFilter,
    clear,
    page: current,
    setPage,
    pageSize,
    setPageSize,
    sortKey: sort?.key,
    sortDir: sort ? sort.dir : "none",
    onSort: (key) =>
      // Toggle asc → desc on the same column; a fresh column always starts ascending, because landing on a
      // descending sort you did not ask for reads as the table having reordered itself.
      setSort((prev) => (prev?.key === key
        ? { key, dir: prev.dir === "ascending" ? "descending" : "ascending" }
        : { key, dir: "ascending" })),
    toolbarProps: {
      search: searchText ? { label: searchLabel ?? "Search", value: search, onChange: setSearch, placeholder: searchPlaceholder } : undefined,
      filters: toolbarFilters,
    },
    pagerProps: { page: current, pageSize, total: matched.length, onPageChange: setPage, onPageSizeChange: setPageSize },
  };
}

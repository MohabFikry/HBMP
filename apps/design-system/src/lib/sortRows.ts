/**
 * The one row comparator.
 *
 * It lives here rather than inside `DataTable` because two things now sort: the table's own uncontrolled
 * sort, and `useTableQuery`, which MUST sort before it paginates. Two comparators would eventually disagree,
 * and the way that surfaces is a table whose order changes when you turn on paging — which reads as data
 * corruption, not as a sorting difference.
 */

/** What a column sorts BY. `null`/`undefined` mean "this row has no value for this column". */
export type SortValue = string | number | null | undefined;

export type SortDirection = "ascending" | "descending";

/**
 * Compare two sort values.
 *
 * Absent values sink to the BOTTOM in both directions. Reversing them along with everything else would put
 * "no value" at the top of a descending list, where it reads as data rather than as its absence — the em-dash
 * rows would lead the queue.
 *
 * Strings compare with `localeCompare` so Arabic labels order by the language's own collation rather than by
 * UTF-16 code point, which interleaves them arbitrarily.
 */
export function compareSortValues(x: SortValue, y: SortValue, dir: SortDirection): number {
  if (x === null || x === undefined) return y === null || y === undefined ? 0 : 1;
  if (y === null || y === undefined) return -1;
  const sign = dir === "ascending" ? 1 : -1;
  if (typeof x === "number" && typeof y === "number") return (x - y) * sign;
  return String(x).localeCompare(String(y)) * sign;
}

/** Sort a COPY of `rows`. The array belongs to the caller; sorting in place would reorder their state. */
export function sortRows<Row>(rows: readonly Row[], pick: (row: Row) => SortValue, dir: SortDirection): Row[] {
  return [...rows].sort((a, b) => compareSortValues(pick(a), pick(b), dir));
}

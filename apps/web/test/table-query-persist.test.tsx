import { afterEach, describe, expect, it } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { useTableQuery } from "@mersal/design-system";

interface Row { id: string; name: string; status: string }

const ROWS: Row[] = [
  { id: "1", name: "Fatma Ibrahim", status: "Booked" },
  { id: "2", name: "Khaled Mostafa", status: "Checked in" },
  { id: "3", name: "Layla Haddad", status: "Booked" },
];

const COLUMNS = [
  { key: "name", header: "Name", cell: (r: Row) => r.name, sortValue: (r: Row) => r.name },
  { key: "status", header: "Status", cell: (r: Row) => r.status, sortValue: (r: Row) => r.status },
];

const FILTERS = [{
  key: "status",
  label: "Status",
  options: [{ value: "Booked", label: "Booked" }, { value: "Checked in", label: "Checked in" }],
  match: (r: Row, v: string) => r.status === v,
}];

function query(persistKey?: string) {
  return renderHook(() => useTableQuery<Row>({
    rows: ROWS, columns: COLUMNS, filters: FILTERS,
    searchText: (r) => `${r.name} ${r.status}`,
    pageSize: 2, persistKey,
  }));
}

afterEach(() => sessionStorage.clear());

/**
 * "Open the patient file and come back to the same place."
 *
 * Every route in this app unmounts on navigation, so a worklist re-mounts with an empty query. An operator who
 * had searched a name, filtered to one status and paged to 2 came back to an unfiltered page 1 with the row
 * they were working on somewhere off screen — and the more precisely they had narrowed it, the more the reset
 * destroyed. Persisting belongs to the hook rather than to each screen so a table cannot be given search,
 * filters and paging while quietly forgetting all three.
 */
describe("Table query persistence", () => {
  it("restores search, filter and sort after the screen unmounts", () => {
    const first = query("worklist");
    act(() => {
      first.result.current.setSearch("a");
      first.result.current.setFilter("status", "Booked");
      first.result.current.onSort("name");
    });
    expect(first.result.current.search).toBe("a");
    first.unmount();

    // A fresh mount — the same thing a route change does.
    const second = query("worklist");
    expect(second.result.current.search).toBe("a");
    expect(second.result.current.filterValues.status).toBe("Booked");
    expect(second.result.current.sortKey).toBe("name");
  });

  it("restores the page the operator was on", () => {
    // Unfiltered, so there genuinely IS a page 2 to come back to — the hook clamps page to the page count,
    // which is why this is a separate case from the one above.
    const first = query("worklist");
    act(() => first.result.current.setPage(2));
    first.unmount();

    expect(query("worklist").result.current.page).toBe(2);
  });

  it("keeps nothing without a persistKey", () => {
    // Opt-in: a table that has never asked to be remembered must not start reading another's stored query,
    // and most tables genuinely want to open clean.
    const first = query();
    act(() => first.result.current.setSearch("khaled"));
    first.unmount();

    expect(query().result.current.search).toBe("");
    expect(sessionStorage.length).toBe(0);
  });

  it("keeps two tables' queries apart", () => {
    const a = query("visits");
    act(() => a.result.current.setSearch("fatma"));
    a.unmount();

    expect(query("patients").result.current.search).toBe("");
    expect(query("visits").result.current.search).toBe("fatma");
  });

  it("stores the shape of the query and never a row", () => {
    // A browser store on a machine operators share. It holds what was typed and what was picked; the rows
    // themselves are re-fetched through the same gate as the first time.
    const q = query("worklist");
    act(() => q.result.current.setSearch("fatma"));
    const raw = sessionStorage.getItem("mrs.table.worklist") ?? "";
    expect(raw).toContain("fatma");
    for (const r of ROWS) expect(raw).not.toContain(r.name === "Fatma Ibrahim" ? r.id + '","name' : r.name);
  });

  it("gives a filter group added since the query was stored its own default", () => {
    const first = query("worklist");
    act(() => first.result.current.setFilter("status", "Booked"));
    first.unmount();

    // A second group appears in a later release; the stored query knows nothing about it.
    const second = renderHook(() => useTableQuery<Row>({
      rows: ROWS, columns: COLUMNS, pageSize: 2, persistKey: "worklist",
      filters: [...FILTERS, { key: "urgency", label: "Urgency", initial: "High",
        options: [{ value: "High", label: "High" }], match: () => true }],
    }));
    expect(second.result.current.filterValues.status).toBe("Booked");
    expect(second.result.current.filterValues.urgency).toBe("High");
  });
});

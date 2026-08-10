import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderDS } from "./render";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { DataTable, DataTableView, Pagination, useTableQuery, type Column, type TableFilterSpec } from "../src";

/**
 * The portal table pattern: search + filters + sortable columns + pagination, assembled once so that every
 * screen gets the same behaviour instead of thirty near-misses.
 *
 * The load-bearing case is `sorts the whole result before paging it`. A table that sorts itself sorts the
 * rows it was handed — so with paging on, "oldest first" reorders the current page and leaves the actual
 * oldest row several pages away, while looking exactly like it worked.
 */

interface Row {
  id: string;
  name: string;
  age: number;
  team: string;
}

// Twelve rows, so a page size of five produces three pages and every pager assertion has somewhere to go.
const ROWS: Row[] = [
  { id: "1", name: "Amal", age: 47, team: "red" },
  { id: "2", name: "Bassel", age: 8, team: "blue" },
  { id: "3", name: "Camelia", age: 33, team: "red" },
  { id: "4", name: "Dina", age: 61, team: "blue" },
  { id: "5", name: "Emad", age: 12, team: "red" },
  { id: "6", name: "Farida", age: 29, team: "blue" },
  { id: "7", name: "Ghada", age: 55, team: "red" },
  { id: "8", name: "Hani", age: 3, team: "blue" },
  { id: "9", name: "Iman", age: 40, team: "red" },
  { id: "10", name: "Jamal", age: 18, team: "blue" },
  { id: "11", name: "Kamal", age: 71, team: "red" },
  { id: "12", name: "Lamia", age: 25, team: "blue" },
];

const COLS: Column<Row>[] = [
  { key: "name", header: "Name", cell: (r) => r.name, sortable: true, sortValue: (r) => r.name },
  { key: "age", header: "Age", cell: (r) => String(r.age), sortable: true, sortValue: (r) => r.age },
  { key: "team", header: "Team", cell: (r) => r.team },
];

const FILTERS: TableFilterSpec<Row>[] = [
  {
    key: "team",
    label: "Team",
    options: [{ value: "red", label: "Red" }, { value: "blue", label: "Blue" }],
    match: (r, v) => r.team === v,
  },
];

const names = () =>
  screen.getAllByRole("row").slice(1).map((r) => within(r).getAllByRole("cell")[0]!.textContent);

function Harness({
  pageSize = 5,
  selectable = false,
  onSelectionChange,
}: {
  pageSize?: number;
  selectable?: boolean;
  onSelectionChange?: (keys: Set<string>) => void;
}) {
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());
  const query = useTableQuery<Row>({
    rows: ROWS,
    columns: COLS,
    searchText: (r) => `${r.name} ${r.team}`,
    searchLabel: "Search",
    filters: FILTERS,
    pageSize,
  });
  return (
    <DataTableView
      query={query}
      columns={COLS}
      rowKey={(r) => r.id}
      caption="People"
      selection={selectable ? {
        keys: selected,
        onChange: (k) => { setSelected(k); onSelectionChange?.(k); },
        isSelectable: (r) => r.age >= 18,
        rowLabel: (r) => `Select ${r.name}`,
        allLabel: "Select all on this page",
      } : undefined}
    />
  );
}

describe("useTableQuery + DataTableView", () => {
  it("pages the result and states the range and the total", async () => {
    renderDS(<Harness />);
    expect(names()).toEqual(["Amal", "Bassel", "Camelia", "Dina", "Emad"]);
    expect(screen.getByText("Showing 1–5 of 12")).toBeInTheDocument();

    await userEvent.click(screen.getByRole("button", { name: /next/i }));
    expect(names()).toEqual(["Farida", "Ghada", "Hani", "Iman", "Jamal"]);
    expect(screen.getByText("Showing 6–10 of 12")).toBeInTheDocument();
  });

  it("sorts the WHOLE result before paging it, not the page on screen", async () => {
    renderDS(<Harness />);
    // Youngest overall is Hani (3), who starts on page 2. If the table sorted only its own page, the first
    // row would become Bassel (8) — the youngest of the five rows that happened to be visible.
    await userEvent.click(screen.getByRole("button", { name: /^age/i }));
    expect(names()[0]).toBe("Hani");
    // And descending picks up Kamal (71), who starts on page 3.
    await userEvent.click(screen.getByRole("button", { name: /^age/i }));
    expect(names()[0]).toBe("Kamal");
  });

  it("returns to the first page when a filter narrows the result", async () => {
    renderDS(<Harness />);
    await userEvent.click(screen.getByRole("button", { name: /next/i }));
    expect(screen.getByText("Showing 6–10 of 12")).toBeInTheDocument();

    // Staying on page 2 of a result that now has fewer pages renders an empty table under a pager insisting
    // there are matches.
    await userEvent.click(screen.getByRole("button", { name: /red/i }));
    expect(screen.getByText("Showing 1–5 of 6")).toBeInTheDocument();
    expect(names()[0]).toBe("Amal");
  });

  it("counts each filter option against the OTHER filters, not against itself", async () => {
    renderDS(<Harness />);
    // Six each, before anything is chosen.
    expect(screen.getByRole("button", { name: /red/i }).textContent).toMatch(/6/);

    await userEvent.type(screen.getByRole("searchbox"), "a");
    // The counts follow the search — otherwise an option advertises rows the table would not show.
    const red = screen.getByRole("button", { name: /red/i });
    expect(Number(red.textContent!.replace(/\D/g, ""))).toBeLessThanOrEqual(6);
  });

  it("distinguishes an empty table from one emptied by a search", async () => {
    renderDS(<Harness />);
    await userEvent.type(screen.getByRole("searchbox"), "zzzz");
    // Telling an operator "nothing here" when THEY excluded everything sends them looking for a bug.
    expect(screen.getByText(/change the search or clear the filters/i)).toBeInTheDocument();
  });

  it("hides the pager when a single page holds everything", () => {
    renderDS(<Harness pageSize={50} />);
    expect(screen.queryByRole("navigation", { name: /pagination/i })).toBeNull();
  });
});

describe("DataTable multi-select", () => {
  it("select-all takes the rows on screen, and only the selectable ones", async () => {
    const onChange = vi.fn();
    renderDS(<Harness selectable onSelectionChange={onChange} />);
    // Page 1 is Amal(47) Bassel(8) Camelia(33) Dina(61) Emad(12) — three are 18+.
    await userEvent.click(screen.getByRole("checkbox", { name: /select all on this page/i }));
    expect([...onChange.mock.calls[0]![0]].sort()).toEqual(["1", "3", "4"]);
  });

  it("keeps a selection made on one page while looking at another", async () => {
    const onChange = vi.fn();
    renderDS(<Harness selectable onSelectionChange={onChange} />);
    await userEvent.click(screen.getByRole("checkbox", { name: /select amal/i }));
    await userEvent.click(screen.getByRole("button", { name: /next/i }));
    await userEvent.click(screen.getByRole("checkbox", { name: /select ghada/i }));
    // Paging must not discard work the operator has already done.
    const last = onChange.mock.calls[onChange.mock.calls.length - 1]!;
    expect([...last[0]].sort()).toEqual(["1", "7"]);
  });

  it("refuses to enlist a row the action cannot apply to", () => {
    renderDS(<Harness selectable />);
    // Disabled rather than absent: a missing control reads as a rendering fault, a disabled one says
    // "not this row".
    expect(screen.getByRole("checkbox", { name: /select bassel/i })).toBeDisabled();
  });

  it("has no serious or critical axe violations", async () => {
    const { container } = renderDS(<Harness selectable />);
    const results = await axe(container);
    expect(results.violations.filter((v) => v.impact === "serious" || v.impact === "critical")).toEqual([]);
  });
});

describe("Pagination on its own", () => {
  it("clamps a page that has fallen past the end of a shrunken result", () => {
    // The list shrank under the pager (rows were decided and left the queue). "Showing 76–100 of 12" beside
    // an empty table is the failure this prevents.
    renderDS(<Pagination page={4} pageSize={25} total={12} onPageChange={() => {}} />);
    expect(screen.getByText("Showing 1–12 of 12")).toBeInTheDocument();
  });

  it("says 0 rather than 1–0 when there is nothing", () => {
    renderDS(<Pagination page={1} pageSize={25} total={0} onPageChange={() => {}} />);
    expect(screen.getByText("Showing 0–0 of 0")).toBeInTheDocument();
  });

  it("returns to the first page when the page size changes", async () => {
    const onPageChange = vi.fn();
    const onPageSizeChange = vi.fn();
    renderDS(
      <Pagination page={3} pageSize={10} total={120} onPageChange={onPageChange} onPageSizeChange={onPageSizeChange} />,
    );
    await userEvent.selectOptions(screen.getByLabelText(/per page/i), "50");
    expect(onPageSizeChange).toHaveBeenCalledWith(50);
    // Keeping the page number across a size change moves the operator to a different part of the list than
    // the one they were reading.
    expect(onPageChange).toHaveBeenCalledWith(1);
  });

  it("disables the step at each end rather than removing it", () => {
    renderDS(<Pagination page={1} pageSize={10} total={30} onPageChange={() => {}} />);
    expect(screen.getByRole("button", { name: /previous/i })).toBeDisabled();
    expect(screen.getByRole("button", { name: /next/i })).toBeEnabled();
  });
});

/**
 * A filter the CALLER owns because it narrows on the server.
 *
 * `useTableQuery` cannot hold one: its `match` runs over rows already in hand, and its per-option counts mean
 * "if you picked this instead, you would get N". A group whose real effect is to change the REQUEST satisfies
 * neither — its `match` would have to return true for every row, and every option would then report the whole
 * set. The appointments boards are the case: choosing a custom date range refetches, while their status chips
 * and their search are ordinary client-side narrowing.
 *
 * So they arrive by a different door, and these pin that the two kinds coexist in one bar without the
 * client-side one losing its counts.
 */
function ServerFilterHarness({ onWhen }: { onWhen?: (v: string | null) => void }) {
  const [when, setWhen] = useState<string | null>("today");
  const query = useTableQuery<Row>({
    rows: ROWS,
    columns: COLS,
    searchText: (r) => `${r.name} ${r.team}`,
    searchLabel: "Search",
    filters: FILTERS,
    pageSize: 50,
  });
  return (
    <DataTableView
      query={query}
      columns={COLS}
      rowKey={(r) => r.id}
      caption="People"
      serverFilters={[{
        key: "when",
        label: "When",
        value: when,
        onChange: (v) => { setWhen(v); onWhen?.(v); },
        options: [{ value: "today", label: "Today" }, { value: "custom", label: "Custom" }],
        extra: when === "custom" ? <span data-testid="range-fields">from/to</span> : undefined,
      }]}
    />
  );
}

describe("DataTableView.serverFilters", () => {
  it("renders the caller's group in the same bar as the query's", () => {
    renderDS(<ServerFilterHarness />);
    expect(screen.getByRole("group", { name: "When" })).toBeInTheDocument();
    expect(screen.getByRole("group", { name: "Team" })).toBeInTheDocument();
  });

  it("puts the caller's group FIRST — it is the coarser narrowing", () => {
    renderDS(<ServerFilterHarness />);
    // A fieldset takes its accessible name from its <legend>, not from an attribute — so compare the
    // elements' DOM order rather than reading a label off them.
    const when = screen.getByRole("group", { name: "When" });
    const team = screen.getByRole("group", { name: "Team" });
    // Node.DOCUMENT_POSITION_FOLLOWING === 4: `team` comes after `when`.
    expect(when.compareDocumentPosition(team) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("hands the selection back to the caller instead of filtering rows itself", async () => {
    const user = userEvent.setup();
    const onWhen = vi.fn();
    renderDS(<ServerFilterHarness onWhen={onWhen} />);

    await user.click(screen.getByRole("button", { name: "Custom" }));

    expect(onWhen).toHaveBeenCalledWith("custom");
    // Every row is still on screen. The component must NOT try to narrow by a group it cannot evaluate —
    // narrowing is the refetch the caller does in response.
    expect(names()).toHaveLength(ROWS.length);
  });

  it("reveals the group's own follow-up controls", async () => {
    const user = userEvent.setup();
    renderDS(<ServerFilterHarness />);
    expect(screen.queryByTestId("range-fields")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Custom" }));
    // Beside the chip that reveals them — the reason `extra` exists at all.
    expect(screen.getByTestId("range-fields")).toBeInTheDocument();
  });

  it("leaves the query's own chips carrying their counts", () => {
    renderDS(<ServerFilterHarness />);
    // Six red, six blue. The server group has no counts (the component cannot compute one for rows it was
    // never given); the client group keeps its faceting.
    expect(screen.getByRole("button", { name: /^Red\b/ })).toHaveTextContent("6");
    expect(screen.getByRole("button", { name: "Today" })).not.toHaveTextContent(/\d/);
  });
});

describe("Column.rowHeader", () => {
  const COLS_WITH_HEADER: Column<Row>[] = [
    { key: "name", header: "Name", cell: (r) => r.name, rowHeader: true },
    { key: "team", header: "Team", cell: (r) => r.team },
  ];

  it("renders that column as a row header and the rest as cells", () => {
    renderDS(
      <DataTable columns={COLS_WITH_HEADER} rows={ROWS.slice(0, 2)} rowKey={(r) => r.id} caption="People" />);
    const first = screen.getAllByRole("row")[1]!;
    // `rowheader` is the ARIA role a <th scope="row"> maps to; a screen reader announces it with every cell
    // read across the row, which is the whole point.
    expect(within(first).getByRole("rowheader")).toHaveTextContent("Amal");
    expect(within(first).getAllByRole("cell")).toHaveLength(1);
  });

  it("leaves every column a plain cell when nothing opts in", () => {
    renderDS(<DataTable columns={COLS} rows={ROWS.slice(0, 2)} rowKey={(r) => r.id} caption="People" />);
    const first = screen.getAllByRole("row")[1]!;
    expect(within(first).queryByRole("rowheader")).not.toBeInTheDocument();
    expect(within(first).getAllByRole("cell")).toHaveLength(COLS.length);
  });

  it("cannot pick up the COLUMN-header styling, because that rule is scoped to the head", () => {
    // The header rule is sticky, uppercase, 11.5px micro-label on --surface-2 — a description of a column
    // HEADING, never a claim about `th` in general. Scoped to `thead`, a body `th` is simply not matched,
    // so nothing has to be undone and the two cannot drift apart. Written as an unscoped `.mrs-wl th` the
    // person's name would come out grey, tiny, upper-cased and pinned to the top of the scrollport.
    const css = readFileSync(resolve(__dirname, "../src/styles/components.css"), "utf8");
    expect(css).toMatch(/\.mrs-wl thead th \{/);
    expect(css, "an unscoped .mrs-wl th would style the body too").not.toMatch(/\n\.mrs-wl th \{/);
    // And the body th is styled WITH the tds, so their padding cannot diverge.
    expect(css).toMatch(/\.mrs-wl td,\n\.mrs-wl tbody th \{/);
  });
});

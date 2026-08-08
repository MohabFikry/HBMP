import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderDS } from "./render";
import { DataTable, TableToolbar, type Column } from "../src";

interface Row {
  id: string;
  name: string;
  age: number;
  branch?: string;
}

const ROWS: Row[] = [
  { id: "3", name: "Zeinab", age: 31, branch: "Maadi" },
  { id: "1", name: "Amal", age: 47 },
  { id: "2", name: "Karim", age: 8, branch: "Dokki" },
];

const COLS: Column<Row>[] = [
  { key: "name", header: "Name", cell: (r) => r.name, sortable: true, sortValue: (r) => r.name },
  { key: "age", header: "Age", cell: (r) => String(r.age), sortable: true, sortValue: (r) => r.age },
  { key: "branch", header: "Branch", cell: (r) => r.branch ?? "—", sortable: true, sortValue: (r) => r.branch },
  { key: "id", header: "Ref", cell: (r) => r.id },
];

const names = () =>
  screen.getAllByRole("row").slice(1).map((r) => within(r).getAllByRole("cell")[0].textContent);

/**
 * Sorting used to be controlled-only, so every caller re-implemented the comparator, the state and the click
 * handler — and "sortable columns" as a house standard rested on each screen remembering to. These cover the
 * built-in mode that replaces that.
 */
describe("DataTable — built-in (uncontrolled) sort", () => {
  it("sorts ascending on first click and descending on the second", async () => {
    const user = userEvent.setup();
    renderDS(<DataTable columns={COLS} rows={ROWS} rowKey={(r) => r.id} caption="People" />);

    await user.click(screen.getByRole("button", { name: /name/i }));
    expect(names()).toEqual(["Amal", "Karim", "Zeinab"]);

    await user.click(screen.getByRole("button", { name: /name/i }));
    expect(names()).toEqual(["Zeinab", "Karim", "Amal"]);
  });

  it("sorts NUMBERS numerically, not as strings", async () => {
    const user = userEvent.setup();
    renderDS(<DataTable columns={COLS} rows={ROWS} rowKey={(r) => r.id} caption="People" />);

    await user.click(screen.getByRole("button", { name: /age/i }));
    // As strings this would be 31, 47, 8 — the bug every hand-rolled comparator eventually ships.
    expect(names()).toEqual(["Karim", "Zeinab", "Amal"]);
  });

  it("starts a NEW column ascending rather than inheriting the previous direction", async () => {
    const user = userEvent.setup();
    renderDS(<DataTable columns={COLS} rows={ROWS} rowKey={(r) => r.id} caption="People" />);

    await user.click(screen.getByRole("button", { name: /name/i }));
    await user.click(screen.getByRole("button", { name: /name/i }));   // now descending
    await user.click(screen.getByRole("button", { name: /age/i }));

    // Landing on a descending sort nobody asked for reads as the table reordering itself.
    expect(names()).toEqual(["Karim", "Zeinab", "Amal"]);
  });

  it("sinks missing values to the bottom in BOTH directions", async () => {
    const user = userEvent.setup();
    renderDS(<DataTable columns={COLS} rows={ROWS} rowKey={(r) => r.id} caption="People" />);

    await user.click(screen.getByRole("button", { name: /branch/i }));
    expect(names()).toEqual(["Karim", "Zeinab", "Amal"]);   // Dokki, Maadi, then the one with none

    await user.click(screen.getByRole("button", { name: /branch/i }));
    // Amal stays last: "no value" at the top of a descending list reads as data rather than its absence.
    expect(names()).toEqual(["Zeinab", "Karim", "Amal"]);
  });

  it("reports direction through aria-sort", async () => {
    const user = userEvent.setup();
    renderDS(<DataTable columns={COLS} rows={ROWS} rowKey={(r) => r.id} caption="People" />);

    const header = screen.getByRole("columnheader", { name: /name/i });
    expect(header).toHaveAttribute("aria-sort", "none");
    await user.click(within(header).getByRole("button"));
    expect(header).toHaveAttribute("aria-sort", "ascending");
    await user.click(within(header).getByRole("button"));
    expect(header).toHaveAttribute("aria-sort", "descending");
  });

  it("leaves ordering alone when the caller controls sort", async () => {
    const user = userEvent.setup();
    const onSort = vi.fn();
    renderDS(
      <DataTable columns={COLS} rows={ROWS} rowKey={(r) => r.id} caption="People" onSort={onSort} />,
    );

    await user.click(screen.getByRole("button", { name: /name/i }));

    // The caller (or the server) owns the order; the table must not also reorder behind their back.
    expect(onSort).toHaveBeenCalledWith("name");
    expect(names()).toEqual(["Zeinab", "Amal", "Karim"]);
  });

  it("does not mutate the caller's array", async () => {
    const user = userEvent.setup();
    const rows = [...ROWS];
    renderDS(<DataTable columns={COLS} rows={rows} rowKey={(r) => r.id} caption="People" />);

    await user.click(screen.getByRole("button", { name: /name/i }));

    expect(rows.map((r) => r.name)).toEqual(["Zeinab", "Amal", "Karim"]);
  });
});

function Harness() {
  const [q, setQ] = useState("");
  const [status, setStatus] = useState<string | null>(null);
  return (
    <TableToolbar
      search={{ label: "Search", value: q, onChange: setQ }}
      filters={[{
        key: "status",
        label: "Status",
        value: status,
        onChange: setStatus,
        options: [
          { value: "booked", label: "Booked", count: 12 },
          { value: "checked-in", label: "Checked in", count: 3 },
        ],
      }]}
    />
  );
}

describe("TableToolbar", () => {
  it("toggles a filter on, and OFF again when the active chip is pressed", async () => {
    const user = userEvent.setup();
    renderDS(<Harness />);

    const booked = screen.getByRole("button", { name: /booked/i });
    expect(booked).toHaveAttribute("aria-pressed", "false");

    await user.click(booked);
    expect(screen.getByRole("button", { name: /booked/i })).toHaveAttribute("aria-pressed", "true");

    // Clearing by pressing the active chip is the first thing anyone tries; without it a single-select group
    // is a trap once you have chosen.
    await user.click(screen.getByRole("button", { name: /booked/i }));
    expect(screen.getByRole("button", { name: /booked/i })).toHaveAttribute("aria-pressed", "false");
  });

  it("is single-select within a group", async () => {
    const user = userEvent.setup();
    renderDS(<Harness />);

    await user.click(screen.getByRole("button", { name: /booked/i }));
    await user.click(screen.getByRole("button", { name: /checked in/i }));

    expect(screen.getByRole("button", { name: /booked/i })).toHaveAttribute("aria-pressed", "false");
    expect(screen.getByRole("button", { name: /checked in/i })).toHaveAttribute("aria-pressed", "true");
  });

  it("names the filter group, so its chips are not unattributed buttons", () => {
    renderDS(<Harness />);
    // A fieldset+legend: a screen-reader user hears "Status: Booked, pressed" rather than a bare "Booked".
    expect(screen.getByRole("group", { name: /status/i })).toBeInTheDocument();
  });

  it("drives search through the caller's state", async () => {
    const user = userEvent.setup();
    renderDS(<Harness />);

    await user.type(screen.getByLabelText(/search/i), "hana");
    expect(screen.getByLabelText(/search/i)).toHaveValue("hana");
  });

  it("keeps the group's visible label in normal flow, not in the legend", () => {
    // A rendered <legend> is laid out against the top of its fieldset's BOX rather than in flow. The moment a
    // group carried an `extra` — the appointments board's date range, a label-over-control pair some 30px
    // taller than a chip — the fieldset grew and "WHEN" rose with it, while SEARCH, FROM, TO and STATUS stayed
    // on the label line below. Nothing was misaligned by accident; the legend was measuring a different box.
    //
    // jsdom performs no layout, so what is pinned here is the STRUCTURE that fixed it: the legend carries the
    // accessible name only, and a span carries the visible one.
    const { container } = renderDS(<Harness />);

    const legend = container.querySelector("fieldset > legend");
    expect(legend).toHaveClass("sr-only");

    const visible = container.querySelector(".mrs-toolbar-grouplabel");
    expect(visible).toHaveTextContent(/status/i);
    // aria-hidden, so the group is announced once rather than twice.
    expect(visible).toHaveAttribute("aria-hidden", "true");
    // ...and the group is still named, which is the thing the legend was there for.
    expect(screen.getByRole("group", { name: /status/i })).toBeInTheDocument();
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderDS(<Harness />);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

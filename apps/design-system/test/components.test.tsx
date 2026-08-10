import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderDS } from "./render";
import {
  Button,
  DataTable,
  InputField,
  SegmentedControl,
  StatusChip,
  Tabs,
  type Column,
  type StatusKind,
} from "../src";

describe("Button", () => {
  it("sets aria-busy and disables while loading", () => {
    renderDS(
      <Button variant="primary" loading>
        Save
      </Button>,
    );
    const btn = screen.getByRole("button", { name: "Save" });
    expect(btn).toHaveAttribute("aria-busy", "true");
    expect(btn).toBeDisabled();
  });

  it("fires onClick when enabled", async () => {
    const onClick = vi.fn();
    renderDS(<Button onClick={onClick}>Go</Button>);
    await userEvent.click(screen.getByRole("button", { name: "Go" }));
    expect(onClick).toHaveBeenCalledOnce();
  });
});

describe("StatusChip (color-blind safe)", () => {
  const kinds: StatusKind[] = ["ok", "info", "part", "warn", "bad", "neu"];
  it("always renders a visible text label alongside the hue (never color-only)", () => {
    kinds.forEach((k) => {
      const { unmount } = renderDS(<StatusChip kind={k} label={`Label-${k}`} />);
      expect(screen.getByText(`Label-${k}`)).toBeInTheDocument();
      unmount();
    });
  });

  it("encodes a distinct grayscale shape per kind via data-shape", () => {
    renderDS(
      <>
        <StatusChip kind="ok" label="ok" />
        <StatusChip kind="bad" label="bad" />
      </>,
    );
    expect(screen.getByText("ok").closest(".mrs-chip")).toHaveAttribute("data-shape", "pill");
    expect(screen.getByText("bad").closest(".mrs-chip")).toHaveAttribute("data-shape", "square");
  });
});

describe("InputField", () => {
  it("ties error to the control via aria-describedby + aria-invalid", () => {
    renderDS(<InputField label="Name" error="Required" />);
    const input = screen.getByLabelText("Name");
    expect(input).toHaveAttribute("aria-invalid", "true");
    const describedBy = input.getAttribute("aria-describedby");
    expect(describedBy).toBeTruthy();
    expect(screen.getByRole("alert")).toHaveTextContent("Required");
  });
});

describe("SegmentedControl", () => {
  it("is a radiogroup and moves selection with arrow keys", async () => {
    const onChange = vi.fn();
    renderDS(
      <SegmentedControl
        aria-label="Filter"
        value="all"
        onChange={onChange}
        segments={[
          { value: "all", label: "All" },
          { value: "review", label: "Review" },
        ]}
      />,
    );
    const group = screen.getByRole("radiogroup", { name: "Filter" });
    const all = within(group).getByRole("radio", { name: "All" });
    all.focus();
    await userEvent.keyboard("{ArrowRight}");
    expect(onChange).toHaveBeenCalledWith("review");
  });
});

interface DemoRow {
  id: string;
  service: string;
  status: StatusKind;
}
const rows: DemoRow[] = [
  { id: "A-1", service: "MRI", status: "info" },
  { id: "A-2", service: "CT", status: "ok" },
];
const columns: Column<DemoRow>[] = [
  { key: "id", header: "ID", cell: (r) => r.id },
  { key: "service", header: "Service", cell: (r) => r.service },
];

describe("DataTable", () => {
  it("renders the loading state exclusively", () => {
    renderDS(<DataTable caption="cap" columns={columns} rows={[]} rowKey={(r) => r.id} loading />);
    expect(screen.getByText("Loading…")).toBeInTheDocument();
  });

  it("renders an empty state when no rows", () => {
    renderDS(
      <DataTable caption="cap" columns={columns} rows={[]} rowKey={(r) => r.id} emptyLabel="Nothing here" />,
    );
    expect(screen.getByText("Nothing here")).toBeInTheDocument();
  });

  it("renders an error state with role=alert", () => {
    renderDS(<DataTable caption="cap" columns={columns} rows={[]} rowKey={(r) => r.id} error="Boom" />);
    expect(screen.getByRole("alert")).toHaveTextContent("Boom");
  });

  it("marks the selected interactive row with aria-selected", async () => {
    const onSelect = vi.fn();
    renderDS(
      <DataTable
        caption="cap"
        columns={columns}
        rows={rows}
        rowKey={(r) => r.id}
        interactive
        selectedKey="A-1"
        onSelect={onSelect}
      />,
    );
    const selected = screen.getByText("MRI").closest("tr");
    expect(selected).toHaveAttribute("aria-selected", "true");
    await userEvent.click(screen.getByText("CT"));
    expect(onSelect).toHaveBeenCalledWith(rows[1]);
  });
});

describe("Tabs", () => {
  it("is a tablist with correct roles and switches panels on click", async () => {
    const onValueChange = vi.fn();
    function Demo() {
      const [value, setValue] = useState("a");
      return (
        <Tabs
          aria-label="Sections"
          value={value}
          onValueChange={(v) => {
            setValue(v);
            onValueChange(v);
          }}
          items={[
            { value: "a", label: "First", content: <p>First content</p> },
            { value: "b", label: "Second", content: <p>Second content</p> },
          ]}
        />
      );
    }
    renderDS(<Demo />);
    expect(screen.getByRole("tablist", { name: "Sections" })).toBeInTheDocument();
    expect(screen.getByText("First content")).toBeVisible();
    expect(screen.queryByText("Second content")).not.toBeVisible();

    await userEvent.click(screen.getByRole("tab", { name: "Second" }));
    expect(onValueChange).toHaveBeenCalledWith("b");
    expect(screen.getByText("Second content")).toBeVisible();
  });

  it("applies the pill visual variant without changing tab semantics", () => {
    renderDS(
      <Tabs
        variant="pill"
        aria-label="Sections"
        value="a"
        onValueChange={() => {}}
        items={[{ value: "a", label: "First", content: <p>First</p> }]}
      />,
    );
    expect(screen.getByRole("tablist")).toHaveClass("mrs-tabs--pill");
    expect(screen.getByRole("tab", { name: "First" })).toBeInTheDocument();
  });
});

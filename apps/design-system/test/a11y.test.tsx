import { describe, expect, it } from "vitest";
import { axe } from "jest-axe";
import { renderDS } from "./render";
import {
  Button,
  Card,
  DataTable,
  InputField,
  KpiCard,
  Logo,
  NavRail,
  SearchField,
  SegmentedControl,
  StatusChip,
  type Column,
  type NavItem,
  type StatusKind,
} from "../src";

/**
 * Accessibility gate (hard CI requirement, 0B §9 / 21). axe must find zero serious/critical violations
 * on the component set. This mirrors the "axe on the gallery/Storybook" acceptance criterion — we assert
 * on the same components the gallery renders, in both LTR and (implicitly, via mirrored logical CSS) RTL.
 */

const STATUS: StatusKind[] = ["ok", "info", "part", "warn", "bad", "neu"];

interface Row {
  id: string;
  service: string;
  status: StatusKind;
}
const rows: Row[] = [
  { id: "AUTH-1", service: "MRI", status: "info" },
  { id: "AUTH-2", service: "CT", status: "ok" },
];
const columns: Column<Row>[] = [
  { key: "id", header: "Authorization", cell: (r) => <span className="mono">{r.id}</span>, sortable: true },
  { key: "service", header: "Service", cell: (r) => r.service },
  { key: "status", header: "Status", cell: (r) => <StatusChip kind={r.status} label={r.status} /> },
];
const navItems: NavItem[] = [
  { key: "reception", group: "Access", label: "Reception" },
  { key: "doctor", group: "Clinical", label: "Doctor" },
];

async function expectNoViolations(el: HTMLElement) {
  const results = await axe(el, {
    // Assert on the enforced tiers; axe defaults already flag serious/critical rules.
    rules: {
      // color-contrast needs real layout/paint which jsdom lacks; the contract is verified by design tokens.
      "color-contrast": { enabled: false },
    },
  });
  expect(results).toHaveNoViolations();
}

describe("axe — no serious/critical violations", () => {
  it("Buttons (all variants + states)", async () => {
    const { container } = renderDS(
      <div>
        <Button variant="primary">Primary</Button>
        <Button variant="secondary">Secondary</Button>
        <Button variant="ghost">Ghost</Button>
        <Button variant="danger">Danger</Button>
        <Button variant="primary" loading>
          Loading
        </Button>
        <Button variant="ghost" leadingIcon={<span />} aria-label="Icon only" />
      </div>,
    );
    await expectNoViolations(container);
  });

  it("Status chips", async () => {
    const { container } = renderDS(
      <div>
        {STATUS.map((k) => (
          <StatusChip key={k} kind={k} label={k} />
        ))}
      </div>,
    );
    await expectNoViolations(container);
  });

  it("Fields", async () => {
    const { container } = renderDS(
      <div>
        <SearchField aria-label="Search" />
        <InputField label="Name" help="As printed" />
        <InputField label="Bad" error="Required" />
      </div>,
    );
    await expectNoViolations(container);
  });

  it("Segmented control", async () => {
    const { container } = renderDS(
      <SegmentedControl
        aria-label="Filter"
        value="all"
        onChange={() => {}}
        segments={[
          { value: "all", label: "All" },
          { value: "review", label: "Review" },
        ]}
      />,
    );
    await expectNoViolations(container);
  });

  it("Data table (worklist)", async () => {
    const { container } = renderDS(
      <DataTable
        caption="Approval worklist"
        columns={columns}
        rows={rows}
        rowKey={(r) => r.id}
        interactive
        selectedKey="AUTH-1"
        sortKey="id"
        sortDir="ascending"
        onSort={() => {}}
      />,
    );
    await expectNoViolations(container);
  });

  it("KPI card", async () => {
    const { container } = renderDS(<KpiCard label="Visits today" value="148" delta="+12%" direction="up" />);
    await expectNoViolations(container);
  });

  it("Navigation rail", async () => {
    const { container } = renderDS(
      <NavRail aria-label="Screens" items={navItems} current="reception" onNavigate={() => {}} />,
    );
    await expectNoViolations(container);
  });

  it("Logo lockup + card", async () => {
    const { container } = renderDS(
      <Card style={{ padding: 20 }}>
        <Logo variant="mark" wordmark="HBMP" />
      </Card>,
    );
    await expectNoViolations(container);
  });
});

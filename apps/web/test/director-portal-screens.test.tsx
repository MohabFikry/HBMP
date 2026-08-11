import { describe, expect, it } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { ServiceUse } from "../src/screens/director/ServiceUse";
import { ClaimsCost } from "../src/screens/director/ClaimsCost";
import { periodFor } from "../src/screens/director/PeriodControl";
import { portalForRole } from "../src/portals/catalog";
import { permissionsForRole } from "../src/authz/permissions";

/**
 * The Medical Director portal's oversight screens, RENDERED.
 *
 * The assertions are about what the screens SAY, not about markup — every defect this pass fixed was of the
 * form "the portal states something that is not true of the data, or cannot state it at all", and a test
 * asserting class names would have passed throughout.
 */
const wrap = (ui: React.ReactNode) =>
  render(
    <AppProviders authClient={new DevAuthClient()}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>{ui}</MemoryRouter>
    </AppProviders>,
  );

describe("Utilization", () => {
  it("shows every axis the report supports, not just the one the dashboard pinned", async () => {
    wrap(<ServiceUse />);
    // The defect this replaces: `/reports/utilization` accepts four dimensions, the dashboard hard-coded
    // `provider`, and the other three were reachable from no screen in the application.
    for (const axis of ["Provider", "Medication", "Laboratory", "Radiology"]) {
      expect(screen.getByRole("radio", { name: axis })).toBeTruthy();
    }
  });

  it("changing the axis changes the rows, so the picker is not decorative", async () => {
    const user = userEvent.setup();
    wrap(<ServiceUse />);
    await waitFor(() => expect(screen.getByText(/Nile Central Hospital/)).toBeTruthy());

    await user.click(screen.getByRole("radio", { name: "Medication" }));
    await waitFor(() => expect(screen.getByText(/metformin/)).toBeTruthy());
    // And the provider rows are gone — a picker that ADDED rows would pass a "the new one is present" check
    // while showing two axes at once.
    expect(screen.queryByText(/Nile Central Hospital/)).toBeNull();
  });

  it("states the window it is showing, in dates rather than only a preset name", async () => {
    wrap(<ServiceUse />);
    // A preset name is a promise; the dates are the promise kept, and they are what a supervisor writes down.
    // Before this control existed, no director screen sent a period OR said what one it had been given.
    const showing = await screen.findByText(/Showing/);
    expect(showing.textContent).toMatch(/\d/);
  });
});

describe("Claims & Cost", () => {
  it("reports outcomes, cost by service line, and why claims were refused", async () => {
    wrap(<ClaimsCost />);
    // By ROLE, not by text: each section's title is also its table's accessible caption, so a bare text
    // query matches twice and throws — which is itself the sign that the caption is doing its job.
    await waitFor(() => expect(screen.getByRole("heading", { name: "Outcomes" })).toBeTruthy());
    expect(screen.getByRole("heading", { name: "Cost by service line" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Why claims were refused" })).toBeTruthy();
    expect(screen.getByText("NOT_COVERED")).toBeTruthy();
  });

  it("formats money as money", async () => {
    wrap(<ClaimsCost />);
    // The dashboard's financial KPI used to be `String(sum-of-decimals)` — no currency, no grouping, and
    // exposed to float artefacts — in an app whose every other amount goes through useFormat().money.
    const total = await screen.findByText(/Total allowed/);
    const tile = total.closest("*")?.parentElement;
    expect(within(tile as HTMLElement).getByText(/EGP|£|٬|,/)).toBeTruthy();
  });

  it("shows the approval and denial rates against the same decided total", async () => {
    wrap(<ClaimsCost />);
    await waitFor(() => expect(screen.getByText("Approval rate")).toBeTruthy());
    expect(screen.getByText("Denial rate")).toBeTruthy();
  });
});

describe("the period control", () => {
  it("resolves a quarter to the calendar quarter, not to ninety days", () => {
    // These are different questions and only coincidentally the same number. A director comparing against a
    // board report is comparing against quarters.
    const quarter = periodFor("quarter");
    expect(quarter.from.slice(5, 7)).toMatch(/01|04|07|10/);
    expect(quarter.from.slice(8, 10)).toBe("01");
  });

  it("resolves the rolling windows to the length they claim", () => {
    const days = (p: { from: string; to: string }) =>
      Math.round((Date.parse(p.to) - Date.parse(p.from)) / 86_400_000);
    expect(days(periodFor("30d"))).toBe(30);
    expect(days(periodFor("90d"))).toBe(90);
  });
});

describe("the portal's own navigation", () => {
  it("gives the SLA board a door on the director's portal", () => {
    /*
     * `medical_director` has always held `approvals.sla`, and `/approvals/sla` has always rendered for them
     * — the router resolves a path against the whole catalog and then checks the permission, which passed.
     * But the section existed only on the approvals portal, which `portalsForRoles` never returns for a
     * director, so a working screen they were entitled to appeared in no navigation they could see.
     */
    const director = portalForRole("medical_director");
    const sla = director.sections.find((s) => s.permission === "approvals.sla");
    expect(sla, "the SLA board must be reachable from the portal of the person who answers for the SLA").toBeTruthy();
    expect(permissionsForRole("medical_director").has("approvals.sla")).toBe(true);
  });

  it("holds a permission for every section it shows, and shows a section for every permission it holds", () => {
    /*
     * Both directions, because the two failures are different and the platform has now had one of each: an
     * org admin once had a nav entry whose API could only ever 403, and the director had a permission whose
     * only screen was on somebody else's portal. A nav item that cannot succeed teaches people the platform
     * is broken; a permission with no door just wastes the work that built it.
     */
    const held = permissionsForRole("medical_director");
    const director = portalForRole("medical_director");
    const shown = new Set(director.sections.map((s) => s.permission));

    for (const section of director.sections) {
      expect(held.has(section.permission), `the director portal shows "${section.key}" but the role does not hold ${section.permission}`).toBe(true);
    }
    for (const permission of held) {
      // `profile.read` and `profile.export` are cross-portal deep links opened FOR somebody from a worklist
      // or a search result, never navigated to from a menu (design 39 §6) — they are correctly doorless.
      if (permission === "profile.read" || permission === "profile.export") continue;
      expect(shown.has(permission), `medical_director holds ${permission} and no section on its portal uses it`).toBe(true);
    }
  });
});

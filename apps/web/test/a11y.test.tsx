import { describe, expect, it } from "vitest";
import { screen } from "@testing-library/react";
import { axe } from "jest-axe";
import { renderApp } from "./helpers";

async function noViolations(el: HTMLElement) {
  const results = await axe(el, { rules: { "color-contrast": { enabled: false } } });
  expect(results).toHaveNoViolations();
}

describe("axe — portal shell & login (a11y gate)", () => {
  it("login screen has no serious/critical violations", async () => {
    const { container } = renderApp("/login");
    await screen.findByRole("heading", { name: "Sign in" });
    await noViolations(container);
  });

  it("portal shell (banner + navigation + main landmarks) has no serious/critical violations", async () => {
    const { container } = renderApp("/", "medical_approval");
    await screen.findByRole("heading", { name: "Approval Worklist" });
    // Landmarks present.
    expect(screen.getByRole("banner")).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Approval Worklist" })).toBeInTheDocument();
    expect(screen.getByRole("main")).toBeInTheDocument();
    await noViolations(container);
  });

  it("403 page has no serious/critical violations", async () => {
    const { container } = renderApp("/finance/settlements", "reception");
    await screen.findByRole("heading", { name: "You don't have access to this page" });
    await noViolations(container);
  });
});

/**
 * The reception screens, rendered THROUGH THE ROUTE rather than in isolation.
 *
 * This distinction is the whole point of these two cases. `reception-booking.test.tsx` already runs axe over
 * the booking screen, but it mounts the component with `renderNode`, and `PageHeader` returns null when there
 * is no session — so no <h1> is ever rendered and the page has no heading hierarchy to get wrong. The screen
 * headed its three steps with <h3> directly under the page <h1> for exactly that reason: in isolation the h3
 * was the FIRST heading, which `heading-order` allows, and the skipped level only exists once the portal
 * shell supplies the h1 around it. An isolation test cannot see a document-outline defect, because in
 * isolation there is no document outline.
 */
describe("axe — reception portal routes (a11y gate)", () => {
  it("book appointment has no serious/critical violations", async () => {
    const { container } = renderApp("/reception/book", "reception");
    await screen.findByRole("heading", { level: 1, name: "Book an Appointment" });
    // The steps are sections OF the page, so they sit one level below its h1 — asserted directly, because
    // this is the regression the route-level render exists to catch.
    expect(await screen.findByRole("heading", { level: 2, name: "1. Patient" })).toBeInTheDocument();
    await noViolations(container);
  });

  it("appointments board has no serious/critical violations", async () => {
    const { container } = renderApp("/reception/appointments", "reception");
    await screen.findByRole("heading", { level: 1, name: "Appointments" });
    await noViolations(container);
  });
});

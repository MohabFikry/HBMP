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
    await screen.findByRole("heading", { name: "Sign in to Mersal HBMP" });
    await noViolations(container);
  });

  it("portal shell (banner + navigation + main landmarks) has no serious/critical violations", async () => {
    const { container } = renderApp("/", "medical_approval");
    await screen.findByRole("heading", { name: "Approval worklist" });
    // Landmarks present.
    expect(screen.getByRole("banner")).toBeInTheDocument();
    expect(screen.getByRole("navigation", { name: "Approval worklist" })).toBeInTheDocument();
    expect(screen.getByRole("main")).toBeInTheDocument();
    await noViolations(container);
  });

  it("403 page has no serious/critical violations", async () => {
    const { container } = renderApp("/finance/settlements", "reception");
    await screen.findByRole("heading", { name: "You don't have access to this page" });
    await noViolations(container);
  });
});

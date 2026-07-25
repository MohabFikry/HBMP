import { describe, expect, it, beforeEach } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderApp } from "./helpers";
import { auditClient } from "../src/audit/auditClient";

beforeEach(() => auditClient.drain());

describe("Permission-driven routing (US-070 / US-071)", () => {
  it("lands a signed-in user on their own portal home", async () => {
    renderApp("/", "reception");
    // Reception's first section is Eligibility search.
    expect(await screen.findByRole("heading", { name: "Eligibility search" })).toBeInTheDocument();
  });

  it("generates a nav menu with only the role's own sections (hides forbidden items)", async () => {
    renderApp("/", "reception");
    const nav = await screen.findByRole("navigation", { name: "Reception" });
    // Reception sees its own sections…
    expect(within(nav).getByText("Eligibility search")).toBeInTheDocument();
    expect(within(nav).getByText("Today's visits")).toBeInTheDocument();
    // …and never another portal's (no EMR, no finance in the menu).
    expect(within(nav).queryByText("Encounter workspace")).not.toBeInTheDocument();
    expect(within(nav).queryByText("Provider settlements")).not.toBeInTheDocument();
  });

  it("MIN-NECESSARY: Finance has no clinical route — a diagnosis/EMR deep link is a 403", async () => {
    renderApp("/clinician/encounter", "finance");
    expect(await screen.findByRole("heading", { name: "You don't have access to this page" })).toBeInTheDocument();
    // …and offers a request-access affordance.
    expect(screen.getByRole("button", { name: "Request access" })).toBeInTheDocument();
  });

  it("audits a forbidden deep-link attempt (access.denied)", async () => {
    renderApp("/clinician/encounter", "finance");
    await screen.findByRole("heading", { name: "You don't have access to this page" });
    await waitFor(() => {
      const events = auditClient.peek();
      expect(events.some((e) => e.type === "access.denied" && e.path === "/clinician/encounter")).toBe(true);
    });
  });

  it("a deep link the user CAN access renders the section, not a 403", async () => {
    renderApp("/finance/settlements", "finance");
    expect(await screen.findByRole("heading", { name: "Provider settlements" })).toBeInTheDocument();
  });

  it("an unknown path renders 404, not 403", async () => {
    renderApp("/nope/nowhere", "reception");
    expect(await screen.findByRole("heading", { name: "Page not found" })).toBeInTheDocument();
  });

  it("redirects an unauthenticated visitor to the login screen", async () => {
    renderApp("/finance/settlements");
    expect(await screen.findByRole("heading", { name: "Sign in to Mersal HBMP" })).toBeInTheDocument();
  });
});

describe("Login + MFA (US-070)", () => {
  it("blocks sign-in without a valid 6-digit MFA code, then lands on the portal", async () => {
    renderApp("/login");
    await screen.findByRole("heading", { name: "Sign in to Mersal HBMP" });

    // Select the pharmacy role.
    await userEvent.selectOptions(screen.getByLabelText("Role (demo sign-in)"), "pharmacy");

    // Submit with no code → blocked with an error.
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByText("A valid 6-digit code is required.")).toBeInTheDocument();

    // Provide a valid code → lands on the pharmacy portal home.
    await userEvent.type(screen.getByLabelText("Authenticator code"), "123456");
    await userEvent.click(screen.getByRole("button", { name: "Sign in" }));
    expect(await screen.findByRole("heading", { name: "Prescription queue" })).toBeInTheDocument();
  });
});

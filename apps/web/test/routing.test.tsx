import { describe, expect, it, beforeEach } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderApp } from "./helpers";
import { auditClient } from "../src/audit/auditClient";

beforeEach(() => auditClient.drain());

describe("Permission-driven routing (US-070 / US-071)", () => {
  it("lands a signed-in user on their own portal home", async () => {
    renderApp("/", "reception");
    // 14.5 — reception's first section is now the Dashboard: the desk's landing page is how the day is
    // going, not a search box they have to think of something to type into.
    expect(await screen.findByRole("heading", { name: "Dashboard" })).toBeInTheDocument();
  });

  it("generates a nav menu with only the role's own sections (hides forbidden items)", async () => {
    renderApp("/", "reception");
    const nav = await screen.findByRole("navigation", { name: "Reception" });
    // Reception sees its own sections…
    // "Eligibility Check", not "Eligibility Search": one screen was mounted under two portals whose rails
    // named it differently (reception said Search, beneficiary management said Check) while its own
    // heading said a third thing. Both rails and the heading now use the nav label.
    expect(within(nav).getByText("Eligibility Check")).toBeInTheDocument();
    expect(within(nav).getByText("Dashboard")).toBeInTheDocument();
    // "Today's Visits" and "Check-in" were folded into the dashboard and the appointments table in 14.5;
    // leaving them in the rail would offer two doors to one thing.
    expect(within(nav).queryByText("Today's Visits")).not.toBeInTheDocument();
    expect(within(nav).queryByText("Check-in")).not.toBeInTheDocument();
    // …and never another portal's (no EMR, no finance in the menu).
    expect(within(nav).queryByText("Encounter Workspace")).not.toBeInTheDocument();
    expect(within(nav).queryByText("Provider Settlements")).not.toBeInTheDocument();
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
    expect(await screen.findByRole("heading", { name: "Provider Settlements" })).toBeInTheDocument();
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

describe("Browser tab title (replaces the on-screen breadcrumb)", () => {
  it("names the tab after the active section and its portal", async () => {
    renderApp("/finance/settlements", "finance");
    await screen.findByRole("heading", { name: "Provider Settlements" });
    await waitFor(() => expect(document.title).toBe("Provider Settlements | Mersal HBMP"));
  });

  it("follows navigation — the tab retitles when the section changes", async () => {
    renderApp("/", "reception");
    await screen.findByRole("heading", { name: "Dashboard" });
    await waitFor(() => expect(document.title).toBe("Dashboard | Mersal HBMP"));

    const nav = await screen.findByRole("navigation", { name: "Reception" });
    await userEvent.click(within(nav).getByText("Eligibility Check"));
    await waitFor(() => expect(document.title).toBe("Eligibility Check | Mersal HBMP"));
  });

  it("keeps the brand alone outside a portal, and renders no breadcrumb inside one", async () => {
    const { unmount } = renderApp("/", "reception");
    await screen.findByRole("heading", { name: "Dashboard" });
    // The nav rail is the only navigation landmark left; the crumb trail is gone.
    expect(screen.queryByRole("navigation", { name: "Breadcrumb" })).not.toBeInTheDocument();
    expect(screen.getAllByRole("navigation")).toHaveLength(1);

    unmount();
    expect(document.title).toBe("Mersal HBMP");
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
    expect(await screen.findByRole("heading", { name: "Prescription Queue" })).toBeInTheDocument();
  });
});

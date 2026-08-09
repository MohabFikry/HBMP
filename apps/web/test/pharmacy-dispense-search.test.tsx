import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ApiError } from "../src/api/http";
import { PharmacyDispense } from "../src/screens/PharmacyDispense";

/**
 * The dispensing counter (phase 6, redesigned).
 *
 * <p>Two things are pinned here. The screen asks WHO before it shows anything — it used to list every
 * dispensable prescription in the tenant, which is both the wrong workflow and a disclosure of other
 * patients' prescriptions on the way to one. And a search that could not run is never rendered as "this
 * member has no prescriptions": a 503 from the patient directory and an empty result set are different
 * answers, and only one of them is about the patient.</p>
 */

/** Surfaces the router's path so a navigation can be asserted — MemoryRouter never touches window.location. */
function Where() {
  return <span data-testid="path">{useLocation().pathname}</span>;
}

function renderScreen(api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <PharmacyDispense />
        <Where />
      </MemoryRouter>
    </AppProviders>,
  );
}

/** A client whose search always fails with the given status. */
function failingSearch(status: number): ApiClient {
  const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
  (api as { pharmacySearch: unknown }).pharmacySearch =
    vi.fn().mockRejectedValue(new ApiError("http", "refused", status));
  return api;
}

describe("search-first", () => {
  it("shows no prescriptions until one is searched for", async () => {
    renderScreen();
    expect(await screen.findByText(/Enter a prescription number/)).toBeInTheDocument();
    // Nothing from the fixture is on screen — the counter has not been told who the member is yet.
    expect(screen.queryByText("RX-2026-033110")).toBeNull();
  });

  it("finds a member's prescriptions by prescription number", async () => {
    const user = userEvent.setup();
    renderScreen();

    await user.type(screen.getByLabelText("Prescription number"), "RX-2026-033110");
    await user.click(screen.getByRole("button", { name: "Search" }));

    expect(await screen.findByText("RX-2026-033110")).toBeInTheDocument();
  });

  it("cannot be searched with an empty form", async () => {
    renderScreen();
    expect(await screen.findByRole("button", { name: "Search" })).toBeDisabled();
  });
});

describe("a search that could not run is not a negative result", () => {
  it("says the directory was unreachable rather than 'no prescriptions'", async () => {
    const user = userEvent.setup();
    renderScreen(failingSearch(503));

    await user.type(screen.getByLabelText("Card number"), "MRS-2026-0019");
    await user.type(screen.getByLabelText("Member number"), "MRS-M-2026-000019");
    await user.click(screen.getByRole("button", { name: "Search" }));

    // The distinction the whole screen turns on: this is NOT a report about the member.
    const alert = await screen.findByText(/could not be reached/i);
    expect(alert.textContent).toMatch(/NOT a report/i);
    expect(screen.queryByText(/No dispensable prescription matches/i)).toBeNull();
  });

  it("explains WHY one identifier is not enough instead of returning nothing", async () => {
    const user = userEvent.setup();
    renderScreen(failingSearch(422));

    await user.type(screen.getByLabelText("Card number"), "MRS-2026-0019");
    await user.click(screen.getByRole("button", { name: "Search" }));

    // A card number is a lookup key, not an authenticator — and the refusal has to say so, or it reads as
    // a broken search rather than a deliberate rule.
    const alert = await screen.findByText(/card number on its own is not enough/i);
    expect(alert.textContent).toMatch(/member number or passport/i);
  });
});

describe("the counter can read what it is dispensing", () => {
  it("opens the prescription at its OWN url, keyed by the Rx number", async () => {
    const user = userEvent.setup();
    renderScreen();

    await user.type(screen.getByLabelText("Prescription number"), "RX-2026-033110");
    await user.click(screen.getByRole("button", { name: "Search" }));
    await user.click(await screen.findByRole("button", { name: "Open" }));

    // Dispensing moved off this screen and onto the prescription's own page: the search is how you reach the
    // task, not the task. The URL is keyed by "RX-2026-033110" — not "RX-33110" (the internal id) and not a
    // uuid — because that is the reference printed on the paper in the patient's hand, so a pharmacist who
    // reloads, or who sends the link to a colleague, lands on the prescription rather than an empty search.
    //
    // What the page itself renders is asserted in prescription-page.test.tsx.
    await waitFor(() => {
      expect(screen.getByTestId("path")).toHaveTextContent("/pharmacy/rx/RX-2026-033110");
    });
  });

  it("names the prescriber in the results, never the word 'Prescriber'", async () => {
    const user = userEvent.setup();
    renderScreen();

    await user.type(screen.getByLabelText("Prescription number"), "RX-2026-033110");
    await user.click(screen.getByRole("button", { name: "Search" }));

    // The queue rendered the literal words "Prescriber" and "Medication" where the values belong — the name
    // of a field printed where its value should be, which reads as data and cannot be told apart from it.
    expect(await screen.findByText("Dr. N. Fahmy")).toBeInTheDocument();
    expect(screen.queryByText(/^Medication$/)).toBeNull();
  });
});

describe("accessibility", () => {
  it("has no axe violations", async () => {
    const { container } = renderScreen();
    await screen.findByText(/Enter a prescription number/);
    expect(await axe(container)).toHaveNoViolations();
  });
});

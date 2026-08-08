import { describe, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { AppRouter } from "../src/routing/AppRouter";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { seedSession } from "./helpers";

/**
 * The encounter workspace's Prescriptions / Labs / Imaging tables.
 *
 * ============================================================================================================
 * WHY THIS SUITE EXISTS
 * ============================================================================================================
 * A Timeline column was added to these two tables and did not appear — and the reason was not the column. Both
 * tabs filter the clinician's own lists to the patient in front of them with
 * `r.beneficiary.id === encounter.patientId`, and `DevApiClient.getEncounter` echoed the ENCOUNTER id straight
 * back as `patientId`. That comparison was an encounter id against a beneficiary id: never equal, so both
 * tables rendered their empty state and EVERY column on them was invisible — in the demo build and in the
 * route-level axe sweep, which walks these routes and had nothing to look at.
 *
 * The live client never had the bug (`patientId: e.beneficiaryId`), which is exactly why it survived: the
 * fixture disagreed with the thing it stands in for, so the only build that could show the defect was the one
 * nobody tests against.
 *
 * These assert the tables RENDER and carry their columns. A test that only checked "the column is in the
 * array" would have passed throughout.
 */
function renderWorkspace() {
  seedSession("doctor");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <MemoryRouter
        initialEntries={["/clinician/encounter?encounter=ENC-2026-000231"]}
        future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
      >
        <AppRouter />
      </MemoryRouter>
    </AppProviders>,
  );
}

async function openTab(name: RegExp) {
  const user = userEvent.setup();
  renderWorkspace();
  await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });
  await user.click(await screen.findByRole("tab", { name }));
  return user;
}

describe("Encounter picker", () => {
  it("carries a Timeline column on the visit list", async () => {
    seedSession("doctor");
    render(
      <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
        <MemoryRouter
          initialEntries={["/clinician/encounter"]}
          future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
        >
          <AppRouter />
        </MemoryRouter>
      </AppProviders>,
    );

    // Three rows for Amal — the picker lists ENCOUNTERS, and is right to; only My Patients folds by person.
    await screen.findAllByText("Amal Hassan", {}, { timeout: 5000 });
    const table = document.querySelector("table")!;
    const headers = [...table.querySelectorAll("thead th")].map((th) => th.textContent?.trim());
    // The ENCOUNTER reference leads: this board lists visits, not people, so it is what tells two rows of
    // the same patient apart.
    expect(headers).toEqual(["Encounter", "Patient", "Started", "State", "Timeline"]);
    expect(within(table as HTMLElement).getAllByRole("button", { name: /timeline/i }).length)
      .toBe(table.querySelectorAll("tbody tr").length);
  });

  it("opens one visit's whole episode without also opening the encounter", async () => {
    const user = userEvent.setup();
    seedSession("doctor");
    render(
      <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
        <MemoryRouter
          initialEntries={["/clinician/encounter"]}
          future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
        >
          <AppRouter />
        </MemoryRouter>
      </AppProviders>,
    );

    await screen.findAllByText("Amal Hassan", {}, { timeout: 5000 });
    const table = document.querySelector("table") as HTMLElement;
    await user.click(within(table).getAllByRole("button", { name: /timeline/i })[0]);

    const dialog = await screen.findByRole("dialog");
    // The WHOLE visit — no reference filter, so the order and prescription steps are in it too. Filtering on
    // the encounter's own key would have stripped exactly those, which is most of what happened.
    expect(within(dialog).getByText(/visit started/i)).toBeInTheDocument();
    // TWO of them — the visit raised two orders, and an unfiltered episode shows both.
    expect(within(dialog).getAllByText(/investigation ordered/i)).toHaveLength(2);
    expect(within(dialog).getByText(/prescription written/i)).toBeInTheDocument();

    // A row here opens the encounter on click; the button must not do that as well. Counted off the raw DOM
    // because Radix marks a covered dialog aria-hidden.
    expect(document.querySelectorAll('[role="dialog"]')).toHaveLength(1);
    expect(screen.queryByRole("tab", { name: /soap note/i })).not.toBeInTheDocument();
  });
});

describe("Encounter picker", () => {
  it("offers a When filter over the visit list", async () => {
    seedSession("doctor");
    render(
      <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
        <MemoryRouter
          initialEntries={["/clinician/encounter"]}
          future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
        >
          <AppRouter />
        </MemoryRouter>
      </AppProviders>,
    );

    await screen.findAllByText("Amal Hassan", {}, { timeout: 5000 });
    expect(screen.getByRole("button", { name: /last 30 days/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /custom date/i })).toBeInTheDocument();
  });
});

describe("Encounter workspace tabs", () => {
  it("offers NO date filter on the prescriptions tab", async () => {
    // 31.1 — reversed deliberately. The tab has already narrowed this list to one patient, and the table
    // sits directly above the composer the doctor came here to type into; a period chip group plus eight
    // rows of history pushed that composer below the fold to answer a question the tab had answered twice.
    // The full rule, asserted across all four tabs, lives in `encounter-transaction-actions`.
    await openTab(/prescriptions/i);
    expect(screen.queryByRole("button", { name: /last 30 days/i })).toBeNull();
  });

  it("shows the patient's prescriptions, with a Timeline column", async () => {
    await openTab(/prescriptions/i);

    const table = document.querySelector("table")!;
    expect(table, "the tab rendered its empty state instead of a table").toBeTruthy();
    const headers = [...table.querySelectorAll("thead th")].map((th) => th.textContent?.trim());
    expect(headers).toContain("Timeline");
    expect(table.querySelectorAll("tbody tr").length).toBeGreaterThan(0);
    expect(within(table as HTMLElement).getAllByRole("button", { name: /timeline/i }).length).toBeGreaterThan(0);
  });

  it("shows the patient's lab orders, with a Timeline column", async () => {
    await openTab(/labs/i);

    const table = document.querySelector("table")!;
    expect(table, "the tab rendered its empty state instead of a table").toBeTruthy();
    const headers = [...table.querySelectorAll("thead th")].map((th) => th.textContent?.trim());
    expect(headers).toContain("Timeline");
    expect(table.querySelectorAll("tbody tr").length).toBeGreaterThan(0);
  });

  it("opens the visit's own timeline from the workspace header", async () => {
    const user = await openTab(/prescriptions/i);
    await user.click(screen.getByRole("button", { name: /visit timeline/i }));

    const dialog = await screen.findByRole("dialog");
    // The whole visit, not one transaction — so steps from more than one reference are in it.
    expect(within(dialog).getByText(/visit started/i)).toBeInTheDocument();
    expect(within(dialog).getByText(/medicine dispensed/i)).toBeInTheDocument();
  });

  it("narrows a row's timeline to that transaction alone", async () => {
    const user = await openTab(/prescriptions/i);
    const table = document.querySelector("table") as HTMLElement;
    await user.click(within(table).getAllByRole("button", { name: /timeline/i })[0]);

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/prescription written/i)).toBeInTheDocument();
    // The visit's OTHER steps must not be here — the fixture spans two orders and a prescription precisely so
    // that a filter which silently passed everything through would fail this.
    expect(within(dialog).queryByText(/visit started/i)).not.toBeInTheDocument();
    expect(within(dialog).queryByText(/investigation ordered/i)).not.toBeInTheDocument();
  });
});

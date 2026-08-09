import { describe, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { AppRouter } from "../src/routing/AppRouter";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { seedSession } from "./helpers";

/**
 * 29.2 / 29.3 — the OP Procedures tab, and the Radiology rename's user-facing half.
 *
 * <p>The Procedures tab uses the SHARED composer (`InvestigationsTab`, parameterised by order type) rather
 * than a third copy — design 45 §2, invariant 2. What these tests can actually pin is the consequence of
 * that: the tab exists, it is headed for procedures rather than inheriting the Lab labels, and the History
 * pane shows procedures separately from labs while remaining the same gated section.</p>
 */
function renderEncounter() {
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

/**
 * The one VISIBLE tab panel.
 *
 * <p>`Tabs` mounts every pane with `forceMount` and hides the inactive ones with `hidden` — deliberately, so
 * SSR and loading never blank a panel. That means `screen.getByText` sees the contents of ALL panes at once,
 * and an unscoped assertion here is either ambiguous or, worse, quietly satisfied by a hidden pane. Every
 * assertion about what a pane shows has to be scoped to the one the user is looking at.</p>
 */
function visiblePane(): HTMLElement {
  const panes = Array.from(document.querySelectorAll<HTMLElement>(".mrs-tabpane"))
    .filter((el) => !el.hasAttribute("hidden"));
  // The encounter's own pane wraps the History pane, so the INNERMOST visible one is the one under test.
  return panes[panes.length - 1];
}

async function openTab(name: RegExp) {
  const user = userEvent.setup();
  renderEncounter();
  await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });
  // The ENCOUNTER's own tablist is the first on the page; the History pane renders a nested tablist whose
  // labels deliberately overlap ("OP Procedures" appears in both). Scoping keeps this helper unambiguous.
  const outer = (await screen.findAllByRole("tablist"))[0];
  await user.click(await within(outer).findByRole("tab", { name }));
  return user;
}

describe("29.3 — OP Procedures in the encounter", () => {
  it("offers an OP Procedures tab beside Prescriptions, Labs and Radiology", async () => {
    renderEncounter();
    await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });

    expect(await screen.findByRole("tab", { name: /prescriptions/i })).toBeInTheDocument();
    expect(await screen.findByRole("tab", { name: /^labs$/i })).toBeInTheDocument();
    expect(await screen.findByRole("tab", { name: /^radiology$/i })).toBeInTheDocument();
    expect(await screen.findByRole("tab", { name: /op procedures/i })).toBeInTheDocument();
  });

  it("heads the Procedures tab for procedures rather than inheriting the Lab labels", async () => {
    // The failure this catches is silent: a fourth order type added to a chain of ternaries falls through to
    // the Lab arm, and the tab renders correctly in every respect except that it says "Lab orders".
    await openTab(/op procedures/i);

    expect(await screen.findByText(/procedures ordered for this patient/i)).toBeInTheDocument();
    expect(screen.queryByText(/labs for this patient/i)).not.toBeInTheDocument();
  });

  it("no user-facing string in the encounter says Imaging", async () => {
    // 29.1 acceptance (design 45 §1): "no user-facing string says Imaging". The tab, its heading, its empty
    // state and its compose button were all still spelled the old way after the identifier rename.
    const { container } = renderEncounter();
    await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });

    expect(screen.getByRole("tab", { name: /radiology/i })).toBeInTheDocument();
    expect(container.textContent).not.toMatch(/imaging/i);
  });
});

describe("29.3 — OP Procedures in the History tab", () => {
  it("shows procedures in their own pane, separate from investigations", async () => {
    const user = await openTab(/history/i);

    const lists = await screen.findAllByRole("tablist");
    const inner = lists[lists.length - 1];
    await user.click(await within(inner).findByRole("tab", { name: /op procedures/i }));

    // The procedure fixture, and NOT the lab rows — the pane filters the same authorised section.
    const pane = visiblePane();
    expect(await within(pane).findByText(/therapeutic exercise/i)).toBeInTheDocument();
    expect(within(pane).queryByText(/haematology/i)).not.toBeInTheDocument();
  });

  it("leaves labs and radiology in the investigations pane", async () => {
    const user = await openTab(/history/i);

    const lists = await screen.findAllByRole("tablist");
    const inner = lists[lists.length - 1];
    await user.click(await within(inner).findByRole("tab", { name: /investigations/i }));

    const pane = visiblePane();
    expect(await within(pane).findByText(/haematology/i)).toBeInTheDocument();
    // A procedure is not an "investigation" to a doctor reading this list, even though it is one to the
    // ordering system. The split is the point.
    expect(within(pane).queryByText(/therapeutic exercise/i)).not.toBeInTheDocument();
  });

  it("keeps the sensitivity gate intact in the split panes", async () => {
    // Splitting rows must not weaken what SectionView does with them. The restricted serology row still
    // renders as existence-only, with no result value — design 37 §6, and the reason the pane filters ROWS
    // rather than becoming a section of its own.
    const user = await openTab(/history/i);

    const lists = await screen.findAllByRole("tablist");
    const inner = lists[lists.length - 1];
    await user.click(await within(inner).findByRole("tab", { name: /investigations/i }));

    const pane = visiblePane();
    expect(await within(pane).findByText(/serology/i)).toBeInTheDocument();
    expect(within(pane).getByText(/Hb 11\.2/i)).toBeInTheDocument();   // an unrestricted result still shows
    // The restricted serology row carries NO value — the owning service never sent one (design 37 §6).
    expect(within(pane).queryByText(/anti-hcv|viral load/i)).not.toBeInTheDocument();
  });
});

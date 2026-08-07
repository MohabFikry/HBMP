import { describe, expect, it } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { AppProviders } from "../src/App";
import { AppRouter } from "../src/routing/AppRouter";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { renderApp, seedSession } from "./helpers";

/**
 * The way OUT of the encounter workspace.
 *
 * ============================================================================================================
 * WHY THIS SUITE EXISTS
 * ============================================================================================================
 * The workspace is never somewhere you navigate TO from a menu — it is opened FOR a visit, from the day board,
 * from a patient's file, or from the encounters list on "My Patients". So the way back is not a convenience,
 * it is the second half of every one of those journeys. It broke in two different ways at once, and both were
 * invisible from the workspace itself:
 *
 *  - "Start visit" on the day board navigated with NO origin. `useBackTarget` renders nothing when there is
 *    neither a `from` nor history behind the entry, so the single most-used door into the workspace led to a
 *    screen with no way back at all: a doctor who finished a consultation had to reach for the nav rail to
 *    return to their own day.
 *  - The picker's `setSearchParams` pushed a fresh history entry, and a fresh entry carries no `location.state`
 *    unless one is given. Arriving from a patient's file and then choosing a visit therefore dropped the `from`
 *    that got you there.
 *
 * ============================================================================================================
 * "BACK" IS TWO GUARANTEES, AND ONLY ONE OF THEM IS AUTOMATIC
 * ============================================================================================================
 * WHERE it goes is `state.from`, read by `useBackTarget`, which prefers it over `navigate(-1)` — history alone
 * is wrong after a redirect and empty on a pasted deep link. WHAT you come back to is the worklist's
 * `persistKey`, which restores its own search, filters and page on re-mount.
 *
 * The pair is the point, which is why the first test asserts both. Landing on an unfiltered page 1 of the
 * right screen is technically "back" and still loses the row the clinician was working on.
 */

/** The panel's own search box — the app shell has a global one, so a bare `searchbox` role matches two. */
function panelSearch() {
  return screen.getByPlaceholderText(/name, branch or encounter/i) as HTMLInputElement;
}

function Where() {
  const loc = useLocation();
  return <span data-testid="where">{`${loc.pathname}${loc.search}`}</span>;
}

/** `renderApp`, but able to plant an origin on the first entry — which is what a deep link cannot have. */
function renderAppAt(entry: { pathname: string; search?: string; state?: unknown }) {
  seedSession("doctor");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <MemoryRouter initialEntries={[entry]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <AppRouter />
        <Where />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("Leaving the encounter workspace", () => {
  it("returns to My Patients with the search still applied", async () => {
    const user = userEvent.setup();
    renderApp("/clinician/patients", "doctor");

    await screen.findByText("Amal Hassan", {}, { timeout: 5000 });
    // Narrow the panel. This is the state that has to survive the round trip.
    await user.type(panelSearch(), "amal");
    await waitFor(() => expect(screen.queryByText("Yusuf Haddad")).not.toBeInTheDocument());

    await user.click(screen.getByRole("button", { name: /encounters \(3\)/i }));
    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getAllByRole("button", { name: /open this encounter/i })[1]);

    await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });
    await user.click(screen.getByRole("button", { name: /^back$/i }));

    await screen.findByRole("heading", { name: /my patients/i }, { timeout: 5000 });
    expect(panelSearch().value).toBe("amal");
    // Restoring the typed text but not re-applying it would look identical in the input and be a different bug.
    expect(screen.queryByText("Yusuf Haddad")).not.toBeInTheDocument();
  });

  it("offers a way back after Start visit on the day board", async () => {
    const user = userEvent.setup();
    renderApp("/clinician/visits", "doctor");

    // Every row carries a Start visit button now — the ones for patients who have not arrived are DISABLED
    // rather than absent, so the board is asked for the one that can actually be pressed.
    const buttons = await screen.findAllByRole("button", { name: /start visit/i }, { timeout: 5000 });
    const ready = buttons.find((b) => !(b as HTMLButtonElement).disabled)!;
    expect(ready).toBeDefined();
    await user.click(ready);
    await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });

    // The regression this suite was written for: there was no such button here at all.
    await user.click(screen.getByRole("button", { name: /^back$/i }));
    expect(await screen.findByRole("heading", { name: /my visits/i }, { timeout: 5000 })).toBeInTheDocument();
  });

  it("still offers a way off the workspace after a reload", async () => {
    const user = userEvent.setup();
    // A RELOAD destroys both origins at once: `location.state` does not survive it and react-router's history
    // index resets to 0. This is the case that stranded a clinician on a screen that is not in the nav rail —
    // and it is the most ordinary thing in the world to do, so "there is no way back" cannot be the answer.
    renderAppAt({ pathname: "/clinician/encounter", search: "?encounter=ENC-2026-000231" });

    await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });
    // Labelled for where it GOES, not "Back": nothing was left behind to go back to, and a control that says
    // "Back" and lands somewhere the user has never been is lying about what it did. Only on an OPEN
    // encounter — the picker gets none, see above.
    const out = await screen.findByRole("button", { name: /my patients/i });
    await user.click(out);

    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/clinician/patients"));
  });

  it("offers no invented way off the PICKER, which the nav rail already reaches", async () => {
    // The fallback exists so a RELOADED workspace is not a dead end. The picker is a list on the same route,
    // reachable from the rail, and there the fallback rendered a "My patients" control in the header that
    // duplicated the rail entry two inches to its left.
    renderAppAt({ pathname: "/clinician/encounter" });

    await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });
    expect(document.querySelector(".pagehead-back")).toBeNull();
  });

  it("keeps the origin when a visit is chosen in the picker", async () => {
    const user = userEvent.setup();
    const beneficiaryId = "aaaaaaaa-0000-0000-0000-000000000231";
    // The patient file's "Start encounter" lands here: a PERSON, not a visit, so the picker opens narrowed.
    renderAppAt({
      pathname: "/clinician/encounter",
      search: `?beneficiaryId=${beneficiaryId}`,
      state: { from: `/patients/${beneficiaryId}` },
    });

    // Three rows, not one: the picker lists ENCOUNTERS and is right to — only the panel folds by person.
    const visits = await screen.findAllByText("Amal Hassan", {}, { timeout: 5000 });
    await user.click(visits[0]);
    await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });
    await user.click(screen.getByRole("button", { name: /^back$/i }));

    // The FILE, not the picker. `setSearchParams` drops state unless it is re-stated, and the fallback to
    // navigate(-1) would land one entry back — on the picker the clinician had just left.
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent(`/patients/${beneficiaryId}`));
  });
});

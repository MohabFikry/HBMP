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
 * 29.2 (design 45 §2) — the procedure TYPE and the sessions field, in the composer.
 *
 * <p>`encounter-procedures.test.tsx` proves the tab exists and is spelled for procedures. It cannot see that
 * the composer inside it offered no way to choose a type — which orders-service REQUIRES, refusing every
 * typeless Procedure line 422. These tests drive the fields themselves.</p>
 *
 * <p><b>The assertion that matters most is the flag one.</b> Sessions must follow `isSessionBased` off the
 * master-data row, never the type's name: dialysis and rehabilitation are session-based too, and
 * `if (type === 'Physiotherapy')` would pass a Physiotherapy-only test while being wrong for both.</p>
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

function visiblePane(): HTMLElement {
  const panes = Array.from(document.querySelectorAll<HTMLElement>(".mrs-tabpane"))
    .filter((el) => !el.hasAttribute("hidden"));
  return panes[panes.length - 1];
}

async function openTab(name: RegExp) {
  const user = userEvent.setup();
  renderEncounter();
  await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });
  const outer = (await screen.findAllByRole("tablist"))[0];
  await user.click(await within(outer).findByRole("tab", { name }));
  return user;
}

describe("29.2 — the OP Procedures composer offers a type", () => {
  it("shows a procedure type field on the Procedures tab", async () => {
    await openTab(/op procedures/i);

    const pane = visiblePane();
    expect(await within(pane).findByLabelText(/procedure type|نوع الإجراء/i)).toBeInTheDocument();
  });

  it("offers the master-data kinds rather than a hardcoded list", async () => {
    await openTab(/op procedures/i);

    const pane = visiblePane();
    const select = await within(pane).findByLabelText(/procedure type|نوع الإجراء/i);
    // Seeded kinds from masterdata 0015/0017 — both session-based and not.
    expect(within(select).getByRole("option", { name: /physiotherapy/i })).toBeInTheDocument();
    expect(within(select).getByRole("option", { name: /dialysis/i })).toBeInTheDocument();
    expect(within(select).getByRole("option", { name: /minor surgery/i })).toBeInTheDocument();
  });

  it("does NOT show a procedure type field on the Labs tab", async () => {
    // A type on a lab line is refused by orders-service rather than ignored, so offering one here would
    // compose an order the write path rejects.
    await openTab(/^labs$/i);

    const pane = visiblePane();
    expect(within(pane).queryByLabelText(/procedure type|نوع الإجراء/i)).not.toBeInTheDocument();
  });
});

describe("29.2 — sessions follow the flag, not the name", () => {
  it("reveals a sessions field for a session-based type", async () => {
    const user = await openTab(/op procedures/i);

    const pane = visiblePane();
    const select = await within(pane).findByLabelText(/procedure type|نوع الإجراء/i);
    await user.selectOptions(select, "Physiotherapy");

    expect(await within(pane).findByLabelText(/sessions|عدد الجلسات/i)).toBeInTheDocument();
  });

  it("reveals it for Dialysis too — the case a name check would get wrong", async () => {
    // This is the test `if (type === 'Physiotherapy')` fails. Dialysis is session-based in the same master
    // data and must behave identically without a line of code naming it.
    const user = await openTab(/op procedures/i);

    const pane = visiblePane();
    const select = await within(pane).findByLabelText(/procedure type|نوع الإجراء/i);
    await user.selectOptions(select, "Dialysis");

    expect(await within(pane).findByLabelText(/sessions|عدد الجلسات/i)).toBeInTheDocument();
  });

  it("hides the sessions field for a type that is not session-based", async () => {
    const user = await openTab(/op procedures/i);

    const pane = visiblePane();
    const select = await within(pane).findByLabelText(/procedure type|نوع الإجراء/i);
    await user.selectOptions(select, "MinorSurgery");

    expect(within(pane).queryByLabelText(/sessions|عدد الجلسات/i)).not.toBeInTheDocument();
  });

  it("starts the sessions field at the type's default", async () => {
    // Physiotherapy seeds default_sessions = 6. A blank field would make the commonest case a typing task.
    const user = await openTab(/op procedures/i);

    const pane = visiblePane();
    await user.selectOptions(await within(pane).findByLabelText(/procedure type|نوع الإجراء/i), "Physiotherapy");

    expect(await within(pane).findByLabelText(/sessions|عدد الجلسات/i)).toHaveValue(6);
  });
});

/**
 * 31.1 — the COURSE, at the level it is decided.
 *
 * <p>The kind and the session count were on each LINE, so a two-item course could carry two kinds and two
 * session counts — not a course any centre can deliver — and there was nowhere at all to record "three of
 * these at each attendance", because the quantity slot was already spent on the session count. The tests
 * above still hold: the session field follows the type's FLAG and not its name. What changed is where the
 * type is asked for, and what the line's quantity now means.</p>
 */
describe("31.1 — one kind and one session count for the whole order", () => {
  it("asks for the procedure type ONCE, above the lines", async () => {
    await openTab(/op procedures/i);

    // Exactly one. A second would mean the per-line control is still there, which is the defect: two lines
    // would then be able to disagree about what kind of course this is.
    const pane = visiblePane();
    expect((await within(pane).findAllByLabelText(/procedure type|نوع الإجراء/i)).length).toBe(1);
  });

  it("labels the line quantity as PER SESSION once a session-based course is chosen", async () => {
    // The two used to be one field. Separating them is what makes "three of these at each attendance"
    // expressible at all; the server derives the metered total, so consume and the centre's queue are
    // untouched.
    const user = await openTab(/op procedures/i);

    const pane = visiblePane();
    await user.selectOptions(
      await within(pane).findByLabelText(/procedure type|نوع الإجراء/i), "Physiotherapy");

    expect(await within(pane).findByLabelText(/quantity per session|الكمية لكل جلسة/i)).toBeInTheDocument();
  });
});

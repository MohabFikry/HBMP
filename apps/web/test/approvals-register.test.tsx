import { describe, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ApprovalsRegister } from "../src/screens/ApprovalsRegister";

/**
 * Every authorization, for the approval team (ADR-0034).
 *
 * <p>Two things are pinned. The register is NOT the inbox — a queue that fills with a few hundred dispenses a
 * day is a queue people stop reading, so the worklist keeps its default and this screen asks for the other
 * thing deliberately. And a substitution shows what was WRITTEN beside what was DELIVERED: the whole reason
 * the authorization is a separate document is that a counter must not be able to erase what a prescriber
 * chose, and a screen that showed only the delivered molecule would undo that in the one place it matters.</p>
 */

function renderRegister(api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <ApprovalsRegister />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("the authorization register", () => {
  it("opens on what was delivered, not on the decision queue", async () => {
    renderRegister();

    // AUTH-7101 is a dispense; AUTH-9001 is a request awaiting review. The default view is the register.
    expect(await screen.findByText("AUTH-7101")).toBeInTheDocument();
    expect(screen.queryByText("AUTH-9001")).toBeNull();
  });

  it("says which kind each row is", async () => {
    renderRegister();
    await screen.findByText("AUTH-7101");

    // A reviewer needs to know whether they are looking at a question or a receipt before anything else on
    // the row means something.
    // role="grid", not "table": the rows are selectable, and `aria-selected` on a <tr> inside an implicit
    // table role is invalid ARIA — the attribute is ignored and a screen-reader user is never told which row
    // is current.
    const table = screen.getByRole("grid");
    expect(within(table).getByRole("columnheader", { name: "Kind" })).toBeInTheDocument();
  });

  it("names the prescription or order each authorization was issued against", async () => {
    renderRegister();
    await screen.findByText("AUTH-7101");

    // The only string on the row a human can look up. An authorization with no trace of what it was issued
    // against is a number with nothing behind it.
    expect(screen.getByText("RX-2026-000410")).toBeInTheDocument();
    expect(screen.getByText("ORD-2026-055012")).toBeInTheDocument();
  });

  it("can be switched to the decisions still waiting", async () => {
    const user = userEvent.setup();
    renderRegister();
    await screen.findByText("AUTH-7101");

    await user.click(screen.getByRole("radio", { name: "Awaiting decision" }));

    expect(await screen.findByText("AUTH-9001")).toBeInTheDocument();
    expect(screen.queryByText("AUTH-7101")).toBeNull();
  });
});

describe("what was delivered", () => {
  it("shows the written medicine beside the delivered one on a substitution", async () => {
    const user = userEvent.setup();
    renderRegister();
    await screen.findByText("AUTH-7101");

    await user.click(screen.getByText("AUTH-7101"));

    // BOTH. This is the invariant the whole design exists for: the prescription still says what the
    // prescriber wrote, and the authorization is where the difference is recorded.
    expect(await screen.findByText("Augmentin 1g 14 f.c. tabs")).toBeInTheDocument();
    expect(screen.getByText("Amoxicillin+Clavulanic acid 1g tabs")).toBeInTheDocument();
    expect(screen.getByText(/out of stock this morning/i)).toBeInTheDocument();
  });

  it("says in words that the prescription was not edited", async () => {
    const user = userEvent.setup();
    renderRegister();
    await screen.findByText("AUTH-7101");

    await user.click(screen.getByText("AUTH-7101"));

    // Shown only where a substitution actually happened. A reviewer reading a swapped molecule needs to know
    // it is a record of a counter's act, not an amendment to a doctor's decision.
    expect(await screen.findByText(/clinical record is not edited by a counter/i)).toBeInTheDocument();
  });

  it("has no serious or critical a11y violations", async () => {
    const { container } = renderRegister();
    await screen.findByText("AUTH-7101");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

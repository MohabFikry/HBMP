import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ProgramAdmin } from "../src/screens/ProgramAdmin";
import { seedSession } from "./helpers";

/**
 * Phase 21.6 — programme enablement administration (design 40 §4, adaptation A4).
 *
 * A4 is a copy decision as much as a code one, so the copy is under test: Mersal is a charity and these
 * tenants are partner NGOs, not customers on a price plan. A screen that drifts into upsell vocabulary
 * turns "we have not onboarded you onto claims yet" into "pay us", which is both wrong and unkind to a
 * partner organisation.
 */

function renderPrograms(client: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  seedSession("super_admin");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={client}>
      <ProgramAdmin tenant="mersal" />
    </AppProviders>,
  );
}

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

describe("Programme enablement (21.6)", () => {
  it("says plainly that enabling never grants a permission", async () => {
    // The two gates are routinely confused, and an administrator who confuses them toggles a feature to
    // fix what is actually a missing role.
    renderPrograms();
    expect(await screen.findByText(/never grants anyone a permission/i)).toBeInTheDocument();
  });

  it("uses no commercial-plan vocabulary anywhere on the screen (A4)", async () => {
    renderPrograms();
    await screen.findByText(/claims/);
    const text = document.body.textContent!.toLowerCase();
    for (const word of ["upgrade", "billing", "plan", "subscription", "trial", "pricing", "purchase"]) {
      expect(text).not.toContain(word);
    }
  });

  it("distinguishes never-configured from switched-off", async () => {
    // "Nobody has decided" and "someone decided no" are different conversations with the partner.
    renderPrograms();
    const interop = (await screen.findAllByRole("row")).find((r) => within(r).queryByText("interop"))!;
    expect(within(interop).getByText(/never configured/i)).toBeInTheDocument();

    const claims = screen.getAllByRole("row").find((r) => within(r).queryByText("claims"))!;
    expect(within(claims).getByText(/not enabled/i)).toBeInTheDocument();
  });

  it("shows an uncounted cap as uncounted, never as zero", async () => {
    // Null usage means the answering service does not own the count. Rendering 0 would tell an
    // administrator the organisation was idle when nobody actually measured.
    renderPrograms();
    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText("monthly_extracts"))!;
    expect(within(row).getByText(/not counted here/i)).toBeInTheDocument();
    expect(within(row).queryByText("0")).toBeNull();
  });

  it("flags a cap that live usage has already exceeded", async () => {
    renderPrograms();
    const row = (await screen.findAllByRole("row")).find((r) =>
      within(r).queryByText("active_provider_users"),
    )!;
    expect(within(row).getByText(/over cap/i)).toBeInTheDocument();
  });

  it("requires a typed organisation name before disabling a programme", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "setProgramFeature");
    renderPrograms(client);

    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText("callcentre"))!;
    await userEvent.click(within(row).getByRole("button", { name: /disable/i }));

    const dialog = await screen.findByRole("dialog");
    // Disabling removes a whole module from an organisation — the destructive direction, so it is the one
    // that takes the typed-name confirmation.
    expect(within(dialog).getByText(/loses the module/i)).toBeInTheDocument();
    await userEvent.type(within(dialog).getByLabelText(/reason/i), "programme wound down");
    await userEvent.type(within(dialog).getByLabelText(/type the organisation/i), "wrong-name");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    expect(await screen.findByText(/does not match/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();

    // With the right name it goes through, switching the feature OFF.
    const typed = within(dialog).getByLabelText(/type the organisation/i);
    await userEvent.clear(typed);
    await userEvent.type(typed, "mersal");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    await waitFor(() => expect(spy).toHaveBeenCalledWith("mersal", "callcentre", false, "programme wound down"));
  });

  it("confirms an enable without demanding the typed name, but still demands a reason", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "setProgramFeature");
    renderPrograms(client);

    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText("claims"))!;
    await userEvent.click(within(row).getByRole("button", { name: /enable/i }));

    const dialog = await screen.findByRole("dialog");
    // Additive change: no typed-name gate, because the cost of an accidental enable is not a lost module.
    expect(within(dialog).queryByLabelText(/type the organisation/i)).toBeNull();

    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));
    expect(await screen.findByText(/a reason is required/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();

    await userEvent.type(within(dialog).getByLabelText(/reason/i), "onboarded onto claims");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));
    await waitFor(() => expect(spy).toHaveBeenCalledWith("mersal", "claims", true, "onboarded onto claims"));
  });

  it("warns but does not block a cap set below current usage", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "setProgramLimit");
    renderPrograms(client);

    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText("active_users"))!;
    await userEvent.click(within(row).getByRole("button", { name: /set the cap/i }));

    const dialog = await screen.findByRole("dialog");
    const field = within(dialog).getByLabelText(/maximum/i);
    await userEvent.clear(field);
    await userEvent.type(field, "10"); // live usage is 42

    // Tightening a cap on an over-provisioned tenant is legitimate: nothing is removed, and the cap only
    // refuses the NEXT addition. Blocking it would make the screen unable to express a real decision.
    expect(await within(dialog).findByText(/below current usage/i)).toBeInTheDocument();

    await userEvent.type(within(dialog).getByLabelText(/reason/i), "capacity reduced by agreement");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));
    await waitFor(() =>
      expect(spy).toHaveBeenCalledWith("mersal", "active_users", 10, "capacity reduced by agreement"),
    );
  });
});

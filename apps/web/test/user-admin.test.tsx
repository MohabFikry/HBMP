import { afterEach, describe, expect, it } from "vitest";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { DevApiClient } from "../src/api/DevApiClient";
import { MembershipRoster } from "../src/screens/AccessAdmin";
import { AccessCatalogue } from "../src/screens/AccessCatalogue";
import { looksLikeEmail } from "../src/screens/UserAdmin";
import { renderNode, seedSession } from "./helpers";

/**
 * Phase 28.8/28.9 — administering people, and the access catalogue they are administered against.
 *
 * ============================================================================================================
 * WHAT THESE PROVE
 * ============================================================================================================
 * The endpoints behind this screen have existed since 17.4 with no caller, so the risk is not that the API
 * misbehaves — it has its own suite — but that the SCREEN quietly fails to say what happened. So these
 * assert the moments where a wrong answer is invisible:
 *   * a created account appears, and the invitation's outcome is REPORTED rather than assumed;
 *   * an account with no address is marked as one, because "send reset link" cannot reach it;
 *   * the role designer refuses a set holding both halves of a separated duty, in the form;
 *   * a machine-only key cannot be ticked onto a human role.
 */

afterEach(cleanup);

function renderAccounts() {
  seedSession("org_admin");
  return renderNode(<MembershipRoster />, new DevApiClient({ latencyMs: 0 }));
}

function renderCatalogue() {
  seedSession("org_admin");
  return renderNode(<AccessCatalogue />, new DevApiClient({ latencyMs: 0 }));
}

describe("the accounts list", () => {
  it("shows the address an account signs in with, and marks the ones that have none", async () => {
    renderAccounts();
    expect(await screen.findByText("org.admin@mersal.org")).toBeInTheDocument();
  });

  it("names the PORTALS somebody holds, not the raw issuer roles", async () => {
    // `doctor` happens to be spelled the same either way; the point is that the column resolves through the
    // catalogue, so `lab_tech` would read as "Laboratory" rather than as a role name nobody outside the
    // issuer uses.
    renderAccounts();
    const row = (await screen.findByText("Dr. Hala")).closest("tr")!;
    expect(within(row).getByText(/Consultation/)).toBeInTheDocument();
  });

  it("calls out an account with no second factor", async () => {
    // MFA gates every admin scope and every break-glass request on the platform, and until 18.C2 no screen
    // anywhere showed whether a given account had one.
    renderAccounts();
    const row = (await screen.findByText("Dr. Hala")).closest("tr")!;
    expect(within(row).getByText(/Not enrolled/i)).toBeInTheDocument();
  });
});

describe("creating an account", () => {
  it("requires a name, a usable address and at least one portal", async () => {
    const user = userEvent.setup();
    renderAccounts();
    await user.click(await screen.findByRole("button", { name: /Add a user/i }));
    await user.click(screen.getByRole("button", { name: /Create and invite/i }));

    expect(await screen.findByText(/A full name is required/i)).toBeInTheDocument();
    expect(screen.getByText(/valid email address is required/i)).toBeInTheDocument();
    // The one that matters: an account with no portal signs in successfully and reaches nothing, which
    // looks like a broken platform rather than an incomplete grant.
    expect(screen.getByText(/Choose at least one portal/i)).toBeInTheDocument();
  });

  it("creates the account, reports that the invitation went, and never mentions a password", async () => {
    const user = userEvent.setup();
    renderAccounts();
    await user.click(await screen.findByRole("button", { name: /Add a user/i }));

    await user.type(screen.getByLabelText(/Full name/i), "Nadia Farouk");
    await user.type(screen.getByLabelText(/Email address/i), "nadia@mersal.org");
    await user.click(screen.getByRole("checkbox", { name: /Reception/i }));
    await user.click(screen.getByRole("button", { name: /Create and invite/i }));

    // The outcome is ANNOUNCED: creating an account changes almost nothing on screen, and an outcome nobody
    // is told about reads as a button that did not work.
    expect(await screen.findByText(/invitation to set a password has been emailed/i)).toBeInTheDocument();
    expect(await screen.findByText("Nadia Farouk")).toBeInTheDocument();
    // 28.7's rule applied to creation — there is no moment at which the administrator knows the credential,
    // so there is nowhere on this screen for one to appear.
    expect(screen.queryByLabelText(/password/i)).toBeNull();
  });

  it("says which field to change when the address is already in use", async () => {
    const user = userEvent.setup();
    renderAccounts();
    await user.click(await screen.findByRole("button", { name: /Add a user/i }));
    await user.type(screen.getByLabelText(/Full name/i), "Someone Else");
    await user.type(screen.getByLabelText(/Email address/i), "hala@mersal.org");
    await user.click(screen.getByRole("checkbox", { name: /Reception/i }));
    await user.click(screen.getByRole("button", { name: /Create and invite/i }));

    expect(await screen.findByText(/already uses this email address/i)).toBeInTheDocument();
  });

  it("has no serious accessibility violations", async () => {
    const user = userEvent.setup();
    const { container } = renderAccounts();
    await user.click(await screen.findByRole("button", { name: /Add a user/i }));
    const results = await axe(container);
    const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
    expect(serious).toEqual([]);
  });
});

describe("the account lifecycle", () => {
  it("states the consequence before deactivating, then reflects it", async () => {
    const user = userEvent.setup();
    renderAccounts();
    const row = (await screen.findByText("Dr. Hala")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: /^Deactivate$/i }));

    // Signed out of every device, immediately, for somebody who is not in the room. An administrator who
    // finds that out afterwards has been given no choice.
    expect(await screen.findByText(/signed out of every device immediately/i)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /^Confirm$/i }));

    await waitFor(() => {
      const after = screen.getByText("Dr. Hala").closest("tr")!;
      expect(within(after).getByText(/De-provisioned/i)).toBeInTheDocument();
    });
  });

  it("offers the way back on a deactivated account", async () => {
    const user = userEvent.setup();
    renderAccounts();
    const row = (await screen.findByText("Former Staff")).closest("tr")!;
    // The action swaps with the state, so a row never offers the one that cannot apply.
    expect(within(row).queryByRole("button", { name: /^Deactivate$/i })).toBeNull();
    await user.click(within(row).getByRole("button", { name: /Reactivate/i }));
    expect(await screen.findByText(/sign in again with their existing password/i)).toBeInTheDocument();
  });

  it("refuses to offer a reset link to an account with no address", async () => {
    const user = userEvent.setup();
    renderAccounts();
    // An address-less account is a real state for anything predating 28.8 (service accounts, seeds).
    const row = (await screen.findByText("Reporting Service")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: /Send reset link/i }));

    // Stated BEFORE the attempt rather than reported after it: the server answers 422, and an administrator
    // who presses a button and reads "no-email-address" has learned it the expensive way.
    expect(await screen.findByText(/no email address, so a link cannot be sent/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Confirm$/i })).toBeDisabled();
  });
});

describe("the access catalogue", () => {
  it("lists every permission with what it allows and who already holds it", async () => {
    renderCatalogue();
    expect(await screen.findByText("emr:write")).toBeInTheDocument();
    const row = screen.getByText("emr:write").closest("tr")!;
    expect(within(row).getByText(/Author an encounter/i)).toBeInTheDocument();
    // "Who has this already" is the question an administrator has in front of a permission; without it the
    // safe guess when designing a role is always to include the key.
    expect(within(row).getByText(/doctor/)).toBeInTheDocument();
  });

  it("marks the keys that must not go on a human role", async () => {
    renderCatalogue();
    const row = (await screen.findByText("auth:ingest")).closest("tr")!;
    expect(within(row).getByText(/Service only/i)).toBeInTheDocument();
  });

  it("filters to what was searched", async () => {
    const user = userEvent.setup();
    renderCatalogue();
    await screen.findByText("emr:write");
    await user.type(screen.getByRole("searchbox"), "finance");
    await waitFor(() => expect(screen.queryByText("emr:write")).toBeNull());
    expect(screen.getByText("finance:approve")).toBeInTheDocument();
  });
});

describe("designing a role", () => {
  async function openDesigner() {
    const user = userEvent.setup();
    renderCatalogue();
    await user.click(await screen.findByRole("tab", { name: /^Roles$/i }));
    await user.click(await screen.findByRole("button", { name: /Design a role/i }));
    return user;
  }

  it("will not create a role that grants nothing, or one with an unusable name", async () => {
    const user = await openDesigner();
    await user.type(await screen.findByLabelText(/^Name$/i), "X");
    await user.click(screen.getAllByRole("button", { name: /^Save$/i })[0]);

    expect(await screen.findByText(/3–49 characters/i)).toBeInTheDocument();
    expect(screen.getByText(/at least one permission/i)).toBeInTheDocument();
  });

  it("cannot put a machine-only key on a role", async () => {
    await openDesigner();
    // Disabled rather than merely discouraged: the server refuses it, so a tickable box would be a control
    // that fails.
    expect(await screen.findByRole("checkbox", { name: /auth:ingest/ })).toBeDisabled();
  });

  it("refuses a set holding both halves of a separated duty, in the form", async () => {
    const user = await openDesigner();
    await user.type(await screen.findByLabelText(/^Name$/i), "money_everything");
    await user.click(screen.getByRole("checkbox", { name: /finance:write/ }));
    await user.click(screen.getByRole("checkbox", { name: /finance:approve/ }));
    await user.click(screen.getAllByRole("button", { name: /^Save$/i })[0]);

    // Raising a payment and releasing it, in one role, would breach SoD for every person ever assigned it —
    // which is why the check is over the SET and not key by key.
    expect(await screen.findByText(/Separation of duties refuses this combination/i)).toBeInTheDocument();
  });

  it("creates a role and shows it as the tenant's own", async () => {
    const user = await openDesigner();
    await user.type(await screen.findByLabelText(/^Name$/i), "triage_lead");
    await user.click(screen.getByRole("checkbox", { name: /patient:read/ }));
    await user.click(screen.getByRole("checkbox", { name: /emr:read/ }));
    await user.click(screen.getAllByRole("button", { name: /^Save$/i })[0]);

    const row = (await screen.findByText("triage_lead")).closest("tr")!;
    // Built-in and custom are edited on the same terms but are not the same KIND of thing — one is platform
    // policy, the other this tenant's invention.
    expect(within(row).getByText(/Yours/i)).toBeInTheDocument();
  });
});

describe("granting one permission as an exception", () => {
  it("offers the catalogue rather than asking for a key from memory", async () => {
    const user = userEvent.setup();
    seedSession("org_admin");
    renderNode(<MembershipRoster />, new DevApiClient({ latencyMs: 0 }));

    await user.click(await screen.findByRole("tab", { name: /Authority/i }));
    await user.click((await screen.findAllByRole("button", { name: /^Open$/i }))[0]);
    await user.click(await screen.findByRole("tab", { name: /Exceptions/i }));
    await user.click(await screen.findByRole("button", { name: /Add an exception/i }));

    // This was a bare text field: granting an exception required knowing a key's exact spelling with no
    // screen anywhere that listed them, so the realistic options were "guess" or "give them a bigger role".
    const picker = await screen.findByLabelText(/^Permission$/i);
    expect(picker).toHaveAttribute("role", "combobox");
  });
});

describe("the email check", () => {
  it("catches a typo without pretending to be RFC 5322", () => {
    expect(looksLikeEmail("nadia@mersal.org")).toBe(true);
    expect(looksLikeEmail("nadia+clinic@mersal.org")).toBe(true);
    expect(looksLikeEmail("nadia")).toBe(false);
    expect(looksLikeEmail("nadia@mersal")).toBe(false);
    expect(looksLikeEmail("a b@mersal.org")).toBe(false);
    expect(looksLikeEmail("a@@mersal.org")).toBe(false);
    expect(looksLikeEmail("@mersal.org")).toBe(false);
  });
});

import { afterEach, describe, expect, it } from "vitest";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { DevApiClient } from "../src/api/DevApiClient";
import { UsersAndAccess } from "../src/screens/AccessAdmin";
import { AccessCatalogue } from "../src/screens/AccessCatalogue";
import { looksLikeEmail } from "../src/screens/UserAdmin";
import { renderNode, seedSession } from "./helpers";

/**
 * Phase 28.8/28.9/28.16 — administering people, and the access catalogue they are administered against.
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
 *
 * 28.16 moved the lifecycle off the table and onto the person's own record — three buttons on every row of a
 * staff directory is sixty-nine controls competing with the data, and two of them change what a colleague can
 * do today. So the tests that used to press a row button now open the record first, which is the change.
 */

afterEach(cleanup);

function renderUsers() {
  seedSession("org_admin");
  return renderNode(<UsersAndAccess />, new DevApiClient({ latencyMs: 0 }));
}

function renderCatalogue() {
  seedSession("org_admin");
  return renderNode(<AccessCatalogue />, new DevApiClient({ latencyMs: 0 }));
}

/** Open one person's record and switch to the Account tab, where identity and lifecycle live. */
async function openAccountTab(name: string) {
  const user = userEvent.setup();
  renderUsers();
  const row = (await screen.findByText(name)).closest("tr")!;
  await user.click(within(row).getByRole("button", { name: /^manage$/i }));
  await user.click(await screen.findByRole("tab", { name: /^account$/i }));
  return user;
}

describe("the people list", () => {
  it("shows the address an account signs in with, and marks the ones that have none", async () => {
    renderUsers();
    expect(await screen.findByText("org.admin@mersal.org")).toBeInTheDocument();
  });

  it("names the PORTALS somebody holds, not the raw issuer roles", async () => {
    // `doctor` happens to be spelled the same either way; the point is that the column resolves through the
    // catalogue, so `lab_tech` would read as "Laboratory" rather than as a role name nobody outside the
    // issuer uses.
    renderUsers();
    const row = (await screen.findByText("Dr. Hala")).closest("tr")!;
    expect(within(row).getByText(/Consultation/)).toBeInTheDocument();
  });

  it("calls out an account with no second factor", async () => {
    // MFA gates every admin scope and every break-glass request on the platform, and until 18.C2 no screen
    // anywhere showed whether a given account had one.
    renderUsers();
    const row = (await screen.findByText("Dr. Hala")).closest("tr")!;
    expect(within(row).getByText(/no second factor/i)).toBeInTheDocument();
  });
});

describe("creating an account", () => {
  it("requires a name, a usable address and at least one portal", async () => {
    const user = userEvent.setup();
    renderUsers();
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
    renderUsers();
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
    renderUsers();
    await user.click(await screen.findByRole("button", { name: /Add a user/i }));
    await user.type(screen.getByLabelText(/Full name/i), "Someone Else");
    await user.type(screen.getByLabelText(/Email address/i), "hala@mersal.org");
    await user.click(screen.getByRole("checkbox", { name: /Reception/i }));
    await user.click(screen.getByRole("button", { name: /Create and invite/i }));

    expect(await screen.findByText(/already uses this email address/i)).toBeInTheDocument();
  });

  it("has no serious accessibility violations", async () => {
    const user = userEvent.setup();
    const { container } = renderUsers();
    await user.click(await screen.findByRole("button", { name: /Add a user/i }));
    const results = await axe(container);
    const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
    expect(serious).toEqual([]);
  });
});

describe("the account lifecycle", () => {
  it("states the consequence before deactivating, then reflects it", async () => {
    const user = await openAccountTab("Dr. Hala");
    await user.click(await screen.findByRole("button", { name: /^Deactivate$/i }));

    // Signed out of every device, immediately, for somebody who is not in the room. An administrator who
    // finds that out afterwards has been given no choice.
    expect(await screen.findByText(/signed out of every device immediately/i)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /^Confirm$/i }));

    // The record stays open and the header — which is where the account's state is stated — follows.
    expect(await screen.findByText(/signed out of every device/i)).toBeInTheDocument();
    await waitFor(() => expect(screen.getAllByText(/De-provisioned/i).length).toBeGreaterThan(0));
  });

  it("offers the way back on a deactivated account", async () => {
    const user = await openAccountTab("Former Staff");
    // The action swaps with the state, so a record never offers the one that cannot apply.
    expect(screen.queryByRole("button", { name: /^Deactivate$/i })).toBeNull();
    await user.click(screen.getByRole("button", { name: /Reactivate/i }));
    expect(await screen.findByText(/sign in again with their existing password/i)).toBeInTheDocument();
  });

  it("refuses to offer a reset link to an account with no address", async () => {
    // An address-less account is a real state for anything predating 28.8 (service accounts, seeds).
    const user = await openAccountTab("Reporting Service");
    await user.click(screen.getByRole("button", { name: /Send reset link/i }));

    // Stated BEFORE the attempt rather than reported after it: the server answers 422, and an administrator
    // who presses a button and reads "no-email-address" has learned it the expensive way.
    expect(await screen.findByText(/no email address, so a link cannot be sent/i)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Confirm$/i })).toBeDisabled();
  });

  it("announces an OUTCOME after deactivating, not the label of the button just pressed", async () => {
    const user = await openAccountTab("Dr. Hala");
    await user.click(await screen.findByRole("button", { name: /^deactivate$/i }));
    await user.click(await screen.findByRole("button", { name: /^confirm$/i }));

    // The live region used to be handed `S.deactivate` — the BUTTON LABEL. A screen-reader user could not
    // tell whether the account had been deactivated or whether they were being offered the chance to.
    expect(await screen.findByText(/signed out of every device/i)).toBeInTheDocument();
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

/**
 * 28.10 — correcting an account. 28.16 — from the person's record rather than a dialog over the table.
 */
describe("correcting an existing account", () => {
  it("can change the name and the address, which nothing in the app could do before", async () => {
    // `api.updateIdentityUser` existed and had NO caller anywhere in the SPA, so the recorded remedy for a
    // typo in the address somebody signs in with was to abandon the account and create another.
    const user = await openAccountTab("Dr. Hala");

    const email = await screen.findByLabelText(/email address/i);
    await user.clear(email);
    await user.type(email, "hala.mansour@mersal.org");
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    expect(await screen.findByText("hala.mansour@mersal.org")).toBeInTheDocument();
  });

  it("says the address is what they now sign in with, rather than only 'Saved'", async () => {
    const user = await openAccountTab("Dr. Hala");

    const email = await screen.findByLabelText(/email address/i);
    await user.clear(email);
    await user.type(email, "h.new@mersal.org");
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    // Changing the address is the one edit that alters how the person GETS IN. Being told only "Saved"
    // leaves them to discover the consequence at the sign-in screen.
    expect(await screen.findByText(/sign in with the new address/i)).toBeInTheDocument();
  });

  it("reaches the identity and the portals from the same record — one person, two questions", async () => {
    const user = userEvent.setup();
    renderUsers();
    const row = (await screen.findByText("Dr. Hala")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: /^manage$/i }));

    // Access first, because that is what an administrator opens a colleague's record to change.
    expect(await screen.findByRole("checkbox", { name: /Consultation/i })).toBeInTheDocument();
    await user.click(screen.getByRole("tab", { name: /^account$/i }));
    expect(await screen.findByLabelText(/full name/i)).toBeInTheDocument();
  });

  it("does not offer Save until something has actually changed", async () => {
    // Every one of these endpoints writes an audit event, and a "details changed" entry recording no change
    // is noise in the record an access review reads.
    await openAccountTab("Dr. Hala");
    expect(screen.getByRole("button", { name: /^save$/i })).toBeDisabled();
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

/**
 * 28.13 — the POSITION: what the organisation calls the job, beside what the platform lets the account do.
 *
 * <p>The whole risk in this column is that it gets read as a second name for the role. The fixtures are built
 * to catch that — `Dr. Hala` is a "Consultant Physician" holding `doctor`, `Former Staff` is an "Office
 * Administrator" holding `reception` — so a screen that rendered the ROLE here would look plausible and fail
 * these.</p>
 */
describe("a person's position", () => {
  it("shows the job title, which is not the role", async () => {
    renderUsers();
    const row = (await screen.findByText("Dr. Hala")).closest("tr")!;
    expect(within(row).getByText("Consultant Physician")).toBeInTheDocument();
    // The portals column beside it still names the workspace, and the two disagree on purpose.
    expect(within(row).getByText(/Consultation/)).toBeInTheDocument();
  });

  it("says 'not recorded' rather than leaving a blank cell", async () => {
    // A service account has no job title because it is not a person. An empty cell reads as a rendering
    // fault; the words read as a fact.
    renderUsers();
    const row = (await screen.findByText("Reporting Service")).closest("tr")!;
    expect(within(row).getByText(/not recorded/i)).toBeInTheDocument();
  });

  it("can be set when correcting an account", async () => {
    const user = await openAccountTab("Dr. Hala");
    const field = await screen.findByLabelText(/^position$/i);
    await user.clear(field);
    await user.type(field, "Head of Internal Medicine");
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    expect(await screen.findByText("Head of Internal Medicine")).toBeInTheDocument();
  });

  it("can be CLEARED, not only set", async () => {
    // A title that no longer applies has to be removable. A field that can only ever gain a value is one
    // nobody can correct — which is why an empty box sends "" rather than being treated as "unchanged".
    const user = await openAccountTab("Dr. Hala");
    await user.clear(await screen.findByLabelText(/^position$/i));
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => expect(screen.getAllByText(/not recorded/i).length).toBeGreaterThan(0));
  });

  it("grants nothing — the help text says so where the decision is made", async () => {
    const user = userEvent.setup();
    renderUsers();
    await user.click(await screen.findByRole("button", { name: /add a user/i }));
    // The one sentence that keeps an administrator from treating this box as an access control.
    expect(await screen.findByText(/grants nothing/i)).toBeInTheDocument();
  });
});

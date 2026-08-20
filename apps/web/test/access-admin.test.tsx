import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import { UsersAndAccess } from "../src/screens/AccessAdmin";
import { seedSession } from "./helpers";

/**
 * Phase 21.6 / 28.16 — Users & Access: one table of people, one record each.
 *
 * These assert the things the screen exists to make true, not that components render: that merging the two
 * lists did NOT merge the principal, that an exception cannot be created without a reason, that an SoD
 * refusal lands IN the form rather than disappearing, that a lapsed override is shown rather than hidden,
 * and that the preview renders the server's mode-2 answer verbatim instead of a second opinion computed in
 * the browser.
 */

function renderScreen(client: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  // Rendered as an org_admin: PageHeader reads the session, and the screen is only reachable by an
  // administrator anyway. The gating itself is asserted on the SERVER (UiGatingIsCosmeticTests) — a test
  // that checked the button was absent would keep passing after the endpoint turned permissive.
  seedSession("org_admin");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={client}>
      <UsersAndAccess />
    </AppProviders>,
  );
}

/** Open one person's record from the table. */
async function openRecord(name: string, client?: ApiClient): Promise<void> {
  renderScreen(client);
  const row = (await screen.findByText(name)).closest("tr")!;
  await userEvent.click(within(row).getByRole("button", { name: /^manage$/i }));
  await screen.findByRole("tab", { name: /^access$/i });
}

/**
 * Open a record and switch to `tab`, returning the ACTIVE panel.
 *
 * Both panels are force-mounted (the design-system Tabs keeps content in the DOM and hides the inactive
 * one), so assertions must be scoped to the active panel — an unscoped query sweeps in both.
 */
async function openTab(name: string, tab: "access" | "account", client?: ApiClient): Promise<HTMLElement> {
  await openRecord(name, client);
  // Radix tabs activate on pointer events, which fireEvent.click does not synthesise.
  await userEvent.click(screen.getByRole("tab", { name: new RegExp(`^${tab}$`, "i") }));
  return await screen.findByRole("tabpanel");
}

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

/**
 * 28.16 — the merge is of the LIST, not of the principal.
 *
 * <p>Accounts and Authority were two tabs listing the same colleagues with the same name in the first column
 * of each. They are one table now. What must NOT have happened is the union of a person's authority across
 * organisations — design 40 invariant 1 — so these assert both halves: one row per person, and a record that
 * makes you name the membership before it will configure anything.</p>
 */
describe("one table of people", () => {
  it("lists a person holding authority in two organisations ONCE, naming both", async () => {
    renderScreen();
    await screen.findByText("Sara Ibrahim");

    const sara = screen.getAllByRole("row").slice(1).filter((r) => within(r).queryByText("Sara Ibrahim"));
    // Two rows was the old shape, and it was two rows because the table was keyed on the membership. A
    // directory of colleagues that lists one colleague twice is a directory nobody can count.
    expect(sara).toHaveLength(1);
    // Both organisations, in one cell, because she is one person holding authority in two of them.
    expect(within(sara[0]).getByText("mersal, partner-ngo")).toBeInTheDocument();
  });

  it("refuses to blend two memberships — the record makes you choose which one you are configuring", async () => {
    await openRecord("Sara Ibrahim");

    // Authorization evaluates against the membership, never the identity. A single "everything Sara can do"
    // view is the one thing design 40 forbids, so the switch is the screen admitting there is no such thing.
    const picker = await screen.findByRole("group", { name: /configuring authority in/i });
    expect(within(picker).getAllByRole("radio")).toHaveLength(2);
  });

  it("shows no membership switch for somebody who holds exactly one", async () => {
    // A radio group with one option is a control that cannot be operated. On a single-tenant platform that
    // would be every record on the screen carrying a distinction that does not apply there.
    await openRecord("Dr. Hala");
    expect(screen.queryByRole("group", { name: /configuring authority in/i })).toBeNull();
  });

  it("carries the address, the position and the portals in ONE row", async () => {
    renderScreen();
    const row = (await screen.findByText("Dr. Hala")).closest("tr")!;

    // Reading these three took two tabs before: the address and the position were on Accounts, the
    // authority on Authority, and neither table said what the other one held.
    expect(within(row).getByText("hala@mersal.org")).toBeInTheDocument();
    expect(within(row).getByText("Consultant Physician")).toBeInTheDocument();
    expect(within(row).getByText(/Consultation/)).toBeInTheDocument();
  });

  it("marks the account that has no address, and the one with no second factor", async () => {
    renderScreen();
    // Neither can be helped back in: "send a reset link" cannot reach an account with no address, and MFA
    // gates every admin scope and every break-glass request on the platform.
    const svc = (await screen.findByText("Reporting Service")).closest("tr")!;
    expect(within(svc).getByText(/no address/i)).toBeInTheDocument();

    const hala = screen.getByText("Dr. Hala").closest("tr")!;
    expect(within(hala).getByText(/no second factor/i)).toBeInTheDocument();
  });

  it("says an account holds no membership rather than leaving the row half-blank", async () => {
    renderScreen();
    const svc = (await screen.findByText("Reporting Service")).closest("tr")!;
    // An account that can sign in and reach nothing is a real state and a finding, not a rendering gap.
    expect(within(svc).getByText(/no membership/i)).toBeInTheDocument();
  });

  it("shows a suspended membership as its own state, beside the account's", async () => {
    renderScreen();
    await screen.findByText("Mohamed Farouk");
    const row = screen.getAllByRole("row").find((r) => within(r).queryByText("Mohamed Farouk"))!;
    // Invited / Suspended / Ended are three different remedies (accept, ask an administrator, start over),
    // and none of them is the same fact as the ACCOUNT being active — which this row also says.
    expect(within(row).getByText(/suspended/i)).toBeInTheDocument();
    expect(within(row).getByText(/^active$/i)).toBeInTheDocument();
  });

  it("calls out lapsed exceptions separately from live ones", async () => {
    // An override expiring changes someone's authority with nobody being told. The count on its own would
    // hide that: "3 exceptions" reads as three things in force.
    renderScreen();
    const row = (await screen.findByText("Sara Ibrahim")).closest("tr")!;
    expect(within(row).getByText(/lapsed/i)).toBeInTheDocument();
  });
});

/**
 * The portal column resolved `roles.includes(issuerRoleFor(portal))`, which only ever matches the CANONICAL
 * issuer name. Two portals answer to more than one: `provider_admin` is also granted as `network_team`, and
 * `radiology_tech` is also `imaging_tech` through the rename's dual-accept window. An account holding an
 * alias rendered as holding no portal at all — and since the edit form requires at least one, it could not be
 * saved either. The seeded platform has exactly such an account.
 */
describe("portals granted under an issuer alias", () => {
  it("names the portal an alias grants instead of showing none", async () => {
    renderScreen();
    const row = (await screen.findByText("Nour Habib")).closest("tr")!;
    // Exact, because their POSITION is "Provider Network Analyst" — the two columns sit next to each other
    // and a loose match would pass on a screen that rendered the job title as the portal.
    expect(within(row).getByText("Provider Network")).toBeInTheDocument();
  });

  it("ticks that portal in the record, so the form can be saved at all", async () => {
    await openRecord("Nour Habib");
    expect(await screen.findByRole("checkbox", { name: /Provider Network/i })).toBeChecked();
  });
});

/** The questions somebody opens a staff directory holding, which used to mean reading every row of two. */
describe("finding the rows that need attention", () => {
  it("narrows to the accounts with no second factor", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findByText("Dr. Hala");
    expect(screen.getByText("Sara Ibrahim")).toBeInTheDocument();

    // A filter CHIP, not a dropdown: the toolbar's single-select groups are pressed buttons with
    // `aria-pressed`, which is also what makes the active one legible without colour.
    await user.click(screen.getByRole("button", { name: /no second factor/i }));

    // MFA gates every admin scope and every break-glass request, so "who has none" is a governance question
    // that previously meant reading every row of the accounts tab.
    await waitFor(() => expect(screen.queryByText("Sara Ibrahim")).toBeNull());
    expect(screen.getByText("Dr. Hala")).toBeInTheDocument();
  });

  it("searches across the name, the address, the position and the portal", async () => {
    const user = userEvent.setup();
    renderScreen();
    await screen.findByText("Dr. Hala");

    await user.type(screen.getByRole("searchbox"), "Dispensing Pharmacist");
    await waitFor(() => expect(screen.queryByText("Dr. Hala")).toBeNull());
    expect(screen.getByText("Mohamed Farouk")).toBeInTheDocument();
  });
});

describe("configuring portals from the record", () => {
  it("grants a portal and reports it, without touching the identity endpoints", async () => {
    const user = userEvent.setup();
    const client = new DevApiClient({ latencyMs: 0 });
    const roles = vi.spyOn(client, "setIdentityUserRoles");
    const details = vi.spyOn(client, "updateIdentityUser");
    await openRecord("Dr. Hala", client);

    await user.click(await screen.findByRole("checkbox", { name: /Pharmacy/i }));
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    // ONE write, to the grant. This used to sit behind the same Save as the name and the email address —
    // two endpoints, one button, and a half-failure that left the record and the access disagreeing with
    // nothing on screen to say which half landed.
    await waitFor(() => expect(roles).toHaveBeenCalledTimes(1));
    expect(roles.mock.calls[0][1]).toEqual(expect.arrayContaining(["doctor", "pharmacist"]));
    expect(details).not.toHaveBeenCalled();
    expect(await screen.findByText(/portals updated/i)).toBeInTheDocument();
  });

  it("will not leave somebody with no portal at all", async () => {
    const user = userEvent.setup();
    const client = new DevApiClient({ latencyMs: 0 });
    const roles = vi.spyOn(client, "setIdentityUserRoles");
    await openRecord("Dr. Hala", client);

    await user.click(await screen.findByRole("checkbox", { name: /Consultation/i }));
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    // An account with no portal signs in successfully and reaches nothing, which reads as a broken platform
    // rather than as an incomplete grant.
    expect(await screen.findByText(/at least one portal/i)).toBeInTheDocument();
    expect(roles).not.toHaveBeenCalled();
  });
});

describe("exceptions, granted from the access catalogue", () => {
  it("refuses to submit an exception without a reason", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "setMembershipOverride");
    await openRecord("Sara Ibrahim", client);

    await userEvent.click(await screen.findByRole("button", { name: /add an exception/i }));
    // Scoped to the dialog: the panel behind it captions its own table "Permission exceptions".
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/permission/i), "orders:read");
    await userEvent.click(within(dialog).getByRole("button", { name: /^save$/i }));

    // The reason is mandatory client-side AND server-side; an unexplained exception cannot be reviewed later.
    expect(await screen.findByText(/a reason is required/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });

  it("offers the catalogue rather than asking for a key from memory", async () => {
    await openRecord("Sara Ibrahim");
    await userEvent.click(await screen.findByRole("button", { name: /add an exception/i }));

    // This was a bare text field: granting an exception required knowing a key's exact spelling with no
    // screen anywhere that listed them, so the realistic options were "guess" or "give them a bigger role".
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByLabelText(/^Permission$/i)).toHaveAttribute("role", "combobox");
  });

  it("renders an SoD refusal INSIDE the form, labelled as the duty conflict it is", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    // The real 409 from identity-service names both halves of the split duty in its detail.
    vi.spyOn(client, "setMembershipOverride").mockRejectedValue(
      new ApiError("http", "conflict", 409, {
        title: "sod-conflict",
        detail: "doctor vs medical_approval: self-approval of own clinical request",
      }),
    );
    await openRecord("Sara Ibrahim", client);

    await userEvent.click(await screen.findByRole("button", { name: /add an exception/i }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/permission/i), "emr:write");
    await userEvent.click(await screen.findByRole("option", { name: /emr:write/ }));
    await userEvent.type(within(dialog).getByLabelText(/reason/i), "covering the reviewer");
    await userEvent.click(within(dialog).getByRole("button", { name: /^save$/i }));

    // role="alert" (InlineAlert tone="bad") so it is announced, and it stays on screen — a toast that
    // vanishes leaves the administrator holding a form they believe they submitted.
    const alert = await screen.findByRole("alert");
    expect(within(alert).getByText(/segregation of duties/i)).toBeInTheDocument();
    expect(alert.textContent).toMatch(/self-approval of own clinical request/);
    // The dialog is still open with the operator's work intact.
    expect(screen.getByLabelText(/reason/i)).toHaveValue("covering the reviewer");
  });

  it("does not label a non-409 refusal as a duty conflict", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    vi.spyOn(client, "setMembershipOverride").mockRejectedValue(
      new ApiError("http", "unprocessable", 422, { title: "unknown-scope" }),
    );
    await openRecord("Sara Ibrahim", client);

    await userEvent.click(await screen.findByRole("button", { name: /add an exception/i }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByLabelText(/permission/i), "not:a:key");
    await userEvent.type(within(dialog).getByLabelText(/reason/i), "typo");
    await userEvent.click(within(dialog).getByRole("button", { name: /^save$/i }));

    const alert = await screen.findByRole("alert");
    // Naming a validation failure as an SoD conflict sends someone hunting a duty split that does not exist.
    expect(within(alert).queryByText(/segregation of duties/i)).toBeNull();
  });

  it("lists a lapsed exception as expired instead of hiding it", async () => {
    await openRecord("Sara Ibrahim");
    const table = await screen.findByRole("table", { name: /permission exceptions/i });
    const row = within(table).getByText("claims:submit").closest("tr")!;
    // The evaluator already ignores it; hiding it here leaves an administrator unable to explain why
    // someone lost a key overnight.
    expect(within(row).getByText(/lapsed/i)).toBeInTheDocument();
  });

  /**
   * 28.16 — `DELETE .../overrides/{scopeKey}` shipped in 21.2 and nothing in the SPA ever called it.
   *
   * <p>That is the worse half to leave missing. An administrator who cannot withdraw a narrow,
   * reason-carrying exception has one remaining way to correct it — change the person's ROLE — which is
   * precisely the over-granting the exception path exists to avoid.</p>
   */
  it("withdraws an exception, after saying what withdrawing it does", async () => {
    const user = userEvent.setup();
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "removeMembershipOverride");
    await openRecord("Sara Ibrahim", client);

    // Scoped to the exceptions table: the same key is also a row of the effective-access panel below it,
    // which is exactly the point — one is the decision and the other is its consequence.
    const table = await screen.findByRole("table", { name: /permission exceptions/i });
    const row = within(table).getByText("reports:export").closest("tr")!;
    await user.click(within(row).getByRole("button", { name: /withdraw/i }));

    const dialog = await screen.findByRole("dialog");
    // The permission goes back to whatever the roles say, and the decision stays in the audit record. Both
    // halves matter: one is what changes, the other is what does not.
    expect(within(dialog).getByText(/nothing is erased/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();

    await user.click(within(dialog).getByRole("button", { name: /withdraw/i }));
    await waitFor(() => expect(spy).toHaveBeenCalledWith(expect.any(String), "reports:export"));
    // Gone from the table, because the fixture actually removed it — a stub that answered the same three
    // rows forever could not tell a working withdrawal from a broken one.
    await waitFor(() =>
      expect(within(screen.getByRole("table", { name: /permission exceptions/i })).queryByText("reports:export"))
        .toBeNull(),
    );
  });
});

describe("branch reach", () => {
  it("shows each grant's window and the reason it was given", async () => {
    await openRecord("Sara Ibrahim");
    const row = (await screen.findByText(/Covering Alexandria for October/)).closest("tr")!;
    // "Doctor covering Alexandria for October only" is a first-class expiring fact, and the reason is a
    // COLUMN rather than a tooltip because a reviewer working down a list will not hover every row.
    expect(within(row).getByText(/31 Oct 2026|٣١/)).toBeInTheDocument();
  });

  it("names the branches instead of eight characters of a uuid", async () => {
    await openRecord("Sara Ibrahim");
    // "Which clinics can this person see" is the question the panel exists for, and "Maadi" is an answer to
    // it. `b1000000` is not — it cannot be copied anywhere that would accept it and reads as a bug.
    expect(await screen.findByText("Maadi")).toBeInTheDocument();
    expect(screen.getByText("Alexandria")).toBeInTheDocument();
  });

  it("marks an open-ended grant rather than showing an empty cell", async () => {
    await openRecord("Sara Ibrahim");
    expect(await screen.findByText(/open-ended/i)).toBeInTheDocument();
  });
});

describe("sessions", () => {
  it("confirms before revoking, and revokes only the targeted session", async () => {
    const user = userEvent.setup();
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "revokeAccessSession");
    const panel = await openTab("Sara Ibrahim", "account", client);

    const row = (await within(panel).findAllByRole("row")).find((r) => within(r).queryByText(/Safari on iPhone/))!;
    await user.click(within(row).getByRole("button", { name: /revoke/i }));

    // Signing someone out of every device to kill one of them is a clinical interruption; the per-session
    // path is the whole reason the admin endpoint gained a session id.
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/other sessions are untouched/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();

    await user.click(within(dialog).getByRole("button", { name: /revoke/i }));
    await waitFor(() => expect(spy).toHaveBeenCalledWith("u-5", "S-2"));
  });
});

describe("the effective-access preview", () => {
  it("renders the server's mode-2 answer verbatim — the browser never recomputes the algebra", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "effectiveAccess");
    await openRecord("Sara Ibrahim", client);

    const region = await screen.findByRole("region", { name: /effective access/i });
    await within(region).findByText("encounters:read");
    expect(spy).toHaveBeenCalledTimes(1);

    // PARITY: every key the server returned is on screen, and nothing else is. A browser-side reduction
    // would be a third opinion about who can do what, and the parity suite only covers the two on the server.
    const served = await spy.mock.results[0].value;
    const keyCells = within(region)
      .getAllByRole("row")
      .slice(1)
      .map((r) => r.querySelector("td")!.textContent!);
    for (const k of served.keys) {
      expect(keyCells.some((c) => c.startsWith(k.key))).toBe(true);
    }
    expect(keyCells).toHaveLength(served.keys.length);
  });

  it("shows a denied key with its reason, and a deprecated key with its replacement", async () => {
    await openRecord("Sara Ibrahim");
    const region = await screen.findByRole("region", { name: /effective access/i });

    const rows = await within(region).findAllByRole("row");
    const denied = rows.find((r) => within(r).queryByText("orders:read"))!;
    expect(denied).toHaveAttribute("data-source", "denied");
    expect(within(denied).getByText(/under investigation/i)).toBeInTheDocument();

    // Deprecated keys still WORK — hiding them would conceal the migration debt rather than retire it.
    const deprecated = rows.find((r) => within(r).queryByText("labs:read"))!;
    expect(deprecated).toHaveAttribute("data-deprecated", "true");
    expect(within(deprecated).getByText(/investigations:read/)).toBeInTheDocument();
  });

  it("moves when an exception is granted, rather than describing a fixed picture", async () => {
    const user = userEvent.setup();
    await openRecord("Dr. Hala");

    const region = await screen.findByRole("region", { name: /effective access/i });
    expect(within(region).queryByText("finance:approve")).toBeNull();

    await user.click(await screen.findByRole("button", { name: /add an exception/i }));
    const dialog = await screen.findByRole("dialog");
    await user.type(within(dialog).getByLabelText(/permission/i), "finance:approve");
    await user.click(await screen.findByRole("option", { name: /finance:approve/ }));
    await user.type(within(dialog).getByLabelText(/reason/i), "covering the month end");
    await user.click(within(dialog).getByRole("button", { name: /^save$/i }));

    // The panel exists to show what an exception DOES. A preview that never changed would be a picture of
    // one, and the screen's most important answer would be untestable.
    await waitFor(() =>
      expect(within(screen.getByRole("region", { name: /effective access/i })).getByText("finance:approve"))
        .toBeInTheDocument(),
    );
  });
});

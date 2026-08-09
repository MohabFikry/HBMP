import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import { MembershipRoster } from "../src/screens/AccessAdmin";
import { seedSession } from "./helpers";

/**
 * Phase 21.6 — the membership administration screens (design 40 §1–§3, §6).
 *
 * These tests assert the things the screens exist to make true, not that components render: that an
 * exception cannot be created without a reason, that an SoD refusal lands IN the form rather than
 * disappearing, that a lapsed override is shown rather than hidden, and that the preview renders the
 * server's mode-2 answer verbatim instead of a second opinion computed in the browser.
 */

function renderRoster(client: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  // Rendered as an org_admin: PageHeader reads the session, and the screen is only reachable by an
  // administrator anyway. The gating itself is asserted on the SERVER (UiGatingIsCosmeticTests) — a test
  // that checked the button was absent would keep passing after the endpoint turned permissive.
  seedSession("org_admin");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={client}>
      <MembershipRoster />
    </AppProviders>,
  );
}

/**
 * Open the first membership's detail and switch to `tab`, returning the ACTIVE panel.
 *
 * Every panel is force-mounted (the design-system Tabs keeps content in the DOM and hides the inactive
 * ones), so assertions must be scoped to the active panel — an unscoped query sweeps in all five tabs.
 */
async function openDetail(tab?: string, client?: ApiClient): Promise<HTMLElement> {
  renderRoster(client);
  const open = await screen.findAllByRole("button", { name: /^open$/i });
  await userEvent.click(open[0]);
  await screen.findByRole("tab", { name: /roles/i });
  if (tab) {
    // Radix tabs activate on pointer events, which fireEvent.click does not synthesise.
    await userEvent.click(screen.getByRole("tab", { name: new RegExp(tab, "i") }));
  }
  return await screen.findByRole("tabpanel");
}

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

describe("Membership roster (21.6)", () => {
  it("lists memberships, not identities — one person appears once per organisation", async () => {
    // Invariant 1: authorization evaluates against the membership. The same person holding authority in two
    // organisations must render as two rows; collapsing them to one identity is the thing the design forbids.
    renderRoster();
    await screen.findAllByText(/Sara Ibrahim/);

    const rows = screen.getAllByRole("row").slice(1);
    const sara = rows.filter((r) => within(r).queryByText(/Sara Ibrahim/));
    expect(sara).toHaveLength(2);
    expect(sara.some((r) => within(r).queryByText(/partner-ngo/))).toBe(true);
    expect(sara.some((r) => within(r).queryByText(/^mersal$/))).toBe(true);
  });

  it("calls out lapsed exceptions separately from live ones", async () => {
    // An override expiring changes someone's authority with nobody being told. The count on its own would
    // hide that: "3 exceptions" reads as three things in force.
    renderRoster();
    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText(/Sara Ibrahim/))!;
    expect(within(row).getByText(/lapsed/i)).toBeInTheDocument();
  });

  it("shows a suspended membership as its own state, not as inactive", async () => {
    // Invited / Suspended / Ended are three different remedies (accept, ask an administrator, start over).
    renderRoster();
    await waitFor(() => expect(screen.getByText(/Mohamed Farouk/)).toBeInTheDocument());
    const row = screen.getAllByRole("row").find((r) => within(r).queryByText(/Mohamed Farouk/))!;
    expect(within(row).getByText(/suspended/i)).toBeInTheDocument();
  });
});

describe("Exceptions tab (21.6)", () => {
  it("refuses to submit an exception without a reason", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "setMembershipOverride");
    const panel = await openDetail("exceptions", client);

    await userEvent.click(within(panel).getByRole("button", { name: /add an exception/i }));
    await userEvent.type(await screen.findByLabelText(/permission/i), "orders:read");
    await userEvent.click(screen.getByRole("button", { name: /^save$/i }));

    // The reason is mandatory client-side AND server-side; an unexplained exception cannot be reviewed later.
    expect(await screen.findByText(/a reason is required/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
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
    const panel = await openDetail("exceptions", client);

    await userEvent.click(within(panel).getByRole("button", { name: /add an exception/i }));
    await userEvent.type(await screen.findByLabelText(/permission/i), "approvals:decide");
    await userEvent.type(screen.getByLabelText(/reason/i), "covering the reviewer");
    await userEvent.click(screen.getByRole("button", { name: /^save$/i }));

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
    const panel = await openDetail("exceptions", client);

    await userEvent.click(within(panel).getByRole("button", { name: /add an exception/i }));
    await userEvent.type(await screen.findByLabelText(/permission/i), "not:a:key");
    await userEvent.type(screen.getByLabelText(/reason/i), "typo");
    await userEvent.click(screen.getByRole("button", { name: /^save$/i }));

    const alert = await screen.findByRole("alert");
    // Naming a validation failure as an SoD conflict sends someone hunting a duty split that does not exist.
    expect(within(alert).queryByText(/segregation of duties/i)).toBeNull();
  });

  it("lists a lapsed exception as expired instead of hiding it", async () => {
    const panel = await openDetail("exceptions");
    const row = within(panel).getAllByRole("row").find((r) => within(r).queryByText("claims:submit"))!;
    // The evaluator already ignores it; hiding it here leaves an administrator unable to explain why
    // someone lost a key overnight.
    expect(within(row).getByText(/lapsed/i)).toBeInTheDocument();
  });
});

describe("Branch reach tab (21.6)", () => {
  it("shows each grant's window and the reason it was given", async () => {
    const panel = await openDetail("branch reach");
    const row = (await within(panel).findAllByRole("row")).find((r) =>
      within(r).queryByText(/Covering Alexandria for October/),
    )!;
    // "Doctor covering Alexandria for October only" is a first-class expiring fact, and the reason is a
    // COLUMN rather than a tooltip because a reviewer working down a list will not hover every row.
    expect(row).toBeTruthy();
    expect(within(row).getByText(/31 Oct 2026|٣١/)).toBeInTheDocument();
  });

  it("marks an open-ended grant rather than showing an empty cell", async () => {
    const panel = await openDetail("branch reach");
    expect(await within(panel).findByText(/open-ended/i)).toBeInTheDocument();
  });
});

describe("Sessions tab (21.6)", () => {
  it("confirms before revoking, and revokes only the targeted session", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "revokeAccessSession");
    const panel = await openDetail("sessions", client);

    const row = (await within(panel).findAllByRole("row")).find((r) => within(r).queryByText(/Safari on iPhone/))!;
    await userEvent.click(within(row).getByRole("button", { name: /revoke/i }));

    // Signing someone out of every device to kill one of them is a clinical interruption; the per-session
    // path is the whole reason the admin endpoint gained a session id.
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/other sessions are untouched/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();

    await userEvent.click(within(dialog).getByRole("button", { name: /revoke/i }));
    await waitFor(() => expect(spy).toHaveBeenCalledWith(expect.any(String), "S-2"));
  });
});

describe("Effective-access preview tab (21.6)", () => {
  it("renders the server's mode-2 answer verbatim — the browser never recomputes the algebra", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "effectiveAccess");
    const panel = await openDetail("effective access", client);

    await within(panel).findByText("encounters:read");
    expect(spy).toHaveBeenCalledTimes(1);

    // PARITY: every key the server returned is on screen, and nothing else is. A browser-side reduction
    // would be a third opinion about who can do what, and the parity suite only covers the two on the server.
    const served = await spy.mock.results[0].value;
    // Scoped to the preview's own region: every tab panel is force-mounted, so an unscoped row query would
    // sweep in the roles/exceptions/grants tables too.
    const keyCells = within(panel)
      .getAllByRole("row")
      .slice(1)
      .map((r) => r.querySelector("td")!.textContent!);
    for (const k of served.keys) {
      expect(keyCells.some((c) => c.startsWith(k.key))).toBe(true);
    }
    expect(keyCells).toHaveLength(served.keys.length);
  });

  it("shows a denied key with its reason, and a deprecated key with its replacement", async () => {
    const panel = await openDetail("effective access");

    const rows = await within(panel).findAllByRole("row");
    const denied = rows.find((r) => within(r).queryByText("orders:read"))!;
    expect(denied).toHaveAttribute("data-source", "denied");
    expect(within(denied).getByText(/under investigation/i)).toBeInTheDocument();

    // Deprecated keys still WORK — hiding them would conceal the migration debt rather than retire it.
    const deprecated = rows.find((r) => within(r).queryByText("labs:read"))!;
    expect(deprecated).toHaveAttribute("data-deprecated", "true");
    expect(within(deprecated).getByText(/investigations:read/)).toBeInTheDocument();
  });
});

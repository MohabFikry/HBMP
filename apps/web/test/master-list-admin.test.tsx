import { describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { MasterListAdmin } from "../src/screens/MasterListAdmin";
import { ApiError } from "../src/api/http";

/**
 * The clinical master lists (ADR-0035 §4).
 *
 * <p><b>What this screen exists to fix.</b> `medical_director` already held `admin:edit-masterdata` and
 * `POST /api/v1/admin/master-data` was already built — effective-dated, versioned, rationale-mandatory,
 * audited. There was no door: `portalForRole` gives one portal per role, the only Master Data screen lived in
 * the `admin` portal, and it was read-only anyway. The authority had been granted and had nowhere to go.</p>
 *
 * <p>What these tests guard is the governance, not the form. A code table is safety-critical — a wrong ICD
 * mapping misroutes a diagnosis, a wrong ATC entry breaks an interaction check — and none of that fails
 * loudly. So: an edit appends rather than overwrites, a rationale is not optional, a change is shown against
 * what it replaces, and a diff that could not be read is never rendered as "nothing changes".</p>
 */

function render(api: ApiClient = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient) {
  return renderNode(<MasterListAdmin />, api);
}

async function openEditor(user: ReturnType<typeof userEvent.setup>) {
  const table = await screen.findByRole("grid", { name: /in force/i })
    .catch(() => screen.findByRole("table", { name: /in force/i }));
  await user.click(within(table as HTMLElement).getAllByRole("button", { name: /^Edit — / })[0]);
}

describe("the master list", () => {
  it("shows the versions in force", async () => {
    render();
    expect(await screen.findByText("E11.9")).toBeInTheDocument();
    expect(screen.getByText("A10BA02")).toBeInTheDocument();
  });

  it("says an edit APPENDS a version rather than replacing one", async () => {
    render();
    // The single most important thing a supervisor must understand before touching this screen: a record
    // written last March still resolves this code as it read last March. It is stated on the page, not
    // buried in a tooltip.
    expect(await screen.findByText(/APPENDS a version; it never overwrites one/i)).toBeInTheDocument();
  });

  it("offers no edit control for a code system outside clinical governance", async () => {
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { adminMasterData: unknown }).adminMasterData = vi.fn().mockResolvedValue([
      { id: "MDV-9", system: "Formulary", code: "TIER-A", versionNo: 1, retired: false,
        effectiveFrom: "2026-01-01T00:00:00Z", rationale: "Initial" },
    ]);
    render(api);

    // Shown, because it IS in force and the supervisor should see it — but with no control, rather than a
    // button that 403s. An affordance that always fails teaches people the screen is broken.
    expect(await screen.findByText("TIER-A")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^Edit — / })).not.toBeInTheDocument();
  });
});

describe("the editor", () => {
  it("will not save without a rationale", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "adminMasterDataUpsert");
    render(api);
    await openEditor(user);

    await user.click(screen.getByRole("button", { name: /Save new version/i }));

    // The rationale is what somebody reads in three years asking why a code changed the week a claim was
    // denied. A version history with no account of any of it is a list of changes, not a record.
    expect(await screen.findByText(/State why/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });

  it("sends the parsed attributes, the rationale and the code", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "adminMasterDataUpsert");
    render(api);
    await openEditor(user);

    await user.type(screen.getByLabelText(/Attributes/i), "title = Type 2 diabetes\nchronic = true");
    await user.type(screen.getByLabelText(/Why this change/i), "Annual ICD refresh");
    await user.click(screen.getByRole("button", { name: /Save new version/i }));

    await waitFor(() => expect(spy).toHaveBeenCalled());
    const sent = spy.mock.calls[spy.mock.calls.length - 1][0];
    expect(sent.code).toBe("E11.9");
    expect(sent.rationale).toBe("Annual ICD refresh");
    expect(sent.attributes).toEqual({ title: "Type 2 diabetes", chronic: "true" });
    expect(sent.retired).toBe(false);
  });

  it("refuses a malformed attribute line rather than dropping it", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "adminMasterDataUpsert");
    render(api);
    await openEditor(user);

    await user.type(screen.getByLabelText(/Attributes/i), "title = Fine\nthis line has no equals sign");
    await user.type(screen.getByLabelText(/Why this change/i), "Annual refresh");
    await user.click(screen.getByRole("button", { name: /Save new version/i }));

    // Silently dropping the bad line would write a version missing an attribute nobody meant to remove, and
    // it would look like a clean save.
    expect(await screen.findByText(/Each line must read name = value/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });

  it("shows what changes against the version in force", async () => {
    const user = userEvent.setup();
    render();
    await openEditor(user);

    // The fixture's version in force carries title / chronic / billable. Removing one must be as visible as
    // adding one — a diff over only the proposed keys would hide the deletion, which is the change most
    // likely to break a downstream read.
    await user.type(screen.getByLabelText(/Attributes/i), "title = Type 2 diabetes mellitus\nnewAttr = yes");

    await waitFor(() => expect(screen.getByText("Changed")).toBeInTheDocument());
    expect(screen.getByText("Added")).toBeInTheDocument();
    expect(screen.getAllByText("Removed").length).toBeGreaterThan(0);
  });

  it("NEVER renders an unreadable diff as 'nothing changes'", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { adminMasterDataAsOf: unknown }).adminMasterDataAsOf =
      vi.fn().mockRejectedValue(new Error("down"));
    render(api);
    await openEditor(user);

    // The same rule the counter's price tiles and the clinical checks follow: a failed read is never rendered
    // as a clean result. An empty diff reads as "nothing changes", which is the one thing it must not say
    // when the truth is "we could not see what is there".
    expect(await screen.findByText(/could not be read, so this change cannot be compared/i)).toBeInTheDocument();
    expect(screen.queryByText(/Nothing changes/i)).not.toBeInTheDocument();
  });

  it("explains a server refusal for a non-clinical system in the supervisor's own terms", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { adminMasterDataUpsert: unknown }).adminMasterDataUpsert = vi.fn().mockRejectedValue(
      new ApiError("http", "code-system-out-of-scope", 403, {
        type: "urn:hbmp:code-system-out-of-scope", title: "code-system-out-of-scope",
      }),
    );
    render(api);
    await openEditor(user);

    await user.type(screen.getByLabelText(/Why this change/i), "Annual refresh");
    await user.click(screen.getByRole("button", { name: /Save new version/i }));

    // Not "could not save". A supervisor refused on scope needs to know it is a boundary, not a fault — the
    // generic failure would send them looking for a bug that is not there.
    expect(await screen.findByText(/not part of clinical governance/i)).toBeInTheDocument();
  });

  it("has no serious or critical a11y violations", async () => {
    const user = userEvent.setup();
    const { container } = render();
    await openEditor(user);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

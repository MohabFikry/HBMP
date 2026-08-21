import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { AdminGovernance } from "../src/screens/AdminConsole";
import { renderNode, seedSession } from "./helpers";

/**
 * Phase 33.7 — the governance surface, which could not govern.
 *
 * ============================================================================================================
 * WHAT THESE PROVE
 * ============================================================================================================
 * Three defects of one shape: admin-service computed the answer, and the screen either dropped it on the way
 * in or had no way to act on it.
 *
 * The first is the sharpest. `recertify` and `revoke` are keyed by an item id, and no endpoint on any service
 * returned one — so an access-review campaign could be opened, counted and swept, and the two decisions it
 * consists of were addressable only by somebody who already knew a uuid. FR-IAM-007 was satisfied by a
 * surface nobody could complete, and the campaign row could not even say how much was outstanding, because
 * all five counts the service returns were dropped in the client mapping.
 *
 * The tests below assert on what reaches the SERVER as well as what reaches the screen — the lesson pass 6
 * paid for twice. A row that renders "3 outstanding" proves nothing if the button beside it posts nowhere.
 */

afterEach(cleanup);

function render(api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  seedSession("org_admin");
  return { ...renderNode(<AdminGovernance />, api), api };
}

/** A client that records the decisions it is asked to make, so the assertions can be about the REQUEST. */
function recordingApi() {
  const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
  const calls = {
    recertify: vi.fn().mockResolvedValue(undefined),
    revoke: vi.fn().mockResolvedValue(undefined),
    sweep: vi.fn().mockResolvedValue({ autoExpired: 3 }),
    approve: vi.fn().mockResolvedValue(undefined),
    reject: vi.fn().mockResolvedValue(undefined),
  };
  (api as { recertifyAccessItem: unknown }).recertifyAccessItem = calls.recertify;
  (api as { revokeAccessItem: unknown }).revokeAccessItem = calls.revoke;
  (api as { sweepAccessCampaign: unknown }).sweepAccessCampaign = calls.sweep;
  (api as { approveBreakGlass: unknown }).approveBreakGlass = calls.approve;
  (api as { rejectBreakGlass: unknown }).rejectBreakGlass = calls.reject;
  return { api, calls };
}

async function openWorklist(user: ReturnType<typeof userEvent.setup>) {
  const row = (await screen.findByText(/Q3 2026 high-sensitivity/)).closest("tr")!;
  await user.click(within(row).getByRole("button", { name: "Review" }));
  return screen.findByRole("table", { name: /Grants under review/ });
}

// ── The counts the service always returned ────────────────────────────────────────────────────────────────

describe("a campaign says where it stands", () => {
  it("shows outstanding against the total, not just a name and a due date", async () => {
    render();

    // The row used to carry name / min tier / status / due and nothing else, so a campaign nobody had
    // started and one that was finished rendered identically as "Open, due 5 Aug".
    const row = (await screen.findByText(/Q3 2026 high-sensitivity/)).closest("tr")!;
    expect(within(row).getByText("3 / 5")).toBeInTheDocument();
  });

  it("counts lapsed grants apart from revoked ones", async () => {
    render();

    // Both removed access; only one of them means somebody looked. Three of the closed Q2 campaign's four
    // grants lapsed against one that was actually decided — the shape a "closed" chip alone conceals.
    const row = (await screen.findByText(/Q2 2026 high-sensitivity/)).closest("tr")!;
    expect(within(row).getByText("3")).toBeInTheDocument();
    expect(within(row).getByText("1")).toBeInTheDocument();
    expect(screen.getByText(/removed because the deadline passed, not because anybody reviewed them/i))
      .toBeInTheDocument();
  });
});

// ── The worklist that did not exist ───────────────────────────────────────────────────────────────────────

describe("a campaign can actually be reviewed", () => {
  it("opens the grants under review, which no screen could reach before", async () => {
    const user = userEvent.setup();
    render();

    const table = await openWorklist(user);
    expect(within(table).getByText("Sara Ibrahim")).toBeInTheDocument();
    expect(within(table).getByText("medical_approval")).toBeInTheDocument();
  });

  it("keeps a grant by posting the decision, not by redrawing the row", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    render(api);

    const table = await openWorklist(user);
    const row = within(table).getByText("Sara Ibrahim").closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Keep" }));

    // The confirmation names the person and the role — "keep this access?" with neither is a question
    // nobody can answer.
    expect(await screen.findByText(/Sara Ibrahim keeps the doctor role/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Keep" }));

    await waitFor(() => expect(calls.recertify).toHaveBeenCalledWith("ITEM-1"));
  });

  it("confirms a removal destructively, because it revokes the binding itself", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    render(api);

    const table = await openWorklist(user);
    const row = within(table).getByText("Sara Ibrahim").closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Remove" }));

    // Not "the review row is marked revoked" — the person loses the role. The dialog says the consequence
    // in the world rather than the one in the database.
    expect(await screen.findByText(/loses the doctor role immediately/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Remove" }));

    await waitFor(() => expect(calls.revoke).toHaveBeenCalledWith("ITEM-1"));
  });

  it("offers no decision on a grant already decided", async () => {
    const user = userEvent.setup();
    render();

    const table = await openWorklist(user);
    // Recertified in the fixture. The server answers 404 for a second decision, and a button that is refused
    // teaches an administrator the surface is unreliable rather than that the work is done.
    const decided = within(table).getByText("Org Admin").closest("tr")!;
    expect(within(decided).queryByRole("button", { name: "Keep" })).toBeNull();
    expect(within(decided).getByText(/Confirmed with operations/)).toBeInTheDocument();
  });

  it("renders a grant whose subject identity could not be resolved, by id", async () => {
    const user = userEvent.setup();
    render();

    // Dropping the row would hide a super_admin grant from its own recertification because a name lookup
    // failed. The id is shown and the reason is said out loud.
    const table = await openWorklist(user);
    expect(within(table).getByText("u-9")).toBeInTheDocument();
    expect(within(table).getByText(/Name unavailable/)).toBeInTheDocument();
  });
});

describe("the sweep says what it is about to do", () => {
  it("refuses before the deadline and says why, rather than succeeding at nothing", async () => {
    const user = userEvent.setup();
    // A campaign whose deadline is a year out. The server returns `0 expired` for a premature sweep, which
    // on screen is indistinguishable from a campaign with nothing outstanding.
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { accessReviewCampaigns: unknown }).accessReviewCampaigns = vi.fn().mockResolvedValue([{
      id: "CAMP-1", name: "Q3 2026 high-sensitivity access recertification",
      status: { kind: "info", label: { en: "Open", ar: "مفتوحة" } }, minTier: "T3",
      dueAt: "2027-08-05T00:00:00Z", total: 5, pending: 3, recertified: 1, revoked: 1, autoExpired: 0,
    }]);
    render(api);

    await openWorklist(user);
    expect(screen.getByRole("button", { name: "Lapse the rest" })).toBeDisabled();
    expect(screen.getByText(/deadline has not passed/i)).toBeInTheDocument();
  });

  it("names how many people lose access, and reports how many did", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    render(api);

    await openWorklist(user);
    await user.click(screen.getByRole("button", { name: "Lapse the rest" }));

    // Three, and the dialog says so — "close the campaign" is not the same sentence as "remove three
    // people's access without anybody having assessed whether they needed it".
    expect(await screen.findByText(/3 grants nobody has decided will be REMOVED/)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Lapse the rest" }));

    await waitFor(() => expect(calls.sweep).toHaveBeenCalledWith("CAMP-1"));
    expect(await screen.findByText(/3 grants lapsed/)).toBeInTheDocument();
  });
});

// ── The break-glass register ──────────────────────────────────────────────────────────────────────────────

describe("the emergency-access register reports what it is for", () => {
  it("shows how many uses fell outside what the grant covered", async () => {
    render();

    // The number admin-service computes and the contract had no field for. A register that renders
    // "Expired · 20 Jul" and drops "four of its eleven uses were out of scope" is not reporting anything.
    const row = (await screen.findByText("•••8a91")).closest("tr")!;
    expect(within(row).getByText("11")).toBeInTheDocument();
    expect(within(row).getByText("4")).toBeInTheDocument();
    expect(within(row).getByText("Not reviewed")).toBeInTheDocument();
  });

  it("never receives the requester's user id — only the token the server made", async () => {
    render();

    // The truncation used to happen here, which meant the whole subject id crossed the wire and the
    // minimisation the screen advertises held only for whoever was looking at the table.
    const cell = await screen.findByText("•••8a91");
    expect(cell.textContent).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}/);
  });

  it("approves a request, which an org admin holds the authority to do and had no button for", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    render(api);

    const row = (await screen.findByText("•••4f2a")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Approve" }));
    expect(await screen.findByText(/cannot approve your own request/i)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Approve" }));

    await waitFor(() => expect(calls.approve).toHaveBeenCalledWith("BG-2"));
  });

  it("will not refuse a request without a reason, and says so instead of disabling silently", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    render(api);

    const row = (await screen.findByText("•••4f2a")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Refuse" }));

    const confirm = await screen.findByRole("button", { name: "Refuse" });
    expect(confirm).toBeDisabled();
    expect(screen.getByText("A reason is required.")).toBeInTheDocument();

    await user.type(screen.getByLabelText(/Why \(recorded on the audit trail\)/), "Ordinary access suffices.");
    await user.click(screen.getByRole("button", { name: "Refuse" }));

    await waitFor(() => expect(calls.reject).toHaveBeenCalledWith("BG-2", "Ordinary access suffices."));
  });

  it("offers no decision on a grant past the point of deciding", async () => {
    render();

    // Approving a grant that already expired means nothing, and the server would refuse it.
    const row = (await screen.findByText("•••8a91")).closest("tr")!;
    expect(within(row).queryByRole("button", { name: "Approve" })).toBeNull();
  });
});

// ── The rules were shown; the breaches were not ───────────────────────────────────────────────────────────

describe("separation of duties is reported as breached, not only as a rule", () => {
  it("names who currently holds a conflicting pair", async () => {
    render();

    // The Access Catalogue renders the rules. This renders the people in breach of them, which
    // `/admin/dashboards/sod-violations` has returned since phase 8b with nothing calling it — so the whole
    // policy could be read without learning it was being broken.
    expect(await screen.findByText("Duties held together")).toBeInTheDocument();
    const row = (await screen.findByText("Sara Ibrahim")).closest("tr")!;
    expect(within(row).getByText("doctor")).toBeInTheDocument();
    expect(within(row).getByText("medical_approval")).toBeInTheDocument();
  });
});

describe("accessibility", () => {
  it("has no axe violations", async () => {
    const { container } = render();
    await screen.findByText("Duties held together");
    expect(await axe(container)).toHaveNoViolations();
  });
});

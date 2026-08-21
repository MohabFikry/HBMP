import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { Escalations, MyCases } from "../src/screens/CaseManager";
import { PORTALS } from "../src/portals/catalog";
import { rolePermissions } from "../src/authz/permissions";
import { renderNode, seedSession } from "./helpers";

/**
 * Phase 33.7 — the case manager's loop, which was open at both ends.
 *
 * ============================================================================================================
 * WHAT THESE PROVE
 * ============================================================================================================
 * `case_manager` has held `case:read`, `case:write` AND `case:manage` since the 0001 identity seed. Design 11
 * §3.3 gives the role `C🟠ASG R🟠ASG U🟠ASG` on `approval_case`; design 10 §3.11 lists "open/track cases;
 * coordinate referrals; manage care plans" among its key capabilities. case-service implements nine write
 * endpoints against those scopes.
 *
 * The SPA held three READ permissions and reached none of the nine. So: a coordination task could be listed
 * and never completed, an escalation could be read and never raised or resolved, and a case could never be
 * closed. A caseworker's load only ever grew, and the count beside their name stopped meaning anything after
 * the first week.
 *
 * The escalation register was worse than incomplete. `HttpApiClient` wrote the status chip as a LITERAL —
 * amber "Escalated" on every row, whatever the server said — on the reasoning that "an escalation is by
 * definition something that needed raising". True of the act and not of the record: case-service tracks
 * Raised → Acknowledged → Resolved, and a register whose purpose is showing what is outstanding showed
 * everything as outstanding, permanently.
 */

afterEach(cleanup);

function render(api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  seedSession("case_manager");
  return renderNode(<MyCases />, api);
}

function renderEscalations(api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  seedSession("case_manager");
  return renderNode(<Escalations />, api);
}

/** A client that records the writes it is asked to make, so the assertions can be about the REQUEST. */
function recordingApi() {
  const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
  const calls = {
    updateTask: vi.fn().mockResolvedValue(undefined),
    escalate: vi.fn().mockResolvedValue(undefined),
    updateEscalation: vi.fn().mockResolvedValue(undefined),
    setState: vi.fn().mockResolvedValue(undefined),
  };
  (api as { updateCaseTask: unknown }).updateCaseTask = calls.updateTask;
  (api as { raiseEscalation: unknown }).raiseEscalation = calls.escalate;
  (api as { updateEscalation: unknown }).updateEscalation = calls.updateEscalation;
  (api as { setCaseState: unknown }).setCaseState = calls.setState;
  return { api, calls };
}

async function openCase(user: ReturnType<typeof userEvent.setup>) {
  const row = (await screen.findByText("CASE-2026-000042")).closest("tr")!;
  await user.click(within(row).getByRole("button", { name: "Open 360" }));
  return screen.findByRole("table", { name: /Coordination tasks/ });
}

// ── The nav entry that was a second name for one screen ───────────────────────────────────────────────────

describe("the cases portal offers each screen once", () => {
  it("no longer lists Beneficiary 360 as a section of its own", () => {
    // `/cases/beneficiary-360` and `/cases/my-cases` both routed to `<MyCases />`, so the rail offered one
    // screen twice. The 360 is the detail panel beside the list — it has never been a separate screen, and a
    // second nav entry claiming otherwise is how somebody comes to look for a view that does not exist.
    const cases = PORTALS.find((p) => p.role === "case_manager")!;
    expect(cases.sections.map((s) => s.key)).not.toContain("beneficiary360");
    expect(rolePermissions.case_manager).not.toContain("case.beneficiary360");
  });

  it("gives the role the write permission its token has always carried", () => {
    // The seed grants case:read, case:write and case:manage. The SPA granted three reads.
    expect(rolePermissions.case_manager).toContain("case.coordinate");
  });
});

// ── Tasks ─────────────────────────────────────────────────────────────────────────────────────────────────

describe("a coordination task can be finished", () => {
  it("starts a task by posting the transition", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    render(api);

    const table = await openCase(user);
    const row = within(table).getByText("Book retinal screening").closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Start" }));

    await waitFor(() => expect(calls.updateTask).toHaveBeenCalledWith("CASE-2026-000042", "TSK-1", "in_progress"));
  });

  it("completes one with the outcome note that is the point of recording it", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    render(api);

    const table = await openCase(user);
    const row = within(table).getByText("Confirm pharmacy refill").closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Complete" }));

    expect(await screen.findByText(/is marked done and leaves your outstanding list/)).toBeInTheDocument();
    await user.type(screen.getByLabelText(/What happened/), "Pharmacy confirmed a 30-day supply.");
    await user.click(screen.getByRole("button", { name: "Complete" }));

    await waitFor(() => expect(calls.updateTask).toHaveBeenCalledWith(
      "CASE-2026-000042", "TSK-2", "done", "Pharmacy confirmed a 30-day supply."));
  });

  it("offers no transition out of a task that is already done", async () => {
    const user = userEvent.setup();
    render();

    // Done and Cancelled are terminal in `CaseWorkflow`; the server answers 409 for any move out of them.
    const table = await openCase(user);
    const row = within(table).getByText(/Call beneficiary/).closest("tr")!;
    expect(within(row).queryByRole("button", { name: "Complete" })).toBeNull();
    expect(within(row).queryByRole("button", { name: "Start" })).toBeNull();
  });
});

// ── Escalations ───────────────────────────────────────────────────────────────────────────────────────────

describe("an escalation can be raised", () => {
  it("posts the target role and the reason, which the server requires", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    render(api);

    await openCase(user);
    await user.click(screen.getByRole("button", { name: "Escalate" }));
    await user.click(await screen.findByLabelText("Ask"));
    await user.click(await screen.findByText("Medical Director"));
    await user.type(screen.getByLabelText(/What you need, and why now/), "Imaging authorisation pending 48h.");
    await user.click(screen.getByRole("button", { name: "Escalate" }));

    await waitFor(() => expect(calls.escalate).toHaveBeenCalledWith(
      "CASE-2026-000042", "medical_director", "Imaging authorisation pending 48h.", expect.any(String)));
  });

  it("will not raise one with no reason, and says so rather than being bounced by the server", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    render(api);

    await openCase(user);
    await user.click(screen.getByRole("button", { name: "Escalate" }));

    // 422 role-required / reason-required. An escalation is raised because something is urgent; a round trip
    // that comes back "reason-required" spends the operator's attention on the form instead of the case.
    const confirm = await screen.findByRole("button", { name: "Escalate" });
    expect(confirm).toBeDisabled();
    expect(screen.getByText("Choose who to ask, and say what you need.")).toBeInTheDocument();
    expect(calls.escalate).not.toHaveBeenCalled();
  });
});

describe("the escalation register reports what is still outstanding", () => {
  it("renders the three states differently instead of one amber chip for all of them", async () => {
    renderEscalations();

    // The chip used to be a literal written by HttpApiClient, so a resolved escalation and one raised this
    // morning were visually identical — in the one table whose whole purpose is telling them apart.
    expect(await screen.findByText("Escalated")).toBeInTheDocument();
    expect(screen.getByText("Acknowledged")).toBeInTheDocument();
    expect(screen.getByText("Resolved")).toBeInTheDocument();
  });

  it("acknowledges a raised escalation", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    renderEscalations(api);

    const row = (await screen.findByText(/Urgent authorization for post-surgical/)).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Acknowledge" }));

    await waitFor(() => expect(calls.updateEscalation).toHaveBeenCalledWith(
      "CASE-2026-000051", "ESC-1", "acknowledged"));
  });

  it("resolves one with the note that is the only account of how it ended", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    renderEscalations(api);

    const row = (await screen.findByText(/Repeat refusal of the same imaging/)).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: "Resolve" }));

    expect(await screen.findByText(/It stops being outstanding on CASE-2026-000042/)).toBeInTheDocument();
    await user.type(screen.getByLabelText(/How it was settled/), "Director approved on clinical grounds.");
    await user.click(screen.getByRole("button", { name: "Resolve" }));

    await waitFor(() => expect(calls.updateEscalation).toHaveBeenCalledWith(
      "CASE-2026-000042", "ESC-2", "resolved", "Director approved on clinical grounds."));
  });

  it("shows a resolved escalation's note rather than a control it cannot use", async () => {
    renderEscalations();

    const row = (await screen.findByText(/Pharmacy substitution disputed/)).closest("tr")!;
    expect(within(row).queryByRole("button", { name: "Resolve" })).toBeNull();
    expect(within(row).getByText(/Prescriber accepted the substitution/)).toBeInTheDocument();
  });
});

// ── Closing a case ────────────────────────────────────────────────────────────────────────────────────────

describe("a case can be closed", () => {
  it("names the consequence — the case leaves the load and the access goes with it", async () => {
    const user = userEvent.setup();
    const { api, calls } = recordingApi();
    render(api);

    await openCase(user);
    await user.click(screen.getByRole("button", { name: "Close this case" }));

    // Access follows assignment (design 10 §3.11: "unassignment revokes it"), so closing a case is also the
    // caseworker giving up their view of that beneficiary. Said in the dialog, because it is the part
    // somebody would not expect.
    expect(await screen.findByText(/access to this beneficiary's coordination view goes with the assignment/))
      .toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Close this case" }));

    await waitFor(() => expect(calls.setState).toHaveBeenCalledWith("CASE-2026-000042", "closed"));
  });
});

describe("accessibility", () => {
  it("has no axe violations on the escalation register", async () => {
    const { container } = renderEscalations();
    await screen.findByText("Escalated");
    expect(await axe(container)).toHaveNoViolations();
  });

  it("has no axe violations on the coordination 360", async () => {
    const user = userEvent.setup();
    const { container } = render();
    await openCase(user);
    expect(await axe(container)).toHaveNoViolations();
  });
});

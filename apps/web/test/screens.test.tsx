import { describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderApp } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import type { DecisionRequest } from "@mersal/contracts";

/** DevApiClient that counts decide() calls so we can prove a blocked submission never reached the API. */
class SpyApi extends DevApiClient {
  decideCalls: DecisionRequest[] = [];
  override decide(req: DecisionRequest) {
    this.decideCalls.push(req);
    return super.decide(req);
  }
}

async function openFirstApprovalReview() {
  const reviewButtons = await screen.findAllByRole("button", { name: "Review" });
  await userEvent.click(reviewButtons[0]);
  // The decision form's rationale field appears once the review loads.
  await screen.findByRole("group", { name: "Decision" });
}

describe("Approvals decision — mandatory rationale (US-060)", () => {
  it("blocks a reject with no rationale and never calls the API", async () => {
    const api = new SpyApi({ latencyMs: 0 });
    renderApp("/approvals/worklist", "medical_approval", api);
    await openFirstApprovalReview();

    await userEvent.click(screen.getByRole("radio", { name: "Reject" }));
    await userEvent.click(screen.getByRole("button", { name: "Submit decision" }));

    expect(
      await screen.findByText("A rationale is required for reject, partial, and request-info."),
    ).toBeInTheDocument();
    expect(api.decideCalls).toHaveLength(0);
  });

  it("submits a reject once a rationale is provided", async () => {
    const api = new SpyApi({ latencyMs: 0 });
    renderApp("/approvals/worklist", "medical_approval", api);
    await openFirstApprovalReview();

    await userEvent.click(screen.getByRole("radio", { name: "Reject" }));
    await userEvent.type(screen.getByLabelText("Rationale"), "Not medically necessary at this time.");
    await userEvent.click(screen.getByRole("button", { name: "Submit decision" }));

    await waitFor(() => expect(api.decideCalls).toHaveLength(1));
    expect(api.decideCalls[0].decision).toBe("reject");
    expect(api.decideCalls[0].rationale.length).toBeGreaterThan(0);
  });
});

describe("Executive dashboard — chart data-table alternative (US-073)", () => {
  it("exposes a data-table toggle that reveals an accessible table for a chart", async () => {
    renderApp("/director/dashboards", "medical_director");
    await screen.findByRole("heading", { name: "Clinic workload (visits/day)" });

    const toggles = screen.getAllByRole("button", { name: "Show data table" });
    await userEvent.click(toggles[0]);

    expect(await screen.findByRole("columnheader", { name: "Day" })).toBeInTheDocument();
    expect(screen.getByRole("columnheader", { name: "Visits" })).toBeInTheDocument();
  });

  it("MIN-NECESSARY: the finance summaries screen has a data-table toggle (US-073) and no diagnosis dimension", async () => {
    renderApp("/finance/summaries", "finance");
    await screen.findByRole("heading", { name: "Financial Summaries" });
    // Toggle reveals the accessible table; its dimensions are billing (service line / category / provider).
    await userEvent.click(screen.getByRole("button", { name: "Show data table" }));
    expect(await screen.findByRole("columnheader", { name: "Share" })).toBeInTheDocument();
    // No clinical grouping is even offered.
    expect(screen.queryByRole("radio", { name: /diagnos/i })).not.toBeInTheDocument();
  });
});

describe("Finance portal — no clinical reach (US-095, Phase 10.3)", () => {
  it("utilization shows billing codes + spend and never a diagnosis column", async () => {
    renderApp("/finance/utilization", "finance");
    await screen.findByRole("heading", { name: "Utilization" });
    // Billing code present; no diagnosis/clinical column header anywhere.
    expect(await screen.findByText("70553")).toBeInTheDocument();
    expect(screen.queryByRole("columnheader", { name: /diagnos/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("columnheader", { name: /clinical/i })).not.toBeInTheDocument();
  });

  it("export confirms and reports an audited row count", async () => {
    const confirmSpy = vi.spyOn(window, "confirm").mockReturnValue(true);
    renderApp("/finance/exports", "finance");
    await screen.findByRole("heading", { name: "Exports" });
    await userEvent.click(screen.getByRole("button", { name: "Export (masked, audited)" }));
    expect(await screen.findByText(/rows/)).toBeInTheDocument();
    confirmSpy.mockRestore();
  });
});

describe("Case manager — coordination 360 is a summary (Phase 10.3)", () => {
  it("shows coord-visible diagnoses but only masked note/rx/result sections", async () => {
    renderApp("/cases/my-cases", "case_manager");
    // Open the first assigned case's 360.
    const openButtons = await screen.findAllByRole("button", { name: "Open 360" });
    await userEvent.click(openButtons[0]);
    await screen.findByRole("heading", { name: "Clinical summary (coordination)" });
    // Diagnosis is coord-visible; notes/results appear only as "summary only" masked counts.
    expect(await screen.findByText(/E11\.9/)).toBeInTheDocument();
    expect(screen.getAllByText(/summary only/).length).toBeGreaterThan(0);
  });
});

describe("Async states", () => {
  it("renders an error state with Retry when the service fails", async () => {
    const api = new DevApiClient({ latencyMs: 0, fault: "error" });
    renderApp("/lab/queue", "lab", api);
    expect(await screen.findByRole("button", { name: "Retry" })).toBeInTheDocument();
  });

  it("renders an empty state when the queue is empty", async () => {
    const api = new DevApiClient({ latencyMs: 0, fault: "empty" });
    renderApp("/pharmacy/queue", "pharmacy", api);
    expect(await screen.findByText("No prescriptions awaiting dispense.")).toBeInTheDocument();
  });
});

describe("Lab queue — min-necessary", () => {
  it("shows a masked patient token and no prescription data", async () => {
    renderApp("/lab/queue", "lab");
    const table = await screen.findByRole("table", { name: "Lab Order Queue" });
    expect(within(table).getByText(/•••4821/)).toBeInTheDocument();
    // No prescription/drug column leaks into the lab zone.
    expect(within(table).queryByText(/prescription/i)).not.toBeInTheDocument();
  });
});

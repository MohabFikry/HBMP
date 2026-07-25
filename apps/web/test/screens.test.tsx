import { describe, expect, it } from "vitest";
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

  it("MIN-NECESSARY: the finance-scoped dashboard shows spend categories, not diagnoses", async () => {
    renderApp("/finance/utilization", "finance");
    await screen.findByRole("heading", { name: "Top spend categories" });
    expect(screen.queryByRole("heading", { name: "Top diagnoses" })).not.toBeInTheDocument();
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
    const table = await screen.findByRole("table", { name: "Lab order queue" });
    expect(within(table).getByText(/•••4821/)).toBeInTheDocument();
    // No prescription/drug column leaks into the lab zone.
    expect(within(table).queryByText(/prescription/i)).not.toBeInTheDocument();
  });
});

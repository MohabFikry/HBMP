import { describe, expect, it } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { ApiProvider } from "../src/api/ApiProvider";
import { DevApiClient } from "../src/api/DevApiClient";
import { ApprovalsRetrospective } from "../src/screens/ApprovalsRetrospective";
import { ClaimsAdjudication } from "../src/screens/ClaimsAdjudication";
import { ClaimsWorklist, ClaimsReconciliation } from "../src/screens/ClaimsPortal";
import { ApprovalsWorklist } from "../src/screens/ApprovalsWorklist";
import { permissionsForRole } from "../src/authz/permissions";
import { portalForRole } from "../src/portals/catalog";
import { RECON_BUCKETS } from "@mersal/contracts";
import { ApiError } from "../src/api/http";

/**
 * The approvals and claims surfaces this pass rebuilt, RENDERED.
 *
 * Every assertion is about what a screen SAYS or REACHES, because every defect fixed here was of that shape:
 * a control sent to an endpoint that does not accept it, a column that could never be filled, a queue nobody
 * could see, an action nobody could take. A test asserting markup would have passed throughout.
 */
const wrap = (ui: React.ReactNode, api?: DevApiClient) =>
  render(
    <AppProviders authClient={new DevAuthClient()}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        {api ? <ApiProvider client={api}>{ui}</ApiProvider> : ui}
      </MemoryRouter>
    </AppProviders>,
  );

// -------------------------------------------------------------------------------------------------------
describe("Claims worklist", () => {
  it("renders the money, which it could not before", async () => {
    // The whole defect in one assertion. The screen called `/claims/worklist` — the per-LINE adjudication
    // queue — and mapped `claimedAmount` and `netPayable` off a payload that has neither, so every row's
    // money columns read 0.00 and blank. Nothing about the screen looked broken.
    wrap(<ClaimsWorklist />);
    await waitFor(() => expect(screen.getByText("CLM-2026-004411")).toBeTruthy());
    const grid = screen.getByRole("grid");
    expect(within(grid).getAllByText(/3,200|٣٬٢٠٠/).length).toBeGreaterThan(0);
  });

  it("offers only statuses that exist, and narrows on them", async () => {
    const user = userEvent.setup();
    wrap(<ClaimsWorklist />);
    await waitFor(() => expect(screen.getByText("CLM-2026-004411")).toBeTruthy());

    // "Adjudicated" and "Rejected" were two of the three segments offered. Neither is a member of
    // ClaimStatus, and the parameter carrying them was not bound by the endpoint at all — so all three
    // segments returned identical rows.
    expect(screen.queryByRole("radio", { name: "Adjudicated" })).toBeNull();
    expect(screen.queryByRole("radio", { name: "Rejected" })).toBeNull();

    await user.click(screen.getByRole("radio", { name: "Denied" }));
    await waitFor(() => expect(screen.getByText("CLM-2026-004413")).toBeTruthy());
    expect(screen.queryByText("CLM-2026-004411")).toBeNull();
  });

  it("opens the claim's lines and adjustments beside it", async () => {
    const user = userEvent.setup();
    wrap(<ClaimsWorklist />);
    await waitFor(() => expect(screen.getByText("CLM-2026-004412")).toBeTruthy());
    await user.click(screen.getByText("CLM-2026-004412"));
    await waitFor(() => expect(screen.getByRole("heading", { name: "Lines" })).toBeTruthy());
    expect(screen.getByRole("heading", { name: "Adjustments" })).toBeTruthy();
  });
});

// -------------------------------------------------------------------------------------------------------
describe("Reconciliation", () => {
  it("offers all six buckets — including the two that carry the money", async () => {
    wrap(<ClaimsReconciliation />);
    // Duplicate is the double-billing signal and DeliveredNotBilled is money the platform is owed and never
    // asked for. The server classified both; neither could be selected, and neither had a chip, so they
    // rendered as their raw English token in both languages.
    for (const label of ["Matched", "Price variance", "Quantity variance",
                         "Billed, not delivered", "Delivered, not billed", "Duplicate"]) {
      expect(screen.getByRole("radio", { name: label })).toBeTruthy();
    }
    expect(RECON_BUCKETS.length).toBe(6);
  });

  it("states the window it is showing", async () => {
    // The endpoint defaults to the last ninety CAIRO days. The screen sent nothing and displayed nothing, so
    // the list silently ended ninety days back with no indication anything preceded it.
    wrap(<ClaimsReconciliation />);
    expect(screen.getByText(/Showing/)).toBeTruthy();
    expect(screen.getByRole("radio", { name: "Last 30 days" })).toBeTruthy();
  });
});

// -------------------------------------------------------------------------------------------------------
describe("Adjudication", () => {
  it("shows the engine's recommendation and whether the service was rendered", async () => {
    wrap(<ClaimsAdjudication />);
    // Two rows carry the same claim number — one row per LINE is the whole point of this queue.
    await waitFor(() => expect(screen.getAllByText("CLM-2026-004412").length).toBe(2));
    const grid = screen.getByRole("grid");
    expect(within(grid).getAllByText("RequiresManualReview").length).toBeGreaterThan(0);
    // A boolean, not a result. The officer confirms the service happened without reading what it found.
    expect(within(grid).getAllByText("Not recorded").length).toBeGreaterThan(0);
  });

  it("refuses a denial with no rationale before the round trip", async () => {
    const user = userEvent.setup();
    wrap(<ClaimsAdjudication />);
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Decide" }).length).toBeGreaterThan(0));
    await user.click(screen.getAllByRole("button", { name: "Decide" })[0]);

    // Scoped to the decision FORM: the queue's engine-recommendation filter above it also offers "Deny",
    // and they are different questions — what the engine advised, versus what this reviewer decides.
    const form = await screen.findByRole("form", { name: "Decision" });
    await user.click(within(form).getByRole("radio", { name: "Deny" }));
    await user.click(within(form).getByRole("button", { name: "Record decision" }));
    expect(await screen.findByText(/rationale is required/i)).toBeTruthy();
  });

  it("offers reason codes as a pick list, not a text box", async () => {
    const user = userEvent.setup();
    wrap(<ClaimsAdjudication />);
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Decide" }).length).toBeGreaterThan(0));
    await user.click(screen.getAllByRole("button", { name: "Decide" })[0]);
    // A free-text code the adjudicator does not know is refused with a 422 AFTER the reviewer has written a
    // rationale, which is work thrown away.
    expect(await screen.findByRole("checkbox", { name: "NO_PRIOR_AUTH" })).toBeTruthy();
    expect(screen.getByRole("checkbox", { name: "DUPLICATE_CLAIM" })).toBeTruthy();
  });

  it("renders dual control as pending, not as a failure", async () => {
    const user = userEvent.setup();
    // The fixture holds any allowed amount above 5,000 for a second approver — the server's 202 outcome.
    wrap(<ClaimsAdjudication />);
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Decide" }).length).toBeGreaterThan(0));
    await user.click(screen.getAllByRole("button", { name: "Decide" })[0]);

    await user.click(await screen.findByRole("radio", { name: "Partial" }));
    await user.type(screen.getByLabelText("Allowed amount"), "9000");
    await user.type(screen.getByLabelText("Rationale"), "Tariff caps this line.");
    await user.click(screen.getByRole("button", { name: "Record decision" }));

    expect(await screen.findByText(/Waiting for a second approver/)).toBeTruthy();
    expect(screen.getByText(/Nothing has been refused/)).toBeTruthy();
  });

  it("says which segregation-of-duties rule refused, not just that something did", async () => {
    const user = userEvent.setup();
    class Sod extends DevApiClient {
      override decideClaimLine(): never {
        throw new ApiError("http", "segregation-of-duties", 403,
          { reason: "SOD_PROVIDER_AFFILIATED" } as unknown as Record<string, unknown>);
      }
    }
    wrap(<ClaimsAdjudication />, new Sod({ latencyMs: 0 }));
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Decide" }).length).toBeGreaterThan(0));
    await user.click(screen.getAllByRole("button", { name: "Decide" })[0]);
    await user.click(screen.getByRole("button", { name: "Record decision" }));

    // Each of the three means something different about what to do next. One generic "forbidden" tells the
    // reviewer only that the software is refusing them.
    expect(await screen.findByText(/affiliated with the claiming provider/i)).toBeTruthy();
  });
});

// -------------------------------------------------------------------------------------------------------
describe("Break-glass retrospective review", () => {
  it("shows the queue and how long its oldest case has waited", async () => {
    wrap(<ApprovalsRetrospective />);
    await waitFor(() => expect(screen.getByText("AUTH-2026-000881")).toBeTruthy());
    // A count alone looks identical whether the queue turned over yesterday or has been stuck since March,
    // and only one of those is a finding.
    expect(screen.getByText("Oldest open case")).toBeTruthy();
    expect(screen.getAllByText(/Long overdue/).length).toBeGreaterThan(0);
  });

  it("records a review and the case leaves the queue", async () => {
    const user = userEvent.setup();
    wrap(<ApprovalsRetrospective />);
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Review" }).length).toBeGreaterThan(0));
    await user.click(screen.getAllByRole("button", { name: "Review" })[0]);

    await user.type(await screen.findByLabelText(/What you concluded/), "Provider systems were down.");
    await user.click(screen.getByRole("button", { name: "Justified" }));

    // Before this pass nothing anywhere could close one: `RetrospectiveReviewed` was declared and read, never
    // assigned. The queue was write-only.
    await waitFor(() => expect(screen.queryByText("AUTH-2026-000881")).toBeNull());
  });

  it("will not record a review with no reasoning", async () => {
    const user = userEvent.setup();
    wrap(<ApprovalsRetrospective />);
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Review" }).length).toBeGreaterThan(0));
    await user.click(screen.getAllByRole("button", { name: "Review" })[0]);
    await user.click(await screen.findByRole("button", { name: "Justified" }));
    expect(await screen.findByText(/written rationale is required/i)).toBeTruthy();
  });

  it("says the self-review refusal as itself", async () => {
    const user = userEvent.setup();
    class Sod extends DevApiClient {
      override completeRetrospectiveReview(): never {
        throw new ApiError("http", "segregation-of-duties", 403);
      }
    }
    wrap(<ApprovalsRetrospective />, new Sod({ latencyMs: 0 }));
    await waitFor(() => expect(screen.getAllByRole("button", { name: "Review" }).length).toBeGreaterThan(0));
    await user.click(screen.getAllByRole("button", { name: "Review" })[0]);
    await user.type(await screen.findByLabelText(/What you concluded/), "Fine by me.");
    await user.click(screen.getByRole("button", { name: "Justified" }));
    expect(await screen.findByText(/you took this break-glass decision/i)).toBeTruthy();
  });

  it("is the DIRECTOR's screen, not the approval team's", () => {
    // medical_approval raises manual and emergency authorizations. Granting them the review would make one
    // team both actor and auditor as a class — which the server's per-person SoD does not cover.
    expect(permissionsForRole("medical_director").has("director.breakglass")).toBe(true);
    expect(permissionsForRole("medical_approval").has("director.breakglass")).toBe(false);
    expect(portalForRole("medical_director")?.sections.some((s) => s.key === "break-glass")).toBe(true);
    expect(portalForRole("medical_approval")?.sections.some((s) => s.key === "break-glass")).toBe(false);
  });
});

// -------------------------------------------------------------------------------------------------------
describe("Approvals queue", () => {
  it("has no column it cannot fill", async () => {
    wrap(<ApprovalsWorklist />);
    await waitFor(() => expect(screen.getByRole("grid")).toBeTruthy());
    // "Est. cost" was declared `numeric: true, sortable: true` over a literal em-dash on every row — the
    // column sorted a constant. approvals-service holds no prices at all: no amount on the aggregate, no
    // tariff client, no column to source one from. A permanently blank column reads as missing data.
    expect(screen.queryByRole("button", { name: /Est\. cost/i })).toBeNull();
    expect(screen.queryByText("Est. cost")).toBeNull();
  });

  it("can ask who is holding a request", async () => {
    const user = userEvent.setup();
    wrap(<ApprovalsWorklist />);
    await waitFor(() => expect(screen.getByRole("grid")).toBeTruthy());
    // The projection carried no ownership and `unassigned` was served and never called, so a queue worked by
    // several reviewers could not answer "is this mine" or "has anyone taken it".
    expect(screen.getByRole("radio", { name: "Mine" })).toBeTruthy();
    await user.click(screen.getByRole("radio", { name: "Unassigned" }));
    await waitFor(() => expect(screen.getAllByText("Unassigned").length).toBeGreaterThan(1));
  });
});

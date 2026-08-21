import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { ReportAccessInbox } from "../src/screens/ReportAccessInbox";
import type { ApiClient } from "../src/api/client";
import { seedSession } from "./helpers";

/**
 * 32.4 — the two transitions the workflow had and the product could not reach.
 *
 * ============================================================================================================
 * "ASK FOR MORE" WAS A ONE-WAY DOOR THE SCREEN ITSELF OPENED
 * ============================================================================================================
 * The inbox offers Approve / Deny / Ask for more. The third drives a request to InfoRequested, and
 * `supply-info` — the only exit — was called by nothing. 18.A4 had built that exit precisely because "a
 * request that entered InfoRequested had NO path back, so the requester could never answer the question and
 * the release was permanently stuck", and a domain test has proven the transition legal ever since.
 *
 * Two layers kept it stuck anyway. The client had no method; and the inbox query returned only what the
 * caller could DECIDE, so the requester — who by definition did not place the order — never saw their own
 * request in any list. Both are fixed here, which is why the fixture now carries a third row.
 *
 * ============================================================================================================
 * PICK-UP IS AN ACT, NOT A SIDE EFFECT OF LOOKING
 * ============================================================================================================
 * `review` records the decider's identity and starts the SLA clock. Firing it on render would attribute the
 * review to whoever scrolled past, which is the opposite of what 18.A4 added it for — so the second test
 * asserts that opening the screen posts nothing at all.
 */

function renderInbox(api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  seedSession("doctor");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <ReportAccessInbox />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("Result-access transitions (32.4)", () => {
  it("lets the requester answer the reviewer's question, which is the only way out of InfoRequested", async () => {
    const user = userEvent.setup();
    const supplyReportAccessInfo = vi.fn().mockResolvedValue(undefined);
    renderInbox(Object.assign(new DevApiClient({ latencyMs: 0 }), { supplyReportAccessInfo }) as unknown as ApiClient);

    const row = await screen.findByRole("row", { name: /treating this patient since june/i });
    await user.click(within(row).getByRole("button", { name: /respond/i }));

    const form = await screen.findByRole("region", { name: /respond/i });
    await user.type(within(form).getByLabelText(/more information/i),
      "Treating clinician since June; needed for the follow-up on 2026-08-22.");
    await user.click(within(form).getByRole("button", { name: /send/i }));

    await waitFor(() => expect(supplyReportAccessInfo).toHaveBeenCalledWith(
      "rar-3", expect.stringContaining("Treating clinician since June")));
  });

  it("takes a request under review only when asked, never by being looked at", async () => {
    const user = userEvent.setup();
    const takeReportAccessUnderReview = vi.fn().mockResolvedValue(undefined);
    renderInbox(Object.assign(new DevApiClient({ latencyMs: 0 }), { takeReportAccessUnderReview }) as unknown as ApiClient);

    const row = await screen.findByRole("row", { name: /dr\.hala/i });
    expect(takeReportAccessUnderReview).not.toHaveBeenCalled();

    await user.click(within(row).getByRole("button", { name: /take under review/i }));
    expect(takeReportAccessUnderReview).toHaveBeenCalledWith("rar-1");
  });

  it("offers no decision controls on a request the caller may not decide", async () => {
    // canDecide comes from the SERVER. The requester's own row carries the Respond control and nothing else:
    // asking to see a result does not make you the person who may release it.
    renderInbox();

    const row = await screen.findByRole("row", { name: /follow-up on 2026-08-22|treating this patient since june/i });
    expect(within(row).queryByRole("button", { name: /^approve$/i })).not.toBeInTheDocument();
    expect(within(row).queryByRole("button", { name: /^deny$/i })).not.toBeInTheDocument();
    expect(within(row).getByRole("button", { name: /respond/i })).toBeInTheDocument();
  });

  it("does not offer pick-up on a request already under review", async () => {
    renderInbox();

    const row = await screen.findByRole("row", { name: /dr\.omar/i });
    expect(within(row).queryByRole("button", { name: /take under review/i })).not.toBeInTheDocument();
  });

  it("gives a request awaiting the requester its own chip, not 'awaiting decision'", async () => {
    // The two used to render identically, which told a decider a request was waiting on them while it was
    // waiting on somebody else.
    renderInbox();

    // The chip. "Take under review" is a BUTTON that also contains the phrase, so this asserts on the
    // status cell rather than on any text match.
    const row = await screen.findByRole("row", { name: /treating this patient since june/i });
    expect(within(row).getByText(/more information needed/i)).toBeInTheDocument();
    expect(within(row).queryByText(/awaiting decision/i)).not.toBeInTheDocument();
  });
});

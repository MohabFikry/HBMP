import { describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { freezeClock, renderApp, renderNode } from "./helpers";
import { CallCentreWorkspace, type Cc360, type CcApi } from "../src/screens/CallCentre";

freezeClock();

const BEN = "b-amal";

function make360(): Cc360 {
  return {
    identity: { beneficiaryId: BEN, memberNo: "MRS-M-1001", displayName: "Amal Hassan", ageBand: "30-39", status: "Active" },
    coverage: [{ category: "Outpatient", annualLimit: 10000, remainingLimit: 7500 }],
    contacts: [],
    appointments: [],
    openReferrals: [],
  };
}

function fakeApi(over: Partial<CcApi> = {}): CcApi {
  return {
    openInteraction: vi.fn().mockResolvedValue({ interactionId: "i1", callRef: "CALL-2026-000001" }),
    openMember: vi.fn().mockResolvedValue(true),
    search: vi.fn().mockResolvedValue([{ beneficiaryId: BEN, displayName: "Amal Hassan", memberNo: "MRS-M-1001" }]),
    summary: vi.fn().mockResolvedValue(make360()),
    clinics: vi.fn().mockResolvedValue([]),
    slots: vi.fn().mockResolvedValue([]),
    book: vi.fn().mockResolvedValue("ok"),
    reschedule: vi.fn().mockResolvedValue("ok"),
    cancel: vi.fn().mockResolvedValue("ok"),
    close: vi.fn().mockResolvedValue("ok"),
    history: vi.fn().mockResolvedValue([]),
    ...over,
  };
}

/**
 * Leaving a screen and coming back.
 *
 * <b>The fault this covers.</b> The unified patient profile is opened FOR someone — from a worklist row, a
 * search result, an open call — and it had no way back. Worse, the two call-centre entry points were plain
 * `<a href>`s, so opening a caller's profile mid-call did a full browser navigation: the SPA was torn down and
 * rebuilt, the open interaction vanished from the screen while remaining Open on the server, and the agent's
 * only visible option was to start a second call for the same conversation.
 *
 * Two separate mechanisms, so two separate suites: a Back control that knows where it came from, and screen
 * state that survives the round trip.
 */
describe("the patient profile can be left the way it was entered", () => {
  it("offers Back when opened from somewhere, and returns there", async () => {
    const user = userEvent.setup();
    renderApp("/reception/appointments", "reception");

    // Reception's board offers the patient file on every row.
    const openFile = await screen.findAllByRole("button", { name: /^patient file$/i });
    await user.click(openFile[0]);

    await screen.findByRole("heading", { name: /patient profile/i });
    const back = await screen.findByRole("button", { name: /^back$/i });

    await user.click(back);
    // Back to the board, not to a blank screen or out of the app.
    await waitFor(() => expect(screen.queryByRole("heading", { name: /patient profile/i })).not.toBeInTheDocument());
    expect(await screen.findAllByRole("button", { name: /^patient file$/i })).not.toHaveLength(0);
  });

  /**
   * A deep link pasted into a fresh tab has nothing behind it. A Back control there would take the user OUT of
   * the app, which is worse than not offering one — so it is absent rather than present and wrong.
   */
  it("offers no Back on a deep link with no history behind it", async () => {
    renderApp(`/patients/${BEN}`, "reception");

    await screen.findByRole("heading", { name: /patient profile/i });
    expect(screen.queryByRole("button", { name: /^back$/i })).not.toBeInTheDocument();
  });
});

describe("a screen's state survives leaving it", () => {
  /**
   * The call is the thing that must not be lost. Unmounting and remounting the workspace is exactly what a
   * navigation does — React tears the screen down either way — so this reproduces the round trip without
   * needing the profile in the middle.
   */
  it("restores the open call and the member's file after the screen unmounts", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    const first = renderNode(<CallCentreWorkspace api={api} />);

    await user.click(screen.getByRole("button", { name: /start call/i }));
    await user.type(await screen.findByLabelText(/find member/i), "+20100000000");
    await user.click(screen.getByRole("button", { name: /^search$/i }));
    await user.click(await screen.findByRole("button", { name: /Amal Hassan/ }));
    await screen.findByTestId("cc-360");
    await user.type(screen.getByLabelText(/call summary/i), "Caller asked about their remaining limit.");

    first.unmount();
    renderNode(<CallCentreWorkspace api={api} />);

    // The call is still on, and the workspace knows it.
    expect(await screen.findByRole("button", { name: /close call/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /start call/i })).not.toBeInTheDocument();
    // The member's file comes back…
    expect(await screen.findByTestId("cc-360")).toBeInTheDocument();
    // …and the summary the agent had already typed is still there to finish.
    expect(screen.getByLabelText(/call summary/i)).toHaveValue("Caller asked about their remaining limit.");
  });

  /**
   * THE RULE FOR WHAT MAY BE RESTORED. sessionStorage is a browser-visible store on a machine agents share,
   * so it holds the shape of the work — which call, which member — and never the member's details. The file
   * is re-fetched on return, through the same server gate as the first time, so coming back re-authorizes the
   * disclosure instead of redisplaying a cached one.
   */
  it("re-fetches the member's file rather than restoring it from the browser", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    const first = renderNode(<CallCentreWorkspace api={api} />);

    await user.click(screen.getByRole("button", { name: /start call/i }));
    await user.type(await screen.findByLabelText(/find member/i), "+20100000000");
    await user.click(screen.getByRole("button", { name: /^search$/i }));
    await user.click(await screen.findByRole("button", { name: /Amal Hassan/ }));
    await screen.findByTestId("cc-360");
    expect(api.summary).toHaveBeenCalledTimes(1);

    // Nothing about the member is in the browser's store — only which member is open.
    const stored = JSON.stringify(sessionStorage);
    expect(stored).not.toContain("Outpatient");
    expect(stored).not.toContain("7500");

    first.unmount();
    renderNode(<CallCentreWorkspace api={api} />);

    await screen.findByTestId("cc-360");
    await waitFor(() => expect(api.summary).toHaveBeenCalledTimes(2));
  });

  /**
   * A call that has genuinely ended must NOT be resumed. Restoring it would hand the agent a call bar for an
   * interaction the server has already closed — every action on it refused, for a reason the screen cannot
   * explain.
   */
  it("does not resume a call that was closed", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    const first = renderNode(<CallCentreWorkspace api={api} />);

    await user.click(screen.getByRole("button", { name: /start call/i }));
    await user.type(await screen.findByLabelText(/call summary/i), "Answered an eligibility question.");
    await user.click(screen.getByRole("button", { name: /close call/i }));
    await screen.findByRole("button", { name: /start call/i });

    first.unmount();
    renderNode(<CallCentreWorkspace api={api} />);

    expect(await screen.findByRole("button", { name: /start call/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /close call/i })).not.toBeInTheDocument();
  });
});

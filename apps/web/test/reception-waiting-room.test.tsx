import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ReceptionDashboard } from "../src/screens/ReceptionDashboard";

/**
 * 32.6 (C2) — the waiting room, on the desk that owns it.
 *
 * <p>Five endpoints served this queue and nothing in the product called any of them for four phases, while
 * the write half ran on every check-in: tickets were issued, never read, never ordered and never cleared.</p>
 */
function renderScreen(api: ApiClient) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <ReceptionDashboard />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("the waiting room", () => {
  it("shows who is in the building, in the server's call order", async () => {
    renderScreen(new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient);

    const table = await screen.findByRole("table", { name: "Waiting room" });
    const rows = table.querySelectorAll("tbody tr");
    expect(rows).toHaveLength(2);
    // Position 1 is the server's answer to "who is next". Nothing on this screen re-sorts it: a board that
    // disagreed with the service about who is next is worse than no board.
    expect(rows[0].textContent).toContain("Amal Hassan");
  });

  it("names a person by member number when no name was recorded", async () => {
    renderScreen(new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient);

    // Check-in can be recorded without a name. A board that prints "Unknown" calls somebody who is not there.
    expect(await screen.findByText(/Name not recorded/)).toBeInTheDocument();
  });

  it("calls the next patient through", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const callNext = vi.fn().mockResolvedValue({
      queueId: "q-1", appointmentId: "appt-2", position: 0, memberNo: "MRS-M-2026-000009",
      displayName: "Amal Hassan", appointmentType: "FollowUp", state: "InConsultation", waitSeconds: 1140,
    });
    (api as { callNextWaiting: unknown }).callNextWaiting = callNext;

    renderScreen(api);
    await user.click(await screen.findByRole("button", { name: "Call next" }));

    await waitFor(() => expect(callNext).toHaveBeenCalled());
    expect(await screen.findByText("Called")).toBeInTheDocument();
  });

  it("says so when there is nobody to call rather than doing nothing", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    // 204 → null. An empty waiting room is an ANSWER; a button that appears to do nothing reads as broken.
    (api as { callNextWaiting: unknown }).callNextWaiting = vi.fn().mockResolvedValue(null);

    renderScreen(api);
    await user.click(await screen.findByRole("button", { name: "Call next" }));

    expect(await screen.findByText("Nobody is waiting to be called.")).toBeInTheDocument();
  });

  it("asks before taking somebody off the board", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const remove = vi.fn().mockResolvedValue(undefined);
    (api as { removeWaiting: unknown }).removeWaiting = remove;

    renderScreen(api);
    await user.click((await screen.findAllByRole("button", { name: "Remove" }))[0]);

    // The call lives in the dialog, not in the row button. They are standing in the room, and a mis-tap
    // takes them off the board with nothing on screen to say it happened.
    expect(remove).not.toHaveBeenCalled();
    expect(await screen.findByText(/Remove from the waiting room\?/)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Remove", hidden: false }));
    await waitFor(() => expect(remove).toHaveBeenCalledWith("q-1"));
  });
});

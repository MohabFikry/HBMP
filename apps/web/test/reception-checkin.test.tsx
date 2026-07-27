import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import type { AppointmentRow } from "@mersal/contracts";
import { ReceptionCheckIn } from "../src/screens/ReceptionDesk";

function booked(rowVersion?: number): AppointmentRow {
  return {
    id: "appt-1",
    beneficiary: { id: "b1", token: "•••4821" },
    appointmentType: "Consultation",
    status: { kind: "info", label: { en: "Booked", ar: "محجوز" } },
    scheduledStart: "2026-07-26T09:00:00Z",
    checkInEligible: true,
    checkedIn: false,
    rowVersion,
  };
}

/** 18.D1 (E3): the row as the SERVER returns it AFTER a successful check-in. The desk must derive its chip
 * from this, not from a local "we sent the request" flag. */
function checkedIn(rowVersion?: number): AppointmentRow {
  return {
    ...booked(rowVersion),
    status: { kind: "ok", label: { en: "Checked in", ar: "تم الوصول" } },
    checkInEligible: false,
    checkedIn: true,
  };
}

function fakeApi(over: Partial<ApiClient> = {}): ApiClient {
  return {
    appointments: vi.fn().mockResolvedValue([booked(42)]),
    checkIn: vi.fn().mockResolvedValue({ id: "appt-1", status: { kind: "ok", label: { en: "Checked in", ar: "تم الوصول" } } }),
    ...over,
  } as unknown as ApiClient;
}

function renderCheckIn(api: ApiClient) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <ReceptionCheckIn />
    </AppProviders>,
  );
}

describe("17.0 — reception check-in optimistic concurrency (If-Match opt-in)", () => {
  it("echoes the row version read on the board as the If-Match token", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderCheckIn(api);

    await user.click(await screen.findByRole("button", { name: /check in/i }));

    await waitFor(() => expect(api.checkIn).toHaveBeenCalledWith("appt-1", 42));
  });

  it("renders the checked-in chip only from SERVER state, after a reload (18.D1 / E3)", async () => {
    // The rule: a read may be optimistic, a server-invariant operation may not. This screen used to paint the
    // green chip from a local `done` set the moment the request was SENT — so the board showed "checked in"
    // for a patient the server had not admitted, and a reload silently disagreed with what the desk had just
    // seen. The first load returns Booked, the reload after a successful check-in returns CheckedIn.
    const user = userEvent.setup();
    const appointments = vi.fn()
      .mockResolvedValueOnce([booked(42)])
      .mockResolvedValue([checkedIn(43)]);
    const api = fakeApi({ appointments });
    renderCheckIn(api);

    await user.click(await screen.findByRole("button", { name: /check in/i }));

    // The board was RE-READ, and the action cell now shows the confirmed chip instead of the button. Both
    // the status column and the action cell render "Checked in", which is the point — they agree because
    // both derive from the same server row.
    await waitFor(() => expect(appointments).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByRole("button", { name: /check in/i })).not.toBeInTheDocument());
    expect(screen.getAllByText(/checked in/i).length).toBeGreaterThan(0);
  });

  it("on a 412 stale write, shows the changed notice and reloads the board instead of double-acting", async () => {
    const user = userEvent.setup();
    const checkIn = vi.fn().mockRejectedValue(new ApiError("http", "Version mismatch", 412));
    const appointments = vi.fn().mockResolvedValue([booked(42)]);
    const api = fakeApi({ checkIn, appointments });
    renderCheckIn(api);

    await user.click(await screen.findByRole("button", { name: /check in/i }));

    // The stale notice appears…
    expect(await screen.findByText(/changed since the board loaded/i)).toBeInTheDocument();
    // …the row is NOT marked checked-in (no double-action)…
    expect(screen.queryByText(/^Checked in$/)).not.toBeInTheDocument();
    // …and the board is re-loaded (initial load + reload).
    await waitFor(() => expect(appointments).toHaveBeenCalledTimes(2));
  });
});

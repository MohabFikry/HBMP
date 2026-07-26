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
    rowVersion,
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

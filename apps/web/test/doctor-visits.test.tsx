import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import type { AppointmentRow } from "@mersal/contracts";
import { DoctorVisits } from "../src/screens/DoctorVisits";

function row(over: Partial<AppointmentRow> = {}): AppointmentRow {
  return {
    id: "appt-1",
    beneficiary: { id: "ben-9", token: "•••4821" },
    appointmentType: "Consultation",
    status: { kind: "ok", label: { en: "Checked in", ar: "تم الوصول" } },
    scheduledStart: "2026-07-26T09:00:00Z",
    checkInEligible: false,
    checkedIn: true,
    noShowEligible: false,
    startVisitEligible: true,
    rowVersion: 3,
    ...over,
  };
}

function Where() {
  return <span data-testid="where">{useLocation().pathname + useLocation().search}</span>;
}

function fakeApi(over: Partial<ApiClient> = {}): ApiClient {
  return {
    appointments: vi.fn().mockResolvedValue([row()]),
    startVisit: vi.fn().mockResolvedValue({ encounterId: "enc-77" }),
    ...over,
  } as unknown as ApiClient;
}

function renderVisits(api: ApiClient) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <DoctorVisits />
        <Where />
      </MemoryRouter>
    </AppProviders>,
  );
}

/**
 * The doctor's own day list (23 §1). The narrowing is the point: "my visits" must be resolved from the token,
 * and "start visit" must only be offered for a patient who has actually arrived.
 */
describe("Doctor visits (US-030 / 23 §1)", () => {
  it("asks for the caller's OWN list, not everyone's", async () => {
    const appointments = vi.fn().mockResolvedValue([row()]);
    renderVisits(fakeApi({ appointments }));

    await waitFor(() => expect(appointments).toHaveBeenCalled());
    // mine=true — the server resolves the practitioner from the token; the client never names a doctor id.
    expect(appointments).toHaveBeenCalledWith("all", true);
  });

  it("starts the visit and lands in the encounter workspace", async () => {
    const user = userEvent.setup();
    const startVisit = vi.fn().mockResolvedValue({ encounterId: "enc-77" });
    renderVisits(fakeApi({ startVisit }));

    await user.click(await screen.findByRole("button", { name: /start visit/i }));
    expect(startVisit).toHaveBeenCalledWith("appt-1", "ben-9");
    // Starting a visit and then hunting for it would be two steps for one intent.
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/clinician/encounter?encounter=enc-77"));
  });

  it("offers no Start visit for a patient who has not arrived, and says what is awaited", async () => {
    renderVisits(fakeApi({
      appointments: vi.fn().mockResolvedValue([
        row({ checkedIn: false, checkInEligible: true, startVisitEligible: false,
              status: { kind: "info", label: { en: "Booked", ar: "محجوز" } } }),
      ]),
    }));

    expect(await screen.findByText(/waiting for the desk/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /start visit/i })).not.toBeInTheDocument();
  });

  it("a 403 says the appointment belongs to another practitioner rather than failing silently", async () => {
    const user = userEvent.setup();
    const startVisit = vi.fn().mockRejectedValue(new ApiError("http", "not-the-assigned-doctor", 403));
    const appointments = vi.fn().mockResolvedValue([row()]);
    renderVisits(fakeApi({ startVisit, appointments }));

    await user.click(await screen.findByRole("button", { name: /start visit/i }));
    expect(await screen.findByText(/assigned to another practitioner/i)).toBeInTheDocument();
    await waitFor(() => expect(appointments).toHaveBeenCalledTimes(2));
  });

  it("a 409 (already open / moved on) re-reads instead of opening a second visit", async () => {
    const user = userEvent.setup();
    const startVisit = vi.fn().mockRejectedValue(new ApiError("http", "appointment-not-checked-in", 409));
    const appointments = vi.fn().mockResolvedValue([row()]);
    renderVisits(fakeApi({ startVisit, appointments }));

    await user.click(await screen.findByRole("button", { name: /start visit/i }));
    expect(await screen.findByText(/changed since the list loaded/i)).toBeInTheDocument();
    await waitFor(() => expect(appointments).toHaveBeenCalledTimes(2));
  });

  it("carries a patient-file entry point on every row", async () => {
    const user = userEvent.setup();
    renderVisits(fakeApi());
    await user.click(await screen.findByRole("button", { name: /patient file/i }));
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/patients/ben-9"));
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderVisits(fakeApi());
    await screen.findByRole("button", { name: /start visit/i });
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

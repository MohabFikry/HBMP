import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, useLocation } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
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

  it("shows Start visit DISABLED for a patient who has not arrived", async () => {
    // It used to render the word "Pending" where every other row had a button, so "can I start this visit
    // yet?" had to be inferred from the ABSENCE of a control — and the Status column two cells to the left
    // was already saying "Booked". One control in two states answers it directly.
    renderVisits(fakeApi({
      appointments: vi.fn().mockResolvedValue([
        row({ checkedIn: false, checkInEligible: true, startVisitEligible: false,
              status: { kind: "info", label: { en: "Booked", ar: "محجوز" } } }),
      ]),
    }));

    const start = await screen.findByRole("button", { name: /start visit/i });
    expect(start).toBeDisabled();
    // A disabled control with no explanation is the commonest way an interface stops making sense.
    expect(start).toHaveAttribute("title", expect.stringMatching(/checks this patient in/i));
    expect(screen.queryByText(/^pending$/i)).not.toBeInTheDocument();
  });

  it("offers nothing to start on a visit that is already finished", async () => {
    // Neither startable nor awaiting check-in — completed, cancelled, no-show. A permanently dead button on
    // a finished visit is worse than no button: it implies an action that will never become available.
    renderVisits(fakeApi({
      appointments: vi.fn().mockResolvedValue([
        row({ checkedIn: false, checkInEligible: false, startVisitEligible: false,
              status: { kind: "neu", label: { en: "Completed", ar: "مكتمل" } } }),
      ]),
    }));

    await screen.findByRole("button", { name: /timeline/i });
    expect(screen.queryByRole("button", { name: /start visit/i })).not.toBeInTheDocument();
  });

  it("shows the patient's NAME, falling back to the token only when there isn't one", async () => {
    renderVisits(fakeApi({
      appointments: vi.fn().mockResolvedValue([
        row({ id: "a-named", beneficiaryName: "Fatma Ibrahim" }),
        row({ id: "a-unnamed", beneficiary: { id: "ben-2", token: "•••7788" } }),
      ]),
    }));

    // The doctor is about to call this person into a room; "•••4821" cannot be read out.
    expect(await screen.findByText("Fatma Ibrahim")).toBeInTheDocument();
    expect(screen.getByText("•••7788")).toBeInTheDocument();
  });

  it("searches across patient, type and status", async () => {
    const user = userEvent.setup();
    renderVisits(fakeApi({
      appointments: vi.fn().mockResolvedValue([
        row({ id: "a-1", beneficiaryName: "Fatma Ibrahim" }),
        row({ id: "a-2", beneficiaryName: "Khaled Mostafa" }),
      ]),
    }));

    await screen.findByText("Fatma Ibrahim");
    await user.type(screen.getByRole("searchbox"), "khaled");

    await waitFor(() => expect(screen.queryByText("Fatma Ibrahim")).not.toBeInTheDocument());
    expect(screen.getByText("Khaled Mostafa")).toBeInTheDocument();
  });

  it("filters by status", async () => {
    const user = userEvent.setup();
    renderVisits(fakeApi({
      appointments: vi.fn().mockResolvedValue([
        row({ id: "a-in", beneficiaryName: "Fatma Ibrahim" }),
        row({ id: "a-booked", beneficiaryName: "Khaled Mostafa", checkedIn: false, checkInEligible: true,
              startVisitEligible: false, status: { kind: "info", label: { en: "Booked", ar: "محجوز" } } }),
      ]),
    }));

    await screen.findByText("Fatma Ibrahim");
    await user.click(screen.getByRole("button", { name: /booked/i }));

    await waitFor(() => expect(screen.queryByText("Fatma Ibrahim")).not.toBeInTheDocument());
    expect(screen.getByText("Khaled Mostafa")).toBeInTheDocument();
  });

  it("pages a clinic that does not fit on one screen", async () => {
    renderVisits(fakeApi({
      appointments: vi.fn().mockResolvedValue(
        Array.from({ length: 14 }, (_, i) =>
          row({ id: `a-${i}`, beneficiaryName: `Patient ${String(i).padStart(2, "0")}`,
                scheduledStart: `2026-07-26T${String(9 + i).padStart(2, "0")}:00:00Z` })),
      ),
    }));

    // Ten per page, in time order — so the last four are on page 2 rather than silently absent.
    expect(await screen.findByText("Patient 00")).toBeInTheDocument();
    expect(screen.getByText("Patient 09")).toBeInTheDocument();
    expect(screen.queryByText("Patient 10")).not.toBeInTheDocument();
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

/**
 * The encounter workspace is reached FROM somewhere — a profile's encounter row, or "Start visit" here. Both
 * have navigated to it with `?encounter=` since the screen existed, and it never read the parameter: every
 * arrival landed on the patient picker with nothing selected, so a doctor who pressed "Start visit" got a
 * list and had to find in it the visit they had just started.
 */
describe("Encounter workspace deep link", () => {
  it("opens the encounter named in the query string", async () => {
    const { DoctorEncounter } = await import("../src/screens/DoctorEncounter");
    // A COMPLETE Encounter. Mocking the client bypasses the contract parse that normally guarantees these
    // fields, so a partial fixture makes the panel throw on `e.soap.subjective` — a fault in the test, not in
    // the screen, and one that surfaces as an unhandled error rather than a failure.
    const getEncounter = vi.fn().mockResolvedValue({
      id: "enc-77", patientId: "ben-9", patientName: { en: "Fatma Ibrahim", ar: "فاطمة" },
      openedAt: "2026-08-01T09:00:00Z", signed: false, noteId: null,
      soap: { subjective: "", objective: "", assessment: "", plan: "" },
      vitals: {
        heightCm: null, weightKg: null, systolic: null, diastolic: null,
        heartRate: null, tempC: null, spo2: null, measuredAt: null,
      },
      allergies: [], diagnoses: [],
    });
    render(
      <AppProviders
        authClient={new DevAuthClient()}
        apiClient={{
          listPatients: vi.fn().mockResolvedValue([]),
          getEncounter,
          // The panel renders PatientContextBar, which reads the profile header. Stubbed so the deep-link
          // assertion is not tangled up with the context bar's own fetch.
          patientProfile: vi.fn().mockResolvedValue({ beneficiaryId: "ben-9", servedAt: "2026-08-01T09:00:00Z", sections: [] }),
          // Same reason: MemberClinicalPanel sits under the context bar and reads the member's standing
          // clinical facts on mount. An empty record is the honest stub — nothing has been recorded here.
          memberClinicalRecord: vi.fn().mockResolvedValue({
            beneficiaryId: "ben-9", bloodGroup: null, bloodGroupRecordedAt: null, allergies: [],
          }),
        } as unknown as ApiClient}
      >
        <MemoryRouter
          initialEntries={["/clinician/encounter?encounter=enc-77"]}
          future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
        >
          <DoctorEncounter />
        </MemoryRouter>
      </AppProviders>,
    );

    // Selected on the FIRST paint, from the URL — not after a click on the picker.
    await waitFor(() => expect(getEncounter).toHaveBeenCalledWith("enc-77"));
  });
});

import { describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { CallCentreWorkspace, CallHistory, type CcApi, type Cc360 } from "../src/screens/CallCentre";

const BEN = "b-amal";

function make360(): Cc360 {
  return {
    identity: { beneficiaryId: BEN, memberNo: "MRS-M-1001", displayName: "Amal Hassan", ageBand: "30-39", status: "Active" },
    coverage: [{ category: "Outpatient", annualLimit: 10000, remainingLimit: 7500 }],
    contacts: [{ contactId: "c1", kind: "Phone", value: "+20100000000", isPrimary: true }],
    appointments: [
      { appointmentId: "a1", appointmentType: "Consultation", status: "Scheduled", scheduledStart: new Date().toISOString(), branchName: "Aswan", doctorName: "Dr. Nour", specialty: "Cardiology", canReschedule: true, canCancel: true },
      { appointmentId: "a2", appointmentType: "Consultation", status: "Completed", scheduledStart: new Date().toISOString(), branchName: "Maadi", doctorName: "Dr. Sami", specialty: "Dermatology", canReschedule: false, canCancel: false },
    ],
    openReferrals: [{ referralRef: "REF-2026-000007", status: "Requested", requestedSpecialty: "Endocrinology" }],
  };
}

/**
 * Choose a clinic and a time. Reserving used to need neither — the slot id was invented with
 * crypto.randomUUID(), so every call-centre booking named a slot that could not exist and emr answered 404.
 * The Book button is disabled until a real one is picked, so these steps are the contract now.
 */
async function verifyAndOpen(user: ReturnType<typeof userEvent.setup>) {
  await startAndSelect(user);
  await user.click(screen.getByLabelText("MemberNo"));
  await user.click(screen.getByLabelText("DateOfBirth"));
  await user.click(screen.getByRole("button", { name: /verify — pass/i }));
  await screen.findByTestId("cc-360");
}

async function pickClinicAndTime(user: ReturnType<typeof userEvent.setup>) {
  await user.selectOptions(await screen.findByLabelText(/clinic/i), "p1|l1");
  const times = await screen.findAllByRole("radio");
  await user.click(times[times.length - 1]);
}

function fakeApi(over: Partial<CcApi> = {}): CcApi {
  return {
    openInteraction: vi.fn().mockResolvedValue({ interactionId: "i1", callRef: "CALL-2026-000001" }),
    verify: vi.fn().mockImplementation((_i, _b, types: string[], pass: boolean) => Promise.resolve(pass && types.length >= 2)),
    search: vi.fn().mockResolvedValue([{ beneficiaryId: BEN, displayName: "Amal Hassan", memberNo: "MRS-M-1001", challengeableIdentifierTypes: ["MemberNo", "DateOfBirth", "Phone"] }]),
    summary: vi.fn().mockResolvedValue(make360()),
    // Cross-branch clinic list, each option carrying its own branch (15.3).
    clinics: vi.fn().mockResolvedValue([
      { providerId: "p1", locationId: "l1", branchId: "br-dokki", label: "Mersal Dokki · Dokki Clinic", openSlots: 2 },
    ]),
    slots: vi.fn().mockResolvedValue([{ slotId: "slot-1", start: "2026-07-22T11:00:00Z" }]),
    book: vi.fn().mockResolvedValue("ok"),
    reschedule: vi.fn().mockResolvedValue("ok"),
    cancel: vi.fn().mockResolvedValue("ok"),
    close: vi.fn().mockResolvedValue(undefined),
    history: vi.fn().mockResolvedValue([]),
    ...over,
  };
}

async function startAndSelect(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole("button", { name: /start call/i }));
  await user.type(await screen.findByLabelText(/find member/i), "+20100000000");
  await user.click(screen.getByRole("button", { name: /^search$/i }));
  await user.click(await screen.findByRole("button", { name: /Amal Hassan/ }));
}

describe("15.5 — Call Centre workspace: verify before disclose", () => {
  it("shows NO member detail before verification (only name + challenge types)", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await startAndSelect(user);

    expect(screen.getByTestId("cc-lockchip")).toHaveTextContent(/not yet verified/i);
    // Challenge checkboxes are offered…
    expect(screen.getByLabelText("MemberNo")).toBeInTheDocument();
    // …but no coverage / appointment / contact detail is anywhere in the DOM.
    expect(screen.queryByTestId("cc-360")).not.toBeInTheDocument();
    expect(screen.queryByText(/Dr\. Nour/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Outpatient/)).not.toBeInTheDocument();
  });

  it("rejects a pass with fewer than two identifier types", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreWorkspace api={api} />);
    await startAndSelect(user);

    await user.click(screen.getByLabelText("MemberNo"));           // only one
    await user.click(screen.getByRole("button", { name: /verify — pass/i }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/at least two/i);
    expect(api.verify).not.toHaveBeenCalled();
  });

  it("unlocks the cross-branch 360 after a pass and announces it", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await startAndSelect(user);

    await user.click(screen.getByLabelText("MemberNo"));
    await user.click(screen.getByLabelText("DateOfBirth"));
    await user.click(screen.getByRole("button", { name: /verify — pass/i }));

    expect(await screen.findByTestId("cc-360")).toBeInTheDocument();
    // Appointments from every branch are shown.
    expect(screen.getByText(/Aswan/)).toBeInTheDocument();
    expect(screen.getByText(/Maadi/)).toBeInTheDocument();
    // The outcome is announced for screen readers.
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/unlocked/i));
  });
});

describe("15.5 — Call Centre workspace: act", () => {
  it("shows a clear recoverable state when a slot was just taken (409)", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi({ book: vi.fn().mockResolvedValue("conflict") })} />);
    await startAndSelect(user);
    await user.click(screen.getByLabelText("MemberNo"));
    await user.click(screen.getByLabelText("Phone"));
    await user.click(screen.getByRole("button", { name: /verify — pass/i }));
    await screen.findByTestId("cc-360");

    await pickClinicAndTime(user);
    await user.click(screen.getByRole("button", { name: /^book$/i }));
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/just taken/i));
  });

  it("requires a cancellation reason", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreWorkspace api={api} />);
    await startAndSelect(user);
    await user.click(screen.getByLabelText("MemberNo"));
    await user.click(screen.getByLabelText("Phone"));
    await user.click(screen.getByRole("button", { name: /verify — pass/i }));
    await screen.findByTestId("cc-360");

    // Open the cancel affordance on the cancellable appointment, then submit with no reason.
    await user.click(screen.getAllByRole("button", { name: /cancel appointment/i })[0]);
    await user.click(screen.getByRole("button", { name: /cancel appointment/i }));
    expect(await screen.findByRole("alert")).toHaveTextContent(/reason is required/i);
    expect(api.cancel).not.toHaveBeenCalled();
  });

  it("reschedules a changeable appointment and announces the confirmation", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreWorkspace api={api} />);
    await startAndSelect(user);
    await user.click(screen.getByLabelText("MemberNo"));
    await user.click(screen.getByLabelText("Phone"));
    await user.click(screen.getByRole("button", { name: /verify — pass/i }));
    await screen.findByTestId("cc-360");

    // Only the changeable appointment (a1) offers Reschedule.
    await pickClinicAndTime(user);
    await user.click(screen.getByRole("button", { name: /^reschedule$/i }));
    // A REAL slot id now, taken from the picker rather than generated.
    expect(api.reschedule).toHaveBeenCalledWith("i1", "a1", "slot-1");
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/rescheduled/i));
  });

  it("surfaces a recoverable state when the new slot was just taken (409)", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi({ reschedule: vi.fn().mockResolvedValue("conflict") })} />);
    await startAndSelect(user);
    await user.click(screen.getByLabelText("MemberNo"));
    await user.click(screen.getByLabelText("Phone"));
    await user.click(screen.getByRole("button", { name: /verify — pass/i }));
    await screen.findByTestId("cc-360");

    await pickClinicAndTime(user);
    await user.click(screen.getByRole("button", { name: /^reschedule$/i }));
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/just taken/i));
  });
});

describe("15.5 — Call history: load failure is distinct from empty", () => {
  /**
   * "Reservation-only, wider scope" (15.3). The wider scope is the cross-branch clinic list; the narrower power
   * is the absence of arrivals. Hiding buttons is NOT the boundary — the server enforces it by granting the
   * call centre appointment:reserve instead of appointment:write — but the screen must not offer what the
   * server will refuse, or every agent learns to ignore a 403.
   */
  it("offers reservation actions only — never check-in, no-show or start-visit", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await verifyAndOpen(user);

    // Present: the reservation verbs.
    expect(screen.getByRole("button", { name: /^book$/i })).toBeInTheDocument();
    expect(screen.getAllByRole("button", { name: /^reschedule$/i }).length).toBeGreaterThan(0);
    expect(screen.getAllByRole("button", { name: /cancel appointment/i }).length).toBeGreaterThan(0);

    // Absent: everything that records a patient physically arriving or being seen.
    expect(screen.queryByRole("button", { name: /check.?in/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /no.?show/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /start visit/i })).not.toBeInTheDocument();
    // And it says so, so the absence reads as deliberate rather than as a missing feature.
    expect(screen.getByText(/reservations only/i)).toBeInTheDocument();
  });

  it("will not reserve until a real time is chosen", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreWorkspace api={api} />);
    await verifyAndOpen(user);

    // The button is disabled rather than sending an invented slot id, which is what it used to do.
    expect(screen.getByRole("button", { name: /^book$/i })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: /^book$/i }));
    expect(api.book).not.toHaveBeenCalled();

    await pickClinicAndTime(user);
    expect(screen.getByRole("button", { name: /^book$/i })).toBeEnabled();
    await user.click(screen.getByRole("button", { name: /^book$/i }));
    // The branch travels with the clinic — the agent never states it separately.
    expect(api.book).toHaveBeenCalledWith("i1", BEN, "slot-1", "br-dokki");
  });

  it("renders an error + retry (not 'no calls') when history fails to load", async () => {
    const user = userEvent.setup();
    const history = vi.fn().mockRejectedValueOnce(new Error("boom")).mockResolvedValueOnce([]);
    renderNode(<CallHistory api={fakeApi({ history })} />);

    expect(await screen.findByRole("alert")).toHaveTextContent(/couldn't load call history/i);
    expect(screen.queryByText(/no calls yet/i)).not.toBeInTheDocument();

    // Retry recovers to the empty state.
    await user.click(screen.getByRole("button", { name: /retry/i }));
    expect(await screen.findByText(/no calls yet/i)).toBeInTheDocument();
  });
});

describe("15.5 — Call Centre workspace: a11y", () => {
  it("has no serious/critical a11y violations before verification", async () => {
    const user = userEvent.setup();
    const { container } = renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await startAndSelect(user);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

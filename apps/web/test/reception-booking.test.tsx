import { describe, expect, it } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import type { BookingRequest } from "@mersal/contracts";
import { ReceptionBooking } from "../src/screens/ReceptionBooking";

class BookingApi extends DevApiClient {
  booked: BookingRequest[] = [];
  slotCalls = 0;
  bookImpl: ((i: BookingRequest) => Promise<any>) | null = null;

  override searchEligibility(q: string) {
    void q;
    return Promise.resolve([
      { id: "ben-7", name: { en: "Omar Khalil", ar: "عمر خليل" }, cardNumber: "MRS-M-014882" },
    ]);
  }
  override providerList() {
    return Promise.resolve([
      { id: "prov-1", code: "P-1", legalName: "Mersal Dokki", providerType: "Clinic", onboardingState: "Active", status: { kind: "ok" as const, label: { en: "Active", ar: "نشط" } } },
      { id: "prov-2", code: "P-2", legalName: "Mersal Maadi", providerType: "Clinic", onboardingState: "Active", status: { kind: "ok" as const, label: { en: "Active", ar: "نشط" } } },
    ]);
  }
  override providerLocations(providerId: string) {
    void providerId;
    return Promise.resolve([{ id: "loc-1", name: "Dokki Clinic", governorate: "Giza", isPrimary: true }]);
  }
  override openSlots() {
    this.slotCalls++;
    return Promise.resolve([
      { id: "slot-1", start: "2026-07-22T11:00:00Z", end: "2026-07-22T11:15:00Z", open: true },
      { id: "slot-2", start: "2026-07-22T11:15:00Z", end: "2026-07-22T11:30:00Z", open: false },
    ]);
  }
  override bookAppointment(input: BookingRequest) {
    this.booked.push(input);
    if (this.bookImpl) return this.bookImpl(input);
    return Promise.resolve({
      id: "appt-new",
      status: { kind: "info" as const, label: { en: "Booked", ar: "محجوز" } },
      scheduledStart: "2026-07-22T11:00:00Z",
    });
  }
}

/** Walk the whole form: find the patient, pick the clinic, pick a time. */
async function fillForm(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/search by name/i), "Omar");
  await user.click(screen.getByRole("button", { name: /^search$/i }));
  await user.click(await screen.findByRole("button", { name: /^choose$/i }));

  await user.click(screen.getByRole("combobox", { name: /provider/i }));
  await user.click(await screen.findByRole("option", { name: /Mersal Dokki/ }));
  await user.click(screen.getByRole("combobox", { name: /^location$/i }));
  await user.click(await screen.findByRole("option", { name: /Dokki Clinic/ }));

  const times = await screen.findAllByRole("radio");
  await user.click(times[0]);
}

describe("Reception booking (US-020)", () => {
  it("books the chosen slot and does NOT send a branch — the server owns that", async () => {
    const user = userEvent.setup();
    const api = new BookingApi({ latencyMs: 0 });
    renderNode(<ReceptionBooking />, api as unknown as ApiClient);

    await fillForm(user);
    await user.click(screen.getByRole("button", { name: /book appointment/i }));

    await waitFor(() => expect(api.booked).toHaveLength(1));
    expect(api.booked[0]).toMatchObject({
      beneficiaryId: "ben-7",
      providerId: "prov-1",
      locationId: "loc-1",
      slotId: "slot-1",
    });
    // A branch field here could only offer a choice the server would refuse: a BranchScoped desk books into
    // its active branch and naming another is a 403.
    expect(api.booked[0].branchId).toBeUndefined();

    expect(await screen.findByText(/appointment booked/i)).toBeInTheDocument();
  });

  it("renders availability from the SERVER's flag — a taken slot is not selectable", async () => {
    const user = userEvent.setup();
    renderNode(<ReceptionBooking />, new BookingApi({ latencyMs: 0 }) as unknown as ApiClient);

    await user.type(screen.getByLabelText(/search by name/i), "Omar");
    await user.click(screen.getByRole("button", { name: /^search$/i }));
    await user.click(await screen.findByRole("button", { name: /^choose$/i }));
    await user.click(screen.getByRole("combobox", { name: /provider/i }));
    await user.click(await screen.findByRole("option", { name: /Mersal Dokki/ }));
    await user.click(screen.getByRole("combobox", { name: /^location$/i }));
    await user.click(await screen.findByRole("option", { name: /Dokki Clinic/ }));

    const times = await screen.findAllByRole("radio");
    expect(times).toHaveLength(2);
    expect(times[0]).toBeEnabled();
    // open:false — never re-derived here from the clock.
    expect(times[1]).toBeDisabled();
    expect(within(times[1]).getByText(/taken/i)).toBeInTheDocument();
  });

  it("refuses to submit an incomplete form and says what is missing", async () => {
    const user = userEvent.setup();
    const api = new BookingApi({ latencyMs: 0 });
    renderNode(<ReceptionBooking />, api as unknown as ApiClient);

    await user.click(screen.getByRole("button", { name: /book appointment/i }));
    expect(await screen.findByText(/choose a patient first/i)).toBeInTheDocument();
    expect(api.booked).toHaveLength(0);
  });

  it("on a 409 the slot was taken concurrently: the failure is SHOWN and the times re-read", async () => {
    const user = userEvent.setup();
    const api = new BookingApi({ latencyMs: 0 });
    api.bookImpl = () => Promise.reject(new ApiError("http", "Slot already booked", 409));
    renderNode(<ReceptionBooking />, api as unknown as ApiClient);

    await fillForm(user);
    const before = api.slotCalls;
    await user.click(screen.getByRole("button", { name: /book appointment/i }));

    // Not swallowed — a silent failure reads as "nothing happened" and invites the double booking.
    expect(await screen.findByRole("alert")).toBeInTheDocument();
    expect(screen.queryByText(/appointment booked/i)).not.toBeInTheDocument();
    // …and the board of times is re-read rather than leaving a dead selection in place.
    await waitFor(() => expect(api.slotCalls).toBeGreaterThan(before));
  });

  it("changing the provider clears the location and the slots loaded for the old clinic", async () => {
    const user = userEvent.setup();
    const api = new BookingApi({ latencyMs: 0 });
    renderNode(<ReceptionBooking />, api as unknown as ApiClient);

    await user.click(screen.getByRole("combobox", { name: /provider/i }));
    await user.click(await screen.findByRole("option", { name: /Mersal Dokki/ }));
    await user.click(screen.getByRole("combobox", { name: /^location$/i }));
    await user.click(await screen.findByRole("option", { name: /Dokki Clinic/ }));
    expect(await screen.findAllByRole("radio")).toHaveLength(2);

    // Switch to a DIFFERENT provider: the times belonged to the old provider/location pair, and leaving
    // them would let the desk book a time at the wrong clinic.
    await user.click(screen.getByRole("combobox", { name: /provider/i }));
    await user.click(await screen.findByRole("option", { name: /Mersal Maadi/ }));
    // The times must not come back: a slot response still in flight for the OLD clinic would otherwise
    // resolve after the clear and repopulate the list.
    await waitFor(() => expect(screen.queryAllByRole("radio")).toHaveLength(0));
    // The location is cleared too — it belonged to the previous provider.
    expect(screen.getByRole("combobox", { name: /^location$/i })).toHaveTextContent(/choose a location/i);
  });

  it("has no serious/critical a11y violations", async () => {
    const user = userEvent.setup();
    const { container } = renderNode(<ReceptionBooking />, new BookingApi({ latencyMs: 0 }) as unknown as ApiClient);
    await fillForm(user);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

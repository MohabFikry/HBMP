import { describe, expect, it } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { freezeClock, renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import type { BookingRequest } from "@mersal/contracts";
import { ReceptionBooking } from "../src/screens/ReceptionBooking";

// FILE SCOPE, not inside one describe: every suite in this file shares fixtures that name absolute
// July-2026 dates, and the booking calendar defaults its month to the real clock. Scoping the freeze to
// a single describe left the others rotting on the same time-bomb.
freezeClock();

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
  clinicsImpl: (() => Promise<any>) | null = null;
  override bookableClinics() {
    if (this.clinicsImpl) return this.clinicsImpl();
    return Promise.resolve([
      { providerId: "prov-1", locationId: "loc-1", label: "Mersal Dokki · Dokki Clinic", openSlots: 2 },
      { providerId: "prov-2", locationId: "loc-2", label: "Mersal Maadi · Maadi Clinic", openSlots: 5 },
    ]);
  }
  override openSlots() {
    this.slotCalls++;
    return Promise.resolve([
      { id: "slot-1", start: "2026-07-22T11:00:00Z", end: "2026-07-22T11:15:00Z", open: true },
      { id: "slot-2", start: "2026-07-22T11:15:00Z", end: "2026-07-22T11:30:00Z", open: false },
    ]);
  }
  /** One day, matching the two slots above — the calendar strip must agree with the times beside it. */
  override appointmentDays() {
    return Promise.resolve([{ day: "2026-07-22", openSlots: 1 }]);
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

async function choose(user: ReturnType<typeof userEvent.setup>, name: RegExp, option: RegExp) {
  await user.click(await screen.findByRole("combobox", { name }));
  await user.click(await screen.findByRole("option", { name: option }));
}

/** Pick the patient only — the tests that care about the appointment half continue from here. */
async function pickPatient(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/search by name/i), "Omar");
  await user.click(screen.getByRole("button", { name: /^search$/i }));
  await user.click(await screen.findByRole("button", { name: /^choose$/i }));
}

/** The times for the chosen day — scoped to the Time section, since the day strip is radios too. */
function timeButtons() {
  return within(screen.getByRole("radiogroup", { name: /available times/i })).getAllByRole("radio");
}

/**
 * Walk the whole form: patient → specialty → doctor → time. The clinic picker is gone: 14.5 replaced it
 * with the two fields booking actually filters on, and the clinic behind the doctor is resolved server-side.
 */
async function fillForm(user: ReturnType<typeof userEvent.setup>) {
  await pickPatient(user);
  await choose(user, /^specialty$/i, /Pediatrics/i);
  await choose(user, /^doctor$/i, /Hana Mansour/i);
  await waitFor(() => expect(timeButtons().length).toBeGreaterThan(0));
  await user.click(timeButtons()[0]);
}

describe("Reception booking (US-020) — eligibility gate", () => {
  /** A search returning one active and one suspended member. */
  class MixedStatusApi extends BookingApi {
    override searchEligibility() {
      return Promise.resolve([
        {
          id: "ben-active", name: { en: "Omar Khalil", ar: "عمر خليل" }, cardNumber: "MRS-M-014882",
          status: { kind: "ok" as const, label: { en: "Active", ar: "نشط" } }, bookable: true,
        },
        {
          id: "ben-suspended", name: { en: "Yusuf Haddad", ar: "يوسف حداد" }, cardNumber: "MRS-M-017702",
          status: { kind: "warn" as const, label: { en: "Suspended", ar: "موقوف" } }, bookable: false,
        },
      ]);
    }
  }

  async function search(user: ReturnType<typeof userEvent.setup>, api: BookingApi) {
    renderNode(<ReceptionBooking />, api as unknown as ApiClient);
    await user.type(screen.getByLabelText(/search by name/i), "a");
    await user.click(screen.getByRole("button", { name: /^search$/i }));
    return screen.findByText("Yusuf Haddad");
  }

  it("shows each result's status, so the desk learns it at the moment of the search", async () => {
    const user = userEvent.setup();
    await search(user, new MixedStatusApi({ latencyMs: 0 }));

    const suspended = (await screen.findByText("Yusuf Haddad")).closest("li")!;
    expect(within(suspended).getByText(/suspended/i)).toBeInTheDocument();
    const active = (await screen.findByText("Omar Khalil")).closest("li")!;
    expect(within(active).getByText(/^active$/i)).toBeInTheDocument();
  });

  it("offers no way to choose a non-active member, and says why", async () => {
    const user = userEvent.setup();
    await search(user, new MixedStatusApi({ latencyMs: 0 }));

    const suspended = (await screen.findByText("Yusuf Haddad")).closest("li")!;
    // Not merely disabled — absent. And the reason is stated, because "why can't I book them?" is the next
    // question the operator has to answer to the person in front of them.
    expect(within(suspended).queryByRole("button", { name: /^choose$/i })).not.toBeInTheDocument();
    expect(within(suspended).getByText(/cannot be booked/i)).toBeInTheDocument();

    // The active one is unaffected.
    const active = (await screen.findByText("Omar Khalil")).closest("li")!;
    expect(within(active).getByRole("button", { name: /^choose$/i })).toBeInTheDocument();
  });

  it("treats an ABSENT status as not bookable — default-deny, not default-allow", async () => {
    const user = userEvent.setup();
    class NoStatusApi extends BookingApi {
      override searchEligibility() {
        // An older service, or a fixture that never set it. "Not stated" must not render as "fine".
        return Promise.resolve([
          { id: "ben-x", name: { en: "Unknown Status", ar: "غير معروف" }, cardNumber: "MRS-M-1", bookable: false },
        ]);
      }
    }
    renderNode(<ReceptionBooking />, new NoStatusApi({ latencyMs: 0 }) as unknown as ApiClient);
    await user.type(screen.getByLabelText(/search by name/i), "a");
    await user.click(screen.getByRole("button", { name: /^search$/i }));

    const row = (await screen.findByText("Unknown Status")).closest("li")!;
    expect(within(row).queryByRole("button", { name: /^choose$/i })).not.toBeInTheDocument();
  });
});

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

    await pickPatient(user);
    await choose(user, /^specialty$/i, /Pediatrics/i);
    await choose(user, /^doctor$/i, /Hana Mansour/i);

    await waitFor(() => expect(timeButtons()).toHaveLength(2));
    const times = timeButtons();
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

  /**
   * The invalidation rule, at the link that now sits above the times. Branch → specialty → doctor → time:
   * changing any link must drop everything below it in the SAME update, or a render exists where the desk is
   * looking at one doctor's times under another doctor's name and books it.
   */
  it("changing the specialty clears the doctor and the time chosen under it", async () => {
    const user = userEvent.setup();
    renderNode(<ReceptionBooking />, new BookingApi({ latencyMs: 0 }) as unknown as ApiClient);

    await pickPatient(user);
    await choose(user, /^specialty$/i, /Pediatrics/i);
    await choose(user, /^doctor$/i, /Hana Mansour/i);
    await waitFor(() => expect(timeButtons().length).toBeGreaterThan(0));
    await user.click(timeButtons()[0]);
    expect(timeButtons()[0]).toHaveAttribute("aria-checked", "true");

    await choose(user, /^specialty$/i, /Cardiology/i);

    // The doctor is cleared, and with no doctor there are no times to have kept selected.
    // The Combobox is an <input>: an emptied one has no value, and the placeholder is what invites the
    // next choice. textContent would be empty either way and prove nothing.
    await waitFor(() => expect(screen.getByRole("combobox", { name: /^doctor$/i })).toHaveValue(""));
    expect(screen.queryByRole("radiogroup", { name: /available times/i })).not.toBeInTheDocument();
  });

  it("offers only specialties that actually have a bookable doctor behind them", async () => {
    const user = userEvent.setup();
    renderNode(<ReceptionBooking />, new BookingApi({ latencyMs: 0 }) as unknown as ApiClient);
    await pickPatient(user);

    await user.click(await screen.findByRole("combobox", { name: /^specialty$/i }));
    const offered = screen.getAllByRole("option").map((o) => o.textContent ?? "");

    // Obstetrics has a doctor in the directory (PRC-3) but no open slots, so choosing it would present an
    // empty doctor list — a dead end discovered only after the choice.
    expect(offered.some((o) => /Pediatrics/.test(o))).toBe(true);
    expect(offered.some((o) => /Obstetrics/.test(o))).toBe(false);
  });

  it("has no serious/critical a11y violations", async () => {
    const user = userEvent.setup();
    const { container } = renderNode(<ReceptionBooking />, new BookingApi({ latencyMs: 0 }) as unknown as ApiClient);
    await fillForm(user);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

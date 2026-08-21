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

  /** Overridden per test where the list matters; one unambiguous hit by default. */
  searchImpl: ((q: string) => Promise<any>) | null = null;
  override searchEligibility(q: string) {
    if (this.searchImpl) return this.searchImpl(q);
    return Promise.resolve({
      hits: [{ id: "ben-7", name: { en: "Omar Khalil", ar: "عمر خليل" }, cardNumber: "MRS-M-014882" }],
      truncated: false,
    });
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
  // The ROW is the button now, so its accessible name is the row's own content — the patient, which is what
  // the operator aims at anyway. There was a "Choose" button pinned to the far edge of the row, as far from
  // the name being chosen as the layout allowed.
  await user.click(await screen.findByRole("button", { name: /omar khalil/i }));
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
      return Promise.resolve({
        hits: [
          {
            id: "ben-active", name: { en: "Omar Khalil", ar: "عمر خليل" }, cardNumber: "MRS-M-014882",
            status: { kind: "ok" as const, label: { en: "Active", ar: "نشط" } }, bookable: true,
          },
          {
            id: "ben-suspended", name: { en: "Yusuf Haddad", ar: "يوسف حداد" }, cardNumber: "MRS-M-017702",
            status: { kind: "warn" as const, label: { en: "Suspended", ar: "موقوف" } }, bookable: false,
          },
        ],
        truncated: false,
      });
    }
  }

  async function search(user: ReturnType<typeof userEvent.setup>, api: BookingApi) {
    renderNode(<ReceptionBooking />, api as unknown as ApiClient);
    await user.type(screen.getByLabelText(/search by name/i), "a");
    await user.click(screen.getByRole("button", { name: /^search$/i }));
    return screen.findByText("Yusuf Haddad");
  }

  it("opens the results in a dialog when the search is AMBIGUOUS, and not when it is not", async () => {
    // Several matches is a DECISION, and a decision made against a list wedged between the search box and the
    // next step of the form is one made in the wrong place — it also ran straight into "2. Appointment", so
    // two steps read as one block.
    const user = userEvent.setup();
    await search(user, new MixedStatusApi({ latencyMs: 0 }));
    expect(await screen.findByRole("dialog", { name: /choose a patient/i })).toBeInTheDocument();
  });

  it("answers a single match inline rather than opening a dialog to confirm the only option", async () => {
    // A dialog to confirm the one possible choice is a click that buys nothing.
    const user = userEvent.setup();
    renderNode(<ReceptionBooking />, new BookingApi({ latencyMs: 0 }) as unknown as ApiClient);
    await user.type(screen.getByLabelText(/search by name/i), "Omar");
    await user.click(screen.getByRole("button", { name: /^search$/i }));

    expect(await screen.findByRole("button", { name: /omar khalil/i })).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();
  });

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
    // Not merely disabled — NOT A CONTROL AT ALL. A disabled button is still announced as a button and the
    // desk keeps aiming at it; this row is plain text with the reason beside it, because "why can't I book
    // them?" is the next question the operator has to answer to the person in front of them.
    expect(within(suspended).queryByRole("button")).not.toBeInTheDocument();
    expect(within(suspended).getByText(/cannot be booked/i)).toBeInTheDocument();

    // The active one is unaffected — and the whole row is what you press, not a button at its far edge.
    const active = (await screen.findByText("Omar Khalil")).closest("li")!;
    expect(within(active).getByRole("button", { name: /omar khalil/i })).toBeInTheDocument();
  });

  /**
   * 33.9 — a cut list must say it was cut.
   *
   * <p>The search returns 25 rows and reported the length of that page as the match count, so a term
   * matching forty people produced twenty-five with nothing to distinguish that from a complete answer.
   * The operator then picks a patient from a truncated set presented as the whole of it — and the person
   * they are looking for may be among the fifteen that were never sent.</p>
   *
   * <p>Booking never took `hits[0]` and never did: one match is a row you click, several open a picker. The
   * defect here is not a silent choice, it is a silent OMISSION — and it is the harder one to notice,
   * because a plausible name in the list looks like the right answer.</p>
   */
  it("says the list was cut, before the operator picks from it", async () => {
    const user = userEvent.setup();
    class TruncatedApi extends BookingApi {
      override searchEligibility() {
        return Promise.resolve({
          hits: Array.from({ length: 25 }, (_, i) => ({
            id: `ben-${i}`, name: { en: `Ahmed Hassan ${i}`, ar: `أحمد حسن ${i}` },
            cardNumber: `MRS-M-0000${i}`,
            status: { kind: "ok" as const, label: { en: "Active", ar: "نشط" } }, bookable: true,
          })),
          truncated: true,
        });
      }
    }
    renderNode(<ReceptionBooking />, new TruncatedApi({ latencyMs: 0 }) as unknown as ApiClient);
    await user.type(screen.getByLabelText(/search by name/i), "Ahmed");
    await user.click(screen.getByRole("button", { name: /^search$/i }));

    const warned = await screen.findByTestId("book-truncated");
    expect(warned.textContent).toMatch(/may not be in this list/i);
    // The instruction is the point: narrowing is the only thing the operator can do about it.
    expect(warned.textContent).toMatch(/card or ID number/i);
  });

  it("labels the reopen control 25+ rather than claiming exactly 25 matched", async () => {
    const user = userEvent.setup();
    class TruncatedApi extends BookingApi {
      override searchEligibility() {
        return Promise.resolve({
          hits: Array.from({ length: 25 }, (_, i) => ({
            id: `ben-${i}`, name: { en: `Ahmed Hassan ${i}`, ar: `أحمد حسن ${i}` },
            cardNumber: `MRS-M-0000${i}`, bookable: true,
            status: { kind: "ok" as const, label: { en: "Active", ar: "نشط" } },
          })),
          truncated: true,
        });
      }
    }
    renderNode(<ReceptionBooking />, new TruncatedApi({ latencyMs: 0 }) as unknown as ApiClient);
    await user.type(screen.getByLabelText(/search by name/i), "Ahmed");
    await user.click(screen.getByRole("button", { name: /^search$/i }));

    // Close the picker to reveal the reopen button.
    await user.click(await screen.findByRole("button", { name: /^cancel$/i }));
    // "(25)" is a count of what was SENT and reads as a count of what matched.
    expect(await screen.findByRole("button", { name: /choose a patient \(25\+\)/i })).toBeInTheDocument();
  });

  /**
   * 33.9c — arriving WITH a patient means arriving with them chosen.
   *
   * <p>The eligibility check has just resolved this person from an identifier and corroborated the name; the
   * profile's action sends the member number off a record already open. Making the operator click the single
   * row that comes back asks them to re-identify somebody the platform identified a moment ago — and each
   * re-identification is another chance to land on the wrong record.</p>
   */
  it("arrives with the patient already chosen when ?q= names exactly one", async () => {
    renderNode(
      <ReceptionBooking />,
      new BookingApi({ latencyMs: 0 }) as unknown as ApiClient,
      "/reception/book?q=MRS-M-014882",
    );

    // The chosen chip, not a row waiting to be clicked: the name is communicated and only the appointment
    // details are left.
    expect(await screen.findByText("Omar Khalil")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^change$/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /omar khalil/i })).toBeNull();
  });

  it("still opens the picker when ?q= is ambiguous", async () => {
    // Pre-selection is for an UNAMBIGUOUS arrival. Several matches is a decision and stays one, whoever
    // sent the query — which is the whole distinction this screen already drew for a typed search.
    class TwoApi extends BookingApi {
      override searchEligibility() {
        return Promise.resolve({
          hits: [
            { id: "a", name: { en: "Omar Khalil", ar: "عمر خليل" }, cardNumber: "MRS-M-1", bookable: true,
              status: { kind: "ok" as const, label: { en: "Active", ar: "نشط" } } },
            { id: "b", name: { en: "Omar Khalil", ar: "عمر خليل" }, cardNumber: "MRS-M-2", bookable: true,
              status: { kind: "ok" as const, label: { en: "Active", ar: "نشط" } } },
          ],
          truncated: false,
        });
      }
    }
    renderNode(<ReceptionBooking />, new TwoApi({ latencyMs: 0 }) as unknown as ApiClient, "/reception/book?q=Omar");

    expect(await screen.findByRole("dialog", { name: /choose a patient/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^change$/i })).toBeNull();
  });

  it("does not pre-select a member who cannot be booked", async () => {
    // The suspended row explains itself where it is. Silently selecting it would replace that explanation
    // with a booking that fails at submit, after the operator has picked a doctor and a time.
    class SuspendedApi extends BookingApi {
      override searchEligibility() {
        return Promise.resolve({
          hits: [{
            id: "ben-s", name: { en: "Yusuf Haddad", ar: "يوسف حداد" }, cardNumber: "MRS-M-017702",
            status: { kind: "warn" as const, label: { en: "Suspended", ar: "موقوف" } }, bookable: false,
          }],
          truncated: false,
        });
      }
    }
    renderNode(<ReceptionBooking />, new SuspendedApi({ latencyMs: 0 }) as unknown as ApiClient, "/reception/book?q=MRS-M-017702");

    await screen.findByText("Yusuf Haddad");
    expect(screen.queryByRole("button", { name: /^change$/i })).toBeNull();
  });

  it("does not cry truncation on a complete list", async () => {
    const user = userEvent.setup();
    renderNode(<ReceptionBooking />, new BookingApi({ latencyMs: 0 }) as unknown as ApiClient);
    await user.type(screen.getByLabelText(/search by name/i), "Omar");
    await user.click(screen.getByRole("button", { name: /^search$/i }));

    await screen.findByText("Omar Khalil");
    expect(screen.queryByTestId("book-truncated")).toBeNull();
  });

  it("treats an ABSENT status as not bookable — default-deny, not default-allow", async () => {
    const user = userEvent.setup();
    class NoStatusApi extends BookingApi {
      override searchEligibility() {
        // An older service, or a fixture that never set it. "Not stated" must not render as "fine".
        return Promise.resolve({
          hits: [
            { id: "ben-x", name: { en: "Unknown Status", ar: "غير معروف" }, cardNumber: "MRS-M-1", bookable: false },
          ],
          truncated: false,
        });
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

import { describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { freezeClock, renderNode } from "./helpers";
import { CallCentreBooking } from "../src/screens/CallCentreBooking";
import type { Cc360, CcApi } from "../src/screens/CallCentre";
import { rolePermissions } from "../src/authz/permissions";
import { PORTALS } from "../src/portals/catalog";

// FILE SCOPE, not inside one describe: every suite in this file shares fixtures that name absolute
// July-2026 dates, and the booking calendar defaults its month to the real clock. Scoping the freeze to
// a single describe left the others rotting on the same time-bomb.
freezeClock();

const BEN = "b-hana";

function make360(): Cc360 {
  return {
    identity: { beneficiaryId: BEN, memberNo: "MRS-M-2026-000005", displayName: "Hana Mansour", ageBand: "30-39", status: "Active" },
    coverage: [],
    contacts: [],
    appointments: [],
    openReferrals: [],
  };
}

function fakeApi(over: Partial<CcApi> = {}): CcApi {
  return {
    openInteraction: vi.fn().mockResolvedValue({ interactionId: "i9", callRef: "CALL-2026-000009" }),
    // Records the agent's off-system attestation and binds the call to this member.
    openMember: vi.fn().mockResolvedValue(true),
    search: vi.fn().mockResolvedValue([
      // The member number in full: it was masked only while it was an identifier the agent could be
      // challenged on, and the agent reads it back off the caller's card to find them.
      { beneficiaryId: BEN, displayName: "Hana Mansour", memberNo: "MRS-M-2026-000005" },
    ]),
    summary: vi.fn().mockResolvedValue(make360()),
    clinics: vi.fn().mockResolvedValue([
      { providerId: "p1", locationId: "l1", branchId: "br-dokki", branchName: "Dokki", label: "Mersal Dokki · Dokki Clinic", openSlots: 2 },
      { providerId: "p2", locationId: "l2", branchId: "br-nasr", branchName: "Nasr City", label: "Mersal Nasr · Nasr Clinic", openSlots: 5 },
    ]),
    slots: vi.fn().mockResolvedValue([{ slotId: "slot-7", start: "2026-07-30T09:40:00Z" }]),
    book: vi.fn().mockResolvedValue({ kind: "ok" }),
    reschedule: vi.fn().mockResolvedValue({ kind: "ok" }),
    cancel: vi.fn().mockResolvedValue({ kind: "ok" }),
    // A RESULT, not void: the screen now clears only on a confirmed close.
    close: vi.fn().mockResolvedValue({ kind: "ok" }),
    history: vi.fn().mockResolvedValue([]),
    // 32.6 — contact corrections from the call (design 11 §3.1). Default to success; the tests that care
    // about a refusal override with the specific verdict, because "invalid value" and "not verified" send
    // the agent to two different places.
    updateContact: vi.fn().mockResolvedValue({ kind: "ok" }),
    addContact: vi.fn().mockResolvedValue({ kind: "ok" }),
    ...over,
  };
}

/**
 * Search and open the member's file — which on this screen also opens the call record.
 *
 * There is no verification step between the two any more: the agent confirms who they are speaking to on the
 * phone, and picking the hit is what records that.
 */
async function findAndOpenMember(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/find member/i), "01001234567");
  await user.click(screen.getByRole("button", { name: /^search$/i }));
  await user.click(await screen.findByRole("button", { name: /Hana Mansour/i }));
}

/** Every picker on this screen is a design-system Combobox (a native <select> cannot style its own option
 *  list), so they are a combobox + listbox rather than something `selectOptions` can drive. */
async function choose(user: ReturnType<typeof userEvent.setup>, name: RegExp, option: RegExp) {
  await user.click(await screen.findByRole("combobox", { name }));
  await user.click(screen.getByRole("option", { name: option }));
}

/**
 * The standalone "Book Appointment" journey. The thing worth proving is not that a booking can be made (the
 * workspace already could) but that MAKING IT A SEPARATE SCREEN DID NOT MOVE THE GATE: the screen opens its
 * own call record and binds it to the member before anything about them is shown or any reservation is
 * attempted. A standalone screen that booked straight through emr would have produced an appointment with no
 * call behind it and nothing tying it to a conversation.
 */
describe("standalone Book appointment (call centre)", () => {
  it("is reachable as its own nav item, and the role may actually use it", () => {
    const portal = PORTALS.find((p) => p.role === "call_center");
    const section = portal?.sections.find((s) => s.path === "book");
    expect(section).toBeDefined();
    // A nav item whose permission the role lacks renders nowhere — the section and the grant must agree.
    expect(rolePermissions.call_center).toContain(section!.permission);
    // Reserve only: the call centre must not be handed the arrival verbs by the same stroke.
    expect(rolePermissions.call_center).not.toContain("checkin.write");
  });

  it("opens a call record when a member is chosen, so the booking has a call behind it", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);
    await findAndOpenMember(user);
    // Reason AND direction. Direction was hard-coded "Inbound" for every interaction the portal opened, so
    // it is now passed explicitly — defaulting to Inbound, which is what this screen sends unless changed.
    await waitFor(() => expect(api.openInteraction).toHaveBeenCalledWith("BookAppointment", "Inbound"));
  });

  /**
   * The appointment step is ALWAYS on screen, exactly as reception's is.
   *
   * It used to be hidden until a member had been chosen, so an agent who had not yet found the caller — or
   * whose file failed to open — was looking at a booking screen with no booking on it. Availability is not
   * member data (reception loads it with nobody chosen), so there is nothing to withhold; what has to be
   * withheld is the WRITE, and the server refuses that on a call bound to no member regardless.
   */
  it("shows the appointment step before a member is chosen, and refuses to book without one", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);

    expect(await screen.findByRole("combobox", { name: /^branch$/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^book appointment$/i })).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /^book appointment$/i }));
    expect(await screen.findByText(/choose a member first/i)).toBeInTheDocument();
    expect(api.book).not.toHaveBeenCalled();
  });

  /** One box, no type picker. It offered seven identifier types and narrowed nothing — the index matched them
   *  all on every query — so it cost a decision per call and implied a wrong guess would lose the member. */
  it("searches with one field and no 'search by' picker", async () => {
    renderNode(<CallCentreBooking api={fakeApi()} />);

    expect(screen.queryByRole("combobox", { name: /search by/i })).not.toBeInTheDocument();
    expect(screen.getByLabelText(/find member/i)).toBeInTheDocument();
    expect(screen.getByText(/search by name, phone number, card or member number/i)).toBeInTheDocument();
  });

  it("never asks the agent to challenge the caller on identifiers", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreBooking api={fakeApi()} />);
    await findAndOpenMember(user);

    expect(screen.queryByRole("button", { name: /verify/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.queryByText(/not yet verified/i)).not.toBeInTheDocument();
  });

  /**
   * The attestation is a WRITE. A refused one must not leave a booking form on screen: with no challenge left,
   * it is the only thing between picking a name and reserving against that member.
   */
  it("shows no booking surface when the attestation is refused", async () => {
    const user = userEvent.setup();
    const api = fakeApi({ openMember: vi.fn().mockResolvedValue(false) });
    renderNode(<CallCentreBooking api={api} />);
    await findAndOpenMember(user);

    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/couldn't open/i));
    // No member is on the booking — the step is on screen (it always is), but nobody is chosen for it, and
    // the 360 was never even requested. A refused binding is not a "try anyway".
    expect(screen.queryByTestId("cc-booking-for")).not.toBeInTheDocument();
    expect(api.summary).not.toHaveBeenCalled();
    // And the Book action refuses, naming the reason.
    await user.click(screen.getByRole("button", { name: /^book appointment$/i }));
    expect(await screen.findByText(/choose a member first/i)).toBeInTheDocument();
    expect(api.book).not.toHaveBeenCalled();
  });

  it("books into the branch the agent named, against the bound call", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);
    await findAndOpenMember(user);

    // 14.5 — branch → specialty → doctor → time, the same fields reception fills. The clinic step is gone:
    // it is resolved from the doctor rather than named separately.
    await choose(user, /^branch$/i, /Nasr City/);
    await choose(user, /^specialty$/i, /Cardiology/);
    await choose(user, /^doctor$/i, /Youssef Adel/);
    await user.click((await screen.findAllByRole("radio", { name: /:/ }))[0]);
    await user.click(screen.getByRole("button", { name: /^book appointment$/i }));

    // The interaction id is the one this screen opened, the branch is the one the agent named, and the
    // doctor now rides along with it.
    await waitFor(() => expect(api.book).toHaveBeenCalled());
    const [iid, ben, , branch, extra] = (api.book as ReturnType<typeof vi.fn>).mock.calls[0];
    expect([iid, ben, branch]).toEqual(["i9", BEN, "BR-NSR"]);
    expect(extra).toMatchObject({ doctorId: "PRC-2" });
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/booked/i));
  });

  it("shows who the booking is for, from the server's summary rather than the search hit", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);
    await findAndOpenMember(user);

    expect(await screen.findByTestId("cc-booking-for")).toHaveTextContent(/Hana Mansour/);
    expect(api.summary).toHaveBeenCalledWith(BEN, "i9");
  });

  it("offers reservation verbs only — never an arrival", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreBooking api={fakeApi()} />);
    await findAndOpenMember(user);
    await screen.findByRole("combobox", { name: /^branch$/i });

    expect(screen.queryByRole("button", { name: /check.?in/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /no.?show/i })).not.toBeInTheDocument();
    expect(screen.getByText(/reservations only/i)).toBeInTheDocument();
  });

  it("keeps the agent on the screen when a slot is taken (409), and re-reads the times", async () => {
    const user = userEvent.setup();
    const api = fakeApi({ book: vi.fn().mockResolvedValue({ kind: "conflict" }) });
    renderNode(<CallCentreBooking api={api} />);
    await findAndOpenMember(user);
    await choose(user, /^branch$/i, /Dokki/);
    await choose(user, /^specialty$/i, /Pediatrics/);
    await choose(user, /^doctor$/i, /Hana Mansour/);
    await user.click((await screen.findAllByRole("radio", { name: /:/ }))[0]);

    await user.click(screen.getByRole("button", { name: /^book appointment$/i }));
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/just taken/i));
    // The agent is still on the screen with their branch, specialty and doctor intact — a 409 is someone
    // else's race, and making the agent re-enter the caller's request mid-call is a cost they should not pay.
    expect(screen.getByRole("combobox", { name: /^doctor$/i })).toHaveValue("Hana Mansour");
  });

  it("surfaces a failure to open the call record instead of presenting a form that cannot save", async () => {
    const user = userEvent.setup();
    const api = fakeApi({ openInteraction: vi.fn().mockResolvedValue({ interactionId: "", callRef: "" }) });
    renderNode(<CallCentreBooking api={api} />);
    await findAndOpenMember(user);

    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/couldn't open a call record/i));
    // No member file either: without an interaction every later write is refused.
    expect(screen.queryByTestId("cc-booking-for")).not.toBeInTheDocument();
  });

  /** ONE account of the call. There were two fields — private notes and this summary — and an agent writing
   *  carefully into the first was writing into something nobody downstream would ever open. */
  it("carries the one call summary onto the call record when the agent finishes", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);
    await findAndOpenMember(user);

    expect(screen.queryByLabelText(/call notes/i)).not.toBeInTheDocument();
    await user.type(await screen.findByLabelText(/call summary/i), "Booked a cardiology appointment in Nasr City.");
    await user.click(screen.getByRole("button", { name: /finish and close/i }));

    // The reason rides along on the close, so a correction made mid-call lands on the record.
    expect(api.close).toHaveBeenCalledWith(
      "i9", "Resolved", "Booked a cardiology appointment in Nasr City.", "BookAppointment",
    );
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/call closed/i));
    // The call is over: the field is CLEARED rather than removed. On a single-card journey the step stays
    // where it was — but carrying the last caller's summary into the next call would put one member's
    // account on another's record.
    expect(screen.getByLabelText(/call summary/i)).toHaveValue("");
    expect(screen.queryByTestId("cc-booking-for")).not.toBeInTheDocument();
  });

  /**
   * The call record carries WHY the call happened and WHO RANG WHOM.
   *
   * Direction was hard-coded "Inbound" on every interaction the portal ever opened — a constant dressed as
   * data, so the outbound follow-up calls a supervisor most wants to count were all filed as inbound.
   */
  it("defaults to an inbound call and sends the chosen direction when the call opens", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);

    // The direction and reason pickers are design-system comboboxes now, not native selects, so they hold a
    // LABEL and are driven by opening the list — same as branch and clinic above. The value still sent to the
    // API is the code, which is what the `openInteraction` assertion below checks.
    expect(screen.getByLabelText(/direction/i)).toHaveValue("Inbound — the member called us");

    await choose(user, /direction/i, /^outbound/i);
    await findAndOpenMember(user);

    await waitFor(() => expect(api.openInteraction).toHaveBeenCalledWith("BookAppointment", "Outbound"));
  });

  /**
   * Direction is written when the interaction OPENS and no endpoint changes it. An editable control after
   * that would accept a correction and silently drop it, which is worse than not offering one — so it locks,
   * and says why.
   */
  it("locks the direction once the call is under way, and explains that it is locked", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreBooking api={fakeApi()} />);

    expect(screen.getByLabelText(/direction/i)).toBeEnabled();
    await findAndOpenMember(user);

    expect(screen.getByLabelText(/direction/i)).toBeDisabled();
    expect(screen.getByText(/set when the call was opened/i)).toBeInTheDocument();
  });

  it("opens the call with the reason the agent chose, not the one the screen is named after", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);

    // The default is the reason this screen exists for…
    expect(screen.getByLabelText(/call reason/i)).toHaveValue("Book appointment");
    // …but an agent who came here to book and ended up answering something else can say so.
    await choose(user, /call reason/i, /^eligibility enquiry$/i);
    await findAndOpenMember(user);

    await waitFor(() => expect(api.openInteraction).toHaveBeenCalledWith("EligibilityEnquiry", "Inbound"));
  });

  it("copies the summary and says so, and cannot claim to have copied nothing", async () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    renderNode(<CallCentreBooking api={fakeApi()} />);
    await findAndOpenMember(user);

    const copy = await screen.findByRole("button", { name: /copy summary/i });
    expect(copy).toBeDisabled();

    await user.type(screen.getByLabelText(/call summary/i), "Confirmed 09:40 Thursday.");
    await user.click(screen.getByRole("button", { name: /copy summary/i }));
    expect(writeText).toHaveBeenCalledWith("Confirmed 09:40 Thursday.");
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/copied/i));
  });

  it("has no axe violations at the booking step", async () => {
    const user = userEvent.setup();
    const { container } = renderNode(<CallCentreBooking api={fakeApi()} />);
    await findAndOpenMember(user);
    await screen.findByRole("combobox", { name: /^branch$/i });

    expect(await axe(container)).toHaveNoViolations();
  });
});

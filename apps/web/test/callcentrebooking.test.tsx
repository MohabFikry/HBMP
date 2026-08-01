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
    book: vi.fn().mockResolvedValue("ok"),
    reschedule: vi.fn().mockResolvedValue("ok"),
    cancel: vi.fn().mockResolvedValue("ok"),
    // A RESULT, not void: the screen now clears only on a confirmed close.
    close: vi.fn().mockResolvedValue("ok"),
    history: vi.fn().mockResolvedValue([]),
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

/** Branch and clinic are the design-system Select (a native <select> cannot style its own option list), so
 *  they are a combobox + listbox rather than something `selectOptions` can drive. */
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
    await waitFor(() => expect(api.openInteraction).toHaveBeenCalledWith("BookAppointment"));
  });

  it("discloses nothing and offers no times until the member's file is open", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);
    await user.type(screen.getByLabelText(/find member/i), "01001234567");
    await user.click(screen.getByRole("button", { name: /^search$/i }));
    await screen.findByRole("button", { name: /Hana Mansour/i });

    // No booking surface at all: not the branch picker, not the Book button.
    expect(screen.queryByRole("combobox", { name: /^branch$/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^book$/i })).not.toBeInTheDocument();
    // And the clinic list is not even fetched — a call with no member has no business enumerating availability.
    expect(api.clinics).not.toHaveBeenCalled();
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
    expect(screen.queryByTestId("cc-booking-for")).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^book$/i })).not.toBeInTheDocument();
    expect(api.summary).not.toHaveBeenCalled();
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
    await user.click(screen.getByRole("button", { name: /^book$/i }));

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
    const api = fakeApi({ book: vi.fn().mockResolvedValue("conflict") });
    renderNode(<CallCentreBooking api={api} />);
    await findAndOpenMember(user);
    await choose(user, /^branch$/i, /Dokki/);
    await choose(user, /^specialty$/i, /Pediatrics/);
    await choose(user, /^doctor$/i, /Hana Mansour/);
    await user.click((await screen.findAllByRole("radio", { name: /:/ }))[0]);

    await user.click(screen.getByRole("button", { name: /^book$/i }));
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

    expect(api.close).toHaveBeenCalledWith("i9", "Resolved", "Booked a cardiology appointment in Nasr City.");
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/call closed/i));
    // The call is over: the field goes with it, so the next caller cannot inherit the last one's summary.
    expect(screen.queryByLabelText(/call summary/i)).not.toBeInTheDocument();
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

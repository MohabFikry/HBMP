import { describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { CallCentreBooking } from "../src/screens/CallCentreBooking";
import type { Cc360, CcApi } from "../src/screens/CallCentre";
import { rolePermissions } from "../src/authz/permissions";
import { PORTALS } from "../src/portals/catalog";

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
    verify: vi.fn().mockImplementation((_i, _b, types: string[], pass: boolean) => Promise.resolve(pass && types.length >= 2)),
    search: vi.fn().mockResolvedValue([
      { beneficiaryId: BEN, displayName: "Hana Mansour", memberNo: "MRS-M-2026-000005", challengeableIdentifierTypes: ["MemberNo", "DateOfBirth", "Phone"] },
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
    close: vi.fn().mockResolvedValue(undefined),
    history: vi.fn().mockResolvedValue([]),
    ...over,
  };
}

async function findAndSelect(user: ReturnType<typeof userEvent.setup>) {
  await user.type(screen.getByLabelText(/find member/i), "01001234567");
  await user.click(screen.getByRole("button", { name: /^search$/i }));
  await user.click(await screen.findByRole("button", { name: /Hana Mansour/i }));
}

async function verifyPass(user: ReturnType<typeof userEvent.setup>) {
  await user.click(await screen.findByLabelText("Member number"));
  await user.click(screen.getByLabelText("Date of birth"));
  await user.click(screen.getByRole("button", { name: /verify — pass/i }));
}

/**
 * Phase 20.4 — the standalone "Book appointment" journey. The thing worth proving is not that a booking can be
 * made (the workspace already could) but that MAKING IT A SEPARATE SCREEN DID NOT MOVE THE VERIFICATION GATE:
 * the screen opens its own call record and records a PASS before anything about the member is shown or any
 * reservation is attempted. A standalone screen that booked straight through emr would have produced an
 * appointment with no call and no verification behind it.
 */
describe("20.4 — standalone Book appointment (call centre)", () => {
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
    await findAndSelect(user);
    await waitFor(() => expect(api.openInteraction).toHaveBeenCalledWith("BookAppointment"));
  });

  it("discloses nothing about the member and offers no times before a verification pass", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);
    await findAndSelect(user);

    expect(await screen.findByTestId("cc-lockchip")).toBeInTheDocument();
    // No booking surface at all: not the branch picker, not the Book button.
    expect(screen.queryByLabelText(/^branch$/i)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^book$/i })).not.toBeInTheDocument();
    // And the clinic list is not even fetched — an unverified call has no business enumerating availability.
    expect(api.clinics).not.toHaveBeenCalled();
  });

  it("refuses to pass on a single identifier", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);
    await findAndSelect(user);
    await user.click(await screen.findByLabelText("Member number"));
    await user.click(screen.getByRole("button", { name: /verify — pass/i }));

    expect(screen.getByRole("alert")).toHaveTextContent(/at least two/i);
    expect(api.verify).not.toHaveBeenCalled();
  });

  it("books into the branch the agent named, against the verified call", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);
    await findAndSelect(user);
    await verifyPass(user);

    await user.selectOptions(await screen.findByLabelText(/^branch$/i), "br-nasr");
    await user.selectOptions(screen.getByLabelText(/clinic/i), "p2|l2");
    await user.click((await screen.findAllByRole("radio"))[0]);
    await user.click(screen.getByRole("button", { name: /^book$/i }));

    // The interaction id is the one this screen opened, and the branch travels with the clinic.
    expect(api.book).toHaveBeenCalledWith("i9", BEN, "slot-7", "br-nasr");
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/booked/i));
  });

  it("shows who the booking is for, from the server's summary rather than the search hit", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);
    await findAndSelect(user);
    await verifyPass(user);

    expect(await screen.findByTestId("cc-booking-for")).toHaveTextContent(/Hana Mansour/);
    expect(api.summary).toHaveBeenCalledWith(BEN, "i9");
  });

  it("offers reservation verbs only — never an arrival", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreBooking api={fakeApi()} />);
    await findAndSelect(user);
    await verifyPass(user);
    await screen.findByLabelText(/^branch$/i);

    expect(screen.queryByRole("button", { name: /check.?in/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /no.?show/i })).not.toBeInTheDocument();
    expect(screen.getByText(/reservations only/i)).toBeInTheDocument();
  });

  it("keeps the agent on the screen when a slot is taken (409), and re-reads the times", async () => {
    const user = userEvent.setup();
    const api = fakeApi({ book: vi.fn().mockResolvedValue("conflict") });
    renderNode(<CallCentreBooking api={api} />);
    await findAndSelect(user);
    await verifyPass(user);
    await user.selectOptions(await screen.findByLabelText(/^branch$/i), "br-dokki");
    await user.selectOptions(screen.getByLabelText(/clinic/i), "p1|l1");
    await user.click((await screen.findAllByRole("radio"))[0]);

    const before = (api.slots as ReturnType<typeof vi.fn>).mock.calls.length;
    await user.click(screen.getByRole("button", { name: /^book$/i }));
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/just taken/i));
    // A 409 proves the loaded list is stale — leaving the dead choice selected invites a second failure.
    expect((api.slots as ReturnType<typeof vi.fn>).mock.calls.length).toBeGreaterThan(before);
  });

  it("surfaces a failure to open the call record instead of presenting a form that cannot save", async () => {
    const user = userEvent.setup();
    const api = fakeApi({ openInteraction: vi.fn().mockResolvedValue({ interactionId: "", callRef: "" }) });
    renderNode(<CallCentreBooking api={api} />);
    await findAndSelect(user);

    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/couldn't open a call record/i));
    // No verify step either: without an interaction every later write is refused.
    expect(screen.queryByTestId("cc-lockchip")).not.toBeInTheDocument();
  });

  it("carries the call notes onto the call record when the agent finishes", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreBooking api={api} />);
    await findAndSelect(user);
    await user.type(await screen.findByLabelText(/call notes/i), "Asked for the earliest Dokki slot.");
    await user.click(screen.getByRole("button", { name: /finish and close/i }));

    expect(api.close).toHaveBeenCalledWith("i9", "Resolved", "Asked for the earliest Dokki slot.");
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/call closed/i));
    // The call is over: its notes field goes with it, so the next caller cannot inherit the last one's note.
    expect(screen.queryByLabelText(/call notes/i)).not.toBeInTheDocument();
  });

  it("copies the notes and says so, and cannot claim to have copied nothing", async () => {
    const user = userEvent.setup();
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    renderNode(<CallCentreBooking api={fakeApi()} />);
    await findAndSelect(user);

    const copy = await screen.findByRole("button", { name: /copy notes/i });
    expect(copy).toBeDisabled();

    await user.type(screen.getByLabelText(/call notes/i), "Confirmed 09:40 Thursday.");
    await user.click(screen.getByRole("button", { name: /copy notes/i }));
    expect(writeText).toHaveBeenCalledWith("Confirmed 09:40 Thursday.");
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/copied/i));
  });

  it("has no axe violations at the booking step", async () => {
    const user = userEvent.setup();
    const { container } = renderNode(<CallCentreBooking api={fakeApi()} />);
    await findAndSelect(user);
    await verifyPass(user);
    await screen.findByLabelText(/^branch$/i);

    expect(await axe(container)).toHaveNoViolations();
  });
});

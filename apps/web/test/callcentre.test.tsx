import { describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { freezeClock, renderNode } from "./helpers";
import { CallCentreWorkspace, CallHistory, type CcApi, type Cc360 } from "../src/screens/CallCentre";

// FILE SCOPE, not inside one describe: every suite in this file shares fixtures that name absolute
// July-2026 dates, and the booking calendar defaults its month to the real clock. Scoping the freeze to
// a single describe left the others rotting on the same time-bomb.
freezeClock();

const BEN = "b-amal";

function make360(): Cc360 {
  return {
    identity: { beneficiaryId: BEN, memberNo: "MRS-M-1001", displayName: "Amal Hassan", ageBand: "30-39", status: "Active" },
    coverage: [{ category: "Outpatient", annualLimit: 10000, remainingLimit: 7500 }],
    contacts: [{ contactId: "c1", kind: "Phone", value: "+20100000000", isPrimary: true }],
    appointments: [
      { appointmentId: "a1", appointmentType: "Consultation", status: "Scheduled", scheduledStart: new Date().toISOString(), branchName: "Aswan", doctorName: "Dr. Nour", specialty: "Cardiology", canReschedule: true, canCancel: true, rowVersion: 41 },
      { appointmentId: "a2", appointmentType: "Consultation", status: "Completed", scheduledStart: new Date().toISOString(), branchName: "Maadi", doctorName: "Dr. Sami", specialty: "Dermatology", canReschedule: false, canCancel: false, rowVersion: 7 },
    ],
    openReferrals: [{ referralRef: "REF-2026-000007", status: "Requested", requestedSpecialty: "Endocrinology" }],
  };
}

/**
 * Choose a clinic and a time. Reserving used to need neither — the slot id was invented with
 * crypto.randomUUID(), so every call-centre booking named a slot that could not exist and emr answered 404.
 * The Book button is disabled until a real one is picked, so these steps are the contract now.
 */
async function openMemberAndReserve(user: ReturnType<typeof userEvent.setup>) {
  await startAndOpenMember(user);
  await openReservePanel(user);
}

/**
 * The reservation panel is an ACTION on the member file now, not a permanent fixture inside it: most calls are
 * not bookings, so a booking form open under every member's appointment list is noise and an invitation to
 * book by accident. Idempotent — the button is gone once the panel is showing — so helpers may both call it.
 */
async function openReservePanel(user: ReturnType<typeof userEvent.setup>) {
  const open = screen.queryByRole("button", { name: /new appointment/i });
  if (open) await user.click(open);
}

/**
 * Every picker on this screen is a design-system Combobox, not a native <select>: a native one draws its
 * option list in the OS, so it cannot wear the Mersal surface at all. That makes it a combobox + listbox
 * rather than something `selectOptions` can drive, and these helpers are what the agent actually does.
 */
async function choose(user: ReturnType<typeof userEvent.setup>, name: RegExp, option: RegExp) {
  await user.click(await screen.findByRole("combobox", { name }));
  await user.click(screen.getByRole("option", { name: option }));
}

async function optionsOf(user: ReturnType<typeof userEvent.setup>, name: RegExp) {
  await user.click(await screen.findByRole("combobox", { name }));
  const names = screen.getAllByRole("option").map((o) => o.textContent ?? "");
  await user.keyboard("{Escape}");
  return names;
}

/** The times for the chosen day — scoped, because the day strip is radios too. */
function timeButtons() {
  return within(screen.getByRole("radiogroup", { name: /available times/i })).getAllByRole("radio");
}

/**
 * 14.5 — branch → specialty → doctor → time, the shared form. The clinic step is gone: it is resolved from
 * the chosen doctor rather than named separately, so the two controls can no longer disagree about where the
 * patient is expected.
 */
async function pickClinicAndTime(user: ReturnType<typeof userEvent.setup>) {
  await openReservePanel(user);
  await choose(user, /^branch$/i, /Dokki/);
  await choose(user, /^specialty$/i, /Pediatrics/);
  await choose(user, /^doctor$/i, /Hana Mansour/);
  await waitFor(() => expect(timeButtons().length).toBeGreaterThan(0));
  await user.click(timeButtons()[0]);
}

function fakeApi(over: Partial<CcApi> = {}): CcApi {
  return {
    openInteraction: vi.fn().mockResolvedValue({ interactionId: "i1", callRef: "CALL-2026-000001" }),
    // Records the agent's off-system attestation and binds the call. No identifier types, no pass/fail.
    openMember: vi.fn().mockResolvedValue(true),
    // The member number arrives in full: it was masked only while it was an identifier the agent could be
    // challenged on, and the agent needs it to tell two people with the same name apart.
    search: vi.fn().mockResolvedValue([{ beneficiaryId: BEN, displayName: "Amal Hassan", memberNo: "MRS-M-1001" }]),
    summary: vi.fn().mockResolvedValue(make360()),
    // Cross-branch clinic list, each option carrying its own branch (15.3).
    clinics: vi.fn().mockResolvedValue([
      { providerId: "p1", locationId: "l1", branchId: "br-dokki", branchName: "Dokki", label: "Mersal Dokki · Dokki Clinic", openSlots: 2 },
      // A second BRANCH, so the branch step is a real choice rather than a formality.
      { providerId: "p2", locationId: "l2", branchId: "br-nasr", branchName: "Nasr City", label: "Mersal Nasr · Nasr Clinic", openSlots: 5 },
    ]),
    slots: vi.fn().mockResolvedValue([{ slotId: "slot-1", start: "2026-07-22T11:00:00Z" }]),
    book: vi.fn().mockResolvedValue({ kind: "ok" }),
    reschedule: vi.fn().mockResolvedValue({ kind: "ok" }),
    cancel: vi.fn().mockResolvedValue({ kind: "ok" }),
    // Returns a RESULT now. It used to resolve `undefined` for every call, which is what let the missing
    // `summary` argument survive: the server had required one since 20.3b and refused every close with 422,
    // and no test could see it because the fake always succeeded.
    close: vi.fn().mockResolvedValue({ kind: "ok" }),
    history: vi.fn().mockResolvedValue([]),
    ...over,
  };
}

/** Start a call and search, stopping BEFORE a member is chosen. */
async function startAndSearch(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole("button", { name: /start call/i }));
  await user.type(await screen.findByLabelText(/find member/i), "+20100000000");
  await user.click(screen.getByRole("button", { name: /^search$/i }));
}

/**
 * Start a call, search, and open the member's file.
 *
 * Picking the hit IS the whole gesture now — the identifier-challenge step between the two is gone, because
 * the agent confirms who they are speaking to on the phone.
 */
async function startAndOpenMember(user: ReturnType<typeof userEvent.setup>) {
  await startAndSearch(user);
  await user.click(await screen.findByRole("button", { name: /Amal Hassan/ }));
  await screen.findByTestId("cc-360");
}

describe("Call Centre workspace: opening a member's file", () => {
  /**
   * A search hit is a way to pick the right person, not a disclosure. It carries a name and a member number
   * and nothing else — the same rule as before, minus the challenge the agent no longer administers.
   */
  it("discloses nothing about a member until their file is opened", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await startAndSearch(user);

    // The hit itself is present…
    expect(await screen.findByRole("button", { name: /Amal Hassan/ })).toBeInTheDocument();
    // …but no coverage / appointment / contact detail is anywhere in the DOM.
    expect(screen.queryByTestId("cc-360")).not.toBeInTheDocument();
    expect(screen.queryByText(/Dr\. Nour/)).not.toBeInTheDocument();
    expect(screen.queryByText(/Outpatient/)).not.toBeInTheDocument();
  });

  /** The identifier challenge is GONE, not merely bypassed. If any of it comes back by accident — a
   *  fieldset, a checkbox, a Pass button — this is what says so. */
  it("never asks the agent to challenge the caller on identifiers", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await startAndSearch(user);

    expect(screen.queryByRole("button", { name: /verify/i })).not.toBeInTheDocument();
    expect(screen.queryByRole("checkbox")).not.toBeInTheDocument();
    expect(screen.queryByText(/not yet verified/i)).not.toBeInTheDocument();
    // It says what the click means instead.
    expect(screen.getByText(/confirm who you are speaking to/i)).toBeInTheDocument();
  });

  it("records the attestation, binds the call, and shows the cross-branch 360", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreWorkspace api={api} />);
    await startAndOpenMember(user);

    // ONE call, carrying the interaction and the beneficiary — nothing else to get wrong.
    expect(api.openMember).toHaveBeenCalledWith("i1", BEN);

    // Appointments from every branch are shown.
    expect(screen.getByText(/Aswan/)).toBeInTheDocument();
    expect(screen.getByText(/Maadi/)).toBeInTheDocument();
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/file open/i));
  });

  /**
   * The attestation is a WRITE, and a refused write must not leave a member's file on screen.
   *
   * This is the failure the flow has to get right: with the challenge gone, the attestation is the only thing
   * standing between picking a name and reading a file, so a client that renders optimistically would disclose
   * on a call the server never bound.
   */
  it("shows nothing about the member when the attestation is refused", async () => {
    const user = userEvent.setup();
    const api = fakeApi({ openMember: vi.fn().mockResolvedValue(false) });
    renderNode(<CallCentreWorkspace api={api} />);
    await startAndSearch(user);
    await user.click(await screen.findByRole("button", { name: /Amal Hassan/ }));

    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/couldn't open/i));
    expect(screen.queryByTestId("cc-360")).not.toBeInTheDocument();
    expect(screen.queryByText(/Dr\. Nour/)).not.toBeInTheDocument();
    // And the 360 was never even requested — a refused binding is not a "try anyway".
    expect(api.summary).not.toHaveBeenCalled();
  });

  /** One box, no type picker. The index matches every identifier at once, so a picker only ever cost the
   *  agent a decision and implied that guessing wrong would lose the member. */
  it("searches with one field and no 'search by' picker", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreWorkspace api={api} />);
    await user.click(screen.getByRole("button", { name: /start call/i }));

    expect(screen.queryByRole("combobox", { name: /search by/i })).not.toBeInTheDocument();
    const box = await screen.findByLabelText(/find member/i);
    // The help text names what one box actually matches, including the name a caller offers first.
    expect(screen.getByText(/search by name, phone number, card or member number/i)).toBeInTheDocument();

    await user.type(box, "Amal Hassan");
    await user.click(screen.getByRole("button", { name: /^search$/i }));
    expect(api.search).toHaveBeenCalledWith("Amal Hassan");
  });
});

describe("15.5 — Call Centre workspace: act", () => {
  it("shows a clear recoverable state when a slot was just taken (409)", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi({ book: vi.fn().mockResolvedValue({ kind: "conflict" }) })} />);
    await startAndOpenMember(user);

    await pickClinicAndTime(user);
    await user.click(screen.getByRole("button", { name: /^book$/i }));
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/just taken/i));
  });

  it("requires a cancellation reason", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreWorkspace api={api} />);
    await startAndOpenMember(user);

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
    await startAndOpenMember(user);

    // Only the changeable appointment (a1) offers Reschedule.
    await pickClinicAndTime(user);
    await user.click(screen.getByRole("button", { name: /^reschedule$/i }));
    // A REAL slot id now, taken from the picker rather than generated — plus a1's rowVersion, which rides
    // along as If-Match so a reschedule computed against a stale file is refused rather than applied.
    expect(api.reschedule).toHaveBeenCalledWith("i1", "a1", "slot-1", 41);
    await waitFor(() => expect(screen.getByTestId("cc-live")).toHaveTextContent(/rescheduled/i));
  });

  it("surfaces a recoverable state when the new slot was just taken (409)", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi({ reschedule: vi.fn().mockResolvedValue({ kind: "conflict" }) })} />);
    await startAndOpenMember(user);

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
    await openMemberAndReserve(user);

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
    await openMemberAndReserve(user);

    // The button is disabled rather than sending an invented slot id, which is what it used to do.
    expect(screen.getByRole("button", { name: /^book$/i })).toBeDisabled();
    await user.click(screen.getByRole("button", { name: /^book$/i }));
    expect(api.book).not.toHaveBeenCalled();

    await pickClinicAndTime(user);
    expect(screen.getByRole("button", { name: /^book$/i })).toBeEnabled();
    await user.click(screen.getByRole("button", { name: /^book$/i }));
    // The agent named the branch, and the doctor they picked rides along with it.
    await waitFor(() => expect(api.book).toHaveBeenCalled());
    const [iid, ben, , branch] = (api.book as ReturnType<typeof vi.fn>).mock.calls[0];
    expect([iid, ben, branch]).toEqual(["i1", BEN, "BR-DOK"]);
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
  it("has no serious/critical a11y violations at the search step", async () => {
    const user = userEvent.setup();
    const { container } = renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await startAndSearch(user);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });

  it("has no serious/critical a11y violations with a member's file open", async () => {
    const user = userEvent.setup();
    const { container } = renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await startAndOpenMember(user);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

/**
 * The branch belongs to the DECISION, not to the chrome. An app-bar "all branches" chip states the scope and
 * changes nothing; what the agent actually needs is to name the branch the appointment is FOR, at the moment
 * they make it — and then to be offered only clinics in that branch.
 */
describe("15.3 — the call centre names the branch it is booking into", () => {
  it("offers every branch, and no specialty until one is chosen", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await openMemberAndReserve(user);

    // The specialty picker is inert until a branch is named — a specialty list spanning branches is how
    // someone books Maadi for a caller expecting Dokki.
    expect(screen.getByRole("combobox", { name: /^specialty$/i })).toBeDisabled();

    // Opened ONCE and chosen from the same list: the combobox filters on typed text, so opening, escaping
    // and reopening is a different interaction from the one the agent performs.
    await user.click(await screen.findByRole("combobox", { name: /^branch$/i }));
    const branchNames = screen.getAllByRole("option").map((o) => o.textContent ?? "");
    expect(branchNames.some((n) => /Dokki/.test(n))).toBe(true);
    expect(branchNames.some((n) => /Nasr City/.test(n))).toBe(true);
    await user.click(screen.getByRole("option", { name: /Nasr City/ }));

    await waitFor(() => expect(screen.getByRole("combobox", { name: /^specialty$/i })).toBeEnabled());
  });

  it("offers only the chosen branch's doctors", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await openMemberAndReserve(user);

    await choose(user, /^branch$/i, /Nasr City/);
    await choose(user, /^specialty$/i, /Cardiology/);
    const doctors = await optionsOf(user, /^doctor$/i);

    // Youssef works at Nasr City; Hana does not.
    expect(doctors.some((n) => /Youssef Adel/.test(n))).toBe(true);
    expect(doctors.some((n) => /Hana Mansour/.test(n))).toBe(false);
  });

  it("changing the branch clears the specialty, the doctor and the times under them", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await openMemberAndReserve(user);

    await choose(user, /^branch$/i, /Dokki/);
    await choose(user, /^specialty$/i, /Pediatrics/);
    await choose(user, /^doctor$/i, /Hana Mansour/);
    await waitFor(() => expect(timeButtons().length).toBeGreaterThan(0));
    await user.click(timeButtons()[0]);
    expect(screen.getByRole("button", { name: /^book$/i })).toBeEnabled();

    await choose(user, /^branch$/i, /Nasr City/);

    // Nothing carried over — the whole chain below the branch is dropped in one update, so there is no
    // render where the agent sees a Dokki doctor under a Nasr City heading.
    await waitFor(() => expect(screen.getByRole("combobox", { name: /^specialty$/i })).toHaveValue(""));
    expect(screen.getByRole("combobox", { name: /^doctor$/i })).toHaveValue("");
    expect(screen.getByRole("button", { name: /^book$/i })).toBeDisabled();
  });

  it("books into the branch the agent named, carrying the doctor", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreWorkspace api={api} />);
    await openMemberAndReserve(user);

    await choose(user, /^branch$/i, /Nasr City/);
    await choose(user, /^specialty$/i, /Cardiology/);
    await choose(user, /^doctor$/i, /Youssef Adel/);
    await waitFor(() => expect(timeButtons().length).toBeGreaterThan(0));
    await user.click(timeButtons()[0]);
    await user.click(screen.getByRole("button", { name: /^book$/i }));

    await waitFor(() => expect(api.book).toHaveBeenCalled());
    const [iid, ben, , branch, extra] = (api.book as ReturnType<typeof vi.fn>).mock.calls[0];
    expect([iid, ben, branch]).toEqual(["i1", BEN, "BR-NSR"]);
    expect(extra).toMatchObject({ doctorId: "PRC-2" });
  });
});

/**
 * The wrap-up contract, and the reason it needs its own suite.
 *
 * Phase 20.3b made `summary` mandatory at close for every outcome but Abandoned. The workspace never collected
 * or sent one, so every close was refused 422 — and because `close` resolved `void` and the caller cleared the
 * call bar unconditionally, the agent saw a wrapped-up call while the interaction stayed Open on the server.
 * An Open interaction is an unexpired caller verification, so the portal's defining control never expired.
 *
 * Neither side's tests could catch it: the backend E2E builds its own request body, and the fake here always
 * succeeded. These assert the SHAPE of the call and the handling of a refusal, which is where the gap was.
 */
describe("wrap-up: the call is only closed when the server says so", () => {
  it("sends the one summary the server requires, and clears the call bar on success", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreWorkspace api={api} />);
    await startAndSearch(user);

    await user.type(screen.getByLabelText(/call summary/i), "Booked a follow-up and corrected the phone number.");
    await user.click(screen.getByRole("button", { name: /close call/i }));

    await waitFor(() => expect(api.close).toHaveBeenCalled());
    // THREE arguments. The fourth was `notes`, a second body of text kept apart from the summary and read by
    // nobody downstream; there is one account of a call now and this is it.
    expect((api.close as ReturnType<typeof vi.fn>).mock.calls[0]).toEqual([
      "i1", "Resolved", "Booked a follow-up and corrected the phone number.", "BookAppointment",
    ]);
    // Closed for real → back to the pre-call state.
    await screen.findByRole("button", { name: /start call/i });
  });

  /** One field, one label — whether the agent types it on the member's file or in the wrap-up card. Two
   *  controls sharing an accessible name is what made an agent wonder which of them saves. */
  it("offers exactly one call-summary control with a member's file open", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await startAndOpenMember(user);

    expect(screen.getAllByLabelText(/call summary/i)).toHaveLength(1);
    expect(screen.queryByLabelText(/call notes/i)).not.toBeInTheDocument();
  });

  it("keeps the call OPEN and says why when the server refuses the close", async () => {
    const user = userEvent.setup();
    const api = fakeApi({ close: vi.fn().mockResolvedValue({ kind: "summary-required" }) });
    renderNode(<CallCentreWorkspace api={api} />);
    await startAndSearch(user);

    await user.click(screen.getByRole("button", { name: /close call/i }));

    // The refusal is shown as an error on the field that caused it…
    expect(await screen.findByRole("alert")).toHaveTextContent(/summary is required/i);
    // …and announced, because an agent mid-call is not looking at the wrap-up card.
    expect(screen.getByTestId("cc-live")).toHaveTextContent(/summary is required/i);
    // The call bar is UNCHANGED: the call really is still open, and saying otherwise is the original bug.
    expect(screen.queryByRole("button", { name: /start call/i })).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /close call/i })).toBeInTheDocument();
  });
});

describe("search hits and stale writes", () => {
  /**
   * The member number is shown IN FULL on a search hit.
   *
   * It used to arrive masked (`•••001`) because MemberNo was an identifier the agent could be challenged on,
   * and a readable one let them tick that box off their own screen. With the challenge gone the mask protects
   * nothing and costs the agent the one field that separates two people with the same name.
   */
  it("shows the real member number on a search hit", async () => {
    const user = userEvent.setup();
    renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await startAndSearch(user);

    expect(await screen.findByText("MRS-M-1001")).toBeInTheDocument();
    expect(screen.queryByText("•••001")).not.toBeInTheDocument();
  });

  it("sends the appointment's rowVersion as the If-Match token on a cancel", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderNode(<CallCentreWorkspace api={api} />);
    await startAndOpenMember(user);

    await user.click(screen.getByRole("button", { name: /^cancel appointment — /i }));
    await choose(user, /cancellation reason/i, /^patient request$/i);
    await user.click(within(screen.getByRole("dialog")).getByRole("button", { name: /^cancel appointment$/i }));

    await waitFor(() => expect(api.cancel).toHaveBeenCalled());
    // a1's token — without it emr's 412-on-stale-write can never fire for a call-centre cancellation.
    expect((api.cancel as ReturnType<typeof vi.fn>).mock.calls[0]).toEqual(["i1", "a1", "PatientRequest", 41]);
  });
});

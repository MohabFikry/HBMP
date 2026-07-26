import { describe, expect, it, vi } from "vitest";
import { screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { CallCentreWorkspace, type CcApi, type Cc360 } from "../src/screens/CallCentre";

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

function fakeApi(over: Partial<CcApi> = {}): CcApi {
  return {
    openInteraction: vi.fn().mockResolvedValue({ interactionId: "i1", callRef: "CALL-2026-000001" }),
    verify: vi.fn().mockImplementation((_i, _b, types: string[], pass: boolean) => Promise.resolve(pass && types.length >= 2)),
    search: vi.fn().mockResolvedValue([{ beneficiaryId: BEN, displayName: "Amal Hassan", memberNo: "MRS-M-1001", challengeableIdentifierTypes: ["MemberNo", "DateOfBirth", "Phone"] }]),
    summary: vi.fn().mockResolvedValue(make360()),
    book: vi.fn().mockResolvedValue("ok"),
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
});

describe("15.5 — Call Centre workspace: a11y", () => {
  it("has no serious/critical a11y violations before verification", async () => {
    const user = userEvent.setup();
    const { container } = renderNode(<CallCentreWorkspace api={fakeApi()} />);
    await startAndSelect(user);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

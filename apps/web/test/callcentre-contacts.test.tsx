import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { CallCentreWorkspace } from "../src/screens/CallCentre";
import type { CcApi, Cc360 } from "../src/screens/CallCentre";

/**
 * 32.6 (C4) — correcting a contact on the call.
 *
 * <p>Design 11 §3.1 gives the call centre `U🟠(contact, CVP)`. The endpoints have existed since 15.4 —
 * verified-caller-only, value validated server-side, forwarded to patient-service, audited with the call_ref
 * — and the workspace listed contacts read-only. "My number changed" is among the commonest reasons a member
 * rings, and it was the one thing the agent could not do.</p>
 */
const BEN = "11111111-1111-1111-1111-111111111111";

function make360(): Cc360 {
  return {
    identity: { beneficiaryId: BEN, memberNo: "MRS-M-1001", displayName: "Amal Hassan", ageBand: "30-39", status: "Active" },
    coverage: [{ category: "Outpatient", annualLimit: 10000, remainingLimit: 7500 }],
    contacts: [{ contactId: "c1", kind: "Phone", value: "+20100000000", isPrimary: true }],
    appointments: [],
    openReferrals: [],
  };
}

function fakeApi(over: Partial<CcApi> = {}): CcApi {
  return {
    openInteraction: vi.fn().mockResolvedValue({ interactionId: "i1", callRef: "CALL-2026-000001" }),
    openMember: vi.fn().mockResolvedValue(true),
    search: vi.fn().mockResolvedValue([{ beneficiaryId: BEN, displayName: "Amal Hassan", memberNo: "MRS-M-1001" }]),
    summary: vi.fn().mockResolvedValue(make360()),
    clinics: vi.fn().mockResolvedValue([]),
    slots: vi.fn().mockResolvedValue([]),
    book: vi.fn().mockResolvedValue({ kind: "ok" }),
    reschedule: vi.fn().mockResolvedValue({ kind: "ok" }),
    cancel: vi.fn().mockResolvedValue({ kind: "ok" }),
    close: vi.fn().mockResolvedValue({ kind: "ok" }),
    history: vi.fn().mockResolvedValue([]),
    updateContact: vi.fn().mockResolvedValue({ kind: "ok" }),
    addContact: vi.fn().mockResolvedValue({ kind: "ok" }),
    ...over,
  } as CcApi;
}

function renderScreen(api: CcApi) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <CallCentreWorkspace api={api} />
      </MemoryRouter>
    </AppProviders>,
  );
}

/** Start a call and open the member's file — every contact write is authorized against that binding. */
async function openMember(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole("button", { name: /start call/i }));
  await user.type(await screen.findByLabelText(/find member/i), "+20100000000");
  await user.click(screen.getByRole("button", { name: /^search$/i }));
  await user.click(await screen.findByRole("button", { name: /Amal Hassan/ }));
  await screen.findByTestId("cc-360");
}

describe("correcting a contact from the call", () => {
  it("sends the correction to the service", async () => {
    const user = userEvent.setup();
    const update = vi.fn().mockResolvedValue({ kind: "ok" });
    const api = fakeApi({ updateContact: update });

    renderScreen(api);
    await openMember(user);

    await user.click(await screen.findByRole("button", { name: /Correct — Phone/ }));
    const field = await screen.findByLabelText(/New value/);
    await user.clear(field);
    await user.type(field, "+20111222333");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(update).toHaveBeenCalled());
    // interactionId, beneficiaryId, contactId, kind, value — the call is what the write is authorized
    // against, so it goes with every one.
    expect(update.mock.calls[0].slice(1)).toEqual([BEN, "c1", "Phone", "+20111222333"]);
  });

  it("re-reads the file rather than patching the row it edited", async () => {
    const user = userEvent.setup();
    const summary = vi.fn().mockResolvedValue(make360());
    const api = fakeApi({ summary });

    renderScreen(api);
    await openMember(user);
    const before = summary.mock.calls.length;

    await user.click(await screen.findByRole("button", { name: /Correct — Phone/ }));
    await user.click(screen.getByRole("button", { name: "Save" }));

    // patient-service owns the one-primary rule: adding a primary demotes the incumbent. A screen that
    // edited its own copy would show two stars until the next reload.
    await waitFor(() => expect(summary.mock.calls.length).toBeGreaterThan(before));
  });

  it("tells an invalid value apart from an unverified call", async () => {
    const user = userEvent.setup();
    const api = fakeApi({ updateContact: vi.fn().mockResolvedValue({ kind: "invalid" }) });

    renderScreen(api);
    await openMember(user);
    await user.click(await screen.findByRole("button", { name: /Correct — Phone/ }));
    await user.click(screen.getByRole("button", { name: "Save" }));

    // 422 is about the VALUE and the member is on the phone to read it back.
    expect(await screen.findByText(/not a well-formed value/)).toBeInTheDocument();
  });

  it("says the call is not open on that file when the server refuses", async () => {
    const user = userEvent.setup();
    const api = fakeApi({ updateContact: vi.fn().mockResolvedValue({ kind: "not-verified" }) });

    renderScreen(api);
    await openMember(user);
    await user.click(await screen.findByRole("button", { name: /Correct — Phone/ }));
    await user.click(screen.getByRole("button", { name: "Save" }));

    // 403 is about the CALL. Retyping the number would never fix it, so the agent is not sent to do that.
    expect(await screen.findByText(/not open on that member's file/)).toBeInTheDocument();
  });

  it("adds a contact the member does not have on file", async () => {
    const user = userEvent.setup();
    const add = vi.fn().mockResolvedValue({ kind: "ok" });
    const api = fakeApi({ addContact: add });

    renderScreen(api);
    await openMember(user);

    await user.click(await screen.findByRole("button", { name: /Add a contact/ }));
    await user.click(screen.getByRole("combobox", { name: /Kind/ }));
    await user.click(await screen.findByRole("option", { name: /Email/ }));
    await user.type(screen.getByLabelText(/New value/), "amal@example.com");
    await user.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() => expect(add).toHaveBeenCalled());
    expect(add.mock.calls[0].slice(1)).toEqual([BEN, "Email", "amal@example.com", false]);
  });
});

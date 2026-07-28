import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import type { Role } from "../src/authz/permissions";
import { RegistrationApprovals } from "../src/screens/BeneficiaryPortal";
import { seedSession } from "./helpers";

/**
 * US-003 — the registration approval worklist. Until this screen existed the approval endpoints were
 * UI-less: nothing created applications, nobody could verify documents or bind coverage, and the only
 * activation path was the status screen. The workflow this pins:
 *
 *   officer prepares (documents verified, coverage bound) → supervisor decides → approve issues MRS-M-*.
 *
 * The officer/supervisor split is SoD: the person who vouched for the documents must not be the one who
 * activates the member. The hiding here is cosmetic (§6) — the server refuses an officer's hand-crafted
 * decision with urn:hbmp:approver-required — but the screen must still tell each role the truth about
 * which half is theirs.
 */

function renderAs(role: Role, client: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  seedSession(role);
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={client}>
      <RegistrationApprovals />
    </AppProviders>,
  );
}

const rowOf = async (name: RegExp) =>
  (await screen.findAllByRole("row")).find((r) => within(r).queryByText(name))!;

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

describe("The worklist", () => {
  it("shows each application's workflow state distinctly, including 'not started'", async () => {
    renderAs("beneficiary_mgmt");
    expect(within(await rowOf(/Omar Khaled/)).getByText(/in review/i)).toBeInTheDocument();
    expect(within(await rowOf(/Rania Mostafa/)).getByText(/info requested/i)).toBeInTheDocument();
    // Registered before applications were auto-created: still on the queue — a person the queue cannot
    // show is a person nobody reviews.
    expect(within(await rowOf(/Karim Fawzy/)).getByText(/not started/i)).toBeInTheDocument();
  });

  it("puts the approver's notes ON the row — they are the officer's to-do item", async () => {
    renderAs("beneficiary_mgmt");
    expect(within(await rowOf(/Rania Mostafa/)).getByText(/UNHCR letter is expired/i)).toBeInTheDocument();
  });

  it("offers Start review exactly where there is no open application", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "createRegistration");
    renderAs("beneficiary_mgmt", client);

    const karim = await rowOf(/Karim Fawzy/);
    await userEvent.click(within(karim).getByRole("button", { name: /start review/i }));
    expect(spy).toHaveBeenCalledWith("BEN-7", expect.any(String));

    // And nowhere else: an open application must not be restartable from the queue.
    expect(within(await rowOf(/Omar Khaled/)).queryByRole("button", { name: /start review/i })).toBeNull();
  });
});

describe("The officer's half — preparation", () => {
  it("records an approval guard when toggled", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "setRegistrationChecks");
    renderAs("beneficiary_mgmt", client);

    const omar = await rowOf(/Omar Khaled/);
    // Docs already verified in the fixture; coverage is the missing one.
    const coverage = within(omar).getByRole("checkbox", { name: /coverage bound/i });
    expect(coverage).not.toBeChecked();
    await userEvent.click(coverage);
    expect(spy).toHaveBeenCalledWith("REG-1", { coverageBound: true });
  });

  it("shows the officer WHO decides instead of a decision button", async () => {
    renderAs("beneficiary_mgmt");
    const omar = await rowOf(/Omar Khaled/);
    expect(within(omar).queryByRole("button", { name: /decide/i })).toBeNull();
    // The absence is explained: an unexplained missing button reads as a broken screen, and the server
    // would refuse the hand-crafted request anyway (urn:hbmp:approver-required).
    expect(within(omar).getByText(/supervisor/i)).toBeInTheDocument();
  });
});

describe("The supervisor's half — decision", () => {
  it("blocks Approve until both guards hold, and says which are missing", async () => {
    renderAs("beneficiary_mgmt_supervisor");
    const omar = await rowOf(/Omar Khaled/); // docs ✓, coverage ✗
    await userEvent.click(within(omar).getByRole("button", { name: /decide/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("radio", { name: /approve/i })).toBeDisabled();
    expect(within(dialog).getByText(/documents verified and coverage bound/i)).toBeInTheDocument();
    // The other two decisions stay available — an incomplete application can still be bounced or refused.
    expect(within(dialog).getByRole("radio", { name: /request information/i })).toBeEnabled();
    expect(within(dialog).getByRole("radio", { name: /reject/i })).toBeEnabled();
  });

  it("requires notes for Request information — they go back to the officer", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "decideRegistration");
    renderAs("beneficiary_mgmt_supervisor", client);

    const omar = await rowOf(/Omar Khaled/);
    await userEvent.click(within(omar).getByRole("button", { name: /decide/i }));
    const dialog = await screen.findByRole("dialog");

    await userEvent.click(within(dialog).getByRole("radio", { name: /request information/i }));
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));
    expect(await within(dialog).findByText(/notes are required/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();

    await userEvent.type(within(dialog).getByLabelText(/notes/i), "need the current UNHCR letter");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));
    expect(spy).toHaveBeenCalledWith("REG-1", "RequestInfo", "need the current UNHCR letter");
  });

  it("announces the issued member number on approve — it goes on the card", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    // Both guards held for this one.
    vi.spyOn(client, "registrationWorklist").mockResolvedValue([
      {
        beneficiary: {
          id: "BEN-1", memberNo: undefined, givenName: "Omar", familyName: "Khaled",
          status: { kind: "info", label: { en: "Pending", ar: "قيد الانتظار" } }, statusRaw: "Pending",
          identifiers: [{ type: "NationalID", value: "•••2931", isPrimary: true }],
        },
        registration: { id: "REG-1", status: "Pending", documentsVerified: true, coverageBound: true, notes: null },
      },
    ]);
    renderAs("beneficiary_mgmt_supervisor", client);

    const omar = await rowOf(/Omar Khaled/);
    await userEvent.click(within(omar).getByRole("button", { name: /decide/i }));
    const dialog = await screen.findByRole("dialog");
    // Approve pre-selected when the guards hold.
    expect(within(dialog).getByRole("radio", { name: /approve/i })).toBeChecked();
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    expect(await screen.findByText(/MRS-M-2026-000418/)).toBeInTheDocument();
  });

  it("renders the server's refusal in the dialog and keeps the work", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    vi.spyOn(client, "decideRegistration").mockRejectedValue(
      new ApiError("http", "forbidden", 403, {
        title: "approver-required",
        detail: "registration decisions are made by a beneficiary-management supervisor (US-003)",
        type: "urn:hbmp:approver-required",
      }),
    );
    renderAs("beneficiary_mgmt_supervisor", client);

    const omar = await rowOf(/Omar Khaled/);
    await userEvent.click(within(omar).getByRole("button", { name: /decide/i }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("radio", { name: /reject/i }));
    await userEvent.type(within(dialog).getByLabelText(/notes/i), "not eligible under current criteria");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    const alert = await within(dialog).findByRole("alert");
    expect(alert.textContent).toMatch(/supervisor/i);
    expect(within(dialog).getByLabelText(/notes/i)).toHaveValue("not eligible under current criteria");
  });
});

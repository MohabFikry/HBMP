import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import { BeneficiaryManage, BeneficiaryRegister, BeneficiaryStatus } from "../src/screens/BeneficiaryPortal";
import { seedSession } from "./helpers";

/**
 * The beneficiary-management portal (US-001/004) — the defects this pins were all found live:
 *
 *  • the status screen offered Activate/Suspend on EVERY row, manufacturing 409s the screen then swallowed
 *    (try/finally with no catch — the spinner stopped and nothing else happened);
 *  • a successful change left the row's status column showing the OLD status;
 *  • the fraud-Blocked state rendered ordinary buttons that the server refuses;
 *  • the register form asked the operator to TYPE an enum member from a parenthetical hint, forwarded
 *    impossible dates ("2026-02-31") to the server, and rendered the duplicate-identifier 409 with the
 *    generic "reload" guidance — which walks the operator into creating the very duplicate.
 */

function renderScreen(ui: React.ReactElement, client: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  seedSession("beneficiary_mgmt");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={client}>
      {ui}
    </AppProviders>,
  );
}

async function search(name: string) {
  await userEvent.type(screen.getByLabelText(/search by name/i), name);
  await userEvent.click(screen.getByRole("button", { name: /^search$/i }));
}

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

describe("Status & reactivation — legal transitions only", () => {
  it("offers only the current status's legal moves, named as operations", async () => {
    renderScreen(<BeneficiaryStatus />);
    await search("a"); // matches all fixtures

    // Suspended → the one legal desk move is Reinstate. The old screen offered Activate AND Suspend
    // here; Suspend was an invited "already in status" 409.
    const suspended = (await screen.findAllByRole("row")).find((r) => within(r).queryByText(/Amina Yusuf/))!;
    await userEvent.click(within(suspended).getByRole("button", { name: /change status/i }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("radio", { name: /reinstate/i })).toBeInTheDocument();
    expect(within(dialog).queryByRole("radio", { name: /suspend/i })).toBeNull();
    expect(within(dialog).queryByRole("radio", { name: /^activate$/i })).toBeNull();
  });

  it("locks the fraud-Blocked state and says WHY instead of rendering a doomed button", async () => {
    renderScreen(<BeneficiaryStatus />);
    await search("Hassan");

    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText(/Hassan Tariq/))!;
    // 23 §1: both edges of Blocked belong to a director's case review. A button here would 403.
    expect(within(row).queryByRole("button", { name: /change status/i })).toBeNull();
    expect(within(row).getByText(/medical director/i)).toBeInTheDocument();
  });

  it("demands a reason exactly where the server records one", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "changeBeneficiaryStatus");
    renderScreen(<BeneficiaryStatus />, client);
    await search("Salma"); // Active

    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText(/Salma Adel/))!;
    await userEvent.click(within(row).getByRole("button", { name: /change status/i }));
    const dialog = await screen.findByRole("dialog");

    await userEvent.click(within(dialog).getByRole("radio", { name: /suspend/i }));
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    expect(await within(dialog).findByText(/a reason is required/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();

    await userEvent.type(within(dialog).getByLabelText(/reason/i), "non-payment hold");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));
    expect(spy).toHaveBeenCalledWith("BEN-2", "Suspended", "non-payment hold");
  });

  it("does not demand a reason for reinstatement — activation is the default good outcome", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "changeBeneficiaryStatus");
    renderScreen(<BeneficiaryStatus />, client);
    await search("Amina");

    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText(/Amina Yusuf/))!;
    await userEvent.click(within(row).getByRole("button", { name: /change status/i }));
    const dialog = await screen.findByRole("dialog");
    // Single legal move → pre-selected; no reason field rendered at all.
    expect(within(dialog).queryByLabelText(/reason/i)).toBeNull();
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));
    expect(spy).toHaveBeenCalledWith("BEN-3", "Active", "");
  });

  it("shows the server's refusal IN the dialog and keeps it open — never a silent stop", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    vi.spyOn(client, "changeBeneficiaryStatus").mockRejectedValue(
      new ApiError("http", "conflict", 409, { title: "transition-denied", detail: "already in status Active" }),
    );
    renderScreen(<BeneficiaryStatus />, client);
    await search("Amina");

    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText(/Amina Yusuf/))!;
    await userEvent.click(within(row).getByRole("button", { name: /change status/i }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    // The old screen's try/finally swallowed this entirely.
    const alert = await within(dialog).findByRole("alert");
    expect(alert.textContent).toMatch(/already in status Active/);
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("re-queries after a successful change so the row shows server truth", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const searchSpy = vi.spyOn(client, "beneficiarySearch");
    renderScreen(<BeneficiaryStatus />, client);
    await search("Amina");
    expect(searchSpy).toHaveBeenCalledTimes(1);

    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText(/Amina Yusuf/))!;
    await userEvent.click(within(row).getByRole("button", { name: /change status/i }));
    await userEvent.click(within(await screen.findByRole("dialog")).getByRole("button", { name: /confirm/i }));

    // Reactivation can ISSUE a member number now; only the server knows it. The old screen left the stale
    // row and painted "Status updated" next to the old status.
    await vi.waitFor(() => expect(searchSpy).toHaveBeenCalledTimes(2));
    expect(screen.queryByRole("dialog")).toBeNull();
  });
});

describe("Search — typed failures", () => {
  it("names a permission denial instead of blaming the connection", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    vi.spyOn(client, "beneficiarySearch").mockRejectedValue(
      new ApiError("http", "forbidden", 403, { title: "forbidden" }),
    );
    renderScreen(<BeneficiaryManage />, client);
    await search("x");

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toMatch(/don't have access/i);
    // Retrying an authorization decision cannot change it.
    expect(screen.queryByRole("button", { name: /retry/i })).toBeNull();
  });

  it("offers retry for a server fault", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    vi.spyOn(client, "beneficiarySearch").mockRejectedValue(new ApiError("http", "boom", 503));
    renderScreen(<BeneficiaryManage />, client);
    await search("x");
    await screen.findByRole("alert");
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
  });
});

describe("Register — a closed vocabulary and real dates", () => {
  it("renders the identifier type as a select over the four legal values", async () => {
    renderScreen(<BeneficiaryRegister />);
    const select = screen.getByLabelText(/identifier type/i);
    const options = within(select).getAllByRole("option").map((o) => (o as HTMLOptionElement).value);
    expect(options).toEqual(["NationalID", "Passport", "RefugeeID", "UNHCRNo"]);
  });

  it("rejects an impossible calendar date client-side", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "registerBeneficiary");
    renderScreen(<BeneficiaryRegister />, client);

    await userEvent.type(screen.getByLabelText(/given name/i), "Nour");
    await userEvent.type(screen.getByLabelText(/family name/i), "Said");
    await userEvent.type(screen.getByLabelText(/identifier value/i), "29901011234567");
    // Matches YYYY-MM-DD, is not a date. The old screen forwarded it; the server's 400 came back as
    // "something went wrong — reload", destroying the guidance along with the operator's confidence.
    await userEvent.type(screen.getByLabelText(/birth date/i), "2026-02-31");
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    expect(await screen.findByText(/enter a real date/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });

  it("marks each missing required field at the field", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "registerBeneficiary");
    renderScreen(<BeneficiaryRegister />, client);

    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));
    expect(await screen.findAllByText(/^required\.$/i)).toHaveLength(3); // given, family, id value
    expect(spy).not.toHaveBeenCalled();
  });

  it("turns the duplicate-identifier 409 into a search instruction, not a reload", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    vi.spyOn(client, "registerBeneficiary").mockRejectedValue(
      new ApiError("http", "conflict", 409, {
        title: "duplicate-identifier",
        detail: "NationalID '299…' already exists on beneficiary 1234",
        type: "urn:hbmp:duplicate-identifier",
      }),
    );
    renderScreen(<BeneficiaryRegister />, client);

    await userEvent.type(screen.getByLabelText(/given name/i), "Nour");
    await userEvent.type(screen.getByLabelText(/family name/i), "Said");
    await userEvent.type(screen.getByLabelText(/identifier value/i), "29901011234567");
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toMatch(/already registered/i);
    expect(alert.textContent).toMatch(/search \/ manage/i);
    // And the form is NOT cleared — the typing is the operator's evidence for the search.
    expect(screen.getByLabelText(/identifier value/i)).toHaveValue("29901011234567");
  });

  it("clears the form only after a confirmed success", async () => {
    renderScreen(<BeneficiaryRegister />);
    await userEvent.type(screen.getByLabelText(/given name/i), "Nour");
    await userEvent.type(screen.getByLabelText(/family name/i), "Said");
    await userEvent.type(screen.getByLabelText(/identifier value/i), "29901011234567");
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    expect(await screen.findByText(/registered \(pending\)/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/given name/i)).toHaveValue("");
  });
});

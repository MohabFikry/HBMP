import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import type { PolicyApi } from "../src/api/policyApi";
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
      <MemoryRouter>{ui}</MemoryRouter>
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

/**
 * The registration form's reference data, stubbed.
 *
 * The plan and network-tier lists come from policy-service. Injecting them keeps these tests about the FORM
 * — its validation, its errors, its clearing behaviour — rather than about whether a gateway is reachable,
 * and it exercises the same seam the screen uses in production.
 */
const PLAN_ID = "11111111-1111-1111-1111-111111111111";
const TIER_ID = "22222222-2222-2222-2222-222222222222";

function stubPolicyApi(): PolicyApi {
  return {
    plans: async () => [
      { planId: PLAN_ID, planCode: "MERSAL", nameEn: "Mersal", nameAr: "مرسال", category: "Standard", status: "Active" },
    ],
    networkTiers: async () => [
      {
        networkTierId: TIER_ID, tierCode: "MERSAL", nameEn: "Mersal Network", nameAr: "شبكة مرسال",
        rank: 1, isOutOfNetwork: false, status: "Active",
      },
    ],
    // The batch panel reads the column contract off the server so the expected-columns table cannot drift
    // from the template the engine actually parses against.
    bulkTemplates: async () => [
      {
        jobType: "MemberEnrolment", purposeEn: "Register and enrol members.", purposeAr: "تسجيل الأعضاء وقيدهم.",
        columns: [
          { name: "card_number", kind: "Text", required: true, descriptionEn: "The card number.", descriptionAr: "رقم البطاقة." },
        ],
      },
    ],
  } as unknown as PolicyApi;
}

/** Fill everything the form demands, so a test about ONE rule is not also a test about the other eleven. */
async function fillValidRegistration() {
  await userEvent.type(screen.getByLabelText(/card number/i), "#A-1001");
  await userEvent.type(screen.getByLabelText(/first name/i), "Nour");
  await userEvent.type(screen.getByLabelText(/last name/i), "Said");
  await userEvent.type(screen.getByLabelText(/identifier value/i), "29901011234567");
  await userEvent.type(screen.getByLabelText(/^number/i), "1234567890");
  await userEvent.type(screen.getByLabelText(/contribution/i), "20");

  const pick = async (label: RegExp, option: RegExp) => {
    await userEvent.click(screen.getByRole("combobox", { name: label }));
    await userEvent.click(await screen.findByRole("option", { name: option }));
  };
  await pick(/gender/i, /female/i);
  await pick(/nationality/i, /syria/i);
  await pick(/^plan/i, /mersal/i);
  await pick(/network tier/i, /mersal network/i);

  // type="date" wants the value set rather than typed character by character.
  await userEvent.type(screen.getByLabelText(/birthdate/i), "1990-01-01");
}

describe("Register — the operational record", () => {
  it("renders the identifier type as a closed vocabulary, not a free-text enum", async () => {
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />);
    await userEvent.click(screen.getByRole("combobox", { name: /identifier type/i }));
    const options = (await screen.findAllByRole("option")).map((o) => o.textContent?.trim());
    expect(options).toEqual(["National ID", "Passport", "Refugee ID", "UNHCR number"]);
  });

  it("filters a long list as you type instead of making you walk it", async () => {
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />);

    // First-letter typeahead over a hundred nationalities means pressing S walks Saudi Arabia → Senegal →
    // Sierra Leone before it reaches South Sudan. Typing the word is how a long list is meant to behave.
    const nationality = screen.getByRole("combobox", { name: /nationality/i });
    await userEvent.click(nationality);
    await userEvent.type(nationality, "south");

    const options = (await screen.findAllByRole("option")).map((o) => o.textContent);
    expect(options.some((o) => /South Sudan/.test(o ?? ""))).toBe(true);
    expect(options.some((o) => /Saudi Arabia/.test(o ?? ""))).toBe(false);
  });

  it("finds a country by its code as well as its name", async () => {
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />);
    const nationality = screen.getByRole("combobox", { name: /nationality/i });
    await userEvent.click(nationality);
    await userEvent.type(nationality, "ER");

    expect((await screen.findAllByRole("option")).some((o) => /Eritrea/.test(o.textContent ?? ""))).toBe(true);
  });

  it("never keeps a half-typed query as the value", async () => {
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />);
    const nationality = screen.getByRole("combobox", { name: /nationality/i });

    await userEvent.click(nationality);
    await userEvent.click(await screen.findByRole("option", { name: /syria/i }));
    expect(nationality).toHaveValue("Syria");

    // Type nonsense, then abandon it. The input is a QUERY; the value is whatever is selected.
    await userEvent.type(nationality, "zzzz");
    await userEvent.keyboard("{Escape}");
    expect(nationality).toHaveValue("Syria");
  });

  it("groups the form into named sections rather than one flat grid of twenty inputs", () => {
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />);
    // Real fieldsets, so the grouping is in the accessibility tree and not only in the paint.
    for (const section of [/identity/i, /personal details/i, /contact/i, /coverage/i]) {
      expect(screen.getByRole("group", { name: section })).toBeInTheDocument();
    }
  });

  it("carries NO field the operator cannot fill in", () => {
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />);

    // Status and age are both DERIVED — status by the approval workflow, age from the birthdate — so neither
    // has an input, and a read-only box sitting in a grid of editable ones is just a hole in the alignment.
    // They belong on the record you read, not the form you fill in.
    expect(screen.queryByLabelText(/^status/i)).not.toBeInTheDocument();
    expect(screen.queryByLabelText(/^age/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/supervisor's decision/i)).not.toBeInTheDocument();
  });

  it("marks a mandatory field exactly once", () => {
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />);

    // InputField renders its own mark from `required`; a hand-written " *" in the label as well produced
    // "Card number * *" on every mandatory field.
    const label = screen.getByText(/^card number$/i).closest("label");
    expect(label?.textContent?.match(/\*/g) ?? []).toHaveLength(1);
  });

  it("collapses the notes, and says what is inside while they are shut", async () => {
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />);

    // Six optional prose boxes are the tallest thing on the form and the least often filled.
    const summary = screen.getByText(/^notes$/i).closest("summary")!;
    const details = summary.closest("details")!;
    expect(details.open).toBe(false);
    expect(summary.textContent).toMatch(/optional/i);

    await userEvent.click(summary);
    expect(details.open).toBe(true);
    expect(screen.getByLabelText(/known diagnosis/i)).toBeInTheDocument();
  });

  it("marks each missing required field at the field, and does not submit", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "registerBeneficiary");
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />, client);

    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    // The four closed vocabularies that are simply unchosen. Everything else carries a rule-specific message
    // ("an Egyptian National ID is exactly 14 digits") rather than a bare "Required".
    expect(await screen.findAllByText(/^required\.$/i)).toHaveLength(4);
    expect(spy).not.toHaveBeenCalled();
  });

  it("refuses a future birthdate before the server has to", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "registerBeneficiary");
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />, client);

    await fillValidRegistration();
    const tomorrow = new Date(Date.now() + 86_400_000).toISOString().slice(0, 10);
    await userEvent.clear(screen.getByLabelText(/birthdate/i));
    await userEvent.type(screen.getByLabelText(/birthdate/i), tomorrow);
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    expect(await screen.findByText(/enter a real date/i)).toBeInTheDocument();
    expect(spy).not.toHaveBeenCalled();
  });

  it("normalizes the card number so one card cannot become two records", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "registerBeneficiary");
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />, client);

    await fillValidRegistration();
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    // The '#' is a convention, not data — and neither is the case or the padding.
    expect(spy).toHaveBeenCalledWith(expect.objectContaining({ cardNumber: "A-1001" }), expect.anything());
  });

  it("sends the elected coverage as an intent, with the contribution as a number", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "registerBeneficiary");
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />, client);

    await fillValidRegistration();
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    expect(spy).toHaveBeenCalledWith(
      expect.objectContaining({
        enrolment: expect.objectContaining({
          planId: PLAN_ID, networkTierId: TIER_ID, contributionPercent: 20,
        }),
      }),
      expect.anything(),
    );
  });

  it("assembles the phone from the dial code and the national number", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "registerBeneficiary");
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />, client);

    await fillValidRegistration();
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    expect(spy).toHaveBeenCalledWith(expect.objectContaining({ phone: "+201234567890" }), expect.anything());
  });

  it("sends only the note slots the operator actually filled", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "registerBeneficiary");
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />, client);

    await fillValidRegistration();
    await userEvent.type(screen.getByLabelText(/known diagnosis/i), "Type 2 diabetes");
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    // A blank slot and a cleared one read identically later; storing empties makes "is the diagnosis on file"
    // unanswerable.
    expect(spy).toHaveBeenCalledWith(
      expect.objectContaining({ notes: [{ slot: 1, value: "Type 2 diabetes" }] }),
      expect.anything(),
    );
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
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />, client);

    await fillValidRegistration();
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toMatch(/already registered/i);
    expect(alert.textContent).toMatch(/search \/ manage/i);
    // And the form is NOT cleared — the typing is the operator's evidence for the search.
    expect(screen.getByLabelText(/identifier value/i)).toHaveValue("29901011234567");
  });

  it("gives the duplicate-CARD 409 its own remedy, because opening the existing record is wrong advice", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    vi.spyOn(client, "registerBeneficiary").mockRejectedValue(
      new ApiError("http", "conflict", 409, {
        title: "duplicate-card-number",
        detail: "card 'A-1001' is already held by beneficiary 1234",
        type: "urn:hbmp:duplicate-card-number",
      }),
    );
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />, client);

    await fillValidRegistration();
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    const alert = await screen.findByRole("alert");
    // A card clash is usually a mis-read or a re-issue — NOT "this is the same person, open them".
    expect(alert.textContent).toMatch(/already held by another beneficiary/i);
    expect(alert.textContent).not.toMatch(/search \/ manage/i);
  });

  it("clears the form only after a confirmed success", async () => {
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />);
    await fillValidRegistration();
    await userEvent.click(screen.getByRole("button", { name: /register beneficiary/i }));

    expect(await screen.findByText(/registered \(pending\)/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/first name/i)).toHaveValue("");
  });

  it("offers the file path for the hundreds-at-a-time case", async () => {
    renderScreen(<BeneficiaryRegister policyApi={stubPolicyApi()} />);
    await userEvent.click(screen.getByRole("radio", { name: /many from a file/i }));

    // The same upload → check → commit pipeline as Bulk & Imports, so "nothing is applied until commit" is
    // the same guarantee rather than a second implementation of it.
    expect(await screen.findByText(/nothing is written until you commit/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/file \(csv or xlsx\)/i)).toBeInTheDocument();
  });
});

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
import { BeneficiaryRegister } from "../src/screens/BeneficiaryPortal";
import { StatusChangeModal } from "../src/screens/BeneficiaryStatusDialog";
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

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

describe("Status change — legal transitions only", () => {
  /*
   * These used to drive the "Status & Reactivation" SCREEN, which is gone (19.7 nav rework): its whole job
   * was to find a person you had usually just been looking at and press one button, so it is now an action on
   * the beneficiary's detail. The DIALOG is unchanged and so are these assertions — they simply render it
   * directly rather than through a search that no longer exists.
   */
  const openDialog = (props: Partial<React.ComponentProps<typeof StatusChangeModal>> = {}, client?: ApiClient) =>
    renderScreen(
      <StatusChangeModal
        beneficiaryId="BEN-2"
        name="Salma Adel"
        statusRaw="Active"
        onClose={() => {}}
        onChanged={() => {}}
        {...props}
      />,
      client,
    );

  it("offers only the current status's legal moves, named as operations", async () => {
    // Suspended → the one legal desk move is Reinstate. The old screen offered Activate AND Suspend
    // here; Suspend was an invited "already in status" 409.
    openDialog({ beneficiaryId: "BEN-3", name: "Amina Yusuf", statusRaw: "Suspended" });
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("radio", { name: /reinstate/i })).toBeInTheDocument();
    expect(within(dialog).queryByRole("radio", { name: /suspend/i })).toBeNull();
    expect(within(dialog).queryByRole("radio", { name: /^activate$/i })).toBeNull();
  });

  it("locks the fraud-Blocked state and says WHY instead of rendering a doomed control", async () => {
    // 23 §1: both edges of Blocked belong to a director's case review. A radio here would 403.
    openDialog({ beneficiaryId: "BEN-9", name: "Hassan Tariq", statusRaw: "Blocked" });
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).queryByRole("radio")).toBeNull();
    expect(within(dialog).queryByRole("button", { name: /confirm/i })).toBeNull();
    expect(within(dialog).getByText(/unlocked by a director/i)).toBeInTheDocument();
  });

  it("names a status it was never told, rather than showing an empty dialog", async () => {
    // A caller whose role was not disclosed the beneficiary's status. "No moves" and "we cannot see the
    // status" are different facts and lead to different next steps.
    openDialog({ statusRaw: null });
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/was not disclosed to your role/i)).toBeInTheDocument();
  });

  it("demands a reason exactly where the server records one", async () => {
    const client = new DevApiClient({ latencyMs: 0 });
    const spy = vi.spyOn(client, "changeBeneficiaryStatus");
    openDialog({}, client);
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
    openDialog({ beneficiaryId: "BEN-3", name: "Amina Yusuf", statusRaw: "Suspended" }, client);
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
    openDialog({ beneficiaryId: "BEN-3", name: "Amina Yusuf", statusRaw: "Suspended" }, client);
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));

    // The old screen's try/finally swallowed this entirely.
    const alert = await within(dialog).findByRole("alert");
    expect(alert.textContent).toMatch(/already in status Active/);
    expect(screen.getByRole("dialog")).toBeInTheDocument();
  });

  it("tells its caller to re-read after a successful change", async () => {
    // Reactivation can ISSUE a member number, and only the server knows it — so the dialog's contract is to
    // report success and let the caller re-query, never to patch the row locally.
    const client = new DevApiClient({ latencyMs: 0 });
    const onChanged = vi.fn();
    openDialog({ beneficiaryId: "BEN-3", name: "Amina Yusuf", statusRaw: "Suspended", onChanged }, client);
    const dialog = await screen.findByRole("dialog");
    await userEvent.click(within(dialog).getByRole("button", { name: /confirm/i }));
    await vi.waitFor(() => expect(onChanged).toHaveBeenCalled());
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

    // Every required field that is simply EMPTY says the same thing, whether it is typed or chosen.
    //
    // This assertion used to expect 4 — the four closed vocabularies — because the typed fields answered a
    // blank box with their FORMAT rule instead: three empty name boxes explained that "names can contain
    // letters, spaces, hyphens, apostrophes and periods only", and an empty phone box asked for "8–15
    // digits, with an optional leading +". Neither is what went wrong. The operator had not filled the
    // field in, and the form said so in one vocabulary for droplists and another for text boxes.
    //
    // The rule-specific messages are still exactly right for a field that HAS a value and got it wrong —
    // which is the case they were written for, and which the birthdate and National ID tests below cover.
    expect(await screen.findAllByText(/^required\.$/i)).toHaveLength(11);
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

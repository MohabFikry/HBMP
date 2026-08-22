import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { ApiError } from "../src/api/http";
import type { PayerDetail, PayerView, PolicyApi } from "../src/api/policyApi";
import { PolicyPayers } from "../src/screens/PolicyPayerAdmin";
import { seedSession } from "./helpers";

/**
 * Phase 19.7 — payer administration (design 56).
 *
 * The screen these replace was a four-column read-only table. Each test here holds one claim the rebuild
 * makes and that a later edit could quietly break:
 *
 *  · the controls exist at all, and only for the role the server would accept;
 *  · an expired agreement on an ACTIVE payer is said in words rather than left to be inferred from two chips
 *    that happen to disagree;
 *  · withheld commercial terms read as "restricted", never as "not recorded";
 *  · the deactivation refusal — the 409 that names how many policies still hang off the payer — reaches the
 *    operator instead of being swallowed;
 *  · a reason that explains nothing cannot clear the dialog.
 */

// ── a fake PolicyApi ────────────────────────────────────────────────────────────────────────────────────

const payer = (over: Partial<PayerView> = {}): PayerView => ({
  payerId: "p1",
  payerCode: "GRANT-EU",
  nameEn: "European Aid Fund",
  nameAr: "صندوق العون الأوروبي",
  payerType: "Donor",
  status: "Active",
  statusReason: null,
  statusChangedAt: null,
  agreement: {
    externalRef: "EU-2026-114",
    agreementNo: "AGR-2026-14",
    agreementFrom: "2026-01-01",
    agreementTo: "2027-01-01",
    state: "InForce",
  },
  terms: {
    fundingCeiling: 5_000_000,
    currency: "EGP",
    settlementTermsDays: 30,
    invoicingCadence: "Quarterly",
    claimSubmissionWindowDays: 90,
  },
  contacts: { primary: { name: "Huda Salem", title: "Programme officer", email: "huda@example.org", phone: null } },
  notes: null,
  updatedAt: "2026-08-01T09:00:00Z",
  updatedByName: "Sara Hassan",
  ...over,
});

const detail = (p: PayerView, over: Partial<PayerDetail["book"]> = {}): PayerDetail => ({
  payer: p,
  book: {
    policyCount: 4, activePolicyCount: 3, memberCount: 812, activeMemberCount: 790, planCount: 2,
    committedLimit: 4_100_000, consumedValue: 900_000, ceilingPercentCommitted: 82,
    ...over,
  },
});

function fakeApi(rows: PayerView[], overrides: Partial<PolicyApi> = {}): PolicyApi {
  const reject = () => Promise.reject(new ApiError("network", "not stubbed in this test"));
  return {
    payers: () => Promise.resolve(rows),
    payer: (id: string) => {
      const found = rows.find((r) => r.payerId === id);
      return found ? Promise.resolve(detail(found)) : reject();
    },
    createPayer: reject,
    updatePayer: reject,
    deactivatePayer: reject,
    reactivatePayer: reject,
    payerHistory: () => Promise.resolve({ payerId: "p1", entries: [] }),
    ...overrides,
  } as unknown as PolicyApi;
}

function renderScreen(api: PolicyApi, role = "policy_admin") {
  seedSession(role as Parameters<typeof seedSession>[0]);
  return render(
    <AppProviders authClient={new DevAuthClient()}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <PolicyPayers api={api} />
      </MemoryRouter>
    </AppProviders>,
  );
}

/** Open the one payer's detail — everything below the list needs a selected row. */
async function select(name = "European Aid Fund") {
  const row = await screen.findByRole("row", { name: new RegExp(name) });
  await userEvent.click(within(row).getByText(name));
}

afterEach(() => { cleanup(); localStorage.clear(); });

// ── the list ────────────────────────────────────────────────────────────────────────────────────────────

describe("the payer list", () => {
  it("shows the agreement state as a labelled chip, not as a date somebody has to compare to today", async () => {
    renderScreen(fakeApi([payer(), payer({ payerId: "p2", payerCode: "MOH", nameEn: "Ministry of Health", nameAr: "وزارة الصحة", payerType: "Government", agreement: { externalRef: null, agreementNo: null, agreementFrom: "2024-01-01", agreementTo: "2025-01-01", state: "Expired" } })]));

    const grid = within(await screen.findByRole("grid"));
    expect(grid.getByText("In force")).toBeInTheDocument();
    expect(grid.getByText("Expired")).toBeInTheDocument();
  });

  it("narrows to the expired agreements when that filter is pressed", async () => {
    renderScreen(fakeApi([
      payer(),
      payer({ payerId: "p2", payerCode: "MOH", nameEn: "Ministry of Health", nameAr: "وزارة الصحة", agreement: { externalRef: null, agreementNo: null, agreementFrom: null, agreementTo: null, state: "Expired" } }),
    ]));
    await screen.findByText("European Aid Fund");

    // The chip's own label is "Expired"; the one inside the table cell is not a button.
    const chip = screen.getAllByRole("button", { name: /Expired/ })[0];
    await userEvent.click(chip!);

    await waitFor(() => expect(screen.queryByText("European Aid Fund")).not.toBeInTheDocument());
    expect(screen.getByText("Ministry of Health")).toBeInTheDocument();
  });
});

// ── the controls ────────────────────────────────────────────────────────────────────────────────────────

describe("who is offered a write", () => {
  it("offers New, Edit and Deactivate to a policy administrator", async () => {
    renderScreen(fakeApi([payer()]));
    expect(await screen.findByRole("button", { name: "New payer" })).toBeInTheDocument();

    await select();
    expect(await screen.findByRole("button", { name: "Edit this payer" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Deactivate this payer" })).toBeInTheDocument();
  });

  /**
   * A claims officer holds `policy:read` and reaches this screen legitimately — they adjudicate against
   * these terms. The server refuses every write with 403, so the affordance is ABSENT rather than present
   * and disabled: a button that can only fail teaches an operator that the screen is broken.
   */
  it("offers a claims officer no write at all — and still lets them read the history", async () => {
    renderScreen(fakeApi([payer()]), "claims_officer");
    await screen.findByText("European Aid Fund");
    expect(screen.queryByRole("button", { name: "New payer" })).not.toBeInTheDocument();

    await select();
    expect(await screen.findByRole("button", { name: "Change history" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Edit this payer" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Deactivate this payer" })).not.toBeInTheDocument();
  });

  it("offers Reactivate — not Deactivate — on a payer that is already off", async () => {
    renderScreen(fakeApi([payer({ status: "Inactive", statusReason: "The 2025 grant closed and will not be renewed." })]));
    await select();

    expect(await screen.findByRole("button", { name: "Reactivate this payer" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Deactivate this payer" })).not.toBeInTheDocument();
    // The reason the record is off is the first thing anybody opening it wants.
    expect(screen.getByText(/The 2025 grant closed/)).toBeInTheDocument();
  });
});

// ── the detail ──────────────────────────────────────────────────────────────────────────────────────────

describe("the payer detail", () => {
  it("says in words that an active payer's funding has run out", async () => {
    renderScreen(fakeApi([payer({
      agreement: { externalRef: null, agreementNo: "AGR-1", agreementFrom: "2024-01-01", agreementTo: "2025-01-01", state: "Expired" },
    })]));
    await select();

    expect(await screen.findByText(/its funding agreement has expired/)).toBeInTheDocument();
  });

  /**
   * `terms: null` means WITHHELD. Five empty rows would read as "no ceiling recorded", which is a different
   * and much worse answer to give somebody about a ceiling that exists.
   */
  it("says withheld terms are restricted, never that they are unrecorded", async () => {
    renderScreen(fakeApi([payer({ terms: null })]), "claims_officer");
    await select();

    expect(await screen.findByText(/restricted for your role/i)).toBeInTheDocument();
    expect(screen.queryByText("Funding ceiling")).not.toBeInTheDocument();
  });

  it("counts the book of business and shows how much of the ceiling is committed", async () => {
    renderScreen(fakeApi([payer()]));
    await select();

    expect(await screen.findByText("812")).toBeInTheDocument();          // members
    expect(screen.getByText(/82% of the ceiling committed/)).toBeInTheDocument();
  });

  /** `null` is "you may not see this"; `0` is zero. Rendering both the same would tell a role with no
   *  amount access that a payer with a book of business has none. */
  it("distinguishes a withheld committed total from a zero one", async () => {
    const p = payer();
    const api = fakeApi([p], { payer: () => Promise.resolve(detail(p, { committedLimit: null, ceilingPercentCommitted: null })) });
    renderScreen(api, "claims_officer");
    await select();

    expect(await screen.findByText("Restricted for your role")).toBeInTheDocument();
  });

  it("renders the payer's own currency, not the platform default", async () => {
    renderScreen(fakeApi([payer({
      terms: { fundingCeiling: 250_000, currency: "USD", settlementTermsDays: 45, invoicingCadence: "Monthly", claimSubmissionWindowDays: 60 },
    })]));
    await select();

    // A USD grant rendered in pounds is not a formatting slip — it is a number somebody would act on.
    // The "Funding ceiling" fact and the KPI strip both render it, so assert there is at least one and that
    // none of them is in pounds.
    const dollars = await screen.findAllByText(/\$\s?250,000/);
    expect(dollars.length).toBeGreaterThan(0);
    expect(screen.queryByText(/EGP\s?250,000|£250,000/)).not.toBeInTheDocument();
  });
});

// ── deactivation ────────────────────────────────────────────────────────────────────────────────────────

describe("deactivating a payer", () => {
  const openDialog = async () => {
    await select();
    await userEvent.click(await screen.findByRole("button", { name: "Deactivate this payer" }));
  };

  it("will not confirm on a reason that explains nothing", async () => {
    renderScreen(fakeApi([payer()]));
    await openDialog();

    const confirm = await screen.findByRole("button", { name: "Deactivate this payer" });
    expect(confirm).toBeDisabled();

    await userEvent.type(screen.getByLabelText(/Why/), "old");
    expect(confirm).toBeDisabled();

    await userEvent.clear(screen.getByLabelText(/Why/));
    await userEvent.type(screen.getByLabelText(/Why/), "The 2025 grant closed and will not be renewed.");
    await waitFor(() => expect(confirm).toBeEnabled());
  });

  /**
   * THE refusal. A payer still funding live policies is not a row to switch off, and the server says so with
   * a count. Swallowing it would leave the operator pressing a button that does nothing.
   */
  it("shows the server's refusal, with its count, instead of failing silently", async () => {
    const deactivatePayer = vi.fn().mockRejectedValue(
      new ApiError("http", "conflict", 409, {
        detail: "This payer still funds 3 active policies. End or transfer them first.",
      }),
    );
    renderScreen(fakeApi([payer()], { deactivatePayer } as Partial<PolicyApi>));
    await openDialog();

    await userEvent.type(screen.getByLabelText(/Why/), "The funding agreement ended on 31 December.");
    await userEvent.click(screen.getByRole("button", { name: "Deactivate this payer" }));

    expect(await screen.findByText(/still funds 3 active policies/)).toBeInTheDocument();
    expect(deactivatePayer).toHaveBeenCalledOnce();
  });

  it("sends the reason it was given", async () => {
    const deactivatePayer = vi.fn().mockResolvedValue(payer({ status: "Inactive" }));
    renderScreen(fakeApi([payer()], { deactivatePayer } as Partial<PolicyApi>));
    await openDialog();

    await userEvent.type(screen.getByLabelText(/Why/), "The donor wound the programme up in June.");
    await userEvent.click(screen.getByRole("button", { name: "Deactivate this payer" }));

    await waitFor(() =>
      expect(deactivatePayer).toHaveBeenCalledWith("p1", "The donor wound the programme up in June.", expect.any(String)));
  });
});

// ── the form ────────────────────────────────────────────────────────────────────────────────────────────

describe("creating and editing", () => {
  it("asks for a code on create and refuses to change one on edit", async () => {
    renderScreen(fakeApi([payer()]));

    await userEvent.click(await screen.findByRole("button", { name: "New payer" }));
    expect(await screen.findByLabelText(/Payer code/)).not.toHaveAttribute("readonly");
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));

    await select();
    await userEvent.click(await screen.findByRole("button", { name: "Edit this payer" }));
    expect(await screen.findByLabelText(/Payer code/)).toHaveAttribute("readonly");
  });

  it("refuses a ceiling of zero rather than storing a payer funded for nothing", async () => {
    const createPayer = vi.fn();
    renderScreen(fakeApi([payer()], { createPayer } as Partial<PolicyApi>));
    await userEvent.click(await screen.findByRole("button", { name: "New payer" }));

    await userEvent.type(await screen.findByLabelText(/Payer code/), "NEW-1");
    await userEvent.type(screen.getByLabelText(/Name \(English\)/), "New donor");
    await userEvent.type(screen.getByLabelText(/Name \(Arabic\)/), "مانح جديد");
    await userEvent.type(screen.getByLabelText(/Funding ceiling/), "0");
    await userEvent.click(screen.getByRole("button", { name: /Create payer/ }));

    // The help text under the field says the same thing; the ALERT is the one that fires on submit.
    expect(await screen.findByRole("alert")).toHaveTextContent(/is not 'uncapped'/);
    expect(createPayer).not.toHaveBeenCalled();
  });

  it("sends the contact block with the blank entries dropped", async () => {
    const createPayer = vi.fn().mockResolvedValue(payer());
    renderScreen(fakeApi([payer()], { createPayer } as Partial<PolicyApi>));
    await userEvent.click(await screen.findByRole("button", { name: "New payer" }));

    await userEvent.type(await screen.findByLabelText(/Payer code/), "NEW-1");
    await userEvent.type(screen.getByLabelText(/Name \(English\)/), "New donor");
    await userEvent.type(screen.getByLabelText(/Name \(Arabic\)/), "مانح جديد");

    const dayToDay = screen.getByRole("group", { name: "Day-to-day contact" });
    await userEvent.type(within(dayToDay).getByLabelText("Name"), "Nadia Farouk");
    await userEvent.click(screen.getByRole("button", { name: /Create payer/ }));

    await waitFor(() => expect(createPayer).toHaveBeenCalled());
    const [body] = createPayer.mock.calls[0]!;
    expect(body.contacts.primary.name).toBe("Nadia Farouk");
    // An entry with nothing in it is not a contact — it must not come back as a card with a heading.
    expect(body.contacts.finance).toBeNull();
    expect(body.contacts.escalation).toBeNull();
  });
});

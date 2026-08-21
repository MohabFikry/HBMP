import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ReceptionEligibility } from "../src/screens/ReceptionEligibility";
import { renderNode, seedSession } from "./helpers";

/**
 * 33.9 — the eligibility check identifies a person before it answers about one.
 *
 * ============================================================================================================
 * WHAT WENT WRONG
 * ============================================================================================================
 * The screen ran `searchEligibility(query)` on ONE free-text box — a card number, an ID, or any fragment of a
 * name — and then checked `hits[0]`. Typing "Ahmed" matched every Ahmed on the platform, whichever the
 * database returned first was chosen, and the plan, remaining annual cap and visit verdict rendered on the
 * card belonged to a person nobody had picked. Nothing on screen said a choice had been made at all.
 *
 * That is a wrong-patient defect. The desk turns somebody away, or admits them, on another member's coverage.
 *
 * These tests drive the SCREEN, because that is where the two calls were chained. The rule itself —
 * which identifiers may be presented and whether a name agrees — is the service's, and is tested there.
 */

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

function renderScreen(api: ApiClient = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient) {
  seedSession("reception");
  return renderNode(<ReceptionEligibility />, api);
}

const idField = () => screen.getByLabelText(/card, member or id number/i);
const nameField = () => screen.getByLabelText(/part of their name/i);
const checkButton = () => screen.getByRole("button", { name: /check eligibility/i });

describe("a check cannot be run on a name alone", () => {
  it("refuses to submit until both the number and the name are given", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const verify = vi.fn();
    (api as { verifyBeneficiary: unknown }).verifyBeneficiary = verify;
    renderScreen(api);

    // The whole defect in one gesture: this used to be a complete query.
    await user.type(nameField(), "Amal");
    expect(checkButton()).toBeDisabled();

    await user.type(idField(), "MRS-M-10231");
    expect(checkButton()).toBeEnabled();
    expect(verify).not.toHaveBeenCalled();
  });

  it("sends BOTH to the service and never a bare search", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const verify = vi.fn().mockResolvedValue({
      verified: true,
      hit: { id: "ben-1", name: { en: "Amal Hassan", ar: "أمل حسن" }, cardNumber: "MRS-M-10231" },
    });
    const search = vi.fn();
    (api as { verifyBeneficiary: unknown }).verifyBeneficiary = verify;
    (api as { searchEligibility: unknown }).searchEligibility = search;
    renderScreen(api);

    await user.type(idField(), "MRS-M-10231");
    await user.type(nameField(), "Hassan");
    await user.click(checkButton());

    await waitFor(() => expect(verify).toHaveBeenCalledWith("MRS-M-10231", "Hassan"));
    // The old path, locked out. `searchEligibility` still exists and is right for booking and the call
    // centre; what it is not is a way to answer "is THIS person covered".
    expect(search).not.toHaveBeenCalled();
  });

  it("checks the member the service verified, not the first of several", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { verifyBeneficiary: unknown }).verifyBeneficiary = vi.fn().mockResolvedValue({
      verified: true,
      hit: { id: "ben-verified", name: { en: "Amal Hassan", ar: "أمل حسن" }, cardNumber: "MRS-M-10231" },
    });
    const check = vi.fn().mockResolvedValue({
      scope: "membership", benefitCategory: null,
      status: { kind: "ok", label: { en: "Eligible", ar: "مؤهل" } },
      beneficiary: { id: "ben-verified", name: { en: "Amal Hassan", ar: "أمل حسن" }, cardNumber: "MRS-M-10231" },
      coverage: null,
      costShare: { known: false, why: { en: "No category named.", ar: "لم تحدد فئة." } },
      visitGate: { allowed: true },
    });
    (api as { checkEligibility: unknown }).checkEligibility = check;
    renderScreen(api);

    await user.type(idField(), "MRS-M-10231");
    await user.type(nameField(), "Hassan");
    await user.click(checkButton());

    await waitFor(() => expect(check).toHaveBeenCalledWith("ben-verified", undefined));
  });
});

describe("a refusal says which refusal it is, and names nobody", () => {
  async function refuse(reason: string) {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { verifyBeneficiary: unknown }).verifyBeneficiary =
      vi.fn().mockResolvedValue({ verified: false, reason });
    const check = vi.fn();
    (api as { checkEligibility: unknown }).checkEligibility = check;
    renderScreen(api);

    await user.type(idField(), "MRS-M-10231");
    await user.type(nameField(), "Khalil");
    await user.click(checkButton());
    return check;
  }

  it("tells a name mismatch apart from an unknown number, because they need different actions", async () => {
    const check = await refuse("name-mismatch");

    // "Ask them to say their name again" and "re-read the digits" are different instructions, and an
    // operator who reads "no match" is likely to try again more loosely rather than stop.
    expect(await screen.findByTestId("elig-mismatch")).toBeInTheDocument();
    expect(screen.getByText(/does not match this number/i)).toBeInTheDocument();
    expect(screen.getByText(/coverage behind it is somebody else's/i)).toBeInTheDocument();
    // Nothing was checked. The point of refusing is that no verdict is produced for the wrong record.
    expect(check).not.toHaveBeenCalled();
  });

  it("does not show the name on file — the service does not send it and the screen has none", async () => {
    await refuse("name-mismatch");
    await screen.findByTestId("elig-mismatch");

    // A screen that said "no, that card belongs to Amal Hassan" would give the name behind any card number
    // to whoever holds one — a worse disclosure than the defect this replaced.
    expect(screen.queryByText(/amal/i)).toBeNull();
    expect(screen.queryByText(/hassan/i)).toBeNull();
  });

  it("says the number is unknown when it is", async () => {
    await refuse("not-found");
    expect(await screen.findByTestId("elig-empty")).toBeInTheDocument();
    expect(screen.getByText(/not registered yet/i)).toBeInTheDocument();
  });

  it("asks for more of the name rather than accepting one letter", async () => {
    await refuse("name-too-short");
    expect(await screen.findByTestId("elig-short")).toBeInTheDocument();
    expect(screen.getByText(/two letters or more/i)).toBeInTheDocument();
  });
});

describe("the coverage details are shown in full", () => {
  const RESULT = {
    scope: "membership" as const, benefitCategory: null,
    status: { kind: "ok" as const, label: { en: "Eligible", ar: "مؤهل" } },
    beneficiary: {
      id: "ben-1", name: { en: "Amal Hassan", ar: "أمل حسن" },
      cardNumber: "MRS-CARD-4821", memberNo: "MRS-M-2026-000001",
    },
    coverage: {
      planName: null,
      band: { en: "LAB · IMAGING · PHARMACY · CONSULT", ar: "مختبر · أشعة · صيدلية · كشف" },
      policyNo: "POL-2026-000318",
      annualCapRemaining: 2960,
      limits: [
        { category: "CONSULT", limitType: "Count", remaining: 4 },
        { category: "LAB", limitType: "Amount", remaining: 3200 },
      ],
    },
    costShare: { known: false as const, why: { en: "No category named.", ar: "لم تحدد فئة." } },
    visitGate: { allowed: true },
  };

  async function show(over: Record<string, unknown> = {}) {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { verifyBeneficiary: unknown }).verifyBeneficiary = vi.fn().mockResolvedValue({
      verified: true,
      hit: { id: "ben-1", name: { en: "Amal Hassan", ar: "أمل حسن" }, cardNumber: "MRS-CARD-4821" },
    });
    (api as { checkEligibility: unknown }).checkEligibility =
      vi.fn().mockResolvedValue({ ...RESULT, ...over });
    renderScreen(api);
    await user.type(idField(), "MRS-CARD-4821");
    await user.type(nameField(), "Hassan");
    await user.click(checkButton());
  }

  /**
   * Every limit, not the one the client happened to pick.
   *
   * The api client took the first monetary limit for the headline and discarded the rest, so a member with
   * four consultations and EGP 3,200 of laboratory cover left was summarised as one number — and the desk
   * could not answer "how many consultations do they have?" from the screen that question belongs on.
   */
  it("lists every benefit limit, with its own kind of quantity", async () => {
    await show();
    const table = await screen.findByRole("table", { name: /remaining by benefit/i });
    expect(within(table).getByText("CONSULT")).toBeInTheDocument();
    expect(within(table).getByText("LAB")).toBeInTheDocument();
    // A Count of 4 and an Amount of 3200 are different quantities, and only the second is money. Rendering
    // both as currency would put "EGP 4" against a member's consultations.
    expect(within(table).getByText("4")).toBeInTheDocument();
    expect(within(table).getByText(/3,200/)).toBeInTheDocument();
  });

  it("shows them at BENEFIT scope too, not only at membership scope", async () => {
    // Naming a category asks an ADDITIONAL question; it does not make the rest of the coverage less true,
    // and a desk that must run the check twice to see the whole picture will run it once.
    await show({ scope: "benefit", benefitCategory: "LAB" });
    expect(await screen.findByRole("table", { name: /remaining by benefit/i })).toBeInTheDocument();
  });

  it("shows the policy, and no plan name it does not have", async () => {
    await show();
    expect(await screen.findByText("POL-2026-000318")).toBeInTheDocument();
    // The client used to send the literal "Benefit coverage" here, so every card printed a plan name that
    // was not one and a reader could not tell the placeholder from a real plan.
    expect(screen.queryByText(/^benefit coverage$/i)).toBeNull();
    expect(screen.queryByText(/^plan$/i)).toBeNull();
  });

  it("shows the card and the member number as the different identifiers they are", async () => {
    await show();
    expect(await screen.findByText("MRS-CARD-4821")).toBeInTheDocument();
    expect(screen.getByText("MRS-M-2026-000001")).toBeInTheDocument();
  });

  it("does not print the same number twice under two labels", async () => {
    // When the projection has no card number the client falls back to the member number, and a card showing
    // "Card: X · Member no.: X" teaches a reader that the labels do not mean anything.
    await show({
      beneficiary: { ...RESULT.beneficiary, cardNumber: "MRS-M-2026-000001", memberNo: "MRS-M-2026-000001" },
    });
    await screen.findByText(/MRS-M-2026-000001/);
    expect(screen.queryByText(/member no\./i)).toBeNull();
  });

  it("says so plainly when a coverage carries no per-benefit limits", async () => {
    await show({ coverage: { ...RESULT.coverage, limits: [] } });
    expect(await screen.findByText(/no per-benefit limits/i)).toBeInTheDocument();
  });
});

describe("the fixture client applies the same rule as the service", () => {
  it("verifies a real identifier with a matching name", async () => {
    const api = new DevApiClient({ latencyMs: 0 });
    const v = await api.verifyBeneficiary("MRS-M-10231", "Hassan");
    expect(v.verified).toBe(true);
    if (!v.verified) throw new Error("unreachable");
    expect(v.hit.id).toBe("MRS-M-10231");
  });

  it("refuses the same identifier with somebody else's name", async () => {
    const api = new DevApiClient({ latencyMs: 0 });
    // The dev portal is where this behaviour is demonstrated, and a fixture that verified anything would
    // show a working screen for a rule that does not hold — which is how the original defect went unnoticed.
    expect(await api.verifyBeneficiary("MRS-M-10231", "Haddad")).toEqual({
      verified: false, reason: "name-mismatch",
    });
    expect(await api.verifyBeneficiary("MRS-M-10231", "H")).toEqual({
      verified: false, reason: "name-too-short",
    });
    expect(await api.verifyBeneficiary("MRS-M-99999", "Hassan")).toEqual({
      verified: false, reason: "not-found",
    });
  });
});

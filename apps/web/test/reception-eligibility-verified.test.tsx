import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor } from "@testing-library/react";
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

const idField = () => screen.getByLabelText(/card or id number/i);
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

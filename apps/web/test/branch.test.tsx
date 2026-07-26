import { describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderApp, renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ReportAccessInput } from "@mersal/contracts";
import { BranchSwitcher, type BranchOption } from "../src/shell/BranchSwitcher";
import { RestrictedResultCard, RequestAccessDialog, type RestrictedResult } from "../src/screens/RestrictedResultCard";

const branches: BranchOption[] = [
  { id: "b-maadi", name: "Maadi", isHome: true },
  { id: "b-dokki", name: "Dokki", isHome: false },
];

describe("14.8 — branch switcher", () => {
  it("BranchScoped: lists permitted branches (Home marked) and announces a switch", async () => {
    const onSwitch = vi.fn();
    renderNode(<BranchSwitcher memberScoped={false} branches={branches} activeBranchId="b-maadi" onSwitch={onSwitch} />);

    const select = screen.getByLabelText(/active branch/i) as HTMLSelectElement;
    expect(within(select).getByRole("option", { name: /Maadi · Home/ })).toBeInTheDocument();

    await userEvent.selectOptions(select, "b-dokki");
    expect(onSwitch).toHaveBeenCalledWith("b-dokki");
    expect(screen.getByTestId("branch-live")).toHaveTextContent(/Dokki/);
  });

  it("MemberScoped: shows an All branches indicator, never a restriction", () => {
    renderNode(<BranchSwitcher memberScoped branches={[]} activeBranchId={null} onSwitch={vi.fn()} />);
    expect(screen.getByTestId("all-branches-indicator")).toHaveTextContent(/all branches/i);
    expect(screen.queryByLabelText(/active branch/i)).not.toBeInTheDocument();
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderNode(<BranchSwitcher memberScoped={false} branches={branches} activeBranchId="b-maadi" onSwitch={vi.fn()} />);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

describe("14.8 — restricted-result card", () => {
  const result: RestrictedResult = { restricted: true, category: "CPT", status: "Completed", orderingBranch: "Maadi", sensitivityLevel: "Sensitive" };

  it("renders the locked state with a RESTRICTED marker and a request action — and NO value fields", () => {
    renderNode(<RestrictedResultCard result={result} onRequestAccess={vi.fn()} />);
    expect(screen.getByTestId("restricted-chip")).toHaveTextContent(/restricted/i);
    expect(screen.getByRole("button", { name: /request access/i })).toBeInTheDocument();
    // The payload is existence-only: no value/result-content fields are present anywhere.
    const card = screen.getByRole("region", { name: /restricted result/i });
    expect(card.textContent ?? "").not.toMatch(/mg\/dl|mmol|positive|negative|value/i);
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderNode(<RestrictedResultCard result={result} onRequestAccess={vi.fn()} />);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

describe("14.8 — request-access dialog", () => {
  it("requires purpose + justification, then submits", async () => {
    const onSubmit = vi.fn();
    renderNode(<RequestAccessDialog onSubmit={onSubmit} onCancel={vi.fn()} />);

    // Submit empty → inline validation, no submit.
    await userEvent.click(screen.getByRole("button", { name: /submit request/i }));
    expect(onSubmit).not.toHaveBeenCalled();
    expect(screen.getByText(/a purpose is required/i)).toBeInTheDocument();
    expect(screen.getByText(/a justification is required/i)).toBeInTheDocument();

    await userEvent.selectOptions(screen.getByLabelText(/purpose/i), "ClinicalReview");
    await userEvent.type(screen.getByLabelText(/justification/i), "continuity of care");
    await userEvent.click(screen.getByRole("button", { name: /submit request/i }));

    expect(onSubmit).toHaveBeenCalledWith(expect.objectContaining({ purposeCode: "ClinicalReview", justification: "continuity of care" }));
  });
});

/**
 * The gap-fix: prove the restricted-result card is reachable through the LIVE results inbox — opening a
 * sensitivity-restricted result surfaces the locked card and the request-access flow reaches the API. A
 * Standard result in the same inbox shows its value, so the gate — not the UI — decides disclosure.
 */
describe("14.6/14.8 — results inbox wires the sensitivity gate", () => {
  class ReqSpyApi extends DevApiClient {
    requests: ReportAccessInput[] = [];
    override requestReportAccess(input: ReportAccessInput) {
      this.requests.push(input);
      return super.requestReportAccess(input);
    }
  }

  it("opens a restricted result as the locked card and files an access request", async () => {
    const api = new ReqSpyApi({ latencyMs: 0 });
    renderApp("/clinician/results", "doctor", api);

    // The completed results inbox renders with a per-row "View result" action.
    const viewButtons = await screen.findAllByRole("button", { name: /view result/i });
    // ord-2 (row order: ord-2 then ord-3) is the sensitivity-restricted one.
    await userEvent.click(viewButtons[0]);

    // Locked card appears — existence only, no value.
    expect(await screen.findByTestId("restricted-chip")).toHaveTextContent(/restricted/i);

    // Request access → dialog → submit reaches the API with the order/line anchor.
    await userEvent.click(screen.getByRole("button", { name: /request access/i }));
    await userEvent.selectOptions(await screen.findByLabelText(/purpose/i), "ClinicalReview");
    await userEvent.type(screen.getByLabelText(/justification/i), "continuity of care");
    await userEvent.click(screen.getByRole("button", { name: /submit request/i }));

    expect(api.requests).toHaveLength(1);
    expect(api.requests[0]).toMatchObject({ purposeCode: "ClinicalReview", orderId: "ord-2", lineId: "ln-2" });
  });

  it("shows the value for a NON-restricted result (gate discloses)", async () => {
    renderApp("/clinician/results", "doctor", new DevApiClient({ latencyMs: 0 }));
    const viewButtons = await screen.findAllByRole("button", { name: /view result/i });
    // ord-3 is a Standard lab result → value disclosed.
    await userEvent.click(viewButtons[1]);
    expect(await screen.findByText(/within reference range/i)).toBeInTheDocument();
  });
});

/** Claims portal smoke — the newly-created officer portal renders its worklist against the live contract. */
describe("Claims portal (Phase 10b UI)", () => {
  it("renders the claims worklist for the claims officer", async () => {
    renderApp("/claims/worklist", "claims_officer", new DevApiClient({ latencyMs: 0 }));
    expect(await screen.findByRole("heading", { name: /claims worklist/i })).toBeInTheDocument();
    // A claim row from the fixture is present (masked claim number, amounts — no diagnosis).
    expect(await screen.findByText(/CLM-2026-004411/)).toBeInTheDocument();
  });
});

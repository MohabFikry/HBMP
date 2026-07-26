import { describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
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

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

    // A combobox, not a native <select>: the OS draws a native option list itself, so it could never wear
    // the app's surface/accent. Closed, it must contribute no listbox at all.
    const combo = screen.getByRole("combobox", { name: /active branch/i });
    // `toHaveValue`, not `toHaveTextContent`: this is a searchable Combobox now, so the control is an <input>
    // and the branch is its value. "· Home" is still there because the switcher passes `hintWhenClosed` —
    // the qualifier saying which of the branches is this operator's own.
    expect(combo).toHaveValue("Maadi · Home");
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();

    await userEvent.click(combo);
    const list = screen.getByRole("listbox");
    expect(within(list).getByRole("option", { name: /Maadi · Home/ })).toHaveAttribute("aria-selected", "true");

    await userEvent.click(within(list).getByRole("option", { name: "Dokki" }));
    expect(onSwitch).toHaveBeenCalledWith("b-dokki");
    expect(screen.getByTestId("branch-live")).toHaveTextContent(/Dokki/);
    expect(screen.queryByRole("listbox")).not.toBeInTheDocument();
  });

  it("BranchScoped: is operable by keyboard alone, with focus kept on the combobox", async () => {
    const onSwitch = vi.fn();
    renderNode(<BranchSwitcher memberScoped={false} branches={branches} activeBranchId="b-maadi" onSwitch={onSwitch} />);
    const combo = screen.getByRole("combobox", { name: /active branch/i });

    // A real <input>, so it is in the tab order without a tabindex of its own (the harness renders a skip
    // link ahead of it, so reach it directly rather than counting tab stops). It was a <button> while this
    // was a `Select`; what the assertion is about — a natively focusable element rather than a div with a
    // tabindex bolted on — is true of both, so the element name is checked rather than assumed.
    expect(combo.tagName).toBe("INPUT");
    expect(combo).not.toHaveAttribute("tabindex");
    combo.focus();
    expect(combo).toHaveFocus();
    await userEvent.keyboard("{ArrowDown}"); // opens, active = the selection
    expect(screen.getByRole("listbox")).toBeInTheDocument();
    await userEvent.keyboard("{ArrowDown}"); // → Dokki
    // Focus never moves into the list; the active option is named by aria-activedescendant instead.
    expect(combo).toHaveFocus();
    const activeId = combo.getAttribute("aria-activedescendant");
    expect(document.getElementById(activeId ?? "")).toHaveTextContent("Dokki");

    await userEvent.keyboard("{Enter}");
    expect(onSwitch).toHaveBeenCalledWith("b-dokki");
  });

  it("MemberScoped: shows an All branches indicator, never a restriction", () => {
    renderNode(<BranchSwitcher memberScoped branches={[]} activeBranchId={null} onSwitch={vi.fn()} />);
    expect(screen.getByTestId("all-branches-indicator")).toHaveTextContent(/all branches/i);
    expect(screen.queryByRole("combobox")).not.toBeInTheDocument();
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderNode(<BranchSwitcher memberScoped={false} branches={branches} activeBranchId="b-maadi" onSwitch={vi.fn()} />);
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });

  it("has no serious/critical a11y violations with the list OPEN", async () => {
    // The listbox only exists while open, so the closed-state sweep above never sees the new markup —
    // aria-activedescendant, the option roles and the required-children relationship all live here.
    //
    // Swept over `document.body`, NOT over `container`, and that is load-bearing rather than incidental: the
    // list is portalled out of the control (see `Popup.tsx`), so a sweep scoped to the render container no
    // longer contains the markup this test exists to check. It would still have passed — over nothing. The
    // relationships being validated span the portal (`aria-controls` and `aria-activedescendant` on the input
    // point at ids inside the list), so both halves have to be in scope for the check to mean anything.
    renderNode(<BranchSwitcher memberScoped={false} branches={branches} activeBranchId="b-maadi" onSwitch={vi.fn()} />);
    await userEvent.click(screen.getByRole("combobox", { name: /active branch/i }));
    expect(screen.getByRole("listbox")).toBeInTheDocument();
    // `region` is off for this one sweep only. It is a PAGE-structure rule — "all content sits inside a
    // landmark" — and the harness renders a component, not a page, so at document scope it fires on the skip
    // link and on the switcher itself and says nothing about either. Scoping to `container` used to dodge it
    // by accident; now that the scope has to be the document, the dodge has to be deliberate and named.
    expect(await axe(document.body, {
      rules: { "color-contrast": { enabled: false }, region: { enabled: false } },
    })).toHaveNoViolations();
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

    // The purpose control is the design system's Select (a listbox combobox), not a native <select> — so it
    // is opened and chosen from, the way a user does, rather than driven by selectOptions. The native
    // element it replaced could not be styled and opened an OS-drawn list on the one screen that must read
    // as unmistakably different from an ordinary result.
    await userEvent.click(screen.getByRole("combobox", { name: /purpose/i }));
    await userEvent.click(screen.getByRole("option", { name: "ClinicalReview" }));
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
    await userEvent.click(await screen.findByRole("combobox", { name: /purpose/i }));
    await userEvent.click(screen.getByRole("option", { name: "ClinicalReview" }));
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

/** Claims portal smoke — the officer portal renders its worklist against the live contract. */
describe("Claims portal (Phase 10b UI)", () => {
  it("renders the claims worklist for the claims officer", async () => {
    renderApp("/claims/worklist", "claims_officer", new DevApiClient({ latencyMs: 0 }));
    // "Claims", not "Claims Worklist": the line-level queue is now its own screen, "Adjudication", and the
    // old title claimed both jobs for a screen that was reading the line endpoint and rendering claim columns.
    expect(await screen.findByRole("heading", { name: /^claims$/i })).toBeInTheDocument();
    // A claim row from the fixture is present (masked claim number, amounts — no diagnosis).
    expect(await screen.findByText(/CLM-2026-004411/)).toBeInTheDocument();
  });
});

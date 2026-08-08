import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { AmendLineDialog } from "../src/screens/AmendLineDialog";
import type { AmendLineDialogProps } from "../src/screens/AmendLineDialog";

/**
 * 30.6 / design 46 §7, §10 — cancelling and amending a signed line from the doctor's view.
 *
 * <p>Three properties carry this dialog, and each has a failure the screen would otherwise hide:</p>
 * <ul>
 *   <li><b>The reason is coded and mandatory.</b> Free text alone answers nothing at scale.</li>
 *   <li><b>A locked line explains, it does not vanish.</b> "A hidden control makes the doctor think the
 *   feature is missing" — and then they ring the pharmacy instead.</li>
 *   <li><b>The chronic preview marks the collected portion immutable</b> BEFORE confirming, because the
 *   prescriber's real question is what happens to what the patient already has.</li>
 * </ul>
 */

const REASONS = [
  { code: "PrescribingError", nameEn: "Prescribing error", nameAr: "خطأ في الوصف" },
  { code: "ClinicalChange", nameEn: "Clinical change", nameAr: "تغير الحالة السريرية" },
];

function renderDialog(overrides: Partial<AmendLineDialogProps> = {}) {
  const onConfirm = vi.fn();
  const props: AmendLineDialogProps = {
    open: true,
    action: "cancel",
    lineLabel: "80053 — Comprehensive metabolic panel",
    reasons: REASONS,
    onCancel: () => {},
    onConfirm,
    ...overrides,
  };
  const view = render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <AmendLineDialog {...props} />
    </AppProviders>,
  );
  return { ...view, onConfirm };
}

describe("30.6 — amend/cancel dialog", () => {
  it("NAMES the line being withdrawn rather than saying 'this line'", () => {
    renderDialog();
    // A doctor confirming "are you sure?" is confirming the sentence in their head, not this one.
    expect(screen.getByTestId("amend-subject")).toHaveTextContent("Comprehensive metabolic panel");
  });

  it("refuses to submit without a coded reason, and says so", async () => {
    const user = userEvent.setup();
    const { onConfirm } = renderDialog();

    await user.click(screen.getByRole("button", { name: /withdraw item/i }));

    expect(onConfirm).not.toHaveBeenCalled();
    expect(screen.getByText(/a reason is required/i)).toBeInTheDocument();
  });

  it("submits the CODE, with the free text as an addition and never a substitute", async () => {
    const user = userEvent.setup();
    const { onConfirm } = renderDialog();

    await user.click(screen.getByRole("combobox", { name: /reason/i }));
    await user.click(await screen.findByRole("option", { name: /clinical change/i }));
    await user.type(screen.getByLabelText(/notes/i), "patient improved");
    await user.click(screen.getByRole("button", { name: /withdraw item/i }));

    expect(onConfirm).toHaveBeenCalledWith(
      expect.objectContaining({ reasonCode: "ClinicalChange", reasonText: "patient improved" }),
    );
  });

  it("a CONSUMED line explains itself instead of disappearing", () => {
    // Design 46 §10: consumed lines show the action disabled WITH THE REASON VISIBLE — not hidden.
    renderDialog({ locked: { what: "Consumed", when: "2026-08-07T14:32:00Z", by: "Maadi Pharmacy" } });

    expect(screen.getByTestId("amend-locked")).toBeInTheDocument();
    expect(screen.getByText(/already delivered/i)).toBeInTheDocument();
    expect(screen.getByText(/Maadi Pharmacy/)).toBeInTheDocument();
    // No confirm affordance at all — the dialog is explaining, not asking.
    expect(screen.queryByRole("button", { name: /withdraw item/i })).not.toBeInTheDocument();
  });

  it("an EXPIRED order says the recovery, because it is different from every other lock", () => {
    renderDialog({ locked: { what: "Expired" } });

    expect(screen.getByText(/approval team can revalidate/i)).toBeInTheDocument();
  });

  it("the chronic preview shows the collected portion FIRST and marks it immutable", () => {
    renderDialog({
      action: "amend",
      currentQuantity: 270,
      chronicPreview: {
        newTotal: 180, alreadyDispensed: 90, remainingWindows: [90], verdict: "Reallocated",
      },
    });

    const preview = screen.getByTestId("chronic-preview");
    expect(preview).toHaveTextContent("90");
    expect(screen.getByText(/immutable/i)).toBeInTheDocument();
    expect(preview).toHaveTextContent("180");
  });

  it("does not carry a reason forward from one line to the next", async () => {
    // A coded reason silently reused on a different line is a reason that is WRONG, which is worse than one
    // that is absent — and it would be invisible, because the field would look filled in.
    const user = userEvent.setup();
    const { rerender } = renderDialog();

    await user.click(screen.getByRole("combobox", { name: /reason/i }));
    await user.click(await screen.findByRole("option", { name: /clinical change/i }));

    rerender(
      <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
        <AmendLineDialog
          open action="cancel" lineLabel="85025 — Complete blood count"
          reasons={REASONS} onCancel={() => {}} onConfirm={vi.fn()}
        />
      </AppProviders>,
    );

    expect(screen.getByText(/choose a reason/i)).toBeInTheDocument();
  });

  it("is axe-clean in English and in Arabic RTL", async () => {
    const { container, unmount } = renderDialog({
      action: "amend",
      currentQuantity: 270,
      chronicPreview: {
        newTotal: 180, alreadyDispensed: 90, remainingWindows: [90], verdict: "Reallocated",
      },
    });
    expect(await axe(container)).toHaveNoViolations();
    unmount();

    document.documentElement.lang = "ar";
    document.documentElement.dir = "rtl";
    try {
      const rtl = renderDialog({ locked: { what: "Consumed", when: "2026-08-07T14:32:00Z" } });
      expect(await axe(rtl.container)).toHaveNoViolations();
    } finally {
      document.documentElement.lang = "en";
      document.documentElement.dir = "ltr";
    }
  });
});

/**
 * 30.6 — the WIRING, which is what a component test alone cannot prove.
 *
 * <p>The dialog was written, type-checked, tested and axe-clean while no screen imported it — so Vite
 * tree-shook it out and the shipped bundle contained none of it. That is the same shape of gap as phase 29's
 * unreachable chronic machinery, and the lesson is identical: a green component test says the component is
 * correct, never that anything opens it.</p>
 */
describe("30.6 — the detail dialogs actually reach the API", () => {
  it("withdrawing a line from the ORDER dialog calls cancelOrderLine with the coded reason", async () => {
    const { OrderDetailModal } = await import("../src/screens/encounter/OrderDetailModal");
    const cancelOrderLine = vi.fn().mockResolvedValue(undefined);
    const api = {
      amendmentReasons: vi.fn().mockResolvedValue(REASONS),
      cancelOrderLine,
    } as any;

    const order = {
      id: "ord-1", orderNo: "ORD-2026-000118", orderType: "Lab", primaryCode: "80053",
      lineCount: 1, beneficiary: { id: "ben-1", token: "•••4821" },
      status: { kind: "info", label: { en: "Active", ar: "نشط" } },
      requestedAt: "2026-08-07T09:00:00Z", expiresAt: null, encounterId: "enc-1",
      lines: [{
        id: "line-1", code: "80053", codeSystem: "CPT", description: "Comprehensive metabolic panel",
        quantityOrdered: 1, quantityConsumed: 0,
        status: { kind: "info", label: { en: "Active", ar: "نشط" } },
      }],
    } as any;

    const user = userEvent.setup();
    render(
      <AppProviders authClient={new DevAuthClient()} apiClient={api}>
        <OrderDetailModal order={order} onOpenChange={() => {}} />
      </AppProviders>,
    );

    // The action is on the LINE, because that is the amendable unit (design 46 §3).
    await user.click(await screen.findByRole("button", { name: /^withdraw$/i }));
    await user.click(await screen.findByRole("combobox", { name: /reason/i }));
    await user.click(await screen.findByRole("option", { name: /clinical change/i }));
    await user.click(screen.getByRole("button", { name: /withdraw item/i }));

    expect(cancelOrderLine).toHaveBeenCalledWith("ord-1", "line-1", "ClinicalChange", undefined);
  });

  it("a DELIVERED line offers the action disabled, with the reason beside it", async () => {
    const { OrderDetailModal } = await import("../src/screens/encounter/OrderDetailModal");
    const order = {
      id: "ord-2", orderNo: "ORD-2026-000119", orderType: "Lab", primaryCode: "85025",
      lineCount: 1, beneficiary: { id: "ben-1", token: "•••4821" },
      status: { kind: "ok", label: { en: "Completed", ar: "مكتمل" } },
      requestedAt: "2026-08-07T09:00:00Z", expiresAt: null, encounterId: "enc-1",
      lines: [{
        id: "line-2", code: "85025", codeSystem: "CPT", description: "Complete blood count",
        quantityOrdered: 1, quantityConsumed: 1,
        status: { kind: "ok", label: { en: "Completed", ar: "مكتمل" } },
      }],
    } as any;

    render(
      <AppProviders authClient={new DevAuthClient()} apiClient={{ amendmentReasons: vi.fn().mockResolvedValue([]) } as any}>
        <OrderDetailModal order={order} onOpenChange={() => {}} />
      </AppProviders>,
    );

    // PRESENT and disabled — never absent. A hidden control reads as a missing feature.
    const withdraw = await screen.findByRole("button", { name: /^withdraw$/i });
    expect(withdraw).toBeDisabled();
    expect(screen.getByText(/delivered — cannot be changed/i)).toBeInTheDocument();
    // And the explanation is tied to the control for a screen reader, not merely near it.
    expect(withdraw).toHaveAttribute("aria-describedby", "lock-line-2");
  });
});

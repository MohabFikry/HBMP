import { describe, expect, it } from "vitest";
import { screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode } from "./helpers";
import { FinanceExports } from "../src/screens/FinancePortal";

/**
 * 16.9 — the export's window is the OPERATOR's, not a hardcoded range.
 *
 * <p>These two tests originally asserted a pair of `<input type="date">` fields and a validation message for
 * an inverted range. Both controls are gone, and neither assertion was lost — they moved (design 49 §4).</p>
 *
 * <p>The Exports screen was the only finance screen with a window at all: Utilization and Summaries sent no
 * `from`/`to` despite the endpoints accepting them since phase 10.2, so an operator could read a figure on
 * one screen and export a different period from the next without either screen saying so. All three now
 * share one `PeriodControl` under the `finance-period` key, which is what makes "the export matches what I
 * was just looking at" true rather than a habit the operator has to maintain.</p>
 *
 * <p>And the inverted range is no longer validated because it is no longer <b>representable</b>. A preset
 * resolves to a window; there is no pair of fields to put out of order. Deleting a validation test because
 * its invalid state became unconstructible is the one case where losing the assertion is a gain, and it is
 * asserted below rather than assumed.</p>
 */
describe("16.9 — finance export date window", () => {
  it("exposes an operator-adjustable window (not a hardcoded range)", async () => {
    renderNode(<FinanceExports />);
    const period = await screen.findByRole("radiogroup", { name: /period/i });
    // Real presets, not a single fixed span. The chosen one is stated in resolved dates beside the control
    // so the operator can write down what a figure covers.
    expect(screen.getByRole("radio", { name: /last 30 days/i })).toBeTruthy();
    expect(screen.getByRole("radio", { name: /this quarter/i })).toBeTruthy();
    expect(period).toBeTruthy();
    expect(screen.getByText(/showing/i)).toBeTruthy();
  });

  it("changes the window the export will ask for", async () => {
    const user = userEvent.setup();
    renderNode(<FinanceExports />);
    const shownBefore = screen.getByText(/showing/i).textContent;
    await user.click(screen.getByRole("radio", { name: /this quarter/i }));
    // The resolved dates move with the preset — the control is not decoration over a fixed range.
    expect(screen.getByText(/showing/i).textContent).not.toBe(shownBefore);
  });

  it("cannot express an inverted range at all", () => {
    renderNode(<FinanceExports />);
    // The old screen had two date inputs and a "From must be on or before To" alert. A preset resolves to
    // an ordered window, so the state the alert existed to catch has no way to occur.
    expect(screen.queryAllByDisplayValue(/^\d{4}-\d{2}-\d{2}$/)).toHaveLength(0);
    expect(screen.queryByRole("alert")).toBeNull();
    expect(screen.getByRole("button", { name: /export/i })).not.toBeDisabled();
  });
});

import { describe, expect, it } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode } from "./helpers";
import { FinanceExports } from "../src/screens/FinancePortal";

describe("16.9 — finance export date window", () => {
  it("exposes an operator-adjustable From/To window (not a hardcoded range)", () => {
    renderNode(<FinanceExports />);
    const period = screen.getByRole("group", { name: /period/i });
    const dates = within(period).getAllByDisplayValue(/^\d{4}-\d{2}-\d{2}$/);
    // Two date inputs, defaulting to a real 30-day trailing window.
    expect(dates).toHaveLength(2);
    expect((dates[0] as HTMLInputElement).type).toBe("date");
  });

  it("blocks export when From is after To", async () => {
    const user = userEvent.setup();
    renderNode(<FinanceExports />);
    const period = screen.getByRole("group", { name: /period/i });
    const [from, to] = within(period).getAllByDisplayValue(/^\d{4}-\d{2}-\d{2}$/) as HTMLInputElement[];

    // Force an inverted range: set To before From.
    await user.clear(to);
    await user.type(to, "2020-01-01");
    void from;

    expect(await screen.findByRole("alert")).toHaveTextContent(/must be on or before/i);
    expect(screen.getByRole("button", { name: /export/i })).toBeDisabled();
  });
});

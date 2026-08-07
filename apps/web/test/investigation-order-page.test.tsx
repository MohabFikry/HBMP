import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ApiError } from "../src/api/http";
import { InvestigationOrderPage } from "../src/screens/lab/InvestigationOrderPage";

/**
 * One investigation order, on its own page (ADR-0034).
 *
 * <p>The bench's counterpart of the prescription page, and these tests guard the same two things for the same
 * reason. The MONEY: a bench is the last point at which a beneficiary is told what to pay, with no reviewer
 * between that sentence and the patient, so a figure the screen cannot establish must read "cannot be quoted"
 * and never 0.00 — a zero at a counter means "free". And the SUBSTITUTION: examinations have no equivalence
 * set anywhere in master data, so the control here must ASK the approval team rather than offer a list
 * nobody has vetted.</p>
 */

const PRICED = "ORD-2026-055012";
const NO_SPLIT = "ORD-2026-055019";
const UNPRICED = "ORD-2026-077009";

function renderPage(orderNo = PRICED, api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <InvestigationOrderPage orderNo={orderNo} />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("the investigation order page", () => {
  it("shows every line, not just the first", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    // The queue collapsed an order to its first test and a panel count. A three-line order was three facts
    // squeezed into one number, and the technician could not say which two of the three they had performed.
    expect(within(table).getByText("Complete blood count")).toBeInTheDocument();
    expect(within(table).getByText("Erythrocyte sedimentation rate")).toBeInTheDocument();
    expect(within(table).getByText("C-reactive protein")).toBeInTheDocument();
  });

  it("shows ordered and performed as separate figures", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    // "2 of 5" and "3 remaining" answer different questions. A bench showing only the remainder cannot tell
    // a fresh order from one the patient has been working through across three visits.
    for (const header of ["Ordered", "Performed", "Remaining"]) {
      expect(within(table).getByRole("columnheader", { name: header })).toBeInTheDocument();
    }
  });

  it("gives every per-line control the examination's name", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    // Identically-labelled buttons on a multi-line order are unusable by keyboard or screen reader — and
    // picking the wrong row asks the approval team about the wrong test.
    const buttons = within(table).getAllByRole("button", { name: /Ask about a different examination — / });
    expect(buttons.length).toBe(3);
    expect(new Set(buttons.map((b) => b.getAttribute("aria-label"))).size).toBe(3);
  });

  it("shows the unit price on every line, and says so when there is none", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    // "515 for the order" does not tell a technician which examination is the expensive one, which is the
    // conversation when a patient cannot afford all of it today.
    expect(within(table).getByRole("columnheader", { name: "Unit price" })).toBeInTheDocument();
    // NOT a match on "EGP". `money()` formats in the APP's language, so an Arabic render produces
    // "\u0661\u0668\u0660\u066b\u0660\u0660 \u062c.\u0645." and the currency code never appears — a test asserting the Latin symbol
    // passes or fails on whichever language the previous file left behind. What is worth pinning is that a
    // priced line shows a PRICE rather than the not-recorded fallback.
    //
    // And it WAITS. The price is a second fetch, so the rows render "No price" until it lands — asserting
    // immediately passes only when the machine is fast enough, which is a test that reports load, not truth.
    await waitFor(() => {
      expect(within(screen.getByRole("table")).queryByText("No price")).toBeNull();
    });
  });

  it("says NO PRICE on a line the catalogue does not price, never 0.00", async () => {
    renderPage(UNPRICED);
    const table = await screen.findByRole("table");

    // No examination in master data carries a price today, so this is the state the bench actually meets.
    // A zero here would tell a patient the scan is free.
    expect(within(table).getAllByText("No price").length).toBeGreaterThan(0);
    expect(within(table).queryByText(/EGP\s*0\.00|0\.00\s*EGP/)).toBeNull();
  });

  it("shows the three figures", async () => {
    renderPage();

    expect(await screen.findByText("Order total")).toBeInTheDocument();
    expect(screen.getByText("Patient pays")).toBeInTheDocument();
    expect(screen.getByText("Payer pays")).toBeInTheDocument();
  });

  it("quotes the two shares on what is being performed, not on the whole order", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "orderPricing");
    renderPage(PRICED, api);

    const table = await screen.findByRole("table");
    await screen.findByText("Order total");

    // Before anything is entered the tiles answer "what if the whole order is delivered" — and say so.
    // Quoting a basis of nothing would put "Patient pays EGP 0.00" on an untouched bench screen.
    expect(screen.getAllByText("If the whole order is delivered").length).toBe(2);

    const box = within(table).getAllByRole("spinbutton")[0];
    await user.clear(box);
    await user.type(box, "1");

    // Re-quoted against the server on the new basis, never scaled here: the split runs a deductible before
    // coinsurance, so one examination of three does not cost a third of the share.
    await waitFor(
      () => expect(screen.getAllByText("For the 1 being performed now").length).toBe(2),
      { timeout: 3000 },
    );

    const basis = spy.mock.calls[spy.mock.calls.length - 1]?.[1];
    expect(basis).toBeTruthy();
    expect(Object.values(basis as Record<string, number>)).toEqual([1]);
  });

  it("clears the shares rather than leaving stale ones beside a changed quantity", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    renderPage(PRICED, api);

    const table = await screen.findByRole("table");
    await screen.findByText("Order total");

    (api as { orderPricing: unknown }).orderPricing = vi.fn().mockRejectedValue(new Error("down"));

    const box = within(table).getAllByRole("spinbutton")[0];
    await user.clear(box);
    await user.type(box, "1");

    // A share left over from the previous quantity looks like an answer, which is precisely why it is worse
    // than none — it would be read out to the patient without hesitation.
    await waitFor(
      () => expect(screen.getByText(/NOT a report that it is free/i)).toBeInTheDocument(),
      { timeout: 3000 },
    );
  });

  it("NEVER renders an unquotable share as a zero amount", async () => {
    renderPage(NO_SPLIT);

    // The total is still shown — the list price IS known — but the split says it cannot be quoted, with the
    // reason, so the technician does not improvise a figure.
    const unknown = await screen.findAllByText("Cannot be quoted");
    expect(unknown.length).toBe(2);
    expect(screen.queryByText(/EGP\s*0\.00|0\.00\s*EGP/)).toBeNull();
    expect(screen.getByText(/does not price this examination category/i)).toBeInTheDocument();
  });

  it("withholds the total too when nothing on the order has a price", async () => {
    renderPage(UNPRICED);

    // Three unknowns, not a zero total. Quoting the priced lines alone would understate what is owed, and
    // quoting nothing at all as 0 would say the scan is free.
    const unknown = await screen.findAllByText("Cannot be quoted");
    expect(unknown.length).toBe(3);
    expect(screen.queryByText(/EGP\s*0\.00|0\.00\s*EGP/)).toBeNull();
  });

  it("does not render a price at all when pricing fails", async () => {
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { orderPricing: unknown }).orderPricing = vi.fn().mockRejectedValue(new Error("down"));
    renderPage(PRICED, api);

    // A failed fetch is not a free scan — the same rule the clinical checks and the dispensing counter follow.
    expect(await screen.findByText(/NOT a report that it is free/i)).toBeInTheDocument();
  });

  it("says the priced figure is an estimate, not the final bill", async () => {
    renderPage();
    expect(await screen.findByText(/final amount is set when the claim is adjudicated/i)).toBeInTheDocument();
  });
});

describe("the action bar", () => {
  it("fills every remaining quantity from one control", async () => {
    const user = userEvent.setup();
    renderPage();
    const table = await screen.findByRole("table");

    // A three-panel order performed in full is the common case, and typing three numbers to say so is three
    // chances to type one of them wrong.
    await user.click(screen.getAllByRole("button", { name: "Perform all" })[0]);

    for (const input of within(table).getAllByRole("spinbutton")) {
      expect(Number((input as HTMLInputElement).value)).toBeGreaterThan(0);
    }
  });

  it("fills one line from its own tick, and unfills it on a second press", async () => {
    const user = userEvent.setup();
    renderPage();
    const table = await screen.findByRole("table");

    const tick = within(table).getAllByRole("button", { name: /Fill the remaining quantity — / })[0];
    // aria-pressed, not colour: the filled state has to be announced, not just seen.
    expect(tick).toHaveAttribute("aria-pressed", "false");
    await user.click(tick);
    expect(tick).toHaveAttribute("aria-pressed", "true");
    await user.click(tick);
    expect(tick).toHaveAttribute("aria-pressed", "false");
  });

  it("will not submit nothing", async () => {
    renderPage();
    await screen.findByRole("table");

    expect(screen.getByRole("button", { name: /^Submit$/ })).toBeDisabled();
    expect(screen.getByText("Nothing selected")).toBeInTheDocument();
  });

  it("counts what is about to be recorded", async () => {
    const user = userEvent.setup();
    renderPage();
    const table = await screen.findByRole("table");

    await user.click(within(table).getAllByRole("button", { name: /Fill the remaining quantity — / })[0]);

    // The running total stays visible while quantities are typed — it is the last thing worth checking
    // before an irreversible act, and it was previously below the fold.
    expect(await screen.findByText(/1 of 3 lines · 1 units/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Submit$/ })).toBeEnabled();
  });

  it("refuses a quantity larger than what is left, and says so on the line", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    const box = within(table).getAllByRole("spinbutton")[0];
    // fireEvent, not user.type: user-event enforces a number input's own `max` while typing, which is the
    // very thing under test — and a pharmacist pasting or using the spinner bypasses it anyway. This is the
    // entry the browser actually delivers.
    fireEvent.change(box, { target: { value: "99" } });

    // NOT silently clamped to the remainder. Rewriting 99 to 1 under the operator's hand changes the
    // figure they are about to confirm without telling them — the same defect as an audit that quietly
    // corrects a row. What they typed stays, the field says what is wrong, and Submit refuses.
    expect(box).toHaveValue(99);
    expect(box).toHaveAttribute("aria-invalid", "true");
    expect(await screen.findByText(/Only 1 left on this line/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Submit$/ })).toBeDisabled();
  });

  it("lets it through again once the quantity is corrected", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    const box = within(table).getAllByRole("spinbutton")[0];
    fireEvent.change(box, { target: { value: "99" } });
    expect(screen.getByRole("button", { name: /^Submit$/ })).toBeDisabled();

    fireEvent.change(box, { target: { value: "1" } });
    expect(screen.getByRole("button", { name: /^Submit$/ })).toBeEnabled();
  });

  it("says the screen is current when the audit finds nothing moved", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole("table");

    await user.click(screen.getByRole("button", { name: "Audit" }));
    expect(await screen.findByText(/are current/i)).toBeInTheDocument();
  });

  it("reports a panel performed elsewhere rather than silently correcting it", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    renderPage(PRICED, api as unknown as ApiClient);
    await screen.findByRole("table");

    // Somebody else worked this order at another site while it sat open on this bench. The audit must SAY
    // so — a screen that silently swapped the numbers would leave the technician confirming quantities they
    // never read.
    vi.spyOn(api, "investigationOrder").mockImplementation(async (orderNo: string) => {
      const fresh = await DevApiClient.prototype.investigationOrder.call(api, orderNo);
      return fresh && {
        ...fresh,
        lines: fresh.lines.map((l, i) => (i === 0 ? { ...l, quantityConsumed: 1 } : l)),
      };
    });

    await user.click(screen.getByRole("button", { name: "Audit" }));

    expect(await screen.findByText(/out of date and has been refreshed/i)).toBeInTheDocument();
    expect(screen.getByText(/quantities performed elsewhere/i)).toBeInTheDocument();
  });

  it("does NOT claim the screen is current when the re-read failed", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    renderPage(PRICED, api as unknown as ApiClient);
    await screen.findByRole("table");

    vi.spyOn(api, "investigationOrder").mockRejectedValue(new Error("down"));
    await user.click(screen.getByRole("button", { name: "Audit" }));

    // The same rule the clinical checks and the price follow: a failed read is never rendered as a clean
    // result — and the order the technician is working from stays on screen rather than being blanked.
    expect(await screen.findByText(/nothing on this screen has been confirmed/i)).toBeInTheDocument();
    expect(screen.getByRole("table")).toBeInTheDocument();
  });
});

describe("asking about a different examination", () => {
  it("asks the approval team rather than offering a list", async () => {
    const user = userEvent.setup();
    renderPage();
    const table = await screen.findByRole("table");

    await user.click(within(table).getAllByRole("button", { name: /Ask about a different examination — / })[0]);
    const dialog = within(await screen.findByRole("dialog"));

    // No picker, and the screen says why: nothing in master data records that one test may stand in for
    // another, so a list here would be derived from the category — a technician prescribing.
    expect(dialog.getByText(/no approved list of equivalent examinations/i)).toBeInTheDocument();
    expect(dialog.queryAllByRole("radio")).toHaveLength(0);
  });

  it("refuses to send without a reason", async () => {
    const user = userEvent.setup();
    renderPage();
    const table = await screen.findByRole("table");

    await user.click(within(table).getAllByRole("button", { name: /Ask about a different examination — / })[0]);
    const dialog = within(await screen.findByRole("dialog"));

    // An approver with an empty box decides on who asked, not on why — and unlike a dispensing substitution
    // there is no formulary anyone downstream could infer the answer from.
    expect(dialog.getByRole("button", { name: "Send request" })).toBeDisabled();
    await user.type(dialog.getAllByRole("textbox")[0], "broken");
    expect(dialog.getByRole("button", { name: "Send request" })).toBeDisabled();
  });

  it("sends the line and the reason, and says the order is unchanged", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const ask = vi.spyOn(api, "requestSubstitution");
    renderPage(PRICED, api as unknown as ApiClient);
    const table = await screen.findByRole("table");

    await user.click(within(table).getAllByRole("button", { name: /Ask about a different examination — / })[0]);
    const dialog = within(await screen.findByRole("dialog"));
    await user.type(
      dialog.getAllByRole("textbox")[0],
      "The analyser is out of service until Thursday and the patient travelled today.",
    );
    await user.click(dialog.getByRole("button", { name: "Send request" }));

    await waitFor(() => expect(ask).toHaveBeenCalledTimes(1));
    expect(ask.mock.calls[0][0]).toMatchObject({
      orderReference: PRICED,
      orderedCode: "58410-2",
      reason: "The analyser is out of service until Thursday and the patient travelled today.",
    });

    // Nothing has been authorized yet, and the screen must not imply otherwise: the technician asked a
    // question, and the order stays exactly as the doctor wrote it until somebody answers.
    const outcome = await screen.findByText(/AUTH-2026-000488/);
    expect(outcome.textContent).toMatch(/order is unchanged until they decide/i);
  });

  it("treats 'already asked' as an answer, not a failure", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { requestSubstitution: unknown }).requestSubstitution =
      vi.fn().mockRejectedValue(new ApiError("http", "already open", 409));
    renderPage(PRICED, api);
    const table = await screen.findByRole("table");

    await user.click(within(table).getAllByRole("button", { name: /Ask about a different examination — / })[0]);
    const dialog = within(await screen.findByRole("dialog"));
    await user.type(dialog.getAllByRole("textbox")[0], "Scanner down since yesterday and the patient is here.");
    await user.click(dialog.getByRole("button", { name: "Send request" }));

    // A third copy of the same question makes the approval team work it three times.
    expect(await screen.findByText(/already asked about this line/i)).toBeInTheDocument();
  });
});

describe("accessibility", () => {
  it("has no serious or critical violations", async () => {
    const { container } = renderPage();
    await screen.findByRole("table");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

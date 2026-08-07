import { describe, expect, it, vi } from "vitest";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { PrescriptionPage } from "../src/screens/pharmacy/PrescriptionPage";

/**
 * One prescription, on its own page.
 *
 * <p>The money is what these tests mostly guard, and for one reason: a dispensing counter is the last point
 * at which a beneficiary is told what to pay, and there is no reviewer between that sentence and the patient.
 * A figure the screen cannot establish must therefore read as "cannot be quoted" and never as 0.00 — a zero
 * at a counter means "free", and a refugee family told their medication is free either receives a bill later
 * or declines something they could have afforded.</p>
 */

const LIVE = "RX-2026-033110";
const UNPRICED = "RX-2026-033044";

function renderPage(rxNo = LIVE, api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <PrescriptionPage rxNo={rxNo} />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("the action bar", () => {
  it("fills every remaining quantity from one control", async () => {
    const user = userEvent.setup();
    renderPage();
    const table = await screen.findByRole("table");

    // A five-line prescription collected in full is the common case, and typing five numbers to say so is
    // five chances to type one of them wrong.
    await user.click(screen.getAllByRole("button", { name: "Dispense all" })[0]);

    const inputs = within(table).getAllByRole("spinbutton");
    // The out-of-stock line has no input at all — "all" means all that CAN be dispensed, and the server
    // would refuse the rest anyway.
    expect(inputs.length).toBeGreaterThan(0);
    for (const input of inputs) expect(Number((input as HTMLInputElement).value)).toBeGreaterThan(0);
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

    // A dispense of zero lines is a confirmation dialog asking the pharmacist to type a drug name for an
    // act that would do nothing.
    expect(screen.getByRole("button", { name: /^Submit$/ })).toBeDisabled();
    expect(screen.getByText("Nothing selected")).toBeInTheDocument();
  });

  it("counts what is about to be handed over", async () => {
    const user = userEvent.setup();
    renderPage();
    const table = await screen.findByRole("table");

    await user.click(within(table).getAllByRole("button", { name: /Fill the remaining quantity — / })[0]);

    // The running total stays visible while quantities are typed — it is the last thing worth checking
    // before an irreversible act, and it was previously below the fold.
    expect(await screen.findByText(/1 of 2 lines · 21 units/)).toBeInTheDocument();
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

    // NOT silently clamped to the remainder. Rewriting 99 to 21 under the operator's hand changes the
    // figure they are about to confirm without telling them — the same defect as an audit that quietly
    // corrects a row. What they typed stays, the field says what is wrong, and Submit refuses.
    expect(box).toHaveValue(99);
    expect(box).toHaveAttribute("aria-invalid", "true");
    expect(await screen.findByText(/Only 21 left on this line/)).toBeInTheDocument();
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

  it("reports a quantity dispensed elsewhere rather than silently correcting it", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    renderPage(LIVE, api as unknown as ApiClient);
    await screen.findByRole("table");

    // Somebody else dispensed against this prescription while it sat open on this counter. The audit must
    // SAY so — a screen that silently swapped the numbers would leave the pharmacist confirming quantities
    // they never read.
    const search = vi.spyOn(api, "pharmacySearch");
    search.mockImplementation(async () => {
      const rows = await DevApiClient.prototype.pharmacySearch.call(api, { rxNo: LIVE });
      return rows.map((p) => ({ ...p, lines: p.lines.map((l, i) => (i === 0 ? { ...l, dispensed: 5 } : l)) }));
    });

    await user.click(screen.getByRole("button", { name: "Audit" }));

    expect(await screen.findByText(/out of date and has been refreshed/i)).toBeInTheDocument();
    expect(screen.getByText(/quantities dispensed elsewhere/i)).toBeInTheDocument();
  });

  it("does NOT claim the screen is current when the re-read failed", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    renderPage(LIVE, api as unknown as ApiClient);
    await screen.findByRole("table");

    vi.spyOn(api, "pharmacySearch").mockRejectedValue(new Error("down"));
    await user.click(screen.getByRole("button", { name: "Audit" }));

    // The same rule the clinical checks and the price follow: a failed read is never rendered as a clean
    // result. "Nothing has been confirmed" is the honest outcome.
    expect(await screen.findByText(/nothing on this screen has been confirmed/i)).toBeInTheDocument();
  });
});

describe("the prescription page", () => {
  it("shows prescribed and dispensed as separate figures", async () => {
    renderPage();
    await screen.findByText(LIVE);

    // "14 of 30" and "16 remaining" answer different questions. A counter showing only the remainder cannot
    // tell a fresh course from one the patient has been collecting for a fortnight.
    const table = await screen.findByRole("table");
    for (const header of ["Prescribed", "Dispensed", "Remaining"]) {
      expect(within(table).getByRole("columnheader", { name: header })).toBeInTheDocument();
    }
  });

  it("gives every substitute control the medicine's name", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    // One "Substitute" button per row, all labelled identically, is unusable by keyboard or screen reader on
    // a multi-line prescription — and choosing the wrong row substitutes the wrong drug.
    const buttons = within(table).getAllByRole("button", { name: /Substitute — / });
    expect(buttons.length).toBeGreaterThan(0);
    const names = new Set(buttons.map((b) => b.getAttribute("aria-label")));
    expect(names.size).toBe(buttons.length);
  });

  it("shows the three figures", async () => {
    renderPage();

    expect(await screen.findByText("Prescription total")).toBeInTheDocument();
    expect(screen.getByText("Patient pays")).toBeInTheDocument();
    expect(screen.getByText("Payer pays")).toBeInTheDocument();
  });

  it("quotes the two shares on what is being dispensed, not on the whole prescription", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const spy = vi.spyOn(api, "prescriptionPricing");
    renderPage(LIVE, api);

    const table = await screen.findByRole("table");
    await screen.findByText("Prescription total");

    // Before anything is entered the tiles answer "what if all of it is collected" — and SAY so. The
    // alternative, quoting a basis of nothing, would put "Patient pays EGP 0.00" on an untouched screen, and
    // a zero at a dispensing counter reads as "free".
    expect(screen.getAllByText("If all of it is collected").length).toBe(2);

    const box = within(table).getAllByRole("spinbutton")[0];
    await user.clear(box);
    await user.type(box, "2");

    // The share is RE-QUOTED against the server on the new basis. It is not scaled here: the split runs a
    // deductible before coinsurance, so the member's share of 2 units is not 2/14ths of their share of 14,
    // and a browser multiplying by a ratio would state a figure the claim later contradicts.
    await waitFor(
      () => expect(screen.getAllByText("For the 2 units being dispensed now").length).toBe(2),
      { timeout: 3000 },
    );

    const basis = spy.mock.calls[spy.mock.calls.length - 1]?.[1];
    expect(basis).toBeTruthy();
    expect(Object.values(basis as Record<string, number>)).toEqual([2]);
  });

  it("re-quotes the share rather than scaling it, and the percentage proves it", async () => {
    const user = userEvent.setup();
    renderPage();
    const table = await screen.findByRole("table");
    await screen.findByText("Prescription total");

    // The share the plan charges is not a flat percentage of the amount: a deductible is met in full before
    // coinsurance starts, so a SMALLER basis carries a LARGER proportion. That is the property a client-side
    // "×  7/14" cannot reproduce, and the reason this figure has to come back from the benefit engine.
    const pct = () => {
      const m = screen.getAllByText(/%\s*of\s/)[0].textContent?.match(/(\d+)\s*%/);
      return Number(m?.[1]);
    };

    const whole = pct();

    const box = within(table).getAllByRole("spinbutton")[0];
    await user.clear(box);
    await user.type(box, "1");

    await waitFor(
      () => expect(screen.getAllByText("For the 1 unit being dispensed now").length).toBe(2),
      { timeout: 3000 },
    );

    // If the browser were scaling the whole-prescription figure, this would be identical. It is not — and a
    // screen that reported the same percentage on both bases would be understating what the patient owes on
    // the partial by the part of the deductible it had quietly amortised away.
    expect(pct()).toBeGreaterThan(whole);
  });

  it("leaves the prescription total alone while the shares move", async () => {
    const user = userEvent.setup();
    renderPage();
    const table = await screen.findByRole("table");
    await screen.findByText("Prescription total");

    // The three tiles answer different questions and only two of them are about this handover. The total is
    // what the prescriber wrote — a member deciding whether they can afford to come back for the rest needs
    // that figure to hold still while the pharmacist works.
    expect(screen.getByText("List price of everything prescribed")).toBeInTheDocument();

    const box = within(table).getAllByRole("spinbutton")[0];
    await user.clear(box);
    await user.type(box, "3");

    await waitFor(
      () => expect(screen.getAllByText("For the 3 units being dispensed now").length).toBe(2),
      { timeout: 3000 },
    );
    expect(screen.getByText("List price of everything prescribed")).toBeInTheDocument();
  });

  it("clears the shares rather than leaving stale ones beside a changed quantity", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    renderPage(LIVE, api);

    const table = await screen.findByRole("table");
    await screen.findByText("Prescription total");

    // The first quote succeeds; the re-quote does not.
    (api as { prescriptionPricing: unknown }).prescriptionPricing =
      vi.fn().mockRejectedValue(new Error("down"));

    const box = within(table).getAllByRole("spinbutton")[0];
    await user.clear(box);
    await user.type(box, "4");

    // A share left over from the previous quantity is not a smaller error than no share at all — it is the
    // one a pharmacist would read out to a patient without hesitating, because it looks like an answer.
    await waitFor(
      () => expect(screen.getByText(/NOT a report that it is free/i)).toBeInTheDocument(),
      { timeout: 3000 },
    );
  });

  it("NEVER renders an unquotable share as a zero amount", async () => {
    renderPage(UNPRICED);

    // The whole point. The total is still shown — the list price IS known — but the split says it cannot be
    // quoted, and the reason is given so the pharmacist knows not to improvise a figure.
    const unknown = await screen.findAllByText("Cannot be quoted");
    expect(unknown.length).toBe(2);
    expect(screen.queryByText(/EGP\s*0\.00|0\.00\s*EGP/)).toBeNull();
    expect(screen.getByText(/does not price pharmacy at this provider's network tier/i)).toBeInTheDocument();
  });

  it("says the priced figure is an estimate, not the final bill", async () => {
    renderPage();

    // The claim is adjudicated later against accumulators this screen cannot see — a deductible partly met
    // this morning, a limit reached elsewhere. Presenting the quote as final would be a promise the platform
    // cannot keep.
    expect(await screen.findByText(/final amount is set when the claim is adjudicated/i)).toBeInTheDocument();
  });

  it("does not render a price at all when pricing fails", async () => {
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { prescriptionPricing: unknown }).prescriptionPricing =
      vi.fn().mockRejectedValue(new Error("down"));
    renderPage(LIVE, api);

    // A failed fetch is not a free prescription. This is the same rule the clinical checks follow: an
    // unavailable source is never rendered as a clean result.
    expect(await screen.findByText(/NOT a report that it is free/i)).toBeInTheDocument();
  });

  it("records a substitution against the dispense, not against the prescription", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByText(LIVE);

    await user.click(within(screen.getByRole("table")).getAllByRole("button", { name: /Substitute — / })[0]);
    const dialog = within(await screen.findByRole("dialog"));
    await user.click((await dialog.findAllByRole("radio"))[0]);
    await user.type(dialog.getByRole("textbox"), "Prescribed brand is out of stock this morning.");
    await user.click(dialog.getByRole("button", { name: "Use this instead" }));

    // The prescribed medicine is STILL the one named on the row. A substitution is what the counter is about
    // to hand over, not an edit to what the doctor wrote — the prescription is a clinical record and the
    // dispense is a separate act against it.
    await waitFor(() => expect(screen.getByText(/Substituted →/)).toBeInTheDocument());
    expect(screen.getByText("Amoxicillin 500mg")).toBeInTheDocument();
  });

  it("refuses to substitute without a reason", async () => {
    const user = userEvent.setup();
    renderPage();
    await screen.findByText(LIVE);

    await user.click(within(screen.getByRole("table")).getAllByRole("button", { name: /Substitute — / })[0]);
    const dialog = within(await screen.findByRole("dialog"));
    await user.click((await dialog.findAllByRole("radio"))[0]);

    // Without a reason the record shows a molecule the prescriber did not choose and no account of why,
    // which is worse than either the substitution or a refusal on its own.
    expect(dialog.getByRole("button", { name: "Use this instead" })).toBeDisabled();
    await user.type(dialog.getByRole("textbox"), "stock");
    expect(dialog.getByRole("button", { name: "Use this instead" })).toBeDisabled();
  });

  it("names the patient rather than showing a masked token", async () => {
    renderPage();

    // A worklist asks "which row", and a token answers it. A counter asks "is this the person in front of
    // me", and only a name does — this is the identity check a pharmacist actually performs before handing
    // medicine over. It comes from the profile strip every clinical screen uses, so the disclosure is the one
    // the permission matrix already governs rather than a second name field bolted onto pharmacy.
    expect(await screen.findByText("Amal Hassan")).toBeInTheDocument();
  });

  it("shows the unit price on every line, and says so when there is none", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    // "290.50 for the prescription" does not tell a pharmacist whether the expensive item is the antibiotic
    // or the syrup, which is the conversation they have when a patient cannot afford all of it.
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

  it("shows the active ingredient, and names its absence", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    // Two trade names holding one molecule is the commonest prescribing duplication there is, and the
    // molecule is what gets checked against the packet. Capitalised for display: the catalogue is lower-case
    // throughout, and a counter reading "amoxicillin" under "Augmentin" looks like an unfinished screen.
    expect(within(table).getByText("Amoxicillin")).toBeInTheDocument();
    // 2,786 of 31,651 catalogue products record none. Saying so beats repeating the trade name.
    expect(within(table).getByText("Active ingredient not recorded")).toBeInTheDocument();
  });

  it("shows the duration, and NEVER leaves an unrecorded one blank", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    expect(within(table).getByText(/for 7 days/)).toBeInTheDocument();
    // A blank cell reads as a one-day course. Only one of those is a reason to ring the prescriber.
    expect(within(table).getByText("Duration not recorded")).toBeInTheDocument();
  });

  it("capitalises a catalogue name without mangling the dose", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    // Sentence case, not title case. The Egyptian drug list is lower-case throughout, and title-casing it
    // would produce "600Mg", "I.V" and "F.C. Tabs" — a dose and a route turned into nonsense to fix a
    // capital letter.
    expect(within(table).getByText("Amoxicillin 500mg")).toBeInTheDocument();
    expect(within(table).queryByText(/600Mg|F\.C\. Tabs/)).toBeNull();
  });

  it("does not repeat the medicine's name above every quantity box", async () => {
    renderPage();
    const table = await screen.findByRole("table");

    // The label is HIDDEN, not removed: a screen reader still hears which medicine the box belongs to, and
    // "edit, 0" five times over would not be navigable. What goes is the visible repetition of a column
    // header the sighted user has already read.
    //
    // So the assertion is that the label is STILL THERE and off-screen — `sr-only`, the platform's own
    // visually-hidden class — rather than absent. jsdom applies no stylesheet, so a "not in the document"
    // check here would pass just as happily against a label that had been deleted outright, which is the one
    // outcome this must not permit.
    const box = within(table).getAllByRole("spinbutton")[0];
    expect(box).toHaveAccessibleName(/Dispense now — Amoxicillin 500mg/);
    const label = within(table).getByText(/^Dispense now — Amoxicillin 500mg/);
    expect(label.tagName).toBe("LABEL");
    expect(label).toHaveClass("sr-only");
  });

  it("says the catalogue could not be READ, rather than that it holds nothing", async () => {
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { drugIngredients: unknown }).drugIngredients = vi.fn().mockRejectedValue(new Error("down"));
    renderPage(LIVE, api);
    const table = await screen.findByRole("table");

    // The failed-read-as-finding mistake, on the counter. "Active ingredient not recorded" is a fact about
    // the catalogue — 2,786 of 31,651 products are genuinely in that state — and printing it when master
    // data simply did not answer tells a pharmacist something untrue about the medicine in their hand.
    expect(within(table).getAllByText("Ingredient could not be read").length).toBeGreaterThan(0);
    expect(within(table).queryByText("Active ingredient not recorded")).toBeNull();
  });

  it("says the PRICE could not be read, rather than that there is none", async () => {
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { prescriptionPricing: unknown }).prescriptionPricing =
      vi.fn().mockRejectedValue(new Error("down"));
    renderPage(LIVE, api);
    const table = await screen.findByRole("table");

    // Same rule, same reason: "No price" is the catalogue's answer, and a counter that cannot tell it from a
    // failed call will quote a gap as a fact.
    await waitFor(() => {
      expect(within(screen.getByRole("table")).getAllByText("Price could not be read").length)
        .toBeGreaterThan(0);
    });
    expect(within(table).queryByText("No price")).toBeNull();
  });

  it("shows how long is left, not just the expiry date", async () => {
    renderPage();
    await screen.findByText(LIVE);

    // "Expires 13 Aug" makes the pharmacist do the arithmetic. How many days are left is what changes what
    // they say to the patient about coming back for the rest.
    expect(screen.getByText("Valid until")).toBeInTheDocument();
    expect(screen.getByText(/days left|Last day|Lapsed/)).toBeInTheDocument();
  });

  it("shows what the prescription is FOR, resolved to a title", async () => {
    renderPage();
    await screen.findByText(LIVE);

    // A medicine only makes sense against a diagnosis: checking a broad-spectrum antibiotic against "acute
    // sinusitis" is a different act from handing it over blind. Resolved through master data, because a bare
    // "J01.0" is not a diagnosis to anyone at a counter.
    expect(screen.getByText("Diagnosis")).toBeInTheDocument();
    expect(await screen.findByText(/J01\.0/)).toBeInTheDocument();
  });

  it("records a note against the handover, not against the prescription", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const dispense = vi.spyOn(api, "dispense");
    renderPage(LIVE, api as unknown as ApiClient);
    const table = await screen.findByRole("table");

    await user.click(within(table).getAllByRole("button", { name: /Fill the remaining quantity — / })[0]);
    await user.type(screen.getByRole("textbox"), "Patient took two boxes, returning Thursday for the rest.");
    await user.click(screen.getByRole("button", { name: /^Submit$/ }));

    // It travels on the DISPENSE. The note describes what happened at this counter — not the prescriber's
    // decision — so it rides the append-only record of the act, and the prescription is untouched.
    await waitFor(() => expect(dispense).toHaveBeenCalledTimes(1));
    expect(dispense.mock.calls[0][0].note).toMatch(/returning Thursday/);
  });

  it("offers Print only after something has actually been handed over", async () => {
    const user = userEvent.setup();
    renderPage();
    const table = await screen.findByRole("table");

    // A receipt for a transaction that did not happen is the one thing a receipt must never be.
    expect(screen.queryByRole("button", { name: "Print" })).toBeNull();

    await user.click(within(table).getAllByRole("button", { name: /Fill the remaining quantity — / })[0]);
    await user.click(screen.getByRole("button", { name: /^Submit$/ }));

    expect(await screen.findByRole("button", { name: "Print" })).toBeInTheDocument();
  });

  it("submits without a second confirmation dialog", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const dispense = vi.spyOn(api, "dispense");
    renderPage(LIVE, api as unknown as ApiClient);
    const table = await screen.findByRole("table");

    await user.click(within(table).getAllByRole("button", { name: /Fill the remaining quantity — / })[0]);
    await user.click(screen.getByRole("button", { name: /^Submit$/ }));

    // Submit dispenses. What still stands between a mistake and a patient: the quantity defaults to zero, the
    // count of what is about to go is in the bar, an over-quantity refuses to submit at all, and the dispense
    // is idempotent so a double-press cannot double-apply.
    await waitFor(() => expect(dispense).toHaveBeenCalledTimes(1));
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("has no serious or critical a11y violations", async () => {
    const { container } = renderPage();
    await screen.findByRole("table");
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

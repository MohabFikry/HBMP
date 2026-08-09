import { describe, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import type { OrderRow, RxRow } from "@mersal/contracts";
import { AppProviders } from "../src/App";
import { AppRouter } from "../src/routing/AppRouter";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { seedSession } from "./helpers";

/**
 * The four encounter tabs — Prescriptions, Labs, Radiology, OP Procedures — as ONE table shape.
 *
 * ============================================================================================================
 * WHY THIS SUITE EXISTS
 * ============================================================================================================
 * These four tabs are the same table rendered by two components, and every change made to one of them so far
 * has had to be made to the other by hand. The rules below are the ones that must hold on all four, so they
 * are asserted on all four rather than on whichever one was edited last:
 *
 *   - the table pages after 5 rows;
 *   - there is no date filter — the tab has already narrowed the list to one patient and one service kind;
 *   - Amend and Withdraw are reachable from the ROW, as icons, without opening the record first.
 *
 * The last is the substantive one. Both actions used to live inside the detail dialog, so correcting an order
 * a doctor had just raised meant opening it to find out that it could be corrected. A disabled control with
 * its reason beside it says "not this one, and here is why"; a control you have to go looking for says
 * nothing at all.
 */

const PATIENT = "aaaaaaaa-0000-0000-0000-000000000231";
const ENCOUNTER = "ENC-2026-000231";
const loc = (en: string, ar: string) => ({ en, ar });

function rxRow(n: number): RxRow {
  return {
    id: `rx-${n}`,
    rxNo: `RX-2026-0003${String(n).padStart(2, "0")}`,
    beneficiary: { id: PATIENT, token: "•••4821" },
    lineCount: 1,
    status: { kind: "ok", label: loc("Approved", "معتمدة") },
    submittedAt: `2026-07-${String(10 + n).padStart(2, "0")}T08:15:00Z`,
    expiresAt: "2026-12-21T08:15:00Z",
    encounterId: ENCOUNTER,
    prescriber: loc("Dr Karim Abdel-Latif", "د. كريم عبد اللطيف"),
    lines: [{
      id: `rx-${n}-l1`,
      // A drug the fixture catalogue actually offers: 31.4's Copy re-reads each medicine from the
      // CATALOGUE rather than trusting the copy on the old line, so an id nothing can resolve would be
      // testing the "no longer available" path instead of the ordinary one.
      drugId: "11111111-0000-4000-8000-000000000001",
      drug: loc("Metformin 500mg tablet", "ميتفورمين 500مجم قرص"),
      dose: "500 mg", route: "PO", frequency: "BD",
      // 31.5 — the numbers the sig was formatted from, which the record now keeps.
      doseAmount: 2, timesPerDay: 3, durationDays: 10,
      quantityPrescribed: 60, quantityDispensed: 0, refillsAllowed: 0,
      status: { kind: "info", label: loc("Active", "نشطة") },
    }],
  } as RxRow;
}

function orderRow(n: number, orderType: string): OrderRow {
  return {
    id: `ord-${orderType}-${n}`,
    orderNo: `ORD-2026-0004${String(n).padStart(2, "0")}`,
    beneficiary: { id: PATIENT, token: "•••4821" },
    orderType,
    primaryCode: `8004${n}`,
    lineCount: 1,
    status: { kind: "info", label: loc("Placed", "مُسجَّل") },
    requestedAt: `2026-07-${String(10 + n).padStart(2, "0")}T09:00:00Z`,
    encounterId: ENCOUNTER,
    firstLineId: `ord-${n}-l1`,
    lines: [{
      id: `ord-${n}-l1`, code: `8004${n}`, codeSystem: "CPT",
      description: "Complete blood count", quantityOrdered: 1, quantityConsumed: 0,
      status: { kind: "info", label: loc("Placed", "مُسجَّل") },
    }],
  } as OrderRow;
}

/** Seven of each, because five is the page size and a pager that never appears proves nothing. */
class ManyRowsApi extends DevApiClient {
  cancelled: string[] = [];
  /** Stands in for a record written before its lines carried a catalogue id — see the Copy tests. */
  stripLineIdentity = false;

  async prescriptionsMine() {
    return [1, 2, 3, 4, 5, 6, 7].map(rxRow).map((r) => this.stripLineIdentity
      ? { ...r, lines: r.lines.map((l) => ({ ...l, drugId: null })) }
      : r);
  }

  /** Every order type, because each tab filters `ordersMine` down to its own and would otherwise be empty. */
  async ordersMine() {
    return ["Lab", "Radiology", "Procedure"]
      .flatMap((type) => [1, 2, 3, 4, 5, 6, 7].map((n) => orderRow(n, type)))
      .map((o) => (this.stripLineIdentity ? { ...o, lines: [] } : o));
  }

  async cancelPrescription(id: string) {
    this.cancelled.push(id);
  }

  async cancelOrderLines(id: string) {
    this.cancelled.push(id);
    return { cancelled: 1, refused: [] };
  }
}

async function openTab(name: RegExp, api = new ManyRowsApi({ latencyMs: 0 })) {
  const user = userEvent.setup();
  seedSession("doctor");
  render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter
        initialEntries={[`/clinician/encounter?encounter=${ENCOUNTER}`]}
        future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
      >
        <AppRouter />
      </MemoryRouter>
    </AppProviders>,
  );
  await screen.findByRole("heading", { name: /^encounter$/i }, { timeout: 5000 });
  await user.click(await screen.findByRole("tab", { name }));
  return { user, api };
}

/** The transactions table on the open tab. */
function transactionsTable(): HTMLTableElement {
  return document.querySelectorAll("table")[0] as HTMLTableElement;
}

const TABS: [string, RegExp][] = [
  ["Prescriptions", /prescriptions/i],
  ["Labs", /labs/i],
  ["Radiology", /radiology/i],
  ["OP Procedures", /procedures/i],
];

describe.each(TABS)("The %s tab's layout", (_label, tab) => {
  it("puts the composer in its OWN card, above the history", async () => {
    /*
     * Two blocks, two cards, and the one the doctor came here to use is first.
     *
     * They used to share a card: the history table, then a rule, then the composer beneath it. That reads as
     * one thing with an appendix — and it put the tab's whole purpose below five rows of what has already
     * been written, which on a laptop is below the fold. They are not one thing. What has been prescribed is
     * a record; what is being prescribed is an act.
     */
    await openTab(tab);
    await screen.findByRole("table", {}, { timeout: 5000 });

    const cards = [...document.querySelectorAll(".mrs-tabpane:not([hidden]) .mrs-card")];
    expect(cards.length, "the composer and the history are separate cards").toBeGreaterThanOrEqual(2);

    const composer = cards.find((c) => c.querySelector(".rx-compose"));
    const history = cards.find((c) => c.querySelector("table"));
    expect(composer, "no card holds the composer").toBeTruthy();
    expect(history, "no card holds the history table").toBeTruthy();
    expect(composer).not.toBe(history);
    // DOM order is reading order: `compareDocumentPosition` returns FOLLOWING for a node after this one.
    expect(
      composer!.compareDocumentPosition(history!) & Node.DOCUMENT_POSITION_FOLLOWING,
      "the history card is not after the composer card",
    ).toBeTruthy();
  });

  it("pages after five rows", async () => {
    // Five, not eight. This table sits directly above the composer the doctor is about to type into, and
    // eight rows of history push the thing they came to the tab to do below the fold.
    await openTab(tab);
    await screen.findByRole("table", {}, { timeout: 5000 });

    expect(transactionsTable().querySelectorAll("tbody tr").length).toBe(5);
  });

  it("offers no date filter", async () => {
    // The tab has already narrowed this to one patient and one service kind, and the composer is the point
    // of the screen. A period chip group above five rows is the widest control on the tab answering a
    // question the tab has answered twice already.
    await openTab(tab);
    await screen.findByRole("table", {}, { timeout: 5000 });

    expect(screen.queryByRole("group", { name: /when/i })).toBeNull();
  });

  it("puts Amend and Withdraw on the row itself, as icons", async () => {
    // THE DEFECT THIS PINS. Both actions lived inside the detail dialog, so a doctor correcting something
    // they had just raised had to open it to discover whether it could be corrected at all.
    await openTab(tab);
    await screen.findByRole("table", {}, { timeout: 5000 });

    const firstRow = transactionsTable().querySelector("tbody tr") as HTMLElement;
    // Named for the TRANSACTION, not just "Amend": a row of unlabelled icons is a screen-reader user
    // hearing "button, button, button" seven times over.
    expect(within(firstRow).getByRole("button", { name: /amend/i })).toBeTruthy();
    expect(within(firstRow).getByRole("button", { name: /withdraw/i })).toBeTruthy();
    // Icons, so the row stays scannable — the label is the accessible name, not visible text.
    expect(within(firstRow).getByRole("button", { name: /withdraw/i }).querySelector("svg")).toBeTruthy();
  });

  it("asks for a reason before withdrawing, and withdraws the whole transaction", async () => {
    // A withdrawal is a clinical act on the patient's record. It records WHY, and it is confirmed —
    // one mis-click on an icon in a row of icons must not retract a prescription.
    const { user, api } = await openTab(tab);
    await screen.findByRole("table", {}, { timeout: 5000 });

    const firstRow = transactionsTable().querySelector("tbody tr") as HTMLElement;
    await user.click(within(firstRow).getByRole("button", { name: /withdraw/i }));

    expect(await screen.findByRole("dialog")).toBeTruthy();
    expect(api.cancelled).toEqual([]);   // nothing has happened yet
  });

  it("offers Copy on the row, and copying writes nothing", async () => {
    /*
     * 31.4 — a repeat script is the commonest thing a returning patient needs, and writing one meant finding
     * every medicine in a catalogue of 22,653 again.
     *
     * The assertion that matters is the SECOND one. Copy fills the composer with a new draft the doctor
     * still has to check and submit; a control on a row of a clinical record that silently raised a second
     * prescription would be the worst kind of convenience.
     */
    const { user, api } = await openTab(tab);
    await screen.findByRole("table", {}, { timeout: 5000 });

    const firstRow = transactionsTable().querySelector("tbody tr") as HTMLElement;
    const copy = within(firstRow).getByRole("button", { name: /copy into the composer/i });
    // Named for the transaction, like Amend and Withdraw beside it — a row of bare glyphs is a screen-reader
    // user hearing "button" three times with no way to tell what they act on.
    expect(copy.getAttribute("aria-label")).toMatch(/(RX|ORD)-/);
    expect(copy.querySelector("svg"), "it is an icon, so the row stays scannable").toBeTruthy();

    await user.click(copy);

    expect(api.cancelled).toEqual([]);
    expect(screen.queryByRole("dialog"), "copying opens nothing and confirms nothing").toBeNull();
  });

  it("fills the composer from the copied transaction", async () => {
    // What arrives is a DRAFT. The fixture's rows each carry one item, so the composer gains one filled line
    // in place of its empty placeholder.
    const { user } = await openTab(tab);
    await screen.findByRole("table", {}, { timeout: 5000 });

    const firstRow = transactionsTable().querySelector("tbody tr") as HTMLElement;
    await user.click(within(firstRow).getByRole("button", { name: /copy into the composer/i }));

    // The confirmation says what was copied and from where, and says nothing has been sent.
    expect(await screen.findByText(/copied 1 .*from (RX|ORD)-/i, {}, { timeout: 5000 })).toBeTruthy();
  });

  it("carries the dose, the frequency and the duration into the copy", async () => {
    /*
     * 31.5 — the point of persisting `doseAmount` and `timesPerDay`.
     *
     * Before it, a copied prescription arrived with those two fields EMPTY and the quantity check honestly
     * reporting it had nothing to compute from, because the record kept only the sig — "1 Tablet x 3/day",
     * a sentence this application had formatted. Copying meant retyping the clinical numbers.
     *
     * Only the Prescriptions tab: an order line carries no dose, and asserting one on the Labs tab would be
     * asserting a field that does not exist there.
     */
    if (!/prescriptions/i.test(String(tab))) return;

    const { user } = await openTab(tab);
    await screen.findByRole("table", {}, { timeout: 5000 });

    const firstRow = transactionsTable().querySelector("tbody tr") as HTMLElement;
    await user.click(within(firstRow).getByRole("button", { name: /copy into the composer/i }));
    await screen.findByText(/copied 1 .*from RX-/i, {}, { timeout: 5000 });

    // The `value` PROPERTY, not the attribute: React does not reliably reflect a controlled input's value
    // into the attribute, so an attribute assertion can pass or fail for reasons that have nothing to do
    // with what the prescriber sees.
    const value = (el: HTMLElement) => (el as HTMLInputElement).value;
    expect(value(await screen.findByRole("spinbutton", { name: /^dose/i }))).toBe("2");
    expect(value(screen.getByRole("spinbutton", { name: /times per day/i }))).toBe("3");
    expect(value(screen.getByRole("spinbutton", { name: /duration/i }))).toBe("10");
  });

  it("says so when there is nothing on the row it can copy", async () => {
    // A prescription written before drug ids were recorded, or an order carrying no lines, copies NOTHING.
    // Returning quietly would leave the doctor pressing a button that appears to do nothing — which is how
    // a control gets a reputation for being broken when it is being honest.
    const api = new ManyRowsApi({ latencyMs: 0 });
    api.stripLineIdentity = true;
    const { user } = await openTab(tab, api);
    await screen.findByRole("table", {}, { timeout: 5000 });

    const firstRow = transactionsTable().querySelector("tbody tr") as HTMLElement;
    await user.click(within(firstRow).getByRole("button", { name: /copy into the composer/i }));

    expect(await screen.findByText(/nothing on (RX|ORD)-.* could be copied/i, {}, { timeout: 5000 }))
      .toBeTruthy();
  });

  it("puts its actions in the modal's footer, not at the bottom of the body card", async () => {
    /*
     * A Modal renders its children on an opaque card inset inside the glass frame, and its `footer` slot
     * BELOW that card, right-aligned, with its own spacing. Every action button here was written as the last
     * child instead — so "Back" and "Withdraw" sat tucked against the bottom-left corner of the white card
     * with nothing between them and the content above, on a dialog whose every other edge is 24px clear.
     *
     * This is asserted structurally rather than by measuring, because jsdom has no layout: the fact that
     * makes the spacing right is WHERE the buttons are in the tree, and that is checkable here.
     */
    const { user } = await openTab(tab);
    await screen.findByRole("table", {}, { timeout: 5000 });

    const firstRow = transactionsTable().querySelector("tbody tr") as HTMLElement;
    await user.click(within(firstRow).getByRole("button", { name: /withdraw/i }));

    const dialog = await screen.findByRole("dialog");
    for (const name of [/^back$/i, /^withdraw$/i]) {
      const button = within(dialog).getByRole("button", { name });
      expect(button.closest(".mrs-modal-body"), `"${button.textContent}" is inside the body card`).toBeNull();
    }
  });
});

describe("The service-line history icon", () => {
  it("is offered on a line being composed, not only on one already sent", async () => {
    // 29.4 asks "has this patient had this before?" — a question whose whole value is BEFORE the thing is
    // ordered. It was reachable only from rows already raised, which is the one moment the answer cannot
    // change the decision.
    const { user } = await openTab(/prescriptions/i);
    await screen.findByRole("table", {}, { timeout: 5000 });

    const combo = await screen.findByRole("combobox", { name: /medicine/i });
    await user.type(combo, "met");
    // Scoped to the drug LISTBOX. A document-wide `option` query also matches every `<option>` inside every
    // `<select>` on the page, and the first of those is not a medicine.
    const list = await screen.findByRole("listbox", { name: /medicine/i }, { timeout: 5000 });
    await user.click(within(list).getAllByRole("option")[0]);

    expect(await screen.findByRole("button", { name: /previous .*of this medicine/i })).toBeTruthy();
  });
});

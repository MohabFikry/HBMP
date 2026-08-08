import { describe, expect, it } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import type { OrderRow, RxRow } from "@mersal/contracts";
import { AppProviders } from "../src/App";
import { AppRouter } from "../src/routing/AppRouter";
import { DevAuthClient } from "../src/auth/authClient";
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
      drugId: "22222222-2222-2222-2222-222222222222",
      drug: loc("Metformin 500mg tablet", "ميتفورمين 500مجم قرص"),
      dose: "500 mg", route: "PO", frequency: "BD",
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

  async prescriptionsMine() {
    return [1, 2, 3, 4, 5, 6, 7].map(rxRow);
  }

  /** Every order type, because each tab filters `ordersMine` down to its own and would otherwise be empty. */
  async ordersMine() {
    return ["Lab", "Radiology", "Procedure"]
      .flatMap((type) => [1, 2, 3, 4, 5, 6, 7].map((n) => orderRow(n, type)));
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

/** The transactions table on the open tab — the first one, which sits above the composer. */
function transactionsTable(): HTMLTableElement {
  return document.querySelectorAll("table")[0] as HTMLTableElement;
}

const TABS: [string, RegExp][] = [
  ["Prescriptions", /prescriptions/i],
  ["Labs", /labs/i],
  ["Radiology", /radiology/i],
  ["OP Procedures", /procedures/i],
];

describe.each(TABS)("The %s tab's transactions table", (_label, tab) => {
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

import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode } from "./helpers";
import { ClaimsWorklist } from "../src/screens/ClaimsPortal";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ClaimRow } from "@mersal/contracts";

/**
 * "Which is the oldest?" is the question a worklist is opened to answer.
 *
 * <b>The defect.</b> 65 tables across 28 screens had not one sortable column — the approvals worklist, the
 * claims worklist, the provider directory, the inventory movement ledger. Sorting a bare `DataTable` costs
 * only `sortable: true` plus `sortValue`: the component sorts itself, and it shares its comparator with
 * `useTableQuery` so turning paging on later cannot change the order.
 *
 * <b>The invariant that matters more than the count.</b> `sortable` means two different things depending on
 * the table:
 *
 * <ul>
 *   <li><b>Uncontrolled</b> (no `onSort`): the table sorts itself from `column.sortValue`. A sortable column
 *       WITHOUT a `sortValue` is a header that says "you can order by this" and then does nothing when
 *       pressed — `DataTable.sortedRows` returns the rows untouched.</li>
 *   <li><b>Controlled</b> (`onSort` supplied): the SERVER sorts, the column key IS the server's sort field,
 *       and `sortValue` is meaningless. `PolicyList` and `MemberAdmin` are these.</li>
 * </ul>
 *
 * Both are correct and they look almost identical in a diff, which is why the rule is asserted rather than
 * remembered.
 */

const SRC = resolve(__dirname, "../src");

function tsxFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) tsxFiles(p, out);
    else if (p.endsWith(".tsx")) out.push(p);
  }
  return out;
}

const readCode = (f: string) =>
  readFileSync(f, "utf8").replace(/\/\*[\s\S]*?\*\/|\/\/[^\n]*/g, (c) => c.replace(/[^\n]/g, " "));

function columnLiterals(src: string): Array<{ index: number; text: string }> {
  const out: Array<{ index: number; text: string }> = [];
  for (let i = 0; i < src.length; i++) {
    if (src[i] !== "{" || !/^\{\s*key:\s*"/.test(src.slice(i, i + 40))) continue;
    let depth = 0, j = i;
    for (; j < src.length; j++) {
      if (src[j] === "{") depth++;
      else if (src[j] === "}") { depth--; if (!depth) break; }
    }
    out.push({ index: i, text: src.slice(i, j + 1) });
    i = j;
  }
  return out;
}

describe("a sortable header actually sorts", () => {
  it("marks a large number of columns sortable — otherwise the assertions below are about nothing", () => {
    let sortable = 0;
    for (const f of tsxFiles(SRC)) sortable += (readCode(f).match(/sortable:\s*true/g) ?? []).length;
    // 125 before this pass, ~300 after. The floor is deliberately below both so it pins "many", not a total
    // that every future column would have to be added to.
    expect(sortable).toBeGreaterThan(250);
  });

  it("gives every sortable column in a SELF-sorting table something to sort by", () => {
    const offenders: string[] = [];
    for (const file of tsxFiles(SRC)) {
      const src = readCode(file);
      // Controlled tables are the server's; there the column key is the sort field and sortValue is unused.
      if (/onSort=/.test(src)) continue;
      for (const col of columnLiterals(src)) {
        if (!/sortable:\s*true/.test(col.text)) continue;
        if (/sortValue:/.test(col.text)) continue;
        offenders.push(
          `${file.slice(SRC.length + 1)}:${src.slice(0, col.index).split("\n").length} ` +
          `key="${/key:\s*"([^"]+)"/.exec(col.text)?.[1]}"`);
      }
    }
    expect(
      offenders,
      "this table sorts ITSELF, so a sortable column needs `sortValue` — without one the header is a button " +
        "that reorders nothing. (If the table is server-sorted, it should be passing `onSort`.)",
    ).toEqual([]);
  });

  it("reorders the rows when the header is pressed", async () => {
    // The static check above cannot see whether the wiring works. This one presses a real header on a real
    // screen and reads the order back out of the DOM.
    const user = userEvent.setup();
    const rows: ClaimRow[] = [
      { claimNo: "CLM-3", origin: "Provider", status: { kind: "neu", label: { en: "Received", ar: "" } },
        claimedAmount: 300, netPayable: 300, serviceDateFrom: "2026-03-01", submittedAt: "2026-03-02" },
      { claimNo: "CLM-1", origin: "Provider", status: { kind: "neu", label: { en: "Received", ar: "" } },
        claimedAmount: 100, netPayable: 100, serviceDateFrom: "2026-01-01", submittedAt: "2026-01-02" },
      { claimNo: "CLM-2", origin: "Provider", status: { kind: "neu", label: { en: "Received", ar: "" } },
        claimedAmount: 200, netPayable: 200, serviceDateFrom: "2026-02-01", submittedAt: "2026-02-02" },
    ] as ClaimRow[];

    class Api extends DevApiClient {
      override claimsWorklist() { return Promise.resolve(rows); }
    }
    renderNode(<ClaimsWorklist />, new Api());

    const claimNos = () =>
      within(screen.getByRole("table"))
        .getAllByText(/^CLM-\d$/)
        .map((el) => el.textContent);

    expect(await screen.findByText("CLM-3")).toBeInTheDocument();
    // The fixture order, untouched.
    expect(claimNos()).toEqual(["CLM-3", "CLM-1", "CLM-2"]);

    await user.click(within(screen.getByRole("table")).getByRole("button", { name: "Claim" }));
    expect(claimNos()).toEqual(["CLM-1", "CLM-2", "CLM-3"]);

    // A second press reverses it; a fresh column would start ascending again.
    await user.click(within(screen.getByRole("table")).getByRole("button", { name: "Claim" }));
    expect(claimNos()).toEqual(["CLM-3", "CLM-2", "CLM-1"]);
  });

  it("sorts dates chronologically, not by the text they are rendered as", async () => {
    // The reason `sortValue` reads the RAW field rather than the cell: "1 Feb 2026" and "1 Mar 2026" order
    // M-before-F as strings, and a queue sorted that way looks sorted and is not.
    const user = userEvent.setup();
    const rows = [
      { claimNo: "CLM-A", origin: "P", status: { kind: "neu", label: { en: "Received", ar: "" } },
        claimedAmount: 1, netPayable: 1, serviceDateFrom: "2026-03-01", submittedAt: "2026-03-01" },
      { claimNo: "CLM-B", origin: "P", status: { kind: "neu", label: { en: "Received", ar: "" } },
        claimedAmount: 1, netPayable: 1, serviceDateFrom: "2026-02-01", submittedAt: "2026-02-01" },
    ] as ClaimRow[];
    class Api extends DevApiClient {
      override claimsWorklist() { return Promise.resolve(rows); }
    }
    renderNode(<ClaimsWorklist />, new Api());
    await screen.findByText("CLM-A");

    await user.click(within(screen.getByRole("table")).getByRole("button", { name: "Service date" }));
    const order = within(screen.getByRole("table")).getAllByText(/^CLM-[AB]$/).map((e) => e.textContent);
    // February first. Sorting the rendered strings would put March first.
    expect(order).toEqual(["CLM-B", "CLM-A"]);
  });
});

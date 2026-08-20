import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import { NetworkDirectory } from "../src/screens/NetworkPortal";
import type { ProviderSummary } from "@mersal/contracts";

/**
 * A queue an operator works through needs a way to find a row in it.
 *
 * <b>The defect.</b> Nineteen unbounded operational lists rendered whole, with no search, no filter and no
 * pager — the approvals worklist, the claims worklist, the provider directory ("the tenant's whole network",
 * says its own comment), the identity account store, an append-only stock ledger. Finding a row meant
 * scrolling past every other one.
 *
 * `DataTableView` has existed the whole time and composes the three correctly; its doc comment predicted
 * exactly this ("a house standard that lives in a document is one every screen implements slightly
 * differently"). The migration is wiring, not new capability.
 *
 * <b>What is asserted here.</b> The provider directory stands in for the class: it is the one whose whole
 * point is finding one provider among the network's, and it exercises every part — a search over a field
 * that is NOT the one an operator would think of first, a filter group derived from the rows rather than
 * declared, and a pager. The static check below then holds the line for the rest.
 */

function provider(n: number, over: Partial<ProviderSummary> = {}): ProviderSummary {
  return {
    id: `p-${n}`,
    code: `PRV-${String(n).padStart(3, "0")}`,
    legalName: `Provider ${n}`,
    providerType: "Clinic",
    status: { kind: "ok", label: { en: "Active", ar: "نشط" } },
    onboardingState: "Credentialed",
    ...over,
  } as ProviderSummary;
}

function withProviders(rows: ProviderSummary[]) {
  class Api extends DevApiClient {
    override providerList() { return Promise.resolve(rows); }
  }
  return new Api();
}

const table = () => within(screen.getByRole("table"));

describe("the provider directory can be searched, filtered and paged", () => {
  it("searches on the provider CODE, not only the name", async () => {
    // The code is what a claim or a contract cites, so it has to be reachable even though the name is what
    // an operator would think of first. A search that only matched the name would fail on the one
    // identifier they actually have in hand.
    const user = userEvent.setup();
    renderNode(<NetworkDirectory />, withProviders([
      provider(1, { legalName: "Nile Clinic", code: "PRV-777" }),
      provider(2, { legalName: "Delta Hospital", code: "PRV-888" }),
    ]));
    expect(await screen.findByText("Nile Clinic")).toBeInTheDocument();

    await user.type(screen.getByRole("searchbox"), "888");

    expect(table().queryByText("Nile Clinic")).not.toBeInTheDocument();
    expect(table().getByText("Delta Hospital")).toBeInTheDocument();
  });

  it("says the rows were FILTERED OUT rather than that there are none", async () => {
    // An empty queue is good news; an empty queue because you typed something needs the search cleared.
    // Telling that operator "no providers" is a lie that sends them looking for a bug.
    const user = userEvent.setup();
    renderNode(<NetworkDirectory />, withProviders([provider(1)]));
    await screen.findByText("Provider 1");

    await user.type(screen.getByRole("searchbox"), "zzzzz");

    expect(await screen.findByText(/change the search or clear the filters/i)).toBeInTheDocument();
  });

  it("builds its type filter from the rows, and offers none when they are all alike", async () => {
    const user = userEvent.setup();
    renderNode(<NetworkDirectory />, withProviders([
      provider(1, { providerType: "Clinic" }),
      provider(2, { providerType: "Pharmacy" }),
    ]));
    await screen.findByText("Provider 1");

    // A hardcoded vocabulary would show a chip for Imaging in a network with no imaging centre.
    await user.click(screen.getByRole("button", { name: /Pharmacy/ }));
    expect(table().queryByText("Provider 1")).not.toBeInTheDocument();
    expect(table().getByText("Provider 2")).toBeInTheDocument();
  });

  it("offers no filter group at all when every row shares the value", async () => {
    renderNode(<NetworkDirectory />, withProviders([provider(1), provider(2)]));
    await screen.findByText("Provider 1");
    // Both are Clinic/Credentialed. A group whose every chip returns everything is a control that cannot do
    // anything, and on a one-branch network that is most of the toolbar.
    expect(screen.queryByRole("button", { name: /^Clinic$/ })).not.toBeInTheDocument();
  });

  it("pages a directory bigger than one screen", async () => {
    const user = userEvent.setup();
    // Zero-padded, because the directory opens sorted by NAME and names sort as strings: unpadded,
    // "Provider 30" lands before "Provider 4" and the last row by number is not the last row on screen.
    const name = (n: number) => `Provider ${String(n).padStart(2, "0")}`;
    renderNode(<NetworkDirectory />, withProviders(
      Array.from({ length: 30 }, (_, i) => provider(i + 1, { legalName: name(i + 1) }))));
    await screen.findByText(name(1));

    // 25 to a page, so the thirtieth is on page 2 — before this it was simply the thirtieth row of an
    // unbroken list.
    expect(table().queryByText(name(30))).not.toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: /next/i }));
    expect(table().getByText(name(30))).toBeInTheDocument();
  });
});

// ── Holding the line ─────────────────────────────────────────────────────────────────────────────────────

const SRC = resolve(__dirname, "../src");

function tsxFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) tsxFiles(p, out);
    else if (p.endsWith(".tsx")) out.push(p);
  }
  return out;
}

/**
 * Screens whose tables are deliberately NOT `DataTableView`, each with the reason.
 *
 * An allow-list rather than a count, so a new unbounded queue has to argue its case in this file rather than
 * defaulting in. Removing a name from here is a claim that the screen now needs the standard.
 */
const BARE_TABLE_OK: Record<string, string> = {
  "screens/MemberAdmin.tsx": "server-side search, sort and paging — the book is too big for the browser",
  "screens/PolicyBook.tsx": "server-side sort and paging on the policy list; the rest are bounded config",
  "screens/BranchInventory.tsx": "the ledger is server-paged; the stock table above it IS a DataTableView",
  // 28.10 — AdminConsole.tsx is GONE from this list. Its four remaining screens all render `DataTableView`
  // now. Master data was the case that made the old entry indefensible: the server returns up to five
  // hundred in-force versions and the screen drew every one of them as an unbroken, unsearchable list, which
  // is the same as not having the screen. "Bounded config table" was true of the tenant list and was being
  // used to cover a table two orders of magnitude larger.
  "screens/AccessAdmin.tsx": "roles, exceptions and sessions belong to ONE membership",
  // 28.10 — the permission catalogue and the assignment register moved to `DataTableView`: the first is a
  // few hundred rows an administrator searches by domain, the second grows with every grant the tenant has
  // ever made. What is left here is the role list (the tenant's own roles) and the separated-duty matrix,
  // both genuinely bounded governance tables.
  "screens/AccessCatalogue.tsx": "the role list and the separated-duty matrix are bounded governance tables",
  "screens/ApprovalsRegister.tsx": "the register migrated; the items table lists ONE authorization's deliveries",
  "screens/CaseManager.tsx": "cases and escalations migrated; the task list belongs to ONE case",
  "screens/FinancePortal.tsx": "utilization and settlements migrated; the line table belongs to ONE settlement",
  "screens/BranchLicences.tsx": "the roster migrated; the alert and reassignment lists are derived summaries",
  "screens/ProcedureCentre.tsx": "the queue migrated; the counter table lists ONE verified order",
  "screens/NetworkTierAdmin.tsx": "assignments migrated; the tier list is bounded configuration",
  "screens/NetworkPortal.tsx": "the directory IS a DataTableView; contracts and locations are per-provider",
  "screens/ApprovalEngineAdmin.tsx": "a rule set, bounded by design",
  "screens/ApprovalsExtra.tsx": "an SLA board (one row per status) and an override register",
  "screens/BatchIntake.tsx": "server-capped error and preview lists — a pager is the wrong control",
  "screens/PolicyBulk.tsx": "server-capped error and preview lists — a pager is the wrong control",
  "screens/BranchRoster.tsx": "one branch's exceptions",
  "screens/BranchesOverview.tsx": "one row per branch",
  "screens/ClaimsPortal.tsx": "worklist and reconciliation migrated; the denial list is a top-N",
  // 2026-08-11 — both genuinely bounded, and bounded on the SERVER rather than by hoping. Utilization is
  // `top: 25` in ReportQueries; Claims & Cost is three outcome rows, a handful of service lines and a
  // top-ten denial list. A pager over twenty-five ranked rows hides the tail that makes a ranking useful.
  // The SLA-breach list on ReportView.tsx is NOT here: it can reach a hundred rows and is a worklist, so it
  // is a DataTableView.
  "screens/director/ServiceUse.tsx": "a server-capped top-25 ranking on one axis",
  "screens/director/ClaimsCost.tsx": "outcome, cost and denial summaries — all server-capped top-N",
  "screens/LabQueue.tsx": "the result of an explicit search",
  "screens/MasterListAdmin.tsx": "an in-force code list",
  "screens/PharmacyDispense.tsx": "the result of an explicit search",
  "screens/PolicyAnalytics.tsx": "a drill-down from a chart band",
  "screens/PolicyProductAdmin.tsx": "payers and plan versions — bounded configuration",
  "screens/ProfileSectionViews.tsx": "one patient's record, sectioned",
  "screens/ProgramAdmin.tsx": "programmes and capacity — bounded configuration",
  "screens/ReportView.tsx": "a rendered report's own tables",
  "screens/ServiceHistoryModal.tsx": "previous occurrences of one service for one member",
  "screens/Substitutions.tsx": "a drug search and its alternatives",
};

describe("bare DataTable is an exception with a reason", () => {
  it("has no screen using a bare table without an entry saying why", () => {
    const undeclared: string[] = [];
    for (const file of tsxFiles(SRC)) {
      const src = readFileSync(file, "utf8");
      if (!/<DataTable(?![A-Za-z])/.test(src)) continue;
      const rel = file.slice(SRC.length + 1);
      if (!(rel in BARE_TABLE_OK)) undeclared.push(rel);
    }
    expect(
      undeclared,
      "a new screen is rendering a bare `DataTable`. If it is an operational queue it wants " +
        "`DataTableView` — toolbar, table and pager, wired correctly. If it is genuinely bounded, add it to " +
        "BARE_TABLE_OK in this file with the reason.",
    ).toEqual([]);
  });

  it("has no stale entry — a screen that stopped using bare tables should leave the list", () => {
    const stale = Object.keys(BARE_TABLE_OK).filter((rel) => {
      try {
        return !/<DataTable(?![A-Za-z])/.test(readFileSync(join(SRC, rel), "utf8"));
      } catch {
        return true; // the file is gone
      }
    });
    expect(stale, "these no longer use a bare DataTable; remove them from BARE_TABLE_OK").toEqual([]);
  });
});

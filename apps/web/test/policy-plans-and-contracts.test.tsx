import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { ApiError } from "../src/api/http";
import type {
  PlanAdminView, PlanDetail, PolicyAdminView, PolicyApi, PolicyDetail, PolicyQueryRow,
} from "../src/api/policyApi";
import { PolicyPlans } from "../src/screens/PolicyProductAdmin";
import { PolicyList } from "../src/screens/PolicyBook";
import { seedSession } from "./helpers";

/**
 * Phase 19.8 — the plan and the contract, given the treatment the payer got in 19.7.
 *
 * The two refusals are the point of these, and they are deliberately DIFFERENT from each other:
 *
 *  · withdrawing a plan an active policy still sells is refused, with the count — it is a catalogue action,
 *    and pulling a product members are being enrolled onto would strand those enrolments;
 *  · suspending a contract with live members is NOT refused — it IS the operation — so the count is shown
 *    in the confirmation as impact rather than returned as a barrier.
 *
 * Everything else here holds one claim a later edit could quietly break: the code fields stay read-only, the
 * reason gate holds, an irreversible move says so, and withheld terms read as restricted.
 */

// ── fakes ───────────────────────────────────────────────────────────────────────────────────────────────

const planView = (over: Partial<PlanAdminView> = {}): PlanAdminView => ({
  planId: "p1",
  planCode: "PLAN-GOLD",
  nameEn: "Gold",
  nameAr: "ذهبي",
  description: "The full benefit package.",
  category: "Premium",
  status: "Active",
  statusReason: null,
  statusChangedAt: null,
  updatedAt: "2026-08-01T09:00:00Z",
  updatedByName: "Policy Admin",
  ...over,
});

const planDetail = (p: PlanAdminView): PlanDetail => ({
  plan: p,
  book: {
    versionCount: 3, draftCount: 1, activeCount: 1, supersededCount: 1,
    policyCount: 4, activePolicyCount: 3, memberCount: 812, activeMemberCount: 790,
    firstEffectiveFrom: "2025-01-01", lastEffectiveTo: null,
  },
});

const policyRow = (over: Partial<PolicyQueryRow> = {}): PolicyQueryRow => ({
  policyId: "pol1",
  policyNo: "POL-2026-0001",
  status: "Active",
  effectiveFrom: "2026-01-01",
  effectiveTo: null,
  memberCount: 120,
  memberCountBand: "Medium",
  planCount: 2,
  payerId: "pay1",
  maxMembers: 500,
  totalLimit: null,
  totalConsumed: null,
  percentUsed: 40,
  utilizationBand: "Normal",
  ...over,
} as PolicyQueryRow);

const policyView = (over: Partial<PolicyAdminView> = {}): PolicyAdminView => ({
  policyId: "pol1",
  policyNo: "POL-2026-0001",
  status: "Active",
  statusReason: null,
  statusChangedAt: null,
  effectiveFrom: "2026-01-01",
  effectiveTo: "2026-12-31",
  windowState: "InForce",
  terms: { payerId: "pay1", maxMembers: 500, previousPolicyId: null, notes: null },
  updatedAt: "2026-08-01T09:00:00Z",
  updatedByName: "Member Admin",
  ...over,
});

const policyDetail = (p: PolicyAdminView, over: Partial<PolicyDetail["book"]> = {}): PolicyDetail => ({
  policy: p,
  book: {
    memberCount: 120, activeMemberCount: 118, planCount: 2,
    committedLimit: 1_200_000, consumedValue: 480_000, percentOfCap: 23.6,
    ...over,
  },
});

function fakeApi(overrides: Partial<PolicyApi> = {}): PolicyApi {
  const reject = () => Promise.reject(new ApiError("network", "not stubbed in this test"));
  return {
    payers: () => Promise.resolve([]),
    plans: () => Promise.resolve([{
      planId: "p1", planCode: "PLAN-GOLD", nameEn: "Gold", nameAr: "ذهبي",
      description: "The full benefit package.", category: "Premium", status: "Active",
    }]),
    plan: () => Promise.resolve(planDetail(planView())),
    planVersions: () => Promise.resolve([]),
    benefitCategories: () => Promise.resolve([]),
    networkTiers: () => Promise.resolve([]),
    createPlan: reject,
    updatePlan: reject,
    deactivatePlan: reject,
    reactivatePlan: reject,
    planHistory: () => Promise.resolve({ planId: "p1", entries: [] }),
    policyQuery: () => Promise.resolve({
      items: [policyRow()], page: 1, pageSize: 25, totalCount: 1, totalPages: 1, sortedBy: "policyno",
      payerScopeApplied: false, identityMatchTruncated: false, unavailable: [],
    }),
    policy: () => Promise.resolve(policyDetail(policyView())),
    createPolicy: reject,
    updatePolicy: reject,
    changePolicyStatus: reject,
    policyHistory: () => Promise.resolve({ policyId: "pol1", entries: [] }),
    // Selecting a policy force-mounts its tabs; the first one reads the plans under it.
    policyPlans: () => Promise.resolve([]),
    policyGroups: () => Promise.resolve([]),
    notes: () => Promise.resolve([]),
    documents: () => Promise.resolve([]),
    timeline: () => Promise.resolve({ entries: [], nextCursor: null }),
    ...overrides,
  } as unknown as PolicyApi;
}

function renderScreen(ui: React.ReactElement, role = "policy_admin") {
  seedSession(role as Parameters<typeof seedSession>[0]);
  return render(
    <AppProviders authClient={new DevAuthClient()}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>{ui}</MemoryRouter>
    </AppProviders>,
  );
}

/** The house table renders a grid, so its cells are `gridcell`. Clicking the row's own text is what an
 *  operator does and what the payer suite already does. */
const selectRow = async (label: string) => {
  const row = await screen.findByRole("row", { name: new RegExp(label) });
  await userEvent.click(within(row).getByText(label));
};

/** A modal has a Cancel in its footer AND a close control the design system labels the same way. */
const dialogButton = async (name: string) =>
  within(await screen.findByRole("dialog")).getAllByRole("button", { name }).at(-1)!;

afterEach(() => { cleanup(); localStorage.clear(); });

// ════════════════════════════════════════════════════════════════════════════════════════════════════════
// PLANS
// ════════════════════════════════════════════════════════════════════════════════════════════════════════

describe("the plan record", () => {
  it("offers New, Edit, Withdraw and History to a product administrator", async () => {
    renderScreen(<PolicyPlans api={fakeApi()} />);
    expect(await screen.findByRole("button", { name: "New plan" })).toBeInTheDocument();

    await selectRow("Gold");
    expect(await screen.findByRole("button", { name: "Edit this plan" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Withdraw this plan" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Change history" })).toBeInTheDocument();
  });

  it("offers a claims officer no write at all, and still the history", async () => {
    renderScreen(<PolicyPlans api={fakeApi()} />, "claims_officer");
    await screen.findByText("Gold");
    expect(screen.queryByRole("button", { name: "New plan" })).not.toBeInTheDocument();

    await selectRow("Gold");
    expect(await screen.findByRole("button", { name: "Change history" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Edit this plan" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Withdraw this plan" })).not.toBeInTheDocument();
  });

  it("counts the versions and what is sold against them", async () => {
    renderScreen(<PolicyPlans api={fakeApi()} />);
    await selectRow("Gold");

    expect(await screen.findByText("790")).toBeInTheDocument();   // active members
    // The sellable window, derived from the versions — `lastEffectiveTo: null` reads as open-ended, which is
    // a different thing from unknown.
    expect(screen.getByText(/01 Jan 2025 → open-ended/)).toBeInTheDocument();
  });

  /** THE refusal. Withdrawing a plan an active policy still sells would strand those enrolments. */
  it("keeps the dialog open and shows the count when withdrawal is refused", async () => {
    const deactivatePlan = vi.fn().mockRejectedValue(
      new ApiError("http", "conflict", 409, {
        detail: "This plan is still attached to 3 active policies. Detach it there first.",
      }),
    );
    renderScreen(<PolicyPlans api={fakeApi({ deactivatePlan } as Partial<PolicyApi>)} />);
    await selectRow("Gold");
    await userEvent.click(await screen.findByRole("button", { name: "Withdraw this plan" }));

    await userEvent.type(screen.getByLabelText(/Why/), "Withdrawing this product from the 2026 catalogue.");
    await userEvent.click(screen.getByRole("button", { name: "Withdraw this plan" }));

    expect(await screen.findByText(/still attached to 3 active policies/)).toBeInTheDocument();
    expect(deactivatePlan).toHaveBeenCalledOnce();
  });

  it("will not confirm a withdrawal on a reason that explains nothing", async () => {
    renderScreen(<PolicyPlans api={fakeApi()} />);
    await selectRow("Gold");
    await userEvent.click(await screen.findByRole("button", { name: "Withdraw this plan" }));

    const confirm = await screen.findByRole("button", { name: "Withdraw this plan" });
    expect(confirm).toBeDisabled();
    await userEvent.type(screen.getByLabelText(/Why/), "old");
    expect(confirm).toBeDisabled();
    await userEvent.clear(screen.getByLabelText(/Why/));
    await userEvent.type(screen.getByLabelText(/Why/), "Superseded by the 2027 package.");
    await waitFor(() => expect(confirm).toBeEnabled());
  });

  it("asks for a plan code on create and refuses to change one on edit", async () => {
    renderScreen(<PolicyPlans api={fakeApi()} />);

    await userEvent.click(await screen.findByRole("button", { name: "New plan" }));
    expect(await screen.findByLabelText(/Plan code/)).not.toHaveAttribute("readonly");
    await userEvent.click(await dialogButton("Cancel"));

    await selectRow("Gold");
    await userEvent.click(await screen.findByRole("button", { name: "Edit this plan" }));
    expect(await screen.findByLabelText(/Plan code/)).toHaveAttribute("readonly");
  });

  it("offers Return — not Withdraw — on a plan that is already off", async () => {
    const withdrawn = planView({ status: "Inactive", statusReason: "Superseded by the 2027 package." });
    renderScreen(<PolicyPlans api={fakeApi({
      plans: () => Promise.resolve([{ ...withdrawn, description: null }]),
      plan: () => Promise.resolve(planDetail(withdrawn)),
    } as Partial<PolicyApi>)} />);
    await selectRow("Gold");

    expect(await screen.findByRole("button", { name: "Return this plan to the catalogue" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Withdraw this plan" })).not.toBeInTheDocument();
    expect(screen.getByText(/Superseded by the 2027 package/)).toBeInTheDocument();
  });
});

// ════════════════════════════════════════════════════════════════════════════════════════════════════════
// CONTRACTS
// ════════════════════════════════════════════════════════════════════════════════════════════════════════

describe("the contract record", () => {
  it("offers New, Edit, Suspend, End and History to a membership administrator", async () => {
    renderScreen(<PolicyList api={fakeApi()} />, "beneficiary_mgmt");
    expect(await screen.findByRole("button", { name: "New policy" })).toBeInTheDocument();

    await selectRow("POL-2026-0001");
    expect(await screen.findByRole("button", { name: "Edit this policy" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Suspend this policy" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "End this policy" })).toBeInTheDocument();
  });

  /**
   * The counterpart to the plan's refusal, and the opposite answer. Suspending IS the operation, so the
   * member count is CONTEXT in the dialog rather than a barrier returned from the server.
   */
  it("states how many members a suspension reaches, before the button", async () => {
    renderScreen(<PolicyList api={fakeApi()} />, "beneficiary_mgmt");
    await selectRow("POL-2026-0001");
    await userEvent.click(await screen.findByRole("button", { name: "Suspend this policy" }));

    expect(await screen.findByText(/118 members are active on this policy right now/)).toBeInTheDocument();
  });

  it("sends the move and the reason it was given", async () => {
    const changePolicyStatus = vi.fn().mockResolvedValue({
      policy: policyView({ status: "Suspended" }), activeMembersAffected: 118,
    });
    renderScreen(<PolicyList api={fakeApi({ changePolicyStatus } as Partial<PolicyApi>)} />, "beneficiary_mgmt");
    await selectRow("POL-2026-0001");
    await userEvent.click(await screen.findByRole("button", { name: "Suspend this policy" }));

    await userEvent.type(screen.getByLabelText(/Why/), "The payer missed the February settlement.");
    await userEvent.click(screen.getByRole("button", { name: "Suspend this policy" }));

    await waitFor(() => expect(changePolicyStatus).toHaveBeenCalledWith(
      "pol1", "suspend", "The payer missed the February settlement.", expect.any(String)));
  });

  /** Ending is the one move here that cannot be taken back, and the dialog must not claim otherwise. */
  it("says ending cannot be undone, where suspending says it can", async () => {
    renderScreen(<PolicyList api={fakeApi()} />, "beneficiary_mgmt");
    await selectRow("POL-2026-0001");

    await userEvent.click(await screen.findByRole("button", { name: "Suspend this policy" }));
    expect(await screen.findByText(/can be resumed at any time/)).toBeInTheDocument();
    await userEvent.click(await dialogButton("Cancel"));

    await userEvent.click(await screen.findByRole("button", { name: "End this policy" }));
    expect(await screen.findByText(/cannot be undone — the way back is a renewal/)).toBeInTheDocument();
  });

  it("says in words that an active contract's own window has closed", async () => {
    renderScreen(<PolicyList api={fakeApi({
      policy: () => Promise.resolve(policyDetail(policyView({ windowState: "Ended", effectiveTo: "2025-12-31" }))),
    } as Partial<PolicyApi>)} />, "beneficiary_mgmt");
    await selectRow("POL-2026-0001");

    expect(await screen.findByText(/its own effective window has closed/)).toBeInTheDocument();
  });

  it("withholds the terms as a block rather than showing them empty", async () => {
    renderScreen(<PolicyList api={fakeApi({
      policy: () => Promise.resolve(policyDetail(policyView({ terms: null }), { committedLimit: null })),
    } as Partial<PolicyApi>)} />, "claims_officer");
    await selectRow("POL-2026-0001");

    expect(await screen.findByText("Restricted for your role")).toBeInTheDocument();
  });

  it("refuses to change a policy number on edit", async () => {
    renderScreen(<PolicyList api={fakeApi()} />, "beneficiary_mgmt");
    await selectRow("POL-2026-0001");
    await userEvent.click(await screen.findByRole("button", { name: "Edit this policy" }));

    // Scoped to the dialog: the register's own filter bar has a "Policy number" field too.
    const dialog = within(await screen.findByRole("dialog"));
    expect(dialog.getByLabelText(/Policy number/)).toHaveAttribute("readonly");
  });

  it("offers a reader no write at all", async () => {
    renderScreen(<PolicyList api={fakeApi()} />, "claims_officer");
    await screen.findByText("POL-2026-0001");
    expect(screen.queryByRole("button", { name: "New policy" })).not.toBeInTheDocument();

    await selectRow("POL-2026-0001");
    expect(await screen.findByRole("button", { name: "Change history" })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Suspend this policy" })).not.toBeInTheDocument();
  });
});

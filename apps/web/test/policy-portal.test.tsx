import { describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode, seedSession } from "./helpers";
import { NotesPanel, LimitMeters } from "../src/screens/PolicyPanels";
import { PolicyPlans, diffRules } from "../src/screens/PolicyProductAdmin";
import { MemberSearch } from "../src/screens/MemberAdmin";
import { NetworkTiers } from "../src/screens/NetworkTierAdmin";
import { permissionsForRole } from "../src/authz/permissions";
import { PORTALS } from "../src/portals/catalog";
import { ApiError } from "../src/api/http";
import type {
  BenefitRuleView,
  NoteView,
  PlanVersionView,
  PolicyApi,
} from "../src/api/policyApi";

/**
 * Phase 19.6 — the portal's load-bearing behaviours.
 *
 * Each test here corresponds to a claim the design makes that a screen could quietly break: a cancelled note
 * stays visible, a withheld body never reaches the DOM, an active plan version cannot be edited, a chart
 * always has its data table, and a retry after a failed write reuses its idempotency key.
 */

// ── A fake PolicyApi ────────────────────────────────────────────────────────────────────────────────────

function fakeApi(overrides: Partial<PolicyApi> = {}): PolicyApi {
  const reject = () => Promise.reject(new ApiError("network", "not stubbed in this test"));
  return {
    payers: () => Promise.resolve([]),
    plans: () => Promise.resolve([]),
    benefitCategories: () => Promise.resolve([]),
    planVersions: () => Promise.resolve([]),
    planVersion: reject,
    setPlanRules: reject,
    validatePlanVersion: () => Promise.resolve({ valid: true, problems: [] }),
    activatePlanVersion: reject,
    amendPlan: reject,
    policyQuery: () =>
      Promise.resolve({
        items: [], page: 1, pageSize: 50, totalCount: 0, totalPages: 0, sortedBy: "policyNo",
        payerScopeApplied: false, identityMatchTruncated: false, unavailable: [],
      }),
    policyPlans: () => Promise.resolve([]),
    attachPolicyPlan: reject,
    policyGroups: () => Promise.resolve([]),
    createGroup: reject,
    memberQuery: () =>
      Promise.resolve({
        items: [], page: 1, pageSize: 50, totalCount: 0, totalPages: 0, sortedBy: "memberNo",
        payerScopeApplied: false, identityMatchTruncated: false, unavailable: [],
      }),
    enrollment: reject,
    enrol: reject,
    terminate: reject,
    reinstate: reject,
    changeGroup: reject,
    changePlan: reject,
    previewPlanChange: reject,
    coverageDetails: reject,
    notes: () => Promise.resolve([]),
    addNote: reject,
    cancelNote: reject,
    pinNote: reject,
    documents: () => Promise.resolve([]),
    documentDownloadUrl: reject,
    timeline: () => Promise.resolve({ entries: [], nextCursor: null }),
    memberUtilization: reject,
    scopeUtilization: reject,
    networkTiers: () => Promise.resolve([]),
    createTier: reject,
    updateTier: reject,
    tierAssignments: () => Promise.resolve([]),
    assignTier: reject,
    revokeAssignment: () => Promise.resolve(),
    resolveTier: reject,
    bulkTemplates: () => Promise.resolve([]),
    uploadBulk: reject,
    validateBulk: reject,
    commitBulk: reject,
    bulkRows: () => Promise.resolve([]),
    bulkReconciliation: reject,
    exportUtilization: () => Promise.resolve(""),
    analytics: reject,
    analyticsOutlierMembers: () => Promise.resolve([]),
    analyticsExport: () => Promise.resolve(""),
    ...overrides,
  } as PolicyApi;
}

const baseNote = (over: Partial<NoteView>): NoteView => ({
  noteId: "n1",
  scope: "Enrollment",
  scopeRef: "e1",
  noteType: "General",
  visibilityClass: "Administrative",
  body: "Member asked to change branch.",
  bodyWithheld: false,
  withheldReason: null,
  authoredByUsername: "s.hassan",
  authoredByDisplay: "Sara Hassan",
  authoredAt: "2026-07-01T09:00:00Z",
  status: "Active",
  cancelledByUsername: null,
  cancelledAt: null,
  cancellationReason: null,
  supersedesNoteId: null,
  pinned: false,
  canCancel: false,
  ...over,
});

// ── Notes ───────────────────────────────────────────────────────────────────────────────────────────────

describe("Notes panel — a cancelled note is never hidden", () => {
  it("renders it struck through with the canceller, the timestamp and the reason", async () => {
    const api = fakeApi({
      notes: () =>
        Promise.resolve([
          baseNote({
            noteId: "n-cancelled",
            status: "Cancelled",
            body: "Wrongly recorded as terminated.",
            cancelledByUsername: "m.fouad",
            cancelledAt: "2026-07-02T11:30:00Z",
            cancellationReason: "Raised against the wrong member.",
          }),
        ]),
    });
    renderNode(<NotesPanel api={api} scope="enrollments" scopeRef="e1" canAdd={false} />);

    const item = await screen.findByTestId("note-item");
    // Still there, and its body is still readable — cancellation withdraws the assertion, not the record.
    expect(within(item).getByText("Wrongly recorded as terminated.")).toBeInTheDocument();
    expect(item.className).toContain("cancelled");

    // The fourth cue is the WORD, not the colour or the strike-through.
    expect(within(item).getByText("Cancelled")).toBeInTheDocument();

    const cancellation = within(item).getByTestId("note-cancellation");
    expect(cancellation).toHaveTextContent("m.fouad");
    expect(cancellation).toHaveTextContent("Raised against the wrong member.");
    // Africa/Cairo, not the machine's zone: 11:30Z is 14:30 in Cairo in July (UTC+3 under DST).
    expect(cancellation).toHaveTextContent("14:30");
  });

  it("shows a withheld body as a locked state and never puts the body in the DOM", async () => {
    const api = fakeApi({
      notes: () =>
        Promise.resolve([
          baseNote({
            noteId: "n-clinical",
            noteType: "Clinical",
            visibilityClass: "Clinical",
            body: null,
            bodyWithheld: true,
            withheldReason: "Clinical content is not readable by your role.",
          }),
        ]),
    });
    renderNode(<NotesPanel api={api} scope="enrollments" scopeRef="e1" canAdd={false} />);

    const locked = await screen.findByTestId("note-withheld");
    expect(locked).toHaveTextContent("Restricted — clinical note");
    expect(locked).toHaveTextContent("Clinical content is not readable by your role.");
    // Existence, type, author and date are all present — the note is not made to look absent.
    expect(screen.getByText("s.hassan")).toBeInTheDocument();
    expect(screen.getByText("Clinical")).toBeInTheDocument();
  });

  it("refuses to cancel without a reason and never calls the API", async () => {
    const cancelNote = vi.fn();
    const api = fakeApi({
      notes: () => Promise.resolve([baseNote({ canCancel: true })]),
      cancelNote,
    });
    renderNode(<NotesPanel api={api} scope="enrollments" scopeRef="e1" canAdd={false} />);

    await userEvent.click(await screen.findByRole("button", { name: "Cancel note" }));
    await userEvent.click(screen.getByRole("button", { name: "Cancel this note" }));

    expect(await screen.findByText("A reason is required to cancel a note.")).toBeInTheDocument();
    expect(cancelNote).not.toHaveBeenCalled();
  });

  it("offers no cancel affordance when the server says this caller may not cancel", async () => {
    const api = fakeApi({ notes: () => Promise.resolve([baseNote({ canCancel: false })]) });
    renderNode(<NotesPanel api={api} scope="enrollments" scopeRef="e1" canAdd={false} />);

    await screen.findByTestId("note-item");
    expect(screen.queryByRole("button", { name: "Cancel note" })).not.toBeInTheDocument();
  });
});

describe("Notes panel — idempotency", () => {
  it("reuses the key when a write fails and rotates it only after one succeeds", async () => {
    const keys: string[] = [];
    let failNext = true;
    const api = fakeApi({
      notes: () => Promise.resolve([]),
      addNote: (_scope, _id, _body, key) => {
        keys.push(key);
        if (failNext) {
          failNext = false;
          return Promise.reject(new ApiError("network", "connection lost"));
        }
        return Promise.resolve(baseNote({}));
      },
    });
    renderNode(<NotesPanel api={api} scope="enrollments" scopeRef="e1" />);

    const body = await screen.findByLabelText("Note");
    await userEvent.type(body, "Branch transfer agreed.");
    await userEvent.click(screen.getByRole("button", { name: "Save note" }));
    await screen.findByText(/Could not reach the server/);

    // Second press = the SAME logical write, so the SAME key. This is what stops a retried note becoming two.
    await userEvent.click(screen.getByRole("button", { name: "Save note" }));
    await waitFor(() => expect(keys).toHaveLength(2));
    expect(keys[0]).toBe(keys[1]);

    // A genuinely new note gets a new key, or the second note would be swallowed as a replay of the first.
    await userEvent.type(await screen.findByLabelText("Note"), "A second, different note.");
    await userEvent.click(screen.getByRole("button", { name: "Save note" }));
    await waitFor(() => expect(keys).toHaveLength(3));
    expect(keys[2]).not.toBe(keys[0]);
  });
});

// ── Plan version immutability ───────────────────────────────────────────────────────────────────────────

const rule = (over: Partial<BenefitRuleView> = {}): BenefitRuleView => ({
  ruleId: "r1",
  benefitCategoryId: "c1",
  benefitCategoryCode: "LAB",
  isCovered: true,
  limitType: "Annual",
  limitValue: 1000,
  resetPeriod: "Yearly",
  deductible: null,
  deductibleWaived: false,
  waitingPeriodDays: 30,
  requiresPreauth: false,
  preauthCostThreshold: null,
  exclusions: "[]",
  notes: null,
  tiers: [],
  ...over,
});

const version = (over: Partial<PlanVersionView>): PlanVersionView => ({
  planVersionId: "v1",
  planId: "p1",
  versionNo: 1,
  effectiveFrom: "2026-01-01",
  effectiveTo: null,
  status: "Draft",
  editable: true,
  activatedAt: null,
  supersededByVersionId: null,
  rules: [rule()],
  ...over,
});

function plansApi(v: PlanVersionView, extra: Partial<PolicyApi> = {}) {
  return fakeApi({
    plans: () => Promise.resolve([{ planId: "p1", planCode: "STD", nameEn: "Standard", nameAr: "قياسي", description: null, category: "Core", status: "Active" }]),
    benefitCategories: () => Promise.resolve([{ benefitCategoryId: "c1", code: "LAB", name: "Laboratory" }]),
    networkTiers: () =>
      Promise.resolve([
        { networkTierId: "t1", tierCode: "TIER1", nameEn: "Preferred", nameAr: "مفضّل", rank: 1, description: null, isOutOfNetwork: false, status: "Active" },
      ]),
    planVersions: () => Promise.resolve([v]),
    ...extra,
  });
}

async function openPlan() {
  // An interactive DataTable is a role="grid"; its rows are the selectable elements, not buttons.
  await userEvent.click(await screen.findByRole("row", { name: /STD/ }));
}

describe("Plan version editor — an active version is immutable", () => {
  it("states why, offers amendment, and renders no save control", async () => {
    renderNode(<PolicyPlans api={plansApi(version({ status: "Active", editable: false, activatedAt: "2026-01-01T00:00:00Z" }))} />);
    await openPlan();

    const notice = await screen.findByTestId("immutable-notice");
    expect(notice).toHaveTextContent("can never be edited");
    expect(notice).toHaveTextContent("Amend it to create a new draft");

    expect(screen.getByText("Active — immutable")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Save benefit configuration" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Activate" })).not.toBeInTheDocument();
    // The one legitimate way forward is present.
    expect(screen.getByRole("button", { name: "Amend — create a new draft" })).toBeInTheDocument();

    // Every field is disabled, not merely unstyled: a keyboard user must not be able to type into it either.
    expect(screen.getByLabelText("Covered — LAB")).toBeDisabled();
    expect(screen.getByLabelText("Limit — LAB")).toBeDisabled();
    expect(screen.getByLabelText("Waiting (days) — LAB")).toBeDisabled();
  });

  it("lets a draft be edited, and gates activation on validation", async () => {
    const setPlanRules = vi.fn().mockResolvedValue(version({}));
    renderNode(
      <PolicyPlans api={plansApi(version({ status: "Draft", editable: true }), { setPlanRules })} />,
    );
    await openPlan();

    await screen.findByText("Draft — editable");
    expect(screen.getByLabelText("Covered — LAB")).not.toBeDisabled();

    // Activation is unreachable until the draft has been validated — the server enforces the same
    // transition; this only stops an operator reaching for it first.
    expect(screen.getByRole("button", { name: "Activate" })).toBeDisabled();
    await userEvent.click(screen.getByRole("button", { name: "Validate" }));
    await screen.findByTestId("validation-result");
    await waitFor(() => expect(screen.getByRole("button", { name: "Activate" })).not.toBeDisabled());

    await userEvent.click(screen.getByRole("button", { name: "Save benefit configuration" }));
    await waitFor(() => expect(setPlanRules).toHaveBeenCalledTimes(1));
    // The rules go back keyed by CODE, which is what the service writes — proving the read/write round trip.
    expect(setPlanRules.mock.calls[0][1][0].benefitCategoryCode).toBe("LAB");
  });

  it("surfaces every activation problem the service reported", async () => {
    renderNode(
      <PolicyPlans
        api={plansApi(version({}), {
          validatePlanVersion: () =>
            Promise.resolve({
              valid: false,
              problems: [{ code: "TIER_UNPRICED", detail: "LAB leaves TIER1 unpriced." }],
            }),
        })}
      />,
    );
    await openPlan();
    await userEvent.click(await screen.findByRole("button", { name: "Validate" }));

    const result = await screen.findByTestId("validation-result");
    expect(result).toHaveTextContent("TIER_UNPRICED");
    expect(result).toHaveTextContent("LAB leaves TIER1 unpriced.");
    expect(screen.getByRole("button", { name: "Activate" })).toBeDisabled();
  });
});

describe("Version diff", () => {
  it("names what changed, what was added and what was removed", () => {
    const before = [rule({ limitValue: 1000, waitingPeriodDays: 30 }), rule({ benefitCategoryCode: "PHARMACY", benefitCategoryId: "c2" })];
    const after = [
      rule({ limitValue: 1500, waitingPeriodDays: 0 }),
      rule({ benefitCategoryCode: "IMAGING", benefitCategoryId: "c3" }),
    ];
    const lines = diffRules(after, before);

    const changed = lines.find((l) => l.category === "LAB");
    expect(changed?.kind).toBe("changed");
    expect(changed?.detail).toContain("limit 1000 → 1500");
    expect(changed?.detail).toContain("waiting 30d → 0d");

    expect(lines.find((l) => l.category === "IMAGING")?.kind).toBe("added");
    expect(lines.find((l) => l.category === "PHARMACY")?.kind).toBe("removed");
  });

  it("reports nothing when two versions carry the same configuration", () => {
    expect(diffRules([rule()], [rule()])).toEqual([]);
  });
});

// ── Member query ────────────────────────────────────────────────────────────────────────────────────────

describe("Member query table", () => {
  it("renders the page and says so when the result is only a subset", async () => {
    const api = fakeApi({
      memberQuery: () =>
        Promise.resolve({
          items: [
            {
              enrollmentId: "e1", beneficiaryId: "b1", memberNo: "MRS-M-2026-000001",
              givenName: "Nour", familyName: "Ali", beneficiaryStatus: "Active",
              policyId: "p1", policyPlanId: "pp1", planLabel: "Standard", groupId: null, payerId: null,
              relationship: "Principal", status: "Active",
              effectiveFrom: "2026-01-01", effectiveTo: null,
              waitingPeriodEndsOn: null, waitingPeriodState: "Served", branchId: null,
              terminationReason: null,
              totalLimit: 1000, totalConsumed: 300, totalRemaining: 700, percentUsed: 30,
              utilizationBand: "Low",
            },
          ],
          page: 1, pageSize: 50, totalCount: 1, totalPages: 1, sortedBy: "memberNo",
          payerScopeApplied: true,
          identityMatchTruncated: true,
          unavailable: [],
        }),
    });
    renderNode(<MemberSearch api={api} />);

    expect(await screen.findByText("MRS-M-2026-000001")).toBeInTheDocument();
    expect(screen.getByText("Nour Ali")).toBeInTheDocument();
    expect(screen.getByText("30%")).toBeInTheDocument();

    // Both caveats are stated. A truncated page presented as a complete one is a wrong answer, not a short one.
    expect(screen.getByText(/this page is a subset/)).toBeInTheDocument();
    expect(screen.getByText(/not the whole book/)).toBeInTheDocument();
  });

  it("renders a blank name rather than a guessed one when patient-service could not be asked", async () => {
    const api = fakeApi({
      memberQuery: () =>
        Promise.resolve({
          items: [
            {
              enrollmentId: "e1", beneficiaryId: "b1", memberNo: "MRS-M-2026-000002",
              givenName: null, familyName: null, beneficiaryStatus: null,
              policyId: "p1", policyPlanId: "pp1", planLabel: null, groupId: null, payerId: null,
              relationship: "Spouse", status: "Active",
              effectiveFrom: "2026-01-01", effectiveTo: null,
              waitingPeriodEndsOn: null, waitingPeriodState: "Served", branchId: null,
              terminationReason: null,
              totalLimit: null, totalConsumed: null, totalRemaining: null, percentUsed: null,
              utilizationBand: "Unknown",
            },
          ],
          page: 1, pageSize: 50, totalCount: 1, totalPages: 1, sortedBy: "memberNo",
          payerScopeApplied: false, identityMatchTruncated: false, unavailable: ["patient"],
        }),
    });
    renderNode(<MemberSearch api={api} />);

    const row = await screen.findByText("MRS-M-2026-000002");
    expect(row).toBeInTheDocument();
    expect(screen.getAllByText("—").length).toBeGreaterThan(0);
  });
});

// ── The plan-change dry run ─────────────────────────────────────────────────────────────────────────────

const memberRow = {
  enrollmentId: "e1", beneficiaryId: "b1", memberNo: "MRS-M-2026-000001",
  givenName: "Nour", familyName: "Ali", beneficiaryStatus: "Active",
  policyId: "p1", policyPlanId: "pp1", planLabel: "Rich", groupId: null, payerId: null,
  relationship: "Principal", status: "Active",
  effectiveFrom: "2026-01-01", effectiveTo: null,
  waitingPeriodEndsOn: null, waitingPeriodState: "Served", branchId: null, terminationReason: null,
  totalLimit: 1000, totalConsumed: 300, totalRemaining: 700, percentUsed: 30, utilizationBand: "Low",
} as const;

const leanPlan = {
  policyPlanId: "pp2", policyId: "p1", planVersionId: "v2", planLabel: "Lean",
  effectiveFrom: "2026-01-01", effectiveTo: null, isDefault: false, eligibilityRule: null,
  maxMembers: null, status: "Active", memberCount: 4,
} as const;

const previewFor = (dropped: unknown[] = []) => ({
  enrollmentId: "e1", fromPolicyPlanId: "pp1", toPolicyPlanId: "pp2", toPlanLabel: "Lean",
  planVersionId: "v2", effectiveDate: "2026-06-01", consumptionPolicy: "CarryForward",
  rows: [
    {
      benefitCategoryId: "c1", benefitCategoryCode: "LAB", held: true,
      currentLimitValue: 1000, consumedValue: 300, newLimitValue: 500, remaining: 200, exhausted: false,
    },
  ],
  droppedCategories: dropped,
});

async function openChangePlan(api: PolicyApi) {
  const { MemberDetail } = await import("../src/screens/MemberAdmin");
  renderNode(<MemberDetail api={api} row={memberRow} onChanged={() => {}} />);
  await userEvent.click(await screen.findByRole("button", { name: "Change plan" }));
  return screen.findByTestId("dialog-changePlan");
}

describe("Change plan — the officer sees the consequence before confirming", () => {
  it("shows the server's arithmetic, both ceilings, and not an estimate assembled here", async () => {
    const previewPlanChange = vi.fn().mockResolvedValue(previewFor());
    const api = fakeApi({ policyPlans: () => Promise.resolve([leanPlan]), previewPlanChange });
    const dialog = await openChangePlan(api);

    // Nothing to preview until a target plan is named, and nothing to confirm either.
    expect(within(dialog).getByRole("button", { name: "Confirm" })).toBeDisabled();
    expect(previewPlanChange).not.toHaveBeenCalled();

    await userEvent.selectOptions(within(dialog).getByLabelText("Move to plan"), "pp2");

    const preview = await within(dialog).findByTestId("carry-preview");
    // 300 consumed against a new ceiling of 500 leaves 200 — the number the whole dialog exists to show.
    expect(within(preview).getByText(/1,000\.00/)).toBeInTheDocument();
    expect(within(preview).getByText(/\b500\.00/)).toBeInTheDocument();
    expect(within(preview).getByText(/\b200\.00/)).toBeInTheDocument();
    expect(within(preview).getByText("LAB")).toBeInTheDocument();
    await waitFor(() => expect(within(dialog).getByRole("button", { name: "Confirm" })).toBeEnabled());
  });

  it("names the benefits the new plan would withdraw", async () => {
    const api = fakeApi({
      policyPlans: () => Promise.resolve([leanPlan]),
      previewPlanChange: () =>
        Promise.resolve(
          previewFor([
            { benefitCategoryId: "c2", benefitCategoryCode: "PHARMACY", currentLimitValue: 400, consumedValue: 120 },
          ]) as never,
        ),
    });
    const dialog = await openChangePlan(api);
    await userEvent.selectOptions(within(dialog).getByLabelText("Move to plan"), "pp2");

    // The one consequence no client-side estimate could recover: the new plan grants no row for this at all,
    // so without the server saying so the benefit would simply disappear between screens.
    const dropped = await within(dialog).findByTestId("carry-dropped");
    expect(within(dropped).getByText("PHARMACY")).toBeInTheDocument();
    expect(within(dropped).getByText(/400\.00/)).toBeInTheDocument();
    expect(within(dropped).getByText(/120\.00/)).toBeInTheDocument();
  });

  it("refuses to let the change be confirmed when the dry run failed", async () => {
    const changePlan = vi.fn();
    const api = fakeApi({
      policyPlans: () => Promise.resolve([leanPlan]),
      previewPlanChange: () => Promise.reject(new ApiError("http", "PLAN_NOT_IN_FORCE", 409)),
      changePlan,
    });
    const dialog = await openChangePlan(api);
    await userEvent.selectOptions(within(dialog).getByLabelText("Move to plan"), "pp2");

    expect(await within(dialog).findByTestId("preview-error")).toBeInTheDocument();
    expect(within(dialog).queryByTestId("carry-preview")).not.toBeInTheDocument();
    // The preview runs the same resolution the change does, so a failed preview is a change that would fail.
    expect(within(dialog).getByRole("button", { name: "Confirm" })).toBeDisabled();
    expect(changePlan).not.toHaveBeenCalled();
  });
});

// ── Charts always carry their data table ────────────────────────────────────────────────────────────────

describe("Accessible alternative for every proportion drawn", () => {
  it("puts the data table in the DOM with no toggle to find", () => {
    renderNode(
      <LimitMeters
        caption="Consumption against limit"
        rows={[
          { label: "LAB", consumed: 300, limit: 1000, valueText: "EGP 300.00", limitText: "EGP 1,000.00" },
          { label: "PHARMACY", consumed: 950, limit: 1000, valueText: "EGP 950.00", limitText: "EGP 1,000.00" },
        ]}
      />,
    );

    const table = screen.getByRole("table", { name: "Consumption against limit" });
    expect(within(table).getByRole("rowheader", { name: "LAB" })).toBeInTheDocument();
    expect(within(table).getByText("30%")).toBeInTheDocument();
    expect(within(table).getByText("95%")).toBeInTheDocument();
    // No control stands between a screen-reader user and the numbers.
    expect(screen.queryByRole("button", { name: /table/i })).not.toBeInTheDocument();
  });
});

// ── Network tiers: two roles, one screen, different authority ───────────────────────────────────────────

describe("Network tier administration", () => {
  it("is read-only for a policy administrator, with the write affordances absent", async () => {
    seedSession("policy_admin");
    renderNode(<NetworkTiers api={fakeApi()} />);

    expect(await screen.findByTestId("tiers-read-only")).toHaveTextContent("Network Team owns the tier structure");
    expect(screen.queryByTestId("tier-create")).not.toBeInTheDocument();
  });

  it("offers tier creation to the Network Team", async () => {
    seedSession("provider_admin");
    renderNode(<NetworkTiers api={fakeApi()} />);

    expect(await screen.findByTestId("tier-create")).toBeInTheDocument();
    expect(screen.queryByTestId("tiers-read-only")).not.toBeInTheDocument();
  });
});

// ── Min-necessary is structural ─────────────────────────────────────────────────────────────────────────

describe("Policy administration has no clinical reach", () => {
  it("grants no clinical permission to policy_admin", () => {
    const perms = [...permissionsForRole("policy_admin")];
    for (const clinical of ["emr.read", "emr.write", "results.inbox", "prescriptions.write", "orders.place", "vitals.write"]) {
      expect(perms).not.toContain(clinical);
    }
  });

  it("mounts no clinical route in the policy portal", () => {
    const portal = PORTALS.find((p) => p.role === "policy_admin");
    expect(portal).toBeDefined();
    for (const section of portal!.sections) {
      expect(section.permission.startsWith("emr.")).toBe(false);
      expect(section.permission.startsWith("results.")).toBe(false);
    }
  });

  it("gives beneficiary management the membership book without the benefit product", () => {
    const perms = [...permissionsForRole("beneficiary_mgmt")];
    expect(perms).toContain("policy.members");
    expect(perms).toContain("policy.bulk");
    // Enrolling a member and deciding what that plan pays for are different jobs, held by different people.
    expect(perms).not.toContain("policy.plans");
    expect(perms).not.toContain("policy.payers");
  });
});

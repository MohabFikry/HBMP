import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode, seedSession } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
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
    // A household of one by default — the shape every member has until somebody is enrolled under them.
    family: (enrollmentId: string) =>
      Promise.resolve({ enrollmentId, members: [], unavailable: [], withheld: 0 }),
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
    tierProviders: () => Promise.resolve([]),
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
    // The author is named — by DISPLAY name, because `authoredByUsername` on a note written through the
    // portal is the subject uuid, and a record every note of which is signed with a guid names nobody.
    expect(screen.getByText("Sara Hassan")).toBeInTheDocument();
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

    // Composing is a modal now: the panel opens on the NOTES, not on an empty form.
    await userEvent.click(await screen.findByTestId("add-note"));
    const body = await screen.findByLabelText("Note");
    await userEvent.type(body, "Branch transfer agreed.");
    await userEvent.click(screen.getByRole("button", { name: "Save note" }));
    await screen.findByText(/Could not reach the server/);

    // Second press = the SAME logical write, so the SAME key. This is what stops a retried note becoming two.
    await userEvent.click(screen.getByRole("button", { name: "Save note" }));
    await waitFor(() => expect(keys).toHaveLength(2));
    expect(keys[0]).toBe(keys[1]);

    // A genuinely new note gets a new key, or the second note would be swallowed as a replay of the first.
    // The modal closed on the successful save — a failed one keeps it open, so nothing typed is ever lost.
    await userEvent.click(await screen.findByTestId("add-note"));
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
  // The DIALOG, not the body div that carries the testid. `MembershipDialog` is a real modal now, so its
  // confirm/cancel live in the modal footer — a sibling of `dialog-changePlan`, not a descendant. Querying
  // the role also asserts the thing that changed: this is an actual dialog rather than a card that claimed
  // `aria-modal` while nothing trapped focus.
  await screen.findByTestId("dialog-changePlan");
  return screen.findByRole("dialog");
}

/**
 * Pick a plan in the change dialog.
 *
 * `Move to plan` is the design system's Select — a button + listbox, not a native <select>, because a native
 * one cannot style its own popup and arrived a few pixels shorter than the date field above it. So the test
 * drives it the way a person does: open the list, click the option.
 */
async function choosePlan(dialog: HTMLElement, label: string) {
  await userEvent.click(within(dialog).getByRole("combobox", { name: "Move to plan" }));
  await userEvent.click(await screen.findByRole("option", { name: label }));
}

describe("Change plan — the officer sees the consequence before confirming", () => {
  it("shows the server's arithmetic, both ceilings, and not an estimate assembled here", async () => {
    const previewPlanChange = vi.fn().mockResolvedValue(previewFor());
    const api = fakeApi({ policyPlans: () => Promise.resolve([leanPlan]), previewPlanChange });
    const dialog = await openChangePlan(api);

    // Nothing to preview until a target plan is named, and nothing to confirm either.
    expect(within(dialog).getByRole("button", { name: "Confirm" })).toBeDisabled();
    expect(previewPlanChange).not.toHaveBeenCalled();

    await choosePlan(dialog, "Lean");

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
    await choosePlan(dialog, "Lean");

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
    await choosePlan(dialog, "Lean");

    expect(await within(dialog).findByTestId("preview-error")).toBeInTheDocument();
    expect(within(dialog).queryByTestId("carry-preview")).not.toBeInTheDocument();
    // The preview runs the same resolution the change does, so a failed preview is a change that would fail.
    expect(within(dialog).getByRole("button", { name: "Confirm" })).toBeDisabled();
    expect(changePlan).not.toHaveBeenCalled();
  });
});

// ── The member card ─────────────────────────────────────────────────────────────────────────────────────

/** The dev fixtures with ONE method replaced — the identity read, which is what these cases turn on. */
function withBeneficiary(beneficiary: (id: string) => Promise<unknown>) {
  return Object.assign(new DevApiClient({ latencyMs: 0 }), { beneficiary }) as never;
}

describe("The member card — who this is, before what you can do to them", () => {
  // BEN-1 is a full record in the dev fixtures: born 1989-03-14, Male, SY, with a primary phone.
  const rowWithRecord = { ...memberRow, beneficiaryId: "BEN-1" } as const;

  const renderCard = async () => {
    const { MemberDetail } = await import("../src/screens/MemberAdmin");
    renderNode(<MemberDetail api={fakeApi()} row={rowWithRecord} onChanged={() => {}} />);
  };

  it("shows the general information a desk uses to recognise somebody", async () => {
    await renderCard();
    const info = await screen.findByTestId("member-general-info");

    expect(within(info).getByText(/\d+ yrs/)).toBeInTheDocument();
    expect(within(info).getByText("Male")).toBeInTheDocument();
    expect(within(info).getByText("SY")).toBeInTheDocument();
    expect(within(info).getByText(/\+20 100/)).toBeInTheDocument();
  });

  it("labels each fact in text, so the icon is never the only cue", async () => {
    await renderCard();
    const info = await screen.findByTestId("member-general-info");
    // The icon says which field; the visually-hidden label says it again for anyone not looking at it.
    expect(within(info).getByText("Nationality:")).toBeInTheDocument();
    expect(within(info).getByText("Phone:")).toBeInTheDocument();
  });

  it("renders nothing at all when the role received none of those fields", async () => {
    // patient-service PROJECTS BY ROLE: a caller who may read the membership and not the person gets a record
    // with those fields absent. A row of dashes would tell an officer the system holds no phone number for
    // somebody whose number they are simply not entitled to see — and they would then ask the beneficiary to
    // repeat something already on file.
    const { MemberDetail } = await import("../src/screens/MemberAdmin");
    renderNode(
      <MemberDetail api={fakeApi()} row={rowWithRecord} onChanged={() => {}} />,
      withBeneficiary(() =>
        Promise.resolve({
          id: "BEN-1", givenName: "Omar", familyName: "Khaled",
          status: { kind: "ok", label: { en: "Active", ar: "نشط" } }, statusRaw: "Active",
          identifiers: [],
        })),
    );

    await screen.findByTestId("member-detail");
    await waitFor(() => expect(screen.getByTestId("edit-details")).toBeEnabled());
    expect(screen.queryByTestId("member-general-info")).not.toBeInTheDocument();
  });

  it("opens the correction form from the card, not only from the Details tab", async () => {
    await renderCard();
    const edit = await screen.findByTestId("edit-details");
    await waitFor(() => expect(edit).toBeEnabled());
    await userEvent.click(edit);

    // The SAME modal the Details tab opens — same fields, same PATCH, same log entry.
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByLabelText("Given name")).toHaveValue("Omar");
    expect(within(dialog).getByRole("button", { name: "Save changes" })).toBeInTheDocument();
  });

  it("offers no correction affordance while the record has not arrived", async () => {
    // Enabled with nothing to edit would open an empty form over a real person's record.
    const { MemberDetail } = await import("../src/screens/MemberAdmin");
    renderNode(
      <MemberDetail api={fakeApi()} row={rowWithRecord} onChanged={() => {}} />,
      withBeneficiary(() => new Promise(() => {})),   // in flight, and staying that way
    );
    expect(await screen.findByTestId("edit-details")).toBeDisabled();
  });
});

// ── The covered household ───────────────────────────────────────────────────────────────────────────────

const familyMember = (over: Record<string, unknown>) => ({
  enrollmentId: "e-x", beneficiaryId: "b-x", memberNo: "MRS-M-2026-000009",
  givenName: "Sara", familyName: "Ali", relationship: "Child", status: "Active",
  isPrincipal: false, planLabel: "Rich", effectiveFrom: "2026-01-01", effectiveTo: null,
  isSubject: false, ...over,
});

async function openFamily(api: PolicyApi) {
  const { MemberDetail } = await import("../src/screens/MemberAdmin");
  renderNode(<MemberDetail api={api} row={memberRow} onChanged={() => {}} />);
  await userEvent.click(await screen.findByTestId("open-family"));
  return screen.findByRole("dialog");
}

describe("Family — who else this cover reaches", () => {
  it("lists the household and marks the principal and the member you opened", async () => {
    const api = fakeApi({
      family: () =>
        Promise.resolve({
          enrollmentId: "e1",
          members: [
            familyMember({ enrollmentId: "e0", memberNo: "MRS-M-2026-000001", givenName: "Omar", relationship: "Principal", isPrincipal: true }),
            familyMember({ enrollmentId: "e1", memberNo: "MRS-M-2026-000002", givenName: "Nour", relationship: "Spouse", isSubject: true }),
            familyMember({ enrollmentId: "e2", memberNo: "MRS-M-2026-000003" }),
          ],
          unavailable: [],
          withheld: 0,
        } as never),
    });
    const dialog = await openFamily(api);

    // Body rows by ROLE. The table is a `DataTable` now, so there is no hand-placed testid to select on —
    // and selecting on the rendered table is closer to what the operator sees anyway.
    await within(dialog).findByRole("table");
    const rows = within(dialog).getAllByRole("row").slice(1);
    expect(rows).toHaveLength(3);
    // Both facts are WORDS. A bold row would say nothing to a screen reader, and "which one is the principal"
    // is the question the list is read to answer.
    expect(within(rows[0]).getByText("Principal", { selector: ".mrs-chip, .mrs-chip *" })).toBeInTheDocument();
    expect(within(rows[1]).getByText("This member")).toBeInTheDocument();
    // The member you opened is IN the list, not filtered out of it — which the "This member" chip on row
    // two, asserted just above, already states. The old `data-subject` attribute said the same thing a
    // second time in markup no user could perceive.
  });

  it("says nobody else is covered rather than showing an empty table", async () => {
    // A table with a header and no rows reads as a failed lookup. This is a real and common answer: most
    // beneficiaries are enrolled alone.
    const api = fakeApi({
      family: () =>
        Promise.resolve({
          enrollmentId: "e1",
          members: [familyMember({ enrollmentId: "e1", isSubject: true, isPrincipal: true })],
          unavailable: [],
          withheld: 0,
        } as never),
    });
    const dialog = await openFamily(api);

    expect(await within(dialog).findByText("Nobody else is enrolled under this cover.")).toBeInTheDocument();
    expect(within(dialog).queryByRole("table")).not.toBeInTheDocument();
  });

  it("says how many household members its payer scope withheld", async () => {
    const api = fakeApi({
      family: () =>
        Promise.resolve({
          enrollmentId: "e1",
          members: [
            familyMember({ enrollmentId: "e1", isSubject: true }),
            familyMember({ enrollmentId: "e2" }),
          ],
          unavailable: [],
          withheld: 2,
        } as never),
    });
    const dialog = await openFamily(api);

    // A family of four rendering as two, silently, is a wrong answer. Saying so makes it a true one.
    expect(await within(dialog).findByText(/2 more household member/)).toBeInTheDocument();
  });

  it("shows a member number when the name could not be looked up, and says why", async () => {
    const api = fakeApi({
      family: () =>
        Promise.resolve({
          enrollmentId: "e1",
          members: [
            familyMember({ enrollmentId: "e1", isSubject: true }),
            familyMember({ enrollmentId: "e2", givenName: null, familyName: null }),
          ],
          unavailable: ["patient-service"],
          withheld: 0,
        } as never),
    });
    const dialog = await openFamily(api);

    await within(dialog).findByRole("table");
    const rows = within(dialog).getAllByRole("row").slice(1);
    expect(within(rows[1]).getByText("Name unavailable")).toBeInTheDocument();
    expect(within(dialog).getByText(/Names could not be looked up/)).toBeInTheDocument();
  });
});

// ── A policy document can be looked at without being taken ──────────────────────────────────────────────

describe("Policy documents — looking and taking are different acts", () => {
  const doc = (over: Record<string, unknown> = {}) => ({
    linkId: "link-1", scope: "Policy", scopeRef: "p1", documentId: "d1", versionNo: 1,
    documentClass: "PolicyContract", visibilityClass: "Administrative", title: "contract-2026.pdf",
    uploadedByUsername: "a.hassan", uploadedByDisplay: "A. Hassan", uploadedAt: "2026-02-12T09:30:00Z",
    status: "Active", expired: false, canDownload: true, ...over,
  });

  const render = async (over: Record<string, unknown> = {}) => {
    const { DocumentsPanel } = await import("../src/screens/PolicyPanels");
    const documentDownloadUrl = vi.fn().mockResolvedValue({ url: "https://minio.example/c.pdf?sig=x" });
    const api = fakeApi({ documents: () => Promise.resolve([doc(over)]), documentDownloadUrl } as never);
    renderNode(<DocumentsPanel api={api} scope="policies" scopeRef="p1" />);
    await screen.findByText("contract-2026.pdf");
    return documentDownloadUrl;
  };

  it("offers a view and a download, and records them as different disclosures", async () => {
    // The panel offered only a download, so reading a contract in place meant taking a copy of it — and the
    // audit trail could not tell the two apart a year later.
    const documentDownloadUrl = await render();

    await userEvent.click(screen.getByRole("button", { name: /view — contract-2026\.pdf/i }));
    expect(documentDownloadUrl).toHaveBeenCalledWith("link-1", "preview");

    await userEvent.click(screen.getByRole("button", { name: /close/i }));
    await userEvent.click(screen.getByRole("button", { name: /download — contract-2026\.pdf/i }));
    expect(documentDownloadUrl).toHaveBeenCalledWith("link-1", "download");
  });

  it("opens the document in place rather than navigating away from the policy", async () => {
    await render();
    await userEvent.click(screen.getByRole("button", { name: /view — contract-2026\.pdf/i }));
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
  });

  it("still names a locked document instead of rendering nothing where the buttons would be", async () => {
    await render({ canDownload: false });
    expect(screen.getByText(/^locked$/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /view —/i })).not.toBeInTheDocument();
  });
});

// ── The Logs tab says who, and what moved ───────────────────────────────────────────────────────────────

describe("Change timeline — a log that answers who and what", () => {
  const entry = (over: Record<string, unknown>) => ({
    entryId: "t1", scope: "Enrollment", scopeRef: "e1",
    occurredAt: "2026-07-31T15:18:00Z", eventType: "MemberPlanChanged", eventCategory: "Plan",
    actorUsername: "0f4c-subject-uuid", actorDisplay: "Layla Mansour",
    summaryEn: "Member moved to another plan", summaryAr: "تم نقل العضو إلى خطة أخرى",
    changeDiff: null, diffWithheld: false,
    visibilityClass: "Administrative", sourceService: "policy-service", correlationId: null,
    targetRef: null, targetKind: null, ...over,
  });

  const renderTimeline = async (over: Record<string, unknown>) => {
    const { ChangeTimeline } = await import("../src/screens/PolicyPanels");
    const api = fakeApi({ timeline: () => Promise.resolve({ entries: [entry(over)], nextCursor: null } as never) });
    renderNode(<ChangeTimeline api={api} scope="enrollments" scopeRef="e1" lang="en" />);
  };

  it("names the person who made the change, not their subject id", async () => {
    await renderTimeline({});
    const actor = await screen.findByTestId("timeline-actor");
    expect(actor).toHaveTextContent("Layla Mansour");
    expect(actor).not.toHaveTextContent("0f4c-subject-uuid");
  });

  it("still names the actor when only the subject was recorded", async () => {
    // The guard used to be on actorDisplay while the value rendered was actorUsername — so an entry with a
    // subject and no display name showed nobody at all, which was every entry the service wrote.
    await renderTimeline({ actorDisplay: null });
    expect(await screen.findByTestId("timeline-actor")).toHaveTextContent("0f4c-subject-uuid");
  });

  it("shows the value the field held and the value it holds now", async () => {
    await renderTimeline({
      changeDiff: JSON.stringify({
        plan: { before: "Standard", after: "Enhanced" },
        effectiveDate: { before: null, after: "2026-08-01" },
      }),
    });

    const diff = await screen.findByTestId("timeline-diff");
    expect(within(diff).getByText("Plan")).toBeInTheDocument();
    expect(within(diff).getByText("Standard")).toBeInTheDocument();
    expect(within(diff).getByText("Enhanced")).toBeInTheDocument();
    // A field that had no previous value reads as a dash, not as a missing row.
    expect(within(diff).getByText("Effective date")).toBeInTheDocument();
    expect(within(diff).getByText("2026-08-01")).toBeInTheDocument();
  });

  it("renders the summary alone when the diff is malformed, never raw JSON", async () => {
    // A history panel is not where an operator should discover that an upstream projection changed shape.
    await renderTimeline({ changeDiff: "not json" });
    expect(await screen.findByText("Member moved to another plan")).toBeInTheDocument();
    expect(screen.queryByTestId("timeline-diff")).not.toBeInTheDocument();
  });

  it("keeps saying the detail is withheld when the role may not read it", async () => {
    await renderTimeline({ changeDiff: null, diffWithheld: true });
    expect(await screen.findByText("Change detail withheld for your role.")).toBeInTheDocument();
    expect(screen.queryByTestId("timeline-diff")).not.toBeInTheDocument();
  });

  const renderWithOrigin = async (origin: Record<string, unknown> | null) => {
    const { ChangeTimeline } = await import("../src/screens/PolicyPanels");
    const api = fakeApi({
      timeline: () =>
        Promise.resolve({ entries: [entry({})], nextCursor: "2026-07-30T00:00:00Z", origin } as never),
    });
    renderNode(<ChangeTimeline api={api} scope="enrollments" scopeRef="e1" lang="en" />);
  };

  it("puts the newest change on top and the record's creation at the bottom", async () => {
    await renderWithOrigin(
      entry({
        entryId: "t0", eventType: "MemberEnrolled", eventCategory: "Enrolment",
        occurredAt: "2026-03-01T09:00:00Z", summaryEn: "Member enrolled", actorDisplay: "Mona Adel",
      }),
    );

    // Newest first is the order of the run; the creation is the oldest line there is, so it anchors the end.
    // What the anchor changes is that it is THERE at all — it used to be behind however many pages of history
    // the record had earned, and on a record with no projected enrolment it was not reachable at any depth.
    const items = await screen.findAllByRole("listitem");
    expect(within(items[0]).getByText("Member moved to another plan")).toBeInTheDocument();
    const anchor = items[items.length - 1];
    expect(within(anchor).getByText("Member enrolled")).toBeInTheDocument();
    expect(within(anchor).getByText("Mona Adel", { exact: false })).toBeInTheDocument();
  });

  it("sorts the run newest-first whatever order the page arrived in", async () => {
    const { ChangeTimeline } = await import("../src/screens/PolicyPanels");
    const api = fakeApi({
      timeline: () =>
        Promise.resolve({
          entries: [
            entry({ entryId: "old", occurredAt: "2026-01-02T09:00:00Z", summaryEn: "Older change" }),
            entry({ entryId: "new", occurredAt: "2026-06-02T09:00:00Z", summaryEn: "Newer change" }),
          ],
          nextCursor: null,
        } as never),
    });
    renderNode(<ChangeTimeline api={api} scope="enrollments" scopeRef="e1" lang="en" />);

    const items = await screen.findAllByRole("listitem");
    expect(within(items[0]).getByText("Newer change")).toBeInTheDocument();
    expect(within(items[1]).getByText("Older change")).toBeInTheDocument();
  });

  it("never renders the creation twice when paging reaches it", async () => {
    const origin = entry({ entryId: "t0", eventType: "MemberEnrolled", summaryEn: "Member enrolled" });
    const { ChangeTimeline } = await import("../src/screens/PolicyPanels");
    // A service that leaves the anchor in a later page must not produce two enrolment lines.
    const api = fakeApi({
      timeline: () => Promise.resolve({ entries: [entry({}), origin], nextCursor: null, origin } as never),
    });
    renderNode(<ChangeTimeline api={api} scope="enrollments" scopeRef="e1" lang="en" />);

    expect(await screen.findAllByText("Member enrolled")).toHaveLength(1);
  });

  it("re-reads the history when the record is written to, without a page reload", async () => {
    const { ChangeTimeline } = await import("../src/screens/PolicyPanels");
    const timeline = vi.fn().mockResolvedValue({ entries: [entry({})], nextCursor: null, origin: null });
    const api = fakeApi({ timeline } as never);

    // Stands in for the member screen: the card's actions bump a counter the tabs below it are given.
    function Harness() {
      const [n, setN] = useState(0);
      return (
        <>
          <button type="button" onClick={() => setN((x) => x + 1)}>write</button>
          <ChangeTimeline api={api} scope="enrollments" scopeRef="e1" lang="en" reloadToken={n} />
        </>
      );
    }

    renderNode(<Harness />);
    await screen.findByText("Member moved to another plan");
    expect(timeline).toHaveBeenCalledTimes(1);

    // The tabs stay mounted while the card's actions are used above them, so a plan change left the log
    // showing the history as it was when the tab was opened — and the only way to see your own change was to
    // reload the application.
    await userEvent.click(screen.getByRole("button", { name: "write" }));
    await waitFor(() => expect(timeline).toHaveBeenCalledTimes(2));
  });

  it("offers a refresh, because other people write to this record too", async () => {
    const { ChangeTimeline } = await import("../src/screens/PolicyPanels");
    const timeline = vi.fn().mockResolvedValue({ entries: [entry({})], nextCursor: null, origin: null });
    renderNode(<ChangeTimeline api={fakeApi({ timeline } as never)} scope="enrollments" scopeRef="e1" lang="en" />);
    await screen.findByText("Member moved to another plan");

    await userEvent.click(screen.getByTestId("timeline-refresh"));
    await waitFor(() => expect(timeline).toHaveBeenCalledTimes(2));
  });

  it("says when the creation line was read off the record rather than projected", async () => {
    // A quarter of the dev records have no enrolment event at all. Their history is anchored from the
    // membership row — which is a fact the record holds, and the reader is told which kind of line it is.
    await renderWithOrigin(
      entry({ entryId: "t0", eventType: "MemberEnrolled", summaryEn: "Member enrolled",
        actorDisplay: null, actorUsername: null, derived: true }),
    );

    const anchor = await screen.findByTestId("timeline-origin");
    expect(within(anchor).getByText(/Read from the membership record/)).toBeInTheDocument();
    // No actor is invented for it: the row carries no username to sign it with.
    expect(within(anchor).queryByTestId("timeline-actor")).not.toBeInTheDocument();
  });

  it("renders nothing extra when the service has no origin for the record", async () => {
    await renderWithOrigin(null);
    expect(screen.queryByTestId("timeline-origin")).not.toBeInTheDocument();
    expect(await screen.findByText("Member moved to another plan")).toBeInTheDocument();
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
    // 33.7 — the ISSUER role, not the portal name. This used to seed `provider_admin` and call it "the
    // Network Team", which is the conflation the whole finding turns on: `ROLE_MAP` maps BOTH the issuer's
    // `network_team` (tenant-wide) and its `provider_admin` (one provider's own administrator, T4,
    // provider-scoped) onto the single portal role `provider_admin`. The server's `NetworkAdmin` rule has
    // never named the latter, so the test passed while proving the opposite of its own name.
    seedSession("provider_admin", [], undefined, ["network_team"]);
    renderNode(<NetworkTiers api={fakeApi()} />);

    expect(await screen.findByTestId("tier-create")).toBeInTheDocument();
    expect(screen.queryByTestId("tiers-read-only")).not.toBeInTheDocument();
  });

  it("offers none of it to a provider's own administrator, whom the server refuses", async () => {
    // The counterpart the name above implied and nothing checked. See network-portal.test.tsx for the rest
    // of this pair, and design 52 §5 for why the two roles still share a portal.
    seedSession("provider_admin", [], undefined, ["provider_admin"]);
    renderNode(<NetworkTiers api={fakeApi()} />);

    expect(await screen.findByTestId("tiers-read-only")).toBeInTheDocument();
    expect(screen.queryByTestId("tier-create")).not.toBeInTheDocument();
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

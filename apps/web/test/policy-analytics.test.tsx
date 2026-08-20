import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { PolicyAnalytics } from "../src/screens/PolicyAnalytics";
import { PORTALS } from "../src/portals/catalog";
import { permissionsForRole } from "../src/authz/permissions";
import type { AnalyticsSeries, AnalyticsViewResult, PolicyApi } from "../src/api/policyApi";

/**
 * Phase 19.6b — the dashboard's load-bearing behaviours.
 *
 * Each test is a claim the design makes that a screen could quietly break: the data table is in the DOM with
 * no toggle to find, a scoped total says so, filters survive the URL, a delta chip carries its text cue, and
 * a drill-down goes through the audited endpoint rather than reading identity off the chart.
 */

function series(over: Partial<AnalyticsSeries> = {}): AnalyticsSeries {
  return {
    key: "membership-movement",
    titleEn: "Membership movement",
    titleAr: "حركة العضوية",
    unit: "count",
    points: [
      { key: "joined", labelEn: "Joined", labelAr: "انضم", value: 120 },
      { key: "left", labelEn: "Left", labelAr: "غادر", value: 40 },
    ],
    summaryEn: "Membership movement: 2 series totalling 160; highest is Joined at 120.",
    summaryAr: "حركة العضوية: سلسلتان بإجمالي ١٦٠؛ الأعلى انضم بقيمة ١٢٠.",
    columns: [{ en: "Movement", ar: "الحركة" }, { en: "Members", ar: "الأعضاء" }],
    ...over,
  };
}

function result(over: Partial<AnalyticsViewResult> = {}): AnalyticsViewResult {
  return { view: "Enrolment", series: [series()], deltas: [], payerScopeApplied: false, unavailable: [], ...over };
}

function fakeApi(overrides: Partial<PolicyApi> = {}): PolicyApi {
  return {
    analytics: () => Promise.resolve(result()),
    analyticsOutlierMembers: () => Promise.resolve([]),
    analyticsExport: () => Promise.resolve(""),
    // The filter bar is built from reference data now, not from typed uuids (§5.1), so these are part of
    // the screen's contract rather than incidental.
    payers: () => Promise.resolve([
      { payerId: "pay-1", payerCode: "UNHCR", nameEn: "UNHCR", nameAr: "المفوضية", payerType: "Donor", status: "Active" },
    ]),
    policyQuery: () => Promise.resolve({
      items: [{
        policyId: "pol-1", policyNo: "POL-2026-0001", status: "Active", effectiveFrom: "2026-01-01",
        memberCount: 12, memberCountBand: "Small", planCount: 1, utilizationBand: "Low",
      }],
      page: 1, pageSize: 200, totalCount: 1, totalPages: 1, sortedBy: "policyno",
    }),
    networkTiers: () => Promise.resolve([
      { networkTierId: "t-1", tierCode: "TIER1", nameEn: "Tier 1", nameAr: "الشريحة ١", rank: 1, isOutOfNetwork: false, status: "Active" },
    ]),
    benefitCategories: () => Promise.resolve([{ benefitCategoryId: "bc-1", code: "OP", name: "Outpatient" }]),
    policyPlans: () => Promise.resolve([
      { policyPlanId: "pp-1", policyId: "pol-1", planVersionId: "pv-1", planLabel: "Bronze v1",
        effectiveFrom: "2026-01-01", isDefault: true, status: "Active", memberCount: 12 },
    ]),
    policyGroups: () => Promise.resolve([
      { groupId: "g-1", policyId: "pol-1", groupCode: "G1", nameEn: "Cairo", nameAr: "القاهرة",
        groupType: "Branch", effectiveFrom: "2026-01-01", status: "Active" },
    ]),
    ...overrides,
  } as unknown as PolicyApi;
}

function renderDashboard(api: PolicyApi, url = "/policy/analytics") {
  return render(
    <AppProviders authClient={new DevAuthClient()}>
      <MemoryRouter initialEntries={[url]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <PolicyAnalytics api={api} />
      </MemoryRouter>
    </AppProviders>,
  );
}

// ── The accessible alternative ──────────────────────────────────────────────────────────────────────────

describe("Every chart carries its data table", () => {
  it("renders the table and the summary unconditionally, with no toggle to find", async () => {
    renderDashboard(fakeApi());

    const card = await screen.findByTestId("series-membership-movement");
    // U6: the table is the CONTENT, not an alternative hidden behind a control somebody has to discover.
    const table = within(card).getByRole("table");
    expect(within(table).getByRole("columnheader", { name: "Movement" })).toBeInTheDocument();
    expect(within(table).getByRole("rowheader", { name: "Joined" })).toBeInTheDocument();
    expect(within(table).getByText("120")).toBeInTheDocument();

    // The one-line summary is the server's, composed from the plotted data — a caption written client-side
    // drifts from the chart the first time a series changes shape.
    expect(within(card).getByText(/2 series totalling 160/)).toBeInTheDocument();

    // No control that could leave it switched off.
    expect(within(card).queryByRole("button", { name: /show|table|data/i })).not.toBeInTheDocument();
  });

  it("heads the Arabic table in Arabic, not in English", async () => {
    localStorage.setItem("mersal-lang", "ar");
    try {
      renderDashboard(fakeApi());
      const card = await screen.findByTestId("series-membership-movement");
      const table = within(card).getByRole("table");

      // §3.1: `columns` was one monolingual array, so the accessible table — the element that exists FOR the
      // reader who cannot see the bars — was the only English text left on an Arabic page, and it was the
      // part that names what each number IS.
      expect(within(table).getByRole("columnheader", { name: "الحركة" })).toBeInTheDocument();
      expect(within(table).getByRole("columnheader", { name: "الأعضاء" })).toBeInTheDocument();
      expect(within(table).queryByRole("columnheader", { name: "Movement" })).not.toBeInTheDocument();
    } finally {
      localStorage.removeItem("mersal-lang");
    }
  });

  it("prints every figure in one numeral system", async () => {
    localStorage.setItem("mersal-lang", "ar");
    try {
      renderDashboard(fakeApi());
      const card = await screen.findByTestId("series-membership-movement");

      // §5.8: counts went through `String(value)` and percentages through a template literal, both of which
      // are always Latin, while the currency column resolved ar-EG and printed Arabic-Indic. One card, two
      // numeral systems, for the same kind of quantity.
      expect(within(card).getAllByText("١٢٠").length).toBeGreaterThan(0);
      expect(within(card).queryByText("120")).not.toBeInTheDocument();
    } finally {
      localStorage.removeItem("mersal-lang");
    }
  });

  it("hides the decorative bars from assistive tech rather than duplicating them", async () => {
    const { container } = renderDashboard(fakeApi());
    await screen.findByTestId("series-membership-movement");

    const bars = container.querySelector(".pol-bars");
    // The bars carry nothing the table lacks, so they are hidden — a screen reader that read both would hear
    // every figure twice and have no way to tell which was authoritative.
    expect(bars).toHaveAttribute("aria-hidden", "true");
  });
});

// ── Payer scope ─────────────────────────────────────────────────────────────────────────────────────────

describe("A scoped total says that it is scoped", () => {
  it("states the narrowing rather than letting a small number read as the whole programme", async () => {
    renderDashboard(fakeApi({ analytics: () => Promise.resolve(result({ payerScopeApplied: true })) }));

    const notice = await screen.findByTestId("payer-scoped");
    expect(notice).toHaveTextContent(/only the payers you are assigned to/);
  });

  it("says nothing when the caller is unrestricted", async () => {
    renderDashboard(fakeApi());
    await screen.findByTestId("series-membership-movement");
    expect(screen.queryByTestId("payer-scoped")).not.toBeInTheDocument();
  });
});

// ── Filters live in the URL ─────────────────────────────────────────────────────────────────────────────

describe("The filter bar is the URL", () => {
  it("passes the filters from the address straight to the query", async () => {
    const analytics = vi.fn().mockResolvedValue(result());
    renderDashboard(fakeApi({ analytics }), "/policy/analytics?payerId=p-1&from=2026-03-01&to=2026-03-31&band=High");

    await screen.findByTestId("series-membership-movement");
    // A shared link has to reproduce the number it was shared about; that is the whole reason the filters are
    // in the address rather than in component state.
    await waitFor(() =>
      expect(analytics).toHaveBeenCalledWith("enrolment", expect.objectContaining({
        payerId: "p-1", from: "2026-03-01", to: "2026-03-31", band: "High",
      })),
    );
  });

  it("clears every filter key in one action", async () => {
    const analytics = vi.fn().mockResolvedValue(result());
    renderDashboard(fakeApi({ analytics }), "/policy/analytics?payerId=p-1&tier=TIER-B&category=LAB");

    await screen.findByTestId("series-membership-movement");
    await userEvent.click(screen.getByRole("button", { name: "Clear filters" }));

    // A clear that missed a key would leave an invisible narrowing applied to every subsequent view.
    await waitFor(() => {
      const calls = analytics.mock.calls;
      const last = calls[calls.length - 1][1] as Record<string, string>;
      expect(last.payerId).toBeUndefined();
      expect(last.tier).toBeUndefined();
      expect(last.category).toBeUndefined();
    });
  });
});

// ── The filters are usable ──────────────────────────────────────────────────────────────────────────────

describe("Narrowing the dashboard does not require knowing a uuid", () => {
  it("offers the payers, tiers and categories as pickers rather than text boxes", async () => {
    renderDashboard(fakeApi());
    await screen.findByTestId("series-membership-movement");

    // §5.1 — these were `<InputField type="text">` over uuid and enum-token columns, so the only way to use
    // the dashboard's own narrowing was to already know the value you were filtering to.
    for (const name of [/payer/i, /network tier/i, /benefit category/i, /member status/i, /utilization band/i]) {
      expect(await screen.findByRole("combobox", { name })).toBeInTheDocument();
    }
    expect(screen.queryByRole("textbox", { name: /payer/i })).not.toBeInTheDocument();
  });

  it("writes the chosen id to the URL, so the picker and the shared link agree", async () => {
    const analytics = vi.fn().mockResolvedValue(result());
    renderDashboard(fakeApi({ analytics }));
    await screen.findByTestId("series-membership-movement");

    await userEvent.click(await screen.findByRole("combobox", { name: /payer/i }));
    await userEvent.click(await screen.findByRole("option", { name: "UNHCR" }));

    // The label is for the human; the id is what the query and the shared address carry.
    await waitFor(() =>
      expect(analytics).toHaveBeenLastCalledWith("enrolment", expect.objectContaining({ payerId: "pay-1" })),
    );
  });

  it("will not offer a plan or a group until a policy is chosen, and says why", async () => {
    renderDashboard(fakeApi());
    await screen.findByTestId("series-membership-movement");

    // Both hang off a policy. Enabled-and-empty is a control that can only disappoint; disabled with the
    // reason on it explains the order of the bar instead.
    expect(await screen.findByRole("combobox", { name: /^plan/i })).toBeDisabled();
    expect(screen.getAllByText(/choose a policy first/i).length).toBeGreaterThan(0);
  });

  it("loads the plans of the policy in the address", async () => {
    renderDashboard(fakeApi(), "/policy/analytics?policyId=pol-1");
    await screen.findByTestId("series-membership-movement");

    const plan = await screen.findByRole("combobox", { name: /^plan/i });
    expect(plan).not.toBeDisabled();
    await userEvent.click(plan);
    expect(await screen.findByRole("option", { name: "Bronze v1" })).toBeInTheDocument();
  });

  it("says so when a reference list could not be read, instead of an empty picker", async () => {
    renderDashboard(fakeApi({ payers: () => Promise.reject(new Error("boom")) } as Partial<PolicyApi>));
    await screen.findByTestId("series-membership-movement");

    // An empty picker reads as "there are no payers", which is a statement about the data rather than about
    // the request that failed to make it.
    expect(await screen.findByText(/some filter lists could not be loaded/i)).toBeInTheDocument();
  });
});

// ── Compare mode ────────────────────────────────────────────────────────────────────────────────────────

describe("Delta chips carry a text cue, not a colour", () => {
  it("renders the direction as a word and distinguishes good from bad movement", async () => {
    renderDashboard(
      fakeApi({
        analytics: () =>
          Promise.resolve(
            result({
              deltas: [
                { key: "m.joined", labelEn: "Joined", labelAr: "انضم", current: 120, previous: 100, percentChange: 20, direction: "Up", better: true },
                { key: "m.left", labelEn: "Left", labelAr: "غادر", current: 40, previous: 25, percentChange: 60, direction: "Up", better: false },
              ],
            }),
          ),
      }),
    );

    const strip = await screen.findByTestId("delta-strip");
    // Both moved UP. Only the server knows that one of those is good news, so the word and the hue disagree
    // on purpose — a strip that coloured both the same way would have said nothing.
    expect(within(strip).getByText("Up 20%")).toBeInTheDocument();
    expect(within(strip).getByText("Up 60%")).toBeInTheDocument();
    expect(within(strip).getByText("Joined")).toBeInTheDocument();
    expect(within(strip).getByText("Left")).toBeInTheDocument();
  });
});

// ── Drill-down ──────────────────────────────────────────────────────────────────────────────────────────

describe("Drill-down goes through the audited endpoint", () => {
  it("asks the server for the member rows instead of reading identity off the chart", async () => {
    const analyticsOutlierMembers = vi.fn().mockResolvedValue([
      { enrollmentId: "e-11111111", beneficiaryId: "b-1", policyId: "p-1", policyPlanId: "pp-1", limit: 1000, consumed: 1200, band: "Exhausted" },
    ]);
    renderDashboard(
      fakeApi({
        analytics: () =>
          Promise.resolve(
            result({
              view: "Outliers",
              series: [series({
                key: "limit-outliers",
                titleEn: "Members at the edge of their entitlement",
                points: [{ key: "over-limit", labelEn: "Over the limit", labelAr: "تجاوزوا الحد", value: 14 }],
                columns: [{ en: "Outlier", ar: "القيمة الشاذة" }, { en: "Members", ar: "الأعضاء" }],
              })],
            }),
          ),
        analyticsOutlierMembers,
      }),
      "/policy/analytics",
    );

    // The outliers tab is where a total becomes a list of specific people.
    await userEvent.click(await screen.findByRole("tab", { name: "Outliers & data quality" }));
    await userEvent.click(await screen.findByRole("button", { name: "Over the limit" }));

    const panel = await screen.findByTestId("drill-panel");
    expect(analyticsOutlierMembers).toHaveBeenCalledWith("Exhausted", expect.anything(), 50);
    // Ids and figures. No name came back, because resolving one is a separate, audited call.
    expect(within(panel).getByText("e-111111")).toBeInTheDocument();
    expect(within(panel).getByText(/Member numbers are resolved separately/)).toBeInTheDocument();
  });
});

// ── Structural min-necessary ────────────────────────────────────────────────────────────────────────────

describe("The dashboard's reach is structural", () => {
  it("gives analytics to the three roles that administer benefit, and to no clinical portal", () => {
    const withAnalytics = PORTALS.filter((p) => p.sections.some((s) => s.key === "analytics")).map((p) => p.role);
    expect(withAnalytics).toEqual(expect.arrayContaining(["beneficiary_mgmt", "finance", "policy_admin"]));

    // A doctor's portal has no analytics section at all — not a hidden one. The absence is the control.
    for (const role of ["doctor", "nurse", "lab", "pharmacy", "reception"] as const) {
      const portal = PORTALS.find((p) => p.role === role)!;
      expect(portal.sections.some((s) => s.key === "analytics")).toBe(false);
    }
  });

  it("keeps the analytics permission separate from utilization", () => {
    // Granting "show me this policy's consumption" must not silently grant "aggregate the whole book".
    const claims = permissionsForRole("claims_officer");
    expect(claims).not.toContain("policy.analytics");
    expect(permissionsForRole("policy_admin")).toContain("policy.analytics");
  });
});

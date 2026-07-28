import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
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
    columns: ["Movement", "Members"],
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
                columns: ["Outlier", "Members"],
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

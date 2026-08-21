import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import type { NetworkTierView, PolicyApi, TierAssignmentView } from "../src/api/policyApi";
import { NetworkPerformance, NetworkDirectory } from "../src/screens/NetworkPortal";
import { NetworkTiers } from "../src/screens/NetworkTierAdmin";
import { renderNode, seedSession } from "./helpers";

/**
 * Phase 33.7 — the Network Team's portal, which had no tests at all.
 *
 * ============================================================================================================
 * WHAT THESE PROVE
 * ============================================================================================================
 * Two defects, and the second explains why the first survived.
 *
 * **Performance was four numbers counted in the browser.** The screen fetched the provider DIRECTORY and
 * counted rows whose `status.label.en` equalled "Active" — a tally of a rendering label, over a projection,
 * computed past the 403 `provider-service` gives a provider-scoped caller for exactly this figure. The
 * endpoint that returns these four numbers has existed since phase 2b and had no Kong route, and the
 * route-coverage guard that exists to catch an unrouted resource had "metrics" in its ignore list.
 *
 * **A tier assignment could be revoked and never created.** `assignTier` was implemented in `policyApi` and
 * called by nothing, so this screen removed rows from a table nothing could fill — while its own revoke
 * dialog promised "the assignment can be re-created".
 */

afterEach(cleanup);

const TIER: NetworkTierView = {
  networkTierId: "NT-1", tierCode: "TIER-A", nameEn: "Preferred", nameAr: "مفضّل",
  rank: 1, description: null, isOutOfNetwork: false, status: "Active",
};

const RETIRED: NetworkTierView = { ...TIER, networkTierId: "NT-9", tierCode: "TIER-OLD", status: "Retired" };

const ASSIGNMENT: TierAssignmentView = {
  assignmentId: "TA-1", networkTierId: "NT-1", tierCode: "TIER-A", providerId: "PRV-1",
  scope: "Provider", scopeRef: "PRV-1", effectiveFrom: "2026-01-01", effectiveTo: null, status: "Active",
};

const reject = () => Promise.reject(new Error("not stubbed for this test"));

function tierApi(over: Partial<PolicyApi> = {}): PolicyApi {
  return {
    networkTiers: () => Promise.resolve([TIER, RETIRED]),
    createTier: reject,
    updateTier: reject,
    tierAssignments: () => Promise.resolve([ASSIGNMENT]),
    tierProviders: () => Promise.resolve([
      { providerId: "PRV-1", providerCode: "PRV-0001", legalName: "Nile Central Hospital" },
      { providerId: "PRV-2", providerCode: "PRV-0002", legalName: "Cairo Care Clinic" },
    ]),
    assignTier: reject,
    revokeAssignment: () => Promise.resolve(),
    resolveTier: reject,
    ...over,
  } as unknown as PolicyApi;
}

// ── Performance ───────────────────────────────────────────────────────────────────────────────────────────

describe("the network roll-up comes from the service that owns it", () => {
  it("asks provider-service rather than counting the directory", async () => {
    seedSession("provider_admin", [], undefined, ["network_team"]);
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const metrics = vi.fn().mockResolvedValue({ total: 41, active: 33, suspended: 6, terminated: 2 });
    const list = vi.fn().mockResolvedValue([]);
    (api as { networkMetrics: unknown }).networkMetrics = metrics;
    (api as { providerList: unknown }).providerList = list;

    renderNode(<NetworkPerformance />, api);

    expect(await screen.findByText("41")).toBeInTheDocument();
    expect(metrics).toHaveBeenCalled();
    // The point of the change: the directory is not the source of this figure and is not fetched for it.
    expect(list).not.toHaveBeenCalled();
  });

  it("does not fall to zero when a status label it does not recognise arrives", async () => {
    seedSession("provider_admin", [], undefined, ["network_team"]);
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    // The old implementation counted `status.label.en === "Active"`. A provider whose status renders as
    // anything else — a relabelling, a new state, an Arabic session — silently stopped counting, and four
    // zeroes look exactly like a small network rather than like a broken screen.
    (api as { providerList: unknown }).providerList = vi.fn().mockResolvedValue([
      { id: "P1", code: "P-1", legalName: "A", providerType: "Clinic", status: { kind: "ok", label: { en: "In network", ar: "ضمن الشبكة" } }, onboardingState: "Activated" },
    ]);
    renderNode(<NetworkPerformance />, api);

    // The service counts the ProviderStatus enum, so the label has no say in it.
    expect(await screen.findByText("2")).toBeInTheDocument();
  });

  it("has no axe violations", async () => {
    seedSession("provider_admin", [], undefined, ["network_team"]);
    const { container } = renderNode(<NetworkPerformance />, new DevApiClient({ latencyMs: 0 }));
    await screen.findByText("3");
    expect(await axe(container)).toHaveNoViolations();
  });
});

describe("two roles share this portal and only one owns the network", () => {
  it("does not show the roll-up to a provider's own administrator", async () => {
    // `ROLE_MAP` maps the issuer's `network_team` AND its `provider_admin` onto one portal role, and
    // provider-service answers this endpoint 403 for the second — a provider must not learn the shape of the
    // network it competes in. The client-side mirror could not see the difference because the portal name
    // had already erased it.
    seedSession("provider_admin", [], undefined, ["provider_admin"]);
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    const metrics = vi.fn();
    (api as { networkMetrics: unknown }).networkMetrics = metrics;

    renderNode(<NetworkPerformance />, api);

    expect(await screen.findByText(/belongs to Mersal's Network Team/)).toBeInTheDocument();
    // Not fetched-and-refused: not fetched. A 403 rendered as an error reads as a broken screen.
    expect(metrics).not.toHaveBeenCalled();
  });

  it("offers no tier write to a provider's own administrator", async () => {
    const user = userEvent.setup();
    // The server's `NetworkAdmin` rule names network_team, org_admin and super_admin, and has never named
    // provider_admin. `mayAdministerTiers` compared the PORTAL name against it and answered yes for both, so
    // this caller was shown Create tier and Revoke — each refused with urn:hbmp:network-tier-access-denied.
    seedSession("provider_admin", [], undefined, ["provider_admin"]);
    renderNode(<NetworkTiers api={tierApi()} />, new DevApiClient({ latencyMs: 0 }));

    await user.click(await screen.findByText("TIER-A"));
    expect(screen.queryByTestId("tier-create")).toBeNull();
    expect(screen.queryByTestId("tier-assign")).toBeNull();
    expect(screen.queryByTestId("tier-edit")).toBeNull();
    expect(screen.queryByRole("button", { name: "Revoke" })).toBeNull();
  });

  it("keeps every tier write for the Network Team, who do own it", async () => {
    const user = userEvent.setup();
    seedSession("provider_admin", [], undefined, ["network_team"]);
    renderNode(<NetworkTiers api={tierApi()} />, new DevApiClient({ latencyMs: 0 }));

    await user.click(await screen.findByText("TIER-A"));
    // The fix removes the affordance from the caller who cannot use it, not the feature.
    expect(screen.getByTestId("tier-create")).toBeInTheDocument();
    expect(screen.getByTestId("tier-assign")).toBeInTheDocument();
  });
});

describe("the directory", () => {
  it("has no axe violations", async () => {
    seedSession("provider_admin");
    const { container } = renderNode(<NetworkDirectory />, new DevApiClient({ latencyMs: 0 }));
    await screen.findByText("Nile Central Hospital");
    expect(await axe(container)).toHaveNoViolations();
  });
});

// ── Tier assignment ───────────────────────────────────────────────────────────────────────────────────────

describe("a tier assignment can be created, not only revoked", () => {
  it("posts the assignment the revoke dialog promised could be re-created", async () => {
    const user = userEvent.setup();
    seedSession("provider_admin", [], undefined, ["network_team"]);
    const assignTier = vi.fn().mockResolvedValue(ASSIGNMENT);
    renderNode(<NetworkTiers api={tierApi({ assignTier })} />, new DevApiClient({ latencyMs: 0 }));

    await user.click(await screen.findByText("TIER-A"));
    const panel = await screen.findByTestId("tier-assign");

    await user.click(within(panel).getByLabelText("Provider"));
    await user.click(await screen.findByText(/Nile Central Hospital/));
    await user.click(within(panel).getByRole("button", { name: "Assign to this tier" }));

    await waitFor(() => expect(assignTier).toHaveBeenCalled());
    const [tierId, body] = assignTier.mock.calls[0];
    expect(tierId).toBe("NT-1");
    // A provider-wide assignment's reference IS the provider — there is nothing else it could be, and asking
    // the operator to restate it is how the two get out of step.
    expect(body).toMatchObject({ scope: "Provider", scopeRef: "PRV-1" });
  });

  it("will not assign without a provider, and says which field it wants", async () => {
    const user = userEvent.setup();
    seedSession("provider_admin", [], undefined, ["network_team"]);
    const assignTier = vi.fn();
    renderNode(<NetworkTiers api={tierApi({ assignTier })} />, new DevApiClient({ latencyMs: 0 }));

    await user.click(await screen.findByText("TIER-A"));
    const panel = await screen.findByTestId("tier-assign");
    await user.click(within(panel).getByRole("button", { name: "Assign to this tier" }));

    expect(await screen.findByText("Choose a provider.")).toBeInTheDocument();
    expect(assignTier).not.toHaveBeenCalled();
  });

  it("asks for the reference a location assignment needs and refuses without it", async () => {
    const user = userEvent.setup();
    seedSession("provider_admin", [], undefined, ["network_team"]);
    const assignTier = vi.fn();
    renderNode(<NetworkTiers api={tierApi({ assignTier })} />, new DevApiClient({ latencyMs: 0 }));

    await user.click(await screen.findByText("TIER-A"));
    const panel = await screen.findByTestId("tier-assign");
    await user.click(within(panel).getByLabelText("Provider"));
    await user.click(await screen.findByText(/Nile Central Hospital/));
    await user.click(within(panel).getByLabelText("Applies to"));
    await user.click(await screen.findByText("One location"));
    await user.click(within(panel).getByRole("button", { name: "Assign to this tier" }));

    expect(await screen.findByText(/needs the id it applies to/)).toBeInTheDocument();
    expect(assignTier).not.toHaveBeenCalled();
  });

  it("says why a retired tier takes no assignment instead of letting the server refuse it", async () => {
    const user = userEvent.setup();
    seedSession("provider_admin", [], undefined, ["network_team"]);
    renderNode(<NetworkTiers api={tierApi()} />, new DevApiClient({ latencyMs: 0 }));

    await user.click(await screen.findByText("TIER-OLD"));
    const panel = await screen.findByTestId("tier-assign");
    // Retired tiers stay LISTED on purpose — claims priced against them must still render — so the form has
    // to explain itself rather than simply vanishing or 409-ing after the operator has filled it in.
    expect(within(panel).getByText(/retired tier takes no new assignments/i)).toBeInTheDocument();
    expect(within(panel).queryByRole("button", { name: "Assign to this tier" })).toBeNull();
  });

  it("offers nothing to a policy administrator, who prices at a tier and does not set them", async () => {
    const user = userEvent.setup();
    seedSession("policy_admin");
    renderNode(<NetworkTiers api={tierApi()} />, new DevApiClient({ latencyMs: 0 }));

    await user.click(await screen.findByText("TIER-A"));
    // Mirrors provider-service's NetworkTierGate. Absent rather than present-and-refused.
    expect(screen.queryByTestId("tier-assign")).toBeNull();
    expect(screen.queryByTestId("tier-edit")).toBeNull();
    expect(screen.getByTestId("tiers-read-only")).toBeInTheDocument();
  });
});

describe("a tier's name can be corrected", () => {
  it("saves a rename without retiring and re-creating the tier", async () => {
    const user = userEvent.setup();
    seedSession("provider_admin", [], undefined, ["network_team"]);
    const updateTier = vi.fn().mockResolvedValue(TIER);
    renderNode(<NetworkTiers api={tierApi({ updateTier })} />, new DevApiClient({ latencyMs: 0 }));

    await user.click(await screen.findByText("TIER-A"));
    const panel = await screen.findByTestId("tier-edit");
    const field = within(panel).getByLabelText("Name (English)");
    await user.clear(field);
    await user.type(field, "Preferred network");
    await user.click(within(panel).getByRole("button", { name: "Save" }));

    await waitFor(() => expect(updateTier).toHaveBeenCalledWith("NT-1", expect.objectContaining({
      nameEn: "Preferred network",
    })));
  });

  it("re-seeds when a different tier is selected, so a save cannot rename the wrong one", async () => {
    const user = userEvent.setup();
    seedSession("provider_admin", [], undefined, ["network_team"]);
    renderNode(<NetworkTiers api={tierApi()} />, new DevApiClient({ latencyMs: 0 }));

    await user.click(await screen.findByText("TIER-A"));
    const field = () => within(screen.getByTestId("tier-edit")).getByLabelText<HTMLInputElement>("Name (English)");
    await user.clear(field());
    await user.type(field(), "Edited but not saved");

    await user.click(screen.getByText("TIER-OLD"));
    // Without the re-seed the abandoned edit would still be in the box, and pressing Save would give the
    // newly-selected tier the previous one's name.
    await waitFor(() => expect(field().value).toBe("Preferred"));
  });
});

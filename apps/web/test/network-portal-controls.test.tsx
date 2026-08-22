import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode, seedSession } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import { NetworkDirectory, NetworkOnboarding } from "../src/screens/NetworkPortal";
import type { ProviderSummary } from "@mersal/contracts";

/**
 * Phase 19.9 — the network portal can administer the network, not only list it (design 58).
 *
 * <b>The defect.</b> Five sections offered one write between them: create a provider. Activate, suspend,
 * terminate, add a location, add a contract, price a service, record a credential and provision a user all
 * had endpoints — most with careful guards and audit events — and not one had a button. A provider went live
 * because somebody ran curl.
 *
 * What is asserted here is the part a screenshot cannot show: that the controls exist, that the ones a role
 * may not use are ABSENT rather than disabled, and that the two forms which used to accept free text now
 * offer the vocabulary the server validates against.
 */

const SRC = resolve(__dirname, "../src");
const read = (rel: string) => readFileSync(join(SRC, rel), "utf8");

function provider(n: number, over: Partial<ProviderSummary> = {}): ProviderSummary {
  return {
    id: `p-${n}`,
    code: `PRV-${String(n).padStart(3, "0")}`,
    legalName: `Provider ${n}`,
    providerType: "Clinic",
    status: { kind: "ok", label: { en: "Active", ar: "نشط" } },
    onboardingState: "Activated",
    ...over,
  } as ProviderSummary;
}

function withProviders(rows: ProviderSummary[]) {
  class Api extends DevApiClient {
    override providerList() { return Promise.resolve(rows); }
  }
  return new Api();
}

describe("the provider type is chosen, not typed", () => {
  it("offers the onboarding form a picker rather than a free-text box", async () => {
    // The old field was an `InputField` validated against an enum the operator could not see: typing
    // "hospital" in lower case failed with "unknown provider_type" and there was no way to learn the
    // spelling. A picker cannot produce a value the server rejects for being misspelt.
    const user = userEvent.setup();
    // The ISSUER role, not the portal name: `network_team` and `provider_admin` share this portal, and only
    // the first may onboard. This is the one distinction the portal key cannot carry (see `seedSession`).
    seedSession("provider_admin", [], undefined, ["network_team"]);
    renderNode(<NetworkOnboarding />, withProviders([provider(1, { onboardingState: "Draft" })]));

    await user.click(await screen.findByRole("button", { name: /onboard a provider/i }));
    const dialog = within(await screen.findByRole("dialog"));

    // The type control is a combobox; the two identity fields stay text inputs, which is right.
    expect(dialog.getByRole("combobox", { name: /type/i })).toBeInTheDocument();
    expect(dialog.getByLabelText(/provider code/i)).toBeInTheDocument();
    expect(dialog.getByLabelText(/legal name/i)).toBeInTheDocument();
  });

  it("keeps both spellings of the imaging type on offer", () => {
    // 29.1 runs Imaging → Radiology as expand/backfill/contract, and the contract step is deferred. A
    // provider onboarded before the switch still carries the old spelling and has to be editable without
    // being silently retyped, so BOTH are offered until 0013 lands.
    const src = read("screens/NetworkAdminShared.tsx");
    expect(src).toMatch(/PROVIDER_TYPES[\s\S]*"Radiology"[\s\S]*"Imaging"/);
  });
});

describe("what the role may not do is absent, not disabled", () => {
  it("renders no write control for a caller who is not the Network Team", async () => {
    // Two roles share this portal: Mersal's Network Team, and a contracted provider's OWN administrator,
    // who holds `provider:write` and is RLS-bound to their own row. `provider:admin` is what separates
    // "correct your own address" from "edit the contract Mersal signed with you". A disabled button teaches
    // an operator the screen is broken; an absent one teaches them whose job it is.
    renderNode(<NetworkOnboarding />, withProviders([provider(1, { onboardingState: "Draft" })]));
    await screen.findByText("Provider 1");

    // The dev session is not the Network Team, so the create control is not on the page at all.
    expect(screen.queryByRole("button", { name: /onboard a provider/i })).not.toBeInTheDocument();
    expect(screen.getByText(/Mersal's Network Team's to do/i)).toBeInTheDocument();
  });
});

describe("the directory opens a provider", () => {
  it("selects a row rather than only listing it", async () => {
    // The directory listed the network and could not open one of them. Selecting is now the only navigation
    // — no separate route, so there is no deep link this screen promises and then breaks.
    const user = userEvent.setup();
    renderNode(<NetworkDirectory />, withProviders([provider(1), provider(2)]));
    await screen.findByText("Provider 1");

    expect(screen.getByText(/select a provider to see its record/i)).toBeInTheDocument();
    await user.click(screen.getByText("Provider 1"));
    expect(screen.queryByText(/select a provider to see its record/i)).not.toBeInTheDocument();
  });
});

describe("every status change carries a reason", () => {
  it("routes all four provider moves through the shared reason dialog", () => {
    // `ReasonDialog` holds the ten-character bar the server holds, and stays OPEN when a write is refused so
    // the RFC 7807 detail is rendered instead of the typed reason being thrown away with the dialog. A
    // screen that built its own confirm would drift from both.
    const src = read("screens/NetworkPortal.tsx");
    for (const call of ["activateProvider", "suspendProvider", "terminateProvider", "withdrawTermination"]) {
      expect(src, `${call} must go through ReasonDialog`).toContain(`networkApi.${call}(`);
    }
    expect(src).toContain("ReasonDialog");
    expect(src).not.toMatch(/window\.confirm/);
  });

  it("asks the second approver a different question from the first requester", () => {
    // Termination is dual-controlled: the FIRST call opens a request and changes nothing, the second — from
    // a different token — ends the relationship. Presenting both as "Terminate?" is how somebody clicks
    // through the irreversible one believing they are only asking.
    const src = read("screens/NetworkPortal.tsx");
    expect(src).toContain("approveTerminationTitle");
    expect(src).toMatch(/approving\s*=\s*move === "terminate" && Boolean\(detail\.pendingTermination\)/);
  });
});

describe("a withheld price is absent, never zero", () => {
  it("renders the restriction rather than a number when the caller may not see it", () => {
    // `agreedPrice` is withheld as the WHOLE field from a caller without `provider:finance`. Formatting an
    // absent value would print "EGP 0.00" — free, rather than withheld, which is a different and much worse
    // claim to make about a hospital's tariff.
    const src = read("screens/NetworkContractsSection.tsx");
    expect(src).toMatch(/agreedPrice === null \|\| l\.agreedPrice === undefined/);
    expect(src).toContain("priceWithheld");
  });
});

describe("the readiness checklist is the server's answer", () => {
  it("renders the guard's own blocking sentence rather than composing one", () => {
    // Four conditions gate activation and the endpoint used to answer with the FIRST that failed, as a
    // sentence, after the operator pressed the button. The checklist shows all four early — but the wording
    // of the refusal stays the server's, so the two can never disagree.
    const src = read("screens/NetworkAdminShared.tsx");
    expect(src).toContain("readiness.blockingReason");
    expect(src).toMatch(/hasPrimaryLocation[\s\S]*hasMandatoryCredentials[\s\S]*mandatoryCredentialsValid[\s\S]*hasActiveContract/);
  });
});

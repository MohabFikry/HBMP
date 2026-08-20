import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * 32.6 (C1) — the reception desk asks eligibility-service.
 *
 * <p>`checkEligibility` used to make NO network call. It read the reception search cache, compared
 * `identity.status` to the string "active", and returned that as the verdict. Everything eligibility-service
 * exists to apply — the network tier, the plan version in force on the service date, the waiting period, the
 * remaining limits — was absent from what a beneficiary was told at the desk, and so was the audit event:
 * "who checked this person's eligibility, and what were they told?" had no answer, because as far as the
 * platform was concerned nobody had checked anything.</p>
 *
 * <p>These tests assert on the REQUESTS. A test that only checked the returned shape would have passed
 * against the browser-side verdict too — that is exactly how this survived.</p>
 */
function stubSequence(bodies: unknown[]) {
  let i = 0;
  const fetchMock = vi.fn().mockImplementation(async () => ({
    ok: true,
    status: 200,
    headers: new Headers({ "content-type": "application/json" }),
    json: async () => bodies[Math.min(i++, bodies.length - 1)],
  }));
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

const membershipOk = {
  decision: "Eligible",
  decisionScope: "Membership",
  reasons: ["member status is Active", "no benefit category was named"],
  costShare: { determinate: false, reason: "No benefit category was named, so no cost share could be quoted." },
};

const benefitOk = {
  decision: "Eligible",
  decisionScope: "Benefit",
  reasons: ["active coverage for LAB"],
  costShare: { determinate: true, tierCode: "IN", copayPercent: 10, copayFixed: null, coinsurancePercent: null },
};

function checks(fetchMock: ReturnType<typeof vi.fn>) {
  return fetchMock.mock.calls
    .filter((c) => String(c[0]).includes("/eligibility/check"))
    .map((c) => JSON.parse(String((c[1] as RequestInit).body)));
}

afterEach(() => vi.unstubAllGlobals());

describe("the desk asks the service that owns the rules", () => {
  it("posts an eligibility check even when no category is named", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = stubSequence([membershipOk]);

    const res = await new HttpApiClient().checkEligibility("ben-1");

    // THE assertion. This call did not happen at all before.
    expect(checks(fetchMock)).toHaveLength(1);
    expect(checks(fetchMock)[0]).toEqual({ beneficiaryId: "ben-1" });
    expect(res.scope).toBe("membership");
    expect(res.verdict).toBe("eligible");
  });

  it("says why there is no copay rather than leaving the row out", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    stubSequence([membershipOk]);

    const res = await new HttpApiClient().checkEligibility("ben-1");

    // An absent copay and a copay of zero look identical in a nullable field and mean opposite things at a
    // desk with a beneficiary in front of it.
    expect(res.costShare.known).toBe(false);
    if (!res.costShare.known) expect(res.costShare.why.ar).not.toBe("");
  });

  it("asks the benefit question too when a category is named, and quotes what comes back", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = stubSequence([membershipOk, benefitOk]);

    const res = await new HttpApiClient().checkEligibility("ben-1", "LAB");

    // TWO questions, each asked of the service. The membership one gives the visit gate; the benefit one
    // gives cover and cost share. Collapsing them would turn a NeedsAuthorization benefit — a soft No that
    // routes to approvals — into a person turned away at the door.
    expect(checks(fetchMock)).toEqual([
      { beneficiaryId: "ben-1" },
      { beneficiaryId: "ben-1", benefitCategory: "LAB" },
    ]);
    expect(res.scope).toBe("benefit");
    expect(res.benefitCategory).toBe("LAB");
    expect(res.costShare).toMatchObject({ known: true, copayPercent: 10, tierCode: "IN" });
  });

  it("keeps the door open when the benefit needs authorisation", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    stubSequence([
      membershipOk,
      { decision: "NeedsAuthorization", decisionScope: "Benefit", reasons: ["Annual limit reached"], costShare: null },
    ]);

    const res = await new HttpApiClient().checkEligibility("ben-1", "LAB");

    expect(res.verdict).toBe("review");
    // The visit gate is a question about STANDING, and the membership answer said Eligible.
    expect(res.visitGate.allowed).toBe(true);
  });

  it("does not read a decision it cannot parse as permission to proceed", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    stubSequence([{ decision: "SomethingNew", decisionScope: "Membership", reasons: [], costShare: null }]);

    const res = await new HttpApiClient().checkEligibility("ben-1");

    expect(res.verdict).toBe("review");
    expect(res.visitGate.allowed).toBe(false);
  });
});

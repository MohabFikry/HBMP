import { afterEach, describe, expect, it, vi } from "vitest";
import { HTTP_BRANCH_APIS } from "../src/api/branchApi";
import { createHttpPolicyApi } from "../src/api/policyApi";
import { HttpApiClient } from "../src/api/HttpApiClient";
import { ApiError } from "../src/api/http";
import { FIXTURES } from "@dev/fixtures";

/**
 * 2026-08-09 audit §2 — a field the server stops sending must FAIL, not render as blank, zero or NaN.
 *
 * Three seams, one rule, and each of them used to break it in its own way:
 *
 *   policyApi / branchApi   ended every operation in `as Promise<SomeView>`. A cast asserts a shape; it does
 *                           not check one. Roughly eighty operations — limits, consumed amounts, deductibles,
 *                           on-hand stock, licence expiry dates — sat outside the loud-schema-failure
 *                           behaviour the rest of the app has relied on since phase 12.
 *
 *   HttpApiClient (money)   defaulted an unparseable amount to `0` BEFORE `parseOr` ran, so the zod contract
 *                           validated a well-formed object and passed. "EGP 0" on a settlement screen is a
 *                           statement about what a provider is owed.
 *
 *   HttpApiClient (ids)     defaulted a missing identifier to `""`, likewise pre-validation. An empty id
 *                           becomes a React key, a route parameter, and the body of the next write.
 *
 * The point of these tests is the DIRECTION of the failure. Nobody doubts the services emit valid JSON; what
 * is being defended against is drift, and drift's whole character is that it looks like ordinary data.
 */

function respondWith(body: unknown) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async () => new Response(JSON.stringify(body), { status: 200, headers: { "content-type": "application/json" } })),
  );
}

/** The rejection, or `null` if the call unexpectedly succeeded. */
const failure = (p: Promise<unknown>) => p.then(() => null, (e) => e);

afterEach(() => vi.unstubAllGlobals());

describe("policyApi validates instead of casting", () => {
  it("refuses a coverage detail whose consumed amount the server stopped sending", async () => {
    // Everything present except `categories[].consumed` — the shape drift a rename produces.
    respondWith({
      enrollmentId: "e1", beneficiaryId: "b1", memberNo: "M-1", policyId: "p1", policyPlanId: "pp1",
      planLabel: "Gold", planVersionChangedSinceEnrolment: false, asOf: "2026-08-09",
      enrollmentStatus: "Active", effectiveFrom: "2026-01-01",
      categories: [{
        benefitCategoryCode: "OUTPATIENT", isCovered: true, /* consumed: MISSING */
        currencyCode: "EGP", resetPeriod: "Annual", limitDiffersFromPlan: false,
        waitingPeriodState: "None", requiresPreauth: false, deductibleWaived: false,
        exclusions: [], costShareByTier: [],
      }],
    });

    const err = await failure(createHttpPolicyApi().coverageDetails("e1"));
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).kind).toBe("schema");
  });

  it("accepts a response that carries MORE than the contract knows about", async () => {
    // Forward compatibility is the other half of the rule: a server that ADDS a field must not break a
    // bundle deployed before it. `.passthrough()` is what makes drift an error in one direction only.
    respondWith([{ payerId: "p1", payerCode: "MOH", nameEn: "Ministry", nameAr: "وزارة",
                   payerType: "Government", status: "Active", somethingNewNextQuarter: 42 }]);

    const payers = await createHttpPolicyApi().payers();
    expect(payers[0].payerCode).toBe("MOH");
  });

  it("refuses a member query page that is missing its paging envelope", async () => {
    respondWith({ items: [] });
    const err = await failure(createHttpPolicyApi().memberQuery({}));
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).kind).toBe("schema");
  });
});

/**
 * The HTTP implementations, NAMED — not the `branchApi` re-export.
 *
 * That re-export resolves through `@dev/fixtures`, so under vitest (a fixture build) it is the demo clinic,
 * which never touches `fetch` and therefore sails past every stubbed response below. These cases are about
 * the TRANSPORT's schema behaviour, so they have to hold the transport.
 */
const { branch: httpBranchApi, roster: httpRosterApi, inventory: httpInventoryApi } = HTTP_BRANCH_APIS;

describe("branchApi validates instead of casting", () => {
  it("refuses a stock line whose on-hand count is absent", async () => {
    respondWith({
      asOf: "2026-08-09", branches: ["maadi"],
      stock: [{
        branchId: "maadi", itemId: "i1", sku: "SKU-1", nameEn: "Gauze", nameAr: "شاش",
        category: "Medical", unitOfMeasure: "box", coldChain: false, batchId: null, batchNo: null,
        expiryDate: null, /* onHand: MISSING */ reorderLevel: 10, isLow: false, isQuarantined: false,
      }],
    });
    const err = await failure(httpInventoryApi.stock());
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).kind).toBe("schema");
  });

  it("refuses a roster impact preview that does not say how many people it affects", async () => {
    // The count is what the operator acknowledges and the server re-checks on apply. Rendering a preview
    // with a silently-absent number is how eight people travel to a locked building.
    respondWith({ dryRun: true, affected: [] });
    const err = await failure(httpRosterApi.preview({ kind: "ClinicClosed", dateFrom: "2026-08-10",
                                                  dateTo: "2026-08-10", reason: "maintenance" }));
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).kind).toBe("schema");
  });

  it("keeps a masked licence number as null rather than treating it as drift", async () => {
    // `licenseNo: null` is a min-necessary projection — "not shown to you", not "missing". The schemas mark
    // exactly those fields nullable, and a rule that could not tell the two apart would be unusable here.
    respondWith([{
      practitionerId: "pr1", practitionerType: "Doctor", fullNameEn: "A", fullNameAr: "أ",
      primarySpecialty: null, specialties: [], branches: [], status: "Active",
      licenseNo: null, licenseExpiry: "2027-01-01", licenceValid: true, daysUntilExpiry: 500,
    }]);
    const rows = await httpBranchApi.practitioners();
    expect(rows[0].licenseNo).toBeNull();
  });
});

describe("HttpApiClient refuses to invent a value it would be believed about", () => {
  it("fails a settlement whose total the server did not send, instead of reporting EGP 0", async () => {
    respondWith([{
      id: "s1", settlementNo: "SET-1", providerRef: "PRV-1", providerName: "Clinic",
      periodStart: "2026-07-01", periodEnd: "2026-07-31", currency: "EGP", /* total: MISSING */
      state: "draft", lines: [],
    }]);
    const err = await failure(new HttpApiClient().settlements());
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).kind).toBe("schema");
    expect((err as ApiError).message).toContain("settlement.total");
  });

  it("fails a booking whose appointment id is absent, instead of returning an empty id", async () => {
    respondWith({ /* appointmentId: MISSING */ status: "Booked" });
    const err = await failure(
      new HttpApiClient().bookAppointment({
        beneficiaryId: "b1", providerId: "pr1", locationId: "l1", slotId: "s1", appointmentType: "Consultation",
      }),
    );
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).kind).toBe("schema");
    expect((err as ApiError).message).toContain("booking.appointmentId");
  });

  it("still accepts an amount the service serialised as a decimal string", async () => {
    // The refusal is about ABSENCE, not about typing pedantry. .NET serialises `decimal` as a JSON number,
    // so this is a tolerance rather than a case anything relies on today — but a service that switched to
    // string decimals would be making a serialisation choice, not dropping a field, and the two deserve
    // different answers.
    respondWith({ from: "2026-07-01", to: "2026-07-31", rows: [], totalAuthorized: 0, totalDelivered: 0,
                  totalSpend: "1234.50" });
    const view = await new HttpApiClient().utilization();
    expect(view.totalSpend).toBe(1234.5);
  });
});

describe("the fixture seam", () => {
  it("is present in a test build, which is what the whole suite runs on", () => {
    // If this ever fails, `vite.config.ts` has started aliasing @dev/fixtures to the live stub under test
    // mode — and every screen test would be talking to a client that throws. Cheaper to say so here.
    expect(FIXTURES.available).toBe(true);
  });
});

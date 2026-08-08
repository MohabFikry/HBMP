import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * 29.7 (design 45 §7) — the lowest-price chip and the availability tri-state, THROUGH THE REAL CLIENT.
 *
 * <p><b>Why this file exists beside `drug-price-availability.test.tsx`.</b> That file renders `DrugCombobox`
 * against `DevApiClient`, whose fixtures author `isLowestPrice` and `availability` by hand. It proves the
 * component renders the chips when it is GIVEN them. It cannot prove anything about where they come from —
 * and `HttpApiClient.searchPrescribableDrugs` built its result from an explicit field list that omitted all
 * three, so against a real backend `d.isLowestPrice` was `undefined`, the chip never rendered, and every
 * test stayed green. The contract declares the fields `.optional()`, so zod parsed the gap without a word.</p>
 *
 * <p>These tests drive the mapper directly, which is the seam the defect lived in — the same reasoning
 * `clinician-worklists.test.tsx` records for the prescription-status vocabulary.</p>
 */
function stubSearchResponse(item: Record<string, unknown>) {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: true,
    status: 200,
    headers: new Headers({ "content-type": "application/json" }),
    json: async () => ({ page: 1, pageSize: 20, items: [item] }),
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

const BASE = {
  drugId: "11111111-1111-1111-1111-111111111111",
  tradeName: "Amoxil 500mg",
  activeIngredient: "amoxicillin",
  strength: "500mg",
  form: "Tablet",
  priceEgp: 120,
  atcCode: "J01CA04",
  hasIndicationData: true,
};

async function firstHit(item: Record<string, unknown>) {
  const { HttpApiClient } = await import("../src/api/HttpApiClient");
  stubSearchResponse(item);
  const rows = await new HttpApiClient().searchPrescribableDrugs("amox");
  return rows[0];
}

afterEach(() => vi.unstubAllGlobals());

describe("29.7 — the prescribing combobox reads price and availability from the server", () => {
  it("carries isLowestPrice through from the search response", async () => {
    const hit = await firstHit({ ...BASE, isLowestPrice: true, pricePerUnit: 4 });

    expect(hit.isLowestPrice).toBe(true);
  });

  it("carries pricePerUnit through, because that is what the label is computed on", async () => {
    // The 29.7 correction: a 20-tab pack at 100 EGP is dearer per tablet than a 30-tab pack at 120. The
    // client renders the server's verdict, but the per-unit figure is what makes it explicable.
    const hit = await firstHit({ ...BASE, isLowestPrice: true, pricePerUnit: 4 });

    expect(hit.pricePerUnit).toBe(4);
  });

  it("carries a positive Unavailable through", async () => {
    const hit = await firstHit({ ...BASE, availability: "Unavailable" });

    expect(hit.availability).toBe("Unavailable");
  });

  it("defaults availability to Unknown when the server omits it", async () => {
    // Absence is never a clean result — but for THIS field the platform's answer to absence is the explicit
    // third state, not undefined. `Unknown` renders nothing, so the default is safe; `undefined` would too,
    // but only by accident, and the next reader could not tell which was intended.
    const hit = await firstHit(BASE);

    expect(hit.availability).toBe("Unknown");
  });

  it("does not invent a lowest-price label the server did not send", async () => {
    // A drug with no pack size has no per-unit price and is never labelled. Defaulting this to `true`, or
    // deriving it client-side from priceEgp, is exactly the pack-price comparison §7 exists to prevent.
    const hit = await firstHit(BASE);

    expect(hit.isLowestPrice).toBeFalsy();
    expect(hit.pricePerUnit).toBeUndefined();
  });
});

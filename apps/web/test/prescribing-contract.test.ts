import { describe, expect, it } from "vitest";
import { zValidationResult } from "@mersal/contracts";

// The EXACT shape System.Text.Json emits for FindingView: nullable properties are written as null,
// not omitted. This is what the browser actually received.
const serverPayload = {
  validationId: "0f1d2c3b-4a59-4687-8b21-9c0d1e2f3a4b",
  ranAt: "2026-08-03T15:13:38.123+00:00",
  engineVersion: "26.4",
  overallState: "NotChecked",
  findings: [{
    lineId: "1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f",
    drugId: "2b3c4d5e-6f7a-4b8c-9d0e-1f2a3b4c5d6e",
    kind: "Interaction",
    state: "Ok",
    messageEn: "No interaction found (checked against 0 known pairs).",
    messageAr: "لم يتم العثور على تداخلات.",
    sourceName: "Mersal interaction list",
    sourceVersion: "curated",
    checkedAt: "2026-08-03T15:13:38.123+00:00",
    caveat: "coverage is partial.",
    severity: null,        // <-- null, not undefined
    relatedLineId: null,   // <-- null, not undefined
    requiresAcknowledgement: false,
    isBlocking: false,
  }],
  lineStates: { "1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f": "NotChecked" },
};

/**
 * The validation response contract, against the shape the SERVER ACTUALLY SENDS.
 *
 * This exists because the whole prescribing suite was green while the live screen showed "validation could
 * not run" on every click. The API returned 200 with a correct 10 KB body; the CLIENT threw, because
 * System.Text.Json writes nullable properties as `null` rather than omitting them and the schema used
 * `.optional()` — which accepts `undefined` and rejects `null`.
 *
 * The fixtures could not have caught it: they emitted `undefined` for absent optionals, so they tested a
 * shape the server never produces. This test pins the real one.
 */
describe("the validation response as the server actually serialises it", () => {
  it("parses when nullable fields arrive as null rather than omitted", () => {
    const r = zValidationResult.safeParse(serverPayload);
    if (!r.success) console.log("ISSUES:", JSON.stringify(r.error.issues, null, 2));
    expect(r.success).toBe(true);
  });
});

import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { HttpApiClient } from "../src/api/HttpApiClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ApiError } from "../src/api/http";
import { DoctorResults } from "../src/screens/ClinicianWorklists";
import { renderNode, seedSession } from "./helpers";

/**
 * Phase 33.8 — the result dialog that was entirely placeholders, and the report nobody could read.
 *
 * ============================================================================================================
 * WHAT THESE PROVE
 * ============================================================================================================
 * `resultDetail` had NO test. `DevApiClient` returns the finished contract shape, so the fixture never
 * exercised the mapping — and the mapping was wrong in four places at once.
 *
 * orders-service returned `IEnumerable<ResultResponse>` (an ARRAY) when the caller could read the result and a
 * single object when they could not. The client read both as an object, so `r?.resultValue` was `undefined`
 * and `value` fell to "—". Worse, `ResultResponse` is a fulfillment row and carries no code, category or
 * status at all, so those three were "Result", "—" and "Completed" on every read against a real gateway.
 *
 * These tests drive the REAL `HttpApiClient` against a stubbed `fetch`, because that is the only place the
 * mapping exists — a test against `DevApiClient` would have passed throughout the defect.
 */

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

/** Stub `fetch` with one JSON body, so the assertions are about the client's own mapping. */
function respondWith(body: unknown, status = 200) {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: status >= 200 && status < 300,
    status,
    headers: new Headers({ "content-type": "application/json" }),
    json: async () => body,
    text: async () => JSON.stringify(body),
    blob: async () => new Blob(["bytes"]),
  } as unknown as Response);
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

// ── The mapping ───────────────────────────────────────────────────────────────────────────────────────────

describe("a readable result is read from what the server actually sent", () => {
  it("takes the value, code, category and status from the response rather than from defaults", async () => {
    // The shape orders-service returns now: ONE object carrying the line's context.
    respondWith({
      restricted: false, orderId: "ORD-1", lineId: "ln-1",
      code: "80053", codeSystem: "CPT", category: "Laboratory", status: "Used",
      resultValue: "Sodium 139 mmol/L", hasReport: false, resultUploadedAt: "2026-07-20T11:00:00Z",
    });

    const detail = await new HttpApiClient().resultDetail("ORD-1", "ln-1");

    expect(detail.restricted).toBe(false);
    if (detail.restricted) throw new Error("unreachable");
    // Every one of these was a client-side default before: "Result", "—", "—", "Completed".
    expect(detail.category).toBe("Laboratory");
    expect(detail.code).toBe("80053");
    expect(detail.value).toBe("Sodium 139 mmol/L");
    expect(detail.status).toBe("Used");
  });

  it("reports that a report file exists without being handed its document id", async () => {
    respondWith({
      restricted: false, orderId: "ORD-1", lineId: "ln-3",
      code: "71260", codeSystem: "CPT", category: "Imaging", status: "Used",
      resultValue: null, hasReport: true, resultUploadedAt: "2026-07-20T14:30:00Z",
    });

    const detail = await new HttpApiClient().resultDetail("ORD-1", "ln-3");
    if (detail.restricted) throw new Error("unreachable");

    // A radiology result with a report and no summary — the case the whole fix is about.
    expect(detail.hasReport).toBe(true);
    expect(detail.value).toBe("—");
    // The id is a capability the gate hands out; the browser gets the boolean and the gated route.
    expect(Object.keys(detail)).not.toContain("resultDocumentId");
  });

  it("fails loudly if the endpoint ever answers with an array again", async () => {
    // The original defect, locked out. An array made every field fall to its default and the dialog rendered
    // "Result / — / — / Completed" with nothing logged — a contract break presented as a plausible result.
    // A doctor cannot tell a missing value from an em-dash, so this must be an error, not a fallback.
    respondWith([{ fulfillmentId: "f-1", orderLineId: "ln-1", resultValue: "Sodium 139 mmol/L" }]);

    await expect(new HttpApiClient().resultDetail("ORD-1", "ln-1")).rejects.toThrow(/does not recognise/i);
  });

  it("still parses the restricted projection, which is a different shape on the same route", async () => {
    respondWith({
      restricted: true, orderId: "ORD-1", lineId: "ln-2",
      sensitivityLevel: "Sensitive", category: "CPT", status: "Used", orderingBranchId: null,
    });

    const detail = await new HttpApiClient().resultDetail("ORD-1", "ln-2");

    expect(detail.restricted).toBe(true);
    if (!detail.restricted) throw new Error("unreachable");
    expect(detail.sensitivityLevel).toBe("Sensitive");
  });
});

describe("the report is fetched through the gate, not from the document", () => {
  it("asks orders-service by order and line", async () => {
    const fetchMock = respondWith({});

    await new HttpApiClient().resultReport("ORD-1", "ln-3");

    const url = String(fetchMock.mock.calls[0][0]);
    // Through orders — where the 14.7 sensitivity gate lives — not at document-service, which cannot know
    // whether this line is restricted or whether this reader holds a grant.
    expect(url).toContain("/investigation-orders/ORD-1/lines/ln-3/result/report");
    expect(url).not.toContain("/documents/");
  });
});

// ── The screen ────────────────────────────────────────────────────────────────────────────────────────────

function renderResults(api: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  seedSession("doctor");
  return renderNode(<DoctorResults />, api);
}

describe("the clinician can reach the report", () => {
  it("offers the download only when a report exists", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    // ln-1 in the fixture is the lab case: a summary and no file.
    (api as { resultDetail: unknown }).resultDetail = vi.fn().mockResolvedValue({
      restricted: false, orderId: "ORD-1", lineId: "ln-1", category: "Laboratory",
      code: "80053", value: "Within reference range", status: "Completed", hasReport: false,
    });
    renderResults(api);

    await user.click((await screen.findAllByRole("button", { name: "View result" }))[0]);
    expect(await screen.findByText("Within reference range")).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Download the report" })).toBeNull();
  });

  it("downloads it, and says the summary is not a substitute", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { resultDetail: unknown }).resultDetail = vi.fn().mockResolvedValue({
      restricted: false, orderId: "ORD-1", lineId: "ln-3", category: "Radiology — CT",
      code: "71260", value: "—", status: "Completed", hasReport: true,
    });
    const report = vi.fn().mockResolvedValue(new Blob(["%PDF"], { type: "application/pdf" }));
    (api as { resultReport: unknown }).resultReport = report;
    vi.stubGlobal("URL", { ...URL, createObjectURL: () => "blob:x", revokeObjectURL: () => {} });
    renderResults(api);

    await user.click((await screen.findAllByRole("button", { name: "View result" }))[0]);
    // For imaging the report IS the finding, and the screen says so rather than leaving a doctor to assume
    // the one-line summary is the result.
    expect(await screen.findByText(/summary above is not a substitute/i)).toBeInTheDocument();
    await user.click(screen.getByRole("button", { name: "Download the report" }));

    await waitFor(() => expect(report).toHaveBeenCalledWith("ORD-1", "ln-3"));
  });

  it("tells a refusal apart from a failure, because only one of them can be retried", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 }) as unknown as ApiClient;
    (api as { resultDetail: unknown }).resultDetail = vi.fn().mockResolvedValue({
      restricted: false, orderId: "ORD-1", lineId: "ln-3", category: "Radiology — CT",
      code: "71260", value: "—", status: "Completed", hasReport: true,
    });
    (api as { resultReport: unknown }).resultReport =
      vi.fn().mockRejectedValue(new ApiError("http", "restricted", 403));
    renderResults(api);

    await user.click((await screen.findAllByRole("button", { name: "View result" }))[0]);
    await user.click(await screen.findByRole("button", { name: "Download the report" }));

    // A 403 is the sensitivity gate, not an outage: retrying will not help and there is a defined way to ask.
    const alert = await screen.findByText(/may not read this report/i);
    expect(alert.textContent).toMatch(/time-boxed access/i);
  });
});

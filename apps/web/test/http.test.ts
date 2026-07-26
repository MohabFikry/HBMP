import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, getRaw } from "../src/api/http";

/**
 * The HTTP seam must surface a service's RFC 7807 `problem+json` (`detail`/`title`) rather than a generic
 * "request failed", and must classify failures (network vs http vs schema) so screens can react. These tests
 * drive `getRaw` (the untyped request path) with a mocked `fetch`.
 */

function mockFetch(impl: () => Promise<Response> | Response) {
  vi.stubGlobal("fetch", vi.fn(impl));
}

function problemResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/problem+json" },
  });
}

afterEach(() => vi.unstubAllGlobals());

describe("http problem+json surfacing", () => {
  it("parses problem+json detail onto ApiError (message + reason + status)", async () => {
    mockFetch(() =>
      problemResponse(422, {
        type: "https://mersal/errors/unknown-allergen",
        title: "Unprocessable Entity",
        detail: "Allergen 'X' is not present in master data.",
        traceId: "00-abc-01",
      }),
    );
    const err = await getRaw("/beneficiaries/1/allergies").then(
      () => null,
      (e) => e,
    );
    expect(err).toBeInstanceOf(ApiError);
    expect(err.kind).toBe("http");
    expect(err.status).toBe(422);
    expect(err.message).toBe("Allergen 'X' is not present in master data.");
    expect(err.reason).toBe("Allergen 'X' is not present in master data.");
    expect(err.problem?.title).toBe("Unprocessable Entity");
    expect(err.problem?.traceId).toBe("00-abc-01");
  });

  it("falls back to title when detail is absent", async () => {
    mockFetch(() => problemResponse(409, { title: "Conflict" }));
    const err = await getRaw("/x").then(() => null, (e) => e);
    expect(err.reason).toBe("Conflict");
  });

  it("falls back to a generic message when the error body is not JSON", async () => {
    mockFetch(() => new Response("<html>502</html>", { status: 502, headers: { "content-type": "text/html" } }));
    const err = await getRaw("/x").then(() => null, (e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.kind).toBe("http");
    expect(err.status).toBe(502);
    expect(err.reason).toBe("Request to /x failed");
  });

  it("classifies a thrown fetch as a network error", async () => {
    mockFetch(() => {
      throw new TypeError("Failed to fetch");
    });
    const err = await getRaw("/x").then(() => null, (e) => e);
    expect(err).toBeInstanceOf(ApiError);
    expect(err.kind).toBe("network");
  });

  it("returns null on 204 No Content", async () => {
    mockFetch(() => new Response(null, { status: 204 }));
    await expect(getRaw("/x")).resolves.toBeNull();
  });
});

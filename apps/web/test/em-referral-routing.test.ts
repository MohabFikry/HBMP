import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * 29.2 (design 45 §2, invariant 3) — <b>the doctor picks a service; the SYSTEM decides the vehicle.</b>
 *
 * <p>The routing existed as a pure function and nothing acted on it. `CptRouting` returned
 * `OrderableVehicle.Referral` for every E/M code, `/orderable-services` published that verdict so "the UI
 * can show the doctor what will happen before they commit" — and the UI never called it. No code path
 * anywhere created a referral from an E/M code.</p>
 *
 * <p>These drive the two client seams that were missing: reading the vehicle, and acting on it.</p>
 */
function stub(body: unknown, status = 200) {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: status < 400,
    status,
    headers: new Headers({ "content-type": "application/json" }),
    json: async () => body,
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function lastBody(fetchMock: ReturnType<typeof vi.fn>): any {
  const calls = fetchMock.mock.calls;
  return JSON.parse(String((calls[calls.length - 1]?.[1] as RequestInit).body));
}

function lastUrl(fetchMock: ReturnType<typeof vi.fn>): string {
  const calls = fetchMock.mock.calls;
  return String(calls[calls.length - 1]?.[0]);
}

afterEach(() => vi.unstubAllGlobals());

describe("29.2 — the composer can ask what a code will actually create", () => {
  it("reads the vehicle for each orderable service", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    stub({
      page: 1, pageSize: 50, total: 2,
      items: [
        { code: "29881", description: "Knee arthroscopy", section: "Surgery", vehicle: "ProcedureOrder", orderable: true },
        { code: "99243", description: "Office consultation", section: "EvaluationAndManagement", vehicle: "Referral", orderable: true },
      ],
    });

    const rows = await new HttpApiClient().orderableServices("99");

    expect(rows.map((r) => r.vehicle)).toEqual(["ProcedureOrder", "Referral"]);
    expect(rows[1].section).toBe("EvaluationAndManagement");
  });

  it("carries the reason a code is NOT orderable rather than dropping it", async () => {
    // "a non-orderable code states WHY rather than being silently absent" — a code that vanishes from a
    // search looks like a typo to the doctor who typed it correctly.
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    stub({
      page: 1, pageSize: 50, total: 1,
      items: [{
        code: "99499", description: "Unlisted E/M service", section: "EvaluationAndManagement",
        vehicle: "Referral", orderable: false, reasonEn: "Unlisted — choose a specific service.",
      }],
    });

    const rows = await new HttpApiClient().orderableServices("99499");

    expect(rows[0].orderable).toBe(false);
    expect(rows[0].reason?.en).toContain("Unlisted");
  });

  it("filters by vehicle when asked, so a tab can show only what it creates", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = stub({ page: 1, pageSize: 50, total: 0, items: [] });

    await new HttpApiClient().orderableServices("phys", ["ProcedureOrder", "Referral"]);

    expect(lastUrl(fetchMock)).toContain("kind=ProcedureOrder%2CReferral");
  });
});

describe("29.2 — an E/M code creates a Referral", () => {
  it("sends the CPT code as the referral's requested service", async () => {
    // Design 45 §2: the referral carries "the CPT code as its requested service". Without it the referral
    // names only a specialty, and loop closure has nothing specific to close against.
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = stub({
      referralId: "r-1", referralNo: "REF-2026-000001", status: "Requested",
      requestedServiceCode: "99243",
    });

    await new HttpApiClient().createReferral({
      encounterId: "33333333-3333-3333-3333-333333333333",
      targetSpecialty: "Cardiology",
      reason: "Chest pain on exertion",
      requestedServiceCode: "99243",
    });

    const b = lastBody(fetchMock);
    expect(b.requestedServiceCode).toBe("99243");
    expect(b.requestedServiceCodeSystem).toBe("CPT");
    expect(b.targetSpecialty).toBe("Cardiology");
  });

  it("carries an idempotency key, because a double-tapped referral is not two referrals", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = stub({ referralId: "r-1", referralNo: "REF-2026-000001", status: "Requested" });

    await new HttpApiClient().createReferral({
      encounterId: "33333333-3333-3333-3333-333333333333",
      targetSpecialty: "Cardiology",
      requestedServiceCode: "99243",
    });

    const init = fetchMock.mock.calls[fetchMock.mock.calls.length - 1][1] as RequestInit;
    const headers = new Headers(init.headers);
    expect(headers.get("Idempotency-Key")).toBeTruthy();
  });

  it("returns the referral number, which is what the doctor is told", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    stub({ referralId: "r-1", referralNo: "REF-2026-000001", status: "Requested", requestedServiceCode: "99243" });

    const r = await new HttpApiClient().createReferral({
      encounterId: "33333333-3333-3333-3333-333333333333",
      targetSpecialty: "Cardiology",
      requestedServiceCode: "99243",
    });

    expect(r.referralNo).toBe("REF-2026-000001");
    expect(r.status).toBe("Requested");
  });
});

import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * 29.2 / 29.3 (design 45 §2) — the OP Procedures composer, THROUGH THE REAL CLIENT.
 *
 * <p><b>The defect this file exists for.</b> `orders-service` requires a procedure type on every Procedure
 * line — `ProcedureLineChecks.Validate` returns `TypeMissing` and `Orders.cs` turns that into a 422 — and
 * the composer never sent one. `zInvestigationDraftLine` carried `{lineId, test, quantity, note}` and
 * `submitInvestigationOrder` mapped four fields, none of them the type. So EVERY procedure order composed
 * in the encounter was refused, while `ProcedureOrderEndpointTests` passed because its request builder
 * hardcodes `typeCode: "Physiotherapy"`, and `encounter-procedures.test.tsx` passed because it asserts on
 * tab labels and never submits.</p>
 *
 * <p>Which is the point: the server test proved the endpoint works when GIVEN a type, and the screen test
 * proved the tab is spelled correctly. Neither could see that nothing connected them.</p>
 */
function captureSubmit() {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: true,
    status: 201,
    headers: new Headers({ "content-type": "application/json" }),
    json: async () => ({ orderId: "o-1", orderNo: "ORD-2026-000901", status: "Active" }),
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function bodyOf(fetchMock: ReturnType<typeof vi.fn>): any {
  const calls = fetchMock.mock.calls;
  const call = calls[calls.length - 1];
  return JSON.parse(String((call?.[1] as RequestInit).body));
}

const LINE = {
  lineId: "11111111-1111-1111-1111-111111111111",
  test: { code: "97110", description: "Therapeutic exercise" },
  quantity: 6,
  note: "",
};

afterEach(() => vi.unstubAllGlobals());

describe("29.2 — the composer sends the procedure type the write path requires", () => {
  it("carries procedureTypeCode on the ORDER, where 31.1 moved it", async () => {
    // It was per LINE until 31.1. A course is ONE clinical decision — one kind, one number of attendances —
    // and per line a two-item course could carry two of each, which is not a course any centre can deliver.
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = captureSubmit();

    await new HttpApiClient().submitInvestigationOrder({
      encounterId: "33333333-3333-3333-3333-333333333333",
      orderType: "Procedure",
      lines: [LINE as never],
      acknowledgements: [],
      procedureTypeCode: "Physiotherapy",
      sessions: 6,
    });

    expect(bodyOf(fetchMock).procedureTypeCode).toBe("Physiotherapy");
    expect(bodyOf(fetchMock).sessions).toBe(6);
  });

  it("sends the line quantity as a quantity PER SESSION, and the server derives the total", async () => {
    // 31.1 separates the two. They used to be one field ("sessions ARE the quantity", design 45 §2), which
    // left nowhere to record "three of these at each attendance". The METERED total the server stores is
    // still one number — sessions x this — so consume, partial approval and the delivering centre's queue
    // count exactly the units they always did.
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = captureSubmit();

    await new HttpApiClient().submitInvestigationOrder({
      encounterId: "33333333-3333-3333-3333-333333333333",
      orderType: "Procedure",
      lines: [{ ...LINE, quantity: 3 } as never],
      acknowledgements: [],
      procedureTypeCode: "Dialysis",
      sessions: 10,
    });

    const body = bodyOf(fetchMock);
    expect(body.sessions).toBe(10);
    expect(body.lines[0].quantityPerSession).toBe(3);
  });

  it("sends no procedure type on a lab line, because one there is refused rather than ignored", async () => {
    // `ProcedureLineChecks` returns TypeOnNonProcedureOrder for a type on a lab/radiology line. Sending
    // null keeps the common path exactly as it was.
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = captureSubmit();

    await new HttpApiClient().submitInvestigationOrder({
      encounterId: "33333333-3333-3333-3333-333333333333",
      orderType: "Lab",
      lines: [{ ...LINE, test: { code: "80048", description: "Metabolic panel" } } as never],
      acknowledgements: [],
    });

    expect(bodyOf(fetchMock).procedureTypeCode ?? null).toBeNull();
  });
});

describe("29.2 — the client can ask masterdata what the procedure types are", () => {
  it("reads the session flag off the row rather than inferring it from the name", async () => {
    // "SESSIONS FOLLOW THE FLAG, NOT THE NAME" — dialysis and rehabilitation are session-based too, and
    // `if (type === 'Physiotherapy')` would guarantee this conversation twice more.
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ "content-type": "application/json" }),
      json: async () => [
        { code: "Physiotherapy", nameEn: "Physiotherapy", nameAr: "العلاج الطبيعي", isSessionBased: true, defaultSessions: 6, maxSessions: 30, allowedCptScopes: ["Medicine"], isActive: true, sortOrder: 10 },
        { code: "MinorSurgery", nameEn: "Minor Surgery", nameAr: "جراحة صغرى", isSessionBased: false, defaultSessions: null, maxSessions: null, allowedCptScopes: ["Surgery"], isActive: true, sortOrder: 20 },
      ],
    }));

    const types = await new HttpApiClient().procedureTypes();

    expect(types.map((t) => t.code)).toEqual(["Physiotherapy", "MinorSurgery"]);
    expect(types[0].isSessionBased).toBe(true);
    expect(types[0].defaultSessions).toBe(6);
    expect(types[0].maxSessions).toBe(30);
    expect(types[1].isSessionBased).toBe(false);
  });

  it("carries the Arabic name, because the composer is bilingual", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ "content-type": "application/json" }),
      json: async () => [
        { code: "Dialysis", nameEn: "Dialysis", nameAr: "غسيل كلوي", isSessionBased: true, defaultSessions: 12, maxSessions: 156, allowedCptScopes: ["Medicine"], isActive: true, sortOrder: 40 },
      ],
    }));

    const types = await new HttpApiClient().procedureTypes();

    expect(types[0].name.ar).toBe("غسيل كلوي");
    expect(types[0].name.en).toBe("Dialysis");
  });
});

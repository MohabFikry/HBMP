import { describe, expect, it, vi, afterEach } from "vitest";
import { HttpApiClient } from "../src/api/HttpApiClient";

/**
 * ============================================================================================================
 * THE TEST WHOSE ABSENCE WAS THE BUG
 * ============================================================================================================
 *
 * `HttpApiClient` maps every service payload into the shared contracts and validates the result with
 * `parseOr`, which THROWS on a mismatch. Nothing in this repository ever ran it. Every screen test builds
 * `DevApiClient`, and `HttpApiClient` only does anything when there is a gateway on the other end — so its
 * mappings were, for the entire life of the application, unexecuted code.
 *
 * What that hid (design 49 §1): `settlements()` and `exportReport()` wrote the literal string `"ok"` into a
 * `status` field whose schema is `{ kind, label }`. `parseOr` threw on every call. The Provider Settlements
 * screen and the Exports screen were permanently in their error state in production and permanently green
 * in CI. Two more sites — `caseTasks()` and `escalations()` — had the same crash in the case portal.
 *
 * So this file drives the client over a stubbed `fetch` and asserts on what comes out. It does NOT test the
 * services; a service's shape changing is what the OpenAPI drift gate is for. It tests the ADAPTER, on the
 * principle that the HTTP adapter is code, and untested code is code that does not work.
 *
 * Adding a mapping here is cheap. The next literal `status: "ok"` fails at this file rather than in front of
 * a finance clerk.
 */

type Stub = { body?: unknown; text?: string; headers?: Record<string, string>; status?: number };

/** Stand `fetch` up to answer with one canned response, and hand back the calls it received. */
function stubFetch(stub: Stub) {
  const calls: { url: string; init: RequestInit }[] = [];
  const headers = new Headers(stub.headers ?? {});
  const res = {
    ok: (stub.status ?? 200) < 400,
    status: stub.status ?? 200,
    headers,
    json: async () => stub.body,
    text: async () => stub.text ?? JSON.stringify(stub.body ?? null),
    blob: async () => new Blob([stub.text ?? ""], { type: "text/csv" }),
  };
  vi.stubGlobal("fetch", (url: string, init: RequestInit) => {
    calls.push({ url: String(url), init });
    return Promise.resolve(res as unknown as Response);
  });
  return calls;
}

afterEach(() => { vi.unstubAllGlobals(); });

const SETTLEMENT = {
  settlementId: "stl-1",
  settlementNo: "STL-2026-000007",
  providerRef: "PRV-301",
  contractId: null,
  periodStart: "2026-06-01",
  periodEnd: "2026-06-30",
  currencyCode: "EGP",
  total: 58500,
  status: "Submitted",
  submittedBy: "usr-7",
  approvedBy: null,
  lines: [
    { serviceCode: "70553", serviceLine: "Imaging", deliveredQty: 9, agreedUnitPrice: 6500, lineTotal: 58500, priceSource: "Contract" },
    { serviceCode: "70554", serviceLine: "Imaging", deliveredQty: 2, agreedUnitPrice: 550, lineTotal: 1100, priceSource: "ObservedFloor" },
  ],
};

describe("HttpApiClient — finance", () => {
  it("parses a settlement list, which it could not do at all before", async () => {
    // THE REGRESSION. This exact call threw `ApiError("schema")` for every payload, because the mapping
    // wrote `status: "ok"` — a string — into `zStatus`, which is an object. Not "returned something odd":
    // threw, every time, for every user.
    stubFetch({ body: [SETTLEMENT], headers: { "X-Total-Count": "312" } });
    const page = await new HttpApiClient().settlements();

    expect(page.rows).toHaveLength(1);
    expect(page.rows[0].settlementNo).toBe("STL-2026-000007");
    // The TRUE count behind the endpoint's 100-row cap, so the screen can say it is showing part of it.
    expect(page.total).toBe(312);
  });

  it("gives each settlement state its own chip instead of one green 'ok' for all four", async () => {
    for (const [state, kind] of [["Draft", "neu"], ["Submitted", "warn"], ["Approved", "info"], ["Paid", "ok"]] as const) {
      stubFetch({ body: [{ ...SETTLEMENT, status: state }] });
      const page = await new HttpApiClient().settlements();
      expect(page.rows[0].status.kind, `${state} should be ${kind}`).toBe(kind);
      expect(page.rows[0].state).toBe(state.toLowerCase());
      vi.unstubAllGlobals();
    }
  });

  it("keeps the price source, so a floor-priced line is not rendered as a contract tariff", async () => {
    stubFetch({ body: [SETTLEMENT] });
    const page = await new HttpApiClient().settlements();
    expect(page.rows[0].lines.map((l) => l.priceSource)).toEqual(["Contract", "ObservedFloor"]);
  });

  it("carries submittedBy, which is what lets the screen honour SoD before the click", async () => {
    stubFetch({ body: [SETTLEMENT] });
    const page = await new HttpApiClient().settlements();
    expect(page.rows[0].submittedBy).toBe("usr-7");
  });

  it("sends the status filter to the server rather than filtering a truncated page in the browser", async () => {
    const calls = stubFetch({ body: [] });
    await new HttpApiClient().settlements({ status: "Draft" });
    expect(calls[0].url).toContain("status=Draft");
  });

  it("sends the period on utilization, which it never used to", async () => {
    const calls = stubFetch({
      body: { from: "2026-06-01", to: "2026-06-30", rows: [], totalAuthorized: 0, totalDelivered: 0, totalSpend: 0 },
    });
    await new HttpApiClient().utilization({ from: "2026-06-01", to: "2026-06-30" });
    expect(calls[0].url).toContain("from=2026-06-01");
    expect(calls[0].url).toContain("to=2026-06-30");
  });

  it("sends the period on summaries alongside the dimension", async () => {
    const calls = stubFetch({ body: { dimension: "category", buckets: [], totalSpend: 0 } });
    await new HttpApiClient().financialSummary("category", { from: "2026-05-01", to: "2026-05-31" });
    expect(calls[0].url).toContain("dimension=category");
    expect(calls[0].url).toContain("from=2026-05-01");
  });

  it("returns the exported FILE's receipt, reading the row count off the header", async () => {
    // The old client posted here and parsed `text/csv` as JSON. Between that and the `status: "ok"` literal
    // it could not return at all — and even repaired, it downloaded nothing.
    const calls = stubFetch({
      text: "service_code,spend\n70553,58500\n",
      headers: { "X-Row-Count": "1", "Content-Disposition": 'attachment; filename="utilization-2026-06-01_2026-06-30.csv"' },
    });
    const res = await new HttpApiClient().exportReport({
      report: "utilization", format: "csv", from: "2026-06-01", to: "2026-06-30",
    });
    expect(res.rowCount).toBe(1);
    // The SERVER's filename, not one the client assembled — the local template is how a CSV came to be
    // named `.xlsx`.
    expect(res.filename).toBe("utilization-2026-06-01_2026-06-30.csv");
    expect(res.format).toBe("csv");
    expect(calls[0].init.method).toBe("POST");
  });

  it("treats a missing X-Row-Count as unknown rather than failing the download", async () => {
    stubFetch({ text: "a,b\n1,2\n" });
    const res = await new HttpApiClient().exportReport({
      report: "summary", format: "csv", from: "2026-06-01", to: "2026-06-30",
    });
    // The gateway not exposing the header must not cost the operator the file.
    expect(res.rowCount).toBe(0);
    expect(res.filename).toContain(".csv");
  });
});

describe("HttpApiClient — pharmacy", () => {
  const RX = [{
    prescriptionId: "rx-1",
    rxNo: "RX-2026-000202",
    beneficiaryId: "MRS-M-1",
    status: "Approved",
    expiresAt: null,
    prescriberName: "Dr N",
    submittedAt: "2026-07-20T09:00:00Z",
    primaryIcdCode: null,
    diagnosisCodes: [],
    expired: false,
    lines: [
      {
        prescriptionLineId: "rxl-1", drugId: "d-1", drugName: "Amoxicillin", dose: "1 cap",
        route: "Oral", frequency: "TDS", durationDays: 7,
        quantityPrescribed: 21, quantityDispensed: 0, quantityRemaining: 21, status: "Active",
        quantityUnit: "caps",
        outOfStock: true,
        outOfStockAt: "2026-07-21T07:40:00Z",
        outOfStockNote: "Supplier back-order",
      },
    ],
  }];

  it("reads outOfStock from the server instead of hardcoding false", async () => {
    // THE REGRESSION. `HttpApiClient` wrote `outOfStock: false` as a literal, because the server's view did
    // not carry the field — while `DevApiClient` wrote `true` on one fixture. The chip rendered in
    // development and in the tests and could not render in production. Design 49 §5.
    stubFetch({ body: RX });
    const rows = await new HttpApiClient().pharmacySearch({ rxNo: "RX-2026-000202" });
    expect(rows[0].lines[0].outOfStock).toBe(true);
    expect(rows[0].lines[0].outOfStockNote).toBe("Supplier back-order");
  });

  it("reports whether an out-of-stock flag notified anyone or replayed a colleague's", async () => {
    stubFetch({ body: { prescriptionLineId: "rxl-1", flagged: true, replayed: true, outOfStockAt: "2026-07-21T07:40:00Z" } });
    const res = await new HttpApiClient().flagOutOfStock({ prescriptionId: "rx-1", lineId: "rxl-1" });
    // `replayed` is the whole point: the server does not notify the prescriber twice, and the second
    // pharmacist must not walk away believing they just told them.
    expect(res.replayed).toBe(true);
    expect(res.flagged).toBe(true);
  });

  it("posts the shortage to the line's own endpoint", async () => {
    const calls = stubFetch({ body: { prescriptionLineId: "rxl-1", flagged: true, replayed: false } });
    await new HttpApiClient().flagOutOfStock({ prescriptionId: "rx-1", lineId: "rxl-1", quantity: 5, note: "back-order" });
    expect(calls[0].url).toContain("/prescriptions/rx-1/lines/rxl-1/out-of-stock");
    expect(JSON.parse(String(calls[0].init.body))).toMatchObject({ quantity: 5, note: "back-order" });
  });
});

describe("HttpApiClient — the two literals carried in from the case portal", () => {
  it("parses coordination tasks, which threw on every call", async () => {
    stubFetch({ body: [{ taskId: "t-1", caseId: "c-1", title: "Chase the referral", state: "InProgress", dueAt: null }] });
    const rows = await new HttpApiClient().caseTasks("c-1");
    expect(rows[0].status.kind).toBe("info");
  });

  it("parses escalations, and renders one as attention rather than as success", async () => {
    stubFetch({ body: [{ escalationId: "e-1", caseId: "c-1", caseNo: "CS-1", raisedToRole: "Supervisor", reason: "SLA", raisedAt: "2026-07-20T09:00:00Z" }] });
    const rows = await new HttpApiClient().escalations();
    // An escalation is by definition something that needed raising. The literal made it green.
    expect(rows[0].status.kind).toBe("warn");
  });
});

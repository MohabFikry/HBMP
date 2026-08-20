import { afterEach, describe, expect, it, vi } from "vitest";

/**
 * 29.5 (design 45 §5) — acute/chronic prescribing, THROUGH THE REAL CLIENT.
 *
 * <p><b>The gap this closes.</b> The phase-30 fix (`9a3604b`) wired the SERVER: `POST /prescriptions` learnt
 * `kind` / `refillFrequencyCode` / `durationDays`, the counter learnt to meter against a window, and the
 * sweeper learnt to forfeit one. Nothing wired the CLIENT. `submitPrescription` sent five fields, none of
 * them `kind`, so the server's `DEFAULT 'Acute'` took every prescription the SPA wrote and no doctor could
 * produce a chronic script at all.</p>
 *
 * <p>That is the same defect twice: first the library nothing called, then the endpoint nothing called. The
 * assertion that catches it is the one about the REQUEST, which no screen-level test against fixtures can
 * make.</p>
 */
function capture(status = 201, body: unknown = { prescriptionId: "p-1", rxNo: "RX-1", status: "Draft" }) {
  const fetchMock = vi.fn().mockResolvedValue({
    ok: status < 400,
    status,
    headers: new Headers({ "content-type": "application/json" }),
    json: async () => body,
  });
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function bodyOf(fetchMock: ReturnType<typeof vi.fn>): any {
  const calls = fetchMock.mock.calls;
  return JSON.parse(String((calls[calls.length - 1]?.[1] as RequestInit).body));
}

const LINE = {
  lineId: "11111111-1111-1111-1111-111111111111",
  drug: { drugId: "22222222-2222-2222-2222-222222222222", tradeName: { en: "Metformin", ar: "ميتفورمين" }, hasIndicationData: true },
  dose: "1",
  durationDays: 90,
  quantity: 270,
};

const BASE = {
  encounterId: "33333333-3333-3333-3333-333333333333",
  diagnosisIcdCodes: ["E11"],
  acknowledgements: [],
};

afterEach(() => vi.unstubAllGlobals());

describe("29.5 — the composer can write a chronic script", () => {
  it("sends kind, frequency and duration when the prescription is chronic", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = capture();

    await new HttpApiClient().submitPrescription({
      ...BASE,
      lines: [LINE as never],
      kind: "Chronic",
      refillFrequencyCode: "Monthly",
      durationDays: 90,
    } as never);

    const b = bodyOf(fetchMock);
    expect(b.kind).toBe("Chronic");
    expect(b.refillFrequencyCode).toBe("Monthly");
    expect(b.durationDays).toBe(90);
  });

  it("sends an acute prescription exactly as it always did", async () => {
    // Additive and defaulted, so every existing caller is unaffected. An acute script carries NO schedule:
    // the server refuses `acute-has-no-schedule` if one arrives, because "is this chronic?" has one answer.
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = capture();

    await new HttpApiClient().submitPrescription({ ...BASE, lines: [LINE as never] } as never);

    const b = bodyOf(fetchMock);
    expect(b.kind ?? "Acute").toBe("Acute");
    expect(b.refillFrequencyCode ?? null).toBeNull();
  });
});

describe("29.5 — the composer can read the frequency master table", () => {
  it("lists the supervisor-configurable cadences with their month counts", async () => {
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ "content-type": "application/json" }),
      json: async () => [
        { code: "Monthly", months: 1, nameEn: "Monthly", nameAr: "شهرياً" },
        { code: "Every3Months", months: 3, nameEn: "Every 3 months", nameAr: "كل ٣ أشهر" },
      ],
    }));

    const rows = await new HttpApiClient().refillFrequencies();

    expect(rows.map((r) => r.code)).toEqual(["Monthly", "Every3Months"]);
    expect(rows[1].months).toBe(3);
    expect(rows[1].name.ar).toBe("كل ٣ أشهر");
  });
});

describe("29.5 — the preview asks the SERVER to resolve the drug's pack facts", () => {
  it("sends the drugId rather than pack facts the client does not have", async () => {
    // THE DEFECT THIS PINS. The composer holds no pack facts — they are master data — so it sent nulls,
    // the endpoint (which read them only from the request body) answered `quantity-not-checked`, and
    // chronic prescribing was unreachable for EVERY drug whatever the catalogue recorded.
    //
    // Neither existing test could see it: the UI tests run against DevApiClient, which computes the
    // schedule locally and never fails, and the server tests pass pack facts explicitly. The seam between
    // the two was the one thing nothing exercised — the same gap this whole phase kept producing.
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = capture(200, { total: 90, unit: "SubUnit", frequencyMonths: 1, windows: [] });

    await new HttpApiClient().chronicPreview({
      durationDays: 90,
      refillFrequencyCode: "Monthly",
      doseAmount: 1,
      timesPerDay: 1,
      drugId: "44444444-4444-4444-4444-444444444444",
    });

    const b = bodyOf(fetchMock);
    expect(b.drugId).toBe("44444444-4444-4444-4444-444444444444");
    // And it does NOT invent pack facts. A guessed `isPackSplittable: true` here permits a fractional
    // inhaler — the exact silently-wrong quantity invariant 8 exists to forbid.
    expect(b.isPackSplittable).toBeUndefined();
    expect(b.packContent).toBeUndefined();
  });
});

describe("29.5 — the composer can preview the window schedule before submitting", () => {
  it("returns the per-window quantities the doctor is shown", async () => {
    // The gate's own example: the doctor sees 34/33/33 BEFORE submit and can adjust. Computed by the
    // server, so it cannot drift from what is actually written.
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    vi.stubGlobal("fetch", vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ "content-type": "application/json" }),
      json: async () => ({
        total: 100,
        unit: "SubUnit",
        frequencyMonths: 1,
        windows: [
          { windowNo: 1, scheduledOpen: "2026-08-08", opensAt: "2026-08-08", closesAt: "2026-09-06", allocatedQuantity: 34 },
          { windowNo: 2, scheduledOpen: "2026-09-07", opensAt: "2026-09-02", closesAt: "2026-10-06", allocatedQuantity: 33 },
          { windowNo: 3, scheduledOpen: "2026-10-07", opensAt: "2026-10-02", closesAt: "2026-11-05", allocatedQuantity: 33 },
        ],
      }),
    }));

    const p = await new HttpApiClient().chronicPreview({
      durationDays: 90, refillFrequencyCode: "Monthly", doseAmount: 1, timesPerDay: 1,
      isPackSplittable: true, packContent: 20,
    });

    expect(p.total).toBe(100);
    expect(p.windows.map((w) => w.allocatedQuantity)).toEqual([34, 33, 33]);
    expect(p.windows.reduce((s, w) => s + w.allocatedQuantity, 0)).toBe(p.total);
  });
});

describe("31.3 — the quantity's unit reaches the wire", () => {
  it("sends quantityUnit beside quantityPrescribed", async () => {
    /*
     * THE HAZARD. 31.3 made the composer's Quantity field a box count wherever the catalogue records what a
     * box holds, so a seven-day course of a 24-tablet product is written as "1". The dispensing counter
     * renders that figure and takes the pharmacist's number against it — and 1 box and 1 tablet are the same
     * character.
     *
     * A screen-level test cannot see this: the composer holds the unit either way. What matters is that the
     * MAPPING onto the wire carries it, which is the seam this file exists for.
     */
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = capture();

    await new HttpApiClient().submitPrescription({
      ...BASE,
      lines: [{ ...LINE, quantity: 2, quantityUnit: "boxes" }],
    } as never);

    const b = bodyOf(fetchMock);
    expect(b.lines[0].quantityPrescribed).toBe(2);
    expect(b.lines[0].quantityUnit).toBe("boxes");
  });

  it("sends no unit rather than a plausible one when nothing was computed", async () => {
    // Invariant 8 at the one place a wrong word is worse than none: a unit nobody derived, printed beside a
    // number at a dispensing counter, reads exactly like one that was.
    const { HttpApiClient } = await import("../src/api/HttpApiClient");
    const fetchMock = capture();

    await new HttpApiClient().submitPrescription({
      ...BASE,
      lines: [{ ...LINE, quantity: 30, quantityUnit: "" }],
    } as never);

    expect(bodyOf(fetchMock).lines[0].quantityUnit).toBeNull();
  });
});

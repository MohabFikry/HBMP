import { describe, expect, it } from "vitest";
import {
  zApprovalItem,
  zAuthorizationItem,
  zOrderPricing,
  zRxPricing,
} from "@mersal/contracts";
import rxPricing from "./fixtures/rx-pricing.server.json";
import rxPricingPartial from "./fixtures/rx-pricing-partial.server.json";
import orderPricing from "./fixtures/order-pricing.server.json";
import authorizations from "./fixtures/authorizations.server.json";
import authorizationItems from "./fixtures/authorization-items.server.json";

/**
 * The contracts, checked against what the SERVER actually sends.
 *
 * <p><b>Why this file exists.</b> `zRxPriceLine` called the field `lineId`; pharmacy-service sends
 * `prescriptionLineId`. Every pricing response therefore failed contract validation, the client threw, and
 * the dispensing counter said "the cost of this prescription could not be worked out" on every prescription
 * — while the endpoint had been returning a correct 290.50 EGP the whole time.</p>
 *
 * <p><b>Why nothing caught it.</b> The dev fixture was written from the CONTRACT, so it used `lineId` too:
 * the tests, the fixture and the schema all agreed with each other and none of them had ever seen the
 * server. The OpenAPI drift gate could not help either — these are minimal-API endpoints with no declared
 * response schema, so the generated spec records only `200 OK`. A green suite proving three copies of the
 * same assumption is worse than no suite, because nobody investigates it.</p>
 *
 * <p><b>What these fixtures are.</b> Real responses, captured verbatim from the running services and
 * committed. They contain no PHI beyond synthetic dev data (CLAUDE.md: test data is synthetic; never real
 * PHI in lower envs) — beneficiary ids are dev-seeded, and the drug names are catalogue reference data.
 * Re-capture them when an endpoint's shape changes on purpose; a failure here means the client and the
 * server disagree, which is exactly the condition this file is for.</p>
 */

describe("the contracts match what the services send", () => {
  it("prices a prescription", () => {
    // The regression. `prescriptionLineId`, not `lineId`.
    const r = zRxPricing.safeParse(rxPricing);
    expect(r.success, JSON.stringify(r.success ? [] : r.error.issues, null, 2)).toBe(true);
  });

  it("prices an investigation order", () => {
    const r = zOrderPricing.safeParse(orderPricing);
    expect(r.success, JSON.stringify(r.success ? [] : r.error.issues, null, 2)).toBe(true);
  });

  it("lists what was delivered against an authorization", () => {
    const r = zAuthorizationItem.array().safeParse(authorizationItems);
    expect(r.success, JSON.stringify(r.success ? [] : r.error.issues, null, 2)).toBe(true);
  });

  it("splits a priced prescription between the member and the payer", () => {
    // The behaviour the shape exists to carry, asserted on the real payload rather than on a fixture that
    // agrees with the schema by construction. `determinate` means both figures are real and they account for
    // the whole allowed amount — a split that does not add up is worse than no split.
    const rx = zRxPricing.parse(rxPricing);
    expect(rx.determinate).toBe(true);
    expect(rx.totalEgp).toBeGreaterThan(0);
    expect(rx.memberShareEgp).not.toBeNull();
    expect(rx.payerShareEgp).not.toBeNull();
    // Against `quotedOnEgp`, which is what the split was actually composed on. It equals the total here
    // because this capture asked the whole-prescription question — see the partial fixture beside it.
    expect(rx.quotedOnEgp).toBe(rx.totalEgp);
    expect((rx.memberShareEgp ?? 0) + (rx.payerShareEgp ?? 0)).toBeCloseTo(rx.quotedOnEgp ?? 0, 2);
  });

  it("re-quotes the split on what is being dispensed, and the figure is not proportional", () => {
    // Both fixtures are the SAME prescription from the SAME running service, one asked with no basis and one
    // asked with `?dispense=<line>:2`. That is what makes this a proof rather than an assertion: the total is
    // identical in both, and the member's share is not.
    const whole = zRxPricing.parse(rxPricing);
    const partial = zRxPricing.parse(rxPricingPartial);

    expect(whole.quotedOnDispenseNow ?? false).toBe(false);
    expect(partial.quotedOnDispenseNow).toBe(true);

    // The total is what the prescriber wrote. It does not move while a pharmacist works.
    expect(partial.totalEgp).toBe(whole.totalEgp);

    // The share is quoted on what is being handed over, which is a fraction of it.
    expect(partial.quotedOnEgp).toBeLessThan(partial.totalEgp ?? 0);

    // <b>The reason this cannot be computed in the browser.</b> A deductible is met in full before
    // coinsurance starts, so the share is not linear in the amount. Scaling the whole-prescription figure by
    // the ratio of the two bases gives a number that is wrong by more than half — and would look entirely
    // ordinary on screen. Only the benefit engine may answer this.
    const ratio = (partial.quotedOnEgp ?? 0) / (whole.quotedOnEgp ?? 1);
    const scaled = (whole.memberShareEgp ?? 0) * ratio;
    expect(Math.abs(scaled - (partial.memberShareEgp ?? 0))).toBeGreaterThan(1);

    // Whatever the split is, it still accounts for the whole basis and neither side is a silent zero.
    expect((partial.memberShareEgp ?? 0) + (partial.payerShareEgp ?? 0))
      .toBeCloseTo(partial.quotedOnEgp ?? 0, 2);
  });

  it("still withholds an unquotable share rather than showing a zero", () => {
    // The other half, on the order fixture: no examination in master data carries a price, so the total is
    // unknown. NULL, with a reason — never 0, because a zero at a counter reads as "free".
    const order = zOrderPricing.parse(orderPricing);
    expect(order.determinate).toBe(false);
    expect(order.memberShareEgp ?? null).toBeNull();
    expect(order.payerShareEgp ?? null).toBeNull();
    expect(order.reason).toBeTruthy();
  });

  it("multiplies quantity by unit price, on the wire", () => {
    // The arithmetic the counter quotes, checked against the server's own numbers rather than restated here.
    const rx = zRxPricing.parse(rxPricing);
    for (const line of rx.lines) {
      if (line.unitPriceEgp == null) {
        expect(line.lineTotalEgp ?? null).toBeNull();
        continue;
      }
      expect(line.lineTotalEgp).toBeCloseTo(line.unitPriceEgp * line.quantityPrescribed, 2);
    }
    const sum = rx.lines.reduce((s, l) => s + (l.lineTotalEgp ?? 0), 0);
    expect(rx.totalEgp).toBeCloseTo(sum, 2);
  });

  it("carries the drug id whole, not a display prefix of it", () => {
    // `toPrescriptions` sliced the drug id to eight characters — a display shortening applied to a field
    // NOTHING displays and three things use as an identity: the active-ingredient join, the approved-
    // alternatives lookup behind the substitute control, and the drugId sent on submission.
    //
    // The result was two silent failures at once. Every medicine read "active ingredient not recorded", and
    // the substitute modal reported "no approved alternative is listed" for the entire catalogue — because
    // master data answers 404 to a prefix. Both are states the screens are DESIGNED to show honestly, so
    // neither looked like a bug.
    //
    // Asserted on the real dispensing payload: whatever `drugId` the server sends is a uuid, and a client
    // that shortens it is shortening an identifier.
    for (const line of zRxPricing.parse(rxPricing).lines) {
      expect(line.drugId).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i);
    }
  });

  it("lists authorizations, and says which kind each is", () => {
    // `kind` is the field a reviewer triages on. The worklist mapper defaults it to Review when a server
    // predates ADR-0034, so a missing one is silent — this asserts the live server really sends it.
    const rows = (authorizations as unknown[]).map((a) => {
      const x = a as Record<string, unknown>;
      return zApprovalItem.pick({ kind: true }).safeParse({ kind: x.kind });
    });
    expect(rows.every((r) => r.success)).toBe(true);
  });
});

import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { PrescribingWorkspace } from "../src/screens/prescribing/PrescribingWorkspace";
import { seedSession } from "./helpers";

/**
 * 29.6 — DOSING IN THE DRUG'S OWN UNIT, WITH THE QUANTITY COMPUTED (design 45 §6).
 *
 * ============================================================================================================
 * THE DEFECT THIS SUITE PINS
 * ============================================================================================================
 * The dose field was free text, and `doseAmount` / `timesPerDay` were never sent. So the Quantity check —
 * built, tested and wired in 29.6 — reported "not checked: this line has no numeric dose, frequency and
 * duration to compute a quantity from" on EVERY prescription this platform has ever written. The check was
 * correct; nothing fed it.
 *
 * That is the same shape as the chronic preview that shipped unusable and the E/M routing nothing called:
 * a correct server, and a client that never spoke to it.
 *
 * ============================================================================================================
 * AND WHY THE NUMBER COMES FROM THE SERVER
 * ============================================================================================================
 * `QuantityMath` is one implementation of the arithmetic that decides how much medicine a person is handed.
 * The validation check grades against it and the dispensing counter meters against it. A second copy in
 * TypeScript would be a second answer to that question, and the two would be found to disagree at a counter.
 */

const DRUG = {
  drugId: "22222222-2222-2222-2222-222222222222",
  tradeName: { en: "Metformin 500mg tablet", ar: "ميتفورمين ٥٠٠ ملجم قرص" },
  activeIngredient: "metformin",
  strength: "500 mg",
  form: "tablet",
  priceEgp: 30,
  hasIndicationData: true,
  prescribingUnit: "Tablet",
  packSize: 30,
  isPackSplittable: true,
};

/** A client that serves one searchable drug and records every quantity-preview it is asked for. */
class DosingApi extends DevApiClient {
  previews: unknown[] = [];
  submitted: unknown[] = [];
  /** Set to make the preview refuse the way a catalogue gap does. */
  missingField: string | null = null;
  /** False stands in for the insulin case: the pack counts pens and the dose counts IU. */
  countsInBoxes = true;
  drug = DRUG;

  // The COMBOBOX calls this one, not `searchDrugs` — a different method on the same client, and overriding
  // the wrong one leaves the fixture's drug with no pack facts at all.
  async searchPrescribableDrugs() {
    return [this.drug] as never;
  }

  async quantityPreview(input: Record<string, unknown>) {
    this.previews.push(input);
    if (this.missingField) {
      throw Object.assign(new Error("422"), {
        problem: {
          title: "quantity-not-checked",
          detail: `'${this.missingField}' is not recorded for this drug, so the quantity to dispense cannot `
            + "be computed. A silently wrong quantity is a dispensing error.",
        },
      });
    }
    const total = Number(input.doseAmount) * Number(input.timesPerDay) * Number(input.durationDays);
    return {
      totalUnits: total,
      dispenseQuantity: total,
      packs: null,
      // Null when this fixture is standing in for a product whose pack counts containers, not doses.
      boxes: this.countsInBoxes ? Math.ceil(total / 30) : null,
      packSize: 30,
      prescribingUnit: this.countsInBoxes ? "Tablet" : "IU",
      isPackSplittable: true,
    };
  }

  /** Clean, so this suite tests the SUBMIT PAYLOAD rather than the fixture's clinical verdicts. */
  async validatePrescription(req: { lines: { lineId: string }[] }) {
    return {
      validationId: "v-1",
      ranAt: "2026-08-08T00:00:00Z",
      engineVersion: "test",
      overallState: "Ok",
      findings: [],
      lineStates: Object.fromEntries(req.lines.map((l) => [l.lineId, "Ok"])),
    } as never;
  }

  async submitPrescription(req: unknown) {
    this.submitted.push(req);
    return { prescriptionId: "p-1", rxNo: "RX-1", status: "Draft" };
  }
}

function renderComposer(api: DosingApi) {
  seedSession("doctor");
  render(
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <PrescribingWorkspace
        encounterId="33333333-3333-3333-3333-333333333333"
        beneficiaryId="aaaaaaaa-0000-0000-0000-000000000231"
        diagnosisIcdCodes={["E11"]}
      />
    </AppProviders>,
  );
  return userEvent.setup();
}

async function pickDrug(user: ReturnType<typeof userEvent.setup>) {
  const combo = await screen.findByRole("combobox", { name: /medicine/i });
  await user.type(combo, "met");
  const list = await screen.findByRole("listbox", { name: /medicine/i }, { timeout: 5000 });
  await user.click(within(list).getAllByRole("option")[0]);
}

afterEach(() => vi.unstubAllGlobals());

describe("29.6 — the dose is counted in the drug's own unit", () => {
  it("labels the dose field with the unit master data records", async () => {
    // "60" beside a medicine is a number whose unit the prescriber has to infer from the product name. The
    // catalogue records it, so the field says it.
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);

    expect(await screen.findByText("Tablet")).toBeTruthy();
  });

  it("says nothing about the unit when master data records none", async () => {
    // 838 catalogue rows have no derivable unit. An invented one appears beside the dose field and reads as
    // data — the composer shows the field bare instead.
    const api = new DosingApi({ latencyMs: 0 });
    api.drug = { ...DRUG, prescribingUnit: undefined } as never;
    const user = renderComposer(api);
    await pickDrug(user);

    expect(screen.queryByText("Tablet")).toBeNull();
  });
});

describe("29.6 — the quantity is computed, shown, and still editable", () => {
  it("asks the SERVER for the quantity and fills the field in", async () => {
    // 1 tablet twice a day for 30 days = 60. Computed by `QuantityMath` behind the endpoint, not multiplied
    // here — see the header.
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "30");

    const quantity = await screen.findByRole("spinbutton", { name: /quantity/i });
    await vi.waitFor(() => expect((quantity as HTMLInputElement).value).toBe("60"));

    expect(api.previews.length).toBeGreaterThan(0);
    expect(api.previews[api.previews.length - 1]).toMatchObject({
      drugId: DRUG.drugId, doseAmount: 1, timesPerDay: 2, durationDays: 30,
    });
  });

  it("keeps a quantity the doctor typed rather than overwriting it", async () => {
    // The computed figure is a STARTING POINT. A prescriber who deliberately writes 90 because the patient
    // is travelling must not watch it snap back to 60 on the next keystroke.
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "30");

    const quantity = await screen.findByRole("spinbutton", { name: /quantity/i });
    await vi.waitFor(() => expect((quantity as HTMLInputElement).value).toBe("60"));

    await user.clear(quantity);
    await user.type(quantity, "90");
    // A further edit to the inputs the quantity is derived from must not undo the doctor's own number.
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "0");

    expect((quantity as HTMLInputElement).value).toBe("90");
  });

  it("states WHICH master-data field is missing rather than filling in a number", async () => {
    // Invariant 8. A guessed quantity is a dispensing error that looks exactly like a correct one, so the
    // composer says what is absent and names it as the column a data administrator can act on.
    const api = new DosingApi({ latencyMs: 0 });
    api.missingField = "is_pack_splittable";
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "30");

    expect(await screen.findByText(/is_pack_splittable/)).toBeTruthy();
  });
});

describe("29.6 — the numbers actually reach the server", () => {
  it("sends doseAmount and timesPerDay, which the quantity check has never received", async () => {
    // THE WIRING GAP. Without these the check reports "no numeric dose, frequency and duration to compute a
    // quantity from" on every prescription — a correct check that nothing fed.
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "30");

    // Wait for the computed quantity to land BEFORE validating. It is part of the line's fingerprint, so a
    // quantity that arrives after a validation run correctly marks that run stale — the checks were graded
    // against a different number. Real use settles long before a prescriber reaches for Validate.
    const quantity = await screen.findByRole("spinbutton", { name: /quantity/i });
    await vi.waitFor(() => expect((quantity as HTMLInputElement).value).toBe("60"));

    await user.click(screen.getByRole("button", { name: /validate/i }));
    await user.click(await screen.findByRole("button", { name: /^submit$/i }));

    await vi.waitFor(() => expect(api.submitted.length).toBe(1));
    const line = (api.submitted[0] as { lines: Record<string, unknown>[] }).lines[0];
    expect(line).toMatchObject({ doseAmount: 1, timesPerDay: 2, durationDays: 30 });
  });
});

describe("29.5 — treatment duration comes from the line", () => {
  it("offers no second, script-level duration field", async () => {
    // One fact, one field. A script-level "Treatment duration" beside each line's own duration is two places
    // to state the same thing, and the schedule was computed from whichever the doctor filled in second.
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);
    await user.click(screen.getByRole("radio", { name: /chronic/i }));

    expect(screen.queryByRole("spinbutton", { name: /treatment duration/i })).toBeNull();
    expect(screen.getAllByRole("spinbutton", { name: /duration/i }).length).toBe(1);
  });
});

describe("31.2 — the quantity is said in BOXES, which is what leaves the counter", () => {
  it("states the box count beside the units it was converted from", async () => {
    // "60" beside a medicine tells a prescriber nothing about what the patient carries home. 60 tablets
    // from a box of 30 is two boxes — and the units stay in the sentence so the conversion is checkable.
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "30");

    expect(await screen.findByText(/60 Tablet — 2 boxes of 30/)).toBeTruthy();
  });

  it("REFUSES to count boxes when the pack counts containers rather than doses", async () => {
    // The Lantus case, and the reason this is not a simple division. The catalogue records "5 pens" per box
    // and the dose is in IU: 180 IU over a pack of 5 divides to 36 boxes, when 180 IU is less than a single
    // 300-IU pen. Wrong by two orders of magnitude, and it would print as confidently as a right answer.
    const api = new DosingApi({ latencyMs: 0 });
    api.countsInBoxes = false;
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "90");

    expect(await screen.findByText(/pack size counts containers, not the unit/i)).toBeTruthy();
    expect(screen.queryByText(/boxes of/i)).toBeNull();
  });
});

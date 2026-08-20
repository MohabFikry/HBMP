import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
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
  // 31.3 — the short form the SERVER derives, which is what the dose field is labelled with. "Tablet" is
  // the database's word for the same unit and stays out of the label.
  prescribingUnitShort: "tabs",
  packSize: 30,
  isPackSplittable: true,
};

/** A client that serves one searchable drug and records every quantity-preview it is asked for. */
class DosingApi extends DevApiClient {
  previews: unknown[] = [];
  submitted: unknown[] = [];
  /** Set to make the preview refuse the way a catalogue gap does. */
  missingField: string | null = null;
  /** False stands in for a product whose box contents the catalogue does not record. */
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
      // Null when this fixture stands in for a product whose box contents are unrecorded.
      boxes: this.countsInBoxes ? Math.ceil(total / 30) : null,
      packContent: this.countsInBoxes ? 30 : null,
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
    // catalogue records it, so the field IS it: 31.3 moved the unit off a chip beside the box and into the
    // label, in the short form a prescription is written in — "Dose (tabs)", not "Dose" and a chip saying
    // "Tablet".
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);

    expect(await screen.findByRole("spinbutton", { name: /dose \(tabs\)/i })).toBeTruthy();
  });

  it("says nothing about the unit when master data records none", async () => {
    // 838 catalogue rows have no derivable unit. An invented one appears beside the dose field and reads as
    // data — the composer leaves the label bare instead.
    const api = new DosingApi({ latencyMs: 0 });
    api.drug = { ...DRUG, prescribingUnit: undefined, prescribingUnitShort: undefined } as never;
    const user = renderComposer(api);
    await pickDrug(user);

    expect(await screen.findByRole("spinbutton", { name: /^dose$/i })).toBeTruthy();
    expect(screen.queryByText("Tablet")).toBeNull();
  });
});

describe("29.6 — the quantity is computed, shown, and still editable", () => {
  it("asks the SERVER for the quantity and fills the field in", async () => {
    // 1 tablet twice a day for 30 days = 60 tablets, which is TWO boxes of 30 — and boxes are what the
    // field holds (31.3), because a box is what the patient carries home. Computed by `QuantityMath` behind
    // the endpoint, not multiplied here — see the header.
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "30");

    const quantity = await screen.findByRole("spinbutton", { name: /quantity \(boxes\)/i });
    await vi.waitFor(() => expect((quantity as HTMLInputElement).value).toBe("2"));

    expect(api.previews.length).toBeGreaterThan(0);
    expect(api.previews[api.previews.length - 1]).toMatchObject({
      drugId: DRUG.drugId, doseAmount: 1, timesPerDay: 2, durationDays: 30,
    });
  });

  it("keeps a quantity the doctor typed rather than overwriting it", async () => {
    // The computed figure is a STARTING POINT. A prescriber who deliberately writes 90 because the patient
    // is travelling must not watch it snap back on the next keystroke.
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "30");

    const quantity = await screen.findByRole("spinbutton", { name: /quantity/i });
    await vi.waitFor(() => expect((quantity as HTMLInputElement).value).toBe("2"));

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
    await vi.waitFor(() => expect((quantity as HTMLInputElement).value).toBe("2"));

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

describe("31.3 — the quantity is said in BOXES, which is what leaves the counter", () => {
  it("shows the conversion the box count cannot carry on its own", async () => {
    // The FIELD holds "2". What two means — two boxes of thirty, from a course of sixty tablets — is the
    // part a prescriber has to be able to check, so it is stated beneath it. Without the box's contents the
    // number is unverifiable, and a quantity you cannot verify is one you have to trust.
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "30");

    expect(await screen.findByText(/60 tabs — 30 tabs per box/i)).toBeTruthy();
  });

  it("REFUSES to count boxes when the catalogue does not record what a box holds", async () => {
    // The Lantus case. "Lantus Solostar 100 I.U./ML 5 Pens" states its concentration and never its volume,
    // so how much insulin the box holds is genuinely unknown. Three millilitres per pen is the usual fill
    // and assuming it would produce a box count that prints as confidently as a right one. The field falls
    // back to the dose total, and the LABEL stops claiming boxes.
    const api = new DosingApi({ latencyMs: 0 });
    api.countsInBoxes = false;
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "90");

    expect(await screen.findByText(/does not record how much one box of this holds/i)).toBeTruthy();
    expect(screen.queryByRole("spinbutton", { name: /quantity \(boxes\)/i })).toBeNull();
  });
});

describe("31.3 — the quantity's unit travels with the number", () => {
  it("sends what the quantity COUNTS, not just how many", async () => {
    /*
     * THE HAZARD THIS CLOSES. 31.3 made the composer's Quantity field a box count wherever the catalogue
     * records what a box holds — so a seven-day course of a 24-tablet product is written as "1".
     *
     * The dispensing counter renders `quantityPrescribed` as a bare figure and takes the number the
     * pharmacist hands over against it. A pharmacist reading "1" and giving one TABLET is a dispensing error
     * that the record, without this field, gave them no way to catch: 1 box and 1 tablet are the same
     * character. So the unit is snapshotted onto the line at prescribing time, like the drug name.
     */
    const api = new DosingApi({ latencyMs: 0 });
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "30");

    const quantity = await screen.findByRole("spinbutton", { name: /quantity/i });
    await vi.waitFor(() => expect((quantity as HTMLInputElement).value).toBe("2"));

    await user.click(screen.getByRole("button", { name: /validate/i }));
    await user.click(await screen.findByRole("button", { name: /^submit$/i }));

    await vi.waitFor(() => expect(api.submitted.length).toBe(1));
    const line = (api.submitted[0] as { lines: Record<string, unknown>[] }).lines[0];
    // The composer hands the API client its DRAFT lines; `HttpApiClient.rxLines` maps them onto the wire.
    // What this pins is that the unit is on the line at all — see chronic-prescribing-wiring.test.ts for the
    // assertion that it survives the mapping.
    expect(line).toMatchObject({ quantity: 2, quantityUnit: "boxes" });
  });

  it("sends NO unit when nothing was computed, rather than a plausible one", async () => {
    // Invariant 8 again, at the one place a wrong word is worse than no word. A line whose quantity could
    // not be computed keeps whatever figure the prescriber last saw; labelling it "boxes" on the strength of
    // the last drug they looked at would be a unit nobody derived.
    const api = new DosingApi({ latencyMs: 0 });
    api.missingField = "is_pack_splittable";
    const user = renderComposer(api);
    await pickDrug(user);

    await user.type(await screen.findByRole("spinbutton", { name: /^dose/i }), "1");
    await user.type(screen.getByRole("spinbutton", { name: /times per day/i }), "2");
    await user.type(screen.getByRole("spinbutton", { name: /duration/i }), "30");
    await screen.findByText(/is_pack_splittable/);

    await user.click(screen.getByRole("button", { name: /validate/i }));
    await user.click(await screen.findByRole("button", { name: /^submit$/i }));

    await vi.waitFor(() => expect(api.submitted.length).toBe(1));
    const line = (api.submitted[0] as { lines: Record<string, unknown>[] }).lines[0];
    expect(line.quantityUnit).toBeFalsy();
  });
});

import { describe, expect, it } from "vitest";
import { sectionsFor } from "../src/screens/investigations/InvestigationWorkspace";

/**
 * Which CPT sections the composer's catalogue search is narrowed to, per tab.
 *
 * <p><b>The defect.</b> This was one ternary — `orderType === "Imaging" ? ["Imaging"] : ["Laboratory",
 * "Pathology"]` — written before 29.1 renamed the order type to `Radiology`. After the rename the encounter
 * passes `"Radiology"`, the test is never true, and the RADIOLOGY tab fell through to the lab arm: a doctor
 * ordering a chest x-ray was searching a catalogue of blood panels. `"Procedure"` fell through the same way,
 * so the OP Procedures tab could not offer a single surgery or physiotherapy code.</p>
 *
 * <p>Neither failed loudly. The combobox returned results — just the wrong section's — and the encounter
 * test asserts on tab labels, which were right. This is the "fourth type silently inherits the Lab arm"
 * failure `DoctorEncounter` warns about twenty lines above, arriving in a different file.</p>
 *
 * <p>Extracted and tested as a function because a ternary chain keyed on a union that has GROWN twice is
 * exactly the shape that breaks silently the third time.</p>
 */
describe("the composer searches the section the tab is for", () => {
  it("narrows the Radiology tab to radiology codes", () => {
    // The regression: this returned ["Laboratory","Pathology"] after the 29.1 rename.
    expect(sectionsFor("Radiology")).toEqual(["Imaging"]);
  });

  it("still narrows the pre-switch Imaging spelling to the same section", () => {
    // Orders placed before the switch keep `Imaging` in the row for the life of the order, and the composer
    // must not treat a legacy value as an unknown one.
    expect(sectionsFor("Imaging")).toEqual(["Imaging"]);
  });

  it("narrows the Labs tab to both halves of Pathology and Laboratory", () => {
    // A sample run on an analyser and a specimen read by a pathologist are ordered from one tab and are not
    // the same kind of work.
    expect(sectionsFor("Lab")).toEqual(["Laboratory", "Pathology"]);
  });

  it("offers the OP Procedures tab everything it can route", () => {
    // Design 45 §2: Surgery and Medicine become a Procedure ORDER, E/M becomes a REFERRAL. All three belong
    // in the catalogue because the tab's job is to take a clinical decision and decide the vehicle — "the
    // doctor picks a service; the SYSTEM decides the vehicle".
    expect(sectionsFor("Procedure")).toEqual(["Surgery", "Medicine", "EvaluationAndManagement"]);
  });

  it("offers E/M in NO tab but OP Procedures", () => {
    // Invariant 3 constrains what an E/M code BECOMES, not whether it can be chosen. What must never happen
    // is one reaching a Lab or Radiology queue, which cannot perform it at all.
    for (const t of ["Lab", "Radiology", "Imaging"] as const) {
      expect(sectionsFor(t)).not.toContain("EvaluationAndManagement");
    }
  });
});

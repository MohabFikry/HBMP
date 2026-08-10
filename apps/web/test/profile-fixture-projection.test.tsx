import { describe, expect, it } from "vitest";
import { DevApiClient } from "../src/api/DevApiClient";

/**
 * The fixture answers as the signed-in ROLE, the way the server does.
 *
 * <b>What was wrong.</b> `patientProfile` returned all fifteen sections to everyone. In the dev build a
 * receptionist therefore saw the prescriptions and investigation results that `ProfilePolicies` withholds
 * from them in production — and, the part that matters more, the profile screen was never once rendered
 * against a real projection anywhere a test could observe it. "Reception's History tab has two panels, a
 * doctor's has six" was a claim nothing checked.
 *
 * The matrix itself is checked against the server's source in `profile-fixture-matrix.test.ts`; these are
 * about the fixture USING it.
 */

const keys = async (roles: readonly string[]) => {
  const api = new DevApiClient({ latencyMs: 0, roles: () => roles });
  return (await api.patientProfile("b-amal")).sections.map((s) => s.key);
};

/** The sections the profile screen groups into its History tab, in `PROFILE_TAB_GROUPS` order. */
const HISTORY = ["alerts", "pastMedicalHistory", "encounters", "investigations", "prescriptions", "caseManagement"];
const inHistory = (all: string[]) => HISTORY.filter((k) => all.includes(k));

describe("the fixture projects the patient profile by role", () => {
  it("gives reception exactly the two History sections it is entitled to", async () => {
    // The report that started this: "History shows only alerts and encounters". It is correct, and this is
    // now the thing that says so.
    expect(inHistory(await keys(["reception"]))).toEqual(["alerts", "encounters"]);
  });

  it("gives a treating doctor all six", async () => {
    expect(inHistory(await keys(["doctor"]))).toEqual(HISTORY);
  });

  it("never sends a section the role has no cell for", async () => {
    const reception = await keys(["reception"]);
    // Not merely absent from the History tab — absent from the payload, which is what the server does and
    // what makes the screen drop the tab rather than render it empty.
    expect(reception).not.toContain("prescriptions");
    expect(reception).not.toContain("investigations");
    expect(reception).not.toContain("financial");
  });

  it("sends an existence-only cell as Restricted rather than dropping it", async () => {
    const api = new DevApiClient({ latencyMs: 0, roles: () => ["case_manager"] });
    const profile = await api.patientProfile("b-amal");
    const rx = profile.sections.find((s) => s.key === "prescriptions");
    // The case manager may know a prescription section exists and ask for access — that is a different
    // answer from "no such thing", and collapsing the two is the failure design 39 §6 is about.
    expect(rx?.state).toBe("Restricted");
  });

  it("widens across several roles rather than taking the first", async () => {
    // A supervisor carrying both titles gets the wider cell, matching the server's widest-wins rule.
    expect(await keys(["reception", "doctor"])).toContain("prescriptions");
  });

  it("projects nothing when no role is known, so a client built without a session still works", async () => {
    // Most unit tests construct the client with no session at all. Answering them with an empty profile
    // would be a fixture that is technically righteous and practically useless.
    const all = await keys([]);
    expect(all).toContain("prescriptions");
    expect(all.length).toBe(15);
  });
});

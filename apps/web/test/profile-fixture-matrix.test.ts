import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve as resolvePath } from "node:path";
import { PROFILE_SECTION_MATRIX, type MatrixCell } from "../src/dev/profileSectionMatrix";

/**
 * The dev fixture's copy of the profile section matrix must agree with the server's.
 *
 * <b>The defect this exists for.</b> `DevApiClient.patientProfile` answered every caller with all fifteen
 * sections regardless of role, while the server sends only the sections the role's matrix cell allows. So a
 * receptionist in the dev build saw prescriptions and investigation results that production correctly
 * withholds — and the profile screen's behaviour under a real projection was never exercised anywhere a test
 * could see it. Making the fixture project fixes that, and creates a second copy of the matrix, which is a
 * drift risk. This is the guard for it: no runtime crosses the C#/TypeScript line, so the invariant is stated
 * against the server's SOURCE.
 *
 * <b>It parses rather than trusts.</b> The parser below is deliberately strict — an unrecognised helper or a
 * role whose row it cannot resolve FAILS, rather than being skipped. A silently-skipped role is how a check
 * like this passes while covering nothing.
 */

const POLICIES = resolvePath(__dirname, "../../../libs/authz/ProfilePolicies.cs");

/** `Vis()` / `Treating()` / `Assigned()` / `Res()` → the cell kinds the fixture models. */
const HELPER: Record<string, MatrixCell> = {
  Vis: "vis",
  Treating: "treating",
  Assigned: "assigned",
  Res: "res",
};

type Row = Record<string, MatrixCell>;

/** Section constant name (`PastMedicalHistory`) → wire key (`pastMedicalHistory`). */
function wireKey(constant: string): string {
  return constant.charAt(0).toLowerCase() + constant.slice(1);
}

/** The body of one `private static Dictionary<string, SectionRule> Name() ...` member. */
function memberBody(src: string, name: string): string {
  const start = src.indexOf(`Dictionary<string, SectionRule> ${name}()`);
  if (start < 0) throw new Error(`matrix parser: no row builder named ${name}`);
  // Runs to the next member declaration or to the Matrix dictionary, whichever comes first.
  const rest = src.slice(start);
  const end = rest.slice(1).search(/Dictionary<string, SectionRule> \w+\(\)|private static readonly IReadOnlyDictionary/);
  return end < 0 ? rest : rest.slice(0, end + 1);
}

/** Cells written as `(ProfileSections.X, Helper(...))` inside a `Row(...)` call. */
function rowCells(body: string): Row {
  const out: Row = {};
  for (const m of body.matchAll(/\(ProfileSections\.(\w+),\s*(\w+)\(/g)) {
    const [, section, helper] = m;
    const cell = HELPER[helper];
    if (!cell) throw new Error(`matrix parser: unknown rule helper ${helper}() on ${section}`);
    out[wireKey(section)] = cell;
  }
  return out;
}

/**
 * Three rows are derived: they call another builder, then reassign and remove individual cells. Those
 * mutations are applied in source order so the result matches what the C# actually produces.
 */
function derivedRow(body: string, base: Row): Row {
  const out: Row = { ...base };
  // Statements in order: `row[ProfileSections.X] = Helper(...)` and `row.Remove(ProfileSections.X)`.
  for (const m of body.matchAll(
    /row\[ProfileSections\.(\w+)\]\s*=\s*(\w+)\(|row\.Remove\(ProfileSections\.(\w+)\)/g,
  )) {
    const [, assigned, helper, removed] = m;
    if (removed) {
      delete out[wireKey(removed)];
      continue;
    }
    const cell = HELPER[helper];
    if (!cell) throw new Error(`matrix parser: unknown rule helper ${helper}() on ${assigned}`);
    out[wireKey(assigned)] = cell;
  }
  return out;
}

function serverMatrix(): Record<string, Row> {
  const src = readFileSync(POLICIES, "utf8");

  const plain = (name: string) => rowCells(memberBody(src, name));
  const rows: Record<string, Row> = {
    Reception: plain("Reception"),
    Clinician: plain("Clinician"),
    Diagnostics: plain("Diagnostics"),
    Pharmacy: plain("Pharmacy"),
    MedicalApproval: plain("MedicalApproval"),
    CaseManager: plain("CaseManager"),
    Finance: plain("Finance"),
    BeneficiaryMgmt: plain("BeneficiaryMgmt"),
    PlatformAdmin: plain("PlatformAdmin"),
  };
  rows.CallCentre = derivedRow(memberBody(src, "CallCentre"), rows.Reception);
  rows.Doctor = derivedRow(memberBody(src, "Doctor"), rows.Clinician);
  rows.MedicalDirector = derivedRow(memberBody(src, "MedicalDirector"), rows.MedicalApproval);

  // `["reception"] = Reception(),` — the role → builder table itself, so a role added there without a cell
  // here is caught rather than assumed.
  const byRole: Record<string, Row> = {};
  const table = src.slice(src.indexOf("IReadOnlyDictionary<string, IReadOnlyDictionary<string, SectionRule>> Matrix"));
  for (const m of table.matchAll(/\["(\w+)"\]\s*=\s*(\w+)\(\)/g)) {
    const [, role, builder] = m;
    const row = rows[builder];
    if (!row) throw new Error(`matrix parser: role ${role} uses unmodelled builder ${builder}()`);
    byRole[role] = row;
  }
  return byRole;
}

describe("the dev fixture's profile matrix mirrors the server's", () => {
  const server = serverMatrix();

  it("parses a matrix that is actually there", () => {
    // Guards the guard: a parser that silently matched nothing would make every assertion below vacuous.
    expect(Object.keys(server).length, "roles parsed out of ProfilePolicies.cs").toBeGreaterThan(15);
    expect(server.reception, "a known row should have been parsed").toBeDefined();
    expect(server.reception.encounters).toBe("vis");
    expect(server.doctor.investigations, "the doctor's clinical cells are treating-conditional").toBe("treating");
    expect(server.case_manager.investigations, "the case manager's are existence-only").toBe("res");
  });

  it("covers exactly the same roles", () => {
    expect(Object.keys(PROFILE_SECTION_MATRIX).sort()).toEqual(Object.keys(server).sort());
  });

  it("agrees cell for cell, on every role", () => {
    for (const role of Object.keys(server)) {
      expect(PROFILE_SECTION_MATRIX[role], `role ${role}`).toEqual(server[role]);
    }
  });

  it("still says reception cannot see prescriptions or investigations", () => {
    // The specific projection that started this: not an extra assertion so much as the one a reader of this
    // file will be looking for, stated where they will find it.
    expect(PROFILE_SECTION_MATRIX.reception.prescriptions).toBeUndefined();
    expect(PROFILE_SECTION_MATRIX.reception.investigations).toBeUndefined();
    expect(PROFILE_SECTION_MATRIX.doctor.prescriptions).toBe("treating");
  });
});

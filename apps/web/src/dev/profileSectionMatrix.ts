/**
 * The patient-profile section matrix, mirrored for the DEV FIXTURE ONLY.
 *
 * <b>Why this file exists.</b> The real projection is server-side, in `libs/authz/ProfilePolicies.cs`: the
 * profile a caller receives contains only the sections their role may see, and a section with no cell in the
 * matrix is not sent at all. The fixture client ignored all of that and answered every request with all
 * fifteen sections, whatever role was signed in — so the dev build showed a receptionist the prescriptions
 * and investigation results that production correctly withholds from them.
 *
 * That is worse than a cosmetic difference. It means the screen's behaviour under a real projection — six
 * panels in the History tab for a doctor, two for reception — was never exercised outside production, and a
 * regression in how the screen handles a withheld or absent section could not be seen in dev or caught by a
 * fixture-driven test. The whole point of a contract-valid fixture is that the shapes match; the role
 * projection is part of the shape.
 *
 * <b>Why a copy rather than a shared source.</b> The matrix is C# and this is TypeScript; there is no runtime
 * either can import from the other. A copy is therefore a drift risk, so it is not left to discipline —
 * `test/profile-fixture-matrix.test.ts` parses `ProfilePolicies.cs` and fails if the two disagree about any
 * role's cells. Editing the server matrix without editing this file is a red build.
 *
 * <b>This is not an authorization decision and must never become one.</b> It shapes a fixture payload. The
 * enforcement lives on the server and is tested there; nothing here is consulted by a live build, and this
 * module is inside `src/dev/`, which a live build resolves away entirely.
 */

/**
 * The rule KINDS the server matrix uses, kept distinct rather than pre-resolved to a state so the drift test
 * can compare like for like — collapsing `treating` into `vis` here would make the check unable to tell a
 * conditional cell from an unconditional one.
 */
export type MatrixCell =
  /** Unconditionally visible. */
  | "vis"
  /** Visible while a treating relationship holds; Restricted otherwise. */
  | "treating"
  /** Visible while an active case assignment covers the beneficiary; Restricted otherwise. */
  | "assigned"
  /** Existence-only: the role knows the section is there and may request access. Never visible. */
  | "res";

/** Section keys, spelled as the wire spells them. */
export type MatrixRow = Readonly<Record<string, MatrixCell>>;

const RECEPTION: MatrixRow = {
  header: "vis", alerts: "vis", coverage: "vis", encounters: "vis", authorizations: "vis",
  referrals: "vis", documents: "res", notes: "vis", timeline: "vis", callHistory: "vis",
};

/** Reception's row, minus alerts, with the full call history. */
const CALL_CENTRE: MatrixRow = (() => {
  const { alerts: _dropped, ...rest } = RECEPTION;
  return { ...rest, callHistory: "vis" } as MatrixRow;
})();

const CLINICIAN: MatrixRow = {
  header: "vis", alerts: "vis", coverage: "treating", pastMedicalHistory: "treating",
  encounters: "treating", investigations: "treating", prescriptions: "treating",
  authorizations: "treating", referrals: "treating", documents: "treating", notes: "treating",
  caseManagement: "treating", timeline: "treating", callHistory: "treating",
};

/** The clinician row plus three administrative sections that carry no treating condition. */
const DOCTOR: MatrixRow = { ...CLINICIAN, authorizations: "vis", timeline: "vis", callHistory: "vis" };

const DIAGNOSTICS: MatrixRow = { header: "vis", alerts: "vis", investigations: "vis" };

const PHARMACY: MatrixRow = { header: "vis", alerts: "vis", coverage: "vis", prescriptions: "vis" };

const MEDICAL_APPROVAL: MatrixRow = {
  header: "vis", alerts: "vis", coverage: "vis", pastMedicalHistory: "vis", encounters: "vis",
  investigations: "vis", prescriptions: "vis", authorizations: "vis", referrals: "vis",
  documents: "vis", notes: "vis", caseManagement: "vis", timeline: "vis", callHistory: "vis",
};

const MEDICAL_DIRECTOR: MatrixRow = { ...MEDICAL_APPROVAL, financial: "vis", callHistory: "vis" };

const CASE_MANAGER: MatrixRow = {
  header: "vis", alerts: "vis", coverage: "assigned", pastMedicalHistory: "assigned",
  encounters: "assigned", investigations: "res", prescriptions: "res", authorizations: "assigned",
  referrals: "assigned", documents: "assigned", notes: "assigned", caseManagement: "assigned",
  timeline: "assigned", callHistory: "assigned",
};

const FINANCE: MatrixRow = {
  header: "vis", coverage: "vis", encounters: "vis", authorizations: "vis", documents: "res",
  notes: "vis", financial: "vis", timeline: "vis", callHistory: "vis",
};

const BENEFICIARY_MGMT: MatrixRow = {
  header: "vis", alerts: "vis", coverage: "vis", pastMedicalHistory: "res", encounters: "vis",
  authorizations: "vis", referrals: "vis", documents: "vis", notes: "vis", timeline: "vis",
  callHistory: "vis",
};

const PLATFORM_ADMIN: MatrixRow = { header: "vis", timeline: "vis" };

/** Keyed by ISSUER role — the flat lower-case names the token carries, which is what the server matches. */
export const PROFILE_SECTION_MATRIX: Readonly<Record<string, MatrixRow>> = {
  reception: RECEPTION,
  call_center: CALL_CENTRE,
  call_center_supervisor: CALL_CENTRE,
  doctor: DOCTOR,
  nurse: CLINICIAN,
  lab_tech: DIAGNOSTICS,
  imaging_tech: DIAGNOSTICS,
  radiology_tech: DIAGNOSTICS,
  pharmacist: PHARMACY,
  pharmacy_supervisor: PHARMACY,
  medical_approval: MEDICAL_APPROVAL,
  approvals_team: MEDICAL_APPROVAL,
  medical_director: MEDICAL_DIRECTOR,
  case_manager: CASE_MANAGER,
  finance: FINANCE,
  finance_approver: FINANCE,
  claims_officer: FINANCE,
  claims_reviewer: FINANCE,
  beneficiary_mgmt: BENEFICIARY_MGMT,
  beneficiary_mgmt_supervisor: BENEFICIARY_MGMT,
  org_admin: PLATFORM_ADMIN,
  super_admin: PLATFORM_ADMIN,
};

/**
 * Resolve one section for a set of issuer roles, widest-wins — the same rule the server applies when a user
 * carries several roles, so a supervisor who also holds the officer role gets the wider of the two cells
 * rather than whichever happened to be checked first.
 *
 * The ABAC conditions are treated as SATISFIED here. A dev fixture has no real treating relationship or case
 * assignment to evaluate, and the useful default is the one that shows the screen doing its job; the withheld
 * states are demonstrated deliberately on one beneficiary instead (see `WITHHELD_STATE_DEMO_ID`).
 */
export function sectionStateFor(
  roles: readonly string[],
  section: string,
): "Visible" | "Restricted" | null {
  let best: "Visible" | "Restricted" | null = null;
  for (const role of roles) {
    const cell = PROFILE_SECTION_MATRIX[role]?.[section];
    if (!cell) continue;
    const state = cell === "res" ? "Restricted" : "Visible";
    if (state === "Visible") return "Visible"; // widest possible; nothing can beat it
    best ??= state;
  }
  return best;
}

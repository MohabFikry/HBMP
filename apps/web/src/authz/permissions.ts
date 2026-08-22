/**
 * Permission catalog + role→permission mapping for the Mersal HBMP portals (Phase 9.2).
 *
 * This is the UI-side mirror of 11-permission-matrix.md — it drives *which routes mount and which menu
 * items render*, so a user never sees a route/menu item they cannot use (US-071, min-necessary). The
 * server remains the source of truth and re-authorizes every call; this catalog must stay consistent with
 * the backend `libs/authz` bundles. Effective permissions come from the session (here seeded per role for
 * the dev auth stub; in production they are derived from the token + admin-service EffectiveRoles).
 *
 * The six hard rules from the matrix are encoded structurally: e.g. Reception has NO `emr.*`, Finance has
 * NO clinical/diagnosis permission, Pharmacy has NO `lab.result`, Lab has NO `prescriptions.*`.
 */

export type Permission =
  // Reception / access
  | "eligibility.check"
  | "queue.reception"
  | "appointments.read"
  // Booking is a separate grant from reading the board: the call centre may book across branches but must
  // never check anyone in, and a read-only viewer must get neither.
  | "appointments.book"
  | "checkin.write"
  // Clinical (treating-gated on the server)
  | "emr.read"
  | "emr.write"
  | "orders.place"
  | "prescriptions.write"
  | "referrals.write"
  | "results.inbox"
  | "vitals.write"
  // 25.7 — Branch Management (design 42 §6). BOTH branch roles hold ALL of these: one permission set, two
  // reaches. The manager's extra affordance (comparing the six clinics) is derived from REACH, not from an
  // extra permission — see the catalog's `reachScoped` flag.
  | "branch.practitioners"
  | "branch.roster"
  | "branch.licences"
  | "branch.inventory"
  // Lab / radiology
  | "lab.queue"
  | "lab.consume"
  | "lab.result.upload"
  | "radiology.queue"
  | "radiology.consume"
  | "radiology.result.upload"
  // 29.2b — the EXTERNAL delivering provider (physiotherapy centres, dialysis units, outside clinics).
  | "procedure.queue"
  | "procedure.deliver"
  | "procedure.report"
  // Pharmacy
  | "pharmacy.queue"
  | "pharmacy.dispense"
  | "pharmacy.substitution"
  // Approvals
  | "approvals.worklist"
  // ADR-0034 — the REGISTER (every authorization, including what counters and benches delivered), as
  // distinct from the worklist, which is the queue of things waiting for a decision.
  | "approvals.register"
  | "approvals.decide"
  | "approvals.manual"
  | "approvals.emergency"
  | "approvals.sla"
  | "director.breakglass"
  // 2026-08-11 audit — the two oversight sections the director portal gained.
  | "director.utilization"
  | "director.cost"
  // Beneficiary management / registration
  | "beneficiary.register"
  | "beneficiary.manage"
  | "beneficiary.status"
  | "beneficiary.approvals"
  // Case management
  | "case.read"
  /*
   * 33.7 — `case.beneficiary360` was RETIRED. Its section routed to `<MyCases />`, the same component the
   * "My Cases" section routes to, so the rail offered one screen twice under two names — the duplication the
   * lab and pharmacy portals both had removed by 32.6. The 360 itself is not gone: it is the detail panel
   * that opens beside the list when a case is selected, which is where it always rendered.
   */
  | "case.coordinate"
  | "case.escalations"
  // Patient profile (Phase 20). `profile.read` is held by every role the design-39 §4 matrix names — the
  // COARSE gate only; what each of them receives is decided per section on the SERVER. `profile.export` is
  // narrower: copying a record out of the platform is a different act from looking at it.
  | "profile.read"
  | "profile.export"
  // Call centre (Phase 15) — NO clinical reach by construction
  | "callcentre.workspace"
  | "callcentre.history"
  // Claims (Phase 10b) — codes + amounts only, NO diagnosis by construction (finance-parity)
  | "claims.worklist"
  | "claims.reconciliation"
  | "claims.adjudicate"
  | "claims.insights"
  // Finance (NO clinical/diagnosis by construction)
  | "finance.utilization"
  | "finance.settlements"
  | "finance.summaries"
  | "finance.export"
  // Policy administration (Phase 19) — the benefit PRODUCT and the membership book. No clinical permission
  // exists here by construction: policy administration reads entitlement and money, never a diagnosis.
  | "policy.payers"
  | "policy.plans"
  | "policy.policies"
  | "policy.members"
  | "policy.groups"
  | "policy.utilization"
  | "policy.bulk"
  // 19.6b — the analytical layer. Its own permission rather than riding on `policy.utilization`, because the
  // dashboard aggregates across the WHOLE book (payers, plans, cost) while utilization answers one scope's
  // consumption. Granting the second should not silently grant the first.
  | "policy.analytics"
  // Network / provider admin
  | "provider.directory"
  | "provider.onboarding"
  | "provider.contracts"
  | "provider.locations"
  | "provider.performance"
  // 14.5 — the clinical profile behind a user: specialty + the clinics they work at. Its own grant rather
  // than folding into `provider.locations`, because this administers PEOPLE who can be booked, and it is the
  // upstream of every specialty/doctor filter on the booking screen.
  | "provider.practitioners"
  // Network tiers (19.1b). Held by BOTH the Network Team and policy administration — but only the Network
  // Team may write, which is a capability (see `mayAdministerTiers`), not a second permission. Two
  // permissions would have let the two lists drift until a tier had two owners.
  | "network.tiers"
  // Org / super admin
  | "admin.users"
  | "admin.policies"
  | "admin.masterdata"
  | "admin.tenants"
  | "admin.audit"
  | "admin.config"
  // User & access model (Phase 21.6, design 40)
  | "admin.access"
  | "admin.programs"
  // Medical director oversight
  | "director.dashboards"
  | "director.oversight"
  | "director.quality"
  | "director.escalations"
  // ADR-0035 — clinical governance the supervisor holds. Its own key rather than reusing `admin.masterdata`:
  // that one means "the platform-admin view of every code system", and this one means "the four clinical
  // vocabularies, editable". Sharing a key would give whoever holds either the reach of both.
  | "director.masterlists"
  // ADR-0035 §5 — author the engine's routing/SLA rules. NOT held by medical_approval.
  | "director.engine"
  // Cross-cutting — every role has an in-app inbox (self-service, server row-filtered by recipient).
  | "notification.read";

export type Role =
  | "reception"
  | "doctor"
  | "nurse"
  | "lab"
  | "radiology"
  | "procedure_provider"
  | "pharmacy"
  | "medical_approval"
  | "beneficiary_mgmt"
  | "beneficiary_mgmt_supervisor"
  | "case_manager"
  | "call_center"
  | "claims_officer"
  | "finance"
  | "provider_admin"
  | "policy_admin"
  | "org_admin"
  | "super_admin"
  | "medical_director"
  // 25.7 — the people who run a clinic. Identical permissions; they differ only in how many branches they
  // reach (ADR-0029).
  | "branch_coordinator"
  | "clinics_manager";

/**
 * Role → granted permissions. Derived from 11-permission-matrix §2/§3. Kept deliberately explicit so the
 * min-necessary hard rules are auditable at a glance (no clinical perms in Reception/Finance, etc.).
 */
/**
 * 25.7 — the ONE permission list both branch roles hold (design 42 §1). Declared once and referenced twice
 * below: a coordinator and a clinics manager may do exactly the same things, and differ only in how many
 * branches those things reach.
 */
const BRANCH_ROLE_PERMISSIONS: Permission[] = [
  // reception's five, verbatim
  "eligibility.check",
  "queue.reception",
  "appointments.read",
  "appointments.book",
  "checkin.write",
  // and the four branch authorities
  "branch.practitioners",
  "branch.roster",
  "branch.licences",
  "branch.inventory",
];

export const rolePermissions: Record<Role, Permission[]> = {
  reception: ["eligibility.check", "queue.reception", "appointments.read", "appointments.book", "checkin.write"],

  /*
   * 25.7 — THE BRANCH ROLES SHARE ONE PERMISSION LIST, LITERALLY.
   *
   * Not two lists a test compares: one constant, referenced twice, so the SPA cannot drift even between test
   * runs. The server-side equality is pinned separately by BranchRoleScopeParityTests and
   * BranchRoleSeedTests — three independent statements of one invariant, because this is the rule the whole
   * phase rests on (design 42 §7 rule 1).
   *
   * Reception's five, verbatim, plus the four branch authorities. No `emr.read`: they run the clinic, they
   * do not read clinical notes.
   */
  branch_coordinator: BRANCH_ROLE_PERMISSIONS,
  clinics_manager: BRANCH_ROLE_PERMISSIONS,
  doctor: [
    "emr.read",
    "emr.write",
    "orders.place",
    "prescriptions.write",
    "referrals.write",
    "results.inbox",
    "vitals.write",
    "appointments.read",
  ],
  nurse: ["emr.read", "vitals.write", "results.inbox", "appointments.read"],
  lab: ["lab.queue", "lab.consume", "lab.result.upload"],
  radiology: ["radiology.queue", "radiology.consume", "radiology.result.upload"],
  // 29.2b (design 45 §2b) — deliberately NARROW. A delivering centre records sessions and reports back; it
  // holds no result-upload, no prescription and no order-composition permission at all.
  procedure_provider: ["procedure.queue", "procedure.deliver", "procedure.report"],
  pharmacy: ["pharmacy.queue", "pharmacy.dispense", "pharmacy.substitution"],
  medical_approval: ["approvals.worklist", "approvals.register", "approvals.decide", "approvals.manual", "approvals.emergency", "approvals.sla"],
  // Beneficiary management owns the MEMBERSHIP book: who is enrolled, in which group, on which plan, and
  // what they have used. It does NOT own the benefit product — no payers, no plan versions — because the
  // person enrolling a member must not also be the person who decides what that plan pays for.
  beneficiary_mgmt: [
    "beneficiary.register",
    // The officer PREPARES approvals (verifies documents, binds coverage); the decision buttons are
    // supervisor-only and the server enforces it (urn:hbmp:approver-required).
    "beneficiary.approvals",
    // `beneficiary.manage`, `beneficiary.status` and `policy.utilization` were dropped with the sections they
    // gated (19.7 nav rework). A permission granted to a role with nothing behind it is one nobody can reason
    // about: it reads as access that exists somewhere.
    "eligibility.check",
    "policy.members",
    "policy.groups",
    "policy.bulk",
    "policy.analytics",
  ],
  /*
   * 19.7's approver persona (US-003) — a SUPERSET of the officer, plus the decision.
   *
   * It used to be a strict subset: no register pen, no bulk import, no analytics. The reasoning was
   * separation of duties — the person who creates a record must not be the person who activates it — but the
   * implementation was withholding a menu item, and that is the wrong lever twice over.
   *
   * It did not enforce the rule. The server's check was `is the caller a supervisor`, never `did the caller
   * file THIS application`, so nothing stopped a supervisor from registering someone (the permission was
   * absent from the nav, not from the API) and approving them. The real rule now lives where it belongs:
   * patient-service refuses a decision on a registration whose `created_by` is the actor
   * (`urn:hbmp:self-approval`), which is stricter than the old arrangement and true regardless of what any
   * menu shows.
   *
   * And it made the supervisor less useful than the people they supervise. A supervisor who cannot open the
   * bulk import they are asked about, or the analytics they report from, is a supervisor who borrows an
   * officer's screen — which is a worse audit trail than giving them their own.
   */
  beneficiary_mgmt_supervisor: [
    "beneficiary.register",
    "beneficiary.approvals",
    "eligibility.check",
    "policy.members",
    "policy.groups",
    "policy.bulk",
    "policy.analytics",
  ],
  /*
   * 33.7 — THE ROLE HELD THREE READ PERMISSIONS AND THE TOKEN HELD THREE SCOPES.
   *
   * The 0001 seed grants `case_manager` `case:read`, `case:write` AND `case:manage`. Design 11 §3.3 gives
   * the role `C🟠ASG R🟠ASG U🟠ASG` on `approval_case`, and design 10 §3.11 lists "open/track cases;
   * coordinate referrals; manage care plans" among its key capabilities. case-service implements nine write
   * endpoints against those scopes.
   *
   * The SPA reached none of them. A coordination task could be listed and never completed, an escalation
   * could be read and never raised or resolved, and a case could never be closed — so a caseworker's list
   * only ever grew. `case.coordinate` is the permission behind the affordances that close that loop.
   *
   * Assignment (`case:manage`, POST /assign and /unassign) is deliberately still absent: who holds a case is
   * a supervisor's decision and there is no supervisor surface here to make it from. Design 52 §5.
   */
  case_manager: ["case.read", "case.coordinate", "case.escalations"],
  // Call Centre — a call workspace + call history. No clinical permission exists here (min-necessary).
  // `appointments.book` — RESERVE only. The call centre holds appointment:reserve, not appointment:write, so
  // it can hold and move a time but cannot record an arrival; `checkin.write` is deliberately absent and the
  // server refuses check-in regardless of what the nav shows.
  call_center: ["callcentre.workspace", "callcentre.history", "appointments.read", "appointments.book"],
  // Claims officer — worklist + reconciliation + PHI-free KPIs. No clinical/diagnosis permission (finance-parity).
  claims_officer: ["claims.worklist", "claims.adjudicate", "claims.reconciliation", "claims.insights"],
  // Finance gets the analytics section too: the financial and network views are exactly the money questions
  // this role exists to answer, and reporting-service gates those two views on the FINANCIAL zone anyway —
  // so the section is visible and the views a finance user may not read are refused by the server, not hidden
  // by a nav rule the server does not know about.
  finance: ["finance.utilization", "finance.settlements", "finance.summaries", "finance.export", "policy.analytics"],
  provider_admin: [
    "provider.directory",
    "provider.onboarding",
    "provider.contracts",
    "provider.locations",
    "provider.performance",
    // The Network Team already owns provider onboarding and provider-scoped users; the practitioner record
    // is the same administration for Mersal's OWN clinicians, and the writes need provider:write — which
    // org_admin does not hold, so the section belongs here rather than in the admin console.
    "provider.practitioners",
    "network.tiers",
  ],
  // Policy administration — the benefit product (payers, plans, plan versions) and the policies written
  // against it. Sees network tiers READ-ONLY: it prices what a member pays AT a tier, while the Network
  // Team decides WHICH tier a provider sits in (mirrors provider-service's NetworkTierGate).
  policy_admin: [
    "policy.payers",
    "policy.plans",
    "policy.policies",
    "policy.members",
    "policy.groups",
    "policy.utilization",
    "policy.bulk",
    "policy.analytics",
    "network.tiers",
  ],
  // 28.10 — `admin.tenants` is NOT here. The tenant registry's read and write are both
  // `AdminPolicies.ManageTenant`, which names `super_admin` alone, so an org admin holding this permission
  // could only ever reach a screen that answered 403. Removing it takes nothing away that worked.
  org_admin: ["admin.users", "admin.policies", "admin.masterdata", "admin.audit", "admin.config", "admin.access"],
  // Super admin can administer globally; sensitive PHI reads remain break-glass on the server, not routine UI.
  // `admin.programs` is super-admin only: enablement is set by Mersal programme administration, and a tenant
  // that can switch on its own programmes is not gated at all (design 40 §4, A4).
  super_admin: ["admin.users", "admin.policies", "admin.masterdata", "admin.tenants", "admin.audit", "admin.config", "admin.access", "admin.programs"],
  /*
   * `approvals.sla` was already here and had no door: its only section was declared on the APPROVALS portal,
   * which `portalsForRoles` never hands a director. The section now exists on `/director` too — see the note
   * in portals/catalog.ts.
   *
   * `director.utilization` and `director.cost` are new, and are CLIENT-side nav gates only. The server
   * authority behind them is unchanged: `reporting:read` and `reporting:read-financial`, both of which
   * medical_director has held since the 0001 seed. Nothing here grants anything.
   */
  medical_director: ["director.dashboards", "director.utilization", "director.cost", "director.oversight", "director.quality", "director.escalations", "director.masterlists", "director.engine", "approvals.sla", "director.breakglass"],
};

/**
 * Roles that may generate the role-projected PRINT/EXPORT summary of a patient profile.
 *
 * Mirrors the `profile:export` grants in identity `0009_profile_scopes.sql`, and is deliberately narrower than
 * `profile.read`: copying a patient record out of the platform is a different act from looking at it. Reception
 * hands over a card, not a clinical summary; finance and claims export through the phase-19.5b extract engine,
 * which has its own controls.
 */
const PROFILE_EXPORTERS: ReadonlySet<Role> = new Set<Role>([
  "doctor", "nurse", "medical_approval", "medical_director", "case_manager", "beneficiary_mgmt", "super_admin",
]);

/**
 * Every role the design-39 §4 matrix names may OPEN a profile — `profile.read` is the coarse gate, and what
 * each of them actually receives is decided per section on the server. The roles absent here are the ones with
 * no row in that matrix at all.
 */
const NON_PROFILE_ROLES: ReadonlySet<Role> = new Set<Role>(["provider_admin", "policy_admin"]);

/** Effective permissions for a role (deduplicated). Every role additionally carries `notification.read` —
 * the in-app inbox is a self-service surface available in every portal (the server row-filters by recipient). */
export function permissionsForRole(role: Role): ReadonlySet<Permission> {
  const perms = new Set<Permission>([...rolePermissions[role], "notification.read"]);
  if (!NON_PROFILE_ROLES.has(role)) perms.add("profile.read");
  if (PROFILE_EXPORTERS.has(role)) perms.add("profile.export");
  return perms;
}

/**
 * The permissions of somebody holding SEVERAL portal roles.
 *
 * A plain union, and deliberately so: `permissionsForRole` already adds the cross-cutting grants
 * (`notification.read`, `profile.read`, `profile.export`) per role, and unioning the finished sets keeps
 * exactly one place where those rules live. Computing them again over the merged role list would be a
 * second implementation of the same policy, free to disagree with the first.
 *
 * This does NOT widen anybody. Each portal's nav and routes are gated by its own catalog sections, so a
 * doctor who is also an org admin sees the clinician rail in `/clinician` and the administration rail in
 * `/admin` — never one inside the other. And the server re-authorizes every call from the token, which is
 * where the real answer has always been.
 */
export function unionPermissions(roles: readonly Role[]): ReadonlySet<Permission> {
  const out = new Set<Permission>();
  for (const role of roles) for (const p of permissionsForRole(role)) out.add(p);
  return out;
}

export function hasPermission(perms: ReadonlySet<Permission>, required: Permission): boolean {
  return perms.has(required);
}

/**
 * The issuer roles `provider-service`'s `NetworkAdmin` rule names. One list, quoted from the server.
 *
 * ISSUER names, not portal names — see {@link mayAdministerTiers} for why that distinction is the whole
 * point here.
 */
const TIER_ADMIN_ISSUER_ROLES: ReadonlySet<string> = new Set(["network_team", "org_admin", "super_admin"]);

/**
 * May this caller CREATE or CHANGE a network tier, as opposed to reading one?
 *
 * The Network Team owns the tier structure; policy administration prices benefits at a tier and must be able
 * to see the tiers it is pricing against, but not to invent one. Expressed as a capability rather than as a
 * second permission so the read list and the write list cannot drift apart — the server's `NetworkTierGate`
 * draws exactly this line and returns 403 either way.
 *
 * ## 33.7 — it takes ISSUER roles now, and that is a fix rather than a refactor
 *
 * This used to read `role === "provider_admin" || role === "org_admin" || role === "super_admin"` against
 * the PORTAL role. `ROLE_MAP` maps two issuer roles onto that one portal name:
 *
 *     ["provider_admin", "provider_admin"],   // one provider's own administrator — T4, ABAC provider-bound
 *     ["network_team",   "provider_admin"],   // Mersal's Network Team — T2, tenant-wide
 *
 * The server rule names `network_team`, `org_admin`, `super_admin` and has never named `provider_admin`. So
 * the mirror answered YES for a provider's own administrator, who was shown Create tier, Revoke assignment
 * and (from 33.7) Assign — every one refused with `urn:hbmp:network-tier-access-denied`. The doc comment
 * claiming this "draws exactly this line" was the thing that made it hard to spot: it described the intent
 * accurately and the code did something else.
 *
 * The two roles still share one portal, which is a wider problem than this function — design 07 FR-IAM-003
 * lists them as separate portals and design 11 §3.3 gives them different rows. Recorded in design 52 §5.
 */
export function mayAdministerTiers(issuerRoles: readonly string[] | null | undefined): boolean {
  return (issuerRoles ?? []).some((r) => TIER_ADMIN_ISSUER_ROLES.has(r));
}

/**
 * May this caller read the NETWORK-WIDE provider roll-up?
 *
 * `provider-service` answers `GET /api/v1/metrics` with 403 to any provider-scoped caller: a provider must
 * not learn the shape of the network it competes in. `provider_admin` is provider-scoped
 * (`HbmpPrincipal.ProviderScopedRoles`), and shares the Network Team's portal — so the Performance section
 * has to ask whose portal this actually is, or it offers a section that can only refuse.
 *
 * The same issuer-role list as tier administration, and not a coincidence: both answer "is this the Network
 * Team, or somebody who happens to land in their portal".
 */
export function mayReadTheNetworkRollup(issuerRoles: readonly string[] | null | undefined): boolean {
  return mayAdministerTiers(issuerRoles);
}

/**
 * May this role cancel somebody else's note, or make a back-dated membership change?
 *
 * The server calls it `policy:supervise` and enforces it; this mirror only decides whether the affordance is
 * offered. An operator who is shown a button they will be refused learns nothing about why.
 */
export function maySupervisePolicy(role: Role | null | undefined): boolean {
  return role === "policy_admin" || role === "org_admin" || role === "super_admin";
}

/**
 * May this caller AUTHOR the benefit product — payers, plans, plan versions?
 *
 * The server calls it `policy:admin` and `PolicyPolicies.Rules()` names exactly three roles for it. Claims,
 * finance and the network team hold `policy:read` and reach these screens legitimately — they adjudicate
 * against the terms — so this mirror decides whether the write affordances are RENDERED at all. An operator
 * shown four buttons that each answer 403 learns that the screen is broken; one shown none learns whose job
 * it is.
 *
 * A mirror, never the enforcement: the server refuses either way.
 */
/**
 * May this caller administer MEMBERSHIP — issue a policy, amend its terms, suspend or end it?
 *
 * The server calls it `policy:write`, and its role list is deliberately different from `policy:admin`'s:
 * a policy is a membership artefact, not a benefit product, and Beneficiary Management has issued contracts
 * since 19.2. Putting an EDIT of the same row behind the product scope would mean the team that issues a
 * contract cannot correct its dates.
 *
 * A mirror, never the enforcement: the server refuses either way.
 */
export function mayAdministerMembership(role: Role | null | undefined): boolean {
  return role === "beneficiary_mgmt" || role === "policy_admin"
    || role === "org_admin" || role === "super_admin";
}

export function mayAdministerBenefitProduct(role: Role | null | undefined): boolean {
  return role === "policy_admin" || role === "org_admin" || role === "super_admin";
}

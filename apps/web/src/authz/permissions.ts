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
  | "checkin.write"
  // Clinical (treating-gated on the server)
  | "emr.read"
  | "emr.write"
  | "orders.place"
  | "prescriptions.write"
  | "referrals.write"
  | "results.inbox"
  | "vitals.write"
  // Lab / imaging
  | "lab.queue"
  | "lab.consume"
  | "lab.result.upload"
  | "imaging.queue"
  | "imaging.consume"
  | "imaging.result.upload"
  // Pharmacy
  | "pharmacy.queue"
  | "pharmacy.dispense"
  | "pharmacy.substitution"
  // Approvals
  | "approvals.worklist"
  | "approvals.decide"
  | "approvals.manual"
  | "approvals.emergency"
  | "approvals.sla"
  // Beneficiary management / registration
  | "beneficiary.register"
  | "beneficiary.manage"
  | "beneficiary.status"
  // Case management
  | "case.read"
  | "case.beneficiary360"
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
  // Medical director oversight
  | "director.dashboards"
  | "director.oversight"
  | "director.quality"
  | "director.escalations"
  // Cross-cutting — every role has an in-app inbox (self-service, server row-filtered by recipient).
  | "notification.read";

export type Role =
  | "reception"
  | "doctor"
  | "nurse"
  | "lab"
  | "imaging"
  | "pharmacy"
  | "medical_approval"
  | "beneficiary_mgmt"
  | "case_manager"
  | "call_center"
  | "claims_officer"
  | "finance"
  | "provider_admin"
  | "policy_admin"
  | "org_admin"
  | "super_admin"
  | "medical_director";

/**
 * Role → granted permissions. Derived from 11-permission-matrix §2/§3. Kept deliberately explicit so the
 * min-necessary hard rules are auditable at a glance (no clinical perms in Reception/Finance, etc.).
 */
export const rolePermissions: Record<Role, Permission[]> = {
  reception: ["eligibility.check", "queue.reception", "appointments.read", "checkin.write"],
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
  imaging: ["imaging.queue", "imaging.consume", "imaging.result.upload"],
  pharmacy: ["pharmacy.queue", "pharmacy.dispense", "pharmacy.substitution"],
  medical_approval: ["approvals.worklist", "approvals.decide", "approvals.manual", "approvals.emergency", "approvals.sla"],
  // Beneficiary management owns the MEMBERSHIP book: who is enrolled, in which group, on which plan, and
  // what they have used. It does NOT own the benefit product — no payers, no plan versions — because the
  // person enrolling a member must not also be the person who decides what that plan pays for.
  beneficiary_mgmt: [
    "beneficiary.register",
    "beneficiary.manage",
    "beneficiary.status",
    "eligibility.check",
    "policy.members",
    "policy.groups",
    "policy.utilization",
    "policy.bulk",
    "policy.analytics",
  ],
  case_manager: ["case.read", "case.beneficiary360", "case.escalations"],
  // Call Centre — a call workspace + call history. No clinical permission exists here (min-necessary).
  call_center: ["callcentre.workspace", "callcentre.history", "appointments.read"],
  // Claims officer — worklist + reconciliation + PHI-free KPIs. No clinical/diagnosis permission (finance-parity).
  claims_officer: ["claims.worklist", "claims.reconciliation", "claims.insights"],
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
  org_admin: ["admin.users", "admin.policies", "admin.masterdata", "admin.tenants", "admin.audit", "admin.config"],
  // Super admin can administer globally; sensitive PHI reads remain break-glass on the server, not routine UI.
  super_admin: ["admin.users", "admin.policies", "admin.masterdata", "admin.tenants", "admin.audit", "admin.config"],
  medical_director: ["director.dashboards", "director.oversight", "director.quality", "director.escalations", "approvals.sla"],
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

export function hasPermission(perms: ReadonlySet<Permission>, required: Permission): boolean {
  return perms.has(required);
}

/**
 * May this role CREATE or CHANGE a network tier, as opposed to reading one?
 *
 * The Network Team owns the tier structure; policy administration prices benefits at a tier and must be able
 * to see the tiers it is pricing against, but not to invent one. Expressed as a capability over the role
 * rather than as a second permission so the read list and the write list cannot drift apart — the server's
 * `NetworkTierGate` draws exactly this line and returns 403 either way.
 */
export function mayAdministerTiers(role: Role | null | undefined): boolean {
  return role === "provider_admin" || role === "org_admin" || role === "super_admin";
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

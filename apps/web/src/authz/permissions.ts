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
  // Finance (NO clinical/diagnosis by construction)
  | "finance.utilization"
  | "finance.settlements"
  | "finance.summaries"
  | "finance.export"
  // Network / provider admin
  | "provider.directory"
  | "provider.onboarding"
  | "provider.contracts"
  | "provider.locations"
  | "provider.performance"
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
  | "director.escalations";

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
  | "finance"
  | "provider_admin"
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
  beneficiary_mgmt: ["beneficiary.register", "beneficiary.manage", "beneficiary.status", "eligibility.check"],
  case_manager: ["case.read", "case.beneficiary360", "case.escalations"],
  finance: ["finance.utilization", "finance.settlements", "finance.summaries", "finance.export"],
  provider_admin: ["provider.directory", "provider.onboarding", "provider.contracts", "provider.locations", "provider.performance"],
  org_admin: ["admin.users", "admin.policies", "admin.masterdata", "admin.tenants", "admin.audit", "admin.config"],
  // Super admin can administer globally; sensitive PHI reads remain break-glass on the server, not routine UI.
  super_admin: ["admin.users", "admin.policies", "admin.masterdata", "admin.tenants", "admin.audit", "admin.config"],
  medical_director: ["director.dashboards", "director.oversight", "director.quality", "director.escalations", "approvals.sla"],
};

/** Effective permissions for a role (deduplicated). */
export function permissionsForRole(role: Role): ReadonlySet<Permission> {
  return new Set(rolePermissions[role]);
}

export function hasPermission(perms: ReadonlySet<Permission>, required: Permission): boolean {
  return perms.has(required);
}

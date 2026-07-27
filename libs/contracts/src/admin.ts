import { z } from "zod";
import { zId, zInstant, zStatus } from "./common";

/**
 * Admin / platform-governance contracts (Phase 8b). The Admin portal administers WHO can access the platform —
 * role bindings, tenants, Segregation-of-Duties, access-review campaigns and break-glass — it is NOT a reader of
 * beneficiary PHI or financial CONTENT (that is break-glass only). Subject identities are shown as MASKED tokens;
 * every status renders as a non-color StatusKind chip (accessibility). Reads here are themselves audited server-side.
 */

/** A row of the access matrix — who holds which role, at what sensitivity tier, and when it needs recertifying. */
export const zRoleBinding = z.object({
  id: zId,
  subjectToken: z.string(),
  role: z.string(),
  scope: z.string(),
  tier: z.string(),
  status: zStatus,
  grantedAt: zInstant,
  reviewDueAt: zInstant.optional(),
});
export type RoleBinding = z.infer<typeof zRoleBinding>;

/** A tenant registry entry (super-admin scope). */
export const zTenantSummary = z.object({
  id: zId,
  name: z.string(),
  status: zStatus,
  createdAt: zInstant.optional(),
});
export type TenantSummary = z.infer<typeof zTenantSummary>;

/** One Segregation-of-Duties conflict rule (10-role-matrix §7) — two roles that must not be held together. */
export const zSodConflict = z.object({
  roleA: z.string(),
  roleB: z.string(),
  reason: z.string(),
});
export type SodConflict = z.infer<typeof zSodConflict>;

/** An access-review campaign — a recertification sweep of high-sensitivity grants. */
export const zAccessReviewCampaign = z.object({
  id: zId,
  name: z.string(),
  status: zStatus,
  minTier: z.string().optional(),
  dueAt: zInstant.optional(),
});
export type AccessReviewCampaign = z.infer<typeof zAccessReviewCampaign>;

/** A break-glass grant on the governance dashboard — an emergency, time-boxed, dual-controlled access. */
export const zBreakGlassGrant = z.object({
  id: zId,
  requesterToken: z.string(),
  reasonCode: z.string(),
  status: zStatus,
  requestedAt: zInstant,
  expiresAt: zInstant.optional(),
});
export type BreakGlassGrant = z.infer<typeof zBreakGlassGrant>;

/** A master-data version currently in force (effective-dated governance read, FR-MDM-007). */
export const zMasterDataVersion = z.object({
  id: zId,
  system: z.string(),
  code: z.string(),
  versionNo: z.number().int(),
  retired: z.boolean(),
  effectiveFrom: zInstant,
  rationale: z.string().optional(),
});
export type MasterDataVersion = z.infer<typeof zMasterDataVersion>;

/** A typed system-config entry currently in force (effective-dated, per-tenant or platform "*"). */
export const zSystemConfigEntry = z.object({
  id: zId,
  tenantId: z.string(),
  key: z.string(),
  type: z.string(),
  value: z.string(),
  versionNo: z.number().int(),
});
export type SystemConfigEntry = z.infer<typeof zSystemConfigEntry>;

/**
 * 18.C2 (audit R2 W5) — a user as the IDENTITY STORE knows them.
 *
 * The admin console read the access matrix from admin-service, which is a PROJECTION of role bindings: it
 * knows who holds which role, and nothing about the account itself. So the console could not answer the two
 * questions an administrator actually opens it to ask — is this account active, and does it have a second
 * factor? Phase 17 moved users into identity-service and the console was never repointed.
 *
 * `twoFactorEnabled` is the important column: MFA gates every admin scope and every break-glass request, and
 * until now there was no screen anywhere that showed whether a given account had one.
 */
export const zIdentityUser = z.object({
  id: zId,
  username: z.string(),
  displayName: z.string(),
  /** Masked in the UI — the console administers ACCESS, not identities. */
  tenantId: z.string().optional(),
  isActive: z.boolean(),
  twoFactorEnabled: z.boolean(),
  roles: z.array(z.string()),
});
export type IdentityUser = z.infer<typeof zIdentityUser>;

/** 18.C2 (W5) — one role→scope grant from the identity store, the live source the token issuer reads. */
export const zRoleScopeGrant = z.object({
  role: z.string(),
  scopes: z.array(z.string()),
});
export type RoleScopeGrant = z.infer<typeof zRoleScopeGrant>;

/**
 * 18.C2 (audit R2 W4) — a pending report-access request, as the approver inbox shows it.
 *
 * Deliberately clinical-free: who asked, for which order line, under what purpose, and why. The approver
 * decides whether the REQUESTER may see the result; they do not need the result to decide that, and showing
 * it would disclose the very thing being gated to anyone who can open the inbox.
 */
export const zReportAccessRequestRow = z.object({
  requestId: zId,
  orderId: zId,
  orderLineId: zId,
  /** Masked token, not the beneficiary id — the inbox is a work queue, not a patient record. */
  beneficiaryToken: z.string(),
  requestedBy: z.string(),
  requestedForRole: z.string().optional(),
  purposeCode: z.string(),
  justification: z.string(),
  requestedTtlHours: z.number().optional(),
  status: zStatus,
  createdAt: zInstant,
});
export type ReportAccessRequestRow = z.infer<typeof zReportAccessRequestRow>;

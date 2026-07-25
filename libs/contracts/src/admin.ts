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

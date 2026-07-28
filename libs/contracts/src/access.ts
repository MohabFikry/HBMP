import { z } from "zod";
import { zDate, zId, zInstant, zStatus } from "./common";

/**
 * Phase 21.6 — the user & access model as the admin screens consume it (design 40).
 *
 * These shapes carry NO clinical field and no PHI, by construction and not by convention: this is the
 * authority layer, and per invariant 3 the platform-admin flag it exposes is administrative reach only. If a
 * diagnosis, result or prescription can ever be spelled in one of these types, the separation the whole
 * phase rests on has been lost — the authz projection test asserts exactly that.
 */

/** A role held through a membership. `level` is the ordinal trust tier — lower = more privileged (§2). */
export const zMembershipRole = z.object({
  name: z.string(),
  /** Null when nothing has classified the role yet. Deliberately not defaulted to 0, which would read as
   *  "most privileged" — the one direction a missing value must never be guessed in. */
  level: z.number().int().nullable(),
});
export type MembershipRole = z.infer<typeof zMembershipRole>;

/**
 * One per-membership override — the exception path (§2).
 *
 * `reason` and `grantedBy` are REQUIRED here as well as in the schema. An exception rendered without them
 * cannot be judged at review time, and a reviewer who cannot judge one either rubber-stamps every exception
 * or escalates all of them.
 */
export const zMembershipOverride = z.object({
  id: zId,
  scope: z.string(),
  effect: z.enum(["Allow", "Deny"]),
  reason: z.string(),
  grantedBy: z.string().nullable(),
  validUntil: zInstant.nullable(),
  /** Lapsed but still listed: the evaluator ignores it, and hiding it would leave an administrator unable to
   *  explain why someone's access changed overnight. */
  expired: z.boolean(),
});
export type MembershipOverride = z.infer<typeof zMembershipOverride>;

/** A membership in the roster (§1 — the security principal, never the identity). */
export const zMembershipRow = z.object({
  membershipId: zId,
  userId: zId,
  username: z.string(),
  displayName: z.string(),
  tenantId: z.string(),
  status: zStatus,
  roles: z.array(zMembershipRole),
  level: z.number().int(),
  isPlatformAdmin: z.boolean(),
  overrideCount: z.number().int(),
  expiredOverrideCount: z.number().int(),
  activatedAt: zInstant.nullable(),
  endedAt: zInstant.nullable(),
});
export type MembershipRow = z.infer<typeof zMembershipRow>;

export const zMembershipDetail = zMembershipRow.extend({
  providerId: zId.nullable(),
  homeBranchId: zId.nullable(),
  overrides: z.array(zMembershipOverride),
});
export type MembershipDetail = z.infer<typeof zMembershipDetail>;

/**
 * A time-bounded branch scope grant — REACH, not authority (§3).
 *
 * Holding `orders:consume` says nothing about which branch's orders. "Covering Alexandria for October only"
 * is a first-class expiring fact here rather than a permanent row someone has to remember to delete, which
 * is why `validUntil` and `grantedReason` are part of the contract and not metadata.
 */
export const zBranchScopeGrant = z.object({
  grantId: zId,
  branchId: zId,
  isHome: z.boolean(),
  validFrom: zDate,
  /** Null = open-ended. Evaluated at resolution time — there is no sweeper, so a missed job can never leave
   *  reach switched on after it should have lapsed. */
  validUntil: zDate.nullable(),
  grantedBy: z.string().nullable(),
  grantedReason: z.string().nullable(),
});
export type BranchScopeGrant = z.infer<typeof zBranchScopeGrant>;

/** An active session/device for the sessions tab (§6 — authentication controls stay authentication's). */
export const zAccessSession = z.object({
  sessionId: zId,
  device: z.string(),
  createdAt: zInstant,
  lastSeenAt: zInstant.nullable(),
  /** The caller's own session, which the UI must not offer to revoke without saying so. */
  current: z.boolean(),
});
export type AccessSession = z.infer<typeof zAccessSession>;

/**
 * One key in the effective set, with its PROVENANCE (§5, mode 2).
 *
 * The provenance is the point. A flat list of keys cannot be reviewed: an administrator cannot tell a role
 * grant from a hand-written exception, and so cannot judge whether the exception is still justified.
 */
export const zEffectiveAccessKey = z.object({
  key: z.string(),
  /** `denied` = a Deny override removed a key the roles DO grant — listed, never filtered (see the screen). */
  source: z.enum(["role", "override", "platform-admin", "denied"]),
  via: z.string().optional(),
  deprecated: z.boolean().optional(),
  replacedBy: z.string().nullable().optional(),
  reason: z.string().optional(),
});
export type EffectiveAccessKey = z.infer<typeof zEffectiveAccessKey>;

export const zEffectiveAccess = z.object({
  membershipId: zId,
  keys: z.array(zEffectiveAccessKey),
});
export type EffectiveAccess = z.infer<typeof zEffectiveAccess>;

/**
 * A programme feature switch (§4, adaptation A4).
 *
 * NOT a commercial plan feature. Mersal is a charity and these tenants are partner NGOs and clinics, not
 * customers on a price plan — the copy anywhere near this type must never read like a paywall.
 */
export const zProgramFeature = z.object({
  key: z.string(),
  enabled: z.boolean(),
  /** False = no row at all. "Nobody has decided" and "someone decided no" are different conversations, so
   *  the screen shows both rather than collapsing absence into off. */
  configured: z.boolean(),
  changedBy: z.string().nullable(),
  changedAt: zInstant.nullable(),
});
export type ProgramFeature = z.infer<typeof zProgramFeature>;

/** A numeric cap with its live usage (§4). */
export const zProgramLimit = z.object({
  key: z.string(),
  /** Null = unlimited (no row) — the fail-open direction, chosen so deploying the cap table cannot take a
   *  working platform offline. */
  maxValue: z.number().int().nullable(),
  /** Null = NOT KNOWN to the service that answered, which is not zero and must never render as zero: monthly
   *  extracts and storage are owned by reporting- and document-service. */
  currentUsage: z.number().int().nullable(),
  changedBy: z.string().nullable(),
  changedAt: zInstant.nullable(),
});
export type ProgramLimit = z.infer<typeof zProgramLimit>;

export const zProgramEnablement = z.object({
  tenantId: z.string(),
  features: z.array(zProgramFeature),
  limits: z.array(zProgramLimit),
});
export type ProgramEnablement = z.infer<typeof zProgramEnablement>;

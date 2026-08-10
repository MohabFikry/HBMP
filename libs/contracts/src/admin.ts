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

/**
 * The code systems clinical governance may edit (ADR-0035 §4).
 *
 * <p>The Medical Director holds master-data editing because they absorb the consequence of getting it wrong —
 * a mis-mapped ICD code misroutes a diagnosis into their own approval queue. That argument reaches the
 * clinical vocabularies and stops there, so the editor offers these four and the server refuses the rest.
 * `super_admin` keeps every system; this narrows nobody's existing access.</p>
 */
export const CLINICAL_CODE_SYSTEMS = ["Icd10", "Cpt", "Loinc", "Atc"] as const;
export type ClinicalCodeSystem = (typeof CLINICAL_CODE_SYSTEMS)[number];

/**
 * A proposed master-data edit.
 *
 * <p><b>An edit APPENDS a version; it never mutates one.</b> The prior version's window is closed and a new
 * one opens, so a prescription written last March still resolves the code as it read last March. That is why
 * there is no "id" here — you are not editing a row, you are stating what the code should mean from now on.</p>
 */
export const zMasterDataEdit = z.object({
  system: z.enum(CLINICAL_CODE_SYSTEMS),
  code: z.string().min(1),
  /** The code's attributes, as a flat record. Shape varies by system, so it is not typed further here. */
  attributes: z.record(z.string(), z.union([z.string(), z.number(), z.boolean(), z.null()])),
  /**
   * Why. Mandatory, and enforced by the server independently.
   *
   * <p>This is what an auditor reads in three years when asking why an ATC entry changed the week a claim was
   * denied. A blank rationale makes the version history a list of changes with no account of any of them.</p>
   */
  rationale: z.string().min(1),
  /** Retire the code — recorded as a new version, never a delete. */
  retired: z.boolean().default(false),
});
export type MasterDataEdit = z.infer<typeof zMasterDataEdit>;

/** The version in force at a given instant — what a historical record resolves the code to. */
export const zMasterDataAsOf = z.object({
  id: zId,
  versionNo: z.number().int(),
  attributes: z.record(z.string(), z.unknown()),
  effectiveFrom: zInstant,
  effectiveTo: zInstant.nullish(),
});
export type MasterDataAsOf = z.infer<typeof zMasterDataAsOf>;

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
 * The value types `ConfigValidation.Validate` accepts, in the server's spelling.
 *
 * <p>Named here rather than typed as a bare string because the type decides how the value is PARSED, and a
 * type the server does not know is a 422 the administrator cannot act on — they picked from a list, so the
 * list has to be the real one. `Duration` is a .NET `TimeSpan`, which means a lone number is DAYS: "15" is
 * fifteen days, not fifteen minutes, and the editor says so rather than letting a session timeout be set to
 * a fortnight by somebody who meant a quarter of an hour.</p>
 */
export const CONFIG_VALUE_TYPES = ["Text", "Whole", "Number", "Boolean", "Duration"] as const;
export type ConfigValueType = (typeof CONFIG_VALUE_TYPES)[number];

/**
 * A proposed change to one system-config entry.
 *
 * <p>There is no "delete": the server closes the current version's window and appends a new one, so the
 * history stays resolvable and an auditor can answer "what was this set to in March". `tenantId` is omitted
 * to mean the caller's own tenant — the server pins it, and asking for another is refused rather than
 * silently narrowed.</p>
 */
export const zSystemConfigEdit = z.object({
  key: z.string().min(1),
  type: z.enum(CONFIG_VALUE_TYPES),
  value: z.string().min(1),
  tenantId: z.string().optional(),
});
export type SystemConfigEdit = z.infer<typeof zSystemConfigEdit>;

/**
 * Canonicalise a config value the way `ConfigValidation.Validate` does, or `null` when it does not parse.
 *
 * <p>A deliberate second implementation of a server rule, which is normally the wrong thing to build. It
 * earns its place because the alternative is a round trip to learn that "abc" is not a number: the
 * administrator presses Save, waits, and gets `not-an-integer` as a banner over a form they have already
 * mentally left. Checking here turns that into a field error while they are still typing.</p>
 *
 * <p>It returns the CANONICAL form rather than a boolean so the editor can show what will actually be
 * stored — `TRUE` becomes `true`, `1.50` becomes `1.5` — instead of letting the value change under the
 * administrator on the next read. The server remains the authority; this never decides, it only warns.</p>
 */
export function canonicaliseConfigValue(type: string, raw: string): string | null {
  const value = (raw ?? "").trim();
  if (value.length === 0) return null;
  switch (type) {
    case "Text":
      return value;
    case "Whole":
      return /^[+-]?\d+$/.test(value) ? String(BigInt(value)) : null;
    case "Number": {
      if (!/^[+-]?(\d+\.?\d*|\.\d+)$/.test(value)) return null;
      const n = Number(value);
      return Number.isFinite(n) ? String(n) : null;
    }
    case "Boolean": {
      const lower = value.toLowerCase();
      // `bool.TryParse` accepts "True"/"TRUE"/"true" and nothing else — not "1", not "yes".
      return lower === "true" || lower === "false" ? lower : null;
    }
    case "Duration":
      // .NET `TimeSpan`: [d.]hh:mm[:ss[.fffffff]], or a bare number meaning DAYS. The bare-number case is the
      // one worth being strict about — it is how "15" becomes a fortnight.
      return /^-?(\d+|\d+\.\d{1,2}:\d{2}(:\d{2}(\.\d{1,7})?)?|\d{1,2}:\d{2}(:\d{2}(\.\d{1,7})?)?)$/.test(value)
        ? value
        : null;
    default:
      return null;
  }
}

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
  /**
   * 28.8 — the sign-in credential, so the console has to show it.
   *
   * Nullable because accounts predating 28.8 may have none (service accounts, seeded fixtures), and an
   * address-less account is exactly what an administrator needs to SEE: "send a reset link" cannot reach it,
   * and it cannot sign in by address. Creation now requires one, so the set can only shrink.
   */
  email: z.string().nullable().optional(),
  /** Masked in the UI — the console administers ACCESS, not identities. */
  tenantId: z.string().optional(),
  isActive: z.boolean(),
  twoFactorEnabled: z.boolean(),
  roles: z.array(z.string()),
});
export type IdentityUser = z.infer<typeof zIdentityUser>;

/**
 * 28.9 — one permission in the access catalogue.
 *
 * Every permission on the platform has always been data (`identity.scope`) and no screen listed it. Without
 * the catalogue an administrator deciding what a person needs has one usable strategy — grant the nearest
 * bigger role — which is how least-privilege erodes: not by being rejected, but by being unavailable at the
 * moment of the decision.
 *
 * The flags are not decoration. Each one changes whether a key belongs in a role at all: a service-only key
 * must never reach a human, a deprecated one must not seed a new role, and a platform-administration key is
 * the only kind the A1 short-circuit can reach (and still never grants clinical data).
 */
export const zScopeCatalogEntry = z.object({
  name: z.string(),
  domain: z.string(),
  description: z.string().nullable().optional(),
  serviceOnly: z.boolean(),
  deprecated: z.boolean(),
  replacedBy: z.string().nullable().optional(),
  isPlatformAdminKey: z.boolean(),
  /** Which roles already hold this key IN THIS TENANT — the question an administrator has in front of a
   *  permission is "who has this already", and without it the safe guess is always to include it. */
  heldBy: z.array(z.string()),
});
export type ScopeCatalogEntry = z.infer<typeof zScopeCatalogEntry>;

/** 28.9 — a role and what it actually grants in this tenant. `custom` roles are the tenant's own to edit. */
export const zRoleCatalogEntry = z.object({
  name: z.string(),
  description: z.string().nullable().optional(),
  sensitivityTier: z.string(),
  level: z.number().nullable().optional(),
  custom: z.boolean(),
  builtIn: z.boolean(),
  scopes: z.array(z.string()),
});
export type RoleCatalogEntry = z.infer<typeof zRoleCatalogEntry>;

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

/**
 * One document kind's validity policy (ADR-0035 §6).
 *
 * <p>Two numbers because they answer different questions. `days` is a renewal cadence — how long this kind of
 * document is expected to stay current after it is issued, used to derive a review date when no expiry was
 * recorded. `warnDays` is when somebody is told it is about to lapse; until ADR-0035 that was the hard-coded
 * constant `[90, 60, 30]`, which meant the number a supervisor most obviously owns was the one they could not
 * touch.</p>
 *
 * <p><b>`days` does not override a real expiry.</b> Mersal does not decide when a government-issued card
 * lapses. Anything derived from the cadence is marked as derived, and a document with no expiry at all is
 * UNKNOWN — never rendered as valid.</p>
 */
export const zDocumentValidityItem = z.object({
  kind: z.string(),
  key: z.string(),
  days: z.number().int(),
  warnDays: z.array(z.number().int()),
  /** False = nobody has set this and the value shown is the platform default. Only one of those is a decision. */
  configured: z.boolean(),
  warnConfigured: z.boolean(),
  /** True for documents whose lapse stops a BENEFICIARY being seen, rather than a provider practising. */
  identity: z.boolean(),
  updatedAt: zInstant.nullish(),
});
export type DocumentValidityItem = z.infer<typeof zDocumentValidityItem>;

export const zDocumentValidityView = z.object({
  tenant: z.string(),
  defaultDays: z.number().int(),
  /** Bounds supplied by the server so the screen and the endpoint cannot disagree about them. */
  minDays: z.number().int(),
  maxDays: z.number().int(),
  defaultWarnDays: z.array(z.number().int()),
  items: z.array(zDocumentValidityItem),
});
export type DocumentValidityView = z.infer<typeof zDocumentValidityView>;

/** Set a cadence, thresholds, or both. Omitting one leaves it untouched rather than clearing it. */
export const zSetDocumentValidity = z.object({
  kind: z.string(),
  days: z.number().int().optional(),
  warnDays: z.array(z.number().int()).optional(),
});
export type SetDocumentValidity = z.infer<typeof zSetDocumentValidity>;

import { z } from "zod";
import { zDate, zId, zLocalized, zStatus } from "./common";

/**
 * Provider-network contracts (Phase 2b, US-018..021). The Network Team administers the tenant's provider
 * directory — providers, their locations and contracts. Reference/administrative data (no beneficiary PHI).
 * Statuses render as non-color StatusChip kinds. Prices are omitted here (provider:finance-only).
 */
export const zProviderSummary = z.object({
  id: zId,
  code: z.string(),
  legalName: z.string(),
  // A plain string, deliberately: the read side must render whatever the row holds, including a spelling
  // added after this bundle was built. Narrowing it to an enum is how a widened database becomes a screen
  // full of schema errors.
  providerType: z.string(),
  status: zStatus,
  onboardingState: z.string(),
});
export type ProviderSummary = z.infer<typeof zProviderSummary>;

export const zProviderLocation = z.object({
  id: zId,
  name: z.string(),
  governorate: z.string().optional(),
  address: z.string().optional(),
  isPrimary: z.boolean(),
});
export type ProviderLocation = z.infer<typeof zProviderLocation>;

export const zProviderContract = z.object({
  id: zId,
  contractNo: z.string(),
  status: zStatus,
  effectiveFrom: z.string(),
  effectiveTo: z.string().optional(),
  serviceLines: z.number().int(),
});
export type ProviderContract = z.infer<typeof zProviderContract>;

/** New-provider onboarding request (Network Team). */
export const zCreateProviderInput = z.object({
  code: z.string().min(1),
  legalName: z.string().min(1),
  // 29.1 (design 45 §1) — "Radiology" is the canonical spelling and "Imaging" is retained for the duration
  // of the expand/contract window, exactly as `zInvestigationOrderType` does. Without Radiology here the
  // Network Team could not onboard a radiology centre at all: the portal's own picker is built from this
  // list, so the type the database has been storing since migration 0012 was the one type nobody could pick.
  providerType: z.enum(["Hospital", "Clinic", "Lab", "Pharmacy", "Radiology", "Imaging"]),
});
export type CreateProviderInput = z.infer<typeof zCreateProviderInput>;

/* ── Phase 14.5 practitioners — specialty & clinic assignment (design 37 §4) ───────────────────────────────
   The clinical profile behind a user account. Specialty and the clinics a doctor works at are not decoration:
   they are the two fields the booking screen filters on, so a doctor created without them cannot be booked. */

/** Reference specialty. `parentCode` allows the shallow taxonomy the backend models. */
export const zSpecialty = z.object({
  code: z.string().min(1),
  name: zLocalized,
  parentCode: z.string().optional(),
});
export type Specialty = z.infer<typeof zSpecialty>;

/** A Mersal internal branch (clinic) — org reference data, readable by any authenticated user. */
export const zBranchSummary = z.object({
  id: zId,
  code: z.string(),
  name: zLocalized,
  city: z.string().optional(),
  status: zStatus,
});
export type BranchSummary = z.infer<typeof zBranchSummary>;

/**
 * A practitioner as the admin list and the booking picker see them.
 *
 * `licenseNo` is OPTIONAL because the server omits it for callers without `provider:write` — it is absent,
 * not blank, and rendering it must therefore tolerate its absence rather than treat it as missing data.
 */
export const zPractitioner = z.object({
  id: zId,
  practitionerType: z.string(),
  name: zLocalized,
  /** The one specialty flagged primary, when one is set. A practitioner with none cannot be booked by specialty. */
  primarySpecialty: z.string().optional(),
  specialties: z.array(z.string()),
  /** Branch ids the practitioner holds an ACTIVE assignment to. */
  branches: z.array(zId),
  status: zStatus,
  licenseNo: z.string().optional(),
});
export type Practitioner = z.infer<typeof zPractitioner>;

/**
 * Creating a doctor. `primarySpecialtyCode` and `branchIds` are REQUIRED here even though the backend accepts
 * a practitioner without either: a doctor with no specialty and no clinic is invisible to the booking screen,
 * which filters on exactly those two fields. Making them optional in the form is how you get a doctor record
 * that exists, looks fine in the admin list, and can never be booked.
 */
export const zCreatePractitionerInput = z.object({
  /** The identity account this clinical profile belongs to (logical FK, one practitioner per user). */
  userId: zId,
  practitionerType: z.enum(["Doctor", "Nurse"]),
  fullNameEn: z.string().min(1),
  fullNameAr: z.string().min(1),
  licenseNo: z.string().optional(),
  licenseExpiry: zDate.optional(),
  primarySpecialtyCode: z.string().min(1),
  branchIds: z.array(zId).min(1),
});
export type CreatePractitionerInput = z.infer<typeof zCreatePractitionerInput>;

/**
 * One attachment that did not land after the practitioner row itself was created.
 *
 * This exists because creating a doctor is THREE-PLUS server calls that are not one transaction: the
 * practitioner, then the specialty, then one call per clinic. A specialty that 409s leaves a real
 * practitioner row behind with nothing attached to it. Swallowing that reports success for a doctor who
 * cannot be booked; failing the whole form reports failure for a doctor who now exists and would be
 * duplicated on retry. Neither is true, so the partial outcome is carried back and rendered as itself.
 */
export const zPractitionerAttachFailure = z.object({
  step: z.enum(["specialty", "branch"]),
  /** The specialty code or branch id that failed to attach. */
  ref: z.string(),
  reason: z.string(),
});
export type PractitionerAttachFailure = z.infer<typeof zPractitionerAttachFailure>;

export const zPractitionerCreated = z.object({
  practitioner: zPractitioner,
  /** Empty on a clean create. Non-empty means the record exists but is INCOMPLETE — see above. */
  incomplete: z.array(zPractitionerAttachFailure),
});
export type PractitionerCreated = z.infer<typeof zPractitionerCreated>;

/**
 * The network roll-up — how many providers the tenant has, and in what standing.
 *
 * ## Why this is a contract at all
 *
 * The Performance screen rendered these four numbers by fetching the provider DIRECTORY and counting rows
 * whose `status.label.en` was the string "Active". Three things were wrong with that and only one of them is
 * about tidiness:
 *
 * 1. It counted a **display label**. `status` is a `{kind, label}` chip assembled for rendering, so the tally
 *    depended on a piece of English prose surviving unchanged. Any relabelling — or any server status the
 *    chip mapper does not recognise — silently produces zero, and zero is a plausible-looking number.
 * 2. It counted **whatever the directory returned**, which is a projection with its own filters, rather than
 *    what the tenant has.
 * 3. It computed, in the browser, a figure `provider-service` **refuses to a provider-scoped caller with a
 *    403**. A provider user must not learn the shape of the network they compete in; an authorization that
 *    the client can route around by counting rows is not one.
 *
 * `GET /api/v1/metrics` has returned exactly this since phase 2b. It had no Kong route until 33.7, which is
 * why nothing called it — and the route-coverage guard that exists to catch precisely that had "metrics" in
 * its ignore list.
 */
export const zNetworkMetrics = z.object({
  total: z.number().int(),
  active: z.number().int(),
  suspended: z.number().int(),
  terminated: z.number().int(),
});
export type NetworkMetrics = z.infer<typeof zNetworkMetrics>;

/** How many of a provider's credentials are good, expiring, or already lapsed. */
export const zCredentialCounts = z.object({
  valid: z.number().int(),
  expiringSoon: z.number().int(),
  expired: z.number().int(),
});
export type CredentialCounts = z.infer<typeof zCredentialCounts>;

/**
 * One provider's performance counters.
 *
 * `ordersFulfilled` and `avgTurnaroundHours` are populated by the phase 5/6 fulfillment events;
 * provider-service returns 0 and null today and says so in its own comment. `avgTurnaroundHours` is
 * NULLABLE rather than 0 for the reason that distinction always matters: no orders yet and an average of
 * zero hours are different facts, and only one of them would be alarming.
 */
export const zProviderMetrics = z.object({
  providerId: zId,
  status: z.string(),
  activeContracts: z.number().int(),
  servicesOffered: z.number().int(),
  credentials: zCredentialCounts,
  ordersFulfilled: z.number().int(),
  avgTurnaroundHours: z.number().nullable(),
});
export type ProviderMetrics = z.infer<typeof zProviderMetrics>;

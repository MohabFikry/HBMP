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

/* ── Phase 19.9 — administering the network (design 58) ────────────────────────────────────────────────────
   Phase 2b built the provider domain as a creation pipeline and gave it no second verb: a legal name could
   not be corrected, a primary location could not be moved, a contract's dates could not be fixed, a priced
   line could not be repriced or removed, and one provider user could not be revoked without suspending the
   whole provider. These are the shapes the administrative half speaks in. */

/**
 * What is stopping this provider going live, as four facts rather than one refusal string.
 *
 * The activation endpoint has always answered a blocked attempt with 422 and the FIRST condition that
 * failed, as a sentence, after the operator pressed the button. That is a guessing game with four rounds.
 * All four conditions come back here so the screen can show the checklist BEFORE anything is attempted —
 * and `blockingReason` is still the server's own wording, never re-derived in the browser.
 */
export const zProviderReadiness = z.object({
  hasPrimaryLocation: z.boolean(),
  hasMandatoryCredentials: z.boolean(),
  mandatoryCredentialsValid: z.boolean(),
  hasActiveContract: z.boolean(),
  canActivate: z.boolean(),
  blockingReason: z.string().nullish(),
});
export type ProviderReadiness = z.infer<typeof zProviderReadiness>;

/** An open, not-yet-approved termination. Terminating is dual-controlled: the approver acts under their own
 *  token on a second call, so this is what the second person is shown before they agree to it. */
export const zPendingTermination = z.object({
  requestId: zId,
  reason: z.string(),
  requestedBy: z.string(),
  requestedAt: z.string(),
});
export type PendingTermination = z.infer<typeof zPendingTermination>;

/** How much hangs off this provider. Counts only — each section is its own read. */
export const zProviderBook = z.object({
  locations: z.number().int(),
  contracts: z.number().int(),
  activeContracts: z.number().int(),
  credentials: z.number().int(),
  activeUsers: z.number().int(),
});
export type ProviderBook = z.infer<typeof zProviderBook>;

export const zProviderDetail = z.object({
  providerId: zId,
  providerCode: z.string(),
  legalName: z.string(),
  providerType: z.string(),
  providerTypeLabel: z.string(),
  status: z.string(),
  onboardingState: z.string(),
  /** The name on the building, when it differs from the name on the contract. */
  commercialName: z.string().nullish(),
  taxId: z.string().nullish(),
  phone: z.string().nullish(),
  email: z.string().nullish(),
  notes: z.string().nullish(),
  /** Why the provider is in its CURRENT standing, and who put it there. Distinct from the audit chain,
   *  which is hash-chained evidence behind `audit:read` and not readable by the team that administers it. */
  statusReason: z.string().nullish(),
  statusActorName: z.string().nullish(),
  statusChangedAt: z.string().nullish(),
  createdAt: z.string(),
  updatedAt: z.string(),
  createdByName: z.string().nullish(),
  updatedByName: z.string().nullish(),
  readiness: zProviderReadiness,
  pendingTermination: zPendingTermination.nullish(),
  book: zProviderBook,
  /** The provider-scoped roles THIS caller may grant, computed by the server from its own separation-of-duties
   *  rule. Sent rather than hardcoded: the list is caller-dependent (a Provider Admin may grant the tech roles
   *  and not their own), so a static picker would offer an option that exists only to be refused. */
  provisionableRoles: z.array(z.string()),
});
export type ProviderDetail = z.infer<typeof zProviderDetail>;

/** Editing a provider. `providerCode` is sent and CHECKED rather than omitted: the server refuses a change
 *  loudly, because a form that silently discards a corrected code leaves the operator believing it took. */
export const zProviderWrite = z.object({
  providerCode: z.string().min(1),
  legalName: z.string().min(1),
  providerType: z.string().min(1),
  commercialName: z.string().nullish(),
  taxId: z.string().nullish(),
  phone: z.string().nullish(),
  email: z.string().nullish(),
  notes: z.string().nullish(),
});
export type ProviderWrite = z.infer<typeof zProviderWrite>;

/**
 * A location as the ADMIN screen sees it — including the closed ones.
 *
 * Distinct from {@link zProviderLocation}, which is the picker projection and returns live rows only. "We
 * used to be in Alexandria and closed it in March" is the answer to half the questions asked of this screen,
 * and a list that silently omits it answers them wrong.
 */
export const zProviderLocationAdmin = z.object({
  locationId: zId,
  name: z.string(),
  governorate: z.string().nullish(),
  address: z.string().nullish(),
  geoLat: z.number().nullish(),
  geoLng: z.number().nullish(),
  isPrimary: z.boolean(),
  isDeleted: z.boolean(),
  deactivationReason: z.string().nullish(),
  deactivatedAt: z.string().nullish(),
});
export type ProviderLocationAdmin = z.infer<typeof zProviderLocationAdmin>;

export const zLocationWrite = z.object({
  name: z.string().min(1),
  governorate: z.string().nullish(),
  address: z.string().nullish(),
  geoLat: z.number().nullish(),
  geoLng: z.number().nullish(),
});
export type LocationWrite = z.infer<typeof zLocationWrite>;

/** A contract as the admin screen sees it. `inEffect` is the server's own answer, not a date comparison the
 *  browser repeats: Active-and-within-its-window is what routing means by in effect, and two implementations
 *  of that is one too many. */
export const zContractAdmin = z.object({
  contractId: zId,
  contractNo: z.string(),
  status: z.string(),
  effectiveFrom: z.string(),
  effectiveTo: z.string().nullish(),
  serviceLines: z.number().int(),
  inEffect: z.boolean(),
  statusReason: z.string().nullish(),
  statusActorName: z.string().nullish(),
  statusChangedAt: z.string().nullish(),
});
export type ContractAdmin = z.infer<typeof zContractAdmin>;

export const zContractWrite = z.object({
  contractNo: z.string().min(1),
  effectiveFrom: z.string().min(1),
  effectiveTo: z.string().nullish(),
});
export type ContractWrite = z.infer<typeof zContractWrite>;

/**
 * One priced line.
 *
 * `agreedPrice` is **nullable** because the server withholds the whole field from a caller without
 * `provider:finance` — it is absent, not zero. A zero would read as "free", which is a different and much
 * worse claim than "you are not being shown this", so the screen must render the absence as itself.
 */
export const zServiceLine = z.object({
  serviceLineId: zId,
  serviceType: z.string(),
  codeSystem: z.string(),
  code: z.string(),
  agreedPrice: z.number().nullish(),
  currencyCode: z.string().nullish(),
});
export type ServiceLine = z.infer<typeof zServiceLine>;

export const zServiceLineWrite = z.object({
  serviceType: z.string().min(1),
  codeSystem: z.string().min(1),
  code: z.string().min(1),
  agreedPrice: z.number().min(0),
  currencyCode: z.string().nullish(),
});
export type ServiceLineWrite = z.infer<typeof zServiceLineWrite>;

/** A credential and its standing. `validToday` and `daysUntilExpiry` are the server's, computed against the
 *  business calendar — the same date the activation gate uses, rather than the browser's idea of today. */
export const zProviderCredential = z.object({
  credentialId: zId,
  credentialType: z.string(),
  status: z.string(),
  validFrom: z.string().nullish(),
  validTo: z.string().nullish(),
  documentId: z.string().nullish(),
  isMandatory: z.boolean(),
  isDeleted: z.boolean(),
  validToday: z.boolean(),
  daysUntilExpiry: z.number().int().nullish(),
});
export type ProviderCredentialView = z.infer<typeof zProviderCredential>;

export const zCredentialWrite = z.object({
  credentialType: z.string().min(1),
  status: z.string().min(1),
  validFrom: z.string().nullish(),
  validTo: z.string().nullish(),
  documentId: z.string().nullish(),
  isMandatory: z.boolean(),
});
export type CredentialWrite = z.infer<typeof zCredentialWrite>;

export const zProviderUser = z.object({
  userId: zId,
  subjectRef: z.string(),
  role: z.string(),
  status: z.string(),
  createdAt: z.string(),
  revokedAt: z.string().nullish(),
});
export type ProviderUserView = z.infer<typeof zProviderUser>;

/**
 * One entry of a change timeline, projected from a database trigger's snapshot.
 *
 * `fields` is an open map on purpose: the three twins (provider, location, contract) carry different
 * columns, and the renderer compares an entry with the one before it to show "before → after". Typing one
 * shape per twin would be three near-identical schemas and three near-identical renderers.
 */
export const zAdminHistoryEntry = z.object({
  historyId: z.number().int(),
  operation: z.string(),
  recordedAt: z.string(),
  actorSubject: z.string().nullish(),
  actorName: z.string().nullish(),
  statusReason: z.string().nullish(),
  fields: z.record(z.string(), z.string().nullable()),
});
export type AdminHistoryEntryView = z.infer<typeof zAdminHistoryEntry>;

export const zAdminHistoryPage = z.object({ entries: z.array(zAdminHistoryEntry) });
export type AdminHistoryPage = z.infer<typeof zAdminHistoryPage>;

/** What terminating a contract did — and whether it left the provider Active in the directory and routable
 *  for nothing, which is the pair of truths this platform keeps letting disagree in silence. */
export const zContractTerminationResult = z.object({
  contractId: zId,
  status: z.string(),
  providerBecomesUnroutable: z.boolean(),
  providerStatus: z.string().nullish(),
});
export type ContractTerminationResult = z.infer<typeof zContractTerminationResult>;

/** What withdrawing a credential did. The provider's status is NOT changed by it — that decision has its own
 *  dual control and its own reason — but a mandatory credential going away can take a live provider below
 *  its own activation bar, and the alternative to saying so is nobody noticing for six months. */
export const zCredentialWithdrawResult = z.object({
  credentialId: zId,
  withdrawn: z.boolean(),
  providerNoLongerMeetsActivationBar: z.boolean(),
  readiness: zProviderReadiness.nullish(),
});
export type CredentialWithdrawResult = z.infer<typeof zCredentialWithdrawResult>;

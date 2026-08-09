import { z } from "zod";
import { getRaw, parseOr, postRaw } from "./http";

/**
 * `getRaw`/`postRaw` return `unknown` by design — `http.ts` is the transport and does not know any screen's
 * shape. This is the seam where the module takes responsibility for the response type.
 *
 * ============================================================================================================
 * IT USED TO BE A BARE CAST, AND THE ARGUMENT FOR THAT WAS WRONG
 * ============================================================================================================
 * The comment here read: "casts and not zod schemas deliberately — the shapes are read-only projections the
 * server already validates, and a second schema would be a second place to update when a field is added."
 *
 * The second clause is answered by writing the schema FIRST and inferring the interface from it, which is
 * what every `export type X = z.infer<typeof zX>` below does. There is exactly one definition; there always
 * was room for exactly one.
 *
 * The first clause mistakes what the validation is for. Nobody suspects the server of emitting malformed
 * JSON. The failure this catches is DRIFT — a field renamed or removed on the server while this file still
 * names the old one. A cast makes that `undefined`, and `undefined` renders as an empty cell, sorts as
 * missing, and formats as `NaN`. On this surface those cells are licence expiry dates, on-hand stock counts
 * and money. The rest of the app has had the loud-schema-failure behaviour since phase 12 and these two
 * modules — roughly eighty operations — opted out of it.
 *
 * `.passthrough()` on every object, deliberately: a server that ADDS a field must not break an older bundle.
 * Drift is only an error in the direction where the client is left with less than it needs.
 */
const parsed = <T>(schema: z.ZodType<T>, p: Promise<unknown>): Promise<T> => p.then((d) => parseOr(schema, d));

/**
 * 25.7 — the typed surface the Branch Management portal consumes (design 42 §6).
 *
 * A narrow module rather than an extension of `ApiClient`, following the phase-19 `policyApi` precedent: this
 * is roughly twenty operations against three services, used by exactly one portal family, and folding them
 * into the cross-portal interface would make every other screen re-learn which half applies to it.
 *
 * It goes through `http.ts`, so it inherits the bearer token, the **active-branch header** and RFC-7807
 * parsing for free. The branch header matters more here than anywhere else on the platform: it is what makes
 * one set of screens serve a coordinator (one clinic) and a clinics manager (all six) without a second
 * implementation of anything.
 */

// ── Practitioners & licences ────────────────────────────────────────────────────────────────────────────

export const zBranchPractitioner = z.object({
  practitionerId: z.string(),
  practitionerType: z.string(),
  fullNameEn: z.string(),
  fullNameAr: z.string(),
  primarySpecialty: z.string().nullable(),
  specialties: z.array(z.string()),
  branches: z.array(z.string()),
  status: z.string(),
  /** Masked to the licence-maintaining scopes. Null means "not shown to you", never "none recorded". */
  licenseNo: z.string().nullable(),
  /** The DATE is returned even where the number is masked — it is what the status chip renders. */
  licenseExpiry: z.string().nullable(),
  licenceValid: z.boolean().nullable(),
  daysUntilExpiry: z.number().nullable(),
}).passthrough();
export type BranchPractitioner = z.infer<typeof zBranchPractitioner>;

export const zLicenceAlert = z.object({
  practitionerId: z.string(),
  fullNameEn: z.string(),
  fullNameAr: z.string(),
  practitionerType: z.string(),
  licenseNo: z.string().nullable(),
  licenseExpiry: z.string().nullable(),
  daysUntilExpiry: z.number().nullable(),
  /** "Expiring" | "Expired" — named by the SERVER, not derived from a negative number on the client. */
  status: z.string(),
  branches: z.array(z.string()),
}).passthrough();
export type LicenceAlert = z.infer<typeof zLicenceAlert>;

export const zLicenceAlertsResponse = z.object({
  asOf: z.string(),
  withinDays: z.number(),
  alerts: z.array(zLicenceAlert),
}).passthrough();
export type LicenceAlertsResponse = z.infer<typeof zLicenceAlertsResponse>;

/** An appointment stranded by a lapsed licence or a closed clinic. FLAGGED, never cancelled. */
export const zFlaggedAppointment = z.object({
  appointmentId: z.string(),
  beneficiaryId: z.string(),
  branchId: z.string().nullable(),
  doctorId: z.string().nullable(),
  scheduledStart: z.string(),
  scheduledEnd: z.string(),
  status: z.string(),
  reassignmentNeededAt: z.string(),
  beneficiaryName: z.string().nullable(),
}).passthrough();
export type FlaggedAppointment = z.infer<typeof zFlaggedAppointment>;

export const branchApi = {
  practitioners: (params: { branchId?: string; asOf?: string; includeUnlicensed?: boolean } = {}) =>
    parsed(z.array(zBranchPractitioner), getRaw(`/practitioners${qs(params)}`)),

  licenceAlerts: (withinDays = 90) =>
    parsed(zLicenceAlertsResponse, getRaw(`/practitioners/licence-alerts?withinDays=${withinDays}`)),

  /** Record or renew a licence. BOTH fields required — an expiry is what makes it enforceable (25.3). */
  updateLicence: (practitionerId: string, body: { licenseNo: string; licenseExpiry: string }) =>
    parsed(
      z.object({ practitionerId: z.string(), licenseNo: z.string(), licenseExpiry: z.string() }).passthrough(),
      postRaw(`/practitioners/${practitionerId}/licence`, body)),

  assignBranch: (practitionerId: string, body: { branchId: string; validFrom: string; validTo?: string }) =>
    parsed(
      z.object({ practitionerId: z.string(), branchId: z.string() }).passthrough(),
      postRaw(`/practitioners/${practitionerId}/branches`, body)),

  reassignmentNeeded: (params: { branchId?: string; doctorId?: string } = {}) =>
    parsed(
      z.object({ asOf: z.string(), count: z.number(), appointments: z.array(zFlaggedAppointment) }).passthrough(),
      getRaw(`/appointments/reassignment-needed${qs(params)}`)),
};

// ── Roster ──────────────────────────────────────────────────────────────────────────────────────────────

export const zRosterKind = z.enum(["Leave", "PublicHoliday", "ClinicClosed", "AdHocClinic"]);
export type RosterKind = z.infer<typeof zRosterKind>;

export const zRosterException = z.object({
  exceptionId: z.string(),
  branchId: z.string().nullable(),
  practitionerId: z.string().nullable(),
  dateFrom: z.string(),
  dateTo: z.string(),
  kind: zRosterKind,
  startTime: z.string().nullable(),
  endTime: z.string().nullable(),
  reason: z.string(),
  wholeDay: z.boolean(),
  subtractive: z.boolean(),
  createdAt: z.string(),
  createdBy: z.string().nullable(),
}).passthrough();
export type RosterException = z.infer<typeof zRosterException>;

export const zRosterImpact = z.object({
  dryRun: z.literal(true),
  affectedCount: z.number(),
  affected: z.array(z.object({ appointmentId: z.string(), beneficiaryId: z.string(), beneficiaryName: z.string().nullable(), scheduledStart: z.string(), doctorId: z.string().nullable(), branchId: z.string().nullable() })),
}).passthrough();
export type RosterImpact = z.infer<typeof zRosterImpact>;

export interface CreateRosterExceptionBody {
  kind: RosterKind;
  dateFrom: string;
  dateTo: string;
  reason: string;
  branchId?: string;
  practitionerId?: string;
  startTime?: string;
  endTime?: string;
  /** What the operator SAW in the preview. The server refuses unless it still matches. */
  acknowledgedImpactCount?: number;
}

export const rosterApi = {
  list: (params: { branchId?: string; practitionerId?: string; from?: string; to?: string } = {}) =>
    parsed(z.array(zRosterException), getRaw(`/roster-exceptions${qs(params)}`)),

  /**
   * THE IMPACT PREVIEW. Always called before the apply — never optional, and the apply below sends back the
   * count this returned. Closing a clinic day without seeing whose day it is, is how eight people travel to
   * a locked building.
   */
  preview: (body: CreateRosterExceptionBody) =>
    parsed(zRosterImpact, postRaw(`/roster-exceptions?dryRun=true`, body)),

  apply: (body: CreateRosterExceptionBody) =>
    parsed(
      z.object({ exceptionId: z.string(), affectedCount: z.number(), flagged: z.number(), cancelled: z.number() })
        .passthrough(),
      postRaw(`/roster-exceptions`, body)),

  withdraw: (exceptionId: string) =>
    parsed(
      z.object({ exceptionId: z.string(), withdrawn: z.boolean() }).passthrough(),
      postRaw(`/roster-exceptions/${exceptionId}/withdraw`, {})),
};

// ── Inventory ───────────────────────────────────────────────────────────────────────────────────────────

export const zItemCategory = z.enum(["Medical", "NonMedical"]);
export type ItemCategory = z.infer<typeof zItemCategory>;

export const zMovementKind = z.enum([
  "Receipt", "Issue", "TransferOut", "TransferIn",
  "Adjustment", "WriteOff", "Return", "Count",
]);
export type MovementKind = z.infer<typeof zMovementKind>;

export const zStockLine = z.object({
  branchId: z.string(),
  itemId: z.string(),
  sku: z.string(),
  nameEn: z.string(),
  nameAr: z.string(),
  category: zItemCategory,
  unitOfMeasure: z.string(),
  coldChain: z.boolean(),
  batchId: z.string().nullable(),
  batchNo: z.string().nullable(),
  expiryDate: z.string().nullable(),
  onHand: z.number(),
  reorderLevel: z.number(),
  isLow: z.boolean(),
  /** Expired medical stock: blocked from issue, clearable only by a write-off with a reason. */
  isQuarantined: z.boolean(),
}).passthrough();
export type StockLine = z.infer<typeof zStockLine>;

export const zStockResponse = z.object({
  asOf: z.string(),
  branches: z.array(z.string()),
  stock: z.array(zStockLine),
}).passthrough();
export type StockResponse = z.infer<typeof zStockResponse>;

export const zMovement = z.object({
  movementId: z.string(),
  branchId: z.string(),
  itemId: z.string(),
  batchId: z.string().nullable(),
  kind: zMovementKind,
  /** SIGNED. On-hand is the sum of these; there is no stored balance anywhere. */
  quantity: z.number(),
  reason: z.string().nullable(),
  transferRef: z.string().nullable(),
  counterpartyBranchId: z.string().nullable(),
  actor: z.string(),
  occurredAt: z.string(),
}).passthrough();
export type Movement = z.infer<typeof zMovement>;

export const zInventoryAlerts = z.object({
  asOf: z.string(),
  branches: z.array(z.string()),
  lowStock: z.array(z.object({ branchId: z.string(), itemId: z.string(), name: z.string(), onHand: z.number(), reorderLevel: z.number(), leadTimeDays: z.number() })),
  expiring: z.array(z.object({ branchId: z.string(), itemId: z.string(), batchId: z.string(), batchNo: z.string(), expiryDate: z.string(), name: z.string(), onHand: z.number(), daysRemaining: z.number(), quarantined: z.boolean() })),
  quarantined: z.array(z.object({ branchId: z.string(), itemId: z.string(), batchId: z.string(), batchNo: z.string(), expiryDate: z.string(), name: z.string(), onHand: z.number(), daysRemaining: z.number(), quarantined: z.boolean() })),
}).passthrough();
export type InventoryAlerts = z.infer<typeof zInventoryAlerts>;

export const inventoryApi = {
  stock: (params: { branchId?: string; category?: ItemCategory; lowStock?: boolean; expiringWithinDays?: number } = {}) =>
    parsed(zStockResponse, getRaw(`/inventory/stock${qs(params)}`)),

  movements: (params: { branchId?: string; itemId?: string; kind?: MovementKind; page?: number; pageSize?: number } = {}) =>
    parsed(
      z.object({ total: z.number(), page: z.number(), pageSize: z.number(), movements: z.array(zMovement) })
        .passthrough(),
      getRaw(`/inventory/movements${qs(params)}`)),

  alerts: (branchId?: string) => parsed(zInventoryAlerts, getRaw(`/inventory/alerts${qs({ branchId })}`)),

  /**
   * Post a movement. The `Idempotency-Key` is REQUIRED and must be stable per INTENT: the same key is reused
   * across retries of one operator action, so a slow network cannot turn one receipt into two. A key
   * regenerated per attempt would defeat the entire mechanism, which is why it is minted by the caller that
   * owns the intent (the dialog) and passed in — never generated here.
   */
  postMovement: (
    idempotencyKey: string,
    body: { branchId: string; itemId: string; kind: MovementKind; quantity: number; batchId?: string; reason?: string },
  ) =>
    parsed(
      z.object({ movementId: z.string(), replayed: z.boolean(), quantity: z.number(), onHand: z.number() })
        .passthrough(),
      postRaw(`/inventory/movements`, body, idempotencyKey)),

  transfer: (
    idempotencyKey: string,
    body: { fromBranchId: string; toBranchId: string; itemId: string; quantity: number; batchId?: string; reason?: string },
  ) =>
    parsed(
      z.object({ transferRef: z.string(), outMovementId: z.string(), inMovementId: z.string(),
                 netChange: z.number() }).passthrough(),
      postRaw(`/inventory/transfers`, body, idempotencyKey)),
};

function qs(params: Record<string, string | number | boolean | undefined>): string {
  const entries = Object.entries(params).filter(([, v]) => v !== undefined && v !== "");
  if (entries.length === 0) return "";
  return `?${entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join("&")}`;
}

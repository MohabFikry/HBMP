import { getRaw, postRaw } from "./http";

/**
 * `getRaw`/`postRaw` return `unknown` by design — `http.ts` is the transport and does not know any screen's
 * shape. The casts below are the seam where this module takes responsibility for the response type, exactly
 * as `policyApi` does. They are casts and not zod schemas deliberately: the shapes here are read-only
 * projections the server already validates, and a second schema would be a second place to update when a
 * field is added.
 */
const asJson = <T>(p: Promise<unknown>): Promise<T> => p as Promise<T>;

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

export interface BranchPractitioner {
  practitionerId: string;
  practitionerType: string;
  fullNameEn: string;
  fullNameAr: string;
  primarySpecialty: string | null;
  specialties: string[];
  branches: string[];
  status: string;
  /** Masked to the licence-maintaining scopes. Null means "not shown to you", never "none recorded". */
  licenseNo: string | null;
  /** The DATE is returned even where the number is masked — it is what the status chip renders. */
  licenseExpiry: string | null;
  licenceValid: boolean | null;
  daysUntilExpiry: number | null;
}

export interface LicenceAlert {
  practitionerId: string;
  fullNameEn: string;
  fullNameAr: string;
  practitionerType: string;
  licenseNo: string | null;
  licenseExpiry: string | null;
  daysUntilExpiry: number | null;
  /** "Expiring" | "Expired" — named by the SERVER, not derived from a negative number on the client. */
  status: string;
  branches: string[];
}

export interface LicenceAlertsResponse {
  asOf: string;
  withinDays: number;
  alerts: LicenceAlert[];
}

/** An appointment stranded by a lapsed licence or a closed clinic. FLAGGED, never cancelled. */
export interface FlaggedAppointment {
  appointmentId: string;
  beneficiaryId: string;
  branchId: string | null;
  doctorId: string | null;
  scheduledStart: string;
  scheduledEnd: string;
  status: string;
  reassignmentNeededAt: string;
  beneficiaryName: string | null;
}

export const branchApi = {
  practitioners: (params: { branchId?: string; asOf?: string; includeUnlicensed?: boolean } = {}) =>
    asJson<BranchPractitioner[]>(getRaw(`/practitioners${qs(params)}`)),

  licenceAlerts: (withinDays = 90) =>
    asJson<LicenceAlertsResponse>(getRaw(`/practitioners/licence-alerts?withinDays=${withinDays}`)),

  /** Record or renew a licence. BOTH fields required — an expiry is what makes it enforceable (25.3). */
  updateLicence: (practitionerId: string, body: { licenseNo: string; licenseExpiry: string }) =>
    asJson<{ practitionerId: string; licenseNo: string; licenseExpiry: string }>(
      postRaw(`/practitioners/${practitionerId}/licence`, body)),

  assignBranch: (practitionerId: string, body: { branchId: string; validFrom: string; validTo?: string }) =>
    asJson<{ practitionerId: string; branchId: string }>(
      postRaw(`/practitioners/${practitionerId}/branches`, body)),

  reassignmentNeeded: (params: { branchId?: string; doctorId?: string } = {}) =>
    asJson<{ asOf: string; count: number; appointments: FlaggedAppointment[] }>(
      getRaw(`/appointments/reassignment-needed${qs(params)}`)),
};

// ── Roster ──────────────────────────────────────────────────────────────────────────────────────────────

export type RosterKind = "Leave" | "PublicHoliday" | "ClinicClosed" | "AdHocClinic";

export interface RosterException {
  exceptionId: string;
  branchId: string | null;
  practitionerId: string | null;
  dateFrom: string;
  dateTo: string;
  kind: RosterKind;
  startTime: string | null;
  endTime: string | null;
  reason: string;
  wholeDay: boolean;
  subtractive: boolean;
  createdAt: string;
  createdBy: string | null;
}

export interface RosterImpact {
  dryRun: true;
  affectedCount: number;
  affected: Array<{
    appointmentId: string;
    beneficiaryId: string;
    beneficiaryName: string | null;
    scheduledStart: string;
    doctorId: string | null;
    branchId: string | null;
  }>;
}

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
    asJson<RosterException[]>(getRaw(`/roster-exceptions${qs(params)}`)),

  /**
   * THE IMPACT PREVIEW. Always called before the apply — never optional, and the apply below sends back the
   * count this returned. Closing a clinic day without seeing whose day it is, is how eight people travel to
   * a locked building.
   */
  preview: (body: CreateRosterExceptionBody) =>
    asJson<RosterImpact>(postRaw(`/roster-exceptions?dryRun=true`, body)),

  apply: (body: CreateRosterExceptionBody) =>
    asJson<{ exceptionId: string; affectedCount: number; flagged: number; cancelled: number }>(
      postRaw(`/roster-exceptions`, body)),

  withdraw: (exceptionId: string) =>
    asJson<{ exceptionId: string; withdrawn: boolean }>(
      postRaw(`/roster-exceptions/${exceptionId}/withdraw`, {})),
};

// ── Inventory ───────────────────────────────────────────────────────────────────────────────────────────

export type ItemCategory = "Medical" | "NonMedical";

export type MovementKind =
  | "Receipt" | "Issue" | "TransferOut" | "TransferIn"
  | "Adjustment" | "WriteOff" | "Return" | "Count";

export interface StockLine {
  branchId: string;
  itemId: string;
  sku: string;
  nameEn: string;
  nameAr: string;
  category: ItemCategory;
  unitOfMeasure: string;
  coldChain: boolean;
  batchId: string | null;
  batchNo: string | null;
  expiryDate: string | null;
  onHand: number;
  reorderLevel: number;
  isLow: boolean;
  /** Expired medical stock: blocked from issue, clearable only by a write-off with a reason. */
  isQuarantined: boolean;
}

export interface StockResponse {
  asOf: string;
  branches: string[];
  stock: StockLine[];
}

export interface Movement {
  movementId: string;
  branchId: string;
  itemId: string;
  batchId: string | null;
  kind: MovementKind;
  /** SIGNED. On-hand is the sum of these; there is no stored balance anywhere. */
  quantity: number;
  reason: string | null;
  transferRef: string | null;
  counterpartyBranchId: string | null;
  actor: string;
  occurredAt: string;
}

export interface InventoryAlerts {
  asOf: string;
  branches: string[];
  lowStock: Array<{ branchId: string; itemId: string; name: string; onHand: number; reorderLevel: number; leadTimeDays: number }>;
  expiring: Array<{ branchId: string; itemId: string; batchId: string; batchNo: string; expiryDate: string; name: string; onHand: number; daysRemaining: number; quarantined: boolean }>;
  quarantined: Array<{ branchId: string; itemId: string; batchId: string; batchNo: string; expiryDate: string; name: string; onHand: number; daysRemaining: number; quarantined: boolean }>;
}

export const inventoryApi = {
  stock: (params: { branchId?: string; category?: ItemCategory; lowStock?: boolean; expiringWithinDays?: number } = {}) =>
    asJson<StockResponse>(getRaw(`/inventory/stock${qs(params)}`)),

  movements: (params: { branchId?: string; itemId?: string; kind?: MovementKind; page?: number; pageSize?: number } = {}) =>
    asJson<{ total: number; page: number; pageSize: number; movements: Movement[] }>(
      getRaw(`/inventory/movements${qs(params)}`)),

  alerts: (branchId?: string) => asJson<InventoryAlerts>(getRaw(`/inventory/alerts${qs({ branchId })}`)),

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
    asJson<{ movementId: string; replayed: boolean; quantity: number; onHand: number }>(
      postRaw(`/inventory/movements`, body, idempotencyKey)),

  transfer: (
    idempotencyKey: string,
    body: { fromBranchId: string; toBranchId: string; itemId: string; quantity: number; batchId?: string; reason?: string },
  ) =>
    asJson<{ transferRef: string; outMovementId: string; inMovementId: string; netChange: number }>(
      postRaw(`/inventory/transfers`, body, idempotencyKey)),
};

function qs(params: Record<string, string | number | boolean | undefined>): string {
  const entries = Object.entries(params).filter(([, v]) => v !== undefined && v !== "");
  if (entries.length === 0) return "";
  return `?${entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join("&")}`;
}

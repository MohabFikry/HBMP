import { z } from "zod";
import { deleteRaw, getRaw, parseOr, postRaw, putRaw } from "./http";
import { FIXTURES } from "@dev/fixtures";
import { LIVE } from "../config";

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
 *
 * ============================================================================================================
 * WHY THESE ARE INTERFACES WITH TWO IMPLEMENTATIONS
 * ============================================================================================================
 * They used to be plain object literals calling `http.ts` directly — the only surface on the platform that
 * did. Every other portal resolves through `ApiClient`, which `ApiProvider` swaps for `DevApiClient` in
 * fixture mode, and the SPA is in fixture mode BY DEFAULT (`LIVE = !FIXTURE_MODE`). There is no MSW and no
 * fetch interception anywhere in the tree.
 *
 * So the entire Clinic Management portal — five screens — errored in the demo bundle, and could not be
 * rendered in a screen-level test at all. That is not a hypothetical: it is why the only test covering this
 * portal exercised `LicenceStatus` in isolation, and why the axe route sweep skipped every one of these
 * routes while reporting itself complete.
 *
 * The fix reuses the seam that already exists rather than inventing one. `@dev/fixtures` is aliased to a
 * refusing stub in a live build, so the fixtures are absent from a production bundle rather than merely
 * unreached — the same guarantee `check-live-bundle-clean.sh` enforces for `DevApiClient`.
 */

export interface BranchApi {
  practitioners(params?: { branchId?: string; asOf?: string; includeUnlicensed?: boolean }): Promise<BranchPractitioner[]>;
  licenceAlerts(withinDays?: number): Promise<LicenceAlertsResponse>;
  updateLicence(practitionerId: string, body: { licenseNo: string; licenseExpiry: string }): Promise<{ practitionerId: string; licenseNo: string; licenseExpiry: string }>;
  /** What a PROPOSED expiry would strand. Informational — the server flags, it does not veto. */
  licenceImpact(practitionerId: string, expiry: string): Promise<LicenceImpact>;
  practitionerHistory(practitionerId: string): Promise<PractitionerHistory>;
  assignBranch(practitionerId: string, body: { branchId: string; validFrom: string; validTo?: string }): Promise<{ practitionerId: string; branchId: string }>;
  reassignmentNeeded(params?: { branchId?: string; doctorId?: string }): Promise<{ asOf: string; count: number; appointments: FlaggedAppointment[] }>;
  /** Branch reference data — names for the ids every other read returns. */
  branches(): Promise<BranchRef[]>;
}

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

/** A clinic, by name. Read at plain `RequireAuthorization()` — any signed-in caller. */
export const zBranchRef = z.object({
  branchId: z.string(),
  branchCode: z.string(),
  nameEn: z.string(),
  nameAr: z.string(),
}).passthrough();
export type BranchRef = z.infer<typeof zBranchRef>;

/** The appointments a proposed licence expiry would strand. Same shape as the roster's impact preview. */
export const zLicenceImpact = z.object({
  asOf: z.string(),
  doctorId: z.string(),
  proposedExpiry: z.string(),
  affectedCount: z.number(),
  affected: z.array(z.object({
    appointmentId: z.string(),
    beneficiaryId: z.string(),
    beneficiaryName: z.string().nullable(),
    branchId: z.string().nullable(),
    doctorId: z.string().nullable(),
    scheduledStart: z.string(),
  })),
}).passthrough();
export type LicenceImpact = z.infer<typeof zLicenceImpact>;

/**
 * One entry of a change timeline. VALUES, not diffs — the server returns the state after each change and the
 * client renders "before → after" by comparing adjacent entries, so the diff is written once and works for
 * every history on the platform.
 */
export const zPractitionerHistoryEntry = z.object({
  sequence: z.number(),
  operation: z.string(),
  recordedAt: z.string(),
  actorSubject: z.string().nullable(),
  actorName: z.string().nullable(),
  licenseNo: z.string().nullable(),
  licenseExpiry: z.string().nullable(),
  status: z.string().nullable(),
  deleted: z.boolean(),
}).passthrough();
export type PractitionerHistoryEntry = z.infer<typeof zPractitionerHistoryEntry>;

export const zPractitionerHistory = z.object({
  practitionerId: z.string(),
  entries: z.array(zPractitionerHistoryEntry),
}).passthrough();
export type PractitionerHistory = z.infer<typeof zPractitionerHistory>;

const httpBranchApi: BranchApi = {
  practitioners: (params = {}) =>
    parsed(z.array(zBranchPractitioner), getRaw(`/practitioners${qs(params)}`)),

  licenceAlerts: (withinDays = 90) =>
    parsed(zLicenceAlertsResponse, getRaw(`/practitioners/licence-alerts?withinDays=${withinDays}`)),

  /** Record or renew a licence. BOTH fields required — an expiry is what makes it enforceable (25.3). */
  updateLicence: (practitionerId, body) =>
    parsed(
      z.object({ practitionerId: z.string(), licenseNo: z.string(), licenseExpiry: z.string() }).passthrough(),
      postRaw(`/practitioners/${practitionerId}/licence`, body)),

  // Served by EMR, not provider: provider-service holds no appointments. Keyed on the practitioner id
  // because that is what the screen has; emr knows it as the doctor id.
  licenceImpact: (practitionerId, expiry) =>
    parsed(zLicenceImpact, getRaw(`/appointments/licence-impact?doctorId=${practitionerId}&expiry=${expiry}`)),

  practitionerHistory: (practitionerId) =>
    parsed(zPractitionerHistory, getRaw(`/practitioners/${practitionerId}/history`)),

  assignBranch: (practitionerId, body) =>
    parsed(
      z.object({ practitionerId: z.string(), branchId: z.string() }).passthrough(),
      postRaw(`/practitioners/${practitionerId}/branches`, body)),

  reassignmentNeeded: (params = {}) =>
    parsed(
      z.object({ asOf: z.string(), count: z.number(), appointments: z.array(zFlaggedAppointment) }).passthrough(),
      getRaw(`/appointments/reassignment-needed${qs(params)}`)),

  branches: () => parsed(z.array(zBranchRef), getRaw(`/branches`)),
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

/** One entry of a roster exception's timeline. A WITHDRAWAL arrives as an update with `withdrawn: true`. */
export const zRosterHistoryEntry = z.object({
  sequence: z.number(),
  operation: z.string(),
  recordedAt: z.string(),
  actorSubject: z.string().nullable(),
  kind: z.string().nullable(),
  dateFrom: z.string().nullable(),
  dateTo: z.string().nullable(),
  startTime: z.string().nullable(),
  endTime: z.string().nullable(),
  reason: z.string().nullable(),
  withdrawn: z.boolean(),
}).passthrough();
export type RosterHistoryEntry = z.infer<typeof zRosterHistoryEntry>;

/**
 * 33.10 — ONE CLINIC, ONE DAY.
 *
 * <p>The weekly pattern says what normally happens and the exception calendar says what does not. The
 * question a coordinator actually opens the screen with — is this clinician in today, and how many can they
 * still take — is neither of those, and reading it off the two by eye means applying four rules by hand: a
 * whole-day closure beats an extra clinic, a part-day absence shortens a session without cancelling it, the
 * daily cap applies across every window the date offers and AFTER subtraction, and a trailing partial slot is
 * not a slot.</p>
 *
 * <p><b>So the server answers it.</b> Every line comes out of `SlotGeneration` — the one place availability
 * is decided — run for a single date. Deriving the same answer here would be a second implementation of those
 * four rules in a language with no tests over them, and the first divergence would be a clinic telling a
 * patient it was open on a day the booking engine had already closed.</p>
 */
export const zDayRosterLine = z.object({
  /** Null on an EXTRA session: an ad-hoc clinic is an exception, not a weekly rule, so it has no rule id. */
  availabilityId: z.string().nullable(),
  practitionerId: z.string().nullable(),
  branchId: z.string().nullable(),
  startTime: z.string(),
  endTime: z.string(),
  slotMinutes: z.number(),
  maxPerDay: z.number().nullable(),
  /** What the weekly pattern alone offers, cap included and exceptions excluded. */
  slotsFromPattern: z.number(),
  /** What this DATE offers, once exceptions and the cap have both applied. */
  slotsOffered: z.number(),
  /** Appointments on this clinician's day at this clinic — everything but a cancellation. */
  booked: z.number(),
  /** "Working" | "Off" | "Extra", named by the server rather than inferred here from a slot count. */
  status: z.string(),
  exceptionKind: z.string().nullable(),
  exceptionReason: z.string().nullable(),
}).passthrough();
export type DayRosterLine = z.infer<typeof zDayRosterLine>;

/**
 * An exception in force on this date, whether or not it changed any line.
 *
 * A clinic closed on a day nobody was rostered has no lines at all — and "why is this day empty" still needs
 * an answer, because "nobody is working today" reads identically for a bank holiday and for a rota somebody
 * forgot to enter.
 */
export const zDayRosterNotice = z.object({
  exceptionId: z.string(),
  kind: z.string(),
  reason: z.string(),
  branchId: z.string().nullable(),
  practitionerId: z.string().nullable(),
  wholeDay: z.boolean(),
  startTime: z.string().nullable(),
  endTime: z.string().nullable(),
  subtractive: z.boolean(),
}).passthrough();
export type DayRosterNotice = z.infer<typeof zDayRosterNotice>;

export const zDayRoster = z.object({
  date: z.string(),
  branchId: z.string().nullable(),
  lines: z.array(zDayRosterLine),
  notices: z.array(zDayRosterNotice),
  summary: z.object({
    clinicians: z.number(),
    slotsOffered: z.number(),
    booked: z.number(),
    /** Floored at zero server-side: a walk-in books without consuming a slot, and "-2 open" helps nobody. */
    open: z.number(),
  }).passthrough(),
}).passthrough();
export type DayRoster = z.infer<typeof zDayRoster>;

export interface RosterApi {
  list(params?: { branchId?: string; practitionerId?: string; from?: string; to?: string }): Promise<RosterException[]>;
  preview(body: CreateRosterExceptionBody): Promise<RosterImpact>;
  apply(body: CreateRosterExceptionBody): Promise<{ exceptionId: string; affectedCount: number; flagged: number; cancelled: number }>;
  withdraw(exceptionId: string): Promise<{ exceptionId: string; withdrawn: boolean }>;
  history(exceptionId: string): Promise<{ exceptionId: string; entries: RosterHistoryEntry[] }>;
  /** One clinic on one date: who is working, in what hours, and how full they are. */
  day(params: { branchId?: string; date: string }): Promise<DayRoster>;
}

const httpRosterApi: RosterApi = {
  list: (params = {}) =>
    parsed(z.array(zRosterException), getRaw(`/roster-exceptions${qs(params)}`)),

  /**
   * THE IMPACT PREVIEW. Always called before the apply — never optional, and the apply below sends back the
   * count this returned. Closing a clinic day without seeing whose day it is, is how eight people travel to
   * a locked building.
   */
  preview: (body) => parsed(zRosterImpact, postRaw(`/roster-exceptions?dryRun=true`, body)),

  apply: (body) =>
    parsed(
      z.object({ exceptionId: z.string(), affectedCount: z.number(), flagged: z.number(), cancelled: z.number() })
        .passthrough(),
      postRaw(`/roster-exceptions`, body)),

  /**
   * DELETE, not `POST /{id}/withdraw`.
   *
   * This called a route that has never existed. emr maps `DELETE /roster-exceptions/{id}`; this posted to
   * `/roster-exceptions/{id}/withdraw`, which no service registers. It went unnoticed because nothing called
   * it either — so withdrawing an exception was unreachable from the UI and broken in the client, and the
   * two defects hid each other.
   */
  withdraw: (exceptionId) =>
    parsed(
      z.object({ exceptionId: z.string(), withdrawn: z.boolean() }).passthrough(),
      deleteRaw(`/roster-exceptions/${exceptionId}`)),

  history: (exceptionId) =>
    parsed(
      z.object({ exceptionId: z.string(), entries: z.array(zRosterHistoryEntry) }).passthrough(),
      getRaw(`/roster-exceptions/${exceptionId}/history`)),

  day: (params) => parsed(zDayRoster, getRaw(`/roster/day${qs(params)}`)),
};

// ── The weekly pattern ──────────────────────────────────────────────────────────────────────────────────

/**
 * A recurring availability rule: when this clinician normally works at this clinic on this weekday, in what
 * slot length, and at most how many patients.
 *
 * `maxPerDay` null means UNCAPPED, which is every rule that existed before the cap did. `slotsFromWindow` and
 * `slotsPerDay` are both returned because "24 slots, capped at 20" is the sentence a coordinator is reading;
 * one number alone either hides the cap or makes the session look shorter than it is.
 */
export const zAvailabilityRule = z.object({
  availabilityId: z.string(),
  providerId: z.string(),
  locationId: z.string(),
  branchId: z.string().nullable(),
  doctorId: z.string().nullable(),
  dayOfWeek: z.number(),
  startTime: z.string(),
  endTime: z.string(),
  slotMinutes: z.number(),
  maxPerDay: z.number().nullable(),
  slotsFromWindow: z.number(),
  slotsPerDay: z.number(),
  updatedAt: z.string().nullable(),
  updatedBy: z.string().nullable(),
  updatedByName: z.string().nullable(),
}).passthrough();
export type AvailabilityRule = z.infer<typeof zAvailabilityRule>;

export const zAvailabilityHistoryEntry = z.object({
  sequence: z.number(),
  operation: z.string(),
  recordedAt: z.string(),
  actorSubject: z.string().nullable(),
  actorName: z.string().nullable(),
  startTime: z.string().nullable(),
  endTime: z.string().nullable(),
  slotMinutes: z.number().nullable(),
  maxPerDay: z.number().nullable(),
  retired: z.boolean(),
}).passthrough();
export type AvailabilityHistoryEntry = z.infer<typeof zAvailabilityHistoryEntry>;

export interface UpsertAvailabilityBody {
  providerId: string;
  locationId: string;
  doctorId?: string;
  branchId?: string;
  dayOfWeek: number;
  startTime: string;
  endTime: string;
  slotMinutes: number;
  maxPerDay?: number | null;
}

export interface AvailabilityApi {
  list(params?: { branchId?: string; doctorId?: string }): Promise<AvailabilityRule[]>;
  create(body: UpsertAvailabilityBody): Promise<AvailabilityRule>;
  update(availabilityId: string, body: UpsertAvailabilityBody): Promise<AvailabilityRule>;
  /** Retires the pattern. Does NOT retract slots already generated, or cancel anything booked into them. */
  retire(availabilityId: string): Promise<{ availabilityId: string; retired: boolean }>;
  history(availabilityId: string): Promise<{ availabilityId: string; entries: AvailabilityHistoryEntry[] }>;
}

const httpAvailabilityApi: AvailabilityApi = {
  list: (params = {}) => parsed(z.array(zAvailabilityRule), getRaw(`/provider-availability${qs(params)}`)),

  create: (body) => parsed(zAvailabilityRule, postRaw(`/provider-availability`, body)),

  update: (availabilityId, body) =>
    parsed(zAvailabilityRule, putRaw(`/provider-availability/${availabilityId}`, body)),

  retire: (availabilityId) =>
    parsed(
      z.object({ availabilityId: z.string(), retired: z.boolean() }).passthrough(),
      deleteRaw(`/provider-availability/${availabilityId}`)),

  history: (availabilityId) =>
    parsed(
      z.object({ availabilityId: z.string(), entries: z.array(zAvailabilityHistoryEntry) }).passthrough(),
      getRaw(`/provider-availability/${availabilityId}/history`)),
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

export interface InventoryApi {
  stock(params?: { branchId?: string; category?: ItemCategory; lowStock?: boolean; expiringWithinDays?: number }): Promise<StockResponse>;
  movements(params?: { branchId?: string; itemId?: string; kind?: MovementKind; page?: number; pageSize?: number }): Promise<{ total: number; page: number; pageSize: number; movements: Movement[] }>;
  alerts(branchId?: string): Promise<InventoryAlerts>;
  postMovement(idempotencyKey: string, body: { branchId: string; itemId: string; kind: MovementKind; quantity: number; batchId?: string; reason?: string }): Promise<{ movementId: string; replayed: boolean; quantity: number; onHand: number }>;
  transfer(idempotencyKey: string, body: { fromBranchId: string; toBranchId: string; itemId: string; quantity: number; batchId?: string; reason?: string }): Promise<{ transferRef: string; outMovementId: string; inMovementId: string; netChange: number }>;
}

const httpInventoryApi: InventoryApi = {
  stock: (params = {}) => parsed(zStockResponse, getRaw(`/inventory/stock${qs(params)}`)),

  movements: (params = {}) =>
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

// ── The seam ────────────────────────────────────────────────────────────────────────────────────────────

/** The four surfaces the Clinic Management portal reads, resolved together so a fixture cannot supply half. */
export interface BranchApis {
  branch: BranchApi;
  roster: RosterApi;
  availability: AvailabilityApi;
  inventory: InventoryApi;
}

export const HTTP_BRANCH_APIS: BranchApis = {
  branch: httpBranchApi,
  roster: httpRosterApi,
  availability: httpAvailabilityApi,
  inventory: httpInventoryApi,
};

/**
 * Live builds get the HTTP implementations; fixture builds get the demo ones, through the same
 * `@dev/fixtures` door `ApiProvider` uses for `DevApiClient`.
 *
 * A module-level ternary rather than a hook, deliberately: `LIVE` folds to a constant at build time, so
 * rollup drops the branch it did not take — which is what keeps the fixtures out of a production bundle
 * rather than merely unreached. Wrapping this in a lambda rollup declines to reason about would put the whole
 * subtree back, exactly as `fixtures.live.ts` warns.
 */
const APIS: BranchApis = LIVE ? HTTP_BRANCH_APIS : FIXTURES.createBranchApis();

export const branchApi = APIS.branch;
export const rosterApi = APIS.roster;
export const availabilityApi = APIS.availability;
export const inventoryApi = APIS.inventory;

function qs(params: Record<string, string | number | boolean | undefined>): string {
  const entries = Object.entries(params).filter(([, v]) => v !== undefined && v !== "");
  if (entries.length === 0) return "";
  return `?${entries.map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`).join("&")}`;
}

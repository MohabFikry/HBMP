import { deleteRaw, getRaw, getText, postForm, postRaw, putRaw } from "./http";

/**
 * Phase 19.6 — the typed surface the policy-administration and beneficiary-management screens consume.
 *
 * WHY THIS IS A SEPARATE MODULE FROM `ApiClient`
 * ---------------------------------------------
 * `client.ts` is the cross-portal contract: one interface every flagship screen shares, with zod-validated
 * `@mersal/contracts` types and two full implementations (fixtures + HTTP). Phase 19 adds roughly sixty
 * operations against three services, all of them used by exactly one portal family. Folding them in would
 * have tripled that interface — and every screen in the app would have had to be re-taught which half of it
 * applies to them. So this follows the Phase 15 Call Centre precedent: a NARROW surface, owned by the screens
 * that use it, injectable for tests.
 *
 * It departs from that precedent in one way, deliberately. `CallCentre.tsx` wrote its own `fetch` wrapper,
 * which meant its failures never became {@link ApiError}s and could not be rendered by
 * `writeErrorMessage` — the phase-18 D1 rule that an operator must be able to tell RETRY from RELOAD from
 * STOP. This module goes through `http.ts` instead, so it inherits the bearer token, the active-branch
 * header, RFC-7807 parsing and that error vocabulary for free.
 *
 * MIN-NECESSARY IS THE SERVER'S JOB HERE, NOT THIS FILE'S. Every response type below carries nullable fields
 * where the service projects by role (amounts, contract terms, termination reasons, note bodies). The screens
 * render "withheld" states from those nulls; they never decide entitlement themselves.
 */

// ── Shared value shapes ─────────────────────────────────────────────────────────────────────────────────

export interface QueryPage<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  totalPages: number;
  sortedBy: string;
  /** The caller's payer restriction narrowed this page. Rendered, not swallowed: without it a scoped user
   *  reads "12 policies" as "the organisation has 12 policies". */
  payerScopeApplied: boolean;
  identityMatchTruncated: boolean;
  unavailable: string[];
}

export interface PayerView {
  payerId: string;
  payerCode: string;
  nameEn: string;
  nameAr: string;
  payerType: string;
  status: string;
}

export interface PlanView {
  planId: string;
  planCode: string;
  nameEn: string;
  nameAr: string;
  description?: string | null;
  category: string;
  status: string;
}

export interface BenefitCategoryView {
  benefitCategoryId: string;
  code: string;
  name: string;
}

export interface BenefitRuleTierView {
  ruleTierId: string;
  networkTierId: string;
  tierCode: string;
  isCovered: boolean;
  copayFixed?: number | null;
  copayPercent?: number | null;
  coinsurancePercent?: number | null;
  copayCountsTowardDeductible: boolean;
  requiresPreauthOverride?: boolean | null;
  limitMultiplier?: number | null;
  /** Resolved server-side so the editor, eligibility, approvals and claims cannot disagree about what
   *  actually applies at this tier. */
  effectiveRequiresPreauth: boolean;
  effectiveLimitValue?: number | null;
}

export interface BenefitRuleView {
  ruleId: string;
  benefitCategoryId: string;
  /** 19.6 — the code the rules PUT writes back. Null only when the server had no catalogue to hand. */
  benefitCategoryCode?: string | null;
  isCovered: boolean;
  limitType?: string | null;
  limitValue?: number | null;
  resetPeriod: string;
  deductible?: number | null;
  deductibleWaived: boolean;
  waitingPeriodDays: number;
  requiresPreauth: boolean;
  preauthCostThreshold?: number | null;
  exclusions: string;
  notes?: string | null;
  tiers: BenefitRuleTierView[];
}

export interface PlanVersionView {
  planVersionId: string;
  planId: string;
  versionNo: number;
  effectiveFrom: string;
  effectiveTo?: string | null;
  status: string;
  /** Projected by the server rather than derived from `status` here — the read-only affordance and the API's
   *  409 must agree, and deriving the rule twice is how they drift apart. */
  editable: boolean;
  activatedAt?: string | null;
  supersededByVersionId?: string | null;
  rules: BenefitRuleView[];
}

export interface ActivationProblem {
  code: string;
  detail: string;
}

export interface ValidationResult {
  valid: boolean;
  problems: ActivationProblem[];
}

export interface BenefitRuleTierInput {
  networkTierId: string;
  isCovered: boolean;
  copayFixed?: number | null;
  copayPercent?: number | null;
  coinsurancePercent?: number | null;
  copayCountsTowardDeductible: boolean;
  requiresPreauthOverride?: boolean | null;
  limitMultiplier?: number | null;
}

export interface BenefitRuleInput {
  benefitCategoryCode: string;
  isCovered: boolean;
  limitType?: string | null;
  limitValue?: number | null;
  resetPeriod?: string | null;
  deductible?: number | null;
  deductibleWaived: boolean;
  waitingPeriodDays: number;
  requiresPreauth: boolean;
  preauthCostThreshold?: number | null;
  exclusions?: string | null;
  notes?: string | null;
  tiers?: BenefitRuleTierInput[] | null;
}

export interface PolicyQueryRow {
  policyId: string;
  policyNo: string;
  payerId?: string | null;
  status: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  memberCount: number;
  memberCountBand: string;
  maxMembers?: number | null;
  planCount: number;
  totalLimit?: number | null;
  totalConsumed?: number | null;
  percentUsed?: number | null;
  utilizationBand: string;
}

export interface MemberQueryRow {
  enrollmentId: string;
  beneficiaryId: string;
  memberNo: string;
  givenName?: string | null;
  familyName?: string | null;
  beneficiaryStatus?: string | null;
  policyId: string;
  policyPlanId: string;
  planLabel?: string | null;
  groupId?: string | null;
  payerId?: string | null;
  relationship: string;
  status: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  waitingPeriodEndsOn?: string | null;
  waitingPeriodState: string;
  branchId?: string | null;
  terminationReason?: string | null;
  totalLimit?: number | null;
  totalConsumed?: number | null;
  totalRemaining?: number | null;
  percentUsed?: number | null;
  utilizationBand: string;
}

export interface PolicyPlanView {
  policyPlanId: string;
  policyId: string;
  planVersionId: string;
  planLabel: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  isDefault: boolean;
  eligibilityRule?: string | null;
  maxMembers?: number | null;
  status: string;
  memberCount: number;
}

export interface MemberGroupView {
  groupId: string;
  policyId: string;
  groupCode: string;
  nameEn: string;
  nameAr: string;
  groupType: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  status: string;
}

export interface EnrollmentView {
  enrollmentId: string;
  beneficiaryId: string;
  policyId: string;
  policyPlanId: string;
  groupId?: string | null;
  memberNo: string;
  relationship: string;
  principalEnrollmentId?: string | null;
  effectiveFrom: string;
  effectiveTo?: string | null;
  waitingPeriodEndsOn?: string | null;
  status: string;
  terminationReason?: string | null;
  sourcePlanVersionId?: string | null;
  coveragesGenerated: number;
}

export interface CarriedLimitView {
  benefitCategoryId: string;
  benefitCategoryCode?: string | null;
  limitValue?: number | null;
  consumedValue: number;
  remaining?: number | null;
  exhausted: boolean;
}

/** A benefit the member holds today and would not hold after the change. */
export interface DroppedCategoryView {
  benefitCategoryId: string;
  benefitCategoryCode?: string | null;
  currentLimitValue?: number | null;
  consumedValue: number;
}

export interface PlanChangeView {
  enrollmentId: string;
  policyPlanId: string;
  planVersionId: string;
  consumptionPolicy: string;
  carriedLimits: CarriedLimitView[];
  droppedCategories: DroppedCategoryView[];
}

/**
 * The plan-change DRY RUN. One row per category the new plan covers, carrying BOTH ceilings so the officer can
 * see what is being moved away from, plus the categories that disappear entirely.
 *
 * The client does not compute any of this. Which consumption travels is a server setting (ADR-0020, unsigned),
 * the new plan's limits are the other half of the sum, and a dropped category produces no row at all — so an
 * estimate assembled here would disagree with the outcome exactly when somebody is deciding whether to move a
 * patient mid-treatment.
 */
export interface PlanChangePreviewView {
  enrollmentId: string;
  fromPolicyPlanId: string;
  toPolicyPlanId: string;
  toPlanLabel: string;
  planVersionId: string;
  effectiveDate: string;
  consumptionPolicy: string;
  rows: CarryPreviewRow[];
  droppedCategories: DroppedCategoryView[];
}

export interface CarryPreviewRow {
  benefitCategoryId: string;
  benefitCategoryCode?: string | null;
  /** False when the new plan ADDS this benefit — distinguishes "unbounded today" from "not covered today",
   *  which a null current limit alone cannot. */
  held: boolean;
  currentLimitValue?: number | null;
  consumedValue: number;
  newLimitValue?: number | null;
  remaining?: number | null;
  exhausted: boolean;
}


// ── Analytics (19.6b) ───────────────────────────────────────────────────────────────────────────────────

/** One plotted value. `dimensionId` is what makes a drill-down possible without the client resolving a label
 *  back to an id — a round trip that guesses, and guesses wrong the moment two plans share a label. */
export interface AnalyticsPoint {
  key: string;
  labelEn: string;
  labelAr: string;
  value: number;
  dimensionId?: string | null;
  secondary?: number | null;
}

/**
 * A chart AND the accessible table that always accompanies it.
 *
 * `columns` and `summaryEn`/`summaryAr` come from the server rather than being composed here: a caption the
 * client invents drifts from the data the moment a series changes shape, and the R2 audit finding (U6) is
 * specifically that an alternative nobody maintains is not an alternative.
 */
export interface AnalyticsSeries {
  key: string;
  titleEn: string;
  titleAr: string;
  unit: "count" | "currency" | "percent" | string;
  points: AnalyticsPoint[];
  summaryEn: string;
  summaryAr: string;
  columns: string[];
}

/** A period-over-period movement. `direction` is a WORD because the four-cue rule needs a text cue, and
 *  `better` is separate because direction and desirability are different facts. */
export interface AnalyticsDelta {
  key: string;
  labelEn: string;
  labelAr: string;
  current: number;
  previous: number;
  percentChange?: number | null;
  direction: "Up" | "Down" | "Flat";
  better?: boolean | null;
}

export interface AnalyticsViewResult {
  view: string;
  series: AnalyticsSeries[];
  deltas: AnalyticsDelta[];
  /** True when the caller's payer scope narrowed the aggregate — surfaced so a small number reads as
   *  "your scope" rather than "the programme shrank". */
  payerScopeApplied: boolean;
  unavailable: string[];
}

/** A drill-down row: pointers and figures, never identity. Resolving the person is the audited step after. */
export interface OutlierRow {
  enrollmentId: string;
  beneficiaryId: string;
  policyId: string;
  policyPlanId?: string | null;
  limit: number;
  consumed: number;
  band: string;
}

/** The shared filter bar, in the same vocabulary as policy/member query. Serialised straight into the URL. */
export interface AnalyticsFilters {
  payerId?: string;
  policyId?: string;
  policyPlanId?: string;
  groupId?: string;
  branchId?: string;
  tier?: string;
  category?: string;
  status?: string;
  relationship?: string;
  band?: string;
  from?: string;
  to?: string;
  asOf?: string;
  plans?: string;
  compare?: string;
}

export interface TierCostShare {
  networkTierId: string;
  tierCode: string;
  isCovered: boolean;
  copayFixed?: number | null;
  copayPercent?: number | null;
  coinsurancePercent?: number | null;
  copayCountsTowardDeductible: boolean;
  requiresPreauth: boolean;
  limitAtTier?: number | null;
}

export interface CategoryCoverageDetail {
  benefitCategoryCode: string;
  isCovered: boolean;
  limitType?: string | null;
  limit?: number | null;
  consumed: number;
  remaining?: number | null;
  percentUsed?: number | null;
  currencyCode: string;
  resetPeriod: string;
  resetsOn?: string | null;
  configuredLimit?: number | null;
  /** The member's generated ceiling differs from what the plan in force would grant today — a real and
   *  legitimate divergence after an amendment, surfaced rather than hidden. */
  limitDiffersFromPlan: boolean;
  waitingPeriodEndsOn?: string | null;
  waitingPeriodState: string;
  requiresPreauth: boolean;
  preauthCostThreshold?: number | null;
  deductible?: number | null;
  deductibleWaived: boolean;
  exclusions: string[];
  costShareByTier: TierCostShare[];
}

export interface MemberCoverageDetail {
  enrollmentId: string;
  beneficiaryId: string;
  memberNo: string;
  policyId: string;
  policyPlanId: string;
  planLabel: string;
  planId?: string | null;
  planVersionInForceId?: string | null;
  planVersionNo?: number | null;
  planVersionFrom?: string | null;
  planVersionTo?: string | null;
  planVersionStatus?: string | null;
  enrolledUnderPlanVersionId?: string | null;
  planVersionChangedSinceEnrolment: boolean;
  asOf: string;
  enrollmentStatus: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  categories: CategoryCoverageDetail[];
}

export interface NoteView {
  noteId: string;
  scope: string;
  scopeRef: string;
  noteType: string;
  visibilityClass: string;
  /** Null WITH `bodyWithheld` true is a projection, not an empty note. The screens render a locked state. */
  body?: string | null;
  bodyWithheld: boolean;
  withheldReason?: string | null;
  authoredByUsername: string;
  authoredByDisplay: string;
  authoredAt: string;
  status: string;
  cancelledByUsername?: string | null;
  cancelledAt?: string | null;
  cancellationReason?: string | null;
  supersedesNoteId?: string | null;
  pinned: boolean;
  canCancel: boolean;
}

export interface PolicyDocumentView {
  linkId: string;
  scope: string;
  scopeRef: string;
  documentId: string;
  versionNo: number;
  supersedesLinkId?: string | null;
  documentClass: string;
  visibilityClass: string;
  sensitiveCategory?: string | null;
  title: string;
  description?: string | null;
  documentDate?: string | null;
  issuingProvider?: string | null;
  uploadedByUsername: string;
  uploadedByDisplay: string;
  uploadedAt: string;
  status: string;
  withdrawnByUsername?: string | null;
  withdrawnAt?: string | null;
  withdrawalReason?: string | null;
  expiresOn?: string | null;
  expired: boolean;
  verifiedByUsername?: string | null;
  verifiedAt?: string | null;
  canDownload: boolean;
}

export interface TimelineEntryView {
  entryId: string;
  scope: string;
  scopeRef: string;
  occurredAt: string;
  eventType: string;
  eventCategory: string;
  actorUsername?: string | null;
  actorDisplay?: string | null;
  summaryEn: string;
  summaryAr: string;
  changeDiff?: string | null;
  diffWithheld: boolean;
  visibilityClass: string;
  sourceService: string;
  correlationId?: string | null;
  targetRef?: string | null;
  targetKind?: string | null;
}

export interface TimelinePage {
  entries: TimelineEntryView[];
  nextCursor?: string | null;
}

export interface CategoryUtilizationView {
  benefitCategory: string;
  limitType?: string | null;
  limit?: number | null;
  consumed: number;
  remaining?: number | null;
  percentUsed?: number | null;
  unlimited: boolean;
  currencyCode: string;
  resetPeriod: string;
  resetsOn?: string | null;
  windowActivity?: number | null;
  windowEvents: number;
}

export interface TierUtilizationView {
  tierCode: string;
  outOfNetwork: boolean;
  /** False = the movement's provider was unknown. Never folded into in-network, which would flatter the
   *  network on the single number it is judged by. */
  attributed: boolean;
  netQuantity: number;
  events: number;
}

export interface ExternalUtilizationView {
  encounters?: number | null;
  authorizationsRaised?: number | null;
  authorizationsApproved?: number | null;
  authorizationsDenied?: number | null;
  claimedAmount?: number | null;
  approvedAmount?: number | null;
  memberShareAmount?: number | null;
  currencyCode: string;
  /** Services that did not answer. A null here means "could not ask", never "zero". */
  unavailable: string[];
}

export interface ReconciliationView {
  accumulatorTotal: number;
  reportedTotal: number;
  reconciled: boolean;
}

export interface MemberUtilizationView {
  beneficiaryId: string;
  enrollmentId: string;
  memberNo: string;
  asOf: string;
  windowFrom: string;
  windowTo: string;
  categories: CategoryUtilizationView[];
  byNetworkTier: TierUtilizationView[];
  external: ExternalUtilizationView;
  reconciliation: ReconciliationView;
}

export interface MemberRowView {
  beneficiaryId: string;
  enrollmentId: string;
  memberNo: string;
  policyPlanId: string;
  groupId?: string | null;
  totalLimit: number;
  totalConsumed: number;
  totalRemaining: number;
  percentUsed?: number | null;
  anyUnlimited: boolean;
}

export interface ScopeUtilizationView {
  scope: string;
  scopeId: string;
  asOf: string;
  windowFrom: string;
  windowTo: string;
  memberCount: number;
  totalLimit: number;
  totalConsumed: number;
  totalRemaining: number;
  percentUsed?: number | null;
  outlierThresholdPercent: number;
  members: MemberRowView[];
  outliers: MemberRowView[];
  distribution: { label: string; memberCount: number }[];
  byNetworkTier: TierUtilizationView[];
  external: ExternalUtilizationView;
  reconciliation: ReconciliationView;
}

// ── Network tiers (provider-service) ────────────────────────────────────────────────────────────────────

export interface NetworkTierView {
  networkTierId: string;
  tierCode: string;
  nameEn: string;
  nameAr: string;
  rank: number;
  description?: string | null;
  isOutOfNetwork: boolean;
  status: string;
}

export interface TierAssignmentView {
  assignmentId: string;
  networkTierId: string;
  tierCode?: string | null;
  providerId: string;
  scope: string;
  scopeRef: string;
  effectiveFrom: string;
  effectiveTo?: string | null;
  status: string;
}

export interface TierResolutionView {
  networkTierId: string;
  tierCode: string;
  nameEn: string;
  nameAr: string;
  rank: number;
  isOutOfNetwork: boolean;
  /** "assigned to the out-of-network tier" and "out-of-network because nothing was assigned" price the same
   *  and need very different follow-up. The basis is what tells them apart. */
  basis: string;
  assignmentId?: string | null;
  providerId: string;
  locationId?: string | null;
  serviceCode?: string | null;
  serviceDate: string;
}

// ── Bulk upload (19.5b) ─────────────────────────────────────────────────────────────────────────────────

export interface BulkColumnView {
  name: string;
  kind: string;
  required: boolean;
  descriptionEn: string;
  descriptionAr: string;
}

export interface BulkTemplateView {
  jobType: string;
  purposeEn: string;
  purposeAr: string;
  columns: BulkColumnView[];
}

export interface BulkJobView {
  jobId: string;
  jobType: string;
  fileName: string;
  status: string;
  batchId: string;
  totalRows: number;
  validRows: number;
  invalidRows: number;
  appliedRows: number;
  failedRows: number;
  skippedRows: number;
  /** submitted = valid + invalid, and once complete valid = applied + failed + skipped. A job that cannot
   *  say what happened to a row is one that lost it. */
  balances: boolean;
  failureCode?: string | null;
  failureDetail?: string | null;
  fileDocumentId?: string | null;
  errorDocumentId?: string | null;
  submittedBy?: string | null;
  submittedAt: string;
  completedAt?: string | null;
  rolledBackAt?: string | null;
}

export interface BulkRowError {
  rowNumber: number;
  code: string;
  detailEn: string;
  detailAr: string;
}

export interface BulkRowPreview {
  rowNumber: number;
  summaryEn: string;
  summaryAr: string;
  changes: Record<string, unknown>;
}

export interface BulkRowView {
  rowNumber: number;
  status: string;
  errorCode?: string | null;
  errorDetail?: string | null;
  errorDetailAr?: string | null;
  targetRef?: string | null;
  appliedAt?: string | null;
}

/** The dry run. `wouldChange` is what earns the step: counts alone tell an operator that 9,963 rows are
 *  valid, not that the file is about to move everybody onto the wrong plan. */
export interface BulkValidationView {
  job: BulkJobView;
  totalErrors: number;
  errors: BulkRowError[];
  wouldChange: BulkRowPreview[];
  committable: boolean;
}

export interface BulkCommitView {
  job: BulkJobView;
  totalErrors: number;
  errors: BulkRowError[];
}

export interface BulkReconciliationView {
  jobId: string;
  jobType: string;
  status: string;
  batchId: string;
  submitted: number;
  valid: number;
  invalid: number;
  applied: number;
  failed: number;
  skipped: number;
  balances: boolean;
  errorDocumentId?: string | null;
}

// ── The surface ─────────────────────────────────────────────────────────────────────────────────────────

export interface PolicyApi {
  // Product configuration (19.1 / 19.1b)
  payers(): Promise<PayerView[]>;
  plans(): Promise<PlanView[]>;
  benefitCategories(): Promise<BenefitCategoryView[]>;
  planVersions(planId: string): Promise<PlanVersionView[]>;
  planVersion(planVersionId: string): Promise<PlanVersionView>;
  setPlanRules(planVersionId: string, rules: BenefitRuleInput[], idempotencyKey: string): Promise<PlanVersionView>;
  validatePlanVersion(planVersionId: string): Promise<ValidationResult>;
  activatePlanVersion(planVersionId: string, idempotencyKey: string): Promise<PlanVersionView>;
  amendPlan(planId: string, idempotencyKey: string): Promise<PlanVersionView>;

  // Policies, plans-under-policy, groups (19.2 / 19.2b)
  policyQuery(filters: Record<string, string | number | undefined>): Promise<QueryPage<PolicyQueryRow>>;
  policyPlans(policyId: string): Promise<PolicyPlanView[]>;
  attachPolicyPlan(policyId: string, body: unknown, idempotencyKey: string): Promise<PolicyPlanView>;
  policyGroups(policyId: string): Promise<MemberGroupView[]>;
  createGroup(policyId: string, body: unknown, idempotencyKey: string): Promise<MemberGroupView>;

  // Membership (19.2 / 19.2b)
  memberQuery(filters: Record<string, string | number | undefined>): Promise<QueryPage<MemberQueryRow>>;
  enrollment(enrollmentId: string): Promise<EnrollmentView>;
  enrol(body: unknown, idempotencyKey: string): Promise<EnrollmentView>;
  terminate(enrollmentId: string, effectiveDate: string, reason: string, idempotencyKey: string): Promise<EnrollmentView>;
  reinstate(enrollmentId: string, effectiveDate: string, reason: string | null, idempotencyKey: string): Promise<EnrollmentView>;
  changeGroup(enrollmentId: string, groupId: string | null, effectiveDate: string, reason: string | null, idempotencyKey: string): Promise<EnrollmentView>;
  changePlan(enrollmentId: string, policyPlanId: string, effectiveDate: string, reason: string, idempotencyKey: string): Promise<PlanChangeView>;
  /** Dry run. Carries no Idempotency-Key: nothing is written, so there is nothing to double-apply. */
  previewPlanChange(enrollmentId: string, policyPlanId: string, effectiveDate: string): Promise<PlanChangePreviewView>;
  coverageDetails(enrollmentId: string, asOf?: string): Promise<MemberCoverageDetail>;

  /** The six analytical views. `reporting-service`, not policy — the dashboard reads a pre-aggregated read
   *  model and never the transactional benefit spine. */
  analytics(view: string, filters: AnalyticsFilters): Promise<AnalyticsViewResult>;
  /** The member rows behind an outlier segment. Audited server-side: this is where a total becomes a list of
   *  specific people. */
  analyticsOutlierMembers(band: string, filters: AnalyticsFilters, limit?: number): Promise<OutlierRow[]>;
  analyticsExport(view: string, filters: AnalyticsFilters): Promise<string>;

  // Notes (19.3) — shared by policy and member
  notes(scope: "policies" | "enrollments", id: string): Promise<NoteView[]>;
  addNote(scope: "policies" | "enrollments", id: string, body: unknown, idempotencyKey: string): Promise<NoteView>;
  cancelNote(noteId: string, reason: string, idempotencyKey: string): Promise<NoteView>;
  pinNote(noteId: string, pinned: boolean): Promise<NoteView>;

  // Documents (19.3b)
  documents(scope: "policies" | "enrollments", id: string): Promise<PolicyDocumentView[]>;
  documentDownloadUrl(linkId: string, purpose?: string): Promise<{ url: string; expiresAt?: string }>;
  attachDocument(
    scope: "policies" | "enrollments",
    id: string,
    file: File,
    meta: { documentClass: string; title: string; documentDate?: string; description?: string },
    key?: string,
  ): Promise<PolicyDocumentView>;

  // Timeline (19.3c)
  timeline(scope: "policies" | "enrollments", id: string, cursor?: string): Promise<TimelinePage>;

  // Utilization (19.4)
  memberUtilization(beneficiaryId: string, from?: string, to?: string): Promise<MemberUtilizationView>;
  scopeUtilization(scope: "groups" | "plans" | "policies" | "payers", id: string, from?: string, to?: string): Promise<ScopeUtilizationView>;

  // Network administration (19.1b, provider-service)
  networkTiers(): Promise<NetworkTierView[]>;
  createTier(body: unknown, idempotencyKey: string): Promise<NetworkTierView>;
  updateTier(tierId: string, body: unknown): Promise<NetworkTierView>;
  tierAssignments(tierId: string): Promise<TierAssignmentView[]>;
  assignTier(tierId: string, body: unknown, idempotencyKey: string): Promise<TierAssignmentView>;
  revokeAssignment(assignmentId: string): Promise<void>;
  resolveTier(providerId: string, serviceDate: string, locationId?: string): Promise<TierResolutionView>;

  // Bulk (19.5b)
  bulkTemplates(): Promise<BulkTemplateView[]>;
  uploadBulk(
    jobType: string,
    file: File,
    idempotencyKey: string,
    /** Coverage stated once for the whole batch; fills any cell the file leaves blank. No contribution — that
     *  varies member by member, and one batch-wide figure is the mistake this must not make easy. */
    defaults?: { planId?: string | null; networkTierId?: string | null; branchId?: string | null },
  ): Promise<BulkJobView>;
  validateBulk(jobId: string): Promise<BulkValidationView>;
  commitBulk(jobId: string, idempotencyKey: string): Promise<BulkCommitView>;
  bulkRows(jobId: string, status?: string): Promise<BulkRowView[]>;
  bulkReconciliation(jobId: string): Promise<BulkReconciliationView>;

  /** The audited CSV of a utilization scope. Returns the file's text; the caller triggers the download, so
   *  the bytes never leave the authenticated request the way a signed URL would. */
  exportUtilization(scope: string, scopeId: string, from?: string, to?: string): Promise<string>;
}

/**
 * Analytics is served by reporting-service, not policy-service.
 *
 * Both sit behind the same gateway prefix, so the path has to say which one — a relative `/analytics/...`
 * would resolve against whichever service happens to own `/api/v1` today and break silently the moment that
 * changes. The API base already carries `/api/v1`, so this is the sibling segment.
 */
const ANALYTICS = "/analytics";

const q = (filters: Record<string, string | number | undefined>): string => {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(filters)) {
    if (v !== undefined && v !== null && v !== "") p.set(k, String(v));
  }
  const s = p.toString();
  return s ? `?${s}` : "";
};

/** provider-service sits behind the same gateway; only the path prefix differs. */
export function createHttpPolicyApi(): PolicyApi {
  return {
    payers: () => getRaw("/payers") as Promise<PayerView[]>,
    plans: () => getRaw("/plans") as Promise<PlanView[]>,
    benefitCategories: () => getRaw("/benefit-categories") as Promise<BenefitCategoryView[]>,
    planVersions: (planId) => getRaw(`/plans/${planId}/versions`) as Promise<PlanVersionView[]>,
    planVersion: (id) => getRaw(`/plan-versions/${id}`) as Promise<PlanVersionView>,
    setPlanRules: (id, rules, key) => putRaw(`/plan-versions/${id}/rules`, { rules }, key) as Promise<PlanVersionView>,
    validatePlanVersion: (id) => postRaw(`/plan-versions/${id}/validate`, {}) as Promise<ValidationResult>,
    activatePlanVersion: (id, key) => postRaw(`/plan-versions/${id}/activate`, {}, key) as Promise<PlanVersionView>,
    amendPlan: (planId, key) => postRaw(`/plans/${planId}/amend`, {}, key) as Promise<PlanVersionView>,

    policyQuery: (f) => getRaw(`/policy-query${q(f)}`) as Promise<QueryPage<PolicyQueryRow>>,
    policyPlans: (id) => getRaw(`/policies/${id}/plans`) as Promise<PolicyPlanView[]>,
    attachPolicyPlan: (id, body, key) => postRaw(`/policies/${id}/plans`, body, key) as Promise<PolicyPlanView>,
    policyGroups: (id) => getRaw(`/policies/${id}/groups`) as Promise<MemberGroupView[]>,
    createGroup: (id, body, key) => postRaw(`/policies/${id}/groups`, body, key) as Promise<MemberGroupView>,

    memberQuery: (f) => getRaw(`/member-query${q(f)}`) as Promise<QueryPage<MemberQueryRow>>,
    enrollment: (id) => getRaw(`/enrollments/${id}`) as Promise<EnrollmentView>,
    enrol: (body, key) => postRaw("/enrollments", body, key) as Promise<EnrollmentView>,
    terminate: (id, effectiveDate, reason, key) =>
      postRaw(`/enrollments/${id}/terminate`, { effectiveDate, reason }, key) as Promise<EnrollmentView>,
    reinstate: (id, effectiveDate, reason, key) =>
      postRaw(`/enrollments/${id}/reinstate`, { effectiveDate, reason }, key) as Promise<EnrollmentView>,
    changeGroup: (id, groupId, effectiveDate, reason, key) =>
      postRaw(`/enrollments/${id}/change-group`, { groupId, effectiveDate, reason }, key) as Promise<EnrollmentView>,
    changePlan: (id, policyPlanId, effectiveDate, reason, key) =>
      postRaw(`/enrollments/${id}/change-plan`, { policyPlanId, effectiveDate, reason }, key) as Promise<PlanChangeView>,
    previewPlanChange: (id, policyPlanId, effectiveDate) =>
      postRaw(`/enrollments/${id}/change-plan/preview`, { policyPlanId, effectiveDate }) as Promise<PlanChangePreviewView>,
    coverageDetails: (id, asOf) =>
      getRaw(`/enrollments/${id}/coverage-details${q({ asOf })}`) as Promise<MemberCoverageDetail>,

    notes: (scope, id) => getRaw(`/${scope}/${id}/notes`) as Promise<NoteView[]>,
    addNote: (scope, id, body, key) => postRaw(`/${scope}/${id}/notes`, body, key) as Promise<NoteView>,
    cancelNote: (noteId, reason, key) => postRaw(`/notes/${noteId}/cancel`, { reason }, key) as Promise<NoteView>,
    pinNote: (noteId, pinned) => postRaw(`/notes/${noteId}/${pinned ? "pin" : "unpin"}`, {}) as Promise<NoteView>,

    documents: (scope, id) => getRaw(`/${scope}/${id}/documents`) as Promise<PolicyDocumentView[]>,
    // `purpose` reaches the server's audit record verbatim, which is how a LOOK (the eye) is distinguishable
    // from a TAKE (the download) a year later. Both are disclosures; they are not the same disclosure.
    documentDownloadUrl: (linkId, purpose) =>
      getRaw(`/documents/${linkId}/download${q({ purpose })}`) as Promise<{ url: string; expiresAt?: string }>,
    attachDocument: (scope, id, file, meta, key) =>
      postForm(
        `/${scope}/${id}/documents`,
        {
          file,
          documentClass: meta.documentClass,
          title: meta.title,
          ...(meta.documentDate ? { documentDate: meta.documentDate } : {}),
          ...(meta.description ? { description: meta.description } : {}),
        },
        key,
      ) as Promise<PolicyDocumentView>,

    timeline: (scope, id, cursor) => getRaw(`/${scope}/${id}/timeline${q({ cursor })}`) as Promise<TimelinePage>,

    memberUtilization: (beneficiaryId, from, to) =>
      getRaw(`/utilization/members/${beneficiaryId}${q({ from, to })}`) as Promise<MemberUtilizationView>,
    scopeUtilization: (scope, id, from, to) =>
      getRaw(`/utilization/${scope}/${id}${q({ from, to })}`) as Promise<ScopeUtilizationView>,

    networkTiers: () => getRaw("/network-tiers") as Promise<NetworkTierView[]>,
    createTier: (body, key) => postRaw("/network-tiers", body, key) as Promise<NetworkTierView>,
    updateTier: (id, body) => putRaw(`/network-tiers/${id}`, body) as Promise<NetworkTierView>,
    tierAssignments: (id) => getRaw(`/network-tiers/${id}/assignments`) as Promise<TierAssignmentView[]>,
    assignTier: (id, body, key) => postRaw(`/network-tiers/${id}/assignments`, body, key) as Promise<TierAssignmentView>,
    revokeAssignment: async (assignmentId) => {
      await deleteRaw(`/network-tiers/assignments/${assignmentId}`);
    },
    resolveTier: (providerId, serviceDate, locationId) =>
      getRaw(`/network-tiers/resolve${q({ providerId, serviceDate, locationId })}`) as Promise<TierResolutionView>,

    bulkTemplates: () => getRaw("/bulk-templates") as Promise<BulkTemplateView[]>,
    // `jobType` is a query parameter on the service (the body is the multipart file), so it travels in the
    // URL rather than as a form field. The batch defaults ride alongside it: they are recorded on the JOB, so
    // stating them at upload is what makes the dry run and the commit agree about them.
    uploadBulk: (jobType, file, key, defaults) =>
      postForm(
        `/bulk-jobs${q({
          jobType,
          defaultPlanId: defaults?.planId ?? undefined,
          defaultNetworkTierId: defaults?.networkTierId ?? undefined,
          defaultBranchId: defaults?.branchId ?? undefined,
        })}`,
        { file },
        key,
      ) as Promise<BulkJobView>,
    validateBulk: (jobId) => postRaw(`/bulk-jobs/${jobId}/validate`, {}) as Promise<BulkValidationView>,
    commitBulk: (jobId, key) => postRaw(`/bulk-jobs/${jobId}/commit`, {}, key) as Promise<BulkCommitView>,
    bulkRows: (jobId, status) => getRaw(`/bulk-jobs/${jobId}/rows${q({ status })}`) as Promise<BulkRowView[]>,
    bulkReconciliation: (jobId) => getRaw(`/bulk-jobs/${jobId}/reconciliation`) as Promise<BulkReconciliationView>,

    // Analytics lives under a different service, so it does NOT go through the /api/v1 policy base — Kong
    // routes /api/v1/analytics to reporting-service. `analyticsBase` keeps that explicit rather than letting
    // a relative path silently land on whichever service owns the prefix today.
    analytics: (view, filters) =>
      getRaw(`${ANALYTICS}/${view}${q(filters as Record<string, string | undefined>)}`) as Promise<AnalyticsViewResult>,
    analyticsOutlierMembers: (band, filters, limit) =>
      getRaw(`${ANALYTICS}/outliers/members${q({ ...filters, band, limit })}`) as Promise<OutlierRow[]>,
    analyticsExport: (view, filters) => getText(`${ANALYTICS}/${view}/export${q(filters as Record<string, string | undefined>)}`),

    exportUtilization: (scope, scopeId, from, to) =>
      getText(`/utilization/export${q({ scope, scopeId, from, to })}`),
  };
}

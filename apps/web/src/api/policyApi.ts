import { z } from "zod";
import { deleteRaw, getRaw, getText, parseOr, postForm, postRaw, putRaw } from "./http";

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
 *
 * SCHEMA FIRST, TYPE INFERRED. Every shape below is a `z.object` and its interface is `z.infer` of it — one
 * definition, validated at the seam by `parsed()`. The module used to be plain `interface`s cast onto the
 * response with `as Promise<T>`, and a cast asserts a shape rather than checking one: a field the server
 * renamed arrived as `undefined` and reached the screen as an empty cell, a missing sort key or `NaN`.
 *
 * That mattered most exactly here. This surface is where an officer reads a member's annual limit, what they
 * have consumed against it, the deductible and the coinsurance percentage, and then decides whether to move
 * them to a different plan mid-treatment. A blank where a number should be is not a rendering glitch on that
 * screen. The `.nullable()`s above are the deliberate withholdings; `undefined` never was one.
 */

// ── Shared value shapes ─────────────────────────────────────────────────────────────────────────────────

/** A factory rather than a schema: the page is generic in its row, so the row's schema is the argument. */
export const zQueryPage = <T>(item: z.ZodType<T>) =>
  z.object({
    items: z.array(item),
    page: z.number(),
    pageSize: z.number(),
    totalCount: z.number(),
    totalPages: z.number(),
    sortedBy: z.string(),
    /** The caller's payer restriction narrowed this page. Rendered, not swallowed: without it a scoped user
     *  reads "12 policies" as "the organisation has 12 policies". */
    payerScopeApplied: z.boolean(),
    identityMatchTruncated: z.boolean(),
    unavailable: z.array(z.string()),
  }).passthrough();
export type QueryPage<T> = z.infer<ReturnType<typeof zQueryPage<T>>>;

export const zPayerView = z.object({
  payerId: z.string(),
  payerCode: z.string(),
  nameEn: z.string(),
  nameAr: z.string(),
  payerType: z.string(),
  status: z.string(),
}).passthrough();
export type PayerView = z.infer<typeof zPayerView>;

export const zPlanView = z.object({
  planId: z.string(),
  planCode: z.string(),
  nameEn: z.string(),
  nameAr: z.string(),
  description: z.string().nullable().optional(),
  category: z.string(),
  status: z.string(),
}).passthrough();
export type PlanView = z.infer<typeof zPlanView>;

export const zBenefitCategoryView = z.object({
  benefitCategoryId: z.string(),
  code: z.string(),
  name: z.string(),
}).passthrough();
export type BenefitCategoryView = z.infer<typeof zBenefitCategoryView>;

export const zBenefitRuleTierView = z.object({
  ruleTierId: z.string(),
  networkTierId: z.string(),
  tierCode: z.string(),
  isCovered: z.boolean(),
  copayFixed: z.number().nullable().optional(),
  copayPercent: z.number().nullable().optional(),
  coinsurancePercent: z.number().nullable().optional(),
  copayCountsTowardDeductible: z.boolean(),
  requiresPreauthOverride: z.boolean().nullable().optional(),
  limitMultiplier: z.number().nullable().optional(),
  /** Resolved server-side so the editor, eligibility, approvals and claims cannot disagree about what
   *  actually applies at this tier. */
  effectiveRequiresPreauth: z.boolean(),
  effectiveLimitValue: z.number().nullable().optional(),
}).passthrough();
export type BenefitRuleTierView = z.infer<typeof zBenefitRuleTierView>;

export const zBenefitRuleView = z.object({
  ruleId: z.string(),
  benefitCategoryId: z.string(),
  /** 19.6 — the code the rules PUT writes back. Null only when the server had no catalogue to hand. */
  benefitCategoryCode: z.string().nullable().optional(),
  isCovered: z.boolean(),
  limitType: z.string().nullable().optional(),
  limitValue: z.number().nullable().optional(),
  resetPeriod: z.string(),
  deductible: z.number().nullable().optional(),
  deductibleWaived: z.boolean(),
  waitingPeriodDays: z.number(),
  requiresPreauth: z.boolean(),
  preauthCostThreshold: z.number().nullable().optional(),
  exclusions: z.string(),
  notes: z.string().nullable().optional(),
  tiers: z.array(zBenefitRuleTierView),
}).passthrough();
export type BenefitRuleView = z.infer<typeof zBenefitRuleView>;

export const zPlanVersionView = z.object({
  planVersionId: z.string(),
  planId: z.string(),
  versionNo: z.number(),
  effectiveFrom: z.string(),
  effectiveTo: z.string().nullable().optional(),
  status: z.string(),
  /** Projected by the server rather than derived from `status` here — the read-only affordance and the API's
   *  409 must agree, and deriving the rule twice is how they drift apart. */
  editable: z.boolean(),
  activatedAt: z.string().nullable().optional(),
  supersededByVersionId: z.string().nullable().optional(),
  rules: z.array(zBenefitRuleView),
}).passthrough();
export type PlanVersionView = z.infer<typeof zPlanVersionView>;

export const zActivationProblem = z.object({
  code: z.string(),
  detail: z.string(),
}).passthrough();
export type ActivationProblem = z.infer<typeof zActivationProblem>;

export const zValidationResult = z.object({
  valid: z.boolean(),
  problems: z.array(zActivationProblem),
}).passthrough();
export type ValidationResult = z.infer<typeof zValidationResult>;

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

export const zPolicyQueryRow = z.object({
  policyId: z.string(),
  policyNo: z.string(),
  payerId: z.string().nullable().optional(),
  status: z.string(),
  effectiveFrom: z.string(),
  effectiveTo: z.string().nullable().optional(),
  memberCount: z.number(),
  memberCountBand: z.string(),
  maxMembers: z.number().nullable().optional(),
  planCount: z.number(),
  totalLimit: z.number().nullable().optional(),
  totalConsumed: z.number().nullable().optional(),
  percentUsed: z.number().nullable().optional(),
  utilizationBand: z.string(),
}).passthrough();
export type PolicyQueryRow = z.infer<typeof zPolicyQueryRow>;

export const zMemberQueryRow = z.object({
  enrollmentId: z.string(),
  beneficiaryId: z.string(),
  memberNo: z.string(),
  givenName: z.string().nullable().optional(),
  familyName: z.string().nullable().optional(),
  beneficiaryStatus: z.string().nullable().optional(),
  /** The number printed on the card the beneficiary hands over — how a desk matches person to row. */
  cardNumber: z.string().nullable().optional(),
  policyId: z.string(),
  policyPlanId: z.string(),
  planLabel: z.string().nullable().optional(),
  groupId: z.string().nullable().optional(),
  payerId: z.string().nullable().optional(),
  relationship: z.string(),
  status: z.string(),
  effectiveFrom: z.string(),
  effectiveTo: z.string().nullable().optional(),
  waitingPeriodEndsOn: z.string().nullable().optional(),
  waitingPeriodState: z.string(),
  branchId: z.string().nullable().optional(),
  terminationReason: z.string().nullable().optional(),
  totalLimit: z.number().nullable().optional(),
  totalConsumed: z.number().nullable().optional(),
  totalRemaining: z.number().nullable().optional(),
  percentUsed: z.number().nullable().optional(),
  utilizationBand: z.string(),
}).passthrough();
export type MemberQueryRow = z.infer<typeof zMemberQueryRow>;

/** One person on the same cover. Names ride on the same per-request lookup the roster uses, so they are null
 *  under exactly the same conditions — patient-service could not be asked. */
export const zCoveredFamilyMember = z.object({
  enrollmentId: z.string(),
  beneficiaryId: z.string(),
  memberNo: z.string(),
  givenName: z.string().nullable().optional(),
  familyName: z.string().nullable().optional(),
  relationship: z.string(),
  status: z.string(),
  isPrincipal: z.boolean(),
  planLabel: z.string().nullable().optional(),
  effectiveFrom: z.string().nullable().optional(),
  effectiveTo: z.string().nullable().optional(),
  /** The member the list was opened from. Marked rather than removed — a family list missing the person you
   *  are looking at reads as a list with somebody missing. */
  isSubject: z.boolean(),
}).passthrough();
export type CoveredFamilyMember = z.infer<typeof zCoveredFamilyMember>;

export const zFamilyView = z.object({
  enrollmentId: z.string(),
  members: z.array(zCoveredFamilyMember),
  unavailable: z.array(z.string()),
  /** Household members behind a payer this caller may not read. Counted, so a family of five never renders as
   *  three with nothing to say why. */
  withheld: z.number(),
}).passthrough();
export type FamilyView = z.infer<typeof zFamilyView>;

export const zPolicyPlanView = z.object({
  policyPlanId: z.string(),
  policyId: z.string(),
  planVersionId: z.string(),
  planLabel: z.string(),
  effectiveFrom: z.string(),
  effectiveTo: z.string().nullable().optional(),
  isDefault: z.boolean(),
  eligibilityRule: z.string().nullable().optional(),
  maxMembers: z.number().nullable().optional(),
  status: z.string(),
  memberCount: z.number(),
}).passthrough();
export type PolicyPlanView = z.infer<typeof zPolicyPlanView>;

export const zMemberGroupView = z.object({
  groupId: z.string(),
  policyId: z.string(),
  groupCode: z.string(),
  nameEn: z.string(),
  nameAr: z.string(),
  groupType: z.string(),
  effectiveFrom: z.string(),
  effectiveTo: z.string().nullable().optional(),
  status: z.string(),
}).passthrough();
export type MemberGroupView = z.infer<typeof zMemberGroupView>;

export const zEnrollmentView = z.object({
  enrollmentId: z.string(),
  beneficiaryId: z.string(),
  policyId: z.string(),
  policyPlanId: z.string(),
  groupId: z.string().nullable().optional(),
  memberNo: z.string(),
  relationship: z.string(),
  principalEnrollmentId: z.string().nullable().optional(),
  effectiveFrom: z.string(),
  effectiveTo: z.string().nullable().optional(),
  waitingPeriodEndsOn: z.string().nullable().optional(),
  status: z.string(),
  terminationReason: z.string().nullable().optional(),
  sourcePlanVersionId: z.string().nullable().optional(),
  coveragesGenerated: z.number(),
}).passthrough();
export type EnrollmentView = z.infer<typeof zEnrollmentView>;

export const zCarriedLimitView = z.object({
  benefitCategoryId: z.string(),
  benefitCategoryCode: z.string().nullable().optional(),
  limitValue: z.number().nullable().optional(),
  consumedValue: z.number(),
  remaining: z.number().nullable().optional(),
  exhausted: z.boolean(),
}).passthrough();
export type CarriedLimitView = z.infer<typeof zCarriedLimitView>;

/** A benefit the member holds today and would not hold after the change. */
export const zDroppedCategoryView = z.object({
  benefitCategoryId: z.string(),
  benefitCategoryCode: z.string().nullable().optional(),
  currentLimitValue: z.number().nullable().optional(),
  consumedValue: z.number(),
}).passthrough();
export type DroppedCategoryView = z.infer<typeof zDroppedCategoryView>;

export const zPlanChangeView = z.object({
  enrollmentId: z.string(),
  policyPlanId: z.string(),
  planVersionId: z.string(),
  consumptionPolicy: z.string(),
  carriedLimits: z.array(zCarriedLimitView),
  droppedCategories: z.array(zDroppedCategoryView),
}).passthrough();
export type PlanChangeView = z.infer<typeof zPlanChangeView>;

export const zCarryPreviewRow = z.object({
  benefitCategoryId: z.string(),
  benefitCategoryCode: z.string().nullable().optional(),
  /** False when the new plan ADDS this benefit — distinguishes "unbounded today" from "not covered today",
   *  which a null current limit alone cannot. */
  held: z.boolean(),
  currentLimitValue: z.number().nullable().optional(),
  consumedValue: z.number(),
  newLimitValue: z.number().nullable().optional(),
  remaining: z.number().nullable().optional(),
  exhausted: z.boolean(),
}).passthrough();
export type CarryPreviewRow = z.infer<typeof zCarryPreviewRow>;

/**
 * The plan-change DRY RUN. One row per category the new plan covers, carrying BOTH ceilings so the officer can
 * see what is being moved away from, plus the categories that disappear entirely.
 *
 * The client does not compute any of this. Which consumption travels is a server setting (ADR-0020, unsigned),
 * the new plan's limits are the other half of the sum, and a dropped category produces no row at all — so an
 * estimate assembled here would disagree with the outcome exactly when somebody is deciding whether to move a
 * patient mid-treatment.
 */
export const zPlanChangePreviewView = z.object({
  enrollmentId: z.string(),
  fromPolicyPlanId: z.string(),
  toPolicyPlanId: z.string(),
  toPlanLabel: z.string(),
  planVersionId: z.string(),
  effectiveDate: z.string(),
  consumptionPolicy: z.string(),
  rows: z.array(zCarryPreviewRow),
  droppedCategories: z.array(zDroppedCategoryView),
}).passthrough();
export type PlanChangePreviewView = z.infer<typeof zPlanChangePreviewView>;



// ── Analytics (19.6b) ───────────────────────────────────────────────────────────────────────────────────

/** One plotted value. `dimensionId` is what makes a drill-down possible without the client resolving a label
 *  back to an id — a round trip that guesses, and guesses wrong the moment two plans share a label. */
export const zAnalyticsPoint = z.object({
  key: z.string(),
  labelEn: z.string(),
  labelAr: z.string(),
  value: z.number(),
  dimensionId: z.string().nullable().optional(),
  secondary: z.number().nullable().optional(),
}).passthrough();
export type AnalyticsPoint = z.infer<typeof zAnalyticsPoint>;

/**
 * A chart AND the accessible table that always accompanies it.
 *
 * `columns` and `summaryEn`/`summaryAr` come from the server rather than being composed here: a caption the
 * client invents drifts from the data the moment a series changes shape, and the R2 audit finding (U6) is
 * specifically that an alternative nobody maintains is not an alternative.
 */
export const zAnalyticsSeries = z.object({
  key: z.string(),
  titleEn: z.string(),
  titleAr: z.string(),
  unit: z.string(),
  points: z.array(zAnalyticsPoint),
  summaryEn: z.string(),
  summaryAr: z.string(),
  /**
   * Bilingual, like every other label on the series. It was `string[]` — the last monolingual text on the
   * dashboard, and it sat on the accessible table, so an Arabic reader who could not see the chart got the
   * one part naming what each number IS in English (audit §3.1). Authored server-side rather than mapped
   * here: a client-side table of header translations is a second place deciding what "Net payable" is called.
   */
  columns: z.array(z.object({ en: z.string(), ar: z.string() })),
}).passthrough();
export type AnalyticsSeries = z.infer<typeof zAnalyticsSeries>;

/** A period-over-period movement. `direction` is a WORD because the four-cue rule needs a text cue, and
 *  `better` is separate because direction and desirability are different facts. */
export const zAnalyticsDelta = z.object({
  key: z.string(),
  labelEn: z.string(),
  labelAr: z.string(),
  current: z.number(),
  previous: z.number(),
  percentChange: z.number().nullable().optional(),
  direction: z.enum(["Up", "Down", "Flat"]),
  better: z.boolean().nullable().optional(),
}).passthrough();
export type AnalyticsDelta = z.infer<typeof zAnalyticsDelta>;

export const zAnalyticsViewResult = z.object({
  view: z.string(),
  series: z.array(zAnalyticsSeries),
  deltas: z.array(zAnalyticsDelta),
  /** True when the caller's payer scope narrowed the aggregate — surfaced so a small number reads as
   *  "your scope" rather than "the programme shrank". */
  payerScopeApplied: z.boolean(),
  unavailable: z.array(z.string()),
}).passthrough();
export type AnalyticsViewResult = z.infer<typeof zAnalyticsViewResult>;

/** A drill-down row: pointers and figures, never identity. Resolving the person is the audited step after. */
export const zOutlierRow = z.object({
  enrollmentId: z.string(),
  beneficiaryId: z.string(),
  policyId: z.string(),
  policyPlanId: z.string().nullable().optional(),
  limit: z.number(),
  consumed: z.number(),
  band: z.string(),
}).passthrough();
export type OutlierRow = z.infer<typeof zOutlierRow>;

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

export const zTierCostShare = z.object({
  networkTierId: z.string(),
  tierCode: z.string(),
  isCovered: z.boolean(),
  copayFixed: z.number().nullable().optional(),
  copayPercent: z.number().nullable().optional(),
  coinsurancePercent: z.number().nullable().optional(),
  copayCountsTowardDeductible: z.boolean(),
  requiresPreauth: z.boolean(),
  limitAtTier: z.number().nullable().optional(),
}).passthrough();
export type TierCostShare = z.infer<typeof zTierCostShare>;

export const zCategoryCoverageDetail = z.object({
  benefitCategoryCode: z.string(),
  isCovered: z.boolean(),
  limitType: z.string().nullable().optional(),
  limit: z.number().nullable().optional(),
  consumed: z.number(),
  remaining: z.number().nullable().optional(),
  percentUsed: z.number().nullable().optional(),
  currencyCode: z.string(),
  resetPeriod: z.string(),
  resetsOn: z.string().nullable().optional(),
  configuredLimit: z.number().nullable().optional(),
  /** The member's generated ceiling differs from what the plan in force would grant today — a real and
   *  legitimate divergence after an amendment, surfaced rather than hidden. */
  limitDiffersFromPlan: z.boolean(),
  waitingPeriodEndsOn: z.string().nullable().optional(),
  waitingPeriodState: z.string(),
  requiresPreauth: z.boolean(),
  preauthCostThreshold: z.number().nullable().optional(),
  deductible: z.number().nullable().optional(),
  deductibleWaived: z.boolean(),
  exclusions: z.array(z.string()),
  costShareByTier: z.array(zTierCostShare),
}).passthrough();
export type CategoryCoverageDetail = z.infer<typeof zCategoryCoverageDetail>;

export const zMemberCoverageDetail = z.object({
  enrollmentId: z.string(),
  beneficiaryId: z.string(),
  memberNo: z.string(),
  policyId: z.string(),
  policyPlanId: z.string(),
  planLabel: z.string(),
  planId: z.string().nullable().optional(),
  planVersionInForceId: z.string().nullable().optional(),
  planVersionNo: z.number().nullable().optional(),
  planVersionFrom: z.string().nullable().optional(),
  planVersionTo: z.string().nullable().optional(),
  planVersionStatus: z.string().nullable().optional(),
  enrolledUnderPlanVersionId: z.string().nullable().optional(),
  planVersionChangedSinceEnrolment: z.boolean(),
  asOf: z.string(),
  enrollmentStatus: z.string(),
  effectiveFrom: z.string(),
  effectiveTo: z.string().nullable().optional(),
  categories: z.array(zCategoryCoverageDetail),
}).passthrough();
export type MemberCoverageDetail = z.infer<typeof zMemberCoverageDetail>;

export const zNoteView = z.object({
  noteId: z.string(),
  scope: z.string(),
  scopeRef: z.string(),
  noteType: z.string(),
  visibilityClass: z.string(),
  /** Null WITH `bodyWithheld` true is a projection, not an empty note. The screens render a locked state. */
  body: z.string().nullable().optional(),
  bodyWithheld: z.boolean(),
  withheldReason: z.string().nullable().optional(),
  authoredByUsername: z.string(),
  authoredByDisplay: z.string(),
  authoredAt: z.string(),
  status: z.string(),
  cancelledByUsername: z.string().nullable().optional(),
  cancelledAt: z.string().nullable().optional(),
  cancellationReason: z.string().nullable().optional(),
  supersedesNoteId: z.string().nullable().optional(),
  pinned: z.boolean(),
  canCancel: z.boolean(),
}).passthrough();
export type NoteView = z.infer<typeof zNoteView>;

export const zPolicyDocumentView = z.object({
  linkId: z.string(),
  scope: z.string(),
  scopeRef: z.string(),
  documentId: z.string(),
  versionNo: z.number(),
  supersedesLinkId: z.string().nullable().optional(),
  documentClass: z.string(),
  visibilityClass: z.string(),
  sensitiveCategory: z.string().nullable().optional(),
  title: z.string(),
  description: z.string().nullable().optional(),
  documentDate: z.string().nullable().optional(),
  issuingProvider: z.string().nullable().optional(),
  uploadedByUsername: z.string(),
  uploadedByDisplay: z.string(),
  uploadedAt: z.string(),
  status: z.string(),
  withdrawnByUsername: z.string().nullable().optional(),
  withdrawnAt: z.string().nullable().optional(),
  withdrawalReason: z.string().nullable().optional(),
  expiresOn: z.string().nullable().optional(),
  expired: z.boolean(),
  verifiedByUsername: z.string().nullable().optional(),
  verifiedAt: z.string().nullable().optional(),
  canDownload: z.boolean(),
}).passthrough();
export type PolicyDocumentView = z.infer<typeof zPolicyDocumentView>;

export const zTimelineEntryView = z.object({
  entryId: z.string(),
  scope: z.string(),
  scopeRef: z.string(),
  occurredAt: z.string(),
  eventType: z.string(),
  eventCategory: z.string(),
  actorUsername: z.string().nullable().optional(),
  actorDisplay: z.string().nullable().optional(),
  summaryEn: z.string(),
  summaryAr: z.string(),
  changeDiff: z.string().nullable().optional(),
  diffWithheld: z.boolean(),
  visibilityClass: z.string(),
  sourceService: z.string(),
  correlationId: z.string().nullable().optional(),
  targetRef: z.string().nullable().optional(),
  targetKind: z.string().nullable().optional(),
  /** True when the entry was read off the membership record rather than projected from an event. Only the
   *  origin entry is ever derived, and the panel says so on the row. */
  derived: z.boolean().optional(),
}).passthrough();
export type TimelineEntryView = z.infer<typeof zTimelineEntryView>;

export const zTimelinePage = z.object({
  entries: z.array(zTimelineEntryView),
  nextCursor: z.string().nullable().optional(),
  /** The record's creation, returned on the first page only and excluded from `entries`. Null on a policy
   *  timeline and on an id the service does not know. */
  origin: zTimelineEntryView.nullable().optional(),
}).passthrough();
export type TimelinePage = z.infer<typeof zTimelinePage>;

export const zCategoryUtilizationView = z.object({
  benefitCategory: z.string(),
  limitType: z.string().nullable().optional(),
  limit: z.number().nullable().optional(),
  consumed: z.number(),
  remaining: z.number().nullable().optional(),
  percentUsed: z.number().nullable().optional(),
  unlimited: z.boolean(),
  currencyCode: z.string(),
  resetPeriod: z.string(),
  resetsOn: z.string().nullable().optional(),
  windowActivity: z.number().nullable().optional(),
  windowEvents: z.number(),
}).passthrough();
export type CategoryUtilizationView = z.infer<typeof zCategoryUtilizationView>;

export const zTierUtilizationView = z.object({
  tierCode: z.string(),
  outOfNetwork: z.boolean(),
  /** False = the movement's provider was unknown. Never folded into in-network, which would flatter the
   *  network on the single number it is judged by. */
  attributed: z.boolean(),
  netQuantity: z.number(),
  events: z.number(),
}).passthrough();
export type TierUtilizationView = z.infer<typeof zTierUtilizationView>;

export const zExternalUtilizationView = z.object({
  encounters: z.number().nullable().optional(),
  authorizationsRaised: z.number().nullable().optional(),
  authorizationsApproved: z.number().nullable().optional(),
  authorizationsDenied: z.number().nullable().optional(),
  claimedAmount: z.number().nullable().optional(),
  approvedAmount: z.number().nullable().optional(),
  memberShareAmount: z.number().nullable().optional(),
  currencyCode: z.string(),
  /** Services that did not answer. A null here means "could not ask", never "zero". */
  unavailable: z.array(z.string()),
}).passthrough();
export type ExternalUtilizationView = z.infer<typeof zExternalUtilizationView>;

export const zReconciliationView = z.object({
  accumulatorTotal: z.number(),
  reportedTotal: z.number(),
  reconciled: z.boolean(),
}).passthrough();
export type ReconciliationView = z.infer<typeof zReconciliationView>;

export const zMemberUtilizationView = z.object({
  beneficiaryId: z.string(),
  enrollmentId: z.string(),
  memberNo: z.string(),
  asOf: z.string(),
  windowFrom: z.string(),
  windowTo: z.string(),
  categories: z.array(zCategoryUtilizationView),
  byNetworkTier: z.array(zTierUtilizationView),
  external: zExternalUtilizationView,
  reconciliation: zReconciliationView,
}).passthrough();
export type MemberUtilizationView = z.infer<typeof zMemberUtilizationView>;

export const zMemberRowView = z.object({
  beneficiaryId: z.string(),
  enrollmentId: z.string(),
  memberNo: z.string(),
  policyPlanId: z.string(),
  groupId: z.string().nullable().optional(),
  totalLimit: z.number(),
  totalConsumed: z.number(),
  totalRemaining: z.number(),
  percentUsed: z.number().nullable().optional(),
  anyUnlimited: z.boolean(),
}).passthrough();
export type MemberRowView = z.infer<typeof zMemberRowView>;

export const zScopeUtilizationView = z.object({
  scope: z.string(),
  scopeId: z.string(),
  asOf: z.string(),
  windowFrom: z.string(),
  windowTo: z.string(),
  memberCount: z.number(),
  totalLimit: z.number(),
  totalConsumed: z.number(),
  totalRemaining: z.number(),
  percentUsed: z.number().nullable().optional(),
  outlierThresholdPercent: z.number(),
  members: z.array(zMemberRowView),
  outliers: z.array(zMemberRowView),
  distribution: z.array(z.object({ label: z.string(), memberCount: z.number() })),
  byNetworkTier: z.array(zTierUtilizationView),
  external: zExternalUtilizationView,
  reconciliation: zReconciliationView,
}).passthrough();
export type ScopeUtilizationView = z.infer<typeof zScopeUtilizationView>;

// ── Network tiers (provider-service) ────────────────────────────────────────────────────────────────────

export const zNetworkTierView = z.object({
  networkTierId: z.string(),
  tierCode: z.string(),
  nameEn: z.string(),
  nameAr: z.string(),
  rank: z.number(),
  description: z.string().nullable().optional(),
  isOutOfNetwork: z.boolean(),
  status: z.string(),
}).passthrough();
export type NetworkTierView = z.infer<typeof zNetworkTierView>;

export const zTierAssignmentView = z.object({
  assignmentId: z.string(),
  networkTierId: z.string(),
  tierCode: z.string().nullable().optional(),
  providerId: z.string(),
  scope: z.string(),
  scopeRef: z.string(),
  effectiveFrom: z.string(),
  effectiveTo: z.string().nullable().optional(),
  status: z.string(),
}).passthrough();
export type TierAssignmentView = z.infer<typeof zTierAssignmentView>;

export const zTierResolutionView = z.object({
  networkTierId: z.string(),
  tierCode: z.string(),
  nameEn: z.string(),
  nameAr: z.string(),
  rank: z.number(),
  isOutOfNetwork: z.boolean(),
  /** "assigned to the out-of-network tier" and "out-of-network because nothing was assigned" price the same
   *  and need very different follow-up. The basis is what tells them apart. */
  basis: z.string(),
  assignmentId: z.string().nullable().optional(),
  providerId: z.string(),
  locationId: z.string().nullable().optional(),
  serviceCode: z.string().nullable().optional(),
  serviceDate: z.string(),
}).passthrough();
export type TierResolutionView = z.infer<typeof zTierResolutionView>;

// ── Bulk upload (19.5b) ─────────────────────────────────────────────────────────────────────────────────

export const zBulkColumnView = z.object({
  name: z.string(),
  kind: z.string(),
  required: z.boolean(),
  descriptionEn: z.string(),
  descriptionAr: z.string(),
}).passthrough();
export type BulkColumnView = z.infer<typeof zBulkColumnView>;

export const zBulkTemplateView = z.object({
  jobType: z.string(),
  purposeEn: z.string(),
  purposeAr: z.string(),
  columns: z.array(zBulkColumnView),
}).passthrough();
export type BulkTemplateView = z.infer<typeof zBulkTemplateView>;

export const zBulkJobView = z.object({
  jobId: z.string(),
  jobType: z.string(),
  fileName: z.string(),
  status: z.string(),
  batchId: z.string(),
  totalRows: z.number(),
  validRows: z.number(),
  invalidRows: z.number(),
  appliedRows: z.number(),
  failedRows: z.number(),
  skippedRows: z.number(),
  /** submitted = valid + invalid, and once complete valid = applied + failed + skipped. A job that cannot
   *  say what happened to a row is one that lost it. */
  balances: z.boolean(),
  failureCode: z.string().nullable().optional(),
  failureDetail: z.string().nullable().optional(),
  fileDocumentId: z.string().nullable().optional(),
  errorDocumentId: z.string().nullable().optional(),
  submittedBy: z.string().nullable().optional(),
  submittedAt: z.string(),
  completedAt: z.string().nullable().optional(),
  rolledBackAt: z.string().nullable().optional(),
}).passthrough();
export type BulkJobView = z.infer<typeof zBulkJobView>;

export const zBulkRowError = z.object({
  rowNumber: z.number(),
  code: z.string(),
  detailEn: z.string(),
  detailAr: z.string(),
}).passthrough();
export type BulkRowError = z.infer<typeof zBulkRowError>;

export const zBulkRowPreview = z.object({
  rowNumber: z.number(),
  summaryEn: z.string(),
  summaryAr: z.string(),
  changes: z.record(z.unknown()),
}).passthrough();
export type BulkRowPreview = z.infer<typeof zBulkRowPreview>;

export const zBulkRowView = z.object({
  rowNumber: z.number(),
  status: z.string(),
  errorCode: z.string().nullable().optional(),
  errorDetail: z.string().nullable().optional(),
  errorDetailAr: z.string().nullable().optional(),
  targetRef: z.string().nullable().optional(),
  appliedAt: z.string().nullable().optional(),
}).passthrough();
export type BulkRowView = z.infer<typeof zBulkRowView>;

/** The dry run. `wouldChange` is what earns the step: counts alone tell an operator that 9,963 rows are
 *  valid, not that the file is about to move everybody onto the wrong plan. */
export const zBulkValidationView = z.object({
  job: zBulkJobView,
  totalErrors: z.number(),
  errors: z.array(zBulkRowError),
  wouldChange: z.array(zBulkRowPreview),
  committable: z.boolean(),
}).passthrough();
export type BulkValidationView = z.infer<typeof zBulkValidationView>;

export const zBulkCommitView = z.object({
  job: zBulkJobView,
  totalErrors: z.number(),
  errors: z.array(zBulkRowError),
}).passthrough();
export type BulkCommitView = z.infer<typeof zBulkCommitView>;

export const zBulkReconciliationView = z.object({
  jobId: z.string(),
  jobType: z.string(),
  status: z.string(),
  batchId: z.string(),
  submitted: z.number(),
  valid: z.number(),
  invalid: z.number(),
  applied: z.number(),
  failed: z.number(),
  skipped: z.number(),
  balances: z.boolean(),
  errorDocumentId: z.string().nullable().optional(),
}).passthrough();
export type BulkReconciliationView = z.infer<typeof zBulkReconciliationView>;

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
  /** Who else this cover reaches — the principal and every dependant under them, this member included. */
  family(enrollmentId: string): Promise<FamilyView>;

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

/**
 * Validate a response against the schema its type was inferred from, turning contract drift into a loud
 * `ApiError("schema")` instead of a screen full of blanks and NaN.
 *
 * Every operation below used to end in `as Promise<SomeView>` — a cast, which asserts a shape rather than
 * checking one. The module's own header explained why (see it for the argument and why it does not hold).
 * Roughly fifty operations, carrying limits, consumed amounts, deductibles and coinsurance percentages, were
 * outside the loud-failure behaviour the rest of the app has relied on since phase 12.
 */
const parsed = <T>(schema: z.ZodType<T>, p: Promise<unknown>): Promise<T> => p.then((d) => parseOr(schema, d));

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
    payers: () => parsed(z.array(zPayerView), getRaw("/payers")),
    plans: () => parsed(z.array(zPlanView), getRaw("/plans")),
    benefitCategories: () => parsed(z.array(zBenefitCategoryView), getRaw("/benefit-categories")),
    planVersions: (planId) => parsed(z.array(zPlanVersionView), getRaw(`/plans/${planId}/versions`)),
    planVersion: (id) => parsed(zPlanVersionView, getRaw(`/plan-versions/${id}`)),
    setPlanRules: (id, rules, key) => parsed(zPlanVersionView, putRaw(`/plan-versions/${id}/rules`, { rules }, key)),
    validatePlanVersion: (id) => parsed(zValidationResult, postRaw(`/plan-versions/${id}/validate`, {})),
    activatePlanVersion: (id, key) => parsed(zPlanVersionView, postRaw(`/plan-versions/${id}/activate`, {}, key)),
    amendPlan: (planId, key) => parsed(zPlanVersionView, postRaw(`/plans/${planId}/amend`, {}, key)),

    policyQuery: (f) => parsed(zQueryPage(zPolicyQueryRow), getRaw(`/policy-query${q(f)}`)),
    policyPlans: (id) => parsed(z.array(zPolicyPlanView), getRaw(`/policies/${id}/plans`)),
    attachPolicyPlan: (id, body, key) => parsed(zPolicyPlanView, postRaw(`/policies/${id}/plans`, body, key)),
    policyGroups: (id) => parsed(z.array(zMemberGroupView), getRaw(`/policies/${id}/groups`)),
    createGroup: (id, body, key) => parsed(zMemberGroupView, postRaw(`/policies/${id}/groups`, body, key)),

    memberQuery: (f) => parsed(zQueryPage(zMemberQueryRow), getRaw(`/member-query${q(f)}`)),
    enrollment: (id) => parsed(zEnrollmentView, getRaw(`/enrollments/${id}`)),
    enrol: (body, key) => parsed(zEnrollmentView, postRaw("/enrollments", body, key)),
    terminate: (id, effectiveDate, reason, key) => parsed(zEnrollmentView, postRaw(`/enrollments/${id}/terminate`, { effectiveDate, reason }, key)),
    reinstate: (id, effectiveDate, reason, key) => parsed(zEnrollmentView, postRaw(`/enrollments/${id}/reinstate`, { effectiveDate, reason }, key)),
    changeGroup: (id, groupId, effectiveDate, reason, key) => parsed(zEnrollmentView, postRaw(`/enrollments/${id}/change-group`, { groupId, effectiveDate, reason }, key)),
    changePlan: (id, policyPlanId, effectiveDate, reason, key) => parsed(zPlanChangeView, postRaw(`/enrollments/${id}/change-plan`, { policyPlanId, effectiveDate, reason }, key)),
    previewPlanChange: (id, policyPlanId, effectiveDate) => parsed(zPlanChangePreviewView, postRaw(`/enrollments/${id}/change-plan/preview`, { policyPlanId, effectiveDate })),
    coverageDetails: (id, asOf) => parsed(zMemberCoverageDetail, getRaw(`/enrollments/${id}/coverage-details${q({ asOf })}`)),
    family: (id) => parsed(zFamilyView, getRaw(`/enrollments/${id}/family`)),

    notes: (scope, id) => parsed(z.array(zNoteView), getRaw(`/${scope}/${id}/notes`)),
    addNote: (scope, id, body, key) => parsed(zNoteView, postRaw(`/${scope}/${id}/notes`, body, key)),
    cancelNote: (noteId, reason, key) => parsed(zNoteView, postRaw(`/notes/${noteId}/cancel`, { reason }, key)),
    pinNote: (noteId, pinned) => parsed(zNoteView, postRaw(`/notes/${noteId}/${pinned ? "pin" : "unpin"}`, {})),

    documents: (scope, id) => parsed(z.array(zPolicyDocumentView), getRaw(`/${scope}/${id}/documents`)),
    // `purpose` reaches the server's audit record verbatim, which is how a LOOK (the eye) is distinguishable
    // from a TAKE (the download) a year later. Both are disclosures; they are not the same disclosure.
    documentDownloadUrl: (linkId, purpose) => parsed(z.object({ url: z.string(), expiresAt: z.string().optional() }).passthrough(), getRaw(`/documents/${linkId}/download${q({ purpose })}`)),
    attachDocument: (scope, id, file, meta, key) => parsed(zPolicyDocumentView, postForm(
        `/${scope}/${id}/documents`,
        {
          file,
          documentClass: meta.documentClass,
          title: meta.title,
          ...(meta.documentDate ? { documentDate: meta.documentDate } : {}),
          ...(meta.description ? { description: meta.description } : {}),
        },
        key,
      )),

    timeline: (scope, id, cursor) => parsed(zTimelinePage, getRaw(`/${scope}/${id}/timeline${q({ cursor })}`)),

    memberUtilization: (beneficiaryId, from, to) => parsed(zMemberUtilizationView, getRaw(`/utilization/members/${beneficiaryId}${q({ from, to })}`)),
    scopeUtilization: (scope, id, from, to) => parsed(zScopeUtilizationView, getRaw(`/utilization/${scope}/${id}${q({ from, to })}`)),

    networkTiers: () => parsed(z.array(zNetworkTierView), getRaw("/network-tiers")),
    createTier: (body, key) => parsed(zNetworkTierView, postRaw("/network-tiers", body, key)),
    updateTier: (id, body) => parsed(zNetworkTierView, putRaw(`/network-tiers/${id}`, body)),
    tierAssignments: (id) => parsed(z.array(zTierAssignmentView), getRaw(`/network-tiers/${id}/assignments`)),
    assignTier: (id, body, key) => parsed(zTierAssignmentView, postRaw(`/network-tiers/${id}/assignments`, body, key)),
    revokeAssignment: async (assignmentId) => {
      await deleteRaw(`/network-tiers/assignments/${assignmentId}`);
    },
    resolveTier: (providerId, serviceDate, locationId) => parsed(zTierResolutionView, getRaw(`/network-tiers/resolve${q({ providerId, serviceDate, locationId })}`)),

    bulkTemplates: () => parsed(z.array(zBulkTemplateView), getRaw("/bulk-templates")),
    // `jobType` is a query parameter on the service (the body is the multipart file), so it travels in the
    // URL rather than as a form field. The batch defaults ride alongside it: they are recorded on the JOB, so
    // stating them at upload is what makes the dry run and the commit agree about them.
    uploadBulk: (jobType, file, key, defaults) => parsed(zBulkJobView, postForm(
        `/bulk-jobs${q({
          jobType,
          defaultPlanId: defaults?.planId ?? undefined,
          defaultNetworkTierId: defaults?.networkTierId ?? undefined,
          defaultBranchId: defaults?.branchId ?? undefined,
        })}`,
        { file },
        key,
      )),
    validateBulk: (jobId) => parsed(zBulkValidationView, postRaw(`/bulk-jobs/${jobId}/validate`, {})),
    commitBulk: (jobId, key) => parsed(zBulkCommitView, postRaw(`/bulk-jobs/${jobId}/commit`, {}, key)),
    bulkRows: (jobId, status) => parsed(z.array(zBulkRowView), getRaw(`/bulk-jobs/${jobId}/rows${q({ status })}`)),
    bulkReconciliation: (jobId) => parsed(zBulkReconciliationView, getRaw(`/bulk-jobs/${jobId}/reconciliation`)),

    // Analytics lives under a different service, so it does NOT go through the /api/v1 policy base — Kong
    // routes /api/v1/analytics to reporting-service. `analyticsBase` keeps that explicit rather than letting
    // a relative path silently land on whichever service owns the prefix today.
    analytics: (view, filters) => parsed(zAnalyticsViewResult, getRaw(`${ANALYTICS}/${view}${q(filters as Record<string, string | undefined>)}`)),
    analyticsOutlierMembers: (band, filters, limit) => parsed(z.array(zOutlierRow), getRaw(`${ANALYTICS}/outliers/members${q({ ...filters, band, limit })}`)),
    analyticsExport: (view, filters) => getText(`${ANALYTICS}/${view}/export${q(filters as Record<string, string | undefined>)}`),

    exportUtilization: (scope, scopeId, from, to) =>
      getText(`/utilization/export${q({ scope, scopeId, from, to })}`),
  };
}

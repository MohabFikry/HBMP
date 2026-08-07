/**
 * @mersal/contracts — the shared TS/zod contract mirror for the Mersal HBMP portals.
 *
 * These schemas are the single source of truth for the request/response shapes the flagship screens (Phase
 * 9.3) exchange with the phase APIs. They encode the min-necessary + accessibility invariants structurally:
 *  - eligibility/lab/pharmacy carry MASKED refs and no cross-zone clinical fields;
 *  - the approvals decision refuses reject/partial without a rationale (US-060);
 *  - every dashboard chart carries a required accessible data-table (US-073);
 *  - every human label is bilingual.
 * The same package can later back contract tests that assert the services conform.
 */
export * from "./common";
export * from "./eligibility";
export * from "./emr";
export * from "./lab";
export * from "./pharmacy";
export * from "./approvals";
export * from "./dashboard";
export * from "./case";
export * from "./finance";
export * from "./notification";
export * from "./admin";
export * from "./reception";
export * from "./clinician";
export * from "./fulfillment";
export * from "./formulary";
export * from "./prescribing";
export * from "./investigations";
export * from "./approvals-extra";
export * from "./report-view";
export * from "./provider";
export * from "./beneficiary";
export * from "./claims";
export * from "./profile";
export * from "./access";

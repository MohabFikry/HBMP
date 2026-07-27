import { z } from "zod";
import { zDate, zId, zLocalized, zStatus } from "./common";

/**
 * Reception eligibility (Phase 2). MIN-NECESSARY: the result carries a coverage verdict + benefit band ONLY.
 * There is no diagnosis, no clinical history, no free-text notes field — Reception's zone forbids it, and the
 * schema makes that structural: there is simply nowhere to put a clinical value.
 */
export const zEligibilityQuery = z.object({
  /** Card number, national id, or name fragment — the reception search box. */
  query: z.string().min(2),
});
export type EligibilityQuery = z.infer<typeof zEligibilityQuery>;

/** The demographics reception is allowed to see to confirm identity at the desk. */
export const zBeneficiaryIdentity = z.object({
  id: zId,
  name: zLocalized,
  cardNumber: z.string(),
  // DOB + gender are optional: the reception min-necessary card omits them by design (they are not needed to
  // confirm coverage at the desk). Full-demographic zones supply them; reception renders them only if present.
  dateOfBirth: zDate.optional(),
  gender: z.enum(["female", "male", "unspecified"]).optional(),
});
export type BeneficiaryIdentity = z.infer<typeof zBeneficiaryIdentity>;

export const zCoverage = z.object({
  planName: zLocalized,
  band: zLocalized,
  // validUntil + copayPercent are optional: the reception card summarises active benefit categories + remaining
  // limits, and does not always carry a policy end-date or copay schedule. Screens render them only when present.
  validUntil: zDate.optional(),
  /** Beneficiary copay as a percentage (0–100). */
  copayPercent: z.number().min(0).max(100).optional(),
  // 18.D2 (U7): raw number; formatted as EGP in the active locale at render.
  annualCapRemaining: z.number().optional(),
});
export type Coverage = z.infer<typeof zCoverage>;

/** Visit-gating outcome (Phase 2.3): may the beneficiary be admitted to a visit today, and if not, why. */
export const zVisitGate = z.object({
  allowed: z.boolean(),
  reason: zLocalized.optional(),
});
export type VisitGate = z.infer<typeof zVisitGate>;

export const zEligibilityResult = z.object({
  verdict: z.enum(["eligible", "ineligible", "review"]),
  status: zStatus,
  beneficiary: zBeneficiaryIdentity,
  coverage: zCoverage.nullable(),
  visitGate: zVisitGate,
});
export type EligibilityResult = z.infer<typeof zEligibilityResult>;

/** A thin search hit before a full check (the type-ahead list). */
export const zEligibilityHit = z.object({
  id: zId,
  name: zLocalized,
  cardNumber: z.string(),
});
export type EligibilityHit = z.infer<typeof zEligibilityHit>;

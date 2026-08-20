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
  // validUntil is optional: the reception card summarises active benefit categories + remaining limits, and
  // does not always carry a policy end-date. Screens render it only when present.
  validUntil: zDate.optional(),
  // 18.D2 (U7): raw number; formatted as EGP in the active locale at render.
  annualCapRemaining: z.number().optional(),
});
export type Coverage = z.infer<typeof zCoverage>;

/**
 * 32.6 — what the member pays, or why nobody can say.
 *
 * <p>A discriminated union rather than an optional number, for the reason this codebase keeps rediscovering:
 * an absent copay and a copay of zero look identical in a nullable field and mean opposite things at a desk
 * with a beneficiary in front of it. `known: false` carries the sentence to show instead — "no category was
 * named", "the tier could not be resolved", "the plan's cost share could not be read" — and the screen has
 * nowhere to put a blank.</p>
 *
 * <p>The reasons are `Localized` because they are UI text. The service sends English prose; it is mapped to
 * a typed pair here rather than passed through, per ADR-0042 — an Arabic-reading receptionist must not be
 * shown an English sentence about money.</p>
 */
export const zCostShare = z.discriminatedUnion("known", [
  z.object({
    known: z.literal(true),
    /** The network tier the quote was resolved at — "why this number" for the person reading it out. */
    tierCode: z.string().nullable(),
    copayPercent: z.number().min(0).max(100).nullable(),
    copayFixed: z.number().nullable(),
    coinsurancePercent: z.number().min(0).max(100).nullable(),
  }),
  z.object({ known: z.literal(false), why: zLocalized }),
]);
export type CostShare = z.infer<typeof zCostShare>;

/** Visit-gating outcome (Phase 2.3): may the beneficiary be admitted to a visit today, and if not, why. */
export const zVisitGate = z.object({
  allowed: z.boolean(),
  reason: zLocalized.optional(),
});
export type VisitGate = z.infer<typeof zVisitGate>;

export const zEligibilityResult = z.object({
  verdict: z.enum(["eligible", "ineligible", "review"]),
  /**
   * 32.6 — WHAT the verdict is about.
   *
   * <p>`membership` means the desk asked without naming a benefit category: the person is an active member
   * in good standing, and nothing here says whether any particular service is covered. `benefit` means a
   * category was named and the verdict is about cover for it.</p>
   *
   * <p>Not optional, and not inferable from the other fields. The two answers render the same word —
   * "Eligible" — and a screen that cannot tell them apart will eventually tell a beneficiary they are
   * covered for care nobody checked.</p>
   */
  scope: z.enum(["benefit", "membership"]),
  /** The category the verdict is about. Null at membership scope. */
  benefitCategory: z.string().nullable(),
  status: zStatus,
  beneficiary: zBeneficiaryIdentity,
  coverage: zCoverage.nullable(),
  costShare: zCostShare,
  visitGate: zVisitGate,
});
export type EligibilityResult = z.infer<typeof zEligibilityResult>;

/** A thin search hit before a full check (the type-ahead list). */
export const zEligibilityHit = z.object({
  id: zId,
  name: zLocalized,
  cardNumber: z.string(),
  /**
   * The member's lifecycle status, resolved to a chip.
   *
   * The reception card has always carried `identity.status`; the client used to drop it on the floor here, so
   * a search result gave no hint that the person was suspended and the desk found out only when the booking
   * was refused — or, before the server gate existed, not at all. A booking cannot proceed for a non-Active
   * member, and the place to say so is the moment they are found.
   *
   * Optional because a fixture or an older service may not supply it; absent means "not stated", which the
   * UI must not paint as "fine".
   */
  status: zStatus.optional(),
  /** True only when the server said Active. Absent status ⇒ false — default-deny in the display layer too. */
  bookable: z.boolean().optional(),
});
export type EligibilityHit = z.infer<typeof zEligibilityHit>;

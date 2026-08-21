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
  /**
   * What the beneficiary is holding — the number printed on the card where it is known, and the member
   * number otherwise.
   *
   * These are two different identifiers (33.9b): `memberNo` is the enrolment key policy-service issues,
   * `cardNumber` is what patient-service normalizes and prints. This field is the one a desk compares
   * against the object in front of them, so the card wins where the projection has it.
   */
  cardNumber: z.string(),
  /** The enrolment key, when it differs from the card. Rendered beside it, never instead of it. */
  memberNo: z.string().optional(),
  // DOB + gender are optional: the reception min-necessary card omits them by design (they are not needed to
  // confirm coverage at the desk). Full-demographic zones supply them; reception renders them only if present.
  dateOfBirth: zDate.optional(),
  gender: z.enum(["female", "male", "unspecified"]).optional(),
});
export type BeneficiaryIdentity = z.infer<typeof zBeneficiaryIdentity>;

/**
 * One benefit limit and what is left of it (33.9b).
 *
 * The reception card has always carried the FULL list — a `{category, limitType, remaining}` row per limit
 * per active coverage — and the client picked the first monetary one for the headline and discarded the
 * rest. So a member with a visit count on CONSULT, a cap on PHARMACY and an amount on LAB was summarised as
 * one number, and the desk could not answer "how many consultations do they have left?" from the screen the
 * question belongs on.
 */
export const zCoverageLimit = z.object({
  category: z.string(),
  /** `Amount`, `Count`, `Days`… — the service's own vocabulary, rendered as-is rather than guessed at. */
  limitType: z.string(),
  remaining: z.number(),
});
export type CoverageLimit = z.infer<typeof zCoverageLimit>;

export const zCoverage = z.object({
  /**
   * Nullable, and null is the normal case.
   *
   * The client used to send the literal `"Benefit coverage"` here, so every card printed a plan name that
   * was not a plan name — the reception projection carries no plan, and inventing a label for the field is
   * worse than leaving it out, because a reader cannot tell a placeholder from a real plan. The row is
   * rendered only when this is genuinely known.
   */
  planName: zLocalized.nullable(),
  band: zLocalized,
  /** The policy the active coverage sits under. Real data the reception card held and dropped. */
  policyNo: z.string().optional(),
  // validUntil is optional: the reception card summarises active benefit categories + remaining limits, and
  // does not always carry a policy end-date. Screens render it only when present.
  validUntil: zDate.optional(),
  // 18.D2 (U7): raw number; formatted as EGP in the active locale at render.
  annualCapRemaining: z.number().optional(),
  /**
   * Every limit on every active coverage — see zCoverageLimit. Empty when the card carries none.
   *
   * Required, not defaulted: a producer that forgets it should fail validation rather than quietly render a
   * member as having no limits, which reads at a desk as "nothing left to spend" being unknowable.
   */
  limits: z.array(zCoverageLimit),
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

/**
 * A reception search — the page of hits, and whether there were more (33.9).
 *
 * `searchEligibility` used to return a bare array, so the one thing a caller could not learn was that the
 * list had been cut. The server takes 25 rows; a term matching forty people produced twenty-five, and the
 * operator picked a patient from a truncated set presented as the complete one — with the person they wanted
 * possibly not on it and nothing on screen to suggest looking further.
 *
 * There is deliberately no total. "More than 25" is what an operator acts on; the action is always to narrow
 * the term, and a full count would cost a second query per search to say the same thing.
 */
export const zEligibilitySearch = z.object({
  hits: z.array(zEligibilityHit),
  truncated: z.boolean(),
});
export type EligibilitySearch = z.infer<typeof zEligibilitySearch>;

/**
 * The answer to "does this identifier, corroborated by this name, resolve to one member?" (33.9)
 *
 * The eligibility screen used to run a free-text search and check the FIRST hit, so a partial name was
 * enough to open somebody's coverage — and which somebody depended on the database's ordering. A verified
 * lookup takes an identifier the beneficiary can present and part of the name, and the SERVICE decides
 * whether the two agree.
 *
 * Discriminated on `verified`, so a client reads one field to know which answer it has. The refusal
 * deliberately carries no identity at all — not the name on file, not the member number, not the membership
 * status — because an endpoint that said "that card belongs to someone else called X" would hand out the
 * name behind any card number to whoever holds one.
 *
 * `reason` is a machine code and never a sentence: the wording belongs to the screen, in both locales, and a
 * server-authored string cannot be translated.
 */
export const zVerificationRefusal = z.object({
  verified: z.literal(false),
  /**
   * - `not-found` — nothing on file matches that identifier.
   * - `name-mismatch` — the identifier resolves, and the name given does not agree with the record.
   * - `name-too-short` — the fragment offered narrows nothing; type more of the name.
   */
  reason: z.enum(["not-found", "name-mismatch", "name-too-short"]),
});

export const zVerifiedBeneficiary = z.object({
  verified: z.literal(true),
  hit: zEligibilityHit,
});

export const zBeneficiaryVerification = z.discriminatedUnion("verified", [
  zVerifiedBeneficiary,
  zVerificationRefusal,
]);
export type BeneficiaryVerification = z.infer<typeof zBeneficiaryVerification>;

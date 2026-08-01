import { StatusChip } from "@mersal/design-system";
import type { StatusKind } from "@mersal/design-system";
import type { Localized } from "../../portals/catalog";

/**
 * 25.7 (design 42 §3/§6) — licence status, with FOUR REDUNDANT CUES.
 *
 * <b>"A grey chip that means 'this doctor may not legally practise' is a design failure."</b> That is the
 * sentence this module exists to satisfy, and it is not only about colour blindness — it is about GLANCE.
 * A coordinator scanning twenty rows for the one they must act on today reads shape and icon before they read
 * text, and reads text before they read hue. So the three states differ on every axis at once:
 *
 * | state    | hue    | icon     | shape  | word                          |
 * |----------|--------|----------|--------|-------------------------------|
 * | Valid    | green  | tick     | pill   | "Valid"                       |
 * | Expiring | amber  | triangle | dashed | "Expires in 12 days"          |
 * | Expired  | red    | cross    | square | "EXPIRED — cannot be booked"  |
 *
 * `StatusChip` supplies hue+icon+shape from `kind`; the label supplies the word. The one kind never used here
 * is `neu`, which renders grey — it is the exact failure the design names, and `LicenceStatusFourCueTests`
 * asserts it never appears.
 *
 * The EXPIRED word says what it MEANS, not what it is. "Expired" alone is a fact about a date; "cannot be
 * booked" is the consequence the person reading needs, and it is the reason they will act today.
 */

export type LicenceState = "valid" | "expiring" | "expired" | "notRecorded";

/** The amber window, matching the sweeper's widest warning threshold so screen and email agree. */
export const EXPIRING_WITHIN_DAYS = 90;

/**
 * Derive the state. Prefers the SERVER's answer where it gives one — `licenceValid` is computed against the
 * date being asked about, and re-deriving it on the client from a date string is how a screen and a booking
 * gate end up disagreeing about the same doctor.
 */
export function licenceStateOf(input: {
  licenseExpiry: string | null | undefined;
  licenceValid?: boolean | null;
  daysUntilExpiry?: number | null;
}): LicenceState {
  const { licenseExpiry, licenceValid, daysUntilExpiry } = input;

  // "Not recorded" is NOT "expired", and collapsing the two would put a red chip against every nurse who
  // never had a licence number. It is its own state, with its own remedy.
  if (!licenseExpiry) return "notRecorded";

  const days = daysUntilExpiry ?? daysBetweenToday(licenseExpiry);
  if (licenceValid === false || days < 0) return "expired";
  return days <= EXPIRING_WITHIN_DAYS ? "expiring" : "valid";
}

const KIND: Record<LicenceState, StatusKind> = {
  valid: "ok",
  expiring: "warn",
  // `bad`, never `neu`. See the header: grey is the documented failure.
  expired: "bad",
  // `info`, not `neu` either — an unrecorded licence is an action for the coordinator, not a shrug.
  notRecorded: "info",
};

const WORD: Record<LicenceState, Localized> = {
  valid: { en: "Valid", ar: "ساري" },
  // The day count is appended by the caller — see `licenceLabel`.
  expiring: { en: "Expiring", ar: "قارب على الانتهاء" },
  expired: { en: "EXPIRED — cannot be booked", ar: "منتهٍ — لا يمكن الحجز" },
  notRecorded: { en: "No licence recorded", ar: "لا يوجد ترخيص مسجل" },
};

export function licenceLabel(state: LicenceState, days: number | null | undefined, lang: "en" | "ar"): string {
  if (state === "expiring" && typeof days === "number") {
    return lang === "ar" ? `ينتهي خلال ${days} يومًا` : `Expires in ${days} days`;
  }
  if (state === "expired" && typeof days === "number" && days < 0) {
    const ago = Math.abs(days);
    return lang === "ar" ? `منتهٍ منذ ${ago} يومًا — لا يمكن الحجز` : `EXPIRED ${ago} days ago — cannot be booked`;
  }
  return WORD[state][lang];
}

export interface LicenceStatusProps {
  licenseExpiry: string | null | undefined;
  licenceValid?: boolean | null;
  daysUntilExpiry?: number | null;
  lang: "en" | "ar";
}

export function LicenceStatus({ licenseExpiry, licenceValid, daysUntilExpiry, lang }: LicenceStatusProps) {
  const state = licenceStateOf({ licenseExpiry, licenceValid, daysUntilExpiry });
  const days = daysUntilExpiry ?? (licenseExpiry ? daysBetweenToday(licenseExpiry) : null);
  return <StatusChip kind={KIND[state]} label={licenceLabel(state, days, lang)} />;
}

function daysBetweenToday(isoDate: string): number {
  const expiry = new Date(`${isoDate}T00:00:00Z`).getTime();
  const today = new Date();
  const utcToday = Date.UTC(today.getUTCFullYear(), today.getUTCMonth(), today.getUTCDate());
  return Math.round((expiry - utcToday) / 86_400_000);
}

/** Exposed for the four-cue test, so the mapping is asserted rather than eyeballed. */
export const LICENCE_KINDS = KIND;

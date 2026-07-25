import { z } from "zod";

/**
 * Shared primitives for every HBMP contract. The status vocabulary here is the SAME six-kind, color-blind
 * safe system the design-system StatusChip renders (hue + icon + shape + text). Contracts speak `StatusKind`
 * so the UI never has to invent a mapping from a raw domain enum to a visual state at the call site.
 */
export const zStatusKind = z.enum(["ok", "info", "part", "warn", "bad", "neu"]);
export type StatusKind = z.infer<typeof zStatusKind>;

/** A bilingual label authored on both sides — never machine-translated at runtime. */
export const zLocalized = z.object({ en: z.string(), ar: z.string() });
export type Localized = z.infer<typeof zLocalized>;

/**
 * A status the API has already resolved to a visual kind + a bilingual label. Keeping the mapping server-side
 * keeps the four-cue guarantee consistent across screens and locales.
 */
export const zStatus = z.object({ kind: zStatusKind, label: zLocalized });
export type Status = z.infer<typeof zStatus>;

export const zId = z.string().min(1);
/** ISO-8601 instant. */
export const zInstant = z.string().datetime({ offset: true });
export const zDate = z.string().regex(/^\d{4}-\d{2}-\d{2}$/, "expected YYYY-MM-DD");

/**
 * A masked patient reference — the ONLY beneficiary identity a min-necessary screen may hold. It carries a
 * stable opaque id + a display token (e.g. initials + last-4 of the card), never a full name/DOB unless the
 * screen's zone permits it. Lab/Pharmacy/Approvals worklists use this, not a full demographic record.
 */
export const zPatientRef = z.object({
  id: zId,
  /** Short display token, e.g. "A.M · •••4821". Safe to render in any clinical-fulfillment zone. */
  token: z.string().min(1),
});
export type PatientRef = z.infer<typeof zPatientRef>;

/** A coded clinical concept (ICD-10 / CPT / LOINC / ATC). `system` disambiguates the code space. */
export const zCoded = z.object({
  system: z.enum(["ICD-10", "CPT", "LOINC", "ATC", "RxNorm"]),
  code: z.string().min(1),
  label: zLocalized,
});
export type Coded = z.infer<typeof zCoded>;

/** SLA envelope shared by worklists — dueAt + a pre-computed breached flag (server owns the clock). */
export const zSla = z.object({
  dueAt: zInstant,
  breached: z.boolean(),
  /** Minutes remaining (negative once breached) — for the countdown chip. */
  minutesRemaining: z.number().int(),
});
export type Sla = z.infer<typeof zSla>;

export const zPriority = z.enum(["routine", "urgent", "emergency"]);
export type Priority = z.infer<typeof zPriority>;

/**
 * Envelope for a problem returned by a service (RFC-7807-ish, trimmed). Screens render `.title` and surface
 * `.detail` for the operator; `.code` drives any special handling (e.g. `idempotent.replay`).
 */
export const zProblem = z.object({
  code: z.string(),
  title: zLocalized,
  detail: z.string().optional(),
});
export type Problem = z.infer<typeof zProblem>;

/**
 * Typed mirror of the Mersal design tokens (tokens.css). These are the *names* of the CSS custom
 * properties plus the raw palette values, so TS code can reference tokens without stringly-typed vars.
 * Normative source: 0A §5 + 0B (incl. §10b v1.1). Brand hues are decorative-only by contract.
 */

export const radii = {
  sm: "var(--r-sm)",
  md: "var(--r-md)",
  lg: "var(--r-lg)",
  pill: "var(--r-pill)",
} as const;

export const space = {
  1: "var(--sp1)",
  2: "var(--sp2)",
  3: "var(--sp3)",
  4: "var(--sp4)",
  5: "var(--sp5)",
  6: "var(--sp6)",
  8: "var(--sp8)",
  10: "var(--sp10)",
  12: "var(--sp12)",
} as const;

export const fontSize = {
  display: "var(--fs-display)",
  title1: "var(--fs-title-1)",
  title2: "var(--fs-title-2)",
  title3: "var(--fs-title-3)",
  bodyLg: "var(--fs-body-lg)",
  body: "var(--fs-body)",
  subhead: "var(--fs-subhead)",
  caption: "var(--fs-caption)",
} as const;

/** Theme-aware surface/text/action tokens — resolve per data-theme. */
export const color = {
  surface0: "var(--surface-0)",
  surface1: "var(--surface-1)",
  surface2: "var(--surface-2)",
  text1: "var(--text-1)",
  text2: "var(--text-2)",
  text3: "var(--text-3)",
  accent: "var(--accent)",
  accentPress: "var(--accent-press)",
  accentTint: "var(--accent-tint)",
  border: "var(--border)",
  borderStrong: "var(--border-strong)",
  focus: "var(--focus)",
  /** Decorative-only — never text/controls carrying meaning. */
  brand: "var(--brand)",
  gold: "var(--gold)",
} as const;

/**
 * The canonical color-blind-safe status taxonomy (0A §5.2 / 0B §5). Every status renders as
 * hue + icon + shape + text — never color alone. `shape` is the grayscale-survivable tell.
 */
export type StatusKind = "ok" | "info" | "part" | "warn" | "bad" | "neu";

export interface StatusMeta {
  kind: StatusKind;
  /** background + foreground CSS var pair. */
  bg: string;
  fg: string;
  /** grayscale-survivable shape cue. */
  shape: "pill" | "pill-dashed" | "pill-half" | "pill-outline" | "square" | "pill-ghost";
  /** default English label (i18n overrides at render time). */
  label: string;
}

export const statusMeta: Record<StatusKind, StatusMeta> = {
  ok: { kind: "ok", bg: "var(--st-ok-bg)", fg: "var(--st-ok-fg)", shape: "pill", label: "Approved" },
  info: { kind: "info", bg: "var(--st-info-bg)", fg: "var(--st-info-fg)", shape: "pill-dashed", label: "Under review" },
  part: { kind: "part", bg: "var(--st-part-bg)", fg: "var(--st-part-fg)", shape: "pill-half", label: "Partial" },
  warn: { kind: "warn", bg: "transparent", fg: "var(--st-warn-fg)", shape: "pill-outline", label: "Emergency" },
  bad: { kind: "bad", bg: "var(--st-bad-bg)", fg: "var(--st-bad-fg)", shape: "square", label: "Rejected" },
  neu: { kind: "neu", bg: "transparent", fg: "var(--st-neu-fg)", shape: "pill-ghost", label: "Info requested" },
};

export const motion = {
  micro: "var(--dur-micro)",
  standard: "var(--dur)",
  overlay: "var(--dur-overlay)",
  ease: "var(--ease)",
} as const;

export type Theme = "light" | "dark";
export type Dir = "ltr" | "rtl";
export type Lang = "en" | "ar";

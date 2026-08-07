import type { CheckState, ClinicalSeverity, Localized } from "@mersal/contracts";
import { SEVERITY_RANK } from "@mersal/contracts";
import { useLoc } from "../_shared";

/**
 * The per-line check status — FIVE states, FOUR cues each (hue, icon, shape, word).
 *
 * <b>The distinction the design turns on.</b> `Ok`, `Warning` and `Blocked` are ANSWERS. `NotChecked` and
 * `Unavailable` are not: the first means there was no data to check against, the second that the source
 * failed. Doc 43 §8 invariant 2 — "unavailable is never rendered as OK" — is what this component exists to
 * enforce, and the old prescribing path violated it by rendering an outage as no alerts at all.
 *
 * So the two groups are rendered as distinct visual CLASSES, not as five peers: answered states get a solid
 * filled chip, unanswered ones a dashed border and a hollow glyph. A reader scanning a column sees "we have
 * no answer here" before parsing colour or reading text — which is what survives greyscale, colour-blindness
 * and haste. Colour alone is never the signal (WCAG 2.2 AA, doc 0B four-cue rule).
 */

const LABEL: Record<CheckState, Localized> = {
  Ok: { en: "OK", ar: "سليم" },
  Warning: { en: "Warning", ar: "تحذير" },
  Blocked: { en: "Blocked", ar: "محظور" },
  NotChecked: { en: "Not checked", ar: "لم يتم التحقق" },
  Unavailable: { en: "Check unavailable", ar: "تعذّر التحقق" },
};

/** The shape cue, carried by the glyph itself so it survives a monochrome rendering. */
const GLYPH: Record<CheckState, string> = {
  Ok: "●",          // filled circle
  Warning: "▲",     // filled triangle
  Blocked: "■",     // filled square
  NotChecked: "○",  // hollow circle — no answer
  Unavailable: "◌", // dotted circle — no answer, and the source failed
};

/** Answered vs unanswered. The class that does the real work. */
export function isAnswered(state: CheckState): boolean {
  return state === "Ok" || state === "Warning" || state === "Blocked";
}

export function LineStatusChip({
  state,
  id,
  onClick,
  detailLabel,
}: {
  state: CheckState;
  id?: string;
  /** When given, the chip becomes the button that opens the per-line checks. */
  onClick?: () => void;
  /** Accessible name for the button form, e.g. "Checks for Augmentin 1g". */
  detailLabel?: string;
}) {
  const t = useLoc();
  const modifier = state.toLowerCase();
  const group = isAnswered(state) ? "rx-status--answered" : "rx-status--unanswered";
  const className = `rx-status rx-status--${modifier} ${group}`;

  const inner = (
    <>
      {/* aria-hidden: the word beside it is the accessible name, so a screen reader is not told "▲ Warning". */}
      <span className="rx-status-glyph" aria-hidden="true">{GLYPH[state]}</span>
      <span className="rx-status-word">{t(LABEL[state])}</span>
    </>
  );

  // The chip IS the affordance. The five checks behind it are detail a prescriber wants on demand, not a
  // wall of text under every line — but the summary state stays on the row, because that is the cue the
  // whole design turns on and it must never be a click away.
  if (onClick) {
    return (
      <button
        id={id}
        type="button"
        className={`${className} rx-status--button`}
        data-state={state}
        aria-label={detailLabel}
        onClick={onClick}
      >
        {inner}
        <span className="rx-status-more" aria-hidden="true">›</span>
      </button>
    );
  }

  return (
    <span id={id} className={className} data-state={state}>
      {inner}
    </span>
  );
}

/**
 * How serious a finding is — a FIRST-CLASS chip, not a word buried in a sentence (28.4, doc 44 §2).
 *
 * <b>Why this is separate from the state chip.</b> They answer different questions. The state chip says
 * whether the check produced an ANSWER (`Ok`/`Warning`/`Blocked`) or not (`NotChecked`/`Unavailable`); this
 * one says how much the answer matters. A line can be `Warning` at `Minor` and `Warning` at
 * `Contraindicated`, and before phase 28 those rendered identically — the severity existed on the wire and
 * was interpolated into the message string, where it read as prose rather than as a cue.
 *
 * <b>Why that mattered.</b> Uniform alerting is the best-documented failure mode in clinical decision
 * support: when a contraindicated combination and a trivial one look the same and demand the same click,
 * clinicians learn to dismiss both. Override rates above 90% are routinely reported, and the alerts that
 * were worth stopping for go with the rest.
 *
 * Four cues, like every other status on this platform: hue, glyph, shape and the word itself. Colour is
 * never the signal on its own (WCAG 2.2 AA, doc 0B).
 */

const SEVERITY_LABEL: Record<ClinicalSeverity, Localized> = {
  Contraindicated: { en: "Contraindicated", ar: "مضاد استطباب" },
  Major: { en: "Major", ar: "شديد" },
  Moderate: { en: "Moderate", ar: "متوسط" },
  Minor: { en: "Minor", ar: "طفيف" },
};

/**
 * The shape cue, carried by the glyph so it survives a monochrome rendering — and deliberately DISTINCT
 * from the state glyphs above, so the two chips beside each other never read as one repeated cue.
 */
const SEVERITY_GLYPH: Record<ClinicalSeverity, string> = {
  Contraindicated: "✖",   // cross — do not co-prescribe
  Major: "⬆",             // up — act on this
  Moderate: "◆",          // diamond — be aware
  Minor: "▾",             // down — reference only
};

export function SeverityChip({ severity }: { severity: ClinicalSeverity }) {
  const t = useLoc();
  return (
    <span
      className={`rx-severity rx-severity--${severity.toLowerCase()}`}
      data-severity={severity}
    >
      {/* aria-hidden: the word beside it is the accessible name, so a screen reader is not told "✖ Contraindicated". */}
      <span className="rx-severity-glyph" aria-hidden="true">{SEVERITY_GLYPH[severity]}</span>
      <span className="rx-severity-word">{t(SEVERITY_LABEL[severity])}</span>
    </span>
  );
}

/**
 * The worst severity among a line's findings, or null when none of them carries one.
 *
 * Null does NOT mean harmless — a manufacturer-label interaction carries no grade because a label states an
 * effect rather than a rank, and it still interrupts. The chip is simply omitted, and the state chip and the
 * acknowledgement requirement carry the weight.
 */
export function worstSeverity(findings: { severity?: string | null }[]): ClinicalSeverity | null {
  let worst: ClinicalSeverity | null = null;
  for (const f of findings) {
    const s = f.severity as ClinicalSeverity | null | undefined;
    if (!s || !(s in SEVERITY_RANK)) continue;
    if (worst === null || SEVERITY_RANK[s] > SEVERITY_RANK[worst]) worst = s;
  }
  return worst;
}

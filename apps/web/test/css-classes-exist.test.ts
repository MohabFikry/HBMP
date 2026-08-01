import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, relative, resolve } from "node:path";

/**
 * Every `className` a screen writes must exist in a stylesheet.
 *
 * <b>Why this exists.</b> The Branch Management portal shipped against a vocabulary that was never written:
 * `.lede` on all four screens, `.tabs` on the inventory category switch, `.field` / `.field-label` /
 * `.field-inline` on its form rows. None of them existed in any CSS file, and no other screen in the app used
 * them — so the intro paragraphs, the form labels and the category tabs rendered with no styling at all,
 * beside components that were styled correctly. A class that does not exist fails SILENTLY: the markup is
 * valid, the build is clean, the tests pass, and the only symptom is a screen that looks unfinished.
 *
 * That is the same shape as the `var()` guard added in 18.D2 for undefined custom properties, and it is here
 * for the same reason: this codebase has now shipped the defect twice (`.stack-3` was "already being used
 * without ever being defined" too, by its own comment), which makes it a pattern rather than an accident.
 *
 * Scope: static string literals only. Template and conditional class names are not resolved — this is a
 * cheap net for the common case, not a CSS-in-JS analyser, and a guard that tried to be complete here would
 * either need a real evaluator or start reporting false positives, which is how a checker gets disabled.
 */

const WEB_SRC = resolve(__dirname, "../src");
const CSS_DIRS = [
  resolve(__dirname, "../src/styles"),
  resolve(__dirname, "../../design-system/src/styles"),
  resolve(__dirname, "../../design-system/src/tokens"),
];

/**
 * Classes used by screens that predate this guard and have no rule.
 *
 * NOT a suppression list to grow — every entry is markup pointing at styling that does not exist. The four
 * that mattered most have been fixed and removed: `restricted-card` / `chip` / `chip--restricted` (the
 * withheld-result card, now styled from tokens instead of duplicated inline) and `visually-hidden` (which
 * hid nothing, so a print-failure live region and a filter label both rendered VISIBLY — `sr-only` is the
 * class that exists).
 *
 * What remains is layout and chip styling on the patient profile, the executive dashboard, the beneficiary
 * portal and the booking time picker. Fix one, delete its line; do not add to it.
 */
const KNOWN_MISSING = new Set([
  "ben-checkbox-cell",
  "bk-time",
  "bk-times",
  "call-history",
  "call-row-duration",
  "call-row-when",
  "kpi-cell",
  "profile-chip--critical",
  "profile-chip--info",
  "profile-chip--neutral",
  "profile-section",
]);

function walk(dir: string, ext: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p, ext, out);
    else if (p.endsWith(ext)) out.push(p);
  }
  return out;
}

function definedClasses(): Set<string> {
  const css = CSS_DIRS.flatMap((d) => walk(d, ".css"))
    .map((p) => readFileSync(p, "utf8"))
    .join("\n");
  return new Set([...css.matchAll(/\.([a-zA-Z][\w-]*)/g)].map((m) => m[1]));
}

describe("every className used in a screen has a CSS rule", () => {
  const defined = definedClasses();
  const files = walk(WEB_SRC, ".tsx");

  it("reads a real stylesheet and a real set of screens", () => {
    // Without this, a broken path makes `defined` empty and every file below "fails" — or, worse, makes
    // `files` empty and the whole suite passes having checked nothing.
    expect(defined.size, "no classes parsed — the CSS paths are wrong").toBeGreaterThan(200);
    expect(files.length, "no screens found — the source path is wrong").toBeGreaterThan(50);
    expect(defined.has("mrs-card")).toBe(true);
    expect(defined.has("muted")).toBe(true);
  });

  it.each(files.map((f) => [relative(WEB_SRC, f), f] as const))("%s", (_label, file) => {
    const src = readFileSync(file, "utf8");
    const used = new Set<string>();
    for (const m of src.matchAll(/className="([^"{]+)"/g)) {
      for (const c of m[1].trim().split(/\s+/)) if (c) used.add(c);
    }
    const missing = [...used].filter((c) => !defined.has(c) && !KNOWN_MISSING.has(c)).sort();
    expect(
      missing,
      `these classes are used here but defined in no stylesheet, so they render as nothing: ${missing.join(", ")}`,
    ).toEqual([]);
  });

  it("the debt list is accurate — an entry that got fixed must be removed, not left to rot", () => {
    // A stale allow-list is how a guard quietly stops guarding: once entries no longer correspond to
    // anything, nobody trusts the list enough to shrink it.
    const stillMissing = [...KNOWN_MISSING].filter((c) => !defined.has(c));
    expect(
      [...KNOWN_MISSING].filter((c) => defined.has(c)),
      "these are in KNOWN_MISSING but now HAVE a rule — delete them from the list",
    ).toEqual([]);
    expect(stillMissing.length).toBe(KNOWN_MISSING.size);
  });
});

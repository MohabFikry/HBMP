import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * WCAG 2.2 AA contrast, asserted on the TOKENS — arithmetic, so it runs here in milliseconds rather than in
 * the browser job.
 *
 * <b>Why this exists.</b> `--text-3` shipped at `#6b7c82`, which is 4.35:1 on white and 3.99:1 on
 * `--surface-0` — below AA on every light surface in the system, and it is the colour every worklist table
 * header renders in. It went unnoticed for months for a reason worth recording: `--text-1` and `--text-2`
 * carried their measured ratios in a comment beside them and `--text-3` carried none. The two that had been
 * checked said so; the one that never had was silent, and silence read as fine.
 *
 * The browser job (`e2e/a11y-contrast.spec.ts`) is the authority — it measures COMPOSITED, painted colour,
 * including opacity, gradients and overlays that no arithmetic on hex pairs can see. But it needs Chromium
 * and a built bundle, so it runs once per push and not in the edit loop. This catches the flat-token case
 * immediately and cheaply; it does not replace that job.
 *
 * Deliberately checks every text × surface pair rather than only the pairs used today. A token is a promise
 * that a colour is safe wherever it lands, and "we don't currently put meta text on surface-0" is exactly
 * the kind of fact that stops being true in a screen nobody re-audits.
 */

const TOKENS = resolve(__dirname, "../../design-system/src/tokens/tokens.css");

function theme(selector: string): Record<string, string> {
  const src = readFileSync(TOKENS, "utf8");
  const block = new RegExp(`${selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}\\s*\\{([\\s\\S]*?)\\n\\}`).exec(src);
  if (!block) throw new Error(`token block not found: ${selector}`);
  const out: Record<string, string> = {};
  for (const [, name, hex] of block[1].matchAll(/--([a-z0-9-]+):\s*(#[0-9a-fA-F]{6})\s*;/g)) {
    out[name] = hex.toLowerCase();
  }
  return out;
}

/** WCAG 2.1 relative luminance. */
function luminance(hex: string): number {
  const ch = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * ch[0] + 0.7152 * ch[1] + 0.0722 * ch[2];
}

function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

const AA = 4.5; // normal-size text. Nothing here is large-text, so the 3:1 allowance never applies.

describe.each([
  ["light", 'html[data-theme="light"]'],
  ["dark", 'html[data-theme="dark"]'],
])("%s theme tokens meet WCAG AA", (_name, selector) => {
  const t = theme(selector);

  it("parses a full palette — an empty block would make every assertion below vacuous", () => {
    // The failure mode this guards is the one the rest of the file exists to catch, one level up: a regex
    // that stops matching turns a sweep into a green no-op.
    expect(Object.keys(t).length).toBeGreaterThan(20);
    for (const key of ["text-1", "text-2", "text-3", "surface-0", "surface-1", "surface-2", "accent", "on-accent"]) {
      expect(t[key], `--${key} missing from ${selector}`).toMatch(/^#[0-9a-f]{6}$/);
    }
  });

  const surfaces = ["surface-0", "surface-1", "surface-2"];
  const texts = ["text-1", "text-2", "text-3"];

  it.each(texts.flatMap((text) => surfaces.map((surface) => [text, surface] as const)))(
    "--%s on --%s",
    (text, surface) => {
      const ratio = contrast(t[text], t[surface]);
      expect(
        ratio,
        `--${text} (${t[text]}) on --${surface} (${t[surface]}) is ${ratio.toFixed(2)}:1, below AA's ${AA}:1`,
      ).toBeGreaterThanOrEqual(AA);
    },
  );

  it("--on-accent on --accent", () => {
    // The pairing that broke the avatar: --accent flips from a DARK teal in light theme to a LIGHT one in
    // dark, so a hardcoded #fff is correct in one and 1.78:1 in the other. --on-accent is re-derived per
    // theme precisely so nothing has to hardcode it — and .app-avatar hardcoded it anyway, on the line
    // below the one that used the token correctly.
    const ratio = contrast(t["on-accent"], t.accent);
    expect(ratio, `--on-accent on --accent is ${ratio.toFixed(2)}:1`).toBeGreaterThanOrEqual(AA);
  });

  it.each(["ok", "info", "part", "warn", "bad", "neu"])("--st-%s-fg on its own bg", (status) => {
    const fg = t[`st-${status}-fg`];
    const bg = t[`st-${status}-bg`];
    expect(fg, `--st-${status}-fg missing`).toBeDefined();
    expect(bg, `--st-${status}-bg missing`).toBeDefined();
    const ratio = contrast(fg, bg);
    expect(ratio, `--st-${status}-fg on --st-${status}-bg is ${ratio.toFixed(2)}:1`).toBeGreaterThanOrEqual(AA);
  });
});

describe("the checker itself", () => {
  // A contrast test that computes contrast wrongly is worse than none: it would bless whatever it was
  // given. Pinned against the two ends of the scale and one published reference value.
  it("agrees with the reference ratios", () => {
    expect(contrast("#000000", "#ffffff")).toBeCloseTo(21, 5);
    expect(contrast("#ffffff", "#ffffff")).toBeCloseTo(1, 5);
    expect(contrast("#767676", "#ffffff")).toBeCloseTo(4.54, 1); // the canonical AA-threshold grey
  });

  it("is symmetric — order of arguments cannot change a verdict", () => {
    expect(contrast("#6b7c82", "#ffffff")).toBeCloseTo(contrast("#ffffff", "#6b7c82"), 10);
  });

  it("would have caught the token that shipped below AA", () => {
    // The regression this file was written for, asserted rather than described.
    expect(contrast("#6b7c82", "#f7fbfb")).toBeLessThan(AA);
    expect(contrast("#ffffff", "#5fd3d3")).toBeLessThan(AA);
  });
});

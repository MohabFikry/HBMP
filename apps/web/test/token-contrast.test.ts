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

  /**
   * 2026-08-09 audit — the accent on its OWN TINT.
   *
   * `--accent` carries "5.2:1" beside it, and that is true against white. It is 4.44:1 on `--accent-tint`,
   * which is the background every hover and every selected row paints underneath it. The active nav item was
   * caught and fixed when the browser contrast job first ran; its hover twin, the ghost button and the icon
   * button were not, because that job samples 12 of 112 routes and never paints a hover state at all.
   *
   * Checked here rather than left to the browser for exactly that reason: a pairing that only exists under
   * the pointer is one no screenshot pass will ever composite.
   */
  it("--accent-press on --accent-tint (the hover/selected pairing)", () => {
    const ratio = contrast(t["accent-press"], t["accent-tint"]);
    expect(ratio, `--accent-press on --accent-tint is ${ratio.toFixed(2)}:1`).toBeGreaterThanOrEqual(AA);
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

/**
 * EVERY rule that paints text on `--accent-tint`, swept out of the stylesheet.
 *
 * The hand-listed pair above says the pairing the fix chose is safe. This says nobody has since added a
 * fourth hover rule with a different colour in it — which is precisely how the three came to exist: the
 * active nav item was fixed when the browser job first ran, and the hover twin two lines above it was not,
 * because that job samples 12 routes and never paints a hover state at all.
 *
 * Structural rather than a list, because a list of known-bad pairs is a list somebody has to remember to add
 * to. This reads the CSS.
 */
describe("the floating back-to-top button carries its own contrast", () => {
  /**
   * A control with nothing around it has no container to borrow structure from, and this one shipped as
   * `--surface-1` with a `--border` hairline: #ffffff behind a #d7e3e3 line, which is 1.19:1 against the page
   * and 1.00:1 against a white section card — invisible, exactly as reported. WCAG 1.4.11 wants 3:1 for the
   * boundary of a UI component against what is behind it, and a floating button is the case that rule is for.
   *
   * Pinned per theme, against every surface it can float over, so "make it subtle" cannot quietly return it
   * to a white circle on a white card.
   */
  const UI_COMPONENT = 3; // WCAG 1.4.11 non-text contrast

  describe.each([
    ["light", 'html[data-theme="light"]'],
    ["dark", 'html[data-theme="dark"]'],
  ])("%s theme", (_name, selector) => {
    const t = theme(selector);

    it.each(["surface-0", "surface-1", "surface-2"])("the accent fill stands off --%s", (surface) => {
      const ratio = contrast(t.accent, t[surface]);
      expect(
        ratio,
        `the button (--accent ${t.accent}) on --${surface} (${t[surface]}) is ${ratio.toFixed(2)}:1, ` +
          `below the ${UI_COMPONENT}:1 a UI component needs to be discernible`,
      ).toBeGreaterThanOrEqual(UI_COMPONENT);
    });

    it("the chevron is legible on the fill", () => {
      // --on-accent exists precisely because white fails on the dark theme's lightened teal (1.79:1).
      const ratio = contrast(t["on-accent"], t.accent);
      expect(
        ratio,
        `--on-accent (${t["on-accent"]}) on --accent (${t.accent}) is ${ratio.toFixed(2)}:1`,
      ).toBeGreaterThanOrEqual(AA);
    });

    it("the hover fill stays discernible too", () => {
      const ratio = contrast(t["accent-press"], t["surface-0"]);
      expect(ratio, `--accent-press on the page is ${ratio.toFixed(2)}:1`).toBeGreaterThanOrEqual(UI_COMPONENT);
    });
  });

  it("is actually painted in the accent, not a surface", () => {
    // The arithmetic above is about tokens; this is what ties it to the button. A rule that goes back to
    // `background: var(--surface-1)` would sail past every ratio here while being the original bug.
    const css = readFileSync(resolve(__dirname, "../src/styles/app.css"), "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
    const rule = /\.scrolltop\s*\{([^}]*)\}/.exec(css);
    expect(rule, ".scrolltop rule should exist").not.toBeNull();
    expect(rule![1], "the floating button is accent-filled").toMatch(/background:\s*var\(--accent\)/);
    expect(rule![1], "and its glyph uses the theme-derived on-accent").toMatch(/color:\s*var\(--on-accent\)/);
  });
});

describe("text painted on --accent-tint", () => {
  const CSS = resolve(__dirname, "../../design-system/src/styles/components.css");

  /** Selector → the colour token it sets, for every rule whose background is the accent tint. */
  function tintedRules(): Array<{ selector: string; color: string }> {
    const src = readFileSync(CSS, "utf8");
    const out: Array<{ selector: string; color: string }> = [];
    for (const [, selector, body] of src.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
      if (!/background:\s*var\(--accent-tint\)/.test(body)) continue;
      const color = /(?:^|[\s;])color:\s*var\(--([a-z0-9-]+)\)/.exec(body);
      // No colour of its own ⇒ it inherits, and the inherited body text is checked by the sweep above.
      if (color) out.push({ selector: selector.trim().split("\n").pop()!.trim(), color: color[1] });
    }
    return out;
  }

  it("finds the rules — an empty sweep would make the assertions below vacuous", () => {
    expect(tintedRules().length).toBeGreaterThanOrEqual(3);
  });

  it.each([
    ["light", 'html[data-theme="light"]'],
    ["dark", 'html[data-theme="dark"]'],
  ])("is AA in the %s theme", (_name, selector) => {
    const t = theme(selector);
    const failures = tintedRules()
      .map((r) => ({ ...r, ratio: contrast(t[r.color], t["accent-tint"]) }))
      .filter((r) => r.ratio < AA)
      .map((r) => `${r.selector} uses --${r.color} → ${r.ratio.toFixed(2)}:1`);

    expect(failures, `on --accent-tint (${t["accent-tint"]}):\n  ${failures.join("\n  ")}`).toEqual([]);
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

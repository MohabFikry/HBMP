import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * A nested-card guard must never flatten a KPI tile.
 *
 * <b>The defect.</b> Three screens carry a rule of the shape `.<screen> .mrs-card .mrs-card { padding: 0 }`,
 * so a panel nested inside a panel is not inset twice. A `KpiCard`/`KpiList` tile is also a `.mrs-card`, and
 * it is nested inside its panel BY DESIGN — so the guard matched it at (0,3,0), beat `.mrs-kpi`'s own
 * `padding: var(--sp5)` at (0,1,0), and stripped every tile to zero. The label then sat on the tile's top
 * border beneath the 3px accent hairline, reading as clipped, and the 34px figure ran to the bottom edge.
 *
 * The rule's own comment claimed "there is no such nesting on these screens today". That was true when it was
 * written and silently stopped being true, which is exactly the kind of statement worth pinning: the guard is
 * still wanted, so it cannot simply be deleted, and the exclusion is easy to drop when someone reformats the
 * selector.
 *
 * <b>Why static.</b> jsdom applies no cascade and computes no padding, so nothing in the existing suite can
 * see this. The invariant is therefore stated against the stylesheet, the same way the tab-bar and
 * token-contrast checks state theirs.
 */

const APP_CSS = resolve(__dirname, "../src/styles/app.css");
const COMPONENTS_CSS = resolve(__dirname, "../../design-system/src/styles/components.css");

/** Selectors of every rule that zeroes padding on a card inside a card. */
function nestedCardFlatteners(css: string): string[] {
  const src = css.replace(/\/\*[\s\S]*?\*\//g, "");
  const out: string[] = [];
  for (const m of src.matchAll(/([^{}]+)\{([^}]*)\}/g)) {
    const [, selector, body] = m;
    if (!/\.mrs-card\s+[^,{]*\.mrs-card/.test(selector)) continue;
    if (!/padding:\s*0\b/.test(body)) continue;
    out.push(selector.trim().replace(/\s+/g, " "));
  }
  return out;
}

describe("KPI tiles keep their own padding", () => {
  const app = readFileSync(APP_CSS, "utf8");

  it("finds the guards it is about — otherwise this file asserts nothing", () => {
    // The rules are real and there are several; a regex that stopped matching would make the check vacuous.
    expect(nestedCardFlatteners(app).length).toBeGreaterThanOrEqual(2);
  });

  it("excludes .mrs-kpi from every card-inside-a-card padding reset", () => {
    const offenders = nestedCardFlatteners(app).filter((s) =>
      // Each comma-separated part that targets a nested card must carry the exclusion.
      s.split(",").some((part) => /\.mrs-card\s+[^,]*\.mrs-card/.test(part) && !part.includes(":not(.mrs-kpi)")),
    );
    expect(
      offenders,
      "a KPI tile is a .mrs-card nested by design and carries its own spacing — flattening it strips the " +
        "tile's padding rather than preventing a double inset. Add :not(.mrs-kpi) to the nested-card selector",
    ).toEqual([]);
  });

  it("and the tile still declares padding of its own to be stripped", () => {
    // The other half: the exclusion is pointless if the component rule ever loses its padding.
    const components = readFileSync(COMPONENTS_CSS, "utf8").replace(/\/\*[\s\S]*?\*\//g, "");
    const rule = /\.mrs-kpi\s*\{([^}]*)\}/.exec(components);
    expect(rule, ".mrs-kpi rule should exist").not.toBeNull();
    expect(rule![1]).toMatch(/padding:\s*var\(--sp\d\)/);
  });
});

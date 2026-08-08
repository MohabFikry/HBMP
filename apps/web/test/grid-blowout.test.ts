import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * Single-column grids may not grow wider than the column they sit in.
 *
 * ============================================================================================================
 * WHY THIS EXISTS
 * ============================================================================================================
 * A grid's default `auto` track sizes to at least the MIN-CONTENT of its items, and a track is permitted to
 * exceed its container to honour that. So a `.stack` holding anything with a stubborn intrinsic width — a
 * nine-column table, a fieldset, a long unbroken string — grows wider than its container, and everything in it
 * paints over whatever comes next in the layout.
 *
 * That is exactly what put the encounter's Labs and OP-Procedures tables underneath the vitals rail. The rail
 * was where it belonged; the card beside it was ~170px too wide, and the table's PINNED actions column —
 * `z-index: 1` — punched straight through the rail so the two sets of text stacked on each other. The
 * Prescriptions tab was fine, because its table has seven columns rather than nine. A layout bug that depends
 * on the column count is one that reappears the next time a column is added.
 *
 * ============================================================================================================
 * WHY A STATIC CHECK
 * ============================================================================================================
 * jsdom performs no layout: every box measures zero, nothing ever overflows, and no rendering assertion in
 * this suite can observe the fault. The same blind spot the scroll-design guard was written for. So the rule
 * is asserted against the stylesheet text, which is where the fix lives.
 */

const APP_CSS = resolve(__dirname, "../src/styles/app.css");
const DS_CSS = resolve(__dirname, "../../design-system/src/styles/components.css");

/** Strip comments, so prose ABOUT a value is never mistaken for the value itself. */
const decls = (css: string) => css.replace(/\/\*[\s\S]*?\*\//g, "");

/** The body of one rule, by selector. Null when the selector is not declared. */
function ruleBody(css: string, selector: string): string | null {
  const escaped = selector.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
  const m = new RegExp(`(^|[},])\\s*${escaped}\\s*\\{([^}]*)\\}`, "m").exec(decls(css));
  return m ? m[2] : null;
}

describe("a single-column grid cannot blow out its container", () => {
  // The generic wrappers, plus the encounter-specific ones that hold the tables and the composers. Each is a
  // grid whose ONE column has no business being wider than the box it is in.
  it.each([
    ".stack",
    ".stack-3",
    ".enc-soap",
    ".rx-lines",
    ".rx-line",
  ])("%s clamps its track with minmax(0, ...)", (selector) => {
    const body = ruleBody(readFileSync(APP_CSS, "utf8"), selector);

    expect(body, `${selector} is not declared in app.css`).not.toBeNull();
    expect(body, `${selector} is a grid with an implicit \`auto\` track, so its min-content can push it `
      + "wider than its container").toMatch(/grid-template-columns:\s*minmax\(\s*0/);
  });

  it("keeps the encounter's note column able to shrink", () => {
    // `min-inline-size: 0` on the grid ITEM is the other half: without it the item's automatic minimum is its
    // content, and the clamped track above has nothing to clamp against.
    const css = readFileSync(APP_CSS, "utf8");

    expect(ruleBody(css, ".enc-main")).toMatch(/min-inline-size:\s*0/);
    expect(ruleBody(css, ".enc-main .mrs-tabpane")).toMatch(/min-inline-size:\s*0/);
  });
});

describe("the vitals rail is never painted through", () => {
  it("stacks above a worklist's pinned actions column", () => {
    /*
     * Two positioned elements with `z-index: auto` paint in DOM order, and the rail comes after the note
     * column — so it wins by default. A PINNED table cell does not play by that rule: it carries an explicit
     * `z-index: 1`, which beats `auto` outright. The moment a table beside the rail overflowed, its ACTIONS
     * header rendered on top of a patient's readings.
     *
     * The overflow is fixed above. This is the guard that keeps the next one from landing on the rail.
     */
    const railZ = ruleBody(readFileSync(APP_CSS, "utf8"), ".enc-rail");
    const stickyEnd = decls(readFileSync(DS_CSS, "utf8"))
      .match(/\.mrs-wl th\.mrs-stickyend[\s\S]*?\{([^}]*)\}/);

    const railValue = Number(/z-index:\s*(\d+)/.exec(railZ ?? "")?.[1] ?? NaN);
    const cellValue = Number(/z-index:\s*(\d+)/.exec(stickyEnd?.[1] ?? "")?.[1] ?? NaN);

    expect(railValue, ".enc-rail declares no z-index, so a pinned table cell will paint over it")
      .toBeGreaterThan(cellValue);
  });
});

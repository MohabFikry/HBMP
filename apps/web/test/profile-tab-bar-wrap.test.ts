import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve as resolvePath } from "node:path";

/**
 * The profile tab bar has to survive being narrow.
 *
 * <b>Why this exists.</b> The pill tab bar was built and reviewed on a desktop viewport, where its seven
 * tabs fit on one row and everything about it is correct. Measured in a real browser, the row wraps below
 * 688px in English and below 780px in Arabic — and at 360px it becomes three rows, 156px tall. Two defects
 * only exist in that wrapped state:
 *
 * 1. `--r-pill` is 999px. On a 62px one-row track that draws the intended stadium; on a 156px three-row
 *    track the radius clamps to half the height and the whole bar renders as a lozenge with the first and
 *    last rows cut into by the curve. It looks broken, not styled.
 * 2. The bar is `position: sticky`, and `.profile-section`'s `scroll-margin-block-start` was sized against
 *    the one-row height. Wrapped, the bar is taller than the margin that compensates for it, so tabbing to
 *    a control below the fold parks it underneath the bar — WCAG 2.4.11 (Focus Not Obscured), the exact
 *    thing that margin was added to satisfy. A three-row sticky bar also holds 24% of a 360x640 viewport.
 *
 * <b>Why a static check.</b> jsdom performs no layout, so the 1129-test suite passes over both defects
 * without a flicker — there is no row count and no rendered radius for an assertion to read. The invariant
 * is therefore stated against the stylesheet itself, the same way `css-classes-exist` states its own: a
 * capsule radius and sticky positioning are both correct ONLY where the bar is known to be one row, so
 * both must be confined to the width query that knows it.
 */

const APP_CSS = resolvePath(__dirname, "../src/styles/app.css");
const COMPONENTS_CSS = resolvePath(__dirname, "../../design-system/src/styles/components.css");

interface Rule {
  selector: string;
  body: string;
  /** The enclosing `@media` prelude, or null when the rule applies at every width. */
  media: string | null;
}

/** Comments are stripped first: several rules here carry long prose comments, and prose is not CSS. */
function rules(css: string): Rule[] {
  const src = css.replace(/\/\*[\s\S]*?\*\//g, "");
  const out: Rule[] = [];
  const atRules: string[] = [];
  let prelude = "";

  for (let i = 0; i < src.length; i++) {
    const c = src[i];
    if (c === "{") {
      const head = prelude.trim();
      prelude = "";
      if (head.startsWith("@")) {
        atRules.push(head);
        continue;
      }
      let depth = 1;
      let j = i + 1;
      while (j < src.length && depth > 0) {
        if (src[j] === "{") depth++;
        else if (src[j] === "}") depth--;
        j++;
      }
      const media = atRules.filter((a) => a.startsWith("@media")).join(" and ") || null;
      out.push({ selector: head, body: src.slice(i + 1, j - 1), media });
      i = j - 1;
    } else if (c === "}") {
      atRules.pop();
      prelude = "";
    } else {
      prelude += c;
    }
  }
  return out;
}

const CAPSULE = /border-radius:\s*var\(--r-pill\)/;
const STICKY = /position:\s*sticky/;
/** A header rule counts if it pins itself or declares a layer — both are how one ends up over the bar. */
const STICKY_OR_Z = /position:\s*sticky|z-index:/;
/** A width query, not merely any query — `prefers-reduced-motion` tells you nothing about the row count. */
const WIDTH_QUERY = /min-width:/;

describe("the profile pill tab bar wraps safely", () => {
  const components = rules(readFileSync(COMPONENTS_CSS, "utf8"));
  const app = rules(readFileSync(APP_CSS, "utf8"));

  it("does not give the wrap-capable track a capsule radius at every width", () => {
    // The track sets `flex-wrap: wrap`, so its height is not knowable from the stylesheet. A radius that is
    // only correct at one specific height must not be the unconditional default.
    // The TRACK itself, not its children: `.mrs-tabs--pill .mrs-tab` keeps `--r-pill` and should, because a
    // pill is a fixed 44px tall and a capsule is exactly what it wants. The container is the one whose
    // height is unknown.
    const track = components.filter((r) => r.selector.trim() === ".mrs-tabs--pill");
    expect(track.length, "the pill track rule should exist").toBeGreaterThan(0);

    const unconditionalCapsule = track.filter((r) => r.media === null && CAPSULE.test(r.body));
    expect(
      unconditionalCapsule.map((r) => r.selector),
      "a wrap-capable pill track must not carry --r-pill outside a width query",
    ).toEqual([]);
  });

  it("only sticks the tab bar at widths where it is one row", () => {
    const sticky = app.filter((r) => r.selector.includes(".profile-tabs") && STICKY.test(r.body));
    expect(sticky.length, "the tab bar should still be sticky somewhere").toBeGreaterThan(0);

    const stickyEverywhere = sticky.filter((r) => r.media === null || !WIDTH_QUERY.test(r.media));
    expect(
      stickyEverywhere.map((r) => r.selector),
      "sticky must be confined to a min-width query — a wrapped bar obscures the focus it scrolls to",
    ).toEqual([]);
  });

  it("stacks above the sticky table headers inside its own panels, and below every popup", () => {
    // The bug this pins: the bar shipped at `z-index: 1`, while the worklist tables INSIDE the profile's
    // section cards pin their own `thead th` at 5 (and `.mrs-stickyend` at 6). Same stacking context, higher
    // number — so scrolling a section with a table slid its header straight over the tab bar. A sticky bar
    // that other components paint through is not isolated from them, whatever its offset.
    //
    // The ceiling matters just as much. Popup layers (select and combobox lists at 40, and the overlays
    // above them) MUST stay over the bar: a dropdown opened in a section card and covered by the tab bar
    // would be a worse bug than the one being fixed. So this asserts a slot, not a floor.
    const zOf = (r: Rule): number | null => {
      const m = /z-index:\s*(-?\d+)/.exec(r.body);
      return m ? Number(m[1]) : null;
    };

    const bar = app
      .filter((r) => r.selector.includes(".profile-tabs") && STICKY.test(r.body))
      .map(zOf)
      .filter((z): z is number => z !== null);
    expect(bar.length, "the sticky tab bar should declare a z-index").toBeGreaterThan(0);

    // Read the neighbours rather than hard-coding 6 and 40, so this keeps holding if either layer moves.
    const stickyHeaders = components
      .filter((r) => /\.mrs-wl\b/.test(r.selector) && /\bth\b/.test(r.selector) && STICKY_OR_Z.test(r.body))
      .map(zOf)
      .filter((z): z is number => z !== null);
    const popups = components
      .filter((r) => /\.mrs-(select|combo)-list/.test(r.selector))
      .map(zOf)
      .filter((z): z is number => z !== null);

    expect(stickyHeaders.length, "in-card sticky headers should be findable").toBeGreaterThan(0);
    expect(popups.length, "popup lists should be findable").toBeGreaterThan(0);

    const floor = Math.max(...stickyHeaders);
    const ceiling = Math.min(...popups);
    for (const z of bar) {
      expect(z, `tab bar z-index must clear the in-card sticky headers (${floor})`).toBeGreaterThan(floor);
      expect(z, `tab bar z-index must stay under the popup layer (${ceiling})`).toBeLessThan(ceiling);
    }
  });

  it("only restores the capsule radius at those same widths", () => {
    const capsule = app.filter((r) => r.selector.includes(".profile-tabs") && CAPSULE.test(r.body));
    const unguarded = capsule.filter((r) => r.media === null || !WIDTH_QUERY.test(r.media));
    expect(
      unguarded.map((r) => r.selector),
      "the stadium look belongs to the one-row bar only",
    ).toEqual([]);
  });
});

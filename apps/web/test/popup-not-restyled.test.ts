import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * A list's own row rules must not reach inside a popup that opens within one of its rows.
 *
 * ============================================================================================================
 * READ THIS FIRST — THE RUNTIME EXPOSURE IS GONE, AND THE RULE IS KEPT ANYWAY
 * ============================================================================================================
 * `Combobox` now PORTALS its option list to `<body>` (see `Popup.tsx`), so a popup is no longer a DOM
 * descendant of the row it was opened from and cannot inherit that row's layout. The bug described below can
 * no longer happen through that component.
 *
 * This guard stays, for two reasons, and neither is sentiment:
 *
 * <ul>
 *   <li>The portalling was done to escape CLIPPING by scrolling ancestors, not to fix inheritance — that was
 *       a side effect. A future change that anchors a popup differently, or a third control that renders a
 *       listbox inline, brings the exposure straight back, and it would come back silently.</li>
 *   <li>What it actually asserts is narrower and still true: a rule written for one list's rows should be
 *       scoped to them. That is worth holding whether or not a popup is the thing it currently leaks onto.</li>
 * </ul>
 *
 * So treat a failure here as "an unscoped row rule was written", not as "a dropdown is broken".
 *
 * ============================================================================================================
 * WHY IT WAS WRITTEN
 * ============================================================================================================
 * The rank picker on the "Add a diagnosis" modal opened a list of two options and BOTH RENDERED BLANK. The
 * cause was one space:
 *
 *     .dx-staged-list li { display: grid; grid-template-columns: auto minmax(0, 1fr) 9rem auto; }
 *
 * The picker rendered its own listbox — `<ul class="…-list"><li class="…-option">` — and that popup was a
 * DESCENDANT of the staged row it belonged to. So every option inherited the row's four-column
 * grid, the check glyph took track one, the label landed in `minmax(0, 1fr)` — which resolved to 0px once the
 * fixed 9rem track was placed — and the option's text was laid out into a zero-width box. Measured in a real
 * browser: `li` 200px wide, label 0px wide, scrollWidth 54px. Nothing was missing from the data and nothing
 * was hidden; the words were simply given no room.
 *
 * That failure is invisible to every other kind of test. The markup is correct, the options carry their
 * labels, the component's own unit tests pass, and jsdom performs no layout so nothing measures zero there
 * either. What CAN be checked without a layout engine is the thing that actually went wrong: whether a rule
 * written for a list's rows MATCHES an element that is not one of its rows.
 *
 * ============================================================================================================
 * THE RULE
 * ============================================================================================================
 * A row rule is scoped with `>` so it styles the list's own children and stops there. This is generic over
 * app.css rather than a fix pinned to `.dx-staged-list`, because the trap is the default: a descendant
 * combinator is what you get by not thinking about it, and every screen that puts a picker in a list row
 * walks into the same hole.
 */

const APP_CSS = resolve(__dirname, "../src/styles/app.css");

/** Strip comments so prose about a selector is never read as one. */
const decls = (css: string) => css.replace(/\/\*[\s\S]*?\*\//g, "");

/** Every selector in the sheet, one per comma-separated part, with its declarations. */
function rules(css: string): { selector: string; body: string }[] {
  const out: { selector: string; body: string }[] = [];
  for (const m of decls(css).matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const head = m[1].trim();
    // Skip at-rule preludes (@media, @supports); their nested rules are matched on the next pass.
    if (head.startsWith("@") || head === "") continue;
    for (const selector of head.split(",")) {
      const s = selector.trim();
      if (s) out.push({ selector: s, body: m[2] });
    }
  }
  return out;
}

/**
 * The markup a design-system popup renders, planted inside `container`.
 *
 * `Combobox` renders `.mrs-combo-list > .mrs-combo-option`, and it can be opened from inside a row of any
 * list on any screen. There were two flavours of this markup; the select-only one went with the control it
 * belonged to, and the shape is identical either way.
 */
function popupInside(container: string): { el: Element; li: Element[] } {
  const host = document.createElement("div");
  host.innerHTML = `
    <ul class="${container}">
      <li>
        <span>row content</span>
        <div class="mrs-combo">
          <input class="mrs-combo-input" />
          <ul class="mrs-combo-list mrs-scroll" role="listbox">
            <li class="mrs-combo-option" role="option"><span>A result</span></li>
          </ul>
        </div>
      </li>
    </ul>`;
  document.body.append(host);
  return { el: host, li: [...host.querySelectorAll(".mrs-combo-option")] };
}

/**
 * Every list container app.css writes row rules for, taken from the sheet rather than from a list kept by
 * hand — a hand-kept list is one the next screen forgets to join.
 *
 * BOTH forms are collected, `.foo li` and the correct `.foo > li`. Collecting only the broken form would
 * make this guard delete itself the moment it passed: with nothing left to enumerate it would report green
 * over an empty set forever, and the next `.foo li` would be the only thing it ever caught.
 */
function containersWithRowRules(css: string): string[] {
  const found = new Set<string>();
  for (const { selector } of rules(css)) {
    const m = /^\.([\w-]+)\s+>?\s*li$/.exec(selector);
    if (m) found.add(m[1]);
  }
  return [...found].sort();
}

describe("no list rule reaches into a popup opened inside one of its rows", () => {
  const css = readFileSync(APP_CSS, "utf8");
  const all = rules(css);

  it("has lists to check (a guard over an empty set is a guard that passed by finding nothing)", () => {
    expect(containersWithRowRules(css).length).toBeGreaterThan(5);
  });

  it.each(containersWithRowRules(css))(
    "a listbox inside .%s keeps its own layout",
    (container) => {
      const { el, li } = popupInside(container);
      try {
        const leaked = all.filter(({ selector }) => {
          // Only rules belonging to THIS list. A rule that names a popup class on purpose is not a leak.
          if (!selector.includes(`.${container}`)) return false;
          try {
            return li.some((o) => o.matches(selector));
          } catch {
            return false; // a selector jsdom cannot parse (`:has()`) is not evidence of a leak
          }
        });
        expect(
          leaked.map((r) => r.selector),
          `these rules are written for the rows of .${container} but also match the option elements of a `
            + "listbox opened inside one — scope them with `>`",
        ).toEqual([]);
      } finally {
        el.remove();
      }
    },
  );
});

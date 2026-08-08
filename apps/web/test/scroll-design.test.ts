import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync } from "node:fs";
import { join, relative, resolve } from "node:path";

/**
 * How the app scrolls, held to one design.
 *
 * <b>Why this exists.</b> Every region that scrolls inside a page had grown its own answer to the same three
 * questions, and nothing connected them. Ten different expressions capped ten scroll regions — `288px`,
 * `320px`, `16rem`, `20rem`, `22rem`, `30vh`, `88vh`, `min(60vh, 32rem)`, `min(70vh, 640px)`,
 * `calc(100dvh - 84px)` — so the two dropdowns that do exactly the same job disagreed by 64px, and anyone
 * adding an eleventh had nothing to copy. Not one of them contained its overscroll, so reaching the bottom of
 * an inner list handed the momentum to the page behind it. And none styled a scrollbar, which meant the same
 * component was a chunky grey slab on Windows and an invisible overlay on macOS — on macOS, a list that
 * scrolls looked exactly like a list that does not.
 *
 * <b>Why the a11y suite never caught the keyboard half of it.</b> axe's `scrollable-region-focusable` rule
 * only fires when an element ACTUALLY overflows, and jsdom performs no layout — every box measures zero, so
 * nothing ever overflows and the rule can never trigger. The route-wide axe pass is real coverage for a great
 * deal, and structurally blind to exactly this. Hence a static check.
 */

const WEB_SRC = resolve(__dirname, "../src");
const APP_CSS = resolve(__dirname, "../src/styles/app.css");
const DS_CSS = resolve(__dirname, "../../design-system/src/styles/components.css");
const TOKENS = resolve(__dirname, "../../design-system/src/tokens/tokens.css");

function walk(dir: string, ext: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, name.name);
    if (name.isDirectory()) walk(p, ext, out);
    else if (p.endsWith(ext)) out.push(p);
  }
  return out;
}

/** Strip comments, so prose ABOUT a discarded value is never mistaken for the value itself. */
const decls = (css: string) => css.replace(/\/\*[\s\S]*?\*\//g, "");

describe("the scroll scale", () => {
  it("defines the three roles a scroll region can have, and only three", () => {
    const t = readFileSync(TOKENS, "utf8");
    for (const token of ["--scroll-picker", "--scroll-panel", "--scroll-sheet"]) {
      expect(t, `${token} must be a token, not a number repeated per screen`).toContain(`${token}:`);
    }
  });

  it("caps every scroll region from the scale rather than from a fresh number", () => {
    // A `max-block-size`/`max-height` sitting next to an `overflow` is a scroll region declaring how tall it
    // gets. It must name a token. The exemptions below are surfaces measured against the VIEWPORT rather
    // than against the content scale — a docked pane and a full-bleed shell are sized by the window, and
    // forcing them onto a content scale would be consistency for its own sake.
    const VIEWPORT_SIZED = [
      /max-block-size: calc\(100dvh/,          // the notification pane, docked under the header
      /max-block-size: min\(60dvh, 32rem\)/,   // the bulk-edit workspace — see its own note
      /max-block-size: 30dvh/,                 // the mobile tab bar
      /max-height: 88dvh/,                     // the modal shell
    ];

    const offenders: string[] = [];
    for (const file of [APP_CSS, DS_CSS]) {
      for (const m of decls(readFileSync(file, "utf8"))
        .matchAll(/(max-block-size|max-height):\s*([^;]+);/g)) {
        const value = m[0];
        if (!/var\(--scroll-/.test(value) && VIEWPORT_SIZED.every((re) => !re.test(value))) {
          // Only flag it when it is capping something that actually scrolls: plenty of boxes have a
          // max-height for layout reasons and never overflow.
          offenders.push(`${relative(process.cwd(), file)}: ${value.trim()}`);
        }
      }
    }

    // Reported whole rather than one-at-a-time: the point is that the SET stays closed.
    expect(offenders, "cap a scroll region with --scroll-picker / --scroll-panel / --scroll-sheet").toEqual([]);
  });

  it("never measures a scroll cap in bare vh", () => {
    // On mobile Safari a `vh` box is taller than the visible page while the URL bar is showing, so the last
    // rows of a `vh`-capped region sit behind the toolbar with no way to scroll to them. `vh` is allowed only
    // as the fallback declaration immediately before its `dvh` twin.
    for (const file of [APP_CSS, DS_CSS, TOKENS]) {
      const lines = decls(readFileSync(file, "utf8")).split("\n");
      lines.forEach((line, i) => {
        if (!/\d+vh/.test(line) || /\ddvh/.test(line)) return;
        const prop = line.trim().split(":")[0];
        const next = lines[i + 1] ?? "";
        expect(
          next.includes(prop) && /\ddvh/.test(next),
          `${relative(process.cwd(), file)}:${i + 1} — "${line.trim()}" needs a dvh twin on the next line`,
        ).toBe(true);
      });
    }
  });
});

describe("the shared scroll treatment", () => {
  it("contains its overscroll and styles its own scrollbar", () => {
    const ds = decls(readFileSync(DS_CSS, "utf8"));
    const rule = ds.slice(ds.indexOf(".mrs-scroll {"));

    // Scroll chaining: reaching the bottom of a dropdown used to carry on and scroll the document behind it,
    // which moves the field the dropdown is anchored to out from under the cursor.
    expect(rule).toMatch(/overscroll-behavior:\s*contain/);

    // Declared twice on purpose — the standard properties (Firefox, Chrome 121+) and the WebKit
    // pseudo-elements (Safari, older Chromium). Neither alone covers the browsers this runs on.
    expect(rule).toMatch(/scrollbar-width:\s*thin/);
    expect(rule).toMatch(/scrollbar-color:/);
    expect(ds).toMatch(/\.mrs-scroll::-webkit-scrollbar-thumb/);
  });

  it("lets a TABLE hand its vertical scroll back to the page", () => {
    // The one place `contain` is wrong. A dropdown floats OVER the page, so chaining past its end moves the
    // field it is anchored to out from under the cursor — hence containment. A table is not an overlay: it is
    // a block in the page, usually the tallest thing on it, and the wheel lands on it over most of the
    // screen's area. `contain` blocks the chain even when the box has no vertical overflow to consume, which
    // is the common case for a table sized by its content — so the page stopped scrolling wherever the cursor
    // happened to be over a table.
    //
    // Inline stays contained: horizontal overscroll is what browsers map to back/forward navigation gestures,
    // and chaining that would let a sideways flick inside a claims table leave the page entirely.
    const ds = decls(readFileSync(DS_CSS, "utf8"));
    const table = ds.slice(ds.indexOf(".mrs-wl-scroll {"), ds.indexOf(".mrs-wl {"));
    expect(table).toMatch(/overscroll-behavior-block:\s*auto/);
    expect(table).toMatch(/overscroll-behavior-inline:\s*contain/);

    // And the hand-rolled table wrappers in the app follow the same rule rather than keeping their own.
    const app = decls(readFileSync(APP_CSS, "utf8"));
    const wrap = app.slice(app.indexOf(".pol-tablewrap {"));
    expect(wrap.slice(0, 300)).toMatch(/overscroll-behavior-block:\s*auto/);
  });

  it("leaves the page's own scroller and the rail on the platform scrollbar", () => {
    // A thin bar on the surface a user scrolls MOST is a real ergonomic loss. The rail still gets
    // containment, because scrolling past the last nav item should not move the content pane.
    const app = decls(readFileSync(APP_CSS, "utf8"));
    const rail = app.slice(app.indexOf("nav.mrs-rail {"), app.indexOf(".app-main {"));
    expect(rail).toMatch(/overscroll-behavior:\s*contain/);
    expect(rail).not.toMatch(/scrollbar-width/);
  });
});

describe("keyboard reach", () => {
  it("makes every scroll region that holds no controls a tab stop", () => {
    // A container that scrolls but holds nothing focusable cannot be scrolled without a pointer — arrow keys
    // go to the page instead (WCAG 2.1.1). `.mrs-scroll-focusable` draws the ring; it does NOT create the tab
    // stop, so the class without `tabIndex` is a focus ring that can never appear.
    const offenders: string[] = [];
    for (const file of walk(WEB_SRC, ".tsx").concat(walk(resolve(__dirname, "../../design-system/src"), ".tsx"))) {
      readFileSync(file, "utf8").split("\n").forEach((line, i) => {
        if (line.includes("mrs-scroll-focusable") && !line.includes("tabIndex")) {
          offenders.push(`${relative(process.cwd(), file)}:${i + 1}`);
        }
      });
    }
    expect(offenders, "mrs-scroll-focusable without tabIndex={0} is a ring nothing can focus").toEqual([]);
  });

  it("does not put a tab stop inside a listbox the arrow keys already drive", () => {
    // A dropdown is reached through the control that owns it — arrow keys from the input move the active
    // option and scroll it into view. An extra tab stop in the middle of that is a stop with nothing to do,
    // and it lands between the field and the next field.
    for (const file of walk(WEB_SRC, ".tsx").concat(walk(resolve(__dirname, "../../design-system/src"), ".tsx"))) {
      for (const line of readFileSync(file, "utf8").split("\n")) {
        if (line.includes('role="listbox"')) {
          expect(line, `${relative(process.cwd(), file)} — a listbox is not a tab stop`)
            .not.toContain("mrs-scroll-focusable");
        }
      }
    }
  });
});

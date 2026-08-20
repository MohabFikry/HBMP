import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";

/**
 * A region that scrolls inside the page wears the house scrollbar, or is on the exclusion list by name.
 *
 * ============================================================================================================
 * WHAT GOES WRONG WITHOUT IT
 * ============================================================================================================
 * Nothing styled a scrollbar anywhere in this product until `.mrs-scroll` was written, and the two desktop
 * platforms fail in opposite directions. On Windows and most Linux desktops the default is a ~17px light-grey
 * slab with square end caps — inside a 288px dropdown that is 6% of the width spent on furniture matching
 * nothing else on the page. On macOS it is an overlay bar that is INVISIBLE until you scroll, so a list that
 * scrolls looks exactly like a list that does not, and the only way to discover the remaining options is to
 * try. `.mrs-scroll` answers both, and it also contains scroll chaining, which is what stops the page sliding
 * out from under a dropdown when the wheel reaches the end of its list.
 *
 * The scrolls/dropdowns audit found 22 of 27 in-page regions carrying it. This guard is about the other five:
 * they were not decisions, they were the rule not being applied, and nothing said so.
 *
 * ============================================================================================================
 * WHY THE EXCLUSIONS ARE A LIST AND NOT AN ABSENCE
 * ============================================================================================================
 * Two regions keep the platform scrollbar ON PURPOSE — the nav rail and the page's own scroller — because a
 * thin bar on the primary scroller of a long worklist is a real ergonomic loss. That is a decision, and a
 * decision recorded as "we did not do it here" is indistinguishable from an oversight. Naming them means
 * removing one is a visible edit to this file with a reason attached.
 */

const CSS_FILES = [
  resolve(__dirname, "../src/styles/app.css"),
  resolve(__dirname, "../../design-system/src/styles/components.css"),
];
const TSX_ROOTS = [resolve(__dirname, "../src"), resolve(__dirname, "../../design-system/src")];

/**
 * Selectors that scroll and deliberately do NOT carry the house treatment.
 *
 * Removing an entry is a claim that the region should now be styled; ADDING one is a claim that a scrolling
 * region should keep the OS scrollbar, and it needs the same kind of reason these two have.
 */
const EXCLUDED: Record<string, string> = {
  "nav.mrs-rail":
    "the navigation rail is a primary scroller; a thin bar on it is an ergonomic loss, not a gain",
  ".app-main":
    "the page's own scroller — the OS default is right for the surface the user scrolls most",
};

/** Blank comments, keeping newlines, so prose about `overflow` is never read as a declaration. */
const blank = (m: string) => m.replace(/[^\n]/g, " ");

/** Every rule that establishes a scrollport, with the selector it belongs to. */
function scrollSelectors(path: string): { line: number; selector: string }[] {
  const src = readFileSync(path, "utf8").replace(/\/\*[\s\S]*?\*\//g, blank);
  const out: { line: number; selector: string }[] = [];
  const stack: string[] = [];
  let buf = "";
  src.split("\n").forEach((ln, i) => {
    for (const c of ln) {
      if (c === "{") { stack.push(buf.trim()); buf = ""; }
      else if (c === "}") { stack.pop(); buf = ""; }
      else buf += c;
    }
    if (/overflow[-a-z]*:\s*(auto|scroll|overlay)/.test(ln)) {
      // The innermost non-at-rule selector owns the declaration; `@media` preludes are skipped so a
      // responsive override is attributed to the element rather than to the breakpoint.
      const own = [...stack].reverse().find((s) => s && !s.startsWith("@")) ?? "";
      out.push({ line: i + 1, selector: own });
    }
  });
  return out;
}

function tsxFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) tsxFiles(p, out);
    else if (p.endsWith(".tsx")) out.push(p);
  }
  return out;
}

/** Every class named in a `className` anywhere in either package, paired with the classes beside it. */
function classSets(): string[][] {
  const out: string[][] = [];
  for (const root of TSX_ROOTS) {
    for (const f of tsxFiles(root)) {
      const src = readFileSync(f, "utf8");
      for (const m of src.matchAll(/className=(?:"([^"]*)"|\{`([^`]*)`\}|\{cx\(([^)]*)\)\})/g)) {
        const raw = m[1] ?? m[2] ?? m[3] ?? "";
        out.push(raw.split(/[\s"'`,]+/).filter(Boolean));
      }
    }
  }
  return out;
}

describe("every in-page scroll region wears the house scrollbar", () => {
  const declared = CSS_FILES.flatMap((f) =>
    scrollSelectors(f).map((r) => ({ ...r, file: f.split("/").slice(-1)[0] })));

  it("finds the scroll regions — otherwise every assertion here is over an empty set", () => {
    expect(declared.length).toBeGreaterThan(15);
    expect(declared.filter((r) => r.selector in EXCLUDED).length).toBeGreaterThan(0);
  });

  /**
   * Class-level, not element-level: a stylesheet cannot say which JSX element carries the class, so the check
   * is "every class that scrolls is used together with `.mrs-scroll` somewhere". That is weaker than checking
   * each call site and it is what catches the real failure — a class written with `overflow: auto` and never
   * paired with the treatment at all, which is exactly what all five of the audit's findings were.
   */
  it("pairs every scrolling class with .mrs-scroll at its call sites", () => {
    const sets = classSets();
    const offenders = declared
      .map((r) => r.selector)
      // Only leaf class selectors — a descendant or attribute selector is styling something the class-based
      // check cannot resolve, and `.mrs-popup` is the treatment's own surface.
      .filter((sel) => /^\.[\w-]+$/.test(sel))
      .filter((sel) => !(sel in EXCLUDED))
      .map((sel) => sel.slice(1))
      .filter((cls) => cls !== "mrs-popup" && cls !== "mrs-scroll")
      .filter((cls) => {
        const uses = sets.filter((s) => s.includes(cls));
        // A class with no call site is styled-but-unused; that is a different problem, not this one.
        return uses.length > 0 && !uses.some((s) => s.includes("mrs-scroll"));
      });
    expect(
      [...new Set(offenders)],
      "these classes establish a scrollport but are never rendered with `mrs-scroll`, so they draw the "
        + "platform scrollbar — a grey slab on Windows, and on macOS a bar that is invisible until you "
        + "scroll, which makes a scrollable list indistinguishable from a complete one",
    ).toEqual([]);
  });

  /**
   * A tab stop must show where focus is.
   *
   * A scrolling pane holding nothing focusable cannot be scrolled without a pointer — arrow keys go to the
   * page instead (WCAG 2.1.1) — so it is given `tabIndex={0}`. A SILENT tab stop is worse than none, because
   * the user cannot tell whether their arrow keys will move this region or the page. `.mrs-scroll-focusable`
   * is the ring, and the two belong together.
   *
   * Keyed on `tabIndex`, deliberately. The first version of this asked "does a class that looks like a pane
   * carry the ring?" and flagged eight regions that are reachable the OTHER way — through focusable children
   * (a list of buttons, a notification pane) or through the control that owns them (a listbox driven from its
   * own input). Those need no tab stop of their own and would be made worse by one: `.mrs-scroll`'s own
   * header says so. What is actually checkable is the pair.
   */
  it("gives every scroll pane that is a tab stop a visible focus ring", () => {
    const offenders: string[] = [];
    for (const root of TSX_ROOTS) {
      for (const f of tsxFiles(root)) {
        const src = readFileSync(f, "utf8");
        for (const m of src.matchAll(/<[A-Za-z][^>]*?mrs-scroll\b[^>]*?>/gs)) {
          const el = m[0];
          if (!/\btabIndex\b/.test(el)) continue;
          if (/mrs-scroll-focusable/.test(el)) continue;
          offenders.push(`${f.slice(root.length + 1)}:${src.slice(0, m.index).split("\n").length}`);
        }
      }
    }
    expect(
      offenders,
      "this pane scrolls and is reached by Tab, but shows nothing when focus lands on it — add "
        + "`mrs-scroll-focusable`",
    ).toEqual([]);
  });

  it("has tab-stopped panes to check — the assertion above must not pass over an empty set", () => {
    let stops = 0;
    for (const root of TSX_ROOTS) {
      for (const f of tsxFiles(root)) {
        for (const m of readFileSync(f, "utf8").matchAll(/<[A-Za-z][^>]*?mrs-scroll\b[^>]*?>/gs)) {
          if (/\btabIndex\b/.test(m[0])) stops++;
        }
      }
    }
    expect(stops).toBeGreaterThan(10);
  });
});

/**
 * The popup is portalled, and it has to STAY portalled.
 *
 * An ancestor that scrolls clips its descendants whatever their z-index. `.mrs-modal` is `overflow: auto`, so
 * before `Popup.tsx` a picker opened near the bottom of a dialog had its list cut off at the dialog's edge —
 * measured at 267px of an eight-option list, seven of eight options reachable only by scrolling the dialog,
 * which moves the control out from under the cursor. Twelve pickers were sitting in modals when it was found.
 *
 * `position: fixed` is NOT a fix and that is the part worth guarding: `transform`, `filter`, `will-change`,
 * `contain: paint` and `backdrop-filter` each make an ancestor into a containing block for fixed descendants,
 * and `.mrs-modal` carries `backdrop-filter` for the glass surface.
 */
describe("the popup cannot be clipped by a scrolling ancestor", () => {
  const POPUP = resolve(__dirname, "../../design-system/src/components/Popup.tsx");
  const COMBOBOX = resolve(__dirname, "../../design-system/src/components/Combobox.tsx");

  it("renders the option list through a portal", () => {
    const src = readFileSync(COMBOBOX, "utf8");
    expect(src, "the list must be portalled out of the control").toMatch(/<PopupPortal>/);
    expect(readFileSync(POPUP, "utf8")).toMatch(/createPortal\(children, document\.body\)/);
  });

  it("still re-anchors on scroll in the capture phase", () => {
    // Without capture, a scroll inside `.mrs-modal` never reaches window and the popup stays where the
    // control used to be — which is worse than clipping, because it looks deliberate.
    expect(readFileSync(POPUP, "utf8")).toMatch(/addEventListener\("scroll", measure, true\)/);
  });

  it("keeps the modal's `overflow: auto`, so the guard above is guarding something", () => {
    const css = readFileSync(resolve(__dirname, "../../design-system/src/styles/components.css"), "utf8");
    const modal = /\.mrs-modal \{([^}]*)\}/.exec(css.replace(/\/\*[\s\S]*?\*\//g, blank));
    expect(modal, "`.mrs-modal` should still exist").not.toBeNull();
    expect(modal![1]).toMatch(/overflow:\s*auto/);
  });
});

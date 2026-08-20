import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";

/**
 * No native `<select>` ships. Anywhere.
 *
 * ============================================================================================================
 * WHY THIS IS NOT A STYLING PREFERENCE
 * ============================================================================================================
 * A native select draws its option list in the OPERATING SYSTEM, not in the page. Three consequences, none of
 * which any amount of CSS can reach:
 *
 * <ul>
 *   <li>It ignores `data-theme`. On the dark theme every one of these opened a light popup under a dark card
 *       — the failure is invisible to anyone testing in light mode, which is most people.</li>
 *   <li>It ignores the app's RTL treatment and its focus ring.</li>
 *   <li>It cannot be searched. That is the product decision this guard is downstream of: every dropdown in
 *       Mersal is a searchable combobox, and a native select is permanently the wrong control.</li>
 * </ul>
 *
 * The codebase made the first argument three separate times, in three separate comments, each written when a
 * different screen was converted — and 15 selects were still shipping when the scrolls/dropdowns audit ran.
 * An argument in a comment only reaches whoever reads that file.
 *
 * ============================================================================================================
 * WHY IT COVERS THE DESIGN SYSTEM TOO
 * ============================================================================================================
 * The audit scanned `apps/web` and counted 15. The sixteenth was `Pagination`'s page-size picker, living in
 * the design system and therefore outside the scan — which, on a product where nearly every screen is a
 * paginated table, made it quite possibly the native select an operator met most often. It was found by
 * chasing a stale comment, not by the audit. So this guard reads both packages.
 *
 * There is NO exception list, including for dev-only harnesses. An exception nobody can see the reason for is
 * one the next screen copies.
 */

const ROOTS = [resolve(__dirname, "../src"), resolve(__dirname, "../../design-system/src")];

function sourceFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) sourceFiles(p, out);
    else if (p.endsWith(".tsx")) out.push(p);
  }
  return out;
}

/**
 * Blank out comments, preserving newlines so line numbers survive.
 *
 * Load-bearing: every remaining mention of `<select>` in the SPA is prose EXPLAINING why the control was
 * replaced. Matching those would make this guard fail on its own documentation, and the obvious fix — delete
 * the explanations — is the wrong one.
 */
function code(src: string): string {
  const blank = (m: string) => m.replace(/[^\n]/g, " ");
  return src
    .replace(/\/\*[\s\S]*?\*\//g, blank)     // block comments, including JSX {/* … */}
    .replace(/\/\/[^\n]*/g, blank);          // line comments
}

interface Hit { file: string; line: number }

function nativeSelects(): Hit[] {
  const out: Hit[] = [];
  for (const root of ROOTS) {
    for (const file of sourceFiles(root)) {
      const src = code(readFileSync(file, "utf8"));
      for (const m of src.matchAll(/<select[\s>]/g)) {
        out.push({ file: file.slice(root.length + 1), line: src.slice(0, m.index).split("\n").length });
      }
    }
  }
  return out;
}

describe("every dropdown in the product is a searchable combobox", () => {
  it("reads the sources — otherwise this passes by finding nothing", () => {
    const files = ROOTS.flatMap((r) => sourceFiles(r));
    expect(files.length).toBeGreaterThan(100);
    // A sanity check on the comment-blanking: the SPA still CONTAINS the string `<select>`, in prose. If this
    // ever reaches zero, the explanations have been deleted and the guard below is asserting over a corpus
    // that no longer mentions the thing it is about.
    const mentions = files.filter((f) => readFileSync(f, "utf8").includes("<select"));
    expect(mentions.length).toBeGreaterThan(0);
  });

  it("ships no native <select>", () => {
    expect(
      nativeSelects().map((h) => `${h.file}:${h.line}`),
      "a native <select> draws its option list in the OS: it ignores data-theme, ignores RTL, and cannot be "
        + "searched. Use `ComboboxField` (labelled) or `Combobox` (a toolbar filter naming itself with "
        + "aria-label).",
    ).toEqual([]);
  });

  /**
   * The other half of the rule. Deleting the searchable control and hand-rolling a listbox would pass the
   * assertion above while reintroducing everything it exists to prevent, so the replacement has to still be
   * there and still be reached for.
   */
  it("keeps the searchable control in use across the product", () => {
    const web = sourceFiles(ROOTS[0]).map((f) => code(readFileSync(f, "utf8"))).join("\n");
    const pickers = [...web.matchAll(/<Combobox(Field)?[\s>]/g)].length;
    expect(pickers, "the pickers have gone somewhere other than Combobox").toBeGreaterThan(30);
  });
});

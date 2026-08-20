import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { cleanup, waitFor } from "@testing-library/react";
import { renderApp } from "./helpers";
import { PORTALS } from "../src/portals/catalog";
import type { Role } from "../src/authz/permissions";

/**
 * 2026-08-09 audit — heading levels do not skip.
 *
 * ============================================================================================================
 * WHY THE axe SWEEP NEXT DOOR DOES NOT CATCH THIS
 * ============================================================================================================
 * `heading-order` is a MODERATE rule, and `a11y-routes.test.tsx` filters to serious/critical — the right
 * threshold for a blocking gate, and the reason a whole class of structural defect has been sitting under a
 * green light. Screen-reader users navigate by heading level; an h1 followed by an h3 tells them a section
 * they cannot see has been skipped, and on an admin screen made entirely of panels that is most of the page.
 *
 * So this is its own check with its own threshold, over the same route catalog. It is not a duplicate of the
 * axe sweep; it is the part of it that was deliberately excluded.
 *
 * ============================================================================================================
 * WHAT IS AND IS NOT A SKIP
 * ============================================================================================================
 * Going DOWN by more than one is the defect (h1 → h3). Coming back UP any distance is fine: an h4 followed by
 * an h2 closes two subsections and opens a peer, which is exactly what document structure is for.
 *
 * Headings inside an open dialog are excluded. A modal is its own document for navigation purposes — it takes
 * focus, `aria-modal` hides everything behind it, and its title legitimately restarts the scale. Including
 * them would report the dialog's own h3 against the page h1 underneath, which is not a thing any user
 * experiences.
 */

const ROUTES: Array<{ role: Role; path: string }> = PORTALS.flatMap((p) =>
  p.sections.map((s) => ({ role: p.role as Role, path: `/${p.base}/${s.path}` })),
);

/** The heading levels a page presents, in document order, ignoring anything inside a dialog. */
function levels(container: HTMLElement): Array<{ level: number; text: string }> {
  return [...container.querySelectorAll<HTMLElement>("h1, h2, h3, h4, h5, h6")]
    .filter((h) => !h.closest('[role="dialog"], dialog, [aria-modal="true"]'))
    // `aria-hidden` content is not announced, so it is not part of the structure a screen reader walks.
    .filter((h) => !h.closest('[aria-hidden="true"]'))
    .map((h) => ({ level: Number(h.tagName[1]), text: (h.textContent ?? "").trim().slice(0, 60) }));
}

/** Every downward jump of more than one level, described. */
function skips(found: Array<{ level: number; text: string }>): string[] {
  const out: string[] = [];
  for (let i = 1; i < found.length; i++) {
    const prev = found[i - 1];
    const here = found[i];
    if (here.level > prev.level + 1) {
      out.push(`h${prev.level} "${prev.text}" → h${here.level} "${here.text}"`);
    }
  }
  return out;
}

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

describe("heading order across the portal catalog", () => {
  it("covers the whole catalog, not a sample", () => {
    expect(ROUTES.length).toBeGreaterThan(30);
  });

  it("never skips a level going down", async () => {
    const failures: string[] = [];

    for (const { role, path } of ROUTES) {
      localStorage.clear();
      const { container, unmount } = renderApp(path, role);

      // Same reasoning as the axe sweep: a route still resolving its lazy chunk has no headings yet, and
      // "no headings" passes every assertion below. Wait for content, then require it.
      await waitFor(() =>
        expect(container.querySelector("main")?.textContent?.trim().length ?? 0).toBeGreaterThan(0),
      );

      const found = levels(container);
      if (found.length === 0) {
        failures.push(`${path}: rendered no headings at all — a page with no structure to navigate`);
      }
      for (const skip of skips(found)) failures.push(`${path}: ${skip}`);
      unmount();
    }

    expect(failures, `heading levels skip on these routes:\n  ${failures.join("\n  ")}`).toEqual([]);
  }, 120_000);
});

describe("the checker itself", () => {
  const at = (...ls: number[]) => ls.map((level) => ({ level, text: `h${level}` }));

  it("flags a downward skip", () => {
    expect(skips(at(1, 3))).toHaveLength(1);
    expect(skips(at(1, 2, 4))).toHaveLength(1);
  });

  it("allows a descent by one, and a rise by any amount", () => {
    expect(skips(at(1, 2, 3, 4))).toEqual([]);
    expect(skips(at(1, 2, 3, 2))).toEqual([]);
    expect(skips(at(1, 2, 4, 2, 3))).toHaveLength(1);   // only the 2→4
    expect(skips(at(1, 4, 1))).toHaveLength(1);
  });

  it("says WHICH pair skipped, so a failure is actionable without re-running", () => {
    const [message] = skips([{ level: 1, text: "Policy" }, { level: 3, text: "Tiers" }]);
    expect(message).toContain('h1 "Policy" → h3 "Tiers"');
  });
});

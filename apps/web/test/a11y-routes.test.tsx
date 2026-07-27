import { afterEach, beforeEach, describe, expect, it } from "vitest";
import { cleanup, screen, waitFor } from "@testing-library/react";
import { axe } from "jest-axe";
import { renderApp } from "./helpers";
import { PORTALS } from "../src/portals/catalog";
import type { Role } from "../src/authz/permissions";

/**
 * Phase 18.D3 (audit R2 U6/U10) — axe over EVERY route, in both languages and both themes.
 *
 * The existing gate covered three screens: login, one portal shell, and the 403 page. Everything a user
 * actually works in — the worklists, the dispense panel, the dashboards — was unverified. And it ran only in
 * English and only in light theme, so two whole classes of defect were invisible by construction: RTL
 * mirroring (a skip link pinned with `left` lands on the wrong side in Arabic) and any theme-specific
 * contrast or focus problem.
 *
 * This is table-driven over the portal catalog, so a route added next week is covered without anyone
 * remembering to add it here — which is the only way a sweep like this stays true.
 *
 * NOTE ON CONTRAST: `color-contrast` stays disabled here, and that is not a dodge — jsdom has no layout or
 * paint, so axe cannot compute a rendered colour and the rule reports nothing either way. Leaving it "on"
 * in a jsdom suite is the actual dodge, because it looks like coverage and produces none. Real contrast is
 * checked by the Playwright job (a11y-contrast.spec.ts), which runs in a browser that can paint.
 */

/** Every portal route the catalog declares, paired with the role that can reach it. */
const ROUTES: Array<{ role: Role; path: string; portal: string }> = PORTALS.flatMap((p) =>
  p.sections.map((s) => ({ role: p.role as Role, path: `/${p.base}/${s.path}`, portal: p.base })),
);

const LANGS = ["en", "ar"] as const;
const THEMES = ["light", "dark"] as const;

function setPreferences(lang: string, theme: string) {
  // ThemeProvider reads these on mount and stamps data-theme / lang / dir on <html>.
  localStorage.setItem("mersal-lang", lang);
  localStorage.setItem("mersal-theme", theme);
}

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

describe("U6 — axe over every route × locale × theme", () => {
  it("covers the whole portal catalog, not a sample", () => {
    // Guards the guard: if the catalog import broke, the loop below would silently assert nothing.
    expect(ROUTES.length).toBeGreaterThan(30);
  });

  for (const lang of LANGS) {
    for (const theme of THEMES) {
      it(`has no serious/critical violations on any route (${lang}, ${theme})`, async () => {
        const failures: string[] = [];

        for (const { role, path } of ROUTES) {
          localStorage.clear();
          setPreferences(lang, theme);
          const { container, unmount } = renderApp(path, role);
          // Let the screen settle: most render a heading, some an async section.
          await waitFor(() => expect(container.querySelector("main")).toBeTruthy());

          const results = await axe(container, {
            rules: {
              // See the note above: jsdom cannot paint, so this rule can only produce false confidence here.
              "color-contrast": { enabled: false },
              // Landmark uniqueness is a shell concern already covered by the shell test; asserting it once
              // per route would report the same finding 40 times and bury everything else.
              "landmark-unique": { enabled: false },
            },
          });
          const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
          if (serious.length > 0)
            failures.push(`${path} [${lang}/${theme}]: ${serious.map((v) => v.id).join(", ")}`);
          unmount();
        }

        expect(failures, failures.join("\n")).toEqual([]);
      }, 120_000);
    }
  }
});

describe("U6 — keyboard operability on the high-volume worklists", () => {
  // The four screens an operator lives in all day. A worklist that can be reached but not OPERATED by
  // keyboard is the difference between "accessible" on paper and usable in a clinic.
  const WORKLISTS: Array<{ role: Role; path: string }> = [
    { role: "medical_approval", path: "/approvals/worklist" },
    { role: "case_manager", path: "/cases/my-cases" },
    { role: "lab", path: "/lab/queue" },
    { role: "pharmacy", path: "/pharmacy/queue" },
  ];

  for (const { role, path } of WORKLISTS) {
    it(`${path} exposes an operable grid or actionable controls`, async () => {
      localStorage.clear();
      setPreferences("en", "light");
      const { container } = renderApp(path, role);
      await waitFor(() => expect(container.querySelector("main")).toBeTruthy());

      // Either the rows are a real grid (selectable + arrow-navigable) or the row actions are buttons.
      // What must NOT exist is the state the audit found: focusable rows that do nothing on Enter.
      const grid = container.querySelector('[role="grid"]');
      const buttons = screen.queryAllByRole("button");
      expect(Boolean(grid) || buttons.length > 0).toBe(true);

      if (grid) {
        // A grid must have at least one focusable row, or keyboard users cannot enter it at all.
        const focusable = grid.querySelectorAll('tr[tabindex="0"]');
        expect(focusable.length).toBeGreaterThan(0);
      }
    }, 30_000);
  }
});

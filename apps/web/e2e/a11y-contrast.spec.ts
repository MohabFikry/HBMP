import { test, expect } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";

/**
 * Phase 18.D3 (audit R2 U6) — colour contrast, checked in a browser that can actually paint.
 *
 * `color-contrast` is disabled in both jsdom suites, and it has to be: jsdom has no layout and no rendering,
 * so axe cannot resolve the computed colour of anything and the rule reports nothing whatever the palette
 * does. The consequence was that contrast — the single most common WCAG failure, and the one the design
 * system's whole token layer exists to guarantee — was unverified everywhere, while the a11y gate stayed
 * green.
 *
 * This runs the SAME axe rules in Chromium against the built bundle, in both themes (Playwright projects)
 * and both languages. Dark theme matters independently: 18.D2 found the avatar surface using `--brand`
 * (#00acac, marked "decorative only") under white text at ~2.2:1, and a token that passes in light can fail
 * in dark because the two palettes are derived separately.
 */

/** One representative route per portal — the shells and the dense screens, where contrast actually varies. */
const ROUTES = [
  { path: "/login", role: null },
  { path: "/reception/eligibility", role: "reception" },
  { path: "/clinician/results", role: "doctor" },
  { path: "/approvals/worklist", role: "medical_approval" },
  { path: "/pharmacy/queue", role: "pharmacy" },
  { path: "/lab/queue", role: "lab" },
  { path: "/claims/worklist", role: "claims_officer" },
  { path: "/finance/utilization", role: "finance" },
  { path: "/director/dashboards", role: "medical_director" },
  { path: "/admin/users", role: "org_admin" },
  { path: "/call-centre/workspace", role: "call_center" },
  { path: "/cases/my-cases", role: "case_manager" },
];

const LANGS = ["en", "ar"] as const;

for (const lang of LANGS) {
  for (const { path, role } of ROUTES) {
    test(`contrast: ${path} (${lang})`, async ({ page }) => {
      // Seed the dev session + language before the app boots, the same way the jsdom helper does.
      await page.addInitScript(
        ([r, l]) => {
          localStorage.setItem("mersal-lang", l as string);
          if (r)
            localStorage.setItem(
              "mersal-session",
              JSON.stringify({ userId: `e2e-${r}`, displayName: String(r), role: r, expiresAt: Date.now() + 1_800_000 }),
            );
        },
        [role, lang],
      );

      await page.goto(path);

      // 24.4 — WAIT FOR THE SCREEN, NOT THE SHELL. `state: "attached"` on <main> is satisfied by the app
      // shell, before any lazily-loaded route component has resolved. The jsdom sweep had the identical
      // bug and it was measurable there: 55 of 112 routes handed axe an empty element, so half the
      // platform was audited to no effect while the gate stayed green. Contrast is the rule this job
      // exists for and it needs painted pixels — an empty <main> has none, and reports no violations for
      // the most reassuring of reasons.
      await page.waitForFunction(
        () => (document.querySelector("main")?.textContent ?? "").trim().length > 0,
        undefined,
        { timeout: 15_000 },
      );
      // Load-bearing: if a route ever genuinely renders nothing, fail here rather than pass an empty audit.
      const rendered = await page.evaluate(() => (document.querySelector("main")?.textContent ?? "").trim());
      expect(rendered.length, `${path} (${lang}) rendered an EMPTY <main> — axe would have audited nothing`)
        .toBeGreaterThan(0);

      const contrastViolations = async (state: string) => {
        const results = await new AxeBuilder({ page })
          // ONLY contrast here. Structure is the jsdom suite's job and is already covered on every route;
          // duplicating it would make this job slow and its failures ambiguous about which gate caught what.
          .withRules(["color-contrast"])
          .analyze();
        return results.violations
          .filter((v) => v.impact === "serious" || v.impact === "critical")
          .flatMap((v) => v.nodes.map((n) => `[${state}] ${v.id}: ${n.target.join(" ")} — ${n.failureSummary ?? ""}`));
      };

      const failures = await contrastViolations("resting");

      /*
       * AND AGAIN UNDER THE POINTER (2026-08-09 audit §3).
       *
       * "This job never paints hover" was a literal statement about its coverage, and the defect it let
       * through was not hypothetical: `--accent` is documented at 5.2:1 — against WHITE — and measures
       * 4.44:1 on `--accent-tint`, which is the background every hover paints underneath it. The ACTIVE nav
       * item was caught the first time this job ran and fixed; its hover twin, the ghost button and the icon
       * button were not, because no screenshot pass ever put the pointer on them.
       *
       * Two elements per route, not a sweep: hovering every control would multiply this job's runtime by the
       * number of buttons on the densest screen, and the three states that broke were all of this shape — a
       * nav row and a button. `test/token-contrast.test.ts` sweeps the STYLESHEET for the same class in
       * milliseconds; this is the composited check that arithmetic on hex pairs cannot do.
       */
      for (const selector of [".mrs-navi:not([aria-current])", "button:visible"]) {
        const target = page.locator(selector).first();
        if ((await target.count()) === 0) continue;
        await target.hover();
        failures.push(...(await contrastViolations(`hover ${selector}`)));
      }

      expect(failures, failures.join("\n")).toEqual([]);
    });
  }
}

import { defineConfig, devices } from "@playwright/test";

/**
 * Phase 18.D3 (audit R2 U6) — the browser half of the accessibility gate.
 *
 * The jsdom axe suite covers structure: roles, names, landmarks, labels, list semantics. It cannot cover
 * COLOUR CONTRAST, and not because we chose to skip it — jsdom has no layout engine and no paint, so
 * `getComputedStyle` returns nothing axe can resolve a rendered colour from. The rule was therefore disabled
 * in both existing suites, which meant contrast was unverified across the entire application while the a11y
 * gate reported green. That is the worst kind of gap: it looks covered.
 *
 * This config runs the same axe engine in a real Chromium, where the rule can actually evaluate. It is a
 * SEPARATE job rather than part of `vitest run` because it needs a browser binary and a built app, and a
 * developer running unit tests should not have to have either.
 */
export default defineConfig({
  testDir: "./e2e",
  // Contrast findings are deterministic; a retry would only mask a flaky selector, not a flaky colour.
  retries: 0,
  reporter: process.env.CI ? [["github"], ["list"]] : [["list"]],
  use: {
    baseURL: process.env.PLAYWRIGHT_BASE_URL ?? "http://127.0.0.1:4173",
    // A trace on the first failure is worth far more than a screenshot for a contrast finding: it carries
    // the DOM and the computed styles axe was looking at.
    trace: "retain-on-failure",
  },
  // Both themes get their own project so a failure names which one broke, and light/dark run in parallel.
  projects: [
    {
      name: "light",
      use: { ...devices["Desktop Chrome"], colorScheme: "light" },
    },
    {
      name: "dark",
      use: { ...devices["Desktop Chrome"], colorScheme: "dark" },
    },
  ],
  // Serve the built bundle: contrast depends on the real compiled CSS, not the dev server's injected styles.
  webServer: {
    command: "pnpm --filter @mersal/web preview --port 4173 --strictPort",
    url: "http://127.0.0.1:4173",
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
});

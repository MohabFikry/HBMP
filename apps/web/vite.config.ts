/// <reference types="vitest" />
import { fileURLToPath } from "node:url";
import { defineConfig, loadEnv } from "vite";
import react from "@vitejs/plugin-react";

const here = (p: string) => fileURLToPath(new URL(p, import.meta.url));

/**
 * Is this build LIVE? Read exactly once, here, and used for two things: which `@dev/fixture-mode` the app
 * gets (and therefore what `config.ts` reports as `LIVE`), and whether the fixture backend is bundled at all.
 *
 * `loadEnv` rather than `process.env` alone because the variable arrives two ways: as a Docker `ENV` in the
 * image build, and as `.env`/`.env.local` for a developer running against the Compose stack. It merges both,
 * with the same `envDir` the app itself uses — including the test directory, so the protection described
 * below extends to the alias and a developer's `.env.local` still cannot reshape the unit suite.
 *
 * Test mode is pinned to fixtures outright, ignoring even a shell variable: the suite's whole premise is a
 * role picker and a fixture backend, and "the tests fail on this machine only" is the failure being prevented.
 */
function isLiveBuild(mode: string): boolean {
  if (mode === "test") return false;
  const env = loadEnv(mode, here("."), "VITE_");
  return ["1", "true"].includes((env.VITE_LIVE ?? "").trim().toLowerCase());
}

export default defineConfig(({ mode }) => ({
  plugins: [react()],
  // THE FIXTURE SEAM (2026-08-09 audit §2). A live bundle used to carry `DevApiClient` — 4,111 lines of
  // synthetic patients — and a sign-in that accepts any six digits, both merely unreachable behind a `LIVE`
  // branch. These two aliases are what make them ABSENT instead. Both flip together, from one reading of
  // VITE_LIVE, so the app's belief about its mode cannot disagree with what was bundled.
  // `tools/ci/check-live-bundle-clean.sh` reads the built JS back and proves it. See src/dev/fixtures.ts.
  resolve: {
    alias: {
      "@dev/fixture-mode": isLiveBuild(mode) ? here("src/dev/fixtureMode.live.ts") : here("src/dev/fixtureMode.ts"),
      "@dev/fixtures": isLiveBuild(mode) ? here("src/dev/fixtures.live.ts") : here("src/dev/fixtures.ts"),
    },
  },
  // FIXTURE MODE IS PART OF THE HARNESS, not something to inherit from the machine. Vitest loads .env files
  // through this config, so a developer running the SPA against the live Compose stack (VITE_LIVE=1 in their
  // .env.local) silently put the WHOLE unit suite into live mode: the demo sign-in form is replaced by an
  // OIDC redirect and the login test fails on a machine where nothing is wrong. `import.meta.env` is
  // substituted at transform time, so this cannot be fixed at runtime in a setup file — the tests are pointed
  // at an env directory that holds no .env at all.
  envDir: mode === "test" ? "./test" : undefined,
  // THE CONTRAST GATE'S SERVER, configured HERE rather than as CLI flags. Playwright's webServer used to run
  // `pnpm --filter @mersal/web preview --port 4173 --strictPort`; pnpm parses `--port` as one of ITS OWN
  // options, so the command died before vite ever started and the job failed with
  // "Timed out waiting 120000ms from config.webServer" — a message that reads like a slow server rather than
  // a command that never ran. Flags that have to survive two argument parsers are flags that eventually do
  // not; in the config there is nothing to swallow them.
  //
  // host is pinned to 127.0.0.1 to MATCH playwright.config's url. Vite's default binds localhost, which
  // resolves to ::1 first on some hosts — the server would then be up and the probe still timing out.
  preview: { port: 4173, strictPort: true, host: "127.0.0.1" },
  // ONE ORIGIN IN DEVELOPMENT TOO (ADR-0036 §4, phase 28.2).
  //
  // The deployed image proxies these four prefixes to the gateway from its own nginx (apps/web/nginx.conf
  // .template). Without the same arrangement here, `vite dev` would be the one environment where the SPA and
  // the issuer are cross-origin — and the thing that breaks there is the SameSite=Strict session cookie,
  // which does not fail loudly: the sign-in succeeds and the next authorize reports login_required, reading
  // as a credential problem rather than as a missing proxy.
  //
  // A dev/prod split in *authentication topology* is the split most likely to be discovered in production.
  server: {
    proxy: {
      "/api": { target: "http://localhost:8000", changeOrigin: false },
      "/connect": { target: "http://localhost:8000", changeOrigin: false },
      "/identity": { target: "http://localhost:8000", changeOrigin: false },
      "/.well-known": { target: "http://localhost:8000", changeOrigin: false },
    },
  },
  test: {
    globals: true,
    environment: "jsdom",
    setupFiles: ["./test/setup.ts"],
    css: true,
    include: ["test/**/*.test.{ts,tsx}"],
  },
}));

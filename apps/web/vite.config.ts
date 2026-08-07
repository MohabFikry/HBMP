/// <reference types="vitest" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig(({ mode }) => ({
  plugins: [react()],
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

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
  test: {
    globals: true,
    environment: "jsdom",
    setupFiles: ["./test/setup.ts"],
    css: true,
    include: ["test/**/*.test.{ts,tsx}"],
  },
}));

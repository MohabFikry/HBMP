import type { ReactElement } from "react";
import { render } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { AppRouter } from "../src/routing/AppRouter";
import { DevAuthClient } from "../src/auth/authClient";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import type { Role } from "../src/authz/permissions";

const DISPLAY: Record<string, string> = {};

/** Seed a persisted session so restore() logs the user in as `role` before render. */
export function seedSession(role: Role, ttlMs = 30 * 60 * 1000) {
  localStorage.setItem(
    "mersal-session",
    JSON.stringify({
      userId: `dev-${role}`,
      displayName: DISPLAY[role] ?? role,
      role,
      expiresAt: Date.now() + ttlMs,
    }),
  );
}

/** Render the app at `initialPath` with a fresh dev auth client and an injectable API client (fixtures by default). */
export function renderApp(initialPath = "/", role?: Role, apiClient: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  if (role) seedSession(role);
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={apiClient}>
      <MemoryRouter
        initialEntries={[initialPath]}
        future={{ v7_startTransition: true, v7_relativeSplatPath: true }}
      >
        <AppRouter />
      </MemoryRouter>
    </AppProviders>,
  );
}

export function renderNode(ui: ReactElement) {
  return render(<AppProviders authClient={new DevAuthClient()}>{ui}</AppProviders>);
}

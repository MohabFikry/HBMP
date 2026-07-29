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

/**
 * Render a single screen or component in isolation.
 *
 * Wrapped in a Router even though no route is being exercised: in the real app EVERY screen renders inside
 * one, so a screen that reaches for `useNavigate` — as any worklist with a "Patient file" action must — threw
 * only in the test. That made the harness, not the code, the thing under test.
 */
export function renderNode(ui: ReactElement, apiClient: ApiClient = new DevApiClient({ latencyMs: 0 })) {
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={apiClient}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>{ui}</MemoryRouter>
    </AppProviders>,
  );
}

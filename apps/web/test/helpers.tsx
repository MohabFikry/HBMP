import { afterEach, beforeEach, vi } from "vitest";
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

/**
 * Freeze the calendar for a suite whose fixtures name absolute dates.
 *
 * WHY THIS EXISTS — a defect that cost a day to diagnose. Four suites pinned fixture slots to July 2026
 * ("2026-07-22", "2026-07-30") while the booking calendar defaults its month to `new Date()` and the
 * dashboard's "Today" label compares against the real clock. They passed for as long as the wall clock
 * agreed with the fixtures, then went red at midnight on 1 August 2026 — 14 tests, in four files, with no
 * code change behind any of them. The reception-dashboard case had even documented its own fragility: "a
 * day that is (almost always) not today".
 *
 * A test that depends on the day it runs is worse than a missing one, because it fails for a reason nobody
 * changed and the natural response is to distrust the suite rather than the clock. It also fails on somebody
 * ELSE's commit, which is how a red build gets attributed to the wrong person.
 *
 * `toFake: ["Date"]` is deliberate and narrow: faking the timers too would break `userEvent`, which schedules
 * its own delays. Only the calendar is frozen; everything asynchronous still runs for real.
 *
 * The date chosen is inside the month those fixtures use, so the calendar opens on the month whose slots the
 * fixtures actually provide — which is the state every one of these tests was written against.
 */
export function freezeClock(iso = "2026-07-20T09:00:00.000Z") {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date"] });
    vi.setSystemTime(new Date(iso));
  });
  afterEach(() => {
    vi.useRealTimers();
  });
}

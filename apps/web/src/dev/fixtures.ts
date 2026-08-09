import type { ComponentType } from "react";
import type { ApiClient } from "../api/client";
import { DevApiClient } from "../api/DevApiClient";
import { DevAuthClient } from "../auth/devAuthClient";
import type { AuthClient } from "../auth/authClient";
import { DevLoginForm } from "./DevLoginForm";

/**
 * ============================================================================================================
 * THE ONE DOOR THE FIXTURE BACKEND IS REACHED THROUGH
 * ============================================================================================================
 *
 * A production build of the SPA used to carry the whole demo platform: `DevApiClient` — 4,111 lines of
 * synthetic beneficiaries, prescriptions, claims and clinical results — plus `DevAuthClient`, a sign-in that
 * accepts any six digits and mints the permission set of whichever role you pick. Verified, not suspected:
 * `MRS-M-10231`, `Amal Hassan` and `أمل حسن` were all findable as plain strings in `dist/assets/index-*.js`
 * of a `VITE_LIVE=1` build.
 *
 * None of it was *reachable* — `ApiProvider`, `AuthProvider` and `LoginPage` each branched on `LIVE` first.
 * But "unreachable" is a property of today's control flow, argued fresh every time somebody edits one of
 * those three files, and it is not what anyone means when they ask whether the deployed bundle contains a
 * bypass login. Reachability is the wrong question. Presence is the question.
 *
 * So the three imports live here and nowhere else, and `vite.config.ts` resolves `@dev/fixtures` to
 * `fixtures.live.ts` — the refusing stub next door — whenever the build is parameterised live. Rollup then
 * has no path to any of it and drops all three subtrees. `tools/ci/check-live-bundle-clean.sh` reads the
 * built JavaScript back and fails if a fixture marker survived, because an elimination nobody checks is an
 * intention, and this repository has been bitten by those before.
 *
 * ADDING TO THIS MODULE: anything demo-only belongs behind this door — seed data, a fault injector, a
 * scenario switcher. Import it here, add a refusal to the twin, and add a marker string for it to the gate.
 */
export interface Fixtures {
  /** True in this variant, false in the live twin. Read it via `LIVE` in `config.ts`, never directly. */
  readonly available: boolean;
  createApi(): ApiClient;
  createAuth(): AuthClient;
  /** The no-backend role picker rendered by `LoginPage` in fixture builds. */
  readonly LoginForm: ComponentType;
}

export const FIXTURES: Fixtures = {
  available: true,
  // 250ms so the loading states the screens are built around actually appear in the demo. A fixture that
  // resolves instantly is one where nobody ever sees the skeleton they wrote.
  createApi: () => new DevApiClient({ latencyMs: 250 }),
  createAuth: () => new DevAuthClient(),
  LoginForm: DevLoginForm,
};

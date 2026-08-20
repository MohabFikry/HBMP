import type { ComponentType } from "react";
import type { ApiClient } from "../api/client";
import type { BranchApis } from "../api/branchApi";
import { createDevBranchApis } from "./devBranchApis";
import { DevApiClient } from "../api/DevApiClient";
import { DevAuthClient, DEV_SESSION_KEY } from "../auth/devAuthClient";
import type { AuthClient } from "../auth/authClient";
import { ROLE_MAP } from "../config";
import type { Role } from "../authz/permissions";
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
  /**
   * The Clinic Management portal's four surfaces (design 42 §6).
   *
   * Separate from `createApi` because they are a separate client: `branchApi` and its siblings are a narrow
   * module over three services rather than part of `ApiClient`, following the `policyApi` precedent. They
   * belong behind THIS door for the same reason everything else here does — a live bundle must not contain
   * the demo clinic, and unreachability is not absence.
   */
  createBranchApis(): BranchApis;
  /** The no-backend role picker rendered by `LoginPage` in fixture builds. */
  readonly LoginForm: ComponentType;
}

export const FIXTURES: Fixtures = {
  available: true,
  // 250ms so the loading states the screens are built around actually appear in the demo. A fixture that
  // resolves instantly is one where nobody ever sees the skeleton they wrote.
  createApi: () => new DevApiClient({ latencyMs: 250, roles: signedInIssuerRoles }),
  createAuth: () => new DevAuthClient(),
  createBranchApis: () => createDevBranchApis(),
  LoginForm: DevLoginForm,
};

/**
 * The ISSUER roles the signed-in dev user would be carrying, so the fixture can project the patient profile
 * the way the server does (see `profileSectionMatrix.ts`).
 *
 * Read from storage rather than from React state because `ApiProvider` builds the client once, before anyone
 * has signed in — a value captured at construction would be `null` for the whole session. This is a function
 * so it is evaluated per request, which is also what makes switching roles in the dev login take effect
 * without a reload.
 *
 * Derived by INVERTING `ROLE_MAP` rather than by writing a second table: that map already states which issuer
 * titles land on which portal, and two hand-maintained copies of the same correspondence is precisely the
 * drift this whole change is about. One portal role can come from several issuer titles (`radiology_tech` and
 * `imaging_tech` both mean the radiology portal), and returning all of them matches the server, which decides
 * on the WIDEST cell across every role the caller holds.
 */
function signedInIssuerRoles(): readonly string[] {
  const role = signedInPortalRole();
  return role === null ? [] : ISSUER_ROLES_FOR_PORTAL_ROLE[role] ?? [];
}

function signedInPortalRole(): Role | null {
  try {
    const raw = localStorage.getItem(DEV_SESSION_KEY);
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    const role = (parsed as { role?: unknown } | null)?.role;
    return typeof role === "string" ? (role as Role) : null;
  } catch {
    // A corrupt or unreadable session is "no role known", which the fixture treats as "do not project".
    return null;
  }
}

const ISSUER_ROLES_FOR_PORTAL_ROLE: Partial<Record<Role, string[]>> = ROLE_MAP.reduce<
  Partial<Record<Role, string[]>>
>((acc, [issuer, portal]) => {
  (acc[portal] ??= []).push(issuer);
  return acc;
}, {});

import type { Role } from "./authz/permissions";

/**
 * Runtime configuration for the SPA. In **fixture mode** (default, and in tests) the app runs on
 * `DevAuthClient` + `DevApiClient` with no backend. In **live mode** (`VITE_LIVE=1`) it authenticates
 * against identity-service (auth-code + PKCE, ADR-0015) and calls the real services through Kong.
 *
 * `import.meta.env` is statically replaced by Vite — including under vitest, which loads .env files through
 * the same config. It is NOT undefined in tests, as this comment used to claim: a developer with
 * `VITE_LIVE=1` in their .env.local put the whole unit suite into live mode and the login test failed on a
 * machine where nothing was wrong. `vite.config.ts` now pins the test env, so the suite keeps the injected
 * dev clients regardless of how the developer runs the SPA.
 */
const env = (import.meta as { env?: Record<string, string | undefined> }).env ?? {};

/**
 * Read a build-time variable, treating BLANK as absent.
 *
 * `??` alone is wrong here, because it only falls back on null/undefined. `apps/web/Dockerfile` declares
 * these as ARGs whose defaults are the EMPTY STRING, so a build that does not pass `--build-arg` bakes
 * `VITE_OIDC_AUTHORITY:""` — a value `??` happily keeps. The result was a bundle with an empty issuer and
 * an empty client id: login could not even begin, and nothing in the build or the browser said why. Defaults
 * that only apply when a variable is *undefined* are no defence against a toolchain that supplies "".
 */
const fromEnv = (value: string | undefined, fallback: string): string =>
  value !== undefined && value.trim() !== "" ? value : fallback;

/** Live mode. Accepts `1` or `true`: compose and the Dockerfile disagreed on spelling, and the losing
 * spelling silently downgraded the app to fixture mode against a fully working backend. */
export const LIVE = ["1", "true"].includes((env.VITE_LIVE ?? "").trim().toLowerCase());

/** Base URL for the API gateway (Kong). All service calls are `${API_BASE}/<path>`. */
export const API_BASE = fromEnv(env.VITE_API_BASE, "http://localhost:8000/api/v1");

/**
 * The gateway ORIGIN, without the `/api/v1` prefix. 18.C2 (audit R2 W5): identity-service serves the in-app
 * user/role/scope admin at `/identity/*` — deliberately outside `/api/v1`, because it is the issuer's own
 * surface rather than a domain API. Reaching it needs the origin, not the versioned prefix.
 */
export const GATEWAY_BASE = API_BASE.replace(/\/api\/v1\/?$/, "");

export const OIDC = {
  /** The in-app issuer (identity-service, OpenIddict), as the *browser* reaches it (must match token `iss`).
   * Phase 17.5: this replaced Keycloak — endpoints are `/connect/*` and JWKS is at `/.well-known/jwks`. */
  authority: fromEnv(env.VITE_OIDC_AUTHORITY, "http://localhost:8090"),
  clientId: fromEnv(env.VITE_OIDC_CLIENT_ID, "hbmp-web"),
  redirectUri: fromEnv(env.VITE_OIDC_REDIRECT, "http://localhost:5173/"),
  /**
   * The full space-delimited scope set the SPA requests: exactly `IdentityContract.InteractiveScopes` plus
   * `openid` and `offline_access`. The services enforce a scope PER endpoint (e.g. `finance:read`);
   * requesting the union lets any role reach its own endpoints while the service still denies by role.
   *
   * **This list must equal the issuer's interactive set, and `tools/ci/check-spa-scopes.py` fails the build
   * when it does not.** It is asked for as one union up front, so a drift in EITHER direction breaks
   * everything rather than one screen: a scope the SPA requests but the client does not hold refuses the
   * whole login with `ID2051`, and a scope the SPA omits produces a token that authenticates fine and then
   * 403s on every endpoint guarding it. Both happened — the machine-only ingest/projection scopes were still
   * being requested after 18.B1 narrowed the public client, and the claims, notes and policy-administration
   * scopes added since were never added here.
   */
  scope:
    "openid offline_access admin:break-glass admin:read admin:write appointment:read appointment:write " +
    "audit:read auth:decide auth:emergency auth:manual auth:override auth:read auth:review callcentre:act " +
    "callcentre:history:read callcentre:interaction callcentre:read callcentre:verify case:manage " +
    "case:read case:write " +
    "claims:adjudicate claims:adjust claims:appeal claims:batch claims:decide claims:export claims:ingest " +
    "claims:read claims:reconcile claims:reimburse:submit claims:review claims:settle claims:submit " +
    "document:write eligibility:check emr:read emr:write encounter:write finance:approve finance:export " +
    "finance:read finance:write note:read note:write notification:read orders:consume orders:read " +
    "orders:write patient:read patient:write pharmacy:dispense pharmacy:read policy:admin policy:read " +
    "policy:supervise policy:write profile:export profile:read provider:admin provider:finance " +
    "provider:read provider:write " +
    "reception:read reception:search referral:write reporting:export reporting:read reporting:read-financial " +
    "rx:read rx:write",
};

/**
 * Maps an issuer role (the token's flat lower-case `roles` claim) to the SPA's portal {@link Role}. The
 * issuer uses clinical titles (`lab_tech`, `pharmacist`, …); the portal catalog uses portal keys (`lab`,
 * `pharmacy`, …). The first match (in portal-priority order) wins when a user carries several roles.
 */
const ROLE_MAP: Array<[string, Role]> = [
  ["super_admin", "super_admin"],
  ["org_admin", "org_admin"],
  ["medical_director", "medical_director"],
  ["medical_approval", "medical_approval"],
  // 19.7 — ABOVE beneficiary_mgmt in the list, which is what makes the priority order load-bearing: a
  // supervisor carries the officer role too, and matching the officer first would land them on the portal
  // without the supervisory affordances they were promoted for.
  ["policy_admin", "policy_admin"],
  ["beneficiary_mgmt_supervisor", "beneficiary_mgmt"],
  ["case_manager", "case_manager"],
  ["call_center", "call_center"],
  ["claims_officer", "claims_officer"],
  ["finance", "finance"],
  ["provider_admin", "provider_admin"],
  ["network_team", "provider_admin"],
  ["beneficiary_mgmt", "beneficiary_mgmt"],
  ["doctor", "doctor"],
  ["nurse", "nurse"],
  ["lab_tech", "lab"],
  ["imaging_tech", "imaging"],
  ["pharmacist", "pharmacy"],
  ["reception", "reception"],
];

export function roleFromClaimRoles(roles: readonly string[]): Role | null {
  for (const [kc, role] of ROLE_MAP) {
    if (roles.includes(kc)) return role;
  }
  return null;
}

import type { Role } from "./authz/permissions";

/**
 * Runtime configuration for the SPA. In **fixture mode** (default, and in tests) the app runs on
 * `DevAuthClient` + `DevApiClient` with no backend. In **live mode** (`VITE_LIVE=1`) it authenticates
 * against Keycloak (auth-code + PKCE) and calls the real services through Kong.
 *
 * `import.meta.env` is statically replaced by Vite; in vitest it is undefined, so LIVE is false and the
 * unit tests keep using the injected dev clients unchanged.
 */
const env = (import.meta as { env?: Record<string, string | undefined> }).env ?? {};

export const LIVE = env.VITE_LIVE === "1";

/** Base URL for the API gateway (Kong). All service calls are `${API_BASE}/<path>`. */
export const API_BASE = env.VITE_API_BASE ?? "http://localhost:8000/api/v1";

export const OIDC = {
  /** Keycloak realm issuer, as the *browser* reaches it (must match the token `iss`). */
  authority: env.VITE_OIDC_AUTHORITY ?? "http://localhost:8080/realms/mersal",
  clientId: env.VITE_OIDC_CLIENT_ID ?? "hbmp-web",
  redirectUri: env.VITE_OIDC_REDIRECT ?? "http://localhost:5173/",
  /**
   * The full space-delimited scope set the SPA requests. The services enforce a scope PER endpoint
   * (e.g. `finance:read`); requesting the union lets any role reach its own endpoints while the service
   * still denies by role. Keycloak only issues the scopes the user's client is permitted.
   */
  scope:
    "openid admin:read admin:write admin:break-glass " +
    "appointment:read appointment:write audit:read auth:decide auth:emergency auth:ingest " +
    "auth:manual auth:override auth:read auth:review case:manage case:read case:write document:write " +
    "eligibility:check emr:read emr:write encounter:write finance:approve finance:export finance:project " +
    "finance:read finance:write hello:read notification:ingest notification:read orders:consume " +
    "orders:read orders:write patient:write pharmacy:dispense pharmacy:read policy:write provider:finance " +
    "provider:read provider:write reception:search referral:write reporting:export reporting:project " +
    "reporting:read rx:write",
};

/**
 * Maps a Keycloak realm role to the SPA's portal {@link Role}. The IdP uses clinical titles
 * (`lab_tech`, `pharmacist`, …); the portal catalog uses portal keys (`lab`, `pharmacy`, …).
 * The first match (in portal-priority order) wins when a user carries several roles.
 */
const ROLE_MAP: Array<[string, Role]> = [
  ["super_admin", "super_admin"],
  ["org_admin", "org_admin"],
  ["medical_director", "medical_director"],
  ["medical_approval", "medical_approval"],
  ["case_manager", "case_manager"],
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

export function roleFromRealmRoles(realmRoles: readonly string[]): Role | null {
  for (const [kc, role] of ROLE_MAP) {
    if (realmRoles.includes(kc)) return role;
  }
  return null;
}

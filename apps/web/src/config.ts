import { FIXTURE_MODE } from "@dev/fixture-mode";
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

/**
 * Live mode.
 *
 * Derived from WHICH FIXTURE MODULE WAS BUNDLED, not from a second reading of `VITE_LIVE`. `vite.config.ts`
 * parses that variable once (accepting `1` or `true` — compose and the Dockerfile disagreed on spelling, and
 * the losing spelling silently downgraded the app to fixture mode against a fully working backend) and uses
 * it to alias both `@dev/fixture-mode` and `@dev/fixtures`. So "the app believes it is live" and "the demo
 * backend is not in this bundle" are now one fact. See `src/dev/fixtures.ts` for what that buys.
 */
export const LIVE = !FIXTURE_MODE;

/**
 * The origin this bundle is running on, or `""` where there is no document (node, some test runners).
 *
 * Everything below defaults to a RELATIVE path so the browser resolves it against this origin. That is the
 * 28.2 change: the SPA, the API and the issuer are one origin, reached through the app's own nginx (deployed)
 * or the Vite dev proxy (development). See ADR-0036 §4 for the three separate things that depend on it.
 */
const SELF_ORIGIN = typeof window !== "undefined" && window.location ? window.location.origin : "";

/** Base URL for the API gateway (Kong), same-origin by default. All service calls are `${API_BASE}/<path>`. */
export const API_BASE = fromEnv(env.VITE_API_BASE, "/api/v1");

/**
 * The gateway ORIGIN, without the `/api/v1` prefix. 18.C2 (audit R2 W5): identity-service serves the in-app
 * user/role/scope admin at `/identity/*` — deliberately outside `/api/v1`, because it is the issuer's own
 * surface rather than a domain API. Reaching it needs the origin, not the versioned prefix.
 */
export const GATEWAY_BASE = API_BASE.replace(/\/api\/v1\/?$/, "");

export const OIDC = {
  /**
   * The in-app issuer (identity-service, OpenIddict), as the *browser* reaches it.
   * Phase 17.5: this replaced Keycloak — endpoints are `/connect/*` and JWKS is at `/.well-known/jwks`.
   *
   * **28.2: this is now the app's OWN origin, and that is the point.** It used to be `http://localhost:8090`
   * while the app served from `:5173`, so signing in navigated the browser to a visibly different host.
   *
   * It no longer needs to equal the token's `iss`. OpenIddict pins the issuer identifier via
   * `Issuer:PublicUrl` — the fix for ID2088, where tokens minted at `:8090` were rejected when the same
   * request arrived through Kong at `:8000` — so `iss` is a constant the server states, not something derived
   * from whichever host the request came in on. That is what makes this move cheap: no service that pins
   * `Auth__ValidIssuers` is touched.
   */
  authority: fromEnv(env.VITE_OIDC_AUTHORITY, SELF_ORIGIN),
  clientId: fromEnv(env.VITE_OIDC_CLIENT_ID, "hbmp-web"),
  redirectUri: fromEnv(env.VITE_OIDC_REDIRECT, SELF_ORIGIN ? `${SELF_ORIGIN}/` : "http://localhost:5173/"),
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
    "openid offline_access admin:break-glass admin:read admin:write appointment:read " +
    // appointment:reserve is the call centre's booking power WITHOUT check-in/no-show. Requested here for
    // everyone; the token only ever carries what the caller's ROLE grants, so asking is not receiving.
    "appointment:reserve appointment:write " +
    "audit:read auth:configure auth:decide auth:emergency auth:manual auth:override auth:read auth:request-extension " +
    // ADR-0034 — the bench asks whether another examination may stand in. Granted to lab_tech/imaging_tech
    // only; a pharmacist resolves the same question against the formulary without asking anyone.
    "auth:request-substitution auth:review " +
    // 25.1 — the branch-management authorities (design 42 §1). Requested for everyone, granted only to
    // branch_coordinator / clinics_manager: asking is not receiving. Sized to a clinic precisely so that a
    // coordinator never needs provider:write, which is network-wide and also unmasks licence numbers.
    "branch:inventory:read branch:inventory:write branch:practitioner:write branch:roster:write " +
    "callcentre:act " +
    "callcentre:history:read callcentre:interaction callcentre:read callcentre:verify case:manage " +
    "case:read case:write " +
    "claims:adjudicate claims:adjust claims:appeal claims:batch claims:decide claims:export claims:ingest " +
    "claims:read claims:reconcile claims:reimburse:submit claims:review claims:settle claims:submit " +
    "document:write eligibility:check emr:read emr:write encounter:write finance:approve finance:export " +
    "finance:read finance:write " +
    // 26.1 — the reference catalogue. masterdata-service was authenticated but unscoped; every screen that
    // resolves an ICD, ATC, drug or allergen code now needs this in the token. Listed here as well as in the
    // issuer for the reason `practitioner:read` records below: a scope added to one and not the other signs
    // in cleanly and 403s on the read the feature exists to make.
    "masterdata:read " +
    "note:read note:write notification:read orders:consume orders:read " +
    "orders:write patient:read patient:write pharmacy:dispense pharmacy:read policy:admin policy:read " +
    // 29.2b — the external delivering provider (design 45 §2b). DISTINCT from orders:consume on purpose:
    // granting a physiotherapy centre orders:consume would leave a domain rule inside one service as the only
    // thing between it and the whole investigation queue. Requested here for everyone; only
    // procedure_provider is granted it, so asking is not receiving.
    "procedure:consume procedure:read " +
    "policy:supervise policy:write " +
    // 14.5 sized this scope to the need rather than granting reception the whole provider directory: the
    // booking screen reads specialty and doctor from provider-service under the CALLER's token. It was
    // added to the issuer and not here, so the token signed in fine and 403'd on the one read the feature
    // exists to make.
    "practitioner:read " +
    "profile:export profile:read provider:admin provider:finance " +
    "provider:read provider:write " +
    "reception:read reception:search referral:write reporting:export reporting:read reporting:read-financial " +
    "rx:read rx:write",
};

/**
 * Do the app, the issuer and the redirect target all live on ONE origin? (ADR-0036 §4, phase 28.2.)
 *
 * ============================================================================================================
 * WHY THIS IS CHECKED RATHER THAN INTENDED
 * ============================================================================================================
 * `infra/compose/config/kong.yml` has routed `/connect` and `/.well-known` to the issuer since phase 17, with
 * a comment saying it is done *"so the SPA reaches one origin"*. The SPA then pointed at `:8090` anyway, for
 * two years, because nothing compared the two. An intention recorded in a comment is not a constraint.
 *
 * What a violation costs is worse than the misconfiguration it looks like. The issuer's session cookies are
 * `SameSite=Strict`, so a cross-origin login POST has its cookie dropped by the browser: the sign-in returns
 * success and the authorize that follows reports `login_required`. Nothing is logged, nothing 500s, and the
 * user is told their credentials are wrong.
 *
 * A blank value is NOT a violation — it means "resolve against this document", which is same-origin by
 * definition and is the default. Only an explicitly configured foreign origin is.
 */
export function loginOriginsAgree(authority: string, redirectUri: string, appOrigin: string): boolean {
  const originOf = (value: string): string | null => {
    if (!value.trim()) return null;                     // relative ⇒ this origin, by definition
    try {
      return new URL(value, appOrigin || "http://localhost").origin;
    } catch {
      return null;
    }
  };
  const app = appOrigin.trim() ? new URL(appOrigin).origin : null;
  return [originOf(authority), originOf(redirectUri)]
    .filter((o): o is string => o !== null)
    .every((o) => app === null || o === app);
}

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
  // Mapped to ITSELF since the supervisor gained its own portal (registration approvals — US-003). This
  // row used to read `"beneficiary_mgmt"`, which quietly handed the approver the OFFICER's portal: the
  // register pen they must not hold, and none of the approval affordances they exist for (QA P0-3).
  ["beneficiary_mgmt_supervisor", "beneficiary_mgmt_supervisor"],
  ["case_manager", "case_manager"],
  ["call_center", "call_center"],
  ["claims_officer", "claims_officer"],
  ["finance", "finance"],
  ["provider_admin", "provider_admin"],
  ["network_team", "provider_admin"],
  // 25.1 — the two branch roles. They were MISSING here while everything else about them shipped: the
  // portal exists (`base: "branch"`), the permissions exist (BRANCH_ROLE_PERMISSIONS), the scopes are
  // requested above, and the issuer seeds and grants both roles. Only this table was not updated — so the
  // token carried `branch_coordinator`, `roleFromClaimRoles` found no row, returned null, and the SPA
  // fail-closed to "No portal assigned". A correct login, a correct token and a complete portal, presented
  // to the user as an account with no role.
  //
  // SET BEFORE SINGLE, matching BranchScope.ModeFor on the server: someone holding both supervises the
  // network, and matching the coordinator first would narrow them to one clinic — making the wider,
  // explicitly-granted authority the weaker one.
  ["clinics_manager", "clinics_manager"],
  ["branch_coordinator", "branch_coordinator"],
  ["beneficiary_mgmt", "beneficiary_mgmt"],
  ["doctor", "doctor"],
  ["nurse", "nurse"],
  ["lab_tech", "lab"],
  // 29.1 (design 45 §1) — BOTH spellings map to the radiology portal for the dual-accept window.
  //
  // This is the SPA's own dual-accept and it cannot be inherited from the server: libs/auth's
  // LegacyRoleAliases expands roles when a SERVICE parses a token, but the SPA reads the raw `roles` claim
  // out of the token itself. So a technician who was signed in across the deploy holds a token saying
  // `imaging_tech`, and without this line roleFromClaimRoles finds no match, fail-closes to null, and shows
  // them "No portal assigned" — a correct login and a complete portal, presented as an account with no role.
  // That exact failure is documented twenty lines above for `branch_coordinator`; this is the same bug, and
  // the reason the rename ships with a window rather than a find-and-replace.
  //
  // `imaging_tech` goes at the CONTRACT step, with the rest of the dual-accept surface —
  // docs/runbooks/radiology-rename.md.
  ["radiology_tech", "radiology"],
  ["imaging_tech", "radiology"],
  ["pharmacist", "pharmacy"],
  // 29.2b — the external delivering provider (design 45 §2b).
  ["procedure_provider", "procedure_provider"],
  ["reception", "reception"],
];

export function roleFromClaimRoles(roles: readonly string[]): Role | null {
  for (const [kc, role] of ROLE_MAP) {
    if (roles.includes(kc)) return role;
  }
  return null;
}

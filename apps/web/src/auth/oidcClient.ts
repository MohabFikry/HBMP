import { unionPermissions, type Role } from "../authz/permissions";
import { OIDC, roleFromClaimRoles, rolesFromClaimRoles } from "../config";
import {
  clearTokens, getRefreshToken, getScopeRequest, getToken, setRefreshToken, setScopeRequest, setToken,
} from "./tokenStore";
import type { AuthClient, Session } from "./authClient";

/**
 * A minimal OIDC authorization-code + PKCE client for the in-app issuer (identity-service, OpenIddict —
 * 17.5; formerly Keycloak), no external dependency. `login()` redirects to the IdP's `/connect/authorize`;
 * on return, `restore()` detects the `?code=` callback, exchanges it for an access token, and
 * builds the same {@link Session} shape the dev client produces (so the shell/router/timeout logic is
 * unchanged). The access token is handed to the API layer via the token store; MFA is evidenced by the
 * token's `acr`/`amr` claims. See phase-9 US-070 and libs/auth.
 */
const VERIFIER_KEY = "mersal-pkce-verifier";
const STATE_KEY = "mersal-oidc-state";
/** Records that a re-authorisation has already been spent chasing a scope, so it cannot be spent twice. */
const REAUTH_KEY = "mersal-scope-reauth";
/** How long the bootstrap will wait for the issuer's entitlement answer before carrying on without it. */
const ENTITLEMENT_TIMEOUT_MS = 2000;

interface JwtClaims {
  sub: string;
  exp: number;
  preferred_username?: string;
  name?: string;
  acr?: string;
  amr?: string[] | string;
  /** The issuer's flat, lower-case role claim (17.5). May be an array or a single value. */
  roles?: string[] | string;
}

function decodeJwt(token: string): JwtClaims {
  const payload = token.split(".")[1];
  const json = atob(payload.replace(/-/g, "+").replace(/_/g, "/"));
  return JSON.parse(json) as JwtClaims;
}

function randomString(bytes = 32): string {
  const a = new Uint8Array(bytes);
  crypto.getRandomValues(a);
  return base64url(a.buffer);
}

function base64url(buf: ArrayBuffer): string {
  const bytes = new Uint8Array(buf);
  let s = "";
  for (const b of bytes) s += String.fromCharCode(b);
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
}

async function pkceChallenge(verifier: string): Promise<string> {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(verifier));
  return base64url(digest);
}

function asArray(v: string[] | string | undefined): string[] {
  return Array.isArray(v) ? v : v ? [v] : [];
}

/**
 * Was this token minted for a DIFFERENT set of scopes than the app now asks for?
 *
 * ============================================================================================================
 * WHY A SESSION GOES STALE — AND WHY THIS COMPARES THE QUESTION, NOT THE ANSWER
 * ============================================================================================================
 * A token's scopes are fixed at the moment of authorisation. Add a scope to the SPA's request list — as
 * `masterdata:read` and `eligibility:check` were — and every already-signed-in user keeps the old, narrower
 * token. Refreshing does not close the gap: the issuer re-mints from the CURRENT entitlement but constrains
 * it to the scopes on the stored grant (`ConnectEndpoints`, refresh branch), so a renewal faithfully reissues
 * the same narrow set for as long as the session lives.
 *
 * What that looked like at a dispensing counter: `POST /drugs/prices/by-ids` answering 403, so the unit price
 * and the active ingredient both rendered as "not recorded" — a screen that handles money quietly reporting
 * an authorisation gap as a fact about the medicine.
 *
 * ------------------------------------------------------------------------------------------------------------
 * THE VERSION THIS REPLACES ASKED AN UNANSWERABLE QUESTION.
 * ------------------------------------------------------------------------------------------------------------
 * It required the token to carry EVERY scope in {@link OIDC.scope}. But the issuer grants the INTERSECTION of
 * the request with the user's role entitlement, on purpose — "asking is not receiving", as `config.ts` puts
 * it. A reception token carries 15 of the 80 scopes the SPA requests; the widest role in the system, super
 * admin, carries 21. So the check was false for every user who has ever signed in, and `restore()` cleared
 * the tokens on every page load — which is what "it logs me off whenever I refresh" was. The same call in
 * `renew()` then wiped a healthy session sixty seconds before its token expired.
 *
 * Its unit tests passed because every one of them fabricated a token carrying the SPA's whole request list —
 * a token no issuer in this system can mint.
 *
 * ------------------------------------------------------------------------------------------------------------
 * WHAT IS ACTUALLY KNOWABLE FROM HERE.
 * ------------------------------------------------------------------------------------------------------------
 * The SPA cannot see a user's entitlement, so it cannot tell a legitimately narrow token from a stale one by
 * reading the token. What it CAN tell is whether its own requirements have moved: the scope string it asked
 * with is recorded beside the token, and a mismatch means this token predates a change to the app's needs and
 * a fresh authorisation would widen it. That is precisely the deploy-time case the counter hit.
 *
 * The case NOT covered: an administrator widening a role on the issuer without a matching SPA release. Those
 * sessions keep the narrower token until the refresh token expires. The previous code did not detect that
 * either — nothing local can — and pretending to costs every user their session on every reload.
 *
 * An absent record ⇒ treated as CURRENT, matching the fail-open rule this guard already followed for a token
 * it could not parse: it is not evidence of staleness, and re-authorising on it loops a user through login.
 */
export function scopeRequestChanged(mintedFor: string | null): boolean {
  if (!mintedFor) return false;
  const normalise = (s: string) => [...new Set(s.split(" ").filter(Boolean))].sort().join(" ");
  return normalise(mintedFor) !== normalise(OIDC.scope);
}

/**
 * The scopes a token actually carries, or null when it cannot be read.
 *
 * Null is not an empty set: "this token grants nothing" and "I could not tell" lead to opposite decisions, and
 * collapsing them would have the app re-authorise on every token shape it does not recognise.
 */
export function scopesOf(token: string): Set<string> | null {
  try {
    const claim = (decodeJwt(token) as unknown as { scope?: unknown }).scope;
    const list = typeof claim === "string" ? claim.split(" ") : Array.isArray(claim) ? claim.map(String) : null;
    return list ? new Set(list.filter(Boolean)) : null;
  } catch {
    return null;
  }
}

/**
 * Which scopes the app NEEDS, is ENTITLED to, and does not HOLD.
 *
 * The three-way intersection is the whole point. A scope the token lacks is unremarkable — the issuer grants
 * least privilege and a reception token legitimately carries a fraction of what the SPA asks for. A scope the
 * user is entitled to but the token lacks is a session that predates a grant, and a fresh authorisation fixes
 * it. Only the second is worth acting on, and only the issuer can tell them apart.
 */
export function missingGrantedScopes(
  entitlement: ReadonlySet<string>, held: ReadonlySet<string>, requested: string,
): string[] {
  return requested
    .split(" ")
    .filter((sc) => sc && sc !== "openid" && sc !== "offline_access")
    .filter((sc) => entitlement.has(sc) && !held.has(sc));
}

/**
 * Ask the issuer what this caller would be granted right now (28.11).
 *
 * Returns null on ANY failure — timeout, transport, non-200, a body that is not what we expect. Every one of
 * those means "no information", and the caller treats no information as "nothing to do". This runs in the
 * application's bootstrap path, so an issuer that is slow or down must cost a page load nothing more than the
 * cap below; a check that could hang the portal would be a worse defect than the one it exists to find.
 */
async function fetchEntitlement(token: string): Promise<Set<string> | null> {
  const abort = new AbortController();
  // A plain controller rather than `AbortSignal.timeout`, which is newer than the browsers this deploys to.
  const timer = setTimeout(() => abort.abort(), ENTITLEMENT_TIMEOUT_MS);
  try {
    const res = await fetch(`${OIDC.authority}/connect/entitlement`, {
      headers: { Authorization: `Bearer ${token}` },
      credentials: "same-origin",
      signal: abort.signal,
    });
    if (!res.ok) return null;
    const json = (await res.json()) as { scopes?: unknown };
    if (!Array.isArray(json.scopes)) return null;
    return new Set(json.scopes.map(String));
  } catch {
    return null;
  } finally {
    clearTimeout(timer);
  }
}

/**
 * Re-authorise when the user's entitlement has outgrown their token.
 *
 * ============================================================================================================
 * THE CASE THIS CLOSES
 * ============================================================================================================
 * An administrator adds a scope to a role. Every live session keeps the token it was minted with — the refresh
 * grant re-mints from the current entitlement but constrains it to the scopes on the stored grant, because a
 * refresh must never widen authority. So the gap persists for the life of the refresh token, and every screen
 * needing the new scope collects a 403 that the UI has no way to attribute.
 *
 * `scopeRequestChanged` catches the other half of this — the app's own request list moving — from local state
 * alone. This half is not knowable locally, which is why it costs a round trip.
 *
 * ============================================================================================================
 * WHY IT CANNOT LOOP, AND WHY THAT MATTERS MORE THAN THE FEATURE
 * ============================================================================================================
 * The remedy here is a full-page navigation to `/connect/authorize`. If a re-authorisation were ever to come
 * back still short of the scope — a role changed again mid-flight, an issuer that refuses a scope for a reason
 * this client cannot see, a `config.ts` naming a scope the issuer has never heard of — the naive version would
 * bounce the browser between the app and the issuer forever, and the user could not even reach a login screen
 * to escape it.
 *
 * So one attempt per tab, recorded before navigating and cleared only when a later load finds nothing missing.
 * The second failure keeps the narrow token and lets the 403 happen: a session that is short one scope is a
 * bad afternoon, and an infinite redirect is an unusable portal.
 */
async function reauthoriseIfEntitlementWidened(token: string): Promise<void> {
  const held = scopesOf(token);
  if (!held) return;                       // unreadable ⇒ no evidence, same fail-open rule as everywhere here
  const entitlement = await fetchEntitlement(token);
  if (!entitlement) return;                // could not ask ⇒ nothing to act on

  const missing = missingGrantedScopes(entitlement, held, OIDC.scope);
  if (missing.length === 0) {
    // Satisfied. Clearing here rather than on any successful token exchange is what makes the guard below a
    // one-shot per PROBLEM rather than a one-shot per tab — a later, genuine widening is still acted on.
    try { sessionStorage.removeItem(REAUTH_KEY); } catch { /* ignore */ }
    return;
  }
  try {
    if (sessionStorage.getItem(REAUTH_KEY)) return;
    sessionStorage.setItem(REAUTH_KEY, missing.join(" "));
  } catch {
    // No sessionStorage means no loop guard, and an unguarded redirect loop is worse than a stale scope.
    return;
  }
  // The navigation is STARTED, not awaited to completion — `restore()` still has a session to return, and the
  // caller keeps a working portal for the few milliseconds before the page is replaced. If the navigation
  // never happens, the user is left signed in with a token short one scope, which is the right way to fail.
  await beginSilentAuthorize();
}

function sessionFrom(token: string): Session {
  const c = decodeJwt(token);
  // FAIL CLOSED (H6): an unmapped role yields role=null (→ "no portal assigned" page). Never default to a
  // portal — that would silently grant an authenticated stranger reception access. Roles are the issuer's
  // flat `roles` claim (17.5), no longer Keycloak's nested realm_access.
  const claimed = asArray(c.roles);
  const role: Role | null = roleFromClaimRoles(claimed);
  // Every portal the token names, not just the primary — this is what the picker picks from. `role` stays
  // the first of them, so a single-role token produces a byte-identical session to before.
  const roles: Role[] = rolesFromClaimRoles(claimed);
  const mfa = (c.acr && ["mfa", "aal2", "aal3", "loa2", "loa3", "2fa"].includes(c.acr)) ||
    asArray(c.amr).some((m) => ["mfa", "otp", "hwk", "totp", "webauthn", "sms"].includes(m));
  return {
    userId: c.sub,
    displayName: c.name ?? c.preferred_username ?? c.sub,
    role,
    roles,
    // The UNION over every held role. Still derived from the token's own roles claim, so this cannot grant
    // anything the issuer did not — and the server re-authorizes every call regardless.
    permissions: unionPermissions(roles),
    mfaSatisfied: Boolean(mfa),
    expiresAt: c.exp * 1000,
  };
}

/**
 * Complete a sign-in the SPA has ALREADY driven through `/connect/session` (ADR-0036 §3, phase 28.4).
 *
 * The issuer's cookie is set by then, so `prompt=none` returns a code without rendering anything and the
 * browser lands back here with `?code=` — which `restore()` already knows how to exchange. A full-page
 * navigation, not a hidden iframe: the only screen that starts this is the login screen, so there is no SPA
 * state worth preserving, and an iframe would put a framing dependency (`X-Frame-Options`,
 * `frame-ancestors`) into the authentication path, where a future clickjacking-hardening change would break
 * login and the failure would look like an authentication bug.
 *
 * Under `prompt=none` the issuer answers with `error=login_required` or `error=interaction_required` rather
 * than redirecting to its own login page — which is what stops this looping against a page the SPA does not
 * use. `restore()` reads those.
 */
export async function silentAuthorize(): Promise<never> {
  await beginSilentAuthorize();
  // Navigation replaces the page; this Promise never resolves, so a caller that awaits it never continues.
  return new Promise<never>(() => {});
}

/**
 * Start the silent authorisation and resolve once the navigation has been ISSUED.
 *
 * Split out from {@link silentAuthorize} because that function deliberately never settles — correct for the
 * login screen, which has nothing left to do, and a deadlock for `restore()`, which is mid-bootstrap and has
 * a session to hand back. The distinction is real and not merely a test affordance: a caller either has more
 * work to do after starting the navigation, or it does not.
 */
async function beginSilentAuthorize(): Promise<void> {
  const verifier = randomString();
  const state = randomString(16);
  sessionStorage.setItem(VERIFIER_KEY, verifier);
  sessionStorage.setItem(STATE_KEY, state);
  const challenge = await pkceChallenge(verifier);
  const url = new URL(`${OIDC.authority}/connect/authorize`, window.location.origin);
  url.search = new URLSearchParams({
    client_id: OIDC.clientId,
    redirect_uri: OIDC.redirectUri,
    response_type: "code",
    scope: OIDC.scope,
    state,
    prompt: "none",
    code_challenge: challenge,
    code_challenge_method: "S256",
  }).toString();
  window.location.assign(url.toString());
}

export class OidcAuthClient implements AuthClient {
  /** Ignores the dev (role, mfaCode) args — the IdP owns identity + MFA. Redirects to the issuer. */
  async login(): Promise<Session> {
    const verifier = randomString();
    const state = randomString(16);
    sessionStorage.setItem(VERIFIER_KEY, verifier);
    sessionStorage.setItem(STATE_KEY, state);
    const challenge = await pkceChallenge(verifier);
    // The second argument is not decoration. 28.2 made `authority` default to the app's OWN origin, expressed
    // as "" (resolve against this document) wherever it is configured — and `new URL("/connect/authorize")`
    // with no base throws. Passing the base makes the relative and absolute forms behave identically.
    const url = new URL(`${OIDC.authority}/connect/authorize`, window.location.origin);
    url.search = new URLSearchParams({
      client_id: OIDC.clientId,
      redirect_uri: OIDC.redirectUri,
      response_type: "code",
      scope: OIDC.scope,
      state,
      code_challenge: challenge,
      code_challenge_method: "S256",
    }).toString();
    window.location.assign(url.toString());
    // Navigation replaces the page; this Promise never resolves.
    return new Promise<Session>(() => {});
  }

  async logout(): Promise<void> {
    clearTokens();
    const url = new URL(`${OIDC.authority}/connect/logout`, window.location.origin);
    url.search = new URLSearchParams({
      client_id: OIDC.clientId,
      post_logout_redirect_uri: OIDC.redirectUri,
    }).toString();
    window.location.assign(url.toString());
  }

  async restore(): Promise<Session | null> {
    // 1) Post-redirect callback: exchange the code for a token.
    const params = new URLSearchParams(window.location.search);
    const code = params.get("code");
    const returnedState = params.get("state");

    // 28.4 — a `prompt=none` round trip that could not be satisfied comes back with `error=` instead of a
    // code (`login_required`, or `interaction_required` when a membership must be chosen). That is the
    // protocol working: the issuer does not redirect a silent request to its own login page, which is what
    // keeps the SPA from looping against a page it never renders. Cleared from the URL and reported as "no
    // session" — the login screen is already the right place to be, and leaving `?error=login_required` in
    // the address bar of a perfectly ordinary sign-in page reads like a fault.
    if (!code && params.has("error")) {
      cleanUrl();
      return null;
    }
    if (code) {
      const expected = sessionStorage.getItem(STATE_KEY);
      const verifier = sessionStorage.getItem(VERIFIER_KEY);
      cleanUrl();
      if (!verifier || returnedState !== expected) return null;
      const tokens = await exchangeCode(code, verifier);
      if (!tokens) return null;
      store(tokens);
      // Checked on THIS path too, and not only to catch a widening a fresh sign-in cannot have. It is the
      // path a re-authorisation lands on, so it is where the one-shot guard is cleared once the new token
      // satisfies the app — without it the guard would be spent for the life of the tab.
      await reauthoriseIfEntitlementWidened(tokens.accessToken);
      return sessionFrom(tokens.accessToken);
    }
    // 2) Reload within the tab: reuse a still-valid token.
    const existing = getToken();
    if (existing) {
      // A token minted for a narrower REQUEST than the app now makes can never widen — not by reuse and not
      // by refresh. It is discarded here so the next step is a fresh authorisation, which asks with the
      // current list. Doing it at restore means the counter is already correct by the time it asks for a
      // price, rather than discovering the gap as a 403 and rendering it as missing data.
      if (scopeRequestChanged(getScopeRequest())) {
        clearTokens();
        return null;
      }
      try {
        const s = sessionFrom(existing);
        if (s.expiresAt > Date.now()) {
          // The half of staleness that is not knowable locally: an entitlement widened on the issuer since
          // this token was minted (28.11). Awaited, because the remedy is a navigation and returning a
          // session first would yank the page out from under someone who has started using it.
          await reauthoriseIfEntitlementWidened(existing);
          return s;
        }
      } catch {
        /* fall through to the refresh attempt */
      }
      // 18.C1 — an EXPIRED access token is not the end of the session. Reloading a tab after more than five
      // minutes used to drop the user at the login redirect even though the refresh token was still good.
      setToken(null);
      const renewed = await this.renew();
      if (renewed) {
        const fresh = getToken();
        if (fresh) await reauthoriseIfEntitlementWidened(fresh);
        return renewed;
      }
      clearTokens();
    }
    return null;
  }

  /**
   * Phase 18.C1 (audit R2 W1) — exchange the refresh token for a fresh access token.
   *
   * Returns the new session, or null when the session is genuinely over. Two failures are deliberately NOT
   * distinguished: an expired refresh token and a REUSED one. The issuer rotates on every refresh, so
   * presenting a token that has already been redeemed means either a benign race (two tabs renewing at once)
   * or a stolen token being replayed. There is no way to tell them apart from here, and the safe response to
   * both is the same — discard everything and make the user authenticate. Guessing "it was probably the other
   * tab" and retrying is what turns a detected theft into an undetected one. The single-flight guard below
   * removes the benign case, so a rejection really does mean something went wrong.
   */
  async renew(): Promise<Session | null> {
    if (renewInFlight) return renewInFlight;
    const token = getRefreshToken();
    if (!token) return null;

    renewInFlight = (async () => {
      const tokens = await redeemRefreshToken(token);
      if (!tokens) {
        clearTokens();
        return null;
      }
      // No scope check here. A renewal cannot widen the grant, but neither can the SPA's request list change
      // while this tab is running the JavaScript that defines it — so the only thing a check could do at this
      // point is destroy a session that is working. It did exactly that: sixty seconds before every token
      // expired, the renewal wiped the pair and the timeout logout followed at five minutes. Staleness is
      // decided once, at restore, where the app is newly loaded and the question is meaningful.
      store(tokens);
      return sessionFrom(tokens.accessToken);
    })();
    try {
      return await renewInFlight;
    } finally {
      renewInFlight = null;
    }
  }
}

/** In-flight renewal, shared so concurrent 401s or timers cannot each redeem the SINGLE-USE refresh token and
 * have all but the first read as a reuse — which would log the user out mid-session for no reason. */
let renewInFlight: Promise<Session | null> | null = null;

interface TokenSet {
  accessToken: string;
  refreshToken: string | null;
}

function store(tokens: TokenSet): void {
  setToken(tokens.accessToken);
  // Recorded with the token, not derived from it: what the app ASKED for is the only half of the exchange a
  // later load can compare against. See `scopeRequestChanged`.
  setScopeRequest(OIDC.scope);
  // The issuer ROTATES: each response carries a new refresh token and invalidates the one just used. Failing
  // to persist the replacement would make the NEXT renewal look exactly like a replay attack.
  if (tokens.refreshToken) setRefreshToken(tokens.refreshToken);
}

async function exchangeCode(code: string, verifier: string): Promise<TokenSet | null> {
  return postToken({
    grant_type: "authorization_code",
    client_id: OIDC.clientId,
    code,
    redirect_uri: OIDC.redirectUri,
    code_verifier: verifier,
  });
}

async function redeemRefreshToken(refreshToken: string): Promise<TokenSet | null> {
  return postToken({
    grant_type: "refresh_token",
    client_id: OIDC.clientId,
    refresh_token: refreshToken,
  });
}

/** One shape for both grants. Returning null on ANY non-2xx keeps the caller from having to distinguish
 * invalid_grant from a network blip — both mean "you do not have a usable token", and the caller re-auths. */
async function postToken(form: Record<string, string>): Promise<TokenSet | null> {
  try {
    const res = await fetch(`${OIDC.authority}/connect/token`, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams(form).toString(),
    });
    if (!res.ok) return null;
    const json = (await res.json()) as { access_token?: string; refresh_token?: string };
    if (!json.access_token) return null;
    return { accessToken: json.access_token, refreshToken: json.refresh_token ?? null };
  } catch {
    return null;
  }
}

function cleanUrl(): void {
  sessionStorage.removeItem(STATE_KEY);
  sessionStorage.removeItem(VERIFIER_KEY);
  window.history.replaceState({}, document.title, window.location.pathname);
}

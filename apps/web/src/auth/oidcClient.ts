import { permissionsForRole, type Role } from "../authz/permissions";
import { OIDC, roleFromClaimRoles } from "../config";
import { clearTokens, getRefreshToken, getToken, setRefreshToken, setToken } from "./tokenStore";
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

function sessionFrom(token: string): Session {
  const c = decodeJwt(token);
  // FAIL CLOSED (H6): an unmapped role yields role=null (→ "no portal assigned" page). Never default to a
  // portal — that would silently grant an authenticated stranger reception access. Roles are the issuer's
  // flat `roles` claim (17.5), no longer Keycloak's nested realm_access.
  const role: Role | null = roleFromClaimRoles(asArray(c.roles));
  const mfa = (c.acr && ["mfa", "aal2", "aal3", "loa2", "loa3", "2fa"].includes(c.acr)) ||
    asArray(c.amr).some((m) => ["mfa", "otp", "hwk", "totp", "webauthn", "sms"].includes(m));
  return {
    userId: c.sub,
    displayName: c.name ?? c.preferred_username ?? c.sub,
    role,
    permissions: role ? permissionsForRole(role) : new Set(),
    mfaSatisfied: Boolean(mfa),
    expiresAt: c.exp * 1000,
  };
}

export class OidcAuthClient implements AuthClient {
  /** Ignores the dev (role, mfaCode) args — the IdP owns identity + MFA. Redirects to the issuer. */
  async login(): Promise<Session> {
    const verifier = randomString();
    const state = randomString(16);
    sessionStorage.setItem(VERIFIER_KEY, verifier);
    sessionStorage.setItem(STATE_KEY, state);
    const challenge = await pkceChallenge(verifier);
    const url = new URL(`${OIDC.authority}/connect/authorize`);
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
    const url = new URL(`${OIDC.authority}/connect/logout`);
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
    if (code) {
      const expected = sessionStorage.getItem(STATE_KEY);
      const verifier = sessionStorage.getItem(VERIFIER_KEY);
      cleanUrl();
      if (!verifier || returnedState !== expected) return null;
      const tokens = await exchangeCode(code, verifier);
      if (!tokens) return null;
      store(tokens);
      return sessionFrom(tokens.accessToken);
    }
    // 2) Reload within the tab: reuse a still-valid token.
    const existing = getToken();
    if (existing) {
      try {
        const s = sessionFrom(existing);
        if (s.expiresAt > Date.now()) return s;
      } catch {
        /* fall through to the refresh attempt */
      }
      // 18.C1 — an EXPIRED access token is not the end of the session. Reloading a tab after more than five
      // minutes used to drop the user at the login redirect even though the refresh token was still good.
      setToken(null);
      const renewed = await this.renew();
      if (renewed) return renewed;
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

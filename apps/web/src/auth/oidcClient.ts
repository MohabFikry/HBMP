import { permissionsForRole, type Role } from "../authz/permissions";
import { OIDC, roleFromRealmRoles } from "../config";
import { getToken, setToken } from "./tokenStore";
import type { AuthClient, Session } from "./authClient";

/**
 * A minimal OIDC authorization-code + PKCE client for Keycloak — no external dependency. `login()` redirects
 * to the IdP; on return, `restore()` detects the `?code=` callback, exchanges it for an access token, and
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
  amr?: string[];
  realm_access?: { roles?: string[] };
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

function sessionFrom(token: string): Session {
  const c = decodeJwt(token);
  // FAIL CLOSED (H6): an unmapped realm role yields role=null (→ "no portal assigned" page). Never default
  // to a portal — that would silently grant an authenticated stranger reception access.
  const role: Role | null = roleFromRealmRoles(c.realm_access?.roles ?? []);
  const mfa = (c.acr && ["mfa", "aal2", "aal3", "loa2", "loa3", "2fa"].includes(c.acr)) ||
    (c.amr ?? []).some((m) => ["mfa", "otp", "hwk", "totp", "webauthn", "sms"].includes(m));
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
  /** Ignores the dev (role, mfaCode) args — the IdP owns identity + MFA. Redirects to Keycloak. */
  async login(): Promise<Session> {
    const verifier = randomString();
    const state = randomString(16);
    sessionStorage.setItem(VERIFIER_KEY, verifier);
    sessionStorage.setItem(STATE_KEY, state);
    const challenge = await pkceChallenge(verifier);
    const url = new URL(`${OIDC.authority}/protocol/openid-connect/auth`);
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
    setToken(null);
    const url = new URL(`${OIDC.authority}/protocol/openid-connect/logout`);
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
      const token = await exchangeCode(code, verifier);
      if (!token) return null;
      setToken(token);
      return sessionFrom(token);
    }
    // 2) Reload within the tab: reuse a still-valid token.
    const existing = getToken();
    if (existing) {
      try {
        const s = sessionFrom(existing);
        if (s.expiresAt > Date.now()) return s;
      } catch {
        /* fall through */
      }
      setToken(null);
    }
    return null;
  }
}

async function exchangeCode(code: string, verifier: string): Promise<string | null> {
  try {
    const res = await fetch(`${OIDC.authority}/protocol/openid-connect/token`, {
      method: "POST",
      headers: { "Content-Type": "application/x-www-form-urlencoded" },
      body: new URLSearchParams({
        grant_type: "authorization_code",
        client_id: OIDC.clientId,
        code,
        redirect_uri: OIDC.redirectUri,
        code_verifier: verifier,
      }).toString(),
    });
    if (!res.ok) return null;
    const json = (await res.json()) as { access_token?: string };
    return json.access_token ?? null;
  } catch {
    return null;
  }
}

function cleanUrl(): void {
  sessionStorage.removeItem(STATE_KEY);
  sessionStorage.removeItem(VERIFIER_KEY);
  window.history.replaceState({}, document.title, window.location.pathname);
}

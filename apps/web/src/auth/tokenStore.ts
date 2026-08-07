/**
 * Holds the current access token for the live API client. Kept in module memory (fast, not readable by
 * other tabs) with a sessionStorage mirror so a page reload within the tab restores it without a new
 * redirect. Fixture mode never sets a token, so `getToken()` returns null and `http.ts` sends no bearer.
 */
import { clearDrafts } from "../screens/draftStore";

const KEY = "mersal-access-token";
const REFRESH_KEY = "mersal-refresh-token";
const SCOPE_KEY = "mersal-token-scope-request";
let current: string | null = null;
let refresh: string | null = null;
let scopeRequest: string | null = null;

export function getToken(): string | null {
  if (current) return current;
  try {
    current = sessionStorage.getItem(KEY);
  } catch {
    /* ignore */
  }
  return current;
}

export function setToken(token: string | null): void {
  current = token;
  try {
    if (token) sessionStorage.setItem(KEY, token);
    else sessionStorage.removeItem(KEY);
  } catch {
    /* ignore */
  }
}

/**
 * Phase 18.C1 (audit R2 W1) — the refresh token.
 *
 * The SPA requested `offline_access` and then dropped the `refresh_token` from the exchange response, so a
 * 5-minute access token was all a session ever got. The portal did not appear to break, because the
 * session-timeout logic tracked its OWN clock and `keepAlive()` simply moved that clock forward — so after
 * five minutes the UI showed a live session while every API call returned 401. Being logged out is at least
 * legible; a portal that looks signed in and fails every request is not.
 *
 * It lives beside the access token in sessionStorage rather than localStorage: sessionStorage is per-tab and
 * cleared when the tab closes, so a shared clinic workstation does not carry a long-lived refresh token
 * between users. That is a deliberate trade — closing the tab really does end the session.
 */
export function getRefreshToken(): string | null {
  if (refresh) return refresh;
  try {
    refresh = sessionStorage.getItem(REFRESH_KEY);
  } catch {
    /* ignore */
  }
  return refresh;
}

export function setRefreshToken(token: string | null): void {
  refresh = token;
  try {
    if (token) sessionStorage.setItem(REFRESH_KEY, token);
    else sessionStorage.removeItem(REFRESH_KEY);
  } catch {
    /* ignore */
  }
}

/**
 * The scope string the app ASKED FOR when this token was minted.
 *
 * Not the scopes the token carries — those are the issuer's answer, always a subset, and comparing them to
 * anything the SPA knows is what made the previous staleness guard fire for every user on every reload. This
 * records the QUESTION, so a later load can tell whether the app's own requirements have changed since.
 */
export function getScopeRequest(): string | null {
  if (scopeRequest) return scopeRequest;
  try {
    scopeRequest = sessionStorage.getItem(SCOPE_KEY);
  } catch {
    /* ignore */
  }
  return scopeRequest;
}

export function setScopeRequest(scope: string | null): void {
  scopeRequest = scope;
  try {
    if (scope) sessionStorage.setItem(SCOPE_KEY, scope);
    else sessionStorage.removeItem(SCOPE_KEY);
  } catch {
    /* ignore */
  }
}

/** Drop both tokens. Used on logout and whenever a renewal is refused — never leave a dead pair behind for
 * the next attempt to retry with.
 *
 * The workspace drafts go with them. A half-composed prescription is clinical content sitting in a browser
 * store on a machine a clinic shares, and the end of a session is exactly when it stops being the current
 * user's — the next person at that workstation must not be able to reload into someone else's unsent order.
 */
export function clearTokens(): void {
  setToken(null);
  setRefreshToken(null);
  setScopeRequest(null);
  clearDrafts();
}

/**
 * Holds the current access token for the live API client. Kept in module memory (fast, not readable by
 * other tabs) with a sessionStorage mirror so a page reload within the tab restores it without a new
 * redirect. Fixture mode never sets a token, so `getToken()` returns null and `http.ts` sends no bearer.
 */
const KEY = "mersal-access-token";
let current: string | null = null;

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

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { OidcAuthClient } from "../src/auth/oidcClient";
import { clearTokens, getRefreshToken, getToken, setRefreshToken, setToken } from "../src/auth/tokenStore";
import { activeBranchHeader, setActiveBranch } from "../src/api/activeBranch";
import { getRaw } from "../src/api/http";

/**
 * Phase 18.C1 (audit R2 W1/W2) — the two links that made the live SPA unusable, and both were invisible.
 *
 * W1: the SPA requested `offline_access` and dropped the refresh token, so a session was one 5-minute access
 * token. It did not LOOK broken, because the timeout logic tracked its own clock and `keepAlive()` moved that
 * clock forward — a portal that appears signed in while every request 401s.
 *
 * W2: `X-Active-Branch` was never sent, so choosing a branch in the switcher changed a value in React state
 * and nothing else. A receptionist at one desk could be reading another branch's queue.
 */

// A JWT the client can decode: header.payload.signature, payload base64url-encoded. Never verified here —
// signature checking is the services' job (libs/auth), the SPA only reads claims.
function jwt(expSecondsFromNow: number, sub = "u-1"): string {
  const payload = { sub, exp: Math.floor(Date.now() / 1000) + expSecondsFromNow, roles: ["reception"], name: "Reham" };
  const b64 = (o: unknown) => btoa(JSON.stringify(o)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${b64({ alg: "RS256" })}.${b64(payload)}.sig`;
}

function tokenResponse(body: Record<string, unknown>, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "content-type": "application/json" } });
}

beforeEach(() => {
  clearTokens();
  setActiveBranch(null);
});
afterEach(() => {
  vi.unstubAllGlobals();
  clearTokens();
  setActiveBranch(null);
});

describe("W1 — silent renewal", () => {
  it("exchanges the refresh token for a new access token and stores the ROTATED replacement", async () => {
    setToken(jwt(-10));            // expired
    setRefreshToken("refresh-1");
    const fetchMock = vi.fn((_url: string, _init?: RequestInit) =>
      Promise.resolve(tokenResponse({ access_token: jwt(300), refresh_token: "refresh-2" })));
    vi.stubGlobal("fetch", fetchMock);

    const session = await new OidcAuthClient().renew();

    expect(session).not.toBeNull();
    expect(session!.userId).toBe("u-1");
    // The issuer rotates on every refresh. Keeping the OLD token would make the next renewal look like a
    // replay and log the user out mid-shift.
    expect(getRefreshToken()).toBe("refresh-2");
    expect(getToken()).not.toBeNull();

    const body = String(fetchMock.mock.calls[0]![1]!.body);
    expect(body).toContain("grant_type=refresh_token");
    expect(body).toContain("refresh_token=refresh-1");
  });

  it("a refused renewal clears BOTH tokens so nothing retries with a dead pair", async () => {
    setToken(jwt(-10));
    setRefreshToken("stale-or-replayed");
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(tokenResponse({ error: "invalid_grant" }, 400))));

    expect(await new OidcAuthClient().renew()).toBeNull();
    expect(getToken()).toBeNull();
    expect(getRefreshToken()).toBeNull();
  });

  it("concurrent renewals redeem the single-use token exactly once", async () => {
    // Without single-flight, two tabs (or a timer racing a 401) each POST the same refresh token; the issuer
    // rotates, so the second is a REUSE and gets rejected — logging the user out for no reason. The guard is
    // what lets a rejection genuinely mean something is wrong.
    setRefreshToken("refresh-1");
    const fetchMock = vi.fn(() =>
      Promise.resolve(tokenResponse({ access_token: jwt(300), refresh_token: "refresh-2" })));
    vi.stubGlobal("fetch", fetchMock);

    const client = new OidcAuthClient();
    const [a, b] = await Promise.all([client.renew(), client.renew()]);

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(a).not.toBeNull();
    expect(b).not.toBeNull();
  });

  it("renew is a no-op when there is no refresh token", async () => {
    const fetchMock = vi.fn();
    vi.stubGlobal("fetch", fetchMock);
    expect(await new OidcAuthClient().renew()).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it("a response without an access token is treated as a failure, not a session", async () => {
    setRefreshToken("refresh-1");
    vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(tokenResponse({ refresh_token: "refresh-2" }))));
    expect(await new OidcAuthClient().renew()).toBeNull();
    expect(getRefreshToken()).toBeNull();
  });
});

describe("W2 — X-Active-Branch", () => {
  it("is absent until a branch is chosen", () => {
    expect(activeBranchHeader()).toEqual({});
  });

  it("rides on every API request once set", async () => {
    setActiveBranch("b-dokki");
    const fetchMock = vi.fn((_url: string, _init?: RequestInit) =>
      Promise.resolve(new Response("{}", { status: 200 })));
    vi.stubGlobal("fetch", fetchMock);

    await getRaw("/api/v1/queue");

    const headers = fetchMock.mock.calls[0]![1]!.headers as Record<string, string>;
    expect(headers["X-Active-Branch"]).toBe("b-dokki");
  });

  it("stops being sent when the branch is cleared", async () => {
    setActiveBranch("b-dokki");
    setActiveBranch(null);
    const fetchMock = vi.fn((_url: string, _init?: RequestInit) =>
      Promise.resolve(new Response("{}", { status: 200 })));
    vi.stubGlobal("fetch", fetchMock);

    await getRaw("/api/v1/queue");

    const headers = fetchMock.mock.calls[0]![1]!.headers as Record<string, string>;
    // Absent, not empty: an empty header would be a branch id of "" for the server to reject.
    expect(headers["X-Active-Branch"]).toBeUndefined();
  });
});

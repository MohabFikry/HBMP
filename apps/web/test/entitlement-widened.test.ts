import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { OidcAuthClient, missingGrantedScopes, scopesOf } from "../src/auth/oidcClient";
import { clearTokens, getToken, setScopeRequest, setToken } from "../src/auth/tokenStore";
import { OIDC } from "../src/config";

/**
 * Phase 28.11 — the half of scope staleness that is not knowable from the browser.
 *
 * <p>An administrator adds a scope to a role. Every live session keeps the token it was minted with, because
 * the refresh grant is constrained to the scopes on the stored grant — a refresh must never widen authority.
 * The gap then persists for the life of the refresh token, and every screen needing the new scope collects a
 * 403 the UI cannot attribute.</p>
 *
 * <p>The client cannot detect this by reading its own token: a token narrower than the request is normally
 * just least privilege working. The previous guard assumed otherwise and signed every user out on every page
 * load. So the issuer is asked, and the answer is intersected with what this application actually needs.</p>
 *
 * <p><b>The test that matters most here is the loop guard.</b> The remedy is a full-page navigation, and a
 * re-authorisation that came back still short of the scope would bounce the browser between the app and the
 * issuer with no screen in between — a user could not even reach a login form to escape it. A session missing
 * one scope is a bad afternoon; an infinite redirect is an unusable portal.</p>
 */

const HELD = "openid offline_access appointment:read";
/** What the issuer says reception is entitled to — wider than the token above. */
const ENTITLED = ["appointment:read", "appointment:write", "patient:read", "eligibility:check"];

function jwt(scope: string, expSecondsFromNow = 300): string {
  const payload = {
    sub: "u-1", exp: Math.floor(Date.now() / 1000) + expSecondsFromNow,
    roles: ["reception"], name: "Reham", scope,
  };
  const b64 = (o: unknown) => btoa(JSON.stringify(o)).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
  return `${b64({ alg: "RS256" })}.${b64(payload)}.sig`;
}

/** Stands in for the navigation `silentAuthorize` performs, which a jsdom page cannot actually do. */
let navigated: string[] = [];

beforeEach(() => {
  clearTokens();
  sessionStorage.clear();
  navigated = [];
  vi.stubGlobal("location", {
    ...window.location,
    origin: "http://localhost:5173",
    assign: (url: string) => navigated.push(url),
  });
});
afterEach(() => {
  vi.unstubAllGlobals();
  clearTokens();
  sessionStorage.clear();
});

function issuerSays(scopes: string[] | null, status = 200) {
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
    if (!String(input).includes("/connect/entitlement")) return new Response("{}", { status: 200 });
    if (scopes === null) return new Response("{}", { status });
    return new Response(JSON.stringify({ scopes }), {
      status, headers: { "content-type": "application/json" },
    });
  }));
}

describe("deciding what is actually missing", () => {
  it("ignores a scope the token lacks that the user is not entitled to", () => {
    // The everyday case, and the one the old guard got wrong: reception does not hold `finance:read`, the app
    // asks for it on behalf of other roles, and none of that is staleness.
    const missing = missingGrantedScopes(new Set(ENTITLED), new Set(HELD.split(" ")), OIDC.scope);
    expect(missing).not.toContain("finance:read");
  });

  it("reports a scope the user IS entitled to and the token does not carry", () => {
    const missing = missingGrantedScopes(new Set(ENTITLED), new Set(HELD.split(" ")), OIDC.scope);
    expect(missing).toContain("patient:read");
  });

  it("never reports the protocol scopes, which the issuer may normalise away", () => {
    const missing = missingGrantedScopes(
      new Set([...ENTITLED, "openid", "offline_access"]), new Set(["appointment:read"]), OIDC.scope);
    expect(missing).not.toContain("openid");
    expect(missing).not.toContain("offline_access");
  });

  it("distinguishes a token it cannot read from one that grants nothing", () => {
    // Null and the empty set lead to opposite decisions — "no evidence" versus "no scopes" — and collapsing
    // them would re-authorise on every token shape this build does not recognise.
    expect(scopesOf("not-a-jwt")).toBeNull();
    expect(scopesOf(jwt("emr:read"))).toEqual(new Set(["emr:read"]));
    // A claim that is PRESENT and empty is an answer, not a failure to read one.
    expect(scopesOf(jwt(""))).toEqual(new Set());
  });
});

describe("a reload whose entitlement has outgrown its token", () => {
  it("re-authorises", async () => {
    setToken(jwt(HELD));
    setScopeRequest(OIDC.scope);
    issuerSays(ENTITLED);

    await new OidcAuthClient().restore();

    expect(navigated).toHaveLength(1);
    expect(navigated[0]).toContain("/connect/authorize");
    expect(navigated[0]).toContain("prompt=none");
  });

  it("spends that re-authorisation ONCE, and then lets the 403 happen", async () => {
    // The loop guard. Without it, an entitlement the issuer will not actually grant — a role changed again
    // mid-flight, a scope in `config.ts` the issuer has never heard of — bounces the browser forever.
    setToken(jwt(HELD));
    setScopeRequest(OIDC.scope);
    issuerSays(ENTITLED);

    await new OidcAuthClient().restore();
    expect(navigated).toHaveLength(1);

    // Second load, same unsatisfiable answer.
    setToken(jwt(HELD));
    setScopeRequest(OIDC.scope);
    await new OidcAuthClient().restore();

    expect(navigated).toHaveLength(1);
    // And the session survives — giving up must not also throw the user out.
    expect(getToken()).not.toBeNull();
  });

  it("frees the guard again once a load finds nothing missing", async () => {
    // One-shot per PROBLEM, not per tab: a later, genuine widening is still acted on.
    setToken(jwt(HELD));
    setScopeRequest(OIDC.scope);
    issuerSays(ENTITLED);
    await new OidcAuthClient().restore();
    expect(navigated).toHaveLength(1);

    // A load where the token is current clears the record...
    setToken(jwt(HELD));
    issuerSays(["appointment:read"]);
    await new OidcAuthClient().restore();
    expect(navigated).toHaveLength(1);

    // ...so the next real widening gets its own attempt.
    setToken(jwt(HELD));
    issuerSays(ENTITLED);
    await new OidcAuthClient().restore();
    expect(navigated).toHaveLength(2);
  });
});

describe("when the issuer cannot be asked", () => {
  it("carries on with the session it has", async () => {
    // Fail OPEN. This runs in the bootstrap path, and an issuer that is slow or unreachable must cost a page
    // load nothing — a check that could strand the portal would be a worse defect than the one it finds.
    setToken(jwt(HELD));
    setScopeRequest(OIDC.scope);
    issuerSays(null, 503);

    const session = await new OidcAuthClient().restore();

    expect(session).not.toBeNull();
    expect(navigated).toHaveLength(0);
  });

  it("carries on when the transport itself fails", async () => {
    setToken(jwt(HELD));
    setScopeRequest(OIDC.scope);
    vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("network"); }));

    expect(await new OidcAuthClient().restore()).not.toBeNull();
    expect(navigated).toHaveLength(0);
  });

  it("carries on when the answer is not the shape it expects", async () => {
    // A body we cannot read is no information, not "you are entitled to nothing" — which would silently
    // disable the check while looking like it passed.
    setToken(jwt(HELD));
    setScopeRequest(OIDC.scope);
    vi.stubGlobal("fetch", vi.fn(async () =>
      new Response(JSON.stringify({ scopes: "appointment:read" }), {
        status: 200, headers: { "content-type": "application/json" },
      })));

    expect(await new OidcAuthClient().restore()).not.toBeNull();
    expect(navigated).toHaveLength(0);
  });
});

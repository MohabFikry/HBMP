import { describe, expect, it } from "vitest";
import { scopeRequestChanged } from "../src/auth/oidcClient";
import { OIDC } from "../src/config";

/**
 * A session whose token was minted for a NARROWER request than the app now makes.
 *
 * <p>Token scopes are fixed at authorisation. Add a scope to the SPA's list — as `masterdata:read` was — and
 * every already-signed-in user keeps the old token, and a refresh is constrained to the scopes on the stored
 * grant, so the gap renews itself for as long as the session lives. At a dispensing counter that showed up as
 * `POST /drugs/prices/by-ids` answering 403, with the unit price and the active ingredient rendering as "not
 * recorded" — a screen that handles money reporting an authorisation gap as a fact about the medicine.</p>
 *
 * <p><b>What this file is really guarding, after the fact.</b> The previous version of this check compared the
 * token's GRANTED scopes against everything the SPA requests, and every test here fed it a token carrying the
 * app's entire request list. No issuer in this system mints such a token: the grant is the intersection with
 * the user's role entitlement, so a reception token holds 15 of 80 scopes and the widest role holds 21. The
 * check was therefore false for every real user, `restore()` cleared the tokens on every page load, and the
 * portal signed people out on every refresh — while these tests stayed green, because the fixture was a token
 * that cannot exist.</p>
 *
 * <p>So the first test below is the one that matters: a REAL token shape — narrow, role-filtered — must not be
 * read as stale. Everything else here is detail.</p>
 */

/** The scopes the issuer actually granted a reception sign-in, taken from a live token. */
const RECEPTION_GRANT =
  "openid offline_access appointment:read appointment:write patient:read eligibility:check";

describe("what the app asked for, versus what it asks for now", () => {
  it("does NOT call a session stale merely because its token is role-narrow", () => {
    // The regression test for the sign-out-on-every-refresh bug. A reception token carries a fraction of the
    // request list by design; the request it was minted WITH is what matters, and that is unchanged here.
    expect(scopeRequestChanged(OIDC.scope)).toBe(false);
    // Stated explicitly, so the distinction survives a future edit: the granted set is not the question.
    expect(RECEPTION_GRANT.split(" ").length).toBeLessThan(OIDC.scope.split(" ").length);
  });

  it("is stale when the app now asks for a scope the token was not minted with", () => {
    // Precisely the shape that broke the counter: signed in before `masterdata:read` joined the list.
    const before = OIDC.scope.split(" ").filter((s) => s !== "masterdata:read").join(" ");
    expect(scopeRequestChanged(before)).toBe(true);
  });

  it("is stale when the app now asks for FEWER scopes than the token was minted with", () => {
    // Both directions, deliberately. A release that drops a scope leaves tokens carrying authority the app no
    // longer intends to hold, and least privilege is not a one-way ratchet.
    expect(scopeRequestChanged(`${OIDC.scope} some:retired-scope`)).toBe(true);
  });

  it("ignores ordering and repetition, which carry no meaning in a scope string", () => {
    // A space-delimited set. Re-sorting `config.ts` must not sign the whole estate out.
    const shuffled = [...OIDC.scope.split(" ")].reverse().join(" ");
    expect(scopeRequestChanged(shuffled)).toBe(false);
    expect(scopeRequestChanged(`${OIDC.scope} openid`)).toBe(false);
  });

  it("treats an unrecorded request as CURRENT rather than stale", () => {
    // Fail OPEN, matching the rule the old guard already followed for a token it could not parse. Absence is
    // not evidence of staleness — it is a token stored by a build that predates this record — and
    // re-authorising on it would sign out every open tab the moment this ships.
    expect(scopeRequestChanged(null)).toBe(false);
    expect(scopeRequestChanged("")).toBe(false);
  });
});

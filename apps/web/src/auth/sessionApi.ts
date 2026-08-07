import { OIDC } from "../config";

/**
 * The first-party sign-in API (ADR-0036 §5, phase 28.4).
 *
 * ============================================================================================================
 * WHAT THIS TALKS TO, AND WHY IT NEVER SEES A TOKEN
 * ============================================================================================================
 * `/connect/session/*` establishes the issuer's ordinary session cookie and answers with a STATUS — where in
 * the sign-in sequence the caller now is. It mints nothing. Once the status is `authenticated`, the token
 * still comes from the unchanged authorization-code + PKCE flow in `oidcClient`, run with `prompt=none` so it
 * completes without rendering anything.
 *
 * That split is the whole design. Signing in is up to four steps — password, second factor, membership
 * choice, and enrolment alongside — and a "post the password, get a token" endpoint cannot express any of
 * them, because the token endpoint has no way to say *now give me your TOTP code*.
 */

/** The closed set the server answers with. Anything else is a version skew, not a state. */
export type SessionStatus =
  | "authenticated"
  | "two_factor_required"
  | "membership_selection_required"
  | "no_membership"
  | "locked"
  | "invalid_credentials";

export interface MembershipOption {
  membershipId: string;
  tenantId: string;
  roles: string[];
}

export interface SessionState {
  status: SessionStatus;
  /** Present on `locked` only. */
  retryAfterSeconds?: number | null;
  /** Present on `membership_selection_required` only. */
  memberships?: MembershipOption[] | null;
  /** Present on `authenticated` — lets the UI offer enrolment without changing when it is required. */
  twoFactorEnrolled?: boolean | null;
  csrf?: string | null;
}

/**
 * Raised when the sign-in service itself could not be reached or answered something unusable.
 *
 * DELIBERATELY DISTINCT from a `SessionState`. "We could not ask" is not "your credentials are wrong", and
 * rendering the first as the second would tell a nurse with a correct password to reset it during an outage —
 * the same failure the platform refuses everywhere else it reads a dependency.
 */
export class SessionUnavailableError extends Error {
  constructor(message = "sign-in-unavailable") {
    super(message);
    this.name = "SessionUnavailableError";
  }
}

const base = () => `${OIDC.authority}/connect/session`;

/**
 * Holds the rotating antiforgery token.
 *
 * ASP.NET binds an antiforgery token to the AUTHENTICATED user, so the one fetched before sign-in stops
 * validating the moment the password step succeeds — and the second factor or membership choice that follows
 * is refused with a 400 that looks like a bug in this file. The server hands back a fresh token with every
 * reply; this class's only job is to always send the newest one. A client that kept the first would work for
 * single-step sign-ins and break for every account with a second factor, which is the wrong half to break.
 */
export class SessionClient {
  private csrf: string | null = null;

  private async ensureCsrf(): Promise<string> {
    if (this.csrf) return this.csrf;
    const res = await fetch(`${base()}/antiforgery`, { credentials: "same-origin" });
    if (!res.ok) throw new SessionUnavailableError();
    const json = (await res.json()) as { token?: string };
    if (!json.token) throw new SessionUnavailableError();
    this.csrf = json.token;
    return this.csrf;
  }

  private async post(path: string, body: unknown): Promise<SessionState> {
    const csrf = await this.ensureCsrf();
    let res: Response;
    try {
      res = await fetch(`${base()}${path}`, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-HBMP-CSRF": csrf },
        body: JSON.stringify(body),
      });
    } catch {
      throw new SessionUnavailableError();
    }

    // 400 is the antiforgery refusal. Retry ONCE with a fresh token: a token can go stale for entirely
    // innocent reasons — the tab sat open across an issuer restart, or a previous attempt rotated it — and
    // making the user retype a password because of that is a worse answer than asking again ourselves.
    if (res.status === 400 && this.csrf) {
      this.csrf = null;
      const retry = await this.ensureCsrf();
      res = await fetch(`${base()}${path}`, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-HBMP-CSRF": retry },
        body: JSON.stringify(body),
      });
    }

    if (!res.ok) throw new SessionUnavailableError();
    const state = (await res.json()) as SessionState;
    if (state.csrf) this.csrf = state.csrf;
    return state;
  }

  /** Step 1. `rememberDevice` makes the issuer's cookie survive closing the browser. */
  signIn(username: string, password: string, rememberDevice = false): Promise<SessionState> {
    return this.post("", { username, password, rememberDevice });
  }

  /** Step 2 — TOTP, or a recovery code when the authenticator is gone. */
  submitSecondFactor(code: string, recovery = false): Promise<SessionState> {
    return this.post("/2fa", { code, recovery });
  }

  /** Step 3 — which organization this session acts for. */
  chooseMembership(membershipId: string): Promise<SessionState> {
    return this.post("/membership", { membershipId });
  }

  /** The authenticator secret to enrol against, for a signed-in caller. */
  async authenticator(): Promise<{ key: string; otpauthUri: string }> {
    const res = await fetch(`${base()}/authenticator`, { credentials: "same-origin" });
    if (!res.ok) throw new SessionUnavailableError();
    return (await res.json()) as { key: string; otpauthUri: string };
  }

  /** Confirm enrolment. Returns the recovery codes — shown once, and never fetched again. */
  async enrol(code: string): Promise<{ recoveryCodes: string[] } | SessionState> {
    const csrf = await this.ensureCsrf();
    const res = await fetch(`${base()}/authenticator`, {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-HBMP-CSRF": csrf },
      body: JSON.stringify({ code }),
    });
    if (!res.ok) throw new SessionUnavailableError();
    const json = (await res.json()) as { recoveryCodes?: string[] } & SessionState;
    if (json.csrf) this.csrf = json.csrf;
    return json.recoveryCodes ? { recoveryCodes: json.recoveryCodes } : json;
  }
}

import type { Permission, Role } from "../authz/permissions";

/**
 * Authenticated session. In production `permissions` are derived from the issuer's token + admin-service
 * effective roles; here the dev client seeds them from the role. `mfaSatisfied` reflects the step-up.
 */
export interface Session {
  userId: string;
  displayName: string;
  /**
   * Portal role. `null` means the caller authenticated at the IdP but carries no realm role that maps to a
   * portal — a valid, fail-closed state that renders the "no portal assigned" page (never a default portal).
   */
  role: Role | null;
  permissions: ReadonlySet<Permission>;
  mfaSatisfied: boolean;
  /** Epoch ms when the access token expires (drives the idle/absolute session guard). */
  expiresAt: number;
}

/**
 * AuthClient abstraction. The real implementation wraps an OIDC (identity-service) client — authorization-code +
 * PKCE, MFA via the IdP, silent renew. `DevAuthClient` (src/auth/devAuthClient.ts, fixture builds only)
 * simulates the same shape so the portal shell, routing, and session-timeout logic are identical regardless
 * of backend availability.
 */
export interface AuthClient {
  /** Begin login. Dev: resolves after the caller supplies role + MFA. Prod: redirects to the issuer. */
  login(role: Role, mfaCode: string): Promise<Session>;
  logout(): Promise<void>;
  /** Restore a persisted session on reload (returns null if none/expired). */
  restore(): Promise<Session | null>;
  /**
   * Phase 18.C1 (audit R2 W1) — obtain a fresh access token without a redirect, or null when the session is
   * over. Optional: the dev client has no issuer to renew against, and its `restore()` already returns a
   * session with a full 30-minute window, so there is nothing to renew.
   */
  renew?(): Promise<Session | null>;
}

/**
 * Absolute session lifetime — 30 min, matching the session-policy default tier. Lives here rather than in
 * the dev client because the OIDC path extends the same window on "stay signed in".
 */
export const SESSION_TTL = 30 * 60 * 1000;

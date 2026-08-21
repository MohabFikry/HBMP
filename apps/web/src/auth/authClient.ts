import type { Permission, Role } from "../authz/permissions";

/**
 * Authenticated session. In production `permissions` are derived from the issuer's token + admin-service
 * effective roles; here the dev client seeds them from the role. `mfaSatisfied` reflects the step-up.
 */
export interface Session {
  userId: string;
  displayName: string;
  /**
   * PRIMARY portal role — the first of {@link roles} in `ROLE_MAP` priority order. `null` means the caller
   * authenticated at the IdP but carries no realm role that maps to a portal — a valid, fail-closed state
   * that renders the "no portal assigned" page (never a default portal).
   *
   * It decides the landing portal for a single-portal caller and the `actorRole` on audit events. It does
   * NOT decide what the caller may reach: that is {@link roles}.
   */
  role: Role | null;
  /**
   * EVERY portal role the caller holds, priority-ordered.
   *
   * The session used to carry one role because a person was assumed to do one job. Real staff do not: a
   * clinics manager is often also an org admin, a supervisor keeps the officer role they were promoted from,
   * and an approving doctor holds both a clinic and the approvals queue. Their tokens always said so — the
   * session threw it away, so the portal picker had nothing to pick from.
   *
   * Empty exactly when {@link role} is null; the two are derived from the same claim in one pass.
   */
  roles: readonly Role[];
  /**
   * The ISSUER's own role names, unmapped — 33.7.
   *
   * `ROLE_MAP` is many-to-one on purpose: `lab_tech` and `imaging_tech` are portals, `radiology_tech` is an
   * alias, and a portal is the right unit for deciding which rail to draw. It is the wrong unit for
   * deciding an AUTHORITY, and one row makes that unavoidable:
   *
   *     ["provider_admin", "provider_admin"],
   *     ["network_team",   "provider_admin"],
   *
   * `network_team` is Mersal's Network Team — tenant-wide, T2, it administers the whole provider directory.
   * `provider_admin` is one provider's own administrator — T4, bounded to that provider by ABAC and RLS.
   * Opposite scope, same portal name, and every server rule about them is keyed on the ISSUER name. So
   * `mayAdministerTiers(session.role)` compared a portal name against a rule naming
   * `network_team | org_admin | super_admin` and answered yes for both — offering a provider's own
   * administrator the Create-tier and Revoke-assignment controls, each of which the server refuses with
   * `urn:hbmp:network-tier-access-denied`.
   *
   * This grants nothing. It is the same claim the token already carries, kept rather than discarded, so a
   * client-side mirror of a server rule can be written against the same names the rule uses.
   */
  issuerRoles: readonly string[];
  /**
   * Union across {@link roles}, NOT the primary's set alone.
   *
   * Narrowing to the active portal was the rejected alternative: each portal's nav and routes are already
   * permission-gated by its OWN catalog sections, so the union cannot widen a portal beyond what it
   * contains — while scoping to the active role would 403 a deep link into a portal the caller genuinely
   * holds, and put the client's opinion of their authority at odds with the token's.
   */
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
  /** Begin login. Dev: resolves after the caller supplies roles + MFA. Prod: redirects to the issuer. */
  login(roles: readonly Role[], mfaCode: string): Promise<Session>;
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

import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { auditClient } from "../audit/auditClient";
import type { Permission, Role } from "../authz/permissions";
import { SESSION_TTL, type AuthClient, type Session } from "./authClient";
import { OidcAuthClient } from "./oidcClient";
import { FIXTURES } from "@dev/fixtures";
import { LIVE } from "../config";

/**
 * The default auth client: real OIDC (identity-service) in live mode, else the no-backend dev stub. The stub
 * is reached through `@dev/fixtures`, which a live build resolves to a refusal — so the "any six digits signs
 * you in as any role" client is absent from a production bundle rather than merely unreached.
 *
 * Built ON FIRST USE, not at module scope, and that is load-bearing rather than tidiness. The fixture module
 * pulls in `DevLoginForm`, which uses `useAuth` from this file — a cycle, and with a module-scope
 * initialiser this file's turn to evaluate comes while `FIXTURES` is still the uninitialised half of it.
 * The whole suite failed with `Cannot read properties of undefined (reading 'createAuth')`. Deferring past
 * import time is what makes the seam free to import whatever it needs; memoised so the client stays a
 * singleton, which the session timers rely on.
 */
let cachedAuthClient: AuthClient | null = null;
const defaultAuthClient = (): AuthClient =>
  (cachedAuthClient ??= LIVE ? new OidcAuthClient() : FIXTURES.createAuth());

/** Idle warning fires this long before the session expires. */
const WARN_BEFORE_MS = 60 * 1000;

/**
 * Phase 18.C1 (audit R2 W1) — how early to renew silently.
 *
 * The issuer's access token lives 300s (frozen contract). Renewing 60s out leaves four clear minutes of
 * normal use per cycle and a full minute of slack for a slow round trip, so a renewal can fail and be
 * retried once before the token the user is actually holding goes stale.
 */
const RENEW_BEFORE_MS = 60 * 1000;

interface AuthContextValue {
  session: Session | null;
  ready: boolean;
  /** True in the warning window before expiry (drives the re-auth prompt). */
  timeoutWarning: boolean;
  login: (roles: readonly Role[], mfaCode: string) => Promise<void>;
  logout: (reason?: "user" | "timeout") => Promise<void>;
  /** Refresh the idle timer (called on user activity + on "stay signed in"). */
  keepAlive: () => void;
  can: (permission: Permission) => boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children, client: injected }: { children: ReactNode; client?: AuthClient }) {
  // `useState` and not a default parameter: the default has to be built lazily (see above), and a bare
  // `injected ?? defaultAuthClient()` in the body would hand every render a fresh identity to the effect
  // dependency lists below — remounting the session timers on each pass.
  const [client] = useState<AuthClient>(() => injected ?? defaultAuthClient());
  const [session, setSession] = useState<Session | null>(null);
  const [ready, setReady] = useState(false);
  const [timeoutWarning, setTimeoutWarning] = useState(false);
  const warnTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const expireTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const renewTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const clearTimers = () => {
    if (warnTimer.current) clearTimeout(warnTimer.current);
    if (expireTimer.current) clearTimeout(expireTimer.current);
    if (renewTimer.current) clearTimeout(renewTimer.current);
  };

  const logout = useCallback(
    async (reason: "user" | "timeout" = "user") => {
      clearTimers();
      setTimeoutWarning(false);
      auditClient.emit({
        type: reason === "timeout" ? "auth.timeout" : "auth.logout",
        actorUserId: session?.userId ?? null,
        actorRole: session?.role ?? null,
      });
      await client.logout();
      setSession(null);
    },
    [client, session],
  );

  /**
   * 18.C1 — silent renew, then the timeout UI only if it fails.
   *
   * Previously these timers were the whole session model: warn at T-60s, log out at T. With a 300-second
   * access token that meant a forced re-login every five minutes, so `keepAlive()` compensated by moving
   * `expiresAt` forward WITHOUT getting a new token. The clock advanced and the credential did not: the
   * portal showed a healthy session while every request 401'd. A user cannot act on that — there is nothing
   * on screen suggesting they should sign in again.
   *
   * Now the timers hang off the REAL token expiry and a renewal is attempted first. The timeout modal is
   * reserved for what it was always meant to signal: the session is genuinely ending.
   */
  const scheduleTimers = useCallback(
    (s: Session) => {
      clearTimers();
      const now = Date.now();
      warnTimer.current = setTimeout(() => setTimeoutWarning(true), Math.max(0, s.expiresAt - WARN_BEFORE_MS - now));
      expireTimer.current = setTimeout(() => void logout("timeout"), Math.max(0, s.expiresAt - now));
      if (!client.renew) return;
      renewTimer.current = setTimeout(() => void renewNow(), Math.max(0, s.expiresAt - RENEW_BEFORE_MS - now));
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [logout, client],
  );

  /** Renew and reschedule. On refusal the timers already running carry the session to its real expiry, so a
   * transient failure costs nothing and a genuine one still ends in the warning + logout the user expects. */
  const renewNow = useCallback(async (): Promise<boolean> => {
    if (!client.renew) return false;
    const renewed = await client.renew();
    if (!renewed) return false;
    setSession(renewed);
    setTimeoutWarning(false);
    scheduleTimers(renewed);
    return true;
  }, [client, scheduleTimers]);

  // Restore any persisted session on first load.
  useEffect(() => {
    let alive = true;
    void client.restore().then((s) => {
      if (!alive) return;
      if (s) {
        setSession(s);
        scheduleTimers(s);
      }
      setReady(true);
    });
    return () => {
      alive = false;
      clearTimers();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const login = useCallback(
    async (roles: readonly Role[], mfaCode: string) => {
      const s = await client.login(roles, mfaCode);
      setSession(s);
      setTimeoutWarning(false);
      scheduleTimers(s);
      auditClient.emit({ type: "auth.login", actorUserId: s.userId, actorRole: s.role });
    },
    [client, scheduleTimers],
  );

  /**
   * "Stay signed in" / user activity. 18.C1: against a live issuer this now buys a REAL token. The old
   * behaviour — extend the local clock by a full 30 minutes — is kept only for the dev client, which has no
   * issuer to ask and whose token is a fiction anyway.
   */
  const keepAlive = useCallback(() => {
    if (!session) return;
    if (client.renew) {
      void renewNow().then((ok) => {
        // A refused renewal means the session really is over; say so now rather than leaving the user
        // clicking "stay signed in" against a dead credential.
        if (!ok) void logout("timeout");
      });
      return;
    }
    const extended: Session = { ...session, expiresAt: Date.now() + SESSION_TTL };
    setSession(extended);
    setTimeoutWarning(false);
    scheduleTimers(extended);
  }, [session, client, renewNow, logout, scheduleTimers]);

  const can = useCallback((permission: Permission) => session?.permissions.has(permission) ?? false, [session]);

  const value = useMemo<AuthContextValue>(
    () => ({ session, ready, timeoutWarning, login, logout, keepAlive, can }),
    [session, ready, timeoutWarning, login, logout, keepAlive, can],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error("useAuth must be used within <AuthProvider>");
  return ctx;
}

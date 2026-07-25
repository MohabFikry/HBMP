import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { auditClient } from "../audit/auditClient";
import type { Permission, Role } from "../authz/permissions";
import { DevAuthClient, SESSION_TTL, type AuthClient, type Session } from "./authClient";

/** Idle warning fires this long before the session expires. */
const WARN_BEFORE_MS = 60 * 1000;

interface AuthContextValue {
  session: Session | null;
  ready: boolean;
  /** True in the warning window before expiry (drives the re-auth prompt). */
  timeoutWarning: boolean;
  login: (role: Role, mfaCode: string) => Promise<void>;
  logout: (reason?: "user" | "timeout") => Promise<void>;
  /** Refresh the idle timer (called on user activity + on "stay signed in"). */
  keepAlive: () => void;
  can: (permission: Permission) => boolean;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children, client = new DevAuthClient() }: { children: ReactNode; client?: AuthClient }) {
  const [session, setSession] = useState<Session | null>(null);
  const [ready, setReady] = useState(false);
  const [timeoutWarning, setTimeoutWarning] = useState(false);
  const warnTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const expireTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const clearTimers = () => {
    if (warnTimer.current) clearTimeout(warnTimer.current);
    if (expireTimer.current) clearTimeout(expireTimer.current);
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

  const scheduleTimers = useCallback(
    (s: Session) => {
      clearTimers();
      const now = Date.now();
      const warnIn = Math.max(0, s.expiresAt - WARN_BEFORE_MS - now);
      const expireIn = Math.max(0, s.expiresAt - now);
      warnTimer.current = setTimeout(() => setTimeoutWarning(true), warnIn);
      expireTimer.current = setTimeout(() => void logout("timeout"), expireIn);
    },
    [logout],
  );

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
    async (role: Role, mfaCode: string) => {
      const s = await client.login(role, mfaCode);
      setSession(s);
      setTimeoutWarning(false);
      scheduleTimers(s);
      auditClient.emit({ type: "auth.login", actorUserId: s.userId, actorRole: s.role });
    },
    [client, scheduleTimers],
  );

  const keepAlive = useCallback(() => {
    if (!session) return;
    // Extend to a fresh full window on explicit activity ("stay signed in").
    const extended: Session = { ...session, expiresAt: Date.now() + SESSION_TTL };
    setSession(extended);
    setTimeoutWarning(false);
    scheduleTimers(extended);
  }, [session, scheduleTimers]);

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

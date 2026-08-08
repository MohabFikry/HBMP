import { permissionsForRole, type Permission, type Role } from "../authz/permissions";

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
 * PKCE, MFA via the IdP, silent renew. The dev client below simulates the same shape so the portal shell,
 * routing, and session-timeout logic are identical regardless of backend availability.
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

const STORAGE_KEY = "mersal-session";
const SESSION_TTL_MS = 30 * 60 * 1000; // 30 min absolute — matches session-policy default tier.

const DISPLAY_NAMES: Record<Role, string> = {
  reception: "Reham (Reception)",
  doctor: "Dr. Karim",
  nurse: "Nurse Mona",
  lab: "Al-Shifa Lab",
  radiology: "Nile Radiology",
  procedure_provider: "Cairo Physiotherapy Centre",
  pharmacy: "Mersal Pharmacy",
  medical_approval: "Dr. Reviewer",
  beneficiary_mgmt: "Registration Desk",
  beneficiary_mgmt_supervisor: "Registration Supervisor",
  case_manager: "Case Manager Layla",
  call_center: "Call Agent Sara",
  claims_officer: "Claims Officer Tarek",
  finance: "Finance Officer",
  provider_admin: "Network Admin",
  policy_admin: "Policy Administrator",
  org_admin: "Org Admin",
  super_admin: "Super Admin",
  branch_coordinator: "Nadia (Maadi Coordinator)",
  clinics_manager: "Tarek (Clinics Manager)",
  medical_director: "Medical Director",
};

/**
 * Dev auth client — no live issuer required. Accepts any 6-digit MFA code (the *presence* of a code
 * models the step-up), persists the session to localStorage, and enforces the same expiry the real token
 * would carry. Swap for the OIDC client without touching AuthProvider or the router.
 */
export class DevAuthClient implements AuthClient {
  async login(role: Role, mfaCode: string): Promise<Session> {
    if (!/^\d{6}$/.test(mfaCode)) throw new Error("mfa-required");
    const session: Session = {
      userId: `dev-${role}`,
      displayName: DISPLAY_NAMES[role],
      role,
      permissions: permissionsForRole(role),
      mfaSatisfied: true,
      expiresAt: Date.now() + SESSION_TTL_MS,
    };
    persist(session);
    return session;
  }

  async logout(): Promise<void> {
    try {
      localStorage.removeItem(STORAGE_KEY);
    } catch {
      /* ignore */
    }
  }

  async restore(): Promise<Session | null> {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as { userId: string; displayName: string; role: Role; expiresAt: number };
      if (parsed.expiresAt <= Date.now()) {
        localStorage.removeItem(STORAGE_KEY);
        return null;
      }
      return {
        userId: parsed.userId,
        displayName: parsed.displayName,
        role: parsed.role,
        permissions: permissionsForRole(parsed.role),
        mfaSatisfied: true,
        expiresAt: parsed.expiresAt,
      };
    } catch {
      return null;
    }
  }
}

function persist(session: Session) {
  try {
    localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({
        userId: session.userId,
        displayName: session.displayName,
        role: session.role,
        expiresAt: session.expiresAt,
      }),
    );
  } catch {
    /* ignore */
  }
}

export const SESSION_TTL = SESSION_TTL_MS;

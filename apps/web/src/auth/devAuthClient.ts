import { permissionsForRole, type Role } from "../authz/permissions";
import { SESSION_TTL, type AuthClient, type Session } from "./authClient";

/**
 * The no-backend auth client — **fixture builds only**.
 *
 * It lived in `authClient.ts` next to the {@link AuthClient} interface and {@link Session} type, which the
 * live code imports. That single module meant the live bundle carried this class too: a `login()` that
 * accepts ANY six-digit code and mints a full permission set, and a `restore()` that reads a role out of
 * localStorage and trusts it. Neither is reachable in a live build — {@link AuthProvider} picks the OIDC
 * client — but shipping an unreachable "sign in as super_admin" to production is a thing you have to argue
 * is safe rather than a thing that is absent.
 *
 * It is now imported only through `src/dev/fixtures.ts`, which `vite.config.ts` swaps for a refusing stub
 * when the build is parameterised live. `tools/ci/check-live-bundle-clean.sh` reads the built JS and proves
 * it worked.
 */

const STORAGE_KEY = "mersal-session";

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
      expiresAt: Date.now() + SESSION_TTL,
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

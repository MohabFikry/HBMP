import { unionPermissions, type Role } from "../authz/permissions";
import { SESSION_TTL, type AuthClient, type Session } from "./authClient";
import { issuerRoleFor } from "../config";

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

export const DEV_SESSION_KEY = "mersal-session";
const STORAGE_KEY = DEV_SESSION_KEY;

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
  medical_director: "Yusra (Medical Director)",
};

/**
 * Dev auth client — no live issuer required. Accepts any 6-digit MFA code (the *presence* of a code
 * models the step-up), persists the session to localStorage, and enforces the same expiry the real token
 * would carry. Swap for the OIDC client without touching AuthProvider or the router.
 */
export class DevAuthClient implements AuthClient {
  /**
   * Takes a LIST because the portal picker has to be exercisable with no backend — the whole frontend suite
   * runs against this client, so a dev session that could only ever hold one portal would leave the picker,
   * the switcher and every multi-portal routing rule untestable outside a live issuer.
   *
   * The identity is named after the FIRST role, matching the live client's notion of a primary.
   */
  async login(roles: readonly Role[], mfaCode: string): Promise<Session> {
    if (!/^\d{6}$/.test(mfaCode)) throw new Error("mfa-required");
    if (roles.length === 0) throw new Error("role-required");
    const primary = roles[0];
    const session: Session = {
      userId: `dev-${primary}`,
      displayName: DISPLAY_NAMES[primary],
      role: primary,
      roles: [...roles],
      // The dev client signs in by PORTAL, so the issuer names are derived back from the portals with
      // `issuerRoleFor` — the canonical issuer name for each. That is the right approximation and it has a
      // limit worth stating: signing in as the "provider_admin" portal here always yields the issuer role
      // `provider_admin` (the first ROLE_MAP row), never `network_team`. A live token distinguishes them and
      // this fixture cannot, so a test about the difference has to seed `issuerRoles` explicitly.
      issuerRoles: roles.map(issuerRoleFor),
      permissions: unionPermissions(roles),
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
      const parsed = JSON.parse(raw) as {
        userId: string; displayName: string; role: Role; roles?: Role[];
        issuerRoles?: string[]; expiresAt: number;
      };
      if (parsed.expiresAt <= Date.now()) {
        localStorage.removeItem(STORAGE_KEY);
        return null;
      }
      // `roles` is optional on the way in: a session persisted by the previous build has only `role`, and a
      // developer reloading across the change should keep working rather than be silently signed out.
      const roles = parsed.roles?.length ? parsed.roles : [parsed.role];
      return {
        userId: parsed.userId,
        displayName: parsed.displayName,
        role: parsed.role,
        roles,
        // Same forward-compatibility rule as `roles`: a session persisted before 33.7 has no issuerRoles,
        // and re-deriving them beats signing the developer out.
        issuerRoles: parsed.issuerRoles?.length ? parsed.issuerRoles : roles.map(issuerRoleFor),
        permissions: unionPermissions(roles),
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
        roles: session.roles,
        issuerRoles: session.issuerRoles,
        expiresAt: session.expiresAt,
      }),
    );
  } catch {
    /* ignore */
  }
}

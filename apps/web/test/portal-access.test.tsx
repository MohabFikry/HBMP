import { describe, expect, it } from "vitest";
import { PORTALS, ALL_ROUTES, portalForRole } from "../src/portals/catalog";
import { screenFor } from "../src/screens/registry";
import { permissionsForRole, rolePermissions, type Role } from "../src/authz/permissions";
import { roleFromClaimRoles } from "../src/config";

/**
 * Portal-access audit (US-070/071). Proves every role's portal is reachable end-to-end: each declared section
 * is (a) permitted for its own role, (b) mounts a real screen, and (c) resolves through the router registry.
 * This is the regression guard for "every portal has access" — a new portal that forgets its permission, route,
 * or screen fails here instead of silently rendering a blank page or a 403 to its own users.
 */
describe("Portal access — every role reaches every one of its sections", () => {
  const ALL_ROLES = Object.keys(rolePermissions) as Role[];

  it("defines a portal for every role in the permission catalog (and vice versa)", () => {
    for (const role of ALL_ROLES) expect(() => portalForRole(role)).not.toThrow();
    for (const portal of PORTALS) expect(ALL_ROLES).toContain(portal.role);
  });

  it("grants each role the permission for every section in its own portal (no self-lockout)", () => {
    for (const portal of PORTALS) {
      const perms = permissionsForRole(portal.role);
      for (const section of portal.sections) {
        expect(
          perms.has(section.permission),
          `${portal.role} is missing permission ${section.permission} for its own section ${section.key}`,
        ).toBe(true);
      }
    }
  });

  it("mounts a real screen for every declared section route (no dangling nav item)", () => {
    for (const { fullPath } of ALL_ROUTES) {
      expect(screenFor(fullPath), `no screen resolves for ${fullPath}`).toBeTypeOf("function");
    }
  });

  it("includes the Claims and Call-Centre portals with their wired routes", () => {
    const bases = PORTALS.map((p) => p.base);
    expect(bases).toContain("claims");
    expect(bases).toContain("call-centre");
    for (const path of ["/claims/worklist", "/claims/reconciliation", "/claims/insights"]) {
      expect(screenFor(path)).toBeTypeOf("function");
    }
  });

  it("keeps Claims minimum-necessary — no clinical/diagnosis permission leaks into the claims role", () => {
    const claims = permissionsForRole("claims_officer");
    for (const forbidden of ["emr.read", "emr.write", "results.inbox", "prescriptions.write"] as const) {
      expect(claims.has(forbidden as never)).toBe(false);
    }
  });

  it("maps the call-centre and claims issuer roles to their portals", () => {
    expect(roleFromClaimRoles(["call_center"])).toBe("call_center");
    expect(roleFromClaimRoles(["claims_officer"])).toBe("claims_officer");
  });

  it("FAILS CLOSED (H6): an authenticated caller with no mapped role gets NO portal (never reception)", () => {
    expect(roleFromClaimRoles([])).toBeNull();
    expect(roleFromClaimRoles(["default-roles-mersal", "offline_access", "some_unmapped_role"])).toBeNull();
  });
});

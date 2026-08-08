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

/**
 * Title Case for section names — `Register New`, not `Register new` (09 §8). These strings are the
 * highest-leverage copy in the app: one catalog entry becomes the nav-rail item, the page `<h1>`, the command
 * palette row AND the browser-tab title, so a mis-cased label is visibly wrong in four places at once — which
 * is how `Call Centre` sat beside `Provider network` for a whole phase before anyone noticed.
 *
 * Two exceptions, both deliberate: short function words stay lowercase mid-title ("Book an Appointment"), and
 * a hyphenated compound capitalises only its head ("Check-in"), because "Check-In" reads as two words.
 */
describe("Catalog copy — Title Case (09 §8)", () => {
  /** Words that stay lowercase when they are not the first word of a title. */
  const SMALL = new Set(["a", "an", "the", "and", "or", "of", "for", "to", "in", "on", "at", "by", "with"]);
  const entries = PORTALS.flatMap((p) => [
    { where: `${p.role}.title`, text: p.title.en },
    { where: `${p.role}.eyebrow`, text: p.eyebrow.en },
    ...p.sections.flatMap((s) => [
      { where: `${p.role}.${s.key}.label`, text: s.label.en },
      { where: `${p.role}.${s.key}.group`, text: s.group.en },
    ]),
  ]);

  it("starts every EN label with a capital", () => {
    const bad = entries.filter((e) => e.text[0] !== e.text[0].toUpperCase());
    expect(bad.map((e) => `${e.where}: ${e.text}`)).toEqual([]);
  });

  it("capitalises every word except short function words ('&' and '/' segments included)", () => {
    const bad = entries.filter((e) =>
      e.text
        .split(/\s+/)
        .filter((w) => /[A-Za-z]/.test(w))
        .some((w, i) => {
          if (i > 0 && SMALL.has(w.toLowerCase())) return false;  // "Book an Appointment"
          const head = w.replace(/^[^A-Za-z]+/, "")[0];           // skip a leading "(" or "—"
          return head !== head.toUpperCase();
        }),
    );
    expect(bad.map((e) => `${e.where}: ${e.text}`)).toEqual([]);
  });

  it("gives every EN label an Arabic counterpart (no untranslated shell copy)", () => {
    for (const portal of PORTALS) {
      expect(portal.title.ar.trim()).not.toBe("");
      expect(portal.eyebrow.ar.trim()).not.toBe("");
      for (const section of portal.sections) {
        expect(section.label.ar.trim()).not.toBe("");
        expect(section.group.ar.trim()).not.toBe("");
      }
    }
  });
});

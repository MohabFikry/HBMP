import { describe, expect, it } from "vitest";
import { PORTALS } from "../src/portals/catalog";
import { rolePermissions } from "../src/authz/permissions";
import { screenFor } from "../src/screens/registry";

/**
 * The 19.7 navigation rework, pinned.
 *
 * Three of these assert an ABSENCE, which is the half that rots quietly: a section can be deleted from the
 * catalog and left mounted in the route registry, and the only symptom is that a withdrawn screen still
 * answers when someone types its URL.
 */

const officer = PORTALS.find((p) => p.role === "beneficiary_mgmt")!;
const supervisor = PORTALS.find((p) => p.role === "beneficiary_mgmt_supervisor")!;

/**
 * Notifications is appended to EVERY portal after the literals (a cross-cutting self-service inbox), and its
 * `notification.read` is granted through a different path from the portal role table. These assertions are
 * about the sections this rework arranged, so it is excluded rather than special-cased in five places.
 */
const authored = (portal: typeof officer) => portal.sections.filter((s) => s.key !== "notifications");

describe("The beneficiary portals", () => {
  it("land on Beneficiaries, because the landing page IS the first section", () => {
    // AppShell derives home from `accessible[0]`. There is no separate default-page setting, deliberately —
    // a default configured apart from the menu is one that drifts from the menu.
    for (const portal of [officer, supervisor]) {
      expect(portal.sections[0]!.key).toBe("members");
      expect(portal.sections[0]!.label.en).toBe("Beneficiaries");
      expect(portal.sections[0]!.label.ar).toBe("المستفيدون");
    }
  });

  it("orders the groups membership → registration → patient access → insights", () => {
    // The rail groups CONSECUTIVE runs, so an out-of-order entry renders a second heading with the same
    // name. The order of first appearance is therefore the group order (QA P1-9).
    const groupsInOrder = (portal: typeof officer) => {
      const seen: string[] = [];
      for (const s of authored(portal)) if (seen[seen.length - 1] !== s.group.en) seen.push(s.group.en);
      return seen;
    };
    expect(groupsInOrder(officer)).toEqual(["Membership", "Registration", "Patient Access", "Insights"]);
    // Each group appears exactly once — a repeat is the bug the ordering rule exists to prevent.
    expect(new Set(groupsInOrder(officer)).size).toBe(groupsInOrder(officer).length);
  });

  it("gives the supervisor everything the officer has, plus the decision", () => {
    // It used to be a strict SUBSET, which left the supervisor unable to open the bulk import or the
    // analytics they are asked about. Separation of duties is now enforced by patient-service refusing a
    // decision on a registration the actor filed (urn:hbmp:self-approval), not by hiding menu items.
    const officerKeys = authored(officer).map((s) => s.key);
    const supervisorKeys = authored(supervisor).map((s) => s.key);
    for (const key of officerKeys) expect(supervisorKeys).toContain(key);
    expect(supervisorKeys).toContain("approvals");
  });

  it("backs every section with a permission the role actually holds", () => {
    for (const portal of [officer, supervisor]) {
      const held = new Set(rolePermissions[portal.role]);
      for (const section of authored(portal)) {
        expect(held.has(section.permission), `${portal.role} → ${section.key}`).toBe(true);
      }
    }
  });

  it("grants no permission with nothing behind it", () => {
    // A permission granted to a role that gates no section reads as access existing somewhere it does not.
    for (const portal of [officer, supervisor]) {
      const gated = new Set(authored(portal).map((s) => s.permission));
      for (const permission of rolePermissions[portal.role]) {
        expect(gated.has(permission), `${portal.role} holds ${permission} but nothing uses it`).toBe(true);
      }
    }
  });
});

describe("The retired sections", () => {
  it.each(["manage", "status", "utilization"])("drops %s from both portals", (key) => {
    expect(authored(officer).map((s) => s.key)).not.toContain(key);
    expect(authored(supervisor).map((s) => s.key)).not.toContain(key);
  });

  it.each([
    "/beneficiaries/manage",
    "/beneficiaries/status",
    "/beneficiaries/utilization",
  ])("unmounts %s so it cannot be reached by typing it", (path) => {
    // A path with no catalog section falls through to AppRouter's DEEP-LINK branch, which resolves it from
    // the screen registry and gates it on `profile.read` alone. Leaving these mounted would have kept three
    // withdrawn screens reachable by URL.
    expect(screenFor(path)).toBeUndefined();
  });

  it("keeps utilization where policy administration still opens it", () => {
    // Retired from the beneficiary portal, not from the product: it is a tab in Analytics for beneficiary
    // management, and still its own screen for policy admin.
    expect(screenFor("/policy/utilization")).toBeDefined();
  });
});

import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { axe } from "jest-axe";
import { AppProviders } from "../src/App";
import { AppRouter } from "../src/routing/AppRouter";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { PORTALS, ZONES, portalsForRoles } from "../src/portals/catalog";
import { firstNameOf } from "../src/portals/PortalPicker";
import { ROLE_MAP, issuerRoleFor, rolesFromClaimRoles } from "../src/config";
import { permissionsForRole, unionPermissions } from "../src/authz/permissions";
import { seedSession } from "./helpers";

/**
 * Phase 28.x — a session that can hold several portals, the picker that chooses between them, and the
 * switcher that goes back.
 *
 * ============================================================================================================
 * WHAT THESE PROVE
 * ============================================================================================================
 * The picker is the visible half of a change to the SESSION, and the session half is where the risk is. So
 * these assert the invariants rather than the pixels:
 *   * a single-portal caller's experience is UNCHANGED — no picker, no switcher, same landing page;
 *   * only entitled portals are offered, and the counts are true for the person reading them;
 *   * a portal path the caller does not hold is still refused;
 *   * the catalogue and the issuer's role vocabulary agree, which is what stops a grant silently failing.
 */

afterEach(cleanup);

function renderAt(path: string, role: Parameters<typeof seedSession>[0], extra: Parameters<typeof seedSession>[1] = []) {
  seedSession(role, extra);
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <MemoryRouter initialEntries={[path]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <AppRouter />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("the portal model", () => {
  it("keeps a single-role session behaving exactly as it did", () => {
    // The whole compatibility claim in one assertion: for one role, the primary is that role and the
    // permission set is the same object the old single-role session carried.
    for (const portal of PORTALS) {
      expect(portalsForRoles([portal.role])).toEqual([portal]);
      expect([...unionPermissions([portal.role])].sort()).toEqual([...permissionsForRole(portal.role)].sort());
    }
  });

  it("gives a caller holding several roles every one of their portals", () => {
    const mine = portalsForRoles(["doctor", "org_admin", "lab"]);
    expect(mine.map((p) => p.base).sort()).toEqual(["admin", "clinician", "lab"]);
  });

  it("offers the CLINIC portal once to somebody holding both branch roles, as the wider one", () => {
    // Two roles over one base. Keying on role would offer the same workspace twice under two eyebrows, both
    // going to the same URL — and resolving to the coordinator would narrow a manager to one clinic, which
    // is the exact narrowing ROLE_MAP orders itself to avoid.
    const both = portalsForRoles(["branch_coordinator", "clinics_manager"]);
    const branch = both.filter((p) => p.base === "branch");
    expect(branch).toHaveLength(1);
    expect(branch[0].role).toBe("clinics_manager");
  });

  it("reads every portal role the token names, not just the first", () => {
    const claim = ["doctor", "org_admin", "lab_tech"];
    expect(rolesFromClaimRoles(claim).sort()).toEqual(["doctor", "lab", "org_admin"]);
  });

  it("translates every catalogue role back into a name the issuer knows", () => {
    // The failure this prevents is silent and total: the admin screen POSTs portal keys, identity-service
    // answers 422 for every clinical role, and the tick looks as though it worked until the save.
    const issuerNames = new Set(ROLE_MAP.map(([name]) => name));
    for (const portal of PORTALS) {
      expect(issuerNames.has(issuerRoleFor(portal.role))).toBe(true);
    }
  });

  it("gives every portal a zone the picker renders and a description that is not its own title", () => {
    const zones = new Set(ZONES.map((z) => z.key));
    for (const portal of PORTALS) {
      expect(zones.has(portal.zone)).toBe(true);
      for (const lang of ["en", "ar"] as const) {
        expect(portal.description[lang].length).toBeGreaterThan(20);
        expect(portal.description[lang]).not.toBe(portal.title[lang]);
      }
    }
  });

  it("greets somebody by their name and not by their title", () => {
    expect(firstNameOf("Dr. Karim")).toBe("Karim");
    expect(firstNameOf("Nurse Mona")).toBe("Mona");
    expect(firstNameOf("Reham (Reception)")).toBe("Reham");
    expect(firstNameOf("Tarek")).toBe("Tarek");
    // A name made of nothing but an honorific is shown intact — an empty greeting is worse than an odd one.
    expect(firstNameOf("Dr.")).toBe("Dr.");
    expect(firstNameOf(undefined)).toBe("");
  });
});

describe("the picker", () => {
  it("is skipped entirely by somebody who holds one portal", async () => {
    renderAt("/portals", "reception");
    // Redirected into the portal rather than shown a page with one card: a choice with one answer is not a
    // choice, and the click costs every single-portal user in the system.
    await waitFor(() => expect(screen.queryByRole("heading", { name: /welcome back/i })).toBeNull());
    expect(await screen.findByRole("navigation")).toBeInTheDocument();
  });

  it("offers exactly the portals the caller holds, grouped by zone", async () => {
    renderAt("/portals", "doctor", ["org_admin", "pharmacy"]);

    await screen.findByRole("heading", { name: /welcome back/i });

    // The three held portals are present…
    expect(screen.getByRole("button", { name: /Consultation/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Administration/ })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /Pharmacy/ })).toBeInTheDocument();
    // …and one they do not hold is not, which is the only thing this page must never get wrong.
    expect(screen.queryByRole("button", { name: /Laboratory/ })).toBeNull();

    // Zones with nothing in them are not rendered: an empty "Fulfillment" heading tells a finance officer
    // only that portals they cannot have exist somewhere.
    expect(screen.getByRole("heading", { name: /Clinical & approvals/i })).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: /Operations & administration/i })).toBeInTheDocument();
  });

  it("counts the sections the CALLER can open, not the catalogue total", async () => {
    renderAt("/portals", "doctor", ["org_admin"]);
    const card = await screen.findByRole("button", { name: /Consultation/ });
    // A number that is true for somebody else is worse than no number — it is a promise the portal does not
    // keep. The doctor portal's count must match what their own permissions actually unlock.
    const doctorPortal = PORTALS.find((p) => p.role === "doctor")!;
    const perms = unionPermissions(["doctor", "org_admin"]);
    const openable = doctorPortal.sections.filter((s) => perms.has(s.permission)).length;
    expect(within(card).getByText(new RegExp(`\\b${openable} sections?\\b`))).toBeInTheDocument();
  });

  it("opens the portal it was clicked on", async () => {
    const user = userEvent.setup();
    renderAt("/portals", "doctor", ["org_admin"]);
    await user.click(await screen.findByRole("button", { name: /Administration/ }));
    // Landing INSIDE the portal — the rail is the proof, since the picker renders outside the shell.
    expect(await screen.findByRole("navigation")).toBeInTheDocument();
  });

  it("has no serious accessibility violations", async () => {
    const { container } = renderAt("/portals", "doctor", ["org_admin", "pharmacy"]);
    await screen.findByRole("heading", { name: /welcome back/i });
    const results = await axe(container);
    const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
    expect(serious).toEqual([]);
  });
});

describe("the in-app switcher", () => {
  it("appears in every portal a multi-portal caller opens, identically", async () => {
    // ONE component, so the accessible name is the same sentence in each portal with only the portal's own
    // name substituted. Asserted across two portals because "identical in every portal" is the requirement,
    // and a per-portal copy would pass a single-portal test.
    for (const [path, name] of [
      ["/clinician", "Consultation"],
      ["/admin", "Administration"],
    ] as const) {
      renderAt(path, "doctor", ["org_admin"]);
      const control = await screen.findByRole("button", { name: /Current portal:/ });
      expect(control).toHaveAccessibleName(new RegExp(`Current portal: ${name}\\..*Change portal`));
      cleanup();
    }
  });

  it("is absent for somebody with nowhere to switch to", async () => {
    renderAt("/reception", "reception");
    await screen.findByRole("navigation");
    // A control that says "Change portal" and leads to a screen with one card is a promise made to the
    // majority of users, who hold exactly one portal and have nowhere else to go.
    expect(screen.queryByRole("button", { name: /Current portal:/ })).toBeNull();
  });

  it("returns to the picker", async () => {
    const user = userEvent.setup();
    renderAt("/clinician", "doctor", ["org_admin"]);
    await user.click(await screen.findByRole("button", { name: /Current portal:/ }));
    expect(await screen.findByRole("heading", { name: /welcome back/i })).toBeInTheDocument();
  });
});

describe("routing", () => {
  it("opens a deep link into any portal the caller holds", async () => {
    // The bug this closes: `/admin/access` resolved against the caller's PRIMARY portal, found no matching
    // section, and answered 404 for a screen they had been granted.
    renderAt("/admin/access", "doctor", ["org_admin"]);
    expect(await screen.findByRole("navigation")).toBeInTheDocument();
    expect(screen.queryByText(/not found/i)).toBeNull();
  });

  it("still refuses a portal the caller does not hold", async () => {
    renderAt("/finance/settlements", "doctor", ["org_admin"]);
    // Unchanged behaviour, and the assertion that matters most here: widening the session to several
    // portals must not widen it to ALL of them.
    await waitFor(() => expect(screen.queryByRole("navigation")).not.toBeNull());
    expect(screen.queryByText(/Provider Settlements/)).toBeNull();
  });
});

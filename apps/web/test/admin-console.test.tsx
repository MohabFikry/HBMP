import { afterEach, describe, expect, it } from "vitest";
import { cleanup, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { canonicaliseConfigValue } from "@mersal/contracts";
import { DevApiClient } from "../src/api/DevApiClient";
import { AdminConfig, AdminMasterData } from "../src/screens/AdminConsole";
import { PORTALS } from "../src/portals/catalog";
import { rolePermissions } from "../src/authz/permissions";
import { renderNode, seedSession } from "./helpers";

/**
 * Phase 28.10 — the org-admin console audit.
 *
 * ============================================================================================================
 * WHAT THESE PROVE
 * ============================================================================================================
 * The theme of the audit was surfaces that LOOK finished and are not: a nav item that could only ever 403, a
 * table the server can fill with five hundred rows and the screen drew as one list, and a set of values the
 * product displayed but gave nobody a way to change. Each of those failed silently — nothing threw, nothing
 * logged, and the screen looked correct.
 *
 * So these assert the absences as well as the presences: that the section a caller cannot use is GONE, and
 * that the write nobody could reach now has a control that reaches it.
 */

afterEach(cleanup);

function renderConfig() {
  seedSession("org_admin");
  return renderNode(<AdminConfig />, new DevApiClient({ latencyMs: 0 }));
}

// ── The section that could only ever refuse ───────────────────────────────────────────────────────────────

describe("the org-admin portal offers nothing it cannot do", () => {
  it("does not link the tenant registry, whose read and write are both Super Admin only", async () => {
    // `AdminPolicies.ManageTenant` names `super_admin` and nobody else, so this section answered 403 for
    // every org admin who ever clicked it — not intermittently, by construction. A nav item that cannot
    // succeed teaches its reader that the platform is broken rather than that the power is not theirs.
    const orgAdmin = PORTALS.find((p) => p.role === "org_admin")!;
    expect(orgAdmin.sections.map((s) => s.key)).not.toContain("tenants");
    expect(rolePermissions.org_admin).not.toContain("admin.tenants");
  });

  it("keeps it on the platform portal, where it IS held", async () => {
    // The fix is removing it from the caller who cannot use it, not removing the feature.
    const superAdmin = PORTALS.find((p) => p.role === "super_admin")!;
    expect(superAdmin.sections.map((s) => s.key)).toContain("tenants");
    expect(rolePermissions.super_admin).toContain("admin.tenants");
  });
});

// ── Values that were displayed and could not be changed ───────────────────────────────────────────────────

describe("system configuration is editable", () => {
  it("writes a new value and shows it, rather than only rendering what exists", async () => {
    const user = userEvent.setup();
    renderConfig();

    const row = (await screen.findByText("approvals.sla_hours")).closest("tr")!;
    expect(within(row).getByText("24")).toBeInTheDocument();

    await user.click(within(row).getByRole("button", { name: /change/i }));
    const value = await screen.findByLabelText(/^value$/i);
    await user.clear(value);
    await user.type(value, "48");
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    await waitFor(() => {
      const after = screen.getByText("approvals.sla_hours").closest("tr")!;
      expect(within(after).getByText("48")).toBeInTheDocument();
    });
  });

  it("announces the save, because the visible change is one cell of one row", async () => {
    const user = userEvent.setup();
    renderConfig();

    const row = (await screen.findByText("approvals.sla_hours")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: /change/i }));
    const value = await screen.findByLabelText(/^value$/i);
    await user.clear(value);
    await user.type(value, "36");
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    expect(await screen.findByText(/in force/i)).toBeInTheDocument();
  });

  it("refuses a value that does not parse as its type, before spending a round trip on it", async () => {
    const user = userEvent.setup();
    renderConfig();

    const row = (await screen.findByText("approvals.sla_hours")).closest("tr")!;
    await user.click(within(row).getByRole("button", { name: /change/i }));
    const value = await screen.findByLabelText(/^value$/i);
    await user.clear(value);
    await user.type(value, "soon");
    await user.click(screen.getByRole("button", { name: /^save$/i }));

    // Named for the type, not "invalid": the administrator has to know WHICH shape is wanted. Read off the
    // `role="alert"` node rather than by text — `Field` renders the help line and the error together, and
    // both name the type, so a bare text query would match the hint that was there before anything failed.
    const alerts = await screen.findAllByRole("alert");
    expect(alerts.some((a) => /whole number/i.test(a.textContent ?? ""))).toBe(true);
    // And the row is untouched — a refusal that had already written would be worse than no validation.
    const after = screen.getByText("approvals.sla_hours").closest("tr")!;
    expect(within(after).getByText("24")).toBeInTheDocument();
  });

  it("shows the ORGANISATION a setting applies to, never a sliced uuid", async () => {
    renderConfig();
    const row = (await screen.findByText("approvals.sla_hours")).closest("tr")!;
    // The fixture's tenant is a uuid, so the honest label is the scope rather than eight hex characters —
    // which could not be copied anywhere, are not guaranteed distinct, and read as a truncation bug.
    expect(within(row).getByText(/this organisation/i)).toBeInTheDocument();
    expect(row.textContent).not.toMatch(/[0-9a-f]{8}-[0-9a-f]{4}/i);
  });
});

/**
 * The canonicaliser, as a unit.
 *
 * <p>It is a second implementation of a server rule (`ConfigValidation.Validate`), which is normally the
 * wrong thing to build — so the cases that justify it are the ones pinned here. `Duration` above all: .NET's
 * `TimeSpan` reads a bare number as DAYS, so a session timeout typed as the obvious "15" is a fortnight, and
 * neither the old screen nor the server would have said so.</p>
 */
describe("config values are canonicalised the way the server would", () => {
  it("reads a bare number as a duration in DAYS and accepts it as such", () => {
    expect(canonicaliseConfigValue("Duration", "15")).toBe("15");
    expect(canonicaliseConfigValue("Duration", "0.00:15:00")).toBe("0.00:15:00");
    expect(canonicaliseConfigValue("Duration", "15 minutes")).toBeNull();
  });

  it("normalises rather than merely accepting, so the screen shows what will be stored", () => {
    expect(canonicaliseConfigValue("Boolean", "TRUE")).toBe("true");
    expect(canonicaliseConfigValue("Number", "1.50")).toBe("1.5");
    expect(canonicaliseConfigValue("Whole", "+007")).toBe("7");
  });

  it("refuses the values `bool.TryParse` refuses, not the ones a looser check would allow", () => {
    // "1" and "yes" are the two an over-helpful implementation would accept and the server would reject.
    expect(canonicaliseConfigValue("Boolean", "1")).toBeNull();
    expect(canonicaliseConfigValue("Boolean", "yes")).toBeNull();
  });

  it("treats an empty value as no value, for every type", () => {
    for (const type of ["Text", "Whole", "Number", "Boolean", "Duration"]) {
      expect(canonicaliseConfigValue(type, "   ")).toBeNull();
    }
  });
});

// ── A table the server can fill with five hundred rows ────────────────────────────────────────────────────

describe("master data is searchable", () => {
  it("carries the standard toolbar rather than drawing every in-force version as one list", async () => {
    seedSession("org_admin");
    renderNode(<AdminMasterData />, new DevApiClient({ latencyMs: 0 }));
    await screen.findByText("E11.9");
    expect(screen.getByRole("searchbox")).toBeInTheDocument();
  });

  it("says who owns the edit, so a read-only screen reads as deliberate rather than unfinished", async () => {
    seedSession("org_admin");
    renderNode(<AdminMasterData />, new DevApiClient({ latencyMs: 0 }));
    // `AdminPolicies.EditMasterData` is the Medical Director and the platform admin. An org admin reads
    // these codes and does not own them — which is correct, and was previously indistinguishable from a
    // screen whose buttons had not been built yet.
    expect(await screen.findByText(/clinical governance/i)).toBeInTheDocument();
  });

  it("states the good state instead of a dash a reader has to interpret", async () => {
    seedSession("org_admin");
    renderNode(<AdminMasterData />, new DevApiClient({ latencyMs: 0 }));
    const row = (await screen.findByText("E11.9")).closest("tr")!;
    expect(within(row).getByText(/in force/i)).toBeInTheDocument();
  });
});

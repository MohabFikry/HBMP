import { describe, expect, it } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { branchApi, rosterApi, availabilityApi } from "../src/api/branchApi";
import { BranchRoster } from "../src/screens/BranchRoster";
import { BranchLicenceAlerts, BranchPractitioners } from "../src/screens/BranchLicences";
import { BranchInventory } from "../src/screens/BranchInventory";
import { BranchesOverview } from "../src/screens/BranchesOverview";

/**
 * The Clinic Management portal's screens, RENDERED.
 *
 * <b>This file could not exist before the fixture seam.</b> `branchApi` and its siblings called `http.ts`
 * directly — the only surface on the platform that did — while every other portal resolves through
 * `ApiClient`, which `ApiProvider` swaps for `DevApiClient`. The SPA runs in fixture mode by default and
 * there is no MSW in the tree, so rendering any of these five screens in a test produced a network error,
 * and rendering them in the demo bundle produced the same. That is why this portal's only coverage was a
 * status chip tested in isolation, and why the axe route sweep skipped every one of these routes while
 * reporting itself complete.
 *
 * The assertions below are deliberately about what the screens SAY rather than about markup: the defects
 * this pass fixed were all of the form "the screen states something that is not true of the data", and a
 * test asserting class names would have passed throughout.
 */
/**
 * The app's real provider stack, not a bare ThemeProvider: these screens use `PageHeader`, which reads the
 * session through `useAuth`. Rendering them without it throws before a single assertion runs — and the throw
 * is about auth, which sends you looking in the wrong place entirely.
 *
 * `apiClient` is left to the default, which in fixture mode is `DevApiClient`. The branch surfaces do NOT
 * come from there — they resolve through `@dev/fixtures` at module load, which is the seam under test.
 */
const wrap = (ui: React.ReactNode) =>
  render(
    <AppProviders authClient={new DevAuthClient()}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>{ui}</MemoryRouter>
    </AppProviders>,
  );

describe("the branch API resolves through the fixture seam", () => {
  it("serves the demo clinic instead of attempting a network call", async () => {
    // The seam itself. If this regresses to the HTTP implementation the call rejects rather than resolving,
    // and every test below fails with a network error rather than an assertion.
    const rows = await branchApi.practitioners({ includeUnlicensed: true });
    expect(rows.length).toBeGreaterThan(0);
  });

  it("gives the six clinics NAMES, which the overview needs and the ids cannot supply", async () => {
    const branches = await branchApi.branches();
    expect(branches).toHaveLength(6);
    expect(branches.map((b) => b.nameEn)).toContain("Maadi");
    expect(branches.map((b) => b.nameAr)).toContain("المعادي");
  });

  it("covers all four licence states, because the states needing action are the point", async () => {
    const rows = await branchApi.practitioners({ includeUnlicensed: true });

    expect(rows.some((p) => p.licenceValid === true && (p.daysUntilExpiry ?? 0) > 90)).toBe(true);
    expect(rows.some((p) => p.licenceValid === true && (p.daysUntilExpiry ?? 999) <= 90)).toBe(true);
    expect(rows.some((p) => p.licenceValid === false)).toBe(true);
    // Never recorded is NOT expired — a nurse who never had a licence must not sit in the renewal queue.
    expect(rows.some((p) => p.licenceValid === null && p.licenseExpiry === null)).toBe(true);
  });

  it("hides unlicensed clinicians unless they are asked for", async () => {
    // The booking picker's behaviour and the coordinator's are opposites, and the flag is what separates them.
    const forThePicker = await branchApi.practitioners();
    const forTheCoordinator = await branchApi.practitioners({ includeUnlicensed: true });

    expect(forThePicker.some((p) => p.licenceValid === false)).toBe(false);
    expect(forTheCoordinator.some((p) => p.licenceValid === false)).toBe(true);
  });
});

describe("Practitioners screen", () => {
  it("renders the clinic's clinicians with their licence state", async () => {
    wrap(<BranchPractitioners />);

    await waitFor(() => expect(screen.getByText("Hala Fouad")).toBeInTheDocument());
    // The expired one must be VISIBLE here — this is the screen whose purpose is finding records needing
    // action, so hiding them the way the picker does would empty it of its reason to exist.
    expect(screen.getByText("Nadia Rashed")).toBeInTheDocument();
  });

  it("says 'not shown to you' rather than leaving a masked licence blank", async () => {
    // A blank cell makes "withheld from you" and "none recorded" look identical, and they call for opposite
    // actions. The fixture returns a real number, so this asserts the screen renders the value it was given
    // rather than the mask — the mask's own copy is asserted by the licence-cues suite.
    wrap(<BranchPractitioners />);
    await waitFor(() => expect(screen.getByText("EG-DOC-44182")).toBeInTheDocument());
  });
});

describe("Licence Alerts screen", () => {
  it("lists the licences that need chasing, and the appointments a lapse stranded", async () => {
    wrap(<BranchLicenceAlerts />);

    // Two tables, one screen: who to chase, and who to ring today. Splitting them across nav items would let
    // somebody act on the first and never discover the second.
    await waitFor(() => expect(screen.getByText("Karim Adel")).toBeInTheDocument());
    expect(screen.getByText("Nadia Rashed")).toBeInTheDocument();
    expect(screen.getByText("Amal Hassan")).toBeInTheDocument();
  });

  it("says the stranded appointments are STILL BOOKED, in words", async () => {
    // The single most important thing that table communicates: the system cancelled nobody, and the patient
    // is still expecting to come.
    wrap(<BranchLicenceAlerts />);
    await waitFor(() => expect(screen.getAllByText("Still booked").length).toBeGreaterThan(0));
  });
});

describe("roster exceptions", () => {
  it("previews the impact of an exception before it is applied", async () => {
    const impact = await rosterApi.preview({
      kind: "ClinicClosed", dateFrom: "2026-09-14", dateTo: "2026-09-14", reason: "burst pipe",
    });

    expect(impact.affectedCount).toBeGreaterThan(0);
    // The LIST, not just the count. "8 appointments" is a number; the list is what lets a coordinator
    // recognise the two who cannot easily travel again.
    expect(impact.affected.map((a) => a.beneficiaryName)).toContain("Amal Hassan");
  });

  it("narrows the preview when the exception names ONE practitioner", async () => {
    // C1 — the roster form could not name a practitioner at all, so every exception closed the whole clinic.
    // "Dr Hala is on leave next Tuesday", design 42 §4's own motivating example, was unbuildable.
    const wholeClinic = await rosterApi.preview({
      kind: "ClinicClosed", dateFrom: "2026-09-14", dateTo: "2026-09-14", reason: "holiday",
    });
    const oneDoctor = await rosterApi.preview({
      kind: "Leave", dateFrom: "2026-09-14", dateTo: "2026-09-14", reason: "leave",
      practitionerId: "d0000000-0000-4000-8000-000000000002",
    });

    expect(oneDoctor.affectedCount).toBeLessThan(wholeClinic.affectedCount);
  });

  it("flags and never cancels", async () => {
    const applied = await rosterApi.apply({
      kind: "Leave", dateFrom: "2026-09-20", dateTo: "2026-09-20", reason: "annual leave",
      practitionerId: "d0000000-0000-4000-8000-000000000001",
      acknowledgedImpactCount: 3,
    });

    expect(applied.flagged).toBe(3);
    expect(applied.cancelled).toBe(0);
  });

  it("withdraws an exception, restoring the days it removed", async () => {
    // B1 — this posted to /roster-exceptions/{id}/withdraw, a route no service has ever registered. Nothing
    // called it either, so the broken client and the unreachable action hid each other.
    const before = await rosterApi.list();
    // The LAST one — the exception the case above applied. The fixture store is shared across this file
    // (deliberately: it is what makes the demo behave like an application), so withdrawing the first would
    // delete the seeded holiday another case renders.
    const target = before[before.length - 1].exceptionId;

    const result = await rosterApi.withdraw(target);
    expect(result.withdrawn).toBe(true);

    const after = await rosterApi.list();
    expect(after.map((e) => e.exceptionId)).not.toContain(target);
  });

  it("renders the exceptions the clinic already has, behind the header button", async () => {
    // 33.10 — the exceptions table and its nine-field form used to occupy two thirds of the screen while
    // being MAINTENANCE, not something anyone opens the roster to read. Both moved into a dialog. The count
    // on the button is what the table used to say by being visible, so it is asserted with the content.
    const user = userEvent.setup();
    wrap(<BranchRoster />);

    // The count is what the table used to say by being visible, so it is asserted with the content — against
    // the live list rather than a literal, because the tests above this one withdraw and apply.
    const expected = (await rosterApi.list()).length;
    const trigger = await screen.findByRole("button", { name: /Exceptions/ });
    await waitFor(() => expect(trigger).toHaveTextContent(String(expected)));

    await user.click(trigger);
    const dialog = await screen.findByRole("dialog");
    await waitFor(() => expect(within(dialog).getByText("Eid al-Adha")).toBeInTheDocument());
  });
});

describe("the weekly pattern", () => {
  it("is readable at all, which it was not before", async () => {
    // A2 — provider_availability had no GET anywhere on the platform. The roster screen opened by describing
    // "the weekly pattern" and had no endpoint to fetch one.
    const rules = await availabilityApi.list();
    expect(rules.length).toBeGreaterThan(0);
  });

  it("reports BOTH the window's slot count and the capped one", async () => {
    const rules = await availabilityApi.list();
    const capped = rules.find((r) => r.maxPerDay !== null);
    const uncapped = rules.find((r) => r.maxPerDay === null);

    // "24 slots, capped at 20" is the sentence a coordinator is reading. One number alone either hides the
    // cap or makes the session look shorter than it is.
    expect(capped).toBeDefined();
    expect(capped!.slotsPerDay).toBeLessThan(capped!.slotsFromWindow);

    // An uncapped rule — every rule that predates the cap — must read as offering its whole window.
    expect(uncapped).toBeDefined();
    expect(uncapped!.slotsPerDay).toBe(uncapped!.slotsFromWindow);
  });

  it("recomputes the counts when the window or the cap changes", async () => {
    const [rule] = await availabilityApi.list();
    const widened = await availabilityApi.update(rule.availabilityId, {
      providerId: rule.providerId, locationId: rule.locationId,
      doctorId: rule.doctorId ?? undefined, branchId: rule.branchId ?? undefined,
      dayOfWeek: rule.dayOfWeek, startTime: "09:00", endTime: "17:00", slotMinutes: 15,
      maxPerDay: null,
    });

    expect(widened.slotsFromWindow).toBe(32);
    expect(widened.slotsPerDay).toBe(32);
  });

  it("keeps a timeline of who changed the pattern", async () => {
    // A4 — the only change history was the hash-chained audit store behind audit:read, which branch roles do
    // not hold. The person who runs the clinic could not ask who narrowed their Tuesday.
    const { entries } = await availabilityApi.history("a0000000-0000-4000-8000-000000000001");

    expect(entries.length).toBeGreaterThan(1);
    const last = entries[entries.length - 1];
    expect(last.actorName).toBe("Mona Saleh");
    // The change that matters is visible as a change: the cap appears where it previously was not set.
    expect(entries[0].maxPerDay).toBeNull();
    expect(last.maxPerDay).toBe(12);
  });
});

describe("recording a renewal", () => {
  it("opens as a DIALOG, not a card appended below the table", async () => {
    // C4 — this rendered a `Card` after the table, so clicking the action on row 20 of a 25-row page put the
    // form below the fold with nothing to say it had opened: no focus move, no role, no Esc, no focus
    // returned. On the portal's primary write. Radix Dialog supplies all four; asserting the role is what
    // makes "it is a dialog" a fact rather than an intention.
    const user = userEvent.setup();
    wrap(<BranchPractitioners />);

    await waitFor(() => expect(screen.getByText("Hala Fouad")).toBeInTheDocument());
    await user.click(screen.getAllByRole("button", { name: "Record renewal" })[0]);

    expect(await screen.findByRole("dialog")).toBeInTheDocument();
  });

  it("warns and demands an impact check when the expiry moves EARLIER", async () => {
    // C6 — the roster has required a preview before closing a clinic day since 25.4; shortening a licence
    // strands appointments the same way and asked for nothing at all.
    const user = userEvent.setup();
    wrap(<BranchPractitioners />);

    await waitFor(() => expect(screen.getByText("Hala Fouad")).toBeInTheDocument());
    await user.click(screen.getAllByRole("button", { name: "Record renewal" })[0]);

    const dialog = await screen.findByRole("dialog");
    const expiry = within(dialog).getByLabelText(/Expiry/i);
    await user.clear(expiry);
    await user.type(expiry, "2026-01-01");

    expect(within(dialog).getByText(/cannot be booked/i)).toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Check impact" })).toBeInTheDocument();
    // Save is held until the operator has looked.
    expect(within(dialog).getByRole("button", { name: "Save licence" })).toBeDisabled();
  });

  it("asks for nothing extra when the expiry moves OUTWARDS", async () => {
    // The routine renewal, and the common one. An acknowledgement demanded on every save is one that gets
    // clicked without reading, which would destroy the value of the case above.
    const user = userEvent.setup();
    wrap(<BranchPractitioners />);

    await waitFor(() => expect(screen.getByText("Hala Fouad")).toBeInTheDocument());
    await user.click(screen.getAllByRole("button", { name: "Record renewal" })[0]);

    const dialog = await screen.findByRole("dialog");
    const expiry = within(dialog).getByLabelText(/Expiry/i);
    await user.clear(expiry);
    await user.type(expiry, "2099-01-01");

    expect(within(dialog).queryByText(/cannot be booked/i)).not.toBeInTheDocument();
    expect(within(dialog).getByRole("button", { name: "Save licence" })).toBeEnabled();
  });

  it("is reachable from the ALERTS worklist, which is where the work is identified", async () => {
    // C5 — the "who do I chase" table had no action, so the operator held a name in their head, navigated to
    // another screen, and searched for it.
    const user = userEvent.setup();
    wrap(<BranchLicenceAlerts />);

    await waitFor(() => expect(screen.getByText("Karim Adel")).toBeInTheDocument());
    await user.click(screen.getAllByRole("button", { name: "Record renewal" })[0]);

    expect(await screen.findByRole("dialog")).toBeInTheDocument();
  });
});

describe("licence impact preview", () => {
  it("lists what shortening an expiry would strand", async () => {
    const impact = await branchApi.licenceImpact("d0000000-0000-4000-8000-000000000001", "2026-01-01");
    expect(impact.affectedCount).toBeGreaterThan(0);
  });

  it("strands NOBODY when the expiry moves outwards, which is the common case", async () => {
    // A renewal has to be as clearly answered as an alarming shortening — "0 affected" is what lets a
    // coordinator save without hesitating.
    const impact = await branchApi.licenceImpact("d0000000-0000-4000-8000-000000000001", "2099-01-01");
    expect(impact.affectedCount).toBe(0);
  });
});

describe("Branches Overview", () => {
  it("names the clinics rather than showing truncated ids", async () => {
    // C3 — it rendered `branchId.slice(0, 8)`, with a comment claiming names sat behind provider:read. They
    // do not: GET /branches is plain RequireAuthorization, and the app-bar switcher already fetches it.
    wrap(<BranchesOverview />);

    await waitFor(() => expect(screen.getByText("Maadi")).toBeInTheDocument());
    expect(screen.queryByText("b1000000")).not.toBeInTheDocument();
  });
});

describe("Inventory", () => {
  it("keeps the balance and the ledger together, with the write behind a button", async () => {
    const user = userEvent.setup();
    wrap(<BranchInventory />);

    // Recording a movement is a nine-field form and it sat permanently open BETWEEN the stock table and the
    // ledger — so the two things this screen exists to show, a balance and the movements that produce it,
    // were separated by the form that produces them.
    expect(await screen.findByText("Movement ledger")).toBeInTheDocument();
    expect(screen.queryByRole("dialog")).not.toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: "Record a movement" }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("button", { name: "Record movement" })).toBeInTheDocument();
    // Rendered only while open: a quantity left over from a dialog closed twenty minutes ago, against stock
    // that has moved since, is exactly the stale write an append-only ledger cannot take back.
    expect(within(dialog).getByText(/appended to the ledger/i)).toBeInTheDocument();
  });
});

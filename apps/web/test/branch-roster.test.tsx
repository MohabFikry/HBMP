import { describe, expect, it } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { rosterApi } from "../src/api/branchApi";
import { BranchRoster } from "../src/screens/BranchRoster";

/**
 * 33.10 — the roster, rebuilt.
 *
 * <b>What the old screen did and why it needed replacing.</b> It listed availability RULES: one row per
 * clinician per weekday. Dr Karim, who works three sessions across two clinics, appeared three times in a
 * table that named neither building, so his rows were indistinguishable from one another and from anybody
 * else's. There was no way to ask "what does his week look like", and no way at all to ask "who is in at
 * Dokki on Thursday" — the second question needs the weekly pattern and the exception calendar combined, and
 * they were two tables nothing joined.
 *
 * <b>What these tests hold.</b> Each clinician once; their clinics named wherever a row could otherwise be
 * ambiguous; a week that shows the days somebody does NOT work, because that is what a coordinator looking
 * for cover is reading; and a day view whose numbers already have the exceptions applied to them.
 */
const wrap = (ui: React.ReactNode) =>
  render(
    <AppProviders authClient={new DevAuthClient()}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>{ui}</MemoryRouter>
    </AppProviders>,
  );

const MAADI = "b1000000-0000-4000-8000-000000000001";
const HALA = "d0000000-0000-4000-8000-000000000001";

/** The next date (today included) falling on `dow`, as the ISO string the endpoint and the date input speak. */
function nextIso(dow: number): string {
  const d = new Date();
  while (d.getDay() !== dow) d.setDate(d.getDate() + 1);
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

// ── the clinician list ──────────────────────────────────────────────────────────────────────────────────

describe("the clinician list", () => {
  const grid = async () => within(await screen.findByRole("grid"));

  it("names each clinician ONCE, however many sessions they work", async () => {
    wrap(<BranchRoster />);
    const rows = await grid();

    // Dr Karim has three rules — two at Maadi, one at Dokki. The old table gave him three rows.
    await waitFor(() => expect(rows.getByText("Karim Adel")).toBeInTheDocument());
    expect(rows.getAllByText("Karim Adel")).toHaveLength(1);
  });

  it("names the clinics of anyone who works more than one", async () => {
    wrap(<BranchRoster />);
    const rows = await grid();

    const karim = (await rows.findByText("Karim Adel")).closest("tr")!;
    // Without this the two 14:00 sessions on his week are indistinguishable, and they are in different
    // buildings.
    expect(within(karim).getByText(/Maadi/)).toBeInTheDocument();
    expect(within(karim).getByText(/Dokki/)).toBeInTheDocument();
  });

  it("keeps a clinician who has no pattern at all", async () => {
    wrap(<BranchRoster />);
    const rows = await grid();

    // Mona Saleh is a nurse with no availability rule. She is exactly who a coordinator is looking for when
    // a clinic is short, and a list built from the RULES would not contain her.
    const mona = (await rows.findByText("Mona Saleh")).closest("tr")!;
    expect(within(mona).getByText("No pattern")).toBeInTheDocument();
  });
});

// ── the weekly pattern pane ─────────────────────────────────────────────────────────────────────────────

describe("the weekly pattern pane", () => {
  const open = async (user: ReturnType<typeof userEvent.setup>, name: string) => {
    const row = (await within(await screen.findByRole("grid")).findByText(name)).closest("tr")!;
    await user.click(row);
    return within(await screen.findByRole("region", { name: /weekly pattern/i }));
  };

  it("opens nothing until somebody is chosen", async () => {
    wrap(<BranchRoster />);
    expect(await screen.findByText(/choose a clinician/i)).toBeInTheDocument();
  });

  it("shows the whole week, including the days the clinician does not work", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);
    const pane = await open(user, "Hala Fouad");

    // Dr Hala works Sunday and Tuesday at Maadi. The other five days have to be visible AS free days — a
    // table of only the days somebody works cannot answer "when could she cover?".
    expect(pane.getByText("Sunday")).toBeInTheDocument();
    expect(pane.getByText("Wednesday")).toBeInTheDocument();
    expect(pane.getAllByText("Not working").length).toBeGreaterThan(0);
  });

  it("prints the cap beside the number of slots the hours allow", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);
    const pane = await open(user, "Hala Fouad");

    // "12 of 16 the hours allow" — one sentence. Either number alone makes the cap invisible or unexplained.
    expect(pane.getAllByText(/of 16 the hours allow/).length).toBeGreaterThan(0);
  });

  it("names the clinic on every row of somebody who works two", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);
    const pane = await open(user, "Karim Adel");

    const header = within(pane.getByRole("table")).getAllByRole("columnheader");
    expect(header.map((h) => h.textContent)).toContain("Clinic");
  });

  it("offers a working day to a clinician who has no pattern at all", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);
    const pane = await open(user, "Mona Saleh");

    // Add used to inherit the clinic's provider and location from THIS clinician's other rules, so somebody
    // with none could never be given a first one — and removing a clinician's last day at a clinic took the
    // Add button away with it. Any rule at the same clinic carries the same service point.
    expect(pane.getAllByRole("button", { name: "Add" }).length).toBe(7);
  });

  it("narrows to one clinic when its chip is pressed", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);
    const pane = await open(user, "Karim Adel");

    // Two clinics, so every working day is on screen and the Clinic column tells them apart.
    expect(pane.getAllByText("Not working")).toHaveLength(4);

    await user.click(pane.getByRole("button", { name: /Dokki/ }));

    // Dokki is one session a week, so six of the seven days are now free — and the Clinic column goes, having
    // become a word repeated seven times.
    expect(pane.getAllByText("Not working")).toHaveLength(6);
    expect(within(pane.getByRole("table")).getAllByRole("columnheader").map((h) => h.textContent))
      .not.toContain("Clinic");
  });

  it("presses the chip back to show every clinic again", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);
    const pane = await open(user, "Karim Adel");

    const dokki = pane.getByRole("button", { name: /Dokki/ });
    await user.click(dokki);
    expect(dokki).toHaveAttribute("aria-pressed", "true");

    await user.click(dokki);
    expect(dokki).toHaveAttribute("aria-pressed", "false");
    expect(pane.getAllByText("Not working")).toHaveLength(4);
  });

  it("gives every row action a name, because they are icons", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);
    const pane = await open(user, "Hala Fouad");

    // 0B §6: an icon-only control carries its name. Without this the pane is three unlabelled glyphs per row
    // to a screen reader, on the only controls that change a clinic's hours.
    expect(pane.getAllByRole("button", { name: "Edit" }).length).toBeGreaterThan(0);
    expect(pane.getAllByRole("button", { name: "History" }).length).toBeGreaterThan(0);
    expect(pane.getAllByRole("button", { name: "Remove" }).length).toBeGreaterThan(0);
  });

  it("offers the clinics a clinician is assigned to, and a way to add one", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);
    const pane = await open(user, "Karim Adel");

    expect(pane.getByText("Assigned clinics")).toBeInTheDocument();
    await user.click(pane.getByRole("button", { name: "Add a clinic" }));
    expect(await screen.findByRole("dialog")).toBeInTheDocument();
  });
});

// ── today's roster ──────────────────────────────────────────────────────────────────────────────────────

describe("today's roster", () => {
  it("answers one clinic on one date, with the day's numbers and the week's", async () => {
    const tuesday = nextIso(2);
    const day = await rosterApi.day({ branchId: MAADI, date: tuesday });

    const hala = day.lines.find((l) => l.practitionerId === HALA);
    expect(hala).toBeDefined();
    expect(hala!.status).toBe("Working");
    expect(hala!.slotsFromPattern).toBe(12);
    expect(day.summary.slotsOffered).toBeGreaterThan(0);
  });

  it("keeps a clinician on the roster when they are away, and says why", async () => {
    const tuesday = nextIso(2);
    await rosterApi.apply({
      kind: "Leave", dateFrom: tuesday, dateTo: tuesday, reason: "Annual leave",
      practitionerId: HALA, branchId: MAADI, acknowledgedImpactCount: 0,
    });

    const day = await rosterApi.day({ branchId: MAADI, date: tuesday });
    const hala = day.lines.find((l) => l.practitionerId === HALA)!;

    // "Dr Hala is not on today's roster" and "Dr Hala is on annual leave" are the same screen to somebody
    // ringing round for cover, and only one of them says what to do next.
    expect(hala.status).toBe("Off");
    expect(hala.slotsOffered).toBe(0);
    expect(hala.exceptionReason).toBe("Annual leave");
    // The WEEK is intact. What the day lost is not what the pattern says.
    expect(hala.slotsFromPattern).toBe(12);
  });

  it("shortens a session for a part-day absence rather than closing it", async () => {
    // Sunday, not Tuesday: the test above puts Dr Hala on whole-day leave that Tuesday and the fixture keeps
    // what it is given, exactly as a server would. Her Sunday pattern is the same shape.
    const sunday = nextIso(0);
    await rosterApi.apply({
      kind: "Leave", dateFrom: sunday, dateTo: sunday, reason: "Hospital round",
      practitionerId: HALA, branchId: MAADI, startTime: "11:00", endTime: "13:00",
      acknowledgedImpactCount: 0,
    });

    const day = await rosterApi.day({ branchId: MAADI, date: sunday });
    const hala = day.lines.find((l) => l.practitionerId === HALA)!;

    expect(hala.status).toBe("Working");
    // 09:00–11:00 at fifteen minutes. The cap of 12 never binds, because subtraction happens first.
    expect(hala.slotsOffered).toBe(8);
  });

  it("explains an empty day rather than reporting it as an empty rota", async () => {
    const friday = nextIso(5);   // nobody has a Friday pattern in the fixture
    await rosterApi.apply({
      kind: "ClinicClosed", dateFrom: friday, dateTo: friday, reason: "Power cut",
      branchId: MAADI, acknowledgedImpactCount: 0,
    });

    const day = await rosterApi.day({ branchId: MAADI, date: friday });

    expect(day.lines).toHaveLength(0);
    // Without the notice, a public holiday and a rota nobody entered read identically.
    expect(day.notices.map((n) => n.reason)).toContain("Power cut");
  });

  it("lets a closure outrank an extra clinic on the same day", async () => {
    // Saturday, so the closure the test above records on Friday is not what makes this one pass.
    const saturday = nextIso(6);
    await rosterApi.apply({
      kind: "AdHocClinic", dateFrom: saturday, dateTo: saturday, reason: "Catch-up clinic",
      branchId: MAADI, practitionerId: HALA, startTime: "14:00", endTime: "17:00",
      acknowledgedImpactCount: 0,
    });

    // The extra session exists on its own …
    const before = await rosterApi.day({ branchId: MAADI, date: saturday });
    expect(before.lines.map((l) => l.status)).toContain("Extra");

    await rosterApi.apply({
      kind: "ClinicClosed", dateFrom: saturday, dateTo: saturday, reason: "Burst pipe",
      branchId: MAADI, acknowledgedImpactCount: 0,
    });

    const day = await rosterApi.day({ branchId: MAADI, date: saturday });

    // An extra session at a shut clinic is not a session. The other ordering lets a stale ad-hoc row quietly
    // reopen a building somebody closed.
    expect(day.lines).toHaveLength(0);
  });

  it("is reachable from the screen, with the exceptions already applied", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);

    await user.click(await screen.findByRole("radio", { name: "Today's roster" }));

    expect(await screen.findByText("Clinicians on duty")).toBeInTheDocument();
    expect(screen.getByLabelText("Date")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Today" })).toBeInTheDocument();
  });

  it("names the day on screen once you step off today", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);
    await user.click(await screen.findByRole("radio", { name: "Today's roster" }));

    // It said "Today" whatever date was showing, so stepping forward changed the table and changed nothing on
    // the control that had just been pressed. The weekday is also the fact the date field cannot give.
    await user.click(screen.getByRole("button", { name: "Next day" }));

    const tomorrow = new Date();
    tomorrow.setDate(tomorrow.getDate() + 1);
    const weekday = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"][tomorrow.getDay()];

    // The visible text is the PREFIX of the accessible name (WCAG 2.5.3), so speech control still reaches it.
    expect(await screen.findByRole("button", { name: `${weekday} — back to today` })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Today" })).not.toBeInTheDocument();
  });

  it("gives the day's table a search and a pager", async () => {
    const user = userEvent.setup();
    wrap(<BranchRoster />);
    await user.click(await screen.findByRole("radio", { name: "Today's roster" }));

    // A supervisor with six clinics and no filter gets every session in the group on one date. "Is Hana in
    // today" should not mean scrolling thirty rows.
    expect(await screen.findByLabelText("Search")).toBeInTheDocument();
  });
});

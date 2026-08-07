import { describe, expect, it, vi } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/authClient";
import { ApiError } from "../src/api/http";
import type { ApiClient } from "../src/api/client";
import type { AppointmentRow } from "@mersal/contracts";
import { ReceptionAppointments } from "../src/screens/ReceptionDesk";

function booked(rowVersion?: number): AppointmentRow {
  return {
    id: "appt-1",
    beneficiary: { id: "b1", token: "•••4821" },
    appointmentType: "Consultation",
    status: { kind: "info", label: { en: "Booked", ar: "محجوز" } },
    scheduledStart: "2026-07-26T09:00:00Z",
    checkInEligible: true,
    checkedIn: false,
    noShowEligible: false,
    startVisitEligible: false,
    rowVersion,
  };
}

/** 18.D1 (E3): the row as the SERVER returns it AFTER a successful check-in. The desk must derive its chip
 * from this, not from a local "we sent the request" flag. */
function checkedIn(rowVersion?: number): AppointmentRow {
  return {
    ...booked(rowVersion),
    status: { kind: "ok", label: { en: "Checked in", ar: "تم الوصول" } },
    checkInEligible: false,
    checkedIn: true,
  };
}

/**
 * Row actions, scoped to the TABLE.
 *
 * The board's toolbar now carries status filter chips — "Booked", "Checked in", "No-show" — which are buttons
 * with the same accessible names as the row actions beside them. An unscoped `getByRole("button", {name:
 * /no-show/i})` matches the FILTER first and silently clicks that instead: a test that fails confusingly, or
 * worse, passes for the wrong reason. Scoping says which of the two a case means.
 */
const inTable = () => within(screen.getByRole("table"));

/** MemoryRouter keeps its history in memory, so window.location never moves — read the router's own. */
function Where() {
  return <span data-testid="where">{useLocation().pathname}</span>;
}

function fakeApi(over: Partial<ApiClient> = {}): ApiClient {
  return {
    appointments: vi.fn().mockResolvedValue([booked(42)]),
    // 14.5 — the board joins doctor + specialty in from provider-service. Empty here: these cases are about
    // the desk's TRANSITIONS, and a doctor directory would only add noise to them.
    practitioners: vi.fn().mockResolvedValue([]),
    specialties: vi.fn().mockResolvedValue([]),
    cancelAppointment: vi.fn().mockResolvedValue({ id: "appt-1", status: { kind: "neu", label: { en: "Cancelled", ar: "ملغى" } } }),
    checkIn: vi.fn().mockResolvedValue({ id: "appt-1", status: { kind: "ok", label: { en: "Checked in", ar: "تم الوصول" } } }),
    ...over,
  } as unknown as ApiClient;
}

/**
 * 14.5 — check-in MERGED into the appointments board. There is no separate Check-in screen any more: it was
 * always the same server call against a filtered view of this same table, so the second screen only added a
 * place for the two to disagree. These cases are unchanged in substance and now run against the board, which
 * is the point — the behaviour had to survive the merge, not be rewritten by it.
 */
function renderCheckIn(api: ApiClient) {
  return render(
    // Inside a Router: the board's "Patient file" action navigates, exactly as it does in the app.
    <AppProviders authClient={new DevAuthClient()} apiClient={api}>
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <ReceptionAppointments />
        <Where />
      </MemoryRouter>
    </AppProviders>,
  );
}

describe("17.0 — check-in optimistic concurrency, now on the merged board (If-Match opt-in)", () => {
  it("echoes the row version read on the board as the If-Match token", async () => {
    const user = userEvent.setup();
    const api = fakeApi();
    renderCheckIn(api);

    await screen.findByRole("table");
    await user.click(inTable().getByRole("button", { name: /check in/i }));

    await waitFor(() => expect(api.checkIn).toHaveBeenCalledWith("appt-1", 42));
  });

  it("renders the checked-in chip only from SERVER state, after a reload (18.D1 / E3)", async () => {
    // The rule: a read may be optimistic, a server-invariant operation may not. This screen used to paint the
    // green chip from a local `done` set the moment the request was SENT — so the board showed "checked in"
    // for a patient the server had not admitted, and a reload silently disagreed with what the desk had just
    // seen. The first load returns Booked, the reload after a successful check-in returns CheckedIn.
    const user = userEvent.setup();
    const appointments = vi.fn()
      .mockResolvedValueOnce([booked(42)])
      .mockResolvedValue([checkedIn(43)]);
    const api = fakeApi({ appointments });
    renderCheckIn(api);

    await screen.findByRole("table");
    await user.click(inTable().getByRole("button", { name: /check in/i }));

    // The board was RE-READ, and the action cell now shows the confirmed chip instead of the button. Both
    // the status column and the action cell render "Checked in", which is the point — they agree because
    // both derive from the same server row.
    await waitFor(() => expect(appointments).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(inTable().queryByRole("button", { name: /check in/i })).not.toBeInTheDocument());
    expect(screen.getAllByText(/checked in/i).length).toBeGreaterThan(0);
  });

  it("on a 412 stale write, shows the changed notice and reloads the board instead of double-acting", async () => {
    const user = userEvent.setup();
    const checkIn = vi.fn().mockRejectedValue(new ApiError("http", "Version mismatch", 412));
    const appointments = vi.fn().mockResolvedValue([booked(42)]);
    const api = fakeApi({ checkIn, appointments });
    renderCheckIn(api);

    await screen.findByRole("table");
    await user.click(inTable().getByRole("button", { name: /check in/i }));

    // The stale notice appears…
    expect(await screen.findByText(/changed since the board loaded/i)).toBeInTheDocument();
    // …the row is NOT marked checked-in (no double-action). Scoped to the table: the toolbar carries a
    // "Checked in" FILTER chip with the same text, and matching that would make this assertion vacuous.
    expect(inTable().queryByText(/^Checked in$/)).not.toBeInTheDocument();
    // …and the board is re-loaded (initial load + reload).
    await waitFor(() => expect(appointments).toHaveBeenCalledTimes(2));
  });
});

/**
 * Design 39 §6 — the unified patient profile is opened FOR someone from a worklist, never from a menu. Every
 * clinical worklist gained that entry point; reception's boards, which are the list the DESK works from all
 * day, had none, so on this side of the building the profile was unreachable.
 */
describe("Reception boards — patient-file entry point", () => {
  it("offers a Patient file action on every row and routes to that beneficiary", async () => {
    const user = userEvent.setup();
    renderCheckIn(fakeApi());

    await screen.findByRole("table");
    const openFile = inTable().getByRole("button", { name: /patient file/i });
    await user.click(openFile);

    // The row's beneficiary id is what the deep link must carry — not the appointment id.
    await waitFor(() => expect(screen.getByTestId("where")).toHaveTextContent("/patients/b1"));
  });
});

/**
 * US-022 — the day board carries BOTH decisions the desk makes about an appointment: the patient arrived, or
 * they did not. The no-show action is governed by a grace period after the scheduled end that only the server
 * knows, so the board renders the server's flag and never re-derives it from the browser clock.
 */
describe("Reception day board — check-in and No-show (US-022)", () => {
  const row = (over: Partial<AppointmentRow> = {}): AppointmentRow => ({ ...booked(7), ...over });

  function renderBoard(api: ApiClient) {
    return render(
      <AppProviders authClient={new DevAuthClient()} apiClient={api}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <ReceptionAppointments />
        </MemoryRouter>
      </AppProviders>,
    );
  }

  it("offers No-show ONLY when the server says the window has passed", async () => {
    const api = fakeApi({ appointments: vi.fn().mockResolvedValue([row({ noShowEligible: false })]) });
    renderBoard(api);

    await screen.findByRole("table");
    expect(inTable().getByRole("button", { name: /check in/i })).toBeInTheDocument();

    // PRESENT, and disabled. The button used to be hidden until the window passed, so it appeared out of
    // nowhere partway through the morning and the desk had no idea where it would land; a control that is
    // visible and visibly unusable teaches its own position.
    const ns = inTable().getByRole("button", { name: /no-show/i });
    expect(ns).toHaveAttribute("aria-disabled", "true");
    // `aria-disabled`, not `disabled`: a disabled button leaves the tab order, and with it goes the only
    // route a keyboard or screen-reader user has to the REASON — which is the entire point of showing the
    // control early. "Deactivated" with no reason is the shape of message an operator stops reading.
    expect(ns).not.toBeDisabled();
    expect(ns).toHaveAttribute("title", expect.stringMatching(/once the appointment window has passed/i));
  });

  it("cannot be fired while it is deactivated", async () => {
    // The half that matters. A control that LOOKS disabled and still calls the server on click is worse than
    // one that was never shown — the desk would mark a no-show minutes before the platform allows it, and the
    // server's refusal would arrive as an error nobody can explain.
    const user = userEvent.setup();
    const noShow = vi.fn();
    renderBoard(fakeApi({ noShow, appointments: vi.fn().mockResolvedValue([row({ noShowEligible: false })]) }));

    await screen.findByRole("table");
    await user.click(inTable().getByRole("button", { name: /no-show/i }));

    expect(noShow).not.toHaveBeenCalled();
  });

  it("shows No-show once eligible and sends the row version as If-Match", async () => {
    const user = userEvent.setup();
    const noShow = vi.fn().mockResolvedValue({ id: "appt-1", status: { kind: "warn", label: { en: "No-show", ar: "لم يحضر" } } });
    const appointments = vi.fn().mockResolvedValue([row({ noShowEligible: true })]);
    renderBoard(fakeApi({ noShow, appointments }));

    await screen.findByRole("table");
    await user.click(inTable().getByRole("button", { name: /no-show/i }));
    expect(noShow).toHaveBeenCalledWith("appt-1", 7);
    // The result is re-read from the server rather than painted locally (18.D1 E3).
    await waitFor(() => expect(appointments).toHaveBeenCalledTimes(2));
  });

  it("a 412 on No-show shows the changed notice and re-reads instead of double-acting", async () => {
    const user = userEvent.setup();
    const noShow = vi.fn().mockRejectedValue(new ApiError("http", "Version mismatch", 412));
    const appointments = vi.fn().mockResolvedValue([row({ noShowEligible: true })]);
    renderBoard(fakeApi({ noShow, appointments }));

    await screen.findByRole("table");
    await user.click(inTable().getByRole("button", { name: /no-show/i }));
    expect(await screen.findByText(/changed since the board loaded/i)).toBeInTheDocument();
    await waitFor(() => expect(appointments).toHaveBeenCalledTimes(2));
  });

  it("a checked-in row offers neither action", async () => {
    const api = fakeApi({
      appointments: vi.fn().mockResolvedValue([row({ checkInEligible: false, checkedIn: true, noShowEligible: false })]),
    });
    renderBoard(api);

    await waitFor(() => expect(inTable().queryByRole("button", { name: /check in/i })).not.toBeInTheDocument());
    expect(inTable().queryByRole("button", { name: /no-show/i })).not.toBeInTheDocument();
  });
});

/**
 * 14.5 — the toolbar the merge brought with it: search, a When filter (today / custom range) and a Status
 * filter, over one table that now carries check-in as an action.
 *
 * The split that matters here is WHICH filters re-query. `when` changes which rows exist, so it goes to the
 * server; `status` and the search narrow rows already in hand, so they do not. Getting that backwards means
 * either a status filter that silently misses appointments outside today, or an API call on every keystroke.
 */
describe("Reception board — search, filters and sort (14.5)", () => {
  const rows: AppointmentRow[] = [
    { ...booked(1), id: "a1", beneficiary: { id: "b1", token: "•••1111" }, appointmentType: "Consultation", scheduledStart: "2026-07-26T11:00:00Z" },
    {
      ...booked(2), id: "a2", beneficiary: { id: "b2", token: "•••2222" }, appointmentType: "Procedure",
      scheduledStart: "2026-07-26T08:00:00Z",
      status: { kind: "ok", label: { en: "Checked in", ar: "تم الوصول" } }, checkInEligible: false, checkedIn: true,
    },
  ];

  function renderBoard(api: ApiClient) {
    return render(
      <AppProviders authClient={new DevAuthClient()} apiClient={api}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <ReceptionAppointments />
        </MemoryRouter>
      </AppProviders>,
    );
  }

  const tokens = () =>
    within(screen.getByRole("table")).getAllByRole("row").slice(1)
      .map((r) => within(r).getAllByRole("cell")[0].textContent);

  it("filters by status WITHOUT re-querying — those rows are already in hand", async () => {
    const user = userEvent.setup();
    const appointments = vi.fn().mockResolvedValue(rows);
    renderBoard(fakeApi({ appointments }));
    await screen.findByRole("table");

    await user.click(screen.getByRole("button", { name: /^checked in$/i }));

    expect(tokens()).toEqual(["•••2222"]);
    // One load, not two: a client-side narrowing must not cost a round trip.
    expect(appointments).toHaveBeenCalledTimes(1);
  });

  it("searches the masked token without re-querying", async () => {
    const user = userEvent.setup();
    const appointments = vi.fn().mockResolvedValue(rows);
    renderBoard(fakeApi({ appointments }));
    await screen.findByRole("table");

    await user.type(screen.getByLabelText(/^search$/i), "1111");

    expect(tokens()).toEqual(["•••1111"]);
    expect(appointments).toHaveBeenCalledTimes(1);
  });

  it("says the FILTERS hid the rows, rather than claiming there are none", async () => {
    const user = userEvent.setup();
    renderBoard(fakeApi({ appointments: vi.fn().mockResolvedValue([rows[0]]) }));
    await screen.findByRole("table");

    // Scoped to the FILTER GROUP. The no-show row action is now always rendered — disabled until the window
    // passes — so an unscoped query matches two buttons with the same name, and the one it would have picked
    // is a coin toss between filtering the board and marking a patient absent.
    await user.click(within(screen.getByRole("group", { name: /status/i }))
      .getByRole("button", { name: /^no-show$/i }));

    // "No appointments booked for today" would tell the desk their bookings had vanished.
    expect(await screen.findByText(/no appointments match these filters/i)).toBeInTheDocument();
    expect(screen.queryByText(/no appointments booked for today/i)).not.toBeInTheDocument();
  });

  it("does not apply a custom range until BOTH dates are given", async () => {
    const user = userEvent.setup();
    const appointments = vi.fn().mockResolvedValue(rows);
    renderBoard(fakeApi({ appointments }));
    await screen.findByRole("table");

    await user.click(screen.getByRole("button", { name: /custom range/i }));
    // Says what is actually on screen meanwhile, instead of looking filtered when it is not.
    expect(screen.getByText(/showing today until then/i)).toBeInTheDocument();
    expect(appointments).toHaveBeenCalledTimes(1);

    await user.type(screen.getByLabelText(/^from$/i), "2026-07-20");
    expect(appointments).toHaveBeenCalledTimes(1);   // still incomplete

    await user.type(screen.getByLabelText(/^to$/i), "2026-07-26");
    // Now it re-queries, and the range reaches the server rather than being applied to today's rows.
    await waitFor(() => expect(appointments).toHaveBeenCalledTimes(2));
    expect(appointments).toHaveBeenLastCalledWith("all", false, { from: "2026-07-20", to: "2026-07-26" });
  });

  it("sorts by time chronologically, from the instant rather than the rendered label", async () => {
    const user = userEvent.setup();
    renderBoard(fakeApi({ appointments: vi.fn().mockResolvedValue(rows) }));
    await screen.findByRole("table");

    await user.click(within(screen.getByRole("table")).getByRole("button", { name: /^time$/i }));

    expect(tokens()).toEqual(["•••2222", "•••1111"]);   // 08:00 before 11:00
  });
});

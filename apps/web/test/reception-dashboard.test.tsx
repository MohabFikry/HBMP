import { describe, expect, it, vi } from "vitest";
import { screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { DevApiClient } from "../src/api/DevApiClient";
import type { ApiClient } from "../src/api/client";
import { ReceptionDashboard } from "../src/screens/ReceptionDashboard";

/**
 * 14.5 — the reception dashboard.
 *
 * Three claims worth pinning: the cards come from the SERVER (not from tallying a capped board), today's
 * visits show real names for people who have arrived, and doctor + specialty are joined in from
 * provider-service rather than expected from emr.
 */
describe("Reception dashboard", () => {
  const visitsTable = () => within(screen.getByRole("table", { name: /^visits$/i }));

  it("takes the card figures from the server, not from counting the board", async () => {
    const counts = vi.fn().mockResolvedValue({ total: 137, checkedIn: 12, noShow: 4 });
    const api = new DevApiClient({ latencyMs: 0 });
    (api as unknown as { appointmentCounts: unknown }).appointmentCounts = counts;
    renderNode(<ReceptionDashboard />, api as unknown as ApiClient);

    // 137 could never come from the board: that read is capped at 200 rows and the fixture holds four.
    expect(await screen.findByText("137")).toBeInTheDocument();
    expect(screen.getByText("12")).toBeInTheDocument();
    expect(screen.getByText("4")).toBeInTheDocument();
    expect(counts).toHaveBeenCalled();
  });

  it("lists only patients who have ARRIVED, by name", async () => {
    renderNode(<ReceptionDashboard />);

    // The fixture's checked-in row carries a name (captured at check-in); the booked ones do not, and are
    // not in this section at all — "today's visits" is the people in the building.
    await waitFor(() => expect(visitsTable().getByText("Amal Hassan")).toBeInTheDocument());
    expect(visitsTable().getAllByRole("row")).toHaveLength(2);   // header + the one arrival
  });

  it("joins the doctor's NAME and SPECIALTY in from provider-service", async () => {
    renderNode(<ReceptionDashboard />);
    await waitFor(() => expect(visitsTable().getByText("Amal Hassan")).toBeInTheDocument());

    // emr returns only a doctorId. Who that is belongs to provider-service, which reception reads directly
    // under practitioner:read — neither service composes the other's data on the caller's behalf.
    const row = visitsTable().getByText("Amal Hassan").closest("tr")!;
    expect(within(row).getByText("Hana Mansour")).toBeInTheDocument();
    expect(within(row).getByText("Pediatrics")).toBeInTheDocument();
  });

  it("gives every visit a prominent patient-file action", async () => {
    renderNode(<ReceptionDashboard />);
    await waitFor(() => expect(visitsTable().getByText("Amal Hassan")).toBeInTheDocument());

    expect(visitsTable().getByRole("button", { name: /patient file/i })).toBeInTheDocument();
  });

  it("lays the whole day out in hour bands, including hours with nothing in them", async () => {
    renderNode(<ReceptionDashboard />);

    // An empty band is the answer to "can we fit a walk-in in?", so the schedule is built from a fixed hour
    // list rather than only from the hours that happen to be booked.
    expect(await screen.findByText("08:00")).toBeInTheDocument();
    expect(screen.getByText("15:00")).toBeInTheDocument();
  });

  it("opens a booking note from the visits row", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    // Put a note on the arrival, so the affordance has something to open.
    const original = api.appointments.bind(api);
    (api as unknown as { appointments: unknown }).appointments = async (...args: unknown[]) => {
      const rows = await (original as (...a: unknown[]) => Promise<any[]>)(...args);
      return rows.map((r) => (r.checkedIn ? { ...r, note: "Wheelchair access needed." } : r));
    };
    renderNode(<ReceptionDashboard />, api as unknown as ApiClient);
    await waitFor(() => expect(visitsTable().getByText("Amal Hassan")).toBeInTheDocument());

    await user.click(visitsTable().getByRole("button", { name: /appointment note/i }));
    expect(within(await screen.findByRole("dialog")).getByText(/wheelchair access/i)).toBeInTheDocument();
  });

  it("has no serious/critical a11y violations", async () => {
    const { container } = renderNode(<ReceptionDashboard />);
    await waitFor(() => expect(visitsTable().getByText("Amal Hassan")).toBeInTheDocument());
    expect(await axe(container, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });

  /**
   * The day selector. Today by default — that is what the desk opens the screen for — but "who is in
   * tomorrow?" is asked constantly and previously meant leaving this screen to answer.
   */
  it("shows TODAY by default, named rather than dated", async () => {
    renderNode(<ReceptionDashboard />);
    // "Today" is how the desk refers to it; a date where "Today" belongs makes the reader stop and work out
    // whether it is the current one.
    expect(await screen.findByText(/^today$/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /back to today/i })).not.toBeInTheDocument();
  });

  it("steps to another day, shows its DATE, and offers a way back inside the picker", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const counts = vi.fn().mockResolvedValue({ total: 0, checkedIn: 0, noShow: 0 });
    (api as unknown as { appointmentCounts: unknown }).appointmentCounts = counts;
    const board = vi.fn().mockResolvedValue([]);
    (api as unknown as { appointments: unknown }).appointments = board;
    renderNode(<ReceptionDashboard />, api as unknown as ApiClient);
    await waitFor(() => expect(counts).toHaveBeenCalledTimes(1));

    await user.click(screen.getByRole("button", { name: /next day/i }));

    // Off today, the label becomes the date — and every figure on the page re-reads for that day, so the
    // cards, the visits table and the schedule cannot disagree about which day they describe.
    await waitFor(() => expect(counts).toHaveBeenCalledTimes(2));
    expect(board).toHaveBeenCalledTimes(2);
    expect(screen.queryByText(/^today$/i)).not.toBeInTheDocument();

    // The way back lives INSIDE the month picker now, reached by clicking the label. A standalone button only
    // appeared once you had navigated away, so the one moment you needed it was the one moment its position
    // was unfamiliar — and it did nothing else.
    await user.click(screen.getByRole("button", { name: /choose a day/i }));
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByRole("button", { name: /^today$/i })).toBeInTheDocument();
  });

  it("asks the server for the SAME day across the cards and the board", async () => {
    const user = userEvent.setup();
    const api = new DevApiClient({ latencyMs: 0 });
    const counts = vi.fn().mockResolvedValue({ total: 0, checkedIn: 0, noShow: 0 });
    const board = vi.fn().mockResolvedValue([]);
    (api as unknown as { appointmentCounts: unknown }).appointmentCounts = counts;
    (api as unknown as { appointments: unknown }).appointments = board;
    renderNode(<ReceptionDashboard />, api as unknown as ApiClient);
    await waitFor(() => expect(counts).toHaveBeenCalledTimes(1));

    await user.click(screen.getByRole("button", { name: /next day/i }));
    await waitFor(() => expect(counts).toHaveBeenCalledTimes(2));

    // One piece of state drives both reads. Two clocks would drift the moment either call was slow.
    const askedForByCards = counts.mock.calls[1][0] as string;
    const askedForByBoard = (board.mock.calls[1][2] as { from: string; to: string });
    expect(askedForByBoard.from).toBe(askedForByCards);
    expect(askedForByBoard.to).toBe(askedForByCards);
  });

  /**
   * The picker is a POPOVER anchored to the date, not a modal. Choosing a day is a small adjustment to what
   * is already on screen, and a centred modal dims the very cards and table being compared against.
   */
  it("dismisses on Escape and on a click outside, without changing the day", async () => {
    const user = userEvent.setup();
    renderNode(<ReceptionDashboard />);
    await screen.findByText(/^today$/i);

    await user.click(screen.getByRole("button", { name: /choose a day/i }));
    expect(await screen.findByRole("dialog")).toBeInTheDocument();

    await user.keyboard("{Escape}");
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    // Dismissing is not choosing: the day is untouched.
    expect(screen.getByText(/^today$/i)).toBeInTheDocument();

    await user.click(screen.getByRole("button", { name: /choose a day/i }));
    await screen.findByRole("dialog");
    await user.click(document.body);
    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(screen.getByText(/^today$/i)).toBeInTheDocument();
  });

  it("picking a day from the popover closes it and moves the dashboard", async () => {
    const user = userEvent.setup();
    renderNode(<ReceptionDashboard />);
    await screen.findByText(/^today$/i);

    await user.click(screen.getByRole("button", { name: /choose a day/i }));
    const pop = await screen.findByRole("dialog");
    // The 1st of the shown month — a day that is (almost always) not today, so the label must change.
    const first = within(pop).getAllByRole("radio")[0];
    await user.click(first);

    await waitFor(() => expect(screen.queryByRole("dialog")).not.toBeInTheDocument());
    expect(screen.queryByText(/^today$/i)).not.toBeInTheDocument();
  });
});

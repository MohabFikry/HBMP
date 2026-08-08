import { describe, expect, it, vi } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { renderNode } from "./helpers";
import { BookingTimePicker } from "../src/screens/booking/BookingTimePicker";

const dayCells = () => within(screen.getByRole("radiogroup", { name: /choose a day/i })).getAllByRole("radio");
const times = () => within(screen.getByRole("radiogroup", { name: /available times/i })).getAllByRole("radio");

/** The month currently on screen, as YYYY-MM. */
const MONTH = "2099-01";

/**
 * The month calendar is ALWAYS on screen, and navigable.
 *
 * It rendered only when the server reported a day with slots, so the moment a doctor had nothing free the
 * whole Time section collapsed to one line of text — telling the desk "no times" without showing them what
 * they were choosing between, and with nothing to click to look further out. A caller asking for "sometime
 * after the 20th" cannot be served by that.
 */
describe("BookingTimePicker — the month calendar always shows", () => {
  it("renders a whole month even with NO availability at all", () => {
    renderNode(<BookingTimePicker month={MONTH} days={[]} slots={[]} selectedSlotId={null} onSelectSlot={vi.fn()} />);

    // January has 31 days, and every one of them is drawn whether or not it holds anything.
    expect(dayCells()).toHaveLength(31);
    // And the situation is stated beside the calendar rather than in place of it.
    expect(screen.getByText(/no open times this month/i)).toBeInTheDocument();
  });

  it("names the month it is showing", () => {
    renderNode(<BookingTimePicker month={MONTH} days={[]} slots={[]} selectedSlotId={null} onSelectSlot={vi.fn()} />);
    expect(screen.getByText(/january 2099/i)).toBeInTheDocument();
  });

  it("steps to the next and previous month, and asks the parent to re-fetch", async () => {
    const user = userEvent.setup();
    const onMonthChange = vi.fn();
    renderNode(
      <BookingTimePicker
        month={MONTH} days={[]} slots={[]} selectedSlotId={null}
        onSelectSlot={vi.fn()} onMonthChange={onMonthChange}
      />,
    );

    await user.click(screen.getByRole("button", { name: /next month/i }));
    // The parent is told, because availability for the new month has to be LOADED — navigating without a
    // re-fetch would draw every day empty and claim there is nothing there.
    expect(onMonthChange).toHaveBeenCalledWith("2099-02");

    await user.click(screen.getByRole("button", { name: /previous month/i }));
    expect(onMonthChange).toHaveBeenLastCalledWith("2098-12");
  });

  it("renders the month before a doctor has been chosen", () => {
    // No doctor means no slots and no counts — the operator should still see the month they are choosing
    // from, so the shape of the step is visible before it has anything in it.
    renderNode(<BookingTimePicker month={MONTH} days={[]} slots={[]} selectedSlotId={null} onSelectSlot={vi.fn()} busy />);
    expect(dayCells()).toHaveLength(31);
  });

  it("defaults to the first day that HAS availability, not to the 1st", () => {
    // Opening on an empty day makes a month with plenty of free time look fully booked.
    renderNode(
      <BookingTimePicker
        month={MONTH}
        days={[{ day: "2099-01-15", openSlots: 3 }]}
        slots={[{ id: "s1", start: "2099-01-15T09:00:00Z", end: "2099-01-15T09:15:00Z", open: true }]}
        selectedSlotId={null}
        onSelectSlot={vi.fn()}
      />,
    );

    const checked = dayCells().filter((b) => b.getAttribute("aria-checked") === "true");
    expect(checked).toHaveLength(1);
    expect(checked[0].getAttribute("aria-label")).toMatch(/15/);
    expect(times()).toHaveLength(1);
  });

  it("puts the open-slot count in each day's accessible name", () => {
    renderNode(
      <BookingTimePicker
        month={MONTH}
        days={[{ day: "2099-01-15", openSlots: 4 }]}
        slots={[]}
        selectedSlotId={null}
        onSelectSlot={vi.fn()}
      />,
    );
    // A screen-reader user choosing a day needs to know whether it holds anything BEFORE selecting it.
    const labelled = dayCells().map((b) => b.getAttribute("aria-label") ?? "");
    expect(labelled.some((l) => /4 open/i.test(l))).toBe(true);
    expect(labelled.some((l) => /no times/i.test(l))).toBe(true);
  });

  it("disables a day with nothing on it, and keeps it visible", () => {
    renderNode(<BookingTimePicker month={MONTH} days={[]} slots={[]} selectedSlotId={null} onSelectSlot={vi.fn()} />);
    // Visible so the month reads as a month; disabled so it cannot be chosen into an empty panel.
    expect(dayCells().every((b) => b.hasAttribute("disabled"))).toBe(true);
  });

  it("shows only the CHOSEN day's times, and switches when the day changes", async () => {
    const user = userEvent.setup();
    renderNode(
      <BookingTimePicker
        month={MONTH}
        days={[{ day: "2099-01-15", openSlots: 1 }, { day: "2099-01-16", openSlots: 2 }]}
        slots={[
          { id: "a", start: "2099-01-15T09:00:00Z", end: "2099-01-15T09:15:00Z", open: true },
          { id: "b", start: "2099-01-16T10:00:00Z", end: "2099-01-16T10:15:00Z", open: true },
          { id: "c", start: "2099-01-16T11:00:00Z", end: "2099-01-16T11:15:00Z", open: true },
        ]}
        selectedSlotId={null}
        onSelectSlot={vi.fn()}
      />,
    );

    // Defaults to the first day with availability, not to a blank one.
    expect(times()).toHaveLength(1);

    const second = dayCells().find((b) => (b.getAttribute("aria-label") ?? "").includes("2 open"))!;
    await user.click(second);
    expect(times()).toHaveLength(2);
  });

  it("reports a taken slot from the SERVER's flag rather than re-deriving it", () => {
    renderNode(
      <BookingTimePicker
        month={MONTH}
        days={[{ day: "2099-01-15", openSlots: 1 }]}
        slots={[
          { id: "a", start: "2099-01-15T09:00:00Z", end: "2099-01-15T09:15:00Z", open: true },
          { id: "b", start: "2099-01-15T09:15:00Z", end: "2099-01-15T09:30:00Z", open: false },
        ]}
        selectedSlotId={null}
        onSelectSlot={vi.fn()}
      />,
    );

    expect(times()[0]).toBeEnabled();
    expect(times()[1]).toBeDisabled();
  });
});

import { describe, expect, it } from "vitest";
import { screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { AppointmentNoteButton } from "../src/screens/AppointmentNote";

/**
 * 14.5 — the booking note on an appointment row.
 *
 * A general/administrative note written by reception or the call centre and read by both plus the treating
 * doctor. It is deliberately NOT clinical, and the modal says so — the doctor opening it must not mistake it
 * for a record made by someone with clinical authority.
 */
describe("AppointmentNoteButton", () => {
  it("renders NOTHING when there is no note", () => {
    renderNode(<AppointmentNoteButton note={null} />);
    // Not a greyed-out icon: an affordance that opens onto an empty dialog teaches the operator to stop
    // trusting the icon, and then they stop clicking the ones that do have something.
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("renders nothing for an empty string either", () => {
    // "" and absent are the same fact; emr normalises whitespace-only to null, and the UI must not disagree.
    renderNode(<AppointmentNoteButton note="" />);
    expect(screen.queryByRole("button")).not.toBeInTheDocument();
  });

  it("opens the note in a modal", async () => {
    const user = userEvent.setup();
    renderNode(<AppointmentNoteButton note="Wheelchair access — ground-floor room." />);

    await user.click(screen.getByRole("button", { name: /appointment note/i }));

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/wheelchair access/i)).toBeInTheDocument();
  });

  it("states in the modal that the note is NOT clinical", async () => {
    const user = userEvent.setup();
    renderNode(<AppointmentNoteButton note="Sister attending as interpreter." />);
    await user.click(screen.getByRole("button", { name: /appointment note/i }));

    // The boundary is stated where the note is actually read, not only where it is written.
    expect(within(await screen.findByRole("dialog")).getByText(/not a clinical record/i)).toBeInTheDocument();
  });

  it("gives the icon-only trigger an accessible name", () => {
    renderNode(<AppointmentNoteButton note="x" />);
    // Without one a screen-reader user hears "button" and has no idea what it opens.
    expect(screen.getByRole("button", { name: /appointment note/i })).toBeInTheDocument();
  });

  it("has no serious/critical a11y violations with the note open", async () => {
    const user = userEvent.setup();
    const { baseElement } = renderNode(<AppointmentNoteButton note="Interpreter needed — Tigrinya." />);
    await user.click(screen.getByRole("button", { name: /appointment note/i }));
    await screen.findByRole("dialog");

    // baseElement, not container: the dialog portals outside the render root.
    expect(await axe(baseElement, { rules: { "color-contrast": { enabled: false } } })).toHaveNoViolations();
  });
});

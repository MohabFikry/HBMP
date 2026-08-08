import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
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

  it("no longer states the not-clinical boundary — removed by request", () => {
    // This assertion is INVERTED rather than deleted, because what it guarded is worth leaving a mark for.
    // The dialog was the only place in the application saying that a booking note is not a clinical record;
    // the doctor who opens it is reading free text written at a desk by somebody with no clinical authority.
    // Removing it was an explicit product decision. The string is retained in `S.scope`, so restoring the
    // line — and this test — is a one-line change.
    const src = readFileSync(resolve(__dirname, "..", "src/screens/AppointmentNote.tsx"), "utf-8");
    expect(src).toContain("Not a clinical record.");
    expect(src).not.toMatch(/description=\{t\(S\.scope\)\}/);
  });

  it("gives the icon-only trigger an accessible name", () => {
    renderNode(<AppointmentNoteButton note="x" />);
    // Without one a screen-reader user hears "button" and has no idea what it opens.
    expect(screen.getByRole("button", { name: /appointment note/i })).toBeInTheDocument();
  });

  it("names the author in WORDS, and never as a subject id", async () => {
    // The defect: the dialog was passed `noteBy` — a uuid — and rendered "Written by
    // c18b985c-cc5f-42eb-8b79-e41b7b84f975". A receptionist cannot act on that, which makes it the same as no
    // attribution at all, on the one field that exists so somebody can be asked.
    const user = userEvent.setup();
    renderNode(<AppointmentNoteButton note="Sister will interpret." by="Nada Fahmy" at="2026-08-06T07:38:00Z" />);
    await user.click(screen.getByRole("button", { name: /appointment note/i }));

    const dialog = within(await screen.findByRole("dialog"));
    expect(dialog.getByText("Nada Fahmy")).toBeInTheDocument();
    expect(dialog.queryByText(/[0-9a-f]{8}-[0-9a-f]{4}-/i)).not.toBeInTheDocument();
  });

  it("says 'unknown' for a note written before authorship was captured", async () => {
    // Notes predating emr 0022 carry a date and no name. Falling back to the id would put the uuid straight
    // back on screen; saying nothing at all would leave a bare timestamp with no subject.
    const user = userEvent.setup();
    renderNode(<AppointmentNoteButton note="Older arrangement." by={null} at="2026-07-22T08:05:00Z" />);
    await user.click(screen.getByRole("button", { name: /appointment note/i }));

    expect(within(await screen.findByRole("dialog")).getByText(/unknown/i)).toBeInTheDocument();
  });

  it("wraps the note in quotation marks, which now do the separating", async () => {
    // The rule between text and attribution is gone, so the marks carry the job: they say "the following is
    // somebody's own words" at any length, which a bare paragraph under a header row could not.
    const user = userEvent.setup();
    renderNode(<AppointmentNoteButton note="hello" by="Reception" at="2026-08-06T08:17:00Z" />);
    await user.click(screen.getByRole("button", { name: /appointment note/i }));

    const dialog = await screen.findByRole("dialog");
    const body = [...dialog.querySelectorAll("p")].find((p) => p.textContent?.includes("hello"));
    expect(body?.textContent).toBe("\u201chello\u201d");
    // Emphasised, so they read as a frame rather than as punctuation the author typed.
    expect([...(body?.querySelectorAll("span") ?? [])].some((el) => el.style.fontWeight === "700")).toBe(true);
    // And no rule survives the redesign.
    const meta = [...dialog.querySelectorAll("p")].find((p) => p.textContent?.includes("Reception")) as HTMLElement;
    expect(meta.style.borderBlockStart).toBe("");
  });

  it("puts the author and the moment on one row, each behind its own glyph", async () => {
    const user = userEvent.setup();
    renderNode(<AppointmentNoteButton note="hello" by="Reception" at="2026-08-06T08:17:00Z" />);
    await user.click(screen.getByRole("button", { name: /appointment note/i }));

    const dialog = await screen.findByRole("dialog");
    const meta = [...dialog.querySelectorAll("p")]
      .find((p) => p.textContent?.includes("Reception")) as HTMLElement;

    expect(meta.style.justifyContent).toBe("space-between");
    // Two glyphs, both decorative: the labels beside them carry the meaning for a screen reader, and
    // announcing "person" adds a word that says nothing.
    const icons = meta.querySelectorAll("svg");
    expect(icons).toHaveLength(2);
    icons.forEach((i) => expect(i).toHaveAttribute("aria-hidden", "true"));
  });

  it("still says WHICH fact is which to a screen reader", async () => {
    // The glyphs replaced the visible labels. Without the sr-only text the dialog would read out
    // "Reception, 06 Aug 2026" with nothing to say which is the author and which is the moment.
    const user = userEvent.setup();
    renderNode(<AppointmentNoteButton note="hello" by="Reception" at="2026-08-06T08:17:00Z" />);
    await user.click(screen.getByRole("button", { name: /appointment note/i }));

    const dialog = within(await screen.findByRole("dialog"));
    expect(dialog.getByText(/written by/i)).toBeInTheDocument();
    expect(dialog.getByText(/written at/i)).toBeInTheDocument();
  });

  it("gives the body more air when there is no description above it", () => {
    // jsdom performs no layout, so this is asserted against the STYLESHEET rather than a measured box.
    //
    // The single `margin-top: var(--sp3)` on the body was measured against a description: 12px under a muted
    // paragraph reads as a gap, and under a 20px semibold heading the same 12px reads as a collision. This
    // dialog is simply the first modal to go without a description; the adjacent-sibling rule means the ~26
    // other description-less modals in the app get the corrected gap too, instead of each being re-measured
    // by eye when someone notices.
    const css = readFileSync(
      resolve(__dirname, "..", "..", "design-system", "src", "styles", "components.css"), "utf-8");
    expect(css).toMatch(/\.mrs-modal > h2 \+ \.mrs-modal-body \{\s*margin-top: var\(--sp5\)/);
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

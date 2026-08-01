import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { LICENCE_KINDS, LicenceStatus, licenceLabel, licenceStateOf } from "../src/screens/branch/LicenceStatus";
import { ThemeProvider } from "@mersal/design-system";

/**
 * 25.7 (design 42 §6) — "Licence status uses FOUR cues — Valid / Expiring / Expired differ by hue AND icon
 * AND shape AND word. A grey chip meaning 'may not legally practise' is a design failure."
 *
 * That sentence is the acceptance criterion, so this is the test for it. The three assertions below are, in
 * order: the states map to DISTINCT chip kinds (which is what supplies hue+icon+shape); no state is grey;
 * and the word says what it MEANS rather than merely what it is.
 */
describe("licence status carries four cues", () => {
  const wrap = (ui: React.ReactNode) => render(<ThemeProvider>{ui}</ThemeProvider>);

  it("maps the three states to three DISTINCT chip kinds", () => {
    // `StatusChip` derives hue, icon AND shape from `kind`, so distinct kinds means distinct on all three.
    // Equal kinds would collapse two states into one appearance while the code still "handles" both.
    const kinds = [LICENCE_KINDS.valid, LICENCE_KINDS.expiring, LICENCE_KINDS.expired];
    expect(new Set(kinds).size).toBe(3);
  });

  it("NEVER renders a grey chip for a licence state", () => {
    // `neu` is the grey kind and is the exact failure the design names. An expired licence means "this
    // clinician may not legally practise", and grey is what the eye skips.
    for (const [state, kind] of Object.entries(LICENCE_KINDS)) {
      expect(kind, `licence state '${state}' must not be grey`).not.toBe("neu");
    }
  });

  it("gives EXPIRED a word that states the consequence, not just the fact", () => {
    // "Expired" alone is a fact about a date. "cannot be booked" is what the reader has to act on, and it is
    // the reason they will act today rather than next week.
    expect(licenceLabel("expired", null, "en").toLowerCase()).toContain("cannot be booked");
    expect(licenceLabel("expired", null, "ar")).toContain("لا يمكن الحجز");
  });

  it("keeps 'no licence recorded' distinct from 'expired'", () => {
    // Collapsing them would put a red EXPIRED chip against every nurse who never had a licence number — a
    // false alarm on a worklist is how a real one stops being read.
    expect(licenceStateOf({ licenseExpiry: null })).toBe("notRecorded");
    expect(LICENCE_KINDS.notRecorded).not.toBe(LICENCE_KINDS.expired);
  });

  it("derives the state from the SERVER's answer where it gives one", () => {
    // Re-deriving validity on the client is how a screen and a booking gate end up disagreeing about the
    // same doctor on the same day.
    expect(licenceStateOf({ licenseExpiry: "2099-01-01", licenceValid: false })).toBe("expired");
    expect(licenceStateOf({ licenseExpiry: "2099-01-01", licenceValid: true, daysUntilExpiry: 4000 })).toBe("valid");
    expect(licenceStateOf({ licenseExpiry: "2026-12-01", licenceValid: true, daysUntilExpiry: 12 })).toBe("expiring");
  });

  it("renders the day count in the expiring label, so the row says how urgent it is", () => {
    wrap(<LicenceStatus licenseExpiry="2026-12-01" licenceValid daysUntilExpiry={12} lang="en" />);
    expect(screen.getByText(/expires in 12 days/i)).toBeInTheDocument();
  });

  it("renders an icon and a word together, never a word alone", () => {
    const { container } = wrap(<LicenceStatus licenseExpiry="2020-01-01" licenceValid={false} daysUntilExpiry={-30} lang="en" />);
    const chip = container.querySelector(".mrs-chip");
    expect(chip).toBeTruthy();
    // The shape cue is a data attribute the stylesheet keys off; its presence is what makes the chip
    // distinguishable in a monochrome print-out or to a user who has overridden colours.
    expect(chip?.getAttribute("data-shape")).toBeTruthy();
    expect(chip?.querySelector("svg")).toBeTruthy();
    expect(chip?.textContent ?? "").toMatch(/EXPIRED/);
  });
});

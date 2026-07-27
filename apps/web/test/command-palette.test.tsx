import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { ThemeProvider } from "@mersal/design-system";
import type { ReactNode } from "react";
import { renderApp } from "./helpers";
import { CommandPalette } from "../src/shell/CommandPalette";
import { PORTALS } from "../src/portals/catalog";
import type { Section } from "../src/portals/catalog";

/**
 * Phase 18.F2 — the command palette.
 *
 * 18.D2 deleted the app-bar search because it was bound to nothing and the "/" shortcut trained users to
 * reach for it. This is the replacement, and the tests below are mostly about the two properties that make
 * it safe rather than just convenient: it lists ONLY what the caller may open, and it is operable entirely
 * from the keyboard by someone who cannot see the highlight.
 */

const wrap = ({ children }: { children: ReactNode }) => <ThemeProvider>{children}</ThemeProvider>;

const sections: Section[] = [
  { key: "utilization", path: "utilization", label: { en: "Utilization", ar: "الاستخدام" }, group: { en: "Finance", ar: "المالية" }, icon: "chart", permission: "finance.utilization" },
  { key: "settlements", path: "settlements", label: { en: "Settlements", ar: "التسويات" }, group: { en: "Finance", ar: "المالية" }, icon: "doc", permission: "finance.settlements" },
  { key: "exports", path: "exports", label: { en: "Exports", ar: "التصدير" }, group: { en: "Finance", ar: "المالية" }, icon: "doc", permission: "finance.export" },
];

function renderPalette(over: Partial<React.ComponentProps<typeof CommandPalette>> = {}) {
  const onNavigate = vi.fn();
  const onClose = vi.fn();
  const utils = render(
    <CommandPalette open onClose={onClose} sections={sections} portalBase="finance" onNavigate={onNavigate} {...over} />,
    { wrapper: wrap },
  );
  return { ...utils, onNavigate, onClose };
}

describe("command palette — navigation", () => {
  it("matches a subsequence, so a half-remembered name still finds the destination", async () => {
    // "fset" → "Finance · Settlements". A user reaching for the palette is recalling a place, not typing
    // its name; substring matching would fail on exactly the input people actually produce.
    const user = userEvent.setup();
    renderPalette();

    await user.type(screen.getByRole("combobox"), "fset");

    const options = screen.getAllByRole("option");
    expect(options[0]).toHaveTextContent(/Settlements/);
  });

  it("ranks the tighter, earlier match first", async () => {
    const user = userEvent.setup();
    renderPalette();

    await user.type(screen.getByRole("combobox"), "exp");

    expect(screen.getAllByRole("option")[0]).toHaveTextContent(/Exports/);
  });

  it("navigates to the full portal path on Enter", async () => {
    const user = userEvent.setup();
    const { onNavigate, onClose } = renderPalette();

    await user.type(screen.getByRole("combobox"), "settle");
    await user.keyboard("{Enter}");

    expect(onNavigate).toHaveBeenCalledWith("/finance/settlements");
    expect(onClose).toHaveBeenCalled();
  });

  it("says so when nothing matches instead of showing an empty box", async () => {
    const user = userEvent.setup();
    renderPalette();
    await user.type(screen.getByRole("combobox"), "zzzz");

    expect(screen.getByText(/no matching section/i)).toBeInTheDocument();
    expect(screen.queryAllByRole("option")).toHaveLength(0);
  });
});

describe("command palette — it cannot leak a destination the user may not open", () => {
  it("lists only the sections it is given, which are already permission-filtered", () => {
    // The security property. The palette is handed the SAME array the nav rail renders — the caller's
    // filtered set — so it has no path to a section the user cannot open. A palette that listed everything
    // and 403'd on selection would be an enumeration oracle for the whole platform: you would learn every
    // capability that exists, and which ones you are missing, without touching a protected endpoint.
    renderPalette({ sections: [sections[0]!] });

    expect(screen.getAllByRole("option")).toHaveLength(1);
    expect(screen.queryByText(/Settlements/)).not.toBeInTheDocument();
  });

  it("shows nothing at all for a caller with no accessible sections", () => {
    renderPalette({ sections: [] });
    expect(screen.getByText(/no matching section/i)).toBeInTheDocument();
  });

  it("never surfaces a section from another portal", () => {
    // Cross-portal leakage would be the subtle version of the same bug: a finance user seeing "Approvals ·
    // Worklist" in the palette learns the shape of a portal they have no role for.
    const financeKeys = new Set(sections.map((s) => s.key));
    const otherPortalKeys = PORTALS
      .filter((p) => p.base !== "finance")
      .flatMap((p) => p.sections.map((s) => s.key));

    renderPalette();
    for (const key of otherPortalKeys.filter((k) => !financeKeys.has(k)).slice(0, 10))
      expect(screen.queryByTestId(`cmdk-opt-${key}`)).not.toBeInTheDocument();
  });
});

describe("command palette — keyboard and assistive tech", () => {
  it("moves the selection with the arrow keys and opens the highlighted row", async () => {
    const user = userEvent.setup();
    const { onNavigate } = renderPalette();
    await user.click(screen.getByRole("combobox"));

    await user.keyboard("{ArrowDown}{Enter}");

    // Second entry in the unfiltered list.
    expect(onNavigate).toHaveBeenCalledWith("/finance/settlements");
  });

  it("wraps at both ends rather than dead-ending", async () => {
    // ArrowUp from the first row goes to the LAST. Without wrapping, the fastest way to the bottom of a
    // list is a dead key press, which reads as the palette having stopped responding.
    const user = userEvent.setup();
    const { onNavigate } = renderPalette();
    await user.click(screen.getByRole("combobox"));

    await user.keyboard("{ArrowUp}{Enter}");

    expect(onNavigate).toHaveBeenCalledWith("/finance/exports");
  });

  it("closes on Escape", async () => {
    const user = userEvent.setup();
    const { onClose } = renderPalette();
    await user.click(screen.getByRole("combobox"));
    await user.keyboard("{Escape}");
    expect(onClose).toHaveBeenCalled();
  });

  it("keeps focus in the input and announces the highlighted option via aria-activedescendant", async () => {
    // The reason for combobox + aria-activedescendant rather than roving tabindex: focus must STAY in the
    // input so typing keeps narrowing the list, while a screen reader still announces which option is
    // current. Moving focus to the option would break the search-as-you-type interaction entirely.
    const user = userEvent.setup();
    renderPalette();
    const input = screen.getByRole("combobox");
    await user.click(input);

    await user.keyboard("{ArrowDown}");

    expect(input).toHaveFocus();
    expect(input).toHaveAttribute("aria-activedescendant", "cmdk-opt-settlements");
  });

  it("has no serious or critical axe violations", async () => {
    const { container } = renderPalette();
    const results = await axe(container, { rules: { "color-contrast": { enabled: false } } });
    expect(results.violations.filter((v) => v.impact === "serious" || v.impact === "critical")).toEqual([]);
  });
});

describe("app-bar search field", () => {
  it("is the original text input, restored at the product owner's direction", async () => {
    // Reopens audit U5 KNOWINGLY: the field accepts typing and has nowhere to send it until record search
    // exists server-side (which needs min-necessary field rules + a PHI-read audit). Requested so the
    // affordance stays visible while that is designed; recorded here and in docs/PHASE-18-TODO.md so it is
    // a tracked decision rather than a regression.
    renderApp("/", "medical_approval");

    const field = await screen.findByRole("searchbox", { name: /search/i })
      .catch(() => screen.findByRole("textbox", { name: /search/i }));
    expect(field).toBeInTheDocument();
  });

  it("does not steal \"/\" from someone typing into a field", async () => {
    // The one part of the old behaviour worth keeping guarded: a bare "/" must never be intercepted
    // mid-entry — a dose, a date and a code all contain one.
    const user = userEvent.setup();
    renderApp("/", "medical_approval");
    await screen.findByRole("banner");

    const anyInput = document.createElement("input");
    document.body.appendChild(anyInput);
    anyInput.focus();
    await user.keyboard("2/3");

    expect(anyInput).toHaveValue("2/3");
    anyInput.remove();
  });
});

import { afterEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { useEffect, useState } from "react";
import { SegmentedControl, Tabs, ThemeProvider, useTheme } from "../src";

/**
 * 2026-08-09 audit — arrow keys follow the writing direction.
 *
 * <p>Everything else in this system mirrors through logical CSS and needs no per-direction code. Arrow keys
 * are the exception, because they are spatial in a way CSS cannot express: `ArrowRight` means "the thing to
 * the right of this", and in Arabic the thing to the right is the PREVIOUS one. Both controls got it wrong —
 * `SegmentedControl` hard-coded the LTR mapping, and Radix `Tabs` was never told the document's direction, so
 * it assumed `ltr`.</p>
 *
 * <p>These tests are written the way the defect had to be found: by pressing the key and asking which segment
 * ends up focused. An assertion on the handler's internals would have passed throughout, because the handler
 * was self-consistent — it was consistent with the wrong direction.</p>
 */

/**
 * Switches the provider into Arabic (which is what sets `dir=rtl` on the document) and renders the control
 * only once it has. In an effect rather than during render — setting a parent's state while rendering a child
 * is a React warning — and the children must not mount under `ltr` first, because Radix reads the direction
 * when its roving-focus group initialises.
 */
function InArabic({ children }: { children: React.ReactNode }) {
  const { lang, setLang } = useTheme();
  useEffect(() => {
    if (lang !== "ar") setLang("ar");
  }, [lang, setLang]);
  return lang === "ar" ? <>{children}</> : null;
}

const SEGMENTS = [
  { value: "all", label: "All" },
  { value: "mine", label: "Mine" },
  { value: "urgent", label: "Urgent" },
];

function Filter() {
  const [value, setValue] = useState("all");
  return <SegmentedControl segments={SEGMENTS} value={value} onChange={setValue} aria-label="Scope" />;
}

const TAB_ITEMS = [
  { value: "one", label: "One", content: <p>first</p> },
  { value: "two", label: "Two", content: <p>second</p> },
  { value: "three", label: "Three", content: <p>third</p> },
];

function TabBar() {
  const [value, setValue] = useState("one");
  return <Tabs items={TAB_ITEMS} value={value} onValueChange={setValue} aria-label="Sections" />;
}

afterEach(() => {
  // The provider persists the language, and `document.dir` outlives a render. Left set, the next file in the
  // run starts in Arabic — the kind of cross-test leak that makes an unrelated suite fail on a Tuesday.
  localStorage.clear();
  document.documentElement.dir = "ltr";
  vi.unstubAllGlobals();
});

describe("SegmentedControl arrow keys", () => {
  it("moves forward on ArrowRight in English", async () => {
    const user = userEvent.setup();
    render(<ThemeProvider><Filter /></ThemeProvider>);

    await user.click(screen.getByRole("radio", { name: "All" }));
    await user.keyboard("{ArrowRight}");

    expect(screen.getByRole("radio", { name: "Mine" })).toHaveFocus();
  });

  it("moves forward on ArrowLEFT in Arabic, because the next segment is drawn to the left", async () => {
    const user = userEvent.setup();
    render(<ThemeProvider><InArabic><Filter /></InArabic></ThemeProvider>);

    await user.click(await screen.findByRole("radio", { name: "All" }));
    await user.keyboard("{ArrowLeft}");

    expect(screen.getByRole("radio", { name: "Mine" })).toHaveFocus();
  });

  it("moves BACK on ArrowRight in Arabic — the case that was inverted", async () => {
    const user = userEvent.setup();
    render(<ThemeProvider><InArabic><Filter /></InArabic></ThemeProvider>);

    await user.click(await screen.findByRole("radio", { name: "Urgent" }));
    await user.keyboard("{ArrowRight}");

    expect(screen.getByRole("radio", { name: "Mine" })).toHaveFocus();
  });

  it("keeps ArrowDown meaning 'the next one' in both languages", async () => {
    // Vertical order does not mirror. A reader reaching for ArrowDown means the same thing either way, and
    // flipping it along with the horizontal pair would be a second bug wearing the first one's fix.
    const user = userEvent.setup();
    render(<ThemeProvider><InArabic><Filter /></InArabic></ThemeProvider>);

    await user.click(await screen.findByRole("radio", { name: "All" }));
    await user.keyboard("{ArrowDown}");

    expect(screen.getByRole("radio", { name: "Mine" })).toHaveFocus();
  });
});

describe("Tabs arrow keys", () => {
  it("moves forward on ArrowRight in English", async () => {
    const user = userEvent.setup();
    render(<ThemeProvider><TabBar /></ThemeProvider>);

    await user.click(screen.getByRole("tab", { name: "One" }));
    await user.keyboard("{ArrowRight}");

    expect(screen.getByRole("tab", { name: "Two" })).toHaveFocus();
  });

  it("moves forward on ArrowLEFT in Arabic — Radix has to be told the direction", async () => {
    const user = userEvent.setup();
    render(<ThemeProvider><InArabic><TabBar /></InArabic></ThemeProvider>);

    await user.click(await screen.findByRole("tab", { name: "One" }));
    await user.keyboard("{ArrowLeft}");

    expect(screen.getByRole("tab", { name: "Two" })).toHaveFocus();
  });
});

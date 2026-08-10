import { describe, expect, it, vi, beforeEach } from "vitest";
import type { ReactNode } from "react";
import { act, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { axe } from "jest-axe";
import { renderNode } from "./helpers";
import { ScrollToTop } from "../src/shell/ScrollToTop";

/**
 * The floating "back to top" control that replaced the profile's sticky tab bar.
 *
 * <b>What these assert, and what they cannot.</b> jsdom performs no layout and never scrolls anything, so
 * there is no real scroll position to read. What IS real is the component's contract with the pane: it
 * subscribes to the pane's scroll events, decides from `scrollTop`, and writes `scrollTop` back. Driving
 * those directly tests the wiring that actually broke in review — appearing at the wrong time, never
 * disappearing, and scrolling the document instead of the pane. The visual placement is stated in CSS and
 * covered by the stacking assertion in `profile-tab-bar-wrap`.
 */

/** The shell's scrollport, which is what `ScrollToTop` looks for and what the whole app scrolls inside. */
function withPane(node: ReactNode) {
  return renderNode(<div className="app-main">{node}</div>);
}

/** jsdom does not scroll, so move the pane by hand and tell the component the way a browser would. */
function scrollPaneTo(y: number) {
  const pane = document.querySelector<HTMLElement>(".app-main")!;
  pane.scrollTop = y;
  act(() => {
    pane.dispatchEvent(new Event("scroll"));
  });
}

beforeEach(() => {
  // The component coalesces scroll handling into a frame; run callbacks immediately so assertions are not
  // racing rAF. Returns a number because the component stores the handle and cancels it on unmount.
  vi.stubGlobal("requestAnimationFrame", (cb: FrameRequestCallback) => {
    cb(0);
    return 1;
  });
  vi.stubGlobal("cancelAnimationFrame", () => {});
});

const button = () => screen.queryByRole("button", { name: /back to top/i });

describe("back to top", () => {
  it("is absent until the pane has actually been scrolled", () => {
    withPane(<ScrollToTop />);
    expect(button(), "nothing to go back to at the top of the page").toBeNull();
  });

  it("stays absent for a scroll too short to be worth a control", () => {
    withPane(<ScrollToTop threshold={320} />);
    scrollPaneTo(100);
    expect(button(), "the top is still one flick away — a floating button would just cover content").toBeNull();
  });

  it("appears once the pane is past the threshold, and leaves again on the way back up", async () => {
    withPane(<ScrollToTop threshold={320} />);

    scrollPaneTo(321);
    await waitFor(() => expect(button()).not.toBeNull());

    // The half that regressed in review: it appeared and then never went away.
    scrollPaneTo(0);
    await waitFor(() => expect(button()).toBeNull());
  });

  it("returns the PANE to the top, not the document", async () => {
    withPane(<ScrollToTop threshold={320} />);
    const pane = document.querySelector<HTMLElement>(".app-main")!;
    // jsdom implements neither form of scrollTo; the component falls back to assigning scrollTop, which is
    // the same fallback a browser without the options form would take.
    const windowScroll = vi.fn();
    vi.stubGlobal("scrollTo", windowScroll);

    scrollPaneTo(900);
    await waitFor(() => expect(button()).not.toBeNull());
    await userEvent.click(button()!);

    expect(pane.scrollTop, "the shell scrolls .app-main; scrolling the window would move nothing").toBe(0);
    expect(windowScroll).not.toHaveBeenCalled();
  });

  it("takes the keyboard with it, and leaves no permanent tab stop behind", async () => {
    withPane(<ScrollToTop threshold={320} />);
    const pane = document.querySelector<HTMLElement>(".app-main")!;

    scrollPaneTo(900);
    await waitFor(() => expect(button()).not.toBeNull());
    await userEvent.click(button()!);

    // Without this a screen-reader user presses "back to top" and is still reading the footer.
    expect(document.activeElement, "focus should follow the viewport").toBe(pane);

    // The tabindex is a means, not a leftover: it must not survive on chrome every screen shares.
    act(() => pane.blur());
    expect(pane.hasAttribute("tabindex"), "the -1 was removed on blur").toBe(false);
  });

  it("jumps instead of gliding when the reader asked for less motion", async () => {
    const pane = () => document.querySelector<HTMLElement>(".app-main")!;
    withPane(<ScrollToTop threshold={320} />);
    scrollPaneTo(900);
    await waitFor(() => expect(button()).not.toBeNull());

    // Give the pane a real scrollTo so the behaviour argument is observable.
    const calls: ScrollToOptions[] = [];
    pane().scrollTo = (options?: ScrollToOptions | number) => {
      if (typeof options === "object" && options) calls.push(options);
    };

    vi.stubGlobal("matchMedia", (q: string) => ({
      matches: q.includes("prefers-reduced-motion"),
      media: q,
      addEventListener: () => {},
      removeEventListener: () => {},
    }));

    await userEvent.click(button()!);
    expect(calls[0]?.behavior, "a long smooth scroll is exactly what this preference is about").toBe("auto");
  });

  it("has an accessible name and no axe violations", async () => {
    const { container } = withPane(<ScrollToTop threshold={320} />);
    scrollPaneTo(900);
    await waitFor(() => expect(button()).not.toBeNull());
    // Icon-only: the name comes from aria-label, and without it this is a button that reads as "button".
    expect(button()).toHaveAccessibleName(/back to top/i);
    expect(await axe(container)).toHaveNoViolations();
  });
});

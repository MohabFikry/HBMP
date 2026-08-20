import { useCallback, useEffect, useRef, useState } from "react";
import { Icon } from "@mersal/design-system";
import { useLoc } from "../screens/_shared";

/**
 * A floating "back to top" control for a long scrolling pane.
 *
 * <b>Why this exists.</b> It replaces the patient profile's sticky tab bar. That bar pinned itself so the
 * seven tabs stayed reachable down a long file, and it worked — but only above 60rem, because below that the
 * tabs wrap to three rows and a 156px permanent bar eats a quarter of a phone viewport. So the affordance was
 * absent exactly where the scroll is longest and a person is least able to flick back. This is the inverse
 * trade: nothing is held on screen while you read, and one control appears once you have scrolled far enough
 * to want it — at every width, which is the whole point of the swap.
 *
 * <b>It finds its own scrollport.</b> The document does not scroll in this shell; `.app-main` does
 * (`overflow-y: auto`), and it is the element a sticky child had to cancel padding against. Rather than take
 * a ref through three components, the anchor span below asks the DOM which pane it landed in. Falling back to
 * the window keeps the component usable in a test or a future shell that scrolls the document instead — a
 * component that throws because it could not find one specific class name would be the more brittle choice.
 *
 * <b>Focus goes with the scroll.</b> Scrolling alone moves the viewport and leaves the keyboard where it was,
 * so a screen-reader or keyboard user would press the button and then still be tabbing through the footer.
 * The pane is given `tabindex="-1"` just long enough to receive focus, and it is removed on blur so a stray
 * `-1` is not left on shared chrome.
 */

const STR = {
  /** "Back to top", not "scroll to top": the destination is what matters, and it reads the same aloud. */
  label: { en: "Back to top", ar: "العودة إلى الأعلى" },
};

/**
 * How far the pane must have travelled before the button is worth offering. Roughly one viewport on a phone:
 * below this the top is still a short flick away and a floating control is just something covering content.
 */
const THRESHOLD_PX = 320;

export function ScrollToTop({ threshold = THRESHOLD_PX }: { threshold?: number }) {
  const t = useLoc();
  const [shown, setShown] = useState(false);
  const anchor = useRef<HTMLSpanElement>(null);
  const port = useRef<HTMLElement | null>(null);

  useEffect(() => {
    const pane =
      anchor.current?.closest<HTMLElement>(".app-main") ?? document.querySelector<HTMLElement>(".app-main");
    port.current = pane;
    const target: HTMLElement | Window = pane ?? window;

    // Coalesced into a frame: scroll fires far more often than the answer to "past the threshold?" changes,
    // and this listener runs on every pixel of a long clinical file.
    //
    // `scheduled` is its own flag rather than "is the handle non-zero", because those two are not the same
    // thing and the difference is a hang. Storing the handle as the flag assumes the callback runs strictly
    // AFTER `requestAnimationFrame` returns; if it ever runs during the call, the clear happens first and the
    // handle is written over the top of it, leaving the flag permanently set and every later scroll ignored —
    // the button then appears once and never updates again. Setting the flag before scheduling is correct
    // either way, and costs nothing.
    let scheduled = false;
    let handle = 0;
    const read = () => {
      scheduled = false;
      setShown((pane ? pane.scrollTop : window.scrollY) > threshold);
    };
    const onScroll = () => {
      if (scheduled) return;
      scheduled = true;
      handle = requestAnimationFrame(read);
    };

    target.addEventListener("scroll", onScroll, { passive: true });
    read(); // a pane restored mid-scroll (back navigation) should show the button immediately
    return () => {
      target.removeEventListener("scroll", onScroll);
      if (handle) cancelAnimationFrame(handle);
    };
  }, [threshold]);

  const toTop = useCallback(() => {
    const pane = port.current;
    // Honour the OS setting: a long smooth scroll is exactly the motion this preference is about.
    const reduced = window.matchMedia?.("(prefers-reduced-motion: reduce)")?.matches ?? false;
    const behavior: ScrollBehavior = reduced ? "auto" : "smooth";

    if (!pane) {
      window.scrollTo?.({ top: 0, behavior });
      return;
    }
    // jsdom implements neither `scrollTo` nor smooth scrolling; assigning scrollTop is what it does support,
    // and it is also the correct fallback in any browser missing the options form.
    if (typeof pane.scrollTo === "function") pane.scrollTo({ top: 0, behavior });
    else pane.scrollTop = 0;

    // Take the keyboard with it. Removed again on blur so this does not leave a permanent tab stop on a pane
    // every other screen shares.
    if (!pane.hasAttribute("tabindex")) {
      pane.setAttribute("tabindex", "-1");
      pane.addEventListener("blur", () => pane.removeAttribute("tabindex"), { once: true });
    }
    pane.focus({ preventScroll: true });
  }, []);

  return (
    <>
      {/* Presentational only — it exists so the effect can ask which pane it is inside. */}
      <span ref={anchor} className="scrolltop-anchor" aria-hidden="true" />
      {shown && (
        <button type="button" className="scrolltop" onClick={toTop} aria-label={t(STR.label)} title={t(STR.label)}>
          {/* The design system ships one chevron, pointing down; CSS turns it. Rotating a shared glyph beats
              adding a second path that has to be kept in visual step with it. */}
          <Icon name="chevron" aria-hidden />
        </button>
      )}
    </>
  );
}

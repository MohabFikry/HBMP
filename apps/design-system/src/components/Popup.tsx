import { useCallback, useEffect, useLayoutEffect, useState } from "react";
import type { CSSProperties, ReactNode, RefObject } from "react";
import { createPortal } from "react-dom";

/**
 * A popup that is positioned against a control but does NOT live inside it.
 *
 * ============================================================================================================
 * WHY THIS EXISTS
 * ============================================================================================================
 * `Select` and `Combobox` used to render their option list as an `position: absolute` child of the control.
 * That is the obvious arrangement and it is wrong for one reason: **an ancestor that scrolls clips its
 * descendants, whatever their z-index.** `.mrs-modal` is `overflow: auto`, so a picker opened near the bottom
 * of a dialog had its list cut off at the dialog's edge. Measured on the real stylesheet — an eight-option
 * list in a 520px modal ended 267px past the modal's bottom edge, so seven of the eight options could only be
 * reached by scrolling the DIALOG, which moves the control you just pressed out from under the cursor.
 *
 * z-index cannot fix this. Stacking order decides what paints on top of what; clipping happens first and is
 * decided by the ancestor chain.
 *
 * <b>`position: fixed` alone does not fix it either</b>, which is the trap worth writing down. A fixed
 * element normally escapes ancestor overflow because its containing block is the viewport — but `transform`,
 * `filter`, `perspective`, `will-change`, `contain: paint` and `backdrop-filter` each make an ancestor into a
 * containing block for fixed descendants, and `.mrs-modal` carries `backdrop-filter: blur(28px)` for the
 * glass surface. Inside that modal a fixed popup is contained and clipped exactly like an absolute one.
 *
 * So the list is PORTALLED out of the control and positioned against the control's measured rect. It is the
 * only arrangement no ancestor can clip.
 *
 * ============================================================================================================
 * WHAT THIS COSTS, AND WHY IT IS STILL WORTH IT
 * ============================================================================================================
 * A portalled popup is no longer a DOM descendant of the control, so three things stop being automatic and
 * are handled by the callers rather than assumed:
 *
 * <ul>
 *   <li><b>Outside-click detection.</b> "Is the click inside the control?" must now also ask "…or inside the
 *       popup?", or the first click on an option closes the list before it can commit. Both callers check
 *       both refs.</li>
 *   <li><b>Inherited CSS.</b> The popup no longer inherits from the row it was opened in. That is a net win —
 *       `popup-not-restyled.test.ts` exists because a staged-diagnosis row's `display: grid` reached into an
 *       option list and laid every label out into a zero-width box — but it also means the popup gets its
 *       colours from the document, not from the surface it sits over. Both lists already set their own
 *       background, border and colour, so there is nothing to inherit.</li>
 *   <li><b>Pointer events.</b> Radix's modal dialog sets `pointer-events: none` on the body while it is open
 *       and re-enables them on the dialog itself. A popup appended to the body would inherit `none` and
 *       silently refuse every click. `useAnchoredPopup` sets `pointer-events: auto` inline for exactly
 *       this — see the note at that declaration for why it is not in the stylesheet.</li>
 * </ul>
 *
 * The a11y tree is unaffected: focus never moves to the list in either control — it stays on the trigger or
 * the input and the active option is carried by `aria-activedescendant` — so a portalled list does not break
 * a focus trap and does not need to be inside one. `aria-controls` ties the two together across the portal.
 */

/** Distance between the control and its popup — `--sp1`, stated here because layout maths needs a number. */
const GAP = 4;

/** Below this, a popup is a sliver rather than a list, and it is better to flip and overlap slightly. */
const MIN_USEFUL = 120;

export interface AnchoredPopupPosition {
  /** Apply to the popup element. Fixed positioning against the anchor's measured rect. */
  style: CSSProperties;
  /** True when there was no room below and the popup was placed above the anchor. */
  flipped: boolean;
}

/**
 * Measure `anchorRef` and return the fixed-position style for a popup rendered against it.
 *
 * <p>Re-measures on open, on any scroll anywhere (capture phase, so an ancestor scrollport counts), on
 * viewport resize, and whenever the popup's own size changes — a list that filters down from 200 rows to 3
 * has a different height, and a popup flipped above the control has to move when it shrinks.</p>
 *
 * <p>The available space is published as `--popup-avail` rather than written to `max-block-size` directly, so
 * the stylesheet keeps the last word: `min(var(--scroll-picker), var(--popup-avail))` means the design token
 * still caps the popup at its normal height and the measurement only ever makes it shorter.</p>
 */
export function useAnchoredPopup(
  anchorRef: RefObject<HTMLElement | null>,
  popupRef: RefObject<HTMLElement | null>,
  open: boolean,
): AnchoredPopupPosition {
  const [pos, setPos] = useState<AnchoredPopupPosition>({ style: {}, flipped: false });

  const measure = useCallback(() => {
    const anchor = anchorRef.current;
    if (!anchor) return;
    const r = anchor.getBoundingClientRect();
    const vh = window.innerHeight;
    const vw = window.innerWidth;

    const below = vh - r.bottom - GAP;
    const above = r.top - GAP;
    // Flip only when below is genuinely unusable AND above is better. Preferring "below" keeps the reading
    // order of the control and its list matching the reading order of the page in the common case.
    const flipped = below < MIN_USEFUL && above > below;
    const avail = Math.max(MIN_USEFUL, Math.floor(flipped ? above : below));

    // RTL is read off the anchor's computed direction rather than from the theme: a portalled element is a
    // child of <body>, so it does not inherit the direction of the pane it belongs to, and a control inside
    // an explicitly `dir`-scoped subtree (a bidi phone field, an LTR code column) can differ from the page.
    const rtl = window.getComputedStyle(anchor).direction === "rtl";

    setPos({
      flipped,
      style: {
        position: "fixed",
        // Inline, not in the stylesheet, and it is not a style choice. Radix's modal dialog sets
        // `pointer-events: none` on <body> while it is open and re-enables them on the dialog itself; this
        // popup is portalled to the body, so it inherits the `none` and would swallow every click on an
        // option — the control would look right and do nothing. Declaring it here means the guarantee
        // travels with the portalling that creates the problem, and holds even where the stylesheet has not
        // been loaded (which is exactly the case in the jsdom suite, where this was caught).
        pointerEvents: "auto",
        // The popup is at least as wide as the control it belongs to, and may grow for a long option label.
        minInlineSize: r.width,
        // Never wider than the viewport it has to fit in, whichever edge it is anchored to.
        maxInlineSize: rtl ? r.right : vw - r.left,
        ...(flipped ? { bottom: Math.round(vh - r.top + GAP) } : { top: Math.round(r.bottom + GAP) }),
        ...(rtl ? { right: Math.round(vw - r.right) } : { left: Math.round(r.left) }),
        ["--popup-avail" as string]: `${avail}px`,
      },
    });
  }, [anchorRef]);

  // Layout effect, not effect: the popup must never paint at 0,0 for a frame before jumping to the control.
  useLayoutEffect(() => {
    if (open) measure();
  }, [open, measure]);

  useEffect(() => {
    if (!open) return;
    // Capture phase: a scroll inside `.mrs-modal` or `.mrs-wl-scroll` does not bubble to window, and those
    // are precisely the ancestors this component exists to escape.
    window.addEventListener("scroll", measure, true);
    window.addEventListener("resize", measure);

    // The popup's height changes as the list filters. jsdom has no ResizeObserver, and a test environment
    // that performs no layout has nothing for it to observe either, so its absence is not an error.
    const ro = typeof ResizeObserver === "undefined" || !popupRef.current
      ? null
      : new ResizeObserver(measure);
    if (ro && popupRef.current) ro.observe(popupRef.current);

    return () => {
      window.removeEventListener("scroll", measure, true);
      window.removeEventListener("resize", measure);
      ro?.disconnect();
    };
  }, [open, measure, popupRef]);

  return pos;
}

/**
 * Render `children` at the end of `<body>`, outside every scrollport on the page.
 *
 * <p>Returns `null` rather than portalling when there is no document — the SSR/`happy-dom` guard. Callers
 * already render the popup only while open, so this does not need an `open` prop of its own.</p>
 */
export function PopupPortal({ children }: { children: ReactNode }) {
  if (typeof document === "undefined") return null;
  return createPortal(children, document.body);
}

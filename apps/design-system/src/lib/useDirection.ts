import { useContext } from "react";
import { ThemeContext } from "../theme/ThemeProvider";

/**
 * The writing direction a component should navigate by.
 *
 * ============================================================================================================
 * WHY A COMPONENT NEEDS THIS AT ALL
 * ============================================================================================================
 * Almost nothing in this design system branches on direction — layout mirrors through logical CSS properties
 * (`inset-inline-start`, `margin-inline`), which is the whole reason there is no per-component RTL code. But
 * ARROW KEYS are not layout. `ArrowRight` means "the item to the right of this one", and in Arabic the item to
 * the right is the PREVIOUS one. CSS cannot express that; only the keydown handler can.
 *
 * The 2026-08-09 audit found it inverted on `SegmentedControl` — the spec's signature filter control — and on
 * Radix `Tabs`, which was never told the document's direction and so defaulted to `ltr`. In Arabic, arrowing
 * "forward" walked the focus backwards through both.
 *
 * ============================================================================================================
 * WHY IT READS THE CONTEXT DIRECTLY RATHER THAN CALLING useTheme()
 * ============================================================================================================
 * `useTheme()` throws outside a `ThemeProvider`, which is right for a screen and wrong for a primitive: a
 * `SegmentedControl` rendered in a component test, a Storybook story or an isolated fixture would start
 * crashing for wanting to know which way its arrow keys point. So the context is optional here, and a missing
 * provider falls back to the document's own `dir` — which `ThemeProvider` sets anyway, so the two agree
 * whenever both exist.
 */
export function useDirection(): "ltr" | "rtl" {
  const theme = useContext(ThemeContext);
  if (theme) return theme.dir;
  if (typeof document !== "undefined" && document.documentElement.dir === "rtl") return "rtl";
  return "ltr";
}

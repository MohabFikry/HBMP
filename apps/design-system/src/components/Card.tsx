import type { HTMLAttributes, ReactNode } from "react";
import { cx } from "../lib/cx";

export interface CardProps extends HTMLAttributes<HTMLDivElement> {
  /** `glass` = level-2 floating chrome (content must sit on inner opaque blocks). Default solid level-1. */
  material?: "solid" | "glass";
  as?: "div" | "section" | "aside";
  /**
   * Carry the standard inset (`--sp4`) rather than leaving it to the screen.
   *
   * <p><b>Why this is a prop and not the default.</b> A card is a surface, and a surface that always insets
   * its content cannot hold a full-bleed table or a nested panel — so the default stays bare and the screens
   * that want padding say so.</p>
   *
   * <p><b>Why it exists at all.</b> Because "the screen sets its own" turned out to mean "a stylesheet sets
   * it from three selectors away, based on how deeply the card happens to be nested". The policy portal
   * carried four positional rules plus a de-nesting rule plus a carve-out, and the comments beside them
   * record two separate rounds of that breaking — once when a `Tabs` came between the card and the page
   * wrapper, once when the de-nesting rule flattened every KPI tile and was reported as "the padding is
   * broken on this page". Padding that depends on ancestry breaks the next time somebody nests something.
   * On the card, it cannot.</p>
   */
  padded?: boolean;
  children?: ReactNode;
}

/** Card/panel — solid level-1 reading surface by default; `glass` only for floating chrome (0B §4). */
export function Card({ material = "solid", as = "div", padded = false, className, children, ...rest }: CardProps) {
  const Tag = as;
  return (
    <Tag
      className={cx(material === "glass" ? "mrs-glass" : "mrs-card", padded && "mrs-card-pad", className)}
      {...rest}
    >
      {children}
    </Tag>
  );
}

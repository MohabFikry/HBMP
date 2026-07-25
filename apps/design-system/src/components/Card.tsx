import type { HTMLAttributes, ReactNode } from "react";
import { cx } from "../lib/cx";

export interface CardProps extends HTMLAttributes<HTMLDivElement> {
  /** `glass` = level-2 floating chrome (content must sit on inner opaque blocks). Default solid level-1. */
  material?: "solid" | "glass";
  as?: "div" | "section" | "aside";
  children?: ReactNode;
}

/** Card/panel — solid level-1 reading surface by default; `glass` only for floating chrome (0B §4). */
export function Card({ material = "solid", as = "div", className, children, ...rest }: CardProps) {
  const Tag = as;
  return (
    <Tag className={cx(material === "glass" ? "mrs-glass" : "mrs-card", className)} {...rest}>
      {children}
    </Tag>
  );
}

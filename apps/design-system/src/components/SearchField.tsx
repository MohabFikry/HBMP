import type { InputHTMLAttributes } from "react";
import { Icon } from "./Icon";
import { cx } from "../lib/cx";

export interface SearchFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "type"> {
  /** Accessible name — required (no visible label). */
  "aria-label": string;
  className?: string;
}

/** Search input with a leading icon; RTL-aware padding. Pass aria-label since the label is visual-only. */
export function SearchField({ className, ...rest }: SearchFieldProps) {
  return (
    <div className={cx("mrs-search", className)} role="search">
      <Icon name="search" className="mrs-search-icon" />
      <input type="search" {...rest} />
    </div>
  );
}

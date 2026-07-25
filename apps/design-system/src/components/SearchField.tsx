import { forwardRef, type InputHTMLAttributes } from "react";
import { Icon } from "./Icon";
import { cx } from "../lib/cx";

export interface SearchFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, "type"> {
  /** Accessible name — required (no visible label). */
  "aria-label": string;
  className?: string;
}

/**
 * Search input with a leading icon; RTL-aware padding. Pass aria-label since the label is visual-only.
 * Forwards its ref to the underlying input so callers can focus it (e.g. the "/" keyboard shortcut).
 */
export const SearchField = forwardRef<HTMLInputElement, SearchFieldProps>(function SearchField(
  { className, ...rest },
  ref,
) {
  return (
    <div className={cx("mrs-search", className)} role="search">
      <Icon name="search" className="mrs-search-icon" />
      <input type="search" ref={ref} {...rest} />
    </div>
  );
});

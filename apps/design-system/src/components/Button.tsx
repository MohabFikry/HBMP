import type { ButtonHTMLAttributes, ReactNode } from "react";
import { cx } from "../lib/cx";

/**
 * `warn` was here and is gone. It shipped, it was styled, and in the life of the product nothing ever used
 * it — so the amber middle tier between "ordinary" and "destructive" had no established meaning, and the
 * first screen to reach for it would have been inventing one.
 *
 * The product settled a different division and it works: COLOUR says a control takes something away, and the
 * confirmation's WORDS say what that costs — "you can add it back afterwards" against "this cannot be
 * undone". Severity lives in the sentence, where it can be specific, rather than in a third hue an operator
 * has to learn to read. An unused variant is not a spare part; it is an invitation to use it inconsistently.
 */
export type ButtonVariant = "primary" | "secondary" | "ghost" | "danger";
export type ButtonSize = "sm" | "md" | "lg";

export interface ButtonProps extends Omit<ButtonHTMLAttributes<HTMLButtonElement>, "type"> {
  variant?: ButtonVariant;
  size?: ButtonSize;
  /** Shows a spinner + sets aria-busy; disables interaction. */
  loading?: boolean;
  leadingIcon?: ReactNode;
  type?: "button" | "submit" | "reset";
  children?: ReactNode;
}

const sizeClass: Record<ButtonSize, string> = { sm: "mrs-sm", md: "", lg: "mrs-lg" };
const variantClass: Record<ButtonVariant, string> = {
  primary: "mrs-primary",
  secondary: "mrs-secondary",
  ghost: "mrs-ghost",
  danger: "mrs-danger",
};

/**
 * Button — primary/secondary/ghost/danger, 32/40/48h with ≥44px effective target, 3px focus ring,
 * loading (spinner + aria-busy), disabled (aria-disabled). Icon-only usage must pass aria-label (0B §6).
 */
export function Button({
  variant = "secondary",
  size = "md",
  loading = false,
  leadingIcon,
  className,
  children,
  disabled,
  type = "button",
  ...rest
}: ButtonProps) {
  return (
    <button
      type={type}
      className={cx("mrs-btn", variantClass[variant], sizeClass[size], className)}
      disabled={disabled || loading}
      aria-busy={loading || undefined}
      aria-disabled={disabled || loading || undefined}
      {...rest}
    >
      {loading ? <span className="mrs-spin" aria-hidden="true" /> : leadingIcon}
      {children}
    </button>
  );
}

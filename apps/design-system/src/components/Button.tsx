import type { ButtonHTMLAttributes, ReactNode } from "react";
import { cx } from "../lib/cx";

export type ButtonVariant = "primary" | "secondary" | "ghost" | "danger" | "warn";
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
  warn: "mrs-warn",
};

/**
 * Button — primary/secondary/ghost/danger/warn, 32/40/48h with ≥44px effective target, 3px focus ring,
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

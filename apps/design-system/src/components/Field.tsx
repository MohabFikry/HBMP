import { useId } from "react";
import type { InputHTMLAttributes, ReactNode, TextareaHTMLAttributes } from "react";
import { Icon } from "./Icon";
import { cx } from "../lib/cx";

interface FieldBase {
  label: string;
  help?: string;
  /** Error message — rendered with icon+text+red border (never color alone). */
  error?: string;
  className?: string;
  /** Marks the label with a required indicator. The native `required` attribute (which also sets
   *  aria-required) still passes through to the control — this is only the VISIBLE half, so sighted users
   *  learn a field is mandatory before failing it, not after (QA P2-12). */
  requiredMark?: boolean;
}

export interface InputFieldProps extends FieldBase, Omit<InputHTMLAttributes<HTMLInputElement>, "className"> {}
export interface TextareaFieldProps
  extends FieldBase,
    Omit<TextareaHTMLAttributes<HTMLTextAreaElement>, "className"> {}

function describedBy(base: string, help?: string, error?: string): string | undefined {
  const ids = [help && `${base}-help`, error && `${base}-err`].filter(Boolean);
  return ids.length ? ids.join(" ") : undefined;
}

function Labelled({
  label,
  help,
  error,
  base,
  className,
  requiredMark,
  children,
}: FieldBase & { base: string; children: ReactNode }) {
  return (
    <div className={cx("mrs-field", className)}>
      <label className="mrs-label" htmlFor={base}>
        {label}
        {requiredMark && (
          <span className="mrs-req" aria-hidden="true"> *</span>
        )}
      </label>
      {children}
      {help && (
        <div className="mrs-help" id={`${base}-help`}>
          {help}
        </div>
      )}
      {error && (
        <div className="mrs-error" id={`${base}-err`} role="alert">
          <Icon name="cross" />
          <span>{error}</span>
        </div>
      )}
    </div>
  );
}

/** Text input with always-visible label, helper/error tied via aria-describedby, aria-invalid on error. */
export function InputField({ label, help, error, className, id, ...rest }: InputFieldProps) {
  const auto = useId();
  const base = id ?? auto;
  return (
    <Labelled label={label} help={help} error={error} base={base} className={className} requiredMark={rest.required}>
      <input
        id={base}
        className="mrs-control"
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(base, help, error)}
        {...rest}
      />
    </Labelled>
  );
}

/** Multiline field — same a11y contract as InputField. */
export function TextareaField({ label, help, error, className, id, ...rest }: TextareaFieldProps) {
  const auto = useId();
  const base = id ?? auto;
  return (
    <Labelled label={label} help={help} error={error} base={base} className={className} requiredMark={rest.required}>
      <textarea
        id={base}
        className="mrs-control"
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(base, help, error)}
        {...rest}
      />
    </Labelled>
  );
}

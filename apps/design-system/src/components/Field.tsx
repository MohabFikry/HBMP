import { useId } from "react";
import type { InputHTMLAttributes, ReactNode, TextareaHTMLAttributes } from "react";
import { Icon } from "./Icon";
import { Select } from "./Select";
import type { SelectOption } from "./Select";
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
  /** False when the control is not a labellable element — a <button>-based combobox names itself with
   *  `aria-labelledby` pointing at this label, and an inert `for` on a button is worse than none. */
  labellable = true,
  children,
}: FieldBase & { base: string; labellable?: boolean; children: ReactNode }) {
  return (
    <div className={cx("mrs-field", className)}>
      <label className="mrs-label" id={`${base}-label`} htmlFor={labellable ? base : undefined}>
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

export interface SelectFieldProps extends FieldBase {
  options: SelectOption[];
  /** The chosen value, or null for "nothing chosen" — which renders `placeholder`. */
  value: string | null;
  onChange: (value: string) => void;
  placeholder?: string;
  disabled?: boolean;
  id?: string;
  required?: boolean;
}

/**
 * A labelled <see cref="Select"/> — the same field contract as InputField, over the design system's own
 * listbox rather than a native &lt;select&gt;.
 *
 * ============================================================================================================
 * WHY THIS EXISTS
 * ============================================================================================================
 * Screens were pairing a bare `<label>` with a bare `<select>`, and the result was a control that ignored
 * every field token in the system: the OS drew it, so it sat at a different height from the inputs beside it,
 * kept square corners against the app's radius, and opened a system-blue option list. Next to a Mersal text
 * field it does not read as unstyled — it reads as unfinished. `Select` already solved that; what was missing
 * was the labelled wrapper, so each screen wrote its own and half of them forgot the class.
 */
export function SelectField({
  label, help, error, className, id, options, value, onChange, placeholder, disabled, required,
}: SelectFieldProps) {
  const auto = useId();
  const base = id ?? auto;
  return (
    <Labelled
      label={label} help={help} error={error} base={base} className={className}
      requiredMark={required} labellable={false}
    >
      {/* The trigger is a <button>, which HTML does not let a <label for> name — so the label carries an id
          and the combobox points at it. Same visible pairing, and a screen reader announces the field name. */}
      <Select
        id={base}
        options={options}
        value={value}
        onChange={onChange}
        placeholder={placeholder}
        disabled={disabled}
        aria-labelledby={`${base}-label`}
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

import { useId } from "react";
import type { CSSProperties, InputHTMLAttributes, ReactNode, TextareaHTMLAttributes } from "react";
import { Icon } from "./Icon";
import { Combobox } from "./Combobox";
import type { ComboboxOption } from "./Combobox";
import { cx } from "../lib/cx";

interface FieldBase {
  label: string;
  help?: string;
  /** Error message — rendered with icon+text+red border (never color alone). */
  error?: string;
  className?: string;
  /**
   * Inline style for the field wrapper — in practice a width constraint.
   *
   * <p>Here because screens were hand-building `<div className="mrs-field" style={{ maxWidth: 320 }}>` around
   * a bare control to get one, and a wrapper written by hand is a wrapper half of them get wrong: the QA
   * notes on the policy screens record a label running into a zero-width control for exactly this reason.
   * The field owns its own box now, so the width goes on the field.</p>
   */
  style?: CSSProperties;
  /** Marks the label with a required indicator. The native `required` attribute (which also sets
   *  aria-required) still passes through to the control — this is only the VISIBLE half, so sighted users
   *  learn a field is mandatory before failing it, not after (QA P2-12). */
  requiredMark?: boolean;
  /**
   * Hide the label VISUALLY while keeping it for assistive tech.
   *
   * For a control inside a row that already names it — a rank picker on a line reading "J01.90 Acute
   * sinusitis", where a visible "Rank" above every row would triple the height and say nothing. The label
   * is still rendered and still bound to the control: this moves it off-screen, it never removes it, and a
   * field with no accessible name at all remains impossible to build here.
   */
  hideLabel?: boolean;
}

export interface InputFieldProps extends FieldBase, Omit<InputHTMLAttributes<HTMLInputElement>, "className"> {}
export interface FileFieldProps
  extends FieldBase,
    Omit<InputHTMLAttributes<HTMLInputElement>, "className" | "type" | "value"> {}
/** A checkbox's label sits BESIDE the box, so it does not take `hideLabel` — a checkbox with no visible
 *  label is a box whose meaning lives somewhere else on the screen. */
export interface CheckboxFieldProps
  extends Omit<FieldBase, "hideLabel">,
    Omit<InputHTMLAttributes<HTMLInputElement>, "className" | "type"> {}
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
  style,
  requiredMark,
  hideLabel,
  children,
}: FieldBase & { base: string; children: ReactNode }) {
  return (
    <div className={cx("mrs-field", className)} style={style}>
      <label
        className={cx("mrs-label", hideLabel && "sr-only")}
        id={`${base}-label`}
        htmlFor={base}
      >
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
export function InputField({ label, help, error, className, style, id, hideLabel, ...rest }: InputFieldProps) {
  const auto = useId();
  const base = id ?? auto;
  return (
    // `hideLabel` was declared on FieldBase, honoured by Labelled, and passed on by SelectField ALONE — so on
    // an InputField it fell into `...rest` and landed on the DOM node as an unknown attribute. A prop the
    // shared base documents has to work on every field that inherits it, or the contract is a suggestion.
    <Labelled
      label={label} help={help} error={error} base={base} className={className} style={style}
      requiredMark={rest.required} hideLabel={hideLabel}
    >
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

export interface ComboboxFieldProps extends FieldBase {
  options: ComboboxOption[];
  /** The chosen value, or null for "nothing chosen" — which renders `placeholder`. */
  value: string | null;
  onChange: (value: string) => void;
  placeholder?: string;
  disabled?: boolean;
  id?: string;
  required?: boolean;
  /** An icon belonging to the control. See `ComboboxProps.leadingIcon`. */
  leadingIcon?: ReactNode;
  /** Pill silhouette for filter bars; default is the field radius. See `ComboboxProps.shape`. */
  shape?: "pill" | "field";
  /** Carry the selected option's hint into the closed control. See `ComboboxProps.hintWhenClosed`. */
  hintWhenClosed?: boolean;
}

/**
 * A labelled <see cref="Combobox"/> — the searchable picker, with the same field anatomy as InputField.
 *
 * ============================================================================================================
 * WHY THIS EXISTS, AND WHY ITS ABSENCE WAS THE REAL FINDING
 * ============================================================================================================
 * The scrolls/dropdowns audit counted 56 pickers in the SPA and found 11 searchable. The interesting part was
 * not the ratio but the distribution: `SelectField` — the NON-searchable control — was used 19 times, more
 * than any other picker in the product, while `Combobox` was used 11.
 *
 * That is not 19 considered decisions. This file exported `InputField`, `SelectField` and `TextareaField`, so
 * a developer who wanted a picker with a label attached had exactly one thing to reach for, and it was the
 * one that cannot be typed into. The path of least resistance led away from the control the product should
 * have been using, and every screen that took it was behaving reasonably.
 *
 * So this is the change that makes the standard stick, rather than the 30 call-site edits that follow it. A
 * house rule that requires assembling `<label>` + `<Combobox>` by hand is a rule the next screen will forget;
 * one that is the shortest thing to type is a rule nobody has to remember.
 *
 * `SelectField` — the select-only version this replaced — has been deleted rather than left beside it. It had
 * no call sites once the conversion finished, and the tables/buttons audit already wrote down what an unused
 * control costs: the first screen to reach for it invents its meaning and the second invents a different one.
 * Its two shortcomings are worth recording because they are why this is a better default and not merely a
 * different one: its trigger was a `<button>`, which HTML does not let a `<label for>` name, so clicking the
 * label did nothing; and `Select` accepted no `aria-describedby`, so a helper line under it was on screen and
 * absent from the accessible description.
 */
export function ComboboxField({
  label, help, error, className, style, id, options, value, onChange, placeholder, disabled, required,
  hideLabel, leadingIcon, shape, hintWhenClosed,
}: ComboboxFieldProps) {
  const auto = useId();
  const base = id ?? auto;
  return (
    <Labelled
      label={label} help={help} error={error} base={base} className={className} style={style}
      requiredMark={required} hideLabel={hideLabel}
    >
      <Combobox
        id={base}
        options={options}
        value={value}
        onChange={onChange}
        placeholder={placeholder}
        disabled={disabled}
        invalid={Boolean(error)}
        aria-describedby={describedBy(base, help, error)}
        leadingIcon={leadingIcon}
        shape={shape}
        hintWhenClosed={hintWhenClosed}
        // The label already names the input through `for`/`id`. A second name via `aria-labelledby` would
        // override it with the same text, which is noise in the a11y tree rather than belt and braces.
      />
    </Labelled>
  );
}

/**
 * A file input, wearing the house control.
 *
 * <p>It existed as CSS and as nothing else: `input[type="file"].mrs-control` has been styled since the QA
 * pass that found a grey system button with a ~28px target sitting inside an otherwise themed form — but
 * every screen that needed one hand-built `<div className="mrs-field">` + `<label className="mrs-label">`
 * around it. That is the right markup in the wrong place, and the next screen to need one writes it
 * slightly differently. The native control is kept rather than replaced with a custom picker: it brings
 * keyboard operation, the OS dialog and screen-reader support that a div-and-a-hidden-input reimplements
 * badly.</p>
 */
export function FileField({ label, help, error, className, style, id, hideLabel, ...rest }: FileFieldProps) {
  const auto = useId();
  const base = id ?? auto;
  return (
    <Labelled
      label={label} help={help} error={error} base={base} className={className} style={style}
      requiredMark={rest.required} hideLabel={hideLabel}
    >
      <input
        id={base}
        type="file"
        className="mrs-control"
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy(base, help, error)}
        {...rest}
      />
    </Labelled>
  );
}

/**
 * A checkbox and its label, as one control.
 *
 * ============================================================================================================
 * WHY THIS EXISTS
 * ============================================================================================================
 * `.mrs-checkbox` — 24px box, transparent outset to a 44px target — has been in the stylesheet since the QA
 * pass, and five of the app's twenty-three checkboxes used it. The other eighteen were bare
 * `<input type="checkbox">` wearing one of eight ad-hoc wrapper classes or none, so they rendered at the
 * user agent's ~13px default. That fails WCAG 2.2 AA Target Size (Minimum) at 24px and this project's own
 * 44px bar, and it failed them most visibly on the plan-version editor, where the checkbox IS the control
 * that decides whether a benefit is covered at all.
 *
 * A style rule nobody applies is not a style. Shipping the component is what makes the sized form the
 * default rather than the thing you have to remember.
 *
 * ============================================================================================================
 * THE LABEL WRAPS THE BOX
 * ============================================================================================================
 * `<label>` around both, rather than `htmlFor` beside it: the whole row becomes the hit target, which is the
 * behaviour people try first and the one that makes a 44px target actually reachable on a phone. The `id`
 * still binds so a `help` or `error` can be described.
 */
export function CheckboxField({
  label, help, error, className, style, id, requiredMark, ...rest
}: CheckboxFieldProps) {
  const auto = useId();
  const base = id ?? auto;
  return (
    <div className={cx("mrs-field", "mrs-checkfield", className)} style={style}>
      <label className="mrs-checkrow" htmlFor={base}>
        <input
          id={base}
          type="checkbox"
          className="mrs-checkbox"
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy(base, help, error)}
          {...rest}
        />
        <span>
          {label}
          {requiredMark && <span className="mrs-req" aria-hidden="true"> *</span>}
        </span>
      </label>
      {help && <div className="mrs-help" id={`${base}-help`}>{help}</div>}
      {error && (
        <div className="mrs-error" id={`${base}-err`} role="alert">
          <Icon name="cross" />
          <span>{error}</span>
        </div>
      )}
    </div>
  );
}

/** Multiline field — same a11y contract as InputField. */
export function TextareaField({ label, help, error, className, style, id, hideLabel, ...rest }: TextareaFieldProps) {
  const auto = useId();
  const base = id ?? auto;
  return (
    <Labelled
      label={label} help={help} error={error} base={base} className={className} style={style}
      requiredMark={rest.required} hideLabel={hideLabel}
    >
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

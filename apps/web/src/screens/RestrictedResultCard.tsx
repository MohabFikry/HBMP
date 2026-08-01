import { useState } from "react";
import { Button, Icon, InputField, SelectField, TextareaField, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";

/** Existence-only metadata the server returns for a restricted result (14.7) — NEVER any values. */
export interface RestrictedResult {
  restricted: true;
  category: string;
  date?: string;
  status: string;
  orderingBranch?: string | null;
  sensitivityLevel: string;
}

export interface RestrictedResultCardProps {
  result: RestrictedResult;
  onRequestAccess: () => void;
}

/**
 * Phase 14.8 — the locked-result card (design 37 §7). Renders existence metadata + a RESTRICTED status chip
 * using the four-cue system (neutral hue + lock icon + ghost pill + text — never colour alone) plus a
 * "Request access" action. The server sends NO values for a restricted result, so none can appear in the DOM.
 */
export function RestrictedResultCard({ result, onRequestAccess }: RestrictedResultCardProps) {
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang as "en" | "ar"];

  return (
    <section className="restricted-card" aria-label={t(L.restrictedResult)}>
      <div className="restricted-head">
        {/* Four cues: lock (shape) + ghost pill (border) + weight + text — no colour dependency, because a
            clinician who cannot distinguish hues must still see instantly that this result is withheld. */}
        <span className="chip chip--restricted" data-testid="restricted-chip" role="status">
          <Icon name="lock" aria-hidden />
          {t(L.restricted)}
        </span>
        <strong>{result.category}</strong>
      </div>

      <dl className="restricted-meta">
        <dt>{t(L.activeBranch)}</dt>
        <dd>{result.orderingBranch ?? "—"}</dd>
        {result.date && (
          <>
            <dt>{lang === "ar" ? "التاريخ" : "Date"}</dt>
            <dd>{result.date}</dd>
          </>
        )}
        <dt>{lang === "ar" ? "الحالة" : "Status"}</dt>
        <dd>{result.status}</dd>
      </dl>

      <p className="restricted-body">{t(L.restrictedBody)}</p>
      <Button variant="primary" onClick={onRequestAccess}>
        {t(L.requestAccess)}
      </Button>
    </section>
  );
}

export interface RequestAccessDialogProps {
  onSubmit: (input: { purposeCode: string; justification: string; requestedTtlHours: number }) => void;
  onCancel: () => void;
}

const PURPOSES = ["ContinuityOfCare", "AuthorizationDecision", "ClinicalReview", "Complaint", "Legal", "Other"];

/**
 * Phase 14.8 — the access-request dialog (design 37 §7). Captures purpose + justification (both required, with
 * inline aria-describedby validation) and a requested duration. Submit stays disabled until both are provided —
 * the server also enforces 422, so this is convenience, not the guarantee.
 */
export function RequestAccessDialog({ onSubmit, onCancel }: RequestAccessDialogProps) {
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang as "en" | "ar"];
  const [purpose, setPurpose] = useState("");
  const [justification, setJustification] = useState("");
  const [hours, setHours] = useState(72);
  const [touched, setTouched] = useState(false);

  const purposeInvalid = touched && !purpose;
  const justInvalid = touched && justification.trim().length === 0;
  const valid = purpose !== "" && justification.trim().length > 0;

  return (
    <form
      aria-label={t(L.requestAccess)}
      onSubmit={(e) => {
        e.preventDefault();
        setTouched(true);
        if (valid) onSubmit({ purposeCode: purpose, justification: justification.trim(), requestedTtlHours: hours });
      }}
      className="restricted-request"
    >
      {/* The three fields were a bare label+select, label+textarea and label+input, each with its own inline
          styles. SelectField/TextareaField/InputField carry the label, the help/error wiring and the control
          height that the hand-written versions each approximated differently. `error` renders the same
          role="alert" the inline <p> did, so the validation contract is unchanged. */}
      <SelectField
        id="ra-purpose"
        label={t(L.purpose)}
        placeholder="—"
        options={PURPOSES.map((p) => ({ value: p, label: p }))}
        value={purpose || null}
        onChange={setPurpose}
        error={purposeInvalid ? t(L.purposeRequired) : undefined}
      />

      <TextareaField
        id="ra-just"
        label={t(L.justification)}
        value={justification}
        onChange={(e) => setJustification(e.target.value)}
        rows={3}
        error={justInvalid ? t(L.justificationRequired) : undefined}
      />

      <InputField
        id="ra-hours"
        label={t(L.duration)}
        type="number"
        min={1}
        max={168}
        value={hours}
        onChange={(e) => setHours(Number(e.target.value))}
      />

      <div className="row-actions">
        <Button type="submit" variant="primary">{t(L.submit)}</Button>
        <Button type="button" variant="ghost" onClick={onCancel}>{t(L.cancel)}</Button>
      </div>
    </form>
  );
}

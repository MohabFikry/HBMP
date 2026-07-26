import { useState } from "react";
import { useTheme } from "@mersal/design-system";
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
    <section
      className="restricted-card"
      aria-label={t(L.restrictedResult)}
      style={{ border: "1px solid var(--border, #cbd5e1)", borderRadius: 12, padding: 16, background: "var(--surface-muted, #f8fafc)" }}
    >
      <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
        {/* Four cues: lock icon (shape) + ghost pill (border) + text — no colour dependency. */}
        <span
          className="chip chip--restricted"
          data-testid="restricted-chip"
          role="status"
          style={{ display: "inline-flex", alignItems: "center", gap: 6, border: "1px solid var(--border, #94a3b8)", borderRadius: 999, padding: "2px 10px", fontWeight: 600 }}
        >
          <span aria-hidden>🔒</span>
          {t(L.restricted)}
        </span>
        <strong>{result.category}</strong>
      </div>

      <dl style={{ margin: "12px 0", display: "grid", gridTemplateColumns: "auto 1fr", gap: "4px 12px" }}>
        <dt style={{ opacity: 0.7 }}>{t(L.activeBranch)}</dt>
        <dd>{result.orderingBranch ?? "—"}</dd>
        {result.date && (
          <>
            <dt style={{ opacity: 0.7 }}>{lang === "ar" ? "التاريخ" : "Date"}</dt>
            <dd>{result.date}</dd>
          </>
        )}
        <dt style={{ opacity: 0.7 }}>{lang === "ar" ? "الحالة" : "Status"}</dt>
        <dd>{result.status}</dd>
      </dl>

      <p style={{ margin: "0 0 12px" }}>{t(L.restrictedBody)}</p>
      <button type="button" onClick={onRequestAccess} style={{ minHeight: 44, padding: "0 16px", fontWeight: 600 }}>
        {t(L.requestAccess)}
      </button>
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
      style={{ display: "grid", gap: 12, maxWidth: 480 }}
    >
      <div>
        <label htmlFor="ra-purpose" style={{ display: "block", fontWeight: 600 }}>{t(L.purpose)}</label>
        <select
          id="ra-purpose"
          value={purpose}
          aria-invalid={purposeInvalid}
          aria-describedby={purposeInvalid ? "ra-purpose-err" : undefined}
          onChange={(e) => setPurpose(e.target.value)}
          style={{ minHeight: 44, width: "100%" }}
        >
          <option value="">—</option>
          {PURPOSES.map((p) => <option key={p} value={p}>{p}</option>)}
        </select>
        {purposeInvalid && <p id="ra-purpose-err" role="alert" style={{ color: "var(--danger, #b91c1c)" }}>{t(L.purposeRequired)}</p>}
      </div>

      <div>
        <label htmlFor="ra-just" style={{ display: "block", fontWeight: 600 }}>{t(L.justification)}</label>
        <textarea
          id="ra-just"
          value={justification}
          aria-invalid={justInvalid}
          aria-describedby={justInvalid ? "ra-just-err" : undefined}
          onChange={(e) => setJustification(e.target.value)}
          rows={3}
          style={{ width: "100%" }}
        />
        {justInvalid && <p id="ra-just-err" role="alert" style={{ color: "var(--danger, #b91c1c)" }}>{t(L.justificationRequired)}</p>}
      </div>

      <div>
        <label htmlFor="ra-hours" style={{ display: "block", fontWeight: 600 }}>{t(L.duration)}</label>
        <input id="ra-hours" type="number" min={1} max={168} value={hours} onChange={(e) => setHours(Number(e.target.value))} style={{ minHeight: 44 }} />
      </div>

      <div style={{ display: "flex", gap: 8 }}>
        <button type="submit" style={{ minHeight: 44, padding: "0 16px", fontWeight: 600 }}>{t(L.submit)}</button>
        <button type="button" onClick={onCancel} style={{ minHeight: 44, padding: "0 16px" }}>{t(L.cancel)}</button>
      </div>
    </form>
  );
}

import { useState } from "react";
import { Button, InlineAlert, InputField, Modal, useTheme } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";

/**
 * Phase 18.D1 (audit R2 E4) — the confirmation used before an irreversible clinical or financial action.
 *
 * The only guard in the app was a `window.confirm` on the finance EXPORT — which is reversible, and the one
 * place a stray click costs nothing. Dispensing medication, consuming a lab order, rejecting an
 * authorization, overriding with break-glass and cancelling an appointment had none: a single click and the
 * thing was done, in a table of dozens of similar-looking rows.
 *
 * Two deliberate choices.
 *
 * `window.confirm` is not used. It cannot be translated (the browser supplies OK/Cancel in the BROWSER's
 * language, not the app's — an Arabic-speaking user gets an English dialog), it cannot state what is about to
 * happen in more than one line, and it blocks the main thread. Radix Dialog gives a focus trap, Esc, return
 * focus and a labelled title for free.
 *
 * TYPED confirmation for the most dangerous actions. A yes/no dialog in front of a repetitive task becomes
 * muscle memory within a shift — the operator clicks "Confirm" before reading it. Requiring the medication
 * name, the word the button describes, or the patient's member number makes the dialog impossible to clear
 * without looking at what it says. It is friction on purpose, and it is reserved for actions that cannot be
 * undone; a routine save should never ask for this.
 */
export interface ConfirmActionProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  title: Localized;
  /** What is about to happen, in plain language. Name the thing, not the operation. */
  body: Localized;
  /** The word the operator must type. Omit for a plain confirm. Case-insensitive, trimmed. */
  requireText?: string;
  /** Label for the confirming button — say the ACTION ("Dispense"), never "OK". */
  confirmLabel: Localized;
  onConfirm: () => void | Promise<void>;
  /** Renders the confirm button in a destructive tone. */
  destructive?: boolean;
  /**
   * The line under the title, saying what KIND of consequence this is. Defaults to "This cannot be undone."
   *
   * Overridable because irreversibility is one reason to confirm and not the only one. Revoking a clinician's
   * last clinic is reversible in the data and immediate in the world — they drop off the booking list the
   * moment it is done. That deserves a confirmation, and it does not deserve a dialog claiming an undo exists
   * when it does not, or claiming none exists when one does. Either way the sentence has to be true, because
   * a dialog that overstates on the reversible cases is one nobody reads on the irreversible ones.
   */
  description?: Localized;
}

const S = {
  cancel: { en: "Cancel", ar: "إلغاء" },
  typeToConfirm: { en: "Type {0} to confirm", ar: "اكتب {0} للتأكيد" },
  mismatch: { en: "That does not match — check what you are confirming.", ar: "غير مطابق — راجع ما تؤكده." },
  irreversible: { en: "This cannot be undone.", ar: "لا يمكن التراجع عن هذا." },
} satisfies Record<string, Localized>;

export function ConfirmAction({
  open, onOpenChange, title, body, requireText, confirmLabel, onConfirm, destructive = true, description,
}: ConfirmActionProps) {
  const { lang } = useTheme();
  const t = (l: Localized) => (lang === "ar" ? l.ar : l.en);
  const [typed, setTyped] = useState("");
  const [busy, setBusy] = useState(false);
  const [touched, setTouched] = useState(false);

  const matches = !requireText || typed.trim().toLowerCase() === requireText.trim().toLowerCase();

  async function confirm() {
    if (!matches) { setTouched(true); return; }
    setBusy(true);
    try {
      await onConfirm();
      setTyped("");
      setTouched(false);
      onOpenChange(false);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open={open}
      onOpenChange={(next) => { if (!next) { setTyped(""); setTouched(false); } onOpenChange(next); }}
      title={t(title)}
      description={t(description ?? S.irreversible)}
      footer={
        <>
          {/*
            The dismiss takes its weight from what it sits beside. Against an ordinary commit it is `ghost`,
            so the commit dominates. Against a DESTRUCTIVE one it is `secondary`: backing out is the
            recommended action there, and it must not be the lighter of the two — which is the same reasoning
            that made three cancellation dialogs in this product relabel their dismiss to "Keep it".
          */}
          <Button variant={destructive ? "secondary" : "ghost"} onClick={() => onOpenChange(false)}>
            {t(S.cancel)}
          </Button>
          <Button
            variant={destructive ? "danger" : "primary"}
            onClick={() => void confirm()}
            loading={busy}
            // Disabled until it matches, so the dangerous button is not the one under the cursor by default.
            disabled={!matches}
          >
            {t(confirmLabel)}
          </Button>
        </>
      }
    >
      <p>{t(body)}</p>
      {requireText && (
        <>
          <InputField
            label={t(S.typeToConfirm).replace("{0}", `“${requireText}”`)}
            value={typed}
            onChange={(e) => setTyped(e.currentTarget.value)}
            autoComplete="off"
          />
          <div aria-live="polite">
            {touched && !matches && <InlineAlert tone="bad">{t(S.mismatch)}</InlineAlert>}
          </div>
        </>
      )}
    </Modal>
  );
}

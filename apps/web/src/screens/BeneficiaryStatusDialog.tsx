import { useState } from "react";
import { Button, InlineAlert, InputField, Modal, useToast } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useWrite } from "../api/useWrite";
import { useLoc } from "./_shared";

/**
 * Changing a beneficiary's lifecycle status — the dialog, extracted from the screen that used to own it.
 *
 * ============================================================================================================
 * WHY THIS IS NO LONGER A SCREEN
 * ============================================================================================================
 * "Status & Reactivation" was a nav section whose entire job was: search for a person you have usually just
 * been looking at, find them again, then press one button. It is an action on a record, not a place — and
 * every route into it started from a record. It now opens from the beneficiary's detail, where the person is
 * already on screen and their current status is already known, which removes the search step and with it the
 * chance of acting on the wrong Amina Yusuf.
 *
 * The dialog itself is unchanged, including the part that matters: the transitions offered are the ones the
 * server will accept, and a failure leaves the modal open with the reason.
 */

const S = {
  changeStatus: { en: "Change status", ar: "تغيير الحالة" },
  newStatus: { en: "New status", ar: "الحالة الجديدة" },
  reason: { en: "Reason", ar: "السبب" },
  reasonRequired: {
    en: "A reason is required for this change — it is recorded and reviewed.",
    ar: "السبب مطلوب لهذا التغيير — يُسجَّل ويُراجَع.",
  },
  confirm: { en: "Confirm", ar: "تأكيد" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  close: { en: "Close", ar: "إغلاق" },
  changed: { en: "Status updated.", ar: "تم تحديث الحالة." },
  blockedLocked: {
    en: "A blocked record is unlocked by a director, not at the desk.",
    ar: "السجل المحظور يفكّه المدير، لا الموظف.",
  },
  unknownStatus: {
    en: "This beneficiary's status was not disclosed to your role, so no change can be offered here.",
    ar: "لم تُفصح حالة هذا المستفيد لدورك، لذا لا يمكن عرض أي تغيير هنا.",
  },

  activate: { en: "Activate", ar: "تفعيل" },
  suspend: { en: "Suspend", ar: "إيقاف" },
  reinstate: { en: "Reinstate", ar: "إعادة تفعيل" },
  renew: { en: "Renew", ar: "تجديد" },
  reactivate: { en: "Reactivate", ar: "إعادة تنشيط" },
  deactivate: { en: "Deactivate", ar: "إلغاء التفعيل" },
} satisfies Record<string, Localized>;

/** Exported so a caller can label its trigger with the same words the dialog is titled with. */
export const STATUS_STRINGS = S;

export interface DeskTransition {
  to: string;
  label: Localized;
  needsReason: boolean;
  danger?: boolean;
}

/**
 * The transitions this DESK may offer, per current status — the UI mirror of `BeneficiaryLifecycle` +
 * 23 §1's Actor column. Offering an illegal move (the old screen showed Activate/Suspend on every row)
 * just manufactures 409s: the server refuses, and the operator learns the rules by being told off.
 *
 * `needsReason` mirrors `RequiresReason`: a reason is demanded exactly where the server records one, and
 * not where it would be theatre (activation needs no justification — it is the default good outcome).
 * Blocked is absent on purpose: both edges of the fraud state are a director's, and the screen says so
 * instead of rendering a button that 403s.
 */
export const DESK_TRANSITIONS: Record<string, DeskTransition[]> = {
  Pending: [
    { to: "Active", label: S.activate, needsReason: false },
    { to: "Inactive", label: S.deactivate, needsReason: true, danger: true },
  ],
  Active: [
    { to: "Suspended", label: S.suspend, needsReason: true, danger: true },
    { to: "Inactive", label: S.deactivate, needsReason: true, danger: true },
  ],
  Suspended: [{ to: "Active", label: S.reinstate, needsReason: false }],
  Expired: [{ to: "Active", label: S.renew, needsReason: false }],
  Inactive: [{ to: "Active", label: S.reactivate, needsReason: false }],
  Blocked: [],
};

/** Whether the desk has any move to offer from this status — what a caller gates its trigger on. */
export const canChangeStatus = (statusRaw: string | null | undefined): boolean =>
  Boolean(statusRaw) && (DESK_TRANSITIONS[statusRaw!] ?? []).length > 0;

export function StatusChangeModal({
  beneficiaryId,
  name,
  statusRaw,
  onClose,
  onChanged,
}: {
  beneficiaryId: string;
  /** For the dialog title — a status change is applied to a PERSON, and the title says which. */
  name: string;
  /** The beneficiary lifecycle status (Pending/Active/Suspended/…), NOT an enrollment status. */
  statusRaw: string | null | undefined;
  onClose: () => void;
  onChanged: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const write = useWrite();
  const options = statusRaw ? DESK_TRANSITIONS[statusRaw] ?? [] : [];
  const [choice, setChoice] = useState(options.length === 1 ? options[0]!.to : "");
  const [reason, setReason] = useState("");
  const [touched, setTouched] = useState(false);

  const selected = options.find((o) => o.to === choice);
  const reasonError = touched && selected?.needsReason && reason.trim() === "" ? t(S.reasonRequired) : undefined;

  const confirm = async () => {
    setTouched(true);
    if (!selected) return;
    if (selected.needsReason && reason.trim() === "") return;
    const ok = await write.run(() => api.changeBeneficiaryStatus(beneficiaryId, selected.to, reason.trim()));
    if (ok) {
      toast(t(S.changed), "ok");
      onChanged();
    }
    // On failure the modal STAYS OPEN with the typed error rendered below — the old screen's try/finally
    // swallowed the rejection entirely, so a 409 looked identical to success with a stopped spinner.
  };

  // Nothing to offer: a blocked record, or a status the caller's role was not disclosed. Both get a named
  // reason rather than an empty fieldset, because a dialog with no options and no explanation reads as a
  // broken screen — and the two cases have different answers (ask a director / you cannot see this).
  const nothingToDo = options.length === 0;

  return (
    <Modal
      open
      onOpenChange={(o) => !o && onClose()}
      title={`${t(S.changeStatus)} — ${name}`}
      closeLabel={t(S.close)}
      footer={
        nothingToDo ? (
          <Button variant="ghost" onClick={onClose}>{t(S.close)}</Button>
        ) : (
          <>
            <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
            <Button
              variant={selected?.danger ? "danger" : "primary"}
              onClick={confirm}
              loading={write.busy}
              disabled={write.busy || !selected}
            >
              {t(S.confirm)}
            </Button>
          </>
        )
      }
    >
      {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}

      {nothingToDo ? (
        <InlineAlert tone="info">{t(statusRaw ? S.blockedLocked : S.unknownStatus)}</InlineAlert>
      ) : (
        <>
          <fieldset className="mrs-choice">
            <legend className="mrs-label">{t(S.newStatus)}</legend>
            {options.map((o) => (
              <label key={o.to} className="mrs-choice-opt">
                <input type="radio" name="transition" value={o.to} checked={choice === o.to} onChange={() => setChoice(o.to)} />
                <span>{t(o.label)}</span>
              </label>
            ))}
          </fieldset>

          {selected?.needsReason ? (
            <InputField label={t(S.reason)} value={reason} error={reasonError} onChange={(e) => setReason(e.currentTarget.value)} autoComplete="off" />
          ) : null}
        </>
      )}
    </Modal>
  );
}

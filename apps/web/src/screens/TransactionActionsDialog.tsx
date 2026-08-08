import { useEffect, useMemo, useState } from "react";
import { Button, InlineAlert, Modal, Select, StatusChip, TextareaField } from "@mersal/design-system";
import type { Localized, WithdrawResult } from "@mersal/contracts";
import type { AmendReasonOption } from "./AmendLineDialog";
import { useLoc } from "./_shared";

/**
 * Amend or withdraw a WHOLE transaction — a prescription, or a lab / radiology / OP-procedure order.
 *
 * ============================================================================================================
 * WHY THIS EXISTS BESIDE `AmendLineDialog` RATHER THAN REPLACING IT
 * ============================================================================================================
 * They answer different questions. `AmendLineDialog` answers "change THIS line", which is what a pharmacist
 * reading one dispensing row wants. This answers "withdraw that prescription" — the act a doctor scanning
 * their own list actually intends, and which they previously had to perform by opening the record, reading
 * the lines, and cancelling each one, with a chance to stop halfway at every step.
 *
 * ============================================================================================================
 * THREE THINGS IT REFUSES TO DO
 * ============================================================================================================
 * **It does not hide what it cannot do.** A line that is already dispensed shows its quantity greyed with the
 * reason beside it (design 46 §10). A hidden control makes the doctor think the feature is missing.
 *
 * **It does not report a partial withdrawal as a whole one.** The result names every line that could not be
 * withdrawn. "3 of 5 withdrawn" is a true sentence; "withdrawn" would not be, and the two lines still live
 * are the ones the patient will be given.
 *
 * **It does not amend a line the doctor did not touch.** Only quantities that actually changed are sent, so
 * confirming an amendment with one field edited supersedes one line, not five — five superseded rows in the
 * record, four of them identical to their originals, is a history nobody can read.
 */

export type TransactionAction = "amend" | "withdraw";

/** One line of the transaction, as this dialog needs to show and edit it. */
export interface TransactionLine {
  id: string;
  /** What the doctor would call it — a medicine name or a procedure description. Never a uuid. */
  label: string;
  quantity: number;
  /** Why this line cannot be changed, in the doctor's words. Null ⇒ it can. */
  locked: string | null;
}

const S = {
  amendTitle: { en: "Amend", ar: "تعديل" },
  withdrawTitle: { en: "Withdraw", ar: "سحب" },

  amendBody: {
    en: "Signed items are not edited. Each quantity you change is replaced by a new version, and the original "
      + "stays in the record exactly as it was written.",
    ar: "لا يتم تعديل البنود الموقّعة. كل كمية تُغيّرها تُستبدل بنسخة جديدة، ويبقى الأصل في السجل كما كُتب.",
  },
  withdrawBody: {
    en: "Every item that can still be withdrawn will be. They stay visible in the record, marked withdrawn, "
      + "with your reason.",
    ar: "سيتم سحب كل بند ما زال قابلاً للسحب. تبقى ظاهرة في السجل بحالة مسحوب مع السبب الذي تُدخله.",
  },

  items: { en: "Items", ar: "البنود" },
  quantity: { en: "Quantity", ar: "الكمية" },
  reason: { en: "Reason", ar: "السبب" },
  reasonPlaceholder: { en: "Choose a reason…", ar: "اختر السبب…" },
  reasonRequired: { en: "A reason is required.", ar: "السبب مطلوب." },
  notes: { en: "Notes (optional)", ar: "ملاحظات (اختياري)" },
  notesHelp: {
    en: "The code answers “how often”; this answers “what happened here”.",
    ar: "الرمز يجيب عن «كم مرة»؛ وهذا يجيب عن «ماذا حدث هنا».",
  },
  noChange: {
    en: "Change at least one quantity. Nothing has been altered.",
    ar: "غيّر كمية واحدة على الأقل. لم يتغيّر شيء.",
  },
  nothingAmendable: {
    en: "No item here can still be changed.",
    ar: "لا يوجد بند هنا ما زال قابلاً للتغيير.",
  },
  failed: {
    en: "That change could not be applied. Nothing was altered — reopen the record to see its current state.",
    ar: "تعذّر تطبيق التغيير. لم يُعدَّل شيء — أعد فتح السجل لعرض حالته الحالية.",
  },

  back: { en: "Back", ar: "رجوع" },
  close: { en: "Close", ar: "إغلاق" },
  confirmAmend: { en: "Replace with new versions", ar: "استبدال بنسخ جديدة" },
  confirmWithdraw: { en: "Withdraw", ar: "سحب" },

  // ---- the partial-success report --------------------------------------------------------------------
  resultAll: { en: "Withdrawn.", ar: "تم السحب." },
  resultNone: { en: "Nothing could be withdrawn.", ar: "تعذّر سحب أي بند." },
  resultSome: { en: "Partly withdrawn. These items are still live:", ar: "تم السحب جزئياً. البنود التالية ما زالت سارية:" },
} satisfies Record<string, Localized>;

export function TransactionActionsDialog({
  open,
  action,
  reference,
  lines,
  reasons,
  onCancel,
  onWithdraw,
  onAmend,
  onDone,
}: {
  open: boolean;
  action: TransactionAction;
  /** RX-2026-000312 / ORD-2026-000118 — what the doctor is matching this against. */
  reference: string;
  lines: TransactionLine[];
  reasons: AmendReasonOption[];
  onCancel: () => void;
  onWithdraw: (input: { reasonCode: string; reasonText?: string }) => Promise<WithdrawResult>;
  /** Called once per CHANGED quantity, never for an untouched line. */
  onAmend: (input: { lineId: string; quantity: number; reasonCode: string; reasonText?: string }) => Promise<void>;
  /** After anything was actually applied, so the list behind can refetch. */
  onDone: () => void;
}) {
  const t = useLoc();

  const [reasonCode, setReasonCode] = useState("");
  const [reasonText, setReasonText] = useState("");
  const [quantities, setQuantities] = useState<Record<string, string>>({});
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);
  const [result, setResult] = useState<WithdrawResult | null>(null);

  // Reopening must not carry the previous answer forward: a coded reason typed for one prescription and
  // silently reused on the next is a reason that is WRONG, which is worse than one that is absent.
  useEffect(() => {
    if (!open) return;
    setReasonCode("");
    setReasonText("");
    setQuantities(Object.fromEntries(lines.map((l) => [l.id, String(l.quantity)])));
    setTouched(false);
    setFailed(false);
    setResult(null);
  }, [open, reference, action, lines]);

  const options = useMemo(
    () => reasons.map((r) => ({ value: r.code, label: t({ en: r.nameEn, ar: r.nameAr }) })),
    [reasons, t],
  );

  const changed = useMemo(
    () => lines.filter((l) => l.locked === null && Number(quantities[l.id]) !== l.quantity
      && quantities[l.id] !== undefined && quantities[l.id] !== ""),
    [lines, quantities],
  );
  const anyAmendable = lines.some((l) => l.locked === null);

  if (!open) return null;

  const title = `${t(action === "amend" ? S.amendTitle : S.withdrawTitle)} — ${reference}`;

  // ---- the report, once a withdrawal has actually happened ---------------------------------------------
  if (result) {
    const stillLive = result.lines.filter((l) => !l.withdrawn);
    return (
      <Modal open onOpenChange={(o) => { if (!o) { onDone(); onCancel(); } }} title={title}>
        <div data-testid="withdraw-result">
          {result.withdrawn === result.total && result.total > 0 && (
            <InlineAlert tone="ok">{t(S.resultAll)}</InlineAlert>
          )}
          {result.withdrawn === 0 && <InlineAlert tone="bad">{t(S.resultNone)}</InlineAlert>}
          {result.withdrawn > 0 && result.withdrawn < result.total && (
            <>
              {/* NAMED, not counted. "3 of 5" tells a doctor something went wrong and not which two are
                  still going to be dispensed. */}
              <InlineAlert tone="warn">{t(S.resultSome)}</InlineAlert>
              <ul>
                {stillLive.map((l) => (
                  <li key={l.label}>{l.label}{l.refusal ? ` — ${l.refusal}` : ""}</li>
                ))}
              </ul>
            </>
          )}
          <Button variant="secondary" onClick={() => { onDone(); onCancel(); }}>{t(S.close)}</Button>
        </div>
      </Modal>
    );
  }

  const missingReason = touched && reasonCode === "";
  const noChange = action === "amend" && touched && changed.length === 0;

  return (
    <Modal open onOpenChange={(o) => { if (!o) onCancel(); }} title={title}>
      <div data-testid="transaction-dialog">
        <p>{t(action === "amend" ? S.amendBody : S.withdrawBody)}</p>

        <h3 className="section-h">{t(S.items)}</h3>
        <ul className="txn-lines">
          {lines.map((l) => (
            <li key={l.id} className="txn-line">
              <span className="txn-line-label">{l.label}</span>
              {action === "amend" ? (
                <label className="rx-field">
                  {/* The line's own name is IN the field's accessible name: five boxes all called
                      "Quantity" is five boxes a screen-reader user cannot tell apart. */}
                  <span className="rx-field-label">{t(S.quantity)}</span>
                  <input
                    className="rx-field-input"
                    type="number"
                    min={0}
                    inputMode="decimal"
                    aria-label={`${t(S.quantity)} — ${l.label}`}
                    // Disabled, never hidden, with the reason beside it (design 46 §10).
                    disabled={l.locked !== null || busy}
                    aria-describedby={l.locked ? `txnlock-${l.id}` : undefined}
                    value={quantities[l.id] ?? String(l.quantity)}
                    onChange={(e) => {
                      const v = e.currentTarget.value;   // read before the updater
                      setQuantities((prev) => ({ ...prev, [l.id]: v }));
                    }}
                  />
                </label>
              ) : (
                <span className="tnum">{l.quantity}</span>
              )}
              {l.locked && (
                <span id={`txnlock-${l.id}`}>
                  <StatusChip kind="warn" label={l.locked} />
                </span>
              )}
            </li>
          ))}
        </ul>

        {action === "amend" && !anyAmendable && <InlineAlert tone="info">{t(S.nothingAmendable)}</InlineAlert>}

        <label htmlFor="txn-reason">{t(S.reason)}</label>
        <Select
          id="txn-reason"
          aria-label={t(S.reason)}
          placeholder={t(S.reasonPlaceholder)}
          options={options}
          value={reasonCode === "" ? null : reasonCode}
          onChange={(v) => setReasonCode(v)}
        />
        {missingReason && <InlineAlert tone="bad">{t(S.reasonRequired)}</InlineAlert>}
        {noChange && <InlineAlert tone="info">{t(S.noChange)}</InlineAlert>}
        {failed && <InlineAlert tone="bad">{t(S.failed)}</InlineAlert>}

        <TextareaField
          label={t(S.notes)}
          help={t(S.notesHelp)}
          maxLength={300}
          value={reasonText}
          onChange={(e) => setReasonText(e.target.value)}
        />

        <Button variant="secondary" onClick={onCancel}>{t(S.back)}</Button>
        <Button
          variant="danger"
          disabled={busy || (action === "amend" && !anyAmendable)}
          onClick={async () => {
            setTouched(true);
            if (reasonCode === "") return;
            if (action === "amend" && changed.length === 0) return;

            setBusy(true);
            setFailed(false);
            const text = reasonText.trim() === "" ? undefined : reasonText.trim();
            try {
              if (action === "withdraw") {
                setResult(await onWithdraw({ reasonCode, reasonText: text }));
              } else {
                // Only what CHANGED. Superseding five lines because the dialog was opened would put four
                // amendments into the record that nobody made.
                for (const l of changed) {
                  await onAmend({ lineId: l.id, quantity: Number(quantities[l.id]), reasonCode, reasonText: text });
                }
                onDone();
                onCancel();
              }
            } catch {
              setFailed(true);
            } finally {
              setBusy(false);
            }
          }}
        >
          {t(action === "amend" ? S.confirmAmend : S.confirmWithdraw)}
        </Button>
      </div>
    </Modal>
  );
}

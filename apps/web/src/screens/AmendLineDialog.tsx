import { useEffect, useMemo, useState } from "react";
import { Button, ComboboxField, InlineAlert, Modal, StatusChip, TextareaField } from "@mersal/design-system";
import type { ChronicAmendPreview, Localized } from "@mersal/contracts";
import { useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";

/**
 * 30.6 — cancel or amend ONE signed line (design 46 §1–§3, §7).
 *
 * <p><b>The confirmation names exactly what will change.</b> A doctor confirming "are you sure?" is
 * confirming a sentence they wrote in their head, not the one the system is about to execute. So the dialog
 * restates the line, the change, and — for a chronic amendment — the recomputed schedule, before the button
 * is live.</p>
 *
 * <p><b>The reason is CODED and mandatory, and the free text is additional.</b> The codes are what make "how
 * often do we cancel, and why" answerable; free text alone answers nothing at scale. The picker is served by
 * the API rather than hard-coded here, so adding a reason stays a data change (`GET /amendment-reasons`).</p>
 *
 * <p><b>A consumed line shows the action DISABLED with its reason visible — never hidden.</b> Design 46 §10:
 * "A hidden control makes the doctor think the feature is missing." So they see the button, greyed, beside
 * the sentence that explains it — "dispensed 14:32, Maadi Pharmacy" — which is also the information they need
 * in order to do the next right thing.</p>
 */

/**
 * `amend-schedule` is the CHRONIC variant (32.6, design 46 §4): duration and frequency rather than a
 * quantity, because a chronic script's quantity is derived from its course and editing it directly would
 * describe a schedule nobody dispenses against.
 */
export type AmendAction = "cancel" | "amend" | "amend-schedule";

export interface AmendReasonOption {
  code: string;
  nameEn: string;
  nameAr: string;
}

/** Why this line cannot be amended, in the words the doctor needs. Absent ⇒ it can. */
export interface LineLockedReason {
  /** "Consumed" · "Cancelled" · "Superseded" · "Expired" */
  what: string;
  when?: string | null;
  by?: string | null;
}

export interface ChronicPreview {
  newTotal: number;
  alreadyDispensed: number;
  /** The recomputed remaining windows. Collected ones are NOT here — they are immutable. */
  remainingWindows: number[];
  verdict: string;
}

export interface AmendLineDialogProps {
  open: boolean;
  action: AmendAction;
  /** What the doctor is acting on, so the confirmation can name it rather than say "this line". */
  lineLabel: string;
  currentQuantity?: number;
  /** Present ⇒ the action is unavailable and the dialog explains instead of asking. */
  locked?: LineLockedReason | null;
  reasons: AmendReasonOption[];
  /** Rendered before confirming, with the dispensed portion marked immutable (design 46 §10). */
  chronicPreview?: ChronicPreview | null;
  /** The chronic script as it stands, so the fields open on the current answer rather than empty. */
  currentDurationDays?: number;
  currentFrequencyMonths?: number;
  /**
   * Ask the SERVER what this change would do (32.6).
   *
   * <p>Not optional decoration: the three refusals below are the server's outcomes, and the arithmetic
   * behind them must not be re-derived here — `zChronicPreview` forbids the fork, because the copies drift
   * and the drift appears as a doctor shown a schedule the pharmacy never honours.</p>
   */
  onPreview?: (req: { durationDays: number; frequencyMonths: number; convertToAcute: boolean })
    => Promise<ChronicAmendPreview>;
  onCancel: () => void;
  onConfirm: (input: {
    reasonCode: string; reasonText?: string; quantity?: number;
    durationDays?: number; frequencyMonths?: number; convertToAcute?: boolean;
  }) => Promise<void> | void;
}

const S = {
  cancelTitle: { en: "Withdraw this item", ar: "سحب هذا البند" },
  amendTitle: { en: "Amend this item", ar: "تعديل هذا البند" },

  // The confirmation SAYS what happens, rather than asking whether the reader is sure.
  cancelBody: {
    en: "This item will be withdrawn. It stays visible in the record, marked withdrawn, with your reason.",
    ar: "سيتم سحب هذا البند. سيبقى ظاهراً في السجل بحالة مسحوب مع السبب الذي تُدخله.",
  },
  amendBody: {
    en: "The signed item is not edited. A new version replaces it, and the original stays in the record "
      + "exactly as it was written.",
    ar: "لا يتم تعديل البند الموقّع. تحل نسخة جديدة محله، ويبقى الأصل في السجل كما كُتب تماماً.",
  },

  reason: { en: "Reason", ar: "السبب" },
  reasonPlaceholder: { en: "Choose a reason…", ar: "اختر السبب…" },
  reasonRequired: { en: "A reason is required.", ar: "السبب مطلوب." },
  notes: { en: "Notes (optional)", ar: "ملاحظات (اختياري)" },
  notesHelp: {
    en: "The code answers “how often”; this answers “what happened here”.",
    ar: "الرمز يجيب عن «كم مرة»؛ وهذا يجيب عن «ماذا حدث هنا».",
  },

  quantity: { en: "New quantity", ar: "الكمية الجديدة" },

  // ---- the locked case -------------------------------------------------------------------------------
  lockedTitle: { en: "This item can no longer be changed", ar: "لم يعد بالإمكان تغيير هذا البند" },
  lockedConsumed: { en: "Already delivered", ar: "تم تنفيذه بالفعل" },
  lockedCancelled: { en: "Already withdrawn", ar: "تم سحبه بالفعل" },
  lockedSuperseded: { en: "Already amended", ar: "تم تعديله بالفعل" },
  lockedExpired: { en: "The order has expired", ar: "انتهت صلاحية الطلب" },
  lockedExpiredHelp: {
    en: "An expired order is not amendable — the approval team can revalidate it, and the patient does not "
      + "need a new order.",
    ar: "الطلب المنتهي غير قابل للتعديل — يمكن لفريق الموافقات إعادة اعتماده، ولا يحتاج المريض إلى طلب جديد.",
  },

  // ---- the chronic preview ---------------------------------------------------------------------------
  previewTitle: { en: "Recomputed schedule", ar: "الجدول بعد إعادة الحساب" },
  durationDays: { en: "Duration (days)", ar: "المدة (أيام)" },
  frequencyMonths: { en: "Refill every (months)", ar: "إعادة الصرف كل (شهور)" },
  belowDispensed: {
    en: "That total is below what has already been collected ({collected}). The patient cannot return "
      + "medicine they have — choose a longer duration.",
    ar: "هذا الإجمالي أقل مما تم صرفه بالفعل ({collected}). لا يمكن للمريض إعادة دواء بحوزته — اختر مدة أطول.",
  },
  noLongerChronic: {
    en: "At this duration it is no longer a chronic prescription: it stops having refill windows, and the "
      + "patient collects it once instead of monthly.",
    ar: "بهذه المدة لم تعد وصفة مزمنة: تنتهي نوافذ إعادة الصرف، ويصرفها المريض مرة واحدة بدلًا من شهريًا.",
  },
  convertToAcute: { en: "Convert it to acute", ar: "تحويلها إلى وصفة حادة" },
  notChecked: {
    en: "The schedule could not be computed: the drug catalogue does not record {field}. Nothing has been "
      + "changed.",
    ar: "تعذّر حساب الجدول: لا يسجّل دليل الأدوية {field}. لم يتغير شيء.",
  },
  previewFailed: {
    en: "The recomputed schedule could not be loaded, so there is nothing to confirm against.",
    ar: "تعذّر تحميل الجدول بعد إعادة الحساب، فلا يوجد ما يُؤكَّد عليه.",
  },
  previewCollected: { en: "Already collected (unchanged)", ar: "تم صرفه بالفعل (بدون تغيير)" },
  previewImmutable: { en: "Immutable", ar: "غير قابل للتغيير" },
  previewTotal: { en: "New total", ar: "الإجمالي الجديد" },
  previewWindow: { en: "Collection", ar: "صرفة" },

  confirmCancel: { en: "Withdraw item", ar: "سحب البند" },
  confirmAmend: { en: "Replace with new version", ar: "استبدال بنسخة جديدة" },
  close: { en: "Close", ar: "إغلاق" },
  back: { en: "Back", ar: "رجوع" },
} satisfies Record<string, Localized>;

/** The word for a lock, chosen so each reads as the distinct fact it is. */
function lockedLabel(what: string): Localized {
  if (what === "Consumed" || what === "Dispensed") return S.lockedConsumed;
  if (what === "Cancelled") return S.lockedCancelled;
  if (what === "Superseded") return S.lockedSuperseded;
  return S.lockedExpired;
}

export function AmendLineDialog(props: AmendLineDialogProps) {
  const { open, action, lineLabel, currentQuantity, locked, reasons, chronicPreview } = props;
  const t = useLoc();
  const { dateTime } = useFormat();

  const [reasonCode, setReasonCode] = useState("");
  const [reasonText, setReasonText] = useState("");
  const [quantity, setQuantity] = useState<string>(String(currentQuantity ?? ""));
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);

  // 32.6 — the chronic variant's own state. `preview` is the SERVER's answer, re-fetched whenever the
  // numbers change, and it is what decides whether confirming is possible at all.
  const chronic = action === "amend-schedule";
  const [durationDays, setDurationDays] = useState(String(props.currentDurationDays ?? ""));
  const [frequencyMonths, setFrequencyMonths] = useState(String(props.currentFrequencyMonths ?? 1));
  const [convertToAcute, setConvertToAcute] = useState(false);
  const [preview, setPreview] = useState<ChronicAmendPreview | null>(null);
  const [previewFailed, setPreviewFailed] = useState(false);

  // ABOVE the locked early-return, with every other hook. Placing these after it meant a locked line
  // rendered fewer hooks than an unlocked one — React counts them positionally, and the order-actions suite
  // caught it as "Rendered more hooks than during the previous render". A conditional return is a hook
  // boundary whether or not it looks like one.
  // Reset the conversion confirmation whenever the numbers move: a checkbox ticked for 20 days and silently
  // carried onto 200 would authorise a conversion the prescriber never looked at.
  useEffect(() => { setConvertToAcute(false); }, [durationDays, frequencyMonths]);

  const onPreview = props.onPreview;
  useEffect(() => {
    if (!chronic || !onPreview) return;
    const days = Number(durationDays);
    const months = Number(frequencyMonths);
    if (!Number.isFinite(days) || days <= 0 || !Number.isFinite(months) || months <= 0) {
      setPreview(null);
      return;
    }
    let live = true;
    void onPreview({ durationDays: days, frequencyMonths: months, convertToAcute })
      .then((p) => { if (live) { setPreview(p); setPreviewFailed(false); } })
      // A failed preview is not an empty one: with no schedule to confirm against, the dialog says so and
      // refuses rather than letting the prescriber confirm an arithmetic nobody produced.
      .catch(() => { if (live) { setPreview(null); setPreviewFailed(true); } });
    return () => { live = false; };
  }, [chronic, onPreview, durationDays, frequencyMonths, convertToAcute]);


  // Reopening must not carry the previous answer forward: a reason typed for one line and silently reused on
  // another is a coded reason that is wrong, which is worse than one that is absent.
  useEffect(() => {
    if (!open) return;
    setReasonCode("");
    setReasonText("");
    setQuantity(String(currentQuantity ?? ""));
    setTouched(false);
  }, [open, currentQuantity, lineLabel]);

  const options = useMemo(
    () => reasons.map((r) => ({ value: r.code, label: t({ en: r.nameEn, ar: r.nameAr }) })),
    [reasons, t],
  );

  if (!open) return null;

  // ---- LOCKED: explain, do not ask ---------------------------------------------------------------------
  if (locked) {
    return (
      <Modal open onOpenChange={(o) => { if (!o) props.onCancel(); }} title={t(S.lockedTitle)}>
        <div data-testid="amend-locked">
          {/* The CHIP carries the status; the line beneath carries the detail a doctor acts on — WHEN and
              BY WHOM. Repeating the status in both is noise on a dialog whose whole job is one sentence. */}
          <StatusChip kind="warn" label={t(lockedLabel(locked.what))} />
          {(locked.when || locked.by) && (
            <p data-testid="amend-locked-detail">
              {[locked.when ? dateTime(locked.when) : null, locked.by ?? null]
                .filter(Boolean).join(" · ")}
            </p>
          )}
          {locked.what === "Expired" && <InlineAlert tone="info">{t(S.lockedExpiredHelp)}</InlineAlert>}
          <Button variant="secondary" onClick={props.onCancel}>{t(S.close)}</Button>
        </div>
      </Modal>
    );
  }

  // Only the outcomes that actually produced a schedule render one. BelowDispensed and NotChecked carry no
  // windows, and showing an empty schedule beside a refusal would read as "here is what will happen".
  const previewAsLegacy: ChronicPreview | null =
    preview && (preview.outcome === "Reallocated" || preview.outcome === "ConvertedToAcute")
      ? {
          newTotal: preview.newTotal,
          alreadyDispensed: preview.alreadyDispensed,
          remainingWindows: preview.remainingWindows,
          verdict: preview.outcome,
        }
      : null;

  const missingReason = touched && reasonCode === "";
  // Every refusal is knowable BEFORE the request, so it is rendered before it — a prescriber should not
  // learn from a 409 that they asked a patient to return medicine.
  const chronicBlocked = chronic && (
    previewFailed
    || preview === null
    || preview.outcome === "BelowDispensed"
    || preview.outcome === "NotChecked"
    || (preview.outcome === "NoLongerChronic" && !convertToAcute));
  const confirmLabel = action === "cancel" ? S.confirmCancel : S.confirmAmend;

  return (
    <Modal open onOpenChange={(o) => { if (!o) props.onCancel(); }}
           title={t(action === "cancel" ? S.cancelTitle : S.amendTitle)}>
      <div data-testid="amend-dialog">
        {/* NAMED, not "this line". The doctor is confirming a specific thing. */}
        <p data-testid="amend-subject"><strong>{lineLabel}</strong></p>
        <p>{t(action === "cancel" ? S.cancelBody : S.amendBody)}</p>

        {chronic && (
          <div className="amend-chronic">
            <label>
              {t(S.durationDays)}
              <input
                type="number"
                min={1}
                inputMode="numeric"
                value={durationDays}
                aria-label={t(S.durationDays)}
                onChange={(e) => setDurationDays(e.target.value)}
              />
            </label>
            <label>
              {t(S.frequencyMonths)}
              <input
                type="number"
                min={1}
                inputMode="numeric"
                value={frequencyMonths}
                aria-label={t(S.frequencyMonths)}
                onChange={(e) => setFrequencyMonths(e.target.value)}
              />
            </label>

            {previewFailed && <InlineAlert tone="bad">{t(S.previewFailed)}</InlineAlert>}

            {preview?.outcome === "BelowDispensed" && (
              <InlineAlert tone="bad">
                {t(S.belowDispensed).replace("{collected}", `${preview.alreadyDispensed} ${preview.unit}`)}
              </InlineAlert>
            )}

            {preview?.outcome === "NotChecked" && (
              <InlineAlert tone="warn">
                {t(S.notChecked).replace("{field}", preview.missingField ?? "—")}
              </InlineAlert>
            )}

            {preview?.outcome === "NoLongerChronic" && (
              <>
                <InlineAlert tone="warn">{t(S.noLongerChronic)}</InlineAlert>
                {/* An explicit act, because the conversion changes the dispensing pattern the patient was
                    told to expect. Design 46 §4: "either refuse, or convert it to acute with an explicit
                    confirmation from the prescriber". */}
                <label className="amend-convert">
                  <input
                    type="checkbox"
                    checked={convertToAcute}
                    onChange={(e) => setConvertToAcute(e.target.checked)}
                  />
                  {t(S.convertToAcute)}
                </label>
              </>
            )}
          </div>
        )}

        {action === "amend" && (
          <label>
            {t(S.quantity)}
            <input
              type="number"
              min={0}
              inputMode="decimal"
              value={quantity}
              aria-label={t(S.quantity)}
              onChange={(e) => setQuantity(e.target.value)}
            />
          </label>
        )}

        {/* 32.6 — the preview this section has rendered since 30.3, now with something to render. The
            `chronicPreview` prop stays for callers that already compute one; the chronic variant feeds it
            from the server. */}
        {(chronicPreview ?? previewAsLegacy) && (
          <section data-testid="chronic-preview" aria-label={t(S.previewTitle)}>
            <h3>{t(S.previewTitle)}</h3>
            {/* The dispensed portion is shown FIRST and marked immutable — design 46 §10 asks for exactly
                this, because the doctor's question is "what happens to what has already been collected?" */}
            <p>
              {t(S.previewCollected)}: <strong>{(chronicPreview ?? previewAsLegacy)!.alreadyDispensed}</strong>{" "}
              <StatusChip kind="neu" label={t(S.previewImmutable)} />
            </p>
            <ul>
              {(chronicPreview ?? previewAsLegacy)!.remainingWindows.map((qty, i) => (
                <li key={`w${i}`}>{t(S.previewWindow)} {i + 1}: {qty}</li>
              ))}
            </ul>
            <p>{t(S.previewTotal)}: <strong>{(chronicPreview ?? previewAsLegacy)!.newTotal}</strong></p>
          </section>
        )}

        {/* The reason list is a server catalogue, so it is searchable. The bare <label> is gone with it:
            `ComboboxField` renders the label, binds it to the input, and drops the duplicate `aria-label`
            that was naming the control a second time with the same words. */}
        <ComboboxField
          id="amend-reason"
          label={t(S.reason)}
          placeholder={t(S.reasonPlaceholder)}
          options={options}
          value={reasonCode === "" ? null : reasonCode}
          onChange={(v) => setReasonCode(v)}
        />
        {missingReason && <InlineAlert tone="bad">{t(S.reasonRequired)}</InlineAlert>}

        <TextareaField
          label={t(S.notes)}
          help={t(S.notesHelp)}
          maxLength={300}
          value={reasonText}
          onChange={(e) => setReasonText(e.target.value)}
        />

        <Button variant="secondary" onClick={props.onCancel}>{t(S.back)}</Button>
        <Button
          variant="danger"
          // Disabled by the REFUSALS, not only by the request in flight. Every one of them is knowable
          // before the click, and an enabled button beside "the patient cannot return medicine they have"
          // invites the click that produces the 409.
          disabled={busy || chronicBlocked}
          onClick={async () => {
            setTouched(true);
            if (reasonCode === "") return;
            setBusy(true);
            try {
              await props.onConfirm({
                reasonCode,
                reasonText: reasonText.trim() === "" ? undefined : reasonText.trim(),
                quantity: action === "amend" ? Number(quantity) : undefined,
                durationDays: chronic ? Number(durationDays) : undefined,
                frequencyMonths: chronic ? Number(frequencyMonths) : undefined,
                convertToAcute: chronic ? convertToAcute : undefined,
              });
            } finally {
              setBusy(false);
            }
          }}
        >
          {t(confirmLabel)}
        </Button>
      </div>
    </Modal>
  );
}

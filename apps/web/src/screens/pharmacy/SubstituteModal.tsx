import { useEffect, useState } from "react";
import { Button, InlineAlert, Modal, StatusChip } from "@mersal/design-system";
import type { Coded, DrugRef, Localized } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useLoc } from "../_shared";

const S = {
  title: { en: "Substitute this medicine", ar: "استبدال هذا الدواء" },
  original: { en: "Prescribed", ar: "الموصوف" },
  pick: { en: "Approved alternatives", ar: "البدائل المعتمدة" },
  loading: { en: "Looking up alternatives…", ar: "جارٍ البحث عن البدائل…" },
  none: {
    en: "No approved alternative is listed for this medicine. Dispense it as written, or refer the patient "
      + "back to the prescriber — substituting outside the formulary is not something this screen can record.",
    ar: "لا يوجد بديل معتمد مسجَّل لهذا الدواء. اصرفه كما هو مكتوب، أو أعد المريض إلى الطبيب الواصف — "
      + "فالاستبدال خارج القائمة المعتمدة لا يمكن تسجيله من هذه الشاشة.",
  },
  failed: {
    en: "The formulary could not be reached, so alternatives could not be listed. This is NOT a report that "
      + "none exist.",
    ar: "تعذّر الوصول إلى قائمة البدائل، لذلك لم يتم عرضها. هذا ليس تقريراً بعدم وجود بدائل.",
  },
  reason: { en: "Why are you substituting?", ar: "ما سبب الاستبدال؟" },
  reasonHint: {
    en: "The prescriber sees this. A substitution is you overriding what a doctor wrote — the record has to "
      + "say on whose judgement, and why.",
    ar: "سيطّلع الطبيب الواصف على هذا. الاستبدال تجاوز لما كتبه الطبيب — لذا يجب أن يوضّح السجل السبب "
      + "وصاحب القرار.",
  },
  tooShort: {
    en: "Write at least a short sentence. 'Out of stock' and 'patient reaction to the brand' are different "
      + "facts, and only one of them is about the patient.",
    ar: "اكتب جملة قصيرة على الأقل. «غير متوفر» و«تفاعل المريض مع العلامة التجارية» حقيقتان مختلفتان.",
  },
  apply: { en: "Use this instead", ar: "استخدم هذا بدلاً منه" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  sameIngredient: { en: "Same active ingredient", ar: "نفس المادة الفعالة" },
} satisfies Record<string, Localized>;

/**
 * Swapping a prescribed medicine for an approved formulary alternative at the counter.
 *
 * <b>Why the alternatives come from the formulary and are not typed in.</b> A pharmacist substituting
 * freehand is a pharmacist prescribing, which is not what this role is authorised to do. The list is the
 * drug's own ATC-5 class from master data — the same set the Substitutions screen shows — so a substitution
 * is a choice between equivalents rather than a free hand.
 *
 * <b>Why the reason is mandatory.</b> The dispense record is the only place the patient's actual medicine is
 * written down. Without a reason it shows a molecule the prescriber did not choose and no account of why,
 * which is worse than either the substitution or the refusal on its own.
 *
 * <b>An empty list is not a failure, and a failure is not an empty list.</b> The two are rendered
 * differently on purpose: "no alternative is approved for this" is an answer a pharmacist can act on, and
 * "the formulary did not respond" is not.
 */
export function SubstituteModal({
  open,
  onOpenChange,
  drug,
  onChosen,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  drug: Coded;
  onChosen: (substitute: Coded, reason: string) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const [state, setState] = useState<"loading" | "ready" | "error">("loading");
  const [alts, setAlts] = useState<DrugRef[]>([]);
  const [picked, setPicked] = useState<DrugRef | null>(null);
  const [reason, setReason] = useState("");

  useEffect(() => {
    if (!open) return;
    let live = true;
    setState("loading");
    setPicked(null);
    setReason("");
    api.drugAlternatives(drug.code)
      .then((rows) => { if (live) { setAlts(rows); setState("ready"); } })
      .catch(() => { if (live) setState("error"); });
    return () => { live = false; };
  }, [open, drug.code, api]);

  const reasonOk = reason.trim().length >= 10;

  return (
    <Modal open={open} onOpenChange={onOpenChange} title={t(S.title)}>
      <p className="muted" style={{ marginBlockStart: 0 }}>
        <strong>{t(S.original)}:</strong> {t(drug.label)}
      </p>

      {state === "loading" && <p className="muted">{t(S.loading)}</p>}
      {state === "error" && <InlineAlert tone="bad">{t(S.failed)}</InlineAlert>}
      {state === "ready" && alts.length === 0 && <InlineAlert tone="warn">{t(S.none)}</InlineAlert>}

      {state === "ready" && alts.length > 0 && (
        <>
          <h3 className="section-h">{t(S.pick)}</h3>
          <ul className="rxv-lines">
            {alts.map((a) => (
              <li key={a.drugId} className="rxv-line">
                <label className="rx-sub-option">
                  <input
                    type="radio"
                    name="substitute"
                    checked={picked?.drugId === a.drugId}
                    onChange={() => setPicked(a)}
                  />
                  <span className="rxv-drug">{t(a.name)}</span>
                  <StatusChip kind="neu" label={t(S.sameIngredient)} />
                </label>
              </li>
            ))}
          </ul>

          <label className="mc-field">
            <span className="mc-field-label">{t(S.reason)}</span>
            <p className="muted" style={{ margin: 0 }}>{t(S.reasonHint)}</p>
            <textarea
              className="rx-field-input"
              rows={2}
              value={reason}
              onChange={(e) => setReason(e.currentTarget.value)}
            />
          </label>
          {reason.trim().length > 0 && !reasonOk && <InlineAlert tone="warn">{t(S.tooShort)}</InlineAlert>}
        </>
      )}

      <div className="rx-actions">
        <Button variant="ghost" onClick={() => onOpenChange(false)}>{t(S.cancel)}</Button>
        <Button
          variant="primary"
          disabled={!picked || !reasonOk}
          onClick={() => {
            if (!picked) return;
            // `system: "ATC"` because that is the code space the formulary's alternatives come from — the
            // drug's own ATC-5 class. The dispense record carries the substitute's catalogue id, so the
            // molecule actually handed over is identifiable later without re-deriving it from a name.
            onChosen({ system: "ATC", code: picked.drugId, label: picked.name }, reason.trim());
            onOpenChange(false);
          }}
        >
          {t(S.apply)}
        </Button>
      </div>
    </Modal>
  );
}

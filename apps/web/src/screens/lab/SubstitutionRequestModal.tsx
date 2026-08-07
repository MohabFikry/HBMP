import { useState } from "react";
import { Button, InlineAlert, Modal } from "@mersal/design-system";
import type { InvestigationOrder, InvestigationOrderLine, Localized } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useLoc } from "../_shared";

const S = {
  title: { en: "Ask about a different examination", ar: "الاستفسار عن فحص بديل" },
  ordered: { en: "Ordered", ar: "المطلوب" },
  why: {
    en: "There is no approved list of equivalent examinations. A drug can be swapped for another in the same "
      + "class because master data says they are equivalent; nothing records that one test may stand in for "
      + "another. So this asks the approval team rather than offering you a choice nobody has vetted.",
    ar: "لا توجد قائمة معتمدة بالفحوصات المكافئة. يمكن استبدال الدواء بآخر من نفس الفئة لأن البيانات المرجعية "
      + "تُقرّ بتكافئهما، ولا يوجد ما يُسجّل أن فحصاً يغني عن آخر. لذلك يُحال هذا إلى فريق الموافقات بدلاً من "
      + "عرض بدائل لم يعتمدها أحد.",
  },
  reason: { en: "Why can't this be performed as written?", ar: "لماذا لا يمكن تنفيذه كما هو مكتوب؟" },
  reasonHint: {
    en: "The approval team sees this and nothing else. Say what happened — the whole decision rests on it.",
    ar: "لن يرى فريق الموافقات سوى هذا. اذكر ما حدث — فالقرار كله يستند إليه.",
  },
  reasonPlaceholder: {
    en: "e.g. the contrast MRI scanner is out of service until Thursday and the patient travelled today",
    ar: "مثال: جهاز الرنين بالصبغة متوقف حتى الخميس وقد سافر المريض اليوم",
  },
  tooShort: {
    en: "Write at least a short sentence. An approver with an empty box is deciding on who asked, not on why.",
    ar: "اكتب جملة قصيرة على الأقل. المُوافِق بدون سبب يقرّر بناءً على من طلب، لا على السبب.",
  },
  proposed: { en: "Suggested alternative code (optional)", ar: "كود بديل مقترح (اختياري)" },
  proposedHint: {
    en: "Leave this blank if you don't have one. \"We can't run this\" is a complete request — naming a "
      + "replacement test is a clinical choice, and it is not yours to make.",
    ar: "اتركه فارغاً إن لم يكن لديك اقتراح. «لا يمكننا تنفيذ هذا» طلب مكتمل — أما تحديد فحص بديل فقرار "
      + "إكلينيكي ليس من اختصاصك.",
  },
  send: { en: "Send request", ar: "إرسال الطلب" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  failed: { en: "The request could not be sent.", ar: "تعذّر إرسال الطلب." },
  alreadyRequested: {
    en: "Someone has already asked about this line. It is with the approval team.",
    ar: "سبق أن استفسر أحدهم عن هذا البند. الطلب لدى فريق الموافقات.",
  },
} satisfies Record<string, Localized>;

/**
 * Asking the approval team whether another examination may stand in for the one ordered.
 *
 * <b>Why this is a question and the pharmacy's equivalent is a picker.</b> A pharmacist substituting a drug
 * chooses from the product's own ATC-5 class — a clinically-sound equivalence set that exists in master data,
 * which the server checks the choice against and refuses anything outside. Nothing equivalent exists for
 * examinations: the catalogue records a category and a sensitivity, and neither says that one test may stand
 * in for another. A picker here would have to be derived from the category, which would put "any radiology
 * procedure" behind a button — a technician prescribing.
 *
 * <b>Why the proposal is optional.</b> "We cannot run this one" is a complete and useful request. Requiring
 * a replacement would push a technician into naming a test they are not qualified to choose, and an approver
 * reading a suggestion made under that pressure is worse off than one reading none.
 */
export function SubstitutionRequestModal({
  open,
  onOpenChange,
  order,
  line,
  onSent,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  order: InvestigationOrder;
  line: InvestigationOrderLine;
  onSent: (message: Localized) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const [reason, setReason] = useState("");
  const [proposed, setProposed] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  const reasonOk = reason.trim().length >= 10;

  async function send() {
    setBusy(true);
    setError(null);
    try {
      const { authNo } = await api.requestSubstitution({
        orderId: order.id,
        orderLineId: line.id,
        orderReference: order.orderNo,
        beneficiaryId: order.patient.id,
        orderedCode: line.test.code,
        orderedLabel: t(line.test.label),
        proposedCode: proposed.trim() || undefined,
        reason: reason.trim(),
      });
      onSent({
        en: `Sent to the approval team as ${authNo}. The order is unchanged until they decide.`,
        ar: `أُرسل إلى فريق الموافقات برقم ${authNo}. يبقى الطلب كما هو حتى يصدر قرارهم.`,
      });
      onOpenChange(false);
    } catch (e) {
      // A 409 means the question is already asked — which is an ANSWER, not a failure, and the technician
      // should stop trying rather than raise a third copy of the same request.
      setError((e as { status?: number })?.status === 409 ? S.alreadyRequested : S.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal open={open} onOpenChange={onOpenChange} title={t(S.title)}>
      <p className="muted" style={{ marginBlockStart: 0 }}>
        <strong>{t(S.ordered)}:</strong> <span className="tnum">{line.test.code}</span> · {t(line.test.label)}
      </p>

      <InlineAlert tone="info">{t(S.why)}</InlineAlert>

      <label className="mc-field">
        <span className="mc-field-label">{t(S.reason)}</span>
        <p className="muted" style={{ margin: 0 }}>{t(S.reasonHint)}</p>
        <textarea
          className="rx-field-input"
          rows={3}
          placeholder={t(S.reasonPlaceholder)}
          value={reason}
          onChange={(e) => setReason(e.currentTarget.value)}
        />
      </label>
      {reason.trim().length > 0 && !reasonOk && <InlineAlert tone="warn">{t(S.tooShort)}</InlineAlert>}

      <label className="mc-field">
        <span className="mc-field-label">{t(S.proposed)}</span>
        <p className="muted" style={{ margin: 0 }}>{t(S.proposedHint)}</p>
        <input
          className="rx-field-input"
          type="text"
          value={proposed}
          onChange={(e) => setProposed(e.currentTarget.value)}
        />
      </label>

      <div aria-live="polite">
        {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      </div>

      <div className="rx-actions">
        <Button variant="ghost" onClick={() => onOpenChange(false)}>{t(S.cancel)}</Button>
        <Button variant="primary" disabled={!reasonOk} loading={busy} onClick={() => void send()}>
          {t(S.send)}
        </Button>
      </div>
    </Modal>
  );
}

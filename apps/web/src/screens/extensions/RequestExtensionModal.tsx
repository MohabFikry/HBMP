import { useState } from "react";
import { Button, InlineAlert, Modal } from "@mersal/design-system";
import type { Localized, ValidityExtensionRequest } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { ApiError } from "../../api/http";
import { useLoc } from "../_shared";

const S = {
  title: { en: "Ask for this to be revalidated", ar: "طلب إعادة تفعيل هذا" },
  reason: { en: "Why does this need extending?", ar: "لماذا يحتاج هذا إلى تمديد؟" },
  reasonHint: {
    en: "The approval team sees this and nothing else. Say what happened — the whole decision rests on it.",
    ar: "لن يرى فريق الموافقات سوى هذا. اذكر ما حدث — فالقرار كله يستند إليه.",
  },
  tooShort: {
    en: "Write at least a short sentence. An approver with an empty box is deciding on who asked, not on why.",
    ar: "اكتب جملة قصيرة على الأقل. المُوافِق بدون سبب يقرّر بناءً على من طلب، لا على السبب.",
  },
  send: { en: "Send request", ar: "إرسال الطلب" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  failed: { en: "The request could not be sent.", ar: "تعذّر إرسال الطلب." },
} satisfies Record<string, Localized>;

/**
 * Asking the approval team to revalidate something that has expired.
 *
 * <b>One component for both counters.</b> A pharmacist holding a lapsed prescription and a technician
 * holding a lapsed order are doing the same thing, to the same queue, under the same scope — and the part
 * that is easy to get subtly wrong is identical in both: the reason floor, the 409-is-an-answer reading, and
 * the confirmation that must not let "request sent" read as "sorted". Two copies of that would drift, and
 * the copy that drifted would be the one nobody looked at again.
 *
 * The caller supplies the wording around it — what expired, and what the patient should be told — because
 * that genuinely differs between a course of antibiotics and a chest x-ray.
 */
export function RequestExtensionModal({
  open,
  onOpenChange,
  item,
  placeholder,
  sentMessage,
  alreadyRequestedMessage,
  onSent,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  item: Pick<ValidityExtensionRequest, "itemType" | "itemId" | "itemReference" | "beneficiaryId" | "expiredAt">;
  placeholder: Localized;
  /** Must contain `{authNo}`, and must say the item is still expired until a decision lands. */
  sentMessage: Localized;
  alreadyRequestedMessage: Localized;
  /** Called with the message to display. The outcome is rendered by the CALLER, in its own live region. */
  onSent: (message: Localized) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  // The server's floor is ten characters. Mirrored so the counter is told before the round trip rather than
  // by a 422 — but the server keeps enforcing it, because this is a hint and not a rule.
  const reasonOk = reason.trim().length >= 10;

  async function send() {
    setBusy(true);
    setFailed(false);
    try {
      const res = await api.requestValidityExtension({ ...item, reason: reason.trim() });
      onOpenChange(false);
      setReason("");
      onSent({
        en: t(sentMessage).replace("{authNo}", res.authNo),
        ar: t(sentMessage).replace("{authNo}", res.authNo),
      });
    } catch (e) {
      // A 409 is an ANSWER — somebody already asked — not a failure to ask. Reporting "failed" would send
      // the counter round the loop to raise a duplicate the server refuses anyway.
      const status = e instanceof ApiError ? e.status : 0;
      if (status === 409) {
        onOpenChange(false);
        setReason("");
        onSent(alreadyRequestedMessage);
      } else {
        setFailed(true);
      }
    } finally {
      setBusy(false);
    }
  }

  const heading = item.itemReference ? `${t(S.title)} · ${item.itemReference}` : t(S.title);

  return (
    <Modal open={open} onOpenChange={onOpenChange} title={heading}>
      <label className="mc-field">
        <span className="mc-field-label">{t(S.reason)}</span>
        <p className="muted" style={{ margin: 0 }}>{t(S.reasonHint)}</p>
        <textarea
          className="rx-field-input"
          rows={3}
          value={reason}
          placeholder={t(placeholder)}
          onChange={(e) => setReason(e.currentTarget.value)}
        />
      </label>
      {reason.trim().length > 0 && !reasonOk && <InlineAlert tone="warn">{t(S.tooShort)}</InlineAlert>}
      {failed && <InlineAlert tone="bad">{t(S.failed)}</InlineAlert>}
      <div className="rx-actions">
        <Button variant="ghost" onClick={() => onOpenChange(false)}>{t(S.cancel)}</Button>
        <Button variant="primary" loading={busy} disabled={!reasonOk || busy} onClick={() => void send()}>
          {t(S.send)}
        </Button>
      </div>
    </Modal>
  );
}

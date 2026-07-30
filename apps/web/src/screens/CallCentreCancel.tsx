import { useCallback, useState } from "react";
import { Button, Icon, InlineAlert, Modal } from "@mersal/design-system";
import type { AppointmentRow, Localized } from "@mersal/contracts";
import { identifierTypeLabel } from "./statusLabels";
import type { CcApi } from "./CallCentre";

const S = {
  cancel: { en: "Cancel appointment", ar: "إلغاء الموعد" },
  title: { en: "Cancel this appointment?", ar: "إلغاء هذا الموعد؟" },
  body: {
    en: "The time is released for someone else and anyone on the waitlist may be offered it. Verify the caller first — this cannot be undone from here.",
    ar: "سيُتاح الوقت لشخص آخر وقد يُعرض على من في قائمة الانتظار. تحقّق من هوية المتصل أولاً — لا يمكن التراجع عن هذا من هنا.",
  },
  verifyStep: { en: "1. Verify the caller", ar: "١. تحقّق من هوية المتصل" },
  reasonStep: { en: "2. Reason", ar: "٢. السبب" },
  needTwo: { en: "Confirm at least two identifiers with the caller.", ar: "أكّد هويتين على الأقل مع المتصل." },
  needReason: { en: "Choose a reason.", ar: "اختر سبباً." },
  keep: { en: "Keep it", ar: "الاحتفاظ به" },
  lookupFailed: {
    en: "Couldn't load this member's identifiers. Cancel from the call workspace instead.",
    ar: "تعذّر تحميل هويات هذا العضو. ألغِ من مساحة المكالمة بدلاً من ذلك.",
  },
  failed: { en: "The cancellation was refused. Nothing was changed.", ar: "تم رفض الإلغاء. لم يتغير شيء." },
  loadingIds: { en: "Loading this member's identifiers…", ar: "جاري تحميل هويات هذا العضو…" },
} satisfies Record<string, Localized>;

const CANCEL_REASONS = [
  "PatientRequest", "PatientUnwell", "TransportIssue", "Rescheduling",
  "ClinicClosure", "DuplicateBooking", "Other",
];

/**
 * Cancelling an appointment from the call centre's board.
 *
 * ============================================================================================================
 * WHY THIS IS NOT JUST A BUTTON
 * ============================================================================================================
 * The obvious implementation — call emr's `/appointments/{id}/cancel` directly — WOULD have worked: the call
 * centre holds `appointment:reserve`, so emr permits it. That is exactly the problem. Every reserve path is
 * supposed to run through callcentre-service, which refuses without an interaction carrying a recorded
 * verification PASS, and going straight to emr would step around the one gate the whole of phase 15 exists
 * to enforce. An agent could then cancel any appointment for anyone who rang up and read out a name.
 *
 * So this does what `CallCentreBooking` already does for booking: it OPENS its own call record, verifies
 * inside itself, and then acts through the façade. The dialog is a different route to the same rule, not an
 * exemption from it — and if the member's identifiers cannot be loaded it refuses and points at the
 * workspace rather than falling back to the unguarded path.
 *
 * The reason is a CODE, not free text: the call centre's cancellations are what the no-show and rebook
 * reports group by, and a typed sentence cannot be counted.
 */
export function CallCentreCancelButton({
  row, api, t, onCancelled,
}: {
  row: AppointmentRow;
  api: CcApi;
  t: (l: Localized) => string;
  onCancelled: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [types, setTypes] = useState<string[] | null>(null);
  const [ticks, setTicks] = useState<Set<string>>(new Set());
  const [reason, setReason] = useState("");
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);

  // Only an appointment that is still going to happen can be cancelled. The server refuses the transition
  // otherwise, and a button that can only fail teaches the agent the screen is unreliable.
  const cancellable = row.checkInEligible || row.checkedIn;

  const start = useCallback(async () => {
    setOpen(true);
    setError(null);
    setTicks(new Set());
    setReason("");
    setTypes(null);
    // Which identifiers this member can be challenged on. Without them there is nothing to verify against,
    // and verification is the point — so a failure here stops the flow rather than skipping it.
    const name = row.beneficiaryName ?? "";
    const matches = await api.search(name).catch(() => []);
    const hit = matches.find((m) => m.beneficiaryId === row.beneficiary.id);
    if (!hit) {
      setError(S.lookupFailed);
      return;
    }
    setTypes(hit.challengeableIdentifierTypes);
  }, [api, row]);

  const toggle = (type: string) =>
    setTicks((prev) => {
      const next = new Set(prev);
      if (next.has(type)) next.delete(type); else next.add(type);
      return next;
    });

  async function confirm() {
    if (ticks.size < 2) { setError(S.needTwo); return; }
    if (!reason) { setError(S.needReason); return; }
    setError(null);
    setBusy(true);
    try {
      // The call record this cancellation hangs off. Opened here, exactly as the standalone booking screen
      // opens its own — an action with no call behind it is the audit gap phase 15 closed.
      const opened = await api.openInteraction("CancelAppointment").catch(() => null);
      if (!opened?.interactionId) { setError(S.failed); return; }

      const passed = await api.verify(opened.interactionId, row.beneficiary.id, [...ticks], true);
      if (!passed) { setError(S.failed); return; }

      const outcome = await api.cancel(opened.interactionId, row.id, reason);
      if (outcome !== "ok") { setError(S.failed); return; }

      await api.close(opened.interactionId, "Resolved", `Cancelled: ${reason}`).catch(() => {});
      setOpen(false);
      onCancelled();
    } finally {
      setBusy(false);
    }
  }

  if (!cancellable) return null;

  return (
    <>
      <Button
        variant="ghost"
        size="sm"
        // Icon-only, so it needs a name — and the name says WHICH appointment, because a table of identical
        // "Cancel appointment" buttons is unusable with a screen reader.
        aria-label={`${t(S.cancel)} — ${row.beneficiaryName ?? row.beneficiary.token}`}
        title={t(S.cancel)}
        leadingIcon={<Icon name="cross" />}
        onClick={() => void start()}
      />
      <Modal
        open={open}
        onOpenChange={setOpen}
        title={t(S.title)}
        description={t(S.body)}
        footer={
          <>
            {/* "Keep it", not "Cancel": a Cancel button on a cancellation dialog is read by half of
                operators as "cancel the appointment". */}
            <Button variant="secondary" onClick={() => setOpen(false)}>{t(S.keep)}</Button>
            <Button variant="danger" loading={busy} disabled={!types} onClick={() => void confirm()}>
              {t(S.cancel)}
            </Button>
          </>
        }
      >
        <div className="stack-3">
          <p style={{ margin: 0 }}><strong>{row.beneficiaryName ?? row.beneficiary.token}</strong></p>

          <fieldset className="fieldset">
            <legend>{t(S.verifyStep)}</legend>
            {types === null ? (
              <p className="muted" role="status">{t(S.loadingIds)}</p>
            ) : (
              types.map((type) => (
                <label key={type} className="check">
                  <input type="checkbox" checked={ticks.has(type)} onChange={() => toggle(type)} />{" "}
                  {t(identifierTypeLabel(type))}
                </label>
              ))
            )}
          </fieldset>

          <div className="cc-field">
            <span id="cc-cancel-reason-code">{t(S.reasonStep)}</span>
            <select
              aria-labelledby="cc-cancel-reason-code"
              className="mrs-control"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
            >
              <option value="">—</option>
              {CANCEL_REASONS.map((code) => <option key={code} value={code}>{code}</option>)}
            </select>
          </div>

          {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
        </div>
      </Modal>
    </>
  );
}

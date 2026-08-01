import { useCallback, useRef, useState } from "react";
import { Button, Icon, InlineAlert, Modal } from "@mersal/design-system";
import type { AppointmentRow, Localized } from "@mersal/contracts";
import type { CcApi } from "./CallCentre";

const S = {
  cancel: { en: "Cancel appointment", ar: "إلغاء الموعد" },
  title: { en: "Cancel this appointment?", ar: "إلغاء هذا الموعد؟" },
  body: {
    en: "The time is released for someone else and anyone on the waitlist may be offered it. Confirm who you are speaking to before you continue — this cannot be undone from here.",
    ar: "سيُتاح الوقت لشخص آخر وقد يُعرض على من في قائمة الانتظار. تأكّد ممّن تتحدث إليه قبل المتابعة — لا يمكن التراجع عن هذا من هنا.",
  },
  reasonStep: { en: "Reason", ar: "السبب" },
  needReason: { en: "Choose a reason.", ar: "اختر سبباً." },
  keep: { en: "Keep it", ar: "الاحتفاظ به" },
  lookupFailed: {
    en: "Couldn't find this member. Cancel from the call workspace instead.",
    ar: "تعذّر العثور على هذا العضو. ألغِ من مساحة المكالمة بدلاً من ذلك.",
  },
  failed: { en: "The cancellation was refused. Nothing was changed.", ar: "تم رفض الإلغاء. لم يتغير شيء." },
  loadingMember: { en: "Finding this member…", ar: "جاري البحث عن هذا العضو…" },
  // The appointment IS cancelled at this point — only the call record failed to wrap up. Saying "failed"
  // here would send the agent back to cancel an appointment that is already gone.
  closeFailed: {
    en: "The appointment was cancelled, but this call is still open. Press Confirm again to finish the call record.",
    ar: "تم إلغاء الموعد، لكن هذه المكالمة ما زالت مفتوحة. اضغط تأكيد مرة أخرى لإنهاء سجل المكالمة.",
  },
} satisfies Record<string, Localized>;

/** The wrap-up summary other roles read on the member's profile. Built from the reason code so it says what
 *  happened without the agent typing it — a cancellation taken from the board has exactly one story. */
const summaryFor = (reason: string) =>
  `Appointment cancelled by the call centre on this call. Reason: ${reason}.`;

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
 * supposed to run through callcentre-service, which refuses on a call not bound to that member, and going
 * straight to emr would step around the gate entirely — leaving a cancellation with no call behind it and
 * nothing in the audit trail tying it to a conversation.
 *
 * So this does what `CallCentreBooking` already does for booking: it OPENS its own call record, binds it to
 * the member, and then acts through the façade. The dialog is a different route to the same rule, not an
 * exemption from it — and if the member cannot be resolved it refuses and points at the workspace rather than
 * falling back to the unguarded path.
 *
 * <b>The identifier challenge is gone.</b> Identity is confirmed by the agent on the phone; the dialog says so
 * and asks for the one thing it still needs, which is the reason.
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
  /** The resolved member. `null` while the lookup is in flight — the dialog cannot act until it knows the
   *  beneficiary id the call has to be bound to. */
  const [found, setFound] = useState<{ beneficiaryId: string } | null>(null);
  const [reason, setReason] = useState("");
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  // Progress through the three server steps, so a retry resumes rather than restarts. Refs, not state: a
  // re-render between attempts must not lose track of a call that is already open or an appointment that is
  // already cancelled.
  const interaction = useRef<string | null>(null);
  const cancelled = useRef(false);

  // Only an appointment that is still going to happen can be cancelled. The server refuses the transition
  // otherwise, and a button that can only fail teaches the agent the screen is unreliable.
  const cancellable = row.checkInEligible || row.checkedIn;

  const start = useCallback(async () => {
    setOpen(true);
    setError(null);
    setReason("");
    setFound(null);
    // A fresh dialog is a fresh call — never resume the previous one's progress.
    interaction.current = null;
    cancelled.current = false;
    // Confirm the row's beneficiary actually resolves through the call-centre directory before offering to
    // act. Failing here stops the flow rather than skipping it: without a member there is nothing to bind the
    // call to, and the cancel would be refused anyway — better said now than after the agent commits.
    const name = row.beneficiaryName ?? "";
    const matches = await api.search(name).catch(() => []);
    const hit = matches.find((m) => m.beneficiaryId === row.beneficiary.id);
    if (!hit) {
      setError(S.lookupFailed);
      return;
    }
    setFound({ beneficiaryId: hit.beneficiaryId });
  }, [api, row]);

  async function confirm() {
    if (!reason) { setError(S.needReason); return; }
    setError(null);
    setBusy(true);
    try {
      // The call record this cancellation hangs off. Opened here, exactly as the standalone booking screen
      // opens its own — an action with no call behind it is the audit gap phase 15 closed.
      //
      // Held in a ref across attempts. A retry after a failed wrap-up must finish THIS call, not open a
      // second one and leave the first Open forever — which is the very state this whole fix is about.
      if (!interaction.current) {
        const opened = await api.openInteraction("CancelAppointment").catch(() => null);
        if (!opened?.interactionId) { setError(S.failed); return; }
        interaction.current = opened.interactionId;

        // Bind the call to this member — the agent confirmed who they are speaking to on the phone before
        // opening this dialog, and the server refuses every act on a call it cannot resolve to a member.
        const bound = await api.openMember(opened.interactionId, row.beneficiary.id);
        if (!bound) { setError(S.failed); return; }
      }

      // Likewise skip the cancellation itself if a previous attempt already made it — retrying the whole
      // flow would send a second cancel for an appointment that is already gone.
      if (!cancelled.current) {
        const outcome = await api.cancel(interaction.current, row.id, reason);
        if (outcome !== "ok") { setError(S.failed); return; }
        cancelled.current = true;
      }

      // A summary is REQUIRED to close with outcome Resolved. This used to swallow the refusal with
      // `.catch(() => {})`, so every cancellation taken here left its interaction Open on the server — and an
      // open interaction is still bound to that member, still disclosing.
      const closed = await api.close(interaction.current, "Resolved", summaryFor(reason));
      if (closed !== "ok") { setError(S.closeFailed); return; }

      interaction.current = null;
      cancelled.current = false;
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
            <Button variant="danger" loading={busy} disabled={!found} onClick={() => void confirm()}>
              {t(S.cancel)}
            </Button>
          </>
        }
      >
        <div className="stack-3">
          <p style={{ margin: 0 }}><strong>{row.beneficiaryName ?? row.beneficiary.token}</strong></p>

          {found === null && !error && <p className="muted" role="status">{t(S.loadingMember)}</p>}

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

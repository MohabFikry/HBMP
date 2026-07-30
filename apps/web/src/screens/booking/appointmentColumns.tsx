import { useState } from "react";
import { Button, Icon, InputField, Modal, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AppointmentRow, Localized, Practitioner, Specialty } from "@mersal/contracts";
import type { Formatters } from "../../i18n/useFormat";
import { AppointmentNoteButton } from "../AppointmentNote";

const S = {
  patient: { en: "Patient", ar: "المريض" },
  doctor: { en: "Doctor", ar: "الطبيب" },
  specialty: { en: "Specialty", ar: "التخصص" },
  type: { en: "Type", ar: "النوع" },
  time: { en: "Time", ar: "الوقت" },
  status: { en: "Status", ar: "الحالة" },
  note: { en: "Note", ar: "ملاحظة" },
  unnamed: { en: "Name not recorded", ar: "الاسم غير مسجل" },
  noDoctor: { en: "No named doctor", ar: "بدون طبيب محدد" },

  cancel: { en: "Cancel appointment", ar: "إلغاء الموعد" },
  cancelTitle: { en: "Cancel this appointment?", ar: "إلغاء هذا الموعد؟" },
  cancelBody: {
    en: "The time is released for someone else and anyone on the waitlist may be offered it. Tell the patient before you confirm.",
    ar: "سيُتاح الوقت لشخص آخر وقد يُعرض على من في قائمة الانتظار. أبلغ المريض قبل التأكيد.",
  },
  reason: { en: "Reason", ar: "السبب" },
  reasonHelp: { en: "Recorded on the appointment and in the audit trail.", ar: "يُسجل على الموعد وفي سجل التدقيق." },
  reasonRequired: { en: "A reason is required.", ar: "السبب مطلوب." },
  keep: { en: "Keep it", ar: "الاحتفاظ به" },
  confirm: { en: "Cancel appointment", ar: "إلغاء الموعد" },
} satisfies Record<string, Localized>;

export interface ColumnDeps {
  t: (l: Localized) => string;
  fmt: Formatters;
  /** Practitioners, for resolving the doctor's name and specialty. Read under `practitioner:read`. */
  doctorById: Map<string, Practitioner>;
  specialties: Specialty[];
}

/**
 * Who the appointment is FOR.
 *
 * <b>The name, not the masked token.</b> Reception and the call centre are both entitled to it — the desk
 * greets the person and walks them to a room, the agent is speaking to them on the phone — and neither job
 * can be done against "•••4821". The token remains on the boards that genuinely do not need identity (lab,
 * pharmacy, approvals), where it is the right answer.
 *
 * The token is still the FALLBACK, for a row booked before the name was captured. Blank would read as data
 * loss; the token at least identifies the row against a queue ticket.
 */
export function patientColumn({ t }: Pick<ColumnDeps, "t">): Column<AppointmentRow> {
  return {
    key: "patient",
    header: t(S.patient),
    sortable: true,
    cell: (r) =>
      r.beneficiaryName
        ? <strong>{r.beneficiaryName}</strong>
        : <span className="tnum muted" title={t(S.unnamed)}>{r.beneficiary.token}</span>,
    sortValue: (r) => r.beneficiaryName ?? r.beneficiary.token,
  };
}

/**
 * Doctor and specialty, joined in from provider-service.
 *
 * emr returns a `doctorId` and nothing more — who a practitioner IS belongs to provider-service, and having
 * emr answer for it would be one service composing another's data on the caller's behalf. Both portals hold
 * `practitioner:read` and read it directly, so the join happens here.
 */
export function doctorColumns({ t, doctorById, specialties }: ColumnDeps): Column<AppointmentRow>[] {
  const specialtyName = (code?: string) => {
    if (!code) return null;
    const hit = specialties.find((s) => s.code === code);
    // The code is the honest fallback while the reference list loads — a dash would claim the doctor has no
    // specialty, which is a different and worse statement.
    return hit ? t(hit.name) : code;
  };
  const doctorOf = (r: AppointmentRow) => (r.doctorId ? doctorById.get(r.doctorId) : undefined);

  return [
    {
      key: "doctor",
      header: t(S.doctor),
      sortable: true,
      cell: (r) => {
        const d = doctorOf(r);
        // A general clinic session genuinely has no named doctor — that is a fact about the appointment, not
        // a gap in the data, so it is said rather than left blank.
        return d ? t(d.name) : <span className="muted">{t(S.noDoctor)}</span>;
      },
      sortValue: (r) => doctorOf(r)?.name.en,
    },
    {
      key: "specialty",
      header: t(S.specialty),
      sortable: true,
      cell: (r) => specialtyName(doctorOf(r)?.primarySpecialty) ?? <span className="muted">—</span>,
      sortValue: (r) => specialtyName(doctorOf(r)?.primarySpecialty) ?? undefined,
    },
  ];
}

export function timeAndStatusColumns({ t, fmt }: Pick<ColumnDeps, "t" | "fmt">): Column<AppointmentRow>[] {
  return [
    { key: "type", header: t(S.type), cell: (r) => r.appointmentType, sortable: true, sortValue: (r) => r.appointmentType },
    {
      key: "time",
      header: t(S.time),
      sortable: true,
      cell: (r) => <span className="tnum">{fmt.time(r.scheduledStart)}</span>,
      // The ISO instant, not the rendered time — the only value that orders correctly across midnight.
      sortValue: (r) => r.scheduledStart,
    },
    {
      key: "status",
      header: t(S.status),
      sortable: true,
      cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />,
      // Sorted by the LOCALISED label, so an Arabic user gets Arabic alphabetical order rather than an
      // ordering derived from English text they cannot see.
      sortValue: (r) => t(r.status.label),
    },
  ];
}

export function noteColumn({ t }: Pick<ColumnDeps, "t">): Column<AppointmentRow> {
  return {
    key: "note",
    header: t(S.note),
    cell: (r) => <AppointmentNoteButton note={r.note} by={r.noteBy} at={r.noteAt} />,
  };
}

/**
 * The cancel action, with a confirmation that asks for a reason.
 *
 * <b>Why a modal and not a bare icon.</b> Cancelling releases the slot and may hand it straight to someone
 * on the waitlist — it is not undoable by clicking again, and the patient is usually unaware until they
 * arrive. A single mis-click in a dense table should not be able to do that.
 *
 * <b>Why the reason is mandatory.</b> Not a schema rule — ours. A cancellation with no reason is
 * unanswerable when the patient rings back asking why, and it is the field every rebook and no-show report
 * groups by. Requiring it costs the operator three seconds at the one moment they still know the answer.
 */
export function CancelAppointmentButton({
  row, t, onCancel,
}: {
  row: AppointmentRow;
  t: (l: Localized) => string;
  onCancel: (reason: string) => Promise<unknown>;
}) {
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState("");
  const [missing, setMissing] = useState(false);
  const [busy, setBusy] = useState(false);

  // A cancelled or completed appointment has nothing to cancel; the server refuses the transition anyway, and
  // offering a button that can only fail teaches the operator the screen is unreliable.
  const cancellable = row.checkInEligible || row.checkedIn;
  if (!cancellable) return null;

  async function confirm() {
    if (!reason.trim()) {
      setMissing(true);
      return;
    }
    setMissing(false);
    setBusy(true);
    try {
      await onCancel(reason.trim());
      setOpen(false);
      setReason("");
    } finally {
      setBusy(false);
    }
  }

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
        onClick={() => setOpen(true)}
      />
      <Modal
        open={open}
        onOpenChange={setOpen}
        title={t(S.cancelTitle)}
        description={t(S.cancelBody)}
        footer={
          <>
            {/* "Keep it" rather than "Cancel": a Cancel button on a cancellation dialog is genuinely
                ambiguous — half of operators read it as "cancel the appointment". */}
            <Button variant="secondary" onClick={() => setOpen(false)}>{t(S.keep)}</Button>
            <Button variant="danger" loading={busy} onClick={() => void confirm()}>{t(S.confirm)}</Button>
          </>
        }
      >
        <div className="stack-3">
          <p style={{ margin: 0 }}>
            <strong>{row.beneficiaryName ?? row.beneficiary.token}</strong>
          </p>
          <InputField
            label={t(S.reason)}
            help={t(S.reasonHelp)}
            value={reason}
            error={missing ? t(S.reasonRequired) : undefined}
            onChange={(e) => setReason(e.currentTarget.value)}
            autoComplete="off"
          />
        </div>
      </Modal>
    </>
  );
}

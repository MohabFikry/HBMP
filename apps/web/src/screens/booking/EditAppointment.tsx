import { useCallback, useEffect, useMemo, useState } from "react";
import { Button, ComboboxField, Icon, InlineAlert, Modal, TextareaField } from "@mersal/design-system";
import type {
  AppointmentDay, AppointmentRow, BookableSlot, DoctorAvailability, Localized, Practitioner,
} from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { BookingTimePicker, monthKey } from "./BookingTimePicker";
import { NOTE_MAX } from "./BookingForm";
import { bookableDoctors } from "./bookableDoctors";

const S = {
  edit: { en: "Edit appointment", ar: "تعديل الموعد" },
  title: { en: "Edit appointment", ar: "تعديل الموعد" },
  body: {
    en: "Changing the time releases the old slot and holds the new one. Every change is recorded on the appointment's timeline.",
    ar: "تغيير الوقت يحرّر الموعد القديم ويحجز الجديد. تُسجَّل كل التغييرات في مسار الموعد.",
  },
  current: { en: "Currently", ar: "حالياً" },
  notes: { en: "Appointment notes", ar: "ملاحظات الموعد" },
  notesHelp: {
    en: "Access needs, an interpreter, arrangements. Not for clinical details.",
    ar: "احتياجات الوصول، مترجم، ترتيبات. ليست لتفاصيل طبية.",
  },
  notesTooLong: { en: "Notes must be 500 characters or fewer.", ar: "يجب ألا تتجاوز الملاحظات 500 حرف." },
  newTime: { en: "Move to a different time (optional)", ar: "النقل إلى وقت آخر (اختياري)" },
  doctor: { en: "Doctor", ar: "الطبيب" },
  doctorHelp: {
    en: "Changing the doctor shows that doctor's free times — pick one to move the appointment.",
    ar: "تغيير الطبيب يعرض أوقاته المتاحة — اختر وقتاً لنقل الموعد.",
  },
  doctorAny: { en: "Any doctor at this clinic", ar: "أي طبيب في هذه العيادة" },
  doctorNoSlots: {
    en: "That doctor has no free time in this month. Try another month, or another doctor.",
    ar: "لا يوجد وقت متاح لهذا الطبيب في هذا الشهر. جرّب شهراً آخر أو طبيباً آخر.",
  },
  save: { en: "Save changes", ar: "حفظ التغييرات" },
  keep: { en: "Discard", ar: "تجاهل" },
  nothing: { en: "Nothing has been changed.", ar: "لم يتغير شيء." },
  failed: { en: "The change was refused — nothing was saved. Reload and try again.", ar: "تم رفض التغيير — لم يُحفظ شيء. أعد التحميل وحاول مجدداً." },
  slotTaken: { en: "That time was taken while you were choosing. Pick another.", ar: "تم حجز هذا الوقت أثناء اختيارك. اختر وقتاً آخر." },
  cannotMove: {
    en: "This appointment has no clinic session recorded, so it cannot be moved from here — cancel and rebook instead.",
    ar: "لا توجد جلسة عيادة مسجلة لهذا الموعد، لذا لا يمكن نقله من هنا — ألغِه واحجز من جديد.",
  },
} satisfies Record<string, Localized>;

/**
 * Editing an appointment: its note, and the time it sits at.
 *
 * ============================================================================================================
 * WHY THESE TWO AND NOT A GENERAL-PURPOSE FORM
 * ============================================================================================================
 * Everything else about an appointment is either identity (whose it is — changing that is a different
 * appointment, not an edit) or state (checked in, no-show, cancelled — those are transitions with their own
 * rules and their own buttons). What is genuinely amendable is the arrangement: when it happens, and the note
 * describing how to receive the person. So that is what this offers.
 *
 * ============================================================================================================
 * BOTH CHANGES LAND ON THE TIMELINE
 * ============================================================================================================
 * The history trigger snapshots the row on every update, and `AppointmentTimeline` emits `Rescheduled` and
 * `NoteEdited` steps for exactly these two. That is the reason editing is allowed at all rather than forcing
 * a cancel-and-rebook: an appointment that quietly moved with no record is one nobody can explain to the
 * patient who turns up at the old time.
 *
 * The time is OPTIONAL. Most edits are a note correction taken down mid-call, and forcing a slot choice to
 * fix a typo would make the common case the expensive one.
 */
export function EditAppointmentButton({
  row, t, onSaved,
}: {
  row: AppointmentRow;
  t: (l: Localized) => string;
  onSaved: () => void;
}) {
  const api = useApi();

  const [open, setOpen] = useState(false);
  const [note, setNote] = useState(row.note ?? "");
  const [slotId, setSlotId] = useState<string | null>(null);
  const [month, setMonth] = useState(() => monthKey(new Date(row.scheduledStart)));
  const [slots, setSlots] = useState<BookableSlot[]>([]);
  const [days, setDays] = useState<AppointmentDay[]>([]);
  // The practitioner the times are being shown FOR. Starts as the appointment's own; "" means the whole
  // clinic, which is how a desk finds the earliest time regardless of who holds it.
  const [doctorId, setDoctorId] = useState<string>(row.doctorId ?? "");
  const [practitioners, setPractitioners] = useState<Practitioner[]>([]);
  const [availability, setAvailability] = useState<DoctorAvailability[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  // An appointment whose clinic session is unknown cannot have its other times looked up. Rather than
  // silently hiding the time picker, the dialog says so and leaves the note editable.
  const movable = Boolean(row.providerId && row.locationId);

  // Only rows that are still going to happen can be edited. The server refuses the transitions anyway, and a
  // button that can only fail teaches the operator the screen is unreliable.
  const editable = row.checkInEligible || row.checkedIn;

  const load = useCallback(() => {
    if (!open || !movable) return;
    const [y, m] = month.split("-").map(Number);
    const from = new Date(Date.UTC(y, m - 1, 1, 12)).toISOString();
    const to = new Date(Date.UTC(y, m, 0, 12)).toISOString();
    let live = true;
    void Promise.all([
      // Filtered by the CHOSEN doctor, not by the appointment's original one — that filter was what made the
      // doctor unchangeable: the picker could only ever offer slots the current doctor already held.
      api.openSlots(row.providerId!, row.locationId!, from, to, doctorId || undefined)
        .catch(() => [] as BookableSlot[]),
      api.appointmentDays(row.providerId!, row.locationId!, from, to, doctorId || undefined)
        .catch(() => [] as AppointmentDay[]),
    ]).then(([sl, dd]) => {
      if (!live) return;
      setSlots(sl);
      setDays(dd);
    });
    return () => { live = false; };
  }, [api, open, movable, month, row.providerId, row.locationId, doctorId]);

  // Who else could hold this appointment. Read once the dialog opens, and degraded to an empty list rather
  // than blocking the edit: a note correction must not depend on the practitioner directory being reachable.
  useEffect(() => {
    if (!open || !movable) return;
    let live = true;
    void Promise.all([
      api.practitioners({ branchId: row.branchId ?? undefined, type: "Doctor" }).catch(() => [] as Practitioner[]),
      api.doctorAvailability(row.branchId ?? undefined).catch(() => [] as DoctorAvailability[]),
    ]).then(([ps, av]) => {
      if (!live) return;
      setPractitioners(ps);
      setAvailability(av);
    });
    return () => { live = false; };
  }, [api, open, movable, row.branchId]);

  const doctors = useMemo(() => bookableDoctors(practitioners, availability), [practitioners, availability]);

  useEffect(() => load(), [load]);

  if (!editable) return null;

  const noteChanged = note.trim() !== (row.note ?? "").trim();
  const tooLong = note.trim().length > NOTE_MAX;

  async function save() {
    if (tooLong) return;
    if (!noteChanged && !slotId) { setError(S.nothing); return; }
    setError(null);
    setBusy(true);
    try {
      // The note first: it is the change that cannot fail on a race, so doing it first means a lost slot does
      // not also lose a correction the operator has already typed.
      if (noteChanged) await api.updateAppointmentNote(row.id, note.trim());
      if (slotId) await api.rescheduleAppointment(row.id, slotId, row.rowVersion);
      setOpen(false);
      setSlotId(null);
      onSaved();
    } catch (e) {
      // A 409 here means somebody took the slot between load and submit — a different message, because the
      // remedy is "pick another", not "try again".
      const status = (e as { status?: number } | null)?.status;
      setError(status === 409 ? S.slotTaken : S.failed);
      load();
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
        // "Edit appointment" buttons is unusable with a screen reader.
        aria-label={`${t(S.edit)} — ${row.beneficiaryName ?? row.beneficiary.token}`}
        title={t(S.edit)}
        leadingIcon={<Icon name="pen" />}
        onClick={() => { setNote(row.note ?? ""); setSlotId(null); setError(null); setOpen(true); }}
      />
      <Modal
        open={open}
        onOpenChange={setOpen}
        // WIDE. The body holds a month calendar beside a column of times, and at the default width the two
        // were squeezed into a strip barely wider than the calendar itself — the times column ended up
        // narrower than the timestamps it lists, and the whole dialog scrolled vertically to show a layout
        // that is meant to be read side by side.
        wide
        title={t(S.title)}
        description={t(S.body)}
        footer={
          <>
            <Button variant="ghost" onClick={() => setOpen(false)}>{t(S.keep)}</Button>
            <Button leadingIcon={<Icon name="check2" />} variant="primary" loading={busy} onClick={() => void save()}>{t(S.save)}</Button>
          </>
        }
      >
        <div className="edit-appt">
          <p className="edit-appt-who">
            <strong>{row.beneficiaryName ?? row.beneficiary.token}</strong>
          </p>

          <TextareaField
            label={t(S.notes)}
            help={t(S.notesHelp)}
            value={note}
            maxLength={NOTE_MAX}
            error={tooLong ? t(S.notesTooLong) : undefined}
            onChange={(e) => setNote(e.currentTarget.value)}
          />

          {movable ? (
            <section aria-label={t(S.newTime)}>
              <h4 className="section-h">{t(S.newTime)}</h4>
              {/*
                Changing the doctor is a RESCHEDULE, not a separate save.

                There is no "assign this doctor" call and there should not be: an appointment's practitioner
                is whoever holds the slot it sits in, so moving it to another doctor means moving it into one
                of THEIR slots. Presenting it as a filter over the times makes that true by construction —
                the desk cannot produce an appointment whose named doctor and whose session disagree, which
                is exactly the row the server used to be able to write.
              */}
              <ComboboxField
                className="book-field"
                id={`ea-doc-${row.id}`}
                label={t(S.doctor)}
                help={t(S.doctorHelp)}
                value={doctorId}
                onChange={(next) => { setDoctorId(next); setSlotId(null); }}
                options={[
                  { value: "", label: t(S.doctorAny) },
                  ...doctors.map((d) => ({ value: d.id, label: t(d.name) })),
                ]}
              />
              {doctorId && slots.length === 0 && (
                <InlineAlert tone="info">{t(S.doctorNoSlots)}</InlineAlert>
              )}
              <BookingTimePicker
                days={days}
                slots={slots}
                selectedSlotId={slotId}
                onSelectSlot={setSlotId}
                month={month}
                onMonthChange={(next) => { setMonth(next); setSlotId(null); }}
              />
            </section>
          ) : (
            <InlineAlert tone="info">{t(S.cannotMove)}</InlineAlert>
          )}

          {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
        </div>
      </Modal>
    </>
  );
}

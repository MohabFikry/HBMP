import { useCallback, useEffect, useState } from "react";
import { Button, Icon, InlineAlert, Modal, TextareaField } from "@mersal/design-system";
import type { AppointmentDay, AppointmentRow, BookableSlot, Localized } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { BookingTimePicker, monthKey } from "./BookingTimePicker";
import { NOTE_MAX } from "./BookingForm";

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
      api.openSlots(row.providerId!, row.locationId!, from, to, row.doctorId ?? undefined)
        .catch(() => [] as BookableSlot[]),
      api.appointmentDays(row.providerId!, row.locationId!, from, to, row.doctorId ?? undefined)
        .catch(() => [] as AppointmentDay[]),
    ]).then(([sl, dd]) => {
      if (!live) return;
      setSlots(sl);
      setDays(dd);
    });
    return () => { live = false; };
  }, [api, open, movable, month, row.providerId, row.locationId, row.doctorId]);

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
        leadingIcon={<Icon name="doc" />}
        onClick={() => { setNote(row.note ?? ""); setSlotId(null); setError(null); setOpen(true); }}
      />
      <Modal
        open={open}
        onOpenChange={setOpen}
        title={t(S.title)}
        description={t(S.body)}
        footer={
          <>
            <Button variant="secondary" onClick={() => setOpen(false)}>{t(S.keep)}</Button>
            <Button variant="primary" loading={busy} onClick={() => void save()}>{t(S.save)}</Button>
          </>
        }
      >
        <div className="stack-3">
          <p style={{ margin: 0 }}>
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

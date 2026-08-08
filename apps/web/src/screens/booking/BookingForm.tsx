import { useEffect, useMemo, useRef, useState } from "react";
import { Combobox, InlineAlert, TextareaField } from "@mersal/design-system";
import type {
  AppointmentDay, BookableSlot, BranchSummary, DoctorAvailability, Localized, Practitioner, Specialty,
} from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useLoc } from "../_shared";
import { BookingTimePicker, monthKey } from "./BookingTimePicker";
import { availableSpecialties, bookableDoctors, type BookableDoctor } from "./bookableDoctors";

const S = {
  branch: { en: "Branch", ar: "الفرع" },
  pickBranch: { en: "Choose a branch", ar: "اختر الفرع" },
  specialty: { en: "Specialty", ar: "التخصص" },
  pickSpecialty: { en: "Choose a specialty", ar: "اختر التخصص" },
  pickBranchFirst: { en: "Choose a branch first", ar: "اختر الفرع أولاً" },
  doctor: { en: "Doctor", ar: "الطبيب" },
  pickDoctor: { en: "Choose a doctor", ar: "اختر الطبيب" },
  pickSpecialtyFirst: { en: "Choose a specialty first", ar: "اختر التخصص أولاً" },
  notes: { en: "Appointment notes", ar: "ملاحظات الموعد" },
  notesHelp: {
    en: "Access needs, an interpreter, arrangements. Shared with the clinic and the doctor. Not for clinical details.",
    ar: "احتياجات الوصول، مترجم، ترتيبات. تُشارك مع العيادة والطبيب. ليست لتفاصيل طبية.",
  },
  notesTooLong: { en: "Notes must be 500 characters or fewer.", ar: "يجب ألا تتجاوز الملاحظات 500 حرف." },
  noSpecialties: {
    en: "No specialty has bookable times at this branch. Ask the clinic to publish availability.",
    ar: "لا يوجد تخصص بأوقات متاحة في هذا الفرع. اطلب من العيادة نشر أوقاتها.",
  },
  slotsOpen: { en: "open", ar: "متاح" },
} satisfies Record<string, Localized>;

/** Mirrors emr's `AppointmentNote.MaxLength`. The server REFUSES a longer note rather than truncating, so the
 *  form must stop the operator before they lose the tail of a sentence they believed they had written. */
export const NOTE_MAX = 500;

export interface BookingSelection {
  branchId: string | null;
  doctorId: string | null;
  slotId: string | null;
  note: string;
  /** The clinic behind the chosen doctor's slots — the server needs provider+location to book. */
  providerId: string | null;
  locationId: string | null;
}

export interface BookingFormProps {
  /**
   * How the branch is decided. `fixed` = the caller's own active branch, which reception cannot change here
   * (the server refuses a request naming another); `choose` = the call centre naming the branch it is
   * booking into.
   */
  branchMode: "fixed" | "choose";
  /** Selectable branches, for `choose`. Ignored in `fixed` mode. */
  branches?: BranchSummary[];
  onChange: (selection: BookingSelection) => void;
  disabled?: boolean;
  /**
   * Bump to re-read availability WITHOUT resetting the form.
   *
   * The case this exists for is a 409: someone took the slot between load and submit. The times are stale
   * and must be re-read, but the operator's specialty and doctor are still exactly what they wanted —
   * remounting the form to refresh would throw those away and make them choose the same doctor again, which
   * is a punishment for someone else's booking landing first.
   */
  reloadToken?: number;
}

/**
 * Specialty → doctor → time → notes, shared by Reception and the Call Centre.
 *
 * ============================================================================================================
 * WHAT IS SHARED AND WHAT DELIBERATELY IS NOT
 * ============================================================================================================
 * Both portals book with the same flow, and the only difference is the branch — reception books into its own,
 * the call centre names one. That difference is a PROP here rather than two components, because everything
 * after it is identical and two copies of a four-step dependency chain is two places for the invalidation
 * rules to drift.
 *
 * What is NOT shared is the call centre's verification gate. Every reserve path in callcentre-service demands
 * an interaction with a recorded verification PASS, and it refuses the write otherwise. That gate wraps this
 * form on the call-centre side; it is not a step inside it. "Same flow" means the same fields, not the same
 * authority.
 *
 * ============================================================================================================
 * THE INVALIDATION CHAIN IS THE INTERESTING PART
 * ============================================================================================================
 * Branch decides which specialties exist; specialty decides which doctors; doctor decides which times. So
 * changing any link must drop everything below it IN THE SAME UPDATE — otherwise a render exists where the
 * operator is looking at one doctor's times under another doctor's name, and books it.
 */
export function BookingForm({
  branchMode, branches = [], onChange, disabled = false, reloadToken = 0,
}: BookingFormProps) {
  const api = useApi();
  const t = useLoc();

  const [branchId, setBranchId] = useState<string | null>(null);
  const [specialtyCode, setSpecialtyCode] = useState<string | null>(null);
  const [doctorId, setDoctorId] = useState<string | null>(null);
  const [slotId, setSlotId] = useState<string | null>(null);
  const [note, setNote] = useState("");

  const [specialtyRef, setSpecialtyRef] = useState<Specialty[]>([]);
  const [practitioners, setPractitioners] = useState<Practitioner[]>([]);
  const [availability, setAvailability] = useState<DoctorAvailability[]>([]);
  const [slots, setSlots] = useState<BookableSlot[]>([]);
  const [days, setDays] = useState<AppointmentDay[]>([]);
  const [slotsBusy, setSlotsBusy] = useState(false);
  // The month the calendar is showing. Availability is fetched FOR THIS MONTH — a calendar that navigates
  // without re-fetching draws every day of the new month as empty and says there is nothing there.
  const [month, setMonth] = useState(() => monthKey(new Date()));

  // The specialty reference set is org data and does not change with the branch — loaded once.
  useEffect(() => {
    let live = true;
    void api.specialties().then((s) => live && setSpecialtyRef(s)).catch(() => live && setSpecialtyRef([]));
    return () => { live = false; };
  }, [api]);

  // Two reads, joined: provider-service says who the clinicians ARE, emr says who has open time. Neither
  // service may answer for the other (see `bookableDoctors`), so the join happens here.
  useEffect(() => {
    let live = true;
    const branchArg = branchMode === "choose" ? branchId ?? undefined : undefined;
    if (branchMode === "choose" && !branchId) {
      setPractitioners([]); setAvailability([]);
      return;
    }
    void Promise.all([
      api.practitioners({ branchId: branchArg, type: "Doctor" }).catch(() => [] as Practitioner[]),
      api.doctorAvailability(branchArg).catch(() => [] as DoctorAvailability[]),
    ]).then(([p, a]) => {
      if (!live) return;
      setPractitioners(p);
      setAvailability(a);
    });
    return () => { live = false; };
  }, [api, branchMode, branchId]);

  const doctors = useMemo(() => bookableDoctors(practitioners, availability), [practitioners, availability]);
  const specialties = useMemo(() => availableSpecialties(doctors, specialtyRef), [doctors, specialtyRef]);
  const doctorsInSpecialty = useMemo(
    () => doctors.filter((d) => d.specialtyCode === specialtyCode),
    [doctors, specialtyCode],
  );
  const chosenDoctor: BookableDoctor | null = doctorsInSpecialty.find((d) => d.id === doctorId) ?? null;

  /**
   * The clinic behind the chosen doctor. Taken from the clinics the caller may book into rather than stated
   * separately: the desk chose a person, and a second control for "which clinic" is one more thing that can
   * disagree with the first.
   */
  const [clinic, setClinic] = useState<{ providerId: string; locationId: string } | null>(null);
  useEffect(() => {
    let live = true;
    if (!chosenDoctor) { setClinic(null); return; }
    void api.bookableClinics(branchMode === "choose" ? branchId ?? undefined : undefined)
      .then((cs) => { if (live) setClinic(cs[0] ? { providerId: cs[0].providerId, locationId: cs[0].locationId } : null); })
      .catch(() => { if (live) setClinic(null); });
    return () => { live = false; };
  }, [api, chosenDoctor, branchMode, branchId]);

  // Every slot request carries a generation: the response for the PREVIOUS doctor can still be in flight
  // when the operator switches, and letting it land repopulates the times with someone else's calendar.
  const gen = useRef(0);
  useEffect(() => {
    const mine = ++gen.current;
    setSlots([]); setDays([]); setSlotId(null);
    if (!chosenDoctor || !clinic) return;
    setSlotsBusy(true);
    // The whole visible month, anchored at noon UTC so the first and last days cannot slip a day under
    // Cairo's offset. The server still hides past slots, so a month already begun simply returns fewer.
    const [y, m] = month.split("-").map(Number);
    const from = new Date(Date.UTC(y, m - 1, 1, 12)).toISOString();
    const to = new Date(Date.UTC(y, m, 0, 12)).toISOString();
    void Promise.all([
      api.openSlots(clinic.providerId, clinic.locationId, from, to, chosenDoctor.id).catch(() => [] as BookableSlot[]),
      api.appointmentDays(clinic.providerId, clinic.locationId, from, to, chosenDoctor.id).catch(() => [] as AppointmentDay[]),
    ]).then(([sl, dd]) => {
      if (gen.current !== mine) return;
      setSlots(sl); setDays(dd);
    }).finally(() => { if (gen.current === mine) setSlotsBusy(false); });
  }, [api, chosenDoctor, clinic, reloadToken, month]);

  // One place the selection leaves this component, so a caller can never see a half-updated chain.
  useEffect(() => {
    onChange({
      branchId, doctorId, slotId, note: note.trim(),
      providerId: clinic?.providerId ?? null,
      locationId: clinic?.locationId ?? null,
    });
    // `onChange` is intentionally absent: callers pass an inline closure, and depending on it would fire
    // this effect on every parent render, which is an update loop rather than a notification.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [branchId, doctorId, slotId, note, clinic]);

  const noteTooLong = note.trim().length > NOTE_MAX;

  return (
    <div className="stack">
      {branchMode === "choose" && (
        <div className="book-field">
          <span className="mrs-label" id="bk-branch">{t(S.branch)}</span>
          <Combobox
            aria-labelledby="bk-branch"
            options={branches.map((b) => ({ value: b.id, label: t(b.name), hint: b.city }))}
            value={branchId}
            placeholder={t(S.pickBranch)}
            disabled={disabled}
            onChange={(v) => {
              // Everything below the branch is invalidated in ONE update. Leaving the doctor set would show
              // a Dokki clinician under a Nasr City heading until the next fetch landed.
              setBranchId(v); setSpecialtyCode(null); setDoctorId(null); setSlotId(null);
            }}
          />
        </div>
      )}

      <div className="book-grid">
        <div className="book-field">
          <span className="mrs-label" id="bk-specialty">{t(S.specialty)}</span>
          {/* Combobox, not Select: specialties are a long closed list and first-letter typeahead makes an
              operator looking for "Ophthalmology" walk the whole O range. */}
          <Combobox
            aria-labelledby="bk-specialty"
            options={specialties.map((s) => ({ value: s.code, label: t(s.name), hint: s.code, keywords: s.code }))}
            value={specialtyCode}
            placeholder={
              branchMode === "choose" && !branchId ? t(S.pickBranchFirst) : t(S.pickSpecialty)
            }
            disabled={disabled || specialties.length === 0}
            onChange={(v) => { setSpecialtyCode(v); setDoctorId(null); setSlotId(null); }}
          />
        </div>

        <div className="book-field">
          <span className="mrs-label" id="bk-doctor">{t(S.doctor)}</span>
          <Combobox
            aria-labelledby="bk-doctor"
            options={doctorsInSpecialty.map((d) => ({
              value: d.id,
              label: t(d.name),
              // The count is the useful hint at a desk: "who can I actually get this patient in to see?"
              hint: `${d.openSlots} ${t(S.slotsOpen)}`,
            }))}
            value={doctorId}
            placeholder={specialtyCode ? t(S.pickDoctor) : t(S.pickSpecialtyFirst)}
            disabled={disabled || !specialtyCode}
            onChange={(v) => { setDoctorId(v); setSlotId(null); }}
          />
        </div>
      </div>

      {/* Only once a branch is settled: before that, "no specialty has times here" is not yet true. */}
      {(branchMode === "fixed" || branchId) && specialtyRef.length > 0 && specialties.length === 0 && (
        <InlineAlert tone="warn">{t(S.noSpecialties)}</InlineAlert>
      )}

      {/* ALWAYS rendered — never behind a step. See the note in BookingTimePicker. */}
      <BookingTimePicker
        days={days}
        slots={slots}
        selectedSlotId={slotId}
        onSelectSlot={setSlotId}
        busy={slotsBusy}
        month={month}
        onMonthChange={(next) => {
          // The time chosen in the old month is dropped in the SAME update as the month change: keeping it
          // would leave a booking pointing at a slot the calendar is no longer showing.
          setMonth(next);
          setSlotId(null);
        }}
      />

      <TextareaField
        label={t(S.notes)}
        help={t(S.notesHelp)}
        value={note}
        maxLength={NOTE_MAX}
        error={noteTooLong ? t(S.notesTooLong) : undefined}
        disabled={disabled}
        onChange={(e) => setNote(e.currentTarget.value)}
      />
    </div>
  );
}

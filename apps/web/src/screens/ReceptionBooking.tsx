import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Button, Card, Icon, InlineAlert, InputField, StatusChip } from "@mersal/design-system";
import type { EligibilityHit, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useWrite } from "../api/useWrite";
import { useFormat } from "../i18n/useFormat";
import { PageHeader, useLoc, readErrorMessage } from "./_shared";
import { BookingForm, NOTE_MAX, type BookingSelection } from "./booking/BookingForm";

const S = {
  title: { en: "Book an Appointment", ar: "حجز موعد" },
  // The branch is NOT a field: it is whatever the app bar says, and the server refuses anything else.
  branchNote: {
    en: "The appointment is booked in your active branch — switch branches in the header to book elsewhere.",
    ar: "يُحجز الموعد في فرعك النشط — بدّل الفرع من الأعلى للحجز في مكان آخر.",
  },
  step1: { en: "1. Patient", ar: "١. المريض" },
  step2: { en: "2. Appointment", ar: "٢. الموعد" },
  searchLabel: { en: "Search by name or card number", ar: "ابحث بالاسم أو رقم البطاقة" },
  search: { en: "Search", ar: "بحث" },
  noMatches: { en: "No matching beneficiary.", ar: "لا يوجد مستفيد مطابق." },
  change: { en: "Change", ar: "تغيير" },
  choose: { en: "Choose", ar: "اختيار" },
  book: { en: "Book appointment", ar: "احجز الموعد" },
  booked: { en: "Appointment booked", ar: "تم حجز الموعد" },
  bookedAt: { en: "Booked for", ar: "محجوز في" },
  bookAnother: { en: "Book another", ar: "حجز موعد آخر" },
  needPatient: { en: "Choose a patient first.", ar: "اختر المريض أولاً." },
  needDoctor: { en: "Choose a specialty and doctor.", ar: "اختر التخصص والطبيب." },
  needSlot: { en: "Choose a time.", ar: "اختر الوقت." },
  noteTooLong: { en: "Shorten the appointment notes before booking.", ar: "اختصر ملاحظات الموعد قبل الحجز." },
  notBookable: {
    en: "Not active — cannot be booked. Refer to the Case Manager.",
    ar: "غير نشط — لا يمكن الحجز. راجع مدير الحالة.",
  },
} satisfies Record<string, Localized>;

/**
 * Reception booking (US-020, 14.5).
 *
 * The desk books into ITS OWN branch: there is deliberately no branch field — the server resolves the branch
 * from the caller's active branch and refuses a request naming another, so a picker here could only ever
 * offer a choice the server would reject. Switching branches is a header action, which keeps one visible
 * answer to "where am I working?" instead of two that can disagree.
 *
 * Everything after the patient — specialty, doctor, time, notes — is `BookingForm`, shared verbatim with the
 * call centre. The only difference between the two portals is where the branch comes from, which is why that
 * is the component's single prop rather than the reason for a second component.
 */
export function ReceptionBooking() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const write = useWrite();

  // `?q=` — the patient the caller arrived WITH. The profile's "Book appointment" sends the member number,
  // because otherwise that action lands on an empty form and asks the operator to look up the person whose
  // file they were just reading.
  const [params] = useSearchParams();
  const initialQuery = params.get("q") ?? "";

  // Step 1 — patient
  const [query, setQuery] = useState(initialQuery);
  const [hits, setHits] = useState<EligibilityHit[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<Localized | null>(null);
  const [patient, setPatient] = useState<EligibilityHit | null>(null);

  // Step 2 — the shared form reports its whole selection at once, so this screen never sees a half-updated
  // specialty/doctor/time chain.
  const [sel, setSel] = useState<BookingSelection>({
    branchId: null, doctorId: null, slotId: null, note: "", providerId: null, locationId: null,
  });
  const [formKey, setFormKey] = useState(0);      // remount: a full reset, after a completed booking
  const [reloadToken, setReloadToken] = useState(0);   // re-read times, keeping the operator's choices

  const [confirmed, setConfirmed] = useState<{ at: string } | null>(null);
  const [attempted, setAttempted] = useState(false);

  // Run the arrival search once, on mount. Not on every `query` change — that would fire a request per
  // keystroke for anyone typing in the box.
  useEffect(() => {
    if (initialQuery.trim()) void runSearch(initialQuery.trim());
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  async function runSearch(term: string) {
    setSearching(true);
    setSearchError(null);
    try {
      setHits(await api.searchEligibility(term));
    } catch (err) {
      // 401/403 read differently from "nothing found" — say which one happened.
      setSearchError(readErrorMessage(err));
      setHits(null);
    } finally {
      setSearching(false);
    }
  }

  async function doSearch(e: React.FormEvent) {
    e.preventDefault();
    if (!query.trim()) return;
    await runSearch(query.trim());
  }

  const missing = !patient
    ? S.needPatient
    : !sel.doctorId
      ? S.needDoctor
      : !sel.slotId
        ? S.needSlot
        : sel.note.length > NOTE_MAX
          ? S.noteTooLong
          : null;

  async function doBook() {
    setAttempted(true);
    if (missing || !patient || !sel.slotId || !sel.providerId || !sel.locationId) return;
    const ok = await write.run(() =>
      api.bookAppointment({
        beneficiaryId: patient.id,
        providerId: sel.providerId!,
        locationId: sel.locationId!,
        slotId: sel.slotId!,
        appointmentType: "Scheduled",
        doctorId: sel.doctorId ?? undefined,
        note: sel.note || undefined,
        // The desk already has the name on screen — sending it is what lets every board row show WHO the
        // appointment is for instead of a masked token. emr stores it as a snapshot; it never looks it up.
        beneficiaryName: t(patient.name),
      }),
    );
    if (ok) {
      setConfirmed({ at: new Date().toISOString() });
    } else {
      // A 409 means someone took the slot between load and submit. Re-read the times, but KEEP the specialty
      // and doctor: they are still what the operator wanted, and making them pick the same doctor again is a
      // punishment for someone else's booking landing first.
      setReloadToken((k) => k + 1);
    }
  }

  function reset() {
    setConfirmed(null);
    setAttempted(false);
    setPatient(null);
    setHits(null);
    setQuery("");
    setFormKey((k) => k + 1);
  }

  if (confirmed) {
    return (
      <>
        <PageHeader title={t(S.title)} />
        <Card as="section" style={{ padding: "var(--sp5)" }}>
          <div role="status" className="stack-3">
            <StatusChip kind="ok" label={t(S.booked)} />
            <p>
              {t(S.bookedAt)} <strong className="tnum">{fmt.dateTime(confirmed.at)}</strong>
            </p>
            <Button variant="primary"
              leadingIcon={<Icon name="plus" />} onClick={reset}>
              {t(S.bookAnother)}
            </Button>
          </div>
        </Card>
      </>
    );
  }

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <p className="muted">{t(S.branchNote)}</p>

        {/* ── 1. Patient ─────────────────────────────────────────────── */}
        <h2 className="section-h">{t(S.step1)}</h2>
        {patient ? (
          <div className="book-chosen">
            <span>
              <strong>{t(patient.name)}</strong> <span className="tnum muted">{patient.cardNumber}</span>
            </span>
            <Button variant="secondary" size="sm" onClick={() => setPatient(null)}>
              {t(S.change)}
            </Button>
          </div>
        ) : (
          <>
            <form onSubmit={doSearch} noValidate className="book-search">
              <InputField
                label={t(S.searchLabel)}
                value={query}
                onChange={(e) => setQuery(e.currentTarget.value)}
              />
              <Button type="submit" variant="secondary" loading={searching}>
                {t(S.search)}
              </Button>
            </form>
            {searchError && <InlineAlert tone="bad">{t(searchError)}</InlineAlert>}
            {hits && hits.length === 0 && <p role="status">{t(S.noMatches)}</p>}
            {hits && hits.length > 0 && (
              <ul className="book-hits">
                {hits.map((h) => (
                  <li key={h.id}>
                    <span>
                      {t(h.name)} <span className="tnum muted">{h.cardNumber}</span>
                      {h.status && <> <StatusChip kind={h.status.kind} label={t(h.status.label)} /></>}
                    </span>
                    {/* Stopped HERE, at the moment the person is found, rather than at submit. A desk that
                        picks a suspended member, chooses a doctor and a time, and is then refused has spent
                        the patient's turn at the counter on a booking that could never have completed — and
                        the server does refuse it (422 urn:hbmp:member-not-active), so the only question is
                        how early they are told. */}
                    {h.bookable === false ? (
                      <span className="row-actions">
                        <span className="muted">{t(S.notBookable)}</span>
                      </span>
                    ) : (
                      <Button variant="secondary" size="sm" onClick={() => setPatient(h)}>
                        {t(S.choose)}
                      </Button>
                    )}
                  </li>
                ))}
              </ul>
            )}
          </>
        )}

        {/* ── 2. Appointment ─────────────────────────────────────────── */}
        <h2 className="section-h">{t(S.step2)}</h2>
        <BookingForm key={formKey} branchMode="fixed" onChange={setSel} reloadToken={reloadToken} />

        {/* Only after a submit attempt: telling the desk what is missing before they have tried is noise. */}
        {attempted && missing && <InlineAlert tone="warn">{t(missing)}</InlineAlert>}
        {write.error && <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert>}

        <div className="book-actions">
          <Button variant="primary"
              leadingIcon={<Icon name="calendar" />} loading={write.busy} onClick={() => void doBook()}>
            {t(S.book)}
          </Button>
        </div>
      </Card>
    </>
  );
}

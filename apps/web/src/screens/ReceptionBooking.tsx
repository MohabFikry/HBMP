import { useEffect, useState } from "react";
import { useSearchParams } from "react-router-dom";
import { Button, Card, Icon, InlineAlert, InputField, Modal, StatusChip } from "@mersal/design-system";
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
  /** The results dialog. Opened only when the search is AMBIGUOUS — see `runSearch`. */
  matchesTitle: { en: "Choose a patient", ar: "اختر المريض" },
  matchesSub: {
    en: "More than one beneficiary matches. Select the right one to continue.",
    ar: "أكثر من مستفيد مطابق. اختر الصحيح للمتابعة.",
  },
  matchesCount: { en: "matches", ar: "نتائج" },
  /**
   * 33.9 — the page was cut, and the person they want may not be on it.
   *
   * The search returns 25 rows and does not say how many matched, so a term matching forty people gave
   * twenty-five with nothing to distinguish that from a complete answer. The instruction is the point:
   * "narrow it" is the only thing an operator can do, and an identifier is what narrows it to one.
   */
  tooMany: {
    en: "More than 25 beneficiaries match. The person you are looking for may not be in this list — add a "
      + "card or ID number to narrow it.",
    ar: "أكثر من ٢٥ مستفيداً مطابقاً. قد لا يكون الشخص المطلوب ضمن هذه القائمة — أضف رقم البطاقة أو الهوية "
      + "لتضييق البحث.",
  },
  matchesTruncated: { en: "More than 25 match", ar: "أكثر من ٢٥ نتيجة" },
  cancelPick: { en: "Cancel", ar: "إلغاء" },
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
 * One search result. The WHOLE ROW is the control.
 *
 * <p>It was a row of text with a "Choose" button pinned to its end, so the target was a 70px button at the
 * far edge while the thing being chosen — the name — sat at the other. A `<button>` wrapping the row makes
 * the target the row, which is what people aim at anyway, and it costs nothing in accessibility: it is still
 * one control with one accessible name, still reachable by Tab, still activated by Enter and Space.</p>
 *
 * <p><b>A row that cannot be chosen is not a button.</b> Not a disabled one either — a disabled control is
 * still announced as a control, and the desk would keep aiming at it. It renders as plain text with the
 * reason beside it, which is the answer to the question they were about to ask.</p>
 */
function HitRow({
  hit, t, onChoose,
}: {
  hit: EligibilityHit;
  t: (l: Localized) => string;
  onChoose: (hit: EligibilityHit) => void;
}) {
  const identity = (
    <span className="book-hit-id">
      <strong>{t(hit.name)}</strong> <span className="tnum muted">{hit.cardNumber}</span>
      {hit.status && <StatusChip kind={hit.status.kind} label={t(hit.status.label)} />}
    </span>
  );

  // Stopped HERE, at the moment the person is found, rather than at submit. A desk that picks a suspended
  // member, chooses a doctor and a time, and is then refused has spent the patient's turn at the counter on a
  // booking that could never have completed — and the server does refuse it (422 urn:hbmp:member-not-active),
  // so the only question is how early they are told.
  if (hit.bookable === false) {
    return (
      <li className="book-hit book-hit--blocked">
        {identity}
        <span className="muted">{t(S.notBookable)}</span>
      </li>
    );
  }

  return (
    <li className="book-hit">
      <button type="button" className="book-hit-pick" onClick={() => onChoose(hit)}>
        {identity}
        <span className="book-hit-go" aria-hidden="true"><Icon name="chevron" width={16} height={16} /></span>
      </button>
    </li>
  );
}

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
  /** The page was cut. See `runSearch` — the operator has to be told, because the person they want may not
   *  be on the list they are about to pick from. */
  const [truncated, setTruncated] = useState(false);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<Localized | null>(null);
  const [patient, setPatient] = useState<EligibilityHit | null>(null);
  const [picking, setPicking] = useState(false);

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
      const found = await api.searchEligibility(term);
      setHits(found.hits);
      // 33.9 — the page is 25 rows and the search does not say how many matched. A term matching forty
      // people used to give twenty-five with nothing to say the list had been cut, so the operator picked
      // from a truncated set presented as the complete one — and the patient they wanted could be among the
      // fifteen that were never sent.
      setTruncated(found.truncated);
      // Ambiguity is what the dialog is FOR. One match answers the question on the spot and stays inline;
      // several is a decision, and a decision made against a list wedged between the search box and the next
      // step of the form is one made in the wrong place.
      setPicking(found.hits.length > 1);
    } catch (err) {
      // 401/403 read differently from "nothing found" — say which one happened.
      setSearchError(readErrorMessage(err));
      setHits(null);
      setTruncated(false);
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
    setTruncated(false);
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
      <Card as="section" className="book-card" style={{ padding: "var(--sp5)" }}>
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
              <Button leadingIcon={<Icon name="search" />} type="submit" variant="secondary" loading={searching}>
                {t(S.search)}
              </Button>
            </form>
            {searchError && <InlineAlert tone="bad">{t(searchError)}</InlineAlert>}
            {hits && hits.length === 0 && <p role="status">{t(S.noMatches)}</p>}
            {/* Said BEFORE the list, not after it: an operator who has already found a plausible name will
                not read a footnote telling them there were others. */}
            {truncated && <InlineAlert tone="warn" data-testid="book-truncated">{t(S.tooMany)}</InlineAlert>}
            {/* Exactly one match stays inline: it is an answer, not a decision, and a dialog to confirm the
                only possible choice is a click that buys nothing. Several open the picker below. */}
            {hits && hits.length === 1 && (
              <ul className="book-hits">
                <HitRow hit={hits[0]} t={t} onChoose={setPatient} />
              </ul>
            )}
            {hits && hits.length > 1 && (
              <Modal
                open={picking}
                onOpenChange={setPicking}
                title={t(S.matchesTitle)}
                description={truncated
                  ? `${t(S.matchesTruncated)} — ${t(S.matchesSub)}`
                  : `${hits.length} ${t(S.matchesCount)} — ${t(S.matchesSub)}`}
                footer={<Button variant="secondary" onClick={() => setPicking(false)}>{t(S.cancelPick)}</Button>}
              >
                <ul className="book-hits book-hits--picker">
                  {hits.map((h) => (
                    <HitRow
                      key={h.id}
                      hit={h}
                      t={t}
                      onChoose={(picked) => { setPatient(picked); setPicking(false); }}
                    />
                  ))}
                </ul>
              </Modal>
            )}
            {/* Reopening costs one click rather than re-running the search — the results are still here. */}
            {hits && hits.length > 1 && !picking && (
              <Button variant="secondary" size="sm" onClick={() => setPicking(true)}>
                {/* "(25)" on a capped page is a count of what was SENT, and it reads as a count of what
                    matched. "(25+)" is the only honest label available without a second query. */}
                {`${t(S.matchesTitle)} (${hits.length}${truncated ? "+" : ""})`}
              </Button>
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

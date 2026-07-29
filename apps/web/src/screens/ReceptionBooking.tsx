import { useCallback, useEffect, useRef, useState } from "react";
import { Button, Card, InputField, Select, StatusChip, InlineAlert } from "@mersal/design-system";
import type { SelectOption } from "@mersal/design-system";
import type { BookableSlot, EligibilityHit, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useWrite } from "../api/useWrite";
import { useFormat } from "../i18n/useFormat";
import { PageHeader, useLoc, readErrorMessage } from "./_shared";

const S = {
  title: { en: "Book an appointment", ar: "حجز موعد" },
  // The branch is NOT a field: it is whatever the app bar says, and the server refuses anything else.
  branchNote: {
    en: "The appointment is booked in your active branch — switch branches in the header to book elsewhere.",
    ar: "يُحجز الموعد في فرعك النشط — بدّل الفرع من الأعلى للحجز في مكان آخر.",
  },
  step1: { en: "1. Patient", ar: "١. المريض" },
  step2: { en: "2. Clinic", ar: "٢. العيادة" },
  step3: { en: "3. Time", ar: "٣. الوقت" },
  searchLabel: { en: "Search by name or card number", ar: "ابحث بالاسم أو رقم البطاقة" },
  search: { en: "Search", ar: "بحث" },
  searching: { en: "Searching…", ar: "جاري البحث…" },
  noMatches: { en: "No matching beneficiary.", ar: "لا يوجد مستفيد مطابق." },
  chosen: { en: "Selected", ar: "المحدد" },
  change: { en: "Change", ar: "تغيير" },
  choose: { en: "Choose", ar: "اختيار" },
  provider: { en: "Provider", ar: "مقدم الخدمة" },
  location: { en: "Location", ar: "الموقع" },
  apptType: { en: "Appointment type", ar: "نوع الموعد" },
  pickProvider: { en: "Choose a provider", ar: "اختر مقدم الخدمة" },
  pickLocation: { en: "Choose a location", ar: "اختر الموقع" },
  slotsLoading: { en: "Loading available times…", ar: "جاري تحميل الأوقات المتاحة…" },
  noClinics: {
    en: "No clinics are available to you for booking. Your role cannot read the provider directory — ask an administrator to expose your branch's clinics.",
    ar: "لا توجد عيادات متاحة لك للحجز. لا يستطيع دورك قراءة دليل مقدمي الخدمة — اطلب من المسؤول إتاحة عيادات فرعك.",
  },
  noSlots: {
    en: "No open times for this clinic. Try another location or a later date.",
    ar: "لا توجد أوقات متاحة لهذه العيادة. جرّب موقعًا آخر أو تاريخًا لاحقًا.",
  },
  slotTaken: { en: "Taken", ar: "محجوز" },
  book: { en: "Book appointment", ar: "احجز الموعد" },
  booked: { en: "Appointment booked", ar: "تم حجز الموعد" },
  bookedAt: { en: "Booked for", ar: "محجوز في" },
  bookAnother: { en: "Book another", ar: "حجز موعد آخر" },
  needPatient: { en: "Choose a patient first.", ar: "اختر المريض أولاً." },
  needClinic: { en: "Choose a provider and location.", ar: "اختر مقدم الخدمة والموقع." },
  needSlot: { en: "Choose a time.", ar: "اختر الوقت." },
} satisfies Record<string, Localized>;

/** The emr AppointmentType enum values the desk may book. Referral/FollowUp need a linkage the desk has not
 *  got in hand, and the server rejects them without it — so they are not offered here. */
const TYPES = ["Scheduled", "Consultation", "Procedure", "WalkIn"] as const;
const TYPE_LABELS: Record<string, Localized> = {
  Scheduled: { en: "Scheduled", ar: "مجدول" },
  Consultation: { en: "Consultation", ar: "كشف" },
  Procedure: { en: "Procedure", ar: "إجراء" },
  WalkIn: { en: "Walk-in", ar: "بدون موعد" },
};

/**
 * Reception booking (US-020). The desk books into ITS OWN branch: there is deliberately no branch field —
 * the server resolves the branch from the caller's active branch and refuses a request naming another, so a
 * picker here could only ever offer a choice the server would reject. Switching branches is a header action,
 * which keeps one visible answer to "where am I working?" instead of two that can disagree.
 *
 * Availability is never derived locally. `openSlots` returns the server's own `open` flag because it holds
 * the no-double-book invariant and can see slots held by bookings this desk may not read; a slot taken
 * between load and submit comes back 409, which is surfaced rather than swallowed.
 */
export function ReceptionBooking() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const write = useWrite();

  // Step 1 — patient
  const [query, setQuery] = useState("");
  const [hits, setHits] = useState<EligibilityHit[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [searchError, setSearchError] = useState<Localized | null>(null);
  const [patient, setPatient] = useState<EligibilityHit | null>(null);

  // Step 2 — clinic
  const [providers, setProviders] = useState<SelectOption[]>([]);
  const [providersFailed, setProvidersFailed] = useState(false);
  const [providerId, setProviderId] = useState<string | null>(null);
  const [locations, setLocations] = useState<SelectOption[]>([]);
  const [locationId, setLocationId] = useState<string | null>(null);
  const [apptType, setApptType] = useState<string>("Scheduled");

  // Step 3 — time
  const [slots, setSlots] = useState<BookableSlot[] | null>(null);
  const [slotsBusy, setSlotsBusy] = useState(false);
  const [slotId, setSlotId] = useState<string | null>(null);

  const [confirmed, setConfirmed] = useState<{ at: string } | null>(null);
  const [attempted, setAttempted] = useState(false);

  useEffect(() => {
    let live = true;
    void api
      .providerList()
      .then((ps) => {
        if (!live) return;
        setProviders(ps.map((p) => ({ value: p.id, label: p.legalName, hint: p.code })));
        // An EMPTY list is as unbookable as a failed one, and an empty dropdown reads as "still loading".
        setProvidersFailed(ps.length === 0);
      })
      .catch(() => {
        if (!live) return;
        setProviders([]);
        // Reception is correctly refused the provider directory (403 provider:read). Saying so beats an empty
        // dropdown the operator will keep clicking.
        setProvidersFailed(true);
      });
    return () => {
      live = false;
    };
  }, [api]);

  /**
   * Locations belong to a provider, so a new provider invalidates the location AND the slots loaded for the
   * old pair — keeping them would let the desk book a time at the clinic they just navigated away from.
   *
   * The clearing happens HERE, in the same batch as the provider change, and deliberately not in an effect:
   * an effect that cleared the location would run on a render where providerId was already the NEW one while
   * locationId was still the OLD one, and the slot-loading effect — running in the same pass — would fetch
   * for that mismatched pair. The times came back for a clinic nobody had selected.
   */
  function pickProvider(id: string) {
    setProviderId(id);
    setLocationId(null);
    setLocations([]);
    slotGen.current++;   // abandon any slot request still in flight for the previous clinic
    setSlots(null);
    setSlotId(null);
    setSlotsBusy(false);
  }

  useEffect(() => {
    if (!providerId) return;
    let live = true;
    void api
      .providerLocations(providerId)
      .then((ls) => live && setLocations(ls.map((l) => ({ value: l.id, label: l.name, hint: l.governorate }))))
      .catch(() => live && setLocations([]));
    return () => {
      live = false;
    };
  }, [api, providerId]);

  // Every slot request carries a generation. Without it the response for the PREVIOUS clinic resolves after
  // the switch has already cleared the list and repopulates it — the desk then picks a time that belongs to
  // the clinic they navigated away from, which is exactly the wrong-clinic booking the clearing prevents.
  const slotGen = useRef(0);
  const loadSlots = useCallback(() => {
    if (!providerId || !locationId) return;
    const gen = ++slotGen.current;
    setSlotsBusy(true);
    setSlotId(null);
    void api
      .openSlots(providerId, locationId)
      .then((r) => { if (gen === slotGen.current) setSlots(r); })
      .catch(() => { if (gen === slotGen.current) setSlots([]); })
      .finally(() => { if (gen === slotGen.current) setSlotsBusy(false); });
  }, [api, providerId, locationId]);

  useEffect(() => {
    if (providerId && locationId) loadSlots();
  }, [providerId, locationId, loadSlots]);

  async function doSearch(e: React.FormEvent) {
    e.preventDefault();
    if (!query.trim()) return;
    setSearching(true);
    setSearchError(null);
    try {
      setHits(await api.searchEligibility(query.trim()));
    } catch (err) {
      // 401/403 read a differently from "nothing found" — say which one happened.
      setSearchError(readErrorMessage(err));
      setHits(null);
    } finally {
      setSearching(false);
    }
  }

  const missing = !patient ? S.needPatient : !providerId || !locationId ? S.needClinic : !slotId ? S.needSlot : null;

  async function doBook() {
    setAttempted(true);
    if (missing || !patient || !providerId || !locationId || !slotId) return;
    const ok = await write.run(() =>
      api.bookAppointment({
        beneficiaryId: patient.id,
        providerId,
        locationId,
        slotId,
        appointmentType: apptType,
      }),
    );
    if (ok) {
      const at = slots?.find((s) => s.id === slotId)?.start ?? new Date().toISOString();
      setConfirmed({ at });
    } else {
      // A 409 means someone took the slot between load and submit — re-read rather than leaving a dead
      // choice selected and inviting a second press.
      loadSlots();
    }
  }

  function reset() {
    setConfirmed(null);
    setAttempted(false);
    setPatient(null);
    setHits(null);
    setQuery("");
    setSlotId(null);
    loadSlots();
  }

  if (confirmed) {
    return (
      <>
        <PageHeader title={t(S.title)} />
        <Card as="section" style={{ padding: "var(--sp4)" }}>
          <div role="status" className="stack-3">
            <StatusChip kind="ok" label={t(S.booked)} />
            <p>
              {t(S.bookedAt)} <strong className="tnum">{fmt.dateTime(confirmed.at)}</strong>
            </p>
            <Button variant="primary" onClick={reset}>
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
      <Card as="section" style={{ padding: "var(--sp4)" }}>
        <p className="muted">{t(S.branchNote)}</p>

        {/* ── 1. Patient ─────────────────────────────────────────────── */}
        <h3 className="section-h">{t(S.step1)}</h3>
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
                    </span>
                    <Button variant="secondary" size="sm" onClick={() => setPatient(h)}>
                      {t(S.choose)}
                    </Button>
                  </li>
                ))}
              </ul>
            )}
          </>
        )}

        {/* ── 2. Clinic ──────────────────────────────────────────────── */}
        <h3 className="section-h">{t(S.step2)}</h3>
        {providersFailed && <InlineAlert tone="warn">{t(S.noClinics)}</InlineAlert>}
        <div className="book-grid">
          <label className="book-field">
            <span className="mrs-label">{t(S.provider)}</span>
            <Select
              aria-label={t(S.provider)}
              options={providers}
              value={providerId}
              placeholder={t(S.pickProvider)}
              onChange={pickProvider}
            />
          </label>
          <label className="book-field">
            <span className="mrs-label">{t(S.location)}</span>
            <Select
              aria-label={t(S.location)}
              options={locations}
              value={locationId}
              placeholder={t(S.pickLocation)}
              disabled={!providerId || locations.length === 0}
              onChange={setLocationId}
            />
          </label>
          <label className="book-field">
            <span className="mrs-label">{t(S.apptType)}</span>
            <Select
              aria-label={t(S.apptType)}
              options={TYPES.map((v) => ({ value: v, label: t(TYPE_LABELS[v]) }))}
              value={apptType}
              onChange={setApptType}
            />
          </label>
        </div>

        {/* ── 3. Time ────────────────────────────────────────────────── */}
        <h3 className="section-h">{t(S.step3)}</h3>
        {slotsBusy && <p role="status">{t(S.slotsLoading)}</p>}
        {!slotsBusy && slots && slots.length === 0 && <p role="status">{t(S.noSlots)}</p>}
        {!slotsBusy && slots && slots.length > 0 && (
          // A radiogroup's children must be the radios themselves: wrapping them in <li> inside a <ul
          // role="radiogroup"> strips the list role and orphans every item (axe: listitem).
          <div className="book-slots" role="radiogroup" aria-label={t(S.step3)}>
            {slots.map((s) => (
              <button
                key={s.id}
                type="button"
                role="radio"
                aria-checked={slotId === s.id}
                // The server's own answer, not a time comparison done here.
                disabled={!s.open}
                className="book-slot"
                onClick={() => setSlotId(s.id)}
              >
                <span className="tnum">{fmt.time(s.start)}</span>
                {!s.open && <span className="muted"> · {t(S.slotTaken)}</span>}
              </button>
            ))}
          </div>
        )}

        {/* Only after a submit attempt: telling the desk what is missing before they have tried is noise. */}
        {attempted && missing && <InlineAlert tone="warn">{t(missing)}</InlineAlert>}
        {write.error && <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert>}

        <div className="book-actions">
          <Button variant="primary" loading={write.busy} onClick={() => void doBook()}>
            {t(S.book)}
          </Button>
        </div>
      </Card>
    </>
  );
}

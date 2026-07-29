import { useCallback, useState } from "react";
import { Button, Card, Icon, InputField, Select, StatusChip, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";
import { PageHeader } from "./_shared";
import { identifierTypeLabel, memberStatus } from "./statusLabels";
import { ReservationPicker, useReservation } from "./ReservationPicker";
import { CallNotes } from "./CallNotes";
import { createHttpCcApi, type CcApi, type CcMatch } from "./CallCentre";

/**
 * Phase 20.4 — the standalone "Book appointment" journey for the call centre.
 *
 * <b>Why it is its own screen and not a link into the workspace.</b> Booking is the single thing the call
 * centre does most, and the workspace makes it the fifth step of a general-purpose call: pick a reason, start
 * the call, search, verify, then find the reservation panel inside the member file. As its own nav item the
 * journey is what the agent actually has in front of them — a caller who wants an appointment — and nothing
 * else renders.
 *
 * <b>What it does NOT do is skip verification.</b> A booking still has to hang off a verified call: every
 * reserve path in callcentre-service requires an interaction with a recorded verification PASS for that
 * beneficiary, and it refuses the write otherwise. So this screen OPENS the interaction itself (reason
 * `BookAppointment`) the moment a member is chosen, and verifies inside itself. The alternative — booking
 * straight through emr, which the call centre could do because it holds `appointment:reserve` — would have
 * produced an appointment with no call behind it and no verification recorded, which is exactly the audit
 * trail the phase-15 gate exists to guarantee. The separate screen is a different route to the same rule, not
 * an exemption from it.
 *
 * Nothing about the member is displayed before the PASS (verify-before-disclose), and no clinical field exists
 * anywhere in this graph.
 */

/** The identifiers reception/eligibility indexes, i.e. the ones a search can actually hit. */
const SEARCH_BY: { key: string; example: { en: string; ar: string }; numeric: boolean }[] = [
  { key: "Phone", example: { en: "01001234567", ar: "01001234567" }, numeric: true },
  { key: "MemberNo", example: { en: "MRS-M-2026-000005", ar: "MRS-M-2026-000005" }, numeric: false },
  { key: "NationalId", example: { en: "29801011234567", ar: "29801011234567" }, numeric: true },
  { key: "Passport", example: { en: "A01234567", ar: "A01234567" }, numeric: false },
  { key: "RefugeeId", example: { en: "REF-2026-0001", ar: "REF-2026-0001" }, numeric: false },
  { key: "UnhcrNo", example: { en: "760-C01234567", ar: "760-C01234567" }, numeric: false },
  { key: "FullName", example: { en: "Hana Mansour", ar: "هناء منصور" }, numeric: false },
];

/** ONE client for the module — see the note in CallCentre.tsx; a per-render client makes every effort keyed
 *  on `api` re-run and discard the previous request's result. */
const defaultCcApi = createHttpCcApi();

export function CallCentreBooking({ api = defaultCcApi }: { api?: CcApi }) {
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang];

  const [searchBy, setSearchBy] = useState("");
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<CcMatch[] | null>(null);
  const [selected, setSelected] = useState<CcMatch | null>(null);
  const [interactionId, setInteractionId] = useState<string | null>(null);
  const [ticks, setTicks] = useState<Set<string>>(new Set());
  const [verifiedFor, setVerifiedFor] = useState<string | null>(null);
  const [verifyError, setVerifyError] = useState(false);
  const [verifiedName, setVerifiedName] = useState<string | null>(null);
  const [verifiedStatus, setVerifiedStatus] = useState<string | null>(null);
  const [announce, setAnnounce] = useState("");
  const [notes, setNotes] = useState("");
  const [closed, setClosed] = useState(false);

  const isVerified = !!selected && verifiedFor === selected.beneficiaryId;
  const r = useReservation(api, isVerified);

  const chosen = SEARCH_BY.find((s) => s.key === searchBy) ?? null;

  const doSearch = useCallback(async () => {
    if (!query.trim()) return;
    setResults(await api.search(query.trim()));
    setSelected(null); setVerifiedFor(null); setVerifiedName(null);
  }, [api, query]);

  /**
   * Choosing a member opens the call record. It happens here rather than behind a "start call" button because
   * on this screen the call is already in progress — the agent is on the phone, that is why they are booking.
   * A failure has to be surfaced: without an interaction id every later write is refused, and a screen that
   * silently carried on would present a working booking form that cannot save.
   */
  const select = useCallback(async (m: CcMatch) => {
    setSelected(m); setTicks(new Set()); setVerifiedFor(null); setVerifiedName(null); setVerifyError(false);
    if (interactionId) return;
    const opened = await api.openInteraction("BookAppointment").catch(() => null);
    if (!opened?.interactionId) { setSelected(null); setAnnounce(t(L.ccBookOpenFailed)); return; }
    setInteractionId(opened.interactionId);
  }, [api, interactionId, t]);

  const toggle = (type: string) =>
    setTicks((prev) => {
      const next = new Set(prev);
      if (next.has(type)) next.delete(type); else next.add(type);
      return next;
    });

  const verify = useCallback(async (pass: boolean) => {
    if (!interactionId || !selected) return;
    if (pass && ticks.size < 2) { setVerifyError(true); return; }
    setVerifyError(false);
    const ok = await api.verify(interactionId, selected.beneficiaryId, [...ticks], pass);
    if (!ok) { setAnnounce(t(L.ccFailed)); return; }
    // The name and status shown from here on come from the SUMMARY, i.e. from the server after it has
    // re-checked the verification — not from the pre-verification search hit, which is a lookup and not a
    // disclosure. If the summary is refused, nothing about the member is shown.
    const s = await api.summary(selected.beneficiaryId, interactionId).catch(() => null);
    setVerifiedFor(selected.beneficiaryId);
    setVerifiedName(s?.identity.displayName ?? null);
    setVerifiedStatus(s?.identity.status ?? null);
    setAnnounce(t(L.ccVerified));
  }, [api, interactionId, selected, ticks, t]);

  const book = useCallback(async () => {
    if (!interactionId || !verifiedFor || !r.slotId || !r.chosenClinic) return;
    // The branch travels with the CLINIC, never from a second control that could disagree with it — two
    // pickers that can drift is how someone is told to come to Maadi for a Dokki appointment.
    const outcome = await api.book(interactionId, verifiedFor, r.slotId, r.chosenClinic.branchId ?? null);
    setAnnounce(outcome === "ok" ? t(L.ccBooked) : outcome === "conflict" ? t(L.ccSlotTaken) : t(L.ccBookFailed));
    // Both a success and a 409 invalidate the list: one consumed the slot, the other proves someone else did.
    if (outcome !== "error") r.refresh();
  }, [api, interactionId, verifiedFor, r, t]);

  const finish = useCallback(async () => {
    if (!interactionId) return;
    await api.close(interactionId, "Resolved", notes);
    setInteractionId(null); setSelected(null); setResults(null); setVerifiedFor(null);
    setVerifiedName(null); setVerifiedStatus(null); setQuery(""); setNotes(""); setClosed(true);
    setAnnounce(t(L.ccBookClosed));
  }, [api, interactionId, notes, t]);

  const copyNotes = useCallback(async () => {
    // Guard the METHOD, not just the object: a non-secure context exposes `navigator.clipboard` as undefined,
    // and jsdom exposes neither, so an unguarded call is an unhandled rejection in the agent's face.
    try {
      await navigator.clipboard?.writeText?.(notes);
      setAnnounce(t(L.ccCopied));
    } catch {
      setAnnounce(t(L.ccCopyFailed));
    }
  }, [notes, t]);

  return (
    <div className="cc-workspace">
      <PageHeader title={t(L.ccBookTitle)} />
      <p className="cc-muted">{t(L.ccBookIntro)}</p>

      <div aria-live="polite" role="status" data-testid="cc-live" className="cc-live">{announce}</div>

      {/* 1. FIND — identifier-led, because a caller states an identifier, not a name. */}
      <Card>
        <h2 className="cc-step">{t(L.ccStepFind)}</h2>
        <div className="cc-search">
          <div className="cc-field">
            <span id="cc-searchby-label">{t(L.ccSearchBy)}</span>
            <Select
              aria-labelledby="cc-searchby-label"
              value={searchBy || null}
              placeholder={t(L.ccSearchByAny)}
              options={SEARCH_BY.map((s) => ({ value: s.key, label: t(identifierTypeLabel(s.key)) }))}
              onChange={setSearchBy}
            />
          </div>
          <InputField
            label={chosen ? t(identifierTypeLabel(chosen.key)) : t(L.ccSearchLabel)}
            // Honest help text: the server matches the term against EVERY indexed identifier, so this picker
            // sets the keypad and the example — it does not narrow the match. Claiming otherwise would make a
            // hit on a different identifier look like a bug.
            help={chosen ? `${t(L.ccSearchByHint)} ${t(chosen.example)}` : t(L.ccSearchByHint)}
            inputMode={chosen?.numeric ? "numeric" : undefined}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyDown={(e) => { if (e.key === "Enter") void doSearch(); }}
          />
          <Button variant="secondary" onClick={doSearch}>{t(L.ccSearch)}</Button>
        </div>
        {results && results.length === 0 && <p role="status">{t(L.ccNoResults)}</p>}
        {results && results.length > 0 && (
          <ul className="cc-results">
            {results.map((m) => (
              <li key={m.beneficiaryId}>
                <button
                  type="button"
                  className="cc-result"
                  onClick={() => void select(m)}
                  aria-pressed={selected?.beneficiaryId === m.beneficiaryId}
                >
                  <span>{m.displayName}</span>
                  {m.memberNo && <span className="cc-muted">{m.memberNo}</span>}
                </button>
              </li>
            ))}
          </ul>
        )}
      </Card>

      {/* 2. VERIFY — no member detail here, only which identifiers to challenge on. */}
      {selected && !isVerified && (
        <Card>
          <h2 className="cc-step">{t(L.ccStepVerify)}</h2>
          <div className="cc-locked" role="region" aria-label={t(L.ccNotVerified)}>
            <span className="cc-lockchip" data-testid="cc-lockchip">
              <Icon name="info" /> <span>{t(L.ccNotVerified)}</span>
            </span>
            <p>{t(L.ccNotVerifiedBody)}</p>
            <fieldset>
              <legend>{t(L.ccChallengeOn)}</legend>
              {selected.challengeableIdentifierTypes.map((type) => (
                <label key={type} className="cc-check">
                  <input type="checkbox" checked={ticks.has(type)} onChange={() => toggle(type)} />{" "}
                  {t(identifierTypeLabel(type))}
                </label>
              ))}
            </fieldset>
            {verifyError && <p role="alert" className="cc-error">{t(L.ccNeedTwo)}</p>}
            <div className="cc-verify-actions">
              <Button variant="primary" onClick={() => verify(true)}>{t(L.ccPass)}</Button>
              <Button variant="ghost" onClick={() => verify(false)}>{t(L.ccFail)}</Button>
            </div>
          </div>
        </Card>
      )}

      {/* 3. CHOOSE — branch, clinic, time. Unlocked only by a PASS. */}
      {isVerified && (
        <Card>
          <h2 className="cc-step">{t(L.ccStepChoose)}</h2>
          <div data-testid="cc-booking-for" className="cc-booking-for">
            <span>{verifiedName ?? selected.displayName}</span>
            {selected.memberNo && <span className="cc-muted">· {selected.memberNo}</span>}
            {verifiedStatus && (
              <StatusChip
                kind={memberStatus(verifiedStatus).kind}
                label={t(memberStatus(verifiedStatus).label)}
              />
            )}
            <a
              className="profile-action-link"
              href={`/patients/${encodeURIComponent(selected.beneficiaryId)}?interactionId=${encodeURIComponent(interactionId ?? "")}`}
            >
              {t(L.ccOpenProfile)}
            </a>
          </div>
          <ReservationPicker r={r} onBook={book} bookLabel={t(L.ccBook)} />
        </Card>
      )}

      {/* WRAP UP — the notes land on the call record this booking was made under. */}
      {interactionId && (
        <Card>
          <div className="cc-wrapup">
            <CallNotes value={notes} onChange={setNotes} onCopy={copyNotes} />
            <div className="cc-verify-actions">
              <Button variant="secondary" onClick={finish}>{t(L.ccBookFinish)}</Button>
            </div>
          </div>
        </Card>
      )}

      {closed && !interactionId && (
        <p role="status" className="cc-muted">{t(L.ccBookClosed)}</p>
      )}
    </div>
  );
}

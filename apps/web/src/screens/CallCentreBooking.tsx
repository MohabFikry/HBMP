import { useCallback, useEffect, useState } from "react";
import { Button, Card, Icon, InlineAlert, StatusChip, useTheme } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { L } from "../i18n/strings";
import { PageHeader, useOpenProfile } from "./_shared";
import { memberStatus, callReasonLabel } from "./statusLabels";
import { BookingForm, NOTE_MAX, type BookingSelection } from "./booking/BookingForm";
import { CallSummaryDraft } from "./CallNotes";
import { MemberSearch } from "./CallCentreSearch";
import { useRestorableState } from "./useRestorableState";
import { CALL_REASONS, createHttpCcApi, withReason, type CcApi, type CcDirection, type CcMatch } from "./CallCentre";
import { useApi } from "../api/ApiProvider";
import type { BranchSummary } from "@mersal/contracts";

/**
 * The standalone "Book appointment" journey for the call centre.
 *
 * <b>Why it is its own screen and not a link into the workspace.</b> Booking is the single thing the call
 * centre does most, and the workspace makes it a step of a general-purpose call: pick a reason, start the call,
 * search, then find the reservation panel inside the member file. As its own nav item the journey is what the
 * agent actually has in front of them — a caller who wants an appointment — and nothing else renders.
 *
 * <b>It is reception's booking screen, with the call centre's steps.</b> Same single padded card, same
 * `1. … / 2. …` section headings, same search → choose → `BookingForm` → Book shape, and the same rule that
 * the appointment step is ALWAYS on screen rather than appearing once a person has been picked. It used to be
 * hidden behind the chosen member, so an agent who had not yet found the caller — or whose file failed to
 * open — was looking at a booking screen with no booking on it. What is missing is now said at the moment
 * they try to book, exactly as reception says it.
 *
 * The third step is the one reception does not have: this booking hangs off a CALL, and the call has a
 * reason, a direction and a summary that other roles read.
 *
 * <b>The booking still hangs off a call.</b> Every reserve path in callcentre-service requires an interaction
 * bound to that beneficiary and refuses the write otherwise. So this screen OPENS the interaction itself the
 * moment a member is chosen, and records the agent's identity attestation with it. The alternative — booking
 * straight through emr, which the call centre could do because it holds `appointment:reserve` — would produce
 * an appointment with no call behind it, which is exactly the audit trail this arrangement exists to guarantee.
 *
 * <b>The VERIFY step is gone.</b> Identity is confirmed by the agent on the phone; the search is a plain search
 * and choosing a hit opens the member's file. No clinical field exists anywhere in this graph.
 */

/** ONE client for the module — see the note in CallCentre.tsx; a per-render client makes every effort keyed
 *  on `api` re-run and discard the previous request's result. */
const defaultCcApi = createHttpCcApi();

export function CallCentreBooking({ api = defaultCcApi }: { api?: CcApi }) {
  const { lang } = useTheme();
  const t = (l: Localized) => l[lang];
  const openProfile = useOpenProfile();

  /** Restored so that opening the caller's profile mid-booking does not lose the call, the search or the
   *  member — see useRestorableState. Only the shape of the work; the member's details are re-fetched. */
  const [query, setQuery] = useRestorableState("cc-booking.query", "");
  const [interactionId, setInteractionId] = useRestorableState<string | null>("cc-booking.call", null);
  const [openedFor, setOpenedFor] = useRestorableState<CcMatch | null>("cc-booking.member", null);
  /** The one account of this call, read by other roles on the member's profile; required at close. */
  const [wrapSummary, setWrapSummary] = useRestorableState("cc-booking.summary", "");
  /** Why the call happened. Defaults to the reason this screen exists for, and is editable — an agent who
   *  came here to book and ended up answering an eligibility question should be able to say so. */
  const [reason, setReason] = useRestorableState("cc-booking.reason", "BookAppointment");
  /** Who rang whom. INBOUND by default because most hotline calls are; recorded when the interaction opens
   *  and not correctable after, so the control below locks once the call is under way. */
  const [direction, setDirection] = useRestorableState<CcDirection>("cc-booking.direction", "Inbound");

  const [results, setResults] = useState<CcMatch[] | null>(null);
  const [searching, setSearching] = useState(false);
  const [memberName, setMemberName] = useState<string | null>(null);
  const [memberStatusCode, setMemberStatusCode] = useState<string | null>(null);
  const [memberNo, setMemberNo] = useState<string | null>(null);
  const [announce, setAnnounce] = useState("");
  const [summaryError, setSummaryError] = useState(false);
  const [attempted, setAttempted] = useState(false);
  const [closed, setClosed] = useState(false);

  // 14.5 — the SAME form reception uses, with the branch as a picker rather than the caller's own. The server
  // still refuses every reserve on a call not bound to this beneficiary, regardless of what is on screen.
  const [sel, setSel] = useState<BookingSelection>({
    branchId: null, doctorId: null, slotId: null, note: "", providerId: null, locationId: null,
  });
  const [branches, setBranches] = useState<BranchSummary[]>([]);
  const [reloadToken, setReloadToken] = useState(0);
  const webApi = useApi();

  useEffect(() => {
    let live = true;
    void webApi.branches().then((b) => live && setBranches(b)).catch(() => live && setBranches([]));
    return () => { live = false; };
  }, [webApi]);

  const doSearch = useCallback(async () => {
    if (!query.trim()) return;
    setSearching(true);
    try {
      setResults(await api.search(query.trim()));
    } finally {
      setSearching(false);
    }
  }, [api, query]);

  /**
   * Choosing a member opens the call record AND the member's file, in one gesture.
   *
   * The call is opened here rather than behind a "start call" button because on this screen it is already in
   * progress — the agent is on the phone, that is why they are booking. Both steps have to be surfaced when
   * they fail: without an interaction the booking cannot save, and without the binding every reserve is
   * refused, so a screen that carried on silently would present a working form that cannot write.
   *
   * The name and status shown from here on come from the SERVER'S 360, after it has re-checked the binding —
   * not from the search hit, which is a lookup and not a disclosure.
   */
  const openMember = useCallback(async (m: CcMatch) => {
    let id = interactionId;
    if (!id) {
      const opened = await api.openInteraction(reason, direction).catch(() => null);
      if (!opened?.interactionId) { setAnnounce(t(L.ccBookOpenFailed)); return; }
      id = opened.interactionId;
      setInteractionId(id);
    }
    const attested = await api.openMember(id, m.beneficiaryId).catch(() => false);
    if (!attested) { setAnnounce(t(L.ccOpenFileFailed)); return; }
    const s = await api.summary(m.beneficiaryId, id).catch(() => null);
    if (!s) { setAnnounce(t(L.ccOpenFileFailed)); return; }
    setOpenedFor(m);
    setMemberName(s.identity.displayName);
    setMemberStatusCode(s.identity.status);
    setMemberNo(s.identity.memberNo ?? null);
    setAnnounce(t(L.ccFileOpened));
  }, [api, interactionId, reason, direction, setInteractionId, setOpenedFor, t]);

  /** Re-read the member's header after returning from the profile. The restored state names WHICH member is
   *  open; their details come back from the server, through the same gate as the first time. */
  useEffect(() => {
    if (!interactionId || !openedFor || memberName) return;
    let live = true;
    void api.summary(openedFor.beneficiaryId, interactionId)
      .then((s) => {
        if (!live || !s) return;
        setMemberName(s.identity.displayName);
        setMemberStatusCode(s.identity.status);
        setMemberNo(s.identity.memberNo ?? null);
      })
      .catch(() => { /* leave the file closed; the agent can re-open it from the search */ });
    return () => { live = false; };
  }, [api, interactionId, openedFor, memberName]);

  /**
   * What still stands between the agent and a booking, in the order they would hit it — reception's exact
   * pattern, and reception's exact wording where the step is the same. Reported only after a submit attempt.
   */
  const missing: Localized | null =
    !openedFor ? L.ccNeedMember
    : !sel.doctorId ? L.ccNeedDoctor
    : !sel.slotId ? L.ccNeedSlot
    : sel.note.length > NOTE_MAX ? L.ccNoteTooLong
    : null;

  const book = useCallback(async () => {
    setAttempted(true);
    if (!interactionId || !openedFor || !sel.slotId) return;
    const outcome = await api.book(interactionId, openedFor.beneficiaryId, sel.slotId, sel.branchId, {
      doctorId: sel.doctorId,
      note: sel.note || undefined,
    });
    setAnnounce(
      outcome.kind === "ok" ? t(L.ccBooked)
      : outcome.kind === "conflict" ? t(L.ccSlotTaken)
      : withReason(t(L.ccBookFailed), outcome),
    );
    // Both a success and a 409 invalidate the times: one consumed the slot, the other proves someone else
    // did. Re-read them WITHOUT resetting the agent's branch/specialty/doctor — they are still what the
    // caller asked for, and making the agent re-enter them mid-call is a cost paid for someone else's race.
    if (outcome.kind !== "error") setReloadToken((k) => k + 1);
  }, [api, interactionId, openedFor, sel, t]);

  /**
   * Wrap up this booking call. The close is CHECKED — it used to be awaited and discarded, and because the
   * server requires a summary for any outcome but Abandoned, it had been failing 422 on every booking while
   * this screen reset itself as though the call were done. The interaction stayed Open, and with it the
   * binding to that member.
   */
  const finish = useCallback(async () => {
    if (!interactionId) return;
    const result = await api.close(interactionId, "Resolved", wrapSummary.trim(), reason);
    if (result.kind !== "ok") {
      setSummaryError(result.kind === "summary-required");
      setAnnounce(
        result.kind === "summary-required" ? t(L.ccSummaryRequired)
        : result.kind === "not-your-call" ? t(L.ccNotYourCall)
        : withReason(t(L.ccCloseFailed), result),
      );
      return;   // the call is still open — leave the screen showing it
    }
    setSummaryError(false);
    setInteractionId(null); setOpenedFor(null); setResults(null); setAttempted(false);
    setMemberName(null); setMemberStatusCode(null); setMemberNo(null);
    setQuery(""); setWrapSummary(""); setReason("BookAppointment"); setDirection("Inbound");
    setClosed(true);
    setAnnounce(t(L.ccBookClosed));
  }, [
    api, interactionId, wrapSummary, reason,
    setInteractionId, setOpenedFor, setQuery, setWrapSummary, setReason, setDirection, t,
  ]);

  const copySummary = useCallback(async () => {
    // Guard the METHOD, not just the object: a non-secure context exposes `navigator.clipboard` as undefined,
    // and jsdom exposes neither, so an unguarded call is an unhandled rejection in the agent's face.
    try {
      await navigator.clipboard?.writeText?.(wrapSummary);
      setAnnounce(t(L.ccCopied));
    } catch {
      setAnnounce(t(L.ccCopyFailed));
    }
  }, [wrapSummary, t]);

  return (
    <>
      <PageHeader title={t(L.ccBookTitle)} />

      <div aria-live="polite" role="status" data-testid="cc-live" className="cc-live">{announce}</div>

      {/* ONE padded card for the whole journey, as reception has. Three unpadded cards stacked on top of one
          another was what put the text against the edges and made each step look like a separate screen. */}
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <p className="muted">{t(L.ccBookIntro)}</p>

        {/* ── 1. Member ──────────────────────────────────────────────────────────────────────────────── */}
        <h2 className="section-h">{t(L.ccStepMember)}</h2>
        {openedFor ? (
          <div className="book-chosen" data-testid="cc-booking-for">
            <span>
              <strong>{memberName ?? openedFor.displayName}</strong>{" "}
              {/* From the server's 360, after it re-checked the binding — not from the search hit, which is a
                  lookup rather than a disclosure. */}
              {memberNo && <span className="tnum muted">{memberNo}</span>}
              {memberStatusCode && (
                <> <StatusChip
                  kind={memberStatus(memberStatusCode).kind}
                  label={t(memberStatus(memberStatusCode).label)}
                /></>
              )}
            </span>
            <span className="row-actions">
              {/* A <Link>, not an <a href>: as a plain anchor this reloaded the SPA and destroyed the open
                  call and the booking in progress. `state.from` is what the profile's Back button returns to. */}
              {/* A BUTTON, not a bare link. It routes (an `<a href>` here reloaded the SPA and destroyed the
                  open call), and it now looks like the action it is: 0B §10c — a bare text link beside a
                  button is a hierarchy claim, and this one sat next to one while being the more useful of
                  the two. `useOpenProfile` records the origin so Back returns to the live workspace, and the
                  interactionId rides along because profile-service checks that binding itself (ADR-0026). */}
              <Button
                variant="secondary"
                size="sm"
                leadingIcon={<Icon name="user" />}
                onClick={() => openProfile(
                  openedFor.beneficiaryId,
                  `?interactionId=${encodeURIComponent(interactionId ?? "")}`)}
              >
                {t(L.ccOpenProfile)}
              </Button>
              {/* Changes the member on the SAME call — the agent misidentified the caller, they did not start
                  a different conversation. The call record, its reason and its direction all stand. */}
              <Button variant="secondary" size="sm" onClick={() => { setOpenedFor(null); setMemberName(null); }}>
                {t(L.ccChange)}
              </Button>
            </span>
          </div>
        ) : (
          <MemberSearch
            query={query}
            onQueryChange={setQuery}
            onSearch={() => void doSearch()}
            results={results}
            onSelect={(m) => void openMember(m)}
            busy={searching}
          />
        )}

        {/* ── 2. Appointment ─────────────────────────────────────────────────────────────────────────── */}
        <h2 className="section-h">{t(L.ccStepAppointment)}</h2>
        {/* Kept here rather than inside the shared form: "no arrivals" is a CALL CENTRE truth, not a property
            of the booking fields. The server enforces it with `appointment:reserve` instead of
            `appointment:write`, so the absent check-in and no-show buttons present a boundary that holds
            without them. */}
        <p className="muted">{t(L.ccReserveOnly)}</p>
        <BookingForm
          branchMode="choose"
          branches={branches}
          onChange={setSel}
          reloadToken={reloadToken}
        />

        {/* Only after a submit attempt: telling the agent what is missing before they have tried is noise. */}
        {attempted && missing && <InlineAlert tone="warn">{t(missing)}</InlineAlert>}

        <div className="book-actions">
          <Button variant="primary" onClick={() => void book()}>{t(L.ccBookAction)}</Button>
        </div>

        {/* ── 3. Call record ─────────────────────────────────────────────────────────────────────────── */}
        <h2 className="section-h">{t(L.ccStepCallRecord)}</h2>
        <div className="cc-callmeta">
          <label className="cc-field">
            <span>{t(L.ccReason)}</span>
            <select
              className="mrs-control"
              value={reason}
              onChange={(e) => setReason(e.target.value)}
            >
              {CALL_REASONS.map((r) => <option key={r} value={r}>{t(callReasonLabel(r))}</option>)}
            </select>
          </label>

          <label className="cc-field">
            <span>{t(L.ccDirection)}</span>
            <select
              className="mrs-control"
              value={direction}
              onChange={(e) => setDirection(e.target.value as CcDirection)}
              // Locked once the call exists. Direction is written when the interaction OPENS and there is no
              // endpoint that changes it, so an editable control here would accept a correction and silently
              // drop it — worse than not offering one.
              disabled={interactionId !== null}
            >
              <option value="Inbound">{t(L.ccInbound)}</option>
              <option value="Outbound">{t(L.ccOutbound)}</option>
            </select>
            {interactionId !== null && <span className="cc-hint">{t(L.ccDirectionLocked)}</span>}
          </label>
        </div>

        <CallSummaryDraft value={wrapSummary} onChange={setWrapSummary} onCopy={copySummary} />
        {/* The draft above is the input; this reports the server's refusal against it, with role="alert" so
            it is announced rather than only appearing. */}
        {summaryError && <InlineAlert tone="bad">{t(L.ccSummaryRequired)}</InlineAlert>}

        <div className="book-actions">
          <Button variant="secondary" disabled={!interactionId} onClick={() => void finish()}>
            {t(L.ccBookFinish)}
          </Button>
        </div>

        {closed && !interactionId && (
          <p role="status" className="muted">{t(L.ccBookClosed)}</p>
        )}
      </Card>
    </>
  );
}

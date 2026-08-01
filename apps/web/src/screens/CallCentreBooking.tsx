import { useCallback, useEffect, useState } from "react";
import { Link, useLocation } from "react-router-dom";
import { Button, Card, StatusChip, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";
import { PageHeader } from "./_shared";
import { memberStatus } from "./statusLabels";
import { BookingForm, type BookingSelection } from "./booking/BookingForm";
import { CallSummaryDraft } from "./CallNotes";
import { MemberSearch } from "./CallCentreSearch";
import { useRestorableState } from "./useRestorableState";
import { createHttpCcApi, type CcApi, type CcMatch } from "./CallCentre";
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
 * <b>The booking still hangs off a call.</b> Every reserve path in callcentre-service requires an interaction
 * bound to that beneficiary and refuses the write otherwise. So this screen OPENS the interaction itself
 * (reason `BookAppointment`) the moment a member is chosen, and records the agent's identity attestation with
 * it. The alternative — booking straight through emr, which the call centre could do because it holds
 * `appointment:reserve` — would produce an appointment with no call behind it, which is exactly the audit trail
 * this arrangement exists to guarantee.
 *
 * <b>The VERIFY step is gone.</b> Identity is confirmed by the agent on the phone; the search is a plain search
 * and picking a hit opens the member's file. No clinical field exists anywhere in this graph.
 */

/** ONE client for the module — see the note in CallCentre.tsx; a per-render client makes every effort keyed
 *  on `api` re-run and discard the previous request's result. */
const defaultCcApi = createHttpCcApi();

export function CallCentreBooking({ api = defaultCcApi }: { api?: CcApi }) {
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang];
  const location = useLocation();

  /** Restored so that opening the caller's profile mid-booking does not lose the call, the search or the
   *  member — see useRestorableState. Only the shape of the work; the member's details are re-fetched. */
  const [query, setQuery] = useRestorableState("cc-booking.query", "");
  const [interactionId, setInteractionId] = useRestorableState<string | null>("cc-booking.call", null);
  const [openedFor, setOpenedFor] = useRestorableState<CcMatch | null>("cc-booking.member", null);
  /** The one account of this call, read by other roles on the member's profile; required at close. */
  const [wrapSummary, setWrapSummary] = useRestorableState("cc-booking.summary", "");

  const [results, setResults] = useState<CcMatch[] | null>(null);
  const [memberName, setMemberName] = useState<string | null>(null);
  const [memberStatusCode, setMemberStatusCode] = useState<string | null>(null);
  const [memberNo, setMemberNo] = useState<string | null>(null);
  const [announce, setAnnounce] = useState("");
  const [summaryError, setSummaryError] = useState(false);
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
    setResults(await api.search(query.trim()));
    setOpenedFor(null); setMemberName(null);
  }, [api, query, setOpenedFor]);

  /**
   * Choosing a member opens the call record AND the member's file, in one gesture.
   *
   * The call is opened here rather than behind a "start call" button because on this screen it is already in
   * progress — the agent is on the phone, that is why they are booking. Both steps have to be surfaced when
   * they fail: without an interaction the booking form cannot save, and without the binding every reserve is
   * refused, so a screen that carried on silently would present a working form that cannot write.
   *
   * The name and status shown from here on come from the SERVER'S 360, after it has re-checked the binding —
   * not from the search hit, which is a lookup and not a disclosure.
   */
  const openMember = useCallback(async (m: CcMatch) => {
    let id = interactionId;
    if (!id) {
      const opened = await api.openInteraction("BookAppointment").catch(() => null);
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
  }, [api, interactionId, setInteractionId, setOpenedFor, t]);

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

  const book = useCallback(async () => {
    if (!interactionId || !openedFor || !sel.slotId) return;
    const outcome = await api.book(interactionId, openedFor.beneficiaryId, sel.slotId, sel.branchId, {
      doctorId: sel.doctorId,
      note: sel.note || undefined,
    });
    setAnnounce(outcome === "ok" ? t(L.ccBooked) : outcome === "conflict" ? t(L.ccSlotTaken) : t(L.ccBookFailed));
    // Both a success and a 409 invalidate the times: one consumed the slot, the other proves someone else
    // did. Re-read them WITHOUT resetting the agent's branch/specialty/doctor — they are still what the
    // caller asked for, and making the agent re-enter them mid-call is a cost paid for someone else's race.
    if (outcome !== "error") setReloadToken((k) => k + 1);
  }, [api, interactionId, openedFor, sel, t]);

  /**
   * Wrap up this booking call. The close is CHECKED — it used to be awaited and discarded, and because the
   * server requires a summary for any outcome but Abandoned, it had been failing 422 on every booking while
   * this screen reset itself as though the call were done. The interaction stayed Open, and with it the
   * caller verification recorded against that member.
   */
  const finish = useCallback(async () => {
    if (!interactionId) return;
    const result = await api.close(interactionId, "Resolved", wrapSummary.trim());
    if (result !== "ok") {
      setSummaryError(result === "summary-required");
      setAnnounce(
        result === "summary-required" ? t(L.ccSummaryRequired)
        : result === "not-your-call" ? t(L.ccNotYourCall)
        : t(L.ccCloseFailed),
      );
      return;   // the call is still open — leave the screen showing it
    }
    setSummaryError(false);
    setInteractionId(null); setOpenedFor(null); setResults(null);
    setMemberName(null); setMemberStatusCode(null); setMemberNo(null);
    setQuery(""); setWrapSummary(""); setClosed(true);
    setAnnounce(t(L.ccBookClosed));
  }, [api, interactionId, wrapSummary, setInteractionId, setOpenedFor, setQuery, setWrapSummary, t]);

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
    <div className="cc-workspace">
      <PageHeader title={t(L.ccBookTitle)} />
      <p className="cc-muted">{t(L.ccBookIntro)}</p>

      <div aria-live="polite" role="status" data-testid="cc-live" className="cc-live">{announce}</div>

      {/* 1. FIND — one box. A caller says a name or reads a number off their card; the agent types it. */}
      <Card>
        <h2 className="cc-step">{t(L.ccStepFind)}</h2>
        <p className="cc-muted">{t(L.ccOpenFileHelp)}</p>
        <MemberSearch
          query={query}
          onQueryChange={setQuery}
          onSearch={() => void doSearch()}
          results={results}
          selectedId={openedFor?.beneficiaryId ?? null}
          onSelect={(m) => void openMember(m)}
        />
      </Card>

      {/* 2. CHOOSE — branch, clinic, time. Shown once the member's file is open on this call. */}
      {openedFor && (
        <Card>
          <h2 className="cc-step">{t(L.ccStepChoose)}</h2>
          <div data-testid="cc-booking-for" className="cc-booking-for">
            <span>{memberName ?? openedFor.displayName}</span>
            {/* From the server's 360, after it re-checked the binding — not from the search hit, which is a
                lookup rather than a disclosure. */}
            {memberNo && <span className="cc-muted">· {memberNo}</span>}
            {memberStatusCode && (
              <StatusChip
                kind={memberStatus(memberStatusCode).kind}
                label={t(memberStatus(memberStatusCode).label)}
              />
            )}
            {/* A <Link>, not an <a href>: as a plain anchor this reloaded the SPA and destroyed the open call
                and the booking in progress. `state.from` is what the profile's Back button returns to. */}
            <Link
              className="profile-action-link"
              to={`/patients/${encodeURIComponent(openedFor.beneficiaryId)}?interactionId=${encodeURIComponent(interactionId ?? "")}`}
              state={{ from: `${location.pathname}${location.search}` }}
            >
              {t(L.ccOpenProfile)}
            </Link>
          </div>
          {/* Kept here rather than inside the shared form: "no arrivals" is a CALL CENTRE truth, not a
              property of the booking fields. The server enforces it with `appointment:reserve` instead of
              `appointment:write`, so the absent check-in and no-show buttons are presentation of a boundary
              that holds without them. */}
          <p className="cc-muted">{t(L.ccReserveOnly)}</p>
          <BookingForm
            branchMode="choose"
            branches={branches}
            onChange={setSel}
            reloadToken={reloadToken}
          />
          <div className="book-actions">
            <Button variant="primary" disabled={!sel.slotId} onClick={book}>{t(L.ccBook)}</Button>
          </div>
        </Card>
      )}

      {/* WRAP UP — ONE account of the call, which lands on the member's profile for the roles who read it
          later. There were two fields here: private notes and this summary, and an agent writing carefully
          into the first was writing into a field nobody downstream would open. */}
      {interactionId && (
        <Card>
          <div className="cc-wrapup">
            <CallSummaryDraft value={wrapSummary} onChange={setWrapSummary} onCopy={copySummary} />
            {/* The draft above is the input; this reports the server's refusal against it, with role="alert"
                so it is announced rather than only appearing. */}
            {summaryError && <p role="alert" className="cc-error">{t(L.ccSummaryRequired)}</p>}
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

import { memberStatus, callOutcomeLabel, identifierTypeLabel, appointmentTypeLabel } from "./statusLabels";
import { useFormat } from "../i18n/useFormat";
import { useCallback, useEffect, useState } from "react";
import { Button, Card, Icon, InputField, StatusChip, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";
import { API_BASE } from "../config";
import { getToken } from "../auth/tokenStore";
import { PageHeader } from "./_shared";
import { BookingForm, type BookingSelection } from "./booking/BookingForm";
import { useApi } from "../api/ApiProvider";
import type { BranchSummary } from "@mersal/contracts";
import { CallNotes } from "./CallNotes";

// ── Types (mirror the callcentre-service DTOs; CLINICAL-FREE by construction) ───────────────────────────
export interface CcMatch {
  beneficiaryId: string;
  displayName: string;
  memberNo?: string | null;
  challengeableIdentifierTypes: string[];
}
export interface CcCoverageLine { category: string; annualLimit?: number | null; remainingLimit?: number | null }
export interface CcContact { contactId: string; kind: string; value: string; isPrimary: boolean }
export interface CcAppointment {
  appointmentId: string; appointmentType: string; status: string; scheduledStart: string;
  branchName?: string | null; doctorName?: string | null; specialty?: string | null;
  canReschedule: boolean; canCancel: boolean;
}
export interface CcReferral { referralRef: string; status: string; requestedSpecialty?: string | null }
export interface CcClinic { providerId: string; locationId: string; branchId?: string | null; branchName?: string | null; label: string; openSlots: number }
export interface CcSlot { slotId: string; start: string }
export interface Cc360 {
  identity: { beneficiaryId: string; memberNo?: string | null; displayName: string; ageBand?: string | null; status: string };
  coverage: CcCoverageLine[];
  contacts: CcContact[];
  appointments: CcAppointment[];
  openReferrals: CcReferral[];
}
export interface CcCallRow { callRef: string; startedAt: string; status: string; reasonCode?: string | null; outcome?: string | null }

/** The narrow surface the workspace needs. The default implementation calls the gateway; tests inject a fake. */
export interface CcApi {
  openInteraction(reasonCode: string): Promise<{ interactionId: string; callRef: string }>;
  verify(interactionId: string, beneficiaryId: string, types: string[], pass: boolean): Promise<boolean>;
  search(q: string): Promise<CcMatch[]>;
  summary(beneficiaryId: string, interactionId: string): Promise<Cc360 | null>;
  /** Clinics with bookable times, across every branch the agent can reach. Each option carries its branch, so
   *  choosing a clinic IS choosing a branch — no second picker that could disagree with it. */
  clinics(): Promise<CcClinic[]>;
  slots(providerId: string, locationId: string): Promise<CcSlot[]>;
  /** A REAL slot id and the branch it belongs to. Both used to be invented client-side. */
  /** 14.5 — the agent now picks a DOCTOR and may record a general note; both ride the same verified path. */
  book(
    interactionId: string,
    beneficiaryId: string,
    slotId: string,
    branchId?: string | null,
    extra?: { doctorId?: string | null; note?: string },
  ): Promise<"ok" | "conflict" | "error">;
  reschedule(interactionId: string, appointmentId: string, newSlotId: string): Promise<"ok" | "conflict" | "error">;
  cancel(interactionId: string, appointmentId: string, reasonCode: string): Promise<"ok" | "error">;
  close(interactionId: string, outcome: string, notes: string): Promise<void>;
  history(): Promise<CcCallRow[]>;
}

const REASONS = ["BookAppointment", "RescheduleAppointment", "CancelAppointment", "AppointmentEnquiry", "EligibilityEnquiry", "UpdateContact", "Complaint", "Other"];
const CANCEL_REASONS = ["PatientRequest", "PatientUnwell", "TransportIssue", "Rescheduling", "ClinicClosure", "DuplicateBooking", "Other"];
// Bilingual display labels for the reason enums — the enum stays the option `value` (sent to the service),
// only the shown text is localized so the AR portal never renders a raw English enum literal.
const REASON_LABELS: Record<string, { en: string; ar: string }> = {
  BookAppointment: { en: "Book appointment", ar: "حجز موعد" },
  RescheduleAppointment: { en: "Reschedule appointment", ar: "إعادة جدولة موعد" },
  CancelAppointment: { en: "Cancel appointment", ar: "إلغاء موعد" },
  AppointmentEnquiry: { en: "Appointment enquiry", ar: "استفسار عن موعد" },
  EligibilityEnquiry: { en: "Eligibility enquiry", ar: "استفسار عن الأهلية" },
  UpdateContact: { en: "Update contact", ar: "تحديث بيانات الاتصال" },
  Complaint: { en: "Complaint", ar: "شكوى" },
  Other: { en: "Other", ar: "أخرى" },
};
const CANCEL_REASON_LABELS: Record<string, { en: string; ar: string }> = {
  PatientRequest: { en: "Patient request", ar: "طلب المريض" },
  PatientUnwell: { en: "Patient unwell", ar: "اعتلال صحة المريض" },
  TransportIssue: { en: "Transport issue", ar: "مشكلة في المواصلات" },
  Rescheduling: { en: "Rescheduling", ar: "إعادة جدولة" },
  ClinicClosure: { en: "Clinic closure", ar: "إغلاق العيادة" },
  DuplicateBooking: { en: "Duplicate booking", ar: "حجز مكرّر" },
  Other: { en: "Other", ar: "أخرى" },
};
const OUTCOMES = ["Resolved", "FollowUpRequired", "Transferred", "Abandoned", "NoAction"];

async function req<T>(
  method: string, path: string, body?: unknown, idempotencyKey?: string,
): Promise<{ status: number; data: T | null }> {
  const token = getToken();
  const resp = await fetch(`${API_BASE}${path}`, {
    method,
    headers: {
      ...(body ? { "Content-Type": "application/json" } : {}),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
      ...(idempotencyKey ? { "Idempotency-Key": idempotencyKey } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
  });
  const data = resp.status === 204 ? null : ((await resp.json().catch(() => null)) as T | null);
  return { status: resp.status, data };
}

/**
 * The key for one BOOKING INTENT. Derived from the interaction, the member and the slot, so pressing Book twice
 * after a timeout replays the same write instead of holding two slots, while choosing a different time is a
 * genuinely new one. emr REQUIRES this header and rejects the request without it — the call centre sent none, so
 * every reservation came back 400 "Idempotency-Key header is required" long before the slot was ever considered.
 */
const bookingKey = (interactionId: string, beneficiaryId: string, slotId: string) =>
  `cc-book:${interactionId}:${beneficiaryId}:${slotId}`;

/** The live gateway-backed implementation (used in the app; tests inject a fake instead). */
export function createHttpCcApi(): CcApi {
  return {
    async openInteraction(reasonCode) {
      const r = await req<{ interactionId: string; callRef: string }>("POST", "/call-interactions", { direction: "Inbound", reasonCode });
      return r.data ?? { interactionId: "", callRef: "" };
    },
    async verify(interactionId, beneficiaryId, types, pass) {
      const r = await req("POST", `/call-interactions/${interactionId}/verification`, { beneficiaryId, verifiedIdentifierTypes: types, result: pass ? "Passed" : "Failed" });
      return r.status >= 200 && r.status < 300 && pass;
    },
    async search(q) {
      const r = await req<{ matches: CcMatch[] }>("GET", `/call-centre/search?q=${encodeURIComponent(q)}`);
      return r.data?.matches ?? [];
    },
    async summary(beneficiaryId, interactionId) {
      const r = await req<Cc360>("GET", `/call-centre/members/${beneficiaryId}/summary?interactionId=${interactionId}`);
      return r.status === 200 ? r.data : null;
    },
    async clinics() {
      // emr answers which clinics have bookable times; provider-service puts names to the ids. Neither needs
      // provider:read, which the call centre does not hold.
      const r = await req<any[]>("GET", "/branch-clinics");
      const rows = r.data ?? [];
      if (rows.length === 0) return [];
      const ids = rows.map((c) => c.locationId).filter(Boolean).join(",");
      const labels = new Map<string, string>();
      const l = await req<any[]>("GET", `/clinic-labels?locationIds=${encodeURIComponent(ids)}`);
      for (const row of l.data ?? []) labels.set(String(row.locationId), `${row.providerName} · ${row.locationName}`);

      // Branch NAMES too: the agent chooses the branch they are booking into, so it has to be a name.
      const branchIds = [...new Set(rows.map((c) => c.branchId).filter(Boolean).map(String))];
      const branchNames = new Map<string, string>();
      if (branchIds.length > 0) {
        const b = await req<any[]>("GET", `/branch-labels?branchIds=${encodeURIComponent(branchIds.join(","))}`);
        for (const row of b.data ?? []) branchNames.set(String(row.branchId), String(row.nameEn));
      }

      return rows.map((c) => ({
        providerId: String(c.providerId), locationId: String(c.locationId), branchId: c.branchId ?? null,
        branchName: c.branchId ? branchNames.get(String(c.branchId)) ?? null : null,
        label: labels.get(String(c.locationId)) ?? String(c.locationId).slice(0, 8),
        openSlots: Number(c.openSlots ?? 0),
      }));
    },
    async slots(providerId, locationId) {
      const r = await req<any[]>("GET", `/appointment-slots?providerId=${providerId}&locationId=${locationId}&onlyOpen=true`);
      return (r.data ?? []).map((x) => ({ slotId: String(x.slotId), start: String(x.slotStart) }));
    },
    async book(interactionId, beneficiaryId, slotId, branchId, extra) {
      // slotId used to be crypto.randomUUID(): a slot that cannot exist, so emr answered 404 and no reservation
      // the call centre made could ever hold a real time.
      const r = await req("POST", "/call-centre/appointments", {
        interactionId, beneficiaryId, slotId, branchId, appointmentType: "Scheduled",
        // Omitted rather than sent as null when unset — the slot is authoritative for the doctor when it
        // names one, and an explicit null would overwrite that with "no doctor".
        ...(extra?.doctorId ? { doctorId: extra.doctorId } : {}),
        ...(extra?.note ? { note: extra.note } : {}),
      }, bookingKey(interactionId, beneficiaryId, slotId));
      if (r.status === 409) return "conflict";
      return r.status >= 200 && r.status < 300 ? "ok" : "error";
    },
    async reschedule(interactionId, appointmentId, newSlotId) {
      const r = await req("POST", `/call-centre/appointments/${appointmentId}/reschedule`, { interactionId, newSlotId },
        bookingKey(interactionId, appointmentId, newSlotId));
      if (r.status === 409) return "conflict";
      return r.status >= 200 && r.status < 300 ? "ok" : "error";
    },
    async cancel(interactionId, appointmentId, reasonCode) {
      const r = await req("POST", `/call-centre/appointments/${appointmentId}/cancel`, { interactionId, reasonCode },
        `cc-cancel:${interactionId}:${appointmentId}`);
      return r.status >= 200 && r.status < 300 ? "ok" : "error";
    },
    async close(interactionId, outcome, notes) {
      await req("POST", `/call-interactions/${interactionId}/close`, { outcome, notes });
    },
    async history() {
      const r = await req<{ items: CcCallRow[] }>("GET", "/call-interactions");
      return r.data?.items ?? [];
    },
  };
}

/**
 * Phase 15.5 — the call-shaped Call Centre workspace (the heart of the portal, design 37 §6). A single screen:
 * START CALL → SEARCH (phone-first) → VERIFY (≥2 identifier TYPES) → MEMBER 360 (all branches) → ACT (book /
 * cancel) → WRAP UP. Nothing about a member renders until a verification PASS is recorded (verify-before-disclose,
 * enforced again server-side). Verification result + booking outcomes announce via aria-live. No clinical field
 * exists anywhere in this graph.
 */
/**
 * ONE client for the module, not one per render.
 *
 * `api = createHttpCcApi()` as a default PARAMETER builds a new object every time the component renders, so any
 * effect keyed on `api` re-runs on every render and its cleanup marks the previous request stale. The clinic list
 * fetched fine — five times, 200 each — and never landed in state, because each run was discarded by the next.
 * The same shape caused a request storm in the policy screens; there it wasted requests, here it silently
 * emptied the reservation panel. Tests still inject their own client through the prop.
 */
const defaultCcApi = createHttpCcApi();

export function CallCentreWorkspace({ api = defaultCcApi }: { api?: CcApi }) {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang];

  const [interactionId, setInteractionId] = useState<string | null>(null);
  const [reason, setReason] = useState(REASONS[0]);
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<CcMatch[] | null>(null);
  const [selected, setSelected] = useState<CcMatch | null>(null);
  const [ticks, setTicks] = useState<Set<string>>(new Set());
  const [verifiedFor, setVerifiedFor] = useState<string | null>(null);
  const [summary, setSummary] = useState<Cc360 | null>(null);
  const [announce, setAnnounce] = useState("");
  const [verifyError, setVerifyError] = useState(false);
  const [cancelReason, setCancelReason] = useState("");
  const [cancelFor, setCancelFor] = useState<string | null>(null);
  const [cancelError, setCancelError] = useState(false);
  const [outcome, setOutcome] = useState(OUTCOMES[0]);
  const [notes, setNotes] = useState("");
  /**
   * The reservation panel is an ACTION ON THE FILE rather than a permanent fixture inside it. Most calls are
   * not bookings — an eligibility question, a contact correction — and a booking form sitting open under every
   * member's appointment list is both noise and an invitation to book by accident. Cleared whenever the member
   * changes, so the panel never carries a branch/clinic/time chosen for a different person.
   */
  const [showReserve, setShowReserve] = useState(false);

  const startCall = useCallback(async () => {
    const { interactionId: id } = await api.openInteraction(reason);
    setInteractionId(id);
    setResults(null); setSelected(null); setVerifiedFor(null); setSummary(null); setAnnounce("");
  }, [api, reason]);

  const doSearch = useCallback(async () => {
    if (!query.trim()) return;
    setResults(await api.search(query.trim()));
    setSelected(null); setVerifiedFor(null); setSummary(null);
  }, [api, query]);

  const select = useCallback((m: CcMatch) => {
    setSelected(m); setTicks(new Set()); setVerifiedFor(null); setSummary(null); setVerifyError(false);
    setShowReserve(false);
  }, []);

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
    if (ok) {
      const s = await api.summary(selected.beneficiaryId, interactionId);
      setVerifiedFor(selected.beneficiaryId);
      setSummary(s);
      setAnnounce(t(L.ccVerified));
    } else {
      setAnnounce(t(L.ccFailed));
    }
  }, [api, interactionId, selected, ticks, t]);

  // 14.5 — the SAME form the standalone Book-appointment journey and reception use. The workspace was the
  // last caller of the old branch → clinic → time picker, so converting it retires that second copy of the
  // dependency chain rather than leaving the call centre with two booking UIs that can drift apart.
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

  const book = useCallback(async () => {
    if (!interactionId || !verifiedFor || !sel.slotId) return;
    const outcome = await api.book(interactionId, verifiedFor, sel.slotId, sel.branchId, {
      doctorId: sel.doctorId,
      note: sel.note || undefined,
    });
    setAnnounce(outcome === "ok" ? t(L.ccBooked) : outcome === "conflict" ? t(L.ccSlotTaken) : t(L.ccBookFailed));
    // A 409 invalidates the times exactly as a success does: one consumed the slot, the other proves someone
    // else did. Only a transport error leaves the loaded times still true. The agent's branch, specialty and
    // doctor survive either way — re-entering the caller's request mid-call is a cost they should not pay.
    if (outcome !== "error") setReloadToken((k) => k + 1);
  }, [api, interactionId, verifiedFor, sel, t]);

  const copyNotes = useCallback(async () => {
    // Guard the METHOD, not just the object: a non-secure context exposes `navigator.clipboard` as undefined
    // and jsdom exposes neither, so an unguarded call is an unhandled rejection in the agent's face.
    try {
      await navigator.clipboard?.writeText?.(notes);
      setAnnounce(t(L.ccCopied));
    } catch {
      setAnnounce(t(L.ccCopyFailed));
    }
  }, [notes, t]);

  const cancel = useCallback(async (appointmentId: string) => {
    if (!interactionId) return;
    if (!cancelReason) { setCancelError(true); return; }
    setCancelError(false);
    const r = await api.cancel(interactionId, appointmentId, cancelReason);
    setAnnounce(r === "ok" ? t(L.ccCancelled) : t(L.ccBookFailed));
    setCancelFor(null);
  }, [api, interactionId, cancelReason, t]);

  const reschedule = useCallback(async (appointmentId: string) => {
    // Rescheduling needs a NEW time, which lives in the reservation panel — so opening the panel is part of
    // the instruction, not a separate thing the agent has to work out.
    if (!interactionId || !sel.slotId) { setShowReserve(true); setAnnounce(t(L.ccPickTime)); return; }
    const outcome = await api.reschedule(interactionId, appointmentId, sel.slotId);
    setAnnounce(outcome === "ok" ? t(L.ccRescheduled) : outcome === "conflict" ? t(L.ccSlotTaken) : t(L.ccBookFailed));
    if (outcome !== "error") setReloadToken((k) => k + 1);
  }, [api, interactionId, sel.slotId, t]);

  const isVerified = !!selected && verifiedFor === selected.beneficiaryId;

  return (
    <div className="cc-workspace">
      <PageHeader title={t({ en: "Call workspace", ar: "مساحة المكالمة" })} />

      {/* aria-live outcome announcer (verification / booking / cancellation) */}
      <div aria-live="polite" role="status" data-testid="cc-live" className="cc-live">{announce}</div>

      {/* 1. CALL BAR */}
      <Card>
        <div className="cc-callbar" role="group" aria-label={t(L.ccReason)}>
          {!interactionId ? (
            <>
              <label htmlFor="cc-reason">{t(L.ccReason)}</label>
              <select id="cc-reason" value={reason} onChange={(e) => setReason(e.target.value)}>
                {REASONS.map((r) => <option key={r} value={r}>{t(REASON_LABELS[r])}</option>)}
              </select>
              <Button variant="primary" onClick={startCall}>{t(L.ccStartCall)}</Button>
            </>
          ) : (
            <>
              <StatusChip kind="ok" label={t(L.ccOnCall)} />
              <Button variant="ghost" onClick={async () => { await api.close(interactionId, outcome, notes); setInteractionId(null); }}>
                {t(L.ccCloseCall)}
              </Button>
            </>
          )}
        </div>
      </Card>

      {interactionId && (
        <>
          {/* 2. SEARCH */}
          <Card>
            <div className="cc-search">
              <InputField
                label={t(L.ccSearchLabel)}
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
                    <button type="button" className="cc-result" onClick={() => select(m)} aria-pressed={selected?.beneficiaryId === m.beneficiaryId}>
                      <span>{m.displayName}</span>
                      {m.memberNo && <span className="cc-muted">{m.memberNo}</span>}
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </Card>

          {/* 3. VERIFY — visible only until verified. NO member detail is shown here. */}
          {selected && !isVerified && (
            <Card>
              <div className="cc-locked" role="region" aria-label={t(L.ccNotVerified)}>
                <span className="cc-lockchip" data-testid="cc-lockchip">
                  <Icon name="info" /> <span>{t(L.ccNotVerified)}</span>
                </span>
                <p>{t(L.ccNotVerifiedBody)}</p>
                <fieldset>
                  <legend>{t(L.ccChallengeOn)}</legend>
                  {selected.challengeableIdentifierTypes.map((type) => (
                    <label key={type} className="cc-check">
                      {/* Localized: the raw enum literal was rendered, so an Arabic-portal agent was asked to
                          challenge the caller on "DateOfBirth". */}
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

          {/* 4. MEMBER 360 — unlocked only after a PASS. */}
          {isVerified && summary && (
            <Card>
              <div className="cc-360" data-testid="cc-360">
                <h2>{summary.identity.displayName} {summary.identity.memberNo && <span className="cc-muted">· {summary.identity.memberNo}</span>}</h2>
                {/* 18.D2 (U3) — the chip's KIND and LABEL now come from the SAME value. This was
                    kind="ok" with a server-supplied label, so a Suspended or Expired member displayed as
                    green with the real word beside it in small text — and an agent under call pressure
                    reads the colour. */}
                <StatusChip
                  kind={memberStatus(summary.identity.status).kind}
                  label={t(memberStatus(summary.identity.status).label)}
                />
                {/* Phase 20 — into the unified profile, carrying the verified interaction. The link appears
                    only AFTER a pass, because the phase-15 gate is what makes any disclosure legitimate; the
                    profile endpoint refuses an unverified call-centre principal independently (ADR-0026). */}
                <a
                  className="profile-action-link"
                  href={`/patients/${encodeURIComponent(selected.beneficiaryId)}?interactionId=${encodeURIComponent(interactionId ?? "")}`}
                >
                  {t(L.ccOpenProfile)}
                </a>

                <section aria-label={t(L.ccCoverage)}>
                  <h3>{t(L.ccCoverage)}</h3>
                  <ul>{summary.coverage.map((c) => <li key={c.category}>{c.category}: {c.remainingLimit ?? "—"} / {c.annualLimit ?? "—"}</li>)}</ul>
                </section>

                <section aria-label={t(L.ccContacts)}>
                  <h3>{t(L.ccContacts)}</h3>
                  <ul>{summary.contacts.map((c) => <li key={c.contactId}>{t(identifierTypeLabel(c.kind))}: {c.value}{c.isPrimary ? " ★" : ""}</li>)}</ul>
                </section>

                <section aria-label={t(L.ccAppointments)}>
                  <h3>{t(L.ccAppointments)}</h3>
                  <ul className="cc-appts">
                    {summary.appointments.map((a) => (
                      <li key={a.appointmentId}>
                        <span>{t(appointmentTypeLabel(a.appointmentType))} · {fmt.date(a.scheduledStart)} · {a.branchName ?? "—"} · {a.doctorName ?? "—"}{a.specialty ? ` (${a.specialty})` : ""}</span>
                        {a.canReschedule && (
                          <Button variant="ghost" onClick={() => reschedule(a.appointmentId)}>{t(L.ccReschedule)}</Button>
                        )}
                        {a.canCancel && (
                          cancelFor === a.appointmentId ? (
                            <span className="cc-cancel">
                              <label>
                                {t(L.ccCancelReason)}
                                <select value={cancelReason} onChange={(e) => setCancelReason(e.target.value)}>
                                  <option value="">—</option>
                                  {CANCEL_REASONS.map((code) => <option key={code} value={code}>{t(CANCEL_REASON_LABELS[code])}</option>)}
                                </select>
                              </label>
                              <Button variant="danger" onClick={() => cancel(a.appointmentId)}>{t(L.ccCancel)}</Button>
                              {cancelError && <span role="alert" className="cc-error">{t(L.ccCancelReasonRequired)}</span>}
                            </span>
                          ) : (
                            <Button variant="ghost" onClick={() => { setCancelFor(a.appointmentId); setCancelReason(""); setCancelError(false); }}>{t(L.ccCancel)}</Button>
                          )
                        )}
                      </li>
                    ))}
                  </ul>
                  {/* Booking is an ACTION ON THE FILE: the agent is already looking at this member's
                      appointments, so "New appointment" belongs here rather than on another screen. The panel
                      itself is shared with the standalone Book-appointment journey, so the branch → clinic →
                      time rules cannot drift between the two. Arrivals are deliberately absent — no check-in,
                      no no-show, no start-visit — and the server enforces that with appointment:reserve rather
                      than appointment:write, so the missing buttons are presentation, not the boundary. */}
                  {showReserve ? (
                    <>
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
                    </>
                  ) : (
                    <Button variant="secondary" onClick={() => setShowReserve(true)}>
                      <Icon name="plus" /> {t(L.ccNewAppointment)}
                    </Button>
                  )}
                </section>

                {summary.openReferrals.length > 0 && (
                  <section aria-label={t(L.ccReferrals)}>
                    <h3>{t(L.ccReferrals)}</h3>
                    <ul>{summary.openReferrals.map((ref) => <li key={ref.referralRef}>{ref.referralRef} · {ref.status}{ref.requestedSpecialty ? ` · ${ref.requestedSpecialty}` : ""}</li>)}</ul>
                  </section>
                )}

                {/* Call notes ON THE FILE. They were only reachable in the wrap-up card at the very bottom, so
                    an agent taking a note mid-call had to leave the member they were reading. Same state as
                    what is sent on close — one note per call, not two that can disagree. */}
                <section aria-label={t(L.ccCallNotes)}>
                  <h3>{t(L.ccCallNotes)}</h3>
                  <CallNotes value={notes} onChange={setNotes} onCopy={copyNotes} />
                </section>
              </div>
            </Card>
          )}

          {/* 6. WRAP UP — the outcome, plus the notes when there is no member file to host them (a call that
              never resolved to a member still has to be recorded). */}
          <Card>
            <div className="cc-wrapup" role="group" aria-label={t(L.ccWrapUp)}>
              <label>{t(L.ccOutcome)}
                <select value={outcome} onChange={(e) => setOutcome(e.target.value)}>
                  {OUTCOMES.map((o) => <option key={o} value={o}>{o}</option>)}
                </select>
              </label>
              {/* Rendered here ONLY when the file is not showing it — two controls with the same label is an
                  ambiguous accessible name, and the duplicate is what makes an agent wonder which one saves. */}
              {!(isVerified && summary) && (
                <InputField label={t(L.ccCallNotes)} value={notes} onChange={(e) => setNotes(e.target.value)} />
              )}
            </div>
          </Card>
        </>
      )}
    </div>
  );
}

/** Phase 15.5 — the agent's own call history (supervisors see the team, server-side). */
export function CallHistory({ api = defaultCcApi }: { api?: CcApi }) {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang];
  const [rows, setRows] = useState<CcCallRow[] | null>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    let live = true;
    api.history()
      .then((r) => { if (live) { setRows(r); setFailed(false); } })
      .catch(() => { if (live) setFailed(true); });
    return () => { live = false; };
  }, [api]);

  return (
    <div>
      <PageHeader title={t(L.ccHistoryTitle)} />
      {/* An error is distinct from an empty history — never render a failed load as "no calls". */}
      {failed && (
        <p role="alert" className="cc-error">
          {t(L.ccHistoryError)}{" "}
          <button type="button" onClick={() => { setFailed(false); setRows(null); void api.history().then((r) => setRows(r)).catch(() => setFailed(true)); }}>
            {t(L.retry)}
          </button>
        </p>
      )}
      {!failed && rows && rows.length === 0 && <p role="status">{t(L.ccHistoryEmpty)}</p>}
      {!failed && rows && rows.length > 0 && (
        <ul className="cc-history">
          {rows.map((r) => (
            <li key={r.callRef}>{r.callRef} · {fmt.dateTime(r.startedAt)} · {r.status}{r.outcome ? ` · ${t(callOutcomeLabel(r.outcome))}` : ""}</li>
          ))}
        </ul>
      )}
    </div>
  );
}

import { memberStatus, callOutcomeLabel, callReasonLabel, identifierTypeLabel, appointmentTypeLabel } from "./statusLabels";
import { useFormat } from "../i18n/useFormat";
import { useCallback, useEffect, useState } from "react";
import { Button, Card, Icon, InputField, Modal, StatusChip, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";
import { API_BASE } from "../config";
import { getToken } from "../auth/tokenStore";
import { ApiError } from "../api/http";
import { PageHeader, useOpenProfile } from "./_shared";
import { BookingForm, type BookingSelection } from "./booking/BookingForm";
import { useApi } from "../api/ApiProvider";
import type { BranchSummary } from "@mersal/contracts";
import { CallSummaryDraft } from "./CallNotes";
import { MemberSearch } from "./CallCentreSearch";
import { useRestorableState } from "./useRestorableState";

// ── Types (mirror the callcentre-service DTOs; CLINICAL-FREE by construction) ───────────────────────────
export interface CcMatch {
  beneficiaryId: string;
  displayName: string;
  /** The real member number. It used to arrive masked (`•••001`) because MemberNo was an identifier the agent
   *  could be challenged on, and showing it in full would have let them answer their own challenge. Identity is
   *  now confirmed on the phone, so the mask protected nothing and cost the agent the one field that tells two
   *  members with the same name apart. */
  memberNo?: string | null;
}
export interface CcCoverageLine { category: string; annualLimit?: number | null; remainingLimit?: number | null }
export interface CcContact { contactId: string; kind: string; value: string; isPrimary: boolean }
export interface CcAppointment {
  appointmentId: string; appointmentType: string; status: string; scheduledStart: string;
  branchName?: string | null; doctorName?: string | null; specialty?: string | null;
  canReschedule: boolean; canCancel: boolean;
  /** emr's `xmin` concurrency token, echoed back as `If-Match` on a reschedule/cancel so a stale write gets
   *  412 instead of silently clobbering a change another agent already made to this appointment. */
  rowVersion: number;
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

// ── Wire rows for the four gateway reads with no generated contract ────────────────────────────────────
// These were `any[]`, which CLAUDE.md's "TS strict, no `any`" forbids and which eslint had been failing the
// frontend build on. `any` here was not laziness so much as honesty about untyped aggregation endpoints —
// but it disables checking on every field access below, so a renamed field would have compiled and produced
// a clinic list of "undefined". Named shapes restore that check.
//
// The String()/Number() coercions at the call sites are deliberately KEPT. These types describe what the
// gateway is expected to send; they do not verify it, and there is no runtime validation on this path. A
// declared type plus a coercion says "expected string, defended anyway", which is the true position.
interface BranchClinicRow { providerId: string; locationId: string; branchId?: string | null; openSlots?: number | null }
interface ClinicLabelRow { locationId: string; providerName: string; locationName: string }
interface BranchLabelRow { branchId: string; nameEn: string }
interface AppointmentSlotRow { slotId: string; slotStart: string }

/** The narrow surface the workspace needs. The default implementation calls the gateway; tests inject a fake. */
export interface CcApi {
  /**
   * Open the call record. `direction` says WHO RANG WHOM — inbound (the member called us) or outbound (we
   * called them). It was hard-coded "Inbound" on every call the portal ever opened, which made the field a
   * constant dressed as data: outbound follow-up calls, the ones a supervisor most wants to count, were all
   * filed as inbound. It is set at open and cannot be corrected afterwards, so the control that collects it
   * locks once the call is under way.
   */
  openInteraction(reasonCode: string, direction: CcDirection): Promise<{ interactionId: string; callRef: string }>;
  /**
   * Open a member's file on this call.
   *
   * Records the agent's attestation that they confirmed, on the phone, who they are speaking to — and BINDS the
   * interaction to that member, which is what every later read and write on this call is authorized against.
   * It replaced a `verify(…, types, pass)` that asked the agent to tick ≥2 identifiers for the server to score.
   */
  openMember(interactionId: string, beneficiaryId: string): Promise<boolean>;
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
  ): Promise<CcOutcome<CcBookResult>>;
  /** `rowVersion` rides along as If-Match: a reschedule computed against times the agent loaded before someone
   *  else moved the appointment must be refused (412 → "stale"), not applied. */
  reschedule(interactionId: string, appointmentId: string, newSlotId: string, rowVersion?: number): Promise<CcOutcome<CcWriteResult>>;
  cancel(interactionId: string, appointmentId: string, reasonCode: string, rowVersion?: number): Promise<CcOutcome<CcWriteResult>>;
  /**
   * Wrap up the call. `summary` is REQUIRED by the server for every outcome but `Abandoned` (phase 20.3b) — it
   * is what other roles read later through the patient profile. The result is RETURNED rather than swallowed:
   * this used to be `Promise<void>`, so a 422 for a missing summary was invisible and the workspace cleared the
   * call bar as though the call had been wrapped up, leaving the interaction Open in the database forever.
   *
   * There is no `notes` argument. It carried a second body of text kept apart from the summary; the call centre
   * now writes one account of the call, which is this one.
   */
  close(interactionId: string, outcome: string, summary: string, reasonCode?: string): Promise<CcOutcome<CcCloseResult>>;
  history(): Promise<CcCallRow[]>;
}

/** Who rang whom. Recorded on the interaction when it opens. */
export type CcDirection = "Inbound" | "Outbound";

/** A write against the emr engine. `stale` is emr's 412 — the appointment moved under the agent. */
export type CcWriteResult = "ok" | "conflict" | "stale" | "error";

/** Wrap-up outcome. `summary-required` is the server's 422 and is a correctable mistake, not a failure. */
export type CcCloseResult = "ok" | "summary-required" | "not-your-call" | "error";

/** The verdict a booking POST can reach. */
export type CcBookResult = "ok" | "conflict" | "error";

/**
 * A write's verdict, plus what the server said when the verdict is the generic `"error"`.
 *
 * ============================================================================================================
 * WHY THE WORD ALONE WAS NOT ENOUGH
 * ============================================================================================================
 * Each method used to return a bare word from a union, so every failure the screen has no specific sentence
 * for collapsed into "Couldn't book that time." The agent is on the phone with the person it concerns, and
 * the server frequently knows the actual reason — the member's coverage lapsed, the clinic is closed that
 * day, the referral required for this specialty has expired. All of it was read off the wire and dropped.
 *
 * `detail` is populated ONLY for `"error"`. The named verdicts — 409 slot-taken, 412 stale, 422
 * summary-required, 403 not-your-call — already have sentences written for the conversation the agent is
 * having, and replacing those with a service's own phrasing would be a downgrade, not an improvement.
 */
export interface CcOutcome<K extends string> {
  kind: K;
  /** The service's RFC-7807 `detail` (or `title`), when the failure carried one. `"error"` only. */
  detail?: string;
}

/** The call reasons the service accepts (mirrors `CallReasonCode`). Exported: the booking journey offers the
 *  same list in its call-record step, and two copies would drift the moment one gained a value. */
export const CALL_REASONS = ["BookAppointment", "RescheduleAppointment", "CancelAppointment", "AppointmentEnquiry", "EligibilityEnquiry", "UpdateContact", "Complaint", "Other"];
const CANCEL_REASONS = ["PatientRequest", "PatientUnwell", "TransportIssue", "Rescheduling", "ClinicClosure", "DuplicateBooking", "Other"];
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

/**
 * The call centre's own request helper.
 *
 * ============================================================================================================
 * WHY IT IS STILL SEPARATE FROM `http.ts`, AND WHAT IT NO LONGER GETS WRONG
 * ============================================================================================================
 * `http.ts` throws on any non-2xx. Every call below distinguishes SPECIFIC statuses as ANSWERS rather than
 * failures — 409 "someone already opened this", 412 "the appointment moved while you were on the phone", 422
 * `summary-required`, 403 "not your call" — and each drives a different thing the agent says to the person
 * they are speaking to. Routing those through an exception and re-reading `.status` off it would be the same
 * branch with a longer path to it, so the `{ status, data }` shape stays.
 *
 * What was genuinely wrong, and is fixed here: a TRANSPORT failure — the tablet dropping its wifi mid-call —
 * escaped as a raw `TypeError: Failed to fetch`. `writeErrorMessage` renders anything that is not an
 * {@link ApiError} by stringifying it, so the agent read "TypeError: Failed to fetch" and had no way to tell
 * that from a server refusal. That is precisely the RETRY / RELOAD / STOP distinction the phase-18 D1 rule
 * exists for, and it is the one class this helper could never express.
 *
 * Fixed alongside it: a failure carrying an RFC-7807 body no longer renders as a bare "error". The verdicts
 * are {@link CcOutcome} rather than bare words, so the server's `detail` reaches the agent — see that type
 * for why only the GENERIC failure carries it.
 *
 * NOT changed: the absent `X-Active-Branch` header. The call centre is deliberately cross-branch and states
 * the branch in the body where it matters (see `zBookingRequest.branchId`); adding the header here would
 * narrow an agent to one clinic, which is the opposite of the job.
 */
async function req<T>(
  method: string, path: string, body?: unknown, idempotencyKey?: string, ifMatch?: number,
): Promise<{ status: number; data: T | null }> {
  const token = getToken();
  let resp: Response;
  try {
    resp = await fetch(`${API_BASE}${path}`, {
      method,
      headers: {
        ...(body ? { "Content-Type": "application/json" } : {}),
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
        ...(idempotencyKey ? { "Idempotency-Key": idempotencyKey } : {}),
        // Quoted per RFC 7232. callcentre-service forwards this verbatim to emr, which has always parsed it and
        // returned 412 on a stale write — the guarantee was implemented end to end and simply never armed,
        // because no call-centre client ever sent the header.
        ...(ifMatch !== undefined ? { "If-Match": `"${ifMatch}"` } : {}),
      },
      body: body ? JSON.stringify(body) : undefined,
    });
  } catch (e) {
    throw new ApiError("network", e instanceof Error ? e.message : "Network request failed");
  }
  const data = resp.status === 204 ? null : ((await resp.json().catch(() => null)) as T | null);
  return { status: resp.status, data };
}

/**
 * The generic failure sentence with the service's own reason appended, when it gave one.
 *
 * Parenthesised and untranslated, exactly as `writeError.withDetail` does it: the service's `detail` is one
 * string in one language, and the alternative to showing it in a sentence of the other is not showing it at
 * all. An agent on the phone being told "the referral for this specialty expired on 12 July" in English
 * inside an Arabic sentence is strictly better than being told "Couldn't book that time."
 */
export function withReason(base: string, o: { detail?: string }): string {
  return o.detail ? `${base} (${o.detail})` : base;
}

/**
 * Build a verdict. The server's own `detail` is attached only to the generic `"error"` — see {@link CcOutcome}.
 */
function verdict<K extends string>(kind: K, body?: unknown): CcOutcome<K> {
  if (kind !== "error") return { kind };
  const b = body && typeof body === "object" ? (body as Record<string, unknown>) : undefined;
  const str = (v: unknown) => (typeof v === "string" && v.length > 0 ? v : undefined);
  return { kind, detail: str(b?.detail) ?? str(b?.title) };
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
    async openInteraction(reasonCode, direction) {
      const r = await req<{ interactionId: string; callRef: string }>("POST", "/call-interactions", { direction, reasonCode });
      return r.data ?? { interactionId: "", callRef: "" };
    },
    async openMember(interactionId, beneficiaryId) {
      // One field. The server records method "OffSystem" and binds the call; there is no threshold to meet and
      // nothing to fail, so there is no pass/fail to send or interpret.
      const r = await req("POST", `/call-interactions/${interactionId}/verification`, { beneficiaryId });
      return r.status >= 200 && r.status < 300;
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
      const r = await req<BranchClinicRow[]>("GET", "/branch-clinics");
      const rows = r.data ?? [];
      if (rows.length === 0) return [];
      const ids = rows.map((c) => c.locationId).filter(Boolean).join(",");
      const labels = new Map<string, string>();
      const l = await req<ClinicLabelRow[]>("GET", `/clinic-labels?locationIds=${encodeURIComponent(ids)}`);
      for (const row of l.data ?? []) labels.set(String(row.locationId), `${row.providerName} · ${row.locationName}`);

      // Branch NAMES too: the agent chooses the branch they are booking into, so it has to be a name.
      const branchIds = [...new Set(rows.map((c) => c.branchId).filter(Boolean).map(String))];
      const branchNames = new Map<string, string>();
      if (branchIds.length > 0) {
        const b = await req<BranchLabelRow[]>("GET", `/branch-labels?branchIds=${encodeURIComponent(branchIds.join(","))}`);
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
      const r = await req<AppointmentSlotRow[]>("GET", `/appointment-slots?providerId=${providerId}&locationId=${locationId}&onlyOpen=true`);
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
      if (r.status === 409) return verdict("conflict");
      return verdict(r.status >= 200 && r.status < 300 ? "ok" : "error", r.data);
    },
    async reschedule(interactionId, appointmentId, newSlotId, rowVersion) {
      const r = await req("POST", `/call-centre/appointments/${appointmentId}/reschedule`, { interactionId, newSlotId },
        bookingKey(interactionId, appointmentId, newSlotId), rowVersion);
      if (r.status === 409) return verdict("conflict");
      if (r.status === 412) return verdict("stale");
      return verdict(r.status >= 200 && r.status < 300 ? "ok" : "error", r.data);
    },
    async cancel(interactionId, appointmentId, reasonCode, rowVersion) {
      const r = await req("POST", `/call-centre/appointments/${appointmentId}/cancel`, { interactionId, reasonCode },
        `cc-cancel:${interactionId}:${appointmentId}`, rowVersion);
      if (r.status === 409) return verdict("conflict");
      if (r.status === 412) return verdict("stale");
      return verdict(r.status >= 200 && r.status < 300 ? "ok" : "error", r.data);
    },
    async close(interactionId, outcome, summary, reasonCode) {
      const r = await req<{ title?: string }>("POST", `/call-interactions/${interactionId}/close`,
        // `summary` is sent even when blank: letting the server refuse the close is the point. Omitting the
        // field to avoid the 422 would recreate the bug this replaced — a call that reads as wrapped up and
        // is still Open, still bound to that member, on the server.
        //
        // `reasonCode` rides along so a reason corrected mid-call lands on the record. The direction cannot:
        // it is fixed when the interaction opens, which is why its control locks rather than pretending.
        { outcome, summary, ...(reasonCode ? { reasonCode } : {}) });
      if (r.status >= 200 && r.status < 300) return verdict("ok");
      if (r.status === 422 && r.data?.title === "summary-required") return verdict("summary-required");
      if (r.status === 403) return verdict("not-your-call");
      return verdict("error", r.data);
    },
    async history() {
      const r = await req<{ items: CcCallRow[] }>("GET", "/call-interactions");
      return r.data?.items ?? [];
    },
  };
}

/**
 * The call-shaped Call Centre workspace (the heart of the portal, design 37 §6). A single screen:
 * START CALL → SEARCH → OPEN THE MEMBER'S FILE → MEMBER 360 (all branches) → ACT (book / cancel) → WRAP UP.
 *
 * <b>The VERIFY step is gone.</b> Identity is confirmed by the agent on the phone, so the screen no longer asks
 * them to tick ≥2 identifier types for the server to score. Opening a member's file records that attestation and
 * binds the call to that member, which is still what authorizes every read and write that follows — a call
 * cannot disclose a member it was not opened against, and stops disclosing when it closes.
 *
 * Booking outcomes and the file opening announce via aria-live. No clinical field exists anywhere in this graph.
 *
 * <b>The screen's state survives leaving it.</b> An agent opening the caller's patient profile mid-call used to
 * come back to a workspace that had forgotten the call — while the interaction was still Open on the server —
 * leaving them no visible option but to start a second call for the same conversation.
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
  const openProfile = useOpenProfile();

  /**
   * RESTORED state — what the agent needs to still be here after opening the caller's profile and coming back.
   *
   * Only the SHAPE of the work is persisted: the call id, what was typed, which member is open. The member's
   * details are not, and are re-fetched below through the same server gate as the first time — so returning to
   * this screen re-authorizes the disclosure rather than redisplaying a cached one.
   */
  const [interactionId, setInteractionId] = useRestorableState<string | null>("cc-workspace.call", null);
  const [query, setQuery] = useRestorableState("cc-workspace.query", "");
  const [openedFor, setOpenedFor] = useRestorableState<CcMatch | null>("cc-workspace.member", null);
  const [outcome, setOutcome] = useRestorableState("cc-workspace.outcome", OUTCOMES[0]);
  /** The one account of the call — what other roles read on the member's profile. There is no second "notes"
   *  field any more; this is it, so it is drafted on the file and sent at close. */
  const [wrapSummary, setWrapSummary] = useRestorableState("cc-workspace.summary", "");

  const [reason, setReason] = useState(CALL_REASONS[0]);
  /** Who rang whom. INBOUND by default because most hotline calls are — but it was HARD-CODED "Inbound" on
   *  every interaction the portal ever opened, so outbound follow-up calls (the ones a supervisor most wants
   *  to count) were all filed as inbound. Chosen before the call opens, because that is when it is written. */
  const [direction, setDirection] = useState<CcDirection>("Inbound");
  const [results, setResults] = useState<CcMatch[] | null>(null);
  const [summary, setSummary] = useState<Cc360 | null>(null);
  const [announce, setAnnounce] = useState("");
  const [cancelReason, setCancelReason] = useState("");
  const [cancelFor, setCancelFor] = useState<string | null>(null);
  const [cancelError, setCancelError] = useState(false);
  const [summaryError, setSummaryError] = useState(false);
  /**
   * The reservation panel is an ACTION ON THE FILE rather than a permanent fixture inside it. Most calls are
   * not bookings — an eligibility question, a contact correction — and a booking form sitting open under every
   * member's appointment list is both noise and an invitation to book by accident. Cleared whenever the member
   * changes, so the panel never carries a branch/clinic/time chosen for a different person.
   */
  const [showReserve, setShowReserve] = useState(false);

  const startCall = useCallback(async () => {
    const { interactionId: id } = await api.openInteraction(reason, direction);
    setInteractionId(id);
    setResults(null); setOpenedFor(null); setSummary(null); setAnnounce("");
  }, [api, reason, direction, setInteractionId, setOpenedFor]);

  const doSearch = useCallback(async () => {
    if (!query.trim()) return;
    setResults(await api.search(query.trim()));
    setOpenedFor(null); setSummary(null);
  }, [api, query, setOpenedFor]);

  /**
   * Open a member's file — the one step that replaced the identifier challenge.
   *
   * Picking a search hit is now the whole gesture: it records the agent's attestation that they confirmed the
   * caller on the phone, binds the call to this member, and loads the 360. The 360 is loaded FIRST-CLASS, not
   * optimistically — if the server refuses, nothing about the member renders and the agent is told, because the
   * screen showing a file the server would not serve is the failure mode worth avoiding.
   */
  const openMember = useCallback(async (m: CcMatch) => {
    if (!interactionId) return;
    setShowReserve(false);
    const attested = await api.openMember(interactionId, m.beneficiaryId).catch(() => false);
    if (!attested) { setAnnounce(t(L.ccOpenFileFailed)); return; }
    const s = await api.summary(m.beneficiaryId, interactionId).catch(() => null);
    if (!s) { setAnnounce(t(L.ccOpenFileFailed)); return; }
    setOpenedFor(m);
    setSummary(s);
    setAnnounce(t(L.ccFileOpened));
  }, [api, interactionId, setOpenedFor, t]);

  /**
   * Re-read the member's file after returning to this screen.
   *
   * The restored state says WHICH member is open on this call; it does not carry their details, and must not.
   * So a return trip re-fetches through the same server gate as the first visit — if the call has since been
   * closed elsewhere, or the binding no longer holds, the file simply does not come back.
   */
  useEffect(() => {
    if (!interactionId || !openedFor || summary) return;
    let live = true;
    void api.summary(openedFor.beneficiaryId, interactionId)
      .then((s) => { if (live && s) setSummary(s); })
      .catch(() => { /* the file stays closed; the agent can re-open it from the search */ });
    return () => { live = false; };
  }, [api, interactionId, openedFor, summary]);

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
    // A 409 invalidates the times exactly as a success does: one consumed the slot, the other proves someone
    // else did. Only a transport error leaves the loaded times still true. The agent's branch, specialty and
    // doctor survive either way — re-entering the caller's request mid-call is a cost they should not pay.
    if (outcome.kind !== "error") setReloadToken((k) => k + 1);
  }, [api, interactionId, openedFor, sel, t]);

  /**
   * Wrap up. The call bar clears ONLY on a confirmed close — the previous version awaited a `Promise<void>`
   * and reset regardless, so a refused close left the agent believing the call was wrapped up while the
   * interaction stayed Open on the server, still bound to that member.
   *
   * A confirmed close also DROPS the restored state. Resuming a call that has genuinely ended is worse than
   * starting clean: the agent would come back to a call bar for an interaction the server has already closed.
   */
  const closeCall = useCallback(async () => {
    if (!interactionId) return;
    const result = await api.close(interactionId, outcome, wrapSummary.trim(), reason);
    if (result.kind === "ok") {
      setSummaryError(false);
      setInteractionId(null);
      setResults(null); setOpenedFor(null); setSummary(null);
      setQuery(""); setWrapSummary(""); setShowReserve(false);
      setDirection("Inbound");
      setAnnounce(t(L.ccBookClosed));
      return;
    }
    // Everything below leaves the call OPEN, which is the truth — so the call bar stays as it is.
    setSummaryError(result.kind === "summary-required");
    setAnnounce(
      result.kind === "summary-required" ? t(L.ccSummaryRequired)
      : result.kind === "not-your-call" ? t(L.ccNotYourCall)
      : withReason(t(L.ccCloseFailed), result),
    );
  }, [api, interactionId, outcome, wrapSummary, reason, setInteractionId, setOpenedFor, setQuery, setWrapSummary, t]);

  const copySummary = useCallback(async () => {
    // Guard the METHOD, not just the object: a non-secure context exposes `navigator.clipboard` as undefined
    // and jsdom exposes neither, so an unguarded call is an unhandled rejection in the agent's face.
    try {
      await navigator.clipboard?.writeText?.(wrapSummary);
      setAnnounce(t(L.ccCopied));
    } catch {
      setAnnounce(t(L.ccCopyFailed));
    }
  }, [wrapSummary, t]);

  /** The appointment's current concurrency token, from the 360 the agent is looking at. */
  const rowVersionOf = useCallback((appointmentId: string) =>
    summary?.appointments.find((a) => a.appointmentId === appointmentId)?.rowVersion,
  [summary]);

  const cancel = useCallback(async (appointmentId: string) => {
    if (!interactionId) return;
    if (!cancelReason) { setCancelError(true); return; }
    setCancelError(false);
    const r = await api.cancel(interactionId, appointmentId, cancelReason, rowVersionOf(appointmentId));
    setAnnounce(
      r.kind === "ok" ? t(L.ccCancelled)
      : r.kind === "stale" ? t(L.ccApptStale)
      : withReason(t(L.ccBookFailed), r),
    );
    setCancelFor(null);
    // A stale refusal means the file on screen is out of date — re-read it rather than leaving the agent
    // acting on times that have already moved.
    if (r.kind === "stale") setReloadToken((k) => k + 1);
  }, [api, interactionId, cancelReason, rowVersionOf, t]);

  const reschedule = useCallback(async (appointmentId: string) => {
    // Rescheduling needs a NEW time, which lives in the reservation panel — so opening the panel is part of
    // the instruction, not a separate thing the agent has to work out.
    if (!interactionId || !sel.slotId) { setShowReserve(true); setAnnounce(t(L.ccPickTime)); return; }
    const outcome = await api.reschedule(interactionId, appointmentId, sel.slotId, rowVersionOf(appointmentId));
    setAnnounce(
      outcome.kind === "ok" ? t(L.ccRescheduled)
      : outcome.kind === "conflict" ? t(L.ccSlotTaken)
      : outcome.kind === "stale" ? t(L.ccApptStale)
      : withReason(t(L.ccBookFailed), outcome),
    );
    if (outcome.kind !== "error") setReloadToken((k) => k + 1);
  }, [api, interactionId, sel.slotId, rowVersionOf, t]);

  return (
    <div className="cc-workspace">
      <PageHeader title={t({ en: "Call workspace", ar: "مساحة المكالمة" })} />

      {/* aria-live outcome announcer (file opened / booking / cancellation) */}
      <div aria-live="polite" role="status" data-testid="cc-live" className="cc-live">{announce}</div>

      {/* 1. CALL BAR */}
      <Card>
        <div className="cc-callbar" role="group" aria-label={t(L.ccReason)}>
          {!interactionId ? (
            <>
              <label htmlFor="cc-reason">{t(L.ccReason)}</label>
              <select id="cc-reason" value={reason} onChange={(e) => setReason(e.target.value)}>
                {CALL_REASONS.map((r) => <option key={r} value={r}>{t(callReasonLabel(r))}</option>)}
              </select>
              {/* Offered BEFORE the call opens, because that is the only moment it can be recorded — the
                  interaction stores it at creation and nothing changes it afterwards. */}
              <label htmlFor="cc-direction">{t(L.ccDirection)}</label>
              <select
                id="cc-direction"
                value={direction}
                onChange={(e) => setDirection(e.target.value as CcDirection)}
              >
                <option value="Inbound">{t(L.ccInbound)}</option>
                <option value="Outbound">{t(L.ccOutbound)}</option>
              </select>
              <Button variant="primary" onClick={startCall}>{t(L.ccStartCall)}</Button>
            </>
          ) : (
            <>
              <StatusChip kind="ok" label={t(L.ccOnCall)} />
              <Button variant="ghost" onClick={closeCall}>{t(L.ccCloseCall)}</Button>
            </>
          )}
        </div>
      </Card>

      {interactionId && (
        <>
          {/* 2. SEARCH — one box, matching name / phone / card or member number / every other identifier.
                 Picking a hit opens the file: the agent confirmed who they are speaking to on the phone, and
                 the click is what records it. */}
          <Card>
            <p className="cc-muted">{t(L.ccOpenFileHelp)}</p>
            <MemberSearch
              query={query}
              onQueryChange={setQuery}
              onSearch={() => void doSearch()}
              results={results}
              onSelect={(m) => void openMember(m)}
            />
          </Card>

          {/* 3. MEMBER 360 — the file, once it is open on this call. */}
          {openedFor && summary && (
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
                {/* Into the unified profile, carrying the interaction this call is bound to — profile-service
                    checks that binding independently (ADR-0026).

                    A react-router <Link>, NOT an <a href>. As a plain anchor this reloaded the whole SPA, which
                    tore down the open call, the search results and the member on screen — so an agent who
                    looked at the caller's profile mid-call came back to an empty workspace while the interaction
                    was still Open on the server. `state.from` is what the profile's Back button returns to. */}
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
                          <Button
                            variant="ghost"
                            aria-label={`${t(L.ccCancel)} — ${fmt.date(a.scheduledStart)}`}
                            leadingIcon={<Icon name="cross" />}
                            onClick={() => { setCancelFor(a.appointmentId); setCancelReason(""); setCancelError(false); }}
                          >
                            {t(L.ccCancel)}
                          </Button>
                        )}
                      </li>
                    ))}
                  </ul>
                  {/*
                    ONE confirmation dialog for the list, opened by whichever row was clicked.

                    Cancelling releases the time and may hand it straight to someone on the waitlist. It is
                    not undoable by clicking again, and the member usually finds out only when they arrive —
                    so it gets a deliberate confirm rather than a second click in a dense list. The reason is
                    a CODE here, not free text: the call centre's cancellations are what the no-show and
                    rebook reports group by, and a typed sentence cannot be counted.
                  */}
                  <Modal
                    open={cancelFor !== null}
                    onOpenChange={(open: boolean) => { if (!open) setCancelFor(null); }}
                    title={t(L.ccCancel)}
                    description={t(L.ccCancelConfirm)}
                    footer={
                      <>
                        {/* "Keep it", not "Cancel": a Cancel button on a cancellation dialog is read by half
                            of operators as "cancel the appointment". */}
                        <Button variant="secondary" onClick={() => setCancelFor(null)}>{t(L.ccKeep)}</Button>
                        <Button variant="danger" onClick={() => cancelFor && cancel(cancelFor)}>{t(L.ccCancel)}</Button>
                      </>
                    }
                  >
                    <div className="cc-field">
                      <span id="cc-cancel-reason">{t(L.ccCancelReason)}</span>
                      <select
                        aria-labelledby="cc-cancel-reason"
                        className="mrs-control"
                        value={cancelReason}
                        onChange={(e) => setCancelReason(e.target.value)}
                      >
                        <option value="">—</option>
                        {CANCEL_REASONS.map((code) => <option key={code} value={code}>{t(CANCEL_REASON_LABELS[code])}</option>)}
                      </select>
                      {cancelError && <span role="alert" className="cc-error">{t(L.ccCancelReasonRequired)}</span>}
                    </div>
                  </Modal>

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

                {/* THE call summary, drafted on the file the agent is reading. This used to be "call notes"
                    here and a separate "call summary" in the wrap-up card below — two boxes, two labels, and
                    only one of them reaching the people who later read the call. They are one field now, and
                    it is the one other roles read.

                    NO section aria-label or <h3> here, unlike the sections above. Those group LISTS and need
                    a name; this is a single labelled field, and wrapping it in a region called "Call summary"
                    put that name on two elements — so `getAllByLabelText(/call summary/i)` found the region
                    and the textarea, which is exactly the ambiguity a screen-reader user would have to
                    resolve. The field labels itself. */}
                <section>
                  <CallSummaryDraft value={wrapSummary} onChange={setWrapSummary} onCopy={copySummary} />
                </section>
              </div>
            </Card>
          )}

          {/* 4. WRAP UP — the outcome, plus the summary when there is no member file to host it (a call that
              never resolved to a member still has to be recorded). */}
          <Card>
            <div className="cc-wrapup" role="group" aria-label={t(L.ccWrapUp)}>
              <label>{t(L.ccOutcome)}
                {/* Localized like every other enum on this screen. These were rendered as raw literals, so an
                    Arabic-portal agent picked their wrap-up from "FollowUpRequired" and "NoAction" — the exact
                    bug already fixed for the identifier types a few cards up. `callOutcomeLabel` existed and
                    was in use in the call-history list below; only this control skipped it. */}
                <select
                  value={outcome}
                  // Changing the outcome can make the summary optional (Abandoned), so a "summary is
                  // required" error left over from a Resolved attempt would be sitting on a form that is
                  // now valid — telling the agent to fix something the server no longer asks for.
                  onChange={(e) => { setOutcome(e.target.value); setSummaryError(false); }}
                >
                  {OUTCOMES.map((o) => <option key={o} value={o}>{t(callOutcomeLabel(o))}</option>)}
                </select>
              </label>

              {/* The summary OTHER ROLES read. Required by the server for every outcome but Abandoned, so it is
                  asked for here rather than discovered as a 422 after the agent thinks they are done.

                  Rendered ONLY when the member file is not already hosting it: it is one field in one piece of
                  state, and two controls carrying the same accessible name is exactly what makes an agent
                  wonder which of them saves. */}
              {!(openedFor && summary) && (
                <InputField
                  label={t(L.ccSummary)}
                  help={t(L.ccSummaryHelp)}
                  value={wrapSummary}
                  onChange={(e) => { setWrapSummary(e.target.value); if (summaryError) setSummaryError(false); }}
                  // `required` drives BOTH aria-required and the visible asterisk (InputField derives
                  // requiredMark from it), and `error` renders icon + text + border with role="alert" and
                  // aria-invalid — the design system's non-colour error contract, rather than a bare red <p>.
                  required={outcome !== "Abandoned"}
                  error={summaryError ? t(L.ccSummaryRequired) : undefined}
                  maxLength={500}
                />
              )}

              {/* The file IS showing the field, so the error has to be shown here too — otherwise a refused
                  close reports "a summary is required" only in the aria-live region, and a sighted agent
                  looking at the wrap-up card sees nothing at all. */}
              {openedFor && summary && summaryError && (
                <p role="alert" className="cc-error">{t(L.ccSummaryRequired)}</p>
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

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  Button, Card, Icon, InlineAlert, Modal, SelectField, StatusChip, TextareaField,
} from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { newIdempotencyKey } from "../api/http";
import { writeErrorMessage } from "../api/writeError";
import type {
  NoteView,
  PolicyApi,
  PolicyDocumentView,
  TimelineEntryView,
} from "../api/policyApi";
import { useFormat } from "../i18n/useFormat";
import { useLoc, readErrorMessage } from "./_shared";
// The preview dialog lives with the documents code — one component, so a policy contract and a member's card
// scan cannot end up rendering (or failing to render) differently.
import { DocumentPreview } from "./BeneficiaryDocuments";

/**
 * Phase 19.6 — the panels shared by the POLICY and MEMBER detail screens: notes, documents, the change
 * timeline, and the accessible-by-construction meter used everywhere a proportion is drawn.
 *
 * These are one component each rather than one per screen on purpose. A note rendered two ways is a note that
 * gets cancelled correctly on one screen and hidden on the other — and "hidden" is the single behaviour the
 * 19.3 design forbids outright.
 */

// ── Bilingual labels ────────────────────────────────────────────────────────────────────────────────────

const S = {
  notes: { en: "Notes", ar: "الملاحظات" },
  addNote: { en: "Add note", ar: "إضافة ملاحظة" },
  noteType: { en: "Note type", ar: "نوع الملاحظة" },
  visibility: { en: "Visibility", ar: "مستوى الاطّلاع" },
  /** Said on the form, because the field decides access and looks like a category. */
  visibilityHint: {
    en: "This decides who can ever read the note. A clinical note is withheld from administrative roles — they see that it exists and not what it says.",
    ar: "يحدد هذا من يمكنه قراءة الملاحظة. تُحجب الملاحظة السريرية عن الأدوار الإدارية — يرون وجودها لا نصّها.",
  },
  body: { en: "Note", ar: "نص الملاحظة" },
  pin: { en: "Pin to top", ar: "تثبيت في الأعلى" },
  save: { en: "Save note", ar: "حفظ الملاحظة" },
  cancelNote: { en: "Cancel note", ar: "إلغاء الملاحظة" },
  cancelReason: { en: "Reason for cancelling (required)", ar: "سبب الإلغاء (مطلوب)" },
  confirmCancel: { en: "Cancel this note", ar: "إلغاء هذه الملاحظة" },
  keep: { en: "Keep it", ar: "الاحتفاظ بها" },
  reasonRequired: { en: "A reason is required to cancel a note.", ar: "السبب مطلوب لإلغاء الملاحظة." },
  bodyRequired: { en: "A note needs a body.", ar: "الملاحظة تحتاج إلى نص." },
  cancelled: { en: "Cancelled", ar: "ملغاة" },
  pinned: { en: "Pinned", ar: "مثبّتة" },
  cancelledBy: { en: "Cancelled by", ar: "أُلغيت بواسطة" },
  restricted: { en: "Restricted — clinical note", ar: "مقيّدة — ملاحظة سريرية" },
  restrictedHint: {
    en: "This note exists and you may not read its body. Ask someone entitled to it rather than assuming nothing was written.",
    ar: "هذه الملاحظة موجودة ولا يمكنك قراءة نصها. اسأل شخصًا مخوّلًا بدلًا من افتراض أنه لم يُكتب شيء.",
  },
  noNotes: { en: "No notes recorded.", ar: "لا توجد ملاحظات مسجّلة." },
  documents: { en: "Documents", ar: "المستندات" },
  noDocuments: { en: "No documents filed.", ar: "لا توجد مستندات." },
  view: { en: "View", ar: "عرض" },
  download: { en: "Download", ar: "تنزيل" },
  locked: { en: "Locked", ar: "مقفل" },
  lockedHint: {
    en: "You may see that this document exists, but not its content.",
    ar: "يمكنك رؤية وجود هذا المستند دون محتواه.",
  },
  withdrawn: { en: "Withdrawn", ar: "مسحوب" },
  expired: { en: "Expired", ar: "منتهي" },
  verified: { en: "Verified", ar: "موثّق" },
  timeline: { en: "Change timeline", ar: "سجل التغييرات" },
  refresh: { en: "Refresh", ar: "تحديث" },
  recordStart: { en: "Where this record begins", ar: "بداية هذا السجل" },
  /** Said on the row, because the reader is entitled to know which lines are projected events and which were
   *  read off the record itself. */
  originDerived: {
    en: "Read from the membership record — no enrolment event was projected for it.",
    ar: "مأخوذ من سجل العضوية — لم يُسجَّل حدث تسجيل مقابل له.",
  },
  noTimeline: { en: "Nothing has changed on this record yet.", ar: "لم يتغيّر شيء في هذا السجل بعد." },
  more: { en: "Load older entries", ar: "تحميل إدخالات أقدم" },
  diffWithheld: { en: "Change detail withheld for your role.", ar: "تفاصيل التغيير محجوبة عن دورك." },
  by: { en: "by", ar: "بواسطة" },
  /** Read out between the two values. The arrow between them is aria-hidden — "Standard arrow Enhanced" is
   *  not a sentence, and a screen reader needs the relationship spoken. */
  changedTo: { en: "changed to", ar: "أصبحت" },
  saving: { en: "Saving…", ar: "جارٍ الحفظ…" },
  noteAdded: { en: "Note saved.", ar: "تم حفظ الملاحظة." },
  noteCancelled: { en: "Note cancelled. It stays visible, struck through.", ar: "أُلغيت الملاحظة. تبقى ظاهرة مشطوبة." },
} satisfies Record<string, Localized>;

const NOTE_TYPES = ["General", "Eligibility", "Exception", "Approval", "Complaint", "Financial", "Clinical", "Administrative"];
const NOTE_TYPE_LABELS: Record<string, Localized> = {
  General: { en: "General", ar: "عامة" },
  Eligibility: { en: "Eligibility", ar: "الأهلية" },
  Exception: { en: "Exception", ar: "استثناء" },
  Approval: { en: "Approval", ar: "موافقة" },
  Complaint: { en: "Complaint", ar: "شكوى" },
  Financial: { en: "Financial", ar: "مالية" },
  Clinical: { en: "Clinical", ar: "سريرية" },
  Administrative: { en: "Administrative", ar: "إدارية" },
};
const VISIBILITIES = ["Administrative", "Financial", "Clinical", "Restricted"];
const VISIBILITY_LABELS: Record<string, Localized> = {
  Administrative: { en: "Administrative", ar: "إدارية" },
  Financial: { en: "Financial", ar: "مالية" },
  Clinical: { en: "Clinical", ar: "سريرية" },
  Restricted: { en: "Restricted", ar: "مقيّدة" },
};

// ── Idempotency ─────────────────────────────────────────────────────────────────────────────────────────

/**
 * One key per FORM INSTANCE, not per submit (phase 18 D1). A key minted at submit time makes every retry a
 * new write, which is the failure the header exists to prevent; a key that never rotates makes the SECOND
 * genuine note a silent replay of the first. So: mint once, rotate only after a success.
 */
export function useIdempotencyKey(): [string, () => void] {
  const [key, setKey] = useState(newIdempotencyKey);
  return [key, useCallback(() => setKey(newIdempotencyKey()), [])];
}

// ── Accessible proportion meter ─────────────────────────────────────────────────────────────────────────

export interface MeterRow {
  label: string;
  consumed: number;
  limit?: number | null;
  /** Rendered verbatim in the data table's value column (already formatted money or a count). */
  valueText: string;
  limitText: string;
}

/**
 * A limit-vs-consumed bar set that ALWAYS renders its data table.
 *
 * The R2 audit finding (U6) was that charts hid their data behind a "show table" toggle defaulting to off,
 * which means a screen-reader user reaches a graphic with no accessible equivalent unless they first find and
 * operate a control they cannot see the purpose of. The table here is in the DOM unconditionally — visually
 * hidden, not conditionally rendered — and the bars are `aria-hidden`, because a decorative duplicate of data
 * already announced is noise.
 */
export function LimitMeters({ caption, rows }: { caption: string; rows: MeterRow[] }) {
  const t = useLoc();
  const headers = {
    category: { en: "Category", ar: "الفئة" },
    used: { en: "Used", ar: "المستخدم" },
    limit: { en: "Limit", ar: "الحد" },
    percent: { en: "% of limit", ar: "٪ من الحد" },
  } satisfies Record<string, Localized>;

  return (
    <div className="pol-meters">
      <div aria-hidden="true">
        {rows.map((r) => {
          const pct = r.limit && r.limit > 0 ? Math.min(100, (r.consumed / r.limit) * 100) : null;
          // Threshold is stated in TEXT on the row as well as by width — colour and length alone are not a
          // status (0A §5.2).
          const kind = pct === null ? "neu" : pct >= 100 ? "bad" : pct >= 80 ? "warn" : "ok";
          return (
            <div key={r.label} className="pol-meter">
              <div className="pol-meter-head">
                <span>{r.label}</span>
                <span className="pol-meter-val">
                  {r.valueText} / {r.limitText}
                </span>
              </div>
              <div className="pol-meter-track">
                <div className={`pol-meter-fill ${kind}`} style={{ width: `${pct ?? 0}%` }} />
              </div>
            </div>
          );
        })}
      </div>
      {/* `mini-table sr-only`, matching the chart alternative on the executive dashboard: hidden today, but
          a table that is only ever one prop away from being shown should not be the one table with no
          treatment at all. */}
      <table className="mini-table sr-only">
        <caption>{caption}</caption>
        <thead>
          <tr>
            <th scope="col">{t(headers.category)}</th>
            <th scope="col">{t(headers.used)}</th>
            <th scope="col">{t(headers.limit)}</th>
            <th scope="col">{t(headers.percent)}</th>
          </tr>
        </thead>
        <tbody>
          {rows.map((r) => (
            <tr key={r.label}>
              <th scope="row">{r.label}</th>
              <td>{r.valueText}</td>
              <td>{r.limitText}</td>
              <td>{r.limit && r.limit > 0 ? `${Math.round((r.consumed / r.limit) * 100)}%` : "—"}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

// ── Notes ───────────────────────────────────────────────────────────────────────────────────────────────

export interface NotesPanelProps {
  api: PolicyApi;
  scope: "policies" | "enrollments";
  scopeRef: string;
  /** False on a read-only surface (a superseded record, or a role without write rights). */
  canAdd?: boolean;
}

export function NotesPanel({ api, scope, scopeRef, canAdd = true }: NotesPanelProps) {
  const t = useLoc();
  const fmt = useFormat();
  const [notes, setNotes] = useState<NoteView[] | null>(null);
  const [loadError, setLoadError] = useState<Localized | null>(null);
  const [noteType, setNoteType] = useState(NOTE_TYPES[0]);
  const [visibility, setVisibility] = useState(VISIBILITIES[0]);
  const [body, setBody] = useState("");
  const [pinned, setPinned] = useState(false);
  const [busy, setBusy] = useState(false);
  const [formError, setFormError] = useState<Localized | null>(null);
  const [announce, setAnnounce] = useState("");
  const [composing, setComposing] = useState(false);
  const [cancelling, setCancelling] = useState<NoteView | null>(null);
  const [cancelReason, setCancelReason] = useState("");
  const [cancelError, setCancelError] = useState<Localized | null>(null);
  const [addKey, rotateAddKey] = useIdempotencyKey();
  const [cancelKey, rotateCancelKey] = useIdempotencyKey();

  const load = useCallback(async () => {
    try {
      setNotes(await api.notes(scope, scopeRef));
      setLoadError(null);
    } catch (e) {
      setLoadError(readErrorMessage(e));
    }
  }, [api, scope, scopeRef]);

  useEffect(() => {
    void load();
  }, [load]);

  /** Pinned first, then newest first. Sorted here rather than trusted from the wire so two panels agree. */
  const ordered = useMemo(() => {
    if (!notes) return [];
    return [...notes].sort((a, b) => {
      if (a.pinned !== b.pinned) return a.pinned ? -1 : 1;
      return b.authoredAt.localeCompare(a.authoredAt);
    });
  }, [notes]);

  async function submit() {
    if (!body.trim()) {
      setFormError(S.bodyRequired);
      return;
    }
    setBusy(true);
    setFormError(null);
    try {
      await api.addNote(scope, scopeRef, { noteType, body: body.trim(), visibilityClass: visibility, pinned }, addKey);
      rotateAddKey();
      setBody("");
      setPinned(false);
      setComposing(false);
      setAnnounce(t(S.noteAdded));
      await load();
    } catch (e) {
      setFormError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  async function confirmCancel() {
    if (!cancelling) return;
    if (!cancelReason.trim()) {
      setCancelError(S.reasonRequired);
      return;
    }
    setBusy(true);
    setCancelError(null);
    try {
      await api.cancelNote(cancelling.noteId, cancelReason.trim(), cancelKey);
      rotateCancelKey();
      setCancelling(null);
      setCancelReason("");
      setAnnounce(t(S.noteCancelled));
      await load();
    } catch (e) {
      setCancelError(writeErrorMessage(e).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card className="pol-panel" data-testid="notes-panel">
      {/*
        * ============================================================================================================
        * THE NOTES ARE THE PANEL. COMPOSING ONE IS A MODAL.
        * ============================================================================================================
        * The form used to sit permanently above the list: three controls, a checkbox and a primary button, so
        * opening the tab showed an empty form and pushed the notes — the thing somebody came to read — below
        * the fold. Reading is the common case by a wide margin; writing is occasional and deserves the room a
        * modal gives it, not the top of every visit.
        */}
      <div className="pol-panel-head">
        <h3>{t(S.notes)}</h3>
        {canAdd && (
          <Button
            variant="primary"
            size="sm"
            leadingIcon={<Icon name="plus" />}
            onClick={() => { setComposing(true); setFormError(null); }}
            aria-haspopup="dialog"
            data-testid="add-note"
          >
            {t(S.addNote)}
          </Button>
        )}
      </div>
      <div aria-live="polite" role="status" className="sr-only">
        {announce}
      </div>

      {composing && (
        <Modal
          open
          onOpenChange={(o) => !o && !busy && setComposing(false)}
          title={t(S.addNote)}
          closeLabel={t(S.keep)}
          footer={
            <>
              <Button variant="ghost" onClick={() => setComposing(false)} disabled={busy}>{t(S.keep)}</Button>
              {/* No optimistic UI: the note is not in the list until the server says it is (phase 18 D1). */}
              <Button variant="primary" onClick={submit} loading={busy} disabled={busy}>
                {busy ? t(S.saving) : t(S.save)}
              </Button>
            </>
          }
        >
          <div className="pol-note-form">
            <SelectField
              id="note-type"
              label={t(S.noteType)}
              value={noteType}
              onChange={setNoteType}
              options={NOTE_TYPES.map((n) => ({ value: n, label: t(NOTE_TYPE_LABELS[n]) }))}
            />
            {/*
              * Visibility decides WHO can ever read this body — see NoteVisibilityRules. It is the most
              * consequential field on the form and it was a bare, unstyled select; it now carries a hint
              * saying what the choice does, because "Financial" and "Clinical" look like categories and
              * behave like access control.
              */}
            <SelectField
              id="note-visibility"
              label={t(S.visibility)}
              help={t(S.visibilityHint)}
              value={visibility}
              onChange={setVisibility}
              options={VISIBILITIES.map((v) => ({ value: v, label: t(VISIBILITY_LABELS[v]) }))}
            />
            <TextareaField label={t(S.body)} value={body} onChange={(e) => setBody(e.target.value)} rows={5} />
            <label className="pol-check">
              <input type="checkbox" className="mrs-checkbox" checked={pinned} onChange={(e) => setPinned(e.target.checked)} />
              {t(S.pin)}
            </label>
            {formError && <InlineAlert tone="bad">{t(formError)}</InlineAlert>}
          </div>
        </Modal>
      )}

      {loadError && <InlineAlert tone="bad">{t(loadError)}</InlineAlert>}
      {notes && ordered.length === 0 && <InlineAlert tone="info">{t(S.noNotes)}</InlineAlert>}

      <ul className="pol-notes">
        {ordered.map((n) => {
          const isCancelled = n.status === "Cancelled";
          return (
            <li key={n.noteId} className={isCancelled ? "pol-note cancelled" : "pol-note"} data-testid="note-item">
              <div className="pol-note-meta">
                <StatusChip kind="neu" label={t(NOTE_TYPE_LABELS[n.noteType] ?? { en: n.noteType, ar: n.noteType })} />
                {n.pinned && <StatusChip kind="info" label={t(S.pinned)} />}
                {/* The four-cue cancelled treatment: neutral hue + icon + ghost pill + the WORD. A cancelled
                    note is never hidden — 19.3's whole argument is that a record which quietly loses a note
                    is worse than one that shows a struck-through note somebody withdrew. */}
                {isCancelled && <StatusChip kind="neu" label={t(S.cancelled)} />}
                {/*
                  * The NAME, falling back to the subject. It rendered `authoredByUsername` alone, which on a
                  * note written through the portal is a uuid — so every note on the record was signed
                  * `e77f18c6-819c-4910-8b94-4a6872fbb9b2`. Both are snapshots taken at write time, so a
                  * signature survives the author being renamed or de-provisioned; the display one is the
                  * half a human can read.
                  */}
                <span className="pol-note-author">{n.authoredByDisplay || n.authoredByUsername}</span>
                <span className="pol-note-time">{fmt.dateTime(n.authoredAt)}</span>
              </div>

              {n.bodyWithheld ? (
                <div className="pol-locked" data-testid="note-withheld">
                  <Icon name="info" />
                  <div>
                    <strong>{t(S.restricted)}</strong>
                    <p>{n.withheldReason ?? t(S.restrictedHint)}</p>
                  </div>
                </div>
              ) : (
                <p className="pol-note-body">{n.body}</p>
              )}

              {isCancelled && (
                <p className="pol-note-cancel" data-testid="note-cancellation">
                  {t(S.cancelledBy)} <strong>{n.cancelledByUsername}</strong> · {fmt.dateTime(n.cancelledAt)} ·{" "}
                  {n.cancellationReason}
                </p>
              )}

              {/* `canCancel` comes from the server, which knows whether this caller is the author or holds
                  the supervisory scope. Deriving it here from the username would offer a button the API
                  refuses. */}
              {!isCancelled && n.canCancel && (
                <Button variant="ghost" onClick={() => { setCancelling(n); setCancelReason(""); setCancelError(null); }}>
                  {t(S.cancelNote)}
                </Button>
              )}
            </li>
          );
        })}
      </ul>

      {/*
        * THE DESIGN SYSTEM'S MODAL, not a div claiming to be one (2026-08-09 audit §3).
        *
        * This was `<div role="dialog" aria-modal="true">` with no scrim, no focus trap, no Escape and no
        * focus restore. `aria-modal="true"` is an ASSERTION to assistive technology that everything outside
        * this element is inert — and it was false: Tab walked straight out into the note list behind, where a
        * screen reader then read content its own API had just been told was hidden. A dialog that lies about
        * that is worse than a plain inline form, which at least does not.
        *
        * The composer twenty lines above already used `Modal`. Same file, same panel, two answers.
        */}
      {cancelling && (
        <Modal
          open
          onOpenChange={(o) => !o && !busy && setCancelling(null)}
          title={t(S.cancelNote)}
          closeLabel={t(S.keep)}
          footer={
            <>
              <Button variant="ghost" onClick={() => setCancelling(null)} disabled={busy}>
                {t(S.keep)}
              </Button>
              <Button variant="primary" onClick={confirmCancel} loading={busy} disabled={busy}>
                {t(S.confirmCancel)}
              </Button>
            </>
          }
        >
          <div className="pol-dialog">
            <TextareaField
              label={t(S.cancelReason)}
              value={cancelReason}
              onChange={(e) => setCancelReason(e.target.value)}
              rows={2}
            />
            {cancelError && <InlineAlert tone="bad">{t(cancelError)}</InlineAlert>}
          </div>
        </Modal>
      )}
    </Card>
  );
}

// ── Documents ───────────────────────────────────────────────────────────────────────────────────────────

export function DocumentsPanel({
  api,
  scope,
  scopeRef,
}: {
  api: PolicyApi;
  scope: "policies" | "enrollments";
  scopeRef: string;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const [docs, setDocs] = useState<PolicyDocumentView[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [preview, setPreview] = useState<{ doc: PolicyDocumentView; url: string } | null>(null);

  useEffect(() => {
    let live = true;
    api
      .documents(scope, scopeRef)
      .then((d) => live && setDocs(d))
      .catch((e) => live && setError(writeErrorMessage(e).message));
    return () => {
      live = false;
    };
  }, [api, scope, scopeRef]);

  /**
   * LOOKING AND TAKING ARE DIFFERENT ACTS.
   *
   * Both resolve a short-TTL signed URL through the same audited endpoint, and they send a different
   * `purpose` — so a year later the record can distinguish somebody who glanced at a contract from somebody
   * who took a copy of it. This panel offered only the second: every read of a policy document was filed as a
   * download, which is the heavier of the two disclosures, and reading one in place meant taking one.
   */
  async function open(doc: PolicyDocumentView, purpose: "preview" | "download") {
    try {
      const { url } = await api.documentDownloadUrl(doc.linkId, purpose);
      if (purpose === "download") window.open(url, "_blank", "noopener,noreferrer");
      else setPreview({ doc, url });
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }

  return (
    <Card className="pol-panel" data-testid="documents-panel">
      <h3>{t(S.documents)}</h3>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {docs && docs.length === 0 && <InlineAlert tone="info">{t(S.noDocuments)}</InlineAlert>}
      <ul className="pol-docs">
        {(docs ?? []).map((d) => (
          <li key={d.linkId} className="pol-doc">
            <div className="pol-doc-meta">
              <strong>{d.title}</strong>
              <StatusChip kind="neu" label={d.documentClass} />
              {d.status === "Withdrawn" && <StatusChip kind="bad" label={t(S.withdrawn)} />}
              {d.expired && <StatusChip kind="warn" label={t(S.expired)} />}
              {d.verifiedAt && <StatusChip kind="ok" label={t(S.verified)} />}
              <span>
                v{d.versionNo} · {d.uploadedByUsername} · {fmt.dateTime(d.uploadedAt)}
              </span>
            </div>
            {/* Listing and downloading are different authorities (19.3b). Everyone entitled to the record
                sees that a document EXISTS; fetching the bytes is narrower and always audited. */}
            {d.canDownload ? (
              <div className="pol-doc-actions">
                {/* Icon-only, so each carries a name that includes the DOCUMENT — "View" alone in a list of
                    nine tells a screen-reader user nothing about which one they are on. */}
                <Button
                  variant="ghost"
                  size="sm"
                  aria-label={`${t(S.view)} — ${d.title}`}
                  onClick={() => void open(d, "preview")}
                >
                  <Icon name="eye" aria-hidden />
                </Button>
                <Button
                  variant="ghost"
                  size="sm"
                  aria-label={`${t(S.download)} — ${d.title}`}
                  onClick={() => void open(d, "download")}
                >
                  <Icon name="download" aria-hidden />
                </Button>
              </div>
            ) : (
              <span className="pol-locked-inline" title={t(S.lockedHint)}>
                <Icon name="info" /> {t(S.locked)}
              </span>
            )}
          </li>
        ))}
      </ul>

      {preview && (
        <DocumentPreview doc={preview.doc} url={preview.url} onClose={() => setPreview(null)} />
      )}
    </Card>
  );
}

// ── Change timeline ─────────────────────────────────────────────────────────────────────────────────────

export function ChangeTimeline({
  api,
  scope,
  scopeRef,
  lang,
  reloadToken,
}: {
  api: PolicyApi;
  scope: "policies" | "enrollments";
  scopeRef: string;
  lang: "en" | "ar";
  /** Bumped by the screen after any write against this record. The panel is mounted for as long as the tab is
   *  open, so without it a plan changed from the card above never reached the history below. */
  reloadToken?: number;
}) {
  const t = useLoc();
  const [entries, setEntries] = useState<TimelineEntryView[]>([]);
  const [origin, setOrigin] = useState<TimelineEntryView | null>(null);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  const loadedFor = useRef<string>("");

  const fetchPage = useCallback(
    async (from?: string) => {
      setBusy(true);
      try {
        const page = await api.timeline(scope, scopeRef, from);
        // A reload REPLACES the run — appending would show yesterday's page under today's and repeat every
        // entry the first page still holds.
        setEntries((prev) => (from ? [...prev, ...page.entries] : page.entries));
        // Only the first page carries the anchor, and only the first page may replace it — a later page must
        // not blank out the origin the reader is already looking at.
        if (!from) setOrigin(page.origin ?? null);
        setCursor(page.nextCursor ?? null);
        setError(null);
      } catch (e) {
        setError(readErrorMessage(e));
      } finally {
        setBusy(false);
      }
    },
    [api, scope, scopeRef],
  );

  useEffect(() => {
    // The token includes reloadToken, so a write on the record re-runs this effect. The ref still guards the
    // double-invoke of StrictMode and any re-render that does not change what is being shown.
    const token = `${scope}:${scopeRef}:${reloadToken ?? 0}`;
    if (loadedFor.current === token) return;
    loadedFor.current = token;
    void fetchPage();
  }, [fetchPage, scope, scopeRef, reloadToken]);

  /**
   * NEWEST FIRST, always — sorted here rather than trusted from the wire.
   *
   * The service orders by `occurred_at DESC`, but the panel accumulates pages and the anchor is spliced back
   * in below; one client-side sort is what guarantees the rule holds for whatever ends up in the array. The
   * anchor is filtered out by id as well: the service drops it from the first page, and paging far enough
   * back would otherwise fetch it a second time and render the enrolment twice.
   */
  const ordered = useMemo(
    () =>
      entries
        .filter((e) => e.entryId !== origin?.entryId)
        .sort((a, b) => b.occurredAt.localeCompare(a.occurredAt)),
    [entries, origin],
  );

  return (
    <Card className="pol-panel" data-testid="timeline-panel">
      <div className="pol-panel-head">
        <h3>{t(S.timeline)}</h3>
        {/* A history is written by other people too. The panel loads when the tab opens and after a write on
            this screen; this is how a reader gets today's entry from somebody else's desk without reloading
            the application. */}
        <Button
          variant="ghost"
          size="sm"
          leadingIcon={<Icon name="undo" />}
          onClick={() => void fetchPage()}
          loading={busy}
          data-testid="timeline-refresh"
        >
          {t(S.refresh)}
        </Button>
      </div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {ordered.length === 0 && !origin && !error && <InlineAlert tone="info">{t(S.noTimeline)}</InlineAlert>}
      <ol className="pol-timeline">
        {ordered.map((e) => (
          <li key={e.entryId}>
            <TimelineRow e={e} lang={lang} />
          </li>
        ))}

        {/* Older entries load INTO the run, above the anchor — putting the control below the record's
            creation would read as "load something from before this record existed". */}
        {cursor && (
          <li className="pol-timeline-more">
            <Button variant="ghost" onClick={() => fetchPage(cursor)}>{t(S.more)}</Button>
          </li>
        )}

        {/*
          * THE OLDEST LINE IS ALWAYS THE CREATION, AND IT IS ALWAYS HERE.
          *
          * The run reads newest-first, so the record's beginning belongs at the BOTTOM — that is where the
          * chronology puts it. What the anchor changes is that it is present at all: the page is cursor-paged,
          * so the creation was behind however many "load older" clicks the record had earned, and on records
          * enrolled before the projection existed (all of them, in the dev database) it was not reachable at
          * any depth. The history simply began mid-sentence.
          */}
        {origin && (
          <li className="pol-timeline-origin" data-testid="timeline-origin">
            <span className="pol-timeline-eyebrow">{t(S.recordStart)}</span>
            <TimelineRow e={origin} lang={lang} />
            {origin.derived && <p className="pol-muted">{t(S.originDerived)}</p>}
          </li>
        )}
      </ol>
    </Card>
  );
}

/** One entry, wherever it sits: inside the paged run or pulled out as the record's origin. Both render
 *  through here so the anchor cannot drift into being a second, differently-shaped kind of log line. */
function TimelineRow({ e, lang }: { e: TimelineEntryView; lang: "en" | "ar" }) {
  const t = useLoc();
  const fmt = useFormat();
  return (
    <>
      <div className="pol-timeline-head">
        <StatusChip kind="neu" label={e.eventCategory} />
        <time dateTime={e.occurredAt}>{fmt.dateTime(e.occurredAt)}</time>
        {/*
          * WHO. The guard used to be `e.actorDisplay &&` while the value rendered was
          * `actorUsername ?? actorDisplay` — so an entry with a username and no display name showed no
          * actor at all, which is every entry policy-service writes. "Somebody changed this member's
          * plan" is not a log.
          *
          * Display name first now, subject second: the name is what a person recognises, and the
          * subject is a uuid that answers the question only for whoever can resolve it. Both are
          * snapshots taken at write time, so a renamed or de-provisioned user still signs their change.
          */}
        {(e.actorDisplay || e.actorUsername) && (
          <span data-testid="timeline-actor">
            {t(S.by)} {e.actorDisplay ?? e.actorUsername}
          </span>
        )}
      </div>
      {/* The service authors BOTH summaries; the client picks, it does not translate. */}
      <p>{lang === "ar" ? e.summaryAr : e.summaryEn}</p>
      <ChangeDiff json={e.changeDiff} />
      {e.diffWithheld && <p className="pol-muted">{t(S.diffWithheld)}</p>}
    </>
  );
}

/**
 * WHAT CHANGED — the field, the value it held, and the value it holds now.
 *
 * ============================================================================================================
 * THE SERVER HAS ALWAYS SENT THIS
 * ============================================================================================================
 * `changeDiff` has been on every timeline entry since 19.3c, minimized to the fields that actually moved and
 * projected by role at read time. The panel rendered the summary sentence and dropped it — so the Logs tab
 * said "Member moved to another plan" and could not say which plan, from which plan, and the one question a
 * log exists to answer had to be taken to the audit trail by someone with `audit:read`.
 *
 * ============================================================================================================
 * PARSED DEFENSIVELY
 * ============================================================================================================
 * It arrives as a JSON string. Anything that is not an object of {before, after} pairs is skipped rather than
 * rendered raw: a history panel is not the place to discover that an upstream projection changed shape, and a
 * blob of JSON in front of an officer is worse than the summary alone.
 */
function ChangeDiff({ json }: { json?: string | null }) {
  const t = useLoc();
  const changes = useMemo(() => parseDiff(json), [json]);
  if (changes.length === 0) return null;

  return (
    <dl className="pol-diff-fields" data-testid="timeline-diff">
      {changes.map((change) => (
        <div key={change.field}>
          <dt>{t(fieldLabel(change.field))}</dt>
          <dd>
            {/* Cleared and set are different events and read differently: "Standard → Enhanced",
                "— → Enhanced", "12 Mar 2026 → —". The dash is the value's absence, not a missing field. */}
            <span className="pol-diff-before">{change.before ?? "—"}</span>
            <span aria-hidden="true"> → </span>
            <span className="sr-only">{t(S.changedTo)} </span>
            <span className="pol-diff-after">{change.after ?? "—"}</span>
          </dd>
        </div>
      ))}
    </dl>
  );
}

function parseDiff(json?: string | null): { field: string; before: string | null; after: string | null }[] {
  if (!json) return [];
  try {
    const parsed: unknown = JSON.parse(json);
    if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return [];
    return Object.entries(parsed as Record<string, unknown>).flatMap(([field, value]) => {
      if (typeof value !== "object" || value === null) return [];
      const pair = value as { before?: unknown; after?: unknown };
      const text = (v: unknown) => (v === null || v === undefined ? null : String(v));
      return [{ field, before: text(pair.before), after: text(pair.after) }];
    });
  } catch {
    return [];
  }
}

/**
 * Bilingual labels for the fields a diff names.
 *
 * The fallback is the raw key, deliberately: a field a newer service starts recording shows up as itself
 * rather than disappearing, and a history with a line missing is worse than one with an untranslated label.
 */
function fieldLabel(field: string): Localized {
  const labels: Record<string, Localized> = {
    status: { en: "Status", ar: "الحالة" },
    plan: { en: "Plan", ar: "الخطة" },
    group: { en: "Group", ar: "المجموعة" },
    relationship: { en: "Relationship", ar: "صلة القرابة" },
    effectiveFrom: { en: "Cover from", ar: "التغطية من" },
    effectiveDate: { en: "Effective date", ar: "تاريخ السريان" },
    coveredUntil: { en: "Covered until", ar: "التغطية حتى" },
    givenName: { en: "Given name", ar: "الاسم الأول" },
    middleName: { en: "Middle name", ar: "الاسم الأوسط" },
    familyName: { en: "Family name", ar: "اسم العائلة" },
    birthDate: { en: "Date of birth", ar: "تاريخ الميلاد" },
    birthDateIsApproximate: { en: "Birth date is approximate", ar: "تاريخ الميلاد تقريبي" },
    sex: { en: "Sex", ar: "النوع" },
    nationalityCode: { en: "Nationality", ar: "الجنسية" },
    individualNo: { en: "Individual no.", ar: "رقم الفرد" },
    caseNo: { en: "Case no.", ar: "رقم الحالة" },
  };
  return labels[field] ?? { en: field, ar: field };
}

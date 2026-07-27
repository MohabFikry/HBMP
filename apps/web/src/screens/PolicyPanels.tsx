import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Button, Card, Icon, InlineAlert, StatusChip, TextareaField } from "@mersal/design-system";
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
import { useLoc } from "./_shared";

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
  noTimeline: { en: "Nothing has changed on this record yet.", ar: "لم يتغيّر شيء في هذا السجل بعد." },
  more: { en: "Load older entries", ar: "تحميل إدخالات أقدم" },
  diffWithheld: { en: "Change detail withheld for your role.", ar: "تفاصيل التغيير محجوبة عن دورك." },
  by: { en: "by", ar: "بواسطة" },
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
      <table className="sr-only">
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
      setLoadError(writeErrorMessage(e).message);
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
      <h3>{t(S.notes)}</h3>
      <div aria-live="polite" role="status" className="sr-only">
        {announce}
      </div>

      {canAdd && (
        <div className="pol-note-form">
          <label htmlFor="note-type">{t(S.noteType)}</label>
          <select id="note-type" value={noteType} onChange={(e) => setNoteType(e.target.value)}>
            {NOTE_TYPES.map((n) => (
              <option key={n} value={n}>
                {t(NOTE_TYPE_LABELS[n])}
              </option>
            ))}
          </select>
          <label htmlFor="note-visibility">{t(S.visibility)}</label>
          <select id="note-visibility" value={visibility} onChange={(e) => setVisibility(e.target.value)}>
            {VISIBILITIES.map((v) => (
              <option key={v} value={v}>
                {t(VISIBILITY_LABELS[v])}
              </option>
            ))}
          </select>
          <TextareaField label={t(S.body)} value={body} onChange={(e) => setBody(e.target.value)} rows={3} />
          <label className="pol-check">
            <input type="checkbox" checked={pinned} onChange={(e) => setPinned(e.target.checked)} />
            {t(S.pin)}
          </label>
          {formError && <InlineAlert tone="bad">{t(formError)}</InlineAlert>}
          {/* No optimistic UI: the note is not in the list until the server says it is (phase 18 D1). */}
          <Button variant="primary" onClick={submit} disabled={busy}>
            {busy ? t(S.saving) : t(S.save)}
          </Button>
        </div>
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
                <span className="pol-note-author">{n.authoredByUsername}</span>
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

      {cancelling && (
        <div className="pol-dialog" role="dialog" aria-modal="true" aria-label={t(S.cancelNote)}>
          <TextareaField
            label={t(S.cancelReason)}
            value={cancelReason}
            onChange={(e) => setCancelReason(e.target.value)}
            rows={2}
          />
          {cancelError && <InlineAlert tone="bad">{t(cancelError)}</InlineAlert>}
          <div className="pol-dialog-actions">
            <Button variant="primary" onClick={confirmCancel} disabled={busy}>
              {t(S.confirmCancel)}
            </Button>
            <Button variant="ghost" onClick={() => setCancelling(null)}>
              {t(S.keep)}
            </Button>
          </div>
        </div>
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

  async function download(linkId: string) {
    try {
      const { url } = await api.documentDownloadUrl(linkId);
      window.open(url, "_blank", "noopener,noreferrer");
    } catch (e) {
      setError(writeErrorMessage(e).message);
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
              <Button variant="secondary" onClick={() => download(d.linkId)}>
                {t(S.download)}
              </Button>
            ) : (
              <span className="pol-locked-inline" title={t(S.lockedHint)}>
                <Icon name="info" /> {t(S.locked)}
              </span>
            )}
          </li>
        ))}
      </ul>
    </Card>
  );
}

// ── Change timeline ─────────────────────────────────────────────────────────────────────────────────────

export function ChangeTimeline({
  api,
  scope,
  scopeRef,
  lang,
}: {
  api: PolicyApi;
  scope: "policies" | "enrollments";
  scopeRef: string;
  lang: "en" | "ar";
}) {
  const t = useLoc();
  const fmt = useFormat();
  const [entries, setEntries] = useState<TimelineEntryView[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const loadedFor = useRef<string>("");

  const fetchPage = useCallback(
    async (from?: string) => {
      try {
        const page = await api.timeline(scope, scopeRef, from);
        setEntries((prev) => (from ? [...prev, ...page.entries] : page.entries));
        setCursor(page.nextCursor ?? null);
      } catch (e) {
        setError(writeErrorMessage(e).message);
      }
    },
    [api, scope, scopeRef],
  );

  useEffect(() => {
    const token = `${scope}:${scopeRef}`;
    if (loadedFor.current === token) return;
    loadedFor.current = token;
    void fetchPage();
  }, [fetchPage, scope, scopeRef]);

  return (
    <Card className="pol-panel" data-testid="timeline-panel">
      <h3>{t(S.timeline)}</h3>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {entries.length === 0 && !error && <InlineAlert tone="info">{t(S.noTimeline)}</InlineAlert>}
      <ol className="pol-timeline">
        {entries.map((e) => (
          <li key={e.entryId}>
            <div className="pol-timeline-head">
              <StatusChip kind="neu" label={e.eventCategory} />
              <time dateTime={e.occurredAt}>{fmt.dateTime(e.occurredAt)}</time>
              {e.actorDisplay && (
                <span>
                  {t(S.by)} {e.actorUsername ?? e.actorDisplay}
                </span>
              )}
            </div>
            {/* The service authors BOTH summaries; the client picks, it does not translate. */}
            <p>{lang === "ar" ? e.summaryAr : e.summaryEn}</p>
            {e.diffWithheld && <p className="pol-muted">{t(S.diffWithheld)}</p>}
          </li>
        ))}
      </ol>
      {cursor && (
        <Button variant="ghost" onClick={() => fetchPage(cursor)}>
          {t(S.more)}
        </Button>
      )}
    </Card>
  );
}

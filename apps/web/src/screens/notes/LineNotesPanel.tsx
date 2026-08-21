import { useCallback, useEffect, useState } from "react";
import { Button, ComboboxField, Icon, InlineAlert, TextareaField, useToast } from "@mersal/design-system";
import type { LineNote, LineNoteKind, Localized, NoteVisibility } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useLoc } from "../_shared";

/**
 * Notes on an order or prescription line (design 46 §7b).
 *
 * ============================================================================================================
 * ONE PANEL, FOUR ORDER KINDS
 * ============================================================================================================
 * Labs, radiology, procedures and prescriptions all render this. Doc 46 §7b requires exactly that and gives
 * the reason: "A second notes mechanism means two behaviours for 'cancel a note' and two answers to 'who can
 * read this'." The server side is the same model on four subjects; this is its one reader.
 *
 * <b>Why not the policy NotesPanel.</b> Doc 38 §5's panel is the same MODEL — append-only, signed,
 * cancellable-not-deletable, class-projected — and the server side reuses it. Its component cannot be reused
 * as-is because `NoteView` carries `noteType` and `pinned`, which are policy-administration concepts: an
 * order line has no note type and nothing to pin. Bending that shape onto a clinical order would have meant
 * either dead controls or a widened contract, and the doc's concern is one MECHANISM, not one component
 * serving two vocabularies.
 *
 * ============================================================================================================
 * WHAT THE READER SEES IS NOT THIS COMPONENT'S DECISION
 * ============================================================================================================
 * The class projection happens server-side, before serialization: a note this caller may not read never
 * reaches the payload. So there is no filtering here, and there must not be — "the screen does not show it"
 * is not a control.
 *
 * What the component does decide is what may be WRITTEN. A fulfiller writes `FromFulfiller` and nothing
 * else, because letting a lab or a pharmacy write `ToFulfiller` would put words in the ordering clinician's
 * mouth on a surface that reads as clinical instruction. The server enforces it too (403
 * `provider-note-class`); offering the choice and then refusing it would be a worse screen.
 */

const S = {
  title: { en: "Notes", ar: "الملاحظات" },
  add: { en: "Add note", ar: "إضافة ملاحظة" },
  save: { en: "Save note", ar: "حفظ الملاحظة" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  body: { en: "Note", ar: "الملاحظة" },
  hint: {
    en: "An operational instruction — \"fasting sample\", \"left knee\", \"syrup if available\". Clinical "
      + "findings belong in the encounter note: anything written here sits outside the record the next "
      + "clinician reads.",
    ar: "تعليمات تشغيلية — «عينة صائم»، «الركبة اليسرى»، «شراب إن توفر». النتائج الإكلينيكية مكانها ملاحظة "
      + "اللقاء: ما يُكتب هنا يقع خارج السجل الذي يقرأه الطبيب التالي.",
  },
  visibility: { en: "Who can read this", ar: "من يمكنه القراءة" },
  visToFulfiller: { en: "The provider filling this order", ar: "مقدّم الخدمة المنفّذ" },
  visInternal: { en: "Internal clinical roles only", ar: "الأدوار الإكلينيكية الداخلية فقط" },
  visFromFulfiller: { en: "Reply to the ordering clinician", ar: "الرد على الطبيب الطالب" },
  empty: { en: "No notes on this line.", ar: "لا توجد ملاحظات على هذا السطر." },
  loadFailed: {
    en: "The notes could not be loaded. This is NOT a report that there are none.",
    ar: "تعذّر تحميل الملاحظات. هذا ليس تقريرًا بعدم وجودها.",
  },
  saveFailed: { en: "The note could not be saved. Try again.", ar: "تعذّر حفظ الملاحظة. حاول مرة أخرى." },
  bodyRequired: { en: "Write the note before saving.", ar: "اكتب الملاحظة قبل الحفظ." },
  tooLong: { en: "A note is capped at 500 characters.", ar: "الحد الأقصى للملاحظة 500 حرف." },
  saved: { en: "Note saved.", ar: "تم حفظ الملاحظة." },
  withdraw: { en: "Withdraw", ar: "سحب" },
  withdrawReason: { en: "Why is it being withdrawn?", ar: "سبب السحب؟" },
  withdrawn: { en: "Withdrawn", ar: "مسحوبة" },
  reasonRequired: {
    en: "A reason is required — the note stays visible, struck through, with this beside it.",
    ar: "السبب مطلوب — تبقى الملاحظة ظاهرة مشطوبة، مع هذا السبب بجوارها.",
  },
} satisfies Record<string, Localized>;

const VISIBILITY_LABEL: Record<NoteVisibility, Localized> = {
  ToFulfiller: S.visToFulfiller,
  Internal: S.visInternal,
  FromFulfiller: S.visFromFulfiller,
};

export function LineNotesPanel({
  kind, orderId, lineId, lineLabel, asFulfiller = false, canWrite = true,
}: {
  kind: LineNoteKind;
  orderId: string;
  lineId: string;
  /**
   * What this line IS, folded into the panel's accessible name.
   *
   * <p>A prescription has one of these panels per line, and a labelled section is a landmark: five lines
   * gave five landmarks all called "Notes", which axe refuses (landmark-unique) and which is unusable by
   * anyone navigating by region. The same reasoning the substitute button in this table already follows —
   * one identically-named control per row is not a name.</p>
   */
  lineLabel?: string;
  /** True on a provider queue. Restricts the writable class to FromFulfiller, as the server does. */
  asFulfiller?: boolean;
  canWrite?: boolean;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [notes, setNotes] = useState<LineNote[] | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [composing, setComposing] = useState(false);
  const [body, setBody] = useState("");
  const [visibility, setVisibility] = useState<NoteVisibility>(asFulfiller ? "FromFulfiller" : "ToFulfiller");
  const [error, setError] = useState<Localized | null>(null);
  const [busy, setBusy] = useState(false);
  const [withdrawing, setWithdrawing] = useState<string | null>(null);
  const [reason, setReason] = useState("");

  const load = useCallback(async () => {
    try {
      setNotes(await api.lineNotes(kind, orderId, lineId));
      setLoadError(false);
    } catch {
      // A failed read is not an empty list. Rendering "No notes on this line" here would tell a pharmacist
      // there is no instruction when there may be one they are about to act against.
      setLoadError(true);
    }
  }, [api, kind, orderId, lineId]);

  useEffect(() => { void load(); }, [load]);

  const writable: NoteVisibility[] = asFulfiller ? ["FromFulfiller"] : ["ToFulfiller", "Internal"];

  async function save() {
    const text = body.trim();
    if (!text) { setError(S.bodyRequired); return; }
    if (text.length > 500) { setError(S.tooLong); return; }
    setBusy(true);
    try {
      await api.writeLineNote(kind, orderId, lineId, text, visibility);
      toast(t(S.saved));
      setBody("");
      setComposing(false);
      setError(null);
      await load();
    } catch {
      setError(S.saveFailed);
    } finally {
      setBusy(false);
    }
  }

  async function withdraw(noteId: string) {
    const why = reason.trim();
    if (!why) { setError(S.reasonRequired); return; }
    setBusy(true);
    try {
      await api.cancelLineNote(kind, noteId, why);
      setWithdrawing(null);
      setReason("");
      setError(null);
      await load();
    } catch {
      setError(S.saveFailed);
    } finally {
      setBusy(false);
    }
  }

  const panelName = lineLabel ? `${t(S.title)} — ${lineLabel}` : t(S.title);

  if (loadError) {
    return (
      <section className="ln-panel" aria-label={panelName}>
        <InlineAlert tone="bad">{t(S.loadFailed)}</InlineAlert>
      </section>
    );
  }

  return (
    <section className="ln-panel" aria-label={panelName}>
      {/* NOT a heading. This panel mounts at different depths — inside a table cell at the counter, inside a
          modal on the doctor's side — so any fixed level is wrong somewhere, and axe caught exactly that
          (heading-order: an h4 under the counter's h2). The section's aria-label carries the accessible
          name, which is what a heading would have contributed; this is the visible label only. */}
      <p className="ln-title" aria-hidden="true">{t(S.title)}</p>

      <ul className="ln-list" aria-live="polite">
        {notes === null ? null : notes.length === 0 ? (
          <li className="ln-empty">{t(S.empty)}</li>
        ) : (
          notes.map((n) => (
            <li key={n.noteId} className="ln-note" data-status={n.status}>
              <p className="ln-body">{n.body}</p>
              <p className="ln-meta">
                <span>{n.authorDisplayName}</span>
                <span>· {t(VISIBILITY_LABEL[n.visibility])}</span>
                <time dateTime={n.authoredAt}>· {n.authoredAt.slice(0, 10)}</time>
                {n.status === "Cancelled" ? (
                  <span className="ln-withdrawn">
                    · {t(S.withdrawn)}{n.cancelReason ? `: ${n.cancelReason}` : ""}
                  </span>
                ) : null}
              </p>
              {canWrite && n.status === "Active" ? (
                withdrawing === n.noteId ? (
                  <div className="ln-withdraw">
                    <TextareaField
                      label={t(S.withdrawReason)}
                      value={reason}
                      onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setReason(e.target.value)}
                      rows={2}
                    />
                    <div className="ln-actions">
                      <Button size="sm" variant="ghost" onClick={() => setWithdrawing(null)}>{t(S.cancel)}</Button>
                      <Button size="sm" onClick={() => void withdraw(n.noteId)} disabled={busy}>
                        {t(S.withdraw)}
                      </Button>
                    </div>
                  </div>
                ) : (
                  <Button size="sm" variant="ghost" onClick={() => setWithdrawing(n.noteId)}>
                    {t(S.withdraw)}
                  </Button>
                )
              ) : null}
            </li>
          ))
        )}
      </ul>

      {error ? <InlineAlert tone="bad">{t(error)}</InlineAlert> : null}

      {canWrite ? (
        composing ? (
          <div className="ln-compose">
            <TextareaField
              label={t(S.body)}
              value={body}
              onChange={(e: React.ChangeEvent<HTMLTextAreaElement>) => setBody(e.target.value)}
              rows={3}
              help={t(S.hint)}
            />
            <ComboboxField
              label={t(S.visibility)}
              value={visibility}
              onChange={(v) => setVisibility(v as NoteVisibility)}
              options={writable.map((v) => ({ value: v, label: t(VISIBILITY_LABEL[v]) }))}
            />
            <div className="ln-actions">
              <Button size="sm" variant="ghost" onClick={() => setComposing(false)}>{t(S.cancel)}</Button>
              <Button
                size="sm"
                onClick={() => void save()}
                disabled={busy}
                leadingIcon={<Icon name="check2" aria-hidden="true" />}
              >
                {t(S.save)}
              </Button>
            </div>
          </div>
        ) : (
          <Button
            size="sm"
            variant="secondary"
            leadingIcon={<Icon name="plus" aria-hidden="true" />}
            onClick={() => setComposing(true)}
          >
            {t(S.add)}
          </Button>
        )
      ) : null}
    </section>
  );
}

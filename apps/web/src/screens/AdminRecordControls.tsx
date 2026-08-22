import { useEffect, useState, type ReactNode } from "react";
import { Button, Icon, InlineAlert, Modal, StatusChip, TextareaField } from "@mersal/design-system";
import type { IconName } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { writeErrorMessage } from "../api/writeError";
import { fillLocalized, useLoc, readErrorMessage } from "./_shared";
import { ConfirmAction } from "./ConfirmAction";
import { useIdempotencyKey } from "./PolicyPanels";
import { useFormat } from "../i18n/useFormat";

/**
 * The controls a catalogue record carries: the action row, the reason-gated status change, and the history.
 *
 * ============================================================================================================
 * WHY THIS IS A MODULE AND NOT A PATTERN EACH SCREEN REPEATS
 * ============================================================================================================
 * 19.7 built these for the payer. 19.8 needed the same three for the plan and the contract, and the audit that
 * ran between them had just finished counting what happens when a pattern is repeated rather than shared:
 * eight ad-hoc wrapper classes around one checkbox, four hand-picked field widths, five ways of building a
 * filter bar. Three screens each writing their own confirmation dialog would drift the same way — and this one
 * carries a rule that must not drift, because it is the difference between a change somebody can explain next
 * year and one they cannot.
 *
 * ============================================================================================================
 * THE REASON IS THE POINT
 * ============================================================================================================
 * Every status change in this portal requires one, of at least ten characters, and the server enforces the
 * same bar. Not pedantry: a one-word reason ("old", "unpaid") is indistinguishable from no reason at all to
 * whoever reads the record next year, and being readable then is the entire purpose of requiring one.
 *
 * The confirm stays unpressable until the reason reads like a sentence, and the dialog stays OPEN when the
 * write is refused — `ConfirmAction` swallows the rejection and leaves it up so the caller can render the RFC
 * 7807 detail. That is what carries "this plan is still attached to 3 active policies" to the operator instead
 * of dismissing it along with the typed reason.
 */

const S = {
  history: { en: "Change history", ar: "سجل التغييرات" },
  edit: { en: "Edit", ar: "تعديل" },
  close: { en: "Close", ar: "إغلاق" },
  reason: { en: "Why", ar: "السبب" },
  reasonHint: {
    en: "In a sentence somebody reading this record next year would understand. It is stored on the record and on the change history.",
    ar: "بجملة يفهمها من يقرأ هذا السجل بعد عام. تُحفَظ على السجل وفي سجل التغييرات.",
  },
  reasonTooShort: { en: "Say why, in a sentence.", ar: "اذكر السبب في جملة." },
  historyHint: {
    en: "Every create and edit, newest first. This is the operational record kept beside the row; the tamper-evident audit trail is separate and belongs to Compliance.",
    ar: "كل إنشاء وتعديل، الأحدث أولًا. هذا هو السجل التشغيلي المحفوظ مع السجل؛ أما سجل التدقيق غير القابل للعبث فمنفصل ويخص الالتزام.",
  },
  noHistory: { en: "No history recorded.", ar: "لا يوجد سجل." },
  historyStartsAt: {
    en: "History is recorded from the day this record was last written. A record created before change tracking existed has none until it is next edited.",
    ar: "يُسجَّل التاريخ من يوم آخر كتابة لهذا السجل. السجل المنشأ قبل تفعيل التتبع لا يحتوي على تاريخ حتى يُعدَّل مرة أخرى.",
  },
  created: { en: "Created", ar: "أُنشئ" },
  changed: { en: "Changed", ar: "عُدّل" },
  unknownActor: { en: "Not recorded", ar: "غير مسجّل" },
} satisfies Record<string, Localized>;

/** The minimum a history entry has to carry to be rendered by {@link HistoryModal}. */
export interface AdminHistoryEntry {
  historyId: number;
  operation: string;
  recordedAt: string;
  actorName?: string | null;
  statusReason?: string | null;
}

// ── The action row ──────────────────────────────────────────────────────────────────────────────────────

export interface RecordActionsProps {
  /** History is offered to READERS too: "who changed this" is a question a claims officer disputing a term
   *  has every reason to ask, and the projection withholds whatever their role may not see. */
  onHistory: () => void;
  /** Omitted entirely for a role that may not write — never rendered disabled. A disabled button teaches an
   *  operator that the screen is broken; an absent one teaches them whose job it is. */
  onEdit?: () => void;
  editLabel?: Localized;
  /** The status move, when there is one. `icon` distinguishes switching off from switching back on. */
  status?: { label: Localized; icon: IconName; onClick: () => void };
  /** Anything the record needs beyond the three — the plan's Amend, say. */
  children?: ReactNode;
}

export function RecordActions({ onHistory, onEdit, editLabel, status, children }: RecordActionsProps) {
  const t = useLoc();
  return (
    <div className="rst-actions">
      {children}
      {/* Icon-only: the glyph IS the control, so each carries its own accessible name. */}
      <Button variant="ghost" aria-label={t(S.history)} title={t(S.history)} onClick={onHistory}>
        <Icon name="history" />
      </Button>
      {onEdit && (
        <Button
          variant="ghost"
          aria-label={t(editLabel ?? S.edit)}
          title={t(editLabel ?? S.edit)}
          onClick={onEdit}
        >
          <Icon name="pen" />
        </Button>
      )}
      {status && (
        <Button
          variant="ghost"
          aria-label={t(status.label)}
          title={t(status.label)}
          onClick={status.onClick}
        >
          <Icon name={status.icon} />
        </Button>
      )}
    </div>
  );
}

// ── The reason-gated status change ──────────────────────────────────────────────────────────────────────

export interface ReasonDialogProps {
  title: Localized;
  body: Localized;
  /** What KIND of consequence this is. Defaults to the reversible line, because most of these are. */
  description?: Localized;
  confirmLabel: Localized;
  /** Runs with the trimmed reason. Throw to keep the dialog open — the message is rendered here. */
  onConfirm: (reason: string, idempotencyKey: string) => Promise<void>;
  onClose: () => void;
  onDone: () => void | Promise<void>;
  /** Extra context above the reason — an impact line, a count, a warning. */
  children?: ReactNode;
}

/** Ten characters, matching the server. See the module header for why. */
export const MIN_REASON = 10;

export function ReasonDialog({
  title, body, description, confirmLabel, onConfirm, onClose, onDone, children,
}: ReasonDialogProps) {
  const t = useLoc();
  const [key, rotate] = useIdempotencyKey();
  const [reason, setReason] = useState("");
  const [problem, setProblem] = useState<Localized | null>(null);

  return (
    <ConfirmAction
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={title}
      body={body}
      description={description}
      confirmLabel={confirmLabel}
      canConfirm={reason.trim().length >= MIN_REASON}
      onConfirm={async () => {
        try {
          await onConfirm(reason.trim(), key);
          await onDone();
        } catch (e) {
          rotate();
          setProblem(writeErrorMessage(e).message);
          // Re-thrown so the dialog stays open. Resolving here would dismiss it — and a dialog that closes
          // on a refusal reads as "done", with the typed reason thrown away.
          throw e;
        }
      }}
    >
      {children}
      {problem && <InlineAlert tone="bad">{t(problem)}</InlineAlert>}
      <TextareaField
        label={t(S.reason)}
        help={t(S.reasonHint)}
        rows={3}
        value={reason}
        onChange={(e) => setReason(e.currentTarget.value)}
        error={reason.trim().length > 0 && reason.trim().length < MIN_REASON ? t(S.reasonTooShort) : undefined}
        required
      />
    </ConfirmAction>
  );
}

// ── The history ─────────────────────────────────────────────────────────────────────────────────────────

export interface HistoryModalProps<E extends AdminHistoryEntry> {
  title: Localized;
  load: () => Promise<{ entries: E[] }>;
  /** The facts THIS record's history shows, rendered under each entry's header line. */
  facts: (entry: E) => ReactNode;
  onClose: () => void;
}

export function HistoryModal<E extends AdminHistoryEntry>({ title, load, facts, onClose }: HistoryModalProps<E>) {
  const t = useLoc();
  const fmt = useFormat();
  const [entries, setEntries] = useState<E[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    load()
      .then((p) => { if (live) setEntries(p.entries); })
      .catch((e) => { if (live) setError(readErrorMessage(e)); });
    return () => { live = false; };
    // `load` is a closure over the record's id; re-running on every render would refetch in a loop.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <Modal
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={t(title)}
      closeLabel={t(S.close)}
      wide
      footer={<Button variant="ghost" onClick={onClose}>{t(S.close)}</Button>}
    >
      <p className="pol-muted">{t(S.historyHint)}</p>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {/* An empty history on an OLD record is not a fault, and saying so stops it reading as one: the
          triggers record from the migration forward, so a row nobody has touched since has nothing yet. */}
      {entries !== null && entries.length === 0 && (
        <>
          <p className="pol-muted">{t(S.noHistory)}</p>
          <InlineAlert tone="info">{t(S.historyStartsAt)}</InlineAlert>
        </>
      )}
      {entries && entries.length > 0 && (
        <ol className="pay-history">
          {entries.map((e) => (
            <li key={e.historyId}>
              <div className="pay-history-when">
                <StatusChip
                  kind={e.operation === "INSERT" ? "info" : "neu"}
                  label={t(e.operation === "INSERT" ? S.created : S.changed)}
                />
                <span>{fmt.dateTime(e.recordedAt)}</span>
                <span className="pol-muted">{e.actorName ?? t(S.unknownActor)}</span>
              </div>
              <dl className="pol-identity-list">{facts(e)}</dl>
              {e.statusReason && <p className="pol-muted">{e.statusReason}</p>}
            </li>
          ))}
        </ol>
      )}
    </Modal>
  );
}

// ── One label/value pair ────────────────────────────────────────────────────────────────────────────────

export function Fact({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd className={mono ? "tnum" : undefined}>{value}</dd>
    </div>
  );
}

/** `fillLocalized` re-exported so a screen composing a dialog title does not import from two places. */
export { fillLocalized };

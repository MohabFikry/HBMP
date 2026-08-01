import { Button, Icon, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";

/**
 * The call summary, drafted on the member's file, plus a copy button.
 *
 * <b>This was `CallNotes`, and the rename is the change.</b> A call used to carry two bodies of text: private
 * "call notes" the agent typed here, and a separate "call summary" collected in the wrap-up card that other
 * roles read on the member's profile. Two boxes, two labels, and only one of them reaching anybody — an agent
 * writing a careful note here was writing it into a field nobody downstream would ever open. They are one field
 * now, and it is the one that is read.
 *
 * Lives in its own module because BOTH the workspace's member file and the standalone booking screen render it,
 * and having either import it from the other would make a real import cycle (each also needs the other's
 * exports).
 *
 * <b>Why copy exists.</b> Agents routinely have to repeat the account into a confirmation message or a
 * supervisor handover, and retyping it is where the call record and what the member was actually told stop
 * agreeing.
 *
 * The summary is an OPERATIONAL log, not a clinical one — the help text says so, and callcentre-service has
 * nowhere to put a clinical field even if one were typed here.
 */
export function CallSummaryDraft({
  value, onChange, onCopy,
}: {
  value: string;
  onChange: (v: string) => void;
  onCopy: () => void;
}) {
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang];
  return (
    <div className="cc-notes">
      <label className="cc-field cc-notes-field">
        <span>{t(L.ccSummary)}</span>
        <textarea
          className="mrs-control"
          rows={3}
          value={value}
          maxLength={500}
          onChange={(e) => onChange(e.currentTarget.value)}
        />
      </label>
      <p className="cc-muted">{t(L.ccSummaryHelp)}</p>
      {/* Disabled on empty: a "Copied" announcement for an empty clipboard write is a lie the agent may act on. */}
      <Button variant="ghost" onClick={onCopy} disabled={!value.trim()}>
        <Icon name="doc" /> {t(L.ccCopy)}
      </Button>
    </div>
  );
}

import { Button, Icon, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";

/**
 * Call notes plus a copy button. Lives in its own module because BOTH the workspace's member file and the
 * standalone booking screen render it, and having either import it from the other would make a real import
 * cycle (each also needs the other's exports).
 *
 * <b>Why copy exists.</b> Agents routinely have to repeat the note into a confirmation message or a supervisor
 * handover, and retyping it is where the call record and what the member was actually told stop agreeing.
 *
 * The notes are a CONTACT log, not a clinical one — the help text says so, and callcentre-service has nowhere
 * to put a clinical field even if one were typed here.
 */
export function CallNotes({
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
        <span>{t(L.ccCallNotes)}</span>
        <textarea
          className="mrs-control"
          rows={3}
          value={value}
          onChange={(e) => onChange(e.currentTarget.value)}
        />
      </label>
      <p className="cc-muted">{t(L.ccCallNotesHelp)}</p>
      {/* Disabled on empty: a "Copied" announcement for an empty clipboard write is a lie the agent may act on. */}
      <Button variant="ghost" onClick={onCopy} disabled={!value.trim()}>
        <Icon name="doc" /> {t(L.ccCopy)}
      </Button>
    </div>
  );
}

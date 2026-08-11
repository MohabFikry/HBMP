import { useEffect, useRef, useState } from "react";
import { Button, Icon, InlineAlert, InputField, useTheme } from "@mersal/design-system";
import type { Localized } from "../portals/catalog";
import { useApi } from "../api/ApiProvider";
import { useWrite } from "../api/useWrite";
import { L } from "../i18n/strings";
import { PhotoPicker } from "./PhotoPicker";

/**
 * Change your own password.
 *
 * <b>The current password is required</b>, and that requirement is the whole security of this form. Being
 * signed in proves somebody has the DEVICE; it does not prove they are the owner. Without it, an unattended
 * unlocked workstation is a permanent account takeover — the attacker sets a password the owner does not
 * know, and the owner's own recovery path is the only thing that would ever tell them.
 *
 * Collapsed until asked for: it is the rarest thing in this drawer, and an always-open password form is
 * three fields of noise on every visit to change the theme.
 */
function ChangePasswordForm() {
  const { lang } = useTheme();
  const api = useApi();
  const write = useWrite();
  const [open, setOpen] = useState(false);
  const [current, setCurrent] = useState("");
  const [next, setNext] = useState("");
  const [confirm, setConfirm] = useState("");
  const [touched, setTouched] = useState(false);
  const [done, setDone] = useState(false);

  // Checked HERE as well as on the server, because it is the one rule the server cannot check: it receives
  // one new password and has no idea what the person meant to type twice.
  const mismatch = touched && next !== confirm;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setTouched(true);
    setDone(false);
    if (!current || !next || next !== confirm) return;
    const ok = await write.run(() => api.changeMyPassword(current, next));
    if (ok) {
      setDone(true);
      setCurrent("");
      setNext("");
      setConfirm("");
      setTouched(false);
      setOpen(false);
    }
  }

  if (!open) {
    return (
      <>
        {/* The confirmation outlives the form that produced it — collapsing on success would otherwise
            leave the drawer looking exactly as it did before, which reads as a button that did nothing. */}
        <div aria-live="polite">{done && <InlineAlert tone="ok">{L.passwordChanged[lang]}</InlineAlert>}</div>
        <div className="upane-row">
          <span>{L.password[lang]}</span>
          <Button variant="secondary" size="sm" onClick={() => setOpen(true)}>
            {L.changePassword[lang]}
          </Button>
        </div>
      </>
    );
  }

  return (
    <form onSubmit={submit} className="upane-pwform">
      {/* Every other session ends on success — said BEFORE the change, not after, because it is the reason
          somebody would choose this over doing nothing when they suspect their password is known. */}
      <p className="muted upane-pwform-help">{L.changePasswordHelp[lang]}</p>
      {write.error && <InlineAlert tone="bad">{write.error.message[lang]}</InlineAlert>}
      <InputField
        label={L.currentPassword[lang]}
        type="password"
        autoComplete="current-password"
        value={current}
        onChange={(e) => setCurrent(e.target.value)}
      />
      <InputField
        label={L.newPassword[lang]}
        type="password"
        autoComplete="new-password"
        help={L.passwordPolicy[lang]}
        value={next}
        onChange={(e) => setNext(e.target.value)}
      />
      <InputField
        label={L.confirmPassword[lang]}
        type="password"
        autoComplete="new-password"
        error={mismatch ? L.passwordMismatch[lang] : undefined}
        value={confirm}
        onChange={(e) => setConfirm(e.target.value)}
      />
      <div className="upane-pwform-actions">
        <Button type="button" variant="ghost" size="sm" onClick={() => setOpen(false)}>
          {L.cancel[lang]}
        </Button>
        <Button type="submit" variant="primary" size="sm" loading={write.busy}>
          {L.changePassword[lang]}
        </Button>
      </div>
    </form>
  );
}

/**
 * The account pane — a sliding drawer (same pattern as the notification pane) opened from the app-bar
 * avatar. It shows the person's PHOTOGRAPH (initials until one is set, and changeable here since 28.15), their
 * name and their job title,
 * a Settings section (appearance + language preferences), and Sign out. Traps focus, closes on Escape,
 * and returns focus to the avatar on close.
 */
export function UserPane({
  open,
  onClose,
  displayName,
  userId,
  roleLabel,
  position,
  onSignOut,
}: {
  open: boolean;
  onClose: () => void;
  displayName: string;
  /** The signed-in account, so the pane can show and change its photograph. */
  userId: string | undefined;
  /** The fallback caption: the portal's own label, used when no job title is recorded. */
  roleLabel: Localized;
  /** The person's job title, when they have one. Takes precedence over `roleLabel`. */
  position?: string | null;
  onSignOut: () => void;
}) {
  const { theme, lang, toggleTheme, toggleLang } = useTheme();
  const panelRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const panel = panelRef.current;
    panel?.querySelector<HTMLElement>("[data-autofocus]")?.focus();
    function onKey(e: KeyboardEvent) {
      if (e.key === "Escape") {
        e.preventDefault();
        onClose();
        return;
      }
      if (e.key !== "Tab" || !panel) return;
      const f = panel.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])',
      );
      if (f.length === 0) return;
      const first = f[0];
      const last = f[f.length - 1];
      if (e.shiftKey && document.activeElement === first) {
        e.preventDefault();
        last.focus();
      } else if (!e.shiftKey && document.activeElement === last) {
        e.preventDefault();
        first.focus();
      }
    }
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [open, onClose]);

  if (!open) return null;

  return (
    <div className="npane-overlay" onMouseDown={onClose}>
      <div
        ref={panelRef}
        className="npane upane mrs-glass"
        role="dialog"
        aria-modal="true"
        aria-label={L.account[lang]}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <header className="npane-head">
          <h2 className="npane-title">{L.account[lang]}</h2>
          <button type="button" className="npane-close" data-autofocus onClick={onClose} aria-label={L.accountClose[lang]}>
            <Icon name="cross" />
          </button>
        </header>

        <div className="npane-body mrs-scroll">
          <div className="upane-profile">
            {/* 28.15 — the person can change their own picture from the place they already come to act on
                their own account, which is the only such place in the app. */}
            <PhotoPicker userId={userId} name={displayName} t={(l) => l[lang]} />
            <span className="upane-identity">
              <span className="upane-name">{displayName}</span>
              {/* The person's POSITION where one is recorded — the same line the app bar shows, for the
                  same reason: it is a fact about them, not about the portal they happen to be in. This read
                  `roleLabel`, so the pane repeated the portal's name directly under the person's own. */}
              <span className="upane-role">{position ?? roleLabel[lang]}</span>
            </span>
          </div>

          {/*
            28.8 — CHANGING YOUR OWN PASSWORD, which this app has never offered.
            ------------------------------------------------------------------------------------------------
            28.6 gave a locked-out person a way back in and 28.7 removed the administrator's power to choose
            a credential for them. Between them they left the ordinary case unbuilt: somebody who simply
            wants to change a password they already know had to sign out and use "forgot password" — which
            teaches staff that a routine, healthy act is indistinguishable from losing your credentials.

            It lives in the account pane rather than on a settings page because this is where a person
            already goes to act on their own account, and it is the only such place.
          */}
          <div className="upane-section">
            <h3 className="section-h">{L.security[lang]}</h3>
            <ChangePasswordForm />
          </div>

          <div className="upane-section">
            <h3 className="section-h">{L.settings[lang]}</h3>
            <div className="upane-row">
              <span>{L.appearance[lang]}</span>
              <Button variant="secondary" size="sm" leadingIcon={<Icon name="moon" />} onClick={toggleTheme}>
                {theme === "dark" ? L.light[lang] : L.dark[lang]}
              </Button>
            </div>
            <div className="upane-row">
              <span>{L.language[lang]}</span>
              <Button variant="secondary" size="sm" onClick={toggleLang}>
                {lang === "en" ? "العربية" : "English"}
              </Button>
            </div>
          </div>
        </div>

        <footer className="npane-foot">
          <Button variant="primary" onClick={onSignOut} style={{ inlineSize: "100%" }}>
            {L.signOut[lang]}
          </Button>
        </footer>
      </div>
    </div>
  );
}

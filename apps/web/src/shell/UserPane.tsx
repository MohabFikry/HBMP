import { useEffect, useRef } from "react";
import { Button, Icon, useTheme } from "@mersal/design-system";
import type { Localized } from "../portals/catalog";
import { L } from "../i18n/strings";

/** Two-letter initials from a display name, for the avatar placeholder (e.g. "Reception Desk" → "RD"). */
function initialsOf(name: string): string {
  const parts = name.trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return "?";
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
}

/**
 * The account pane — a sliding drawer (same pattern as the notification pane) opened from the app-bar
 * avatar. It shows a circular photo placeholder (initials), the employee name and their role/position,
 * a Settings section (appearance + language preferences), and Sign out. Traps focus, closes on Escape,
 * and returns focus to the avatar on close.
 */
export function UserPane({
  open,
  onClose,
  displayName,
  roleLabel,
  onSignOut,
}: {
  open: boolean;
  onClose: () => void;
  displayName: string;
  roleLabel: Localized;
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
        className="npane mrs-glass"
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
            <span className="upane-avatar" aria-hidden="true">
              {initialsOf(displayName)}
            </span>
            <span className="upane-identity">
              <span className="upane-name">{displayName}</span>
              <span className="upane-role">{roleLabel[lang]}</span>
            </span>
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

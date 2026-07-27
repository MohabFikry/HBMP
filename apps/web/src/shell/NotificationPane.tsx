import { useEffect, useRef, useState } from "react";
import { useFormat } from "../i18n/useFormat";
import { Icon, StatusChip, useTheme } from "@mersal/design-system";
import type { Notification } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import type { Section } from "../portals/catalog";
import { L } from "../i18n/strings";
import { originSection } from "./notificationOrigin";

/**
 * The sliding notification pane (US-072). Opened from the app-bar bell, it slides in from the inline-end
 * edge as a modal drawer. Each notification is compacted into a single dense item — unread dot, subject,
 * a clamped body, its business reference + time, and (when resolvable) a targeted link to the section the
 * notification originates from, so the recipient can jump straight to where they take action or read more.
 * Opening an item marks it read (which also stops its escalation timer server-side) and, when an origin
 * section exists, navigates there. The pane traps focus, closes on Escape, and returns focus to the bell.
 */
export function NotificationPane({
  open,
  onClose,
  portalBase,
  sections,
  onNavigate,
  onChanged,
}: {
  open: boolean;
  onClose: () => void;
  portalBase: string;
  sections: Section[];
  onNavigate: (fullPath: string) => void;
  onChanged: () => void;
}) {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const { lang } = useTheme();
  const t = (l: { en: string; ar: string }) => l[lang];
  const [state, setState] = useState<"loading" | "error" | "ready">("loading");
  const [rows, setRows] = useState<Notification[]>([]);
  const [busy, setBusy] = useState<string | null>(null);
  const panelRef = useRef<HTMLDivElement | null>(null);

  // Load on open; re-load whenever the pane is (re)opened so the list is fresh.
  useEffect(() => {
    if (!open) return;
    let live = true;
    setState("loading");
    api
      .notifications(false)
      .then((n) => {
        if (!live) return;
        // Unread first, then most-recent — the actionable items surface at the top.
        const sorted = [...n].sort(
          (a, b) => Number(a.read) - Number(b.read) || +new Date(b.createdAt) - +new Date(a.createdAt),
        );
        setRows(sorted);
        setState("ready");
      })
      .catch(() => live && setState("error"));
    return () => {
      live = false;
    };
  }, [api, open]);

  // Move focus into the panel on open; Escape closes; basic focus trap within the drawer.
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
      const focusable = panel.querySelectorAll<HTMLElement>(
        'a[href], button:not([disabled]), [tabindex]:not([tabindex="-1"])',
      );
      if (focusable.length === 0) return;
      const first = focusable[0];
      const last = focusable[focusable.length - 1];
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

  async function openItem(n: Notification) {
    setBusy(n.id);
    try {
      if (!n.read) await api.markNotificationRead(n.id);
    } catch {
      /* non-fatal: navigation still proceeds */
    } finally {
      setBusy(null);
    }
    onChanged();
    const target = originSection(n.sourceEventType, sections);
    onClose();
    onNavigate(target ? `/${portalBase}/${target.path}` : `/${portalBase}/notifications`);
  }

  async function markRead(e: React.MouseEvent, id: string) {
    e.stopPropagation();
    setBusy(id);
    try {
      await api.markNotificationRead(id);
      setRows((prev) => prev.map((r) => (r.id === id ? { ...r, read: true } : r)));
      onChanged();
    } finally {
      setBusy(null);
    }
  }

  return (
    <div className="npane-overlay" onMouseDown={onClose}>
      <div
        ref={panelRef}
        className="npane mrs-glass"
        role="dialog"
        aria-modal="true"
        aria-label={L.notifications[lang]}
        onMouseDown={(e) => e.stopPropagation()}
      >
        <header className="npane-head">
          <h2 className="npane-title">{L.notifications[lang]}</h2>
          <button type="button" className="npane-close" data-autofocus onClick={onClose} aria-label={L.notificationsClose[lang]}>
            <Icon name="cross" />
          </button>
        </header>

        <div className="npane-body" aria-live="polite">
          {state === "loading" && <p className="muted npane-msg">…</p>}
          {state === "error" && <StatusChip kind="bad" label={L.notificationsError[lang]} />}
          {state === "ready" && rows.length === 0 && (
            <p className="muted npane-msg">{L.notificationsEmpty[lang]}</p>
          )}
          {state === "ready" && rows.length > 0 && (
            <ul className="npane-list">
              {rows.map((n) => {
                const origin = originSection(n.sourceEventType, sections);
                return (
                  <li key={n.id} className={`npane-item${n.read ? "" : " is-unread"}`}>
                    <button
                      type="button"
                      className="npane-item-open"
                      onClick={() => void openItem(n)}
                      disabled={busy === n.id}
                    >
                      <span className="npane-dot" aria-hidden="true" data-on={!n.read} />
                      <span className="npane-item-main">
                        <span className="npane-subject">{n.subject}</span>
                        <span className="npane-preview">{n.body}</span>
                        <span className="npane-meta">
                          <StatusChip kind={n.status.kind} label={t(n.status.label)} />
                          {n.actionable && !n.read && (
                            <StatusChip kind="warn" label={L.notificationsActionNeeded[lang]} />
                          )}
                          {origin && <span className="npane-origin">▸ {t(origin.label)}</span>}
                          {n.entityRef && <span className="tnum npane-ref">{n.entityRef}</span>}
                          <span className="npane-time tnum">{fmt.dateTime(n.createdAt)}</span>
                        </span>
                      </span>
                    </button>
                    {!n.read && (
                      <button
                        type="button"
                        className="npane-markread"
                        disabled={busy === n.id}
                        onClick={(e) => void markRead(e, n.id)}
                      >
                        {L.notificationsMarkRead[lang]}
                      </button>
                    )}
                  </li>
                );
              })}
            </ul>
          )}
        </div>

        <footer className="npane-foot">
          <button
            type="button"
            className="npane-viewall"
            onClick={() => {
              onClose();
              onNavigate(`/${portalBase}/notifications`);
            }}
          >
            {L.notificationsViewAll[lang]}
          </button>
        </footer>
      </div>
    </div>
  );
}

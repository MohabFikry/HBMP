import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import {
  Button,
  Icon,
  Logo,
  Modal,
  NavRail,
  useTheme,
  type NavItem,
} from "@mersal/design-system";
import { useAuth } from "../auth/AuthProvider";
import { useApi } from "../api/ApiProvider";
import { portalForRole, type Localized, type Section } from "../portals/catalog";
import { L } from "../i18n/strings";
import { NotificationPane } from "./NotificationPane";
import { UserPane } from "./UserPane";
import { BranchSwitcher } from "./BranchSwitcher";
import { useBranchContext } from "./useBranchContext";

/** Two-letter initials for the app-bar avatar placeholder. */
function initials(name: string): string {
  const p = name.trim().split(/\s+/).filter(Boolean);
  if (p.length === 0) return "?";
  if (p.length === 1) return p[0].slice(0, 2).toUpperCase();
  return (p[0][0] + p[p.length - 1][0]).toUpperCase();
}

function useLocalized() {
  const { lang } = useTheme();
  return (l: Localized) => l[lang];
}

/**
 * Unread-notification count for the top-bar bell badge. Polls the caller's own inbox (the
 * notification-service row-filters by recipient == caller, so it is inherently min-necessary). Re-reads on
 * every route change so the badge clears promptly after the Notifications screen marks items read.
 */
function useUnreadCount(enabled: boolean, refreshToken: number): number {
  const api = useApi();
  const location = useLocation();
  const [count, setCount] = useState(0);
  useEffect(() => {
    if (!enabled) return;
    let live = true;
    const load = () =>
      api
        .notifications(true)
        .then((n) => live && setCount(n.length))
        .catch(() => live && setCount(0));
    load();
    const timer = setInterval(load, 60_000);
    return () => {
      live = false;
      clearInterval(timer);
    };
  }, [api, enabled, location.pathname, refreshToken]);
  return count;
}

/**
 * The shared portal shell (14 §1): glass top bar (banner), permission-generated nav rail (navigation),
 * breadcrumb, and the main landmark. Also hosts the global keyboard map and the session-timeout re-auth
 * prompt. Nav items are ONLY the sections the signed-in user may use (min-necessary menus, US-071).
 */
export function AppShell({ children }: { children: ReactNode }) {
  const { session, can, logout, timeoutWarning, keepAlive } = useAuth();
  const { lang } = useTheme();
  const navigate = useNavigate();
  const location = useLocation();
  const tr = useLocalized();

  const portal = session?.role ? portalForRole(session.role) : null;
  const accessible: Section[] = useMemo(
    () => (portal ? portal.sections.filter((s) => can(s.permission)) : []),
    [portal, can],
  );
  const canNotify = !!portal && can("notification.read");
  // 14.8 — branch context for the app-bar switcher (fail-soft: renders only when the caller has branches).
  const branchCtx = useBranchContext(session?.role ?? undefined);
  const [paneOpen, setPaneOpen] = useState(false);
  const [userPaneOpen, setUserPaneOpen] = useState(false);
  const [notifyRefresh, setNotifyRefresh] = useState(0);
  const unread = useUnreadCount(canNotify, notifyRefresh);
  const bellRef = useRef<HTMLButtonElement | null>(null);
  const avatarRef = useRef<HTMLButtonElement | null>(null);

  const homePath = portal && accessible[0] ? `/${portal.base}/${accessible[0].path}` : "/";
  const primaryQueuePath = homePath;

  // Global keyboard map (14 §4): "g h" home, "g q" primary queue. The "/" binding went with the dead
  // search field in 18.D2 (U5) — a shortcut that focuses a control which does nothing is worse than none.
  useEffect(() => {
    let gPending = false;
    let gTimer: ReturnType<typeof setTimeout> | null = null;
    function onKey(e: KeyboardEvent) {
      const target = e.target as HTMLElement;
      const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName) || target.isContentEditable;
      if (typing) return;
      if (e.key === "g") {
        gPending = true;
        if (gTimer) clearTimeout(gTimer);
        gTimer = setTimeout(() => (gPending = false), 800);
        return;
      }
      if (gPending && e.key === "h") {
        gPending = false;
        navigate(homePath);
      } else if (gPending && e.key === "q") {
        gPending = false;
        navigate(primaryQueuePath);
      }
    }
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [navigate, homePath, primaryQueuePath]);

  if (!portal || !session) return <>{children}</>;

  const navItems: NavItem[] = accessible.map((s) => ({
    key: s.path,
    label: tr(s.label),
    group: tr(s.group),
    icon: <Icon name={s.icon} />,
  }));

  const activePath = location.pathname.split("/")[2] ?? accessible[0]?.path;
  const activeSection = accessible.find((s) => s.path === activePath);

  return (
    <div className="app-grid">
      <header className="mrs-glass app-bar" role="banner">
        <Logo variant="lockup" height={48} />
        {/*
          18.D2 (audit R2 U5) — the app-bar search field is REMOVED, along with its "/" shortcut.
          It was bound to nothing: typing in it did nothing, submitting it did nothing, and the global "/"
          binding focused it — actively teaching every user a gesture that never works. A dead control is
          worse than a missing one, because people keep trying it and conclude the app is broken rather
          than that the feature does not exist. A permission-scoped command palette lands in 18.F2; until
          then the nav rail is the way to move around, and it works.
        */}
        <div className="app-actions">
          {!branchCtx.memberScoped && branchCtx.branches.length > 0 && (
            <BranchSwitcher
              memberScoped={false}
              branches={branchCtx.branches}
              activeBranchId={branchCtx.activeBranchId}
              onSwitch={branchCtx.switchBranch}
            />
          )}
          {canNotify && (
            <button
              ref={bellRef}
              type="button"
              className="app-bell"
              aria-haspopup="dialog"
              aria-expanded={paneOpen}
              onClick={() => setPaneOpen((v) => !v)}
              aria-label={
                unread > 0
                  ? `${L.notifications[lang]} — ${unread} ${L.notificationsUnread[lang]}`
                  : L.notifications[lang]
              }
            >
              <Icon name="bell" />
              {unread > 0 && (
                <span className="app-bell-badge" aria-hidden="true">
                  {unread > 9 ? "9+" : unread}
                </span>
              )}
            </button>
          )}
          <button
            ref={avatarRef}
            type="button"
            className="app-userbtn"
            aria-haspopup="dialog"
            aria-expanded={userPaneOpen}
            onClick={() => setUserPaneOpen((v) => !v)}
            aria-label={`${L.accountOpen[lang]} — ${session.displayName}`}
          >
            <span className="app-avatar" aria-hidden="true">
              {initials(session.displayName)}
            </span>
            <span className="app-userbtn-text">
              <span className="app-userbtn-name">{session.displayName}</span>
              <span className="app-userbtn-role">{tr(portal.eyebrow)}</span>
            </span>
          </button>
        </div>
      </header>

      <NavRail
        aria-label={tr(portal.title)}
        items={navItems}
        current={activePath}
        onNavigate={(key) => navigate(`/${portal.base}/${key}`)}
      />

      <main id="main" className="app-main" tabIndex={-1}>
        <nav aria-label={L.breadcrumb[lang]} className="app-crumb">
          <span>{tr(portal.title)}</span>
          {activeSection && (
            <>
              <span aria-hidden="true"> ▸ </span>
              <span aria-current="page">{tr(activeSection.label)}</span>
            </>
          )}
        </nav>
        {children}
      </main>

      {canNotify && (
        <NotificationPane
          open={paneOpen}
          onClose={() => {
            setPaneOpen(false);
            bellRef.current?.focus();
          }}
          portalBase={portal.base}
          sections={accessible}
          onNavigate={(fullPath) => navigate(fullPath)}
          onChanged={() => setNotifyRefresh((n) => n + 1)}
        />
      )}

      <UserPane
        open={userPaneOpen}
        onClose={() => {
          setUserPaneOpen(false);
          avatarRef.current?.focus();
        }}
        displayName={session.displayName}
        roleLabel={portal.eyebrow}
        onSignOut={() => void logout("user")}
      />

      <Modal
        open={timeoutWarning}
        onOpenChange={(o) => {
          if (!o) keepAlive();
        }}
        title={L.timeoutTitle[lang]}
        description={L.timeoutBody[lang]}
        footer={
          <>
            <Button variant="ghost" onClick={() => void logout("timeout")}>
              {L.signOut[lang]}
            </Button>
            <Button variant="primary" onClick={keepAlive}>
              {L.staySignedIn[lang]}
            </Button>
          </>
        }
      >
        <p style={{ margin: 0 }}>{L.timeoutBody[lang]}</p>
      </Modal>
    </div>
  );
}

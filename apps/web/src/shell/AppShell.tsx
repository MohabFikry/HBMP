import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import {
  Button,
  Icon,
  Logo,
  Modal,
  NavRail,
  SearchField,
  useTheme,
  type NavItem,
} from "@mersal/design-system";
import { useAuth } from "../auth/AuthProvider";
import { useApi } from "../api/ApiProvider";
import { portalForRole, type Localized, type Section } from "../portals/catalog";
import { L } from "../i18n/strings";
import { CommandPalette } from "./CommandPalette";
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

/** Static tab title outside a portal (login, access-denied) and the trailing brand inside one. */
const BRAND = "Mersal HBMP";

/**
 * Names the browser tab after the active section: "Register New | Mersal HBMP". This is what replaced the
 * on-screen breadcrumb — one line under the app bar that repeated the nav rail's own current-item
 * highlight, on every screen. The tab is the one place that context is NOT already visible: with several
 * portals open, the tab strip is all the user has to tell them apart. Section first, because tabs truncate
 * from the end.
 *
 * `section` is optional, so this is safe to call before the shell knows the portal — the title falls back to
 * the brand alone, and the effect restores it when the shell unmounts.
 */
function useDocumentTitle(section: string | undefined) {
  const title = section ? `${section} | ${BRAND}` : BRAND;
  useEffect(() => {
    document.title = title;
    return () => {
      document.title = BRAND;
    };
  }, [title]);
}

/**
 * The shared portal shell (14 §1): glass top bar (banner), permission-generated nav rail (navigation),
 * and the main landmark. Also hosts the global keyboard map and the session-timeout re-auth prompt. Nav
 * items are ONLY the sections the signed-in user may use (min-necessary menus, US-071).
 */
export function AppShell({ children }: { children: ReactNode }) {
  const { session, can, logout, timeoutWarning, keepAlive } = useAuth();
  const { lang } = useTheme();
  const navigate = useNavigate();
  const location = useLocation();
  const tr = useLocalized();
  const searchRef = useRef<HTMLInputElement | null>(null);

  const portal = session?.role ? portalForRole(session.role) : null;
  const accessible: Section[] = useMemo(
    () => (portal ? portal.sections.filter((s) => can(s.permission)) : []),
    [portal, can],
  );
  const canNotify = !!portal && can("notification.read");
  // 14.8 — branch context for the app-bar switcher (fail-soft: renders only when the caller has branches).
  const branchCtx = useBranchContext(session?.role ?? undefined);
  const [paneOpen, setPaneOpen] = useState(false);
  const [paletteOpen, setPaletteOpen] = useState(false);   // 18.F2 — ⌘K / Ctrl+K
  const [searchText, setSearchText] = useState("");
  const [paletteSeed, setPaletteSeed] = useState("");
  const [userPaneOpen, setUserPaneOpen] = useState(false);
  const [notifyRefresh, setNotifyRefresh] = useState(0);
  const unread = useUnreadCount(canNotify, notifyRefresh);
  const bellRef = useRef<HTMLButtonElement | null>(null);
  const avatarRef = useRef<HTMLButtonElement | null>(null);

  const activePath = location.pathname.split("/")[2] ?? accessible[0]?.path;
  const activeSection = accessible.find((s) => s.path === activePath);
  useDocumentTitle(activeSection ? tr(activeSection.label) : undefined);

  const homePath = portal && accessible[0] ? `/${portal.base}/${accessible[0].path}` : "/";
  const primaryQueuePath = useMemo(() => {
    if (!portal) return homePath;
    const queue = accessible.find((sec) => /queue|worklist|workspace|inbox/i.test(sec.key)
                                        || /queue|worklist|inbox/i.test(sec.label.en));
    return queue ? `/${portal.base}/${queue.path}` : homePath;
  }, [portal, accessible, homePath]);

  // Global keyboard map (14 §4): ⌘K/Ctrl+K palette, "g h" home, "g q" primary queue.
  //
  // 18.F2 — the palette REPLACES the "/" binding removed in 18.D2, which focused a search field that did
  // nothing. ⌘K is the convention users already have from every other tool, so it needs no discovery.
  //
  // Also fixed here: "g h" and "g q" both navigated to homePath, because primaryQueuePath was assigned from
  // it. Two shortcuts with one effect is not a shortcut — a user who learns "g q" and lands on Home each
  // time concludes the whole scheme is broken. "g q" now goes to the first section in the QUEUE/WORKLIST
  // group, falling back to home only when the portal genuinely has no queue.
  useEffect(() => {
    let gPending = false;
    let gTimer: ReturnType<typeof setTimeout> | null = null;
    function onKey(e: KeyboardEvent) {
      const target = e.target as HTMLElement;
      const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName) || target.isContentEditable;

      // ⌘K / Ctrl+K works even while typing — that is the point of a palette: reach it from anywhere,
      // including from inside the form you are halfway through.
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        setPaletteOpen((v) => !v);
        return;
      }

      // "/" focuses the app-bar search field, as it did before 18.D2. Guarded by `typing` — a bare "/"
      // must never be stolen from someone entering a date, a dose or a code.
      if (e.key === "/" && !typing) {
        e.preventDefault();
        searchRef.current?.focus();
        return;
      }
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
    // A real URL per item: middle-click/ctrl-click open tabs, and screen readers hear links (QA P2-17).
    href: `/${portal.base}/${s.path}`,
  }));

  return (
    <div className="app-grid">
      <header className="mrs-glass app-bar" role="banner">
        <Logo variant="lockup" height={48} />
        <div className="app-search">
          {/* QA P1-5: this field rendered, focused and did nothing. It is now the palette's wide-open
              front door — Enter hands the typed text to the command palette, which is where section
              search actually lives. One search, one implementation. */}
          <SearchField
            aria-label={L.search[lang]}
            placeholder={L.search[lang]}
            ref={searchRef}
            value={searchText}
            onChange={(e) => setSearchText(e.currentTarget.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                setPaletteSeed(searchText);
                setPaletteOpen(true);
                setSearchText("");
              }
            }}
          />
        </div>
        <div className="app-actions">
          {/* Only a BRANCH-SCOPED role gets a branch control here, because only they have one active branch to
              be in. A member-scoped role is not tied to a branch, and an "All branches" chip in the app bar
              states that at the top of every screen while doing nothing — worse, it reads as a global filter
              the user might expect to change what they see. Where the branch actually matters for those roles is
              at the point of a decision: the call centre picks the branch it is booking INTO, inside the
              reservation flow. */}
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

      <CommandPalette
        open={paletteOpen}
        onClose={() => { setPaletteOpen(false); setPaletteSeed(""); }}
        sections={accessible}
        portalBase={portal.base}
        onNavigate={navigate}
        initialQuery={paletteSeed}
      />

      <NavRail
        aria-label={tr(portal.title)}
        items={navItems}
        current={activePath}
        onNavigate={(key) => navigate(`/${portal.base}/${key}`)}
      />

      <main id="main" className="app-main" tabIndex={-1}>
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

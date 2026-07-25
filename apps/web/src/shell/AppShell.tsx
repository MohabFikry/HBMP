import { useEffect, useMemo, useRef, type ReactNode } from "react";
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
import { portalForRole, type Localized, type Section } from "../portals/catalog";
import { L } from "../i18n/strings";

function useLocalized() {
  const { lang } = useTheme();
  return (l: Localized) => l[lang];
}

/**
 * The shared portal shell (14 §1): glass top bar (banner), permission-generated nav rail (navigation),
 * breadcrumb, and the main landmark. Also hosts the global keyboard map and the session-timeout re-auth
 * prompt. Nav items are ONLY the sections the signed-in user may use (min-necessary menus, US-071).
 */
export function AppShell({ children }: { children: ReactNode }) {
  const { session, can, logout, timeoutWarning, keepAlive } = useAuth();
  const { theme, lang, toggleTheme, toggleLang } = useTheme();
  const navigate = useNavigate();
  const location = useLocation();
  const tr = useLocalized();
  const searchRef = useRef<HTMLInputElement | null>(null);

  const portal = session ? portalForRole(session.role) : null;
  const accessible: Section[] = useMemo(
    () => (portal ? portal.sections.filter((s) => can(s.permission)) : []),
    [portal, can],
  );

  const homePath = portal && accessible[0] ? `/${portal.base}/${accessible[0].path}` : "/";
  const primaryQueuePath = homePath;

  // Global keyboard map (14 §4): "/" focus search, "g h" home, "g q" primary queue.
  useEffect(() => {
    let gPending = false;
    let gTimer: ReturnType<typeof setTimeout> | null = null;
    function onKey(e: KeyboardEvent) {
      const target = e.target as HTMLElement;
      const typing = /^(INPUT|TEXTAREA|SELECT)$/.test(target.tagName) || target.isContentEditable;
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
  }));

  const activePath = location.pathname.split("/")[2] ?? accessible[0]?.path;
  const activeSection = accessible.find((s) => s.path === activePath);

  return (
    <div className="app-grid">
      <header className="mrs-glass app-bar" role="banner">
        <Logo variant="mark" wordmark="HBMP" />
        <div className="app-search">
          <SearchField aria-label={L.search[lang]} placeholder={L.search[lang]} ref={searchRef} />
        </div>
        <div className="app-actions">
          <Button variant="ghost" onClick={toggleLang} aria-label={L.language[lang]}>
            {lang === "en" ? "ع" : "EN"}
          </Button>
          <Button
            variant="ghost"
            leadingIcon={<Icon name="moon" />}
            onClick={toggleTheme}
            aria-label={L.theme[lang]}
          >
            <span className="sr-only">{theme === "dark" ? L.light[lang] : L.dark[lang]}</span>
          </Button>
          <span className="app-user" aria-label={L.signedInAs[lang]}>
            {session.displayName}
          </span>
          <Button variant="secondary" size="sm" onClick={() => void logout("user")}>
            {L.signOut[lang]}
          </Button>
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

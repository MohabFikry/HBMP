import { Suspense, type ReactNode } from "react";
import { Navigate, Route, Routes, useLocation } from "react-router-dom";
import { useAuth } from "../auth/AuthProvider";
import { AppShell } from "../shell/AppShell";
import { LoginPage } from "../pages/LoginPage";
import { ForgotPasswordPage } from "../pages/ForgotPasswordPage";
import { ResetPasswordPage } from "../pages/ResetPasswordPage";
import { SectionPage } from "../pages/SectionPage";
import { Forbidden, NotFound, NoPortal } from "./Forbidden";
import { ALL_ROUTES, portalForRole } from "../portals/catalog";
import { screenFor } from "../screens/registry";

/** Home = the first section of the signed-in user's portal that they can access. */
function useHomePath(): string {
  const { session, can } = useAuth();
  if (!session?.role) return "/login";
  const portal = portalForRole(session.role);
  const first = portal.sections.find((s) => can(s.permission));
  return first ? `/${portal.base}/${first.path}` : `/${portal.base}`;
}

/**
 * Resolves the current location against the full route catalog + the user's permissions:
 *  - a bare portal base (`/reception`) redirects to the portal home;
 *  - a known section the user CAN access renders it;
 *  - a known section the user CANNOT access → audited 403 (US-071);
 *  - anything else → 404.
 * The router only ever *mounts* usable routes; this catch-all is what turns forbidden deep links into a
 * 403 (with a request-access affordance) instead of a blank screen.
 */
function ResolveRoute() {
  const { session, can } = useAuth();
  const location = useLocation();
  const home = useHomePath();
  const path = location.pathname.replace(/\/+$/, "") || "/";

  if (!session) return <Navigate to="/login" replace />;
  if (!session.role) return <NoPortal />;
  const portal = portalForRole(session.role);

  // Bare portal base → home.
  if (path === `/${portal.base}`) return <Navigate to={home} replace />;

  const entry = ALL_ROUTES.find((r) => r.fullPath === path);

  // CROSS-PORTAL DEEP LINKS have no catalog section, ON PURPOSE: the unified patient profile is opened FOR
  // someone — from a worklist row, a search result, a notification — never navigated to from a menu
  // (design 39 §6). But this router resolved routes ONLY through the catalog, so `/patients/{id}` and all
  // seven `/{portal}/patient` routes answered NotFound. The whole feature was unreachable in the app:
  // built, tested, projected server-side per role, and behind a door with no handle.
  //
  // Gated on the COARSE permission only. What each role actually receives of the file is decided by the
  // server's per-section projection — that is the authoritative layer, and duplicating it here would be a
  // second opinion about who may see a diagnosis.
  if (!entry) {
    const deepLink = screenFor(path);
    if (!deepLink) return <NotFound />;
    if (!can("profile.read")) return <Forbidden path={path} />;
    return <ScreenBoundary>{deepLink()}</ScreenBoundary>;
  }

  if (!can(entry.section.permission)) return <Forbidden path={path} />;
  // A wired flagship screen (9.3) takes over its route; every other section keeps the 9.2 stub.
  // Screens are code-split (React.lazy), so a Suspense boundary covers the per-portal chunk load.
  const screen = screenFor(path);
  if (!screen) return <SectionPage section={entry.section} />;
  return <ScreenBoundary>{screen()}</ScreenBoundary>;
}

/** Suspense boundary for the per-portal lazy chunk, shared by catalog routes and deep links. */
function ScreenBoundary({ children }: { children: ReactNode }) {
  return (
    <Suspense
      fallback={
        <div className="async-loading" role="status" aria-live="polite" style={{ padding: "var(--sp6)" }}>
          <span className="mrs-spin" aria-hidden="true" />
        </div>
      }
    >
      {children}
    </Suspense>
  );
}

function AuthedApp() {
  const home = useHomePath();
  return (
    <AppShell>
      <Routes>
        <Route index element={<Navigate to={home} replace />} />
        <Route path="*" element={<ResolveRoute />} />
      </Routes>
    </AppShell>
  );
}

export function AppRouter() {
  const { session, ready } = useAuth();
  const home = useHomePath();

  if (!ready) {
    return (
      <div className="login-wrap" role="status" aria-live="polite">
        <span className="muted">Loading…</span>
      </div>
    );
  }

  // Authenticated but no portal role (fail-closed): show the bare "no portal assigned" page — not the shell,
  // not a default portal, not a login loop.
  if (session && !session.role) return <NoPortal />;

  return (
    <Routes>
      <Route path="/login" element={session ? <Navigate to={home} replace /> : <LoginPage />} />
      {/* 28.6 — reachable WITHOUT a session, which is the whole point: the person using them cannot sign in.
          A signed-in visitor is sent home rather than shown a reset form they reached by accident. */}
      <Route path="/forgot-password" element={session ? <Navigate to={home} replace /> : <ForgotPasswordPage />} />
      <Route path="/reset-password" element={session ? <Navigate to={home} replace /> : <ResetPasswordPage />} />
      <Route path="/*" element={session ? <AuthedApp /> : <Navigate to="/login" replace />} />
    </Routes>
  );
}

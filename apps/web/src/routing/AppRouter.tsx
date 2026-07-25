import { Navigate, Route, Routes, useLocation } from "react-router-dom";
import { useAuth } from "../auth/AuthProvider";
import { AppShell } from "../shell/AppShell";
import { LoginPage } from "../pages/LoginPage";
import { SectionPage } from "../pages/SectionPage";
import { Forbidden, NotFound } from "./Forbidden";
import { ALL_ROUTES, portalForRole } from "../portals/catalog";

/** Home = the first section of the signed-in user's portal that they can access. */
function useHomePath(): string {
  const { session, can } = useAuth();
  if (!session) return "/login";
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
  const portal = portalForRole(session.role);

  // Bare portal base → home.
  if (path === `/${portal.base}`) return <Navigate to={home} replace />;

  const entry = ALL_ROUTES.find((r) => r.fullPath === path);
  if (!entry) return <NotFound />;
  if (!can(entry.section.permission)) return <Forbidden path={path} />;
  return <SectionPage section={entry.section} />;
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

  return (
    <Routes>
      <Route path="/login" element={session ? <Navigate to={home} replace /> : <LoginPage />} />
      <Route path="/*" element={session ? <AuthedApp /> : <Navigate to="/login" replace />} />
    </Routes>
  );
}

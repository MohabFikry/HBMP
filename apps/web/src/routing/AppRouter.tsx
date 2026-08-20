import { Suspense, type ReactNode } from "react";
import { Navigate, Route, Routes, useLocation } from "react-router-dom";
import { useAuth } from "../auth/AuthProvider";
import { AppShell } from "../shell/AppShell";
import { LoginPage } from "../pages/LoginPage";
import { ForgotPasswordPage } from "../pages/ForgotPasswordPage";
import { ResetPasswordPage } from "../pages/ResetPasswordPage";
import { SectionPage } from "../pages/SectionPage";
import { Forbidden, NotFound, NoPortal } from "./Forbidden";
import { ALL_ROUTES, portalsForRoles, type PortalDef } from "../portals/catalog";
import { PortalPicker } from "../portals/PortalPicker";
import { screenFor } from "../screens/registry";

/** The first section of a portal the caller can actually open, or the bare base when they can open none. */
export function homePathFor(portal: PortalDef, can: (p: Parameters<ReturnType<typeof useAuth>["can"]>[0]) => boolean): string {
  const first = portal.sections.find((s) => can(s.permission));
  return first ? `/${portal.base}/${first.path}` : `/${portal.base}`;
}

/**
 * Where a sign-in lands.
 *
 * With more than one portal that is the PICKER, not a portal: choosing for somebody who holds four would
 * mean choosing wrong three times out of four, and there would be nothing on the landing screen to tell
 * them the other three exist. With exactly one it is that portal's first usable section, unchanged — a
 * picker with a single card is a click that asks a question with one answer.
 */
function useHomePath(): string {
  const { session, can } = useAuth();
  if (!session?.role) return "/login";
  const mine = portalsForRoles(session.roles);
  if (mine.length > 1) return "/portals";
  const portal = mine[0];
  if (!portal) return "/login";
  return homePathFor(portal, can);
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
  const path = location.pathname.replace(/\/+$/, "") || "/";

  if (!session) return <Navigate to="/login" replace />;
  if (!session.role) return <NoPortal />;

  // Resolve against the portal that OWNS this path, not the caller's primary.
  //
  // This used to be `portalForRole(session.role)`, which was correct while a session held one portal and is
  // the bug once it holds several: an org admin whose primary is `clinics_manager` typing `/admin/users`
  // was answered against the CLINIC portal, found no matching section, and got a 404 for a screen they were
  // granted. The permission check below is unchanged and still decides the answer — this only stops the
  // router asking the wrong portal the question.
  const mine = portalsForRoles(session.roles);
  const base = path.split("/")[1] ?? "";
  const portal = mine.find((p) => p.base === base) ?? mine[0];
  if (!portal) return <NoPortal />;

  // Bare portal base → that portal's own home, which for a base the caller holds is NOT the global home
  // (that would be the picker, sending `/admin` straight back to the screen they just left).
  if (path === `/${portal.base}`) return <Navigate to={homePathFor(portal, can)} replace />;

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

/**
 * The picker, or a redirect past it.
 *
 * OUTSIDE `AppShell` — it is the screen you are on when no portal is chosen, so there is no rail to render,
 * no branch to switch and no portal to name in the app bar. A caller holding exactly one portal never sees
 * it, however they arrived: a bookmark, a back button and the rail's own switcher all resolve the same way.
 */
function PortalPickerRoute() {
  const { session } = useAuth();
  const mine = portalsForRoles(session?.roles ?? []);
  if (mine.length === 0) return <NoPortal />;
  /*
    28.13 — a single-portal caller is NO LONGER redirected away from here.

    They still LAND in their portal: `useHomePath` only sends somebody to /portals when they hold more than
    one, so nobody gains a click on sign-in. What changed is that the switcher is now on every rail, so
    /portals became a place somebody can deliberately ask for — and answering that request with a redirect
    straight back made the button they had just pressed look broken.

    What they get is a page with one card. That is honest: it is the whole of what they hold, and seeing it
    is the answer to the question the button asks.
  */
  return <PortalPicker />;
}

function AuthedApp() {
  const home = useHomePath();
  return (
    <Routes>
      {/* Before the shell-wrapped catch-all, and deliberately not inside it. */}
      <Route path="/portals" element={<PortalPickerRoute />} />
      <Route
        path="*"
        element={
          <AppShell>
            <Routes>
              <Route index element={<Navigate to={home} replace />} />
              <Route path="*" element={<ResolveRoute />} />
            </Routes>
          </AppShell>
        }
      />
    </Routes>
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

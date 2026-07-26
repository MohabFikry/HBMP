import { useEffect, useRef } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, Icon, InlineAlert, useTheme, useToast } from "@mersal/design-system";
import { useAuth } from "../auth/AuthProvider";
import { auditClient } from "../audit/auditClient";
import { portalForRole } from "../portals/catalog";
import { L } from "../i18n/strings";

/**
 * 403 page for a forbidden deep link (US-071). Emits an audited `access.denied` event on mount (once) and
 * offers a "request access" affordance + a route back to the user's own portal — never a blank screen.
 */
export function Forbidden({ path }: { path: string }) {
  const { session } = useAuth();
  const { lang } = useTheme();
  const { toast } = useToast();
  const navigate = useNavigate();
  const emitted = useRef(false);

  useEffect(() => {
    if (emitted.current) return;
    emitted.current = true;
    auditClient.emit({
      type: "access.denied",
      actorUserId: session?.userId ?? null,
      actorRole: session?.role ?? null,
      path,
      reason: "forbidden-deep-link",
    });
  }, [path, session]);

  const home = session?.role ? `/${portalForRole(session.role).base}` : "/login";

  return (
    <Card style={{ padding: "var(--sp6)", maxWidth: 560, margin: "var(--sp8) auto" }}>
      <div style={{ display: "flex", gap: "var(--sp3)", alignItems: "center", marginBottom: "var(--sp3)" }}>
        <Icon name="cross" width={28} height={28} style={{ color: "var(--st-bad-fg)" }} />
        <h1 style={{ fontSize: "var(--fs-title-1)" }}>{L.forbiddenTitle[lang]}</h1>
      </div>
      <p className="muted">{L.forbiddenBody[lang]}</p>
      <InlineAlert tone="info">
        <span className="mono">{path}</span>
      </InlineAlert>
      <div style={{ display: "flex", gap: "var(--sp3)", marginTop: "var(--sp5)", flexWrap: "wrap" }}>
        <Button variant="primary" onClick={() => toast(L.requestSent[lang], "info")}>
          {L.requestAccess[lang]}
        </Button>
        <Button variant="secondary" onClick={() => navigate(home)}>
          {L.backToPortal[lang]}
        </Button>
      </div>
    </Card>
  );
}

export function NotFound() {
  const { lang } = useTheme();
  const navigate = useNavigate();
  const { session } = useAuth();
  const home = session?.role ? `/${portalForRole(session.role).base}` : "/login";
  return (
    <Card style={{ padding: "var(--sp6)", maxWidth: 560, margin: "var(--sp8) auto" }}>
      <h1 style={{ fontSize: "var(--fs-title-1)" }}>{L.notFoundTitle[lang]}</h1>
      <p className="muted">{L.notFoundBody[lang]}</p>
      <Button variant="secondary" onClick={() => navigate(home)}>
        {L.backToPortal[lang]}
      </Button>
    </Card>
  );
}

/**
 * Fail-closed landing (H6) for a caller who authenticated but whose realm role maps to no portal. It offers
 * only "sign out" — never a portal — and logs the denied session so the gap is visible in the audit trail.
 */
export function NoPortal() {
  const { lang } = useTheme();
  const { session, logout } = useAuth();
  const emitted = useRef(false);

  useEffect(() => {
    if (emitted.current) return;
    emitted.current = true;
    auditClient.emit({
      type: "access.denied",
      actorUserId: session?.userId ?? null,
      actorRole: null,
      path: window.location.pathname,
      reason: "no-portal-role",
    });
  }, [session]);

  return (
    <Card role="region" aria-label="no portal assigned" style={{ padding: "var(--sp6)", maxWidth: 560, margin: "var(--sp8) auto" }}>
      <div style={{ display: "flex", gap: "var(--sp3)", alignItems: "center", marginBottom: "var(--sp3)" }}>
        <Icon name="cross" width={28} height={28} style={{ color: "var(--st-bad-fg)" }} />
        <h1 style={{ fontSize: "var(--fs-title-1)" }}>{L.noPortalTitle[lang]}</h1>
      </div>
      <p className="muted">{L.noPortalBody[lang]}</p>
      <div style={{ marginTop: "var(--sp5)" }}>
        <Button variant="secondary" onClick={() => void logout()}>
          {L.signOut[lang]}
        </Button>
      </div>
    </Card>
  );
}

import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, InputField, Logo, useTheme } from "@mersal/design-system";
import { useAuth } from "../auth/AuthProvider";
import { portalForRole } from "../portals/catalog";
import { PORTALS } from "../portals/catalog";
import type { Role } from "../authz/permissions";
import { L } from "../i18n/strings";
import { LIVE } from "../config";

/**
 * Login (US-070): OIDC + MFA. The dev build shows a role picker (standing in for the IdP's account) plus
 * an MFA code step; on success the user lands on THEIR portal's home only. The live build redirects to
 * identity-service and this screen becomes the post-redirect callback — the surrounding flow is unchanged.
 *
 * The issuer is the platform's OWN (ASP.NET Identity + OpenIddict, Phase 17 / ADR-0015). Keycloak was
 * retired then; this screen still named it in the user-facing copy long afterwards, which told every
 * operator that their credentials went somewhere they no longer do.
 */
export function LoginPage() {
  const { login } = useAuth();
  const { lang } = useTheme();
  const navigate = useNavigate();
  const [role, setRole] = useState<Role>("reception");
  const [mfa, setMfa] = useState("");
  const [error, setError] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(undefined);
    if (!/^\d{6}$/.test(mfa)) {
      setError(L.mfaError[lang]);
      return;
    }
    setBusy(true);
    try {
      await login(role, mfa);
      const portal = portalForRole(role);
      navigate(`/${portal.base}`);
    } catch {
      setError(L.mfaError[lang]);
    } finally {
      setBusy(false);
    }
  }

  // Live mode: identity + MFA are owned by the issuer. A single button starts the auth-code + PKCE
  // redirect; on return the role (and thus portal) is derived from the token, not chosen here.
  async function onIssuerSignIn() {
    setBusy(true);
    try {
      await login(role, "000000"); // args ignored by the OIDC client — it redirects to the issuer
    } catch {
      setBusy(false);
      setError(L.mfaError[lang]);
    }
  }

  return (
    <div className="login-wrap">
      <Card style={{ padding: "var(--sp8)", width: "min(440px, 92vw)" }}>
        <div style={{ display: "flex", justifyContent: "center", marginBottom: "var(--sp5)" }}>
          <Logo variant="lockup" height={72} />
        </div>
        <h1 style={{ fontSize: "var(--fs-title-2)", textAlign: "center" }}>{L.loginTitle[lang]}</h1>
        <p className="muted" style={{ textAlign: "center", marginTop: "var(--sp2)" }}>
          {L.loginSub[lang]}
        </p>
        {LIVE ? (
          <div style={{ display: "grid", gap: "var(--sp4)", marginTop: "var(--sp5)" }}>
            <Button type="button" variant="primary" loading={busy} onClick={onIssuerSignIn}>
              {L.signIn[lang]}
            </Button>
            <p className="muted" style={{ textAlign: "center", fontSize: "var(--fs-footnote)" }}>
              {L.signInVia[lang]}
            </p>
          </div>
        ) : (
        <form onSubmit={onSubmit} style={{ display: "grid", gap: "var(--sp4)", marginTop: "var(--sp5)" }}>
          <div className="mrs-field">
            <label className="mrs-label" htmlFor="role">
              {L.chooseRole[lang]}
            </label>
            <select
              id="role"
              className="mrs-control"
              value={role}
              onChange={(e) => setRole(e.target.value as Role)}
            >
              {PORTALS.map((p) => (
                <option key={p.role} value={p.role}>
                  {p.eyebrow[lang]}
                </option>
              ))}
            </select>
          </div>
          <InputField
            label={L.mfaLabel[lang]}
            help={L.mfaHelp[lang]}
            error={error}
            inputMode="numeric"
            autoComplete="one-time-code"
            maxLength={6}
            value={mfa}
            onChange={(e) => setMfa(e.target.value.replace(/\D/g, ""))}
          />
          <Button type="submit" variant="primary" loading={busy}>
            {L.signIn[lang]}
          </Button>
        </form>
        )}
      </Card>
    </div>
  );
}

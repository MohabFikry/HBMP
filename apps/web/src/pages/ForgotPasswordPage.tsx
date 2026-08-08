import { useState } from "react";
import { Link } from "react-router-dom";
import { Button, Card, InlineAlert, InputField, Logo, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";
import { OIDC } from "../config";

/**
 * Asking for a reset link (ADR-0036 §6, phase 28.6).
 *
 * ============================================================================================================
 * WHY THE CONFIRMATION IS DELIBERATELY VAGUE — AND WHY THE 503 IS NOT
 * ============================================================================================================
 * The server answers 202 whether or not the account exists, so this screen must say something that is true
 * either way: *if that account exists, a link is on its way*. Anything more precise turns the page into a free
 * account-existence oracle that costs an attacker nothing — strictly worse than the login form, which at
 * least burns an attempt against a lockout counter.
 *
 * But the vagueness stops at delivery. When no email transport is configured the server answers **503**, and
 * this screen says the capability is unavailable rather than reporting a send. "We've emailed you a link"
 * when nothing was emailed is the platform's own forbidden pattern — a failed operation rendered as a clean
 * result — landing on the one screen a locked-out person reaches when nothing else works.
 */
export function ForgotPasswordPage() {
  const { lang } = useTheme();
  const [username, setUsername] = useState("");
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(undefined);
    setBusy(true);
    try {
      const csrfRes = await fetch(`${OIDC.authority}/connect/session/antiforgery`, { credentials: "same-origin" });
      const { token: csrf } = (await csrfRes.json()) as { token: string };
      const res = await fetch(`${OIDC.authority}/connect/password/forgot`, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-HBMP-CSRF": csrf },
        body: JSON.stringify({ username, lang }),
      });

      if (res.status === 503) {
        // The capability is absent, and says so. It does NOT fall through to the reassuring message.
        setError(L.forgotUnavailable[lang]);
        return;
      }
      if (!res.ok) {
        setError(L.signInUnavailable[lang]);
        return;
      }
      setSent(true);
    } catch {
      setError(L.signInUnavailable[lang]);
    } finally {
      setBusy(false);
    }
  }

  return (
    <main id="main" className="login-wrap">
      <Card style={{ padding: "var(--sp8)", width: "min(440px, 92vw)" }}>
        <div style={{ display: "flex", justifyContent: "center", marginBottom: "var(--sp5)" }}>
          <Logo variant="lockup" height={72} />
        </div>
        <h1 style={{ fontSize: "var(--fs-title-2)", textAlign: "center" }}>{L.forgotTitle[lang]}</h1>

        <div aria-live="polite" style={{ marginTop: "var(--sp4)" }}>
          {error && <InlineAlert tone="bad" data-testid="forgot-error">{error}</InlineAlert>}
        </div>

        {sent ? (
          <div style={{ display: "grid", gap: "var(--sp4)", marginTop: "var(--sp4)" }}>
            <InlineAlert tone="info" data-testid="forgot-sent">{L.forgotSent[lang]}</InlineAlert>
            {/* Said here too: a reset does not solve a lost authenticator, and finding that out after
                changing a password is finding out too late. */}
            <p className="muted" style={{ fontSize: "var(--fs-caption)" }}>{L.resetKeepsTwoFactor[lang]}</p>
            <Link to="/login">{L.signInBack[lang]}</Link>
          </div>
        ) : (
          <>
            <p className="muted" style={{ textAlign: "center", marginTop: "var(--sp2)" }}>
              {L.forgotSub[lang]}
            </p>
            <form onSubmit={onSubmit} style={{ display: "grid", gap: "var(--sp4)", marginTop: "var(--sp4)" }}>
              <InputField
                label={L.usernameLabel[lang]}
                autoComplete="username"
                value={username}
                required
                onChange={(e) => setUsername(e.target.value)}
              />
              <Button type="submit" variant="primary" loading={busy}>
                {L.forgotSubmit[lang]}
              </Button>
              <Link to="/login" style={{ textAlign: "center" }}>{L.signInBack[lang]}</Link>
            </form>
          </>
        )}
      </Card>
    </main>
  );
}

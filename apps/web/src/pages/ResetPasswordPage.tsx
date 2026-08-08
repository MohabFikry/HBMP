import { useState } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { Button, Card, InlineAlert, InputField, Logo, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";
import { OIDC } from "../config";

/**
 * Choosing a new password from a reset link (ADR-0036 §6, phase 28.6).
 *
 * ============================================================================================================
 * WHAT THIS SCREEN HAS TO SAY BEFORE ANYBODY TYPES
 * ============================================================================================================
 * Two things, and both are stated up front rather than discovered afterwards:
 *
 *   1. **Every session ends.** A reset revokes the account's sessions and refresh tokens — because if it was
 *      requested BECAUSE the account was compromised, leaving the attacker's session running defeats the
 *      whole exercise. Somebody with three tabs open deserves to know that before they commit.
 *   2. **Two-step verification is untouched.** A reset does not clear the second factor, and a user who has
 *      one will still need their authenticator code afterwards. Said here so nobody resets a password hoping
 *      it will solve a lost phone — the answer to that is a recovery code, and after that an administrator.
 *
 * The screen is reachable WITHOUT a session, by design: the person using it cannot sign in.
 */
export function ResetPasswordPage() {
  const [params] = useSearchParams();
  const { lang } = useTheme();
  const navigate = useNavigate();

  const userId = params.get("u") ?? "";
  const token = params.get("t") ?? "";

  const [password, setPassword] = useState("");
  const [confirm, setConfirm] = useState("");
  const [error, setError] = useState<string | undefined>();
  const [fieldError, setFieldError] = useState<string | undefined>();
  const [done, setDone] = useState(false);
  const [busy, setBusy] = useState(false);

  // A link with nothing in it is not a form to fill in. Rendering the fields anyway would let somebody type a
  // new password twice and only then be told the link was never valid.
  const linkUsable = userId.length > 0 && token.length > 0;

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(undefined);
    setFieldError(undefined);

    // Checked here as well as by the server, because the server compares nothing — it only ever sees one
    // password, so a typo in the confirmation would silently set the wrong one.
    if (password !== confirm) {
      setFieldError(L.resetMismatch[lang]);
      return;
    }

    setBusy(true);
    try {
      const csrfRes = await fetch(`${OIDC.authority}/connect/session/antiforgery`, { credentials: "same-origin" });
      const { token: csrf } = (await csrfRes.json()) as { token: string };
      const res = await fetch(`${OIDC.authority}/connect/password/reset`, {
        method: "POST",
        credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-HBMP-CSRF": csrf },
        body: JSON.stringify({ userId, token, newPassword: password }),
      });

      if (res.ok) {
        setDone(true);
        return;
      }

      // 422 carries password-policy advice the person can act on — "at least 12 characters" tells them what
      // to do and reveals nothing about any account. Everything else is the one invalid-link message.
      const problem = (await res.json().catch(() => null)) as { detail?: string; title?: string } | null;
      if (res.status === 422 && problem?.detail) setFieldError(problem.detail);
      else setError(L.resetInvalidLink[lang]);
    } catch {
      // A failed READ is never rendered as a rejected password.
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
        <h1 style={{ fontSize: "var(--fs-title-2)", textAlign: "center" }}>{L.resetTitle[lang]}</h1>

        {done ? (
          <div style={{ display: "grid", gap: "var(--sp4)", marginTop: "var(--sp5)" }}>
            <InlineAlert tone="ok" data-testid="reset-done">{L.resetDone[lang]}</InlineAlert>
            <Button variant="primary" onClick={() => navigate("/login", { replace: true })}>
              {L.signIn[lang]}
            </Button>
          </div>
        ) : !linkUsable ? (
          <div style={{ marginTop: "var(--sp5)" }}>
            <InlineAlert tone="bad" data-testid="reset-error">{L.resetInvalidLink[lang]}</InlineAlert>
          </div>
        ) : (
          <>
            <p className="muted" style={{ textAlign: "center", marginTop: "var(--sp2)" }}>
              {L.resetSub[lang]}
            </p>
            {/* Stated BEFORE the fields, not after the deed. */}
            <div style={{ marginTop: "var(--sp4)" }}>
              <InlineAlert tone="info" data-testid="reset-consequences">
                {L.resetEndsSessions[lang]} {L.resetKeepsTwoFactor[lang]}
              </InlineAlert>
            </div>

            <div aria-live="polite" style={{ marginTop: "var(--sp3)" }}>
              {error && <InlineAlert tone="bad" data-testid="reset-error">{error}</InlineAlert>}
            </div>

            <form onSubmit={onSubmit} style={{ display: "grid", gap: "var(--sp4)", marginTop: "var(--sp4)" }}>
              <InputField
                label={L.resetNewPassword[lang]}
                type="password"
                autoComplete="new-password"
                value={password}
                required
                onChange={(e) => setPassword(e.target.value)}
              />
              <InputField
                label={L.resetConfirmPassword[lang]}
                type="password"
                autoComplete="new-password"
                error={fieldError}
                value={confirm}
                required
                onChange={(e) => setConfirm(e.target.value)}
              />
              <Button type="submit" variant="primary" loading={busy}>
                {L.resetSubmit[lang]}
              </Button>
            </form>
          </>
        )}
      </Card>
    </main>
  );
}

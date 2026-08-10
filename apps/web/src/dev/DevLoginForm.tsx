import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, ComboboxField, InputField, Logo, useTheme } from "@mersal/design-system";
import { useAuth } from "../auth/AuthProvider";
import { PORTALS, portalForRole } from "../portals/catalog";
import type { Role } from "../authz/permissions";
import { L } from "../i18n/strings";

/**
 * The no-backend sign-in: pick a portal, type any six digits. **Fixture builds only.**
 *
 * Lifted verbatim out of `LoginPage` — same markup, same ids, same behaviour — for one reason: while it sat
 * inside that file it was compiled into every bundle, including the live one, where the `LIVE` branch
 * returns before it can ever render. A role picker that hands out `super_admin` is not something to ship
 * behind a branch and reason about; it is something to not ship. It is reachable only through
 * `src/dev/fixtures.ts`, which the live build swaps for a refusing stub.
 */
export function DevLoginForm() {
  const { login } = useAuth();
  const { lang } = useTheme();
  const navigate = useNavigate();

  const [role, setRole] = useState<Role>("reception");
  // Extra portals BESIDE the primary, so the picker and the switcher can be reached with no issuer running.
  // Held apart from `role` rather than as one multi-select: the first role is the primary and decides the
  // display name and the fallback landing portal, and a set with no order cannot express that.
  const [extras, setExtras] = useState<Role[]>([]);
  const [mfa, setMfa] = useState("");
  const [fieldError, setFieldError] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);

  const held: Role[] = [role, ...extras.filter((r) => r !== role)];

  async function onSubmit(e: React.FormEvent) {
    e.preventDefault();
    setFieldError(undefined);
    if (!/^\d{6}$/.test(mfa)) {
      setFieldError(L.mfaError[lang]);
      return;
    }
    setBusy(true);
    try {
      await login(held, mfa);
      // More than one portal lands on the picker, exactly as a live sign-in does — otherwise the dev build
      // would take a different route through the app than the one being shipped.
      navigate(held.length > 1 ? "/portals" : `/${portalForRole(role).base}`);
    } catch {
      setFieldError(L.mfaError[lang]);
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
        <h1 style={{ fontSize: "var(--fs-title-2)", textAlign: "center" }}>{L.loginTitle[lang]}</h1>
        <p className="muted" style={{ textAlign: "center", marginTop: "var(--sp2)" }}>
          {L.loginSub[lang]}
        </p>
        <form onSubmit={onSubmit} style={{ display: "grid", gap: "var(--sp4)", marginTop: "var(--sp5)" }}>
          {/* Dev-only, and converted with the rest so "no native <select> ships" needs no exception list —
              an exception nobody can see the reason for is one the next screen copies. Twenty-three portals
              is also past the point where scrolling to a role beats typing it. */}
          <ComboboxField
            id="role"
            label={L.chooseRole[lang]}
            value={role}
            onChange={(v) => setRole(v as Role)}
            options={PORTALS.map((p) => ({ value: p.role, label: p.eyebrow[lang], keywords: p.role }))}
          />
          <fieldset className="dev-extra-portals">
            <legend>{L.extraPortals[lang]}</legend>
            <p className="muted dev-extra-portals-help">{L.extraPortalsHelp[lang]}</p>
            <div className="dev-extra-portals-list mrs-scroll">
              {PORTALS.filter((p) => p.role !== role).map((p) => (
                <label key={p.role} className="dev-extra-portal">
                  <input
                    type="checkbox"
                    checked={extras.includes(p.role)}
                    /* Read before the updater — see the same note in AccessCatalogue: `currentTarget` is
                       null by the time a state updater runs. */
                    onChange={(e) => {
                      const on = e.currentTarget.checked;
                      setExtras((prev) => (on ? [...prev, p.role] : prev.filter((r) => r !== p.role)));
                    }}
                  />
                  <span>{p.eyebrow[lang]}</span>
                </label>
              ))}
            </div>
          </fieldset>
          <InputField
            label={L.mfaLabel[lang]}
            help={L.mfaHelp[lang]}
            error={fieldError}
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
      </Card>
    </main>
  );
}

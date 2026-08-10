import { useState } from "react";
import { Link } from "react-router-dom";
import { Button, InlineAlert, InputField, Logo, useTheme } from "@mersal/design-system";
import { silentAuthorize } from "../auth/oidcClient";
import { SessionClient, SessionUnavailableError, type MembershipOption, type SessionState } from "../auth/sessionApi";
import { LangGlyph, MoonIcon, SunIcon } from "../shell/controlGlyphs";
import { L } from "../i18n/strings";
import { FIXTURES } from "@dev/fixtures";
import { LIVE } from "../config";

/**
 * Login (US-070; rebuilt in phase 28.4 per ADR-0036).
 *
 * ============================================================================================================
 * THE SIGN-IN HAPPENS HERE NOW
 * ============================================================================================================
 * This screen used to be a single button whose only job was to navigate the browser to identity-service — a
 * different origin, a different visual language, and 349 lines of hand-written HTML in a C# file that nothing
 * kept in step with the design system. That is what "the app moves to another platform to log in" described.
 *
 * The credentials, the second factor and the organization choice are now asked for here, by
 * `/connect/session/*`, which sets the issuer's ordinary cookie and answers with a STATUS rather than a
 * token. Only once every factor is satisfied does {@link silentAuthorize} run the UNCHANGED
 * authorization-code + PKCE flow with `prompt=none`, which completes without rendering anything. So nothing
 * about how tokens are minted, narrowed, refreshed or validated changed — only where the person types.
 *
 * ============================================================================================================
 * WHY THIS IS A SEQUENCE AND NOT A FORM
 * ============================================================================================================
 * Signing in has up to four steps, and which ones apply depends on the account: a second factor if one is
 * enrolled, an organization choice if the identity holds more than one active membership. The server decides
 * and this screen follows. It does NOT infer the next step from what it knows about the user — it has no
 * business knowing, and a client that guessed would be a second authorization system with a worse view.
 *
 * The dev build (`LIVE=0`) keeps its role picker untouched — same markup, same behaviour — but it now lives
 * in `src/dev/DevLoginForm.tsx` and is reached through `@dev/fixtures`, so a live bundle does not contain a
 * "sign in as any role with any six digits" form at all. It is how the frontend suite runs without a backend.
 */

/** Field and button glyphs. Inline SVG rather than an icon package: three shapes do not justify a
 *  dependency, and `currentColor` lets them follow the theme's text tokens for free. */
const PersonIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
    <circle cx="12" cy="8" r="3.5" />
    <path d="M4.5 20a7.5 7.5 0 0 1 15 0" strokeLinecap="round" />
  </svg>
);
const LockIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" aria-hidden="true">
    <rect x="4.5" y="10.5" width="15" height="9.5" rx="2.5" />
    <path d="M8 10.5V7.5a4 4 0 0 1 8 0v3" strokeLinecap="round" />
  </svg>
);
const ArrowIcon = () => (
  <svg className="login-submit-arrow" width="18" height="18" viewBox="0 0 24 24" fill="none"
       stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M5 12h13M13 6l6 6-6 6" />
  </svg>
);

type Step = "credentials" | "two_factor" | "membership";

export function LoginPage() {
  const { lang, setLang, theme, setTheme } = useTheme();

  const [client] = useState(() => new SessionClient());
  const [step, setStep] = useState<Step>("credentials");
  const [username, setUsername] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const [useRecovery, setUseRecovery] = useState(false);
  const [remember, setRemember] = useState(false);
  const [memberships, setMemberships] = useState<MembershipOption[]>([]);
  const [chosen, setChosen] = useState<string>("");

  const [error, setError] = useState<string | undefined>();
  const [fieldError, setFieldError] = useState<string | undefined>();
  const [busy, setBusy] = useState(false);

  /**
   * Turn a server status into what the screen does next.
   *
   * Every branch is named. A status this build has never heard of is treated as UNAVAILABLE rather than as a
   * refusal — an unknown answer means the client and the issuer disagree about the protocol, which is an
   * operational fault, and reporting it as "your password is wrong" would send someone to reset a password
   * that was never the problem.
   */
  async function apply(state: SessionState): Promise<void> {
    switch (state.status) {
      case "authenticated":
        // The cookie is set. Everything from here is the ordinary PKCE flow.
        await silentAuthorize();
        return;
      case "two_factor_required":
        setStep("two_factor");
        setCode("");
        return;
      case "membership_selection_required":
        setMemberships(state.memberships ?? []);
        setChosen(state.memberships?.[0]?.membershipId ?? "");
        setStep("membership");
        return;
      case "no_membership":
        setError(L.signInNoMembership[lang]);
        return;
      case "locked": {
        const minutes = Math.max(1, Math.round((state.retryAfterSeconds ?? 60) / 60));
        setError(`${L.signInLocked[lang]} ${L.signInLockedWait[lang].replace("{n}", String(minutes))}`);
        return;
      }
      case "invalid_credentials":
        // The one message covering an unknown username, a wrong password and a deactivated account. The
        // server already refuses to distinguish them; saying more here would rebuild the oracle in the
        // browser. On the second-factor step the same status means the CODE was wrong, and says so.
        setFieldError(step === "two_factor" ? L.twoFactorInvalid[lang] : L.signInInvalid[lang]);
        return;
      default:
        setError(L.signInUnavailable[lang]);
    }
  }

  /** One place that runs a step, so no branch can forget to clear the previous error or drop the busy flag. */
  async function run(action: () => Promise<SessionState>): Promise<void> {
    setError(undefined);
    setFieldError(undefined);
    setBusy(true);
    try {
      await apply(await action());
    } catch (e) {
      // A failed READ is never rendered as a credential verdict.
      setError(e instanceof SessionUnavailableError ? L.signInUnavailable[lang] : L.signInUnavailable[lang]);
    } finally {
      setBusy(false);
    }
  }

  const onCredentials = (e: React.FormEvent) => {
    e.preventDefault();
    void run(() => client.signIn(username, password, remember));
  };

  const onSecondFactor = (e: React.FormEvent) => {
    e.preventDefault();
    void run(() => client.submitSecondFactor(code, useRecovery));
  };

  const onMembership = (e: React.FormEvent) => {
    e.preventDefault();
    void run(() => client.chooseMembership(chosen));
  };

  if (LIVE) {
    return (
      // <main>, not <div>. The signed-in shell provides the main landmark; the login page sits OUTSIDE it and
      // so had none at all — the one page every user meets first, with nothing for a screen reader to jump to
      // and no skip-link target.
      <main id="main" className="login-split">
        <section className="login-hero">
          {/* Three decorative layers, each its own element and each aria-hidden. They carry no meaning and a
              screen reader announcing "image" three times before the headline is pure noise. */}
          <span className="login-hero-waves" aria-hidden="true" />
          <span className="login-hero-glow" aria-hidden="true" />
          <span className="login-hero-glass" aria-hidden="true" />
          <span className="login-hero-grain" aria-hidden="true" />

          <div>
            {/* onDark: the hero is deep teal in BOTH themes, so the theme-picked lockup would put a teal
                wordmark on teal in light mode — about 2:1, the exact problem the dark asset exists to fix. */}
            <Logo variant="lockup" height={104} onDark />
          </div>

          <div>
            <span className="login-kicker">{L.heroKicker[lang]}</span>
            {/* The break is MARKUP, not a newline inside the string: a \n collapses in HTML, and an Arabic
                translation may want to break somewhere else entirely. */}
            <h1 className="login-headline">
              {L.heroHeadlineLead[lang]}
              <br />
              {L.heroHeadlineRest[lang]}
            </h1>
            <p className="login-lede login-hero-compact-hide">{L.heroLede[lang]}</p>
          </div>

          {/* A spacer, so the three-part `space-between` rhythm survives the tiles being removed and the
              headline does not drift to the bottom edge. */}
          <div aria-hidden="true" />
        </section>

        <section className="login-panel">
          <div className="login-panel-controls">
            {/* Icon + the code of the language it switches TO. The glyph alone said nothing about what would
                happen, and a bare "ع" at button-label size read as a stray character rather than a control. */}
            <button
              type="button"
              className="login-icon-btn"
              aria-label={L.toggleLanguage[lang]}
              onClick={() => setLang(lang === "ar" ? "en" : "ar")}
            >
              {/* The target language's own code, drawn as SVG TEXT rather than laid out as HTML.
                  ------------------------------------------------------------------------------------------
                  Not decoration — it is the only way to centre it. Arabic faces carry tall ascent and
                  descent to leave room for diacritics, so the ink of "ع" sits low inside its em box: HTML
                  centres the LINE BOX, which is geometrically right and optically wrong, and no amount of
                  `align-items: center` can see the difference. `dominant-baseline="central"` centres on the
                  font's own central baseline, computed by the renderer from the real metrics of whichever
                  face resolves — so it is correct for Cairo, for a fallback, and for "EN" too.
                  It also gives both buttons an <svg> of identical box size, which is what makes the pair
                  line up rather than merely being the same width. */}
              <LangGlyph code={lang === "ar" ? "EN" : "ع"} />
            </button>
            <button
              type="button"
              className="login-icon-btn"
              aria-label={L.toggleTheme[lang]}
              onClick={() => setTheme(theme === "dark" ? "light" : "dark")}
            >
              {theme === "dark" ? <SunIcon /> : <MoonIcon />}
            </button>
          </div>

          <div className="login-card-wrap">
            <div className="login-card">
            <h1 className="login-title">
              {step === "two_factor"
                ? L.twoFactorTitle[lang]
                : step === "membership"
                  ? L.membershipTitle[lang]
                  : L.loginTitle[lang]}
            </h1>
            <p className="login-sub">
              {step === "two_factor"
                ? L.twoFactorSub[lang]
                : step === "membership"
                  ? L.membershipSub[lang]
                  : L.loginSub[lang]}
            </p>

            {/* aria-live so an outcome that replaces no visible content is still announced. */}
            <div aria-live="polite" style={{ marginTop: "var(--sp4)" }}>
              {error && <InlineAlert tone="bad" data-testid="login-error">{error}</InlineAlert>}
            </div>

            {step === "credentials" && (
              <form onSubmit={onCredentials} style={{ display: "grid", gap: "var(--sp4)", marginTop: "var(--sp4)" }}>
                {/* The icon sits in a wrapper rather than inside InputField, so the shared field component
                    keeps ONE shape for the whole application. `inset-inline-start` in the stylesheet is what
                    makes it swap sides with the text under RTL — a `left` would put it on top of Arabic. */}
                <span className="login-field-icon">
                  <PersonIcon />
                  <InputField
                    label={L.usernameLabel[lang]}
                    autoComplete="username"
                    value={username}
                    required
                    onChange={(e) => setUsername(e.target.value)}
                  />
                </span>
                <span className="login-field-icon">
                  <LockIcon />
                  <InputField
                    label={L.passwordLabel[lang]}
                    type="password"
                    autoComplete="current-password"
                    error={fieldError}
                    value={password}
                    required
                    onChange={(e) => setPassword(e.target.value)}
                  />
                </span>

                <div className="login-meta">
                  {/* Off by default, and it stays off unless somebody ticks it. Mersal's clinic workstations
                      are SHARED, so a persistent cookie means the next person at that terminal is signed in
                      as the last — worth knowing before it is ever turned on by policy. */}
                  <label className="login-remember">
                    <input
                      type="checkbox"
                      checked={remember}
                      onChange={(e) => setRemember(e.target.checked)}
                    />
                    <span>{L.rememberDevice[lang]}</span>
                  </label>
                  <Link to="/forgot-password">{L.signInForgot[lang]}</Link>
                </div>

                <button type="submit" className="login-submit" disabled={busy}>
                  {busy ? L.signInWorking[lang] : L.signIn[lang]}
                  <ArrowIcon />
                </button>
              </form>
            )}

            {step === "two_factor" && (
              <form onSubmit={onSecondFactor} style={{ display: "grid", gap: "var(--sp4)", marginTop: "var(--sp4)" }}>
                <InputField
                  label={useRecovery ? L.twoFactorRecoveryLabel[lang] : L.twoFactorCode[lang]}
                  // A recovery code is not six digits, so the numeric hints belong to the TOTP case only —
                  // an inputMode of "numeric" on a recovery code gives a phone keyboard that cannot type it.
                  inputMode={useRecovery ? undefined : "numeric"}
                  autoComplete="one-time-code"
                  maxLength={useRecovery ? 32 : 6}
                  error={fieldError}
                  value={code}
                  required
                  onChange={(e) => setCode(useRecovery ? e.target.value : e.target.value.replace(/\D/g, ""))}
                />
                <Button type="submit" variant="primary" loading={busy}>
                  {busy ? L.signInWorking[lang] : L.signIn[lang]}
                </Button>
                {/* A lost authenticator has an answer, and it is on the screen where you discover you lost it. */}
                <Button
                  type="button"
                  variant="ghost"
                  onClick={() => {
                    setUseRecovery((r) => !r);
                    setCode("");
                    setFieldError(undefined);
                  }}
                >
                  {useRecovery ? L.twoFactorUseCode[lang] : L.twoFactorUseRecovery[lang]}
                </Button>
              </form>
            )}

            {step === "membership" && (
              <form onSubmit={onMembership} style={{ display: "grid", gap: "var(--sp4)", marginTop: "var(--sp4)" }}>
                {/* The design system's own choice-row vocabulary (`.mrs-choice` / `.mrs-choice-opt`), not
                    inline styles: it already gives a 44px hit target, a visible boundary and a tint when
                    selected, and it makes the whole ROW the label rather than the 18px circle. Re-inventing
                    that here would be a second answer to a question the design system has already settled. */}
                <fieldset className="mrs-choice">
                  <legend className="mrs-label">{L.membershipTitle[lang]}</legend>
                  {memberships.map((m) => (
                    <label key={m.membershipId} className="mrs-choice-opt">
                      <input
                        type="radio"
                        name="membership"
                        value={m.membershipId}
                        checked={chosen === m.membershipId}
                        onChange={() => setChosen(m.membershipId)}
                      />
                      <span>
                        <strong>{m.tenantId}</strong>
                        {m.roles.length > 0 && <span className="muted"> · {m.roles.join(", ")}</span>}
                      </span>
                    </label>
                  ))}
                </fieldset>
                <Button type="submit" variant="primary" loading={busy} disabled={!chosen}>
                  {L.membershipContinue[lang]}
                </Button>
              </form>
            )}

            </div>
            <p className="login-help">{L.signInHelp[lang]}</p>
          </div>
        </section>
      </main>
    );
  }

  // The no-backend build: the role picker, which is a separate module so that it is ABSENT from a live
  // bundle rather than merely unreachable past the branch above. See src/dev/fixtures.ts.
  return <FIXTURES.LoginForm />;
}

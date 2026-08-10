import { useMemo } from "react";
import { useNavigate } from "react-router-dom";
import { Icon, Logo, useTheme } from "@mersal/design-system";
import { useAuth } from "../auth/AuthProvider";
import { L } from "../i18n/strings";
import { LangGlyph, MoonIcon, SunIcon } from "../shell/controlGlyphs";
import { ZONES, portalsForRoles, type PortalDef, type ZoneDef } from "./catalog";

/**
 * Portal picker — where a sign-in lands when the caller holds more than one portal.
 *
 * ==========================================================================================================
 * WHY THIS SCREEN EXISTS
 * ==========================================================================================================
 * The session used to carry ONE role, so there was nothing to choose between: `roleFromClaimRoles` read a
 * token that might name four portals, returned the first by priority, and the other three were discarded
 * without trace. A clinics manager who is also an org admin could reach exactly one of the two portals they
 * had been granted, and no screen anywhere would have told them the second existed.
 *
 * ==========================================================================================================
 * WHAT IT IS NOT
 * ==========================================================================================================
 * It is NOT an authorization boundary. It renders the portals the caller's token already names, and every
 * one of those routes re-checks the permission on entry and is re-authorized again by the server on each
 * call. Hiding a card here would not withhold anything — a route the SPA hides is a route the SPA can be
 * persuaded to unhide — and showing one cannot grant anything.
 *
 * The zones are the same kind of thing: a reading aid, not a scope. Nothing is permitted by zone.
 */
export function PortalPicker() {
  const { session, can, logout } = useAuth();
  const { lang, theme, setLang, setTheme } = useTheme();
  const navigate = useNavigate();

  const portals = useMemo(() => portalsForRoles(session?.roles ?? []), [session?.roles]);

  // Grouped in the ZONES order, and a zone with nothing in it is not rendered — an empty "Fulfillment"
  // heading tells a finance officer only that portals they cannot have exist somewhere.
  const grouped: Array<{ zone: ZoneDef; portals: PortalDef[] }> = ZONES.map((zone) => ({
    zone,
    portals: portals.filter((p) => p.zone === zone.key),
  })).filter((g) => g.portals.length > 0);

  return (
    <main id="main" className="picker-page">
      <header className="picker-bar" role="banner">
        <Logo variant="lockup" height={44} />
        <div className="picker-bar-actions">
          <button
            type="button"
            className="login-icon-btn"
            aria-label={L.toggleLanguage[lang]}
            onClick={() => setLang(lang === "ar" ? "en" : "ar")}
          >
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
          <button type="button" className="picker-signout" onClick={() => void logout("user")}>
            {L.signOut[lang]}
          </button>
        </div>
      </header>

      <h1 className="picker-greeting">{L.welcomeBack[lang].replace("{name}", firstNameOf(session?.displayName))}</h1>
      <p className="picker-lede">{L.portalPickerLede[lang]}</p>

      {grouped.map(({ zone, portals: inZone }) => (
        <section key={zone.key} className="picker-zone" aria-labelledby={`zone-${zone.key}`}>
          {/* The heading and the hairline are siblings in a flex row, so the rule takes whatever width the
              label leaves — under RTL that is the other end, with no rule of its own to write. */}
          <div className="picker-zone-head">
            <h2 className="picker-zone-label" id={`zone-${zone.key}`}>
              {zone.label[lang]}
            </h2>
            <span className="picker-zone-rule" aria-hidden="true" />
          </div>
          <div className="picker-grid">
            {inZone.map((portal) => {
              // The count of what THIS caller can open, not the catalog total. A number that is true for
              // somebody else is worse than no number: it is a promise the portal does not keep.
              const count = portal.sections.filter((s) => can(s.permission)).length;
              const first = portal.sections.find((s) => can(s.permission));
              return (
                <button
                  key={portal.base + portal.role}
                  type="button"
                  className="picker-card"
                  onClick={() => navigate(first ? `/${portal.base}/${first.path}` : `/${portal.base}`)}
                >
                  <span className="picker-card-tile" aria-hidden="true">
                    <Icon name={portal.icon} />
                  </span>
                  <span className="picker-card-name">{portal.title[lang]}</span>
                  <span className="picker-card-desc">{portal.description[lang]}</span>
                  <span className="picker-card-meta">
                    <span className="picker-card-dot" style={{ background: zone.dot }} aria-hidden="true" />
                    {count === 1
                      ? L.portalSectionsOne[lang]
                      : L.portalSections[lang].replace("{n}", String(count))}
                  </span>
                </button>
              );
            })}
          </div>
        </section>
      ))}
    </main>
  );
}

/**
 * The name to greet somebody by.
 *
 * Display names in this system carry titles — "Dr. Karim", "Nurse Mona", "Reham (Reception)" — and the
 * first whitespace-delimited token of those is "Dr.", which is not a greeting. Honorifics are stripped
 * first, and a parenthesised role suffix with them. Falling back to the whole string is deliberate: a name
 * this does not recognise should be shown intact rather than truncated into something wrong.
 */
const HONORIFICS = new Set(["dr", "dr.", "mr", "mr.", "mrs", "mrs.", "ms", "ms.", "prof", "prof.", "nurse", "د", "د.", "أ", "أ.", "م", "م."]);

export function firstNameOf(displayName: string | undefined): string {
  const cleaned = (displayName ?? "").replace(/\s*\([^)]*\)\s*$/, "").trim();
  if (!cleaned) return "";
  const parts = cleaned.split(/\s+/);
  const head = parts.findIndex((p) => !HONORIFICS.has(p.toLowerCase()));
  // Every token is an honorific (a display name of just "Dr."): show it rather than an empty greeting.
  return head === -1 ? cleaned : parts[head];
}

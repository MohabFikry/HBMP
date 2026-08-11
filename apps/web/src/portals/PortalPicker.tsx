import { useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Icon, Logo, SearchField, useTheme } from "@mersal/design-system";
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
  const [query, setQuery] = useState("");
  const searchRef = useRef<HTMLInputElement>(null);

  const portals = useMemo(() => portalsForRoles(session?.roles ?? []), [session?.roles]);

  /*
    THE SEARCH MATCHES BOTH LANGUAGES, always.

    Not the current one. Half this platform's vocabulary is learned in English from colleagues and screens
    while the interface is read in Arabic — somebody who knows the place as "Call Centre" should not have to
    switch language to find it, and the reverse holds for an English reader who knows a portal by its Arabic
    name. Matching both costs one extra string per card and removes an entire class of "it isn't there".

    Section labels are in the haystack too, because the question behind the search is usually a TASK rather
    than a portal name: "eligibility" is how somebody looks for Reception, and the portal's own title does
    not contain that word anywhere.
  */
  const needle = query.trim().toLowerCase();
  const haystack = useMemo(() => {
    const map = new Map<string, string>();
    for (const p of portals) {
      map.set(
        p.base + p.role,
        [p.title.en, p.title.ar, p.eyebrow.en, p.eyebrow.ar, p.description.en, p.description.ar,
         ...p.sections.flatMap((s) => [s.label.en, s.label.ar])].join(" ").toLowerCase(),
      );
    }
    return map;
  }, [portals]);

  const matches = useMemo(
    () => (needle ? portals.filter((p) => (haystack.get(p.base + p.role) ?? "").includes(needle)) : portals),
    [portals, haystack, needle],
  );

  // Grouped in the ZONES order, and a zone with nothing in it is not rendered — an empty "Fulfillment"
  // heading tells a finance officer only that portals they cannot have exist somewhere. The same rule does
  // the work for a search: a zone whose cards are all filtered out simply stops being a heading.
  const grouped: Array<{ zone: ZoneDef; portals: PortalDef[] }> = ZONES.map((zone) => ({
    zone,
    portals: matches.filter((p) => p.zone === zone.key),
  })).filter((g) => g.portals.length > 0);

  /** Where a card goes: the first section this caller may actually open, not the portal's first section. */
  const openPortal = (portal: PortalDef) => {
    const first = portal.sections.find((s) => can(s.permission));
    navigate(first ? `/${portal.base}/${first.path}` : `/${portal.base}`);
  };

  // "/" focuses the search, the same shortcut the app bar binds inside a portal. Guarded on the active
  // element so typing a slash INTO the field does not re-focus it and swallow the character.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      const el = document.activeElement;
      const typing = el instanceof HTMLInputElement || el instanceof HTMLTextAreaElement;
      if (e.key === "/" && !typing) {
        e.preventDefault();
        searchRef.current?.focus();
      }
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, []);

  return (
    <div className="picker-shell">
      {/*
        THE SYSTEM'S APP BAR, not a second header that resembles it.

        This screen used to carry its own `.picker-bar` — a flex row inside the page's own 1240px column, so
        it stopped short of the window edge and scrolled away, while every other screen in the product has a
        full-bleed sticky glass bar. Two bars that are nearly the same is the worse outcome: the difference
        reads as a rendering fault rather than as a different screen.

        The CONTENTS differ, and that part is deliberate. No branch switcher and no notification bell: both
        belong to a portal, and the whole premise here is that one has not been chosen yet. What replaces them
        is the pair the picker has always needed — language and theme — plus sign-out.
      */}
      <header className="mrs-glass app-bar picker-appbar" role="banner">
        <Logo variant="lockup" height={48} />
        <div className="app-search">
          <SearchField
            ref={searchRef}
            aria-label={L.searchPortals[lang]}
            placeholder={L.searchPortals[lang]}
            value={query}
            onChange={(e) => setQuery(e.currentTarget.value)}
            onKeyDown={(e) => {
              // Enter opens the only thing left, and ONLY when there is exactly one. Opening "the first
              // match" out of several would make the key mean something different on every keystroke.
              if (e.key === "Enter" && matches.length === 1) {
                e.preventDefault();
                openPortal(matches[0]);
              }
              if (e.key === "Escape") setQuery("");
            }}
          />
        </div>
        <div className="app-actions">
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

      <main id="main" className="picker-page">
      <h1 className="picker-greeting">{L.welcomeBack[lang].replace("{name}", firstNameOf(session?.displayName))}</h1>
      <p className="picker-lede">{L.portalPickerLede[lang]}</p>

      {/* The count is ANNOUNCED, not just rendered. Filtering a card grid changes the page silently for
          anyone not looking at it, and "no matches" is the outcome most worth hearing. */}
      <div className="picker-status" role="status" aria-live="polite">
        {needle
          ? matches.length === 0
            ? L.portalsNoMatch[lang]
            : L.portalsMatch[lang].replace("{n}", String(matches.length))
          : ""}
      </div>

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
              return (
                <button
                  key={portal.base + portal.role}
                  type="button"
                  className="picker-card"
                  onClick={() => openPortal(portal)}
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

      {/* The empty state is a card-shaped panel rather than a bare line, so the grid does not simply vanish
          and leave the reader wondering whether the page broke or the search worked. */}
      {needle && matches.length === 0 && (
        <p className="picker-empty">{L.portalsNoMatchHelp[lang]}</p>
      )}
      </main>
    </div>
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

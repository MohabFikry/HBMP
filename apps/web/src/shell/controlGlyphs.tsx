/**
 * The three glyphs the chrome outside a portal draws: language, theme, and the switch mark.
 *
 * They lived inside `LoginPage` because it was the only screen with a language and theme control of its own
 * — every other screen has the app bar. The portal picker is the second such screen, and copying them would
 * have made the two pairs of controls free to drift: the sizing comment below is a real optical correction,
 * and a second copy of it is a second thing to remember to fix.
 *
 * Inline SVG rather than an icon package: four shapes do not justify a dependency, and `currentColor` lets
 * them follow the theme's text tokens for free.
 */

/** Icon + the code of the language it switches TO. The glyph alone says nothing about what would happen. */
export const LangGlyph = ({ code }: { code: string }) => (
  <svg width="22" height="17" viewBox="0 0 22 17" aria-hidden="true">
    <text
      x="11"
      y="8.5"
      textAnchor="middle"
      /* Not decoration — it is the only way to centre this. Arabic faces carry tall ascent and descent to
         leave room for diacritics, so the ink of "ع" sits low inside its em box: HTML centres the LINE BOX,
         which is geometrically right and optically wrong, and no amount of `align-items: center` can see the
         difference. `dominant-baseline="central"` centres on the font's own central baseline, computed by
         the renderer from the real metrics of whichever face resolves — so it is correct for Cairo, for a
         fallback, and for "EN" too. */
      dominantBaseline="central"
      fill="currentColor"
      fontFamily="Cairo, Inter, system-ui, sans-serif"
      fontWeight="600"
      /* Arabic letterforms read smaller than Latin capitals at the same size, so the single glyph is set a
         little larger to carry the same weight on the page as "EN". */
      fontSize={code === "EN" ? 13 : 16}
    >
      {code}
    </text>
  </svg>
);

export const MoonIcon = () => (
  <svg width="17" height="17" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true">
    <path d="M20 14.2A8.4 8.4 0 0 1 9.8 4a8.5 8.5 0 1 0 10.2 10.2z" />
  </svg>
);

export const SunIcon = () => (
  <svg width="17" height="17" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7"
       strokeLinecap="round" aria-hidden="true">
    <circle cx="12" cy="12" r="4" />
    <path d="M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6l1.4 1.4M17 17l1.4 1.4M18.4 5.6L17 7M7 17l-1.4 1.4" />
  </svg>
);

/**
 * The switch mark on the portal switcher — two arrows swapping places.
 *
 * Drawn without a direction of its own: both arrows are present, so it means "exchange" rather than "go
 * right", and it needs no mirroring under RTL. A single chevron here would have to be flipped in Arabic and
 * would read as "expand" in either language.
 */
export const SwitchGlyph = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8"
       strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d="M4 8h13" />
    <path d="m13 4 4 4-4 4" />
    <path d="M20 16H7" />
    <path d="m11 12-4 4 4 4" />
  </svg>
);

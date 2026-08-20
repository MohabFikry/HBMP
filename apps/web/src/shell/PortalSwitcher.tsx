import { useNavigate } from "react-router-dom";
import { Icon, useTheme } from "@mersal/design-system";
import { L } from "../i18n/strings";
import type { PortalDef } from "../portals/catalog";
import { SwitchGlyph } from "./controlGlyphs";

/**
 * The portal switcher at the top of the nav rail — ONE component, rendered by {@link AppShell} for every
 * portal, never copied per portal.
 *
 * That is the whole point of it living here and being passed into `NavRail`'s `header` slot: twenty-one
 * portals sharing a control by convention is twenty-one places for it to drift, and the drift is invisible
 * because each portal looks right on its own. Sharing it by construction means a change to the switcher is
 * a change to the switcher.
 *
 * ==========================================================================================================
 * WHY IT IS HIDDEN BELOW TWO PORTALS
 * ==========================================================================================================
 * `AppShell` renders this only when the caller holds more than one. A control that says "Change portal" and
 * leads to a screen with one card is worse than no control: it is a promise of somewhere else to go, made
 * to the majority of users, who hold exactly one portal and have nowhere else to go.
 *
 * "Identical in every portal" is a statement about there being one implementation. It is not a statement
 * about showing a dead button to people it cannot do anything for.
 */
export function PortalSwitcher({ portal }: { portal: PortalDef }) {
  const { lang } = useTheme();
  const navigate = useNavigate();

  return (
    <button
      type="button"
      className="portal-switch"
      /* Spelled out rather than left to the text content, which would read as the bare "Clinic Management
         Change portal" — two labels with no relationship between them. Both visible strings appear here
         verbatim, so this satisfies Label in Name (WCAG 2.5.3) while saying what the control is. */
      aria-label={`${L.currentPortal[lang]}: ${portal.title[lang]}. ${L.changePortal[lang]}`}
      onClick={() => navigate("/portals")}
    >
      <span className="portal-switch-tile" aria-hidden="true">
        <Icon name={portal.icon} />
      </span>
      <span className="portal-switch-text">
        {/* The portal's name is the LOUD line and "Change portal" the quiet one, because the first question
            this control answers is "where am I" — which the user asks far more often than they change. The
            accessible name carries both, so a screen reader hears the same two facts in the same order. */}
        <span className="portal-switch-name">{portal.title[lang]}</span>
        <span className="portal-switch-sub">{L.changePortal[lang]}</span>
      </span>
      <span className="portal-switch-glyph" aria-hidden="true">
        <SwitchGlyph />
      </span>
    </button>
  );
}

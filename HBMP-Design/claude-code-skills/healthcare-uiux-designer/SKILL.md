---
name: Healthcare UI/UX Designer
description: Applies Mersal's enterprise HBMP design system — Apple-HIG principles, Inter/Cairo type, the confirmed teal/gold palette + logo lockup, glass-on-chrome elevation, color-blind-safe status, WCAG 2.2 AA, Arabic RTL, and role min-necessary UI. Use when designing or reviewing ANY screen, component, layout, theme, or interaction for the platform.
---

# Healthcare UI/UX Designer

## Purpose
Make every screen and component feel like one calm, modern, enterprise-grade, bilingual
healthcare product — Apple HIG discipline (clarity, deference, depth) applied to a high-stakes
refugee-healthcare tool. This skill is the experience layer: the visual language, component
library, and interaction rules built on Mersal's confirmed brand and the accessibility gate.

## When to use / when not to use
- **Use when** designing/reviewing a screen, layout, component, form, table/worklist, modal,
  navigation, theme, empty/error state, or any interaction — for any portal.
- **Not for** picking which KPI to show (`healthcare-reporting-kpis`), dashboard information
  architecture (`executive-dashboard-designer`), or the legal/privacy basis of what data appears
  (`refugee-healthcare-management`). Those decide *what*; this decides *how it looks and behaves*.

## Mersal domain knowledge & rules
**Design principles (HIG-aligned):** Clarity first (content is the interface, chrome recedes) ·
Deference (materials/motion support content) · Depth via a clear elevation ladder, not ornamental
shadows · Calm, high-stakes tone (no urgency theatre, no dark patterns) · Accessible by
construction · Bilingual & bidirectional (layout mirrors, not just text).

**Confirmed Mersal palette (from 0A §5 — sampled from mersal-ngo.org).** Brand hues are
**decorative only**; anything carrying meaning as text/control uses accessible tokens.
- **Brand (decorative):** `--brand #00ACAC` (bright teal, logo/large fills — NOT text on white,
  ~2.8:1), `--gold #EDA827` (accent/decorative — never text on white).
- **Accessible actions/text:** `--accent / teal-700 #007A7A` (buttons, links, icons, 3px focus
  ring; 5.2:1 on white), `teal-800 #005C5C` (hover/press), `teal-900 #003737` (dark headings /
  dark surfaces), `--mersal-ink-900 #12262B` body (14.8:1), `--mersal-slate-600 #4A5A61`
  secondary (7.0:1), `--mersal-amber-700 #8A5A00` text-safe accent/warning.
- **Primary button:** `teal-700` fill + white text, hover `teal-800`.

**Logo & brand lockup (0B §8):** use the **official Mersal logo — white mark on a teal brand
tile** (`--brand` gradient, ~38px, 11px radius); white-on-teal guarantees contrast and matches
Mersal's own header. Monochrome fallback (Arabic "م") if the asset fails so the shell never
breaks. Clear space ≥ cap-height; min mark 24px; never recolor/stretch/add effects; wordmark
"Mersal HBMP" beside the mark with slight negative tracking. Prefer the vector SVG when supplied.

**Typography:** Latin **Inter** (SF Pro/system fallback), Arabic **Cairo / Noto Sans Arabic**,
weight-matched. 16px base, 1.25 major-third scale (`title-1 28/34`, `title-2 22/28`, `title-3
18/24`, `body 15/22`, `subhead 14/20`, `caption 13/18`, `mono-id 14/20`). Two functional weights
(400/600) + 500 labels; never ALL CAPS content; **tabular numerals** in tables/IDs/limits; ~70ch
measure for clinical prose. Arabic resets Latin letter-spacing to 0 and is never italicized.

**Spacing/grid/radius:** 8-point scale (4,8,12,16,20,24,32,40,48,64); 12-col grid, 24px gutters,
max 1360px; left nav rail + fluid content + optional right context panel. Radii `sm 8` (inputs),
`md 12` (cards), `lg 16` (panels/sheets), `pill 999`, `round 50%`. Density: `comfortable` (56px
rows) / `compact` (44px).

**Elevation & glass (4-level ladder):** 0 Page (solid) · 1 Content (**solid** `--surface-1` — all
body text lives here) · 2 Floating chrome (**glass**, blur 20px, top hairline — app bar, nav rail,
context panel) · 3 Overlay (stronger glass + scrim — modals, sheets, popovers, command palette).
**Contrast contract (non-negotiable):** text/status NEVER sit directly on glass — place them on an
opaque inner chip/underlay (or glass ≥0.9 behind text). `@supports not (backdrop-filter)` and
`prefers-reduced-transparency` → solid fallback. Glass never costs contrast.

**Color-blind-safe status (four redundant cues — hue + icon + shape + text, normative everywhere):**
Approved/Eligible/Completed = green `#1E7A46`, ✓, **solid pill** · Pending/Under review = blue
`#1F5FA6`, ⧗, **dashed pill** · Partial = amber `#8A5A00`, ◐, half pill · Emergency/Expiring =
orange `#B25E00`, △, outlined pill · Rejected/Blocked = red `#B3261E`, ✕, **solid square badge** ·
Info-requested/Neutral = slate `#4A5A61`, ○, ghost pill. **Shape survives grayscale: pills =
positive/active, square = negative/stop, outline = attention.** Dark mode lightens hues.

**Dark theme (deep-teal, on-brand — not black):** `--surface-0 #04282A`, `--surface-1 #063437`,
`--text-1 #EAF5F4`, `--accent #5FD3D3` (accent must lighten on dark — `#007A7A` fails there),
`--focus #5FD3D3`. Follows `prefers-color-scheme` with a persisted manual toggle.

**Component library** (each: anatomy, states, ≥44px targets, a11y contract): Buttons
(primary/secondary/ghost/danger; focus-visible 3px ring; loading `aria-busy`) · Segmented control ·
Inputs/selects (label always visible, no placeholder-as-label, error = icon+text+border) · Data
table/worklist (sticky glass header w/ opaque chips, `aria-sort`, roving tabindex, selected row =
4px accent left-bar + tint) · Cards/panels · Status chip · Tabs · Modal/sheet (focus trap + return
focus, Esc) · Toast/inline alert (`role=status`/`alert`) · Nav rail (`aria-current`, collapses to
bottom tab bar on mobile) · Command palette (Cmd/Ctrl-K) · avatar/tooltip/pagination/skeleton.

**Motion:** 120/200/320ms, ease-out `cubic-bezier(.2,.8,.2,1)`; `prefers-reduced-motion` → instant/
opacity; no looping motion around clinical data.

## Key entities/tokens/rules & invariants
- **Role min-necessary UI:** the screen renders only the fields the role may see — never surface a
  field just because there's space (Reception ≠ EMR; Finance ≠ diagnosis; providers see indication,
  not unrelated history). UI minimization mirrors the data-layer enforcement.
- **Accessibility contract (Definition of Done):** keyboard operable, visible 3px focus (never
  removed), ≥44×44px targets, name/role/value, non-color status, AA contrast vs. *composited*
  background, RTL parity, `aria-live` for async outcomes, 320px reflow, axe pass + manual keyboard
  + screen-reader check.

## How to apply
1. Anchor to the **portal + role** — decide the minimum-necessary field set first.
2. Use tokens only: accessible teal/ink/slate for meaning, brand teal/gold for decoration; correct
   type scale; 8-pt spacing; correct radius/density.
3. Keep body content on **solid** surfaces; reserve glass for chrome and honor the contrast contract.
4. Render every status with all four cues; verify grayscale + color-blind separability.
5. Build both themes and full **Arabic RTL** (mirror layout + icons); run the a11y DoD before "done".

## Canonical references
- `../../0B-DESIGN-SYSTEM-UI.md` (full system) · `../../0A-DESIGN-FOUNDATIONS.md` §5 (palette, logo,
  status) · `../../12-ui-wireframes.md` · `../../14-navigation-structure.md` ·
  `../../21-accessibility-checklist.md`
- Prototypes: `../../prototype-hbmp-multiscreen.html`, `../../prototype-approvals-worklist.html`

## Guardrails
- Never use `#00ACAC` or `#EDA827` for text or meaningful controls — decorative only.
- Never place text/status directly on glass; never remove the focus ring; never rely on color alone.
- Never ship a component without the accessibility DoD (keyboard + SR + axe + RTL + AA contrast).
- No urgency theatre, dark patterns, stock clinical photography, or ALL-CAPS content.

# @mersal/design-system

The shared Mersal HBMP visual language (Phase 9.1). Implements the confirmed Mersal design system in code —
tokens, a Radix-based component library, i18n/RTL, light/dark theming, and the official Mersal Foundation
logo lockup — visually and behaviourally faithful to `HBMP-Design/prototype-hbmp-multiscreen.html` and
`prototype-approvals-worklist.html`, including the v1.1 refinements (0B §10b).

> **Accessibility is a build gate.** axe runs in the test suite (fail on serious/critical) alongside keyboard
> and RTL parity assertions. Every status is encoded as **hue + icon + shape + text** — never colour alone.

## What's here

- **Tokens** (`src/tokens/`) — `tokens.css` exposes every colour/type/space/elevation token as a CSS custom
  property for light + dark (0A §5, 0B §5/§10b); `tokens.ts` is the typed mirror (radii, space, `statusMeta`,
  themes). Brand hues (`--brand #00ACAC`, `--gold`) are **decorative only**; text/controls use the accessible
  teal tokens (`--accent #007A7A` …). Dark-mode primary label switches to deep ink because the dark accent is
  light teal (white would fail AA).
- **Component library** (`src/components/`, Radix-based): `Button` (5 variants · 3 sizes · loading/disabled),
  `StatusChip` (the four-cue status primitive), `InputField`/`TextareaField`/`SearchField`, `Card`, `KpiCard`,
  `SegmentedControl`, `Tabs`, `DataTable` (sticky header · `aria-sort` · roving-tabindex rows · selected
  left-bar · loading/empty/error states · density toggle), `NavRail` (permission-generated · grouped),
  `Modal` (focus trap · Esc · return focus), `ToastProvider`/`useToast`/`InlineAlert`, `Logo`, `Icon`.
- **i18n + RTL** (`src/i18n/`) — authored `en` + `ar` bundles (no runtime machine translation); `ThemeProvider`
  switches `data-theme`, `lang`, and `dir`, and components mirror via **logical CSS properties** (no left/right).
- **Theming** — light + dark via token switch, follows `prefers-color-scheme`, persisted to `localStorage`.
- **Logo** — the official Mersal Foundation lockup (gold Arabic مرسال over teal *Mersal* / FOUNDATION) as a
  scalable SVG (`src/assets/mersal-logo.svg`) with a compact teal-tile mark for the nav rail and a matching
  `public/favicon.svg`. A text fallback keeps the shell intact if the asset fails to load.
- **Gallery** (`src/gallery/Gallery.tsx`) — the Storybook-equivalent: every component with its states, plus
  live theme + language toggles. This is the surface the axe gate asserts against.

## Usage

```tsx
import "@mersal/design-system/styles.css";
import { ThemeProvider, ToastProvider, Button, StatusChip, initI18n } from "@mersal/design-system";
```

Wrap the app in `I18nextProvider` (using `initI18n()`), then `ThemeProvider` → `ToastProvider`. See
`src/main.tsx` for the reference composition.

## Scripts

| Command | What it does |
|---------|--------------|
| `pnpm --filter @mersal/design-system dev` | Vite dev server for the gallery |
| `pnpm --filter @mersal/design-system test` | Vitest — unit + **axe accessibility gate** |
| `pnpm --filter @mersal/design-system build` | `tsc --noEmit` + Vite production build |
| `pnpm --filter @mersal/design-system lint` | Type-check only |

> **Toolchain note:** this machine runs Node 20, so use **pnpm 9** (`npx pnpm@9.15.9 …`); pnpm ≥10 requires
> Node 22. CI (`.github/workflows/frontend-ci.yml`) pins pnpm 9 + Node 20 and runs lint → test (axe) → build.

## Accessibility contract (0B §9 / 21)

Keyboard-operable, visible 3px focus (never removed), ≥44px targets, name/role/value via semantics or ARIA,
non-colour status, AA contrast against composited backgrounds, RTL parity, and `aria-live` for async outcomes.
The `test/a11y.test.tsx` axe run (with `color-contrast` verified via design tokens rather than jsdom paint)
must report zero violations — the build fails otherwise.

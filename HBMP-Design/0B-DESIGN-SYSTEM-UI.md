# 0B — Enterprise UI/UX Design System

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [12-ui-wireframes.md](12-ui-wireframes.md) · [13-ux-flows.md](13-ux-flows.md) · [14-navigation-structure.md](14-navigation-structure.md) · [21-accessibility-checklist.md](21-accessibility-checklist.md)
> Live reference implementation: `prototype-approvals-worklist.html`

This is the **experience layer** of the platform: the visual language, component library, and interaction rules that make the HBMP feel like a modern, calm, enterprise-grade product. It builds on the confirmed Mersal palette in [0A §5](0A-DESIGN-FOUNDATIONS.md) and the accessibility gate in [21](21-accessibility-checklist.md). Design north star: **Apple Human Interface Guidelines** discipline (clarity, deference, depth) applied to a bilingual, high-stakes healthcare tool.

---

## 1. Design principles (HIG-aligned)

1. **Clarity first.** Content is the interface. Chrome recedes; data and the next action are unmistakable. Legibility at every size, generous negative space, no decoration that competes with meaning.
2. **Deference.** Materials (glass/blur) and motion support content, never upstage it. The UI is a quiet frame around clinical decisions.
3. **Depth via layering, not shadows-as-ornament.** A clear elevation model communicates hierarchy: page → content surface → floating panel → modal. Translucency signals "this floats above context."
4. **Calm, high-stakes tone.** This is refugee healthcare. No playful flourishes, no dark patterns, no urgency theatre. Confidence through restraint.
5. **Accessible by construction.** Contrast, target size, focus, and non-color encoding are design tokens, so violations are hard to build. Glass never costs contrast (see §5).
6. **Bilingual & bidirectional.** Every component works identically in Arabic RTL and English LTR; the layout mirrors, not just the text.

---

## 2. Typography

A single, highly legible type system. Latin uses **Inter** (or SF Pro / system UI as the Apple-native fallback); Arabic uses **Cairo** / **Noto Sans Arabic**, weight-matched. Both are humanist sans with tall x-heights for screen legibility.

```
--font-latin: "Inter", -apple-system, "SF Pro Text", "Segoe UI", system-ui, sans-serif;
--font-arabic: "Cairo", "Noto Sans Arabic", "Geeza Pro", system-ui, sans-serif;
--font-mono:  "SF Mono", "JetBrains Mono", ui-monospace, monospace;  /* IDs, codes */
```

### Type scale (1.25 major-third, 16px base)

| Token | Size / line-height | Weight | Use |
|-------|--------------------|--------|-----|
| `display` | 34 / 40 | 600 | Page hero (rare) |
| `title-1` | 28 / 34 | 600 | Screen title |
| `title-2` | 22 / 28 | 600 | Section headers |
| `title-3` | 18 / 24 | 600 | Card headers |
| `body-lg` | 17 / 26 | 400 | Reading text (clinical notes) |
| `body` | 15 / 22 | 400 | Default UI text |
| `subhead` | 14 / 20 | 500 | Labels, table headers |
| `caption` | 13 / 18 | 400 | Secondary/meta |
| `mono-id` | 14 / 20 | 500 | Business keys (AUTH-…, MRS-M-…) |

Rules: **two functional weights** (400 regular, 600 semibold) plus 500 for labels. Never ALL CAPS for content (only micro-labels with tracking). Numerals **tabular** (`font-variant-numeric: tabular-nums`) in tables, limits, and IDs so columns align. Line length capped ~70ch for clinical prose. Arabic sizes +1px optical bump where needed; never italicize Arabic.

**Refinements applied in the prototypes:** headings use negative tracking (`letter-spacing:-.021em`) with `text-wrap:balance` for even ragging; body uses `-.005em` tracking, `line-height:1.55`, `text-rendering:optimizeLegibility`, grayscale smoothing, and Inter stylistic sets/`tnum` via `font-feature-settings`; monospace IDs reset tracking and force `tnum`. Arabic resets `letter-spacing` to 0 (Latin tracking harms Arabic shaping).

---

## 3. Spacing, grid & radius

- **8-point spacing** scale: `4, 8, 12, 16, 20, 24, 32, 40, 48, 64`. Components snap to it.
- **Layout grid:** 12-column, 24px gutters, max content 1360px; app uses a left nav rail (fixed) + fluid content + optional right context panel.
- **Radii:** `--r-sm 8` (controls/inputs), `--r-md 12` (cards), `--r-lg 16` (panels/sheets), `--r-pill 999` (chips/badges), `--r-round 50%` (avatars). Consistent rounding is a core "modern" cue.
- **Density modes:** `comfortable` (default) and `compact` (data-dense worklists/tables) toggle row height (56 / 44) and padding — HIG-style adaptivity for power users.

---

## 4. Elevation & material (the "glass" system)

Depth is a **4-level elevation ladder**. Glass (translucency + backdrop blur) is reserved for **floating chrome** — never for primary reading surfaces.

| Level | Name | Material | Where |
|-------|------|----------|-------|
| 0 | Page | Solid `--surface-0` | App background |
| 1 | Content | **Solid** `--surface-1` | Cards, tables, forms — all body text lives here |
| 2 | Floating chrome | **Glass** — translucent + `backdrop-filter: blur(20px) saturate(1.2)` + hairline top-highlight | Top app bar, left nav rail, right context panel |
| 3 | Overlay | **Glass, stronger** blur(28px) + scrim behind | Modals, sheets, popovers, command palette |

**Glass tokens (balanced profile):**
```
--glass-bg-light: rgba(255,255,255,0.72);
--glass-bg-dark:  rgba(6,40,42,0.60);
--glass-blur: blur(20px) saturate(1.2);
--glass-hairline: inset 0 1px 0 rgba(255,255,255,0.5);   /* top light edge */
--elev-1: 0 1px 2px rgba(16,38,43,.06), 0 1px 1px rgba(16,38,43,.04);
--elev-2: 0 6px 20px rgba(16,38,43,.10);
--elev-3: 0 16px 48px rgba(16,38,43,.22);
```

**The contrast contract (non-negotiable):** text and status **never** sit directly on glass. Any glass surface that carries text places that text on an **opaque inner chip/underlay** (or the glass opacity is ≥0.9 behind text runs) so measured contrast meets AA against the *effective* composited color, not the ideal. A `@supports not (backdrop-filter: blur(1px))` fallback swaps glass for a solid surface. `prefers-reduced-transparency` → solids. This resolves the glass-vs-legibility tension in favor of legibility, every time.

---

## 5. Color, contrast & dark mode

Uses the confirmed Mersal tokens from [0A §5](0A-DESIGN-FOUNDATIONS.md). Brand hues (`#00ACAC`, `#EDA827`) are **decorative only**; text/controls use accessible tokens.

### Light theme
```
--surface-0: #F4F8F8;   /* page */
--surface-1: #FFFFFF;   /* cards */
--text-1: #12262B;      /* body        14.8:1 */
--text-2: #4A5A61;      /* secondary     7.0:1 */
--accent:  #007A7A;     /* teal-700 actions/links  5.2:1 */
--accent-press: #005C5C;
--brand:   #00ACAC;     /* decorative */
--gold:    #EDA827;     /* decorative accent */
--border:  #D7E3E3;
--focus:   #007A7A;     /* 3px ring */
```

### Dark theme (deep-teal, not black — on-brand)
```
--surface-0: #04282A;
--surface-1: #063437;   /* teal-900 family */
--text-1: #EAF5F4;      /* body on dark   >13:1 */
--text-2: #A9C4C4;      /* secondary       ~6:1 */
--accent:  #5FD3D3;     /* lightened teal for AA on dark  ~7:1 */
--accent-press: #86E3E3;
--border:  #12484B;
--focus:   #5FD3D3;
```

Accent **lightens in dark mode** (`#5FD3D3`) because `#007A7A` fails on a dark surface — a common miss. Both themes verified: body ≥ 7:1, secondary ≥ 4.5:1, controls/borders ≥ 3:1. Theme follows `prefers-color-scheme` by default with a manual toggle (persisted).

### Color-blind–safe status system (normative, extends [0A §5.2](0A-DESIGN-FOUNDATIONS.md))
Every status = **hue + icon + shape + text label** (four redundant cues). Verified for protan/deutan/tritan separability. Charts add **pattern fills + direct labels**, never hue alone.

| Status | Hue (light / dark) | Icon | Badge shape | Label |
|--------|--------------------|------|-------------|-------|
| Approved / Eligible / Completed | `#1E7A46` / `#5FD08A` | check ✓ | solid pill | "Approved" |
| Pending / Under review | `#1F5FA6` / `#7FB4F0` | clock ⧗ | dashed pill | "Under review" |
| Partial | `#8A5A00` / `#E7B23C` | half ◐ | half-filled pill | "Partial" |
| Emergency / Expiring | `#B25E00` / `#F0A860` | triangle △ | outlined pill | "Emergency" |
| Rejected / Blocked | `#B3261E` / `#F2857D` | cross ✕ | solid **square** badge | "Rejected" |
| Info-requested / Neutral | `#4A5A61` / `#A9C4C4` | dot ○ / info | ghost pill | "Info requested" |

Shape is the tell that survives grayscale: **pills = active/positive states, square = negative/stop, outline = attention.**

---

## 6. Component library (specs)

Each component states anatomy, states, sizing (≥44px targets), and a11y contract.

- **Buttons.** Variants: `primary` (accent fill + white text), `secondary` (tinted `--accent` on `--surface-1` with border), `ghost` (text-only), `danger` (red). Sizes 32/40/48h; min target 44 incl. padding. States: default/hover/active/focus-visible(3px ring)/disabled(opacity+`aria-disabled`)/loading(spinner + `aria-busy`). Icon+label preferred; icon-only requires `aria-label`.
- **Segmented control** (HIG signature): filter switches (e.g., All / Under review / Emergency), single-select, arrow-key navigable, selected segment has solid pill on a tinted track.
- **Inputs & selects.** 44h, 12px radius, label always visible (no placeholder-as-label), helper + error text tied via `aria-describedby`, error uses icon+text+red border (not color alone). Search field with leading icon + clear button.
- **Data table / worklist.** Sticky glass header (level 2) with opaque label chips, tabular numerals, sortable columns (`aria-sort`), zebra optional, row = focusable button (roving tabindex), selected row = accent left-bar (4px) + tint + `aria-selected`. Empty/loading/error states defined. Density toggle.
- **Cards & panels.** Solid level-1 surfaces, 12–16px radius, `--elev-1`. Context/detail panel is level-2 glass but its content sits on inner opaque blocks.
- **Status chip / badge.** Renders the §5 four-cue system; tooltip on hover/focus (dismissible, persistent).
- **Tabs.** Underline style, `role="tablist"`, arrow-key nav; no content hidden during load for SSR.
- **Modal / sheet.** Level-3 glass over a scrim; focus trap + return focus; `Esc` closes; labelled by title; bottom sheet variant on mobile.
- **Toast / inline alert.** `role="status"`/`role="alert"`, icon+text, auto-dismiss with pause-on-hover; never the only notification of a critical result.
- **Navigation rail.** Level-2 glass, icon+label items, current item marked with accent bar + `aria-current="page"`; collapses to bottom tab bar on mobile ([14](14-navigation-structure.md)).
- **Command palette** (`Cmd/Ctrl-K`): fast search/jump for power users — HIG-style, keyboard-first.
- **Avatar / identity, tooltip, pagination, skeleton loaders** — all themed, all AA.

---

## 7. Motion

Purposeful, quick, physical. Durations `120ms` (micro), `200ms` (standard), `320ms` (overlay). Easing `cubic-bezier(.2,.8,.2,1)` (HIG-like ease-out). Motion communicates hierarchy: sheets slide from edge, popovers scale-fade from origin, selection uses a subtle spring. **`prefers-reduced-motion: reduce` disables transforms/parallax** and falls back to instant/opacity. No infinite/looping motion around clinical data.

---

## 8. Logo & brand lockup
- **Use the official Mersal logo.** The app bar shows the white Mersal mark (`logo_W.png` from mersal-ngo.org) on a teal brand tile (`--brand` gradient, 38px, 11px radius). White-on-teal guarantees contrast and matches Mersal's own header treatment. A monochrome fallback (Arabic "م") renders if the asset fails to load, so the shell never breaks offline. Replace the hotlinked PNG with the official **vector (SVG)** logo when supplied.
- **Clear space** ≥ the mark's cap-height on all sides; **minimum size** 24px mark. Never recolor, stretch, add effects, or place the logo on a low-contrast background. On dark surfaces use the white mark on the teal tile as-is.
- The wordmark "Mersal HBMP" sits beside the mark in the heading weight with slight negative tracking.

## 8b. Iconography & imagery
- One icon family, **outline style**, 1.5px stroke, 20/24px, currentColor. Icons are paired with text for meaning; decorative icons `aria-hidden`.
- No stock photography in clinical surfaces. Illustrations (empty states) are simple, calm, brand-teal line art.
- Data-density-appropriate icons (status, order type) drawn as shapes so they double as the color-blind shape cue.

---

## 9. Accessibility contract (ties to [21](21-accessibility-checklist.md))
Every component ships meeting: keyboard operability, visible 3px focus (never removed), ≥44px targets, name/role/value via semantics or ARIA, non-color status, AA contrast against *composited* backgrounds, RTL parity, and `aria-live` for async outcomes. **Definition of Done for any UI story includes an axe pass + manual keyboard + screen-reader check.** Glass degrades to solid under `prefers-reduced-transparency`/unsupported backdrop-filter.

---

## 10. Apple HIG → HBMP mapping (quick reference)

| HIG concept | HBMP application |
|-------------|------------------|
| Clarity / legibility | Inter/Cairo type scale, tabular numerals, 70ch measure |
| Deference / materials | Glass only on chrome (§4), content stays solid |
| Depth | 4-level elevation ladder; translucency = "floats above" |
| Consistency | One token set, one component library, both themes |
| Feedback | Focus rings, toasts (`aria-live`), optimistic states with audit |
| Adaptivity | Comfortable/compact density; responsive rail → tab bar |
| Accessibility | Non-color status, targets, reduced-motion/transparency |
| Restraint | No urgency theatre in a high-stakes clinical tool |

---

## 10b. Visual refinement v1.1 (design audit + enterprise polish)

A design audit (via the `healthcare-uiux-designer` skill) found the v1.0 system **correct but visually conservative** — accessibility, status encoding, and glass discipline were right; depth, motion, and hierarchy were timid. v1.1 adds the following **normative** refinements, implemented in both prototypes and required of the production frontend (phase 9):

| # | Finding (v1.0) | Refinement (v1.1) |
|---|----------------|-------------------|
| 1 | Flat single-tint page canvas | **Layered page wash:** subtle top-down gradient (`--page-wash`, ~340px fade into `--surface-0`) in both themes — content zones read as layered, not pasted |
| 2 | Elevation nearly invisible; no hover physics | Refined `--elev-1` (dual-layer soft shadow) + new **`--elev-hover`** token; interactive cards/buttons lift on hover |
| 3 | No applied motion vocabulary | Base transition rule on all interactive elements (background/border/color/shadow 200ms ease-out; transform 120ms); `prefers-reduced-motion` still disables |
| 4 | Buttons flat, no pressed state | Hover = `translateY(-1px)` + `--elev-hover`; active = return + shadow drop; primary gains a **top-light gradient + inner highlight** (still token teal); dark-mode primary text switches to deep ink (`#04282A`) because the dark accent is light teal — white would fail AA |
| 5 | KPI cards under-designed | **Brand hairline** (3px `--brand→--accent` gradient top edge), uppercase micro-label, 34px tabular numerals with tight tracking, delta as bordered pill, hover lift |
| 6 | Table headers weak hierarchy | **Micro-label style:** 11.5px, uppercase, `.06em` tracking, `--text-3` — data rows now dominate |
| 7 | Nav rail groups unarticulated | Hairline dividers between groups (first exempt), micro-label styling |
| 8 | Page header lacked a brand moment | Eyebrow/role label gains a **brand tick** (16×3px `--brand→--gold` gradient bar); `h1` normalized to title-1 (28px) |
| 9 | Dark glass edges too faint | `--glass-brd` alpha raised to `.28` in dark theme |

Rules that did **not** change (and must not): the accessible-token/decorative-brand split, four-cue status chips, the glass contrast contract, focus rings, target sizes, RTL mirroring, and both themes' AA ratios. Brand gradients appear only as **decorative hairlines/ticks** — never as text or control fills carrying meaning.

## 10c. Paired actions & the reference-table modal

Two patterns that belong together, first applied to the bulk-upload screens (Register New → "Many from a file", and Bulk & Imports) and **binding on every screen that repeats the shape**.

### Paired actions

When two controls answer the **same question**, they get **matched visual treatment** — same variant, same size, side by side with `--sp3` between them, wrapping to stacked full-width below 30rem rather than shrinking under the 44px target.

The rule exists because of the failure it prevents: *"Download the template"* rendered as a button beside *"Expected columns"* rendered as an underlined link tells the operator the second one is secondary information. For someone uploading their first enrolment file, the column contract is not secondary — it is the thing that decides whether the upload works. **A bare text link next to a button is a hierarchy claim; make it deliberately or not at all.**

Both carry a leading icon **and** a text label. Icon-only is never acceptable for a primary or paired action.

### Reference-table modal

Content that is **read once and then known** — column contracts, code lists, glossary tables, enum meanings — goes in a modal behind a permanently visible trigger, not inline.

Inline, a 15-row column table pushed the screen's most-used control (choose file → dry run → commit) below the fold on *every* visit, forever, to serve a need that ends after the first successful upload. Reference material earns one click; the workflow earns the top of the page.

**Requirements:**

- Use the design-system `Modal` (Radix Dialog) — focus trap, return-focus-to-trigger, Esc-to-close and `aria-modal` come with it. Do not hand-roll a dialog.
- Trigger carries `aria-haspopup="dialog"`.
- The table stays a **real `<table>`** with a **sticky header** and a scrollable body capped at `min(60vh, 32rem)`. A contract is read by scanning one column downward; losing the header defeats the reading pattern.
- Required/optional is marked by **icon *and* word**, never a lone ✓/— glyph (four-cue rule).
- Column names render in `<code>` with `overflow-wrap: anywhere`, since they are exact match keys.
- Full-screen sheet on mobile; bilingual EN/AR with RTL mirroring; axe clean in both.

### The rule that makes hiding it safe

**Reference content may only be hidden if a failure can bring it straight back.** The modal is therefore *controllable by the parent screen*, so a validation error — "unknown column: `xzy`" — can reopen the contract, ideally scrolled to the offending row. A trigger that only exists at the top of the page, above an error the operator is reading at the bottom, is a dead end dressed as progressive disclosure.

Corollary: the one-sentence guidance that prevents the failure (*"Use the template. An unknown or missing column fails the whole file…"*) **stays inline and always visible.** Only the table moves.

### One component, two screens

Both bulk screens drive the same ingestion engine, so the contract is **one shared component** (`BulkTemplateActions`) fed by the server's template response. Two copies of a column table is how two doors into one registry come to describe different contracts.

## 10d. Timelines — how a step names its actor and its moment

Normative for **every** timeline in every portal: the appointment timeline, the care episode, the policy
version history, and anything added later that renders a list of "what happened, when, by whom".

### The three cases, and why they are three

A step's actor is one of exactly three things, and they are said differently because they are different
facts. Collapsing any two of them is how a timeline stops being worth opening.

| The truth | What is rendered | Why not something else |
|---|---|---|
| Resolved to a person | the display name | — |
| Recorded, but the directory cannot name it | **"Unknown user"**, raw id on `title` | It used to print eight hex characters of the subject id. That is not a name and nobody at a desk can act on it — it reads as a glitch where an answer belongs. The id stays reachable for whoever is holding a support ticket. |
| Nothing was recorded | **"actor not recorded"** | Distinct from the above. "We know who and cannot resolve it" and "nobody was recorded" are different, and a reader deciding whom to ring needs to know which one they are looking at. |

**Never fall back to a neighbouring actor.** Attributing a step to whoever booked the appointment claims they
performed something they did not, and the timeline exists precisely to answer "who" — a plausible wrong answer
defeats it more thoroughly than an honest absence.

### The glyphs

- The actor is preceded by the `user` glyph; the moment by `clock`. Both are 13px, `--text-3`, and
  `aria-hidden`.
- The words they replaced — "by" and "at" — **stay as `sr-only` text**. Without them a step announces a
  timestamp and then a name with nothing to say what either is doing there; a sighted reader separates the two
  facts by their icons and a listening one cannot.
- The glyphs never carry state. A step's meaning is the status chip beside them, which keeps hue + icon +
  shape + text per [§5 colour-blind-safe status](#color-blindsafe-status-system-normative-extends-0a-52).

### Resolving names

Ids are resolved in ONE request for the distinct actors on the timeline, never one per step — a rebooked
appointment repeats the same actor several times. A failure to resolve degrades to "Unknown user" and never
fails the timeline: knowing *when* a no-show was marked is worth more than knowing nobody's name.

## 11. Reference implementations
Two working, single-file builds in this folder demonstrate the system — open either in a browser:

- **`prototype-hbmp-multiscreen.html`** — a multi-screen app with a shared glass shell and nav routing across four flagship screens: Reception eligibility, Doctor consultation/EMR (tabbed SOAP / vitals / orders), Approvals worklist + decision panel, and an Executive dashboard (KPIs + an accessible SVG chart with a data-table alternative). Light/dark toggle, English↔Arabic RTL toggle, color-blind-safe status, keyboard navigation, and responsive layout throughout.
- **`prototype-approvals-worklist.html`** — the focused single-screen build of the Approval worklist + decision panel.

### Cross-references
- Tokens/palette: [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md) · Screens: [12-ui-wireframes.md](12-ui-wireframes.md) · Nav: [14-navigation-structure.md](14-navigation-structure.md) · Accessibility: [21-accessibility-checklist.md](21-accessibility-checklist.md)

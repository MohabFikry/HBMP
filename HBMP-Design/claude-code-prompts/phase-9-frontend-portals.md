# Phase 9 — Frontend & Role Portals (R1–R5, portal by portal)

**Goal:** Implement the Mersal design system in code (tokens, component library, i18n/RTL, theming, logo lockup) matching the two prototypes, then scaffold role-based portals with permission-driven navigation, and build the flagship screens wired to their APIs. Accessibility (axe + keyboard + screen reader + AR/RTL) is a hard gate on every screen; no portal shows data beyond its permission zone; status is color+icon+shape+text.

Backlink: [00-MASTER-PROMPT-LIST.md](00-MASTER-PROMPT-LIST.md)

> Phase 9 runs alongside the backend phases: build the shared design system once (early), then each flagship screen as its APIs land (R1–R5). Every screen reuses the same tokens and components.

---

## Skills to activate
> Activate `healthcare-uiux-designer`, `executive-dashboard-designer`, `patient-journey-designer` — plus always-on `mersal-platform-architect` and `refugee-healthcare-management`. Skill files: [../claude-code-skills/00-SKILLS-INDEX.md](../claude-code-skills/00-SKILLS-INDEX.md).

## Context — read first

- [../0A-DESIGN-FOUNDATIONS.md](../0A-DESIGN-FOUNDATIONS.md) §5 — confirmed Mersal palette + tokens (teal `--mersal-teal-700 #007A7A` for controls, brand `#00ACAC`/gold `#EDA827` decorative only), non-color status system (hue+icon+shape+text), 44px targets, 3px focus.
- [../0B-DESIGN-SYSTEM-UI.md](../0B-DESIGN-SYSTEM-UI.md) — typography (Inter / Cairo), spacing, 4-level elevation + glass tokens, full component library specs + a11y contract.
- [../09-information-architecture.md](../09-information-architecture.md) — IA, portal zones.
- [../11-permission-matrix.md](../11-permission-matrix.md) — permission zones per role (min-necessary UI).
- [../12-ui-wireframes.md](../12-ui-wireframes.md) — flagship screen wireframes.
- [../13-ux-flows.md](../13-ux-flows.md) — screen flows + states.
- [../14-navigation-structure.md](../14-navigation-structure.md) — per-portal nav trees, permission-generated menus, keyboard map, mobile.
- [../21-accessibility-checklist.md](../21-accessibility-checklist.md) — WCAG 2.2 AA gate.
- Prototypes (the visual + interaction target): `../prototype-hbmp-multiscreen.html`, `../prototype-approvals-worklist.html`.
- [../32-user-stories.md](../32-user-stories.md) — US-070/071 (portal + role-scoped data), and the per-screen stories (US-060 approvals, US-073 dashboard, plus R1–R3 reception/EMR/lab/pharmacy).

Stack: React + TypeScript (Vite), Radix UI primitives, i18next, code-split per portal.

---

## Prompts

### 9.1 — Design system in code (tokens + component library + i18n/RTL + logo)

```text
Implement the Mersal design system as /apps/design-system (or /libs/ui). Read ../0A §5, ../0B, ../21, and BOTH prototypes first. The output must be visually and behaviorally faithful to the prototypes.

TOKENS (from ../0A §5 + ../0B)
- Colors as CSS custom properties EXACTLY per ../0A §5: --mersal-teal-brand #00ACAC (brand/decorative only), --mersal-teal-700 #007A7A (primary buttons/links/icons/focus ring), --mersal-teal-800 #005C5C (hover/active/header), --mersal-teal-900 #003737 (dark headings/dark surface), --mersal-teal-050 #E6F7F7 (tints), --mersal-ink-900 #12262B (body), --mersal-slate-600 #4A5A61 (secondary), --mersal-gold #EDA827 (decorative accent), --mersal-amber-700 #8A5A00 (text-safe accent). Enforce: brand hues NEVER used for text/controls.
- Typography: --font-latin Inter (SF Pro/system fallback), --font-arabic Cairo/Noto Sans Arabic; 400/500/600 weights only; tabular numerals in tables/IDs/limits; heading negative tracking + text-wrap:balance; Arabic resets letter-spacing to 0. Type scale + spacing scale + radii per ../0B.
- Elevation: 4-level ladder + glass tokens (--glass-bg-light rgba(255,255,255,.72), --glass-bg-dark rgba(6,40,42,.60), --glass-blur blur(20px) saturate(1.2), hairline). Glass only for floating chrome (nav rail, sticky headers, modals) — never reading surfaces. Degrade to solid under prefers-reduced-transparency / no backdrop-filter.

STATUS SYSTEM (color-blind safe — mandatory)
- Build a StatusChip that encodes every status as hue + icon + shape + TEXT per ../0A §5 table: Success ✓ solid pill; Info ⧗ dashed pill; Attention/Partial ◐ half-filled pill; Caution △ outlined pill; Danger ✕/⛔ solid square badge; Neutral ○ ghost pill. Never color alone.

COMPONENT LIBRARY (Radix-based, per ../0B §component specs) — each with anatomy, states, ≥44px targets, 3px focus-visible ring (--mersal-teal-700), keyboard operability, ARIA name/role/value, RTL parity:
- Button (primary/secondary/ghost/danger; 32/40/48h; loading aria-busy), Input/field set, Data table / worklist (sticky glass header, aria-sort, roving tabindex rows, selected = 4px accent left-bar + tint, empty/loading/error states, density toggle), StatusChip, Tabs, Modal/sheet (level-3 glass, focus trap, Esc, return focus), Navigation rail (level-2 glass, aria-current, collapses to bottom tab bar), Toast/inline alert (role=status/alert, pause-on-hover), Card/panel.

I18N + RTL + THEMING + LOGO
- i18next with `ar` + `en` bundles; `dir` switches document + component mirroring (logical properties, not left/right). Author both locales; no runtime machine translation.
- Light + dark themes (dark surface = --mersal-teal-900) via token switch.
- Mersal logo lockup component: official white mark on teal tile, with accessible text fallback ("Mersal"); scales for nav rail + auth screen. Provide LTR/RTL lockups.

ACCEPTANCE
- Given the built library, When rendered, Then it matches the prototypes' look/feel (type, color, glass, spacing).
- Given any interactive component, When keyboard-navigated, Then focus is visible (3px), targets ≥44px, and it works identically in AR/RTL and EN/LTR.
- Given axe in CI, When it runs on the component gallery/Storybook, Then zero serious/critical violations (build fails otherwise).

Ship: Storybook (or gallery) with every component + states, axe wired into CI (fail on serious/critical), unit tests, tokens exported as CSS vars + TS types.
```

### 9.2 — Role portals scaffolding + permission-driven routing

```text
Scaffold the role portals in /apps/web. Read ../09, ../11, ../14, US-070, US-071 first. Each role gets a DISTINCT, code-split portal with a min-necessary UI.

PORTALS (per ../14 §2): Reception, Doctor/Nurse, Laboratory/Imaging, Pharmacy, Medical Approval, Beneficiary Management/Registration, Case Manager, Finance, Provider/Network Admin, Org/Super Admin, Medical Director.

ROUTING + AUTH
- OIDC login (Keycloak) + MFA; on success land the user on THEIR portal only (US-070). Session timeout with a warning + re-auth prompt.
- Route guards from the user's effective permissions (../11): the router only mounts routes the user can use. A forbidden deep link → 403 page with a "request access" affordance, and the attempt is audited (US-071).

PERMISSION-DRIVEN MENUS (../14)
- The nav rail is GENERATED from effective permissions — a user never sees a route/menu item they cannot use. Max 7±2 primary items, overflow into "More". Breadcrumb `Portal ▸ Section ▸ Record`. Landmark roles (banner/navigation/main/contentinfo). Keyboard map per ../14 §4 (e.g., `g` then `q` → primary queue).
- Min-necessary UI per zone: Finance portal exposes NO diagnosis/clinical routes; Reception exposes no EMR; Pharmacy no results; Lab no prescriptions (../11 hard rules).

ACCEPTANCE (US-070/071)
- Given valid credentials + MFA, When a user signs in, Then they land only on their role's portal.
- Given my role, When I navigate, Then I see only routes/data my permissions allow (e.g., Finance sees no diagnoses).
- Given a deep link I cannot access, When I open it, Then I get a 403 with a request-access affordance, audited.
- Given inactivity beyond timeout, When I return, Then I am warned and re-authenticated.

Ship: portal shells reusing the 9.1 shell components, permission-driven router, 403/timeout flows, tests (routing + permission-gating + a menu that hides forbidden items).
```

### 9.3 — Flagship screens (match prototypes + wireframes, wired to APIs)

```text
Build the flagship screens, each matching ../12 wireframes + the prototypes and wired to its phase APIs. Read ../12, ../13, ../14, ../21, and the relevant user story per screen. EVERY screen must implement loading / empty / error / success states, full keyboard nav, aria-live for async outcomes, RTL parity, and ≥44px targets.

SCREENS
1. Reception — Eligibility (R1/R2): search beneficiary → min-necessary eligibility result card (verdict + coverage, NO clinical/diagnosis fields). Visit gating.
2. Doctor — Consultation / EMR (R2): encounter (SOAP, vitals, allergies, diagnosis), place investigation orders + e-prescriptions. Only patients under a treating relationship (../11).
3. Lab / Imaging — Queue + Consume (R3): order queue, atomic idempotent consume, result upload, partial fulfillment. No prescription data visible.
4. Pharmacy — Dispense (R3): prescription queue, partial dispensing, substitution, out-of-stock. No lab/imaging results visible.
5. Approvals — Worklist + Decision (R4, US-060): match ../prototype-approvals-worklist.html. Worklist (status/priority/SLA), review view showing EMR/notes/docs (field-scoped), decision panel (Approve/Partial/Reject/Request-info) with MANDATORY rationale (reject reason required) and break-glass (emergency/override/manual) with extra-justification affordance.
6. Executive Dashboard (R5, US-073): KPI widgets (TAT, pending approvals, clinic workload, utilization, no-show, top dx/meds, rejected, financials). EVERY chart has a visible data-table toggle (accessible alternative). Aggregate, PHI-free; finance widgets show no diagnoses.

WIRING + STATES
- Consume the phase APIs (eligibility, emr/orders, lab/imaging, pharmacy, approvals, reporting) via typed clients (zod-validated). Use StatusChip for every status (color+icon+shape+text). Optimistic/loading/empty/error/success handled explicitly with aria-live announcements. Mutations that must not double-apply send Idempotency-Key.

ACCEPTANCE
- Given each screen, When loaded with no/slow/failed/successful data, Then the correct loading/empty/error/success state renders and async outcomes are announced via aria-live.
- Given the approvals screen, When a reviewer rejects without a reason, Then submission is blocked (mandatory rationale) — US-060.
- Given the dashboard, When a chart renders, Then a data-table alternative is available — US-073.
- Given any screen in Arabic, When used, Then layout mirrors correctly and keyboard nav + ≥44px targets hold.
- Given a portal, When inspected, Then no data outside its permission zone is exposed (../11).

Ship: the six screens wired to their APIs, per-screen a11y run (axe + keyboard + screen reader + AR/RTL), E2E for the critical flow of each, tests for min-necessary rendering.
```

---

## Guardrails

- **Accessibility gate blocks "done" on every screen.** axe (fail on serious/critical) + manual keyboard + screen-reader + AR/RTL parity are acceptance criteria per screen ([../21](../21-accessibility-checklist.md)). Visible 3px focus, ≥44px targets, aria-live for async outcomes.
- **No screen exposes data beyond its portal's zone** ([../11](../11-permission-matrix.md)): Reception ≠ EMR, Lab ≠ prescriptions, Pharmacy ≠ results, Finance ≠ diagnoses. Menus are permission-generated; forbidden deep links → audited 403.
- **Status is color+icon+shape+text** (color-blind safe) everywhere — always via StatusChip, never color alone.
- **Fidelity to the prototypes.** Tokens, glass, typography, and the flagship screens match `prototype-hbmp-multiscreen.html` and `prototype-approvals-worklist.html`. Brand hues stay decorative; controls use the accessible teal tokens.
- **Bilingual by construction.** Both `ar` and `en` authored; layout mirrors via logical properties; Arabic never machine-translated or italicized.

## Done when

- The shared design system (tokens, component library, i18n/RTL, theming, logo lockup) is implemented, matches the prototypes, and passes axe in CI.
- Role portals are scaffolded with permission-driven routing/menus; login+MFA lands users on their portal only; forbidden routes are audited 403s.
- At least the six flagship screens (Reception eligibility, Doctor consultation/EMR, Lab/Imaging queue+consume, Pharmacy dispense, Approvals worklist+decision, Executive dashboard) are implemented, accessible (axe + keyboard + screen reader + AR/RTL), bilingual, wired to their APIs, and faithful to the prototypes/wireframes — each with loading/empty/error/success states.
- US-070, US-071, US-060, US-073 (and the R1–R3 screen stories) acceptance criteria pass. Global Definition of Done met.

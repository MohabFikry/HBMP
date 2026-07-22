# 21 — Accessibility Checklist (WCAG 2.2 AA)

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [12-ui-wireframes.md](12-ui-wireframes.md) · [13-ux-flows.md](13-ux-flows.md) · [14-navigation-structure.md](14-navigation-structure.md)

Accessibility is a **hard acceptance criterion**, not an enhancement. Target: **WCAG 2.2 Level AA**, plus Arabic RTL parity and low-literacy/low-bandwidth resilience appropriate to a refugee-serving NGO. This checklist is the gate: *no story is Done unless its screens pass the relevant items below.*

---

## 1. Definition of Done — accessibility gate

A story is not Done unless:
- [ ] Keyboard-only operable end-to-end (no mouse), with visible focus at every step.
- [ ] Passes automated axe-core scan with **zero criticals/serious**.
- [ ] Verified with a screen reader (NVDA or VoiceOver) for the primary task.
- [ ] All status/meaning conveyed by **more than color** (icon + shape + text + tooltip).
- [ ] Contrast ≥ 4.5:1 (text) / ≥ 3:1 (large text & UI components).
- [ ] Works in **Arabic RTL** and English LTR (layout mirrors, not just text).
- [ ] All targets ≥ 44×44px; error messages programmatically associated.
- [ ] Responsive at 320px width without loss of content/function (reflow).

---

## 2. Perceivable

| SC | Criterion | Requirement in HBMP | Status |
|----|-----------|---------------------|--------|
| 1.1.1 | Non-text content | All icons/charts have text alternatives; charts have an accessible data table + summary | ☐ |
| 1.3.1 | Info & relationships | Semantic HTML/landmarks; labels tied to fields; tables use headers/scope | ☐ |
| 1.3.2 | Meaningful sequence | DOM order matches visual/reading order incl. RTL | ☐ |
| 1.3.4 | Orientation | Works portrait & landscape | ☐ |
| 1.3.5 | Identify input purpose | `autocomplete` on personal fields | ☐ |
| 1.4.1 | Use of color | **Status = color + icon + shape + text** (see [0A §5.2](0A-DESIGN-FOUNDATIONS.md)) | ☐ |
| 1.4.3 | Contrast (min) | Palette validated (teal-700 5.9:1, etc.); status hexes ≥4.5:1 on white | ☐ |
| 1.4.5 | Images of text | No text baked into images; real text only | ☐ |
| 1.4.10 | Reflow | No horizontal scroll at 320px; single-column collapse | ☐ |
| 1.4.11 | Non-text contrast | Buttons, inputs, focus, chart elements ≥3:1 | ☐ |
| 1.4.12 | Text spacing | Layout survives increased line/letter/word spacing | ☐ |
| 1.4.13 | Content on hover/focus | Tooltips dismissible, hoverable, persistent | ☐ |

## 3. Operable

| SC | Criterion | Requirement | Status |
|----|-----------|-------------|--------|
| 2.1.1 | Keyboard | Every action reachable & operable by keyboard ([14 §4](14-navigation-structure.md)) | ☐ |
| 2.1.2 | No keyboard trap | Focus escapes all components/dialogs | ☐ |
| 2.4.1 | Bypass blocks | Skip-to-content link; landmarks | ☐ |
| 2.4.3 | Focus order | Logical; modal focus trap returns focus on close | ☐ |
| 2.4.7 | Focus visible | 3px ring `#007A7A` (`--mersal-teal-700`), never removed | ☐ |
| 2.4.11 | Focus not obscured (2.2) | Sticky headers/action bars never hide focused element | ☐ |
| 2.5.3 | Label in name | Visible label text included in accessible name | ☐ |
| 2.5.7 | Dragging movements (2.2) | Any drag (e.g., calendar) has single-pointer alternative | ☐ |
| 2.5.8 | Target size min (2.2) | Interactive targets ≥ 24px (we enforce ≥44px) | ☐ |
| 2.2.1 | Timing adjustable | Session-timeout warning with extend option before logout | ☐ |
| 2.3.1 | Three flashes | No flashing content | ☐ |

## 4. Understandable

| SC | Criterion | Requirement | Status |
|----|-----------|-------------|--------|
| 3.1.1/3.1.2 | Language | `lang`/`dir` set per document & per language switch (ar/en) | ☐ |
| 3.2.1/3.2.2 | On focus/input | No unexpected context change on focus or input | ☐ |
| 3.2.6 | Consistent help (2.2) | Help/contact affordance in consistent location across portals | ☐ |
| 3.3.1 | Error identification | Errors in text, `role="alert"`, associated with field | ☐ |
| 3.3.2 | Labels/instructions | Every input labelled; formats hinted | ☐ |
| 3.3.3 | Error suggestion | Actionable correction guidance | ☐ |
| 3.3.7 | Redundant entry (2.2) | Don't re-ask info already provided in a flow (registration wizard) | ☐ |
| 3.3.8 | Accessible auth (2.2) | No cognitive-only test to log in; MFA supports paste/authenticator app | ☐ |

## 5. Robust

| SC | Criterion | Requirement | Status |
|----|-----------|-------------|--------|
| 4.1.2 | Name/role/value | Custom components expose ARIA name/role/state | ☐ |
| 4.1.3 | Status messages | `aria-live` for consume/dispense/approve/upload results, queue updates | ☐ |

---

## 6. Color-blind & non-color redundancy (normative)

The status system from [0A §5.2](0A-DESIGN-FOUNDATIONS.md) is mandatory everywhere status appears (chips, rows, charts, badges):

| Status | Color | Icon | Shape | Text |
|--------|-------|------|-------|------|
| Eligible/Approved/Completed | Success `#1E7A46` | ✓ | solid pill | "Eligible/Approved" |
| Pending/In review | Info `#1F5FA6` | ⧗ | dashed pill | "Pending" |
| Partial | Attention `#8A5A00` | ◐ | half pill | "Partially used" |
| Expiring/Warning | Caution `#B25E00` | △ | outlined pill | "Expiring" |
| Rejected/Blocked/Suspended/Expired | Danger `#B3261E` | ✕/⛔ | solid square | "Rejected" |
| Inactive/Draft/Cancelled | Neutral `#4A5A61` | ○ | ghost pill | "Inactive" |

Validated for protanopia/deuteranopia/tritanopia (hue separability); icon+shape guarantee non-color legibility. Charts additionally use patterns/direct labels, never color alone.

---

## 7. Test scripts

**Keyboard-only (per portal primary task):**
1. Tab from page load → reach primary action without a mouse.
2. Operate the flow (search → select → act) using Enter/Arrows/Esc only.
3. Confirm focus never lost, never trapped, always visible.

**Screen reader (NVDA/VoiceOver):**
1. Landmarks announced (banner/nav/main).
2. Form fields announce label + state + error.
3. Status chips announce full text (e.g., "Rejected"), not just an icon.
4. `aria-live` announces async outcomes (consumed/dispensed/approved).

**RTL/Arabic:**
1. Switch to Arabic → whole layout mirrors, numerals localized, icons directionally correct.
2. Mixed AR/EN content (e.g., Latin drug names) renders with correct bidi isolation.

**Contrast/target:** automated axe + manual spot-check of custom components and charts; measure target sizes.

---

## 8. Tooling & process
- Automated: **axe-core** in CI on every PR (fail on serious/critical); Lighthouse a11y budget.
- Manual: keyboard + screen-reader checklist per story; quarterly full audit.
- Design tokens enforce contrast & target size so violations are hard to introduce.
- Include people with disabilities and low-literacy beneficiaries in UAT where feasible.

---

### Cross-references
- Status tokens & palette: [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md) · Screens: [12-ui-wireframes.md](12-ui-wireframes.md)
- Navigation & keyboard map: [14-navigation-structure.md](14-navigation-structure.md) · Testing: [26-testing-strategy.md](26-testing-strategy.md)

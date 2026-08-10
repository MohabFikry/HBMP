# Mersal HBMP — Scroll Region & Dropdown Audit

**Date:** 2026-08-10 · **Scope:** every scrolling region and every dropdown/picker in `apps/web` (all portals) plus the controls behind them in `apps/design-system` · **Nature:** read-only. No code was modified. This report is the basis for a separate fix effort.

**Method:** mechanical, not sampled.

- A CSS brace-tracking parser walked `apps/design-system/src/styles/*.css` and `apps/web/src/styles/app.css` and resolved every `overflow` / `overflow-x` / `overflow-y` declaration of `auto`, `scroll` or `overlay` back to its full enclosing selector — 20 declarations, 19 distinct scroll regions.
- A JSX scanner walked all `.tsx` files under `apps/web/src` and `apps/design-system/src`, resolved every `<select>`, `<Select>`, `<SelectField>` and `<Combobox>` element (comment text excluded by checking the line prefix), and for each one determined whether it sits inside a `<Modal>` by counting unbalanced `<Modal>` / `</Modal>` tags before it.
- Each scroll class was then cross-referenced against the call sites that apply it, to separate "the CSS scrolls" from "the element carries the house treatment".
- The two headline claims (§H1 clipping, §M3 scrollbar divergence) were **rendered in headless Chrome against the real compiled stylesheet** and measured, rather than reasoned about. Numbers below are from that render.

---

## 0. Executive summary

**Same shape as the tables/buttons audit: the design system already holds the correct answer, and the gap is adoption. With one exception — this time there is also a real defect in the design system itself.**

### The defect

**An open dropdown is clipped by any scrolling ancestor, and 12 pickers currently sit inside one.**

`.mrs-select-list` and `.mrs-combo-list` are `position: absolute` inside their own control root. `.mrs-modal` is `overflow: auto`. So a picker opened near the bottom of a modal has its option list cut off at the modal's edge; the modal grows a scrollbar and the operator must scroll the *dialog* to read the list — which moves the trigger they just pressed.

Measured, on the real stylesheet, with an 8-option list in a 520px modal:

```
modalOverflow  "auto"
modalBottom    546px
listBottom     813px
clippedPx      267        ← 7 of 8 options unreachable without scrolling the dialog
```

A screenshot of the same probe shows one option half-drawn at the modal's bottom edge and the rest gone. This is not a styling nit; it is a picker that cannot be used at the size the dialog is drawn.

It affects **12 design-system pickers across 8 screens** today — including "record an allergy", "amend a line", "change a member's group" and "change a member's plan". It is also a **migration blocker**: two native `<select>`s inside modals (`CallCentre.tsx:708`, `CallCentreCancel.tsx:192`) are *not* clipped today only because the OS draws their popup outside the page. Converting them without fixing this first would make them worse, not better.

### The adoption gap

**11 of 56 pickers are searchable — 20%.**

| Control | Count | Searchable | Popup |
|---|---:|:---:|---|
| `<Combobox>` (design system) | 11 | ✅ yes | Mersal surface |
| `<Select>` (design system) | 11 | ❌ first-letter typeahead only | Mersal surface |
| `<SelectField>` (design system, labelled `Select`) | 19 | ❌ first-letter typeahead only | Mersal surface |
| native `<select>` | 15 | ❌ | **drawn by the OS** |
| **Total** | **56** | **11 (20%)** | |

The distribution is the finding. `SelectField` — the non-searchable control — is used **19 times**, more than any other picker in the product, while `Combobox` is used 11. That is not a series of considered choices. **There is no `ComboboxField`.** `Field.tsx` exports `InputField`, `SelectField` and `TextareaField` and nothing else, so a developer who wants a picker with a label attached has exactly one thing to reach for, and it is the wrong one. The path of least resistance leads away from the control the product should be using.

### The scroll gap is small and nearly closed

`.mrs-scroll` already exists, is well-reasoned, and is applied at 22 call sites. **5 call sites across 4 CSS classes** still render the platform default. Two regions are excluded on purpose and correctly — the nav rail and the page's own scroller — and the exclusion is documented in the stylesheet.

### Counts

| | Total | Conforming | |
|---|---:|---:|---|
| In-page scroll regions (call sites) | **29** | 22 (76%) | 5 unstyled, 2 excluded |
| — distinct CSS scroll classes | 19 | 13 | 4 unstyled, 2 excluded |
| — deliberate exclusions | 2 | n/a | `nav.mrs-rail`, `.app-main` |
| — reachable by keyboard where needed | 29 | 29 | ✅ no gaps |
| Pickers (all kinds) | **56** | 11 (20%) | 45 non-searchable |
| — inside a `<Modal>` (clipped when open) | **12** | 0 | + 2 native, clipped on conversion |
| — drawing an OS popup | 15 | — | native `<select>` |
| Labelled-picker wrappers offered | 3 | — | **`ComboboxField` does not exist** |

---

## 1. High severity

### H1 — An open dropdown is clipped by its scrolling ancestor. 12 live sites.

**Proven, not inferred.** See §0 for the measurement and the render.

**Cause.** Two rules that are individually correct:

```css
.mrs-modal      { overflow: auto; max-height: 88dvh; }   /* components.css:1205 */
.mrs-select-list,
.mrs-combo-list { position: absolute; z-index: 40; max-block-size: var(--scroll-picker); }
```

`z-index: 40` cannot help: a scrolling ancestor clips its descendants regardless of stacking order. Neither popup is portalled to the body, so both are laid out inside the modal's scrollport and painted inside its padding box.

**A second, less obvious trigger.** Per CSS Overflow §3, when one axis is `visible` and the other is not, the `visible` axis computes to `auto`. So `.pol-tablewrap { overflow-x: auto }` and `.mrs-wl-scroll { overflow-x: auto }` are **vertical** clipping contexts too. No picker sits inside one of those today — `PractitionerAdmin`'s six `Combobox`es are in the split layout's right-hand panel, a sibling of the table, not inside it, and this was checked rather than assumed — but `PolicyProductAdmin`'s two in-table `<select>`s (lines 520, 543) sit inside `.pol-grid`, which is `display: block; overflow-x: auto`. They are safe today only because the OS draws their popup. **Converting them is not safe until H1 is fixed.**

**The 12 affected sites:**

| Screen | Line | Control | What it picks |
|---|---:|---|---|
| `encounter/MemberClinicalPanel.tsx` | 231 | `Select` | Blood group (8) |
| `encounter/MemberClinicalPanel.tsx` | 348 | `Select` | **Allergen — an unbounded catalogue** |
| `encounter/MemberClinicalPanel.tsx` | 360 | `Select` | Severity (3) |
| `AmendLineDialog.tsx` | 216 | `Select` | Amendment reason |
| `booking/EditAppointment.tsx` | 227 | `Select` | Appointment field |
| `MemberAdmin.tsx` | 1914 | `SelectField` | **Group — unbounded** |
| `MemberAdmin.tsx` | 1930 | `SelectField` | **Target plan — unbounded** |
| `PolicyPanels.tsx` | 345, 358 | `SelectField` | Policy attributes |
| `BeneficiaryDocuments.tsx` | 346 | `SelectField` | Document type |
| `DoctorEncounter.tsx` | 1236 | `SelectField` | Clinical field |
| `TransactionActionsDialog.tsx` | 377 | `SelectField` | Transaction action |

The unbounded ones are the worst: the longer the list, the more of it is clipped, and those are exactly the lists a search box would rescue.

**Fix.** Portal both popups to `document.body` with fixed positioning anchored to the trigger's rect, re-measured on scroll and resize, with a flip when there is no room below. This is one change inside `Select.tsx` and `Combobox.tsx`; no call site changes. It must land **before** any native `<select>` conversion.

---

### H2 — 15 native `<select>` elements draw an operating-system popup.

The codebase has already made this argument three separate times, in three separate comments, each written when a different screen was converted:

> *"The design-system Select, not a native `<select>`. A native one cannot style its own option list — the popup is drawn by the OS — so it arrived system-blue and square-cornered inside a rounded Mersal card…"* — `PatientProfile.tsx:911`

> *"A bare `<select>` is drawn by the OS: it sat a few pixels shorter than the date field directly above it, kept square corners against the app's radius, and opened a system-blue list. In a modal whose other two controls are Mersal fields, that does not read as plain — it reads as a control somebody forgot to finish."* — `MemberAdmin.tsx:1908`

> *"Built on the design-system Select rather than a native `<select>`: the OS draws a native option list itself…"* — `BranchSwitcher.tsx:31`

These 15 are the ones that were never revisited. They are concentrated in the call centre (7) and the policy portal (5).

Beyond the styling: an OS popup **ignores `data-theme`**. In dark mode the app draws a dark card and the option list opens light. It also ignores the app's RTL treatment and its focus ring.

| Screen | Line | Options | Source | Styled by |
|---|---:|---|---|---|
| `CallCentre.tsx` | 579 | 8 | `CALL_REASONS` | `.cc-callbar select` |
| `CallCentre.tsx` | 585 | 2 | inline | `.cc-callbar select` |
| `CallCentre.tsx` | 708 | 8 | `CANCEL_REASONS` + "—" | `.mrs-control` |
| `CallCentre.tsx` | 780 | 5 | `OUTCOMES` | `.cc-wrapup select` |
| `CallCentreBooking.tsx` | 305 | 8 | `CALL_REASONS` | `.mrs-control` |
| `CallCentreBooking.tsx` | 316 | 2 | inline, `disabled` when locked | `.mrs-control` |
| `CallCentreCancel.tsx` | 192 | 8 | `CANCEL_REASONS` + "—" | `.mrs-control` |
| `PolicyBook.tsx` | 506 | **unbounded** | `policies` | `.mrs-control` |
| `PolicyBook.tsx` | 584 | **unbounded** | `policies` | `.mrs-control` |
| `PolicyBulk.tsx` | 258 | 7 | `JOB_TYPES` | `.mrs-control` |
| `PolicyProductAdmin.tsx` | 520 | 5 | `LIMIT_TYPES` | *none* — bare browser default |
| `PolicyProductAdmin.tsx` | 543 | 4 | `RESET_PERIODS` | *none* — bare browser default |
| `investigations/InvestigationWorkspace.tsx` | 568 | **unbounded** | `procedureTypes` | `.rx-field-input` |
| `prescribing/PrescribingWorkspace.tsx` | 824 | **unbounded** | `frequencies` | `.rx-field-input` |
| `dev/DevLoginForm.tsx` | 61 | 23 | `PORTALS` | `.mrs-control` |

`DevLoginForm` is a dev-only harness and is listed for completeness; converting it is optional.

The two in `PolicyProductAdmin` carry **no class at all** — they are the browser's untouched default control, sitting in a table of Mersal-styled inputs. That mirrors exactly the single unstyled `<button>` the previous audit found at `CallCentre.tsx:849`.

**Four of them list unbounded server data** (`policies`, `procedureTypes`, `frequencies`) through a control whose only search affordance is first-letter typeahead. `PolicyBook`'s two are the worst case: an operator looking for a policy number must arrow through every policy on the book.

---

## 2. Medium severity

### M1 — 45 of 56 pickers cannot be typed into.

This is the request restated as a count. Breaking it down by whether search is *merely nicer* or *actually required*:

**Required — the list is unbounded or long:**

| Site | List |
|---|---|
| `PolicyBook.tsx:506, 584` | every policy on the book |
| `investigations/InvestigationWorkspace.tsx:568` | procedure-type catalogue |
| `prescribing/PrescribingWorkspace.tsx:824` | refill-frequency catalogue |
| `encounter/MemberClinicalPanel.tsx:348` | allergen catalogue |
| `MemberAdmin.tsx:1914, 1930` | groups; plans |
| `BatchIntake.tsx:287, 294, 301` | plans; network tiers; branches |
| `BranchSwitcher.tsx:52, 76` | branches (app bar, every portal) |
| `AmendLineDialog.tsx:216` | amendment reasons (server catalogue) |

**Nice to have — a fixed vocabulary under ~10.** Everything else: call reasons (8), cancel reasons (8), outcomes (5), job types (7), limit types (5), reset periods (4), blood groups (8), severities (3), directions (2).

**On "all dropdowns", including the two-option ones.** `Select`'s own doc comment argues that a select-only control is right for *"a closed vocabulary of five — Male/Female/Other/Unknown"*. That argument is not wrong, but the product has already voted against it: **`PractitionerAdmin` uses `Combobox` for a two-option Nurse/Doctor list** (line 428) and for its status list (line 723). The newest code already treats `Combobox` as the default at every size. Standardising on it is therefore *consistency with existing practice*, not an override of the design system — and one control everywhere is worth more than a threshold rule that every screen will draw in a different place. Recommend proceeding as asked; the `Select` doc comment should be rewritten rather than left contradicting the code.

### M2 — `Combobox` cannot replace `Select` yet: three missing props.

Migration is blocked at three call sites until these land.

| Missing | Needed by | Why |
|---|---|---|
| `leadingIcon` | `BranchSwitcher.tsx:52, 76` | the branch glyph sits *inside* the control and carries the meaning — the comment there says the accessible name replaced a visible label precisely because the icon does the work |
| `shape="pill"` | `BranchSwitcher.tsx:52, 76` | the app-bar silhouette, matched to the app-bar search |
| closed-state `hint` | `BranchSwitcher.tsx` (`"Home branch"`), `BatchIntake.tsx:287, 294` (plan/tier codes) | `Select` renders `label · hint` when closed; `Combobox` renders `selected?.label` only, so the hint vanishes the moment the list closes |

`Combobox` already has `hint` and `keywords` **in the list and in matching** — the gap is only the closed control. `Select`'s `disabled`, `placeholder` and `invalid` all have `Combobox` equivalents.

### M3 — 5 scroll regions across 4 classes render the platform scrollbar.

Rendered side by side against the compiled stylesheet: `.mrs-scroll` draws a thin rounded pill inset from the track; the unstyled control draws a full-bleed square-capped grey slab with arrow buttons at both ends. On macOS the unstyled one is worse in the other direction — invisible until scrolled, so a list that scrolls is indistinguishable from one that does not.

| Class | Call site | Axis | What it is |
|---|---|---|---|
| `.pol-grid` | `PolicyProductAdmin.tsx:485` | x | the benefit-category grid |
| `.pol-costshare` | `PolicyProductAdmin.tsx:600` | x | the per-tier cost-share table |
| `.rx-schedule` | `prescribing/PrescribingWorkspace.tsx:882` | x | chronic collection schedule |
| `.rx-dispense-scroll` | `pharmacy/PrescriptionPage.tsx:588` | x | dispense table |
| `.rx-dispense-scroll` | `lab/InvestigationOrderPage.tsx:425` | x | investigation order table |

All five are horizontal scrollers, and `.mrs-scroll`'s `overscroll-behavior: contain` is exactly right for them for the reason `.mrs-wl-scroll` already documents: horizontal overscroll is what browsers map to back/forward navigation, and a sideways flick inside a dispense table must not leave the page. **Note** that `.mrs-wl-scroll` deliberately sets `overscroll-behavior-block: auto` so the page keeps scrolling under the cursor; these five want the same treatment, not bare `.mrs-scroll`.

### M4 — The two `PolicyProductAdmin` tables are their own scrollport, which costs them their table layout.

`.pol-grid, .pol-costshare { display: block; overflow-x: auto }`. The stylesheet already knows this is a compromise and documents the consequence:

> *"`display: block` buys the horizontal scroll, but it also takes the table OUT of table layout: the browser wraps the rows in an anonymous shrink-to-fit table, so `width: 100%` applies to the block box while the columns size to their own content. A two-column series table then draws its row rules across a quarter of the card and stops — which reads as a clipped or broken table, not as a narrow one."*

The fix already exists — `.pol-tablewrap > .pol-grid { display: table; overflow-x: visible }` — and **11 of the 13 call sites use it.** `PolicyProductAdmin`'s two are the only ones that never got a wrapper. They are also, consequently, the only two tables in the product that scroll without being keyboard-reachable: every wrapped site carries `tabIndex={0}` and `.mrs-scroll-focusable`, and these two carry neither, which is WCAG 2.1.1 for a region a pointer can scroll and a keyboard cannot.

Fixing M4 fixes M3's first two rows as a side effect.

### M5 — Three pickers label their options in English regardless of language.

```tsx
// BatchIntake.tsx:245-255
planOptions   = reference.plans.map((p)    => ({ …, label: p.nameEn, hint: p.planCode }))
tierOptions   = reference.tiers.map((x)    => ({ …, label: x.nameEn, hint: x.tierCode }))
branchOptions = reference.branches.map((b) => ({ …, label: b.nameEn }))
```

`nameAr` is present on all three schemas (`policyApi.ts:60, 70`, `branchApi.ts:204`) and `AmendLineDialog.tsx:145` shows the correct form one file away:

```tsx
label: t({ en: r.nameEn, ar: r.nameAr })
```

Same defect at `BeneficiaryPortal.tsx:505, 509, 513` and `MemberAdmin.tsx:1922` (`` `${g.groupCode} — ${g.nameEn}` ``). An Arabic operator gets an English plan list on the batch-enrolment screen — and once these become searchable, they will also be **unsearchable in Arabic**, because the filter matches on `label`. So M5 must be fixed *with* the conversion, not after it.

---

## 3. Low severity

**L1 — `.cc-callbar select, .cc-wrapup select, .cc-cancel select, .cc-callmeta select`** (`app.css:1116`) hand-reimplements `.mrs-control` at `--r-sm` instead of the field radius, and states no focus style. Moot once H2 lands; the rule should be deleted at that point rather than left behind.

**L2 — `.rx-field-input`** (`app.css`) is a third field skin beside `.mrs-control` and `.mrs-select-trigger` — same job, own border, own radius, own `min-block-size: 44px`. Two of the native selects wear it. Worth collapsing into `.mrs-control` when those two are converted.

**L3 — Nothing gates any of this.** No test asserts that a scrolling region carries `.mrs-scroll`, that no native `<select>` ships in the SPA, or that a picker is not placed inside a scrollport without a portalled popup. Every finding in this report could reappear silently. The tables/buttons pass ended with eight guard files and `tools/ci/check-design-guards.py`; this one should extend that manifest rather than start a parallel one.

**L4 — `.icd-results`** (`DoctorEncounter.tsx:1192`) is a search field over a result list of buttons — deliberately a staging picker, not a combobox, since a diagnosis is *added to a list* rather than *chosen as a value*. Correct as built; recorded so it is not swept into the conversion by mistake. Same for `CommandPalette` and the two bespoke async comboboxes.

**L5 — `.mrs-select-list` / `.mrs-combo-list` duplicate 14 declarations verbatim.** Once H1 changes the positioning strategy, that duplication becomes two places to get the anchoring right. Worth extracting to a shared `.mrs-popup` at the same time.

---

## 4. What is already right

Recorded so the fix effort does not "improve" it.

- **`.mrs-scroll` is a genuinely good standard, and its doc comment is the best explanation of the problem in the repo.** It fixes scroll chaining and the platform scrollbar together, declares both the standard and the `::-webkit-` properties because neither alone covers the target browsers, insets the thumb with a transparent border so it reads as a floating pill, and styles the corner so two axes do not meet in an unstyled grey square.
- **Its exclusions are correct and stated.** The nav rail and `.app-main` keep the OS scrollbar, because a thin bar on the primary scroller of a long worklist is a real ergonomic loss. Do not "finish the job" by adding `.mrs-scroll` to those.
- **`.mrs-wl-scroll` splits `overscroll-behavior` per axis** — `inline: contain`, `block: auto` — and the comment records the bug that forced it (the page stopped scrolling wherever the cursor sat over a table). This is the model for M3.
- **`.mrs-scroll-focusable` was lifted out of the data table** so a keyboard-scrollable region is not the table's private arrangement. All 29 scroll regions are keyboard-reachable — either via `tabIndex={0}`, via focusable children (`.npane-body`, `.icd-results`), or via the control that owns the popup. **No gaps.**
- **`Combobox` is well built.** Prefix matches rank above substring matches, so typing "sud" offers Sudan before South Sudan. `keywords` lets "Egypt" be found by "EG" or "+20". It never keeps free text — Escape and blur revert to the selection, so a half-typed value cannot survive to fail validation somewhere far away. Selection is carried by a check as well as a tint, because the active tint is the same hue.
- **`Select` implements the APG select-only pattern properly** — focus stays on the trigger, `aria-activedescendant` carries the active option, typeahead/Home/End/Escape all behave. Its problem is that it is the wrong pattern for this product, not that it is wrong.
- **The three bespoke comboboxes are justified.** `DrugCombobox` and `CptCombobox` search the server as you type; `CommandPalette` is a command surface, not a field. All three already carry `.mrs-scroll` on their lists.
- **11 of 13 `.pol-grid` / `.pol-costshare` call sites are correctly wrapped**, and `.bulk-columns` got a specific carve-out with a stated reason (a nested scrollport would have detached the sticky `thead` from the box that actually scrolls).

---

## 5. Proposed fix order

Ordered by dependency, not by severity — H1 and M2 are prerequisites for the conversion work, so they come first even though M2 is medium.

| # | Step | Why here |
|---:|---|---|
| 1 | **Portal `Select` + `Combobox` popups** (H1). Fixed positioning anchored to the trigger, re-measured on scroll/resize, flip when there is no room below. Extract the shared `.mrs-popup` (L5) while in there. | Everything else makes it worse until this is done. 12 live sites, and conversion adds more. |
| 2 | **Add `leadingIcon`, `shape` and closed-state `hint` to `Combobox`** (M2). | Blocks steps 4–5. |
| 3 | **Add `ComboboxField` to `Field.tsx`.** | Without it, step 5 has 19 sites with no labelled control to move to, and the next developer reaches for `SelectField` again. This is the change that makes the standard stick. |
| 4 | **Convert the 11 `<Select>` → `Combobox`**, `BranchSwitcher` first (it is in the app bar of every portal, so it is the regression canary). | Depends on 1–2. |
| 5 | **Convert the 19 `<SelectField>` → `ComboboxField`.** Fix M5's English-only labels in the same pass — a searchable list that cannot be searched in Arabic is not fixed. | Depends on 1–3. |
| 6 | **Convert the 14 product `<select>` → `ComboboxField`** (H2). Delete `.cc-*` select rules (L1) and fold `.rx-field-input` into `.mrs-control` (L2) as each site is emptied. `PolicyProductAdmin`'s two need step 7 first — they sit inside a clipping context. | Depends on 1. |
| 7 | **Wrap `PolicyProductAdmin`'s two tables in `.pol-tablewrap`** with `tabIndex={0}` + `.mrs-scroll-focusable` (M4). Restores table layout, adds keyboard reach, and closes two of M3's five rows. | Blocks step 6 for that screen. |
| 8 | **Apply the scroll standard to the remaining 3 sites** — `.rx-schedule`, `.rx-dispense-scroll` ×2 (M3), using `.mrs-wl-scroll`'s per-axis `overscroll-behavior`, not bare `.mrs-scroll`. | Independent; can run any time. |
| 9 | **Rewrite `Select`'s doc comment** to say what it is now for (or deprecate it), so the file stops arguing against the product's own decision. | After 4–6, when the answer is known. |
| 10 | **Guards + `check-design-guards.py` manifest entries** (L3): no native `<select>` in the SPA; every `overflow: auto/scroll` selector is either in the exclusion list or paired with `.mrs-scroll`; no picker inside a scrollport without a portalled popup. Verify each is non-vacuous by reintroducing the defect it is about. | Last, so it locks in what actually shipped. |

**Step 1 alone is worth shipping on its own** if the conversion is deferred: it fixes a control that is currently unusable in 12 dialogs, and it touches two design-system files with no call-site changes.

---

## Appendix A — Scroll region inventory (29 call sites, 19 CSS regions)

`✓` = carries `.mrs-scroll`. `KB` = keyboard-reachable, and by what means.

### Design system

| Selector | Axis | Call site | ✓ | KB |
|---|---|---|:-:|---|
| `.mrs-wl-scroll` | x | `DataTable.tsx:254` | ✓ | `tabIndex` + focusable |
| `.mrs-modal` | both | `Modal.tsx:53` | ✓ | focusable children |
| `.mrs-select-list` | y | `Select.tsx:184` | ✓ | via trigger |
| `.mrs-combo-list` | y | `Combobox.tsx:204` | ✓ | via input |
| `nav.mrs-rail` | both | `NavRail.tsx` | — | focusable children |

### Web app

| Selector | Axis | Call site(s) | ✓ | KB |
|---|---|---|:-:|---|
| `.app-main` | y | shell | *excluded* | page scroller |
| `nav.mrs-rail` (+ `@media ≤760px`) | y / x | shell | *excluded* | focusable children |
| `.npane-body` | y | `NotificationPane.tsx:175`, `UserPane.tsx:84` | ✓ | focusable children |
| `.cmdk-list` | y | `CommandPalette.tsx:146` | ✓ | via input |
| `.pol-tablewrap` | x | `MemberAdmin` ×5, `PolicyBulk:399`, `BatchIntake:434`, `PolicyAnalytics:593` | ✓ | `tabIndex` |
| `.bulk-columns` | both | `BulkTemplateActions.tsx:149` | ✓ | `tabIndex` |
| `.profile-raw` | y | `ProfileSectionViews.tsx:1744` | ✓ | `tabIndex` |
| `.dash-day` | y | `ReceptionDashboard.tsx:383` | ✓ | `tabIndex` |
| `.reg-bulklist` | y | `RegistrationApprovals.tsx:707` | ✓ | `tabIndex` |
| `.icd-results` | y | `DoctorEncounter.tsx:1192` | ✓ | focusable children |
| `.rx-combobox-list` | y | `DrugCombobox.tsx:176`, `CptCombobox.tsx:151` | ✓ | via input |
| **`.pol-grid`** | x | `PolicyProductAdmin.tsx:485` | ✗ | **✗** |
| **`.pol-costshare`** | x | `PolicyProductAdmin.tsx:600` | ✗ | **✗** |
| **`.rx-schedule`** | x | `PrescribingWorkspace.tsx:882` | ✗ | ✗ |
| **`.rx-dispense-scroll`** | x | `PrescriptionPage.tsx:588`, `InvestigationOrderPage.tsx:425` | ✗ | ✗ |

*(`.pol-grid` / `.pol-costshare` have 13 call sites in total; the other 11 sit inside `.pol-tablewrap` or `.bulk-columns`, which reset them to `display: table; overflow-x: visible` and own the scroll themselves. Only the two above are their own scrollport.)*

**Scroll tokens** — `tokens.css:57-59`: `--scroll-picker: 288px` (dropdowns, code lists), `--scroll-panel: 420px` (in-card lists), `--scroll-sheet: min(70dvh, 640px)` (document previews). All three are used; no site hard-codes a scroll height.

---

## Appendix B — Picker inventory (56)

### `<Combobox>` — searchable (11) ✅

`PractitionerAdmin.tsx` 415 (accounts), 428 (type, **n=2**), 437 (specialty), 641 (add specialty), 694 (add branch), 723 (status) · `booking/BookingForm.tsx` 205 (branch), 225 (specialty), 239 (doctor) · `CallCentreAppointments.tsx` 234 (branch filter) · `BeneficiaryPortal.tsx` 301

### `<SelectField>` — not searchable (19)

`ApprovalEngineAdmin.tsx` ×5 · `MemberAdmin.tsx` ×3 (**2 in a modal**) · `PolicyPanels.tsx` ×2 (**both in a modal**) · `BranchInventory.tsx` ×2 · `BeneficiaryDocuments.tsx`, `BranchRoster.tsx`, `DoctorEncounter.tsx`, `MasterListAdmin.tsx`, `PolicyAnalytics.tsx`, `RestrictedResultCard.tsx`, `TransactionActionsDialog.tsx` ×1 each

### `<Select>` — not searchable (11)

`encounter/MemberClinicalPanel.tsx` ×3 (**all in a modal**) · `BatchIntake.tsx` ×3 (plan, tier, branch) · `BranchSwitcher.tsx` ×2 (app bar, `leadingIcon` + `pill`) · `booking/EditAppointment.tsx` (**modal**) · `PatientProfile.tsx` 917 · `AmendLineDialog.tsx` 216 (**modal**)

### Native `<select>` (15) — see §H2 for the full table

### Bespoke searchable listboxes (3) — correct as built

`prescribing/DrugCombobox.tsx` (server search) · `investigations/CptCombobox.tsx` (server search) · `shell/CommandPalette.tsx` (command surface)

### Related, deliberately not a picker (1)

`DoctorEncounter.tsx:1192` `.icd-results` — search field over a result list of buttons; a diagnosis is *added to a staged list*, not *chosen as a value*.

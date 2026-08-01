# Frontend audit — Beneficiary Management portal

**Date:** 2026-07-31
**Scope:** every screen and sub-screen reachable from the `beneficiary_mgmt` / `beneficiary_mgmt_supervisor`
portals (`/beneficiaries/*`), plus the shared components they depend on.
**Method:** the real portal driven in Chromium against the running Compose stack, signed in through
identity-service as `beneficiary_mgmt`. Walked EN/AR × light/dark × 1440 / 768 / 600 / 390 px, including
error, empty and loading states. Findings that could not be reproduced in the browser were confirmed against
the source and, where relevant, measured with `getComputedStyle` / `getBoundingClientRect`.
**Reference:** `HBMP-Design/0B-DESIGN-SYSTEM-UI.md`, `HBMP-Design/21-accessibility-checklist.md`,
`HBMP-Design/14-navigation-structure.md`, skill `healthcare-uiux-designer`.

> **Round 2 (same day)** — a follow-up pass on reported layout and scrolling problems is in §7. It found a
> single root cause behind the "everything scrolls wrong" symptom on every screen.

---

## 1. Surface covered

| Section | Route | Screen |
|---|---|---|
| Register New (one member) | `/beneficiaries/register` | `BeneficiaryPortal.tsx` → `RegisterOneMember` |
| Register New (many from a file) | same, second tab | `BatchIntake.tsx` |
| Registration Approvals | `/beneficiaries/approvals` | `BeneficiaryPortal.tsx` → `RegistrationApprovals` |
| Search / Manage | `/beneficiaries/manage` | `BeneficiaryPortal.tsx` → `BeneficiaryManage` |
| Status & Reactivation | `/beneficiaries/status` | `BeneficiaryPortal.tsx` → `BeneficiaryStatus` |
| Eligibility Check | `/beneficiaries/eligibility` | `ReceptionEligibility.tsx` (shared with Reception) |
| Members + detail tabs | `/beneficiaries/members` | `MemberAdmin.tsx`, `BeneficiaryDocuments.tsx`, `PolicyPanels.tsx` |
| Groups | `/beneficiaries/groups` | `PolicyBook.tsx` |
| Bulk & Imports | `/beneficiaries/bulk` | `PolicyBulk.tsx`, `BulkTemplateActions.tsx` |
| Utilization | `/beneficiaries/utilization` | `PolicyBook.tsx` |
| Analytics (6 tabs) | `/beneficiaries/analytics` | `PolicyAnalytics.tsx` |
| Patient profile | `/beneficiaries/patient`, `/patients/:id` | `PatientProfile.tsx`, `ProfileSectionViews.tsx` |
| Notifications | `/beneficiaries/notifications` | `Notifications.tsx` |
| Shell | all | `AppShell.tsx`, `CommandPalette.tsx`, `NotificationPane.tsx`, `UserPane.tsx` |

**Baseline hygiene that held up.** Every form control on every screen has a programmatic label; every table
carries a `<caption>` and `scope` on its headers; `aria-live` regions are present on async outcomes; no
console errors; status chips implement the four-cue system (including the square-badge tell for negative
states, `components.css` `.mrs-chip.bad`); the register form already moved focus to the first invalid
control on a refused submit.

---

## 2. Fixed

### P0 — broken

#### 2.1 The portal was unusable below 760 px

`apps/web/src/styles/app.css` — `@media (max-width: 760px)`

The block turns the nav rail into a bottom tab bar, but never reset what the desktop rail sets: `top: 60px`,
`height: calc(100dvh - 60px)`, `grid-row: 2`, `align-self: start`. A `position: fixed` box with both `top`
and `bottom` resolved stretches between them regardless of `height`, so the "tab bar" was a full-viewport
panel sitting on top of the page. `align-items: stretch` then made each tab as tall as the viewport.

Measured before: rail `600 × 839`, first item `599 × 839`, page content behind it.
Measured after: rail `57 px` tall, pinned to the bottom, items `56 px`.

A second defect in the same block: `.mrs-navi` carries `width: 100%` (correct for a vertical list) and it was
never reset, so every tab was as wide as the bar and exactly one was ever on screen — a tab bar showing one
tab is a label. Tabs now size to their content (72–143 px, 1068 px of horizontal scroll for 11 sections).

> The block's own comment says this bar exists because "on a phone the app had no navigation at all". It was
> replaced with navigation that covered the app instead.

#### 2.2 Tablet width silently dropped four table columns

`apps/web/src/styles/app.css` — `.pol-screen > * { min-inline-size: 0 }`

`.pol-screen` is a grid; grid items default to `min-width: auto`, so they can never be narrower than their
own min-content. The 1034 px member table therefore pushed `.mrs-card` past the viewport, where
`.app-main { overflow-x: hidden }` cut it off. At 768 px, **Status, From, Waiting period and % used were
unreachable, with no scrollbar to suggest they existed.**

`.mrs-wl-scroll` already owns the horizontal scroll and already declares `max-inline-size: 100%` — it had
nothing finite to resolve that percentage against.

Measured on `/beneficiaries/members` at 768 px:

| | wrapper width | scrollWidth | scrollable |
|---|---|---|---|
| before | 1034 | 1034 | no (clipped) |
| after | 421 | 1034 | yes |

### P1 — accessibility and design-system contract

#### 2.3 Combobox errors were colour-only

`apps/web/src/screens/BeneficiaryPortal.tsx` — `ComboField`

The design system's `Labelled` renders `<Icon name="cross" />` before every error. `ComboField` — the local
wrapper used for Gender, Nationality, Plan, Network tier, Identifier type and Default branch — rendered the
text alone. On a refused submit the same form showed `✕ Enter a real date` under a text box and a bare
`Required.` under the droplist beside it: red text and nothing else, so a reader who cannot see red sees an
ordinary caption. Fails 0B §6 ("error uses icon+text+red border, not colour alone") and WCAG 1.4.1.

Same component: `aria-describedby` used `help ? help-id : error ? error-id : undefined`, which announces the
help text *instead of* the error whenever a field has both — i.e. exactly when the error matters. Now joined,
matching `Labelled`.

#### 2.4 Checkbox targets below both bars

`apps/design-system/src/styles/components.css` — `.mrs-checkbox`; `apps/web/src/styles/app.css` — `.ben-checkbox`

`.mrs-checkbox` was 20 × 20 px and `.ben-checkbox input` re-declared 18 × 18 px — the same control drawn at
two sizes depending on the screen. Both are below WCAG 2.2 AA Target Size (Minimum) of 24 px and far below
this project's own 44 px bar (`21-accessibility-checklist.md`). Affected the Registration Approvals
worklist (24 checkboxes: documents-verified and coverage-bound, the two cells that record an approval
decision), the register form's approximate-date qualifier, and the analytics compare toggle.

Now a 24 px box with a transparent `::after` outset bringing the hit area to 44 px without moving the box.
`.ben-checkbox`'s row target went 40 → 44 px — the row that *was* the justification for a small box was
itself 4 px short of the bar.

#### 2.5 Dark mode: unchecked checkboxes looked ticked

`accent-color` recolours a *checked* box only. An unchecked native checkbox kept the UA's light-mode chrome —
a white filled square on a deep-teal card. Added `color-scheme: dark` under `html[data-theme="dark"]`.

#### 2.6 Modals had no visible close control

`apps/design-system/src/components/Modal.tsx`

Radix gives Esc and outside-click, but neither is visible: a reference modal (the bulk column contract) or a
document preview offered a mouse or touch user no way out that they could see. "It is dismissible" and "it
looks dismissible" are different claims, and only the second one is on screen.

Added a 44 px close to the dialog chrome for every modal in the app, plus a `wide` variant for reference
content. Removed the now-duplicate footer Close from the document preview — two controls with the same name
for the same job is one more thing to read, not one more way out.

### P2 — copy, consistency, RTL

#### 2.7 Empty required fields answered with format rules

`apps/web/src/screens/BeneficiaryPortal.tsx` — `err()` / new `isMissing()`

Pressing Register on an empty form told the operator that "names can contain letters, spaces, hyphens,
apostrophes and periods only" under three blank name boxes, and asked for "8–15 digits, with an optional
leading +" under a blank phone. That is an answer to a question nobody asked — the field is not wrong, it is
missing. The droplists beside them said `Required.`, so **the same failure had two explanations depending on
whether the control happened to be a text box or a list.**

Blank now yields `Required.` everywhere; the rule-specific message still fires the moment a value exists,
which is the case it was written for.

> ⚠️ **This reverses a documented decision.** `test/beneficiary-portal.test.tsx` previously asserted
> `toHaveLength(4)` with the comment "Everything else carries a rule-specific message … rather than a bare
> Required". The test and its comment were updated to record the new intent and why it changed. Revert if you
> disagree with the reading.

#### 2.8 The phone pair broke alignment under error

`.ben-phone` used `align-items: end`, correct only while both cells are the same height. The moment Number
failed validation it grew an error line, the pair's bottom edge moved with it, and Country code — the half
that did *not* fail — slid a full line down the page, so the two boxes of one value no longer sat on the same
row. The two cells are nested inside `.ben-phone`, so the `.ben-grid > .mrs-field` anatomy rule never reached
them. Fixed to `start` plus the same label/control/message grid.

#### 2.9 Nav labels and page titles disagreed

| Nav | Title (before) | Now |
|---|---|---|
| Eligibility Check *(beneficiaries)* / Eligibility Search *(reception)* | Eligibility Search | **Eligibility Check** everywhere |
| Search / Manage | Search / manage | Search / Manage |
| Status & Reactivation | Status & reactivation | Status & Reactivation |

Eligibility was the worst case: one screen mounted under two portals whose rails named it differently, with a
heading that matched neither, and an Arabic label (التحقق من الأهلية = "eligibility check") that disagreed
with the English on the Reception side. Prose references in `BeneficiaryPortal.tsx` and `PatientProfile.tsx`
updated to match.

#### 2.10 The eligibility screen named the wrong role

`ReceptionEligibility.tsx` help text read "Minimum-necessary — **reception** sees coverage only, never
clinical data." The screen is mounted under both `/reception/eligibility` and `/beneficiaries/eligibility`, so
a registration officer was being told about somebody else's permissions. The rule is a property of the
search, not of who runs it, and now says so.

#### 2.11 Registration Approvals repeated one sentence per row

"Decisions are made by a beneficiary-management supervisor." rendered **once per row** — twelve identical
copies of the same paragraph filling the widest column, squeezing the officer's actual notes into a 260 px
gutter beside it. It is a fact about the worklist, not about any row, and it is already stated once above the
table. Rows now show an em dash, with the reason kept in a visually-hidden span.

#### 2.12 Members: a status chip for a non-state, and untranslated enums

* `waitingPeriodState` rendered a `ⓘ None` chip on all 25 rows. The column that exists to flag the handful of
  members still serving a waiting period was drawing the eye to the ones who are not. Now an em dash, with
  the chip reserved for `Serving` (warn) and `Served` (ok).
* policy-service types `status`, `relationship` and `waitingPeriodState` as bare `string`
  (`api/policyApi.ts`), and the table rendered them straight through. **In Arabic the Status column said
  "Active", Relationship said "Principal", Waiting period said "None"** inside an otherwise fully Arabic
  table. Added `ENUM_LABELS` / `useEnumLabel()` with the raw value as fallback, so a value the server adds
  later shows as itself rather than disappearing.

#### 2.13 "Open full profile" was a bare link beside four buttons

0B §10c is explicit: *"a bare text link next to a button is a hierarchy claim; make it deliberately or not at
all."* The claim was being made backwards — opening the record is what an officer does on nearly every member
they select, while terminating one is rare and irreversible. `Open full profile` is now the group's primary
(still an `<a>`, so it can be middle-clicked); `Terminate` moved to the `danger` variant the design system
already defines.

#### 2.14 Bulk & Imports spoke the engine's vocabulary

The first question on the screen — "What are you uploading?" — was answered with `MemberEnrolment`,
`ProviderTierAssignment`, `BenefitRuleImport`: the platform's job-type identifiers, in English regardless of
locale. Added `JOB_TYPE_LABELS` (the values sent to the engine are unchanged). The raw browser `Choose File`
control is now themed via `::file-selector-button` — kept native so keyboard operation, the OS dialog and
screen-reader support come for free.

#### 2.15 The expected-columns modal broke the keys it exists to publish

At the default 520 px width the key column rendered `card_number` as `card_num` / `ber` and `first_name` as
`first_na` / `me`. `overflow-wrap: anywhere` is required by 0B §10c because these are exact match keys — but
with no width to work in, "anywhere" is where they broke. A contract that has to be reassembled by eye is not
a contract. Widened the modal (`wide`) and gave the key column a 12 rem floor.

Separately, `Icon` carries no intrinsic size, so the required/optional icon collapsed inside its flex row and
required-ness was left carried by the word alone — the four-cue rule half-applied. Sized in `.bulk-req svg`.

#### 2.16 RTL: `+20` rendered as `20+`

`+` is a bidi-neutral character and takes the paragraph's direction, so in Arabic the Egypt dialling code
rendered as a country code that does not exist — on the field used to reach a beneficiary. Isolated with
`unicode-bidi: isolate; direction: ltr` on `.ben-phone`'s combobox label, which leaves the mirroring of the
flag and chevron untouched.

#### 2.17 Card-number hint described a character, not the thing

"Usually starts with #" → "The number printed on the beneficiary's card. A leading # is optional." The card
number is the key the record dedupes on; the old hint raised the question it should have answered.

### Files changed

```
apps/design-system/src/components/Modal.tsx        +32
apps/design-system/src/styles/components.css      +103
apps/web/src/portals/catalog.ts                     +2
apps/web/src/screens/BeneficiaryDocuments.tsx       +5
apps/web/src/screens/BeneficiaryPortal.tsx         +72
apps/web/src/screens/BulkTemplateActions.tsx       +11
apps/web/src/screens/MemberAdmin.tsx               +67
apps/web/src/screens/PatientProfile.tsx             +2
apps/web/src/screens/PolicyBulk.tsx                +21
apps/web/src/screens/ReceptionEligibility.tsx       +8
apps/web/src/styles/app.css                        +92
apps/web/test/beneficiary-portal.test.tsx          +14
apps/web/test/routing.test.tsx                      +9
```

### Verification

* `tsc --noEmit` clean in `apps/web` and `apps/design-system`.
* `apps/web`: **381 / 381** tests pass, including `a11y-routes.test.tsx` — axe over every route × locale ×
  theme, no serious/critical violations.
* `apps/design-system`: **36 / 36** tests pass.
* Layout fixes measured in a real browser, before and after (numbers in §2.1 and §2.2).

---

## 3. Open — needs server work

### 3.1 Analytics chart tables have English headers in Arabic

`PolicyAnalytics.tsx` → `SeriesCard`, `series.columns`

Every accessible chart alternative renders `MOVEMENT / MEMBERS`, `RELATIONSHIP / MEMBERS`, `PLAN / MEMBERS`
in Arabic. `AnalyticsSeries` already carries `titleEn`/`titleAr` and `labelEn`/`labelAr`; `columns` is a
single monolingual array.

**Fix belongs in reporting-service**: add `columnsAr` (or emit column *keys* the client resolves). A
client-side translation table would be a second source of truth for the same contract — the failure mode this
codebase guards against elsewhere.

---

## 4. Open — needs a product decision

### 4.1 Search / Manage cannot prevent duplicates *(highest impact)*

A search for `a` returns 22 rows including **eight indistinguishable "Amina Yusuf"**, three "Sara Hassan" and
two "Layla Mahmoud". Pending registrations have no member number and no identifier, so every distinguishing
column is an em dash:

```
Sara Hassan     —    —    ⧗ Pending    [Open]
Sara Hassan     —    —    ⧗ Pending    [Open]
Sara Hassan     —    —    ⧗ Pending    [Open]
```

This is the screen whose job is finding the existing record *before* a second one is created, and the
duplicate-identifier 409 handler in `BeneficiaryPortal.tsx` sends the operator here by name. Suggest adding
**created-at** and the **application id** to the row, and grouping likely duplicates.

### 4.2 Search / Manage has no result count, sort or pagination

22 rows arrive with no header saying how many, no sortable column (`aria-sort` is part of the 0B data-table
spec), and no paging. An operator cannot tell a complete result from a truncated one.

### 4.3 Two search layouts in one portal

Search / Manage puts the button *below* the field; Members attaches it to the *right*. Same job, same portal,
two shapes. Pick one.

### 4.4 Members: columns carrying no information

* `NAME` is an em dash on 20 of 25 rows — bulk-enrolled members have no linked person record. Either the
  column needs an explanation or the rows need a name.
* `% used` mixes `—` (BULK rows) and `0%` (MEM rows) for the same "no utilization yet" fact.

---

## 5. Open — design-system debt

| # | Finding | Where |
|---|---|---|
| 5.1 | Nine Analytics filters (Payer, Plan, Group, Branch, Network tier, Benefit category, Member status, Relationship, Utilization band) render as **blank bordered boxes** — no chevron, no placeholder, nothing saying they are pickers | `PolicyAnalytics.tsx` |
| 5.2 | Utilization renders KPIs as plain text; 0B §10b #5 specifies the `KpiCard` treatment (brand hairline, uppercase micro-label, 34 px tabular numerals) and the component exists | `PolicyBook.tsx` → `UtilizationScreen` |
| 5.3 | Empty states on Manage, Status and Eligibility are one grey sentence in a card, duplicating the prompt already above it. *"An empty screen is an invitation to act."* | `BeneficiaryPortal.tsx`, `ReceptionEligibility.tsx` |
| 5.4 | Zero-value bars: the track is a full-width light rail, so `Over the limit ▬▬▬▬ 0` reads as a full bar | `.pol-bar-track` |
| 5.5 | Dark mode uses the colour Mersal lockup on deep teal; 0B §8 calls for the **white mark on the teal tile** on dark surfaces | `Logo.tsx` |
| 5.6 | `ReceptionEligibility` uses `StatusChip` as the container for its error and no-results messages, where every other screen uses `InlineAlert` with `role="alert"` | `ReceptionEligibility.tsx` |
| 5.7 | Native `<input type="date">` shows `mm/dd/yyyy` in every locale and does not mirror in RTL | register, analytics filters |
| 5.8 | Arabic mixes numeral systems — Arabic-Indic in table dates, Latin digits in chart summary sentences | `PolicyAnalytics.tsx`, `useFormat.ts` |
| 5.9 | Field widths come from the grid, not the content: `Contribution (%)` (1–3 digits) gets the same 260 px box as `Middle name`; `Individual no.` gets 540 px | `.ben-grid` |
| 5.10 | The register error summary sits below the Documents section, ~1300 px under the first error. Focus *is* moved to the first invalid field, so this is a polish item, not a blocker | `BeneficiaryPortal.tsx` |

---

## 6. Checked and found sound

Recorded so the next audit does not re-litigate them:

* **Status chip shapes.** `.mrs-chip.bad` does render the square-ish badge (7 px radius) the four-cue system
  requires for negative states; Cancelled and Terminated are correctly distinguished from the Active pill.
* **Register focus management.** A refused submit moves focus to the first invalid control, whose error is
  tied via `aria-describedby`.
* **Bulk paired actions.** "Download the template" and "Expected columns" are matched buttons with leading
  icons, exactly as 0B §10c requires; the one-sentence guidance stays inline while only the table is hidden;
  the modal is parent-controllable so a validation failure can reopen it.
* **Analytics scroll behaviour.** `.app-main` scrolls to the bottom of the tallest screen and "Export this
  view" is reachable; an earlier suspicion of unreachable content did not hold up.
* **Labels, captions, live regions.** No unlabelled control, no captionless table, no missing `scope` on any
  audited screen.

---

## 7. Round 2 — layout & scrolling

Reported after round 1: *"still a lot of alignment and scrolling issues in almost all screens."* Four
screenshots, all showing some combination of a second scrollbar, a blank band under the app, content stopping
short of its card, and browser-blue links.

Every one of them traced back to **one root cause**, plus four independent alignment defects.

### 7.1 The phantom document scroll — root cause of "everything scrolls wrong" *(P0)*

`apps/design-system/src/styles/base.css` — `.sr-only` · `apps/web/src/styles/app.css` — `.app-grid`, `.app-main`

The shell is `height: 100dvh; overflow: hidden`, and each pane owns its own scroll. It should be impossible
for the window itself to scroll. It scrolled anyway — 212px on Registration Approvals, 584px on Analytics —
and when it did, the entire chrome (app bar, nav rail, content) slid upward as one block and exposed an
unpainted band of page background underneath. A second scrollbar appeared beside the pane's own.

The cause is `.sr-only`:

```css
.sr-only { position: absolute; width: 1px; height: 1px; clip: rect(0 0 0 0); }
```

No `inset`, and — this is the part that mattered — **no positioned ancestor**. `.app-grid` and `.app-main`
were both `position: static`, so the containing block for every one of these boxes was the *initial
containing block*. An absolutely positioned box laid out against the ICB is not clipped by
`overflow: hidden` on an intermediate element: it is placed at that offset **in the document**. So a
`<caption class="sr-only">` 1100px down a scrolled worklist made the document 1100px tall behind a 900px
shell, and the page grew a scroll range it was designed never to have.

Nothing inside the shell was broken, which is why it read as "the layout scrolls wrong" rather than as one
stray element — and why it appeared on *almost all screens*: every table caption, every `aria-live` announcer
and every visually-hidden row explanation is one of these boxes.

**Fix** (two halves, neither works alone, both commented to say so):

* `.app-grid` and `.app-main` are now `position: relative`, so they actually contain what they clip.
* `.sr-only` gains `clip-path: inset(50%)` beside the deprecated `clip`.

**Measured, every section of the portal:**

| | `documentElement.scrollHeight` | window scroll range |
|---|---|---|
| before (approvals / analytics) | 1112 / 1534 vs 900 client | 212px / 584px |
| after (all 11 sections) | 950 = client | **0** |

### 7.2 Unstyled anchors rendered in browser blue and visited-purple

`apps/design-system/src/styles/base.css`

There was no rule for a bare `<a>` anywhere in the system. Any anchor a screen did not style itself fell back
to `#0000EE` / `#551A8B`. The patient profile's section jump list is the clearest case: eleven navy links
with "Referrals" in purple because someone had clicked it once, beside a teal-and-ink page.

Added `a { color: var(--accent) }` with `:visited` deliberately matching `:link` — in an operational tool,
"have I opened this section before" is not information the interface should volunteer, and it is the
mechanism that produced the two-tone list. Verified: all eleven now `rgb(0, 122, 122)`.

### 7.3 The status chip stretched into a full-width banner

`apps/design-system/src/styles/components.css` — `.mrs-chip`

`display: inline-flex` computes to `flex` when the chip is a grid item, and `justify-items: stretch` (the
default) then blows it out to the whole column. In the member detail header a 90px `✓ Active` chip became a
1285px green band across the card — which reads as a banner announcing something, not as this member's
status. Fixed with logical `justify-self: start` / `align-self: start` on the chip itself, so it is right
everywhere rather than patched per screen.

### 7.4 Tables stopping a third of the way across their card

`apps/web/src/styles/app.css` + `MemberAdmin.tsx`, `PolicyBulk.tsx`, `BatchIntake.tsx`

Round 1 fixed this for `.pol-tablewrap`-wrapped tables only. The un-wrapped ones — member coverage,
cost-share, bulk row errors, batch intake — still carried `display: block` from the shared rule, which takes
a table *out of table layout*: the browser wraps the rows in an anonymous shrink-to-fit table, `width: 100%`
applies to the block box, and the columns size to their own content. The result is a full-width card whose
row rules stop at 1065px of 1560px — which reads as clipped, not as narrow.

Wrapped the seven in-scope tables in `.pol-tablewrap` (the wrapper owns the horizontal scroll, the table
stays a table). The bulk column contract is handled by a CSS rule instead, because `.bulk-columns` is already
the scroll container and a nested scrollport would have detached its sticky header from the box that scrolls.

`PolicyProductAdmin.tsx` is outside this audit's scope and is deliberately unchanged — the new rules are
scoped to wrapped tables, so its behaviour is exactly as before.

### 7.5 A chart and its own data table with two different value columns

`.pol-series .pol-costshare`

Each analytics card shows one series twice — once as bars, once as figures. The bars put their value hard
against the card's right edge; the table's value column started a third of the way across. The eye found two
"value" columns stacked on top of each other and had to work out they were the same numbers. The table's
numeric cells are now end-aligned, so both land on the same edge — which is also simply correct for numbers,
compared by scanning a column of digits.

### 7.6 Also fixed

* Member detail header status chip was rendering the raw server enum (`Active`) instead of the bilingual
  label — the roster column was translated in round 1, the detail header was missed.
* `Waiting period: ⓘ None` on the patient profile now shows an em dash, matching the rule applied to the
  membership roster in §2.12.

### Verification

* `tsc --noEmit` clean in both packages.
* 381/381 web tests, 36/36 design-system tests, axe clean over every route × locale × theme.
* Full-portal sweep at 1440px — for all 11 sections: window scroll range 0, no horizontal document overflow,
  nothing escaping `.app-main`, every card the same width, no table narrower than its card.

---

## 8 · Round 3 — Registration Approvals, rebuilt (full stack)

Round 3 is a feature request rather than an audit finding, and it is the first change in this document that
goes below the browser: the notification requirement is not implementable in a portal. It touches
`patient-service`, `notification-service`, `libs/contracts`, the design system and the screen.

### 8.1 What the all-dash column was

Asked directly, so recorded directly. The last column was the **decision** column. Its only ever content was
the supervisor's `Decide` button, so signed in as an officer every row in it read `—`. Round 2 had already
replaced a per-row repetition of the sentence "Decisions are made by a beneficiary-management supervisor"
with that dash plus a screen-reader-only explanation — correct as far as it went, but a column that is empty
for an entire role is a column that role reads as broken data, and it was occupying the widest part of the
table.

It is no longer empty for anyone: the actions cell now carries a **view** control for both roles, and the
decision button beside it for the supervisor. The role sentence is stated once, below the table, for the role
it applies to.

### 8.2 The queue controls, and why they are a design-system pattern

`apps/design-system/src/lib/useTableQuery.ts`, `components/Pagination.tsx`, `components/DataTableView.tsx`,
`lib/sortRows.ts`, `DataTable` row selection

The screen needed search, a status filter, sortable columns and pagination. Those were built as the house
pattern rather than as local code, for the reason `TableToolbar` already gives about filters: a standard that
lives in a document is one every screen implements slightly differently.

**The ordering is the load-bearing part.** `DataTable` sorts itself by default, and a self-sorting table sorts
*the rows it was handed*. With pagination on that means "oldest first" reorders the twenty-five rows on screen
and leaves the actual oldest application on page four — and it looks like it worked. `useTableQuery` therefore
owns the sort, applies it to the whole result, and drives the table in controlled mode. `sortRows.ts` exists so
the hook and the table share one comparator; two comparators would eventually disagree, and the way that
surfaces is a table whose order changes when paging is switched on.

Other decisions worth naming:

* Filter counts are **faceted** — each group counts against search plus every *other* group, so a count says
  "pick this instead and you get N". Counting the full set advertises options that lead to an empty table.
* Narrowing anything **resets to page 1**. Otherwise filtering a nine-page queue while sitting on page 4
  renders an empty table under a pager insisting there are matches.
* Empty-because-nothing-is-here and empty-because-you-filtered are **different screens**. Telling an operator
  "No registrations waiting for review" when they typed the search that excluded everything sends them
  looking for a bug.
* Select-all covers **the rows on screen**, not every row that exists — the dangerous reading of "all" is the
  invisible one. A selection made on page 1 survives paging, because paging must not discard work.
* Pagination states a **range and a total** ("26–50 of 210") rather than "page 2 of 9". A queue is managed
  against how much is left; page position is an artifact of the sort.

Applied to Registration Approvals in this pass. Every other portal table can adopt it by swapping `DataTable`
for `DataTableView` — that is a follow-up, not part of this change.

### 8.3 Registration date and filing officer

`patient.registration` carried `created_at` and **no actor**. Two consequences, and the second is the one that
mattered: "who registered this person?" was answerable only from the audit trail, which is evidence rather
than a queryable operational field — and a `RequestInfo` decision had no queue to land in.

Migration `0005_registration_thread.sql` adds `created_by` and `created_by_name`, stamped at all three
creation paths (register, bulk intake, re-review). The name is a **copy taken at write time**: resolving it
through identity-service on every worklist read would make the queue unable to render a column while that
service restarts, and someone who has since left must still be named on what they filed.

The worklist projection now returns both, plus `total` — the size of the queue, not of the page, so the pager
can say how much work is left. A row nobody is recorded as having filed renders **"Unknown"**, not blank:
that is exactly the state in which a request for information has nowhere to go.

### 8.4 Notes: a column that overwrote itself, now a thread

`registration.notes` is a single column set by the decision endpoint. So "UNHCR letter is expired" was gone
the instant anyone decided again, and the officer it was addressed to **had nowhere to answer**. A request for
information that cannot be replied to is a dead end dressed as a workflow.

`patient.registration_thread` is append-only by construction — the application role holds `SELECT` and
`INSERT` and nothing else, so an entry cannot be edited away after the fact. A supervisor's stated reason for
refusing an application is evidence, and evidence that can be quietly rewritten is not. `registration.notes`
keeps its meaning (the *current* outstanding note) so an older build is unaffected.

In the UI the note left the row entirely. It is prose an approver writes in sentences: capped at 260px and
wrapped, it made every row with a note twice the height of its neighbours and still truncated the ones that
mattered. What the row carries now is an **icon with the entry count** — the count being the part the icon
alone cannot say — opening the conversation in a modal with a reply box. Zero renders as a muted dash with a
named state for a screen reader, so "no notes" and "notes you have not opened" are never the same glyph.

`GET/POST /api/v1/registrations/{id}/thread`. A reply becomes the current note, so the queue shows the last
thing said rather than a question already answered. A `Rejected` or `Active` application is closed and refuses
replies (`urn:hbmp:registration-closed`) — the modal offers no reply box rather than one that 409s.

### 8.5 Request-info now reaches somebody

The chain, end to end:

1. `POST /registrations/{id}/decision` with `RequestInfo` enqueues `RegistrationInfoRequested` **in the same
   transaction as the decision** — a broker that is down delays the notice rather than losing it, and a
   decision that rolls back cannot leave a notice claiming it happened.
2. Destination `notification.registration-events`, a queue of its own. The transport is point-to-point, so
   consumers sharing a queue *compete* for its messages: putting this on `patient.events` would have notified
   roughly half the officers and silently dropped the rest.
3. `RegistrationEventConsumer` (notification-service) builds a single-recipient envelope and dispatches.

**The recipient rides on the event**, unlike every other route in `RoutingTable`, which fans out to a role the
consumer resolves against the directory. Which officer filed a given application is a fact only
patient-service holds, and it is the entire point of the notification. notification-service still contains no
directory logic — it is just told the answer. An application with no `created_by` has no addressee and sends
nothing; inventing a role-wide broadcast for it would train the team to ignore the channel.

The payload carries `{ref}` — the card or member number — and nothing else. The supervisor's prose stays on
the thread, behind authorization. `notifications/ingest` had **no caller anywhere in the repo** before this;
it does now, through the dispatcher rather than the HTTP seam.

Bilingual template seeded in `0003_registration_templates.sql`. The route is `Actionable` with **no escalation
rule**, deliberately: an escalation target must be a resolved recipient on the envelope, and the only
recipient here is the officer. A rule pointing at a role nobody resolved would be inert config that reads as a
working safety net.

### 8.6 The eye — the registration as it was filed

Almost none of this is a new read. **The worklist endpoint has always returned the elected coverage and the
six standing notes, already minimum-necessary projected, and the client threw them away** — likewise the
identity fields beyond name and one identifier. The contract now carries them and the modal renders them.

Only the document list is a second request (`GET /beneficiaries/{id}/documents`, document-service), made when
the modal opens rather than for every row in the queue. Documents are **metadata**: whether the paperwork is
present is the review question, and opening a scan is a separate, separately-audited disclosure that belongs
on the member's documents screen.

A withheld clinical slot renders as a **named locked state**, never as an empty one — beneficiary management
types slot 1 (known diagnosis) and does not read it back, and dropping the slot would read as "no diagnosis
recorded", which is the one wrong answer. Same rule for a field the caller's role was not disclosed: "Not
disclosed to your role" rather than a dash.

### 8.7 Bulk decisions

Selection is supervisor-only and excludes rows there is no decision to take on (no application; already
rejected) — disabled rather than absent, because a checkbox that ticks and then does nothing is worse than one
that will not.

`decideRegistrations` is deliberately a **loop of single decisions, not a bulk endpoint**. Each row keeps its
own audit event, its own idempotency key and its own server-side guard check, so an Approve the server refuses
fails that row and only that row. It never rejects: per-row outcomes come back in `ok`/`error`, because a
thrown error would discard the results of the rows that already succeeded. A partial result is the normal case
and has to be actionable — "1 recorded, 1 refused: no policy/coverage is bound" tells the supervisor what to
do; "bulk decision failed" does not. Once any row has landed the modal becomes a report, not a form, so
confirming again cannot replay decisions that already happened.

### 8.8 Two smaller things

* The **introductory paragraph is gone**. It said the queue was oldest-first, that approval needs both checks,
  and that the decision is a supervisor's. All three are now stated by something the operator can act on — the
  sortable date column, the two checkboxes, and the blocked-approve reason inside the modal. Prose that
  restates the interface is prose that gets skipped, and it pushed the first row below the fold.
* The decision buttons carry icons, and **`Request information` says what it does**: "The officer who
  registered this person is notified and can reply here." It is the one decision whose effect happens
  somewhere the supervisor cannot see, and a supervisor who does not know it reaches anybody writes their note
  as if into a void.

### 8.9 The actions column is pinned

Found by screenshotting the supervisor view at 1440px, not by reasoning about it. With the selection column
and the decision button the table is 1258px inside an 1101px card — a 157px overflow, which `.mrs-wl-scroll`
handles, except that **the column falling past the fold was the last one, and that is where the buttons are**.
The supervisor had to scroll sideways on every row to reach the control they came for.

`Column.stickyEnd` (design system) pins a column to the trailing edge while the rest scroll under it. The
columns that can be read at a glance are the ones that move. Two details that matter: the pinned cell needs an
opaque background matching its row, or the scrolled cells show through and two sets of text stack; and the
pinned *header* needs `z-index: 6`, because `.mrs-wl th` already sets 5 and a pinned header below that passes
under the header cells scrolling beneath it.

### 8.10 Known limit

The screen loads the **oldest 100** pending registrations in one request (the server clamps `pageSize` to 100)
and searches, filters, sorts and pages them in the browser — instant, and incapable of disagreeing with what
is on screen. When the queue is larger it says so, in a banner, with the real total. That is honest and it
matches how a queue is worked; it is not server-side search. Moving search and filtering into
`GET /registrations` is the follow-up if the pending queue routinely exceeds a hundred.

### Verification

* `tsc --noEmit` clean in `libs/contracts`, `apps/web` and `apps/design-system`.
* **398/398** web tests (was 381 — 26 now cover this screen), **50/50** design-system tests (was 36), axe clean
  over every route × locale × theme.
* **102/102** patient-service tests with `--with-db` against the Compose Postgres, **0 skipped** — the eight
  new registration-thread tests exercise the real schema, the real outbox and the real decision endpoint.
* **41/41** notification-service tests with `--with-db`, 0 skipped.
* Migration `0005` applied to the Tier 1 database and verified (`patient.registration_thread` with its RLS
  policy and `SELECT, INSERT`-only grant; `registration.created_by` / `created_by_name`).
* **The whole chain exercised against the running stack**, not only in tests. Registered a beneficiary as
  `beneficiary_mgmt`; the worklist returned `createdBy` = that officer's subject and `createdByName`
  "Beneficiary Mgmt". Decided `RequestInfo` as `beneficiary_mgmt_supervisor`; the thread came back with the
  supervisor's Decision entry, the officer's Reply landed on it, and `notification.notification` held two rows
  — InApp `Delivered` and Email `Sent` — addressed to `e77f18c6-…`, the officer who filed it, subject
  "مطلوب معلومات إضافية للتسجيل E2E-…". Screenshots of both roles taken at 1440px: window scroll 0.

### A gateway note, unrelated to this change

Recreating a service container gives it a new IP, and **Kong caches the old one**. After the patient-service
rebuild every `/api/v1/*` call through the gateway returned a 404 that came from whichever container had taken
the old address — while the same request direct to `patient-service:8080` returned 200. `docker compose
restart kong` after recreating a service, or the symptom is a service that looks broken and is not.

---

## 9 · Round 4 — navigation, access, and the member detail

### 9.1 The nav, reordered

Membership first, and therefore **Beneficiaries is the landing page**. The landing page is `accessible[0]`
(`AppShell`), so section order *is* the landing decision — there is no second setting, deliberately: a default
configured apart from the menu is one that drifts from it. Order is now Membership → Registration → Patient
Access → Insights, and the rail groups consecutive runs, so that order is also the group order.

**Members → Beneficiaries**, in the nav *and* on the screen's own `<h1>`. The list said "Members" while the
item that opened it said "Beneficiaries" — one name too many for one list.

Three sections were removed rather than moved, each because it duplicated something better:

| Removed | Why | Where it went |
|---|---|---|
| Search / Manage | A second, weaker search over the registry the Beneficiaries list already searches | Nowhere — the list is the search |
| Status & Reactivation | A screen whose whole job was to find a person you had just been looking at, then press one button | `Status change`, in the member detail beside Change plan |
| Utilization | A cohort figure in a menu of its own | A tab in Analytics (`Utilization by scope`) |

**The routes were unmounted too, not just the sections.** A path with no catalog section falls through to
`AppRouter`'s deep-link branch, which resolves it from the screen registry and gates it on `profile.read`
alone — so leaving them mounted would have kept three withdrawn screens reachable by typing their URL.
`beneficiary-nav.test.tsx` asserts that absence, because it is the half that rots quietly.

### 9.2 The supervisor is a superset now, and SoD moved to where it binds

The supervisor's portal was a strict *subset* of the officer's: no register pen, no bulk import, no analytics.
The reasoning was separation of duties. The implementation was withholding menu items, and that is the wrong
lever twice over.

**It did not enforce the rule.** The server's check was `is the caller a supervisor` — never *did the caller
file this application*. `POST /beneficiaries` needs `patient:write`, which a supervisor holds, so a supervisor
could always register a person and approve them; the permission was absent from the nav, not from the API.

**And it made the supervisor less capable than the people they supervise**, which just means borrowing an
officer's screen — a worse audit trail than giving them their own.

So the rule moved to patient-service: a decision on a registration whose `created_by` is the actor is refused
with `urn:hbmp:self-approval`, and the denial is audited. This is **strictly stronger** than what it replaced —
it catches self-approval whoever performs it, regardless of what any menu shows — and it is only checkable at
all because `created_by` was added in §8.3. The worklist disables `Decide` on rows the approver filed, with the
reason as the button's accessible name, so the refusal is visible before it is a 403.

### 9.3 Every action in the member detail opens a modal

It was a mixed row: four buttons that revealed an inline panel further down the page, and one anchor that
navigated away. Two different things happening to one row of look-alike controls is a row nobody can predict.

`MembershipDialog` was `<Card role="dialog" aria-modal="true">` — an assertion the markup could not keep.
Nothing trapped focus, Escape did nothing, the page behind stayed in the tab order, and a screen reader was
told the rest of the page was inert while it was fully reachable. It also scrolled away: an officer who had
scrolled to the coverage grid pressed Terminate and the form opened above their viewport. It is a real
`Modal` now — focus trap, Escape, scrim, restored focus, labelled close.

**Open full profile** opened in a modal instead of navigating. Looking someone up, opening their file and
coming back lost the search, the selected member and the tab you were on; a member lookup is a glance and
should not cost the page you were on. `/patients/{id}` still exists for deep links from notifications.

**Status change** is the retired screen, as an action on the record it acts on — beside Change plan, because
both change what this person is entitled to, one at the membership level and one at the beneficiary level. It
is always enabled and the dialog explains itself: "a director unlocks a blocked record" and "your role was not
told this person's status" are different dead ends, and only the dialog can say which. (Briefly it was
disabled instead, which produced a permanently grey button wherever the status was not disclosed —
indistinguishable from a broken control.)

### 9.4 The padding, twice

`.mrs-card` carries no padding by design — a card is a surface and each screen sets its own — which makes a
forgotten one invisible in review and obvious on screen.

The rule was `.pol-screen > .mrs-card`: **direct children only**. Every analytics card is inside a tab panel,
two levels down (`.pol-screen > div > .mrs-tabpane > .pol-view > .mrs-card`), so the filter bar was padded and
the six view panels below it were not — same card, same screen, two different insets depending on whether a
`Tabs` sat in between. The member detail had the identical fault for the identical reason: its identity card
rendered the member number hard against the border.

One rule now covers direct children and tab-panel descendants on both screens, with a guard that a card nested
inside a card is not padded twice (there is no such nesting today — checked across every analytics tab and the
member detail's five).

### 9.5 The Analytics subtitle is gone

"Aggregates over the policy and membership book. No clinical data appears in any view." — true, and needed by
nobody: the first half restates the page title, and the second is a guarantee about the server's projection
that no operator can act on and that reads, to the one person who might worry about it, as a claim rather than
a control. It cost a full row above the filters on every visit; the space goes to the filters and the first
view, which now clears the fold.

### Verification

* `tsc --noEmit` clean in `libs/contracts`, `apps/web`, `apps/design-system`.
* **409/409** web tests (12 new, covering the nav order, the landing page, the supervisor superset, the
  unmounted routes, and the status dialog in its new home), **50/50** design-system, axe clean.
* **104/104** patient-service with `--with-db`, 0 skipped — including a self-approval refusal that leaves the
  application untouched, and a *different* approver succeeding on the same registration (asserting only the
  refusal would pass on a service that refused everybody).
* Live on :5173, both accounts: land on `/beneficiaries/members`, `<h1>` "Beneficiaries", identical nav
  (Membership → Registration → Patient Access → Insights → Inbox). Member detail: identity card padded,
  six actions, profile opens a dialog with the URL unchanged, Change plan traps focus and closes on Escape.
  Analytics: every card padded across every tab, no subtitle.

### Still open (unchanged from §4)

`NAME` is empty on most member rows because policy-service's per-page summary lookup to patient-service
returns nothing for them. `beneficiaryStatus` rides on that same lookup, so `Status change` will show its
"not disclosed" branch on those rows until it is fixed. Pre-existing, and out of this round's scope.

---

## 10 · Round 5 — the Beneficiaries roster: fields, search, editing, logging

### 10.1 The advanced search already existed on the server

`GET /member-query` accepts `identifierType`, `identifierValue`, `name`, `memberNo`, `policyId`,
`policyPlanId`, `groupId`, `relationship`, `status`, `branchId`, `enrolledOn`, `enrolledFromAfter`,
`enrolledToBefore`, `waitingPeriod`, `utilizationBand`, `page`, `pageSize` and `sort`. **The screen was
sending `name` and `pageSize: 50`** — one field of a query surface that was fully built and tested. So this
round was mostly wiring.

Ten filters now, applied together, in a panel that edits a **draft** and submits: ten live filters over a book
this size is ten requests for one intention, arriving out of order. The quick box remains the `name` field and
*replaces* the criteria rather than intersecting with them — an operator typing a name expects that to be the
search, not to be silently combined with a filter they set earlier and cannot see.

### 10.2 Pagination and sorting are the server's here — unlike the approval queue

Round 3 put search, sort and paging in the browser for Registration Approvals, and that was right: an approval
queue is the hundred applications at the front of it. This is the whole membership book — tens of thousands of
rows — so all three are the server's. `Pagination` and `DataTable`'s controlled sort compose for exactly this;
that they were built as separate pieces rather than baked into `DataTableView` is what made it possible.

Only the six columns in `MemberSortFields.Allowed` are marked sortable. A header offering an order the server
rejects answers with a 400.

**The pager is always shown here**, deliberately unlike the approvals queue which hides it on a single page. A
queue is work to get through and a dead control is noise on top of it; the book's SIZE is the answer to a
question operators ask, and the page-size picker is unreachable if it only appears once the result is already
large.

### 10.3 The registration fields, in the right two places

The roster gains **the card number** and nothing else. patient-service's per-page summary endpoint is narrow on
purpose — "a list is the highest-volume disclosure the platform makes" — and the card number earns its place
by the same argument the read guard already makes for it: it is printed on the card the beneficiary hands
over, and a roster showing a name but not the card leaves the desk unable to match the two.

Everything else — date of birth (and whether it is approximate), sex, nationality, identity document, phone,
individual no, case no — is in a new **Details** tab, read one person at a time through
`GET /beneficiaries/{id}`, which projects by role and audits the read. A date of birth in a fifty-row table is
fifty disclosures nobody asked for.

Undisclosed and empty render differently throughout: "Not disclosed to your role" is not "—".

### 10.4 Nothing could correct a registration until now

The only writes on a beneficiary were register (once), status (its own transition table) and the bulk by-card
upsert. An officer who mistyped a birth date had to ask for a re-import of a file they may not have had.

`PATCH /api/v1/beneficiaries/{id}` — partial by construction, so a form showing five fields cannot blank the
four it did not. `patient:write`, which **both** roles hold. Card number, identity document and status stay
out: the first is uniquely indexed among live rows (moving a card between people is a benefit leak), the
second carries the registrar's duplicate check, the third has a legal-transition table. All three are still
SHOWN, with one sentence saying where they change, because an absent field reads as data the system lacks.

`BeneficiaryEditRules` is pure and unit-tested: a future birth date is refused, an unchanged value is **not a
change** (or the log fills with entries recording that somebody opened a form and pressed save), values are
trimmed, an optional field can be emptied and a mandatory one cannot.

### 10.5 Every change is logged, and the log now reaches the Logs tab

A correction writes an audit event with the **field-level** before/after — only the fields that moved, because
a whole-record diff buries one corrected letter in twenty unchanged ones — and publishes
`BeneficiaryDetailsCorrected`. policy-service consumes it and projects an entry onto **every** live membership
the beneficiary holds, since a correction to their name is true of all of them and the Logs tab is opened FROM
a membership.

The event carries **field names, no values**. The history is read by roles whose projection of the identity
record is narrower than the officer's who made the edit; "the date of birth was corrected" is the part
everyone may see, and the values are in the audit trail behind `audit:read`.

Tab renamed **Timeline → Logs**: an operator looking for "who edited this" searches for a log, and "timeline"
reads as a clinical narrative, which is a different thing this product also has.

### 10.6 The audit trail was not recording. At all. Platform-wide.

Verifying 10.5 turned up `audit.audit_event` holding **0 rows** — across every service, after days of running.

`OutboxBase` serializes payloads with `JsonSerializerDefaults.Web` (camelCase). `RabbitMqAuditConsumer`
deserialized with the DEFAULT options: PascalCase and case-**sensitive**. `auditEventId` never bound to
`AuditEventId`, and because `AuditEvent` declares those properties `required`, every message failed with
"missing required properties" and was nacked to the dead-letter queue.

The failure was invisible in the way that matters most: every service emitted correctly, every relay
published, the queue drained, and nothing anywhere reported a problem — from each publisher's point of view
the write succeeded. `19-audit-strategy` makes the trail immutable and hash-chained; none of that helps when
the chain has no links. Every PHI read, every decision, every break-glass and every login since this
environment was built is gone.

One line — the consumer now uses the same serializer the publisher does, shared rather than merely matching,
because two sides of one wire format should be one decision.

### Verification

* `tsc --noEmit` clean across `libs/contracts`, `apps/web`, `apps/design-system`.
* **409/409** web · **50/50** design-system · **118/118** patient-service (`--with-db`, 0 skipped, 12 new for
  the edit rules) · **462/462** policy-service (`--with-db`, 0 skipped) · **3/3** audit-service.
* Live, end to end: filtered the roster to `MEM-2026-000001`, opened Details (all 12 fields), edited the case
  number, and confirmed **three** separate landings — the value persisted in patient-service, an audit row
  `Update / corrected` with `{"caseNo":"CASE-EDITED-0523"} → {"caseNo":"CASE-EDITED-4445"}` and field classes
  `{identity,pii}`, and a `BeneficiaryDetailsCorrected` entry on the member's Logs attributed to
  "Beneficiary Mgmt". The audit store went from 0 rows to recording logins, PHI reads and disclosures again.
* Pagination against the server: 25 of 25 → page size 10 → "Showing 1–10 of 25" → next → "Showing 11–20 of 25".

### 10.7 The Logs tab was showing seed data only

Reported from use: "I changed the plan for one member but the logs don't capture this change."

Every row in `policy.entity_timeline` had been written at one instant — `2026-07-27 11:17:04.776516` — by the
demo seed. The only entries produced by real activity were the `BeneficiaryDetailsCorrected` ones added in
§10.5, which reached the table because they travel through an event and a consumer that was built for them.

`MembershipCommands` publishes `MemberEnrolled`, `MemberTerminated`, `MemberReinstated`, `MemberPlanChanged`
and `MemberEnrolmentCancelled` to `policy.events`, and **nothing consumed them to project the timeline**.
`MemberGroupChanged` was worse: no event was published at all, so it was doubly invisible. `TimelineProjector`
was registered in DI and called only from tests.

Each command now projects its own entry, immediately after emitting its audit event and **inside the same
transaction as the change**. `TimelineProjector`'s note says the timeline should be projected from events
"that already exist" so it cannot drift from the audit trail — that intent is met (same values, same moment,
same transaction) and the guarantee is stronger than a consumer's: the change and its history entry commit
together or neither does. A consumer on `policy.events` was the alternative and buys eventual consistency for
a line the operator expects the moment the dialog closes.

Event ids are derived from `(enrollmentId, eventType, instant)` rather than random, so a retry projects the
same row — `ProjectAsync` dedupes on the source event id, and a random one would make every retry a duplicate
line in somebody's history.

Verified live: a group change from the member detail appears in Logs as "Member moved to another group",
attributed to the operator, seconds after the dialog closed.

### 10.8 Roster page size

Five, not twenty-five. The roster is a LOOKUP — an operator searches for a person, opens them, and works in a
tall six-tab detail below — so a long table pushes the thing they came for off the screen. Five rows separates
"I found them" from "I need to narrow this"; the size picker (5/10/25/50/100) covers reading the book. The
default has to appear in that list: a `<select>` whose value matches no option renders the first one instead,
so the picker would have shown 10 while the table served 5.

---

## 11 · Round 6 — the sweep, and the notification fan-out

Asked for after three separate instances of the same shape turned up in one session: **something fully built,
fully tested, and connected to nothing.**

### 11.1 What the sweep found

| # | Finding | Status |
|---|---|---|
| 1 | **Audit sink dropped 100% of events.** `OutboxBase` writes camelCase; `RabbitMqAuditConsumer` deserialized PascalCase, case-sensitive. `audit.audit_event` held 0 rows platform-wide. | Fixed §10.6 |
| 2 | **Member timeline held only seed data.** policy-service published its membership events and nothing projected them; `TimelineProjector` was called only by tests. | Fixed §10.7 |
| 3 | **Notification fan-out had no subscriber.** 13 routes, 13 bilingual template pairs, an escalation model, a dispatcher — and one delivery path, the one added in §8.5. | Fixed below |
| 4 | **reporting-service `POST /projections`** — same shape: an `EventProjector`, a seam endpoint gated on `reporting:project`, and no caller anywhere. `reporting.fact_cost` holds 0 rows. | **Open** |
| 5 | **`emr.practitioner-branch-revoked`** — a consumer on a queue **nothing publishes to**. The mirror image; practitioner branch revocation never propagates. | **Open** |
| 6 | **`EscalationService`** — registered in DI, constructed only by tests. The dispatcher sets `EscalationDueAt` on actionable notifications and nothing ever sweeps them. | **Open** |
| 7 | Event streams with no subscriber: `provider.events` (17 types), `emr.events` (11), `claims.events` (10), `case.events` (6), `callcentre.events` (3), `finance.events`, `document.events`. | Not necessarily defects — listed |

A methodology note: the first pass used a grep for string-literal event names and **missed** the auth decisions,
which are published through a `switch` returning the type (`Decisions.EventType`). That would have produced a
confidently wrong report — "no service publishes AuthApproved" — so the extraction was redone against
non-literal call sites before anything was claimed.

### 11.2 The fan-out, wired

`DomainEventConsumer` subscribes to `notification.domain-events` and dispatches. It replaces the
registration-only consumer, which was the service's single delivery path.

**Why publishers send a second copy rather than the consumer joining the domain stream.** The transport is
point-to-point: a consumer on `pharmacy.events` would COMPETE with policy-service for those messages and each
event would reach one of them, never both. So a service that wants a notification enqueues a
notification-shaped copy to notification-service's own queue — the decision `policy.registration-enrolments`
already made.

**Recipients ride on the envelope.** `RoutingTable` targets roles; resolving a role to people is directory
business this service is deliberately free of. The publisher knows who is actually waiting — approvals knows
who submitted the authorization, patient knows who filed the registration — so it names them. That is also why
a decision reaches the clinician who requested it instead of every clinician holding the role.

Adding a notification is now a publisher change plus a template row: no new consumer, no new queue. The legacy
registration envelope is still parsed, because messages published by the previous build were on the queue and
dropping them would lose the notices they exist to deliver.

**A silent bug caught by verifying rather than reasoning:** the first field bag named the reference `authNo`
while every auth template interpolates `{ref}`. A missing token renders EMPTY by design, so the notice went
out reading "Authorization  was approved" and nothing failed anywhere. Now named correctly, and pinned by a
test that asserts the mis-named case produces exactly that empty rendering — the contract is that the template
owns the name.

### 11.3 What is still not delivered, precisely

Wiring the bus lights up **6 of 13 routes**, not 13:

* **Delivered now:** `AuthApproved`, `AuthPartiallyApproved`, `AuthRejected`, `AuthInfoRequested`,
  `AuthEmergencyApproved` (approvals-service), `RegistrationInfoRequested` (patient-service).
* **Routed and templated, published by NOBODY under that name:** `AuthSlaBreached`, `OrderLineAvailable`,
  `ResultReady`, `RxReady`, `AppointmentReminder`. The nearest publishers use `OrderResultUploaded`,
  `RxDispensed` / `RxApproved`, `AppointmentReminderIssued`, and `AppointmentNoShow` is published as
  `ApptNoShow`. **This is a naming reconciliation, not a wiring one** — a vocabulary written on one side and
  never adopted on the other.
* **`RxLineOutOfStock`** matches by name and is published to `pharmacy.events`; it needs the same second-copy
  line the auth decisions now have.

**One real limit on the auth notifications.** `POST /authorizations` requires `auth:ingest`, a machine-only
scope, so `created_by` is the routing saga rather than the ordering clinician — the notice then has no human
addressee and is correctly not sent. Making this work for the ordinary flow needs the ingesting service
(orders/pharmacy) to pass the ordering clinician's user id onto the authorization, exactly as
`registration.created_by` now carries the filing officer. That is the next change, and it is small.

### Verification

* **44/44** notification-service (`--with-db`, 0 skipped) · **65/65** approvals · **118/118** patient
  (`--with-db`) · **462/462** policy (`--with-db`) · **409/409** web · **50/50** design-system.
* Live, end to end: submitted an authorization recording a clinician as its submitter, assigned it, approved
  it as `medical_approval` — and `notification.notification` gained two rows addressed to **that clinician**,
  role `requesting_provider`, InApp `Delivered` and Email `Sent`, subject
  "تمت الموافقة على التفويض AUTH-2026-0777", deep-linked to `authorization:11087ee8-…`.
* The registration notice still delivers through the new generic consumer, so the replacement lost nothing.

---

## 12. Round 7 — the member card, the covered family, and a log that answers "who"

### 12.1 The covered family (US-063)

`CoveredFamilyMemberView` has been a section of the administrative 360 since 19.5, and **nothing in the
product ever called it** — the same shape as §11's sweep. Two things were wrong with it besides:

1. **It missed siblings.** The traversal walked one hop out from the enrolments the caller already held.
   From a principal that is right; from a **child** it reaches the father and stops, so a dependant's record
   listed one parent and none of their brothers or sisters. `Household.RootOf` roots the walk on the
   principal, which makes it symmetric from any member of the family. Proved by `HouseholdStoreTests`
   against real Postgres, from the child and from the principal, asserting the two answers are equal.
2. **It carried no names** — enrolment ids and member numbers only. A family list nobody can read.

`GET /api/v1/enrollments/{id}/family` is its own endpoint because the question gets asked on its own:
composing notes, documents and two hundred history rows to answer "are the children on this cover" is a lot
of disclosure for one list. Names come from patient-service through **the caller's own token**, so the
projection and the PHI-read audit stay with the owner. Payer scope is applied per row and the dropped rows
are **counted**, because a family of five rendering as three with nothing to say why is a wrong answer.

Live: asked from a child, the answer is the principal, the spouse, the sibling and the child themselves
(marked `isSubject`), principal first — and `audit.audit_event` gained `covered-family / members:4`.

### 12.2 The member card

Three bands in the order the questions get asked — **who is this** (photo, name, status, member no.,
relationship, and an icon strip of age / sex / nationality / phone) · **what do they hold** (plan, cover
dates) · **what can I do**. It was one flat row of four facts above six same-weight buttons.

* **The photo now loads.** `GET /patients/{id}/photo` requires a bearer and a bare `<img src>` sends none, so
  every avatar in the app silently fell back to initials — the same defect as the bulk-template download
  (audit R3). `MemberAvatar` fetches with the token and hands the browser a blob, revoked on unmount so a
  lookup session does not accumulate a copy of every face the operator looked at.
* **The general-information strip renders nothing rather than dashes** when the role received none of those
  fields. `undefined` (withheld) and `null` (not held) are different facts, and a dash for both tells an
  officer the system has no phone number for somebody whose number they are merely not entitled to see.
* **The identity record is read once**, by the parent, and shared with the Details tab. Two components
  fetching it independently wrote two disclosure entries every time somebody opened a member.
* **Terminate moved to the end**, behind a separator. It used to sit second — one button from the primary,
  so the two most likely mis-clicks on the card were "open the profile" and "end this person's cover".

### 12.3 The log now says who, and what moved

The Logs tab rendered `Plan · 31 Jul 2026, 18:18 — Member moved to another plan`. Two independent faults:

* **The actor was dropped.** The guard was `e.actorDisplay &&` while the value rendered was
  `actorUsername ?? actorDisplay` — and policy-service never set a display name, so the condition was false
  on every entry it wrote. `ActorRef` now carries the token's display name; the panel prefers it and falls
  back to the subject.
* **The diff was never rendered at all.** `changeDiff` has been on the wire since 19.3c, minimized to the
  fields that moved and projected by role at read time; the panel showed `diffWithheld` and threw the diff
  away. It now reads `Plan: Standard → Enhanced`.
* The bags were rewritten to record **labels, not identifiers** — `policyPlanId: 4f2c… → 91ab…` is the same
  fact written so that reading it costs two more lookups. The ids stay on `policy.enrollment_event`, which is
  what 19.5b's as-of extraction reconstructs history from. **The termination reason is deliberately absent**:
  it can say "deceased", `MayReadCase` withholds it from roles that read this history, and a diff carries one
  visibility class — putting it there would route it around the projection that exists to hold it back.

Live: a plan change through the API produced `actor_display = "Policy Admin"` and
`{"plan": {"before": "Enhanced", "after": "Standard"}, "effectiveDate": {...}}`.

### 12.4 Controls that looked unfinished

Note type, Visibility, Move to plan and Change group were **bare `<select>` elements**. A native select is
drawn by the OS: it sat shorter than the fields around it, kept square corners against the app's radius and
opened a system-blue list. `SelectField` (design system) is the labelled form of the existing `Select`; the
trigger is a `<button>`, so the label carries an id and the combobox points at it with `aria-labelledby`
rather than an inert `for`. `FilterSelect` in the roster delegates to it — its old comment said the design
system's Select was "wrong here" because the filter grid needs visible labels, which is exactly what the new
wrapper provides.

Notes composition moved into a modal behind **+ Add note**: the form used to sit permanently above the list,
so opening the tab showed an empty form and pushed the notes below the fold. Reading is the common case;
writing is occasional. The note author was rendering `authoredByUsername` — the subject uuid — so every note
on a record was signed `e77f18c6-819c-…`; it now prefers the display name, same fix as the timeline actor.

### 12.5 What is NOT verified

The browser-level check could not run: the sandbox lost `libnspr4`/`libnss3`, so both Playwright builds fail
to launch (`error while loading shared libraries`). Everything above was verified through the API with a real
PKCE token and through the database, plus **423 web / 50 design-system / 474 policy** tests. The visual
claims — that the seven actions fit one line at 1440px, and the strip's spacing — are argued from the CSS,
not seen. `sudo apt install libnss3 libnspr4 libasound2` restores the screenshot path.

### 12.6 Known demo-data artifacts (not defects)

* **No family links existed** in the dev database (`principal_enrollment_id` null on all 25 enrolments), so
  the family modal would have said "nobody else is enrolled" for everyone. Three enrolments under
  `MEM-2026-000002` were linked as Spouse/Child/Child to make the feature demonstrable.
* **20 of 25 enrolments reference beneficiary ids patient-service does not hold** (orphaned bulk seed, §10).
  Those rows show "Name unavailable" and no information strip — correctly: there is no person record to read.
  The five `MEM-2026-…` rows show the full strip.
* The four linked family members are all genuinely named "Amina Yusuf" in `patient.beneficiary`. The endpoint
  is reporting the seed faithfully.

---

## 13. Round 8 — the Documents tab, and where a log begins

### 13.1 Documents: the list is the tab, filing is a dialog

The Documents tab opened on **an empty upload form** — a type combobox, a title, a date, a file picker and an
Upload button — with the filed paperwork underneath it and, on a member with nothing on file, a one-line
footnote saying so. Four controls to compose the occasional act, above the thing everybody came to read.

`BeneficiaryDocuments` now matches the notes panel next door: a heading with a primary **+ Add document**
button, the list below it, and the form in a modal. Same gesture on both tabs — "add a thing to this record"
should not be a different interaction per tab.

* **The type rule moved onto the form.** "Each document needs a type — the type decides who may read it" was
  heading the whole tab, explaining a form to people who had come to read a list. It is the modal's
  description now, where the choice is actually made.
* **The list sorts newest-first in the client**, not in whatever order the wire supplied — the same rule the
  notes list follows, so two panels on one record cannot disagree about which scan is current.
* **The type combobox became `SelectField`** (§12.4), so the last bare `<select>` on the member screens is
  gone, and the required-field markers now come from the field contract instead of a `*` glued into the label
  text.
* The row keeps what it already did well — name, type chip, withdrawn/expired/verified state, upload date,
  uploader, version — plus a decorative document glyph so nine rows read as a stack of documents at a glance.
  **Locked rows still say "Locked"** rather than rendering an empty cell where the buttons would be, and view
  and download remain two different disclosures.

### 13.2 The first line of the log is now the record's creation

The timeline is newest-first and cursor-paged, which put the one line every reader wants — **when this
membership began, and who began it** — at the far end of however many "load older" pages the record had
earned. And it was frequently not there at all: `MemberEnrolled` is projected only by the enrolment command,
so memberships written by bulk intake, by a migration, or before 19.3c have no such entry. **Every enrolment
in the dev database is in that state.** `MEM-2026-000001`'s history began, with no explanation, at
"Beneficiary details corrected".

`GET /enrollments/{id}/timeline` now returns an `origin` beside the page:

* **Projected first.** The earliest `MemberEnrolled` entry, through the same role projection every other
  entry goes through — actor and diff as they were snapshotted at write time.
* **Derived second, and labelled.** Failing that, the anchor is read off the record itself: the append-only
  `enrollment_event` row for the enrolment (when it was *decided*, which on a back-dated or imported
  membership is not when the row was written), else the membership's own `CreatedAt`. **No actor is
  invented** — the derived row carries none, so nothing signs it. `derived: true` is on the wire and the
  panel says "Read from the membership record — no enrolment event was projected for it."
* **Returned on the first page only**, and **removed from `entries`** when it falls inside that page, so a
  short history does not render the enrolment twice.
* An id the service does not know gets **no origin**, not one stamped `now` — a log that anchors an unknown
  record on the current clock answers a question nobody asked with a value nobody can check.

The panel renders it as the list's first item with an accent rail and a "Where this record begins" label, so
its position above a reverse-chronological run reads as an anchor rather than as a break in the order.

Live, through Kong with a real PKCE token: `MEM-2026-000001` returns
`origin { eventType: MemberEnrolled, occurredAt: 2026-07-29T06:00:25Z, derived: true }` above its three
correction entries, and `MEM-2026-000002` — which had an entirely empty log — now opens on its enrolment.

### 13.3 Tests

* `TimelineOriginTests` (5, DB-gated): the projected entry wins; the **earliest** wins when a re-enrolled
  membership has two; the enrolment event anchors a record with no projection; `CreatedAt` anchors one with
  neither; an unknown membership has no origin.
* `PolicyEndpointTests.A_members_log_is_anchored_on_the_enrolment_and_never_repeats_it` — over HTTP, end to
  end: enrol, read the timeline, assert the origin is the projected enrolment and that its id is **not** also
  in `entries`.
* Web: three timeline tests (origin renders first, the derived label, no origin renders nothing extra) and
  two documents tests (the tab opens on the list with the form behind the +, newest-first ordering).

**480 policy (`--with-db`) · 428 web · 50 design-system.** The `libnss3`/`libnspr4` gap in §12.5 is unchanged:
the visual claims here — the modal's single-column form, the accent rail on the origin row — are argued from
the CSS, not seen.

### 13.4 Round 8b — order, staleness, and the two icons

Three follow-ups from the same session, all on surfaces §13 had just touched.

**The log now reads newest-first, and the creation anchors the bottom.** §13.2 put the origin at the top,
which is where "the first log" lands if you read it as position rather than as chronology. It is the OLDEST
line there is, so the run reads newest→oldest and the anchor sits at the end, with the "load older entries"
control **between** them — a pager below the record's creation would read as "load something from before this
record existed". The run is also sorted client-side by `occurredAt` descending and the anchor filtered out of
it by id: the service drops the anchor from the first page only, so paging far enough back would otherwise
have fetched it again and rendered the enrolment twice.

**The Logs tab no longer needs a page reload to show your own change.** `ChangeTimeline` loads once per
mounted record — and the tabs stay mounted while the card's actions are used above them, so a plan change, a
termination, a status change or a correction left the history showing the state it was in when the tab was
opened. The member screen now bumps a `changeSeq` on every write and passes it as `reloadToken`; the panel
re-reads when it changes. A **Refresh** control sits in the panel head as well, because a history is written
by other people at other desks and a mounted panel cannot know about those at all. (Entries that arrive
through the beneficiary event consumer are eventually consistent — a correction may take a moment to appear,
and the refresh is how you ask again.)

**View and download, as two icons, on the policy documents panel too.** `DocumentsPanel` (the POLICY scope)
offered a single "Download" button, so reading a contract in place meant taking a copy of it and the audit
trail recorded the heavier of the two disclosures for both acts. It now carries the same eye/download pair as
the member panel, over the same `DocumentPreview` component and the same `purpose` parameter — one component,
so a policy contract and a member's card scan cannot render differently.

**Why the member tab looked like it had no icons.** It has had both since §13.1 — the row in the dev database
is `canDownload: false`. It was filed as *Investigations* → `LabResult` → **Clinical**, and `beneficiary_mgmt`
is an administrative role: an officer may FILE clinical paperwork received at the desk and may not open it
back. That is the designed rule, and the row was saying so in a `title` attribute — invisible without a hover,
absent on touch. The sentence is now **on the row**: "This document's type carries a clinical floor — your
role may see that it exists, and may not open it."

**435 web · 480 policy · 50 design-system.**

---

## 14. Round 9 — the open list, worked

Asked for as "read this report again for still not fixed issues". The re-read found three items that had
already been fixed by deletion and never struck, corrected two findings this document had recorded wrongly,
and closed most of the rest. What is left is named at the end with a reason, not a promise.

### 14.1 Three findings were stale

**§4.1, §4.2 and §4.3 describe a screen that no longer exists.** All three are about *Search / Manage* — no
duplicate prevention, no result count or sort or paging, a second search layout. §9.1 deleted that screen
(`portals/catalog.ts`: "a second, weaker search over the same registry this list already searches") and §10
replaced it with the roster's server-side search, sorting and paging. Struck, not fixed.

### 14.2 Two findings were wrong as recorded

**§5.6 said ReceptionEligibility announces nothing.** It announces correctly — the outcome cards sit inside an
`aria-live="polite"` region. What was wrong is narrower and still worth fixing: a failed lookup was a
`StatusChip`, and a chip is the vocabulary for *the state of a thing on screen* (a membership is Active, a
visit is allowed), borrowed to report *the outcome of something the operator just did*. Every other screen
says that with `InlineAlert`. Now it does too.

**§4.4 said `% used` renders the same fact two ways.** It does not — it renders *three different facts* as two
symbols. `percentUsed` is null in two unrelated cases and the server distinguishes them on the same row:
`utilizationBand` is `Unlimited` when a member is covered with no accumulating ceiling, and `Zero` with a null
percentage when there is no coverage at all. Both arrived as "—", beside rows reading "0%". So a member whose
benefit was never metered, a member with no cover, and a member who simply has not claimed yet were two
symbols between them. `libs/benefit-pricing` is explicit that this matters: "an unlimited benefit reported as
0% invites 'plenty left' on something that was never metered." Each fact now says itself.

The `NAME` column's em dash was likewise the table's word for "this field is empty", which is not what
happened: the membership exists and the *person record* behind it could not be read. The member's own detail
card has said "Name unavailable" since 19.5; the roster says it too, so one person does not get two different
explanations depending on which screen you opened.

### 14.3 The analytics filter bar (§5.1)

Recorded as "blank bordered boxes — nothing saying they are pickers". Worse than that: **nine of twelve
filters were free-text over uuid and enum-token columns.** Four wanted a v7 uuid typed from memory
(`payerId`, `policyId`, `policyPlanId`, `groupId`, `branchId`); five wanted an exact token (`High`,
`Principal`, `Terminated`), where a near miss is not an error but an empty chart reading "no data for this
period". The dashboard's own narrowing could only be used by somebody who already knew the answer.

Every filter over a known set is now a picker, and the sets come from the API — the same payer, policy, plan,
group, tier and category lists the rest of the portal is built from, resolved *for this caller*. A bundled
catalogue would offer a payer somebody is not assigned to, which is both wrong and a small disclosure. Plan
and Group hang off a policy, so they are disabled until one is chosen, with the reason on the field: enabled
and empty is a control that can only disappoint. A reference read that fails leaves its list empty and says so
in the bar, rather than silently degrading to a uuid box that looks like it works. `policyId` gained a control
at the same time — it was in `FILTER_KEYS`, so it could arrive by URL and could not be set.

`safe()` wraps each read in a thunk rather than `.catch()` on the promise, because a synchronous throw never
reaches `.catch` and would empty five working pickers over one broken lookup.

### 14.4 Arabic, in two places (§3.1, §5.8)

**The column headers were the last monolingual text on the dashboard**, and they sat on the accessible table
— the element that exists *for* the reader who cannot see the chart. Title, row label and summary were all
authored in both languages; the headers were `IReadOnlyList<string>`. They are `BiText` now, from a named
vocabulary (`AnalyticsColumns`) rather than literals at each call site: "Members" heads five series and "Net
payable" three, and written inline those were five and three independent chances to author a different Arabic
word for one column.

**The numeral systems ran through single cards.** `render()` in `SeriesCard` sent currency through
`fmt.money` (which resolves `ar-EG` → Arabic-Indic) and counts and percentages through `String(value)` and a
template literal (always Latin). Meanwhile the server composed `SummaryAr` under `InvariantCulture`, so the
Arabic sentence above them printed Latin digits too. All three now agree.

Switching the server to `CultureInfo.GetCultureInfo("ar-EG")` was the obvious fix and it does not work:
.NET's `ToString` formats with ASCII digits under *every* culture — `NumberFormatInfo.NativeDigits` exists and
numeric formatting does not apply it — so ar-EG changes the decimal separator and nothing else. It also
*throws* under globalization-invariant mode, which is how .NET runs in a slim container, and from a static
initializer that takes the whole class down. The ten code points are mapped explicitly instead, and a test
pins the hazard: the mapping is indiscriminate, so pointing it at anything carrying a business key would
rewrite the key.

### 14.5 The rest of the design-system debt

* **§5.2 — Utilization KPIs.** Were `<dl class="pol-kpis">`: dt at 0.82rem, dd at 1.25rem. On a screen whose
  whole purpose is "how much of this cohort's entitlement is gone", the answer read as a caption. New
  `KpiList` keeps the definition-list semantics (four terms describing one subject, announced as pairs) and
  takes the hairline, uppercase micro-label and 34px tabular numerals from the same classes `KpiCard` uses —
  so the two cannot drift into two different-looking KPIs. `KpiCard` stays for the dashboards, where each
  tile is an independent headline and the grouping is layout rather than a claim about the content.
* **§5.4 — zero bars.** The track was a solid full-width block, so a zero-value row rendered as a filled pill
  with "0" beside it, on the series that exists to flag people over their limit. An inset hairline over a
  transparent well reads as a container waiting to be filled.
* **§5.5 — the dark lockup.** Recolouring in CSS was not available (`<text fill>` inside an `<img>`), so
  there is a second asset: white Latin wordmark, light slate sub-wordmark, gold Arabic unchanged — that half
  carries its own contrast on both surfaces and is the half the organisation is known by.
* **§5.9 — field widths.** Sized on the CONTROL, not the grid track, so labels still line up across the row:
  shrinking the cell would ragged a whole section to save pixels on one field. `ch` units, because these are
  character counts.
* **§5.10 — the error summary.** Moved to the top of the form, and it now NAMES the failing fields as links
  rather than saying how many. Focus still moves to the first invalid control on submit — that is the faster
  path for a keyboard user; the summary is the map for everyone who scrolls away from it.
* **§5.3 — empty states.** Only ReceptionEligibility survived the §9.1 deletions. "No matching beneficiary"
  is where the desk's work forks, and a grey chip left the operator to work out both branches: a mis-read
  digit, or a person who is genuinely not registered. Naming the two is the whole content of the state.

### 14.6 The §11 sweep leftovers

**The notification vocabulary is reconciled (§11.3).** Four routes were keyed on names no service publishes.
The publishers' names win — renaming them instead would be a wire-contract change across four services with
live consumers, to fix a table only notification-service reads. Template keys are unchanged: those are this
service's own vocabulary and rows in `notification.template`. The retired names are *gone*, not aliased, and
a test asserts that: two names for one fact is two places to change, and they drift.

| Route was | Publisher actually sends | Where |
|---|---|---|
| `ResultReady` | `OrderResultUploaded` | orders `Results.cs` |
| `RxReady` | `RxApproved` | pharmacy `Prescriptions.cs` |
| `AppointmentReminder` | `AppointmentReminderIssued` | emr `Reminders.cs` |
| `AppointmentNoShow` | `ApptNoShow` | emr `Appointments.cs` |

**A queue nobody reads.** Found while doing the above, and not in §11: `InAppReminderChannel` published every
appointment reminder to `notification.events`, which has no consumer — `DomainEventConsumer` reads
`notification.domain-events` and `notification.registration-events`. A publish to an unbound queue does not
fail, so the reminder path looked live end to end: the scheduler ran, the channel was called, the outbox
relayed, and nobody was reminded of anything. It also carried no recipients and no tenant, so it would have
been dead-lettered on the right queue. All three fixed; `ReminderMessage` gained `TenantId` because the
consumer refuses to write a notice under a guessed tenant.

**`RxLineOutOfStock`** matched `RoutingTable` by name all along and still notified nobody — it went only to
`pharmacy.events`, and a notification consumer bound there would compete with policy-service for the
messages. It now sends the second, notification-shaped copy, addressed to the prescriber. That route is
actionable and escalates after eight hours; an escalation on a notice nobody received is a safety net with
nothing under it.

**`PractitionerBranchRevoked` (§11.1 item 5)** was recorded as "a consumer on a queue nothing publishes to".
Narrower and worse: provider-service *has* published it all along — to `provider.events`, while the consumer
binds `emr.practitioner-branch-revoked`. The payload was also missing `tenantId`, which would have
dead-lettered every message even on the right queue. Both fixed.

**The escalation sweep now runs (§11.1 item 6).** `EscalationService` was complete, correct, idempotent, and
constructed only by its tests, while `NotificationDispatcher` has been stamping `EscalationDueAt` on every
actionable notification for three phases. The platform has been writing "escalate this at 14:32 if nobody
acts" onto rows, and 14:32 never arrived. `EscalationSweeper` runs it every five minutes, per tenant — there
is no request, so there is no principal to take a tenant from, and after 18.F3 an unbound session reads
nothing at all. Same shape as `ReportAccessExpirySweeper`, deliberately.

**The ordering clinician reaches the authorization (§11.3).** `POST /authorizations` requires `auth:ingest`,
a machine-only scope, so `CreatedBy` named the routing saga and `NotifyDecisionAsync` correctly sent nothing.
`CreateAuthorizationRequest` now carries `OrderedByUserId`, falling back to the caller so break-glass and
manual paths are unchanged, and `OrderPendingApproval` / `RxSubmitted` carry `orderedByUserId` forward.

### 14.7 What is NOT done, and why

**`OrderLineAvailable` and `AuthSlaBreached` are still unreachable, and it is not a naming problem.** Unlike
the four above there is nothing to rename them to. Orders' vocabulary is Created / PendingApproval /
Activated / Cancelled / LinesConsumed / Completed / ResultUploaded — nothing models "a line that was
unavailable has become available", so the notice has no moment to fire at. And `SlaBreached` is a boolean
computed at decision time, never an event: a breach is the *absence* of a decision, so nothing that happens
can publish it. Only a sweep over what has not happened can, which is the same shape as the escalation sweep
above. Both routes are left in the table with the reason written next to them, because a table with the route
deleted makes the missing publisher invisible to whoever adds one.

**No routing saga calls `POST /authorizations` at all.** §11.3 called the clinician-id change "the next
change, and it is small" — the change was small, and it is not sufficient: `OrderPendingApproval` and
`RxSubmitted` are published and nothing consumes them into an authorization. The ingest seam has no caller,
which is the same §11 shape one level up.

**reporting-service `POST /projections` (§11.1 item 4) is deliberately still unwired.** `EventProjector` and
`AnalyticsProjector` handle 27 event types across six services. Feeding them means a consumer here plus a
second copy from every publisher, and wiring *some* of them is worse than wiring none: a read model fed 4 of
27 fact types produces dashboards that look correct and are wrong, which is this codebase's own stated rule
("a total silently missing a component is not narrower, it is wrong", §11.2). Building the consumer alone
would add another fully-built-and-connected-to-nothing component to the pile the §11 sweep exists to find. It
needs to be done in one pass, with the publisher list enumerated, and that is its own piece of work.

### 14.8 The browser is back — and it found two things immediately

§12.5 and §13.3 have carried the same gap since round 7: no browser, so every visual and contrast claim was
argued from CSS. Restored without root — the NSS/NSPR libraries extracted from `.deb` packages into
`~/.local/playwright-deps` and reached through `LD_LIBRARY_PATH`, Node 22 in `~/.local` (pnpm needs ≥22.13 and
the machine's default node is v20), and `@playwright/test` in a standalone runner at `~/.local/pw-runner` so
the workspace's `node_modules` was never purged. Nothing in the repository changed.

The first in-browser axe pass found **two real WCAG AA contrast failures that jsdom cannot see** — it has no
layout engine, so `color-contrast` reports nothing there whatever the palette does, which is why both sat
under a green a11y gate:

* **The active nav item, in every portal.** `--accent` is documented as 5.2:1 and it is — against *white*.
  `.mrs-navi[aria-current="page"]` paints it on `--accent-tint`, where it is **4.44:1** at 15px. The token
  comment measured the right colour against the wrong background. `--accent-press` on that tint is 6.7:1 and
  is already the "engaged" tone.
* **The registration form's five section legends.** `--text-3` is the meta tone and measures **4.34:1** on
  white, at 11.5px — nowhere near the large-text exemption. These are section headings, not metadata;
  `--text-2` is 7.0:1.

Both fixed and re-verified in the browser: clean.

One pre-existing spec bug, noted and not fixed: `e2e/a11y-contrast.spec.ts` lists `/login` with `role: null`
and then waits for a non-empty `<main>`. The login page has no `<main>` — that element belongs to the
authenticated shell — so that route's four cases cannot pass as written.

### Verification

* `tsc --noEmit` clean in `apps/web` and `apps/design-system`.
* **442 web** across 37 files (was 435), including the axe route × locale × theme sweep · **50 design-system**.
* Backend with `--with-db`: **52 notification** (was 44) · **37 reporting** (was 36) · **185 emr** ·
  **73 provider** · **82 orders** · **55 pharmacy** · **65 approvals**.
* In-browser axe (Chromium, real paint) on `/beneficiaries/analytics` (en + ar), `/beneficiaries/eligibility`
  and `/beneficiaries/register`: no serious or critical violations after the two fixes above.
* Screenshots taken of the rebuilt filter bar, the eligibility empty state and the registration form. The
  fixture-mode bundle was used and then discarded; `apps/web/.env.local` and `apps/web/dist` are as they were.

---

## 15. Round 10 — the reporting projections, wired

§14.7 left this deliberately undone and said why: the projectors handle twenty event types across six
services, and feeding *some* of them produces dashboards that look correct and are wrong. This round does it
in one pass.

### 15.1 The projector's vocabulary had the §11.3 disease too

Before anything could be wired, the two projectors' event names had to be reconciled against what the
platform actually publishes. Seventeen of the twenty-seven cases had **no publisher under that name**:

| Projector case | What is actually published | Where |
|---|---|---|
| `EncounterCreated` | `EncounterStarted` | emr `Program.cs` |
| `AppointmentBooked` | `ApptBooked` | emr `Appointments.cs` |
| `AppointmentAttended` | `ApptCheckedIn` | emr `Queue.cs`, `Program.cs` |
| `AppointmentNoShow` | `ApptNoShow` | emr `Appointments.cs` |
| `OrderLineConsumed` | `OrderLinesConsumed` (plural) | orders `Consume.cs` |
| `ClaimSettled` | `Claim{Status}.v1` | claims `DecisionEndpoints.cs` |
| `DimensionLabelled` | `PayerCreated` / `PolicyPlanAttached` | policy |

Same shape as the notification routing table, one layer down, and with the same silence: a projector whose
switch falls through returns `false`, the event is written to `processed_event`, and no fact appears. A read
model that has quietly stopped being fed is indistinguishable from a quiet week.

**The publishers' names win again**, and the translation lives in `ProjectionMapping` — reporting adapts to
the platform, not the platform to the read model.

### 15.2 Delivery: a relay mirror, not thirteen second copies

notification-service is fed by publishers enqueuing a second, notification-shaped copy. That is right *there*
because the publisher knows something the consumer cannot work out — the recipient. Reporting needs no such
thing: it derives everything from the event's own payload. Making thirteen call sites build a
reporting-shaped envelope would teach thirteen services the read model's field vocabulary, and every schema
change would become a thirteen-service change.

So `RabbitMqEventPublisher` mirrors the raw message to `reporting.projection-events` when its type is on
`ProjectionFeed` — same body, **same `MessageId`**, which is what makes the projector's dedupe work against
relay retries. The original publish is untouched, so no existing consumer sees any difference.

A subscription was not available: the transport is point-to-point (default exchange, destination as routing
key), so a reporting consumer on `policy.events` would compete with eligibility-service and take half its
messages. Same for `orders.events` and `pharmacy.events`, which policy-service consumes.

### 15.3 What the publishers were missing

Delivery alone was not enough — several payloads did not carry what the projectors read. These are additive
enrichments to each service's *own* event, not knowledge of the read model:

* **approvals** — `TatSeconds` and `SlaBreached` are computed on the decision and were written to the row and
  nowhere else, so the approval-TAT report had no turnaround times and no breach counts to build from.
  `priority` too: TAT unaggregated by priority is meaningless, because Urgent and Routine answer different
  promises. `rejectionReason` is deliberately still absent — this domain has no reason-code vocabulary, only
  the reviewer's free clinical text, and deriving a "code" from it would be inventing a taxonomy at the point
  of export.
* **policy** — `CoverageLimitChanged` carried the limit and the new totals and nothing else. The member
  utilization fact needs payer, policy, plan, group, branch, beneficiary, enrolment and category — every axis
  the analytics filter bar narrows by. Without them each fact lands under `Guid.Empty` and "unknown", which
  is worse than no fact: the totals are then right only when nobody filters.
* **policy** — **`MemberGroupChanged` never published at all.** Terminate, reinstate, plan-change and cancel
  all emit to `policy.events` with the same dimension bag; a group change wrote its enrollment_event row and
  its timeline entry and stopped. The enrolment curve counted five of the six movements, and a member moving
  between groups — which is how a cohort is re-cut, and therefore what a group-level report is asked about —
  was invisible to every consumer outside policy-service.
* **emr** — the clinic. `EncounterFact.ClinicId` falls back to "unknown", and a per-clinic chart where every
  row says unknown is a chart of nothing.
* **orders** — `orderType`, which is what splits Lab from Radiology. Without it every consumed line would
  have landed in the Lab bucket under "unknown".
* **pharmacy** — which drug. `RxDispensed` named none, so every dispense would have counted under "unknown"
  and "top medications" would have been one bar. It sends the drug ID rather than the ATC class the
  projector's field is named for: the ATC lives in masterdata-service, and resolving it would put a
  cross-service call inside the transaction that moves a benefit accumulator.
* **claims** — the amounts. Payer, policy, plan and benefit category are **deliberately absent**: claims does
  not hold them. They are left null rather than guessed, so the financial totals are exact and the per-payer
  breakdown is visibly unattributed instead of quietly wrong.

### 15.4 Two projector cases were wrong, not merely unwired

* **`BenefitConsumed`** shared a case with `CoverageLimitChanged`. No service publishes it and none should —
  policy-service already emits `CoverageLimitChanged` from `BenefitConsumptionApplier`, the single writer of
  the accumulator. Two names for one movement is how a fact gets counted twice the day somebody wires the
  second. Removed. (The 19.6b tests had been building their fixtures on that name, so they were proving the
  projector's arithmetic against an event that could never arrive.)
* **`ClaimAdjudicated`** was the wrong **grain**. Adjudication is a pre-decision recommendation: booking it as
  cost would record money a reviewer may still reduce, and record it again when they do. Removed; the
  terminal decision is the hook.

### 15.5 The anti-drift guard

`ProjectionFeedTests` reads the `case "…"` labels out of both projector sources and asserts they correspond
exactly to `ProjectionFeed` mapped through `ProjectionMapping` — no hand-copied expectation, because that
would be a third place to keep in step and would be updated in the same edit that broke the pipeline.

Two projector cases remain unfed, and each must be listed in `KnownUnfed` **with a reason of real length** or
the test fails:

* **`DiagnosisRecorded`** — emr records diagnoses and publishes no event for them. Feeding the top-diagnoses
  report needs a publisher carrying tenant + ICD and nothing else: the fact is a code COUNT, so it must not
  carry the beneficiary it came from.
* **`ServiceValued`** — nothing values a service as an event. finance publishes `SettlementApproved`, a
  settlement total for a provider, which is a different grain; mapping one to the other would report a
  settlement as a service valuation.

### 15.6 Verified live, not only in tests

Rebuilt and redeployed policy-service and reporting-service, then drove real requests through Kong:

```
reporting.dim_label       0 → 1   (payer created → PayerCreated → DimensionLabelled)
reporting.fact_enrolment  0 → 1   (member enrolled → MemberEnrolled, payer + plan both resolved)
```

The consumer logs `reporting-service projecting from reporting.projection-events`, and the queue exists
beside `policy.events` rather than competing with it. `GET /analytics/enrolment` now returns
`joined: 1, net: 1` where it returned nothing — and the same response confirms two of round 9's fixes in
production: `columns` arrives as `{"en":"Movement","ar":"الحركة"}`, and `summaryAr` reads
"٣ سلاسل بإجمالي ٢" in Arabic-Indic digits.

**One honest limit.** The label feed is forward-looking and there is no backfill: the plan attached before
this round still renders as `f948206a`, its uuid fragment. That is `AnalyticsQueries.Label`'s deliberate
fallback — "a truncated id sends someone looking while 'Unknown plan' hides the gap" — and it will keep
saying so for every dimension created before today. Backfilling means re-publishing `PayerCreated` /
`PolicyPlanAttached` for existing rows, which is a migration rather than a wiring change.

### Verification

* **1455 backend tests, 0 failed**, all with `--with-db`: reporting 61 (was 37) · policy 480 · approvals 65 ·
  emr 185 · orders 82 · pharmacy 55 · claims 193 · notification 52 · finance 19 · patient 118 · provider 73 ·
  eligibility 53 · libs/events 19.
* The new reporting tests include an end-to-end case whose payloads are **copied from the publishers** rather
  than invented — a fixture written to match the mapping would agree with itself and prove nothing.
* Live, through Kong, as above.

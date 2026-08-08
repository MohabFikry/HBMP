# Patient Profile — always-visible identity + tabbed sections

> Design [39 §3, §5, §6](../../../HBMP-Design/39-patient-profile.md) (unified patient profile, the identity
> block, four-cue section states). Reworks the layout `PatientProfile.tsx`/`ProfileSectionViews.tsx` already
> implement (phase 20.4) — no server contract change, no new section keys.

## The problem

The profile currently stacks all 15 server sections vertically with a left-hand anchor-jump nav
(`.profile-jump`). On a role with most sections visible this is a long scroll, and skimming it requires the
side nav rather than the content itself telling you where you are. Blood group and allergy — the two facts a
clinician most needs before touching a patient — sit inside a `Alerts` card partway down the page rather than
in the identity block that's already reused (as `ProfileIdentity`) in every other clinical workspace via
`PatientContextBar`.

## What changes

1. The `header` section stops being a section. It becomes an always-visible card above the tab bar, with its
   `<h2>Identity</h2>` label removed (its content already reads as an identity block without one) and no
   corresponding `.profile-jump` entry.
2. `ProfileIdentity` on this card is called exactly the way `PatientContextBar` already calls it —
   `bloodGroup` from the `alerts` section's data, and up to 2 named-allergen chips + a "+N more" chip via the
   existing `chips`/`namedAllergens` pattern. No new component, no new contract field: this reuses the
   identity block's existing optional props, which today only `PatientContextBar` passes.
3. The remaining 14 sections regroup into 7 tabs (order below), replacing `.profile-jump` with a sticky pill
   tab bar. Switching tabs shows/hides pre-fetched content; it never refetches.
4. Within a tab, each constituent section keeps its own card, title, action buttons, and
   Restricted/Unavailable/Empty/Visible handling — unchanged from today, just regrouped. A tab with a mix of
   visible and restricted constituent sections shows both kinds of card, stacked.

## Tab → section mapping

| # | Tab | Constituent server section keys |
|---|-----|----------------------------------|
| 1 | Coverage | `coverage` |
| 2 | History | `alerts`, `pastMedicalHistory`, `encounters`, `investigations`, `prescriptions`, `caseManagement` |
| 3 | Authorizations | `authorizations`, `referrals`, `financial` |
| 4 | Documents | `documents` |
| 5 | Notes | `notes` |
| 6 | Timeline | `timeline` |
| 7 | Call history | `callHistory` |

Default active tab: **Coverage** (first). `header` is not a tab (see above); every other key from
`PROFILE_SECTION_KEYS` maps to exactly one tab — this table is exhaustive over the current key set, and an
unrecognized future key falls back to the History tab (`FallbackView`'s existing behavior, just needs a tab
to render inside) rather than being silently dropped.

Within a tab, constituent sections render in the server's original `PROFILE_SECTION_KEYS` order (unchanged
sort), not the table's left-to-right listing — e.g. inside History, `alerts` still renders before
`encounters` because alerts-before-content is a safety property (existing comment in `ProfileBody`), not a
layout choice this redesign touches.

## Tab bar: `Tabs`, not `SegmentedControl`

Visually a pill bar (matches the reference screenshot / `SegmentedControl`'s `.mrs-seg` look). Semantically
this is primary content-switching, not a filter, so it uses the design system's `Tabs` component
(`apps/design-system/src/components/Tabs.tsx`, Radix-backed — `tablist`/`tab`/`tabpanel` roles, roving focus,
arrow-key nav) rather than `SegmentedControl` (a `radiogroup`, the right semantics for a filter like
`ApprovalsRegister`'s Delivered/Awaiting/Everything switch, wrong for a page's main navigation).

`Tabs` currently ships one visual style (underlined, `.mrs-tab`/`.mrs-tabs`, 0B §6). This adds a `variant`
prop (`"underline" | "pill"`, default `"underline"` — existing call sites unaffected) and a `.mrs-tabs--pill`
CSS rule reusing `.mrs-seg`'s track/pill treatment on `.mrs-tab`. No new component; a visual variant on an
existing one, the same shape as `Button`'s `variant` prop.

`Tabs` already renders every panel with `forceMount` + `hidden` rather than mounting/unmounting on switch, so
no data refetch happens on tab change — the whole profile is one `patientProfile()` call today and stays one.

The tab bar is `position: sticky` at the offset `.profile-jump` used, so it stays reachable while a long
tab's content scrolls beneath it.

## What does not change

- The server contract (`PatientProfileContract`, `PROFILE_SECTION_KEYS`, per-section `state`/`reasonCode`) —
  purely a client-side regrouping of sections the server already returns in one response.
- `SectionCard`/`SectionState`/`SectionContent`/`SectionView` dispatch and the four-state rendering rules.
- `SECTION_ACTIONS` deep-links, `SECTION_TITLES`, `REASONS` — reused as-is per section.
- `PatientContextBar` — already does the identity+bloodGroup+chips pattern this borrows; untouched.
- `AlertsView` — still renders full detail (severity, reaction, critical/operational flags) inside the
  History tab; the identity card's chips are a summary, not a replacement.
- No section gains or loses visibility rules; this is layout only.

## Implementation sketch

`apps/web/src/screens/PatientProfile.tsx`:
- `ProfileBody`: split `ordered` into `header` (rendered directly, no card wrapper/heading) and the rest,
  grouped by the table above into 7 arrays. Replace `<nav className="profile-jump">` with `<Tabs variant="pill">`,
  one `TabItem` per group, `content` = that group's sections mapped through the existing `SectionCard`.
- `HeaderView`/`ProfileIdentity` call site: pass `bloodGroup` (from the `alerts` section's data, `?? null`
  when alerts isn't `Visible`) and `chips` (named-allergen chips, same JSX `PatientContextBar` builds) —
  extract the chip-building logic `PatientContextBar` already has into a small shared helper so both call
  sites (identity card, context bar) build it identically rather than two copies drifting.
- Drop the `header` entry from `SECTION_TITLES`'s nav usage (title itself can stay in the map — other code
  may still reference it — just isn't rendered as a heading here) and from any place that iterated
  `PROFILE_SECTION_KEYS` to build the old jump nav.

`apps/design-system/src/components/Tabs.tsx` + `styles/components.css`: add `variant` prop and
`.mrs-tabs--pill`/`.mrs-tab` pill rule (visual only, reusing `--r-pill`/`--surface-2`/`--elev-1` tokens
already defined for `.mrs-seg`).

CSS (`apps/web/src/styles/app.css`): remove `.profile-jump` styling (no longer rendered), add sticky
positioning for the new tab bar at the same offset, keep `.profile-section`/`.profile-section-head` etc.
unchanged since cards inside a tab still use them.

## Tests to update

- `apps/web/test/patient-profile.test.tsx` — section-ordering assertions need to become tab-content
  assertions (switch to a tab, assert its cards); remove/replace `.profile-jump` assertions; add coverage for
  the identity card's new blood group + allergy chips and for the "Identity" heading no longer rendering.
- `apps/web/test/patient-profile-sections.test.tsx` — assertions that currently find a section by scrolling
  the full stack should instead select its tab first when the section moved out of the always-rendered area
  (all of them did, except `header`/`alerts`... `alerts` moved into History, so this applies to it too).
- Both files' axe checks re-run against the new structure (tabs must pass the same a11y gate).
- No change expected to `member-clinical-panel.test.tsx`, `encounter-workspace.test.tsx`,
  `doctor-visits.test.tsx` — `MemberClinicalPanel`/`PatientContextBar` are untouched.

## Open items deliberately left for implementation, not this spec

- Exact chip/helper function name and file for the shared allergy-chip builder (small refactor, no design
  decision left to make).
- Whether `.mrs-tabs--pill`'s 44px min-height target needs any adjustment for the 7-tab bar to fit at 360px
  reflow — an implementation-time check against the accessibility DoD (0B, WCAG 2.5.8), not a design fork.

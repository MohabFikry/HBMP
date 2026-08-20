# Mersal HBMP — Data Table & Button Audit

**Date:** 2026-08-10 · **Scope:** every data table and every button in `apps/web` (all portals), audited against `apps/design-system` and `HBMP-Design/0B-DESIGN-SYSTEM-UI.md` · **Nature:** read-only. No code was modified. This report is the basis for a separate fix effort.

**Method:** the SPA was parsed mechanically rather than sampled — a JSX-aware extractor walked all 104 `.tsx` files under `apps/web/src`, resolved every `<DataTable>` / `<DataTableView>` / `<Button>` element with its attributes, resolved each table's `caption` back through the screen's local `Localized` object to English, and matched column definitions to their `sortable` / `numeric` flags. Every count below is that extraction, not an estimate. Every named finding was then confirmed by reading the source.

---

## 0. Executive summary

**The design system already contains the correct answer to almost every finding in this report. The gap is adoption, not capability.**

`DataTableView` (`apps/design-system/src/components/DataTableView.tsx`) composes toolbar + table + pager against one `useTableQuery` object, and its own doc comment states why it exists: *"a house standard that lives in a document is one every screen implements slightly differently."* That prediction has come true — **10 of 116 tables use it.** The other 106 are the slightly-different implementations it was built to prevent.

The same holds for buttons. `Button` ships `danger` and `warn` variants, `.mrs-btn svg` normalises icon size, and `.mrs-btn.mrs-danger:has(> svg:only-child)` even handles the icon-only destructive case. The components are right; the call sites are uneven.

### What is actually broken (as opposed to merely inconsistent)

- **One screen silently truncates.** `PolicyBook` asks the server for 50 policies, renders them, and discards `totalCount`, `totalPages` and `identityMatchTruncated`. Policy 51 is unreachable and nothing on screen says so. `MemberAdmin`, same API shape, renders all three.
- **Three destructive writes fire immediately from `ghost`** — the lightest variant in the system, the same one used for "Cancel" — with no confirmation step. The same verb, `revoke`, is `danger` + a confirm modal two screens away.
- **The bulk-intake screens hide the same kind of truncation twice.** The server returns the first 50 errors plus a `totalErrors` count plus a stored file containing all of them. Both screens render the 50 and disclose neither of the other two.
- **65 tables across 28 screens have not one sortable column**, including the approvals worklist, the claims worklist, the provider directory and the inventory movement ledger.

### Counts

| | Total | Conforming | Notes |
|---|---|---|---|
| Tables (all kinds) | **116** | 10 | 94 design-system, 22 hand-rolled `<table>` |
| — `DataTableView` (full standard) | 10 | 10 | 5 screens |
| — bare `DataTable` | 84 | — | no toolbar, no pager |
| — raw `<table>` markup | 22 | — | 14 files, 4 parallel CSS skins |
| Sortable columns | 125 / 368 | 34% | concentrated in 12 files |
| Tables with a pager | **11 / 116** | 9% | 10 auto + `MemberAdmin` |
| Tables with multi-select | **1 / 116** | — | `RegistrationApprovals` only |
| Buttons | **354** | — | 317 `<Button>` + 37 raw `<button>` |
| — with an icon | 80 / 317 | 25% | |
| — `danger` variant | 17 static + 11 conditional | | |
| — `warn` variant | **0** | | shipped, never used |
| — unstyled (browser default) | **1** | | `CallCentre.tsx:849` |

### Findings by severity

| Severity | Count | Theme |
|---|---|---|
| High | 4 | Silent truncation (×2, same shape); unconfirmed destructive actions from the lightest variant; unbounded queues with no way to find a row |
| Medium | 7 | Sort coverage, hand-rolled toolbars, icon inconsistency, wrong glyph, dismiss-weight split, row-action variance, selection-by-colour |
| Low | 6 | Dead icon sizing, unused `warn`, parallel skins, `stickyEnd` under-use, `numeric` residue, one unstyled button |

---

## 1. The standard being audited against

For clarity about what "conforming" means, this is what the design system provides today.

| Concern | Component / API | Where |
|---|---|---|
| Table + sticky header + `aria-sort` + roving-tabindex nav + loading/empty/error | `DataTable` | `components/DataTable.tsx` |
| Search + faceted filter chips + live region | `TableToolbar` | `components/TableToolbar.tsx` |
| Pager + page-size | `Pagination` | `components/Pagination.tsx` |
| Search → filter → **sort → page**, in that order, with session persistence | `useTableQuery` | `lib/useTableQuery.ts` |
| All four assembled correctly | **`DataTableView`** | `components/DataTableView.tsx` |
| Column right-alignment + tabular figures for quantities | `Column.numeric` | `DataTable.tsx:53` |
| Pin the actions column while the rest scrolls | `Column.stickyEnd` | `DataTable.tsx:30` |
| Multi-row selection with per-row eligibility | `RowSelection` | `DataTable.tsx:93` |
| Button variants incl. `danger` / `warn`, ≥44px target, loading, icon slot | `Button` | `components/Button.tsx` |
| Uniform icon family, one glyph per meaning | `Icon` | `components/Icon.tsx` |

Two properties of this set matter for the findings below:

1. **`useTableQuery` sorts before it pages.** A bare `DataTable` sorts *itself*, which means it sorts the rows it was handed. With a pager above it, that orders page 1 and leaves the true first row on page 4. The hook's own comment calls this out. Any screen that adds paging to a bare table without moving sort to the hook will hit it.
2. **`DataTableView` distinguishes "empty" from "no matches."** An empty queue is good news; an empty queue *because you typed something* needs the search cleared. Screens that hand-roll the toolbar have to remember this. Two do; the rest have no search to get it wrong with.

---

## 2. High findings

### H1 — `PolicyBook` truncates the policy list at 50 with no pager and no notice

`screens/PolicyBook.tsx:96` requests `policyQuery({ pageSize: 50 })`. Line 117 renders `page?.items ?? []` into a bare `DataTable`. The response envelope (`api/policyApi.ts:40-53`) carries `totalCount`, `totalPages` and `identityMatchTruncated`; the screen reads **none** of them — `grep identityMatchTruncated screens/PolicyBook.tsx` returns zero, and `totalCount` appears in exactly one screen in the app (`MemberAdmin.tsx:574`).

The comparison is the point. `MemberAdmin` consumes the *same* `QueryPage` shape and does all three things: server-side sort (`MemberAdmin.tsx:470-478`), a real `<Pagination>` fed by `totalCount`, and an `InlineAlert` for `identityMatchTruncated` carrying the comment *"A truncated identity match makes the page a SUBSET. Saying so is the difference between a search and a wrong answer."* `PolicyBook` renders `payerScopeApplied` and `unavailable` (lines 107-110) — so the author was reading the envelope — and stops before the two fields that bound the result.

An operator searching for a policy that sorts 51st is told, in effect, that it does not exist.

*Fix:* wire `Pagination` from `totalCount`, move sort to the server as `MemberAdmin` does, and render the truncation alert. `PolicyBook.tsx:390` and `:473` make the same 50-row call for dropdown population — those are fine, they are pickers, not results.

---

### H2 — Three destructive writes fire from `ghost`, with no confirmation

An extractor matched every `<Button>` whose handler calls a destructive `api.*` method directly. Exactly three exist, and all three are `ghost`:

| Site | Call | Variant | Confirm? |
|---|---|---|---|
| `screens/NetworkTierAdmin.tsx:201` | `api.revokeAssignment` | `ghost` | none — `onClick` awaits it |
| `screens/PractitionerAdmin.tsx:552` | `api.revokeSpecialty` | `ghost` | none — `run()` (`:483`) just fires |
| `screens/PractitionerAdmin.tsx:607` | `api.revokePractitionerBranch` | `ghost` | none — same helper |

Against, two screens over:

| `screens/AccessAdmin.tsx:523` | revoke a session | **`danger`** | opens a `Modal` |
| `screens/AccessAdmin.tsx:545` | confirm in that modal | **`danger`** | — |

So the identical verb carries the heaviest treatment plus a confirmation gate in one screen and the lightest treatment plus none in two others. `ghost` is transparent, borderless, and is what every "Cancel" in the app uses — it is the system's signal for *"this does nothing"*.

These are not cosmetic writes. Revoking a practitioner's specialty changes what they can be booked and can order for; revoking their branch removes a clinician from a clinic (the code at `:606` even renders a `lastClinicWarning` beside the button, so the risk was known); revoking a tier assignment changes which network tier — and therefore which rates — apply to a provider.

*Fix:* `variant="danger"` on all three, and route them through the existing `ConfirmAction` (`screens/ConfirmAction.tsx`) or a `Modal` in the `AccessAdmin` shape. The `lastClinicWarning` becomes the modal's description rather than a caption an operator reads after clicking.

---

### H3 — Unbounded operational lists render with no search, no sort and no pager

84 tables are bare `DataTable`. Most of those are legitimately bounded — a plan's benefit categories, a role→scope matrix, one member's coverage. The problem is the subset whose source is a growing operational list. Every one below is fed by an API returning a plain unbounded array (`Promise<T[]>`, no page envelope), rendered whole, with nothing to search, sort or page it:

| Screen | Table | Source | Grows with |
|---|---|---|---|
| `NetworkPortal.tsx:71` | Providers Directory | `api.providerList()` — comment: *"the tenant's whole network"* | network size |
| `NetworkPortal.tsx:164` / `:186` | Contracts & coverage / Locations & users | `providerList` derivatives | network size |
| `AdminConsole.tsx:146` | Accounts (identity store) | `api.identityUsers()` | every staff account ever |
| `AdminConsole.tsx:152` | Role bindings & recertification | `api.accessMatrix()` | accounts × roles |
| `AccessAdmin.tsx:207` | Users & Access | `api.memberships(…, query)` | has server search; no sort, no pager |
| `ApprovalsWorklist.tsx:153` | Approval Worklist | queue | the core benefit queue |
| `ApprovalsRegister.tsx:127` | Authorizations | register | every authorization ever |
| `ClaimsPortal.tsx:91` / `:132` | Claims Worklist / Reconciliation | `api.claimsWorklist()` | claim volume |
| `CaseManager.tsx:92` / `:209` / `:233` | Cases / Tasks / Escalations | queues | caseload |
| `BranchInventory.tsx:176` / `:199` | Stock / **Movement ledger** | `data.movements` | append-only, forever |
| `FinancePortal.tsx:89` / `:136` | Utilization / Provider Settlements | rollups | period volume |
| `Notifications.tsx:113` | Notifications | inbox | unbounded |
| `PractitionerAdmin.tsx:316` | Current clinicians | roster | headcount |
| `BranchLicences.tsx:124` / `:260` / `:275` | Practitioners / Alerts / Reassignments | rosters | headcount |
| `ProcedureCentre.tsx:214` | Our queue | queue | daily volume |
| `ReceptionDashboard.tsx:298` | Visits | day list | clinic size |
| `NetworkTierAdmin.tsx:182` | Assignments | assignments | providers × tiers |
| `AdminConsole.tsx:256` / `:262` | Access-review campaigns / Break-glass grants | registers | audit volume |

The **movement ledger** is the sharpest case: an append-only stock ledger has no natural size, and the screen offers no way to reach an entry that is not near the top.

*Fix:* migrate to `DataTableView` + `useTableQuery`. For most of these the change is mechanical — the columns already exist, `useTableQuery` needs `searchText` and a filter spec, and paging and the empty/no-match distinction come free. Prioritise by traffic: approvals worklist, claims worklist, provider directory, identity accounts, movement ledger.

> **Do not** add `<Pagination>` to a bare `DataTable` as a shortcut. The table sorts the page it is handed; the hook sorts before paging. Half-migrating produces a table that looks sorted and is not.

---

### H4 — The bulk-intake screens show 50 errors of N and disclose neither N nor the full error file

This is H1's shape a second time, and the server side is blameless — it does everything right.

`BulkJobEngine.cs:34` defines `InlineErrorLimit = 50`. Validation and commit both return `errors.Take(InlineErrorLimit)` **alongside `errors.Count`** as a separate field (`:229`, `:378`), and when there are any errors at all the engine writes the complete list to a stored, access-controlled document (`:222-223`) with the comment *"The full error list goes to a stored, downloadable file — it names people. Only the first N come back inline."* The preview list is capped the same way (`:195`). The SPA contract carries all of it: `totalErrors` and `errorDocumentId` are both in `zBulkValidationView` / `zBulkCommitView` (`api/policyApi.ts:827-840`).

Neither screen reads `totalErrors`. `grep totalErrors screens/PolicyBulk.tsx screens/BatchIntake.tsx` returns nothing. Both render `errors` straight into a `density="compact"` bare `DataTable` (`PolicyBulk.tsx:322`, `BatchIntake.tsx:364`) and stop. The `errorDocumentId` hint is rendered — but only on the *reconciliation* tab (`PolicyBulk.tsx:370`, `BatchIntake.tsx:413`), not beside the truncated table where the operator is actually looking at it.

So an operator uploads a 10,000-row file, sees exactly 50 error rows with no count and no scroll cue, and is not told that 2,950 more exist or that a complete report was already generated for them. The natural reading of a 50-row list with nothing after it is "these are the errors" — and fixing those 50 and re-uploading will fail again.

*Fix:* render `totalErrors` above the table ("Showing 50 of 3,000") and surface the error-document link at the point of truncation. A pager is **not** the answer here — 50 rows is a deliberate privacy cap, not a page — which is why this is a disclosure fix, not a `DataTableView` migration. Search and a filter-by-reason group over the 50 would still help, and `useTableQuery`'s faceted counts would give the per-reason breakdown these screens lack.

> *Correction:* an earlier draft of this audit stated these tables were unbounded and could render thousands of rows. That was wrong — the cap is real and well-designed. The defect is that the screens consume the capped list without the two fields that make the cap honest.

---

## 3. Medium findings

### M1 — 65 tables across 28 screens have zero sortable columns

125 of 368 column definitions carry `sortable: true` (34%), and they cluster: `ProfileSectionViews` alone accounts for 64. These 28 screens have not one, across 65 tables:

`AccessAdmin` (5) · `AdminConsole` (9) · `PolicyBook` (5) · `ApprovalEngineAdmin` · `ApprovalsExtra` (2) · `ApprovalsRegister` (2) · `ApprovalsWorklist` · `BatchIntake` (2) · `BranchInventory` (2) · `BranchLicences` (3) · `BranchRoster` (2) · `BranchesOverview` · `CaseManager` (3) · `ClaimsPortal` (3) · `FinancePortal` (3) · `LabQueue` · `NetworkPortal` (3) · `NetworkTierAdmin` (2) · `Notifications` · `PharmacyDispense` · `PolicyAnalytics` · `PolicyBulk` (2) · `PolicyProductAdmin` (2) · `ProcedureCentre` (2) · `ProgramAdmin` (2) · `ReceptionDesk` · `ReportView` · `Substitutions` (2)

Sorting a bare `DataTable` costs only `sortable: true` plus `sortValue` per column — the component sorts itself and shares its comparator with `useTableQuery`, so turning paging on later cannot change the order (`DataTable.tsx:151-158`).

Not every column should be sortable; a status chip column often should not. But "the oldest item in this queue" is the question every worklist above is opened to answer, and none of them can be asked it.

### M2 — *(promoted to H4 — see §2)*

### M3 — Two screens hand-roll the toolbar `useTableQuery` already provides

`screens/ReceptionDesk.tsx:270-320` and `screens/CallCentreAppointments.tsx` build `<TableToolbar>` with their own `useState` filter values, their own `visible(rows)` predicate, and their own empty-vs-no-match branch (`ReceptionDesk.tsx:338-341`, with a good comment explaining why the distinction matters).

Both get the empty-vs-no-match distinction right (`ReceptionDesk.tsx:338`, `CallCentreAppointments.tsx:221`) — the code is careful and correct. This is not a defect, it is a duplicate. It reimplements what `useTableQuery` centralises, and having done so, still ends at a bare `DataTable` with no search and no pager (`CallCentreAppointments` sorts on one column of six; `ReceptionDesk` on none). It also forgoes faceted counts on the filter chips and session persistence of the query.

*Fix:* express the same filters as `TableFilterSpec` and pass the query to `DataTableView`. The custom date-range `extra` is already supported (`useTableQuery.ts:32`) — that hook parameter exists *because of* this board.

### M4 — Icon usage is inconsistent for the same action

80 of 317 buttons carry an icon (25%). The problem is not the ratio, it is that the same action is iconned in one place and bare in another:

| Action | With icon | Without |
|---|---|---|
| Add | `ApprovalEngineAdmin:243`, `BeneficiaryDocuments:162` (`plus`) | `PractitionerAdmin:575`, `:628` |
| Create | `ApprovalsExtra:122` (`plus`) | `NetworkPortal:235`, `NetworkTierAdmin:155`, `PractitionerAdmin:433` |
| Save | `ApprovalEngineAdmin:602`, `MasterListAdmin:409` (`check2`) | 8 others incl. `AccessAdmin:391`, `PolicyPanels:338` |
| Submit | `ResultUpload:91`, `InvestigationOrderPage:712`, `PrescriptionPage:961` (`check2`) | `DoctorEncounter:1447`, `InvestigationWorkspace:820`, `PrescribingWorkspace:1155` |
| Confirm | `ApprovalsExtra:168`, `ProgramAdmin:303` (`check2`) | 5 others |
| Open profile | `MemberAdmin:875` (`user`) | `BeneficiaryPortal:786` |
| Search | `Substitutions:78` (`search`) | `LabQueue:209`, `PharmacyDispense:206`, `ProcedureCentre:188`, `ReceptionBooking:263` |

An icon that appears on two of ten Save buttons carries no information; it reads as an inconsistency rather than as a cue. Either the action class gets the glyph everywhere or nowhere.

*Fix:* decide per action class, then apply uniformly. The defensible line is **icons on actions that recur across screens in the same meaning** (Add/Create → `plus`, Save/Submit/Confirm → `check2`, Search → `search`, Edit → `pen`, Export → `download`) and **no icon on one-off contextual verbs**. `Icon.tsx` already documents the governing rule — one glyph, one meaning, never reused on the same surface.

### M5 — "Upload" is labelled with the download glyph

`screens/BatchIntake.tsx:304` and `screens/PolicyBulk.tsx:267` render the file-upload action as `leadingIcon={<Icon name="download" />}`. `download` is `<path d="M12 3v12"/><path d="m7 11 5 5 5-5"/>` — an arrow pointing **down** into a tray (`Icon.tsx:32`), and its sibling comment pairs it with `eye` precisely to distinguish *taking a copy* from *looking*. On the two screens whose entire purpose is sending a CSV to the server, the arrow points the wrong way.

There is no `upload` glyph in the family, which is why this happened.

*Fix:* add `upload` to `iconPaths` as `download` mirrored (`<path d="M12 21V9"/><path d="m7 13 5-5 5 5"/><path d="M5 3h14"/>`), with a doc comment stating the pairing, and use it at both sites.

### M6 — Dialog dismiss weight splits 20 / 6

Footer order is universally correct — dismiss first, commit second, in all 26 dialogs. The dismiss button's *variant* is not:

- **`ghost`** in 20: `AccessAdmin:388`, `DoctorEncounter:824/1167/1444`, `PolicyPanels:334/451`, `MemberAdmin:1503`, `ProgramAdmin:189/300`, `ProfileSectionViews:945`, `BeneficiaryDocuments:336`, `MemberClinicalPanel:222/326`, `AppShell:323`, `ConfirmAction:78`, `RegistrationApprovals:689`, `PrescribingWorkspace:1183`, `InvestigationWorkspace:845`, `BeneficiaryStatusDialog:143`
- **`secondary`** in 6: `ServiceHistoryModal:141`, `EditAppointment:192`, `appointmentColumns:205`, `TransactionActionsDialog:280`, `CallCentreCancel:174`, `CallCentre:697`

The plausible rule — *"give the safe option more weight when the other option is destructive"* — does not hold: of the seven dialogs whose commit button is `danger`, four use `secondary` and three use `ghost`.

*Fix:* pick one. `ghost` is the majority and is what `ConfirmAction`, the shared confirm dialog, already uses.

### M7 — Row-action buttons vary in variant and size within the same context

42 buttons live inside table column `cell` renderers. Across them: `secondary` 14, `ghost` 13, `primary` 9, `danger` 1, plus 4 conditional. Sizes split **`sm` 29 / `md` 13** — a full-size button inside a dense worklist row, on 13 sites, next to `sm` buttons doing comparable work elsewhere.

*Fix:* one rule for row actions — `size="sm"`, `variant="ghost"` for the neutral action, `variant="danger"` for the destructive one — and let `stickyEnd` (see L4) keep them reachable.

### M8 — Three screens signal the selected row by button colour alone

`CaseManager.tsx:76`, `FinancePortal.tsx:122` and `PharmacyDispense.tsx:175` all render `variant={selected === r.id ? "primary" : "secondary"}` on a per-row button to indicate which row the detail pane is showing.

`DataTable` already owns this: `interactive` + `selectedKey` gives a 4px accent left-bar, a row tint, and `aria-selected` inside a `role="grid"` (`DataTable.tsx:109-112, 330-333`). Substituting a button-colour swap gives a screen-reader user nothing, and conveys state by hue alone — against the project's own rule that status must be *hue + icon + shape + text* (`CLAUDE.md`, accessibility). `CaseManager` and `FinancePortal` have `interactive` set on their tables already, so they render two cues for one state; `PharmacyDispense` renders only the colour.

*Fix:* rely on the table's selection treatment; the row button becomes a plain, constant-variant action.

---

## 4. Low findings

### L1 — Eight `leadingIcon` sites pass a size that is discarded

Eight sites write `leadingIcon={<Icon name="…" width={16} height={16} />}`; twenty-plus write `<Icon name="…" />` bare. Both render at **17px**, because `.mrs-btn svg { width: 17px; height: 17px }` (`components.css:39`) is an author rule and beats the SVG presentation attribute. Icon scale inside buttons is therefore already uniform and correct — the inline sizes are dead code that reads as if it were load-bearing.

*Fix:* drop the inline `width`/`height` from the eight; the CSS is the single source.

### L2a — `ConfirmAction` had zero call sites *(found during the H2 fix)*

`screens/ConfirmAction.tsx` is a 109-line shared confirmation dialog with an unusually careful rationale — why not `window.confirm` (untranslatable, single-line, blocks the thread), and why typed confirmation for the most dangerous actions ("a yes/no dialog in front of a repetitive task becomes muscle memory within a shift"). **Nothing imported it.** `grep ConfirmAction` matched only its own definition.

Its doc comment names five actions it was built for. Checked: none is unguarded — dispensing, consuming, rejecting and break-glass are all reached through multi-field forms or dedicated workspaces, which is a legitimate guard and arguably a better one than a dialog. So this is dead code, not five open holes. But the `requireText` typed-confirmation path — the one thing in the app designed for the genuinely irreversible — has never run.

Fixed as part of H2, which made it the component's first caller.

### L2 — `variant="warn"` has zero call sites

`Button` ships it and `components.css:124` styles it (`--st-warn-fg` text and border). Nothing uses it. Either there is a real reversible-but-consequential class of action that should be wearing it — plausibly the three actions in **H2**, if they are judged reversible — or it should be removed so the palette does not offer a level nobody has defined.

### L3 — Four parallel table skins

Beside the design system's `.mrs-wl`, `app.css` defines `.pol-grid` / `.pol-costshare` (`:1347`), `.mini-table` (`:570`) and `.rx-dispense-table` (`:4851`), used by 22 hand-written `<table>` elements in 14 files.

Header **typography** has already been unified across all four via the `--tbl-head-*` tokens, and the comments record that work. What still differs:

| | `.mrs-wl` | `.pol-grid` | `.mini-table` | `.rx-dispense-table` |
|---|---|---|---|---|
| Sticky header | **yes** (`z-index: 5`) | no¹ | no | no |
| Cell padding | 14 / 16px | 8 / 12px | 8 / 12px | `--sp3` |
| `aria-sort` | yes | no | no | no |
| Loading / empty / error | yes | no | no | no |
| Numeric alignment | `.mrs-num` | none | none | `.rx-num` |

¹ except `.bulk-columns > .pol-costshare`, whose wrapper owns the scroll and the sticky header.

Several of these are legitimate non-tables — `.mini-table` in `ExecutiveDashboard.tsx:83` and `PolicyPanels.tsx:178` is a chart's accessible data equivalent, and `.pol-costshare` is largely a spec sheet of editable inputs.

**One** is unambiguously a data table built by hand: the family roster (`MemberAdmin.tsx:1226`), complete with its own `<caption class="sr-only">`, `<th scope="col">` row and focusable scroll wrapper — the DS pattern, reimplemented — and differing from a real one only in sitting at `.pol-grid`'s 8/12px cell padding while every other table in the product sits at 14/16px.

> *Correction:* the first draft named **three**, adding member coverage (`MemberAdmin.tsx:1562`) and plan benefit categories (`PolicyProductAdmin.tsx:485`). Reading them properly, neither is a data table. Both expand a row into a `.pol-grid-sub` detail panel, which `DataTable` has no concept of; and every cell of the plan-categories grid is a checkbox, a select or a number field, which makes it a form laid out in columns rather than a list of values. Migrating either would have meant building row expansion into the design system to serve two screens. They stay, and `app.css` now says why.

*Fix:* move the roster to `DataTable`. Leave the chart alternatives, the input grids and the two master/detail grids where they are; document in `app.css` what each skin is for, so none of them accretes a data table.

### L4 — `stickyEnd` used on 3 of 7 actions columns

`Column.stickyEnd` pins the trailing column so the buttons stay reachable while wide columns scroll under them. It is used at `RegistrationApprovals.tsx:414`, `ProfileSectionViews.tsx:785` and `MasterListAdmin.tsx:180`. Four other `key: "actions"` columns, and the ~20 other action columns under different keys, do not have it — so on a wide worklist the operator scrolls sideways to reach the control they came for, once per row. That is the exact scenario `DataTable.tsx:26-29` describes.

### L5 — Eleven quantity columns align figures by hand instead of with `numeric`

`Column.numeric` right-aligns the cell **and its header** and sets tabular figures. `DataTable.tsx:39-42` records that this was already fixed once for money — and indeed **0 money/percent columns** are now missing it. The residue is counts and quantities:

| Site | Column |
|---|---|
| `FinancePortal.tsx:78` / `:79` | `authorizedQty` / `deliveredQty` — side by side, meant to be compared |
| `FinancePortal.tsx:158` | `deliveredQty` |
| `AdminConsole.tsx:176` | scope count |
| `ApprovalsExtra.tsx:50` / `:53` | count / breaches |
| `ClaimsPortal.tsx:147` | count |
| `ApprovalsRegister.tsx:179` | quantity |
| `PharmacyDispense.tsx:159` | line count |
| `ApprovalsWorklist.tsx:130` | estimated cost |
| `ProgramAdmin.tsx:250` / `:255` | cap / current usage — a comparison pair |

Each wraps the value in `<span className="tnum">` inside a start-aligned cell — equal-width digits in a ragged column, which is the precise failure mode the doc comment describes. Authorized-vs-delivered quantity is a column you read by scanning down.

`ApprovalsExtra` is the tell: four adjacent metric columns in one list, where `avg` and `p95` are `numeric` and the `count` and `breaches` either side of them are `.tnum` spans — so two of four sit at a different edge from their neighbours.

> *Correction:* the first draft of this section said **seven**. The detector behind it filtered on a keyword list (`money`, `number`, `Qty`, `Count`…) that `maxValue`, `breaches`, `quantity`, `lines.length` and `estimatedCost` do not match, so it under-reported. Re-scanning every `.tnum` column without the filter and judging them by hand found eleven. `LabQueue.tsx:159` (`3/5` panel progress) was examined and deliberately left: it is a ratio label, and end-aligning it would line up the totals rather than the figures.

### L6 — One button in the app is unstyled

Every raw `<button>` in the SPA carries a class of its own — 36 of them, all genuine custom controls (combobox options, picker rows, shell chrome, the time-slot grid, the sort headers `DataTable` renders). One does not:

```tsx
// screens/CallCentre.tsx:849 — inside <p role="alert" className="cc-error">
<button type="button" onClick={() => { setFailed(false); … }}>{t(L.retry)}</button>
```

`.cc-error` styles only the paragraph (`app.css:1116`), so this renders as a browser-default grey chrome button — the only one in the product. It is also the retry control on a failed load, i.e. the one button on that screen an operator definitely has to press.

*Fix:* `<Button variant="secondary" size="sm">`. The surrounding `role="alert"` and the empty-vs-error distinction above it are already right.

---

## 5. What is already right

Recording this so the fix effort does not "fix" it:

- **Money alignment is complete** — zero money or percent columns are missing `numeric`.
- **Icon-only buttons are accessible** — exactly one exists (`TransactionActionsDialog.tsx:347`) and it has an `aria-label`.
- **Dialog footer order** is dismiss-then-commit in all 26 dialogs.
- **Button sizing meets the target rule** — `.mrs-btn.mrs-sm` was raised to a 44px min-height precisely because `sm` is used in dense rows (`components.css:52-60`).
- **Header typography is already token-unified** across all four table skins.
- **Conditional danger is used well** where it is used — 11 sites compute `danger` from the decision (`ApprovalsWorklist.tsx:430`, `MemberAdmin.tsx:1875`, `BeneficiaryStatusDialog.tsx:149`, `ProgramAdmin.tsx:192`), so the button turns red exactly when the action turns destructive.
- **`ProfileSectionViews` is the sortability exemplar** — 64 sortable columns, every section table fully sortable and compact.
- **`MemberAdmin` is the paging exemplar** — server-side sort, real pager, truncation disclosed.
- **`ReceptionDesk`'s hand-rolled toolbar is good code** — it gets the empty-vs-no-match distinction right, which is why M3 is a consolidation, not a bug.

---

## 6. Suggested fix order

| # | Work | Findings | Size |
|---|---|---|---|
| 1 | `PolicyBook` paging + truncation notice; bulk-intake `totalErrors` + error-file link | H1, H4 | S |
| 2 | `danger` + confirmation on the three unguarded revokes | H2 | S |
| 3 | Add `upload` glyph; fix the two wrong-direction icons | M5 | XS |
| 4 | `numeric: true` on the seven quantity columns; drop the eight dead icon sizes; style the `CallCentre` retry button | L5, L1, L6 | XS |
| 5 | Settle the icon policy per action class and apply it | M4 | M |
| 6 | Settle dismiss weight (`ghost`) and the row-action rule; fix selection-by-colour | M6, M7, M8 | S |
| 7 | `sortable` + `sortValue` across the 28 zero-sort screens | M1 | M |
| 8 | Migrate the H3 queues to `DataTableView`, highest-traffic first | H3 | L |
| 9 | Consolidate `ReceptionDesk` / `CallCentreAppointments` onto `useTableQuery` | M3 | M |
| 10 | Move the three hand-rolled data tables to `DataTable`; document the remaining skins | L3 | M |
| 11 | `stickyEnd` on the remaining actions columns | L4 | S |
| 12 | Decide `warn`: adopt or remove | L2 | XS |

### Making it stick

Steps 1-11 fix the instances. The reason there were 106 instances to fix is that nothing detected a new one.

**Done.** Eight static guards now read the SPA's own source and fail on the shape of each defect rather than on its instances:

| Guard | Holds |
|---|---|
| `apps/web/test/queue-table-view.test.tsx` | an operational queue uses `DataTableView`; a bare table needs a stated reason (H3) |
| `apps/web/test/table-truncation.test.tsx` | a page showing a SUBSET says so — pager, total, truncation notice (H1, H4) |
| `apps/web/test/table-sortable.test.tsx` | a sortable header actually sorts; controlled and self-sorting stay distinct (M1) |
| `apps/web/test/table-numeric-columns.test.ts` | a column of magnitudes is aligned by the COLUMN, not by a span (L5) |
| `apps/web/test/destructive-actions.test.tsx` | a destructive write is `danger` and confirmed; no button ships unstyled (H2, L6) |
| `apps/web/test/button-icon-policy.test.ts` | one glyph per action class; no variant offered that nothing uses (M4, L2) |
| `apps/web/test/button-context-rules.test.ts` | row-action size, dismiss weight beside `danger`, no selection-by-hue (M6, M7, M8) |
| `apps/design-system/test/icons.test.ts` | one glyph per meaning; no two names drawing the same path (M5) |

Each was verified non-vacuous by reintroducing the defect it is about, and several found sites the manual pass had missed — `MemberAdmin`'s `P.edit`/`P.save`, a fourth selection-by-colour site, four columns the `numeric` detector's keyword filter had skipped, and a `PractitionerAdmin` header that had offered a sort since it was written and never performed one.

**The gate is about deletion, not violation.** All eight run inside the ordinary suites, so breaking one is already loud. Removing one is not: the suite goes green with one fewer file, which looks exactly like a good day — the same hole `openapi-drift` fell through when it sat red for a day because nothing said it should be *running*. So `tools/ci/check-design-guards.py` names them with the standard each holds, fails if one is gone, then runs them; it is registered in `REQUIRED_GATES` so its own silence alarms too.

---

## Appendix A — Full table inventory (116)

Context key: **Q** = operational queue/registry, grows with use · **D** = per-entity detail, bounded by one record · **C** = configuration/reference set, bounded by design · **R** = result of an explicit run (search, validation, drill-down)

### A.1 — `DataTableView` (10) — conforming

| Site | Table |
|---|---|
| `ClinicianWorklists.tsx:353` | My Patients |
| `ClinicianWorklists.tsx:605` | Orders |
| `ClinicianWorklists.tsx:718` | Results Inbox |
| `ClinicianWorklists.tsx:843` | Prescriptions |
| `DoctorEncounter.tsx:437` | My patients |
| `DoctorEncounter.tsx:1678` | Prescriptions for this patient |
| `DoctorEncounter.tsx:1905` | Orders (lab/imaging) |
| `DoctorVisits.tsx:253` | My Visits |
| `RegistrationApprovals.tsx:531` | Registration Approvals — the only multi-select table |
| `ReportAccessInbox.tsx:204` | Result Access Requests |

### A.2 — Bare `DataTable` (84)

| Site | Table | Ctx | Sortable | Gap |
|---|---|---|---|---|
| `AccessAdmin.tsx:207` | Users & Access | Q | 0/7 | sort, pager (server search only) |
| `AccessAdmin.tsx:295` | Roles | D | 0/7 | sort |
| `AccessAdmin.tsx:376` | Exceptions | D | 0/7 | sort |
| `AccessAdmin.tsx:487` | Branch reach | D | 0/7 | sort |
| `AccessAdmin.tsx:534` | Sessions | D | 0/7 | sort |
| `AdminConsole.tsx:146` | Accounts (identity store) | Q | 0/11 | **all four** |
| `AdminConsole.tsx:152` | Role bindings & recertification | Q | 0/6 | **all four** |
| `AdminConsole.tsx:190` | Segregation-of-duties conflicts | C | 0/3 | sort |
| `AdminConsole.tsx:197` | Role → scope matrix | C | 0/6 | sort |
| `AdminConsole.tsx:221` | Tenants | C | 0/3 | sort |
| `AdminConsole.tsx:256` | Access-review campaigns | Q | 0/9 | search, sort, pager |
| `AdminConsole.tsx:262` | Break-glass grants | Q | 0/5 | search, sort, pager |
| `AdminConsole.tsx:288` | Master Data versions | C | 0/3 | sort |
| `AdminConsole.tsx:312` | System Config | C | 0/3 | sort, search |
| `ApprovalEngineAdmin.tsx:375` | Approvals engine rules | C | 0/5 | sort |
| `ApprovalsExtra.tsx:68` | SLA / TAT board | C | 0/5 | — |
| `ApprovalsExtra.tsx:185` | Emergency / Override | Q | 0/5 | search, sort, pager |
| `ApprovalsRegister.tsx:127` | Authorizations | Q | 0/6 | **all four** |
| `ApprovalsRegister.tsx:197` | What was delivered | D | 0/6 | sort |
| `ApprovalsWorklist.tsx:153` | Approval Worklist | Q | 0/7 | **all four** |
| `BatchIntake.tsx:364` | Errors | R | — | **H4** — capped at 50, total undisclosed |
| `BatchIntake.tsx:381` | What this file would do | R | — | **H4** — capped at 50, total undisclosed |
| `BranchInventory.tsx:176` | Stock | Q | 0/12 | **all four** |
| `BranchInventory.tsx:199` | Movement ledger | Q | 0/5 | **all four** — append-only |
| `BranchLicences.tsx:124` | Practitioners | Q | 0/7 | search, sort, pager |
| `BranchLicences.tsx:260` | Licence alerts | Q | — | sort, filter |
| `BranchLicences.tsx:275` | Appointments needing reassignment | Q | — | sort, pager |
| `BranchRoster.tsx:134` | Exceptions | C | 0/5 | sort |
| `BranchRoster.tsx:248` | Affected appointments | R | — | sort |
| `BranchesOverview.tsx:119` | Branches Overview | C | 0/6 | sort |
| `CallCentreAppointments.tsx:227` | Appointments | Q | 1/6 | search, pager (M3) |
| `CaseManager.tsx:92` | My Cases | Q | 0/6 | **all four** |
| `CaseManager.tsx:209` | Coordination tasks | Q | 0/6 | sort, filter, pager |
| `CaseManager.tsx:233` | Escalations | Q | 0/6 | sort, filter, pager |
| `ClaimsPortal.tsx:91` | Claims Worklist | Q | 0/7 | **all four** |
| `ClaimsPortal.tsx:132` | Reconciliation | Q | 0/7 | **all four** |
| `ClaimsPortal.tsx:169` | Top denial reasons | C | — | — |
| `FinancePortal.tsx:89` | Utilization | Q | 0/7 | sort, pager · `numeric` (L5) |
| `FinancePortal.tsx:136` | Provider Settlements | Q | 0/7 | sort, pager · selection-by-colour (M8) |
| `FinancePortal.tsx:164` | Settlement lines | D | 0/7 | sort · `numeric` (L5) |
| `LabQueue.tsx:224` | Lab / Imaging results | R | — | sort |
| `MasterListAdmin.tsx:223` | In force | Q | 3/7 | pager |
| `MemberAdmin.tsx:460` | Members | Q | server | — *(exemplar)* |
| `NetworkPortal.tsx:71` | Providers Directory | Q | 0 † | **all four** — whole network |
| `NetworkPortal.tsx:164` | Contracts & coverage | Q | 0 † | **all four** |
| `NetworkPortal.tsx:186` | Locations & users | Q | 0 † | **all four** |
| `NetworkTierAdmin.tsx:120` | Network Tiers | C | — | sort |
| `NetworkTierAdmin.tsx:182` | Assignments | Q | — | sort, pager · **H2** |
| `Notifications.tsx:113` | Notifications | Q | — | **all four**, multi-select |
| `PharmacyDispense.tsx:226` | Dispense | R | 0/5 | sort · selection-by-colour (M8) |
| `PolicyAnalytics.tsx:671` | Members in this band | R | — | sort, pager |
| `PolicyBook.tsx:115` | Policies | Q | — | **H1** |
| `PolicyBook.tsx:188` | Plans | C | — | sort |
| `PolicyBook.tsx:229` | Groups | C | — | sort |
| `PolicyBook.tsx:331` | Members over threshold | R | — | sort, pager |
| `PolicyBook.tsx:515` | Groups | C | — | sort |
| `PolicyBulk.tsx:322` | Errors | R | — | **H4** — capped at 50, total undisclosed |
| `PolicyBulk.tsx:338` | What this file would change | R | — | **H4** — capped at 50, total undisclosed |
| `PolicyProductAdmin.tsx:121` | Payers | C | — | sort |
| `PolicyProductAdmin.tsx:206` | Plans & Versions | C | — | sort |
| `PractitionerAdmin.tsx:316` | Current clinicians | Q | 1/6 | search, pager |
| `ProcedureCentre.tsx:201` | Verify & deliver | R | — | sort |
| `ProcedureCentre.tsx:214` | Our queue | Q | — | search, sort, pager |
| `ProfileSectionViews.tsx:551` | Benefit limits by category | D | 5/5 | — |
| `ProfileSectionViews.tsx:661` | Recorded conditions | D | 5/5 | — |
| `ProfileSectionViews.tsx:817` | Encounters | D | 5/5 | pager (grows over years) |
| `ProfileSectionViews.tsx:1199` | Investigation orders and results | D | 5/5 | pager |
| `ProfileSectionViews.tsx:1262` | Prescriptions and dispensing | D | 5/5 | pager |
| `ProfileSectionViews.tsx:1328` | Authorization requests | D | 5/5 | pager |
| `ProfileSectionViews.tsx:1374` | Referrals | D | 5/5 | — |
| `ProfileSectionViews.tsx:1419` | Documents | D | 5/5 | — |
| `ProfileSectionViews.tsx:1568` | Claims | D | 5/5 | pager |
| `ProfileSectionViews.tsx:1631` | Assigned cases | D | 10/10 | — |
| `ProfileSectionViews.tsx:1637` | Coordination tasks | D | 6/6 | — |
| `ProfileSectionViews.tsx:1643` | Escalations | D | 3/3 | — |
| `ProfileSectionViews.tsx:1696` | Change and access history | D | 5/5 | pager (append-only) |
| `ProgramAdmin.tsx:183` | Programmes | C | 0/4 | sort |
| `ProgramAdmin.tsx:294` | Capacity | C | 0/4 | sort · `numeric` (L5) |
| `ReceptionDashboard.tsx:298` | Visits | Q | 3/5 | search, pager |
| `ReceptionDesk.tsx:343` | Appointments | Q | 0/6 | search, sort, pager (M3) |
| `ReportView.tsx:47` | Report tables | R | — | sort |
| `ServiceHistoryModal.tsx:179` | Previous occurrences | D | 1/5 | — |
| `Substitutions.tsx:89` | Search a medication | R | — | sort |
| `Substitutions.tsx:102` | Approved alternatives | C | — | sort |

`—` = columns built inline or by a helper, so the extractor could not attribute them to one table; the file's total is quoted in M1 instead. `†` = `NetworkPortal`'s 14 column definitions are shared across its three tables by `directoryColumns(t)` and friends; none carries `sortable`.

### A.3 — Raw `<table>` markup (22 in 14 files)

| Site | Class | Verdict |
|---|---|---|
| `MemberAdmin.tsx:1226` | `pol-grid` | **data table** — migrate (L3) |
| `MemberAdmin.tsx:1562` | `pol-grid` | **data table** — migrate (L3) |
| `PolicyProductAdmin.tsx:485` | `pol-grid` | **data table** — migrate (L3) |
| `MemberAdmin.tsx:1621, 1945, 1982, 2023` | `pol-costshare` | input grid — keep |
| `PolicyProductAdmin.tsx:600` | `pol-costshare` | input grid — keep |
| `PolicyAnalytics.tsx:594` | `pol-costshare` | input grid — keep |
| `PolicyBulk.tsx:358`, `BatchIntake.tsx:401` | `pol-costshare` | contract preview — keep |
| `BulkTemplateActions.tsx:150` | `pol-costshare` | template spec — keep |
| `ExecutiveDashboard.tsx:83` | `mini-table` | chart alternative — keep |
| `PolicyPanels.tsx:178` | `mini-table sr-only` | chart alternative — keep |
| `EffectiveAccessPreview.tsx:68` | `mini-table` | matrix preview — keep |
| `FinancePortal.tsx:204` | `mini-table` | summary — keep |
| `MasterListAdmin.tsx:384` | `mini-table md-diff` | diff view — keep |
| `PrescribingWorkspace.tsx:884` | `mini-table` | summary — keep |
| `InvestigationOrderPage.tsx:426, 595` | `rx-dispense-table` / bare | counter grid — keep, review bare `<table>` at 595 |
| `PrescriptionPage.tsx:589, 831` | `rx-dispense-table` / bare | counter grid — keep, review bare `<table>` at 831 |

---

## Appendix B — Button inventory (354)

### B.1 — Distribution

| Variant | Count | | Size | Count | | Icon | Count |
|---|---|---|---|---|---|---|---|
| `ghost` | 107 | | `md` | 209 | | none | 237 |
| `secondary` | 91 | | `sm` | 108 | | `check2` | 14 |
| `primary` | 86 | | `lg` | 0 | | `plus` | 12 |
| `danger` | 17 | | | | | `user` | 8 |
| conditional | 16 | | | | | `chevron` | 8 |
| `warn` | **0** | | | | | `doc` | 7 |
| raw `<button>` | 37 | | | | | `cross` | 5 |

Remaining glyphs, one to three uses each: `pen` 3, `download` 3, `undo` 2, `toggle` 2, `lock` 2, `eye` 2, `clock` 2, `users` 1, `swap` 1, `stethoscope` 1, `search` 1, `ok` 1, `moon` 1, `folder` 1, `calendar` 1.

### B.2 — Buttons by context

| Context | Count | State |
|---|---|---|
| Dialog footers | 52 (26 dialogs) | order correct; dismiss weight split 20/6 (M6) |
| Table row actions | 42 | 4 variants, 2 sizes (M7); 3 use colour for selection (M8) |
| Page-header / toolbar actions | ~60 | icon coverage uneven (M4) |
| Form submits | ~40 | Save/Submit icon split (M4) |
| Inline / list-item actions | ~120 | 3 unguarded destructive (H2) |
| Raw `<button>` | 37 | each reviewed: 36 are custom controls with their own class (combobox options, picker rows, shell chrome, time-slot grid, sort headers) — legitimate. **1 has no class at all** and is a plain action: `CallCentre.tsx:849` (L6) |

### B.3 — Destructive-action register

| Site | Action | Variant | Confirm | Verdict |
|---|---|---|---|---|
| `AccessAdmin.tsx:523` / `:545` | revoke session | `danger` | modal | ✅ reference implementation |
| `MemberAdmin.tsx:972` | terminate membership | `danger` + `cross` | modal | ✅ |
| `MemberAdmin.tsx:1875` | confirm (terminate) | conditional `danger` | — | ✅ |
| `CallCentre.tsx:702`, `CallCentreCancel.tsx:179` | cancel appointment | `danger` | modal | ✅ |
| `ApprovalsWorklist.tsx:278` / `:430` | reject | `danger` / conditional | — | ✅ |
| `TransactionActionsDialog.tsx:283` | amend / withdraw | `danger` | modal | ✅ |
| `AmendLineDialog.tsx:235` | confirm amend | `danger` | modal | ✅ |
| `OrderDetailModal.tsx:265`, `PrescriptionDetailModal.tsx:344` | withdraw | `danger` | — | ✅ |
| `InvestigationWorkspace.tsx:814/848`, `PrescribingWorkspace.tsx:1149/1186` | discard draft | `danger` | modal | ✅ |
| `BeneficiaryStatusDialog.tsx:149`, `ProgramAdmin.tsx:192` | conditional destructive | conditional `danger` | — | ✅ |
| **`NetworkTierAdmin.tsx:201`** | **revoke tier assignment** | **`ghost`** | **none** | ❌ **H2** |
| **`PractitionerAdmin.tsx:552`** | **revoke specialty** | **`ghost`** | **none** | ❌ **H2** |
| **`PractitionerAdmin.tsx:607`** | **revoke clinic** | **`ghost`** | **none** | ❌ **H2** |

---

*Extraction scripts used for this audit are throwaway; the destructive-action matcher in §6 is the one worth keeping as a CI gate.*

# 12 — UI Wireframes

> Back to [00-README-INDEX.md](00-README-INDEX.md) · [0A-DESIGN-FOUNDATIONS.md](0A-DESIGN-FOUNDATIONS.md)
> Siblings: [09-information-architecture.md](09-information-architecture.md) · [13-ux-flows.md](13-ux-flows.md) · [14-navigation-structure.md](14-navigation-structure.md) · [21-accessibility-checklist.md](21-accessibility-checklist.md) · [11-permission-matrix.md](11-permission-matrix.md)

Low-fidelity, **annotated wireframes** for the highest-value screens across portals. Rendered as ASCII/box-drawing layouts (no image tools). Each screen documents: **layout**, **key components**, **states** (loading / empty / error / success), **accessibility annotations**, and **responsive notes**.

**Reading the wireframes**
- Boxes are containers; `[ Button ]` interactive; `▾` menu; `🔔` notifications; `▸` breadcrumb separator.
- Status chips show the **redundant encoding** from [0A §5.2](0A-DESIGN-FOUNDATIONS.md#52-status-color-tokens--never-color-only): `{color + icon + shape + text}`, e.g. `[✓ Eligible]` (Success pill), `[⧗ Pending]` (Info dashed), `[◐ Partial]`, `[△ Expiring]`, `[✕ Rejected]` (Danger square), `[○ Inactive]` (Neutral ghost).
- `⟵RTL` notes mark where layout mirrors for Arabic.
- All targets ≥ 44×44px; focus ring 3px `#007A7A` (`--mersal-teal-700`), never removed.

---

## 0. Shared shell (all portals)

```
+--------------------------------------------------------------------------+
| [≡] Mersal ● {Portal Name}        [ 🔍 Search…        ]  [AR|EN] 🔔³ [User▾]|
+------------------+-------------------------------------------------------+
|  PRIMARY NAV     |  Home ▸ {Section} ▸ {Entity}                          |
|  (role-aware)    | .....................................................|
|  ▸ Home          |                                                       |
|  ▸ Queue/Work    |   << CONTENT AREA >>                                  |
|  ▸ Search        |                                                       |
|  ▸ Reports*      |                                                       |
|  ▸ Settings*     |  [ Contextual action bar aligned to primary task ]   |
+------------------+-------------------------------------------------------+
```
- **a11y:** landmark regions `banner` (top bar), `navigation` (primary nav), `main` (content), `contentinfo` (footer). Skip-link "Skip to main content" first in tab order. `🔔³` announces "3 unread notifications".
- **Responsive:** ≥1024px sidebar persistent; 768–1023px sidebar collapses to `[≡]` drawer; <768px bottom tab bar + hamburger (see [14](14-navigation-structure.md) §mobile).
- **⟵RTL:** nav docks right, search/user cluster docks left, breadcrumb reads right-to-left.

---

## 1. Reception — Eligibility search + result card (minimum-necessary)

### Layout
```
Home ▸ Beneficiary Search
+--------------------------------------------------------------------------+
|  Find beneficiary                                                        |
|  ( ) Member No  ( ) National ID  ( ) UNHCR  ( ) Passport  ( ) Name+DOB   |
|  [  MRS-M-__________________________ ]                 [ 🔍 Search ]      |
+--------------------------------------------------------------------------+
|  RESULT                                                                  |
|  +--------------------------------------------------------------------+  |
|  |  Ahmed K.  •  MRS-M-0032118845            Status: [✓ Active]       |  |
|  |  DOB 1990-04-xx  •  Male  •  Lang: العربية                          |  |
|  |  ------------------------------------------------------------------|  |
|  |  ELIGIBILITY (for: General Consultation)                           |  |
|  |     Result:   [✓ Eligible]                                         |  |
|  |     Coverage: Outpatient — remaining 6 of 8 visits                 |  |
|  |     Limit:    EGP 4,200 remaining this cycle                       |  |
|  |     Approval: not required                                         |  |
|  |  ------------------------------------------------------------------|  |
|  |  ⚠ Reception view — clinical/medical data is not shown here.        |  |
|  |                                    [ Start Visit ]  [ Book Appt ]   |  |
|  +--------------------------------------------------------------------+  |
+--------------------------------------------------------------------------+
```

### Key components
- Search type radio group; single smart input; primary `Search`.
- **Result card** exposes only: identity match, member no., status chip, language, and eligibility summary (result + coverage + limits + approval-needed). **No diagnoses, notes, or history** (FR-ELG-003, min-necessary banner is explicit).
- Service selector (`for: General Consultation`) drives the eligibility computation.

### States
- **Loading:** skeleton card + "Checking eligibility…" (aria-live polite).
- **Empty (no query):** "Search by member number, ID, or name to begin."
- **No match:** `[○ No match]` "No beneficiary found. Check the identifier or [Register]."
- **Not eligible:** `[✕ Not eligible]` with reason code (e.g., "Policy expired 2026-06-30") + `[ Renew / Escalate ]`.
- **Needs approval:** `[⧗ Needs approval]` + `[ Request approval ]`.
- **Stale/degraded:** `[△ Last-known eligibility (offline)]` banner (FR-ELG-009).
- **Success:** `[✓ Eligible]` and enabled `Start Visit`.

### Accessibility annotations
- Radio group labeled "Search by"; each option 44px, arrow-key navigable.
- Result status announced via `aria-live` on change: "Eligible. Outpatient, 6 of 8 visits remaining."
- Status chips carry icon+shape+text+`title` tooltip; not color-only (WCAG 1.4.1).
- Error reason associated with card via `aria-describedby`.

### Responsive
- Desktop: card ~640px wide. Mobile: full-width; actions stack; sticky action bar bottom.
- **⟵RTL:** radio row and card fields mirror; numerals per locale.

---

## 2. Doctor — Consultation / SOAP + order & prescription creation

### Layout
```
Home ▸ My Patients ▸ Ahmed K. (ENC-20260721-0142)
+-------------------+------------------------------------------------------+
| PATIENT CONTEXT   |  [ Summary ] [ SOAP ] [ Vitals ] [ Dx ] [ Orders ]   |
| Ahmed K.          |  [ Rx ] [ Referral ]                                 |
| MRS-M-0032118845  | .....................................................|
| [✓ Active]        |  SOAP NOTE                                           |
| Allergies: ⚠ Pen. |  S | [ subjective free text …                    ]    |
| Problems: HTN     |  O | [ objective … ] + Vitals summary (BP 148/92)     |
| Meds: Amlodipine  |  A | [ Assessment … ]  Dx: [+ ICD-10 search…]         |
| ----------------- |  P | [ Plan … ]                                       |
| Timeline ▾        |                                                      |
|  • 2026-05 Consult|  ORDERS (Lab/Imaging)                                |
|  • 2026-03 Lab    |   [+ Add order line]  ▸ CBC (LOINC)  [remove]         |
|                   |   ▸ Chest X-ray (CPT 71046)  needs approval [⧗]      |
|                   |                                                      |
|                   |  PRESCRIPTIONS                                       |
|                   |   [+ Add drug]  ▸ Amlodipine 5mg  1×daily  30d       |
|                   |   ⚠ Interaction check: none  • Allergy check: OK     |
+-------------------+------------------------------------------------------+
|                        [ Save draft ]   [ Sign & submit encounter ]      |
+--------------------------------------------------------------------------+
```

### Key components
- **Left context rail** (persistent, scoped to patients this doctor treats — FR-CLIN-005): status, allergies (safety-highlighted), problem/med list, collapsible timeline.
- **Tabbed workspace:** Summary / SOAP / Vitals / Diagnoses / Orders / Rx / Referral.
- **SOAP editor** with structured + free text; Assessment binds ICD-10 picker (FR-CLIN-003).
- **Orders builder:** add order lines (LOINC/CPT), per-line "needs approval" chip.
- **Rx builder:** drug (Drug Master/ATC), dose/route/freq/duration/qty; **live interaction + allergy checks** (FR-CLIN-007) shown inline.
- Footer: Save draft / Sign & submit (submitting fires order & Rx routing events).

### States
- **Loading:** rail + tabs skeleton; "Loading record…".
- **Empty tabs:** "No diagnoses yet — add from ICD-10."
- **Interaction warning:** `[△ Interaction: moderate]` expandable detail; **severe** blocks submit `[✕ Blocked — severe interaction]` until resolved/overridden with reason.
- **Allergy hit:** `[✕ Allergy: Penicillin]` prevents that drug.
- **Approval-required order:** `[⧗ Needs approval]` — submit still allowed; line held pending auth.
- **Success:** "Encounter signed. 1 order and 1 prescription submitted." toast + IDs.
- **Save error:** non-destructive banner, draft preserved locally.

### Accessibility annotations
- Tabs are ARIA `tablist`/`tab`/`tabpanel`; arrow-key switch; focus stays on activated tab.
- Allergy/interaction alerts use `role="alert"` (assertive) so SR users hear them immediately.
- ICD/drug pickers are combobox (`aria-expanded`, `aria-activedescendant`), fully keyboard-operable.
- Severe-block state exposes reason field with clear error text; "Sign" button `aria-disabled` with explanation.

### Responsive
- Tablet: context rail collapses to a top summary bar with `[Details ▾]`.
- Mobile: single column, tab bar becomes a stepper; pickers full-screen.
- **⟵RTL:** rail docks right; SOAP labels S/O/A/P mirror; numerals localized.

---

## 3. Lab — Order queue + consume & upload result

### Layout
```
Home ▸ Incoming Orders
+--------------------------------------------------------------------------+
|  Filter: [ All ][ New ][ In progress ]   Sort: [ Oldest ▾ ]  🔍[      ]  |
+--------------------------------------------------------------------------+
|  # ORD-2026-7F3K2A   Beneficiary: A.K. (min ctx)   [⧗ New]   ⏱ 12m       |
|     Lines: CBC • Fasting glucose                    [ Open ]              |
|  ------------------------------------------------------------------------|
|  # ORD-2026-9Q2M11   Beneficiary: S.M.             [◐ Partially used]     |
|     Lines: Lipid panel (1 of 2 done)               [ Open ]              |
+--------------------------------------------------------------------------+

  ORDER DETAIL (ORD-2026-7F3K2A)                                    [ ✕ ]
+--------------------------------------------------------------------------+
|  Beneficiary: Ahmed K.  • Member MRS-M-…845  (identity min-necessary)    |
|  ⚠ Lab view — prescriptions & unrelated clinical data are not shown.     |
|  --------------------------------------------------------------------    |
|  LINE 1  CBC (LOINC 58410-2)          Status [⧗ Active]                   |
|          [ Consume line ]  ← atomic claim                                |
|  LINE 2  Fasting glucose (LOINC …)    Status [✓ Consumed by you 09:14]    |
|          RESULT: [ value ____ ] unit [mg/dL]  [ Attach report .pdf ]     |
|          [ Save result ]   [ Release to clinician ]                      |
+--------------------------------------------------------------------------+
```

### Key components
- **Queue list** of incoming orders (this provider only — FR-NET-005), status chips, wait-time, filter/sort/search.
- **Order detail** with min-necessary beneficiary context + explicit "no Rx / no unrelated clinical" banner (FR-LAB-005).
- **Per-line Consume** button (atomic claim — FR-LAB-003, [FR-INV](07-functional-requirements.md#13-order--prescription-consumption-invariants-inv--first-class-frs)); once consumed shows who/when.
- **Result entry**: structured value + unit and/or document upload; Save then Release (FR-LAB-004).

### States
- **Loading:** queue skeleton rows.
- **Empty queue:** "No incoming orders. New orders appear here automatically."
- **Consume success:** line flips to `[✓ Consumed by you 09:14]`, Consume button removed.
- **Consume conflict:** `[✕ Already consumed]` "This line was just claimed elsewhere." (idempotent conflict — FR-INV-003) — no double claim.
- **Upload scanning:** "Scanning file…" then attached; **infected:** `[✕ File rejected]`.
- **Release success:** `[✓ Released]` + "Clinician notified."
- **Validation error:** value out of plausible range → inline warning, not a block unless configured.

### Accessibility annotations
- Consume is a single, clearly-labeled button per line; on activation SR announces new status via `aria-live`.
- Conflict error uses `role="alert"`; explains no action needed.
- File input has visible label + drag/drop with keyboard-accessible "Browse".
- Critical/abnormal flag (FR-LAB-010) uses `[△]` chip + text, not color alone.

### Responsive
- Mobile: queue cards stack; detail opens full-screen sheet; sticky Consume/Release.
- **⟵RTL:** list metadata and detail fields mirror.

---

## 4. Pharmacy — Dispense (partial / substitution)

### Layout
```
Home ▸ Prescription Queue ▸ RX-2026-K8213M
+--------------------------------------------------------------------------+
|  Beneficiary: Ahmed K. • Member MRS-M-…845   Coverage: [✓ Eligible]       |
|  ⚠ Pharmacy view — investigation/lab results are not shown.               |
+--------------------------------------------------------------------------+
|  Rx LINE 1  Amlodipine 5mg — 1×daily × 30 (qty 30)   [⧗ Active]           |
|    Dispense qty [ 30 ]  of 30      In stock ✔                             |
|    [ Dispense full ]                                                      |
|  ------------------------------------------------------------------------|
|  Rx LINE 2  Atorvastatin 20mg — 1×daily × 30 (qty 30) [⧗ Active]          |
|    In stock: 10 only                                                      |
|    Dispense qty [ 10 ] of 30   → will set [◐ Partially dispensed]         |
|    [ Partial dispense ]                                                   |
|    Substitute? [ Find alternative (formulary) ]                          |
|      chosen: Atorvastatin 20mg (generic)  reason [ stock ▾ ]             |
+--------------------------------------------------------------------------+
|                                   [ Confirm dispense ]  [ Flag prescriber ]|
+--------------------------------------------------------------------------+
```

### Key components
- Min-necessary beneficiary + **coverage** chip; explicit "no lab results" banner (FR-RX-007).
- Per-line **dispense quantity** with in-stock indicator; **Dispense full** / **Partial dispense** (FR-RX-004/005).
- **Substitution** within formulary with original vs. substitute + reason (FR-RX-006).
- **Confirm dispense** performs atomic consumption respecting quantity conservation & coverage decrement (FR-INV-005/006/009).
- **Flag prescriber** for clarification without dispensing (FR-RX-012).

### States
- **Loading:** line skeletons.
- **Empty queue:** "No prescriptions waiting."
- **Partial:** line → `[◐ Partially dispensed]`, remaining qty shown; order stays open.
- **Over-dispense attempt:** `[✕ Exceeds prescribed/remaining]` blocked (FR-INV-005/RX-010).
- **Out of coverage:** `[✕ Coverage exhausted]` + escalate.
- **Substitution outside formulary:** `[✕ Not in formulary]`.
- **Consume conflict / double confirm:** idempotent — second confirm returns original result, no double-dispense (FR-INV-004/009).
- **Success:** `[✓ Dispensed]` / `[◐ Partial]` toast + record.

### Accessibility annotations
- Quantity fields are numeric spinbuttons with min/max, labeled "Dispense quantity of {n}".
- Blocking errors `role="alert"`; explain remaining/limit.
- Substitution flow announces the swap ("Substituted generic; reason: stock").
- Confirm button disabled state carries reason via `aria-describedby`.

### Responsive
- Mobile: each Rx line is a card; quantity stepper large-tap; substitute opens sheet.
- **⟵RTL:** quantities and stock indicators mirror; localized numerals.

---

## 5. Approval — Reviewer worklist + decision

### Layout
```
Home ▸ Worklist
+--------------------------------------------------------------------------+
|  Filter: [ Pending ][ Info requested ][ Emergency ]  Sort: [ SLA ▾ ]     |
+--------------------------------------------------------------------------+
|  AUTH-2026-4KD21  MRI Brain     Requested 2h ago  SLA [△ 1h left]        |
|     Requester: Dr. N.           [⧗ Under review]        [ Review ]        |
|  AUTH-2026-9XB77  Chemo cycle   Emergency (provisional) [△ Retro review] |
|                                                        [ Review ]        |
+--------------------------------------------------------------------------+

  REVIEW  AUTH-2026-4KD21 — MRI Brain
+-------------------+------------------------------------------------------+
| REQUEST           |  CLINICAL CONTEXT (approval role may view EMR)       |
| Service: MRI Brain|   • Dx: Headache, r/o mass (ICD-10 R51)              |
| Cost est: EGP …   |   • Recent notes: … (SOAP)                            |
| Requester: Dr. N. |   • Prior imaging: none                              |
| Beneficiary: A.K. |   • Attached report: neuro exam.pdf [view]           |
| SLA: [△ 1h left]  | .....................................................|
|                   |  DECISION                                            |
|                   |  ( ) Approve   ( ) Partial   ( ) Reject  ( ) Info    |
|                   |  Reason * [ __________________________________ ]     |
|                   |  Coverage granted [ full ▾ ]                         |
+-------------------+------------------------------------------------------+
|              [ Submit decision ]   [ Escalate to Medical Director ]       |
+--------------------------------------------------------------------------+
```

### Key components
- **Worklist** sorted by SLA/TAT with `[△]` deadline chips; emergency items flagged for retrospective review (FR-AUTH-005).
- **Review pane:** request summary + **clinical context** (EMR/notes/reports — approval role explicitly permitted, FR-AUTH-003/FR-CLIN-013).
- **Decision control:** Approve / Partial / Reject / Info-requested, each with **mandatory reason** (FR-AUTH-004) and coverage-granted selector.
- Escalate to Medical Director (override path, FR-AUTH-006); separation-of-duties enforced (FR-AUTH-011).

### States
- **Loading:** worklist skeleton.
- **Empty:** "No pending authorizations. Nice work."
- **Missing reason:** `[✕]` inline "Reason is required for this decision."
- **Info requested:** thread preserved; requester notified; item moves to "Info requested" (FR-AUTH-010).
- **On approve:** linked order/Rx auto-unblocked (FR-AUTH-008); toast "Approved. Order released."
- **SLA breach:** `[✕ SLA breached]` red-square chip + escalation prompt.
- **Self-request block:** `[✕ You cannot approve your own request]`.

### Accessibility annotations
- Decision radios labeled; reason field required + `aria-required`, error via `aria-describedby`.
- SLA chips: icon+shape+text (`[△ 1h left]`), never color-only.
- Clinical context is a labeled region; attachments open in accessible viewer.
- Keyboard: `Review` opens pane and moves focus to first decision control.

### Responsive
- Tablet/mobile: worklist → review is a full-screen push; decision sticky at bottom.
- **⟵RTL:** two-pane order mirrors (request rail right).

---

## 6. Registration wizard (Beneficiary Management)

### Layout
```
Home ▸ Register Beneficiary
Step ①Identity ─ ②Contact/Consent ─ ③Policy ─ ④Documents ─ ⑤Review
+--------------------------------------------------------------------------+
|  ① IDENTITY                                                              |
|  First name [__________]   Last name [__________]                        |
|  DOB [____-__-__]   Sex ( )M ( )F ( )Other   Language ( )AR ( )EN         |
|  Identifiers (add 1+):                                                    |
|    Type [ UNHCR ▾ ]  Value [_______________]  Authority [____]  [+ add]   |
|  ⚠ Possible duplicate: "Ahmad K. • MRS-M-…845" — [ review ] [ not same ]  |
+--------------------------------------------------------------------------+
|  Progress ●●○○○                              [ Back ]        [ Next → ]   |
+--------------------------------------------------------------------------+
```

### Key components
- Horizontal **stepper** (5 steps) with progress dots; each step a form section.
- **Identifiers repeater** (0..n, at least 1) — type/value/authority (FR-REG-002).
- **Live duplicate detection** banner (FR-REG-003) with review/dismiss.
- Later steps: contact + **consent capture** (FR-REG-009), **Policy/coverage** assignment (FR-REG-006), **document upload** with scan (FR-REG-010), and **Review** before commit (issues `MRS-M-…`).

### States
- **Validation:** per-field inline errors; Next disabled until step valid (`aria-disabled` + reason).
- **Duplicate found:** blocking-soft warning; operator must acknowledge before proceeding.
- **Consent missing:** cannot complete; "Mandatory consent required."
- **Upload states:** scanning / attached / rejected.
- **Success:** "Beneficiary registered. Member No MRS-M-0032118845." + next actions (assign appt).
- **Save/resume:** draft preserved if interrupted.

### Accessibility annotations
- Stepper exposes current step to SR ("Step 1 of 5, Identity"); steps are headings.
- Errors summarized at top on submit with anchor links to fields (WCAG 3.3.1/3.3.3).
- Repeater add/remove buttons labeled; focus moves to new row.
- Required fields marked with text + `aria-required`, not asterisk-only.

### Responsive
- Mobile: stepper becomes compact "Step 1/5" with progress bar; one section per screen.
- **⟵RTL:** stepper flows right-to-left; Next/Back swap sides.

---

## 7. Executive dashboard (Medical Director / leadership)

### Layout
```
Home ▸ Dashboard          Period [ This month ▾ ]   Site [ All ▾ ]  [Export]
+--------------------------------------------------------------------------+
|  KPI  Visits 3,412  |  Eligible 92% |  Approval TAT 5.4h | Rx filled 88% |
+-----------------------------+--------------------------------------------+
|  Eligibility mix            |  Approval backlog by SLA                   |
|  [✓]Eligible ▓▓▓▓▓▓▓ 72%     |  [✓]On time ▓▓▓▓▓ | [△]At risk ▓▓ | [✕]Breach ▓|
|  [⧗]Needs appr ▓▓ 14%        |                                            |
|  [✕]Not elig ▓ 8% [◐]Part 6% |  Utilization vs limit ▓▓▓▓▓▓░░ 68%          |
+-----------------------------+--------------------------------------------+
|  Top services / diagnoses (role-permitted)   Provider throughput table   |
|  1 General consult  842      |  Lab A ▓▓▓▓  Imaging B ▓▓  Pharmacy C ▓▓▓▓  |
+--------------------------------------------------------------------------+
```

### Key components
- **KPI strip:** volumes, eligibility %, approval TAT, Rx fill rate (FR-RPT-001).
- Charts: eligibility mix, approval SLA, utilization-vs-limit (FR-RPT-005).
- Role-permitted breakdowns; **Finance variant hides diagnoses** (FR-RPT-002).
- Period/site filters; permissioned export (FR-RPT-004).

### States
- **Loading:** KPI + chart skeletons.
- **Empty period:** "No data for this period."
- **Partial data / delayed:** `[△ Data delayed]` banner.
- **Export success/failure** toast; export audited.

### Accessibility annotations
- Every chart has an accessible **data table** alternative and text summary (WCAG 1.1.1); series use pattern+label, not color-only.
- KPIs are labeled figures with `aria-label` reading value + unit + trend.
- Filters are labeled selects; export announces completion via `aria-live`.

### Responsive
- Desktop: 2–3 column grid. Tablet: 2 col. Mobile: single column, charts scroll; KPIs 2×2.
- **⟵RTL:** grid and bar directions mirror; numerals localized.

---

## 8. Cross-screen component states reference

| Component | Loading | Empty | Error | Success |
|-----------|---------|-------|-------|---------|
| Search | skeleton input hint | "Search to begin" | "No match / try another ID" | result card |
| Queue/Worklist | skeleton rows | "Nothing waiting" | "Couldn't load — retry" | populated + counts |
| Consume/Dispense action | spinner on button | n/a | `[✕ Already consumed]` / `[✕ Exceeds qty]` | `[✓ Consumed/Dispensed]` |
| Decision form | disabled | n/a | `[✕ Reason required]` | "Decision submitted" |
| Upload | "Scanning…" | "No file" | `[✕ File rejected]` | attached chip |
| Wizard step | section skeleton | prefilled defaults | inline field errors + summary | "Step complete" |

All error/success messages use `role="alert"`/`aria-live` and pair icon+text (never color-only).

---

## 9. Wireframe acceptance criteria

- [ ] No screen exposes data beyond its portal's zone in [11-permission-matrix.md](11-permission-matrix.md) (min-necessary banners are explicit where relevant).
- [ ] Every screen defines loading / empty / error / success states.
- [ ] All status chips render `{color + icon + shape + text + tooltip}`.
- [ ] All interactive targets ≥ 44px; visible focus retained.
- [ ] Each screen has RTL mirroring notes and a responsive (tablet/mobile) note.
- [ ] Charts and status carry non-color-redundant + text-alternative encodings.

---

### Cross-references
- IA & sitemaps: [09-information-architecture.md](09-information-architecture.md) · Flows: [13-ux-flows.md](13-ux-flows.md) · Nav: [14-navigation-structure.md](14-navigation-structure.md)
- Requirements realized: [07-functional-requirements.md](07-functional-requirements.md) · Permissions: [11-permission-matrix.md](11-permission-matrix.md)
- Accessibility detail: [21-accessibility-checklist.md](21-accessibility-checklist.md)

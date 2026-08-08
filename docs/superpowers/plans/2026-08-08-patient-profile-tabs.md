# Patient Profile — Always-Visible Identity + Tabbed Sections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rework the Patient Profile screen so the identity block (with blood group + allergy chips) is
always visible above a sticky pill tab bar that groups the remaining 14 server sections into 7 tabs,
replacing the current single anchor-jump stack.

**Architecture:** Pure client-side regrouping of an existing, unchanged server contract
(`PatientProfileContract`/`PROFILE_SECTION_KEYS`). A new `PROFILE_TAB_GROUPS` constant maps each section
key to one of 7 tabs; `ProfileBody` renders the identity card directly (no longer as a `SectionCard`) and
feeds the remaining sections into the design system's `Tabs` component (new `variant="pill"` visual option,
same Radix semantics). The identity card's new blood-group/allergy display reuses logic extracted from
`PatientContextBar`, which already does this exact thing for the encounter workspace.

**Tech Stack:** React + TypeScript, `@radix-ui/react-tabs` (already a dependency via the design system's
`Tabs` component), Vitest + Testing Library + jest-axe, no new dependencies.

## Global Constraints

- Design spec: `docs/superpowers/specs/2026-08-08-patient-profile-tabs-redesign.md` — read it first; this
  plan implements it exactly, including the tab→section mapping table and the "what does not change" list.
- No server contract change. Do not touch `libs/contracts/src/profile.ts`, `services/profile/**`, or the
  section `state`/`reasonCode` semantics.
- Every UI change must keep the accessibility DoD: keyboard operable, visible 3px focus, ≥44×44px targets,
  non-color status, AA contrast, RTL parity, `aria-live` for async outcomes (project `CLAUDE.md`).
- Minimum-necessary is code, not comments: the identity card's blood-group fact must not render for a role
  whose profile payload never included an `alerts` section (see Task 3, the `bloodGroup` prop expression).
- TypeScript strict mode, no `any`. Follow existing patterns in the files you touch rather than introducing
  new ones.
- Conventional commit messages: `<type>(<scope>): <summary>`.

---

### Task 1: `Tabs` pill visual variant (design system)

**Files:**
- Modify: `apps/design-system/src/components/Tabs.tsx`
- Modify: `apps/design-system/src/styles/components.css:502` (after the existing `.mrs-tabpane` rule)
- Test: `apps/design-system/test/components.test.tsx`

**Interfaces:**
- Consumes: nothing new — `@radix-ui/react-tabs` (`RadixTabs`) already imported in `Tabs.tsx`.
- Produces: `TabsProps.variant?: "underline" | "pill"` (default `"underline"`, so every existing call site is
  unaffected). When `variant="pill"`, the rendered `RadixTabs.List` carries an additional `mrs-tabs--pill`
  class. `TabItem`/`Tabs` export shape is otherwise unchanged — Task 3 relies on `Tabs`, `TabItem`.

- [ ] **Step 1: Write the failing test**

Add to `apps/design-system/test/components.test.tsx` (add `Tabs` and `TabItem` to the existing import line
from `"../src"`, which currently reads `Button, DataTable, InputField, SegmentedControl, Select, StatusChip,
type Column, type StatusKind`):

```tsx
describe("Tabs", () => {
  it("is a tablist with correct roles and switches panels on click", async () => {
    const onValueChange = vi.fn();
    function Demo() {
      const [value, setValue] = useState("a");
      return (
        <Tabs
          aria-label="Sections"
          value={value}
          onValueChange={(v) => {
            setValue(v);
            onValueChange(v);
          }}
          items={[
            { value: "a", label: "First", content: <p>First content</p> },
            { value: "b", label: "Second", content: <p>Second content</p> },
          ]}
        />
      );
    }
    renderDS(<Demo />);
    expect(screen.getByRole("tablist", { name: "Sections" })).toBeInTheDocument();
    expect(screen.getByText("First content")).toBeVisible();
    expect(screen.queryByText("Second content")).not.toBeVisible();

    await userEvent.click(screen.getByRole("tab", { name: "Second" }));
    expect(onValueChange).toHaveBeenCalledWith("b");
    expect(screen.getByText("Second content")).toBeVisible();
  });

  it("applies the pill visual variant without changing tab semantics", () => {
    renderDS(
      <Tabs
        variant="pill"
        aria-label="Sections"
        value="a"
        onValueChange={() => {}}
        items={[{ value: "a", label: "First", content: <p>First</p> }]}
      />,
    );
    expect(screen.getByRole("tablist")).toHaveClass("mrs-tabs--pill");
    expect(screen.getByRole("tab", { name: "First" })).toBeInTheDocument();
  });
});
```

`useState` is already imported at the top of this file; add `Tabs` to the existing `"../src"` import.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd apps/design-system && npx vitest run test/components.test.tsx -t Tabs`
Expected: FAIL — `Tabs`/`TabItem` not exported from the test's import, or `variant` prop / `mrs-tabs--pill`
class not recognized (the first assertion that touches `variant` fails; the base tablist test may already
pass since `Tabs` exists — that's fine, the pill-variant test is the one that must fail here).

- [ ] **Step 3: Add the `variant` prop and CSS class**

Replace the full contents of `apps/design-system/src/components/Tabs.tsx`:

```tsx
import * as RadixTabs from "@radix-ui/react-tabs";
import type { ReactNode } from "react";
import { cx } from "../lib/cx";

export interface TabItem {
  value: string;
  label: string;
  content: ReactNode;
}

export interface TabsProps {
  items: TabItem[];
  value: string;
  onValueChange: (value: string) => void;
  "aria-label": string;
  className?: string;
  /**
   * Visual style only — semantics (tablist/tab/tabpanel roles, roving focus, arrow-key nav) are identical
   * either way. "underline" (default, 0B §6) suits a document-style set of panes. "pill" gives the same
   * segmented-control look `SegmentedControl` uses, for a tab bar that reads as primary page navigation
   * rather than a filter — `SegmentedControl` itself stays a `radiogroup`, the correct role for an actual
   * filter switch, so a content-switching tab bar should not borrow it just for the look.
   */
  variant?: "underline" | "pill";
}

/**
 * Tabs — Radix-backed (roving focus, arrow-key nav, correct ARIA). Underline style per 0B §6, or pill.
 * Content is always mounted so SSR/loading never hides a panel unexpectedly.
 */
export function Tabs({ items, value, onValueChange, className, variant = "underline", ...aria }: TabsProps) {
  return (
    <RadixTabs.Root value={value} onValueChange={onValueChange} className={className}>
      <RadixTabs.List
        className={cx("mrs-tabs", variant === "pill" && "mrs-tabs--pill")}
        aria-label={aria["aria-label"]}
      >
        {items.map((it) => (
          <RadixTabs.Trigger key={it.value} value={it.value} className="mrs-tab" asChild>
            <button type="button">{it.label}</button>
          </RadixTabs.Trigger>
        ))}
      </RadixTabs.List>
      {items.map((it) => (
        <RadixTabs.Content
          key={it.value}
          value={it.value}
          className={cx("mrs-tabpane")}
          forceMount
          hidden={it.value !== value}
        >
          {it.content}
        </RadixTabs.Content>
      ))}
    </RadixTabs.Root>
  );
}
```

Add to `apps/design-system/src/styles/components.css`, immediately after the `.mrs-tabpane { padding: var(--sp5) 0; }` rule (currently line 504):

```css
/* Pill variant — same tablist/tab/tabpanel semantics as the underline style above, `.mrs-seg`'s look. */
.mrs-tabs--pill {
  border-bottom: 0;
  display: inline-flex;
  flex-wrap: wrap;
  background: var(--surface-2);
  border: 1px solid var(--border);
  border-radius: var(--r-pill);
  padding: 3px;
  gap: 3px;
}
.mrs-tabs--pill .mrs-tab {
  border-bottom: 0;
  border-radius: var(--r-pill);
  padding: 0 14px;
  font-size: var(--fs-caption);
}
.mrs-tabs--pill .mrs-tab[aria-selected="true"] {
  background: var(--surface-1);
  color: var(--accent);
  box-shadow: var(--elev-1);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd apps/design-system && npx vitest run test/components.test.tsx`
Expected: PASS, full file including the two new `Tabs` tests and every pre-existing test in the file.

- [ ] **Step 5: Commit**

```bash
git add apps/design-system/src/components/Tabs.tsx apps/design-system/src/styles/components.css apps/design-system/test/components.test.tsx
git commit -m "feat(design-system): add Tabs pill variant"
```

---

### Task 2: Extract `AllergyChips`, refactor `PatientContextBar` onto it

**Files:**
- Modify: `apps/web/src/screens/PatientProfile.tsx`

**Interfaces:**
- Consumes: `ProfileAlerts` type (`@mersal/contracts`), `STR.allergyTo`/`STR.moreAlerts`/`STR.alerts`
  (already defined in this file), `useLoc` (already imported).
- Produces: `AllergyChips({ alertData: ProfileAlerts | null; namedAllergens: boolean })` — a function
  component returning the same chip markup `PatientContextBar` builds today. Task 3's identity card uses it.

This is a behavior-preserving refactor: `PatientContextBar`'s rendered output and its existing tests
(`apps/web/test/patient-profile.test.tsx`, the "the patient context bar" describe block) must be unchanged.

- [ ] **Step 1: Add `AllergyChips`, and switch `PatientContextBar` to it**

In `apps/web/src/screens/PatientProfile.tsx`, add a new function directly above `PatientContextBar`
(currently starting at line 973):

```tsx
/**
 * Up to 2 named allergens as warning chips, then a "+N more" chip — or a bare count when `namedAllergens`
 * is false. Shared by `PatientContextBar` (the encounter workspace's identity strip) and the profile's own
 * always-visible identity card (Task 3), so the two read identically rather than drifting.
 *
 * `alertData: null` means "nothing to show" — the caller decides separately (via whether it passes real
 * data at all) whether that silence is because there is nothing recorded or because this viewer's payload
 * never carried an alerts section; this component only renders what it is given.
 */
function AllergyChips({ alertData, namedAllergens }: { alertData: ProfileAlerts | null; namedAllergens: boolean }) {
  const t = useLoc();
  const alertCount = alertData ? alertData.allergies.length + (alertData.criticalFlags?.length ?? 0) : 0;
  // Two named substances, then a remainder. A strip is a fixed-height safety control, and an eight-allergy
  // patient must not push the identity it exists to confirm onto a second line.
  const named = namedAllergens ? alertData?.allergies.slice(0, 2) ?? [] : [];
  const namedRest = alertCount - named.length;

  return (
    <>
      {named.map((a) => (
        <span key={a.allergen} className="profile-chip profile-chip--critical" data-shape="octagon">
          <span aria-hidden="true" className="profile-chip-icon">⚠</span>
          <span>{t(STR.allergyTo)} {a.allergen}</span>
        </span>
      ))}
      {(namedAllergens ? namedRest : alertCount) > 0 ? (
        <span className="profile-chip profile-chip--critical" data-shape="octagon">
          <span aria-hidden="true" className="profile-chip-icon">⚠</span>
          <span>
            {namedAllergens ? namedRest : alertCount}{" "}
            {namedAllergens ? t(STR.moreAlerts) : t(STR.alerts)}
          </span>
        </span>
      ) : null}
    </>
  );
}
```

Then in `PatientContextBar`, replace the local computation and inline chip JSX. Find this block (around
lines 1030–1067 today):

```tsx
  const alerts = profile.sections.find((s) => s.key === "alerts");
  const alertData = alerts?.state === "Visible" ? (alerts.data as ProfileAlerts) : null;
  const alertCount = alertData ? alertData.allergies.length + (alertData.criticalFlags?.length ?? 0) : 0;
  // Two named substances, then a remainder. A strip is a fixed-height safety control, and an eight-allergy
  // patient must not push the identity it exists to confirm onto a second line.
  const named = namedAllergens ? alertData?.allergies.slice(0, 2) ?? [] : [];
  const namedRest = alertCount - named.length;

  return (
    <aside className="patient-context-bar" aria-label={t(STR.title)}>
      {/* The SAME identity block the patient file leads with — see ProfileIdentity. The strip used to be a
          flat dot-separated line of the same fields in a different shape, which made a clinician moving from
          the file into the encounter re-find the member number in a new layout for no reason. */}
      <ProfileIdentity
        data={data}
        onOpen={() => openProfile(beneficiaryId)}
        actions={actions}
        // `?? null` matters: when the alerts section is withheld or failed, `alertData` is null and the
        // strip must still show "not recorded" rather than dropping the fact — `undefined` would drop it.
        bloodGroup={showBloodGroup ? alertData?.bloodGroup ?? null : undefined}
        chips={
          <>
            {named.map((a) => (
              <span key={a.allergen} className="profile-chip profile-chip--critical" data-shape="octagon">
                <span aria-hidden="true" className="profile-chip-icon">⚠</span>
                <span>{t(STR.allergyTo)} {a.allergen}</span>
              </span>
            ))}
            {(namedAllergens ? namedRest : alertCount) > 0 ? (
              <span className="profile-chip profile-chip--critical" data-shape="octagon">
                <span aria-hidden="true" className="profile-chip-icon">⚠</span>
                <span>
                  {namedAllergens ? namedRest : alertCount}{" "}
                  {namedAllergens ? t(STR.moreAlerts) : t(STR.alerts)}
                </span>
              </span>
            ) : null}
          </>
        }
      />
    </aside>
  );
}
```

Replace it with:

```tsx
  const alerts = profile.sections.find((s) => s.key === "alerts");
  const alertData = alerts?.state === "Visible" ? (alerts.data as ProfileAlerts) : null;

  return (
    <aside className="patient-context-bar" aria-label={t(STR.title)}>
      {/* The SAME identity block the patient file leads with — see ProfileIdentity. The strip used to be a
          flat dot-separated line of the same fields in a different shape, which made a clinician moving from
          the file into the encounter re-find the member number in a new layout for no reason. */}
      <ProfileIdentity
        data={data}
        onOpen={() => openProfile(beneficiaryId)}
        actions={actions}
        // `?? null` matters: when the alerts section is withheld or failed, `alertData` is null and the
        // strip must still show "not recorded" rather than dropping the fact — `undefined` would drop it.
        bloodGroup={showBloodGroup ? alertData?.bloodGroup ?? null : undefined}
        chips={<AllergyChips alertData={alertData} namedAllergens={namedAllergens} />}
      />
    </aside>
  );
}
```

- [ ] **Step 2: Run the existing context-bar tests to confirm nothing broke**

Run: `cd apps/web && npx vitest run test/patient-profile.test.tsx -t "the patient context bar"`
Expected: PASS, unchanged (this step is a pure extraction — the rendered output is identical).

- [ ] **Step 3: Commit**

```bash
git add apps/web/src/screens/PatientProfile.tsx
git commit -m "refactor(web): extract AllergyChips out of PatientContextBar"
```

---

### Task 3: Always-visible identity card + 7 pill tabs

**Files:**
- Modify: `apps/web/src/screens/PatientProfile.tsx`
- Modify: `apps/web/src/styles/app.css:1397-1407`

**Interfaces:**
- Consumes: `Tabs`/`TabItem` from `@mersal/design-system` (Task 1), `AllergyChips` (Task 2),
  `ProfileSectionKey` type from `@mersal/contracts`, existing `SECTION_TITLES`, `SectionCard`,
  `SectionState`, `ProfileIdentity`.
- Produces: the new `ProfileBody` render tree Task 4/5's tests target — an always-visible
  `<section aria-label="Identity"...>` (no visible heading) followed by a `role="tablist"` with 7
  `role="tab"` buttons (`Coverage`, `History`, `Authorizations`, `Documents`, `Notes`, `Timeline`, `Call
  history` in English) and one `role="tabpanel"` per tab containing that tab's `SectionCard`s.

- [ ] **Step 1: Update imports and `STR`**

In `apps/web/src/screens/PatientProfile.tsx`, update the design-system import (currently line 3):

```tsx
import { Button, Card, Icon, InlineAlert, Select, Tabs, useTheme } from "@mersal/design-system";
import type { IconName, TabItem } from "@mersal/design-system";
```

Update the contracts type import (currently lines 5–14) to add `ProfileSectionKey`:

```tsx
import type {
  CallHistoryRow,
  ProfileExportSummary,
  CallHistorySection,
  Localized,
  PatientProfile as PatientProfileContract,
  ProfileAlerts,
  ProfileHeader,
  ProfileSection,
  ProfileSectionKey,
} from "@mersal/contracts";
```

`PROFILE_SECTION_KEYS` is no longer used directly by `ProfileBody` after this task (see Step 3) — leave the
import as-is for now; Step 3 removes it if nothing else in the file uses it (checked in that step).

In the `STR` object, rename the `jumpTo` entry (it described the old "Jump to section" nav link list, which
is gone) to describe the tab bar instead, and add the new "History" tab label:

```tsx
  tabsLabel: { en: "Profile sections", ar: "أقسام الملف" },
```

(replacing the existing `jumpTo: { en: "Jump to section", ar: "الانتقال إلى قسم" },` line). Add, anywhere in
`STR` (e.g. right after `alerts`):

```tsx
  historyTab: { en: "History", ar: "السجل الطبي" },
```

- [ ] **Step 2: Run a search to confirm `STR.jumpTo` has no other reader**

Run: `cd apps/web && grep -rn "STR.jumpTo" src/`
Expected: only the one definition site (now renamed) and its one usage inside `ProfileBody`, both of which
Step 3 rewrites together. If this turns up another reader, rename `tabsLabel` back to keep both keys instead
of reusing the slot — but per the current file there is exactly one usage, inside `ProfileBody`'s `<nav>`.

- [ ] **Step 3: Replace `ProfileBody`, remove the dead `header`/`HeaderView` path**

Replace the full `ProfileBody` function (currently lines 307–343) with:

```tsx
type ProfileTabKey = "coverage" | "history" | "authorizations" | "documents" | "notes" | "timeline" | "callHistory";

/**
 * Section keys grouped into the profile's tabs (design spec
 * docs/superpowers/specs/2026-08-08-patient-profile-tabs-redesign.md). Each group's `sections` list is in
 * `PROFILE_SECTION_KEYS` order, which is also render order within the tab — alerts-before-encounters is a
 * safety property (design 39), not a layout choice this table is free to reorder.
 */
const PROFILE_TAB_GROUPS: { key: ProfileTabKey; title: Localized; sections: ProfileSectionKey[] }[] = [
  { key: "coverage", title: SECTION_TITLES.coverage, sections: ["coverage"] },
  {
    key: "history",
    title: STR.historyTab,
    sections: ["alerts", "pastMedicalHistory", "encounters", "investigations", "prescriptions", "caseManagement"],
  },
  { key: "authorizations", title: SECTION_TITLES.authorizations, sections: ["authorizations", "referrals", "financial"] },
  { key: "documents", title: SECTION_TITLES.documents, sections: ["documents"] },
  { key: "notes", title: SECTION_TITLES.notes, sections: ["notes"] },
  { key: "timeline", title: SECTION_TITLES.timeline, sections: ["timeline"] },
  { key: "callHistory", title: SECTION_TITLES.callHistory, sections: ["callHistory"] },
];

function ProfileBody({ profile, onRetry }: { profile: PatientProfileContract; onRetry: () => void }) {
  const t = useLoc();
  const [activeTab, setActiveTab] = useState<ProfileTabKey>("coverage");

  const byKey = useMemo(() => new Map(profile.sections.map((s) => [s.key, s] as const)), [profile.sections]);
  const header = byKey.get("header");
  const alerts = byKey.get("alerts");
  const alertData = alerts?.state === "Visible" ? (alerts.data as ProfileAlerts) : null;

  const tabItems: TabItem[] = useMemo(() => {
    const assigned = new Set(PROFILE_TAB_GROUPS.flatMap((g) => g.sections as string[]));
    // A server ahead of this client sends a key none of the groups above know. It must still be shown
    // (design 39 §6: an unknown section is rendered, not reported as empty) — it lands in History, the
    // catch-all clinical tab, rather than being silently dropped.
    const orphaned = profile.sections.map((s) => s.key).filter((k) => k !== "header" && !assigned.has(k));

    return PROFILE_TAB_GROUPS.map((group) => {
      const keys = group.key === "history" ? [...group.sections, ...orphaned] : group.sections;
      return {
        value: group.key,
        label: t(group.title),
        content: (
          <div className="profile-sections">
            {keys
              .map((key) => byKey.get(key))
              .filter((s): s is ProfileSection => s !== undefined)
              .map((section) => (
                <SectionCard key={section.key} section={section} beneficiaryId={profile.beneficiaryId} onRetry={onRetry} />
              ))}
          </div>
        ),
      };
    });
  }, [byKey, profile.sections, profile.beneficiaryId, onRetry, t]);

  return (
    <div className="patient-profile">
      {header ? (
        <section aria-label={t(SECTION_TITLES.header)} className="profile-identity-card">
          <Card style={{ padding: "var(--sp5)" }}>
            {header.state === "Visible" ? (
              <ProfileIdentity
                data={header.data as ProfileHeader}
                // `alerts` PRESENCE, not just its state, gates the fact: a role whose payload never carries
                // an alerts section at all (reception) gets `undefined` here and no blood-group fact renders
                // — this screen must not invent a "not recorded" claim about data that role has no access to.
                bloodGroup={alerts ? (alertData?.bloodGroup ?? null) : undefined}
                chips={<AllergyChips alertData={alertData} namedAllergens />}
              />
            ) : (
              <SectionState section={header} beneficiaryId={profile.beneficiaryId} onRetry={onRetry} />
            )}
          </Card>
        </section>
      ) : null}

      <Tabs
        variant="pill"
        className="profile-tabs"
        aria-label={t(STR.tabsLabel)}
        value={activeTab}
        onValueChange={(v) => setActiveTab(v as ProfileTabKey)}
        items={tabItems}
      />
    </div>
  );
}
```

Then remove the now-dead `header` branch from `SectionContent` (currently lines 512–519) — `header` never
reaches `SectionCard`/`SectionContent` anymore, it is handled directly in `ProfileBody` above:

```tsx
function SectionContent({ section, beneficiaryId }: { section: ProfileSection; beneficiaryId: string }) {
  if (section.key === "alerts") return <AlertsView data={section.data as ProfileAlerts} />;
  if (section.key === "callHistory") {
    return <CallHistoryView data={section.data as CallHistorySection} beneficiaryId={beneficiaryId} />;
  }
  return <SectionView section={section} beneficiaryId={beneficiaryId} />;
}
```

Remove the now-unused `HeaderView` function entirely (currently lines 539–541, right above the `ProfileIdentity`
doc comment):

```tsx
function HeaderView({ data }: { data: ProfileHeader }) {
  return <ProfileIdentity data={data} />;
}
```

Delete just that function; leave its doc comment block above `ProfileIdentity` itself in place (it documents
`ProfileIdentity`, not `HeaderView`).

Finally, check whether `PROFILE_SECTION_KEYS` is still used anywhere in this file:

Run: `cd apps/web && grep -n "PROFILE_SECTION_KEYS" src/screens/PatientProfile.tsx`
Expected: only the import line. Remove `PROFILE_SECTION_KEYS` from the `@mersal/contracts` import (Step 1's
import block) if so — it was only used by the old `ordered` sort, which this task deletes along with the
rest of the old `ProfileBody`.

- [ ] **Step 4: Update `app.css`**

Replace lines 1397–1404 of `apps/web/src/styles/app.css`:

```css
.patient-profile { display: grid; grid-template-columns: minmax(0, 1fr); gap: var(--sp4); }
@media (min-width: 60rem) {
  .patient-profile { grid-template-columns: 14rem minmax(0, 1fr); align-items: start; }
  .profile-jump { position: sticky; top: var(--sp4); }
}
.profile-jump ul { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--sp1); }
.profile-jump a { display: block; padding: var(--sp2); border-radius: var(--r-md); text-decoration: none; }
.profile-jump a:focus-visible { outline: 3px solid var(--focus); outline-offset: 2px; }
```

with:

```css
.patient-profile { display: grid; gap: var(--sp4); }
/* The tab bar sticks at the same offset the old side nav used. Background matches the page so scrolled
   content does not show through behind the (otherwise transparent-margined) pill track. */
.profile-tabs .mrs-tabs--pill {
  position: sticky;
  top: var(--sp4);
  z-index: 1;
  background: var(--surface-0);
  padding-block: var(--sp2);
}
```

(`.profile-sections`, `.profile-section-head`, `.profile-section-actions` immediately below stay unchanged
— cards inside a tab still use them.)

- [ ] **Step 5: Type-check and build**

Run: `cd apps/web && npx tsc --noEmit`
Expected: no errors. (Tests are fixed in Tasks 4–5; this step only confirms the component compiles —
several existing tests will fail until then, which is expected and addressed next.)

- [ ] **Step 6: Commit**

```bash
git add apps/web/src/screens/PatientProfile.tsx apps/web/src/styles/app.css
git commit -m "feat(web): always-visible patient identity card + 7 pill tabs"
```

(Tests are red at this commit — the next two tasks fix them. If your workflow requires green-at-every-commit,
squash Tasks 3–5 before merging; do not skip writing them as separate commits during development, since each
is independently reviewable.)

---

### Task 4: Fix and extend `apps/web/test/patient-profile.test.tsx`

**Files:**
- Modify: `apps/web/test/patient-profile.test.tsx`

**Interfaces:**
- Consumes: `PatientProfile`, `PatientContextBar` (unchanged exports), the new tab labels from Task 3
  (`"Coverage"`, `"History"`, `"Authorizations"`, `"Documents"`, `"Notes"`, `"Timeline"`, `"Call history"`
  in English).

**The rule this task applies everywhere:** `getByRole`/`findByRole`/`queryByRole` respect the `hidden`
attribute Radix puts on inactive tab panels (`Tabs`'s `forceMount` + `hidden={value !== active}`), so a
positive `*ByRole("region", ...)` query for a section that isn't in the default **Coverage** tab must
activate that section's tab first. Negative assertions (`queryByRole` expecting nothing, because the
section was never in the payload at all) need no change — absence is absence regardless of tab state.
`container.querySelector(...)` and plain `getByText`/`findByText` (not scoped through a `*ByRole("region")`
call) do **not** respect `hidden` — they still find text in an inactive tab's forceMounted content — but any
test that then **clicks** something found that way must still activate the tab first, because a real user
cannot click into a hidden panel.

- [ ] **Step 1: Add the `openTab` helper**

Add near the top of the file, after the existing `stubClipboard`/`setupWithClipboard` helpers (around line
65):

```tsx
/** Activate a tab by its (English) label before querying inside it — see the file-level rule above. */
async function openTab(name: RegExp) {
  await userEvent.setup().click(await screen.findByRole("tab", { name }));
}
```

- [ ] **Step 2: Fix the three-states tests (History, Authorizations tabs)**

In `describe("20.4 — Restricted, Unavailable and Empty are three distinct states", ...)`:

- `"renders a restricted section as locked..."` (investigations) — insert `await openTab(/history/i);`
  immediately before `const section = await screen.findByRole("region", { name: /investigations/i });`.
- `"renders an unavailable section with Retry..."` (encounters) — insert `await openTab(/history/i);` before
  its `findByRole("region", { name: /encounters/i })`.
- `"renders an empty section plainly..."` (referrals) — insert `await openTab(/authorizations/i);` before its
  `findByRole("region", { name: /referrals/i })`.
- `"gives the three states three different markers in the DOM"` — the `container.querySelector` assertions
  afterward need no change (they don't respect `hidden`); only the wait needs to target a tab that's
  actually reachable. Change:

  ```tsx
  const { container } = renderProfile(api);
  await screen.findByRole("region", { name: /referrals/i });
  ```

  to:

  ```tsx
  const { container } = renderProfile(api);
  await openTab(/authorizations/i);
  await screen.findByRole("region", { name: /referrals/i });
  ```

- [ ] **Step 3: Leave the "invents nothing" describe block as-is**

`describe("20.4 — the screen renders the payload and invents nothing", ...)`: the photo test and the "does
not render a section the server did not return" test both only touch `header` (always visible) plus negative
`queryByRole` assertions for sections that were never in the payload — no edits needed here.

Replace the third test in this block, `"orders sections with alerts pinned directly under the header"`
(currently asserting on a global `<h2>` heading order that no longer exists — Identity has no heading and
Alerts is inside a tab), with:

```tsx
  it("shows identity with no visible heading, and pins alerts first inside the History tab", async () => {
    const api = fakeApi({
      patientProfile: vi.fn().mockResolvedValue(
        profile([
          { key: "callHistory", state: "NotApplicable" },
          { key: "alerts", state: "Visible", data: { allergies: [] } },
          { key: "header", state: "Visible", data: header() },
        ]),
      ),
    });
    renderProfile(api);

    // The identity card is reachable as a landmark (an accessible name, for assistive tech and for this
    // query) but renders no VISIBLE "Identity" heading — that label is what this redesign removed.
    await screen.findByRole("region", { name: /identity/i });
    expect(screen.queryByRole("heading", { name: /^identity$/i })).not.toBeInTheDocument();

    // Alerts is still the first thing inside History — pinned directly after the section it protects
    // against acting blind on, same safety property, now inside a tab instead of at the top of a stack.
    await openTab(/history/i);
    const headings = screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent);
    expect(headings[0]).toMatch(/alerts/i);
  });
```

- [ ] **Step 4: Fix the call-history describe block (7 tests, one tab)**

In `describe("20.4 — call history: four cues and a server-generated clipboard", ...)`, every `it()` calls
`renderProfile(...)` and then `await screen.findByRole("region", { name: /call history/i })` (or, in one
case, `getByRole`). Insert `await openTab(/call history/i);` immediately after the `renderProfile(...)` call
and before that `findByRole`/`getByRole` line, in each of these 7 tests:

- `"renders direction with the WORD and an arrow icon, not colour alone"`
- `"copies the SERVER-PROVIDED copyText verbatim, by keyboard, and announces it"`
- `"never puts a summary on the clipboard when the served row has none"`
- `"copy-all goes through the endpoint that writes the audit event"`
- `"falls back to a selectable textarea when the clipboard API is unavailable"`
- `"filters by direction without re-requesting the server's projection"`

Worked example for the first one — change:

```tsx
  it("renders direction with the WORD and an arrow icon, not colour alone", async () => {
    renderProfile(fakeApi({ patientProfile: vi.fn().mockResolvedValue(profile([callHistorySection()])) }));
    const section = await screen.findByRole("region", { name: /call history/i });
```

to:

```tsx
  it("renders direction with the WORD and an arrow icon, not colour alone", async () => {
    renderProfile(fakeApi({ patientProfile: vi.fn().mockResolvedValue(profile([callHistorySection()])) }));
    await openTab(/call history/i);
    const section = await screen.findByRole("region", { name: /call history/i });
```

Apply the identical one-line insertion (render → `await openTab(/call history/i);` → the existing find) at
each of the other 6 sites in this block, including the one that opens with `renderProfile(` inside a
multi-line `fakeApi({...})` call — the insertion point is always right after the `renderProfile(...)` /
`renderNode(...)` statement completes and before the first `findByRole("region", { name: /call history/i })`
or `getByRole("region", { name: /call history/i })` that follows it.

- [ ] **Step 5: Fix the module deep-links describe block**

In `describe("20.4 — module deep-links and the print summary", ...)`:

- `"offers a section's module action only when the role holds the permission"` (encounters, doctor) — insert
  `await openTab(/history/i);` before its `findByRole("region", { name: /encounters/i })`.
- `"offers no module link to a role without the permission"` (encounters, reception) — same insertion.
- `"offers no module link beside a RESTRICTED section"` (investigations, doctor) — insert
  `await openTab(/history/i);` before its `findByRole("region", { name: /investigations/i })`.
- The two print-summary tests (`"fetches the print summary from the SERVER..."`,
  `"does not offer the print summary to a role without profile.export"`) only touch the always-visible
  identity region — no change needed.

- [ ] **Step 6: Rewrite the accessibility test to sweep every tab**

Replace the single test in `describe("20.4 — accessibility", ...)`:

```tsx
  it("is axe-clean with all four section states on screen", async () => {
    const { container } = renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([
            { key: "header", state: "Visible", data: header() },
            { key: "alerts", state: "Visible", data: { allergies: [{ allergen: "Penicillin", severity: "High" }] } },
            { key: "investigations", state: "Restricted", reasonCode: "not-treating" },
            { key: "encounters", state: "Unavailable", reasonCode: "timeout" },
            { key: "referrals", state: "NotApplicable" },
            callHistorySection(),
          ]),
        ),
      }),
    );
    await screen.findByRole("region", { name: /call history/i });
    expect(await axe(container)).toHaveNoViolations();
  });
```

with a version that runs axe once per tab that actually has content, since only the active tab's panel is
in the accessibility tree at any one time — auditing only the default tab would silently skip the other
three withheld-state cards this test exists to cover:

```tsx
  it("is axe-clean on every tab, with all four section states represented", async () => {
    const { container } = renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([
            { key: "header", state: "Visible", data: header() },
            { key: "alerts", state: "Visible", data: { allergies: [{ allergen: "Penicillin", severity: "High" }] } },
            { key: "investigations", state: "Restricted", reasonCode: "not-treating" },
            { key: "encounters", state: "Unavailable", reasonCode: "timeout" },
            { key: "referrals", state: "NotApplicable" },
            callHistorySection(),
          ]),
        ),
      }),
    );
    await screen.findByRole("region", { name: /identity/i });

    for (const tab of [/history/i, /authorizations/i, /call history/i]) {
      await openTab(tab);
      expect(await axe(container)).toHaveNoViolations();
    }
  });
```

(Only these three tabs carry content in this fixture — Coverage, Documents, Notes and Timeline have none, so
auditing them adds nothing; `axe` on an empty tabpanel plus the always-visible identity card is already
covered by every other test in this file that never switches tabs.)

- [ ] **Step 7: Fix the "opens an encounter" describe block**

In `describe("20.4 — the encounters section opens an encounter", ...)`, all three tests render only an
`encounters` section and then click into it. Insert `await openTab(/history/i);` right after
`renderNode(...)`/`renderProfile(...)` and before the first interaction with the encounters row, in:

- `"opens the encounter from the WHOLE row, not the reference alone"` — before
  `await user.click(await screen.findByText("ENC-2026-000074"));`.
- `"opens the visit details in a modal without navigating away"` — before
  `await user.click(await screen.findByRole("button", { name: /view visit details/i }));`.
- `"still offers the details view to a role that cannot open the workspace"` — before its
  `await user.click(await screen.findByRole("button", { name: /view visit details/i }));`.

Worked example for the first — change:

```tsx
    renderNode(
      <ApiProvider client={fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([{ key: "encounters", state: "Visible", data: { items: [enc({ encounterId: "e-77" })] } }]),
        ),
      })}>
        <PatientProfile beneficiaryId={BEN} />
        <Where />
      </ApiProvider>,
    );

    await user.click(await screen.findByText("ENC-2026-000074"));
```

to:

```tsx
    renderNode(
      <ApiProvider client={fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([{ key: "encounters", state: "Visible", data: { items: [enc({ encounterId: "e-77" })] } }]),
        ),
      })}>
        <PatientProfile beneficiaryId={BEN} />
        <Where />
      </ApiProvider>,
    );

    await openTab(/history/i);
    await user.click(await screen.findByText("ENC-2026-000074"));
```

Apply the same one-line insertion at the other two sites in this block, immediately before each one's first
`user.click(await screen.findByRole("button", { name: /view visit details/i }))`.

- [ ] **Step 8: Leave the "past medical history names the condition" describe block as-is**

This block (`icdTitles`, `pmh()` helper) queries with bare `screen.findByText(...)` / `getAllByText(...)`,
never through `findByRole("region", ...)`, and never clicks anything — per the file-level rule, no edit is
needed.

- [ ] **Step 9: Add new tests for the identity card's blood group and allergy chips**

Add a new describe block at the end of the file (after the "past medical history names the condition"
block):

```tsx
// ---------------------------------------------------------------- the identity card: blood group + allergy

describe("the identity card shows blood group and allergy, sourced from alerts", () => {
  it("shows blood group as recorded when alerts carries one", async () => {
    renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([
            { key: "header", state: "Visible", data: header() },
            { key: "alerts", state: "Visible", data: { allergies: [], bloodGroup: "O+" } },
          ]),
        ),
      }),
    );
    const identity = await screen.findByRole("region", { name: /identity/i });
    expect(within(identity).getByText("O+")).toBeInTheDocument();
  });

  it("shows blood group as NOT RECORDED, in words, when alerts carries none", async () => {
    renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([
            { key: "header", state: "Visible", data: header() },
            { key: "alerts", state: "Visible", data: { allergies: [], bloodGroup: null } },
          ]),
        ),
      }),
    );
    const identity = await screen.findByRole("region", { name: /identity/i });
    expect(within(identity).getByText(/blood group not recorded/i)).toBeInTheDocument();
  });

  it("omits blood group entirely for a role whose payload never carries alerts", async () => {
    // Reception's projection has no alerts section at all — the identity card must not invent a "not
    // recorded" claim about clinical data this role has no access to.
    renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(profile([{ key: "header", state: "Visible", data: header() }])),
      }),
    );
    const identity = await screen.findByRole("region", { name: /identity/i });
    expect(within(identity).queryByText(/blood group/i)).not.toBeInTheDocument();
  });

  it("shows up to 2 named allergens on the identity card, plus a remainder count", async () => {
    renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([
            { key: "header", state: "Visible", data: header() },
            {
              key: "alerts",
              state: "Visible",
              data: {
                allergies: [
                  { allergen: "Penicillin", severity: "High" },
                  { allergen: "Latex", severity: "Moderate" },
                  { allergen: "Peanuts", severity: "High" },
                ],
              },
            },
          ]),
        ),
      }),
    );
    const identity = await screen.findByRole("region", { name: /identity/i });
    expect(within(identity).getByText(/penicillin/i)).toBeInTheDocument();
    expect(within(identity).getByText(/latex/i)).toBeInTheDocument();
    expect(within(identity).getByText(/1 more alerts/i)).toBeInTheDocument();
    expect(within(identity).queryByText(/peanuts/i)).not.toBeInTheDocument();
  });
});

describe("the profile tab bar", () => {
  it("renders 7 tabs, defaulting to Coverage", async () => {
    renderProfile(
      fakeApi({
        patientProfile: vi.fn().mockResolvedValue(
          profile([
            { key: "header", state: "Visible", data: header() },
            { key: "coverage", state: "Visible", data: { payerName: "Mersal Foundation" } },
          ]),
        ),
      }),
    );
    await screen.findByRole("region", { name: /identity/i });
    for (const name of [/coverage/i, /history/i, /authorizations/i, /documents/i, /notes/i, /timeline/i, /call history/i]) {
      expect(screen.getByRole("tab", { name })).toBeInTheDocument();
    }
    expect(screen.getByRole("tab", { name: /coverage/i })).toHaveAttribute("aria-selected", "true");
    expect(screen.getByText("Mersal Foundation")).toBeVisible();
  });

  it("switching tabs does not re-request the profile", async () => {
    const patientProfile = vi.fn().mockResolvedValue(
      profile([
        { key: "header", state: "Visible", data: header() },
        { key: "documents", state: "Visible", data: { items: [] } },
      ]),
    );
    renderProfile(fakeApi({ patientProfile }));
    await screen.findByRole("region", { name: /identity/i });
    await openTab(/documents/i);
    await screen.findByRole("region", { name: /documents/i });
    expect(patientProfile).toHaveBeenCalledTimes(1);
  });
});
```

- [ ] **Step 10: Run the full file**

Run: `cd apps/web && npx vitest run test/patient-profile.test.tsx`
Expected: PASS, every test in the file.

- [ ] **Step 11: Commit**

```bash
git add apps/web/test/patient-profile.test.tsx
git commit -m "test(web): update patient-profile tests for tabbed sections"
```

---

### Task 5: Fix `apps/web/test/patient-profile-sections.test.tsx`

**Files:**
- Modify: `apps/web/test/patient-profile-sections.test.tsx`

**Interfaces:**
- Consumes: same as Task 4 — `PatientProfile`, the new tab labels.

Same file-level rule as Task 4. Add the identical helper (this file does not share helpers with
`patient-profile.test.tsx` — `fakeApi`/`profile` are already duplicated between the two files, so this
follows the file's existing convention):

- [ ] **Step 1: Add the `openTab` helper**

Add after the `useArabic` helper (around line 61):

```tsx
/** Activate a tab by its label before querying inside it — see patient-profile.test.tsx's file-level rule. */
async function openTab(name: RegExp) {
  await userEvent.setup().click(await screen.findByRole("tab", { name }));
}
```

- [ ] **Step 2: `describe("20.4 — nested payloads are rendered, not silently dropped")`**

- `"renders coverage's per-category limits..."` — no change (Coverage is the default tab).
- `"renders a history whose only content is nested..."` (pastMedicalHistory) — insert
  `await openTab(/history/i);` before `const section = await screen.findByRole("region", { name: /past medical history/i });`.
- `"renders case management's three lists..."` (caseManagement) — insert `await openTab(/history/i);`
  before its `findByRole("region", { name: /case management/i })`.
- `"renders the financial claims ledger"` (financial) — insert `await openTab(/authorizations/i);` before
  its `findByRole("region", { name: /financial/i })`.
- `"still says No records when the payload genuinely holds nothing"` (prescriptions) — insert
  `await openTab(/history/i);` before its `findByRole("region", { name: /prescriptions/i })`.

- [ ] **Step 3: `describe("20.4 — a field the projection dropped renders as nothing, not as an empty cell")`**

- `"omits the Reason column entirely for a meta-projected encounter list"` (encounters) — insert
  `await openTab(/history/i);` before its `findByRole("region", { name: /encounters/i })`.
- `"shows the Reason column when the clinical projection carries one"` (encounters) — same insertion.
- `"omits rationale and amount columns for a reception-projected authorization list"` (authorizations) —
  insert `await openTab(/authorizations/i);` before its `findByRole("region", { name: /authorizations/i })`.
- `"renders financial headline facts with no claims table under the summary projection"` (financial) —
  insert `await openTab(/authorizations/i);` before its `findByRole("region", { name: /financial/i })`.

- [ ] **Step 4: `describe("20.4 — row-level gates are rendered as gates")`**

- `"marks a sensitivity-restricted result as restricted..."` (investigations) — insert
  `await openTab(/history/i);` before its `findByRole("region", { name: /investigations/i })`.
- `"offers no download control for a document whose content is gated"` (documents) — insert
  `await openTab(/documents/i);` before its `findByRole("region", { name: /documents/i })`.
- `"shows a withheld note's existence and none of its content"` (notes) — insert
  `await openTab(/^notes$/i);` before its `findByRole("region", { name: /^notes$/i })`.
- `"states a referral loop as open or closed..."` (referrals) — insert `await openTab(/authorizations/i);`
  before its `findByRole("region", { name: /referrals/i })`.

- [ ] **Step 5: `describe("20.4 — labels are translated, never raw payload keys")`**

- `"prints no camelCase field name anywhere in a fully populated profile"` — no change. It asserts on
  `container.textContent` for the whole render (not scoped through a `region` query for the fields it
  checks), and Radix `forceMount` keeps every tab's content in the DOM regardless of which is active, so
  the absence check still covers every section. The one `await screen.findByRole("region", { name: /coverage/i })`
  wait it does first still resolves immediately since Coverage is the default tab.
- `"renders Arabic labels in Arabic"` (coverage, Arabic) — no change; Coverage is still the default active
  tab regardless of language.
- `"translates a known status into Arabic and passes an unknown one through"` (referrals, Arabic) — insert
  `await openTab(/الموافقات/);` before its `findByRole("region", { name: /الإحالات/ })` — `/الموافقات/` is
  the Arabic label of the Authorizations tab (reused verbatim from `SECTION_TITLES.authorizations`, set in
  Task 3), which is where referrals now lives.

- [ ] **Step 6: `describe("20.4 — an unknown section key is shown, not reported as empty")`**

`"surfaces nested content from a section this client does not know"` — the fallback section lands in
History per Task 3's `orphaned` logic. Insert `await openTab(/history/i);` before its
`findByRole("region", { name: /someFutureSection/i })`.

- [ ] **Step 7: Rewrite the accessibility describe block to sweep every populated tab**

`FULL_PROFILE` (the shared fixture) carries `coverage`, `pastMedicalHistory`, `encounters`, `investigations`,
`prescriptions`, `authorizations`, `referrals`, `documents`, `notes`, `financial`, `caseManagement`,
`timeline` — every tab except Call history has content under it. Replace all three tests in
`describe("20.4 — accessibility of the new views", ...)`:

```tsx
describe("20.4 — accessibility of the new views", () => {
  it("is axe clean on every populated tab, in English", async () => {
    const { container } = renderSections(FULL_PROFILE);
    await screen.findByRole("region", { name: /coverage/i });

    for (const tab of [/coverage/i, /history/i, /authorizations/i, /documents/i, /^notes$/i, /timeline/i]) {
      await openTab(tab);
      expect(await axe(container)).toHaveNoViolations();
    }
  });

  it("is axe clean on every populated tab, in Arabic RTL", async () => {
    useArabic();
    const { container } = renderSections(FULL_PROFILE);
    await screen.findByRole("region", { name: /التغطية/ });

    for (const tab of [/التغطية/, /السجل الطبي/, /الموافقات/, /المستندات/, /^الملاحظات$/, /السجل الزمني/]) {
      await openTab(tab);
      expect(await axe(container)).toHaveNoViolations();
    }
  });

  it("gives every section table an accessible caption, on every populated tab", async () => {
    // DataTable renders its caption sr-only. A table with no caption is a table a screen-reader user lands
    // in with no idea which section they are reading — and each tab here stacks more than one.
    renderSections(FULL_PROFILE);
    await screen.findByRole("region", { name: /coverage/i });

    for (const tab of [/coverage/i, /history/i, /authorizations/i, /documents/i, /^notes$/i, /timeline/i]) {
      await openTab(tab);
      for (const table of screen.getAllByRole("table")) {
        expect(table.querySelector("caption")?.textContent?.trim()).toBeTruthy();
      }
    }
  });
});
```

(`/السجل الطبي/`, `/الموافقات/`, `/المستندات/`, `/^الملاحظات$/`, `/السجل الزمني/`, `/التغطية/` are the
Arabic tab labels set in Task 3, reusing `SECTION_TITLES`'s existing Arabic text for every tab except
History, whose Arabic label is the new `STR.historyTab` entry.)

- [ ] **Step 8: `describe("20.4 — timeline order")`**

`"puts the newest event first..."` (timeline) — insert `await openTab(/timeline/i);` before its
`findByRole("region", { name: /timeline/i })`.

- [ ] **Step 9: `describe("20.4 — the visit-details modal")`**

The shared `openModal` helper (around line 496–501) is used by two tests directly and the pattern is
repeated inline by three more. Fix the helper first:

```tsx
  async function openModal(over: Partial<ApiClient> = {}) {
    renderSections([visible("encounters", { items: [ROW] })], over);
    await openTab(/history/i);
    const section = await screen.findByRole("region", { name: /encounters/i });
    await userEvent.setup().click(within(section).getByRole("button", { name: /view visit details/i }));
    return screen.findByRole("dialog");
  }
```

This alone fixes `"splits the visit across tabs and loads the clinical record behind them"` and `"renders a
403 as restricted, not as a visit nobody documented"` (both call `openModal()`).

The remaining three tests in this block inline the same render→find→click sequence rather than using the
helper. Apply the same one-line insertion (`await openTab(/history/i);` immediately after the
`renderSections(...)` call, before the `findByRole("region", { name: /encounters/i })` that follows) to:

- `"lists what the visit ordered, scoped to that encounter"`
- `"says why one visit's orders cannot be listed when the projection withheld the encounter id"`
- `"pins the View column so the control is reachable without scrolling sideways"`
- `"does not ask emr for a record whose id the projection withheld"`

Worked example for `"pins the View column..."` — change:

```tsx
  it("pins the View column so the control is reachable without scrolling sideways", async () => {
    renderSections([visible("encounters", { items: [ROW] })]);
    const section = await screen.findByRole("region", { name: /encounters/i });
```

to:

```tsx
  it("pins the View column so the control is reachable without scrolling sideways", async () => {
    renderSections([visible("encounters", { items: [ROW] })]);
    await openTab(/history/i);
    const section = await screen.findByRole("region", { name: /encounters/i });
```

- [ ] **Step 10: Run the full file**

Run: `cd apps/web && npx vitest run test/patient-profile-sections.test.tsx`
Expected: PASS, every test in the file.

- [ ] **Step 11: Commit**

```bash
git add apps/web/test/patient-profile-sections.test.tsx
git commit -m "test(web): update patient-profile-sections tests for tabbed sections"
```

---

### Task 6: Full verification sweep

**Files:** none (verification only).

- [ ] **Step 1: Type-check the whole web app and design system**

Run: `cd apps/web && npx tsc --noEmit && cd ../design-system && npx tsc --noEmit`
Expected: no errors in either package.

- [ ] **Step 2: Run the full web test suite**

Run: `cd apps/web && npx vitest run`
Expected: all tests pass — including `member-clinical-panel.test.tsx`, `encounter-workspace.test.tsx`,
`doctor-visits.test.tsx`, `encounter-back.test.tsx`, `encounter-tabs.test.tsx`, `encounter-procedures.test.tsx`,
`encounter-transaction-actions.test.tsx`, `doctor-patients.test.tsx` (all mount `PatientContextBar`, which
Task 2 refactored without changing behavior) with **no changes required** in any of them. If any of these
fail, the regression is in Task 2's extraction, not something this task should patch around — go back and
compare `AllergyChips`'s output against the pre-refactor inline JSX it replaced.

- [ ] **Step 3: Run the full design-system test suite**

Run: `cd apps/design-system && npx vitest run`
Expected: all tests pass, including the new `Tabs` describe block and the untouched `SegmentedControl` block.

- [ ] **Step 4: Manual check — reflow and RTL**

Run: `cd apps/web && npm run dev`, open a Patient Profile with a role that sees most sections (e.g. seed a
`doctor` session), and check:
- No visible "Identity" text label anywhere on the identity card.
- Blood group and allergy chips render on the identity card.
- The 7-tab pill bar is reachable at 360px viewport width without horizontal page scroll (0B / WCAG 2.5.8
  target-size and reflow requirements) — if the pill bar overflows, wrap it (`.mrs-tabs--pill` already has
  `flex-wrap: wrap` from Task 1) rather than letting it scroll off-screen.
- Switch `apps/web`'s language to Arabic and confirm the tab bar mirrors (pill order reverses, focus ring
  and sticky behavior unchanged) and every tab label reads correctly in Arabic.
- Scroll a long tab's content and confirm the pill bar stays stuck to the top.

- [ ] **Step 5: Final commit (if Step 4 required any fixes)**

```bash
git add -A
git commit -m "fix(web): patient profile reflow/RTL fixes from manual verification"
```

(Only if Step 4 found something to fix — otherwise there is nothing to commit here, Tasks 1–5 already cover
the implementation.)

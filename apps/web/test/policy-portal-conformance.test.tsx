import { describe, expect, it, afterEach } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CheckboxField, FileField, ThemeProvider } from "@mersal/design-system";

/**
 * Design-system conformance in the policy administration portal (33.11).
 *
 * <b>What these hold.</b> The audit that produced them found the same class of fault in three places: a
 * control that the design system already styles, used bare. A checkbox with no class renders at the user
 * agent's ~13px — on the two controls that decide whether a benefit is covered at all. A text input with no
 * `.mrs-control` loses `min-height: 44px`, the ≥3:1 boundary token, and the disabled treatment, on a grid
 * that is disabled whenever the plan version is Active.
 *
 * <b>Why they are source assertions rather than rendered ones.</b> A rendered test proves the control exists;
 * it does not prove the control carries the class that gives it 44px, because jsdom applies no stylesheet.
 * The fault here is precisely a missing class, so the class is what gets asserted. The two component tests
 * below ARE rendered, because there the question is behaviour.
 */

const SRC = resolve(__dirname, "../src");

/** The nine sections of the policy portal, by the files that render them. */
const PORTAL = [
  "screens/PolicyPayerAdmin.tsx",
  "screens/PolicyProductAdmin.tsx",
  "screens/PolicyBook.tsx",
  "screens/MemberAdmin.tsx",
  "screens/PolicyBulk.tsx",
  "screens/PolicyAnalytics.tsx",
  "screens/NetworkTierAdmin.tsx",
];

const read = (rel: string) => readFileSync(join(SRC, rel), "utf8");

function tsxFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) tsxFiles(p, out);
    else if (p.endsWith(".tsx")) out.push(p);
  }
  return out;
}

afterEach(() => cleanup());

describe("no bare form control survives in the policy portal", () => {
  /**
   * `.mrs-checkbox` is 24px with a transparent outset to a 44px target. Without it the box is whatever the
   * user agent draws — about 13px — which fails WCAG 2.2 AA Target Size (Minimum) at 24px and this project's
   * own 44px bar. `CheckboxField` is the other acceptable answer: it applies the class itself.
   */
  it("every checkbox carries the sized class, or is a CheckboxField", () => {
    const offenders: string[] = [];
    for (const rel of PORTAL) {
      const src = read(rel);
      const re = /<input\b[^>]*type="checkbox"[^>]*>/gs;
      for (const m of src.matchAll(re)) {
        if (!m[0].includes("mrs-checkbox")) {
          offenders.push(`${rel}: ${m[0].replace(/\s+/g, " ").slice(0, 70)}…`);
        }
      }
    }
    expect(offenders, "a checkbox with no class is a ~13px target").toEqual([]);
  });

  /**
   * The plan-version editor's grid. Every cell control has to be a design-system component or wear the house
   * class — the fault this pins is a row that held a styled `Combobox` and an unstyled `<input>` side by side.
   */
  it("every input in the plan-version editor wears the house control", () => {
    const src = read("screens/PolicyProductAdmin.tsx");
    const offenders = [...src.matchAll(/<input\b[^>]*>/gs)]
      .map((m) => m[0])
      .filter((tag) => !tag.includes("mrs-control") && !tag.includes("mrs-checkbox"))
      .map((tag) => tag.replace(/\s+/g, " ").slice(0, 70));
    expect(offenders, "an input with no class loses 44px, the ≥3:1 boundary and :disabled").toEqual([]);
  });

  /**
   * A file input is a control the design system styles and now ships as `FileField`; hand-building the field
   * markup around one is how the next screen builds it slightly differently.
   *
   * <b>Scoped to this portal, and the scope is the honest part.</b> Three screens elsewhere — batch intake,
   * beneficiary documents, result upload — still hand-build one, and one of them holds a `ref` to reset the
   * input, which `FileField` does not forward yet. Converting them is a real follow-up in two other portals,
   * not something to smuggle into a policy-portal fix. `PhotoPicker` is a separate case and would never
   * convert: its input is deliberately `sr-only` and driven by a button, so it is not a field at all.
   */
  it("no policy screen hand-builds a file field", () => {
    const offenders = PORTAL.filter((rel) => /type="file"/.test(read(rel)));
    expect(offenders, "use FileField").toEqual([]);
  });
});

describe("no server enum reaches the operator untranslated", () => {
  /**
   * Policy-service returns statuses and limit types as plain strings, so they walk past the typed `Localized`
   * scheme that makes a missing translation a compile error (ADR-0042). An Arabic operator was reading
   * "Superseded", "PerEncounter" and "Validating" in otherwise-Arabic tables.
   *
   * The check is narrow on purpose: a `StatusChip` whose label is a raw `.status` expression, and a
   * `label: x` mapping straight off an options array. Both were the actual shape of the defect.
   */
  it("no StatusChip is labelled with a raw status field", () => {
    const offenders: string[] = [];
    for (const rel of PORTAL) {
      const src = read(rel);
      for (const m of src.matchAll(/label=\{(\w+)\.(status|groupType|payerType|relationship)\}/g)) {
        offenders.push(`${rel}: label={${m[1]}.${m[2]}}`);
      }
    }
    expect(offenders, "wrap it in enumLabel() — see i18n/enumLabels.ts").toEqual([]);
  });

  /**
   * Only the ENUM arrays. A filter built from the rows themselves — plan categories, say — carries catalogue
   * data, not an enum, and there is nothing to translate: the category is whatever the administrator typed.
   * Asserting over every `{value: x, label: x}` would have flagged those too and taught the next person that
   * the guard cries wolf.
   */
  const ENUM_ARRAYS = [
    "LIMIT_TYPES", "RESET_PERIODS", "MEMBER_STATUSES", "RELATIONSHIPS",
    "WAITING_STATES", "SEXES", "ID_TYPES", "POLICY_STATUSES",
  ];
  it("no enum array maps a value straight to its own label", () => {
    const offenders: string[] = [];
    for (const rel of PORTAL) {
      const src = read(rel);
      for (const name of ENUM_ARRAYS) {
        const re = new RegExp(`${name}\\.map\\(\\((\\w+)\\) => \\(\\{ value: \\1, label: \\1 \\}\\)`, "g");
        for (const m of src.matchAll(re)) offenders.push(`${rel}: ${m[0]}`);
      }
    }
    expect(offenders, "wrap it in enumLabel()").toEqual([]);
  });

  /** The table is shared so a member added on the server has one place to be translated, not seven. */
  it("the enum table lives in one module", () => {
    const owners = tsxFiles(SRC)
      .filter((f) => /const ENUM_LABELS/.test(readFileSync(f, "utf8")))
      .map((f) => f.replace(SRC, "src"));
    expect(owners, "ENUM_LABELS belongs to i18n/enumLabels.ts alone").toEqual([]);
  });
});

describe("the portal does not hand-write layout it has classes for", () => {
  /**
   * Card padding is the portal's oldest recurring bug — the stylesheet's own comments record two rounds of it
   * breaking. An inline `padding` on a card escapes the rule that was written to make it uniform, which is
   * how two stacked cards on the bulk screen ended up 4px apart along their shared edge.
   */
  it("no card sets its own padding", () => {
    const offenders: string[] = [];
    for (const rel of PORTAL) {
      const src = read(rel);
      for (const m of src.matchAll(/<Card\b[^>]*style=\{\{[^}]*padding[^}]*\}\}/gs)) {
        offenders.push(`${rel}: ${m[0].replace(/\s+/g, " ").slice(0, 70)}…`);
      }
    }
    expect(offenders, "the portal rule pads these; use <Card padded> outside it").toEqual([]);
  });

  /** Four screens capped a field by hand at 280, 320, 360 and 480 — two of them stacked in one card, ending
   *  120px apart. One token, so the next one cannot disagree. */
  it("no field width is a bare number", () => {
    const offenders: string[] = [];
    for (const rel of PORTAL) {
      const src = read(rel);
      for (const m of src.matchAll(/(?:max|min)Width: \d+/g)) {
        offenders.push(`${rel}: ${m[0]}`);
      }
    }
    expect(offenders, "use var(--field-max)").toEqual([]);
  });

  /** A class named for one portal, used in another, is a class that gets edited for one screen and silently
   *  changes a different one. */
  it("no portal-specific toolbar class is used outside its portal", () => {
    const offenders = tsxFiles(SRC)
      .filter((f) => /branch-toolbar/.test(readFileSync(f, "utf8")))
      .map((f) => f.replace(SRC, "src"));
    expect(offenders, "the shared row is .screen-toolbar").toEqual([]);
  });
});

describe("every catalogue list in the portal can be searched", () => {
  /**
   * Payers and Plans sit in the same nav group and are the same kind of object. One had search, three filter
   * groups, sortable columns and a pager; the other had a bare table. An operator does not care which file a
   * section lives in.
   */
  it.each([
    ["screens/PolicyPayerAdmin.tsx", "Payers"],
    ["screens/PolicyProductAdmin.tsx", "Plans"],
    ["screens/PolicyBook.tsx", "Groups"],
    ["screens/NetworkTierAdmin.tsx", "Network tiers"],
  ])("%s (%s) uses the house table", (rel) => {
    expect(read(rel)).toContain("DataTableView");
  });

  /** The policy register pages on the server, so it cannot use the client-side engine — but it sent three
   *  parameters while the server accepted eleven, so finding one policy meant paging. */
  it("the policy register sends the server its filters", () => {
    const src = read("screens/PolicyBook.tsx");
    for (const param of ["policyNo:", "status:", "payerId:"]) {
      expect(src, `policyQuery should narrow on ${param}`).toContain(param);
    }
  });
});

// ── The two new components ──────────────────────────────────────────────────────────────────────────────

const wrap = (ui: React.ReactNode) => render(<ThemeProvider>{ui}</ThemeProvider>);

describe("CheckboxField", () => {
  it("binds its label to the box, so clicking the words toggles it", async () => {
    const seen: boolean[] = [];
    wrap(
      <CheckboxField label="Out of network" onChange={(e) => seen.push(e.currentTarget.checked)} />,
    );
    const box = screen.getByRole("checkbox", { name: "Out of network" });
    expect(box).toHaveClass("mrs-checkbox");

    await userEvent.click(screen.getByText("Out of network"));
    expect(seen, "the label is part of the target, not decoration beside it").toEqual([true]);
  });

  it("describes itself with its help and announces its error", () => {
    wrap(<CheckboxField label="Compare" help="Shows the previous period" error="Pick a period first" />);
    const box = screen.getByRole("checkbox", { name: "Compare" });
    expect(box).toHaveAccessibleDescription(/Shows the previous period/);
    expect(box).toHaveAttribute("aria-invalid", "true");
    expect(within(screen.getByRole("alert")).getByText("Pick a period first")).toBeInTheDocument();
  });
});

describe("FileField", () => {
  it("is a labelled file input wearing the house control", () => {
    wrap(<FileField label="Spreadsheet" accept=".csv" />);
    const input = screen.getByLabelText("Spreadsheet");
    expect(input).toHaveAttribute("type", "file");
    expect(input).toHaveClass("mrs-control");
    expect(input).toHaveAttribute("accept", ".csv");
  });
});

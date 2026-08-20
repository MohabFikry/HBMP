import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";

/**
 * A button's weight comes from what it sits beside.
 *
 * Three rules, each pinning a split the audit found:
 *
 * <ol>
 *   <li><b>Row actions are `sm`.</b> Twelve full-height buttons sat inside table cells beside `sm` ones doing
 *       comparable work in the next column, growing the row for no reason. (`.mrs-btn.mrs-sm` is already
 *       44px tall — the small size costs nothing in hit area, only in font size and padding.)</li>
 *   <li><b>A dismiss beside a DANGER commit is `secondary`, not `ghost`.</b> Backing out is the recommended
 *       action there and must not be the lighter of the two. This is the same reasoning that made three
 *       cancellation dialogs relabel their dismiss to "Keep it" — comments in all three say operators read a
 *       "Cancel" button on a cancellation dialog as cancelling the appointment.</li>
 *   <li><b>Selection is never conveyed by a button's colour.</b> Three screens turned a row's button from
 *       secondary to primary to say "this row is open below" — a hue, with no aria state and no second cue,
 *       against the project's own rule that status is hue + icon + shape + text. `DataTable` ships
 *       `interactive` + `selectedKey`, which gives a 4px accent bar, a tint and `aria-selected`.</li>
 * </ol>
 */

const SRC = resolve(__dirname, "../src");

function tsxFiles(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const p = join(dir, entry);
    if (statSync(p).isDirectory()) tsxFiles(p, out);
    else if (p.endsWith(".tsx")) out.push(p);
  }
  return out;
}

/**
 * The file with comments blanked out, newlines kept so line numbers still point at the real thing.
 *
 * Not cosmetic. This codebase comments densely and in prose, and the prose contains angle brackets — the
 * comment above one withdraw button cites `.mrs-btn.mrs-danger:has(> svg:only-child)`, and that `>` ended the
 * scan of the tag it was inside, so a correctly-sized button was reported as an offender.
 */
function readCode(file: string): string {
  return readFileSync(file, "utf8").replace(/\/\*[\s\S]*?\*\/|\/\/[^\n]*/g, (c) => c.replace(/[^\n]/g, " "));
}

/** Balanced `{ key: "…", … }` literals — a table's column definitions. */
function columnLiterals(src: string): Array<{ index: number; text: string }> {
  const out: Array<{ index: number; text: string }> = [];
  for (let i = 0; i < src.length; i++) {
    if (src[i] !== "{" || !/\{\s*key:\s*"/.test(src.slice(i, i + 40))) continue;
    let depth = 0, j = i;
    for (; j < src.length; j++) {
      if (src[j] === "{") depth++;
      else if (src[j] === "}") { depth--; if (!depth) break; }
    }
    out.push({ index: i, text: src.slice(i, j + 1) });
    i = j;
  }
  return out;
}

/** A `<Button …>` opening tag, brace-balanced so a ternary prop cannot end it early. */
function buttonOpenings(src: string): Array<{ index: number; open: string }> {
  const out: Array<{ index: number; open: string }> = [];
  const re = /<Button\b/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(src))) {
    let i = m.index + m[0].length, depth = 0;
    for (; i < src.length; i++) {
      const c = src[i];
      if (c === "{") depth++;
      else if (c === "}") depth--;
      else if (c === ">" && depth === 0) break;
    }
    out.push({ index: m.index, open: src.slice(m.index, i + 1) });
  }
  return out;
}

describe("row actions are sized for a row", () => {
  it("finds buttons inside column definitions — otherwise this asserts nothing", () => {
    let n = 0;
    for (const f of tsxFiles(SRC)) {
      for (const c of columnLiterals(readCode(f))) n += buttonOpenings(c.text).length;
    }
    expect(n).toBeGreaterThan(30);
  });

  it('gives every button inside a table cell size="sm"', () => {
    const offenders: string[] = [];
    for (const file of tsxFiles(SRC)) {
      const src = readCode(file);
      for (const col of columnLiterals(src)) {
        for (const b of buttonOpenings(col.text)) {
          if (/size="sm"/.test(b.open)) continue;
          offenders.push(`${file.slice(SRC.length + 1)}:${src.slice(0, col.index).split("\n").length}`);
        }
      }
    }
    expect(offenders, 'a control in a worklist row is size="sm" — it is still a 44px target').toEqual([]);
  });
});

describe("the dismiss beside a destructive commit carries weight", () => {
  /** `footer={…}` blocks holding more than one button. */
  function footers(): Array<{ file: string; line: number; variants: string[] }> {
    const out: Array<{ file: string; line: number; variants: string[] }> = [];
    for (const file of tsxFiles(SRC)) {
      const src = readCode(file);
      for (const m of src.matchAll(/footer=\{([\s\S]{0,1400}?)\n(\s*)\}/g)) {
        const variants = buttonOpenings(m[1]).map(
          (b) => /variant=(\{[^\n]*?\}|"[a-z]+")/.exec(b.open)?.[1] ?? '"secondary"');
        if (variants.length < 2) continue;
        out.push({ file: file.slice(SRC.length + 1), line: src.slice(0, m.index).split("\n").length, variants });
      }
    }
    return out;
  }

  it("finds multi-button dialog footers", () => {
    expect(footers().length).toBeGreaterThan(12);
  });

  it("never pairs a ghost dismiss with a danger commit", () => {
    // ADJACENT pairs, not "the first button". A footer can hold two alternative branches — the status
    // dialog renders a lone Close when there is nothing to do, and a Cancel/Confirm pair otherwise — so the
    // first button in source order is not necessarily the dismiss for the commit further down.
    const offenders = footers()
      .filter((f) => f.variants.some((v, i) =>
        // The QUOTED string, because `{selected?.danger ? "secondary" : "ghost"}` mentions danger as a
        // property name and resolves to neither.
        i > 0 && v.includes('"danger"') && f.variants[i - 1] === '"ghost"'))
      .map((f) => `${f.file}:${f.line} ${f.variants.join(" -> ")}`);
    expect(
      offenders,
      'the safe option must not be lighter than the destructive one — use variant="secondary" for the dismiss',
    ).toEqual([]);
  });
});

describe("selection is not conveyed by a button's colour", () => {
  it("has no button whose variant is chosen by the selected row", () => {
    const offenders: string[] = [];
    for (const file of tsxFiles(SRC)) {
      const src = readCode(file);
      for (const b of buttonOpenings(src)) {
        const variant = /variant=(\{[^\n]*?\})/.exec(b.open)?.[1];
        if (!variant) continue;
        // Row IDENTITY — `selected === r.id` — and not merely the word "selected". A variant that reads
        // `selected?.danger` is asking whether the chosen OPTION is destructive, which is exactly the
        // conditional-danger rule above and must not be reported here.
        if (!/\bselected\s*===/.test(variant)) continue;
        offenders.push(`${file.slice(SRC.length + 1)}:${src.slice(0, b.index).split("\n").length} ${variant}`);
      }
    }
    expect(
      offenders,
      "hue alone is not a status cue — use DataTable's `interactive` + `selectedKey`, which gives an accent " +
        "bar, a tint and aria-selected",
    ).toEqual([]);
  });
});

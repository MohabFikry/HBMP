import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";

/**
 * A column of magnitudes is right-aligned by the COLUMN, not by a span inside the cell.
 *
 * <b>The defect, twice.</b> `Column.numeric` aligns the cell and its header to the end and sets tabular
 * figures, and its own doc comment records that this went wrong once already: thirteen money renders wrapped
 * their value in `.tnum` — which sets the figure WIDTH and nothing else — inside a cell still aligned to the
 * start. "The fix was applied and the column stayed ragged."
 *
 * That round fixed money. It did not fix quantities, and eleven count/quantity/cost columns were still
 * hand-aligning. `ApprovalsExtra` had the tell: four adjacent metric columns where `avg` and `p95` were
 * `numeric` and the `count` and `breaches` either side of them were `.tnum` spans, so two of the four sat at
 * a different edge from their neighbours.
 *
 * <b>What this does NOT flag.</b> `.tnum` on an identifier, a date, an MRN or a service code is CORRECT and
 * deliberate — those read left-to-right like words, and `Column.numeric` explicitly excludes them. So the
 * guard cannot be "`.tnum` implies `numeric`". It keys on the value being a magnitude: a formatted money or
 * number, or a field named like a count or a quantity.
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

/** Balanced `{ key: "…", … }` literals — the column definitions. */
function columnLiterals(src: string): Array<{ index: number; text: string }> {
  const out: Array<{ index: number; text: string }> = [];
  for (let i = 0; i < src.length; i++) {
    if (src[i] !== "{") continue;
    if (!/\{\s*key:\s*"/.test(src.slice(i, i + 40))) continue;
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

/**
 * Is this column's value a MAGNITUDE — something an operator compares by scanning down the column?
 *
 * Money and percentages always are. Otherwise it takes a field that is named as a count or a quantity, which
 * is what distinguishes `r.deliveredQty` from `r.claimNo`: both are digits, and only one of them is a number.
 */
const MAGNITUDE = /fmt\.(money|percent)\b|\b\w*(Qty|Quantity|Count|count)\b|\.length\b/;

/** Cells whose figure is decoration around a chip or a label, where end-alignment is not the answer. */
const NOT_A_COLUMN_OF_FIGURES = /panelsDone|panelsTotal/;

interface Offender { file: string; line: number; key: string }

function handAlignedMagnitudes(): Offender[] {
  const out: Offender[] = [];
  for (const file of tsxFiles(SRC)) {
    const src = readFileSync(file, "utf8");
    for (const col of columnLiterals(src)) {
      if (/numeric:\s*true/.test(col.text)) continue;
      if (!/\btnum\b/.test(col.text)) continue;
      if (!MAGNITUDE.test(col.text)) continue;
      if (NOT_A_COLUMN_OF_FIGURES.test(col.text)) continue;
      out.push({
        file: file.slice(SRC.length + 1),
        line: src.slice(0, col.index).split("\n").length,
        key: /key:\s*"([^"]+)"/.exec(col.text)?.[1] ?? "?",
      });
    }
  }
  return out;
}

describe("columns of figures are aligned by the column", () => {
  it("sees the column definitions at all — otherwise this asserts nothing", () => {
    let columns = 0;
    for (const f of tsxFiles(SRC)) columns += columnLiterals(readFileSync(f, "utf8")).length;
    expect(columns).toBeGreaterThan(300);
  });

  it("still finds plenty of legitimate .tnum that it must NOT flag", () => {
    // Identifiers, dates and codes keep `.tnum` and stay start-aligned. If this fell to zero the guard above
    // would have become "no .tnum anywhere", which is a different and wrong rule.
    let tnumColumns = 0;
    for (const f of tsxFiles(SRC)) {
      for (const col of columnLiterals(readFileSync(f, "utf8"))) {
        if (/\btnum\b/.test(col.text) && !/numeric:\s*true/.test(col.text)) tnumColumns++;
      }
    }
    expect(tnumColumns).toBeGreaterThan(40);
  });

  it("marks every magnitude column numeric instead of wrapping the value in .tnum", () => {
    const offenders = handAlignedMagnitudes();
    expect(
      offenders.map((o) => `${o.file}:${o.line} key="${o.key}"`),
      "this column holds a quantity, a count or an amount — set `numeric: true` on the COLUMN rather than " +
        "wrapping the value in .tnum, which sets the figure width and leaves the column ragged",
    ).toEqual([]);
  });
});

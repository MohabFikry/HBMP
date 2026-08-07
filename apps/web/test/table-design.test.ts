import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync } from "node:fs";
import { join, relative, resolve } from "node:path";

/**
 * How a table is drawn, held to one design.
 *
 * <b>Why this exists.</b> Five vocabularies had grown for one thing. `.mrs-wl` (the worklist, 36 screens),
 * `.mini-table`, `.pol-grid`/`.pol-costshare` (the policy and member screens), `.rx-dispense-table` (the
 * counter), and an inline-styled table on the effective-access preview that carried no class at all — so it
 * was invisible to every search for one. They disagreed on every property a column header has: three sizes
 * (11.5px, 0.78rem, 13px), two colours, and two of the five with no uppercase and no letter-spacing
 * whatsoever. The member screen renders `.mrs-wl` and `.pol-grid` inside the same card, where they read as
 * two different products.
 *
 * <b>And the numbers were ragged.</b> A money column is read by scanning DOWN it, which only works when the
 * digits stack. Fifty-six money renders across the app; thirteen wrapped their value in `.tnum` — which sets
 * `font-variant-numeric` and nothing else, on a `<span>` inside a cell still aligned to the start. So the
 * fix had been applied, it looked applied, and the column was still ragged: alignment lives on the CELL.
 * Only `.rx-dispense-table` got it right, through a private `.rx-num` nobody else could reach.
 */

const WEB_SRC = resolve(__dirname, "../src");
const DS_SRC = resolve(__dirname, "../../design-system/src");
const APP_CSS = resolve(__dirname, "../src/styles/app.css");
const DS_CSS = resolve(__dirname, "../../design-system/src/styles/components.css");
const TOKENS = resolve(__dirname, "../../design-system/src/tokens/tokens.css");

function walk(dir: string, ext: string, out: string[] = []): string[] {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (e.isDirectory()) walk(p, ext, out);
    else if (p.endsWith(ext)) out.push(p);
  }
  return out;
}

const decls = (css: string) => css.replace(/\/\*[\s\S]*?\*\//g, "");
const tsx = () => walk(WEB_SRC, ".tsx").concat(walk(DS_SRC, ".tsx"));

/** Every rule whose selector targets a column header. */
function headerRules(css: string): { selector: string; body: string }[] {
  return [...decls(css).matchAll(/([^{}]+)\{([^{}]*)\}/g)]
    .map((m) => ({ selector: m[1].trim().replace(/\s+/g, " "), body: m[2] }))
    .filter((r) => /(^|[\s,])\.?[\w.-]*\s*(thead\s+th|\bth)\b/.test(r.selector))
    .filter((r) => /font-size|letter-spacing|text-transform|(^|;)\s*color/.test(r.body));
}

describe("the table header", () => {
  it("is described once, in tokens", () => {
    const t = readFileSync(TOKENS, "utf8");
    for (const token of ["--tbl-head-size", "--tbl-head-weight", "--tbl-head-tracking", "--tbl-head-color"]) {
      expect(t, `${token} must be a token, not a value copied into each table's rule`).toContain(`${token}:`);
    }
  });

  it("never hard-codes its type or colour in a table's own rule", () => {
    // The check that would have caught the drift: a header rule setting a literal size, tracking or colour is
    // a table deciding for itself what a header looks like. There is one answer and it is in the tokens.
    const offenders: string[] = [];
    for (const file of [APP_CSS, DS_CSS]) {
      for (const { selector, body } of headerRules(readFileSync(file, "utf8"))) {
        for (const [, prop, value] of body.matchAll(/(font-size|letter-spacing|font-weight|color)\s*:\s*([^;]+)/g)) {
          if (!/var\(--tbl-head-/.test(value)) {
            offenders.push(`${relative(process.cwd(), file)} — ${selector} { ${prop}: ${value.trim()} }`);
          }
        }
      }
    }
    expect(offenders, "use --tbl-head-size / -weight / -tracking / -color").toEqual([]);
  });
});

describe("numeric columns", () => {
  it("aligns on the cell, which is the only place alignment works", () => {
    const ds = decls(readFileSync(DS_CSS, "utf8"));
    const rule = ds.slice(ds.indexOf(".mrs-num {"), ds.indexOf("}", ds.indexOf(".mrs-num {")));
    expect(rule).toMatch(/text-align:\s*end/);
    expect(rule).toMatch(/font-variant-numeric:\s*tabular-nums/);
    // A wrapped figure reads as two values to someone glancing down the column.
    expect(rule).toMatch(/white-space:\s*nowrap/);
  });

  it("is reached through the column flag, not by hand-wrapping the value", () => {
    // `.tnum` on a span sets the figure WIDTH and leaves the column ragged — that is exactly how this looked
    // solved for thirteen columns while being unsolved. A money cell that still wraps in `.tnum` is either a
    // column that should carry `numeric: true`, or an identifier that should not be in this shape at all.
    const offenders: string[] = [];
    for (const file of tsx()) {
      readFileSync(file, "utf8").split("\n").forEach((line, i) => {
        if (!/className="[^"]*\btnum\b/.test(line)) return;
        if (!/\bmoney\(/.test(line)) return;   // dates and IDs legitimately keep .tnum
        // Only inside a table. A `<dd>` in a definition list, a toolbar total and a chart's value label all
        // want tabular figures and have no column to align to — `.tnum` is exactly right for those, and a
        // check that flagged them would be telling the truth about the class and a lie about the defect.
        if (!/<t[dh]\b/.test(line) && !/\bcell:\s*\(/.test(line)) return;
        offenders.push(`${relative(process.cwd(), file)}:${i + 1}`);
      });
    }
    expect(offenders, "a money column wants `numeric: true`, not a .tnum span").toEqual([]);
  });

  it("does not right-align an identifier or a date", () => {
    // `numeric` means "a quantity you would COMPARE down the column". A case number, an MRN, an order
    // reference and a date are all made of numerals and are read left-to-right like words; pushing them to
    // the right edge breaks the alignment of whatever column sits beside them.
    const offenders: string[] = [];
    for (const file of tsx()) {
      readFileSync(file, "utf8").split("\n").forEach((line, i) => {
        if (!line.includes("numeric: true")) return;
        if (/fmt\.date|fmt\.dateTime|\bmrn\b|caseNo|orderNo|contractNo|rxNo/i.test(line)) {
          offenders.push(`${relative(process.cwd(), file)}:${i + 1}`);
        }
      });
    }
    expect(offenders, "an identifier or a date is not a numeric column").toEqual([]);
  });
});

describe("the table vocabulary", () => {
  it("stays closed — a new table joins one of the existing ones", () => {
    // The fifth vocabulary arrived as `<table style={{ ... }}>`, which no search for a table class could
    // find. A table with no class is either using a documented one or inventing a sixth.
    const KNOWN = ["mrs-wl", "mini-table", "pol-grid", "pol-costshare", "rx-dispense-table"];
    const offenders: string[] = [];
    for (const file of tsx()) {
      const src = readFileSync(file, "utf8");
      src.split("\n").forEach((line, i) => {
        const m = line.match(/<table\b([^>]*)>/);
        if (!m) return;
        const attrs = m[1];
        if (attrs.includes("style=")) {
          offenders.push(`${relative(process.cwd(), file)}:${i + 1} — inline-styled table`);
          return;
        }
        // The print slip's table is scoped by `.rx-slip table` and has its own typography on purpose.
        if (!attrs.includes("className") && /rx-slip/.test(src)) return;
        if (attrs.includes("className") && !KNOWN.some((k) => attrs.includes(k))) {
          offenders.push(`${relative(process.cwd(), file)}:${i + 1} — ${attrs.trim()}`);
        }
      });
    }
    expect(offenders, `a table uses one of: ${KNOWN.join(", ")}`).toEqual([]);
  });

  it("never draws a rule under its last row", () => {
    // The card's own edge already ends the table. The trailing hairline floated in the card's bottom padding
    // with nothing beneath it — `.mrs-wl` had always dropped it and the other four had not.
    const WHERE: [string, string][] = [
      ["mini-table", APP_CSS], ["pol-grid", APP_CSS], ["pol-costshare", APP_CSS],
      ["rx-dispense-table", APP_CSS], ["mrs-wl", DS_CSS],
    ];
    for (const [cls, css] of WHERE) {
      const text = decls(readFileSync(css, "utf8"));
      // The VALUE matters, not the presence of the word: the first version of this matched `border` inside
      // `border-bottom: 1px solid red` and passed while the hairline was still drawn — a check that asserted
      // nothing and reported success. Caught by probing it with the defect it exists to find.
      expect(text, `.${cls} must clear its last row's border`)
        .toMatch(new RegExp(`\\.${cls}[^{]*tr:last-child[^{]*\\{[^}]*border[\\w-]*:\\s*(0|none)`));
    }
  });
});

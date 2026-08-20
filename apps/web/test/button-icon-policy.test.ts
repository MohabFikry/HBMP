import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join, resolve } from "node:path";

/**
 * An action class wears the same glyph everywhere, or none anywhere.
 *
 * <b>The defect.</b> 80 of 317 buttons carried an icon, and the split ran THROUGH action classes rather than
 * between them: Save had `check2` on two of ten, Create had `plus` on one of four, Search had `search` on one
 * of five, Add on two of four. An icon that appears on two of ten Save buttons carries no information — it
 * reads as an inconsistency rather than as a cue, and it teaches an operator that the glyphs mean nothing,
 * which is worse than having none. `Icon`'s own doc comment makes the same argument for glyph REUSE; this is
 * the other half of it.
 *
 * <b>The policy.</b> Recurring cross-screen actions carry a glyph — Add/Create `plus`, Save/Submit/Commit
 * `check2`, Search `search`, Upload `upload`, Export `download`, Edit `pen`, open-a-person's-file `user`. One
 * -off contextual verbs (Validate, Apply, Retry, Send, Keep, Discard) and every dismissal (Cancel, Close,
 * Back) carry none.
 *
 * <b>Two deliberate carve-outs</b>, both encoded below rather than left to memory:
 *
 * <ul>
 *   <li>A DESTRUCTIVE confirm carries no glyph. `check2` on a red "Terminate" reads as approval and fights
 *       the thing the colour is saying, so the conditional-variant confirms stay bare.</li>
 *   <li>An ICON-ONLY button is a different control from a labelled one — the glyph is the whole button, not
 *       an ornament on a word — so it is not compared against the labelled members of its class.</li>
 * </ul>
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

interface Btn { file: string; line: number; label: string; icon: string; variant: string; iconOnly: boolean }

/** Every `<Button>` with its label expression, glyph and variant. */
function buttons(): Btn[] {
  const out: Btn[] = [];
  for (const file of tsxFiles(SRC)) {
    const src = readFileSync(file, "utf8");
    const re = /<Button\b/g;
    let m: RegExpExecArray | null;
    while ((m = re.exec(src))) {
      // Balance braces to find this element's own ">".
      let i = m.index + m[0].length, depth = 0, selfClosing = false;
      for (; i < src.length; i++) {
        const c = src[i];
        if (c === "{") depth++;
        else if (c === "}") depth--;
        else if (c === ">" && depth === 0) { selfClosing = src[i - 1] === "/"; break; }
      }
      const open = src.slice(m.index, i + 1);
      const body = selfClosing ? "" : src.slice(i + 1, src.indexOf("</Button>", i)).trim();
      // A glyph chosen by a ternary — `name={decision === "Reject" ? "cross" : "check2"}` — is the RIGHT
      // answer wherever the variant is conditional too, so it is recorded as its own value rather than read
      // as "no icon", which is what a literal-only regex did.
      const literal = /leadingIcon=\{<Icon name="([a-z0-9]+)"/.exec(open);
      const conditional = /leadingIcon=\{<Icon name=\{/.test(open);
      out.push({
        file: file.slice(SRC.length + 1),
        line: src.slice(0, m.index).split("\n").length,
        label: body.replace(/\s+/g, " "),
        icon: literal?.[1] ?? (conditional ? "(conditional)" : ""),
        // The raw expression, not a parsed name: `variant={x ? "danger" : "primary"}` is a button that can be
        // destructive, and reading only the first identifier out of it says "decision", which is nothing.
        variant: /variant=(\{[^\n]*?\}|"[a-z]+")/.exec(open)?.[1] ?? '"secondary"',
        iconOnly: body === "" || /^<Icon\b[^>]*\/>$/.test(body),
      });
    }
  }
  return out;
}

/** The glyph each recurring action class must wear. */
const POLICY: Record<string, string> = {
  add: "plus", create: "plus",
  save: "check2", submit: "check2", commit: "check2", confirm: "check2",
  search: "search", upload: "upload", export: "download", edit: "pen",
  openProfile: "user", openFile: "user",
};

/** `t(S.save)` → `save`. Buttons whose label is not a simple string token are not classified. */
function actionOf(label: string): string | null {
  const m = /^\{t\([A-Z]\.(\w+)\)\}$/.exec(label);
  return m && m[1] in POLICY ? m[1] : null;
}

describe("the button icon policy holds across the product", () => {
  const all = buttons();

  it("sees the buttons — otherwise every assertion here passes vacuously", () => {
    expect(all.length).toBeGreaterThan(300);
    expect(all.filter((b) => b.icon).length).toBeGreaterThan(80);
  });

  it("classifies real buttons — the label regex must not have stopped matching", () => {
    const classified = all.filter((b) => actionOf(b.label));
    expect(classified.length).toBeGreaterThan(35);
  });

  it("gives every member of a recurring action class its glyph", () => {
    const offenders = all
      .filter((b) => !b.iconOnly)
      // A destructive confirm carries no glyph — a check on a red button reads as approval. That covers the
      // ternary variants too: `{kind === "terminate" ? "danger" : "primary"}` is a button that turns red, and
      // it is the red state the rule is about.
      .filter((b) => !b.variant.includes("danger"))
      // A conditional glyph is the correct answer where the variant is conditional; it is checked by the
      // author against the same condition, and there is nothing useful to compare it to here.
      .filter((b) => b.icon !== "(conditional)")
      .filter((b) => {
        const action = actionOf(b.label);
        return action !== null && b.icon !== POLICY[action];
      })
      .map((b) => `${b.file}:${b.line} ${b.label} has icon="${b.icon || "none"}", policy says ` +
        `"${POLICY[actionOf(b.label)!]}"`);
    expect(offenders, "see the policy in this file's header").toEqual([]);
  });

  it("keeps dismissals and one-off verbs bare", () => {
    // The other half of the rule. A glyph on Cancel makes it compete with the action beside it.
    const BARE = ["cancel", "close", "back", "keep", "discard", "clear", "validate", "apply", "retry", "send"];
    const offenders = all
      .filter((b) => !b.iconOnly && b.icon !== "")
      .filter((b) => {
        const m = /^\{t\([A-Z]\.(\w+)\)\}$/.exec(b.label);
        return m !== null && BARE.includes(m[1]);
      })
      .map((b) => `${b.file}:${b.line} ${b.label} should carry no glyph but has "${b.icon}"`);
    expect(offenders).toEqual([]);
  });

  it("never puts plus on something that is not a creation", () => {
    // `plus` means add-a-thing. It was on a "Send reply" button, which adds nothing the operator thinks of
    // as a thing — and a glyph that means two things is the failure `Icon`'s header warns about.
    const offenders = all
      .filter((b) => b.icon === "plus" && !b.iconOnly)
      .filter((b) => !/add|create|new|another|line|item|note|document|row/i.test(b.label))
      .map((b) => `${b.file}:${b.line} ${b.label}`);
    expect(offenders, "`plus` labels a creation — pick a glyph that means this action, or none").toEqual([]);
  });
});

/**
 * A variant the design system offers and nothing uses is a level nobody has defined.
 *
 * `warn` was exactly that — shipped, styled, and never once reached for in the life of the product. The
 * first screen to use it would have been inventing its meaning, and the second would have invented a
 * different one, which is the failure this whole file is about.
 *
 * This lives in the WEB suite rather than the design system's, deliberately: the design system cannot see
 * whether anything uses it, and "is this level real?" is a question only the product can answer.
 */
describe("every button variant the design system offers is one the product uses", () => {
  const BUTTON_TSX = resolve(__dirname, "../../design-system/src/components/Button.tsx");

  it("reads the variant union — otherwise this asserts nothing", () => {
    const union = /export type ButtonVariant =([^;]+);/.exec(readFileSync(BUTTON_TSX, "utf8"));
    expect(union).not.toBeNull();
    expect(union![1].match(/"[a-z]+"/g)!.length).toBeGreaterThanOrEqual(3);
  });

  it("has a call site for each of them", () => {
    const union = /export type ButtonVariant =([^;]+);/.exec(readFileSync(BUTTON_TSX, "utf8"))![1];
    const variants = union.match(/"([a-z]+)"/g)!.map((v) => v.replace(/"/g, ""));
    const all = buttons();
    const unused = variants.filter((v) =>
      // `secondary` is the DEFAULT, so a button can wear it without naming it.
      v !== "secondary" && !all.some((b) => b.variant.includes(v)));
    expect(
      unused,
      "this variant is offered and never used. Either a screen needs it — in which case use it and say what " +
        "the level means — or it should come out, because an unused one gets used inconsistently later.",
    ).toEqual([]);
  });
});

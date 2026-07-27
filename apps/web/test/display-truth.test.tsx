import { describe, expect, it } from "vitest";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { renderHook } from "@testing-library/react";
import { ThemeProvider } from "@mersal/design-system";
import type { ReactNode } from "react";
import { memberStatus, MEMBER_STATUSES } from "../src/screens/statusLabels";
import { useFormat, DISPLAY_TIME_ZONE } from "../src/i18n/useFormat";

/**
 * Phase 18.D2 (audit R2 U3/U7/U8) — what the screen SAYS must be what the data MEANS.
 *
 * Three separate ways the UI was telling the truth about one thing while displaying another: a status chip
 * whose colour was hardcoded green regardless of the status it labelled; times rendered in the machine's
 * zone rather than Cairo; and CSS referring to design tokens that do not exist, so every rule using them
 * silently fell back.
 */

// import.meta.url resolves oddly under the vitest transform here; anchor on the repo root instead.
const ROOT = process.cwd();
const SRC = join(ROOT, "src");
const TOKENS = join(ROOT, "..", "design-system", "src", "tokens", "tokens.css");

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) walk(full, out);
    else if (/\.(ts|tsx)$/.test(entry)) out.push(full);
  }
  return out;
}

describe("U3 — a status chip must not lie", () => {
  it("never renders a non-covered member as green", () => {
    // The defect: `<StatusChip kind="ok" label={summary.identity.status} />` — the LABEL came from the
    // server and the COLOUR was hardcoded. A Suspended, Expired or Blocked member displayed as green with
    // the real word beside it in small text, and an agent under call pressure reads the colour. The
    // consequence is telling a suspended member their coverage is fine.
    for (const state of ["Suspended", "Expired", "Blocked"])
      expect(memberStatus(state).kind).not.toBe("ok");

    expect(memberStatus("Active").kind).toBe("ok");
  });

  it("labels every member state in both languages", () => {
    for (const [state, v] of Object.entries(MEMBER_STATUSES)) {
      expect(v.label.en.length, state).toBeGreaterThan(0);
      expect(v.label.ar.length, state).toBeGreaterThan(0);
      // The raw enum must not leak into the Arabic UI.
      expect(v.label.ar, state).not.toBe(state);
    }
  });

  it("shows an unrecognised state neutrally rather than confidently", () => {
    // Honest: "I do not know what this means" beats a confident green on a value we have never seen.
    const unknown = memberStatus("SomeNewStateWeHaveNotMappedYet");
    expect(unknown.kind).toBe("neu");
    expect(unknown.label.en).toBe("SomeNewStateWeHaveNotMappedYet");
  });
});

describe("U7 — dates, times and money are Cairo + the app locale", () => {
  const wrap = ({ children }: { children: ReactNode }) => <ThemeProvider>{children}</ThemeProvider>;

  it("renders a time in Africa/Cairo regardless of the machine's zone", () => {
    // The headline case. 06:00Z is 08:00 in Cairo (UTC+2 in winter). A clinic PC set to UTC — the default
    // on a fresh Linux image and inside every container — used to render this as 06:00, and the patient was
    // told the wrong time for an appointment the system had booked correctly.
    const { result } = renderHook(() => useFormat(), { wrapper: wrap });
    expect(result.current.time("2026-01-15T06:00:00Z")).toBe("08:00");
    expect(DISPLAY_TIME_ZONE).toBe("Africa/Cairo");
  });

  it("still renders Cairo time across the DST boundary", () => {
    // Egypt observes DST again since 2023: the offset is +2 in January and +3 in July, so a fixed offset
    // would be wrong for half the year. Intl with a zone handles it; a hardcoded +2 would not.
    const { result } = renderHook(() => useFormat(), { wrapper: wrap });
    expect(result.current.time("2026-07-15T06:00:00Z")).toBe("09:00");
  });

  it("formats money as EGP from a raw number", () => {
    const { result } = renderHook(() => useFormat(), { wrapper: wrap });
    const out = result.current.money(12400);
    expect(out).toMatch(/12,400|١٢٬٤٠٠/);
    expect(out).toMatch(/EGP|ج\.م/);
  });

  it("renders a missing value as an em dash, not a blank or 'Invalid Date'", () => {
    const { result } = renderHook(() => useFormat(), { wrapper: wrap });
    expect(result.current.date(null)).toBe("—");
    expect(result.current.dateTime(undefined)).toBe("—");
    expect(result.current.money(null)).toBe("—");
    expect(result.current.date("not a date")).toBe("—");
  });

  it("no screen calls toLocaleDateString/toLocaleString/toLocaleTimeString directly", () => {
    // The guard. Every bare call formats in the MACHINE's zone and the BROWSER's locale, and neither is
    // the right answer here. useFormat is the only sanctioned path.
    const offenders = walk(SRC)
      .filter((f) => !f.endsWith("i18n/useFormat.ts"))
      .filter((f) => /\.toLocale(Date|Time)?String\s*\(/.test(readFileSync(f, "utf8")))
      .map((f) => f.replace(SRC, "src"));

    expect(offenders).toEqual([]);
  });
});

describe("U8 — every CSS token the app references must exist", () => {
  it("references no undefined design token", () => {
    // `var(--radius-2, 8px)`, `var(--brand-teal)` and `var(--status-bad-fg)` were never defined, so every
    // rule using them silently fell back to its hardcoded second argument — or to nothing. The app looked
    // almost right, which is why it survived review: the fallbacks were close to the real values.
    const css = readFileSync(join(SRC, "styles/app.css"), "utf8");
    const tokens = readFileSync(TOKENS, "utf8");

    const used = new Set([...css.matchAll(/var\((--[a-z0-9-]+)/gi)].map((m) => m[1]!));
    const defined = new Set([...tokens.matchAll(/^\s*(--[a-z0-9-]+)\s*:/gim)].map((m) => m[1]!));
    // Locally-scoped variables the stylesheet defines for itself are legitimate.
    const local = new Set([...css.matchAll(/^\s*(--[a-z0-9-]+)\s*:/gim)].map((m) => m[1]!));

    const missing = [...used].filter((v) => !defined.has(v) && !local.has(v)).sort();
    expect(missing).toEqual([]);
  });

  it("uses no legacy hardcoded brand hex", () => {
    // #1d9ba6 / #16808d predate the token system and do not track the dark theme, so a var() fallback to
    // them renders a light-theme colour on a dark background.
    const css = readFileSync(join(SRC, "styles/app.css"), "utf8");
    expect(css).not.toMatch(/#1d9ba6|#16808d/i);
  });
});

describe("U4 — the app is navigable on a phone", () => {
  it("keeps the nav rail on screen below 760px instead of hiding it", () => {
    // It used to be `nav.mrs-rail { display: none }` with nothing in its place: on a phone the app had no
    // navigation at all, so whatever screen you landed on was the only one you could reach.
    const css = readFileSync(join(SRC, "styles/app.css"), "utf8");
    const mobile = css.slice(css.indexOf("@media (max-width: 760px)"));
    const block = mobile.slice(0, mobile.indexOf("\n}\n\n"));

    expect(block).not.toMatch(/nav\.mrs-rail\s*\{\s*display:\s*none/);
    expect(block).toMatch(/position:\s*fixed/);
    expect(block).toMatch(/min-height:\s*(4[4-9]|[5-9]\d)px/);   // the project's own ≥44px target bar
  });
});

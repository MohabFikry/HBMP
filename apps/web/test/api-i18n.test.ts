import { describe, expect, it } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";

/**
 * Phase 24 Gate 4.3 — English text in the Arabic field is worse than a missing translation.
 *
 * The portal contracts carry every user-facing label as `{ en, ar }`. HttpApiClient had one helper,
 * `loc(s)`, that filled BOTH fields from the same string, and it was doing two different jobs:
 *
 *   * legitimately — a value with no language at all: an ICD or CPT code, a masked identifier, a drug name
 *     as the formulary records it. Same text both sides is the correct answer.
 *   * as a bug — English UI text the service does not send a label for: "Under review", "Prescriber",
 *     "Awaiting decision". Copying English into `ar` shows English to an Arabic-reading user, and makes the
 *     payload LOOK translated to anything that checks both fields are populated. Ten literals had reached
 *     the contracts that way.
 *
 * The helpers are now named for the two cases — `neutral()` and `t(en, ar)` — and this holds the line: a
 * hardcoded English literal may not reach `neutral()`, because a literal in this file is by definition text
 * the API layer authored rather than data a service sent.
 */
const SOURCE = resolve(__dirname, "../src/api/HttpApiClient.ts");

describe("Gate 4.3 — the Arabic field carries Arabic", () => {
  const src = readFileSync(SOURCE, "utf8");

  it("never passes a hardcoded literal to the language-neutral wrapper", () => {
    // `neutral("...")` — a string literal the API layer wrote itself, duplicated into `ar`.
    const offenders = [...src.matchAll(/neutral\(\s*"([^"]+)"/g)].map((m) => m[1]);
    expect(
      offenders,
      `these English literals would be copied into the Arabic field verbatim; use t("en", "ar"):\n  ${offenders.join("\n  ")}`,
    ).toEqual([]);
  });

  it("still has both helpers, so the rule cannot be satisfied by deleting the distinction", () => {
    expect(src).toContain("const neutral =");
    expect(src).toContain("const t = (en: string, ar: string)");
  });

  it("gives every t() call a genuinely different Arabic string", () => {
    // A translation that copies the English defeats the point as thoroughly as the original bug did.
    const lazy = [...src.matchAll(/\bt\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\)/g)]
      .filter(([, en, ar]) => en === ar)
      .map(([, en]) => en);
    expect(lazy, `t() called with identical en/ar — use neutral() if the value has no language:\n  ${lazy.join("\n  ")}`)
      .toEqual([]);
  });

  it("uses Arabic script in the translations it does carry", () => {
    const pairs = [...src.matchAll(/\bt\(\s*"([^"]+)"\s*,\s*"([^"]+)"\s*\)/g)];
    expect(pairs.length, "the translations this test guards must exist to be guarded").toBeGreaterThan(5);
    const nonArabic = pairs.filter(([, , ar]) => !/[؀-ۿ]/.test(ar)).map(([, en]) => en);
    expect(nonArabic, `these t() calls have no Arabic script in the ar field:\n  ${nonArabic.join("\n  ")}`)
      .toEqual([]);
  });
});

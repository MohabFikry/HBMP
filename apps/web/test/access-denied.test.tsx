import { describe, expect, it } from "vitest";
import { render, screen } from "@testing-library/react";
import { ThemeProvider } from "@mersal/design-system";
import { AccessDenied, kindFromProblemType } from "../src/routing/AccessDenied";
import { L } from "../src/i18n/strings";

/**
 * Phase 21.6 — the three 403 treatments must render DISTINCTLY (design 40 §4/§6).
 *
 * All three are HTTP 403. The whole point of separating them is that the remedies are different people —
 * your administrator, Mersal, or you. A test that only checked "a 403 page renders" would pass just as
 * happily after someone collapsed them back into one generic page.
 */
function renderIn(lang: "en" | "ar", ui: React.ReactElement) {
  return render(<ThemeProvider lang={lang}>{ui}</ThemeProvider>);
}

describe("403 treatments (21.6)", () => {
  it("maps each problem type to its own treatment", () => {
    expect(kindFromProblemType("https://mersal.foundation/problems/program-not-enabled"))
      .toBe("program-not-enabled");
    expect(kindFromProblemType("https://mersal.foundation/problems/program-limit-reached"))
      .toBe("program-limit-reached");
    expect(kindFromProblemType("urn:hbmp:branch-out-of-scope")).toBe("branch-out-of-scope");
    expect(kindFromProblemType("urn:hbmp:claims-access-denied")).toBe("forbidden");
  });

  it("falls back to the permission denial for an unknown type", () => {
    // The safe default: never claim the platform is at fault when we do not know why.
    expect(kindFromProblemType(undefined)).toBe("forbidden");
    expect(kindFromProblemType("something-nobody-has-seen")).toBe("forbidden");
  });

  it("THE acceptance case — the three treatments render different copy and different actions", () => {
    const { unmount: u1 } = renderIn("en", <AccessDenied kind="forbidden" onRequestAccess={() => {}} />);
    expect(screen.getByRole("heading")).toHaveTextContent(L.forbiddenTitle.en);
    expect(screen.getByRole("button", { name: L.requestAccess.en })).toBeInTheDocument();
    // It must NOT tell someone with a permission problem to contact Mersal.
    expect(screen.queryByRole("button", { name: L.contactMersal.en })).not.toBeInTheDocument();
    u1();

    const { unmount: u2 } = renderIn("en", <AccessDenied kind="program-not-enabled" detailKey="claims" />);
    expect(screen.getByRole("heading")).toHaveTextContent(L.notEnabledTitle.en);
    expect(screen.getByRole("button", { name: L.contactMersal.en })).toBeInTheDocument();
    // …and it must NOT tell someone with an enablement gap to ask their own administrator.
    expect(screen.queryByRole("button", { name: L.requestAccess.en })).not.toBeInTheDocument();
    u2();

    renderIn("en", <AccessDenied kind="branch-out-of-scope" onSwitchBranch={() => {}} />);
    expect(screen.getByRole("heading")).toHaveTextContent(L.branchOutOfScopeTitle.en);
    // The only one the user can fix themselves, so it offers the switcher rather than a person to ask.
    expect(screen.getByRole("button", { name: L.switchBranch.en })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: L.contactMersal.en })).not.toBeInTheDocument();
  });

  it("names the specific programme rather than showing a generic wall", () => {
    renderIn("en", <AccessDenied kind="program-not-enabled" detailKey="callcentre" />);
    // A generic wall produces a support ticket that has to be answered with a question.
    expect(screen.getByText("callcentre")).toBeInTheDocument();
  });

  it("A4 — the not-enabled copy never reads as a paywall", () => {
    renderIn("en", <AccessDenied kind="program-not-enabled" />);
    const text = document.body.textContent ?? "";
    for (const word of ["upgrade", "subscription", "billing", "purchase", "trial", "pricing"]) {
      expect(text.toLowerCase()).not.toContain(word);
    }
  });

  it("every treatment has real Arabic copy, not an English fallback", () => {
    // ThemeProvider resolves the language at module load from localStorage, so the RENDERED language is
    // pinned by the app-level RTL tests rather than here. What this file must guarantee is that the copy
    // EXISTS in both languages — a missing Arabic string does not fail a render, it silently shows English
    // to an Arabic-speaking user, which is exactly the kind of gap nobody reports.
    const pairs = [
      L.notEnabledTitle, L.notEnabledBody, L.contactMersal,
      L.limitReachedTitle, L.limitReachedBody,
      L.branchOutOfScopeTitle, L.branchOutOfScopeBody, L.switchBranch,
    ];
    for (const p of pairs) {
      expect(p.ar.trim().length).toBeGreaterThan(0);
      expect(p.ar).not.toBe(p.en);
      // Arabic script, not a transliteration or a copy-paste of the English.
      expect(p.ar).toMatch(/[\u0600-\u06FF]/);
    }
  });

  it("carries a non-colour cue for each treatment", () => {
    // 21-accessibility: status is never carried by hue alone. The data-treatment hook is the machine-
    // readable cue, and each heading text differs, which is the human one.
    const { container, unmount } = renderIn("en", <AccessDenied kind="program-limit-reached" />);
    expect(container.querySelector('[data-treatment="program-limit-reached"]')).toBeTruthy();
    unmount();

    const { container: c2 } = renderIn("en", <AccessDenied kind="forbidden" />);
    expect(c2.querySelector('[data-treatment="forbidden"]')).toBeTruthy();
  });
});

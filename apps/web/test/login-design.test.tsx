import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { ThemeProvider } from "@mersal/design-system";
import { LoginPage } from "../src/pages/LoginPage";
import { AuthProvider } from "../src/auth/AuthProvider";
import { DevAuthClient } from "../src/auth/authClient";

/**
 * Phase 28.8 — the redesigned sign-in screen.
 *
 * <p>
 * Layout is not asserted here — jsdom has no layout engine, and a test that counted grid columns would pass
 * on a broken page and fail on a fixed one. What IS asserted is everything a redesign can quietly get wrong
 * and nothing else would catch: a number that stops being true, a decorative layer announced to a screen
 * reader, a direction-specific rule that breaks Arabic, and the sign-in itself still working underneath.
 * </p>
 */
vi.mock("../src/config", async () => {
  const actual = await vi.importActual<typeof import("../src/config")>("../src/config");
  return { ...actual, LIVE: true };
});

vi.mock("../src/auth/oidcClient", async () => {
  const actual = await vi.importActual<typeof import("../src/auth/oidcClient")>("../src/auth/oidcClient");
  return { ...actual, silentAuthorize: vi.fn(async () => new Promise<never>(() => {})) };
});

let posted: unknown[] = [];

beforeEach(() => {
  posted = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    if (String(input).endsWith("/antiforgery")) {
      return new Response(JSON.stringify({ token: "csrf" }), {
        status: 200, headers: { "Content-Type": "application/json" },
      });
    }
    posted.push(init?.body ? JSON.parse(String(init.body)) : undefined);
    return new Response(JSON.stringify({ status: "authenticated" }), {
      status: 200, headers: { "Content-Type": "application/json" },
    });
  }));
});
afterEach(() => vi.unstubAllGlobals());

function renderLogin() {
  return render(
    <ThemeProvider>
      <AuthProvider client={new DevAuthClient()}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <LoginPage />
        </MemoryRouter>
      </AuthProvider>
    </ThemeProvider>,
  );
}

describe("the sign-in hero", () => {
  it("carries no statistics tiles", () => {
    // They were removed by request. Asserted rather than simply deleted, because the version before them
    // quoted "18 role portals" against a catalog holding 21 — if tiles ever come back, whatever number they
    // print has to be derived, and this failing is the prompt to remember that.
    const { container } = renderLogin();
    expect(container.querySelector(".login-stats")).toBeNull();
    expect(container.querySelector(".login-stat")).toBeNull();
  });

  it("breaks the headline with markup rather than a newline in the string", () => {
    // A \n collapses to a space in HTML, so a two-line headline written that way silently renders as one —
    // and an Arabic translation may want to break somewhere else entirely.
    const { container } = renderLogin();
    const headline = container.querySelector(".login-headline");
    expect(headline?.querySelector("br")).not.toBeNull();
    expect(headline?.textContent).toContain("One platform.");
    expect(headline?.textContent).toContain("Every step of care.");
  });

  it("keeps the hero on small screens instead of hiding it", () => {
    // It is the only thing on the page that says whose system this is; a bare form on a phone could be
    // anybody's. The stylesheet collapses to one column and trims the hero, and must never `display:none` it.
    const css = readFileSync(resolve(__dirname, "..", "src/styles/app.css"), "utf-8");
    const media = css.slice(css.indexOf("@media (max-width: 900px)"));
    expect(media).toContain("grid-template-columns: 1fr");
    expect(media).not.toMatch(/\.login-hero\s*\{[^}]*display:\s*none/);
  });

  it("announces nothing decorative to a screen reader", () => {
    // Three stacked gradient layers with no meaning. Without aria-hidden they are three things read out
    // before the headline on the one page every user meets first.
    const { container } = renderLogin();
    for (const cls of ["login-hero-waves", "login-hero-glow", "login-hero-glass", "login-hero-grain"]) {
      const el = container.querySelector(`.${cls}`);
      expect(el, `${cls} should be rendered`).not.toBeNull();
      expect(el).toHaveAttribute("aria-hidden", "true");
    }
  });

  it("uses the dark lockup on the hero regardless of theme", () => {
    // The hero is deep teal in BOTH themes. The theme-picked asset would put a teal wordmark on teal in
    // light mode — about 2:1, the exact problem the dark asset was made to fix.
    const src = readFileSync(resolve(__dirname, "..", "src/pages/LoginPage.tsx"), "utf-8");
    expect(src).toMatch(/variant="lockup"[^/]*onDark/s);
  });
});

describe("the sign-in card", () => {
  it("still signs in, with the redesign on top", async () => {
    renderLogin();
    const u = userEvent.setup();
    await u.type(screen.getByLabelText(/username/i), "nurse.mona");
    await u.type(screen.getByLabelText(/^password/i), "correct-horse");
    await u.click(screen.getByRole("button", { name: /sign in/i }));

    expect(posted.length).toBeGreaterThan(0);
  });

  it("offers remember-this-device OFF by default, and sends what was chosen", async () => {
    // A checkbox that changes nothing is a lie on screen. This one is wired to the issuer cookie's
    // persistence — and it stays off unless somebody ticks it, because Mersal's clinic workstations are
    // shared and a persistent cookie means the next person at that terminal is signed in as the last.
    renderLogin();
    const box = screen.getByRole("checkbox", { name: /remember this device/i });
    expect(box).not.toBeChecked();

    const u = userEvent.setup();
    await u.click(box);
    await u.type(screen.getByLabelText(/username/i), "nurse.mona");
    await u.type(screen.getByLabelText(/^password/i), "correct-horse");
    await u.click(screen.getByRole("button", { name: /sign in/i }));

    expect(posted[0]).toMatchObject({ rememberDevice: true });
  });

  it("puts the forgot-password link where somebody looks for it", async () => {
    renderLogin();
    expect(screen.getByRole("link", { name: /forgot password/i })).toHaveAttribute("href", "/forgot-password");
  });

  it("keeps remember-device and forgot-password on the same row", () => {
    // It wrapped before, which put the link on a line of its own directly above the submit button and made
    // an aside look like a second action.
    const { container } = renderLogin();
    const meta = container.querySelector(".login-meta");
    expect(meta?.querySelector("input[type=checkbox]")).not.toBeNull();
    expect(meta?.querySelector("a[href='/forgot-password']")).not.toBeNull();
  });

  it("shows no required asterisks, and keeps the fields required", () => {
    // Both fields are required in a two-field form, so the marks were noise. Only the VISUAL mark goes:
    // `.mrs-req` is already aria-hidden, and `required` stays on the input.
    renderLogin();
    expect(screen.getByLabelText(/username/i)).toBeRequired();
    const css = readFileSync(resolve(__dirname, "..", "src/styles/app.css"), "utf-8");
    expect(css).toMatch(/\.login-card \.mrs-req \{\s*display: none/);
  });

  it("offers language and theme switches, which the app had nowhere else", async () => {
    // `setLang`/`setTheme` existed on the provider and no UI in the application called either.
    renderLogin();
    expect(screen.getByRole("button", { name: /switch language/i })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /switch theme/i })).toBeInTheDocument();
  });

  it("switching language re-renders the page in Arabic", async () => {
    renderLogin();
    const u = userEvent.setup();
    await u.click(screen.getByRole("button", { name: /switch language/i }));
    expect(await screen.findByLabelText(/اسم المستخدم/)).toBeInTheDocument();
  });
});

describe("direction and motion", () => {
  const css = readFileSync(resolve(__dirname, "..", "src/styles/app.css"), "utf-8");
  const block = css.slice(css.indexOf("LOGIN — the split hero"));

  it("uses logical properties for the things that must mirror", () => {
    // A `left:` on the field icon puts it on top of Arabic text; a `border-right` on the glass pane puts the
    // seam on the outside edge. Both are invisible in English and wrong in half the deployment.
    expect(block).toContain("inset-inline-start");
    expect(block).toContain("border-inline-end");
  });

  it("mirrors the submit arrow, because 'forward' points left in Arabic", () => {
    expect(block).toMatch(/\[dir="rtl"\]\s*\.login-submit-arrow\s*\{\s*transform:\s*scaleX\(-1\)/);
  });

  it("centres the field icon on the CONTROL, not on the label-plus-control box", () => {
    // The first version positioned it at 50% of a wrapper that includes the label, so it sat visibly high.
    // `display: contents` flattens the field into this grid and the icon shares the control's cell, which
    // centres it at any label length and with or without an error message underneath.
    expect(block).toMatch(/\.login-field-icon > \.mrs-field \{\s*display: contents/);
    expect(block).toMatch(/\.login-field-icon > svg \{[^}]*align-self: center/);
  });

  it("gives the two toggles the same box", () => {
    // Two controls doing the same KIND of thing at two different sizes is the misalignment you see first.
    expect(block).toMatch(/\.login-icon-btn \{[^}]*inline-size: 44px[^}]*block-size: 44px/);
  });

  it("stops the wave and the card entrance under prefers-reduced-motion", () => {
    // Both are decoration and nothing depends on them, so they simply stop.
    const reduced = block.match(/@media \(prefers-reduced-motion: reduce\) \{[^}]*\{[^}]*\}[^}]*\}/g) ?? [];
    expect(reduced.join(" ")).toContain("login-hero-waves");
    expect(reduced.join(" ")).toContain("login-card");
  });

  it("keeps the decorative hero palette out of the shared token file", () => {
    // The gradient stops are DECORATIVE and scoped to `.login-split`. In tokens.css they would look like
    // accessible colours and end up on text.
    const tokens = readFileSync(
      resolve(__dirname, "..", "..", "design-system", "src", "tokens", "tokens.css"), "utf-8");
    expect(tokens).not.toContain("--login-teal-1");
    expect(block).toContain("--login-teal-1");
  });
});

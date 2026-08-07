import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { ThemeProvider } from "@mersal/design-system";
import { ForgotPasswordPage } from "../src/pages/ForgotPasswordPage";
import { ResetPasswordPage } from "../src/pages/ResetPasswordPage";

/**
 * Phase 28.6 — asking for a reset link, and using one (ADR-0036 §6).
 *
 * The two screens have opposite obligations, and both are tested here because getting either backwards is the
 * failure that matters. The FORGOT screen must be deliberately vague about whether an account exists — and
 * completely unambiguous about whether anything was actually sent. The RESET screen must say what a reset
 * costs BEFORE the fields, not after the deed.
 */

let responses: Array<{ status: number; body?: unknown }> = [];
let posted: Array<{ url: string; body: unknown }> = [];

function fakeIssuer() {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.endsWith("/antiforgery")) {
      return new Response(JSON.stringify({ token: "csrf-1" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }
    posted.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const next = responses.shift() ?? { status: 500 };
    return new Response(next.body ? JSON.stringify(next.body) : "", {
      status: next.status,
      headers: { "Content-Type": "application/json" },
    });
  });
}

const wrap = (ui: React.ReactNode, path = "/") =>
  render(
    <ThemeProvider>
      <MemoryRouter initialEntries={[path]} future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
        <Routes>
          <Route path="/forgot-password" element={ui} />
          <Route path="/reset-password" element={ui} />
          <Route path="/" element={ui} />
          <Route path="/login" element={<p>login</p>} />
        </Routes>
      </MemoryRouter>
    </ThemeProvider>,
  );

beforeEach(() => {
  responses = [];
  posted = [];
  vi.stubGlobal("fetch", fakeIssuer());
});
afterEach(() => vi.unstubAllGlobals());

describe("asking for a reset link", () => {
  it("says the same thing whether or not the account exists", async () => {
    // The server answers 202 either way, so this screen must say something true either way. Anything more
    // precise is a free account-existence oracle costing an attacker nothing — strictly worse than the login
    // form, which at least burns an attempt against a lockout counter.
    responses = [{ status: 202 }];
    wrap(<ForgotPasswordPage />, "/forgot-password");
    const u = userEvent.setup();
    await u.type(screen.getByLabelText(/username/i), "nurse.mona");
    await u.click(screen.getByRole("button", { name: /send reset link/i }));

    const sent = await screen.findByTestId("forgot-sent");
    expect(sent).toHaveTextContent(/if that account exists/i);
    expect(sent).not.toHaveTextContent(/nurse\.mona/);
  });

  it("says a link expires and works once, so an unused one is not assumed live", async () => {
    responses = [{ status: 202 }];
    wrap(<ForgotPasswordPage />, "/forgot-password");
    const u = userEvent.setup();
    await u.type(screen.getByLabelText(/username/i), "someone");
    await u.click(screen.getByRole("button", { name: /send reset link/i }));

    expect(await screen.findByTestId("forgot-sent")).toHaveTextContent(/once.*30 minutes|30 minutes/i);
  });

  it("NEVER claims a link was sent when the server says delivery is unavailable", async () => {
    // THE test. "We've emailed you a link" when nothing could be emailed is a failed operation rendered as a
    // clean result, on the one screen a locked-out person reaches when nothing else works.
    responses = [{ status: 503 }];
    wrap(<ForgotPasswordPage />, "/forgot-password");
    const u = userEvent.setup();
    await u.type(screen.getByLabelText(/username/i), "nurse.mona");
    await u.click(screen.getByRole("button", { name: /send reset link/i }));

    expect(await screen.findByTestId("forgot-error")).toHaveTextContent(/isn't available/i);
    expect(screen.queryByTestId("forgot-sent")).not.toBeInTheDocument();
  });

  it("warns that a reset will not solve a lost authenticator", async () => {
    // Said on the way IN. Discovering it after changing a password is discovering it too late.
    responses = [{ status: 202 }];
    wrap(<ForgotPasswordPage />, "/forgot-password");
    const u = userEvent.setup();
    await u.type(screen.getByLabelText(/username/i), "someone");
    await u.click(screen.getByRole("button", { name: /send reset link/i }));

    await screen.findByTestId("forgot-sent");
    expect(screen.getByText(/does not turn off two-step/i)).toBeInTheDocument();
  });
});

describe("using a reset link", () => {
  const link = "/reset-password?u=11111111-1111-1111-1111-111111111111&t=abc123";

  it("says what a reset costs BEFORE the fields", async () => {
    wrap(<ResetPasswordPage />, link);
    const consequences = screen.getByTestId("reset-consequences");
    expect(consequences).toHaveTextContent(/signs you out everywhere/i);
    expect(consequences).toHaveTextContent(/does not turn off two-step/i);
  });

  it("refuses a link with nothing in it rather than offering a form that cannot work", async () => {
    // Rendering the fields anyway would let somebody type a new password twice and only then be told the
    // link was never valid.
    wrap(<ResetPasswordPage />, "/reset-password");
    expect(screen.getByTestId("reset-error")).toHaveTextContent(/no longer valid/i);
    expect(screen.queryByLabelText(/new password/i)).not.toBeInTheDocument();
  });

  it("catches a mismatched confirmation before sending anything", async () => {
    // The server only ever sees ONE password, so a typo in the confirmation would silently set the wrong one.
    wrap(<ResetPasswordPage />, link);
    const u = userEvent.setup();
    await u.type(screen.getByLabelText(/^new password/i), "Correct-Horse-99!");
    await u.type(screen.getByLabelText(/confirm/i), "Correct-Horse-98!");
    await u.click(screen.getByRole("button", { name: /set new password/i }));

    expect(await screen.findByText(/don't match/i)).toBeInTheDocument();
    expect(posted.filter((p) => p.url.includes("/password/reset"))).toHaveLength(0);
  });

  it("confirms the sessions ended, so nobody wonders whether the other tabs are still live", async () => {
    responses = [{ status: 200, body: { reset: true } }];
    wrap(<ResetPasswordPage />, link);
    const u = userEvent.setup();
    await u.type(screen.getByLabelText(/^new password/i), "Correct-Horse-99!");
    await u.type(screen.getByLabelText(/confirm/i), "Correct-Horse-99!");
    await u.click(screen.getByRole("button", { name: /set new password/i }));

    expect(await screen.findByTestId("reset-done")).toHaveTextContent(/every other session has ended/i);
  });

  it("passes through password-policy advice, which the person can act on", async () => {
    // A 422 says "at least 12 characters" — actionable, and revealing nothing about any account. Collapsing
    // it into "that link is invalid" would send someone to request a new link over a short password.
    responses = [{ status: 422, body: { detail: "Passwords must be at least 12 characters." } }];
    wrap(<ResetPasswordPage />, link);
    const u = userEvent.setup();
    await u.type(screen.getByLabelText(/^new password/i), "short");
    await u.type(screen.getByLabelText(/confirm/i), "short");
    await u.click(screen.getByRole("button", { name: /set new password/i }));

    expect(await screen.findByText(/at least 12 characters/i)).toBeInTheDocument();
  });

  it("reports an expired or reused link as exactly that", async () => {
    responses = [{ status: 400, body: { title: "invalid" } }];
    wrap(<ResetPasswordPage />, link);
    const u = userEvent.setup();
    await u.type(screen.getByLabelText(/^new password/i), "Correct-Horse-99!");
    await u.type(screen.getByLabelText(/confirm/i), "Correct-Horse-99!");
    await u.click(screen.getByRole("button", { name: /set new password/i }));

    expect(await screen.findByTestId("reset-error")).toHaveTextContent(/no longer valid/i);
  });

  it("an unreachable server is not rendered as a rejected password", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("network"); }));
    wrap(<ResetPasswordPage />, link);
    const u = userEvent.setup();
    await u.type(screen.getByLabelText(/^new password/i), "Correct-Horse-99!");
    await u.type(screen.getByLabelText(/confirm/i), "Correct-Horse-99!");
    await u.click(screen.getByRole("button", { name: /set new password/i }));

    expect(await screen.findByTestId("reset-error")).toHaveTextContent(/unavailable/i);
  });
});

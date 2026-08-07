import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { MemoryRouter } from "react-router-dom";
import { ThemeProvider } from "@mersal/design-system";
import { LoginPage } from "../src/pages/LoginPage";
import { AuthProvider } from "../src/auth/AuthProvider";
import { DevAuthClient } from "../src/auth/authClient";

/**
 * Phase 28.4 — the sign-in SEQUENCE now happens in the SPA (ADR-0036 §3).
 *
 * <p>
 * `LIVE` is a build-time constant, so live mode is reached by mocking the config module rather than by
 * setting an env var — `import.meta.env` is substituted at transform time and cannot be changed at runtime.
 * </p>
 * <p>
 * Everything is asserted through the SCREEN, not through the client: what a person is told is the thing that
 * can be wrong in a way nothing else catches. A status mapped to the wrong message still returns a perfectly
 * valid `SessionState`.
 * </p>
 */
vi.mock("../src/config", async () => {
  const actual = await vi.importActual<typeof import("../src/config")>("../src/config");
  return { ...actual, LIVE: true };
});

// The silent authorize is a full-page navigation; jsdom has no such thing. Mocked so the test can assert
// THAT it was reached — which is the only correct outcome of a completed sign-in.
const silentAuthorize = vi.fn(async () => new Promise<never>(() => {}));
vi.mock("../src/auth/oidcClient", async () => {
  const actual = await vi.importActual<typeof import("../src/auth/oidcClient")>("../src/auth/oidcClient");
  return { ...actual, silentAuthorize: (...args: unknown[]) => silentAuthorize(...(args as [])) };
});

type Reply = { status: string; [k: string]: unknown };

/** Queue of replies the fake issuer gives, in order. */
let replies: Reply[] = [];
let calls: Array<{ url: string; body: unknown }> = [];

function fakeIssuer() {
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url.endsWith("/antiforgery")) {
      return new Response(JSON.stringify({ token: "csrf-1" }), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      });
    }
    calls.push({ url, body: init?.body ? JSON.parse(String(init.body)) : undefined });
    const next = replies.shift();
    if (!next) return new Response("", { status: 503 });
    return new Response(JSON.stringify({ csrf: "csrf-next", ...next }), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    });
  });
}

function renderLogin() {
  return render(
    <ThemeProvider>
      {/* The real shell always provides this. The dev branch of the page reads `login` from it; the live
          branch does not touch it, and the client is injected so no OIDC restore() runs in a test. */}
      <AuthProvider client={new DevAuthClient()}>
        <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }}>
          <LoginPage />
        </MemoryRouter>
      </AuthProvider>
    </ThemeProvider>,
  );
}

async function signIn(user = "nurse.mona", pass = "correct-horse") {
  const u = userEvent.setup();
  await u.type(screen.getByLabelText(/username/i), user);
  await u.type(screen.getByLabelText(/^password/i), pass);
  await u.click(screen.getByRole("button", { name: /sign in/i }));
  return u;
}

describe("the sign-in sequence", () => {
  beforeEach(() => {
    replies = [];
    calls = [];
    silentAuthorize.mockClear();
    vi.stubGlobal("fetch", fakeIssuer());
  });
  afterEach(() => vi.unstubAllGlobals());

  it("asks for a username and a password, not for a role", async () => {
    // The live screen used to be one button that navigated away, and the dev screen a ROLE PICKER. Neither
    // is a login. A role picker in live mode would let anyone choose their own portal.
    renderLogin();
    expect(screen.getByLabelText(/username/i)).toBeInTheDocument();
    expect(screen.getByLabelText(/^password/i)).toBeInTheDocument();
    expect(screen.queryByLabelText(/role/i)).not.toBeInTheDocument();
  });

  it("a correct password with nothing else outstanding completes the sign-in", async () => {
    replies = [{ status: "authenticated", twoFactorEnrolled: true }];
    renderLogin();
    await signIn();

    // The ONLY correct end of a completed sign-in: hand over to the unchanged PKCE flow.
    await waitFor(() => expect(silentAuthorize).toHaveBeenCalled());
  });

  it("never sends the password anywhere but the sign-in endpoint", async () => {
    replies = [{ status: "authenticated" }];
    renderLogin();
    await signIn("nurse.mona", "correct-horse");

    await waitFor(() => expect(calls.length).toBeGreaterThan(0));
    const leaked = calls.filter(
      (c) => !c.url.endsWith("/connect/session") && JSON.stringify(c.body ?? {}).includes("correct-horse"),
    );
    expect(leaked).toEqual([]);
  });

  // ---- the second factor -----------------------------------------------------------------------------

  it("asks for the second factor and does NOT complete the sign-in until it is given", async () => {
    // The property the whole design exists for. A "post the password, get a token" endpoint could not have
    // expressed this step, and the second factor would have quietly stopped being part of signing in.
    replies = [{ status: "two_factor_required" }];
    renderLogin();
    await signIn();

    expect(await screen.findByText(/two-step verification/i)).toBeInTheDocument();
    expect(silentAuthorize).not.toHaveBeenCalled();
  });

  it("completes after a good code", async () => {
    replies = [{ status: "two_factor_required" }, { status: "authenticated" }];
    renderLogin();
    const u = await signIn();

    await screen.findByText(/two-step verification/i);
    await u.type(screen.getByLabelText(/authenticator code/i), "123456");
    await u.click(screen.getByRole("button", { name: /^sign in$/i }));

    await waitFor(() => expect(silentAuthorize).toHaveBeenCalled());
  });

  it("offers a recovery code, because a lost authenticator has an answer", async () => {
    // And it is on the screen where you discover you lost it — not in a support call.
    replies = [{ status: "two_factor_required" }];
    renderLogin();
    const u = await signIn();

    await screen.findByText(/two-step verification/i);
    await u.click(screen.getByRole("button", { name: /recovery code/i }));
    expect(screen.getByLabelText(/recovery code/i)).toBeInTheDocument();
  });

  it("a wrong code is reported as a wrong CODE, not as wrong credentials", async () => {
    // Same server status on both steps; the screen must not tell someone their password was wrong when it
    // was accepted a moment ago and the six digits were the problem.
    replies = [{ status: "two_factor_required" }, { status: "invalid_credentials" }];
    renderLogin();
    const u = await signIn();

    await screen.findByText(/two-step verification/i);
    await u.type(screen.getByLabelText(/authenticator code/i), "000000");
    await u.click(screen.getByRole("button", { name: /^sign in$/i }));

    expect(await screen.findByText(/code wasn't accepted/i)).toBeInTheDocument();
  });

  // ---- the organization choice -----------------------------------------------------------------------

  it("asks which organization when the identity holds more than one", async () => {
    replies = [
      {
        status: "membership_selection_required",
        memberships: [
          { membershipId: "m-1", tenantId: "Mersal Maadi", roles: ["reception"] },
          { membershipId: "m-2", tenantId: "Mersal Nasr City", roles: ["reception"] },
        ],
      },
      { status: "authenticated" },
    ];
    renderLogin();
    const u = await signIn();

    expect(await screen.findByText(/Mersal Nasr City/)).toBeInTheDocument();
    await u.click(screen.getByRole("radio", { name: /Mersal Nasr City/ }));
    await u.click(screen.getByRole("button", { name: /continue/i }));

    await waitFor(() => expect(silentAuthorize).toHaveBeenCalled());
    // `calls[calls.length - 1]`, not `.at(-1)` — this project's tsconfig targets below es2022, and vitest
    // transpiles without typechecking, so `.at` ran green here and failed the image build.
    expect(calls[calls.length - 1]?.body).toMatchObject({ membershipId: "m-2" });
  });

  // ---- what each refusal says ------------------------------------------------------------------------

  it("gives ONE message for a wrong password and shows no hint about the username", async () => {
    replies = [{ status: "invalid_credentials" }];
    renderLogin();
    await signIn();

    const message = await screen.findByText(/don't match/i);
    expect(message).toBeInTheDocument();
    // The enumeration rule reaching the screen: nothing here may say the account was unknown, or existed,
    // or was disabled. The server already refuses to distinguish them.
    expect(screen.queryByText(/no such user|unknown user|disabled|deactivated/i)).not.toBeInTheDocument();
  });

  it("tells a locked-out user they are locked, and how long for", async () => {
    // ADR-0036 §5.2. The alternative sends them to reset a password that was never wrong — and the reset
    // does not unlock the account, so they lose the password AND stay locked out.
    replies = [{ status: "locked", retryAfterSeconds: 300 }];
    renderLogin();
    await signIn();

    const alert = await screen.findByTestId("login-error");
    expect(alert).toHaveTextContent(/temporarily locked/i);
    expect(alert).toHaveTextContent(/5 minutes/i);
  });

  it("says an account with no organization is not a password problem", async () => {
    replies = [{ status: "no_membership" }];
    renderLogin();
    await signIn();

    expect(await screen.findByTestId("login-error")).toHaveTextContent(/not active in any organization/i);
  });

  it("a sign-in service that cannot be reached is NEVER reported as a wrong password", async () => {
    // The standing rule, on the screen a locked-out person reaches when nothing else works. Telling somebody
    // their password is wrong during an outage sends them to change a password that was always correct.
    replies = []; // the fake issuer answers 503
    renderLogin();
    await signIn();

    const alert = await screen.findByTestId("login-error");
    expect(alert).toHaveTextContent(/unavailable/i);
    expect(alert).toHaveTextContent(/not a problem with your password/i);
    expect(screen.queryByText(/don't match/i)).not.toBeInTheDocument();
  });

  it("an unrecognised status is an outage, not a refusal", async () => {
    // A client and an issuer disagreeing about the protocol is an operational fault. Rendering it as a
    // credential verdict would be the same lie as the outage case above, arriving by a different route.
    replies = [{ status: "something_this_build_has_never_heard_of" }];
    renderLogin();
    await signIn();

    expect(await screen.findByTestId("login-error")).toHaveTextContent(/unavailable/i);
    expect(silentAuthorize).not.toHaveBeenCalled();
  });
});

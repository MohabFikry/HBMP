import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { AppProviders } from "../src/App";
import { DevAuthClient } from "../src/auth/devAuthClient";
import { DevApiClient } from "../src/api/DevApiClient";
import { ApiError } from "../src/api/http";
import { AsyncSection, classifyReadError } from "../src/screens/_shared";
import { useAsync } from "../src/api/useAsync";
import { auditClient } from "../src/audit/auditClient";
import { seedSession } from "./helpers";

/**
 * A failed READ must name the right remedy — and offer only actions that can work.
 *
 * The regression this pins: every HTTP status collapsed into "The service couldn't complete this request."
 * with a Retry button. Two of them cannot be retried at all. An expired session showed a service-fault
 * message and a button that re-sent the dead token forever, and a 403 invited the user to press Retry
 * against an authorization decision that will never change its mind.
 *
 * The write path (18.D1) already made these distinctions; the read path did not, and reads are most of the
 * app. Asserted at the classifier for the mapping and through a rendered section for the affordance,
 * because the affordance is the half that was actually harmful.
 */

function problem(status: number, type?: string) {
  return new ApiError("http", "failed", status, type ? { type } : undefined);
}

describe("classifyReadError — the remedy matches the cause", () => {
  it("treats an ended session as sign-in, never as retry", () => {
    const { headline, remedy } = classifyReadError(problem(401));
    expect(remedy).toBe("reauth");
    expect(headline.en).toMatch(/session has ended/i);
    // The old copy blamed the service for what is a sign-in problem.
    expect(headline.en).not.toMatch(/couldn't complete/i);
  });

  it("offers NO action on an authorization denial", () => {
    // A 403 is a decision, not a failure. It returns the same answer every time, and the remedy is a
    // person — so a button that re-asks is an invitation to waste the user's time.
    expect(classifyReadError(problem(403)).remedy).toBe("none");
  });

  it("keeps the three 403 treatments distinct, selected from the problem type", () => {
    // All three are HTTP 403; only the `type` separates them, and the remedy is a different person each
    // time (your administrator / Mersal / you, by switching branch).
    const forbidden = classifyReadError(problem(403)).headline.en;
    const notEnabled = classifyReadError(
      problem(403, "https://mersal.foundation/problems/program-not-enabled"),
    ).headline.en;
    const limit = classifyReadError(
      problem(403, "https://mersal.foundation/problems/program-limit-reached"),
    ).headline.en;
    const branch = classifyReadError(problem(403, "urn:hbmp:branch-out-of-scope")).headline.en;

    expect(new Set([forbidden, notEnabled, limit, branch]).size).toBe(4);
    expect(notEnabled).toMatch(/organization/i);
    // A4: never upsell vocabulary — these tenants are partner NGOs, not customers.
    for (const copy of [forbidden, notEnabled, limit, branch]) {
      expect(copy.toLowerCase()).not.toMatch(/upgrade|plan|billing|subscription/);
    }
  });

  it("falls back to the permission denial for an unrecognised 403 type", () => {
    // The safe default: it never claims the platform is at fault when we do not know.
    expect(classifyReadError(problem(403, "urn:something:new")).headline.en).toMatch(/don't have access/i);
  });

  it("still retries the things retrying can fix", () => {
    expect(classifyReadError(problem(429)).remedy).toBe("retry");
    expect(classifyReadError(problem(503)).remedy).toBe("retry");
    expect(classifyReadError(problem(404)).remedy).toBe("retry");
    expect(classifyReadError(new ApiError("network", "offline")).remedy).toBe("retry");
  });

  it("does not offer to retry a malformed response", () => {
    // A schema failure is deterministic — the service will return the same unexpected shape again.
    expect(classifyReadError(new ApiError("schema", "bad shape")).remedy).toBe("none");
  });

  it("carries Arabic for every headline it can produce", () => {
    // A missing translation does not fail a render — it silently shows English to an Arabic speaker.
    const cases = [problem(401), problem(403), problem(429), problem(500), new ApiError("network", "x")];
    for (const c of cases) {
      const ar = classifyReadError(c).headline.ar;
      expect(ar.length).toBeGreaterThan(0);
      expect(ar).toMatch(/[؀-ۿ]/);
    }
  });
});

/** A section whose loader always fails with `err`. */
function FailingSection({ err }: { err: ApiError }) {
  const state = useAsync<string[]>(() => Promise.reject(err), []);
  return (
    <AsyncSection<string[]> state={state} isEmpty={(d) => d.length === 0} emptyLabel={{ en: "none", ar: "لا شيء" }}>
      {() => <p>loaded</p>}
    </AsyncSection>
  );
}

function renderFailing(err: ApiError) {
  seedSession("doctor");
  return render(
    <AppProviders authClient={new DevAuthClient()} apiClient={new DevApiClient({ latencyMs: 0 })}>
      <FailingSection err={err} />
    </AppProviders>,
  );
}

beforeEach(() => localStorage.clear());
afterEach(() => cleanup());

describe("AsyncSection — the rendered affordance", () => {
  it("offers Sign in (not Retry) when the session has ended", async () => {
    renderFailing(problem(401));
    const alert = await screen.findByRole("alert");
    expect(within(alert).getByText(/session has ended/i)).toBeInTheDocument();

    expect(screen.getByRole("button", { name: /sign in/i })).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /retry/i })).toBeNull();
  });

  it("signing in ends the stale session, recorded as a timeout rather than a sign-out", async () => {
    renderFailing(problem(401));
    // The reason does not reach the auth CLIENT (logout() takes no argument) — it selects the AUDIT event
    // type. That is the thing worth pinning: an expiry recorded as a deliberate sign-out would misstate why
    // this person's access ended, and the access audit is what someone reads back later.
    const spy = vi.spyOn(auditClient, "emit");

    await userEvent.click(await screen.findByRole("button", { name: /sign in/i }));
    expect(spy).toHaveBeenCalledWith(expect.objectContaining({ type: "auth.timeout" }));
    expect(spy).not.toHaveBeenCalledWith(expect.objectContaining({ type: "auth.logout" }));
  });

  it("offers nothing to press on a denial", async () => {
    renderFailing(problem(403, "https://mersal.foundation/problems/program-not-enabled"));
    const alert = await screen.findByRole("alert");
    expect(within(alert).getByText(/isn't enabled for your organization/i)).toBeInTheDocument();
    expect(screen.queryByRole("button")).toBeNull();
  });

  it("still shows Retry for a server fault", async () => {
    renderFailing(problem(500));
    await screen.findByRole("alert");
    expect(screen.getByRole("button", { name: /retry/i })).toBeInTheDocument();
  });
});

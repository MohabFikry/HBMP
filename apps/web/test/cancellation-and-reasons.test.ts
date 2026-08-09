import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiError, getRaw, getText, postRaw } from "../src/api/http";
import { withReason } from "../src/screens/CallCentre";
import { writeErrorMessage } from "../src/api/writeError";
import { classifyReadError } from "../src/screens/_shared";

/**
 * 2026-08-09 audit §2.5, the two items left open when the rest of that finding was fixed.
 *
 *   CANCELLATION      nothing this application started could be stopped. `useAsync` and every typeahead
 *                     already discard a superseded ANSWER, so no stale result was ever rendered — but the
 *                     work carried on regardless, and a 250ms-debounced search left one live request per
 *                     pause against the master-data catalogue.
 *
 *   THE SERVER'S SAY  the call centre's writes returned a bare word from a union, so every failure without a
 *                     specific sentence collapsed into "Couldn't book that time" — while the agent was on the
 *                     phone with the person it concerned and the server had said why.
 *
 * The cancellation tests turn on ONE distinction: an abort we asked for must never be reported as a network
 * failure. Getting that wrong is worse than not having cancellation at all, because it puts "check your
 * connection" on screen every time somebody backspaces in a search box.
 */

/**
 * A slow request: it settles only when the caller's signal aborts it.
 *
 * If the verb under test did NOT forward the signal, the fake rejects immediately and says so, rather than
 * hanging until the runner's timeout. A regression here should read as "the signal never reached fetch", not
 * as a flaky slow test — the second is the kind of failure people re-run instead of reading.
 */
function hangingFetch() {
  vi.stubGlobal(
    "fetch",
    vi.fn((_url: string, init?: RequestInit) =>
      new Promise<Response>((_resolve, reject) => {
        const signal = init?.signal;
        if (!signal) return reject(new Error("fetch was called with no AbortSignal — the verb dropped it"));
        if (signal.aborted) return reject(abortError());
        signal.addEventListener("abort", () => reject(abortError()));
      }),
    ),
  );
}

/** What a browser (and undici) raise on abort. jsdom's DOMException is not always available, so the NAME is
 *  what matters — which is exactly what `http.ts` keys on. */
function abortError(): Error {
  const e = new Error("The operation was aborted");
  e.name = "AbortError";
  return e;
}

const failure = (p: Promise<unknown>) => p.then(() => null, (e) => e);

afterEach(() => vi.unstubAllGlobals());

describe("the transport can be cancelled", () => {
  it("threads the signal into fetch, so an abort actually stops the request", async () => {
    hangingFetch();
    const c = new AbortController();
    const pending = failure(getRaw("/drugs/search?q=amox", c.signal));
    c.abort();

    const err = await pending;
    expect(err).toBeInstanceOf(ApiError);
    expect((err as ApiError).kind).toBe("aborted");
  });

  it("reports a cancellation as `aborted`, never as `network`", async () => {
    // The distinction the UI depends on. Both are "no response arrived"; only one is worth telling anybody.
    hangingFetch();
    const c = new AbortController();
    const pending = failure(postRaw("/appointments", { a: 1 }, "key", { signal: c.signal }));
    c.abort();
    expect(((await pending) as ApiError).kind).toBe("aborted");
  });

  it("cancels a CSV export too — getText calls fetch itself and had to be threaded separately", async () => {
    hangingFetch();
    const c = new AbortController();
    const pending = failure(getText("/utilization/export", c.signal));
    c.abort();
    expect(((await pending) as ApiError).kind).toBe("aborted");
  });

  it("still reports a genuine transport failure as `network`", async () => {
    // The other direction, and the one that would break silently: if `isAbort` were too eager, a real
    // outage would be classified as something the app did on purpose and shown to nobody.
    vi.stubGlobal("fetch", vi.fn(async () => { throw new TypeError("Failed to fetch"); }));
    const err = await failure(getRaw("/anything"));
    expect((err as ApiError).kind).toBe("network");
  });
});

describe("a cancellation is never rendered as a fault", () => {
  it("gives a read a Retry rather than a connection warning", () => {
    const { remedy } = classifyReadError(new ApiError("aborted", "cancelled"));
    expect(remedy).toBe("retry");
  });

  it("tells a write's operator the truth: it may or may not have been applied", () => {
    // Nothing cancels a write today — `useWrite` passes no signal — but claiming "nothing was saved" would
    // be a guess, and the guess that produces duplicates.
    const e = writeErrorMessage(new ApiError("aborted", "cancelled"));
    expect(e.possiblyApplied).toBe(true);
    expect(e.action).toBe("reload");
  });
});

describe("the call centre passes on what the server said", () => {
  it("appends the service's reason to the generic failure", () => {
    expect(withReason("Couldn't book that time.", { detail: "Coverage lapsed on 1 August 2026." }))
      .toBe("Couldn't book that time. (Coverage lapsed on 1 August 2026.)");
  });

  it("leaves the sentence alone when the service said nothing", () => {
    expect(withReason("Couldn't book that time.", {})).toBe("Couldn't book that time.");
  });
});

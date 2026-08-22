import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, renderHook, screen, waitFor } from "@testing-library/react";
import { renderApp, seedSession } from "./helpers";
import { useBranchContext } from "../src/shell/useBranchContext";
import { setActiveBranch } from "../src/api/activeBranch";

/**
 * The two controls a caller who reaches more than one clinic cannot work without.
 *
 * <b>Reported as "the branch selector is flaky — sometimes it shows, sometimes it doesn't", and as booking
 * having no branch selector at all.</b> They are one problem seen from two places. A clinics manager reaches
 * six clinics and has NO active branch until they filter, so:
 *
 *   • the app-bar switcher IS their filter — without it they cannot narrow anything;
 *   • a booking has nothing for the server to resolve, and `BranchWriteScope` refuses it with
 *     `branch-target-required`.
 *
 * The switcher resolved once and gave up silently on any failure, so one 401 during the token exchange or one
 * slow gateway left it absent for the session. The booking screen then told them to "switch branches in the
 * header" — a control that filters rather than switches, starts cleared, and might not be there.
 */
describe("the branch switcher survives a failed resolve", () => {
  const realFetch = globalThis.fetch;

  beforeEach(() => { vi.useFakeTimers(); });
  afterEach(() => {
    vi.useRealTimers();
    globalThis.fetch = realFetch;
  });

  const ok = (body: unknown) =>
    Promise.resolve({ ok: true, json: () => Promise.resolve(body) } as Response);

  it("retries, rather than leaving the caller with no filter for the session", async () => {
    let call = 0;
    globalThis.fetch = vi.fn((url: string | URL | Request) => {
      const u = String(url);
      call += 1;
      // The first round fails the way an unexchanged token does: a 401 on both reads.
      if (call <= 2) return Promise.resolve({ ok: false, status: 401 } as Response);
      if (u.endsWith("/me/branches")) {
        return ok({ homeBranch: "b-1", permittedBranches: ["b-1", "b-2"] });
      }
      return ok([{ branchId: "b-1", nameEn: "Maadi" }, { branchId: "b-2", nameEn: "Dokki" }]);
    }) as typeof fetch;

    const { result } = renderHook(() => useBranchContext("clinics_manager"));

    // Nothing yet — and this is the state that used to be permanent.
    expect(result.current.branches).toHaveLength(0);

    await act(async () => { await vi.advanceTimersByTimeAsync(500); });

    expect(result.current.branches.map((b) => b.name)).toEqual(["Maadi", "Dokki"]);
    // A SET-scoped caller starts unfiltered: their first request must carry no X-Active-Branch, or a
    // supervisory worklist opens showing one sixth of its rows with nothing on screen to say so.
    expect(result.current.activeBranchId).toBeNull();
  });

  it("stops after a bounded number of attempts rather than hammering a service that is down", async () => {
    const fetchMock = vi.fn(() => Promise.resolve({ ok: false, status: 503 } as Response));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    renderHook(() => useBranchContext("clinics_manager"));
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000); });

    // Four attempts, two reads each. Fail-soft is still the end state — it is the SILENT single attempt that
    // was wrong, not the giving up.
    expect(fetchMock).toHaveBeenCalledTimes(8);
  });

  it("does not retry an answer that is simply empty", async () => {
    const fetchMock = vi.fn((url: string | URL | Request) =>
      String(url).endsWith("/me/branches")
        ? ok({ homeBranch: null, permittedBranches: [] })
        : ok([]));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    renderHook(() => useBranchContext("branch_coordinator"));
    await act(async () => { await vi.advanceTimersByTimeAsync(60_000); });

    // A 200 listing no branches is a real answer — the fixture harness, or a caller with no assignment — and
    // asking again four times would not change it.
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });
});

describe("booking names the clinic when the caller runs several", () => {
  afterEach(() => { setActiveBranch(null); });

  it("gives a clinics manager a clinic field", async () => {
    seedSession("clinics_manager");
    renderApp("/branch/book", "clinics_manager");

    // Without this the booking has no branch at all: the server refuses it, and the screen's own advice —
    // "switch branches in the header" — points at a control that filters and starts cleared.
    expect(await screen.findByText(/has to name the one it is for/i)).toBeInTheDocument();
    // The clinic field itself — the combobox `BookingForm` renders only in `choose` mode.
    await waitFor(() =>
      expect(screen.getByRole("combobox", { name: /^branch$/i })).toBeInTheDocument());
  });

  it("does not ask a single-clinic desk for something the server would refuse", async () => {
    seedSession("reception");
    renderApp("/reception/book", "reception");

    // Reception's branch is resolved server-side and a request naming another is refused, so a picker here
    // could only ever offer a choice that 403s.
    expect(await screen.findByText(/booked in your active branch/i)).toBeInTheDocument();
  });
});

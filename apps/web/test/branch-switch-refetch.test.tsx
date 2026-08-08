import { afterEach, describe, expect, it, vi } from "vitest";
import { act, renderHook, waitFor } from "@testing-library/react";
import { useAsync } from "../src/api/useAsync";
import { getActiveBranch, setActiveBranch, activeBranchHeader } from "../src/api/activeBranch";

const DOKKI = "0190b100-0000-7000-8000-000000000005";
const MAADI = "0190b100-0000-7000-8000-000000000004";

afterEach(() => setActiveBranch(null));

/**
 * The branch switcher, end to end at the layer that decides whether it does anything.
 *
 * This control has now been broken twice in the same shape. First it changed a value in React state and
 * nothing sent `X-Active-Branch`, so every branch-scoped worklist answered for the caller's HOME branch.
 * That was fixed — and the control was still inert, because sending a header only matters if somebody makes
 * a request, and every screen loads through `useAsync(loader, [])`, whose effect had no dependency on the
 * branch. Switching updated the header for a request nobody went on to make.
 *
 * Both failures look identical from the outside and neither says anything on screen: a receptionist selects
 * Dokki, keeps reading Maadi's queue, and believes they are looking at Dokki. So the re-read is pinned here
 * rather than left to be re-derived by whoever next touches the hook.
 */
describe("Branch switching (design 37 §7)", () => {
  it("re-runs a loader that declared no dependencies of its own", async () => {
    setActiveBranch(DOKKI);
    const loader = vi.fn().mockResolvedValue(["dokki row"]);

    // `[]` — the shape 47 of the app's 78 call sites use, and the one that used to make the switcher inert.
    const { result } = renderHook(() => useAsync(loader, []));
    await waitFor(() => expect(result.current.status).toBe("success"));
    expect(loader).toHaveBeenCalledTimes(1);

    loader.mockResolvedValue(["maadi row"]);
    // Wrapped: the store notifies subscribers synchronously, so this IS a React update.
    act(() => setActiveBranch(MAADI));

    await waitFor(() => expect(loader).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(result.current.data).toEqual(["maadi row"]));
  });

  it("does not re-read when the branch is set to the value it already had", async () => {
    // useBranchContext writes the branch on load and again on the server's echo, usually the same id both
    // times. Notifying on every write would double every screen's requests on a single switch.
    setActiveBranch(DOKKI);
    const loader = vi.fn().mockResolvedValue([]);
    renderHook(() => useAsync(loader, []));
    await waitFor(() => expect(loader).toHaveBeenCalledTimes(1));

    act(() => setActiveBranch(DOKKI));
    act(() => setActiveBranch(DOKKI));

    await new Promise((r) => setTimeout(r, 20));
    expect(loader).toHaveBeenCalledTimes(1);
  });

  it("sends the header only once a branch is active", () => {
    // Member-scoped roles never set one; an absent header means "my default scope", which every service
    // already implements. Sending an empty one would be a different request.
    expect(activeBranchHeader()).toEqual({});
    setActiveBranch(DOKKI);
    expect(activeBranchHeader()).toEqual({ "X-Active-Branch": DOKKI });
    expect(getActiveBranch()).toBe(DOKKI);
  });
});

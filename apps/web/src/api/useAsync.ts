import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react";
import { ApiError } from "./http";
import { getActiveBranch, subscribeActiveBranch } from "./activeBranch";

export type AsyncStatus = "loading" | "success" | "error";

export interface AsyncState<T> {
  status: AsyncStatus;
  data: T | null;
  error: ApiError | null;
  /** Re-run the loader (e.g. a Retry button). */
  reload: () => void;
}

/**
 * Runs an async loader on mount (and on `deps` change), tracking loading / success / error. The screen
 * derives its own *empty* state from a successful-but-empty payload — keeping "no data" (a valid result)
 * distinct from "failed to load" (an error), which matters for the aria-live announcement.
 *
 * ============================================================================================================
 * THE LOADER IS HANDED AN AbortSignal (2026-08-09 audit §2.5)
 * ============================================================================================================
 * It is aborted when this run is superseded — a dependency changed, `reload()` was pressed, or the component
 * unmounted. Loaders that ignore it keep working exactly as before: `() => api.orders()` takes no argument
 * and stays assignable, which is what makes this addition free at the ~78 existing call sites.
 *
 * The `live` flag stays and is NOT redundant. It is what guarantees no superseded result is ever rendered,
 * for every loader that does not forward the signal — which is most of them, since the `ApiClient` methods
 * do not take one. Cancellation stops the WORK; `live` governs the STATE. Removing either on the strength of
 * the other would be a regression, so both are here and each is doing its own job.
 */
export function useAsync<T>(loader: (signal: AbortSignal) => Promise<T>, deps: unknown[] = []): AsyncState<T> {
  const [status, setStatus] = useState<AsyncStatus>("loading");
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [nonce, setNonce] = useState(0);
  const loaderRef = useRef(loader);
  loaderRef.current = loader;

  const reload = useCallback(() => setNonce((n) => n + 1), []);

  /**
   * The active branch, as a DEPENDENCY.
   *
   * Every branch-scoped read is answered for the branch in `X-Active-Branch`, so switching branches changes
   * the correct answer to a question this hook has already asked. Without this line the switcher was
   * decorative: it updated the header for a request nobody went on to make, and the worklist on screen kept
   * showing the previous branch's rows with nothing to indicate it.
   *
   * Subscribed rather than passed in, because the alternative is adding a branch id to 78 `useAsync` call
   * sites and silently reintroducing the bug at every one that gets missed. Member-scoped roles never set a
   * branch, so for them this value stays null and nothing extra ever re-runs.
   */
  const branch = useSyncExternalStore(subscribeActiveBranch, getActiveBranch, getActiveBranch);

  useEffect(() => {
    let live = true;
    const controller = new AbortController();
    setStatus("loading");
    setError(null);
    loaderRef.current(controller.signal).then(
      (result) => {
        if (!live) return;
        setData(result);
        setStatus("success");
      },
      (e: unknown) => {
        if (!live) return;
        // A cancellation we asked for is not a failure to report. It can only reach here if a loader forwards
        // the signal AND rejects before the cleanup below runs — narrow, but rendering "could not reach the
        // server" because the operator typed another character is the exact outcome this guards.
        if (e instanceof ApiError && e.kind === "aborted") return;
        setError(e instanceof ApiError ? e : new ApiError("network", "Unexpected error"));
        setStatus("error");
      },
    );
    return () => {
      live = false;
      controller.abort();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [nonce, branch, ...deps]);

  return { status, data, error, reload };
}

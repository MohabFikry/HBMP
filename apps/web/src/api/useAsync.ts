import { useCallback, useEffect, useRef, useState } from "react";
import { ApiError } from "./http";

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
 */
export function useAsync<T>(loader: () => Promise<T>, deps: unknown[] = []): AsyncState<T> {
  const [status, setStatus] = useState<AsyncStatus>("loading");
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<ApiError | null>(null);
  const [nonce, setNonce] = useState(0);
  const loaderRef = useRef(loader);
  loaderRef.current = loader;

  const reload = useCallback(() => setNonce((n) => n + 1), []);

  useEffect(() => {
    let live = true;
    setStatus("loading");
    setError(null);
    loaderRef.current().then(
      (result) => {
        if (!live) return;
        setData(result);
        setStatus("success");
      },
      (e: unknown) => {
        if (!live) return;
        setError(e instanceof ApiError ? e : new ApiError("network", "Unexpected error"));
        setStatus("error");
      },
    );
    return () => {
      live = false;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [nonce, ...deps]);

  return { status, data, error, reload };
}

import { useCallback, useRef, useState } from "react";

/**
 * `useState` that survives leaving the screen and coming back.
 *
 * <b>The problem it solves.</b> Every route in this app unmounts when you navigate away, so "go back" re-mounts
 * a fresh component with empty state. On most screens that is a minor annoyance — a search box to retype. In the
 * call centre it is a real fault: an agent who opens a caller's patient profile mid-call comes back to a
 * workspace that has forgotten the call is in progress, while the interaction is still Open on the server,
 * holding the binding to that member. The agent's only visible option is to start a second call for the same
 * conversation.
 *
 * <b>Why sessionStorage rather than router state.</b> `location.state` is lost on a hard reload and cannot be
 * read by the screen being returned TO without threading it through every navigation. sessionStorage is keyed by
 * screen, survives reloads, is scoped to the tab, and is cleared when the tab closes — which is the right
 * lifetime for "where I was", and short of the lifetime of anything that should be persisted properly.
 *
 * <b>What must NOT go in here.</b> It is a browser-visible store on a machine agents share, so it holds the
 * SHAPE of the work — a query, a selected id, an open call id, a draft the agent typed — never a member's
 * details. The 360 and the profile are re-fetched from the server on return, through the same gate as the first
 * time, so returning to a screen re-authorizes rather than re-displaying a cached disclosure.
 */
export function useRestorableState<T>(key: string, initial: T): [T, (next: T | ((prev: T) => T)) => void] {
  const storageKey = `mrs.screen.${key}`;

  const [value, setValue] = useState<T>(() => {
    try {
      const raw = sessionStorage.getItem(storageKey);
      return raw === null ? initial : (JSON.parse(raw) as T);
    } catch {
      // Unavailable (private mode, disabled storage) or corrupt. A screen that cannot restore its state is a
      // screen that starts empty — never one that fails to render.
      return initial;
    }
  });

  // The setter is stable across renders, so it is safe in a `useCallback`/`useEffect` dependency list. Without
  // this the hook would re-create it every render and quietly defeat the memoisation of every caller.
  const latest = useRef(value);
  latest.current = value;

  const set = useCallback(
    (next: T | ((prev: T) => T)) => {
      const resolved = typeof next === "function" ? (next as (prev: T) => T)(latest.current) : next;
      latest.current = resolved;
      setValue(resolved);
      try {
        sessionStorage.setItem(storageKey, JSON.stringify(resolved));
      } catch {
        // Storage full or unavailable — the in-memory state is still correct for this visit, which is the part
        // the user can see. Restoring is the enhancement; working is not.
      }
    },
    [storageKey],
  );

  return [value, set];
}

/** Drop a screen's restored state — call when the work it described is genuinely finished (a call wrapped up,
 *  a form submitted), so returning later starts clean instead of resuming something that no longer exists. */
export function clearRestorableState(...keys: string[]): void {
  try {
    for (const k of keys) sessionStorage.removeItem(`mrs.screen.${k}`);
  } catch {
    // Nothing to clear if storage is unavailable.
  }
}

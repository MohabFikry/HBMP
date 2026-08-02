import { useEffect } from "react";

/**
 * Re-run a `useAsync` loader when the tab comes back to the front.
 *
 * For a board whose rows OTHER PEOPLE act on. A doctor's day list is the clearest case: reception checks a
 * patient in, the doctor ends a visit from the encounter workspace, a colleague cancels an appointment — and
 * a list loaded twenty minutes ago goes on offering "Start visit" against a visit that has already been
 * completed. The server refuses it (409/412) and the screen recovers, but the operator has already been told
 * that an action was available when it was not, which is the part that costs them.
 *
 * Deliberately NOT inside `useAsync`. Nearly eighty call sites use that hook, and most of them read something
 * that only the caller changes — a patient's own profile, a report they just generated. Refetching all of
 * them every time somebody alt-tabs spends requests to change nothing. Boards opt in.
 *
 * Both events, because they answer different questions: `visibilitychange` fires for a backgrounded tab, and
 * `focus` fires when the window itself regains focus with the tab already visible. Either one means somebody
 * has come back to this screen.
 */
export function useRefreshOnFocus(reload: () => void, enabled = true) {
  useEffect(() => {
    if (!enabled) return;
    const refresh = () => {
      if (document.visibilityState === "visible") reload();
    };
    window.addEventListener("focus", refresh);
    document.addEventListener("visibilitychange", refresh);
    return () => {
      window.removeEventListener("focus", refresh);
      document.removeEventListener("visibilitychange", refresh);
    };
  }, [reload, enabled]);
}

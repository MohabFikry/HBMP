import { useCallback, useRef, useState } from "react";
import type { Localized } from "@mersal/contracts";
import { newIdempotencyKey } from "./http";
import { writeErrorMessage, type WriteError } from "./writeError";

/**
 * Phase 18.D1 (audit R2 U1/U2) — the one way this app performs a mutation.
 *
 * Two rules that were being applied inconsistently, and are now impossible to get wrong by forgetting:
 *
 * 1. THE KEY IS MINTED ONCE PER FORM INSTANCE, and rotates only after a CONFIRMED success. That ordering is
 *    the whole point. A key minted per CLICK makes every retry a fresh write, so the second press of a
 *    button after a timeout creates a second order, a second registration, a second provider. A key that
 *    never rotates makes the NEXT genuine submission a no-op replay of the last one. Mint once, keep it
 *    across retries of the same attempt, rotate when the server has confirmed.
 *
 * 2. EVERY FAILURE IS SHOWN. The four screens the audit names caught everything as
 *    `catch { setStatus("idle") }` — the spinner stopped and nothing appeared, which reads as "nothing
 *    happened" and invites exactly the retry that duplicates the record.
 *
 * A network failure keeps the key deliberately: that retry is the case the key exists for.
 */
export interface WriteState {
  /** In flight. Drive the button's disabled state from this, never from a local boolean. */
  busy: boolean;
  /** The typed, translated failure, or null. Render via InlineAlert role="alert". */
  error: WriteError | null;
  /** True after a confirmed success — the only signal a screen should treat as "it worked". */
  done: boolean;
}

export interface WriteHandle extends WriteState {
  /**
   * Run a mutation. The callback receives the idempotency key to send. Returns true on success so a caller
   * can clear its form — and only then, because clearing on failure destroys the operator's work.
   */
  run: <T>(fn: (idempotencyKey: string) => Promise<T>) => Promise<boolean>;
  /** Clear the error (e.g. when the operator edits a field after a 422). */
  reset: () => void;
  /** The current key. Exposed for screens that must send it inside a request BODY rather than a header. */
  idempotencyKey: string;
}

export function useWrite(): WriteHandle {
  const keyRef = useRef<string>(newIdempotencyKey());
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<WriteError | null>(null);
  const [done, setDone] = useState(false);

  const run = useCallback(async <T,>(fn: (idempotencyKey: string) => Promise<T>): Promise<boolean> => {
    setBusy(true);
    setError(null);
    setDone(false);
    try {
      await fn(keyRef.current);
      // CONFIRMED success — and only now is a new key correct, so the next distinct submission is not
      // mistaken for a replay of this one.
      keyRef.current = newIdempotencyKey();
      setDone(true);
      return true;
    } catch (e) {
      const failure = writeErrorMessage(e);
      // Keep the SAME key for anything the operator might retry: that is what makes pressing the button
      // again safe rather than duplicative. For a terminal failure the key is irrelevant either way.
      setError(failure);
      return false;
    } finally {
      setBusy(false);
    }
  }, []);

  const reset = useCallback(() => setError(null), []);

  return { busy, error, done, run, reset, idempotencyKey: keyRef.current };
}

/** Convenience for rendering: the message in the active language. */
export function writeErrorText(e: WriteError | null, lang: string): string | null {
  if (!e) return null;
  const l = e.message as Localized;
  return lang === "ar" ? l.ar : l.en;
}

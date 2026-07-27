import { createContext, useCallback, useContext, useEffect, useRef, useState, type ReactNode } from "react";
import { Icon } from "./Icon";
import { cx } from "../lib/cx";

export interface ToastMessage {
  id: number;
  text: string;
  tone?: "ok" | "bad" | "info";
}

interface ToastContextValue {
  toast: (text: string, tone?: ToastMessage["tone"]) => void;
}

const ToastContext = createContext<ToastContextValue | null>(null);

/** Toast provider — renders a single aria-live region (assertive), auto-dismiss with pause-on-hover. */
export function ToastProvider({ children, timeout = 2600 }: { children: ReactNode; timeout?: number }) {
  const [current, setCurrent] = useState<ToastMessage | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const paused = useRef(false);

  const clear = () => {
    if (timer.current) clearTimeout(timer.current);
  };
  const schedule = useCallback(() => {
    clear();
    timer.current = setTimeout(() => {
      if (!paused.current) setCurrent(null);
      else schedule();
    }, timeout);
  }, [timeout]);

  const toast = useCallback(
    (text: string, tone: ToastMessage["tone"] = "ok") => {
      setCurrent({ id: Date.now(), text, tone });
      schedule();
    },
    [schedule],
  );

  useEffect(() => () => clear(), []);

  return (
    <ToastContext.Provider value={{ toast }}>
      {children}
      <div className="mrs-toastwrap" aria-live="assertive" aria-atomic="true">
        {current && (
          <div
            className="mrs-toast"
            role="status"
            onMouseEnter={() => (paused.current = true)}
            onMouseLeave={() => {
              paused.current = false;
              schedule();
            }}
          >
            <Icon name={current.tone === "bad" ? "cross" : current.tone === "info" ? "info" : "ok"} />
            <span>{current.text}</span>
          </div>
        )}
      </div>
    </ToastContext.Provider>
  );
}

export function useToast(): ToastContextValue {
  const ctx = useContext(ToastContext);
  if (!ctx) throw new Error("useToast must be used within <ToastProvider>");
  return ctx;
}

/**
 * Inline alert — role=status/alert, icon + text (never color-only). For persistent, in-flow messages.
 *
 * `warn` (19.6) is the tone for "this is not a failure, and you must not proceed as though it were fine":
 * a result page that is a subset of the matches, a back-dated change that needs rights the caller may not
 * have, counts that do not add up. It was missing, so every such message had to be either shouted as an
 * error (role=alert, which trains people to dismiss alerts) or whispered as information. StatusChip has
 * carried a `warn` kind since 0B; the two vocabularies now agree.
 */
export function InlineAlert({
  tone = "info",
  children,
  className,
  "data-testid": testId,
}: {
  tone?: "ok" | "bad" | "warn" | "info";
  children: ReactNode;
  className?: string;
  /** Test hook. An alert is often the ONLY rendered evidence of a rule (immutability, a withheld column),
   *  and matching it by its prose makes the test fail when the wording is improved rather than when the
   *  behaviour breaks. */
  "data-testid"?: string;
}) {
  return (
    <div
      data-testid={testId}
      className={cx(
        "mrs-alert",
        tone === "bad" && "mrs-alert-bad",
        tone === "ok" && "mrs-alert-ok",
        tone === "warn" && "mrs-alert-warn",
        className,
      )}
      // Only a failure interrupts. A warning is announced politely, in turn — it describes the state of what
      // is on screen rather than the outcome of something the user just did.
      role={tone === "bad" ? "alert" : "status"}
    >
      <Icon name={tone === "bad" ? "cross" : tone === "ok" ? "ok" : tone === "warn" ? "triangle" : "info"} />
      <span>{children}</span>
    </div>
  );
}

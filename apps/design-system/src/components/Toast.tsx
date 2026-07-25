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

/** Inline alert — role=status/alert, icon + text (never color-only). For persistent, in-flow messages. */
export function InlineAlert({
  tone = "info",
  children,
  className,
}: {
  tone?: "ok" | "bad" | "info";
  children: ReactNode;
  className?: string;
}) {
  return (
    <div
      className={cx("mrs-alert", tone === "bad" && "mrs-alert-bad", tone === "ok" && "mrs-alert-ok", className)}
      role={tone === "bad" ? "alert" : "status"}
    >
      <Icon name={tone === "bad" ? "cross" : tone === "ok" ? "ok" : "info"} />
      <span>{children}</span>
    </div>
  );
}

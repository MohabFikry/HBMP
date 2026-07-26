import type { ReactNode } from "react";
import { Button, Card, InlineAlert, useTheme } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { useAuth } from "../auth/AuthProvider";
import { portalForRole } from "../portals/catalog";
import type { AsyncState } from "../api/useAsync";

/** Returns a picker that resolves a bilingual `{en, ar}` value to the active language. */
export function useLoc(): (l: Localized) => string {
  const { lang } = useTheme();
  return (l: Localized) => l[lang];
}

const STR = {
  loading: { en: "Loading…", ar: "جارٍ التحميل…" },
  error: { en: "Couldn't load this. The service may be unavailable.", ar: "تعذّر التحميل. قد تكون الخدمة غير متاحة." },
  retry: { en: "Retry", ar: "إعادة المحاولة" },
} satisfies Record<string, Localized>;

/** Page header (eyebrow + h1 + optional actions), reused by every flagship screen. */
export function PageHeader({ title, actions }: { title: string; actions?: ReactNode }) {
  const { session } = useAuth();
  const t = useLoc();
  if (!session?.role) return null;
  const portal = portalForRole(session.role);
  return (
    <div className="pagehead">
      <div>
        <div className="role-eyebrow">{t(portal.eyebrow)}</div>
        <h1>{title}</h1>
      </div>
      {actions && <div className="pagehead-actions">{actions}</div>}
    </div>
  );
}

interface AsyncSectionProps<T> {
  state: AsyncState<T>;
  /** Given the loaded data, is it "empty"? Drives the empty state (a valid result, not an error). */
  isEmpty?: (data: T) => boolean;
  emptyLabel: Localized;
  children: (data: T) => ReactNode;
}

/**
 * Renders the four explicit states of an async load — loading / error / empty / success — with a polite
 * aria-live region so screen-reader users hear the outcome. Loading and empty announce via `role="status"`;
 * an error announces via `role="alert"` (InlineAlert) and offers Retry.
 */
export function AsyncSection<T>({ state, isEmpty, emptyLabel, children }: AsyncSectionProps<T>) {
  const t = useLoc();

  if (state.status === "loading") {
    return (
      <Card style={{ padding: "var(--sp6)" }}>
        <div className="async-loading" role="status" aria-live="polite">
          <span className="mrs-spin" aria-hidden="true" />
          <span>{t(STR.loading)}</span>
        </div>
      </Card>
    );
  }
  if (state.status === "error") {
    return (
      <Card style={{ padding: "var(--sp6)", display: "grid", gap: "var(--sp3)" }}>
        <InlineAlert tone="bad">{t(STR.error)}</InlineAlert>
        <div>
          <Button variant="secondary" onClick={state.reload}>
            {t(STR.retry)}
          </Button>
        </div>
      </Card>
    );
  }
  const data = state.data as T;
  if (isEmpty?.(data)) {
    return (
      <Card style={{ padding: "var(--sp6)" }}>
        <div aria-live="polite">
          <InlineAlert tone="info">{t(emptyLabel)}</InlineAlert>
        </div>
      </Card>
    );
  }
  return <>{children(data)}</>;
}

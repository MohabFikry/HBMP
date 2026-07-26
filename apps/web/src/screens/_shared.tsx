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
  // Kind-specific headline (a11y: the reason is stated in text, not by colour alone). The service's own
  // RFC 7807 `detail` — when present — is shown beneath as the specific reason.
  errorNetwork: { en: "Couldn't reach the service. Check your connection and retry.", ar: "تعذّر الوصول إلى الخدمة. تحقّق من اتصالك ثم أعد المحاولة." },
  errorHttp: { en: "The service couldn't complete this request.", ar: "تعذّر على الخدمة إتمام هذا الطلب." },
  errorSchema: { en: "The service returned an unexpected response.", ar: "أعادت الخدمة استجابةً غير متوقعة." },
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
    const err = state.error;
    const headline = err?.kind === "network" ? STR.errorNetwork : err?.kind === "schema" ? STR.errorSchema : STR.errorHttp;
    // The server's problem+json `detail`/`title` (http failures only) is the specific, actionable reason.
    const detail = err?.kind === "http" ? (err.problem?.detail ?? err.problem?.title) : undefined;
    return (
      <Card style={{ padding: "var(--sp6)", display: "grid", gap: "var(--sp3)" }}>
        <InlineAlert tone="bad">
          <span>{t(headline)}</span>
          {detail ? <span style={{ display: "block", marginTop: "var(--sp1)", opacity: 0.85, fontSize: "0.9em" }}>{detail}</span> : null}
        </InlineAlert>
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

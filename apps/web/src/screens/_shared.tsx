import { useCallback, useMemo, type ReactNode } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { Button, Card, Icon, InlineAlert, useTheme } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { useAuth } from "../auth/AuthProvider";
import { portalForRole } from "../portals/catalog";
import { kindFromProblemType, type AccessDeniedKind } from "../routing/AccessDenied";
import { L } from "../i18n/strings";
import type { AsyncState } from "../api/useAsync";
import { ApiError } from "../api/http";

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

  // 18.D1's write path already distinguished these; the READ path collapsed every HTTP status into
  // `errorHttp` + Retry. That is wrong in the two cases where retrying cannot possibly work:
  //
  //   • 401 — the session has ended. "Retry" re-sends the same dead token forever, and the message
  //     ("the service couldn't complete this request") blames the service for what is a sign-in problem.
  //     Observed live: an expired issuer key made every screen show this, and the only real remedy —
  //     sign in again — was the one action not offered.
  //   • 403 — an authorization decision. It will return exactly the same answer on every retry, and the
  //     remedy is a PERSON, not a button. Which person depends on the problem type, which is why the
  //     three treatments below exist rather than one (design 40 §4/§6).
  sessionEnded: { en: "Your session has ended. Sign in again to continue.", ar: "انتهت جلستك. سجّل الدخول مجددًا للمتابعة." },
  signIn: { en: "Sign in", ar: "تسجيل الدخول" },
  rateLimited: { en: "Too many requests just now. Wait a moment and retry.", ar: "طلبات كثيرة الآن. انتظر لحظة ثم أعد المحاولة." },
  back: { en: "Back", ar: "رجوع" },
} satisfies Record<string, Localized>;

/**
 * Page header (eyebrow + h1 + optional actions), reused by every flagship screen.
 *
 * <b>`back` renders a back control ABOVE the title</b>, in every portal at once, because this is the one
 * component they all share. It is a real `<button>` rather than a styled link: it runs
 * {@link useBackTarget}'s navigate, so it must be reachable by keyboard and announced as an action.
 */
export function PageHeader({ title, actions, back }: { title: string; actions?: ReactNode; back?: BackTarget }) {
  const { session } = useAuth();
  const t = useLoc();
  if (!session?.role) return null;
  const portal = portalForRole(session.role);
  return (
    <div className="pagehead">
      <div>
        {back && (
          <button type="button" className="pagehead-back" onClick={back.go}>
            {/* The chevron is drawn pointing DOWN and rotated by CSS off the document direction, exactly as
                the pager does it — in RTL "back" is to the right, and a hard-coded arrow is the classic way a
                mirrored layout ends up pointing the wrong way. */}
            <Icon name="chevron" width={14} height={14} aria-hidden="true" />
            <span>{back.label ? t(back.label) : t(STR.back)}</span>
          </button>
        )}
        <div className="role-eyebrow">{t(portal.eyebrow)}</div>
        <h1>{title}</h1>
      </div>
      {actions && <div className="pagehead-actions">{actions}</div>}
    </div>
  );
}

/**
 * Navigate into a patient's profile, recording where we came from.
 *
 * <b>Use this rather than a bare `navigate('/patients/…')`.</b> The profile's Back control reads
 * `location.state.from`, and every call site that forgets to set it degrades that control to `navigate(-1)` —
 * which is subtly wrong after a redirect and useless on a fresh tab. One helper means the profile can rely on
 * the origin being there.
 */
export function useOpenProfile(): (beneficiaryId: string, search?: string) => void {
  const navigate = useNavigate();
  const location = useLocation();
  return useCallback(
    (beneficiaryId: string, search?: string) => {
      navigate(`/patients/${encodeURIComponent(beneficiaryId)}${search ?? ""}`, {
        state: { from: `${location.pathname}${location.search}` },
      });
    },
    [navigate, location.pathname, location.search],
  );
}

/** What a back control needs: where to go, and optionally what to call the place it goes back to. */
export interface BackTarget {
  go: () => void;
  label?: Localized;
}

/**
 * Resolve where "back" goes for a screen that is always opened FOR something (the patient profile, chiefly).
 *
 * <b>Why not just `navigate(-1)`.</b> History alone is wrong twice: on a deep link pasted into a fresh tab
 * there is nothing behind this entry, and after an in-page redirect `-1` lands on the redirect rather than the
 * screen the user came from. So callers pass their origin in `location.state.from` and this prefers it, using
 * `-1` only as a fallback and rendering nothing at all when there is neither.
 *
 * Returns `null` when there is nowhere to go back TO — a back button that leaves the app is worse than none.
 */
export function useBackTarget(label?: Localized): BackTarget | null {
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from;

  return useMemo(() => {
    if (from) return { go: () => navigate(from), label };
    // `idx` is react-router's position in its own history stack; 0 means this entry is the first, so there is
    // nothing of ours behind it.
    const idx = (window.history.state as { idx?: number } | null)?.idx ?? 0;
    if (idx > 0) return { go: () => navigate(-1), label };
    return null;
  }, [from, label, navigate]);
}

/** What the user can actually do about a failed read. `none` means nothing on this screen will help. */
type ReadRemedy = "retry" | "reauth" | "none";

/**
 * Classify a failed READ into a headline and the one action that can help.
 *
 * Deliberately NOT `writeErrorMessage`: that classifier's copy is written for mutations ("your work has not
 * been saved", "the operation may already have applied"), which is false and alarming on a read that
 * changed nothing. The two share a shape, not a vocabulary.
 *
 * Exported for the tests, which assert the mapping directly — the interesting cases (401, 403) are the ones
 * hardest to reproduce through a rendered screen, and they are the ones that regressed.
 */
export function classifyReadError(err: ApiError | null | undefined): { headline: Localized; remedy: ReadRemedy } {
  if (err?.kind === "network") return { headline: STR.errorNetwork, remedy: "retry" };
  if (err?.kind === "schema") return { headline: STR.errorSchema, remedy: "none" };

  switch (err?.status) {
    case 401:
      return { headline: STR.sessionEnded, remedy: "reauth" };
    case 403:
      // One source of truth for the three treatments (design 40 §4/§6) — the same classifier the full-page
      // AccessDenied route uses, so an inline denial and a route denial never disagree about whose problem
      // it is. Selected from the problem `type`, never the status: all three are 403.
      return { headline: FORBIDDEN_HEADLINE[kindFromProblemType(err.problem?.type)], remedy: "none" };
    case 429:
      return { headline: STR.rateLimited, remedy: "retry" };
    default:
      // 404, 5xx and anything unrecognised: a reload is plausibly useful and cannot do harm on a read.
      return { headline: STR.errorHttp, remedy: "retry" };
  }
}

/**
 * The read-side counterpart of `writeErrorMessage(e).message`, for screens that hold a `Localized` error.
 *
 * QA P1-4: several load paths ran their failures through the WRITE classifier, so a 403 on a GET told the
 * user "Your access has changed and no longer covers this action. Nothing was saved." — nothing was being
 * saved, and "has changed" invents a history that never happened. Reads answer with what the read failure
 * actually is (including the three distinct 403 treatments).
 */
export function readErrorMessage(e: unknown): Localized {
  return classifyReadError(e instanceof ApiError ? e : null).headline;
}

/** Inline (one-line) forms of the three 403 treatments. The full-page route versions carry the longer
 *  guidance; a section inside a working screen needs the WHO, not a paragraph. */
const FORBIDDEN_HEADLINE: Record<AccessDeniedKind, Localized> = {
  "forbidden": L.forbiddenTitle,
  "program-not-enabled": L.notEnabledTitle,
  "program-limit-reached": L.limitReachedTitle,
  "branch-out-of-scope": L.branchOutOfScopeTitle,
};

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
  // "timeout" rather than "user": the session ended on its own, and the distinction is audited (the
  // AuthProvider emits auth.timeout vs auth.logout). Recording an expiry as a deliberate sign-out would
  // misreport why access ended.
  const { logout } = useAuth();

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
    const { headline, remedy } = classifyReadError(err);
    // The server's problem+json `detail`/`title` (http failures only) is the specific, actionable reason.
    const detail = err?.kind === "http" ? (err.problem?.detail ?? err.problem?.title) : undefined;
    return (
      <Card style={{ padding: "var(--sp6)", display: "grid", gap: "var(--sp3)" }}>
        <InlineAlert tone="bad">
          <span>{t(headline)}</span>
          {detail ? <span style={{ display: "block", marginTop: "var(--sp1)", opacity: 0.85, fontSize: "0.9em" }}>{detail}</span> : null}
        </InlineAlert>
        {/* The ACTION is the half that was wrong, not just the wording. An action that cannot succeed is
            worse than no action: it reads as "the system is flaky, keep pressing" and hides the real
            remedy. So a denial offers nothing to press, and an ended session offers sign-in. */}
        {remedy === "retry" ? (
          <div>
            <Button variant="secondary" onClick={state.reload}>
              {t(STR.retry)}
            </Button>
          </div>
        ) : remedy === "reauth" ? (
          <div>
            <Button variant="primary" onClick={() => void logout("timeout")}>
              {t(STR.signIn)}
            </Button>
          </div>
        ) : null}
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

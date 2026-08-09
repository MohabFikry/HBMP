import { useCallback, useMemo, useState, type ReactNode } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { Button, Card, Icon, InlineAlert, InputField, useTheme } from "@mersal/design-system";
import type { TableFilterSpec } from "@mersal/design-system";
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
  errorCancelled: { en: "That request was cancelled. Retry to load it again.", ar: "تم إلغاء هذا الطلب. أعد المحاولة لتحميله مرة أخرى." },
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
 * <b>`back` sits in the ACTION GROUP, alongside the page's own controls</b> — in every portal at once,
 * because this is the one component they all share. It used to sit above the eyebrow, which pushed the
 * role label and the title down a line and left the top-left corner carrying three stacked things (Back,
 * DOCTOR, Patient Profile) while the opposite corner held one. Grouping the controls puts every button on
 * the page in one place.
 *
 * It is a real `<button>` rather than a styled link: it runs {@link useBackTarget}'s navigate, so it must be
 * reachable by keyboard and announced as an action.
 */
/**
 * A catalogue product name, capitalised for display.
 *
 * <b>Sentence case, not title case, and not a data fix.</b> The Egyptian drug list is lower-case throughout —
 * all 22,653 products — so a counter reads "augmentin 600mg vial for i.v", which looks like an unfinished
 * screen rather than a medicine. Capitalising the FIRST letter only is the convention drug labelling uses and
 * is the one rule that cannot damage the rest of the string: title case would produce "600Mg", "I.V" and
 * "F.C. Tabs", turning a dose and a route into nonsense.
 *
 * <b>Why it is display and not the catalogue.</b> The names are source data loaded from the market list, and
 * rewriting 22,653 of them would put a derived spelling where the authority's own belongs — and would have to
 * be redone on every reload. Capitalisation is presentation; this is where it belongs.
 *
 * Arabic has no letter case, so this is a no-op on an Arabic name rather than a special case to remember.
 */
export function productName(name: string): string {
  const trimmed = name.trim();
  return trimmed.length === 0 ? trimmed : trimmed[0].toLocaleUpperCase() + trimmed.slice(1);
}

export function PageHeader({ title, actions, back }: { title: string; actions?: ReactNode; back?: BackTarget }) {
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
      {(back || actions) && (
        <div className="pagehead-actions">
          {/* Back FIRST, so the page's own action stays the last thing in the group — the position the eye
              lands on last and the one a primary control has everywhere else in the app. */}
          {back && (
            <button type="button" className="pagehead-back" onClick={back.go}>
              {/* The chevron is drawn pointing DOWN and rotated by CSS off the document direction, exactly as
                  the pager does it — in RTL "back" is to the right, and a hard-coded arrow is the classic way
                  a mirrored layout ends up pointing the wrong way. */}
              <Icon name="chevron" width={14} height={14} aria-hidden="true" />
              <span>{back.label ? t(back.label) : t(STR.back)}</span>
            </button>
          )}
          {actions}
        </div>
      )}
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
 * Returns `null` when there is nowhere to go back TO — a back button that leaves the app is worse than none —
 * unless the caller names a `fallback`, which is a destination INSIDE the app and so has no such problem.
 *
 * <b>Why a fallback is needed at all.</b> A RELOAD destroys both origins at once: `location.state` does not
 * survive it and react-router's history index resets to 0. So a clinician who refreshed the encounter
 * workspace — or followed a link to it — was stranded on a screen whose only exit is the nav rail, and the
 * screens that most need a way back are exactly the ones that are always opened FOR something and therefore
 * never appear in the rail. "Never offer a way out of the app" was the right rule and was being applied to a
 * case it does not cover: a doctor's own worklist is not out of the app.
 *
 * Pass a `fallback` whose identity is STABLE across renders — a module-level constant, not an inline object —
 * or the memo rebuilds every render. Nothing breaks if it does; the control simply re-creates its handler.
 */
export function useBackTarget(
  label?: Localized,
  fallback?: { path: string; label: Localized },
): BackTarget | null {
  const navigate = useNavigate();
  const location = useLocation();
  const from = (location.state as { from?: string } | null)?.from;

  return useMemo(() => {
    if (from) return { go: () => navigate(from), label };
    // `idx` is react-router's position in its own history stack; 0 means this entry is the first, so there is
    // nothing of ours behind it.
    const idx = (window.history.state as { idx?: number } | null)?.idx ?? 0;
    if (idx > 0) return { go: () => navigate(-1), label };
    // Neither an origin nor history: a reload, or a link opened in a fresh tab. The fallback carries its OWN
    // label, because this is not "back" — nothing was left behind to go back to — and a control that says
    // "Back" and lands somewhere the user has never been is lying about what it did.
    if (fallback) return { go: () => navigate(fallback.path), label: fallback.label };
    return null;
  }, [from, label, navigate, fallback]);
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
  // A read WE cancelled. `useAsync` never surfaces one, so this exists for the screen that calls a loader
  // directly — and it offers Retry rather than an explanation, because there is nothing to explain: the
  // request was superseded, and the only useful thing on screen is a way to ask again.
  if (err?.kind === "aborted") return { headline: STR.errorCancelled, remedy: "retry" };

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


/**
 * "When" — the date filter every clinician worklist carries.
 *
 * ============================================================================================================
 * TWO CHIPS: A DEFAULT, AND AN ESCAPE HATCH
 * ============================================================================================================
 * "Last 30 days" answers the question these lists are opened for — what am I working on now — in one click.
 * Everything else is a named period the operator has in mind ("the week of the audit", "since the 14th"), and
 * no ladder of preset windows ever guesses it: a 90/365 pair looked like coverage and was really two more
 * wrong answers to sit between the right one and the one you had to type anyway.
 *
 * The counts come free from `useTableQuery`'s faceting, so the default chip already says how many rows it
 * would leave before you press it.
 *
 * ============================================================================================================
 * WHAT AN INCOMPLETE RANGE MEANS
 * ============================================================================================================
 * Pressing Custom with neither bound filled narrows NOTHING, and says so by leaving every row on screen. The
 * alternative — treating an empty range as "match nothing" — empties the table the instant the chip is
 * pressed, which reads as "there is nothing in this period" about a period the operator has not named yet.
 *
 * One bound alone is a real answer and is honoured: a From with no To means "since then", a To with no From
 * means "up to then". Both are what an operator who filled in one field and stopped actually meant.
 *
 * The To bound covers its whole DAY. `<input type="date">` yields "2026-08-06", which parses to midnight —
 * so comparing an instant against it directly would exclude everything that happened during the day the
 * operator asked for, and a filter that drops today's work is worse than no filter.
 *
 * ============================================================================================================
 * WHY IT LIVES HERE
 * ============================================================================================================
 * Five tables across four screens ask this of four different date fields (`requestedAt`, `submittedAt`,
 * `lastVisit`, and the encounter tabs' own two). A copy per screen is how they drift apart — one board
 * offering 30 days and its neighbour 7 is two answers to "recently" inside one portal.
 */
const WHEN_STR = {
  label: { en: "When", ar: "الفترة" },
  last30: { en: "Last 30 days", ar: "آخر ٣٠ يوماً" },
  custom: { en: "Custom date", ar: "تاريخ محدد" },
  from: { en: "From", ar: "من" },
  to: { en: "To", ar: "إلى" },
} satisfies Record<string, Localized>;

const DAY_MS = 24 * 60 * 60 * 1000;

/**
 * A "When" filter over whichever instant the row is dated by.
 *
 * `at` is passed in rather than assumed: a prescription is dated by when it was SUBMITTED, an order by when
 * it was RAISED and an encounter by when it STARTED — different fields on different shapes. A row carrying no
 * date at all (an unsubmitted draft) matches no window: it has not happened yet, so it cannot be in the last
 * 30 days.
 *
 * A HOOK, because the custom bounds are state and they belong to the filter rather than to each screen that
 * mounts one.
 */
export function useWhenFilter<Row>(
  t: (l: Localized) => string,
  at: (row: Row) => string | null | undefined,
): TableFilterSpec<Row> {
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");

  return useMemo(() => ({
    key: "when",
    label: t(WHEN_STR.label),
    options: [
      { value: "30", label: t(WHEN_STR.last30) },
      { value: "custom", label: t(WHEN_STR.custom) },
    ],
    match: (row: Row, value: string) => {
      const iso = at(row);
      if (!iso) return false;
      const ts = Date.parse(iso);
      if (!Number.isFinite(ts)) return false;

      if (value !== "custom") {
        // Computed at MATCH time, not when the filter was built: a screen left open across midnight would
        // otherwise keep filtering against yesterday's boundary.
        return ts >= Date.now() - 30 * DAY_MS;
      }
      const lower = from ? Date.parse(from) : null;
      // Inclusive of the whole closing day — see the note above.
      const upper = to ? Date.parse(to) + DAY_MS : null;
      if (lower !== null && Number.isFinite(lower) && ts < lower) return false;
      if (upper !== null && Number.isFinite(upper) && ts >= upper) return false;
      return true;
    },
    // Rendered only while Custom is the pressed chip — a follow-up belongs next to what it follows, and two
    // permanently-visible date fields would make the chip that reveals them look inert.
    extra: (value: string | null) =>
      value === "custom" ? (
        <>
          <InputField label={t(WHEN_STR.from)} type="date" value={from}
                      onChange={(e) => setFrom(e.currentTarget.value)} />
          <InputField label={t(WHEN_STR.to)} type="date" value={to}
                      onChange={(e) => setTo(e.currentTarget.value)} />
        </>
      ) : undefined,
  }), [t, at, from, to]);
}

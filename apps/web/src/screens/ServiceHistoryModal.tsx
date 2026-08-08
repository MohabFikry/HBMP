import { useCallback } from "react";
import { Button, Card, DataTable, InlineAlert, Modal, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, ServiceHistoryRow } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";

/**
 * 29.4 — THE service-history modal (design 45 §4). ONE component, consumed by every tab.
 *
 * <p>"Has this patient had this test before, and what did it show?" is the question that prevents duplicate
 * ordering and reveals trends, and it is currently unanswerable without leaving the encounter.</p>
 *
 * <p><b>One component and one endpoint, deliberately.</b> Design 45 §4: "Not one implementation per tab."
 * Four copies of this would be four places for the restricted-result branch to be written slightly
 * differently, and the one that got it wrong would be the one nobody opened in a review.</p>
 *
 * <p><b>THREE STATES, distinctly.</b> has-history · no-previous-occurrences · could-not-load. The third must
 * never render as the second: a clinician reading "no previous tests" when the service was simply unreachable
 * will re-order unnecessarily, or miss a trend. `AsyncSection` is deliberately NOT used here — it collapses
 * error into a retry affordance, and this screen has to make the distinction in words.</p>
 */
const S = {
  title: { en: "Previous occurrences", ar: "الحالات السابقة" },
  close: { en: "Close", ar: "إغلاق" },

  loading: { en: "Loading this service's history…", ar: "جارٍ تحميل سجل هذه الخدمة…" },

  // The three states, in the words that distinguish them.
  none: {
    en: "No previous occurrences of this service for this patient.",
    ar: "لا توجد حالات سابقة لهذه الخدمة لهذا المريض.",
  },
  unavailable: {
    en: "This patient's history for this service could not be loaded. This is NOT a report that there is "
      + "none — please retry before deciding whether to re-order.",
    ar: "تعذّر تحميل سجل هذا المريض لهذه الخدمة. هذا ليس تأكيداً بعدم وجود سجل — يُرجى إعادة المحاولة قبل "
      + "تقرير إعادة الطلب.",
  },
  retry: { en: "Retry", ar: "إعادة المحاولة" },

  // 29.4 — the third state, INSIDE a successful response. The orders half answered and the pharmacy half
  // did not, so this list is incomplete rather than short — and a reader not told that will take it for the
  // whole story, which is the same mistake as reading "could not load" as "none".
  rxHalfMissing: {
    en: "Previous PRESCRIPTIONS could not be loaded, so this list is incomplete. What is shown is correct; "
      + "there may be more.",
    ar: "تعذّر تحميل الوصفات السابقة، لذلك هذه القائمة غير مكتملة. المعروض صحيح، وقد يكون هناك المزيد.",
  },

  cDate: { en: "Date", ar: "التاريخ" },
  cService: { en: "Service", ar: "الخدمة" },
  cStatus: { en: "Status", ar: "الحالة" },
  cActor: { en: "Ordered by", ar: "طلبها" },
  cResult: { en: "Result", ar: "النتيجة" },

  restricted: { en: "Restricted", ar: "مقيّد" },
  restrictedHint: {
    en: "This result is restricted. You can see that it happened, and when — not what it showed.",
    ar: "هذه النتيجة مقيّدة. يمكنك معرفة أنها تمت وتاريخها — لا ما أظهرته.",
  },
  requestAccess: { en: "Request access", ar: "طلب الوصول" },
  noResult: { en: "—", ar: "—" },

  trend: { en: "Trend", ar: "التغيّر" },
  trendHint: {
    en: "Numeric results over time. The table below carries the same values.",
    ar: "النتائج الرقمية عبر الوقت. الجدول أدناه يحمل القيم نفسها.",
  },
} satisfies Record<string, Localized>;

export function ServiceHistoryModal({
  beneficiaryId,
  serviceType,
  code,
  label,
  onClose,
  onRequestAccess,
}: {
  beneficiaryId: string;
  serviceType?: string;
  code: string;
  label?: string;
  onClose: () => void;
  onRequestAccess?: (row: ServiceHistoryRow) => void;
}) {
  const api = useApi();
  const t = useLoc();
  // Africa/Cairo + the app locale, through the shared formatter. `toLocaleDateString` renders in the
  // BROWSER's timezone and locale, so a clinician in Cairo and the audit trail beside them would date the
  // same event differently — display-truth.test.tsx fails the build on a direct call for exactly that reason.
  const { date } = useFormat();

  const state = useAsync(
    useCallback(
      () => api.serviceHistory(beneficiaryId, { serviceType, code }),
      [api, beneficiaryId, serviceType, code],
    ),
    [beneficiaryId, serviceType, code],
  );

  const columns: Column<ServiceHistoryRow>[] = [
    {
      key: "date", header: t(S.cDate), sortable: true,
      sortValue: (r) => r.occurredAt,
      cell: (r) => <span className="tnum">{date(r.occurredAt)}</span>,
    },
    { key: "service", header: t(S.cService), cell: (r) => r.description ?? r.code },
    { key: "status", header: t(S.cStatus), cell: (r) => r.status },
    { key: "actor", header: t(S.cActor), cell: (r) => r.actorUserId ?? "—" },
    {
      key: "result",
      header: t(S.cResult),
      cell: (r) =>
        // A RESTRICTED row is existence-only: date, service, actor, and this marker. The value is not hidden
        // in the DOM — the server never sent one — so there is nothing here to reveal. The request-access
        // action is how the caller asks (design 37 §6).
        r.restricted ? (
          <span>
            <StatusChip kind="warn" label={t(S.restricted)} />
            {onRequestAccess && (
              <Button variant="ghost" onClick={() => onRequestAccess(r)}>{t(S.requestAccess)}</Button>
            )}
          </span>
        ) : (
          r.resultSummary ?? t(S.noResult)
        ),
    },
  ];

  return (
    <Modal
      open
      onOpenChange={(o) => { if (!o) onClose(); }}
      title={`${t(S.title)}${label ? ` — ${label}` : ` — ${code}`}`}
      /* The way out belongs to the dialog. Written as the last child it rendered inside the opaque body
         card, directly under the "no previous occurrences" alert with nothing between them. Retry joins it
         only while there IS something to retry. */
      footer={
        <>
          {state.status === "error" && (
            <Button variant="secondary" onClick={state.reload}>{t(S.retry)}</Button>
          )}
          <Button variant="secondary" onClick={onClose}>{t(S.close)}</Button>
        </>
      }
    >
      {state.status === "loading" && <p role="status">{t(S.loading)}</p>}

      {/* COULD NOT LOAD — its own state, in its own words, with a retry. Never "none". */}
      {state.status === "error" && <InlineAlert tone="warn">{t(S.unavailable)}</InlineAlert>}

      {/* Rendered ABOVE both the empty and the populated case: an incomplete list and an empty-because-
          pharmacy-was-down list are both stories a reader would otherwise get wrong. */}
      {state.status === "success" && state.data?.prescriptionsUnavailable && (
        <InlineAlert tone="warn">{t(S.rxHalfMissing)}</InlineAlert>
      )}

      {state.status === "success" && state.data && state.data.items.length === 0 && (
        <InlineAlert tone="info">{t(S.none)}</InlineAlert>
      )}

      {state.status === "success" && state.data && state.data.items.length > 0 && (
        <>
          {state.data.trend.length > 1 && (
            <Card>
              <h3>{t(S.trend)}</h3>
              <p className="muted">{t(S.trendHint)}</p>
              <Sparkline points={state.data.trend} />
            </Card>
          )}
          {/*
            The data table stays in the DOM ALONGSIDE any chart (design 12 §7). A sparkline is not readable by
            a screen reader and not comparable by eye to a precision anyone would act on; the numbers are the
            record and the chart is the summary, never the other way round.
          */}
          <DataTable
            columns={columns}
            rows={state.data.items}
            rowKey={(r) => r.orderLineId}
            caption={t(S.title)}
          />
        </>
      )}
    </Modal>
  );
}

/**
 * A minimal inline sparkline. Deliberately unlabelled and `aria-hidden`: the table beneath carries the same
 * values, and a chart that announced itself twice would make a screen-reader user hear the series before the
 * numbers that define it.
 */
function Sparkline({ points }: { points: { at: string; value: number }[] }) {
  if (points.length < 2) return null;
  const values = points.map((p) => p.value);
  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min || 1;
  const d = points
    .map((p, i) => {
      const x = (i / (points.length - 1)) * 100;
      const y = 30 - ((p.value - min) / span) * 28;
      return `${i === 0 ? "M" : "L"}${x.toFixed(1)},${y.toFixed(1)}`;
    })
    .join(" ");

  return (
    <svg viewBox="0 0 100 30" preserveAspectRatio="none" aria-hidden="true" focusable="false"
         style={{ width: "100%", height: "3rem" }}>
      <path d={d} fill="none" stroke="currentColor" strokeWidth={1.5} vectorEffect="non-scaling-stroke" />
    </svg>
  );
}

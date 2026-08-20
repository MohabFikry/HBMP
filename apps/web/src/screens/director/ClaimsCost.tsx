import { Card, DataTable, KpiCard, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useAsync } from "../../api/useAsync";
import { useFormat } from "../../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "../_shared";
import { PeriodControl, usePeriod } from "./PeriodControl";

/**
 * What the clinics claimed, what was allowed, and why the rest was not.
 *
 * <b>Served from reporting, not from claims-service.</b> The Medical Director holds
 * `reporting:read-financial` and holds neither `claims:read` nor `claims:reconcile`, and that is the correct
 * boundary rather than an obstacle: a supervisor needs the SHAPE of what was claimed and denied, and opening
 * a claimant's file is the claims officer's authority. Reaching this screen by granting an operational claims
 * scope would have widened a real authority to satisfy an analytical need.
 *
 * <b>This screen could not have existed a week ago.</b> `reporting.financial_fact` was fed by `ServiceValued`,
 * an event nothing on the platform publishes, so every figure here would have been zero — not because nothing
 * was claimed, but because the feed had never been wired. claims-service now emits a settled line per claim
 * line; see the 2026-08-11 design note.
 */

const S = {
  title: { en: "Claims & Cost", ar: "المطالبات والتكلفة" },
  decided: { en: "Claims decided", ar: "مطالبات تم البت فيها" },
  allowed: { en: "Total allowed", ar: "إجمالي المسموح" },
  approvalRate: { en: "Approval rate", ar: "معدل الاعتماد" },
  denialRate: { en: "Denial rate", ar: "معدل الرفض" },

  outcomes: { en: "Outcomes", ar: "النتائج" },
  outcome: { en: "Outcome", ar: "النتيجة" },
  count: { en: "Claims", ar: "المطالبات" },
  share: { en: "Share", ar: "النسبة" },

  cost: { en: "Cost by service line", ar: "التكلفة حسب خط الخدمة" },
  serviceLine: { en: "Service line", ar: "خط الخدمة" },
  amount: { en: "Allowed", ar: "المسموح" },
  lines: { en: "Lines", ar: "البنود" },

  denials: { en: "Why claims were refused", ar: "أسباب رفض المطالبات" },
  reason: { en: "Reason", ar: "السبب" },

  empty: {
    en: "No claims were decided in this period.",
    ar: "لم يتم البت في أي مطالبات خلال هذه الفترة.",
  },
} satisfies Record<string, Localized>;

/** Four cues, not colour alone: hue + icon + shape + the word itself, per the design system's status rule. */
function outcomeChip(outcome: string): { kind: "ok" | "warn" | "bad" | "neu"; label: Localized } {
  switch (outcome) {
    case "Approved": return { kind: "ok", label: { en: "Approved", ar: "معتمدة" } };
    case "PartiallyApproved": return { kind: "warn", label: { en: "Partially approved", ar: "معتمدة جزئيًا" } };
    case "Denied": return { kind: "bad", label: { en: "Denied", ar: "مرفوضة" } };
    default: return { kind: "neu", label: { en: outcome, ar: outcome } };
  }
}

export function ClaimsCost() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const [preset, period, setPreset] = usePeriod();
  const state = useAsync(() => api.claimsCost(period), [period.from, period.to]);

  const outcomeCols = (decided: number): Column<{ outcome: string; count: number }>[] => [
    {
      key: "outcome", header: t(S.outcome), sortable: true, sortValue: (r) => r.outcome,
      cell: (r) => { const c = outcomeChip(r.outcome); return <StatusChip kind={c.kind} label={t(c.label)} />; },
    },
    { key: "count", header: t(S.count), cell: (r) => fmt.number(r.count), numeric: true, sortable: true, sortValue: (r) => r.count },
    {
      key: "share", header: t(S.share), numeric: true,
      cell: (r) => (decided === 0 ? "—" : `${Math.round((r.count / decided) * 100)}%`),
    },
  ];

  const costCols: Column<{ serviceLine: string; amount: number; count: number }>[] = [
    { key: "serviceLine", header: t(S.serviceLine), cell: (r) => r.serviceLine, sortable: true, sortValue: (r) => r.serviceLine },
    { key: "amount", header: t(S.amount), cell: (r) => fmt.money(r.amount), numeric: true, sortable: true, sortValue: (r) => r.amount },
    { key: "count", header: t(S.lines), cell: (r) => fmt.number(r.count), numeric: true, sortable: true, sortValue: (r) => r.count },
  ];

  const denialCols: Column<{ reasonCode: string; count: number }>[] = [
    { key: "reason", header: t(S.reason), cell: (r) => <span className="tnum">{r.reasonCode}</span>, sortable: true, sortValue: (r) => r.reasonCode },
    { key: "count", header: t(S.count), cell: (r) => fmt.number(r.count), numeric: true, sortable: true, sortValue: (r) => r.count },
  ];

  const rate = (d: { decided: number; byOutcome: Array<{ outcome: string; count: number }> }, outcome: string) =>
    d.decided === 0 ? "—" : `${Math.round(((d.byOutcome.find((o) => o.outcome === outcome)?.count ?? 0) / d.decided) * 100)}%`;

  return (
    <>
      <PageHeader title={t(S.title)} />
      <PeriodControl preset={preset} period={period} onChange={setPreset} />
      <AsyncSection state={state} isEmpty={(d) => d.decided === 0} emptyLabel={S.empty}>
        {(d) => (
          <div className="stack" style={{ gap: "var(--sp4)" }}>
            <div className="mrs-kpigrid">
              <KpiCard label={t(S.decided)} value={fmt.number(d.decided)} />
              {/* Money through useFormat — EGP in the ACTIVE locale, never en-US. */}
              <KpiCard label={t(S.allowed)} value={fmt.money(d.totalAllowed)} />
              <KpiCard label={t(S.approvalRate)} value={rate(d, "Approved")} />
              <KpiCard label={t(S.denialRate)} value={rate(d, "Denied")} />
            </div>

            <Card as="section" style={{ padding: "var(--sp3)" }}>
              <h2 className="section-h" style={{ margin: "0 0 var(--sp2)", paddingInline: "var(--sp2)" }}>{t(S.outcomes)}</h2>
              <DataTable columns={outcomeCols(d.decided)} rows={d.byOutcome} rowKey={(r) => r.outcome} caption={t(S.outcomes)} />
            </Card>

            <Card as="section" style={{ padding: "var(--sp3)" }}>
              <h2 className="section-h" style={{ margin: "0 0 var(--sp2)", paddingInline: "var(--sp2)" }}>{t(S.cost)}</h2>
              <DataTable columns={costCols} rows={d.byServiceLine} rowKey={(r) => r.serviceLine} caption={t(S.cost)} />
            </Card>

            {d.topDenialReasons.length > 0 && (
              <Card as="section" style={{ padding: "var(--sp3)" }}>
                <h2 className="section-h" style={{ margin: "0 0 var(--sp2)", paddingInline: "var(--sp2)" }}>{t(S.denials)}</h2>
                <DataTable columns={denialCols} rows={d.topDenialReasons} rowKey={(r) => r.reasonCode} caption={t(S.denials)} />
              </Card>
            )}
          </div>
        )}
      </AsyncSection>
    </>
  );
}

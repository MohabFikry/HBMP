import { useState } from "react";
import { Card, DataTable, SegmentedControl } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AxisUsageRow, Localized, ServiceAxis } from "@mersal/contracts";
import { SERVICE_AXIS_LABELS } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useAsync } from "../../api/useAsync";
import { useFormat } from "../../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "../_shared";
import { PeriodControl, usePeriod } from "./PeriodControl";

/**
 * Utilization across every axis reporting-service supports.
 *
 * <b>What this replaces.</b> `/reports/utilization` has always accepted
 * `dimension=provider|drug|lab|radiology`. The executive dashboard called it once, hard-coded to `provider`,
 * under a widget titled "Utilization by service line" — so it ranked providers beneath a heading promising a
 * different axis, and three of the four dimensions were reachable from no screen in the application.
 *
 * <b>Codes, not names.</b> The rows are whatever the axis is keyed on: a provider id, an ATC class, a service
 * code. Resolving them would mean a masterdata lookup per row, and the platform deliberately keeps that
 * enrichment out of the write path (see the RxDispensed note in reporting's ProjectionMapping). A code a
 * director can look up beats a name this screen invented.
 */

const AXES: ServiceAxis[] = ["provider", "drug", "lab", "radiology"];

const S = {
  title: { en: "Utilization", ar: "الاستخدام" },
  axis: { en: "Measured by", ar: "القياس حسب" },
  code: { en: "Code", ar: "الرمز" },
  count: { en: "Uses", ar: "مرات الاستخدام" },
  share: { en: "Share", ar: "النسبة" },
  empty: {
    en: "Nothing was recorded on this axis for the selected period.",
    ar: "لم يُسجَّل أي استخدام على هذا المحور في الفترة المحددة.",
  },
  caption: { en: "Utilization by the selected axis", ar: "الاستخدام حسب المحور المحدد" },
} satisfies Record<string, Localized>;

export function ServiceUse() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const [preset, period, setPreset] = usePeriod();
  const [axis, setAxis] = useState<ServiceAxis>("provider");
  const state = useAsync(() => api.serviceUse(axis, period), [axis, period.from, period.to]);

  // Computed from the rows on screen rather than from a server total, so the percentages always add up to
  // what is visible. A share against a total the reader cannot see is a share they cannot check.
  const total = (state.data?.rows ?? []).reduce((sum, r) => sum + r.count, 0);

  const cols: Column<AxisUsageRow>[] = [
    { key: "code", header: t(S.code), cell: (r) => <span className="tnum">{r.code}</span>, sortable: true, sortValue: (r) => r.code },
    { key: "count", header: t(S.count), cell: (r) => fmt.number(r.count), numeric: true, sortable: true, sortValue: (r) => r.count },
    {
      key: "share", header: t(S.share), numeric: true,
      cell: (r) => (total === 0 ? "—" : `${Math.round((r.count / total) * 100)}%`),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.title)} />
      <PeriodControl preset={preset} period={period} onChange={setPreset} />
      <div style={{ marginBottom: "var(--sp3)" }}>
        <SegmentedControl
          aria-label={t(S.axis)}
          value={axis}
          onChange={(v) => setAxis(v as ServiceAxis)}
          segments={AXES.map((a) => ({ value: a, label: t(SERVICE_AXIS_LABELS[a]) }))}
        />
      </div>
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.rows.length === 0} emptyLabel={S.empty}>
          {(d) => <DataTable columns={cols} rows={d.rows} rowKey={(r) => r.code} caption={t(S.caption)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

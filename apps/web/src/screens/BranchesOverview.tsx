import { useCallback, useMemo } from "react";
import { Card, DataTable, InlineAlert, StatusChip, useTheme } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import { branchApi, inventoryApi } from "../api/branchApi";
import { useAsync } from "../api/useAsync";
import { PageHeader, useLoc } from "./_shared";
import type { Localized } from "../portals/catalog";

const S = {
  title: { en: "Branches Overview", ar: "نظرة عامة على الفروع" },
  intro: {
    en: "The clinics you supervise, side by side. Clear the branch filter in the header to see them all; choose one to narrow every screen to it.",
    ar: "العيادات التي تشرف عليها، جنبًا إلى جنب. امسح مرشّح الفرع في الأعلى لعرضها جميعًا، أو اختر واحدة لتضييق كل الشاشات عليها.",
  },
  branch: { en: "Clinic", ar: "العيادة" },
  licenceAlerts: { en: "Licence alerts", ar: "تنبيهات التراخيص" },
  flagged: { en: "Awaiting reassignment", ar: "بانتظار إعادة التوزيع" },
  lowStock: { en: "Low stock", ar: "مخزون منخفض" },
  quarantined: { en: "Quarantined stock", ar: "مخزون محجوز" },
  clear: { en: "Clear", ar: "لا شيء" },
  attention: { en: "Needs attention", ar: "يحتاج انتباهًا" },
  noBranches: {
    en: "You are not assigned to any clinic. Ask programme administration to grant you a branch.",
    ar: "لست معينًا لأي عيادة. اطلب من إدارة البرنامج منحك فرعًا.",
  },
  tableIntro: {
    en: "Every figure below is a count of things waiting to be done, not a score.",
    ar: "كل رقم أدناه هو عدد أمور تنتظر الإنجاز، وليس تقييمًا.",
  },
} satisfies Record<string, Localized>;

interface BranchRow {
  branchId: string;
  licenceAlerts: number;
  flagged: number;
  lowStock: number;
  quarantined: number;
}

/**
 * 25.7 (design 42 §6) — the clinics manager's comparison of the branches in reach.
 *
 * <b>No chart.</b> Design 12 §7 requires a data table to stay in the DOM beside any chart, and for six rows
 * of four counts a chart would be the decoration beside the real thing rather than the other way round. If
 * this grows a chart later, the table stays.
 *
 * Reach-scoped rather than permission-scoped: a coordinator holds every permission this screen needs, and
 * simply has one clinic to compare — see `Section.reachScoped` in the catalog for why that distinction
 * matters to the one-permission-set invariant.
 */
export function BranchesOverview() {
  const t = useLoc();
  const { lang } = useTheme();

  // Three independent reads, deliberately not one aggregate endpoint: each already exists, each is
  // branch-scoped by the same header, and a new cross-service aggregate would be a fourth place for the
  // branch rule to be implemented.
  const alerts = useAsync(() => branchApi.licenceAlerts(90), []);
  const flagged = useAsync(() => branchApi.reassignmentNeeded(), []);
  const stock = useAsync(() => inventoryApi.alerts(), []);
  // A fourth read, for the NAMES. See the branch column below for why this screen went without them.
  const branches = useAsync(() => branchApi.branches(), []);

  /**
   * A clinic's name, or its short id when the reference read has not landed (or does not know it).
   *
   * The fallback is the truncated id and NOT a placeholder like "—": a row with a count against it must stay
   * identifiable even when the lookup is missing, and a dash would make two unknown clinics look like one.
   */
  const nameOf = useCallback(
    (branchId: string): string => {
      const b = branches.data?.find((x) => x.branchId === branchId);
      if (!b) return branchId.slice(0, 8);
      return lang === "ar" ? b.nameAr : b.nameEn;
    },
    [branches.data, lang],
  );

  const rows: BranchRow[] = useMemo(() => {
    const byBranch = new Map<string, BranchRow>();
    const ensure = (id: string) => {
      let row = byBranch.get(id);
      if (!row) {
        row = { branchId: id, licenceAlerts: 0, flagged: 0, lowStock: 0, quarantined: 0 };
        byBranch.set(id, row);
      }
      return row;
    };

    for (const b of stock.data?.branches ?? []) ensure(b);
    for (const a of alerts.data?.alerts ?? []) for (const b of a.branches) ensure(b).licenceAlerts += 1;
    for (const a of flagged.data?.appointments ?? []) if (a.branchId) ensure(a.branchId).flagged += 1;
    for (const l of stock.data?.lowStock ?? []) ensure(l.branchId).lowStock += 1;
    for (const q of stock.data?.quarantined ?? []) ensure(q.branchId).quarantined += 1;

    // Sorted by NAME, which is the order a supervisor reads a list of clinics in. Sorting by id put them in
    // an order with no meaning, which reads as no order at all.
    return [...byBranch.values()].sort((a, b) =>
      nameOf(a.branchId).localeCompare(nameOf(b.branchId), lang === "ar" ? "ar" : "en"));
  }, [alerts.data, flagged.data, stock.data, nameOf, lang]);

  const columns: Column<BranchRow>[] = useMemo(
    () => [
      /*
        THE CLINIC'S NAME.

        This rendered `branchId.slice(0, 8)` — an eight-character hex fragment — under a comment asserting
        that "branch NAMES live behind provider:read, which these roles do not hold". That was not true.
        `GET /api/v1/branches` is `RequireAuthorization()` in provider-service: any signed-in caller, because
        branches are org reference data with no PHI. The app-bar branch switcher has been reading exactly
        that endpoint for its labels since 14.8.

        So the manager's flagship comparison screen — the one screen that exists to be read across six
        clinics — labelled its rows `a3f81c2e` while the switcher three centimetres above it said "Maadi".
        The comment was doing the work of a check, and it was wrong.
      */
      { key: "branch", header: t(S.branch), cell: (r) => nameOf(r.branchId), sortable: true, sortValue: (r) => nameOf(r.branchId) },
      { key: "licences", header: t(S.licenceAlerts), cell: (r) => count(r.licenceAlerts, t(S.clear)) },
      { key: "flagged", header: t(S.flagged), cell: (r) => count(r.flagged, t(S.clear)) },
      { key: "low", header: t(S.lowStock), cell: (r) => count(r.lowStock, t(S.clear)) },
      { key: "quarantined", header: t(S.quarantined), cell: (r) => count(r.quarantined, t(S.clear)) },
      {
        key: "state",
        header: "",
        cell: (r) =>
          r.licenceAlerts + r.flagged + r.lowStock + r.quarantined > 0 ? (
            <StatusChip kind="warn" label={t(S.attention)} />
          ) : (
            <StatusChip kind="ok" label={t(S.clear)} />
          ),
      },
    ],
    [t, nameOf],
  );

  const loading = alerts.status === "loading" || flagged.status === "loading"
    || stock.status === "loading" || branches.status === "loading";

  return (
    <div className="branch-screen">
      <PageHeader title={t(S.title)} />
      <p className="muted lede">{t(S.intro)}</p>
      <p className="muted">{t(S.tableIntro)}</p>

      {loading ? (
        <p role="status" aria-live="polite">{lang === "ar" ? "جارٍ التحميل…" : "Loading…"}</p>
      ) : rows.length === 0 ? (
        <InlineAlert tone="info">{t(S.noBranches)}</InlineAlert>
      ) : (
        <Card>
          <DataTable caption={t(S.title)} columns={columns} rows={rows} rowKey={(r) => r.branchId} />
        </Card>
      )}
    </div>
  );
}

/** Zero renders as a WORD, not "0". A column of zeros is noise; "Clear" is an answer. */
function count(n: number, clear: string): string {
  return n === 0 ? clear : String(n);
}

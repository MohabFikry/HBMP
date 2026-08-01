import { useCallback, useEffect, useMemo, useState } from "react";
import { Button, Card, DataTable, InlineAlert, KpiList, StatusChip, Tabs } from "@mersal/design-system";
import type { Localized } from "@mersal/contracts";
import type {
  MemberGroupView,
  PolicyApi,
  PolicyPlanView,
  PolicyQueryRow,
  ScopeUtilizationView,
} from "../api/policyApi";
import { createHttpPolicyApi } from "../api/policyApi";

/** ONE client for the module, not one per render: a default parameter re-evaluates on every call,
 *  and screens key their load effects on the api instance — a fresh instance per render turned the
 *  first failing (or even succeeding) fetch into an unbounded request loop (QA P0-1: ~400 req/s).*/
const httpPolicyApi = createHttpPolicyApi();
import { writeErrorMessage } from "../api/writeError";
import { PageHeader, useLoc, readErrorMessage } from "./_shared";
import { ChangeTimeline, DocumentsPanel, LimitMeters, NotesPanel } from "./PolicyPanels";
import { useFormat } from "../i18n/useFormat";
import { useTheme } from "@mersal/design-system";

/**
 * Phase 19.6 — the policy book: which contracts exist, what plans sit under them, who is grouped how, and
 * what the whole thing has consumed (design 38 §4.2 / §4.5).
 *
 * The PLANS TAB is the part that earns its place. A policy with three plans behaves nothing like a policy
 * with one — members are on different ceilings, the default decides where a new enrolment lands, and the
 * per-plan member count is the first number anyone asks for when a policy looks over-consumed. Presenting
 * plans as a tab rather than a nested drill-down keeps that comparison on one screen.
 */

const S = {
  title: { en: "Policies", ar: "الوثائق" },
  groupsTitle: { en: "Groups", ar: "المجموعات" },
  policyNo: { en: "Policy no.", ar: "رقم الوثيقة" },
  status: { en: "Status", ar: "الحالة" },
  window: { en: "In force", ar: "سارية" },
  members: { en: "Members", ar: "الأعضاء" },
  plans: { en: "Plans", ar: "الخطط" },
  used: { en: "% used", ar: "٪ مستخدم" },
  noPolicies: { en: "No policies match these filters.", ar: "لا توجد وثائق مطابقة." },
  select: { en: "Select a policy to see its plans, groups, utilization and notes.", ar: "اختر وثيقة لعرض خططها ومجموعاتها واستخدامها وملاحظاتها." },
  tabPlans: { en: "Plans", ar: "الخطط" },
  tabGroups: { en: "Groups", ar: "المجموعات" },
  tabUtilization: { en: "Utilization", ar: "الاستخدام" },
  tabNotes: { en: "Notes", ar: "الملاحظات" },
  tabDocuments: { en: "Documents", ar: "المستندات" },
  tabTimeline: { en: "Timeline", ar: "السجل" },
  planLabel: { en: "Label", ar: "التسمية" },
  planVersion: { en: "Version", ar: "الإصدار" },
  default: { en: "Default", ar: "الافتراضية" },
  noPlansUnder: { en: "No plans attached to this policy.", ar: "لا توجد خطط مرتبطة بهذه الوثيقة." },
  noGroups: { en: "No groups defined.", ar: "لا توجد مجموعات." },
  groupCode: { en: "Code", ar: "الرمز" },
  groupName: { en: "Name", ar: "الاسم" },
  groupType: { en: "Type", ar: "النوع" },
  utilizationCaption: { en: "Consumption against limit, by member", ar: "الاستهلاك مقابل الحد، لكل عضو" },
  totalLimit: { en: "Total limit", ar: "إجمالي الحد" },
  totalConsumed: { en: "Consumed", ar: "المستهلك" },
  remaining: { en: "Remaining", ar: "المتبقي" },
  outliers: { en: "Members over threshold", ar: "الأعضاء فوق الحد" },
  reconcileBad: {
    en: "The accumulator and the reported total disagree. Treat these figures as provisional and raise it.",
    ar: "لا يتطابق المُراكِم مع الإجمالي المُبلَّغ. اعتبر هذه الأرقام مبدئية وأبلغ عنها.",
  },
  payerScoped: {
    en: "Narrowed to the payers you are assigned to — this is not the whole book.",
    ar: "تم التضييق على الجهات الممولة المسندة إليك — هذه ليست كل الوثائق.",
  },
  unavailable: { en: "Some figures could not be composed:", ar: "تعذّر تجميع بعض الأرقام:" },
  memberNo: { en: "Member no.", ar: "رقم العضو" },
  export: { en: "Export (audited)", ar: "تصدير (مُدقَّق)" },
  exported: { en: "Export downloaded. The request was audited with its row count.", ar: "تم تنزيل التصدير. سُجّل الطلب مع عدد الصفوف." },
} satisfies Record<string, Localized>;

function bandKind(band: string): "ok" | "warn" | "bad" | "neu" {
  if (band === "Over" || band === "Exhausted") return "bad";
  if (band === "High" || band === "Approaching") return "warn";
  if (band === "None" || band === "Unknown") return "neu";
  return "ok";
}

export function PolicyList({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const fmt = useFormat();
  const { lang } = useTheme();
  const [page, setPage] = useState<{ items: PolicyQueryRow[]; payerScopeApplied: boolean; unavailable: string[] } | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [selected, setSelected] = useState<PolicyQueryRow | null>(null);
  const [tab, setTab] = useState("plans");

  useEffect(() => {
    let live = true;
    api
      .policyQuery({ pageSize: 50 })
      .then((p) => live && setPage(p))
      .catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api]);

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.title)} />
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {/* A payer-scoped user must not read "12 policies" as "the organisation has 12 policies". */}
      {page?.payerScopeApplied && <InlineAlert tone="info">{t(S.payerScoped)}</InlineAlert>}
      {page && page.unavailable.length > 0 && (
        <InlineAlert tone="warn">
          {t(S.unavailable)} {page.unavailable.join(", ")}
        </InlineAlert>
      )}

      <Card>
        <DataTable
          caption={t(S.title)}
          rows={page?.items ?? []}
          rowKey={(r) => r.policyId}
          interactive
          selectedKey={selected?.policyId ?? null}
          onSelect={(r) => setSelected(r)}
          loading={page === null && !error}
          emptyLabel={t(S.noPolicies)}
          columns={[
            { key: "no", header: t(S.policyNo), cell: (r) => r.policyNo },
            { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={r.status} /> },
            {
              key: "window",
              header: t(S.window),
              cell: (r) => `${fmt.date(r.effectiveFrom)} → ${r.effectiveTo ? fmt.date(r.effectiveTo) : "—"}`,
            },
            { key: "members", header: t(S.members), cell: (r) => fmt.number(r.memberCount) },
            { key: "plans", header: t(S.plans), cell: (r) => fmt.number(r.planCount) },
            {
              key: "used",
              header: t(S.used),
              cell: (r) => (
                <StatusChip
                  kind={bandKind(r.utilizationBand)}
                  label={r.percentUsed != null ? `${Math.round(r.percentUsed)}% · ${r.utilizationBand}` : r.utilizationBand}
                />
              ),
            },
          ]}
        />
      </Card>

      {!selected && <InlineAlert tone="info">{t(S.select)}</InlineAlert>}

      {selected && (
        // `Tabs` force-mounts every panel (so a hidden pane is still in the DOM for assistive tech), which
        // means the CONTENT must gate its own fetching — otherwise selecting a policy would fire six
        // requests, including a PHI-adjacent notes read the operator never asked for.
        <Tabs
          aria-label={t(S.title)}
          value={tab}
          onValueChange={setTab}
          items={[
            { value: "plans", label: t(S.tabPlans), content: tab === "plans" ? <PolicyPlansTab api={api} policyId={selected.policyId} /> : null },
            { value: "groups", label: t(S.tabGroups), content: tab === "groups" ? <PolicyGroupsTab api={api} policyId={selected.policyId} /> : null },
            { value: "utilization", label: t(S.tabUtilization), content: tab === "utilization" ? <ScopeUtilizationPanel api={api} scope="policies" id={selected.policyId} /> : null },
            { value: "notes", label: t(S.tabNotes), content: tab === "notes" ? <NotesPanel api={api} scope="policies" scopeRef={selected.policyId} /> : null },
            { value: "documents", label: t(S.tabDocuments), content: tab === "documents" ? <DocumentsPanel api={api} scope="policies" scopeRef={selected.policyId} /> : null },
            { value: "timeline", label: t(S.tabTimeline), content: tab === "timeline" ? <ChangeTimeline api={api} scope="policies" scopeRef={selected.policyId} lang={lang} /> : null },
          ]}
        />
      )}
    </div>
  );
}

function PolicyPlansTab({ api, policyId }: { api: PolicyApi; policyId: string }) {
  const t = useLoc();
  const fmt = useFormat();
  const [rows, setRows] = useState<PolicyPlanView[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    setRows(null);
    api.policyPlans(policyId).then((r) => live && setRows(r)).catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api, policyId]);

  return (
    <Card data-testid="policy-plans-tab">
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      <DataTable
        caption={t(S.tabPlans)}
        rows={rows ?? []}
        rowKey={(r) => r.policyPlanId}
        loading={rows === null && !error}
        emptyLabel={t(S.noPlansUnder)}
        columns={[
          {
            key: "label",
            header: t(S.planLabel),
            cell: (r) => (
              <>
                {r.planLabel} {r.isDefault && <StatusChip kind="info" label={t(S.default)} />}
              </>
            ),
          },
          { key: "window", header: t(S.window), cell: (r) => `${fmt.date(r.effectiveFrom)} → ${r.effectiveTo ? fmt.date(r.effectiveTo) : "—"}` },
          { key: "members", header: t(S.members), cell: (r) => fmt.number(r.memberCount) },
          { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={r.status} /> },
        ]}
      />
    </Card>
  );
}

function PolicyGroupsTab({ api, policyId }: { api: PolicyApi; policyId: string }) {
  const t = useLoc();
  const fmt = useFormat();
  const [rows, setRows] = useState<MemberGroupView[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    setRows(null);
    api.policyGroups(policyId).then((r) => live && setRows(r)).catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api, policyId]);

  return (
    <Card data-testid="policy-groups-tab">
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      <DataTable
        caption={t(S.groupsTitle)}
        rows={rows ?? []}
        rowKey={(r) => r.groupId}
        loading={rows === null && !error}
        emptyLabel={t(S.noGroups)}
        columns={[
          { key: "code", header: t(S.groupCode), cell: (r) => r.groupCode },
          { key: "name", header: t(S.groupName), cell: (r) => r.nameEn },
          { key: "type", header: t(S.groupType), cell: (r) => r.groupType },
          { key: "window", header: t(S.window), cell: (r) => `${fmt.date(r.effectiveFrom)} → ${r.effectiveTo ? fmt.date(r.effectiveTo) : "—"}` },
          { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status === "Active" ? "ok" : "neu"} label={r.status} /> },
        ]}
      />
    </Card>
  );
}

/**
 * A group / plan / policy / payer utilization aggregate. Shared by the policy detail and the standalone
 * Utilization section, because "how much has this cohort used" is one question with one answer.
 */
export function ScopeUtilizationPanel({
  api,
  scope,
  id,
}: {
  api: PolicyApi;
  scope: "groups" | "plans" | "policies" | "payers";
  id: string;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const [view, setView] = useState<ScopeUtilizationView | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    setView(null);
    api
      .scopeUtilization(scope, id)
      .then((v) => live && setView(v))
      .catch((e) => live && setError(writeErrorMessage(e).message));
    return () => { live = false; };
  }, [api, scope, id]);

  const meters = useMemo(
    () =>
      (view?.members ?? []).slice(0, 25).map((m) => ({
        label: m.memberNo,
        consumed: m.totalConsumed,
        limit: m.totalLimit,
        valueText: fmt.money(m.totalConsumed),
        limitText: m.anyUnlimited ? "∞" : fmt.money(m.totalLimit),
      })),
    [view, fmt],
  );

  return (
    <Card data-testid="scope-utilization">
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      {view && (
        <>
          {/* The reconciliation statement is carried in every response and rendered on every report — a
              disagreement between the accumulator and the reported total must be visible to whoever acts on
              the number, not discovered afterwards. */}
          {!view.reconciliation.reconciled && <InlineAlert tone="bad">{t(S.reconcileBad)}</InlineAlert>}
          {/*
            * The four headline figures, in the KPI treatment 0B §10b specifies (audit §5.2).
            *
            * They were `<dl class="pol-kpis">` — dt at 0.82rem, dd at 1.25rem, four pairs in a flex row —
            * which is to say plain text with a size difference. On a screen whose entire purpose is "how much
            * of this cohort's entitlement is gone", the answer read as a caption. `KpiList` keeps the
            * definition-list semantics (four terms describing one subject, announced as pairs) and takes the
            * hairline, the uppercase micro-label and the 34px tabular numerals from the same classes
            * `KpiCard` uses, so the two cannot drift into two different-looking KPIs.
            */}
          <KpiList
            items={[
              { label: t(S.members), value: fmt.number(view.memberCount) },
              { label: t(S.totalLimit), value: fmt.money(view.totalLimit) },
              { label: t(S.totalConsumed), value: fmt.money(view.totalConsumed) },
              { label: t(S.remaining), value: fmt.money(view.totalRemaining) },
            ]}
          />

          <LimitMeters caption={t(S.utilizationCaption)} rows={meters} />

          {view.outliers.length > 0 && (
            <>
              <h4>
                {t(S.outliers)} ({Math.round(view.outlierThresholdPercent)}%)
              </h4>
              <DataTable
                caption={t(S.outliers)}
                rows={view.outliers}
                rowKey={(r) => r.enrollmentId}
                density="compact"
                columns={[
                  { key: "member", header: t(S.memberNo), cell: (r) => r.memberNo },
                  { key: "consumed", header: t(S.totalConsumed), cell: (r) => fmt.money(r.totalConsumed) },
                  { key: "limit", header: t(S.totalLimit), cell: (r) => (r.anyUnlimited ? "∞" : fmt.money(r.totalLimit)) },
                  { key: "pct", header: t(S.used), cell: (r) => (r.percentUsed != null ? `${Math.round(r.percentUsed)}%` : "—") },
                ]}
              />
            </>
          )}

          {view.external.unavailable.length > 0 && (
            <InlineAlert tone="warn">
              {t(S.unavailable)} {view.external.unavailable.join(", ")}
            </InlineAlert>
          )}
        </>
      )}
    </Card>
  );
}

/**
 * The standalone Utilization section — the same aggregate as the policy detail's tab, over any scope, with
 * the audited CSV export.
 *
 * The export goes through the service's own `/utilization/export`, which writes an audit event carrying the
 * row count. Building the CSV client-side from data already on screen would have been easier and would have
 * produced a file nobody could later account for.
 */
export function UtilizationScreen({
  api = httpPolicyApi,
  embedded = false,
}: {
  api?: PolicyApi;
  /**
   * Rendered as a PANEL inside another screen rather than as a screen of its own — which is how the
   * beneficiary portal reaches it now that Utilization is a tab in Analytics rather than a nav section.
   *
   * All it suppresses is the page header. A tab panel that prints its own <h1> gives the page two titles and
   * two competing answers to "where am I"; policy administration still opens this at `/policy/utilization`,
   * where the header is the only thing naming the screen.
   */
  embedded?: boolean;
}) {
  const t = useLoc();
  const [policies, setPolicies] = useState<PolicyQueryRow[]>([]);
  const [scope, setScope] = useState<"policies" | "groups" | "plans" | "payers">("policies");
  const [scopeId, setScopeId] = useState<string>("");
  const [error, setError] = useState<Localized | null>(null);
  const [announce, setAnnounce] = useState("");

  useEffect(() => {
    let live = true;
    api
      .policyQuery({ pageSize: 50 })
      .then((p) => {
        if (!live) return;
        setPolicies(p.items);
        setScopeId(p.items[0]?.policyId ?? "");
      })
      .catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api]);

  async function exportCsv() {
    try {
      const csv = await api.exportUtilization(scopeMap[scope], scopeId);
      const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
      const url = URL.createObjectURL(blob);
      const a = document.createElement("a");
      a.href = url;
      a.download = `utilization-${scope}-${scopeId.slice(0, 8)}.csv`;
      a.click();
      URL.revokeObjectURL(url);
      setAnnounce(t(S.exported));
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }

  return (
    <div className="pol-screen">
      {!embedded && <PageHeader title={t(S.tabUtilization)} />}
      <div aria-live="polite" role="status" className="sr-only">{announce}</div>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      <Card style={{ padding: "var(--sp4)", display: "flex", gap: "var(--sp4)", alignItems: "end", flexWrap: "wrap" }}>
        {/* QA P1-11: the label ran into a zero-width bare select. Real field markup, a minimum width, and
            an explicit empty option — an empty select must LOOK empty, not collapsed. */}
        <div className="mrs-field" style={{ minWidth: 280 }}>
          <label className="mrs-label" htmlFor="util-policy">{t(S.policyNo)}</label>
          <select
            className="mrs-control"
            id="util-policy"
            value={scopeId}
            onChange={(e) => {
              setScope("policies");
              setScopeId(e.target.value);
            }}
          >
            {policies.length === 0 ? <option value="">—</option> : null}
            {policies.map((p) => (
              <option key={p.policyId} value={p.policyId}>
                {p.policyNo}
              </option>
            ))}
          </select>
        </div>
        <Button variant="secondary" onClick={exportCsv} disabled={!scopeId}>
          {t(S.export)}
        </Button>
      </Card>
      {scopeId && <ScopeUtilizationPanel api={api} scope={scope} id={scopeId} />}
    </div>
  );
}

/** The export endpoint names its scopes in the singular domain vocabulary, not the REST path segment. */
const scopeMap: Record<"policies" | "groups" | "plans" | "payers", string> = {
  policies: "Policy",
  groups: "Group",
  plans: "Plan",
  payers: "Payer",
};

/** The standalone Groups section: every group across the policies the caller can see, with its utilization. */
export function GroupsScreen({ api = httpPolicyApi }: { api?: PolicyApi }) {
  const t = useLoc();
  const fmt = useFormat();
  const [policies, setPolicies] = useState<PolicyQueryRow[]>([]);
  const [policyId, setPolicyId] = useState<string | null>(null);
  const [groups, setGroups] = useState<MemberGroupView[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [error, setError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    api
      .policyQuery({ pageSize: 50 })
      .then((p) => {
        if (!live) return;
        setPolicies(p.items);
        setPolicyId(p.items[0]?.policyId ?? null);
      })
      .catch((e) => live && setError(readErrorMessage(e)));
    return () => { live = false; };
  }, [api]);

  const load = useCallback(async () => {
    if (!policyId) return;
    try {
      setGroups(await api.policyGroups(policyId));
      setSelected(null);
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }, [api, policyId]);

  useEffect(() => {
    void load();
  }, [load]);

  return (
    <div className="pol-screen">
      <PageHeader title={t(S.groupsTitle)} />
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      <Card style={{ padding: "var(--sp4)" }}>
        <div className="mrs-field" style={{ maxWidth: 320 }}>
          <label className="mrs-label" htmlFor="grp-policy">{t(S.policyNo)}</label>
          <select className="mrs-control" id="grp-policy" value={policyId ?? ""} onChange={(e) => setPolicyId(e.target.value)}>
            {policies.length === 0 ? <option value="">—</option> : null}
            {policies.map((p) => (
              <option key={p.policyId} value={p.policyId}>
                {p.policyNo}
              </option>
            ))}
          </select>
        </div>
      </Card>
      <Card>
        <DataTable
          caption={t(S.groupsTitle)}
          rows={groups}
          rowKey={(r) => r.groupId}
          interactive
          selectedKey={selected}
          onSelect={(r) => setSelected(r.groupId)}
          emptyLabel={t(S.noGroups)}
          columns={[
            { key: "code", header: t(S.groupCode), cell: (r) => r.groupCode },
            { key: "name", header: t(S.groupName), cell: (r) => r.nameEn },
            { key: "type", header: t(S.groupType), cell: (r) => r.groupType },
            { key: "window", header: t(S.window), cell: (r) => `${fmt.date(r.effectiveFrom)} → ${r.effectiveTo ? fmt.date(r.effectiveTo) : "—"}` },
          ]}
        />
      </Card>
      {selected && <ScopeUtilizationPanel api={api} scope="groups" id={selected} />}
    </div>
  );
}

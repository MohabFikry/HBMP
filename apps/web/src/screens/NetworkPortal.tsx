import { useMemo, useState } from "react";
import { useFormat } from "../i18n/useFormat";
import { Button, Card, DataTable, DataTableView, Icon, InlineAlert, InputField, KpiCard, StatusChip, useTableQuery, useTheme } from "@mersal/design-system";
import { useWrite, writeErrorText } from "../api/useWrite";
import type { Column, TableFilterSpec } from "@mersal/design-system";
import type { CreateProviderInput, Localized, NetworkMetrics, ProviderContract, ProviderLocation, ProviderSummary } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useAuth } from "../auth/AuthProvider";
import { mayReadTheNetworkRollup } from "../authz/permissions";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  dirTitle: { en: "Providers Directory", ar: "دليل مقدمي الخدمة" },
  dirEmpty: { en: "No providers in this network.", ar: "لا يوجد مقدمو خدمة في هذه الشبكة." },
  perfTitle: { en: "Performance", ar: "الأداء" },
  contractsTitle: { en: "Contracts & coverage", ar: "العقود والتغطية" },
  locationsTitle: { en: "Locations & users", ar: "المواقع والمستخدمون" },
  onboardTitle: { en: "Onboarding", ar: "الانضمام" },
  provider: { en: "Provider", ar: "مقدم الخدمة" },
  codeH: { en: "Code", ar: "الرمز" },
  typeH: { en: "Type", ar: "النوع" },
  status: { en: "Status", ar: "الحالة" },
  onboarding: { en: "Onboarding", ar: "الانضمام" },
  total: { en: "Providers", ar: "مقدمو الخدمة" },
  active: { en: "Active", ar: "نشط" },
  suspended: { en: "Suspended", ar: "موقوف" },
  terminated: { en: "Terminated", ar: "منتهٍ" },
  pickProvider: { en: "Select a provider.", ar: "اختر مقدم خدمة." },
  back: { en: "← Back", ar: "→ رجوع" },
  name: { en: "Name", ar: "الاسم" },
  governorate: { en: "Governorate", ar: "المحافظة" },
  address: { en: "Address", ar: "العنوان" },
  primary: { en: "Primary", ar: "رئيسي" },
  contractNo: { en: "Contract", ar: "العقد" },
  from: { en: "From", ar: "من" },
  to: { en: "To", ar: "إلى" },
  lines: { en: "Service lines", ar: "بنود الخدمة" },
  noLocations: { en: "No locations recorded.", ar: "لا توجد مواقع مسجلة." },
  noContracts: { en: "No contracts recorded.", ar: "لا توجد عقود مسجلة." },
  legalName: { en: "Legal name", ar: "الاسم القانوني" },
  code: { en: "Provider code", ar: "رمز مقدم الخدمة" },
  search: { en: "Search", ar: "بحث" },
  dirSearchHint: { en: "Provider name or code", ar: "اسم مقدم الخدمة أو رمزه" },
  noMatches: {
    en: "No providers match. Change the search or clear the filters.",
    ar: "لا يوجد مقدمو خدمة مطابقون. عدّل البحث أو أزل عوامل التصفية.",
  },
  type: { en: "Type (Hospital/Clinic/Lab/Pharmacy/Imaging)", ar: "النوع" },
  create: { en: "Onboard provider", ar: "إضافة مقدم خدمة" },
  created: { en: "Provider created (Draft) — proceed to credentialing.", ar: "تم إنشاء مقدم الخدمة (مسودة)." },
  needFields: { en: "Code, legal name, and a valid type are required.", ar: "الرمز والاسم والنوع الصحيح مطلوبة." },

  // 33.7 — the roll-up belongs to the Network Team, and this portal serves two roles (see below).
  notYourNetwork: {
    en: "This is the network-wide view, which belongs to Mersal's Network Team. A provider's own administrator sees their own organisation — its directory entry, contracts and locations are in the sections above.",
    ar: "هذه نظرة على الشبكة بالكامل، وهي من اختصاص فريق الشبكة في مرسال. أما مسؤول مقدم الخدمة فيرى مؤسسته وحدها — بيانها في الدليل وعقودها ومواقعها في الأقسام أعلاه.",
  },
} satisfies Record<string, Localized>;

// 18.D2 (U7): see useFormat — Africa/Cairo + the app locale, never the browser's.
// "Radiology" is the canonical spelling since 29.1; "Imaging" stays until the deferred contract migration
// retires it, so a provider onboarded before the switch still matches. Order matters only for the picker.
const VALID_TYPES = ["Hospital", "Clinic", "Lab", "Pharmacy", "Radiology", "Imaging"] as const;

function directoryColumns(t: (l: Localized) => string): Column<ProviderSummary>[] {
  return [
    { key: "provider", header: t(S.provider), cell: (r) => r.legalName, sortable: true, sortValue: (r) => r.legalName },
    { key: "code", header: t(S.codeH), cell: (r) => <span className="tnum">{r.code}</span>, sortable: true, sortValue: (r) => r.code },
    { key: "type", header: t(S.typeH), cell: (r) => r.providerType, sortable: true, sortValue: (r) => r.providerType },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "onboarding", header: t(S.onboarding), cell: (r) => <StatusChip kind="neu" label={r.onboardingState} />, sortable: true, sortValue: (r) => r.onboardingState },
  ];
}

/** Providers directory — the tenant's whole network (Network-Team scope). */
export function NetworkDirectory() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<ProviderSummary[]>(() => api.providerList(), []);
  const cols = directoryColumns(t);

  /*
    This is the WHOLE network in one response, and it was rendered as one unbroken list: no search, no
    filter, no pager. Finding a provider meant scrolling past every other one.

    Both filter groups are derived from the rows rather than declared, for the reason the clinician panel
    gives about branches: the vocabulary is the network's, not the client's. A hardcoded type list would show
    a chip for Imaging in a network with no imaging centre, and miss whatever is added next.
  */
  const rows = useMemo(() => state.data ?? [], [state.data]);
  const filters: TableFilterSpec<ProviderSummary>[] = useMemo(() => {
    const groups: TableFilterSpec<ProviderSummary>[] = [];
    const types = [...new Set(rows.map((r) => r.providerType))].sort((a, b) => a.localeCompare(b));
    if (types.length > 1) {
      groups.push({
        key: "type",
        label: t(S.typeH),
        options: types.map((x) => ({ value: x, label: x })),
        match: (r, value) => r.providerType === value,
      });
    }
    const states = [...new Set(rows.map((r) => r.onboardingState))].sort((a, b) => a.localeCompare(b));
    if (states.length > 1) {
      groups.push({
        key: "onboarding",
        label: t(S.onboarding),
        options: states.map((x) => ({ value: x, label: x })),
        match: (r, value) => r.onboardingState === value,
      });
    }
    return groups;
  }, [rows, t]);

  const query = useTableQuery<ProviderSummary>({
    rows,
    columns: cols,
    // The provider code is what a claim or a contract cites, so it has to be searchable even though the
    // name is what an operator would think of first.
    searchText: (r) => [r.legalName, r.code, r.providerType].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.dirSearchHint),
    filters,
    pageSize: 25,
    initialSortKey: "provider",
    persistKey: "network-directory",
  });

  return (
    <>
      <PageHeader title={t(S.dirTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.dirEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.dirTitle)}
              emptyLabel={t(S.dirEmpty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

/**
 * Performance — the network roll-up, from provider-service.
 *
 * ============================================================================================================
 * 33.7 — THE FOUR NUMBERS THAT WERE COUNTED IN THE BROWSER
 * ============================================================================================================
 * This screen used to fetch the provider DIRECTORY and count it:
 *
 *   const by = (label: string) => rows.filter((r) => r.status.label.en === label).length;
 *
 * `status` is the `{kind, label}` chip this client assembles for RENDERING. So the roll-up was a tally of a
 * piece of English prose, over whatever the directory projection happened to return, and it would have gone
 * to four zeroes — silently, plausibly — the first time a status was relabelled or a new one was added that
 * `providerStatusChip` did not recognise.
 *
 * `GET /api/v1/metrics` has returned exactly `{total, active, suspended, terminated}` since phase 2b,
 * computed from the `ProviderStatus` enum over the tenant, excluding soft-deleted rows. It was never routed
 * at the gateway — and the route-coverage guard whose whole job is to catch an unrouted resource had
 * "metrics" in its ignore list, meant for the Prometheus scrape. Both are fixed; this asks the service.
 *
 * The authorization is the part that makes this more than tidiness. The endpoint answers a provider-scoped
 * caller with 403: a provider must not learn the shape of the network it competes in. A count assembled from
 * a list the caller can already read enforces none of that.
 */
export function NetworkPerformance() {
  const t = useLoc();
  const { session } = useAuth();
  /*
    TWO ROLES SHARE THIS PORTAL and only one of them may read this.

    `ROLE_MAP` maps both the issuer's `network_team` (Mersal's Network Team — tenant-wide, T2) and its
    `provider_admin` (one provider's own administrator — T4, bound to that provider by ABAC and RLS) onto the
    single portal role `provider_admin`. provider-service answers this endpoint with 403 for the second, and
    it is right to: a provider must not learn the shape of the network it competes in.

    So the section says whose view it is rather than fetching and rendering the refusal as an error. Hiding
    it outright would be better still — the org-admin portal drops the tenant registry for exactly this
    reason (28.10) — and it cannot be done from a permission, because both roles carry the same portal
    permissions by construction. The portal split that fixes it properly is design 52 §5.
  */
  if (!mayReadTheNetworkRollup(session?.issuerRoles)) {
    return (
      <>
        <PageHeader title={t(S.perfTitle)} />
        <Card as="section" style={{ padding: "var(--sp5)" }}>
          <InlineAlert tone="info">{t(S.notYourNetwork)}</InlineAlert>
        </Card>
      </>
    );
  }
  return <NetworkRollup />;
}

function NetworkRollup() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<NetworkMetrics>(() => api.networkMetrics(), []);
  return (
    <>
      <PageHeader title={t(S.perfTitle)} />
      <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.dirEmpty}>
        {(m) => (
          <div className="kpi-row">
            <KpiCard label={t(S.total)} value={String(m.total)} />
            <KpiCard label={t(S.active)} value={String(m.active)} />
            <KpiCard label={t(S.suspended)} value={String(m.suspended)} />
            <KpiCard label={t(S.terminated)} value={String(m.terminated)} />
          </div>
        )}
      </AsyncSection>
    </>
  );
}

/** A provider picker + a per-provider detail panel (shared by Contracts and Locations sections). */
function ProviderScoped({ title, render }: { title: Localized; render: (p: ProviderSummary) => React.ReactNode }) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<ProviderSummary[]>(() => api.providerList(), []);
  const [picked, setPicked] = useState<ProviderSummary | null>(null);
  return (
    <>
      <PageHeader title={t(title)} />
      {picked ? (
        <div className="stack" style={{ gap: "var(--sp3)" }}>
          <div className="result-head">
            <h2 style={{ margin: 0 }}>{picked.legalName}</h2>
            <Button variant="ghost" size="sm" onClick={() => setPicked(null)}>{t(S.back)}</Button>
          </div>
          {render(picked)}
        </div>
      ) : (
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.dirEmpty}>
            {(rows) => (
              <>
                <p className="muted" style={{ marginTop: 0, paddingInline: "var(--sp2)" }}>{t(S.pickProvider)}</p>
                <ul className="stack" style={{ listStyle: "none", margin: 0, padding: 0, gap: "var(--sp2)" }}>
                  {rows.map((p) => (
                    <li key={p.id}>
                      <button type="button" className="picker-row" onClick={() => setPicked(p)}>
                        <span>{p.legalName}</span>
                        <span className="tnum muted">{p.code}</span>
                        <StatusChip kind={p.status.kind} label={t(p.status.label)} />
                      </button>
                    </li>
                  ))}
                </ul>
              </>
            )}
          </AsyncSection>
        </Card>
      )}
    </>
  );
}

export function NetworkContracts() {
  const api = useApi();
  const t = useLoc();
  return <ProviderScoped title={S.contractsTitle} render={(p) => <ContractsPanel providerId={p.id} t={t} api={api} />} />;
}
function ContractsPanel({ providerId, t, api }: { providerId: string; t: (l: Localized) => string; api: ReturnType<typeof useApi> }) {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const state = useAsync<ProviderContract[]>(() => api.providerContracts(providerId), [providerId]);
  const cols: Column<ProviderContract>[] = [
    { key: "no", header: t(S.contractNo), cell: (r) => <span className="tnum">{r.contractNo}</span>, sortable: true, sortValue: (r) => r.contractNo },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "from", header: t(S.from), cell: (r) => <span className="tnum">{fmt.date(r.effectiveFrom)}</span>, sortable: true, sortValue: (r) => r.effectiveFrom },
    { key: "to", header: t(S.to), cell: (r) => <span className="tnum">{r.effectiveTo ? fmt.date(r.effectiveTo) : "—"}</span> },
    { key: "lines", header: t(S.lines), cell: (r) => r.serviceLines, numeric: true, sortable: true, sortValue: (r) => r.serviceLines },
  ];
  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.noContracts}>
        {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.contractsTitle)} />}
      </AsyncSection>
    </Card>
  );
}

export function NetworkLocations() {
  const api = useApi();
  const t = useLoc();
  return <ProviderScoped title={S.locationsTitle} render={(p) => <LocationsPanel providerId={p.id} t={t} api={api} />} />;
}
function LocationsPanel({ providerId, t, api }: { providerId: string; t: (l: Localized) => string; api: ReturnType<typeof useApi> }) {
  const state = useAsync<ProviderLocation[]>(() => api.providerLocations(providerId), [providerId]);
  const cols: Column<ProviderLocation>[] = [
    { key: "name", header: t(S.name), cell: (r) => r.name, sortable: true, sortValue: (r) => r.name },
    { key: "gov", header: t(S.governorate), cell: (r) => r.governorate ?? "—", sortable: true, sortValue: (r) => r.governorate },
    { key: "addr", header: t(S.address), cell: (r) => r.address ?? "—", sortable: true, sortValue: (r) => r.address },
    { key: "primary", header: t(S.primary), cell: (r) => (r.isPrimary ? <StatusChip kind="ok" label={t(S.primary)} /> : "—") },
  ];
  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.noLocations}>
        {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.locationsTitle)} />}
      </AsyncSection>
    </Card>
  );
}

/** Onboarding — create a new provider (Draft), the first step of the Network-Team onboarding workflow. */
export function NetworkOnboarding() {
  const api = useApi();
  const t = useLoc();
  const [code, setCode] = useState("");
  const [legalName, setLegalName] = useState("");
  const [type, setType] = useState("");
  const [status, setStatus] = useState<"idle" | "saving" | "done" | "invalid">("idle");
  const write = useWrite();          // 18.D1 — per-form idempotency key + typed failures
  const { lang } = useTheme();

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (code.trim() === "" || legalName.trim() === "" || !VALID_TYPES.includes(type as (typeof VALID_TYPES)[number])) {
      setStatus("invalid");
      return;
    }
    setStatus("saving");
    // 18.D1 (U1): this had neither an error surface nor an idempotency key — a retry created a duplicate
    // provider, and a duplicate provider means contracts and claims attached to the wrong one.
    const ok = await write.run((key) =>
      api.createProvider({ code: code.trim(), legalName: legalName.trim(), providerType: type as CreateProviderInput["providerType"] }, key));
    if (ok) {
      setStatus("done");
      setCode(""); setLegalName(""); setType("");
    } else {
      setStatus("idle");
    }
  }
  return (
    <>
      <PageHeader title={t(S.onboardTitle)} />
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <form onSubmit={submit} className="stack" aria-label={t(S.onboardTitle)}>
          <InputField label={t(S.code)} value={code} onChange={(e) => setCode(e.currentTarget.value)} autoComplete="off" />
          <InputField label={t(S.legalName)} value={legalName} onChange={(e) => setLegalName(e.currentTarget.value)} autoComplete="off" />
          <InputField label={t(S.type)} value={type} onChange={(e) => setType(e.currentTarget.value)} autoComplete="off" />
          <div aria-live="polite" className="stack" style={{ gap: "var(--sp2)" }}>
            {status === "invalid" && <InlineAlert tone="bad">{t(S.needFields)}</InlineAlert>}
            {/* 18.D1 (U2): the server's own reason, translated and typed — a 409 reads
                differently from a dropped connection, because they demand opposite actions. */}
            {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
            {status === "done" && <StatusChip kind="ok" label={t(S.created)} />}
            <div><Button leadingIcon={<Icon name="plus" />} type="submit" variant="primary" loading={status === "saving"}>{t(S.create)}</Button></div>
          </div>
        </form>
      </Card>
    </>
  );
}

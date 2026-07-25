import { useState } from "react";
import { Button, Card, DataTable, InlineAlert, InputField, KpiCard, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { CreateProviderInput, Localized, ProviderContract, ProviderLocation, ProviderSummary } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  dirTitle: { en: "Providers directory", ar: "دليل مقدمي الخدمة" },
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
  type: { en: "Type (Hospital/Clinic/Lab/Pharmacy/Imaging)", ar: "النوع" },
  create: { en: "Onboard provider", ar: "إضافة مقدم خدمة" },
  created: { en: "Provider created (Draft) — proceed to credentialing.", ar: "تم إنشاء مقدم الخدمة (مسودة)." },
  needFields: { en: "Code, legal name, and a valid type are required.", ar: "الرمز والاسم والنوع الصحيح مطلوبة." },
} satisfies Record<string, Localized>;

const dt = (s?: string) => (s ? new Date(s).toLocaleDateString() : "—");
const VALID_TYPES = ["Hospital", "Clinic", "Lab", "Pharmacy", "Imaging"] as const;

function directoryColumns(t: (l: Localized) => string): Column<ProviderSummary>[] {
  return [
    { key: "provider", header: t(S.provider), cell: (r) => r.legalName },
    { key: "code", header: t(S.codeH), cell: (r) => <span className="tnum">{r.code}</span> },
    { key: "type", header: t(S.typeH), cell: (r) => r.providerType },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "onboarding", header: t(S.onboarding), cell: (r) => <StatusChip kind="neu" label={r.onboardingState} /> },
  ];
}

/** Providers directory — the tenant's whole network (Network-Team scope). */
export function NetworkDirectory() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<ProviderSummary[]>(() => api.providerList(), []);
  const cols = directoryColumns(t);
  return (
    <>
      <PageHeader title={t(S.dirTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.dirEmpty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.dirTitle)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

/** Performance — network roll-up derived from the directory (active/suspended/terminated counts). */
export function NetworkPerformance() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<ProviderSummary[]>(() => api.providerList(), []);
  return (
    <>
      <PageHeader title={t(S.perfTitle)} />
      <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.dirEmpty}>
        {(rows) => {
          const by = (label: string) => rows.filter((r) => r.status.label.en === label).length;
          return (
            <div className="kpi-row">
              <KpiCard label={t(S.total)} value={String(rows.length)} />
              <KpiCard label={t(S.active)} value={String(by("Active"))} />
              <KpiCard label={t(S.suspended)} value={String(by("Suspended"))} />
              <KpiCard label={t(S.terminated)} value={String(by("Terminated"))} />
            </div>
          );
        }}
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
  const state = useAsync<ProviderContract[]>(() => api.providerContracts(providerId), [providerId]);
  const cols: Column<ProviderContract>[] = [
    { key: "no", header: t(S.contractNo), cell: (r) => <span className="tnum">{r.contractNo}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    { key: "from", header: t(S.from), cell: (r) => <span className="tnum">{dt(r.effectiveFrom)}</span> },
    { key: "to", header: t(S.to), cell: (r) => <span className="tnum">{r.effectiveTo ? dt(r.effectiveTo) : "—"}</span> },
    { key: "lines", header: t(S.lines), cell: (r) => <span className="tnum">{r.serviceLines}</span> },
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
    { key: "name", header: t(S.name), cell: (r) => r.name },
    { key: "gov", header: t(S.governorate), cell: (r) => r.governorate ?? "—" },
    { key: "addr", header: t(S.address), cell: (r) => r.address ?? "—" },
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

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (code.trim() === "" || legalName.trim() === "" || !VALID_TYPES.includes(type as (typeof VALID_TYPES)[number])) {
      setStatus("invalid");
      return;
    }
    setStatus("saving");
    try {
      await api.createProvider({ code: code.trim(), legalName: legalName.trim(), providerType: type as CreateProviderInput["providerType"] });
      setStatus("done");
      setCode(""); setLegalName(""); setType("");
    } catch {
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
            {status === "done" && <StatusChip kind="ok" label={t(S.created)} />}
            <div><Button type="submit" variant="primary" loading={status === "saving"}>{t(S.create)}</Button></div>
          </div>
        </form>
      </Card>
    </>
  );
}

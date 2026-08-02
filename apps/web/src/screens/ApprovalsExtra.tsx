import { useState } from "react";
import { useFormat } from "../i18n/useFormat";
import { Button, Card, DataTable, Icon, InlineAlert, InputField, KpiCard, StatusChip, TextareaField, useTheme } from "@mersal/design-system";
import { useWrite, writeErrorText } from "../api/useWrite";
import type { Column } from "@mersal/design-system";
import type { ApprovalItem, Localized, TatSummary } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  slaTitle: { en: "SLA / TAT Board", ar: "لوحة الاستجابة" },
  slaEmpty: { en: "No decided authorizations to report on yet.", ar: "لا توجد موافقات مُقرّرة بعد." },
  total: { en: "Decided", ar: "تم البت فيها" },
  avg: { en: "Avg TAT (min)", ar: "متوسط الاستجابة (د)" },
  p95: { en: "P95 TAT (min)", ar: "الاستجابة p95 (د)" },
  breaches: { en: "SLA breaches", ar: "تجاوزات الاستجابة" },
  status: { en: "Status", ar: "الحالة" },
  count: { en: "Count", ar: "العدد" },

  manualTitle: { en: "Manual Authorization", ar: "تفويض يدوي" },
  beneficiary: { en: "Beneficiary ID", ar: "معرّف المستفيد" },
  codes: { en: "Service codes (comma-separated)", ar: "رموز الخدمات (مفصولة بفواصل)" },
  justification: { en: "Justification", ar: "المبرر" },
  create: { en: "Create manual authorization", ar: "إنشاء تفويض يدوي" },
  created: { en: "Manual authorization created — flagged for retrospective review.", ar: "تم إنشاء التفويض — مُعلّم للمراجعة اللاحقة." },
  needFields: { en: "Beneficiary, at least one code, and a justification are required.", ar: "المستفيد ورمز واحد على الأقل والمبرر مطلوبة." },

  emgTitle: { en: "Emergency / Override", ar: "طارئ / تجاوز" },
  emgEmpty: { en: "No pending authorizations.", ar: "لا توجد موافقات معلّقة." },
  patient: { en: "Patient", ar: "المريض" },
  service: { en: "Service", ar: "الخدمة" },
  emgApprove: { en: "Emergency approve", ar: "اعتماد طارئ" },
  emgJust: { en: "Reason for emergency approval", ar: "سبب الاعتماد الطارئ" },
  confirm: { en: "Confirm", ar: "تأكيد" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  approved: { en: "Emergency approved.", ar: "تم الاعتماد الطارئ." },
} satisfies Record<string, Localized>;

// 18.D2 (U7): grouped digits follow the app locale (Arabic-Indic in ar-EG), not the browser's.

/** SLA / TAT board — turnaround + breach metrics across decided authorizations (PHI-free reporting read). */
export function ApprovalsSla() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const state = useAsync<TatSummary>(() => api.slaSummary(), []);
  const cols: Column<TatSummary["byStatus"][number]>[] = [
    { key: "status", header: t(S.status), cell: (r) => r.status },
    { key: "count", header: t(S.count), cell: (r) => <span className="tnum">{r.count}</span> },
    { key: "avg", header: t(S.avg), cell: (r) => <span className="tnum">{fmt.number(Math.round(r.avgMinutes))}</span> },
    { key: "p95", header: t(S.p95), cell: (r) => <span className="tnum">{fmt.number(Math.round(r.p95Minutes))}</span> },
    { key: "breaches", header: t(S.breaches), cell: (r) => <span className="tnum">{r.breaches}</span> },
  ];
  return (
    <>
      <PageHeader title={t(S.slaTitle)} />
      <AsyncSection state={state} isEmpty={(d) => d.total === 0} emptyLabel={S.slaEmpty}>
        {(d) => (
          <div className="stack" style={{ gap: "var(--sp4)" }}>
            <div className="kpi-row">
              <KpiCard label={t(S.total)} value={fmt.number(Math.round(d.total))} />
              <KpiCard label={t(S.avg)} value={fmt.number(Math.round(d.avgMinutes))} />
              <KpiCard label={t(S.p95)} value={fmt.number(Math.round(d.p95Minutes))} />
              <KpiCard label={t(S.breaches)} value={fmt.number(Math.round(d.breaches))} />
            </div>
            <Card as="section" style={{ padding: "var(--sp3)" }}>
              <DataTable columns={cols} rows={d.byStatus} rowKey={(r) => r.status} caption={t(S.slaTitle)} />
            </Card>
          </div>
        )}
      </AsyncSection>
    </>
  );
}

/** Manual authorization — a break-glass approval created out-of-band (always flagged for retrospective review). */
export function ApprovalsManual() {
  const api = useApi();
  const t = useLoc();
  const [beneficiaryId, setBeneficiaryId] = useState("");
  const [codes, setCodes] = useState("");
  const [justification, setJustification] = useState("");
  const [status, setStatus] = useState<"idle" | "saving" | "done" | "invalid">("idle");
  const write = useWrite();          // 18.D1 — per-form idempotency key + typed failures
  const { lang } = useTheme();

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const serviceCodes = codes.split(",").map((c) => c.trim()).filter(Boolean);
    if (beneficiaryId.trim() === "" || serviceCodes.length === 0 || justification.trim() === "") {
      setStatus("invalid");
      return;
    }
    setStatus("saving");
    // 18.D1 (U1): a MANUAL AUTHORIZATION is a break-glass-adjacent write — it grants coverage outside the
    // automated path. Failing it silently, with no key, meant a retry could issue two authorizations for the
    // same service and both would be billable.
    const ok = await write.run((key) =>
      api.createManualAuth({ beneficiaryId: beneficiaryId.trim(), serviceCodes, justification: justification.trim() }, key));
    if (ok) {
      setStatus("done");
      setBeneficiaryId(""); setCodes(""); setJustification("");
    } else {
      setStatus("idle");
    }
  }
  return (
    <>
      <PageHeader title={t(S.manualTitle)} />
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <form onSubmit={submit} className="stack" aria-label={t(S.manualTitle)}>
          <InputField label={t(S.beneficiary)} value={beneficiaryId} onChange={(e) => setBeneficiaryId(e.currentTarget.value)} autoComplete="off" />
          <InputField label={t(S.codes)} value={codes} onChange={(e) => setCodes(e.currentTarget.value)} autoComplete="off" />
          <TextareaField label={t(S.justification)} value={justification} onChange={(e) => setJustification(e.currentTarget.value)} rows={3} />
          <div aria-live="polite" className="stack" style={{ gap: "var(--sp2)" }}>
            {status === "invalid" && <InlineAlert tone="bad">{t(S.needFields)}</InlineAlert>}
            {/* 18.D1 (U2): the server's own reason, translated and typed — a 409 reads
                differently from a dropped connection, because they demand opposite actions. */}
            {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
            {status === "done" && <StatusChip kind="ok" label={t(S.created)} />}
            <div><Button type="submit" variant="primary"
              leadingIcon={<Icon name="plus" />} loading={status === "saving"}>{t(S.create)}</Button></div>
          </div>
        </form>
      </Card>
    </>
  );
}

/** Emergency / override — emergency-approve a pending authorization (mandatory reason, retrospective review). */
export function ApprovalsEmergency() {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<ApprovalItem[]>(() => api.approvalWorklist(), []);
  const [active, setActive] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<Set<string>>(new Set());

  async function confirm(id: string) {
    if (reason.trim() === "") return;
    setBusy(true);
    try {
      await api.emergencyApprove(id, reason.trim());
      setDone((prev) => new Set(prev).add(id));
      setActive(null);
      setReason("");
    } finally {
      setBusy(false);
    }
  }

  const cols: Column<ApprovalItem>[] = [
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.patient.token}</span> },
    { key: "service", header: t(S.service), cell: (r) => <span className="tnum">{r.service.code}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    {
      key: "act",
      header: "",
      cell: (r) =>
        done.has(r.id) ? (
          <StatusChip kind="ok" label={t(S.approved)} />
        ) : active === r.id ? (
          <div className="stack" style={{ gap: "var(--sp2)", minWidth: 260 }}>
            <InputField label={t(S.emgJust)} value={reason} onChange={(e) => setReason(e.currentTarget.value)} autoComplete="off" />
            <div style={{ display: "flex", gap: "var(--sp2)" }}>
              <Button variant="primary"
              leadingIcon={<Icon name="check2" />} size="sm" loading={busy} onClick={() => void confirm(r.id)}>{t(S.confirm)}</Button>
              <Button variant="ghost" size="sm" onClick={() => { setActive(null); setReason(""); }}>{t(S.cancel)}</Button>
            </div>
          </div>
        ) : (
          <Button variant="secondary" size="sm" onClick={() => setActive(r.id)}>{t(S.emgApprove)}</Button>
        ),
    },
  ];
  return (
    <>
      <PageHeader title={t(S.emgTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.emgEmpty}>
          {(rows) => (
            <div aria-live="polite">
              <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.emgTitle)} />
            </div>
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

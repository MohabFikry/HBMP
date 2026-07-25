import { useState } from "react";
import { Button, Card, DataTable, InlineAlert, InputField, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { BeneficiaryRow, Localized, RegisterBeneficiaryInput } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { ApiError } from "../api/http";
import { PageHeader, useLoc } from "./_shared";

const S = {
  manageTitle: { en: "Search / manage", ar: "بحث / إدارة" },
  statusTitle: { en: "Status & reactivation", ar: "الحالة وإعادة التفعيل" },
  registerTitle: { en: "Register new", ar: "تسجيل جديد" },
  searchField: { en: "Search by name", ar: "ابحث بالاسم" },
  search: { en: "Search", ar: "بحث" },
  idle: { en: "Search for a beneficiary by name.", ar: "ابحث عن مستفيد بالاسم." },
  loading: { en: "Searching…", ar: "جارٍ البحث…" },
  none: { en: "No beneficiaries match that search.", ar: "لا يوجد مستفيدون مطابقون." },
  error: { en: "Couldn't reach the registry. Try again.", ar: "تعذّر الوصول للسجل. حاول مجدداً." },
  name: { en: "Name", ar: "الاسم" },
  memberNo: { en: "Member no.", ar: "رقم العضوية" },
  identifier: { en: "Identifier", ar: "المعرّف" },
  status: { en: "Status", ar: "الحالة" },
  action: { en: "Action", ar: "إجراء" },
  changeStatus: { en: "Change status", ar: "تغيير الحالة" },
  activate: { en: "Activate", ar: "تفعيل" },
  suspend: { en: "Suspend", ar: "إيقاف" },
  reason: { en: "Reason", ar: "السبب" },
  confirm: { en: "Confirm", ar: "تأكيد" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  changed: { en: "Status updated.", ar: "تم تحديث الحالة." },

  givenName: { en: "Given name", ar: "الاسم الأول" },
  familyName: { en: "Family name", ar: "اسم العائلة" },
  birthDate: { en: "Birth date (YYYY-MM-DD)", ar: "تاريخ الميلاد" },
  idType: { en: "Identifier type (NationalID/Passport/RefugeeID/UNHCRNo)", ar: "نوع المعرّف" },
  idValue: { en: "Identifier value", ar: "قيمة المعرّف" },
  phone: { en: "Phone", ar: "الهاتف" },
  register: { en: "Register beneficiary", ar: "تسجيل المستفيد" },
  registered: { en: "Beneficiary registered (Pending) — proceed to eligibility.", ar: "تم التسجيل (قيد الانتظار)." },
  needFields: { en: "Given/family name, identifier type + value are required.", ar: "الاسم الأول/العائلة ونوع/قيمة المعرّف مطلوبة." },
} satisfies Record<string, Localized>;

const ID_TYPES = ["NationalID", "Passport", "RefugeeID", "UNHCRNo"] as const;

function beneficiaryColumns(t: (l: Localized) => string): Column<BeneficiaryRow>[] {
  return [
    { key: "name", header: t(S.name), cell: (r) => `${r.givenName} ${r.familyName}` },
    { key: "member", header: t(S.memberNo), cell: (r) => <span className="tnum">{r.memberNo ?? "—"}</span> },
    { key: "id", header: t(S.identifier), cell: (r) => <span className="tnum">{r.identifiers[0] ? `${r.identifiers[0].type}: ${r.identifiers[0].value}` : "—"}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];
}

/** A shared name search that renders its results through a caller-supplied column set. */
function BeneficiarySearch({ title, extraCols }: { title: Localized; extraCols?: (reload: () => void) => Column<BeneficiaryRow> }) {
  const api = useApi();
  const t = useLoc();
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<"idle" | "loading" | "error" | "ready">("idle");
  const [rows, setRows] = useState<BeneficiaryRow[]>([]);

  async function run(e: React.FormEvent) {
    e.preventDefault();
    if (query.trim().length < 1) return;
    setStatus("loading");
    try {
      setRows(await api.beneficiarySearch({ name: query.trim() }));
      setStatus("ready");
    } catch (err) {
      void (err instanceof ApiError);
      setStatus("error");
    }
  }
  const reload = () => void run({ preventDefault() {} } as React.FormEvent);

  const cols = beneficiaryColumns(t);
  if (extraCols) cols.push(extraCols(reload));

  return (
    <>
      <PageHeader title={t(title)} />
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <form onSubmit={run} className="stack" aria-label={t(title)}>
          <InputField label={t(S.searchField)} value={query} onChange={(e) => setQuery(e.currentTarget.value)} autoComplete="off" />
          <div><Button type="submit" variant="primary" loading={status === "loading"}>{t(S.search)}</Button></div>
        </form>
      </Card>
      <div aria-live="polite" style={{ marginTop: "var(--sp4)" }}>
        {status === "idle" && <Card style={{ padding: "var(--sp5)" }}><p className="muted">{t(S.idle)}</p></Card>}
        {status === "error" && <Card style={{ padding: "var(--sp5)" }}><StatusChip kind="bad" label={t(S.error)} /></Card>}
        {status === "ready" && rows.length === 0 && <Card style={{ padding: "var(--sp5)" }}><StatusChip kind="neu" label={t(S.none)} /></Card>}
        {status === "ready" && rows.length > 0 && (
          <Card as="section" style={{ padding: "var(--sp3)" }}>
            <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(title)} />
          </Card>
        )}
      </div>
    </>
  );
}

/** Search / manage — find beneficiaries by name (read-only min-necessary identity view). */
export function BeneficiaryManage() {
  return <BeneficiarySearch title={S.manageTitle} />;
}

/** Status & reactivation — find a beneficiary, then activate/suspend with a reason. */
export function BeneficiaryStatus() {
  const api = useApi();
  const t = useLoc();
  const [active, setActive] = useState<string | null>(null);
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [done, setDone] = useState<Set<string>>(new Set());

  async function apply(id: string, toStatus: string) {
    if (reason.trim() === "") return;
    setBusy(true);
    try {
      await api.changeBeneficiaryStatus(id, toStatus, reason.trim());
      setDone((prev) => new Set(prev).add(id));
      setActive(null);
      setReason("");
    } finally {
      setBusy(false);
    }
  }

  const actionCol = (): Column<BeneficiaryRow> => ({
    key: "action",
    header: t(S.action),
    cell: (r) =>
      done.has(r.id) ? (
        <StatusChip kind="ok" label={t(S.changed)} />
      ) : active === r.id ? (
        <div className="stack" style={{ gap: "var(--sp2)", minWidth: 240 }}>
          <InputField label={t(S.reason)} value={reason} onChange={(e) => setReason(e.currentTarget.value)} autoComplete="off" />
          <div style={{ display: "flex", gap: "var(--sp2)", flexWrap: "wrap" }}>
            <Button variant="primary" size="sm" loading={busy} onClick={() => void apply(r.id, "Active")}>{t(S.activate)}</Button>
            <Button variant="secondary" size="sm" loading={busy} onClick={() => void apply(r.id, "Suspended")}>{t(S.suspend)}</Button>
            <Button variant="ghost" size="sm" onClick={() => { setActive(null); setReason(""); }}>{t(S.cancel)}</Button>
          </div>
        </div>
      ) : (
        <Button variant="secondary" size="sm" onClick={() => setActive(r.id)}>{t(S.changeStatus)}</Button>
      ),
  });

  return <BeneficiarySearch title={S.statusTitle} extraCols={() => actionCol()} />;
}

/** Register new — create a beneficiary (Pending), the first step of registration (US-001). */
export function BeneficiaryRegister() {
  const api = useApi();
  const t = useLoc();
  const [f, setF] = useState({ givenName: "", familyName: "", birthDate: "", idType: "", idValue: "", phone: "" });
  const [status, setStatus] = useState<"idle" | "saving" | "done" | "invalid">("idle");
  const set = (k: keyof typeof f) => (e: React.ChangeEvent<HTMLInputElement>) => setF((s) => ({ ...s, [k]: e.currentTarget.value }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (f.givenName.trim() === "" || f.familyName.trim() === "" || !ID_TYPES.includes(f.idType as (typeof ID_TYPES)[number]) || f.idValue.trim() === "") {
      setStatus("invalid");
      return;
    }
    setStatus("saving");
    try {
      const input: RegisterBeneficiaryInput = {
        givenName: f.givenName.trim(),
        familyName: f.familyName.trim(),
        birthDate: f.birthDate.trim() || undefined,
        identifierType: f.idType as RegisterBeneficiaryInput["identifierType"],
        identifierValue: f.idValue.trim(),
        phone: f.phone.trim() || undefined,
      };
      await api.registerBeneficiary(input);
      setStatus("done");
      setF({ givenName: "", familyName: "", birthDate: "", idType: "", idValue: "", phone: "" });
    } catch {
      setStatus("idle");
    }
  }
  return (
    <>
      <PageHeader title={t(S.registerTitle)} />
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <form onSubmit={submit} className="stack" aria-label={t(S.registerTitle)}>
          <div className="kv-grid">
            <InputField label={t(S.givenName)} value={f.givenName} onChange={set("givenName")} autoComplete="off" />
            <InputField label={t(S.familyName)} value={f.familyName} onChange={set("familyName")} autoComplete="off" />
            <InputField label={t(S.birthDate)} value={f.birthDate} onChange={set("birthDate")} inputMode="numeric" autoComplete="off" />
            <InputField label={t(S.phone)} value={f.phone} onChange={set("phone")} inputMode="tel" autoComplete="off" />
            <InputField label={t(S.idType)} value={f.idType} onChange={set("idType")} autoComplete="off" />
            <InputField label={t(S.idValue)} value={f.idValue} onChange={set("idValue")} autoComplete="off" />
          </div>
          <div aria-live="polite" className="stack" style={{ gap: "var(--sp2)" }}>
            {status === "invalid" && <InlineAlert tone="bad">{t(S.needFields)}</InlineAlert>}
            {status === "done" && <StatusChip kind="ok" label={t(S.registered)} />}
            <div><Button type="submit" variant="primary" loading={status === "saving"}>{t(S.register)}</Button></div>
          </div>
        </form>
      </Card>
    </>
  );
}

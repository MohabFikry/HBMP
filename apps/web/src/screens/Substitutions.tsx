import { useState } from "react";
import { Button, Card, DataTable, Icon, InputField, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { DrugRef, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { ApiError } from "../api/http";
import { PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Substitutions", ar: "البدائل" },
  field: { en: "Search a medication", ar: "ابحث عن دواء" },
  help: { en: "Find a drug, then view its policy-approved alternatives (same therapeutic class).", ar: "ابحث عن دواء ثم اعرض بدائله المعتمدة (نفس الفئة العلاجية)." },
  search: { en: "Search", ar: "بحث" },
  idle: { en: "Search for a medication to see approved substitutions.", ar: "ابحث عن دواء لعرض البدائل المعتمدة." },
  loading: { en: "Searching…", ar: "جارٍ البحث…" },
  noHits: { en: "No medications match that search.", ar: "لا توجد أدوية مطابقة." },
  error: { en: "Couldn't reach the formulary. Try again.", ar: "تعذّر الوصول إلى الدليل الدوائي. حاول مجدداً." },
  drug: { en: "Medication", ar: "الدواء" },
  atc: { en: "ATC", ar: "ATC" },
  form: { en: "Form", ar: "الشكل" },
  strength: { en: "Strength", ar: "التركيز" },
  alternatives: { en: "Approved alternatives", ar: "البدائل المعتمدة" },
  viewAlts: { en: "Alternatives", ar: "البدائل" },
  noAlts: { en: "No approved alternatives on the formulary.", ar: "لا توجد بدائل معتمدة في الدليل." },
} satisfies Record<string, Localized>;

/** Formulary substitutions — search a drug, then list its policy-approved alternatives (US-052). */
export function Substitutions() {
  const api = useApi();
  const t = useLoc();
  const [query, setQuery] = useState("");
  const [status, setStatus] = useState<"idle" | "loading" | "error" | "ready">("idle");
  const [hits, setHits] = useState<DrugRef[]>([]);
  const [selected, setSelected] = useState<DrugRef | null>(null);
  const [alts, setAlts] = useState<DrugRef[] | null>(null);

  async function run(e: React.FormEvent) {
    e.preventDefault();
    if (query.trim().length < 2) return;
    setStatus("loading");
    setSelected(null);
    setAlts(null);
    try {
      setHits(await api.searchDrugs(query.trim()));
      setStatus("ready");
    } catch (err) {
      void (err instanceof ApiError);
      setStatus("error");
    }
  }

  async function pick(d: DrugRef) {
    setSelected(d);
    setAlts(null);
    setAlts(await api.drugAlternatives(d.drugId));
  }

  const hitCols: Column<DrugRef>[] = [
    { key: "drug", header: t(S.drug), cell: (d) => t(d.name), sortable: true, sortValue: (d) => t(d.name) },
    { key: "form", header: t(S.form), cell: (d) => d.form ?? "—", sortable: true, sortValue: (d) => d.form },
    { key: "strength", header: t(S.strength), cell: (d) => d.strength ?? "—", sortable: true, sortValue: (d) => d.strength },
    { key: "atc", header: t(S.atc), cell: (d) => <span className="tnum">{d.atcCode ?? "—"}</span> },
    { key: "act", header: "", cell: (d) => <Button variant="secondary" size="sm" onClick={() => void pick(d)}>{t(S.viewAlts)}</Button> },
  ];
  const altCols: Column<DrugRef>[] = [
    { key: "drug", header: t(S.drug), cell: (d) => t(d.name), sortable: true, sortValue: (d) => t(d.name) },
    { key: "form", header: t(S.form), cell: (d) => d.form ?? "—", sortable: true, sortValue: (d) => d.form },
    { key: "strength", header: t(S.strength), cell: (d) => d.strength ?? "—", sortable: true, sortValue: (d) => d.strength },
    { key: "atc", header: t(S.atc), cell: (d) => <span className="tnum">{d.atcCode ?? "—"}</span> },
  ];

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <form onSubmit={run} className="stack" aria-label={t(S.title)}>
          <InputField label={t(S.field)} help={t(S.help)} value={query} onChange={(e) => setQuery(e.currentTarget.value)} autoComplete="off" />
          <div><Button type="submit" variant="primary"
              leadingIcon={<Icon name="search" />} loading={status === "loading"}>{t(S.search)}</Button></div>
        </form>
      </Card>

      <div aria-live="polite" className="stack" style={{ marginTop: "var(--sp4)", gap: "var(--sp4)" }}>
        {status === "idle" && <Card style={{ padding: "var(--sp5)" }}><p className="muted">{t(S.idle)}</p></Card>}
        {status === "error" && <Card style={{ padding: "var(--sp5)" }}><StatusChip kind="bad" label={t(S.error)} /></Card>}
        {status === "ready" && hits.length === 0 && <Card style={{ padding: "var(--sp5)" }}><StatusChip kind="neu" label={t(S.noHits)} /></Card>}
        {status === "ready" && hits.length > 0 && (
          <Card as="section" style={{ padding: "var(--sp3)" }}>
            <DataTable columns={hitCols} rows={hits} rowKey={(d) => d.drugId} caption={t(S.field)} />
          </Card>
        )}
        {selected && (
          <Card as="section" style={{ padding: "var(--sp3)" }}>
            <h2 className="section-h" style={{ margin: "0 0 var(--sp2)", paddingInline: "var(--sp2)" }}>
              {t(S.alternatives)} — {t(selected.name)}
            </h2>
            {alts === null ? (
              <div className="async-loading" role="status" style={{ padding: "var(--sp4)" }}><span className="mrs-spin" aria-hidden="true" /><span>{t(S.loading)}</span></div>
            ) : alts.length === 0 ? (
              <div style={{ padding: "var(--sp3)" }}><StatusChip kind="neu" label={t(S.noAlts)} /></div>
            ) : (
              <DataTable columns={altCols} rows={alts} rowKey={(d) => d.drugId} caption={t(S.alternatives)} />
            )}
          </Card>
        )}
      </div>
    </>
  );
}

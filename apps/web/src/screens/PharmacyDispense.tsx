import { useState } from "react";
import { Button, Card, DataTable, InputField, StatusChip, useToast } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { DispenseLine, Localized, Prescription, PrescriptionLine } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { newIdempotencyKey } from "../api/http";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Prescription queue", ar: "قائمة الوصفات" },
  empty: { en: "No prescriptions awaiting dispense.", ar: "لا توجد وصفات بانتظار الصرف." },
  patient: { en: "Patient", ar: "المريض" },
  prescriber: { en: "Prescriber", ar: "الواصف" },
  lines: { en: "Lines", ar: "البنود" },
  state: { en: "State", ar: "الحالة" },
  action: { en: "Action", ar: "إجراء" },
  open: { en: "Open", ar: "فتح" },
  pick: { en: "Select a prescription to dispense.", ar: "اختر وصفة للصرف." },
  drug: { en: "Drug", ar: "الدواء" },
  dose: { en: "Dose", ar: "الجرعة" },
  remaining: { en: "Remaining", ar: "المتبقي" },
  dispenseQty: { en: "Dispense now", ar: "صرف الآن" },
  substitute: { en: "Substitute (approved)", ar: "بديل (معتمد)" },
  outOfStock: { en: "Out of stock", ar: "غير متوفر" },
  dispenseBtn: { en: "Dispense selected", ar: "صرف المحدد" },
  done: { en: "Fully dispensed.", ar: "تم الصرف بالكامل." },
  partial: { en: "Partially dispensed — lines remain.", ar: "صرف جزئي — بقيت بنود." },
  replay: { en: "Already recorded (idempotent replay) — no double-apply.", ar: "مُسجّل مسبقاً (إعادة متكافئة) — دون ازدواج." },
  fail: { en: "Dispense failed.", ar: "فشل الصرف." },
  nothing: { en: "Enter a quantity on at least one in-stock line.", ar: "أدخل كمية لبند واحد متوفر على الأقل." },
} satisfies Record<string, Localized>;

export function PharmacyDispense() {
  const api = useApi();
  const t = useLoc();
  const q = useAsync<Prescription[]>(() => api.pharmacyQueue(), []);
  const [selected, setSelected] = useState<string | null>(null);

  const cols: Column<Prescription>[] = [
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.patient.token}</span> },
    { key: "prescriber", header: t(S.prescriber), cell: (r) => t(r.prescriber.label) },
    { key: "lines", header: t(S.lines), cell: (r) => <span className="tnum">{r.lines.length}</span> },
    { key: "state", header: t(S.state), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    {
      key: "open",
      header: t(S.action),
      cell: (r) => (
        <Button size="sm" variant={selected === r.id ? "primary" : "secondary"} onClick={() => setSelected(r.id)}>
          {t(S.open)}
        </Button>
      ),
    },
  ];

  const active = q.data?.find((p) => p.id === selected) ?? null;

  return (
    <>
      <PageHeader title={t(S.title)} />
      <div className="split">
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <AsyncSection state={q} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
            {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.title)} />}
          </AsyncSection>
        </Card>
        <div>
          {active ? (
            <DispensePanel key={active.id} rx={active} t={t} onDone={() => { setSelected(null); q.reload(); }} />
          ) : (
            <Card style={{ padding: "var(--sp6)" }}>
              <p className="muted">{t(S.pick)}</p>
            </Card>
          )}
        </div>
      </div>
    </>
  );
}

function DispensePanel({ rx, t, onDone }: { rx: Prescription; t: (l: Localized) => string; onDone: () => void }) {
  const api = useApi();
  const { toast } = useToast();
  const [qty, setQty] = useState<Record<string, number>>(() =>
    Object.fromEntries(rx.lines.map((l) => [l.id, l.outOfStock ? 0 : Math.max(0, l.quantity - l.dispensed)])),
  );
  const [busy, setBusy] = useState(false);

  const remaining = (l: PrescriptionLine) => Math.max(0, l.quantity - l.dispensed);

  async function dispense() {
    const lines: DispenseLine[] = rx.lines
      .filter((l) => !l.outOfStock && (qty[l.id] ?? 0) > 0)
      .map((l) => ({ lineId: l.id, quantity: Math.min(qty[l.id] ?? 0, remaining(l)) }));
    if (lines.length === 0) {
      toast(t(S.nothing), "bad");
      return;
    }
    setBusy(true);
    try {
      const res = await api.dispense({ prescriptionId: rx.id, idempotencyKey: newIdempotencyKey(), lines });
      if (res.replayed) toast(t(S.replay), "info");
      else toast(t(res.linesOutstanding === 0 ? S.done : S.partial), "ok");
      onDone();
    } catch {
      toast(t(S.fail), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <div className="result-head">
        <h2 style={{ margin: 0 }}>{t(S.title)} · <span className="tnum">{rx.id}</span></h2>
        <StatusChip kind={rx.status.kind} label={t(rx.status.label)} />
      </div>
      <ul className="rx-lines">
        {rx.lines.map((l) => (
          <li key={l.id} className="rx-line">
            <div>
              <div><strong>{t(l.drug.label)}</strong> <span className="muted">· {l.dose}</span></div>
              <div className="muted tnum">{t(S.remaining)}: {remaining(l)} / {l.quantity}</div>
            </div>
            {l.outOfStock ? (
              <StatusChip kind="warn" label={t(S.outOfStock)} />
            ) : (
              <InputField
                label={t(S.dispenseQty)}
                type="number"
                min={0}
                max={remaining(l)}
                value={qty[l.id] ?? 0}
                onChange={(e) => setQty((s) => ({ ...s, [l.id]: Number(e.currentTarget.value) }))}
              />
            )}
          </li>
        ))}
      </ul>
      <div>
        <Button variant="primary" loading={busy} onClick={() => void dispense()}>{t(S.dispenseBtn)}</Button>
      </div>
    </Card>
  );
}

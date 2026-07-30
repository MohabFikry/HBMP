import { useState } from "react";
import { Button, Card, DataTable, InputField, StatusChip, useTheme, useToast } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { DispenseLine, Localized, Prescription, PrescriptionLine } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useWrite, writeErrorText } from "../api/useWrite";
import { writeErrorMessage } from "../api/writeError";
import { ConfirmAction } from "./ConfirmAction";
import { PatientContextBar } from "./PatientProfile";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Prescription Queue", ar: "قائمة الوصفات" },
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
  confirmTitle: { en: "Confirm dispense", ar: "تأكيد الصرف" },
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
          {/* Phase 20 — the context bar. A pharmacy's projection is min-header + ALLERGIES (design 39 §4):
              the drug-allergy check is the reason this strip is on the dispensing screen at all. */}
          {active ? <PatientContextBar beneficiaryId={active.patient.id} /> : null}
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
  const { lang } = useTheme();
  /**
   * 18.D1 — quantities default to ZERO, not to the full remaining amount.
   *
   * Pre-filling the maximum makes "dispense everything" the path of least resistance: the pharmacist confirms
   * a form they did not fill in, and a partial dispense — the common case when stock is short — requires them
   * to notice and correct a number that already looked right. Zero forces the quantity to be an act. It also
   * means an accidental submit dispenses nothing rather than a full course of medication that then has to be
   * reversed against a controlled-drug register.
   */
  const [qty, setQty] = useState<Record<string, number>>(() =>
    Object.fromEntries(rx.lines.map((l) => [l.id, 0])),
  );
  const [busy, setBusy] = useState(false);
  // 18.D1 (E4) — dispensing medication is irreversible in the sense that matters: the drugs leave the
  // counter. A confirmation step goes in front of it, and it asks for the drug NAME rather than a yes/no,
  // because a yes/no in a repetitive queue becomes muscle memory inside a shift.
  const [confirming, setConfirming] = useState(false);
  const write = useWrite();

  const remaining = (l: PrescriptionLine) => Math.max(0, l.quantity - l.dispensed);

  const pending = (): DispenseLine[] =>
    rx.lines
      .filter((l) => !l.outOfStock && (qty[l.id] ?? 0) > 0)
      .map((l) => ({ lineId: l.id, quantity: Math.min(qty[l.id] ?? 0, remaining(l)) }));

  /** The drug the operator must name to confirm — the first line actually being dispensed. */
  const firstDrug = (): string => {
    const first = pending()[0];
    const line = rx.lines.find((l) => l.id === first?.lineId);
    return line ? (t(line.drug.label) || line.id) : "";
  };

  function askToDispense() {
    if (pending().length === 0) {
      toast(t(S.nothing), "bad");
      return;
    }
    setConfirming(true);
  }

  async function dispense() {
    const lines = pending();
    if (lines.length === 0) return;
    setBusy(true);
    try {
      // 18.D1: the key comes from useWrite — minted once per panel and rotated only after a CONFIRMED
      // success, so a retry after a timeout replays rather than dispensing a second time.
      const res = await api.dispense({ prescriptionId: rx.id, idempotencyKey: write.idempotencyKey, lines });
      if (res.replayed) toast(t(S.replay), "info");
      else toast(t(res.linesOutstanding === 0 ? S.done : S.partial), "ok");
      onDone();
    } catch (e) {
      toast(writeErrorText(writeErrorMessage(e), lang) ?? t(S.fail), "bad");
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
                /*
                 * 18.D3 (U6) — the drug name is IN the label, not just beside it.
                 *
                 * Every quantity input on this panel used the same label ("Quantity to dispense"), so a
                 * screen-reader user tabbing through a five-line prescription heard "Quantity to dispense,
                 * edit" five times with nothing distinguishing them. The drug name was visible above each
                 * field and invisible to the accessibility tree. Typing 30 into the wrong one dispenses the
                 * wrong medication at the wrong dose — this is a medication-error risk, not a nicety.
                 */
                label={`${t(S.dispenseQty)} — ${t(l.drug.label)}`}
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
        <Button variant="primary" loading={busy} onClick={askToDispense}>{t(S.dispenseBtn)}</Button>
      </div>
      <ConfirmAction
        open={confirming}
        onOpenChange={setConfirming}
        title={S.confirmTitle}
        body={{
          en: `Dispensing ${pending().length} line(s) for prescription ${rx.id}. Stock will be decremented and the prescription updated.`,
          ar: `صرف ${pending().length} بند/بنود للوصفة ${rx.id}. سيتم خصم المخزون وتحديث الوصفة.`,
        }}
        requireText={firstDrug()}
        confirmLabel={S.dispenseBtn}
        onConfirm={dispense}
      />
    </Card>
  );
}

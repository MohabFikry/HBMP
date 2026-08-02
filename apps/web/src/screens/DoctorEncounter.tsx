import { useState } from "react";
import {
  Button,
  Card,
  DataTable,
  InputField,
  Modal,
  StatusChip,
  Tabs,
  TextareaField,
  useToast,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Encounter, Localized, PatientListItem } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { PatientContextBar } from "./PatientProfile";
import { useSearchParams } from "react-router-dom";
import { AsyncSection, PageHeader, useBackTarget, useLoc } from "./_shared";

const S = {
  title: { en: "Encounter Workspace", ar: "مساحة اللقاء" },
  myPatients: { en: "My Patients", ar: "مرضاي" },
  emptyPatients: { en: "No patients under a treating relationship right now.", ar: "لا يوجد مرضى ضمن علاقة علاجية حالياً." },
  name: { en: "Patient", ar: "المريض" },
  mrn: { en: "MRN", ar: "الرقم الطبي" },
  lastVisit: { en: "Last visit", ar: "آخر زيارة" },
  state: { en: "State", ar: "الحالة" },
  treating: { en: "Treating", ar: "علاقة علاجية" },
  pickPatient: { en: "Select a patient to open their encounter.", ar: "اختر مريضاً لفتح اللقاء." },
  tabSummary: { en: "SOAP", ar: "التقييم" },
  tabVitals: { en: "Vitals", ar: "العلامات" },
  vBp: { en: "BP", ar: "ضغط الدم" },
  vHr: { en: "HR", ar: "النبض" },
  vTemp: { en: "Temp", ar: "الحرارة" },
  vHtWt: { en: "Ht/Wt", ar: "الطول/الوزن" },
  tabDx: { en: "Diagnoses & allergies", ar: "التشخيص والحساسية" },
  subjective: { en: "Subjective", ar: "الشكوى" },
  objective: { en: "Objective", ar: "الفحص" },
  assessment: { en: "Assessment", ar: "التقييم" },
  plan: { en: "Plan", ar: "الخطة" },
  diagnoses: { en: "Diagnoses", ar: "التشخيصات" },
  allergies: { en: "Allergies", ar: "الحساسية" },
  placeOrder: { en: "Place investigation order", ar: "طلب فحص" },
  prescribe: { en: "Prescribe", ar: "وصف دواء" },
  orderTest: { en: "Test (LOINC/CPT code)", ar: "الفحص (رمز LOINC/CPT)" },
  orderName: { en: "Test name", ar: "اسم الفحص" },
  urgent: { en: "Mark urgent", ar: "عاجل" },
  submit: { en: "Submit", ar: "إرسال" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  drugCode: { en: "Drug (ATC code)", ar: "الدواء (رمز ATC)" },
  drugName: { en: "Drug name", ar: "اسم الدواء" },
  dose: { en: "Dose", ar: "الجرعة" },
  qty: { en: "Quantity", ar: "الكمية" },
  orderOk: { en: "Investigation order placed.", ar: "تم إرسال طلب الفحص." },
  orderApproval: { en: "Order placed — routed to medical approval.", ar: "تم الطلب — أُحيل للموافقة الطبية." },
  rxOk: { en: "Prescription submitted.", ar: "تم إرسال الوصفة." },
} satisfies Record<string, Localized>;

export function DoctorEncounter() {
  const api = useApi();
  const t = useLoc();
  const back = useBackTarget();
  const patients = useAsync<PatientListItem[]>(() => api.listPatients(), []);

  // `?encounter=` — the encounter this screen was opened FOR, from a profile row or from "Start visit" on the
  // day board. Both have navigated here with it since the workspace existed and it was never read, so every
  // arrival landed on the picker with nothing selected: the doctor pressed "Start visit", got a list, and had
  // to find in it the visit they had just started.
  //
  // Initial state, not an effect, so the panel renders with the right encounter on the FIRST paint rather than
  // flashing the empty state — and so a later click still wins, which an effect keyed on the param would undo.
  const [params] = useSearchParams();
  const [selected, setSelected] = useState<string | null>(() => params.get("encounter"));

  const cols: Column<PatientListItem>[] = [
    { key: "name", header: t(S.name), cell: (r) => <strong>{t(r.name)}</strong> },
    { key: "mrn", header: t(S.mrn), cell: (r) => <span className="tnum">{r.mrn}</span> },
    { key: "lastVisit", header: t(S.lastVisit), cell: (r) => <span className="tnum">{r.lastVisit ?? "—"}</span> },
    {
      key: "treating",
      header: t(S.treating),
      cell: (r) => (r.treating ? <StatusChip kind="ok" label={t(S.treating)} /> : <StatusChip kind="neu" label="—" />),
    },
    { key: "state", header: t(S.state), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];

  return (
    <>
      {/* Reached FROM somewhere — a profile's encounter row, a visit board's "Start visit". Without this the
          workspace was a one-way door: the only way back to the file you opened it from was the nav rail,
          which lands on that screen fresh. `useBackTarget` renders nothing when there genuinely is no origin
          (a pasted deep link in a new tab), so it never offers a way out of the app. */}
      <PageHeader title={t(S.title)} back={back ?? undefined} />
      <div className="split">
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <h2 className="section-h">{t(S.myPatients)}</h2>
          <AsyncSection state={patients} isEmpty={(d) => d.length === 0} emptyLabel={S.emptyPatients}>
            {(rows) => (
              <DataTable
                columns={cols}
                rows={rows}
                rowKey={(r) => r.id}
                caption={t(S.myPatients)}
                interactive
                selectedKey={selected}
                onSelect={(r) => setSelected(r.id)}
              />
            )}
          </AsyncSection>
        </Card>

        <div>
          {selected ? (
            <EncounterPanel patientId={selected} t={t} />
          ) : (
            <Card style={{ padding: "var(--sp6)" }}>
              <p className="muted">{t(S.pickPatient)}</p>
            </Card>
          )}
        </div>
      </div>
    </>
  );
}

function EncounterPanel({ patientId, t }: { patientId: string; t: (l: Localized) => string }) {
  const api = useApi();
  const enc = useAsync<Encounter>(() => api.getEncounter(patientId), [patientId]);
  const [tab, setTab] = useState("summary");

  return (
    <AsyncSection state={enc} emptyLabel={S.pickPatient}>
      {(e) => (
        <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
          {/* Phase 20 — the patient context bar. A safety control, not a convenience: the failure it exists
              to prevent is prescribing or ordering against the wrong person's record. */}
          <PatientContextBar beneficiaryId={patientId} />
          <div className="result-head">
            <h2 style={{ margin: 0 }}>{t(e.patientName)}</h2>
            <div className="row-actions">
              <PlaceOrderModal encounterId={e.id} t={t} />
              <PrescribeModal encounterId={e.id} t={t} />
            </div>
          </div>
          <Tabs
            aria-label={t(S.title)}
            value={tab}
            onValueChange={setTab}
            items={[
              {
                value: "summary",
                label: t(S.tabSummary),
                content: (
                  <dl className="soap">
                    <div><dt>{t(S.subjective)}</dt><dd>{e.soap.subjective}</dd></div>
                    <div><dt>{t(S.objective)}</dt><dd>{e.soap.objective}</dd></div>
                    <div><dt>{t(S.assessment)}</dt><dd>{e.soap.assessment}</dd></div>
                    <div><dt>{t(S.plan)}</dt><dd>{e.soap.plan}</dd></div>
                  </dl>
                ),
              },
              {
                value: "vitals",
                label: t(S.tabVitals),
                content: (
                  <div className="kv-grid tnum">
                    <div><dt>{t(S.vBp)}</dt><dd>{e.vitals.systolic}/{e.vitals.diastolic}</dd></div>
                    <div><dt>{t(S.vHr)}</dt><dd>{e.vitals.heartRate}</dd></div>
                    <div><dt>{t(S.vTemp)}</dt><dd>{e.vitals.tempC}°C</dd></div>
                    <div><dt>{t(S.vHtWt)}</dt><dd>{e.vitals.heightCm} / {e.vitals.weightKg}</dd></div>
                  </div>
                ),
              },
              {
                value: "dx",
                label: t(S.tabDx),
                content: (
                  <div className="stack">
                    <div>
                      <h3 className="section-h">{t(S.diagnoses)}</h3>
                      <ul className="chip-list">
                        {e.diagnoses.map((d) => (
                          <li key={d.code}><StatusChip kind="info" label={`${d.code} · ${t(d.label)}`} /></li>
                        ))}
                      </ul>
                    </div>
                    <div>
                      <h3 className="section-h">{t(S.allergies)}</h3>
                      <ul className="chip-list">
                        {e.allergies.map((a) => (
                          <li key={a.id}><StatusChip kind="warn" label={`${t(a.substance)} · ${a.severity}`} /></li>
                        ))}
                      </ul>
                    </div>
                  </div>
                ),
              },
            ]}
          />
        </Card>
      )}
    </AsyncSection>
  );
}

function PlaceOrderModal({ encounterId, t }: { encounterId: string; t: (l: Localized) => string }) {
  const api = useApi();
  const { toast } = useToast();
  const [open, setOpen] = useState(false);
  const [code, setCode] = useState("58410-2");
  const [name, setName] = useState("Complete blood count");
  const [urgent, setUrgent] = useState(false);
  const [busy, setBusy] = useState(false);

  async function submit() {
    setBusy(true);
    try {
      const res = await api.placeOrder({
        encounterId,
        kind: "lab",
        test: { system: "LOINC", code, label: { en: name, ar: name } },
        priority: urgent ? "urgent" : "routine",
      });
      toast(t(res.requiresApproval ? S.orderApproval : S.orderOk), "ok");
      setOpen(false);
    } catch {
      toast(t({ en: "Order failed", ar: "فشل الطلب" }), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open={open}
      onOpenChange={setOpen}
      title={t(S.placeOrder)}
      trigger={<Button variant="secondary" leadingIcon="+">{t(S.placeOrder)}</Button>}
      footer={
        <>
          <Button variant="ghost" onClick={() => setOpen(false)}>{t(S.cancel)}</Button>
          <Button variant="primary" loading={busy} onClick={() => void submit()}>{t(S.submit)}</Button>
        </>
      }
    >
      <div className="stack">
        <InputField label={t(S.orderTest)} value={code} onChange={(e) => setCode(e.currentTarget.value)} />
        <InputField label={t(S.orderName)} value={name} onChange={(e) => setName(e.currentTarget.value)} />
        <label className="check">
          <input type="checkbox" checked={urgent} onChange={(e) => setUrgent(e.currentTarget.checked)} />
          <span>{t(S.urgent)}</span>
        </label>
      </div>
    </Modal>
  );
}

function PrescribeModal({ encounterId, t }: { encounterId: string; t: (l: Localized) => string }) {
  const api = useApi();
  const { toast } = useToast();
  const [open, setOpen] = useState(false);
  const [code, setCode] = useState("J01CA04");
  const [name, setName] = useState("Amoxicillin 500mg");
  const [dose, setDose] = useState("1 cap × 3/day");
  const [qty, setQty] = useState(21);
  const [advisory, setAdvisory] = useState<string[]>([]);
  const [busy, setBusy] = useState(false);

  async function submit() {
    setBusy(true);
    setAdvisory([]);
    try {
      const res = await api.prescribe({
        encounterId,
        drug: { system: "ATC", code, label: { en: name, ar: name } },
        dose,
        quantity: qty,
      });
      if (res.advisories.length > 0) {
        setAdvisory(res.advisories.map((a) => t(a)));
      } else {
        toast(t(S.rxOk), "ok");
        setOpen(false);
      }
    } catch {
      toast(t({ en: "Prescription failed", ar: "فشل الوصف" }), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open={open}
      onOpenChange={setOpen}
      title={t(S.prescribe)}
      trigger={<Button variant="secondary" leadingIcon="Rx">{t(S.prescribe)}</Button>}
      footer={
        <>
          <Button variant="ghost" onClick={() => setOpen(false)}>{t(S.cancel)}</Button>
          <Button variant="primary" loading={busy} onClick={() => void submit()}>{t(S.submit)}</Button>
        </>
      }
    >
      <div className="stack">
        <InputField label={t(S.drugCode)} value={code} onChange={(e) => setCode(e.currentTarget.value)} />
        <InputField label={t(S.drugName)} value={name} onChange={(e) => setName(e.currentTarget.value)} />
        <InputField label={t(S.dose)} value={dose} onChange={(e) => setDose(e.currentTarget.value)} />
        <InputField
          label={t(S.qty)}
          type="number"
          value={qty}
          onChange={(e) => setQty(Number(e.currentTarget.value))}
        />
        {advisory.length > 0 && (
          <TextareaField
            label={t({ en: "Advisory — acknowledge to continue", ar: "تنبيه — أقر للمتابعة" })}
            value={advisory.join("\n")}
            readOnly
            error={t({ en: "Clinical advisory raised — review before resubmitting.", ar: "تنبيه سريري — راجع قبل إعادة الإرسال." })}
          />
        )}
      </div>
    </Modal>
  );
}

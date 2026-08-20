import { useState } from "react";
import { Button, Card, InlineAlert, InputField, StatusChip } from "@mersal/design-system";
import type { Encounter, Localized, PatientListItem, PatientProfile, VitalInput, VitalType } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { SectionView } from "./ProfileSectionViews";

const S = {
  vitalsTitle: { en: "Vitals & Triage", ar: "العلامات والفرز" },
  resultsTitle: { en: "Results Inbox", ar: "صندوق النتائج" },
  pickPatient: { en: "Select a patient to record vitals.", ar: "اختر مريضاً لتسجيل العلامات." },
  empty: { en: "No patients on your worklist.", ar: "لا يوجد مرضى في قائمتك." },
  back: { en: "← Back to patients", ar: "→ العودة إلى المرضى" },
  record: { en: "Record vitals", ar: "تسجيل العلامات" },
  saved: { en: "Vitals recorded.", ar: "تم تسجيل العلامات." },
  nothing: { en: "Enter at least one reading.", ar: "أدخل قراءة واحدة على الأقل." },
  hr: { en: "Heart rate (bpm)", ar: "معدل النبض" },
  temp: { en: "Temperature (°C)", ar: "الحرارة" },
  spo2: { en: "SpO₂ (%)", ar: "الأكسجين" },
  weight: { en: "Weight (kg)", ar: "الوزن" },
  height: { en: "Height (cm)", ar: "الطول" },
  systolic: { en: "Systolic BP", ar: "الضغط الانقباضي" },
  diastolic: { en: "Diastolic BP", ar: "الضغط الانبساطي" },
  recorded: { en: "Recorded vitals", ar: "العلامات المسجلة" },
  none: { en: "No vitals recorded on this encounter yet.", ar: "لا توجد علامات مسجلة على هذه الزيارة بعد." },

  // ---- 32.6 — the results inbox, which used to be a vitals readout ----
  pickPatientResults: {
    en: "Select a patient to read their investigation results.",
    ar: "اختر مريضاً لعرض نتائج فحوصاته.",
  },
  noResults: {
    en: "No investigation results for this patient.",
    ar: "لا توجد نتائج فحوصات لهذا المريض.",
  },
  notYours: {
    en: "Investigation results are not part of what your role may read for this patient. This is not a "
      + "report that there are none.",
    ar: "نتائج الفحوصات ليست ضمن ما يحق لدورك الاطلاع عليه لهذا المريض. وهذا ليس تأكيداً بعدم وجودها.",
  },
} satisfies Record<string, Localized>;

/** A clickable list of the caller's patients (their own encounters). */
function PatientPicker({ hint, onPick }: { hint: Localized; onPick: (p: PatientListItem) => void }) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<PatientListItem[]>(() => api.listPatients(), []);
  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
        {(rows) => (
          <>
            <p className="muted" style={{ marginTop: 0, paddingInline: "var(--sp2)" }}>{t(hint)}</p>
            <ul className="stack" style={{ listStyle: "none", margin: 0, padding: 0, gap: "var(--sp2)" }}>
              {rows.map((p) => (
                <li key={p.id}>
                  <button type="button" className="picker-row" onClick={() => onPick(p)}>
                    <span>{t(p.name)}</span>
                    <span className="tnum muted">{p.mrn}</span>
                    <StatusChip kind={p.status.kind} label={t(p.status.label)} />
                  </button>
                </li>
              ))}
            </ul>
          </>
        )}
      </AsyncSection>
    </Card>
  );
}

/** Vitals & triage — pick a patient, record readings on their encounter (treating-gated server-side). */
export function NurseVitals() {
  const t = useLoc();
  const [patient, setPatient] = useState<PatientListItem | null>(null);
  return (
    <>
      <PageHeader title={t(S.vitalsTitle)} />
      {patient ? (
        <>
          {/* 32.6 — the readout moved HERE, where it belongs. Triage starts from what was last measured,
              and the nurse who is about to write a set is the one who needs to see the previous one. It used
              to be the whole of the "Results Inbox", which is a different promise entirely. */}
          <VitalsForm patient={patient} onBack={() => setPatient(null)} />
          <VitalsReadout patient={patient} />
        </>
      ) : (
        <PatientPicker hint={S.pickPatient} onPick={setPatient} />
      )}
    </>
  );
}

function VitalsForm({ patient, onBack }: { patient: PatientListItem; onBack: () => void }) {
  const api = useApi();
  const t = useLoc();
  const [fields, setFields] = useState<Record<VitalType, string>>({
    HR: "", Temp: "", SpO2: "", Weight: "", Height: "", BP: "", BPDiastolic: "", BMI: "",
  });
  const [status, setStatus] = useState<"idle" | "saving" | "saved" | "empty">("idle");

  const set = (k: VitalType) => (e: React.ChangeEvent<HTMLInputElement>) =>
    setFields((f) => ({ ...f, [k]: e.currentTarget.value }));

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    const readings: VitalInput[] = (Object.keys(fields) as VitalType[])
      .filter((k) => fields[k].trim() !== "" && !Number.isNaN(Number(fields[k])))
      .map((k) => ({ type: k, value: Number(fields[k]) }));
    if (readings.length === 0) { setStatus("empty"); return; }
    setStatus("saving");
    await api.recordVitals(patient.id, readings);
    setStatus("saved");
    setFields({ HR: "", Temp: "", SpO2: "", Weight: "", Height: "", BP: "", BPDiastolic: "", BMI: "" });
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <div className="result-head">
        <h2 style={{ margin: 0 }}>{t(patient.name)}</h2>
        <Button variant="ghost" size="sm" onClick={onBack}>{t(S.back)}</Button>
      </div>
      <form onSubmit={submit} className="stack" aria-label={t(S.record)}>
        <dl className="kv-grid">
          <InputField label={t(S.hr)} inputMode="decimal" value={fields.HR} onChange={set("HR")} />
          <InputField label={t(S.temp)} inputMode="decimal" value={fields.Temp} onChange={set("Temp")} />
          <InputField label={t(S.spo2)} inputMode="decimal" value={fields.SpO2} onChange={set("SpO2")} />
          <InputField label={t(S.systolic)} inputMode="decimal" value={fields.BP} onChange={set("BP")} />
          {/* Triage records the PAIR. The form asked for a systolic alone because that was all emr could
              store (migration 0017); a nurse writing "118" and nothing else is not a blood pressure. */}
          <InputField label={t(S.diastolic)} inputMode="decimal" value={fields.BPDiastolic} onChange={set("BPDiastolic")} />
          <InputField label={t(S.weight)} inputMode="decimal" value={fields.Weight} onChange={set("Weight")} />
          <InputField label={t(S.height)} inputMode="decimal" value={fields.Height} onChange={set("Height")} />
        </dl>
        <div aria-live="polite" className="stack" style={{ gap: "var(--sp2)" }}>
          {status === "empty" && <InlineAlert tone="bad">{t(S.nothing)}</InlineAlert>}
          {status === "saved" && <StatusChip kind="ok" label={t(S.saved)} />}
          <div>
            <Button type="submit" variant="primary" loading={status === "saving"}>{t(S.record)}</Button>
          </div>
        </div>
      </form>
    </Card>
  );
}

/**
 * 32.6 — the nurse's results inbox, which for four phases was a VITALS readout.
 *
 * <p>The rail said "Results Inbox", the permission was `results.inbox`, and the screen showed the heart rate
 * and temperature the same nurse had typed in on the other tab. Design 11 §3.2 grants nurses
 * <code>lab_result R🟠(TR)</code> and <code>imaging_result R🟠(TR)</code> — the read existed on paper and
 * had no door.</p>
 *
 * <p>It asks profile-service for the INVESTIGATIONS section rather than a nurse-specific endpoint, because
 * that projection is already composed under the caller's own token and already applies design 37 §6: a
 * restricted result comes back marked restricted rather than omitted, so the nurse sees a locked door instead
 * of a gap they would read as "not back yet". A section this caller may not read is ABSENT from the response,
 * which is a third answer again — and it is said out loud rather than shown as an empty table.</p>
 */
export function NurseResults() {
  const t = useLoc();
  const [patient, setPatient] = useState<PatientListItem | null>(null);
  return (
    <>
      <PageHeader title={t(S.resultsTitle)} />
      {patient ? (
        <PatientResults patient={patient} onBack={() => setPatient(null)} />
      ) : (
        <PatientPicker hint={S.pickPatientResults} onPick={setPatient} />
      )}
    </>
  );
}

function PatientResults({ patient, onBack }: { patient: PatientListItem; onBack: () => void }) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<PatientProfile>(
    () => api.patientProfile(patient.id, ["investigations"]), [patient.id]);

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <div className="result-head">
        <h2 style={{ margin: 0 }}>{t(patient.name)}</h2>
        <Button variant="ghost" size="sm" onClick={onBack}>{t(S.back)}</Button>
      </div>
      <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.noResults}>
        {(profile) => {
          const section = profile.sections.find((x) => x.key === "investigations");
          // WITHHELD, not empty. The composer omits a section the caller may not read, and rendering that as
          // "no results" would tell a nurse a patient has no investigations when the truth is that she may
          // not see them.
          if (!section) return <InlineAlert tone="info">{t(S.notYours)}</InlineAlert>;
          return <SectionView section={section} beneficiaryId={patient.id} />;
        }}
      </AsyncSection>
    </Card>
  );
}

/**
 * What was last measured on this encounter.
 *
 * <p>No heading and no back link: it sits under the form, which already carries both. A second copy of each
 * would give the page two &lt;h2&gt;s naming the same patient and two identical "back" buttons, which reads
 * to a screen-reader user as two patients.</p>
 */
function VitalsReadout({ patient }: { patient: PatientListItem }) {
  const api = useApi();
  const t = useLoc();
  const state = useAsync<Encounter>(() => api.getEncounter(patient.id), [patient.id]);
  const rows: Array<[Localized, number | null, string]> = [];
  const push = (label: Localized, v: number | null | undefined, unit: string) => rows.push([label, v ?? null, unit]);
  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <h3 className="section-h" style={{ margin: 0 }}>{t(S.recorded)}</h3>
      <AsyncSection state={state} isEmpty={() => false} emptyLabel={S.none}>
        {(enc) => {
          const v = enc.vitals;
          rows.length = 0;
          push(S.hr, v.heartRate, "bpm");
          push(S.temp, v.tempC, "°C");
          // 32.6 — the form records the PAIR and SpO₂, and the readout showed neither. A panel that displays
          // half of what was measured is how a systolic reading gets read as a blood pressure.
          push(S.spo2, v.spo2, "%");
          push(S.systolic, v.systolic, "mmHg");
          push(S.diastolic, v.diastolic, "mmHg");
          push(S.weight, v.weightKg, "kg");
          push(S.height, v.heightCm, "cm");
          const any = rows.some(([, val]) => val != null);
          if (!any) return <InlineAlert tone="info">{t(S.none)}</InlineAlert>;
          return (
            <dl className="kv-grid" aria-label={t(S.recorded)}>
              {rows.filter(([, val]) => val != null).map(([label, val, unit]) => (
                <div key={label.en}><dt>{t(label)}</dt><dd className="tnum">{val} {unit}</dd></div>
              ))}
            </dl>
          );
        }}
      </AsyncSection>
    </Card>
  );
}

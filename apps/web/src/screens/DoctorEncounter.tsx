import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Button,
  Card,
  DataTable,
  Icon,
  InlineAlert,
  InputField,
  Modal,
  StatusChip,
  Tabs,
  TextareaField,
  useToast,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type {
  Encounter,
  EncounterDiagnosis,
  IcdRef,
  Localized,
  OrderRow,
  PatientListItem,
  RxRow,
  Soap,
  VitalInput,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { PatientContextBar } from "./PatientProfile";
import { SectionView } from "./ProfileSectionViews";
import { useSearchParams } from "react-router-dom";
import { ApiError } from "../api/http";
import { AsyncSection, PageHeader, useBackTarget, useLoc, useOpenProfile } from "./_shared";
import { useFormat } from "../i18n/useFormat";

/**
 * Phase 4 — the encounter workspace (US-030 / US-031).
 *
 * <b>This screen is where a consultation is WRITTEN, not where it is read.</b> That is the whole difference
 * from what stood here before, which rendered the encounter's SOAP note as a definition list: four headings
 * and four paragraphs of text a doctor could look at and not touch. "Start visit" on the day board opened it,
 * so the one action that means "I am seeing this patient now" landed on a read-only page, and the note itself
 * had to be written somewhere else — which in practice meant it was not written at all.
 *
 * The shape is the one a clinician already knows: the patient's identity pinned across the top as a safety
 * strip, the four SOAP sections as the body, and the observations that inform them held beside the writing
 * rather than behind a tab. The two save verbs are deliberately separate and deliberately unequal — a draft
 * can be revised, a signed note cannot — and they say which is which.
 */

const S = {
  title: { en: "Encounter", ar: "الزيارة" },
  myPatients: { en: "My patients", ar: "مرضاي" },
  emptyPatients: { en: "No patients under a treating relationship right now.", ar: "لا يوجد مرضى ضمن علاقة علاجية حالياً." },
  name: { en: "Patient", ar: "المريض" },
  mrn: { en: "Encounter", ar: "الزيارة" },
  lastVisit: { en: "Started", ar: "بدأت" },
  state: { en: "State", ar: "الحالة" },
  treating: { en: "Treating", ar: "علاقة علاجية" },
  pickPatient: { en: "Open a patient to document their encounter.", ar: "افتح مريضاً لتوثيق زيارته." },
  openEncounter: { en: "Open encounter", ar: "فتح الزيارة" },

  tabNote: { en: "SOAP note", ar: "ملاحظة SOAP" },
  tabVitals: { en: "Vitals", ar: "العلامات الحيوية" },
  tabOrders: { en: "Orders", ar: "الطلبات" },
  tabHistory: { en: "History", ar: "السجل" },

  subjective: { en: "Subjective", ar: "الشكوى" },
  objective: { en: "Objective", ar: "الفحص" },
  assessment: { en: "Assessment", ar: "التقييم" },
  plan: { en: "Plan", ar: "الخطة" },
  hintSubjective: { en: "Complaints & history", ar: "الشكوى والتاريخ المرضي" },
  hintObjective: { en: "Examination & findings", ar: "الفحص والنتائج" },
  hintAssessment: { en: "Diagnosis & differential", ar: "التشخيص والتشخيص التفريقي" },
  hintPlan: { en: "Treatment & follow-up", ar: "العلاج والمتابعة" },
  phSubjective: { en: "Chief complaint, history of present illness, review of systems…", ar: "الشكوى الرئيسية، تاريخ المرض الحالي، مراجعة الأجهزة…" },
  phObjective: { en: "Physical examination, general appearance, findings…", ar: "الفحص السريري، المظهر العام، النتائج…" },
  phAssessment: { en: "Clinical assessment and differential diagnosis…", ar: "التقييم السريري والتشخيص التفريقي…" },
  phPlan: { en: "Treatment plan, patient instructions, follow-up…", ar: "خطة العلاج، تعليمات المريض، المتابعة…" },

  addCode: { en: "Add ICD-10", ar: "إضافة ICD-10" },
  codePicker: { en: "Add a diagnosis", ar: "إضافة تشخيص" },
  codeSearch: { en: "Search ICD-10 by code or condition", ar: "ابحث في ICD-10 بالرمز أو الحالة" },
  codeSearchHint: { en: "Type at least two characters.", ar: "اكتب حرفين على الأقل." },
  codeNone: { en: "No ICD-10 code matches that search.", ar: "لا يوجد رمز ICD-10 مطابق." },
  codeAdded: { en: "Diagnosis recorded.", ar: "تم تسجيل التشخيص." },
  codeRemoved: { en: "Diagnosis retracted.", ar: "تم سحب التشخيص." },
  remove: { en: "Retract", ar: "سحب" },
  removeOne: { en: "Retract {code}", ar: "سحب {code}" },
  noDiagnoses: { en: "No diagnosis coded yet.", ar: "لم يُسجَّل تشخيص بعد." },

  vitalsTitle: { en: "Vitals", ar: "العلامات الحيوية" },
  vitalsNone: { en: "No vitals recorded for this encounter.", ar: "لم تُسجَّل علامات حيوية لهذه الزيارة." },
  vitalsRef: { en: "Reference {range}", ar: "المرجع {range}" },
  vitalHigh: { en: "High", ar: "مرتفع" },
  vitalLow: { en: "Low", ar: "منخفض" },
  vitalNormal: { en: "In range", ar: "ضمن المعدل" },
  vBp: { en: "Blood pressure", ar: "ضغط الدم" },
  vHr: { en: "Heart rate", ar: "النبض" },
  vTemp: { en: "Temperature", ar: "الحرارة" },
  vSpo2: { en: "Oxygen saturation", ar: "تشبع الأكسجين" },
  vHeight: { en: "Height", ar: "الطول" },
  vWeight: { en: "Weight", ar: "الوزن" },
  recordVitals: { en: "Record vitals", ar: "تسجيل العلامات" },
  systolic: { en: "Systolic (mmHg)", ar: "الانقباضي (مم زئبق)" },
  diastolic: { en: "Diastolic (mmHg)", ar: "الانبساطي (مم زئبق)" },
  vitalsSaved: { en: "Vitals recorded.", ar: "تم تسجيل العلامات الحيوية." },
  vitalsEmpty: { en: "Enter at least one reading.", ar: "أدخل قراءة واحدة على الأقل." },

  saveDraft: { en: "Save draft", ar: "حفظ مسودة" },
  finalize: { en: "Save & finalize", ar: "حفظ وإنهاء" },
  draftSaved: { en: "Draft saved.", ar: "تم حفظ المسودة." },
  finalized: { en: "Encounter finalized. The note is signed and locked.", ar: "تم إنهاء الزيارة. الملاحظة موقّعة ومقفلة." },
  unsaved: { en: "Unsaved changes", ar: "تغييرات غير محفوظة" },
  saved: { en: "All changes saved", ar: "كل التغييرات محفوظة" },
  emptyNote: { en: "Write something in at least one section before saving.", ar: "اكتب في قسم واحد على الأقل قبل الحفظ." },
  signedTitle: { en: "Signed", ar: "موقّعة" },
  signedBody: {
    en: "This note is signed and can no longer be edited. Record a correction as an addendum.",
    ar: "هذه الملاحظة موقّعة ولا يمكن تعديلها. سجّل التصحيح كملحق.",
  },
  signedDiagnosis: {
    en: "The note is signed — a coded diagnosis can no longer be retracted here.",
    ar: "الملاحظة موقّعة — لا يمكن سحب التشخيص من هنا.",
  },
  confirmFinalize: { en: "Finalize this encounter?", ar: "إنهاء هذه الزيارة؟" },
  confirmFinalizeBody: {
    en: "Signing locks the note. After this, corrections can only be added as an addendum — nothing can be changed in place.",
    ar: "التوقيع يقفل الملاحظة. بعدها لا يمكن التصحيح إلا بإضافة ملحق — ولا يمكن تغيير أي شيء في مكانه.",
  },
  cancel: { en: "Cancel", ar: "إلغاء" },
  signAndLock: { en: "Sign & lock", ar: "توقيع وقفل" },
  patientFile: { en: "Patient file", ar: "ملف المريض" },
  notAuthor: { en: "Only the note's author may sign or amend it.", ar: "لا يمكن التوقيع أو التعديل إلا لكاتب الملاحظة." },
  saveFailed: { en: "The note could not be saved.", ar: "تعذّر حفظ الملاحظة." },

  placeOrder: { en: "Place investigation order", ar: "طلب فحص" },
  prescribe: { en: "Prescribe", ar: "وصف دواء" },
  orderTest: { en: "Test (LOINC/CPT code)", ar: "الفحص (رمز LOINC/CPT)" },
  orderName: { en: "Test name", ar: "اسم الفحص" },
  urgent: { en: "Mark urgent", ar: "عاجل" },
  submit: { en: "Submit", ar: "إرسال" },
  drugCode: { en: "Drug (ATC code)", ar: "الدواء (رمز ATC)" },
  drugName: { en: "Drug name", ar: "اسم الدواء" },
  dose: { en: "Dose", ar: "الجرعة" },
  qty: { en: "Quantity", ar: "الكمية" },
  orderOk: { en: "Investigation order placed.", ar: "تم إرسال طلب الفحص." },
  orderApproval: { en: "Order placed — routed to medical approval.", ar: "تم الطلب — أُحيل للموافقة الطبية." },
  rxOk: { en: "Prescription submitted.", ar: "تم إرسال الوصفة." },
  ordersFor: { en: "Investigations for this patient", ar: "فحوصات هذا المريض" },
  rxFor: { en: "Prescriptions for this patient", ar: "وصفات هذا المريض" },
  noOrders: { en: "You have raised no investigation orders for this patient.", ar: "لم تطلب أي فحوصات لهذا المريض." },
  noRx: { en: "You have written no prescriptions for this patient.", ar: "لم تكتب أي وصفات لهذا المريض." },
  colRef: { en: "Reference", ar: "المرجع" },
  colTest: { en: "Test", ar: "الفحص" },
  colLines: { en: "Lines", ar: "البنود" },
  colWhen: { en: "Raised", ar: "التاريخ" },
  colStatus: { en: "Status", ar: "الحالة" },
  historyEmpty: { en: "No earlier encounters on this patient's file.", ar: "لا توجد زيارات سابقة في ملف هذا المريض." },
} satisfies Record<string, Localized>;

/**
 * Adult reference bands for the vitals panel — the "is this reading normal?" question, which is NOT the
 * question emr's `VitalRange` answers. That one is a plausibility bound (is 400 bpm a typo?) and rejects a
 * save; these are clinical reference ranges and only ever ANNOTATE a reading.
 *
 * They are advisory and adult-general: they take no account of age, pregnancy, altitude or the patient's own
 * baseline, which is exactly why the flag never blocks anything and never appears without the number and the
 * band it was judged against beside it. A doctor overrules this panel by reading it.
 */
const REFERENCE: Record<string, { low: number; high: number }> = {
  systolic: { low: 90, high: 120 },
  diastolic: { low: 60, high: 80 },
  heartRate: { low: 60, high: 100 },
  tempC: { low: 36.3, high: 37.2 },
  spo2: { low: 95, high: 100 },
};

export function DoctorEncounter() {
  const t = useLoc();
  const back = useBackTarget();
  const [params, setParams] = useSearchParams();

  // `?encounter=` — the encounter this screen was opened FOR, from a profile row or from "Start visit" on
  // the day board. It lives in the URL rather than in state so the workspace is a place you can be: Back
  // returns to the picker, forward returns to the encounter, and a link to a consultation is a link.
  const encounterId = params.get("encounter");
  // `?beneficiaryId=` — the patient file's "Start encounter" action. It names a PERSON, not a visit, so it
  // narrows the picker to that person's open encounters rather than guessing which one was meant.
  const beneficiaryId = params.get("beneficiaryId");

  const open = useCallback(
    (id: string) => setParams({ encounter: id }, { replace: false }),
    [setParams],
  );

  return (
    <>
      {/* Reached FROM somewhere — a profile's encounter row, a visit board's "Start visit". `useBackTarget`
          renders nothing when there genuinely is no origin (a pasted deep link in a new tab), so it never
          offers a way out of the app. */}
      <PageHeader title={t(S.title)} back={back ?? undefined} />
      {encounterId ? (
        <EncounterWorkspace encounterId={encounterId} />
      ) : (
        <EncounterPicker beneficiaryId={beneficiaryId} onOpen={open} />
      )}
    </>
  );
}

// ---------------------------------------------------------------- the picker (no encounter chosen yet)

function EncounterPicker({
  beneficiaryId,
  onOpen,
}: {
  beneficiaryId: string | null;
  onOpen: (encounterId: string) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const patients = useAsync<PatientListItem[]>(() => api.listPatients(), []);

  const cols: Column<PatientListItem>[] = [
    { key: "name", header: t(S.name), cell: (r) => <strong>{t(r.name)}</strong> },
    { key: "mrn", header: t(S.mrn), cell: (r) => <span className="tnum">{r.mrn}</span> },
    { key: "lastVisit", header: t(S.lastVisit), cell: (r) => <span className="tnum">{r.lastVisit ?? "—"}</span> },
    { key: "state", header: t(S.state), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp5)" }}>
      <h2 className="section-h">{t(S.myPatients)}</h2>
      <AsyncSection
        state={patients}
        isEmpty={(d) => filterRows(d, beneficiaryId).length === 0}
        emptyLabel={S.emptyPatients}
      >
        {(rows) => (
          <DataTable
            columns={cols}
            rows={filterRows(rows, beneficiaryId)}
            rowKey={(r) => r.id}
            caption={t(S.myPatients)}
            interactive
            onSelect={(r) => onOpen(r.id)}
          />
        )}
      </AsyncSection>
    </Card>
  );
}

function filterRows(rows: PatientListItem[], beneficiaryId: string | null): PatientListItem[] {
  return beneficiaryId ? rows.filter((r) => r.beneficiaryId === beneficiaryId) : rows;
}

// ---------------------------------------------------------------- the workspace

function EncounterWorkspace({ encounterId }: { encounterId: string }) {
  const api = useApi();
  const enc = useAsync<Encounter>(
    useCallback(() => api.getEncounter(encounterId), [api, encounterId]),
    [encounterId],
  );

  return (
    <AsyncSection state={enc} emptyLabel={S.pickPatient}>
      {/* Keyed on the encounter: the editor holds an unsaved draft, and a draft written for one consultation
          must never survive into another. Remounting is the only way to be certain of that. */}
      {(e) => <Workspace key={e.id} encounter={e} onSaved={enc.reload} />}
    </AsyncSection>
  );
}

function Workspace({ encounter, onSaved }: { encounter: Encounter; onSaved: () => void }) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const openProfile = useOpenProfile();
  const [tab, setTab] = useState("note");
  // Which tabs have been opened. `Tabs` keeps every panel MOUNTED by design, so without this every
  // consultation opened fires the orders list, the prescriptions list and the patient's encounter history
  // whether or not the doctor ever looks at them — three reads of clinical data, and three audited PHI
  // accesses, for a note that only needed the note.
  const [visited, setVisited] = useState<ReadonlySet<string>>(() => new Set(["note"]));
  const openTab = (value: string) => {
    setTab(value);
    setVisited((prev) => (prev.has(value) ? prev : new Set([...prev, value])));
  };

  const signed = encounter.signed;
  const [soap, setSoap] = useState<Soap>(encounter.soap);
  const [diagnoses, setDiagnoses] = useState<EncounterDiagnosis[]>(encounter.diagnoses);
  const [noteId, setNoteId] = useState<string | null>(encounter.noteId);
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState<"draft" | "final" | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [confirming, setConfirming] = useState(false);

  const hasContent = Object.values(soap).some((v) => v.trim().length > 0);

  const set = (key: keyof Soap) => (value: string) => {
    setSoap((prev) => ({ ...prev, [key]: value }));
    setDirty(true);
    setError(null);
  };

  /** Persist the note. Returns its id, or null when the write was refused — the caller must not go on to
   *  sign a note that was never saved. */
  async function persist(): Promise<string | null> {
    if (!hasContent) {
      setError(S.emptyNote);
      return null;
    }
    try {
      const res = await api.saveEncounterNote(encounter.id, noteId, soap);
      setNoteId(res.noteId);
      setDirty(false);
      return res.noteId;
    } catch (e) {
      // 403 here has one meaning and it is worth saying: emr lets only a note's AUTHOR amend it, so a
      // covering doctor who opens a colleague's encounter can read it and cannot overwrite it. "Save
      // failed" would send them retrying a thing that will never succeed.
      setError(e instanceof ApiError && e.status === 403 ? S.notAuthor : S.saveFailed);
      return null;
    }
  }

  async function saveDraft() {
    setBusy("draft");
    const id = await persist();
    setBusy(null);
    if (id) toast(t(S.draftSaved), "ok");
  }

  async function finalize() {
    setBusy("final");
    const id = await persist();
    if (!id) {
      setBusy(null);
      setConfirming(false);
      return;
    }
    try {
      await api.signEncounterNote(encounter.id, id);
      setConfirming(false);
      toast(t(S.finalized), "ok");
      // Re-read rather than flipping a local flag: signing changes what the SERVER will now allow, and the
      // screen should be showing the server's answer, not its own guess at it.
      onSaved();
    } catch (e) {
      setError(e instanceof ApiError && e.status === 403 ? S.notAuthor : S.saveFailed);
      setConfirming(false);
    } finally {
      setBusy(null);
    }
  }

  const soapSections = [
    { key: "subjective" as const, letter: "S", title: S.subjective, hint: S.hintSubjective, placeholder: S.phSubjective },
    { key: "objective" as const, letter: "O", title: S.objective, hint: S.hintObjective, placeholder: S.phObjective },
    { key: "assessment" as const, letter: "A", title: S.assessment, hint: S.hintAssessment, placeholder: S.phAssessment },
    { key: "plan" as const, letter: "P", title: S.plan, hint: S.hintPlan, placeholder: S.phPlan },
  ];

  return (
    <>
      {/* A safety control first, and the only thing on this screen that is above the tabs: whichever tab is
          open, the record being written to is named. `namedAllergens` because this is the screen where a
          prescription gets written — here the substance is the decision, not a count to go and look up. */}
      <PatientContextBar
        beneficiaryId={encounter.patientId}
        namedAllergens
        actions={
          <Button
            variant="ghost"
            size="sm"
            leadingIcon={<Icon name="user" width={16} height={16} aria-hidden="true" />}
            onClick={() => openProfile(encounter.patientId)}
          >
            {t(S.patientFile)}
          </Button>
        }
      />

      <div className="enc-layout">
        <div className="enc-main">
          <Tabs
            aria-label={t(S.title)}
            value={tab}
            onValueChange={openTab}
            items={[
              {
                value: "note",
                label: t(S.tabNote),
                content: (
                  <div className="enc-soap">
                    {signed && (
                      <InlineAlert tone="info">
                        <strong>{t(S.signedTitle)}</strong> — {t(S.signedBody)}
                      </InlineAlert>
                    )}
                    {soapSections.map((s) => (
                      <SoapSection
                        key={s.key}
                        letter={s.letter}
                        title={t(s.title)}
                        hint={t(s.hint)}
                        placeholder={t(s.placeholder)}
                        value={soap[s.key]}
                        onChange={set(s.key)}
                        readOnly={signed}
                        action={
                          s.key === "assessment" ? (
                            <DiagnosisPicker
                              encounterId={encounter.id}
                              disabled={signed}
                              primary={diagnoses.length === 0}
                              onAdded={(d) => setDiagnoses((prev) => [...prev, d])}
                            />
                          ) : undefined
                        }
                      >
                        {s.key === "assessment" && (
                          <DiagnosisChips
                            encounterId={encounter.id}
                            diagnoses={diagnoses}
                            signed={signed}
                            onRemoved={(id) => setDiagnoses((prev) => prev.filter((d) => d.id !== id))}
                          />
                        )}
                      </SoapSection>
                    ))}
                  </div>
                ),
              },
              {
                value: "vitals",
                label: t(S.tabVitals),
                content: visited.has("vitals")
                  ? <VitalsTab encounter={encounter} onRecorded={onSaved} />
                  : null,
              },
              {
                value: "orders",
                label: t(S.tabOrders),
                content: visited.has("orders") ? <OrdersTab encounter={encounter} /> : null,
              },
              {
                value: "history",
                label: t(S.tabHistory),
                content: visited.has("history")
                  ? <HistoryTab beneficiaryId={encounter.patientId} />
                  : null,
              },
            ]}
          />
        </div>

        <aside className="enc-rail">
          <VitalsPanel vitals={encounter.vitals} />
          <Card as="section" className="enc-actions">
            {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
            {signed ? (
              <p className="muted" style={{ margin: 0 }}>{t(S.signedBody)}</p>
            ) : (
              <>
                <Button
                  variant="primary"
                  loading={busy === "final"}
                  disabled={!hasContent}
                  leadingIcon={<Icon name="lock" width={16} height={16} aria-hidden="true" />}
                  onClick={() => setConfirming(true)}
                >
                  {t(S.finalize)}
                </Button>
                <Button
                  variant="secondary"
                  loading={busy === "draft"}
                  disabled={!hasContent}
                  leadingIcon={<Icon name="doc" width={16} height={16} aria-hidden="true" />}
                  onClick={() => void saveDraft()}
                >
                  {t(S.saveDraft)}
                </Button>
                {/* Which state the note is in, in words. A doctor interrupted mid-consultation comes back to
                    a screen that must answer "did that save?" without them pressing anything to find out. */}
                <p className={dirty ? "enc-dirty" : "muted"} style={{ margin: 0, fontSize: "0.875rem" }}>
                  {dirty ? t(S.unsaved) : t(S.saved)}
                </p>
              </>
            )}
          </Card>
        </aside>
      </div>

      {/* Signing is irreversible, so it asks — once, and saying what it costs. Every other write on this
          screen can be revised, which is precisely why this one must not look like them. */}
      <Modal
        open={confirming}
        onOpenChange={setConfirming}
        title={t(S.confirmFinalize)}
        footer={
          <>
            <Button variant="ghost" onClick={() => setConfirming(false)}>{t(S.cancel)}</Button>
            <Button variant="primary" loading={busy === "final"} onClick={() => void finalize()}>
              {t(S.signAndLock)}
            </Button>
          </>
        }
      >
        <p style={{ margin: 0 }}>{t(S.confirmFinalizeBody)}</p>
      </Modal>
    </>
  );
}

// ---------------------------------------------------------------- one SOAP section

function SoapSection({
  letter,
  title,
  hint,
  placeholder,
  value,
  onChange,
  readOnly,
  action,
  children,
}: {
  letter: string;
  title: string;
  hint: string;
  placeholder: string;
  value: string;
  onChange: (v: string) => void;
  readOnly: boolean;
  action?: React.ReactNode;
  children?: React.ReactNode;
}) {
  const headingId = `soap-${letter.toLowerCase()}`;
  const hintId = `${headingId}-hint`;
  return (
    <Card as="section" className="soap-card" aria-labelledby={headingId}>
      <div className="soap-card-head">
        {/* Decorative: the letter is a visual anchor for a doctor who reads these four cards a hundred times
            a week. The heading beside it is what is announced. */}
        <span className="soap-badge" aria-hidden="true">{letter}</span>
        <h3 className="soap-title" id={headingId}>{title}</h3>
        <span className="soap-hint" id={hintId}>{hint}</span>
        {action}
      </div>
      {children}
      {/* A bare textarea named BY THE HEADING rather than a TextareaField, which would render a second
          visible label saying the same word directly under the first. One name, one place. */}
      <textarea
        className="mrs-control soap-input"
        aria-labelledby={headingId}
        aria-describedby={hintId}
        placeholder={readOnly ? undefined : placeholder}
        value={value}
        readOnly={readOnly}
        rows={4}
        onChange={(e) => onChange(e.currentTarget.value)}
      />
    </Card>
  );
}

// ---------------------------------------------------------------- diagnoses

function DiagnosisChips({
  encounterId,
  diagnoses,
  signed,
  onRemoved,
}: {
  encounterId: string;
  diagnoses: EncounterDiagnosis[];
  signed: boolean;
  onRemoved: (id: string) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [removing, setRemoving] = useState<string | null>(null);

  if (diagnoses.length === 0) {
    return <p className="muted" style={{ margin: "0 0 var(--sp3)" }}>{t(S.noDiagnoses)}</p>;
  }

  async function remove(id: string) {
    setRemoving(id);
    try {
      await api.removeEncounterDiagnosis(encounterId, id);
      onRemoved(id);
      toast(t(S.codeRemoved), "ok");
    } catch (e) {
      // 409 is the sign-lock, and it is the ONE refusal here that is not a fault: the note was signed, so
      // the assessment is a signed clinical statement and the correction path is an addendum.
      toast(t(e instanceof ApiError && e.status === 409 ? S.signedDiagnosis : S.saveFailed), "bad");
    } finally {
      setRemoving(null);
    }
  }

  return (
    <ul className="chip-list dx-list">
      {diagnoses.map((d) => (
        <li key={d.id ?? d.code} className="dx-chip">
          <span className="dx-code tnum">{d.code}</span>
          <span className="dx-label">{t(d.label)}</span>
          {!signed && d.id && (
            <button
              type="button"
              className="dx-remove"
              aria-label={t(S.removeOne).replace("{code}", d.code)}
              disabled={removing === d.id}
              onClick={() => void remove(d.id!)}
            >
              <Icon name="cross" width={14} height={14} aria-hidden="true" />
            </button>
          )}
        </li>
      ))}
    </ul>
  );
}

function DiagnosisPicker({
  encounterId,
  disabled,
  primary,
  onAdded,
}: {
  encounterId: string;
  disabled: boolean;
  primary: boolean;
  onAdded: (d: EncounterDiagnosis) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<IcdRef[]>([]);
  const [searching, setSearching] = useState(false);
  const [adding, setAdding] = useState<string | null>(null);

  // Debounced: the field is a typeahead over master data, and a request per keystroke would ask the
  // catalogue about "a", "ac", "acu" and "acut" on the way to a search nobody wanted the first four of.
  useEffect(() => {
    if (!open) return;
    let live = true;
    const q = query.trim();
    if (q.length < 2) {
      setResults([]);
      return;
    }
    setSearching(true);
    const timer = setTimeout(() => {
      api.searchIcd(q).then(
        (rows) => { if (live) { setResults(rows); setSearching(false); } },
        () => { if (live) { setResults([]); setSearching(false); } },
      );
    }, 250);
    return () => { live = false; clearTimeout(timer); };
  }, [api, open, query]);

  async function add(code: string) {
    setAdding(code);
    try {
      onAdded(await api.addEncounterDiagnosis(encounterId, code, primary));
      toast(t(S.codeAdded), "ok");
      setOpen(false);
      setQuery("");
    } catch {
      toast(t(S.saveFailed), "bad");
    } finally {
      setAdding(null);
    }
  }

  if (disabled) return null;

  return (
    <Modal
      open={open}
      onOpenChange={setOpen}
      title={t(S.codePicker)}
      trigger={
        <Button
          variant="ghost"
          size="sm"
          leadingIcon={<Icon name="plus" width={16} height={16} aria-hidden="true" />}
        >
          {t(S.addCode)}
        </Button>
      }
    >
      <div className="stack-3">
        <InputField
          label={t(S.codeSearch)}
          help={t(S.codeSearchHint)}
          value={query}
          autoFocus
          onChange={(e) => setQuery(e.currentTarget.value)}
        />
        {/* aria-live so a screen-reader user learns the list changed under a field they are still typing in. */}
        <ul className="icd-results" aria-live="polite" aria-busy={searching}>
          {results.map((r) => (
            <li key={r.code}>
              <button
                type="button"
                className="icd-hit"
                disabled={adding === r.code}
                onClick={() => void add(r.code)}
              >
                <span className="dx-code tnum">{r.code}</span>
                <span>{r.title}</span>
              </button>
            </li>
          ))}
          {!searching && query.trim().length >= 2 && results.length === 0 && (
            <li className="muted">{t(S.codeNone)}</li>
          )}
        </ul>
      </div>
    </Modal>
  );
}

// ---------------------------------------------------------------- vitals

/** Where a reading sits against its reference band, or null when there is no band or no reading. */
function flagFor(key: string, value: number | null): { tone: "ok" | "warn"; label: Localized; icon: "ok" | "triangle" } | null {
  const band = REFERENCE[key];
  if (band === undefined || value === null) return null;
  if (value < band.low) return { tone: "warn", label: S.vitalLow, icon: "triangle" };
  if (value > band.high) return { tone: "warn", label: S.vitalHigh, icon: "triangle" };
  return { tone: "ok", label: S.vitalNormal, icon: "ok" };
}

function VitalsPanel({ vitals }: { vitals: Encounter["vitals"] }) {
  const t = useLoc();
  const { dateTime } = useFormat();

  const rows = [
    { key: "systolic", label: S.vBp, unit: "mmHg",
      value: vitals.systolic, display: bpDisplay(vitals), range: "90–120 / 60–80" },
    { key: "heartRate", label: S.vHr, unit: "bpm", value: vitals.heartRate, display: num(vitals.heartRate), range: "60–100" },
    { key: "tempC", label: S.vTemp, unit: "°C", value: vitals.tempC, display: num(vitals.tempC), range: "36.3–37.2" },
    { key: "spo2", label: S.vSpo2, unit: "%", value: vitals.spo2, display: num(vitals.spo2), range: "95–100" },
  ];
  const any = rows.some((r) => r.display !== null);

  return (
    <Card as="section" className="vitals-panel">
      <div className="vitals-head">
        <h2 className="section-h" style={{ margin: 0 }}>{t(S.vitalsTitle)}</h2>
        {vitals.measuredAt && <span className="muted vitals-when">{dateTime(vitals.measuredAt)}</span>}
      </div>
      {!any ? (
        <p className="muted" style={{ margin: 0 }}>{t(S.vitalsNone)}</p>
      ) : (
        <dl className="vitals-list">
          {rows.map((r) => {
            const flag = flagFor(r.key, r.value);
            return (
              <div key={r.key} className="vital-row">
                <dt>
                  {t(r.label)}
                  <span className="vital-ref">{t(S.vitalsRef).replace("{range}", `${r.range} ${r.unit}`)}</span>
                </dt>
                <dd>
                  <span className="vital-value tnum">{r.display ?? "—"}</span>
                  {/* Four cues, not a coloured dot: hue AND icon AND shape AND the word. A doctor who cannot
                      distinguish the greens from the ambers still reads "High". */}
                  {flag && (
                    <span className={`vital-flag vital-flag--${flag.tone}`}>
                      <Icon name={flag.icon} width={13} height={13} aria-hidden="true" />
                      {t(flag.label)}
                    </span>
                  )}
                </dd>
              </div>
            );
          })}
        </dl>
      )}
    </Card>
  );
}

function num(v: number | null): string | null {
  return v === null ? null : String(v);
}

/** A blood pressure reads as a pair. A lone systolic is shown as "118 / —" rather than as "118", because the
 *  missing half is information — it says the diastolic was never recorded, not that it was normal. */
function bpDisplay(vitals: Encounter["vitals"]): string | null {
  if (vitals.systolic === null && vitals.diastolic === null) return null;
  return `${vitals.systolic ?? "—"} / ${vitals.diastolic ?? "—"}`;
}

function VitalsTab({ encounter, onRecorded }: { encounter: Encounter; onRecorded: () => void }) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [form, setForm] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  const fields: { key: string; type: VitalInput["type"]; label: Localized }[] = [
    { key: "sys", type: "BP", label: S.systolic },
    { key: "dia", type: "BPDiastolic", label: S.diastolic },
    { key: "hr", type: "HR", label: S.vHr },
    { key: "temp", type: "Temp", label: S.vTemp },
    { key: "spo2", type: "SpO2", label: S.vSpo2 },
    { key: "height", type: "Height", label: S.vHeight },
    { key: "weight", type: "Weight", label: S.vWeight },
  ];

  async function submit() {
    const readings: VitalInput[] = fields
      .map((f) => ({ type: f.type, value: Number(form[f.key]) }))
      .filter((r) => form[fields.find((f) => f.type === r.type)!.key]?.trim() && Number.isFinite(r.value));
    if (readings.length === 0) {
      setError(S.vitalsEmpty);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await api.recordVitals(encounter.id, readings);
      setForm({});
      toast(t(S.vitalsSaved), "ok");
      onRecorded();
    } catch {
      setError(S.saveFailed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <h3 className="section-h" style={{ margin: 0 }}>{t(S.recordVitals)}</h3>
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
      <div className="vitals-form">
        {fields.map((f) => (
          <InputField
            key={f.key}
            label={t(f.label)}
            type="number"
            inputMode="decimal"
            value={form[f.key] ?? ""}
            // The value is read HERE, not inside the updater: `currentTarget` is null by the time React runs
            // a functional setState, so reaching for it there throws on the first keystroke.
            onChange={(e) => {
              const next = e.currentTarget.value;
              setForm((prev) => ({ ...prev, [f.key]: next }));
            }}
          />
        ))}
      </div>
      <div className="row-actions">
        <Button
          variant="primary"
          loading={busy}
          leadingIcon={<Icon name="chart" width={16} height={16} aria-hidden="true" />}
          onClick={() => void submit()}
        >
          {t(S.recordVitals)}
        </Button>
      </div>
    </Card>
  );
}

// ---------------------------------------------------------------- orders

function OrdersTab({ encounter }: { encounter: Encounter }) {
  const api = useApi();
  const t = useLoc();
  const { date } = useFormat();
  const orders = useAsync<OrderRow[]>(useCallback(() => api.ordersMine(), [api]), []);
  const rx = useAsync<RxRow[]>(useCallback(() => api.prescriptionsMine(), [api]), []);

  // Filtered to THIS patient in the browser, from the clinician's own lists. Both endpoints already answer
  // "mine" — narrowing further is a display concern, and asking a service for a second, patient-scoped
  // variant of a list it already returns would be a new seam for no new information.
  const mineFor = useMemo(
    () => (orders.data ?? []).filter((o) => o.beneficiary.id === encounter.patientId),
    [orders.data, encounter.patientId],
  );
  const rxFor = useMemo(
    () => (rx.data ?? []).filter((p) => p.beneficiary.id === encounter.patientId),
    [rx.data, encounter.patientId],
  );

  const orderCols: Column<OrderRow>[] = [
    { key: "orderNo", header: t(S.colRef), cell: (r) => <span className="tnum">{r.orderNo}</span> },
    { key: "primaryCode", header: t(S.colTest), cell: (r) => `${r.orderType} · ${r.primaryCode}` },
    { key: "requestedAt", header: t(S.colWhen), cell: (r) => <span className="tnum">{date(r.requestedAt)}</span> },
    { key: "status", header: t(S.colStatus), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];
  const rxCols: Column<RxRow>[] = [
    { key: "id", header: t(S.colRef), cell: (r) => <span className="tnum">{r.id}</span> },
    { key: "lineCount", header: t(S.colLines), cell: (r) => <span className="tnum">{r.lineCount}</span> },
    { key: "submittedAt", header: t(S.colWhen), cell: (r) => <span className="tnum">{r.submittedAt ? date(r.submittedAt) : "—"}</span> },
    { key: "status", header: t(S.colStatus), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
  ];

  return (
    <div className="stack">
      <div className="row-actions">
        <PlaceOrderModal encounterId={encounter.id} t={t} />
        <PrescribeModal encounterId={encounter.id} t={t} />
      </div>
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <h3 className="section-h">{t(S.ordersFor)}</h3>
        {mineFor.length === 0 ? (
          <p className="muted" style={{ margin: 0 }}>{t(S.noOrders)}</p>
        ) : (
          <DataTable columns={orderCols} rows={mineFor} rowKey={(r) => r.id} caption={t(S.ordersFor)} />
        )}
      </Card>
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <h3 className="section-h">{t(S.rxFor)}</h3>
        {rxFor.length === 0 ? (
          <p className="muted" style={{ margin: 0 }}>{t(S.noRx)}</p>
        ) : (
          <DataTable columns={rxCols} rows={rxFor} rowKey={(r) => r.id} caption={t(S.rxFor)} />
        )}
      </Card>
    </div>
  );
}

// ---------------------------------------------------------------- history

function HistoryTab({ beneficiaryId }: { beneficiaryId: string }) {
  const api = useApi();
  const t = useLoc();
  // The profile's own encounters section, gated by the design-39 §4 matrix exactly as it is in the patient
  // file — not a second, parallel history assembled here. One list of a patient's encounters, one authority
  // over who may read it.
  const state = useAsync(
    useCallback(() => api.patientProfile(beneficiaryId, ["encounters"]), [api, beneficiaryId]),
    [beneficiaryId],
  );

  return (
    <AsyncSection state={state} emptyLabel={S.historyEmpty}>
      {(profile) => {
        const section = profile.sections.find((s) => s.key === "encounters");
        if (!section) return <p className="muted">{t(S.historyEmpty)}</p>;
        return <SectionView section={section} beneficiaryId={beneficiaryId} />;
      }}
    </AsyncSection>
  );
}

// ---------------------------------------------------------------- write actions

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
      trigger={
        <Button
          variant="secondary"
          leadingIcon={<Icon name="flask" width={16} height={16} aria-hidden="true" />}
        >
          {t(S.placeOrder)}
        </Button>
      }
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
      trigger={
        <Button
          variant="secondary"
          leadingIcon={<Icon name="pill" width={16} height={16} aria-hidden="true" />}
        >
          {t(S.prescribe)}
        </Button>
      }
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

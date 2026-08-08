import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Button,
  Card,
  DataTableView,
  Icon,
  InlineAlert,
  InputField,
  Modal,
  SelectField,
  StatusChip,
  Tabs,
  useTableQuery,
  useToast,
} from "@mersal/design-system";
import type { Column, IconName, TableFilterSpec } from "@mersal/design-system";
import type {
  DiagnosisRank,
  Encounter,
  EncounterDiagnosis,
  IcdRef,
  Localized,
  OrderRow,
  PatientListItem,
  InvestigationOrderType,
  RxRow,
  Soap,
  VitalInput,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { PatientContextBar } from "./PatientProfile";
import { MemberClinicalPanel } from "./encounter/MemberClinicalPanel";
import { OrderDetailModal } from "./encounter/OrderDetailModal";
import { PrescriptionDetailModal } from "./encounter/PrescriptionDetailModal";
import { SectionView } from "./ProfileSectionViews";
import { useLocation, useSearchParams } from "react-router-dom";
import { ApiError } from "../api/http";
import { AsyncSection, PageHeader, useBackTarget, useLoc, useOpenProfile, useWhenFilter } from "./_shared";
import { draftKeys, useUnsentDrafts } from "./draftStore";
import { ServiceHistoryModal } from "./ServiceHistoryModal";   // 29.4 — one modal, every tab
import { TransactionActionsDialog } from "./TransactionActionsDialog";   // 30.6 — amend/withdraw from the row
import type { TransactionAction } from "./TransactionActionsDialog";
import type { AmendReasonOption } from "./AmendLineDialog";
import { PrescribingWorkspace } from "./prescribing/PrescribingWorkspace";
import { InvestigationWorkspace } from "./investigations/InvestigationWorkspace";
import { EncounterTimelineButton } from "./VisitTimeline";
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
  pickPatient: { en: "Open a patient to document their encounter.", ar: "افتح مريضاً لتوثيق زيارته." },
  search: { en: "Search", ar: "بحث" },
  pickerSearchHint: { en: "Name, encounter or state", ar: "الاسم أو الزيارة أو الحالة" },
  pickerNoMatches: {
    en: "No patients match. Change the search or clear the filters.",
    ar: "لا يوجد مرضى مطابقون. عدّل البحث أو أزل عوامل التصفية.",
  },
  // The three encounter states, worded exactly as the chips in the State column.
  encInProgress: { en: "In progress", ar: "جارٍ" },
  encCompleted: { en: "Completed", ar: "مكتمل" },
  encCancelled: { en: "Cancelled", ar: "ملغى" },

  tabNote: { en: "SOAP note", ar: "ملاحظة SOAP" },
  tabPrescriptions: { en: "Prescriptions", ar: "الوصفات" },
  tabLabs: { en: "Labs", ar: "المختبر" },
  tabRadiology: { en: "Radiology", ar: "الأشعة" },
  labsFor: { en: "Lab orders for this patient", ar: "طلبات المختبر لهذا المريض" },
  radiologyFor: { en: "Radiology orders for this patient", ar: "طلبات الأشعة لهذا المريض" },
  noLabs: { en: "You have raised no lab orders for this patient.", ar: "لم تطلب أي فحوصات مختبر لهذا المريض." },
  noRadiology: { en: "You have raised no radiology orders for this patient.", ar: "لم تطلب أي فحوصات أشعة لهذا المريض." },
  orderLab: { en: "Order a lab test", ar: "طلب فحص مختبر" },
  orderRadiology: { en: "Order radiology", ar: "طلب أشعة" },
  // 29.2 (design 45 §2) — OP Procedures: surgery, physiotherapy, dialysis, injections. NOT E/M, which the
  // system turns into a REFERRAL instead — the doctor picks a service and the system decides the vehicle.
  tabProcedures: { en: "OP Procedures", ar: "الإجراءات الخارجية" },
  proceduresFor: { en: "Procedures ordered for this patient", ar: "الإجراءات المطلوبة لهذا المريض" },
  noProcedures: { en: "You have raised no procedures for this patient.", ar: "لم تطلب أي إجراءات لهذا المريض." },
  orderProcedure: { en: "Order a procedure", ar: "طلب إجراء" },
  colOpen: { en: "Open", ar: "فتح" },
  openOrder: { en: "Open the order", ar: "فتح الطلب" },
  rxDrugMissing: { en: "Medication not recorded", ar: "الدواء غير مسجّل" },
  colHistory: { en: "History", ar: "السجل" },
  viewHistory: { en: "Previous occurrences of this service", ar: "الحالات السابقة لهذه الخدمة" },
  // 30.6 — the two acts, on the ROW. Named for the transaction, because a column of unlabelled icons is a
  // screen-reader user hearing "button, button" once per row with nothing to tell them apart.
  colActions: { en: "Actions", ar: "الإجراءات" },
  amend: { en: "Amend", ar: "تعديل" },
  withdraw: { en: "Withdraw", ar: "سحب" },
  lockedTerminal: { en: "Already closed — cannot be changed", ar: "مغلق بالفعل — لا يمكن تغييره" },
  lockedDispensed: { en: "Dispensed", ar: "تم صرفه" },
  lockedDelivered: { en: "Delivered", ar: "تم تنفيذه" },
  tabHistory: { en: "History", ar: "السجل" },
  histEncounters: { en: "Encounters", ar: "الزيارات" },
  histInvestigations: { en: "Investigations", ar: "الفحوصات" },
  histPrescriptions: { en: "Prescriptions", ar: "الوصفات" },
  histProcedures: { en: "OP Procedures", ar: "الإجراءات الخارجية" },

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

  diagnosisTitle: { en: "Diagnosis", ar: "التشخيص" },
  diagnosisHint: { en: "Coded conditions for this visit", ar: "الحالات المرمّزة لهذه الزيارة" },
  rank: { en: "Rank", ar: "الترتيب" },
  rankPrimary: { en: "Primary", ar: "أساسي" },
  rankSecondary: { en: "Secondary", ar: "ثانوي" },
  noPrimary: { en: "None recorded.", ar: "لم يُسجَّل." },
  needPrimary: {
    en: "Record a primary diagnosis — the condition this visit was chiefly about. It is required to finalize.",
    ar: "سجّل تشخيصاً أساسياً — الحالة التي دارت حولها الزيارة. مطلوب لإنهاء الزيارة.",
  },
  replacesPrimary: {
    en: "This visit already has a primary diagnosis. Recording a second is allowed but rarely intended.",
    ar: "لهذه الزيارة تشخيص أساسي بالفعل. تسجيل تشخيص ثانٍ مسموح لكنه نادراً ما يكون مقصوداً.",
  },
  addCode: { en: "Add diagnosis", ar: "إضافة تشخيص" },
  addOne: { en: "Add", ar: "إضافة" },
  addN: { en: "Add {n} diagnoses", ar: "إضافة {n} تشخيصات" },
  toAdd: { en: "To add ({n})", ar: "للإضافة ({n})" },
  staged: { en: "Added", ar: "مضاف" },
  someFailed: {
    en: "These codes were not recorded: {codes}. They are still listed — press Add to try again.",
    ar: "لم تُسجَّل هذه الرموز: {codes}. ما زالت مدرجة — اضغط «إضافة» لإعادة المحاولة.",
  },
  codePicker: { en: "Add a diagnosis", ar: "إضافة تشخيص" },
  codeSearch: { en: "Search ICD-10 by code or condition", ar: "ابحث في ICD-10 بالرمز أو الحالة" },
  codeSearchHint: { en: "Type at least two characters.", ar: "اكتب حرفين على الأقل." },
  codeNone: { en: "No ICD-10 code matches that search.", ar: "لا يوجد رمز ICD-10 مطابق." },
  codeAdded: { en: "Diagnosis recorded.", ar: "تم تسجيل التشخيص." },
  codeRemoved: { en: "Diagnosis retracted.", ar: "تم سحب التشخيص." },
  removeOne: { en: "Retract {code}", ar: "سحب {code}" },

  vitalsTitle: { en: "Vitals", ar: "العلامات الحيوية" },
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
  finalized: {
    en: "Visit closed. The note is signed and locked, and the appointment is complete.",
    ar: "أُغلقت الزيارة. الملاحظة موقّعة ومقفلة، والموعد مكتمل.",
  },
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
  unsentWork: {
    en: "Composed but not sent: {items}. Send each one or discard it before closing the visit.",
    ar: "مُعدّ ولم يُرسل: {items}. أرسل كلاً منها أو احذفه قبل إغلاق الزيارة.",
  },
  confirmFinalize: { en: "Finalize this encounter?", ar: "إنهاء هذه الزيارة؟" },
  confirmFinalizeBody: {
    en: "This signs the note and closes the visit. The appointment moves to Completed and leaves your day list. After this, corrections can only be added as an addendum — nothing can be changed in place.",
    ar: "سيؤدي هذا إلى توقيع الملاحظة وإغلاق الزيارة. ينتقل الموعد إلى «مكتمل» ويغادر قائمة يومك. بعدها لا يمكن التصحيح إلا بإضافة ملحق — ولا يمكن تغيير أي شيء في مكانه.",
  },
  cancel: { en: "Cancel", ar: "إلغاء" },
  signAndLock: { en: "Sign & close visit", ar: "توقيع وإغلاق الزيارة" },
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
  colView: { en: "View", ar: "عرض" },
  colTimeline: { en: "Timeline", ar: "المسار الزمني" },
  // The Rx number is appended to this at the call site. Every row's button would otherwise carry the same
  // accessible name, so a screen-reader user tabbing the column hears "View prescription" once per row with
  // nothing to tell them which one they are on.
  viewRx: { en: "View prescription", ar: "عرض الوصفة" },
  historyEmpty: { en: "No earlier encounters on this patient's file.", ar: "لا توجد زيارات سابقة في ملف هذا المريض." },
  visitTimeline: { en: "Visit timeline", ar: "مسار الزيارة" },
  when: { en: "When", ar: "الفترة" },
  noMatchesWhen: {
    en: "Nothing in that period. Choose a wider one, or clear the filter.",
    ar: "لا يوجد شيء في هذه الفترة. اختر فترة أوسع أو أزل عامل التصفية.",
  },
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

/**
 * Where the workspace's Back control goes when there is no origin to return to — after a RELOAD, or on a link
 * opened in a fresh tab, both of which destroy `location.state` and reset the history index together.
 *
 * The doctor's own patient list, because that is what this screen is always opened FROM in one hop or two,
 * and because the workspace is not in the nav rail: without this the only way off it was the rail's other
 * entries, which is how a refresh turned into "I have to navigate somewhere else and come back".
 *
 * Module-level so its identity is stable — see `useBackTarget`.
 */
const BACK_FALLBACK = { path: "/clinician/patients", label: S.myPatients };

export function DoctorEncounter() {
  const t = useLoc();
  const location = useLocation();
  const [params, setParams] = useSearchParams();

  // `?encounter=` — the encounter this screen was opened FOR, from a profile row or from "Start visit" on
  // the day board. It lives in the URL rather than in state so the workspace is a place you can be: Back
  // returns to the picker, forward returns to the encounter, and a link to a consultation is a link.
  const encounterId = params.get("encounter");
  // `?beneficiaryId=` — the patient file's "Start encounter" action. It names a PERSON, not a visit, so it
  // narrows the picker to that person's open encounters rather than guessing which one was meant.
  const beneficiaryId = params.get("beneficiaryId");

  /*
    The fallback applies to an OPEN encounter only.

    `useBackTarget` shows nothing when there is neither an origin nor history, and the fallback exists so a
    RELOADED workspace is not a dead end — the workspace is not in the nav rail, so without it a refresh
    stranded the clinician there.

    The PICKER is a different screen with the same route. It is a list, it is reachable from the rail, and on
    it the fallback rendered a "My patients" control in the header that duplicated the rail entry two inches
    to its left. A real origin still shows a real Back control on both — arriving from a patient file gives
    one here exactly as before; what is gone is the invented one on a screen that never needed it.
  */
  const back = useBackTarget(undefined, encounterId ? BACK_FALLBACK : undefined);

  /**
   * Pick an encounter in the picker — same route, new `?encounter=`.
   *
   * The origin is CARRIED THROUGH. `setSearchParams` pushes a fresh history entry, and a fresh entry has no
   * `location.state` unless one is given: arriving here from the patient file and then choosing a visit
   * dropped the `from` that got you here, so Back fell through to `navigate(-1)` and landed on the picker you
   * had just left rather than on the file you came from. Re-stating it keeps one journey one journey.
   */
  const open = useCallback(
    (id: string) => setParams({ encounter: id }, { replace: false, state: location.state }),
    [setParams, location.state],
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
    // The ENCOUNTER reference leads. This board lists VISITS, not people — the same patient holds several
    // rows — so the reference is the column that tells two of their rows apart, and a list is read from the
    // thing that identifies its rows.
    { key: "mrn", header: t(S.mrn), cell: (r) => <span className="tnum">{r.mrn}</span>,
      sortable: true, sortValue: (r) => r.mrn },
    { key: "name", header: t(S.name), cell: (r) => <strong>{t(r.name)}</strong>,
      sortable: true, sortValue: (r) => t(r.name) },
    // Sorts on the ISO date rather than the rendered one, as everywhere else in the portal.
    { key: "lastVisit", header: t(S.lastVisit), cell: (r) => <span className="tnum">{r.lastVisit ?? "—"}</span>,
      sortable: true, sortValue: (r) => r.lastVisit ?? "" },
    { key: "state", header: t(S.state), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />,
      sortable: true, sortValue: (r) => r.status.label.en },
    /*
      The visit's own history, without having to open the visit.

      No `reference` filter: a row here IS one encounter, so the whole episode belongs in it — filtering on
      the encounter's key would strip every step raised by an order or a prescription, which is most of what
      happened. `context` names WHICH visit instead, because the dialog is headed "Visit timeline" and this
      board holds one row per visit rather than one per patient.

      The rows are clickable (a click opens the encounter), so the button stops its own click propagating —
      `EncounterTimelineButton` owns that.
    */
    {
      key: "timeline",
      header: t(S.colTimeline),
      cell: (r) => <EncounterTimelineButton encounterId={r.id} context={r.mrn} />,
    },
  ];

  // The rows this picker actually offers. Deep-linked with a `beneficiaryId`, that is one person's open
  // encounters and the toolbar below would be a search box over three rows — so search and filter only appear
  // when the picker is the full worklist it is on a bare visit to the workspace.
  const rows = useMemo(() => filterRows(patients.data ?? [], beneficiaryId), [patients.data, beneficiaryId]);
  const narrowedToOne = beneficiaryId !== null;
  const when = useWhenFilter<PatientListItem>(t, encounterStartedAt);

  const filters: TableFilterSpec<PatientListItem>[] = useMemo(() => (narrowedToOne ? [] : [
    {
      key: "state",
      label: t(S.state),
      // Matched on the ENGLISH label — the row carries its status only as a pre-resolved chip, and matching
      // the localized half would break the filter the moment the portal is switched to Arabic.
      options: [
        { value: S.encInProgress.en, label: t(S.encInProgress) },
        { value: S.encCompleted.en,  label: t(S.encCompleted) },
        { value: S.encCancelled.en,  label: t(S.encCancelled) },
      ],
      match: (r, value) => r.status.label.en === value,
    },
    // Dated by when the visit STARTED. Deep-linked to one patient the picker holds a handful of rows and
    // neither filter earns its place, which is why both hang off the same condition.
    when,
  ]), [t, narrowedToOne, when]);

  const query = useTableQuery<PatientListItem>({
    rows,
    columns: cols,
    searchText: narrowedToOne
      ? undefined
      : (r) => [r.name.en, r.name.ar, r.mrn, r.status.label.en, r.status.label.ar].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.pickerSearchHint),
    filters,
    pageSize: 10,
    // Most recently seen first: a doctor arriving at the workspace is opening the consultation they just
    // started far more often than one from last month.
    initialSortKey: "lastVisit",
    initialSortDir: "descending",
    // Not persisted. Unlike the worklists this is a doorway, not a place — it is replaced by the workspace the
    // moment a row is picked, so there is no "come back to where I was" to preserve.
  });

  return (
    <Card as="section" style={{ padding: "var(--sp5)" }}>
      <h2 className="section-h">{t(S.myPatients)}</h2>
      <AsyncSection
        state={patients}
        isEmpty={(d) => filterRows(d, beneficiaryId).length === 0}
        emptyLabel={S.emptyPatients}
      >
        {() => (
          <DataTableView
            query={query}
            columns={cols}
            rowKey={(r) => r.id}
            caption={t(S.myPatients)}
            emptyLabel={t(S.emptyPatients)}
            noMatchesLabel={t(S.pickerNoMatches)}
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
  /** Bumped when an allergy or a blood group is recorded, so the context bar re-reads with the panel. */
  const [clinicalNonce, setClinicalNonce] = useState(0);
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
  // An encounter is finalized WITH a primary diagnosis. Everything downstream of a signed visit — the
  // authorization, the claim, the utilisation report — keys on that one code, and a signed note with none
  // is a record that has to be chased back to the doctor after they have moved on to the next patient.
  //
  // Enforced here rather than in emr: emr's sign endpoint is shared with nursing and progress notes, which
  // carry no diagnosis at all, so the rule belongs to SOAP encounter documentation and not to signing.
  const hasPrimary = diagnoses.some((d) => d.rank === "Primary");

  /**
   * Work composed in a sibling tab and never sent.
   *
   * ==========================================================================================================
   * WHY CLOSING THE VISIT WAITS ON IT
   * ==========================================================================================================
   * Writing a prescription is not required to finish a consultation — plenty of visits end without one. But a
   * prescription that was COMPOSED, checked, and had its warnings answered in writing, and then never sent, is
   * not a decision not to prescribe: it is a decision that was made and lost. The doctor believes the patient
   * is collecting medicine; the pharmacy has never heard of it; and the encounter is signed and locked, so the
   * record of the visit says nothing was prescribed at all. Nobody discovers this until the patient does.
   *
   * The rule is therefore not "you must prescribe" but "you must not leave one half-done": send it, or
   * discard it. Both are one click, and Discard exists in each workspace precisely so this can be insisted on.
   *
   * Read through the draft store rather than from state here, because the composers live in OTHER TABS and
   * hold their own — nothing in this component changes when one of them is filled in.
   */
  const draftTabs = useMemo(
    () => [
      { key: draftKeys.prescription(encounter.id), label: S.tabPrescriptions },
      { key: draftKeys.order("Lab", encounter.id), label: S.tabLabs },
      { key: draftKeys.order("Radiology", encounter.id), label: S.tabRadiology },
      { key: draftKeys.order("Procedure", encounter.id), label: S.tabProcedures },
    ],
    [encounter.id],
  );
  const unsentKeys = useUnsentDrafts(draftTabs.map((d) => d.key));
  const unsent = draftTabs.filter((d) => unsentKeys.includes(d.key));
  // Named, not counted. "1 unsent item" sends a doctor hunting through three tabs for it.
  const unsentMessage: Localized = {
    en: S.unsentWork.en.replace("{items}", unsent.map((u) => t(u.label)).join(", ")),
    ar: S.unsentWork.ar.replace("{items}", unsent.map((u) => u.label.ar).join("، ")),
  };

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
    if (!hasPrimary) {
      setError(S.needPrimary);
      setConfirming(false);
      return;
    }
    // Checked here as well as on the button, and not as a belt-and-braces habit: the button reads a snapshot
    // of the draft store, and this reads it at the moment of signing. If they ever disagree, the one that
    // must win is the one closest to the irreversible act.
    if (unsent.length > 0) {
      setError(unsentMessage);
      setConfirming(false);
      return;
    }
    setBusy("final");
    const id = await persist();
    if (!id) {
      setBusy(null);
      setConfirming(false);
      return;
    }
    try {
      await api.signEncounterNote(encounter.id, id);
      // Signing the note and ENDING the visit are two acts, and the second one is what takes the patient
      // off the day list. Doing only the first left a finished consultation sitting in CheckedIn with
      // "Start visit" still offered against it — see the endpoint's own note.
      await api.completeEncounter(encounter.id);
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
        showBloodGroup
        // The bar and the panel below read the same two facts by different routes — the bar through
        // profile-service, the panel straight from emr. Recording an allergy has to move both, or the strip
        // keeps showing the pre-write picture of exactly the thing that was just corrected.
        reloadKey={clinicalNonce}
        actions={
          <>
            {/* The visit's own history, beside the patient's file — "what has happened in this consultation"
                is the question a doctor asks of the screen they are documenting it on. Same steps, same
                rendering and same modal as the appointment timeline on the day board. */}
            <EncounterTimelineButton encounterId={encounter.id} variant="ghost" label={S.visitTimeline} />
            <Button
              variant="ghost"
              size="sm"
              leadingIcon={<Icon name="user" width={16} height={16} aria-hidden="true" />}
              onClick={() => openProfile(encounter.patientId)}
            >
              {t(S.patientFile)}
            </Button>
          </>
        }
      />

      {/* Directly under the identity block and still above the tabs: allergies are not a tab's worth of
          detail to go and find, they are a precondition for everything the tabs let you do. */}
      <MemberClinicalPanel
        beneficiaryId={encounter.patientId}
        onRecorded={() => setClinicalNonce((n) => n + 1)}
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
                    {/*
                      The coded diagnosis leads the note, ABOVE Subjective.

                      It sat inside Assessment, which is where a diagnosis belongs in the SOAP mnemonic and
                      the wrong place for it on a screen. The codes are what everything downstream keys on —
                      the authorization, the claim, the formulary check — so they are the one part of this
                      note another team reads without reading the prose around it, and burying them three
                      cards down made them look like a footnote to the narrative rather than its conclusion.
                    */}
                    <DiagnosisPanel
                      encounterId={encounter.id}
                      diagnoses={diagnoses}
                      signed={signed}
                      onAdded={(d) => setDiagnoses((prev) => [...prev, d])}
                      onRemoved={(id) => setDiagnoses((prev) => prev.filter((d) => d.id !== id))}
                    />
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
                      />
                    ))}
                  </div>
                ),
              },
              /*
                THREE tabs where there was one.
                A single "Orders" tab stacked the investigations table, the prescriptions table and the
                prescribing composer on one scroll, so writing a prescription meant scrolling past a lab
                table that had nothing to do with it — and the lab side had no composer at all, only a modal
                with two hard-coded text boxes. Splitting them by what the doctor came to DO puts each
                composer directly under the list it adds to, and lets the lab and imaging sides carry the
                same multi-line, checked sequence prescribing already had.

                Labs and imaging are separate rather than one "Investigations" tab because they are separate
                ORDERS: one order has one type, it reaches one queue, and the CPT section it draws from
                differs. A combined tab would have to ask the doctor which kind they meant.
              */
              {
                value: "prescriptions",
                label: t(S.tabPrescriptions),
                content: visited.has("prescriptions")
                  ? <PrescriptionsTab encounter={encounter} diagnoses={diagnoses} />
                  : null,
              },
              {
                value: "labs",
                label: t(S.tabLabs),
                content: visited.has("labs")
                  ? <InvestigationsTab encounter={encounter} diagnoses={diagnoses} orderType="Lab" />
                  : null,
              },
              {
                value: "imaging",
                label: t(S.tabRadiology),
                content: visited.has("imaging")
                  ? <InvestigationsTab encounter={encounter} diagnoses={diagnoses} orderType="Radiology" />
                  : null,
              },
              {
                value: "procedures",
                label: t(S.tabProcedures),
                // The SHARED composer, parameterised — not a third copy. Labs and Radiology already ran
                // through InvestigationsTab, so a Procedure order reaches the same consume/authorise/claim
                // path as a lab order rather than forking it (design 45 §2, invariant 2).
                content: visited.has("procedures")
                  ? <InvestigationsTab encounter={encounter} diagnoses={diagnoses} orderType="Procedure" />
                  : null,
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
          <VitalsPanel
            encounterId={encounter.id}
            vitals={encounter.vitals}
            readOnly={signed}
            onRecorded={onSaved}
          />
          <Card as="section" className="enc-actions">
            {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
            {signed ? (
              <p className="muted" style={{ margin: 0 }}>{t(S.signedBody)}</p>
            ) : (
              <>
                <Button
                  variant="primary"
                  loading={busy === "final"}
                  disabled={!hasContent || !hasPrimary || unsent.length > 0}
                  leadingIcon={<Icon name="lock" width={16} height={16} aria-hidden="true" />}
                  onClick={() => setConfirming(true)}
                >
                  {t(S.finalize)}
                </Button>
                {/* Why it is unavailable, next to the control. A disabled primary action with no reason
                    beside it is the commonest way a screen makes someone feel it is broken. */}
                {hasContent && !hasPrimary && (
                  <p className="muted" style={{ margin: 0, fontSize: "0.8125rem" }}>{t(S.needPrimary)}</p>
                )}
                {/* An ALERT rather than the quiet grey line above it. This one names work the doctor has
                    already done and is about to lose, and it points at a tab they are not looking at. */}
                {unsent.length > 0 && <InlineAlert tone="warn">{t(unsentMessage)}</InlineAlert>}
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

/**
 * The encounter's coded conditions — the panel that opens the note.
 *
 * <b>Primary and secondary are drawn as different things, because they are.</b> An encounter has ONE primary
 * diagnosis: the condition the visit was chiefly about, and the code the authorization, the claim and the
 * formulary check all key on. The rest are context. Rendering them as one undifferentiated row of chips left
 * the doctor's own judgement about which was which invisible, and everything downstream reading whichever
 * row emr happened to return first.
 */
function DiagnosisPanel({
  encounterId,
  diagnoses,
  signed,
  onAdded,
  onRemoved,
}: {
  encounterId: string;
  diagnoses: EncounterDiagnosis[];
  signed: boolean;
  onAdded: (d: EncounterDiagnosis) => void;
  onRemoved: (id: string) => void;
}) {
  const t = useLoc();
  const primary = diagnoses.filter((d) => d.rank === "Primary");
  const secondary = diagnoses.filter((d) => d.rank !== "Primary");

  return (
    <Card as="section" className="soap-card dx-panel" aria-labelledby="dx-panel-title">
      <div className="soap-card-head">
        <span className="soap-badge dx-badge" aria-hidden="true">
          <Icon name="check2" width={15} height={15} />
        </span>
        <h3 className="soap-title" id="dx-panel-title">{t(S.diagnosisTitle)}</h3>
        <span className="soap-hint">{t(S.diagnosisHint)}</span>
        <DiagnosisPicker
          encounterId={encounterId}
          disabled={signed}
          hasPrimary={primary.length > 0}
          onAdded={onAdded}
        />
      </div>

      {/*
        Stated in the panel, not discovered at the moment of signing.

        The rule (an encounter is documented with a primary diagnosis) is enforced on Save & finalize, and a
        doctor who only learns it when the button refuses has already finished writing. So the gap is named
        while there is still something easy to do about it.
      */}
      {!signed && primary.length === 0 && (
        <InlineAlert tone="warn">{t(S.needPrimary)}</InlineAlert>
      )}

      <DiagnosisGroup
        label={t(S.rankPrimary)}
        rows={primary}
        encounterId={encounterId}
        signed={signed}
        onRemoved={onRemoved}
        empty={t(S.noPrimary)}
      />
      {secondary.length > 0 && (
        <DiagnosisGroup
          label={t(S.rankSecondary)}
          rows={secondary}
          encounterId={encounterId}
          signed={signed}
          onRemoved={onRemoved}
        />
      )}
    </Card>
  );
}

function DiagnosisGroup({
  label,
  rows,
  encounterId,
  signed,
  onRemoved,
  empty,
}: {
  label: string;
  rows: EncounterDiagnosis[];
  encounterId: string;
  signed: boolean;
  onRemoved: (id: string) => void;
  empty?: string;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [removing, setRemoving] = useState<string | null>(null);

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

  if (rows.length === 0 && !empty) return null;

  return (
    <div className="dx-group">
      <h4 className="section-h" style={{ margin: 0 }}>{label}</h4>
      {rows.length === 0 ? (
        <p className="muted" style={{ margin: 0 }}>{empty}</p>
      ) : (
        <ul className="chip-list dx-list">
          {rows.map((d) => (
            <li key={d.id ?? d.code} className={`dx-chip dx-chip--${d.rank.toLowerCase()}`}>
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
      )}
    </div>
  );
}

/**
 * The ICD-10 picker — several codes per visit, in one pass.
 *
 * <b>Search, stage, then commit.</b> It used to record a code the instant it was clicked and close, which is
 * the right shape for adding one and the wrong shape for the ordinary case: a consultation that ends in a
 * primary plus two comorbidities meant opening the same dialog three times and retyping a search each time.
 * Staging also makes the RANK a decision the doctor takes over the whole set — which of these was the visit
 * about — instead of one they answer three times without seeing the other two.
 *
 * Nothing is written until Add is pressed. A code removed from the staging list was never recorded, so it
 * leaves no retract in the audit trail for a mis-click that never reached the record.
 */
function DiagnosisPicker({
  encounterId,
  disabled,
  hasPrimary,
  onAdded,
}: {
  encounterId: string;
  disabled: boolean;
  /** Whether the encounter ALREADY has a primary — decides what the first staged pick defaults to. */
  hasPrimary: boolean;
  onAdded: (d: EncounterDiagnosis) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [staged, setStaged] = useState<(IcdRef & { rank: DiagnosisRank })[]>([]);
  const [results, setResults] = useState<IcdRef[]>([]);
  const [searching, setSearching] = useState(false);
  const [busy, setBusy] = useState(false);

  // A fresh dialog each time it opens: staging that survived a cancel would re-offer codes the doctor
  // decided against.
  useEffect(() => {
    if (!open) { setStaged([]); setQuery(""); setResults([]); }
  }, [open]);

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

  const stagedPrimary = staged.some((r) => r.rank === "Primary");

  function stage(hit: IcdRef) {
    setStaged((prev) => {
      if (prev.some((r) => r.code === hit.code)) return prev;
      // The first pick takes Primary only while neither the encounter nor this batch has one.
      const rank: DiagnosisRank =
        !hasPrimary && !prev.some((r) => r.rank === "Primary") ? "Primary" : "Secondary";
      return [...prev, { ...hit, rank }];
    });
  }

  function setRank(code: string, rank: DiagnosisRank) {
    setStaged((prev) => prev.map((r) => (r.code === code ? { ...r, rank } : r)));
  }

  async function commit() {
    setBusy(true);
    // Sequential, not Promise.all: each POST is a separate clinical record with its own audit event, and
    // firing six at a diagnosis endpoint that validates every code against master data buys nothing but a
    // harder failure to report.
    const failed: string[] = [];
    for (const row of staged) {
      try {
        onAdded(await api.addEncounterDiagnosis(encounterId, row.code, row.rank));
      } catch {
        failed.push(row.code);
      }
    }
    setBusy(false);
    if (failed.length === 0) {
      toast(t(S.codeAdded), "ok");
      setOpen(false);
      return;
    }
    // Partial success is reported as partial. The ones that saved are on the panel behind the dialog; the
    // ones that did not stay staged, so pressing Add again retries exactly those.
    setStaged((prev) => prev.filter((r) => failed.includes(r.code)));
    toast(t(S.someFailed).replace("{codes}", failed.join(", ")), "bad");
  }

  if (disabled) return null;

  return (
    <Modal
      open={open}
      onOpenChange={setOpen}
      title={t(S.codePicker)}
      /*
       * WIDE. Every row here is a code beside a title read by scanning — "I11.0 Hypertensive heart disease
       * with (congestive) heart failure" — and at the default 520px those titles wrapped to two and three
       * lines, so a list of six results filled the modal and the staged rows below it had to be scrolled to.
       * A staged row is worse still: code, title, rank picker and remove control on ONE line, with the
       * picker holding a fixed 9rem, which left the title about a hundred pixels.
       */
      wide
      trigger={
        // Ghost, matching "Visit timeline" and "Patient file" in the context bar directly above it. It was
        // `secondary` — a bordered, filled control — so the workspace showed three actions of the same weight
        // in the same column of the same screen drawn two different ways, and the odd one out was the one
        // whose border made it look like the page's principal act. Nothing about adding a code is heavier
        // than opening the patient's file.
        <Button
          variant="ghost"
          size="sm"
          leadingIcon={<Icon name="plus" width={16} height={16} aria-hidden="true" />}
        >
          {t(S.addCode)}
        </Button>
      }
      footer={
        <>
          <Button variant="ghost" onClick={() => setOpen(false)}>{t(S.cancel)}</Button>
          <Button
            variant="primary"
            loading={busy}
            disabled={staged.length === 0}
            onClick={() => void commit()}
          >
            {staged.length > 1
              ? t(S.addN).replace("{n}", String(staged.length))
              : t(S.addOne)}
          </Button>
        </>
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
        <ul className="icd-results mrs-scroll" aria-live="polite" aria-busy={searching}>
          {results.map((r) => {
            const already = staged.some((x) => x.code === r.code);
            return (
              <li key={r.code}>
                <button
                  type="button"
                  className="icd-hit"
                  // Not disabled — a disabled control cannot say why it is disabled. It stays pressable and
                  // announces that this code is already on the list.
                  aria-pressed={already}
                  onClick={() => stage(r)}
                >
                  <span className="dx-code tnum">{r.code}</span>
                  <span>{r.title}</span>
                  {already && (
                    <span className="icd-hit-added">
                      <Icon name="ok" width={14} height={14} aria-hidden="true" />
                      {t(S.staged)}
                    </span>
                  )}
                </button>
              </li>
            );
          })}
          {!searching && query.trim().length >= 2 && results.length === 0 && (
            <li className="muted">{t(S.codeNone)}</li>
          )}
        </ul>

        {staged.length > 0 && (
          <div className="dx-staged">
            <h4 className="section-h" style={{ margin: 0 }}>
              {t(S.toAdd).replace("{n}", String(staged.length))}
            </h4>
            {/* The advisory is about the SET, which is the point of staging: a doctor choosing three codes
                at once can see that two of them claim to be the primary. */}
            {hasPrimary && stagedPrimary && <InlineAlert tone="warn">{t(S.replacesPrimary)}</InlineAlert>}
            {!hasPrimary && !stagedPrimary && <InlineAlert tone="warn">{t(S.needPrimary)}</InlineAlert>}
            <ul className="dx-staged-list">
              {staged.map((r) => (
                <li key={r.code}>
                  <span className="dx-code tnum">{r.code}</span>
                  <span className="dx-staged-title">{r.title}</span>
                  <SelectField
                    label={t(S.rank)}
                    hideLabel
                    value={r.rank}
                    onChange={(v) => setRank(r.code, v as DiagnosisRank)}
                    options={[
                      { value: "Primary", label: t(S.rankPrimary) },
                      { value: "Secondary", label: t(S.rankSecondary) },
                    ]}
                  />
                  <button
                    type="button"
                    className="dx-remove"
                    aria-label={t(S.removeOne).replace("{code}", r.code)}
                    onClick={() => setStaged((prev) => prev.filter((x) => x.code !== r.code))}
                  >
                    <Icon name="cross" width={14} height={14} aria-hidden="true" />
                  </button>
                </li>
              ))}
            </ul>
          </div>
        )}
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

/**
 * The readings the rail carries, each with its icon and its unit.
 *
 * `range` is present only for the four that HAVE a clinical reference band. Height and weight do not: there
 * is no normal height, and a weight is read against this patient's own trend rather than against a
 * population band. Printing "Reference 40–120 kg" under a weight would be inventing a norm and flagging
 * people against it.
 */
const VITAL_ROWS: { key: string; label: Localized; icon: IconName; unit: string; range?: string }[] = [
  { key: "systolic", label: S.vBp, icon: "gauge", unit: "mmHg", range: "90–120 / 60–80" },
  { key: "heartRate", label: S.vHr, icon: "heart", unit: "bpm", range: "60–100" },
  { key: "tempC", label: S.vTemp, icon: "temperature", unit: "°C", range: "36.3–37.2" },
  { key: "spo2", label: S.vSpo2, icon: "droplet", unit: "%", range: "95–100" },
  { key: "heightCm", label: S.vHeight, icon: "ruler", unit: "cm" },
  { key: "weightKg", label: S.vWeight, icon: "scale", unit: "kg" },
];

/**
 * The vitals rail — read AND write.
 *
 * <b>There is no Vitals tab any more.</b> There were two places showing the same four numbers: this panel,
 * beside the note, and a tab that duplicated it in order to hold the capture form. A doctor writing "Temp
 * 38.2, chest clear" is reading these as they type, so the panel is the one that has to stay; and once
 * capture opens in a modal there is nothing left for the tab to hold.
 *
 * An unrecorded reading renders as an em dash against its own icon and band, never as an absent row. The
 * empty slot is the information: it says nobody has taken this patient's temperature.
 */
function VitalsPanel({
  encounterId,
  vitals,
  readOnly,
  onRecorded,
}: {
  encounterId: string;
  vitals: Encounter["vitals"];
  readOnly: boolean;
  onRecorded: () => void;
}) {
  const t = useLoc();
  const { dateTime } = useFormat();

  const values: Record<string, number | null> = {
    systolic: vitals.systolic,
    heartRate: vitals.heartRate,
    tempC: vitals.tempC,
    spo2: vitals.spo2,
    heightCm: vitals.heightCm,
    weightKg: vitals.weightKg,
  };
  const display: Record<string, string | null> = {
    systolic: bpDisplay(vitals),
    heartRate: num(vitals.heartRate),
    tempC: num(vitals.tempC),
    spo2: num(vitals.spo2),
    heightCm: num(vitals.heightCm),
    weightKg: num(vitals.weightKg),
  };

  return (
    <Card as="section" className="vitals-panel">
      <div className="vitals-head">
        <h2 className="section-h" style={{ margin: 0 }}>{t(S.vitalsTitle)}</h2>
        {vitals.measuredAt && <span className="muted vitals-when">{dateTime(vitals.measuredAt)}</span>}
        {!readOnly && <RecordVitalsModal encounterId={encounterId} onRecorded={onRecorded} />}
      </div>
      <dl className="vitals-list">
        {VITAL_ROWS.map((r) => {
          const flag = flagFor(r.key, values[r.key]);
          return (
            <div key={r.key} className="vital-row">
              <dt>
                <span className="vital-name">
                  <Icon name={r.icon} width={15} height={15} aria-hidden="true" />
                  {t(r.label)}
                </span>
                <span className="vital-ref">
                  {r.range ? t(S.vitalsRef).replace("{range}", `${r.range} ${r.unit}`) : r.unit}
                </span>
              </dt>
              <dd>
                <span className={display[r.key] === null ? "vital-value vital-value--none" : "vital-value tnum"}>
                  {display[r.key] ?? "—"}
                </span>
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

function RecordVitalsModal({ encounterId, onRecorded }: { encounterId: string; onRecorded: () => void }) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const [open, setOpen] = useState(false);
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
      .filter((f) => (form[f.key] ?? "").trim() !== "" && Number.isFinite(Number(form[f.key])))
      .map((f) => ({ type: f.type, value: Number(form[f.key]) }));
    if (readings.length === 0) {
      setError(S.vitalsEmpty);
      return;
    }
    setBusy(true);
    setError(null);
    try {
      await api.recordVitals(encounterId, readings);
      setForm({});
      setOpen(false);
      toast(t(S.vitalsSaved), "ok");
      onRecorded();
    } catch {
      setError(S.saveFailed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open={open}
      onOpenChange={setOpen}
      title={t(S.recordVitals)}
      trigger={
        <Button
          variant="ghost"
          size="sm"
          // Icon-only in a tight rail header, so the name has to come from aria-label.
          aria-label={t(S.recordVitals)}
          title={t(S.recordVitals)}
          leadingIcon={<Icon name="plus" width={16} height={16} aria-hidden="true" />}
        />
      }
      footer={
        <>
          <Button variant="ghost" onClick={() => setOpen(false)}>{t(S.cancel)}</Button>
          <Button variant="primary" loading={busy} onClick={() => void submit()}>{t(S.submit)}</Button>
        </>
      }
    >
      <div className="stack-3">
        {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
        <div className="vitals-form">
          {fields.map((f) => (
            <InputField
              key={f.key}
              label={t(f.label)}
              type="number"
              inputMode="decimal"
              value={form[f.key] ?? ""}
              // The value is read HERE, not inside the updater: `currentTarget` is null by the time React
              // runs a functional setState, so reaching for it there throws on the first keystroke.
              onChange={(e) => {
                const next = e.currentTarget.value;
                setForm((prev) => ({ ...prev, [f.key]: next }));
              }}
            />
          ))}
        </div>
      </div>
    </Modal>
  );
}

// ---------------------------------------------------------------- prescriptions / labs / imaging
//
// Three tabs, three components, one shape: the list of what this clinician has already raised for THIS
// patient, then the composer that adds to it. The composer sits under its own list on purpose — the first
// question a doctor asks before ordering something is whether they already ordered it.

/** Stable accessor — an inline arrow would be a new identity each render and rebuild the filter's memo. */
const encounterStartedAt = (r: PatientListItem) => r.lastVisit;

/** Both tabs filter the clinician's own lists to this patient in the browser. */
function forPatient<T extends { beneficiary: { id: string } }>(rows: T[] | null | undefined, patientId: string): T[] {
  // Both endpoints already answer "mine"; narrowing further is a display concern, and asking a service for
  // a second patient-scoped variant of a list it already returns would be a new seam for no new information.
  return (rows ?? []).filter((r) => r.beneficiary.id === patientId);
}

function PrescriptionsTab({
  encounter,
  diagnoses,
}: {
  encounter: Encounter;
  /**
   * The encounter's LIVE diagnoses — the staged state, not `encounter.diagnoses`, which is whatever was
   * loaded. The prescribe modal used to receive only an encounter id, so the indication check had nothing
   * to compare against; passing the codes recorded a moment ago is the point.
   */
  diagnoses: EncounterDiagnosis[];
}) {
  const api = useApi();
  const t = useLoc();
  const { date } = useFormat();
  const rx = useAsync<RxRow[]>(useCallback(() => api.prescriptionsMine(), [api]), []);
  const rxFor = useMemo(() => forPatient(rx.data, encounter.patientId), [rx.data, encounter.patientId]);
  const [viewing, setViewing] = useState<RxRow | null>(null);
  // 30.6 — which transaction is being amended or withdrawn, from the ROW. One at a time.
  const [acting, setActing] = useState<{ rx: RxRow; action: TransactionAction } | null>(null);
  const [reasons, setReasons] = useState<AmendReasonOption[]>([]);

  useEffect(() => {
    let live = true;
    // Guarded, and the guard is not defensive clutter: the picker is an ENRICHMENT of a table that must
    // render regardless. A throw here used to take the whole encounter screen down.
    Promise.resolve(api.amendmentReasons?.("prescription") ?? [])
      .then((r) => { if (live) setReasons(r); })
      .catch(() => { if (live) setReasons([]); });
    return () => { live = false; };
  }, [api]);

  const rxCols: Column<RxRow>[] = [
    // The Rx REFERENCE, not the surrogate id. A doctor reads this column to match a prescription against
    // what the pharmacy or the patient is quoting back, and neither of them has ever seen the uuid.
    { key: "rxNo", header: t(S.colRef), cell: (r) => <span className="tnum">{r.rxNo}</span>,
      sortable: true, sortValue: (r) => r.rxNo },
    // A COUNT — a quantity compared down the column, so it right-aligns with tabular figures. `.tnum` on a
    // span sets the figure width and leaves the column ragged; alignment lives on the cell.
    { key: "lineCount", header: t(S.colLines), cell: (r) => r.lineCount,
      numeric: true, sortable: true, sortValue: (r) => r.lineCount },
    // Sorts on the ISO instant, not the rendered date — Arabic-Indic digits sort by glyph.
    { key: "submittedAt", header: t(S.colWhen), cell: (r) => <span className="tnum">{r.submittedAt ? date(r.submittedAt) : "—"}</span>,
      sortable: true, sortValue: (r) => r.submittedAt ?? "" },
    { key: "status", header: t(S.colStatus), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />,
      sortable: true, sortValue: (r) => r.status.label.en },
    // What has happened to THIS prescription. Keyed on the encounter it was written in — which, on this tab,
    // is the encounter the workspace is already open on.
    {
      key: "timeline",
      header: t(S.colTimeline),
      cell: (r) => (r.encounterId
        ? <EncounterTimelineButton encounterId={r.encounterId} reference={r.rxNo} />
        : <span className="muted">—</span>),
    },
    {
      key: "view",
      header: t(S.colView),
      cell: (r) => (
        <Button
          variant="ghost"
          size="sm"
          className="rxv-open"
          aria-label={`${t(S.viewRx)} ${r.rxNo}`}
          leadingIcon={<Icon name="eye" />}
          onClick={() => setViewing(r)}
        />
      ),
    },
    /*
      30.6 — AMEND AND WITHDRAW, ON THE ROW.

      Both used to live inside the detail dialog, so a doctor correcting a prescription they had just written
      had to open it to find out whether it could be corrected. Icons rather than words because this is the
      fifth control in a row and the reference is what the eye is scanning for; the accessible name carries
      the reference so nothing is lost to anyone reading it aloud.
    */
    {
      key: "actions",
      header: t(S.colActions),
      cell: (r) => (
        <span className="row-actions">
          <Button
            variant="ghost"
            size="sm"
            aria-label={`${t(S.amend)} — ${r.rxNo}`}
            onClick={() => setActing({ rx: r, action: "amend" })}
          >
            <Icon name="pen" />
          </Button>
          <Button
            // DANGER, and frameless because it is a glyph — see `.mrs-btn.mrs-danger:has(> svg:only-child)`.
            // It is the only red in the row; a column of outlined red boxes would read as an alarm about
            // the rows themselves rather than as a control that acts on one.
            variant="danger"
            size="sm"
            aria-label={`${t(S.withdraw)} — ${r.rxNo}`}
            onClick={() => setActing({ rx: r, action: "withdraw" })}
          >
            <Icon name="cross" />
          </Button>
        </span>
      ),
    },
  ];

  /*
    Sorting and paging. No search box and — since 31.1 — no date filter either.

    The tab has already narrowed this to ONE patient, and the table sits directly above the composer the
    doctor came here to type into. A period chip group and eight rows of history between the two pushed that
    composer below the fold to answer a question the tab had already answered.
  */
  const rxQuery = useTableQuery<RxRow>({
    rows: rxFor,
    columns: rxCols,
    pageSize: 5,
    initialSortKey: "submittedAt",
    initialSortDir: "descending",
  });

  return (
    <div className="stack">
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <h3 className="section-h">{t(S.rxFor)}</h3>
        {rxFor.length === 0 ? (
          <p className="muted" style={{ margin: 0 }}>{t(S.noRx)}</p>
        ) : (
          <DataTableView query={rxQuery} columns={rxCols} rowKey={(r) => r.id} caption={t(S.rxFor)}
            noMatchesLabel={t(S.noMatchesWhen)} />
        )}

        {/*
          Composed INLINE rather than in a dialog. A prescription line carries five fields plus a per-line
          status and an expanding findings panel; a modal narrow enough to sit over the encounter collapsed
          all of it into a single stacked column, which is the layout the design's own option row is meant
          to avoid.
        */}
        <div className="rx-compose">
          <h4 className="section-h rx-compose-h">{t(S.prescribe)}</h4>
          <PrescribingWorkspace
            encounterId={encounter.id}
            beneficiaryId={encounter.patientId}
            diagnosisIcdCodes={diagnoses.map((d) => d.code)}
            // Re-read the list directly above the composer. Without this the prescription a doctor just
            // wrote does not appear until the screen is reloaded — and "it is not in the list" is how a
            // successful submit reads as a failed one.
            onDone={rx.reload}
          />
        </div>
      </Card>
      {/* Same reason as `onDone` above: a withdrawn drug that stays in the list reads as a failed withdraw. */}
      <PrescriptionDetailModal
        rx={viewing}
        onOpenChange={(open) => !open && setViewing(null)}
        onChanged={rx.reload}
      />

      {/* 30.6 — the transaction-level pair, reached from the row rather than from inside the record. */}
      {acting && (
        <TransactionActionsDialog
          open
          action={acting.action}
          reference={acting.rx.rxNo}
          lines={acting.rx.lines.map((l) => ({
            id: l.id,
            label: l.drug ? t(l.drug) : t(S.rxDrugMissing),
            quantity: l.quantityPrescribed,
            quantityUnit: l.quantityUnit ?? null,
            // Dispensed is the lock that matters here: a medicine the patient already has cannot be
            // un-given, and the amount is what the pharmacy metered against.
            locked: l.quantityDispensed > 0 ? t(S.lockedDispensed)
              : l.status.label.en === "Active" ? null : t(S.lockedTerminal),
          }))}
          reasons={reasons}
          onCancel={() => setActing(null)}
          onWithdraw={({ reasonCode, reasonText }) =>
            api.withdrawPrescription(acting.rx.id, reasonCode, reasonText)}
          onAmend={({ lineId, quantity, reasonCode, reasonText }) =>
            api.amendPrescriptionLine(acting.rx.id, lineId, quantity, reasonCode, reasonText)}
          // 31.2 — removing an item from the script. Its own act, not a quantity of zero.
          onWithdrawLine={({ lineId, reasonCode, reasonText }) =>
            api.cancelPrescriptionLine(acting.rx.id, lineId, reasonCode, reasonText)}
          onDone={rx.reload}
        />
      )}
    </div>
  );
}

function InvestigationsTab({
  encounter,
  diagnoses,
  orderType,
}: {
  encounter: Encounter;
  diagnoses: EncounterDiagnosis[];
  orderType: InvestigationOrderType;
}) {
  const api = useApi();
  const t = useLoc();
  const { date } = useFormat();
  // 29.4 — which service line's history is open, if any. One piece of state, one modal, whichever tab.
  const [historyFor, setHistoryFor] = useState<{ code: string; label: string } | null>(null);
  const orders = useAsync<OrderRow[]>(useCallback(() => api.ordersMine(), [api]), []);

  // Split by TYPE as well as by patient: the imaging tab must not list a blood count. `ordersMine` returns
  // both kinds because it is one worklist; which of them belongs on this tab is this screen's question.
  const mineFor = useMemo(
    () => forPatient(orders.data, encounter.patientId)
      .filter((o) => o.orderType.toLowerCase() === orderType.toLowerCase()),
    [orders.data, encounter.patientId, orderType],
  );

  // 29.2 — three order types now share this tab (design 45 §2). Kept as a lookup rather than a chain of
  // ternaries: a fourth type added to the chain silently inherits the Lab labels, which is how a Procedure
  // tab would come to be headed "Lab orders for this patient".
  const heading = orderType === "Radiology" ? S.radiologyFor : orderType === "Procedure" ? S.proceduresFor : S.labsFor;
  const empty = orderType === "Radiology" ? S.noRadiology : orderType === "Procedure" ? S.noProcedures : S.noLabs;
  const composeHeading = orderType === "Radiology" ? S.orderRadiology : orderType === "Procedure" ? S.orderProcedure : S.orderLab;

  const [viewingOrder, setViewingOrder] = useState<OrderRow | null>(null);
  // 30.6 — which order is being amended or withdrawn, from the ROW.
  const [acting, setActing] = useState<{ order: OrderRow; action: TransactionAction } | null>(null);
  const [reasons, setReasons] = useState<AmendReasonOption[]>([]);

  useEffect(() => {
    let live = true;
    // "order" scope, so the picker offers the reasons an order can be withdrawn for and never the two that
    // belong to a medicine (dose correction, drug unavailable).
    Promise.resolve(api.amendmentReasons?.("order") ?? [])
      .then((r) => { if (live) setReasons(r); })
      .catch(() => { if (live) setReasons([]); });
    return () => { live = false; };
  }, [api]);

  const orderCols: Column<OrderRow>[] = [
    { key: "orderNo", header: t(S.colRef), cell: (r) => <span className="tnum">{r.orderNo}</span>,
      sortable: true, sortValue: (r) => r.orderNo },
    // The order TYPE is the tab; repeating it in every row of a tab that only holds one kind is a column
    // whose every cell says the same word.
    { key: "primaryCode", header: t(S.colTest), cell: (r) => r.primaryCode,
      sortable: true, sortValue: (r) => r.primaryCode },
    // A COUNT, so it right-aligns on the cell with tabular figures — see the prescriptions tab.
    { key: "lineCount", header: t(S.colLines), cell: (r) => r.lineCount,
      numeric: true, sortable: true, sortValue: (r) => r.lineCount },
    { key: "requestedAt", header: t(S.colWhen), cell: (r) => <span className="tnum">{date(r.requestedAt)}</span>,
      sortable: true, sortValue: (r) => r.requestedAt },
    { key: "status", header: t(S.colStatus), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />,
      sortable: true, sortValue: (r) => r.status.label.en },
    // What has happened to THIS order — placed, sent for approval, sample taken, result reported.
    {
      key: "timeline",
      header: t(S.colTimeline),
      cell: (r) => (r.encounterId
        ? <EncounterTimelineButton encounterId={r.encounterId} reference={r.orderNo} />
        : <span className="muted">—</span>),
    },
    // 30.6 — OPEN the order, which is where its lines and their withdraw/amend actions live. The
    // encounter's own tab had no way in: the detail dialog existed only on the worklist, so a doctor
    // correcting an order they had just raised had to leave the visit to do it.
    {
      key: "open",
      header: t(S.colOpen),
      cell: (r) => (
        <Button
          variant="ghost"
          aria-label={`${t(S.openOrder)} — ${r.orderNo}`}
          onClick={() => setViewingOrder(r)}
        >
          <Icon name="chevron" />
        </Button>
      ),
    },
    // 29.4 — "has this patient had this service before, and what did it show?" (design 45 §4). THE shared
    // modal, opened from every service line in every tab — one component and one endpoint, never one
    // implementation per tab.
    {
      key: "history",
      header: t(S.colHistory),
      cell: (r) => (
        <Button
          variant="ghost"
          aria-label={`${t(S.viewHistory)} — ${r.primaryCode}`}
          onClick={() => setHistoryFor({ code: r.primaryCode, label: r.primaryCode })}
        >
          <Icon name="clock" />
        </Button>
      ),
    },
    // 30.6 — amend and withdraw, on the ROW. See the prescriptions tab: the same two acts, worded and placed
    // identically, because a prescriber who learns one must not have to relearn the other.
    {
      key: "actions",
      header: t(S.colActions),
      cell: (r) => (
        <span className="row-actions">
          <Button
            variant="ghost"
            size="sm"
            aria-label={`${t(S.amend)} — ${r.orderNo}`}
            onClick={() => setActing({ order: r, action: "amend" })}
          >
            <Icon name="pen" />
          </Button>
          <Button
            // DANGER, and frameless because it is a glyph — see `.mrs-btn.mrs-danger:has(> svg:only-child)`.
            // It is the only red in the row; a column of outlined red boxes would read as an alarm about
            // the rows themselves rather than as a control that acts on one.
            variant="danger"
            size="sm"
            aria-label={`${t(S.withdraw)} — ${r.orderNo}`}
            onClick={() => setActing({ order: r, action: "withdraw" })}
          >
            <Icon name="cross" />
          </Button>
        </span>
      ),
    },
  ];

  // Sorting and paging — no search box, and since 31.1 no date filter. Same reasoning as the prescriptions
  // tab: the tab has already narrowed this to one patient and one order type, and the composer beneath is
  // what the doctor opened the tab to reach.
  const orderQuery = useTableQuery<OrderRow>({
    rows: mineFor,
    columns: orderCols,
    pageSize: 5,
    initialSortKey: "requestedAt",
    initialSortDir: "descending",
  });

  return (
    <div className="stack">
      <Card as="section" style={{ padding: "var(--sp5)" }}>
        <h3 className="section-h">{t(heading)}</h3>
        {mineFor.length === 0 ? (
          <p className="muted" style={{ margin: 0 }}>{t(empty)}</p>
        ) : (
          <DataTableView query={orderQuery} columns={orderCols} rowKey={(r) => r.id} caption={t(heading)}
            noMatchesLabel={t(S.noMatchesWhen)} />
        )}

        <div className="rx-compose">
          <h4 className="section-h rx-compose-h">{t(composeHeading)}</h4>
          <InvestigationWorkspace
            encounterId={encounter.id}
            beneficiaryId={encounter.patientId}
            orderType={orderType}
            diagnosisIcdCodes={diagnoses.map((d) => d.code)}
            onDone={orders.reload}
          />
        </div>
      </Card>

      {/*
        29.4 — THE shared service-history modal (design 45 §4). Rendered once per tab instance and driven by
        one piece of state, so the Labs, Radiology and OP Procedures tabs open the SAME component against the
        SAME endpoint. Not one implementation per tab: four copies would be four places for the
        restricted-result branch to drift, and the one that drifted would be the one nobody reviewed.
      */}
      {historyFor && (
        <ServiceHistoryModal
          beneficiaryId={encounter.patientId}
          serviceType={orderType}
          code={historyFor.code}
          label={historyFor.label}
          onClose={() => setHistoryFor(null)}
        />
      )}

      {/* 30.6 — the same dialog the worklist opens, so withdrawing a line reads identically in both places. */}
      <OrderDetailModal
        order={viewingOrder}
        onOpenChange={(open) => !open && setViewingOrder(null)}
        onChanged={orders.reload}
      />

      {/* 30.6 — the transaction-level pair, reached from the row. */}
      {acting && (
        <TransactionActionsDialog
          open
          action={acting.action}
          reference={acting.order.orderNo}
          lines={(acting.order.lines ?? []).map((l) => ({
            id: l.id,
            label: l.description ?? l.code,
            quantity: l.quantityOrdered,
            // Consumed is the lock: a session already delivered or a sample already taken is work that
            // happened, and no amendment can un-happen it.
            locked: l.quantityConsumed > 0 ? t(S.lockedDelivered)
              : l.status.label.en === "Placed" || l.status.label.en === "Active" ? null : t(S.lockedTerminal),
          }))}
          reasons={reasons}
          onCancel={() => setActing(null)}
          onWithdraw={({ reasonCode, reasonText }) =>
            api.withdrawOrder(acting.order.id, reasonCode, reasonText)}
          onAmend={({ lineId, quantity, reasonCode, reasonText }) =>
            api.amendOrderLine(acting.order.id, lineId, quantity, reasonCode, reasonText)}
          // 31.2 — the SAME act on an order: Labs, Radiology and OP Procedures share this dialog.
          onWithdrawLine={({ lineId, reasonCode, reasonText }) =>
            api.cancelOrderLine(acting.order.id, lineId, reasonCode, reasonText)}
          onDone={orders.reload}
        />
      )}
    </div>
  );
}


function HistoryTab({ beneficiaryId }: { beneficiaryId: string }) {
  const api = useApi();
  const t = useLoc();
  const [tab, setTab] = useState("encounters");

  // All three in ONE call. The profile endpoint takes a section list and answers them together, so asking
  // per tab would be three round trips and three audited PHI reads for one question.
  const state = useAsync(
    useCallback(
      () => api.patientProfile(beneficiaryId, ["encounters", "investigations", "prescriptions"]),
      [api, beneficiaryId],
    ),
    [beneficiaryId],
  );

  /*
    29.2 (design 45 §3) — OP Procedures is a pane over the INVESTIGATIONS section, not a section of its own.

    A procedure IS an investigation order, so it already travels this path, under this gate, in this payload.
    Splitting the rows the caller has ALREADY been authorised to see, by a routing label that carries no
    clinical content, is what "same projection rules, same sensitivity gating, NO NEW ACCESS PATH" means when
    taken literally — a second section would have been a second thing to gate, and the second one is always
    the one that gets gated differently.

    `filter` narrows the rows; everything else — the restricted-result handling, the request-access action,
    the audit already emitted for the read — is untouched, because it is the same section object.
  */
  const panes: { value: string; key: string; label: Localized; filter?: (row: { orderType?: string }) => boolean }[] = [
    { value: "encounters", key: "encounters", label: S.histEncounters },
    {
      value: "investigations", key: "investigations", label: S.histInvestigations,
      // Labs and Radiology. A row whose type the upstream did not state stays HERE rather than being hidden:
      // an unknown kind is still an investigation the doctor ordered.
      filter: (r) => r.orderType !== "Procedure",
    },
    {
      value: "procedures", key: "investigations", label: S.histProcedures,
      // ONLY an explicit Procedure. Absence never qualifies — see the note on `orderType` in the contract.
      filter: (r) => r.orderType === "Procedure",
    },
    { value: "prescriptions", key: "prescriptions", label: S.histPrescriptions },
  ];

  return (
    <AsyncSection state={state} emptyLabel={S.historyEmpty}>
      {(profile) => (
        <Tabs
          aria-label={t(S.tabHistory)}
          value={tab}
          onValueChange={setTab}
          items={panes.map((p) => {
            const found = profile.sections.find((s) => s.key === p.key);
            // Narrow the ROWS inside `data`, keeping the section's own shape — key, state, reasonCode and
            // requestAccessAction all survive untouched, so SectionView renders restricted results, the
            // request-access action and the three states exactly as it does for the unfiltered pane. Filtering
            // the SECTION rather than its rows would have dropped the gate along with them.
            const rows = (found?.data as { items?: unknown[] } | undefined)?.items;
            const section = found && p.filter && Array.isArray(rows)
              ? {
                  ...found,
                  data: {
                    ...(found.data as Record<string, unknown>),
                    items: rows.filter((r) => p.filter!(r as { orderType?: string })),
                  },
                }
              : found;
            return {
              value: p.value,
              label: t(p.label),
              content: section
                ? <SectionView section={section} beneficiaryId={beneficiaryId} />
                // The server did not return this section at all, which is not the same as returning it
                // empty — and neither is the same as a withheld one, which SectionView renders itself.
                : <p className="muted">{t(S.historyEmpty)}</p>,
            };
          })}
        />
      )}
    </AsyncSection>
  );
}

// ---------------------------------------------------------------- write actions


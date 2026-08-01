import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  Button,
  Card,
  InlineAlert,
  InputField,
  Combobox,
  Icon,
  SegmentedControl,
  StatusChip,
  TextareaField,
} from "@mersal/design-system";
import { useWrite } from "../api/useWrite";
import type { ComboboxOption } from "@mersal/design-system";
import type { Localized, RegisterBeneficiaryInput } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { PageHeader, useLoc } from "./_shared";
import { NATIONALITIES, DIAL_CODES } from "../data/nationalities";
import { flagUrl } from "../data/flags";
import { useRegistrationReference } from "./useRegistrationReference";
import { BatchIntake } from "./BatchIntake";
import { createHttpPolicyApi } from "../api/policyApi";
import type { PolicyApi } from "../api/policyApi";

/** ONE client for the module, not one per render: a default parameter re-evaluates on every call, and the
 *  reference hook keys its load effect on the api instance — a fresh instance per render turns the first
 *  fetch into an unbounded request loop (QA P0-1: ~400 req/s). */
const policyApiForRegistration = createHttpPolicyApi();

const S = {
  manageTitle: { en: "Search / Manage", ar: "بحث / إدارة" },
  statusTitle: { en: "Status & Reactivation", ar: "الحالة وإعادة التفعيل" },
  registerTitle: { en: "Register New", ar: "تسجيل جديد" },
  searchField: { en: "Search by name", ar: "ابحث بالاسم" },
  search: { en: "Search", ar: "بحث" },
  idle: { en: "Search for a beneficiary by name.", ar: "ابحث عن مستفيد بالاسم." },
  manageIntro: {
    en: "Find a beneficiary and open their record. Registration state changes live in Status & Reactivation.",
    ar: "ابحث عن مستفيد وافتح سجلّه. تغييرات حالة التسجيل في «الحالة وإعادة التفعيل».",
  },
  statusIntro: {
    en: "Find a beneficiary, then apply a lifecycle change — reinstate, suspend, renew or deactivate. Every change is recorded with its reason.",
    ar: "ابحث عن مستفيد ثم طبّق تغييرًا في دورة الحياة — إعادة تفعيل، إيقاف، تجديد أو إلغاء تفعيل. يُسجَّل كل تغيير مع سببه.",
  },
  open: { en: "Open", ar: "فتح" },
  none: { en: "No beneficiaries match that search.", ar: "لا يوجد مستفيدون مطابقون." },
  retry: { en: "Retry", ar: "إعادة المحاولة" },
  name: { en: "Name", ar: "الاسم" },
  memberNo: { en: "Member no.", ar: "رقم العضوية" },
  identifier: { en: "Identifier", ar: "المعرّف" },
  status: { en: "Status", ar: "الحالة" },
  action: { en: "Action", ar: "إجراء" },
  changeStatus: { en: "Change status", ar: "تغيير الحالة" },
  changeStatusFor: { en: "Change status", ar: "تغيير الحالة" },
  newStatus: { en: "New status", ar: "الحالة الجديدة" },
  reason: { en: "Reason", ar: "السبب" },
  reasonRequired: { en: "A reason is required for this change — it is recorded and reviewed.", ar: "السبب مطلوب لهذا التغيير — يُسجَّل ويُراجَع." },
  confirm: { en: "Confirm", ar: "تأكيد" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  changed: { en: "Status updated.", ar: "تم تحديث الحالة." },
  // Blocked is the fraud state: the desk can neither place nor lift it (23 §1 — director's case review).
  blockedLocked: {
    en: "Blocked records are changed only by a Medical Director after case review.",
    ar: "السجلات المحظورة لا يغيّرها إلا المدير الطبي بعد مراجعة الحالة.",
  },
  // Action labels named for the OPERATION, not the target state — "Reinstate" tells the operator what they
  // are doing to the person; "Active" only tells them a database value.
  activate: { en: "Activate", ar: "تفعيل" },
  suspend: { en: "Suspend", ar: "إيقاف" },
  reinstate: { en: "Reinstate", ar: "إعادة تفعيل" },
  renew: { en: "Renew", ar: "تجديد" },
  reactivate: { en: "Reactivate", ar: "إعادة تنشيط" },
  deactivate: { en: "Deactivate", ar: "إلغاء التفعيل" },

  givenName: { en: "First name", ar: "الاسم الأول" },
  middleName: { en: "Middle name", ar: "الاسم الأوسط" },
  familyName: { en: "Last name", ar: "اسم العائلة" },
  birthDate: { en: "Birthdate", ar: "تاريخ الميلاد" },
  birthDateInvalid: { en: "Enter a real date, not in the future.", ar: "أدخل تاريخًا صحيحًا، وليس في المستقبل." },
  idType: { en: "Identifier type", ar: "نوع المعرّف" },
  idValue: { en: "Identifier value", ar: "قيمة المعرّف" },
  phone: { en: "Phone no.", ar: "رقم الهاتف" },
  register: { en: "Register beneficiary", ar: "تسجيل المستفيد" },
  registered: { en: "Registered (Pending).", ar: "تم التسجيل (قيد الانتظار)." },
  registeredId: { en: "Record", ar: "السجل" },
  openProfile: { en: "Open profile", ar: "فتح الملف" },
  toEligibility: { en: "Check eligibility", ar: "التحقق من الأهلية" },
  fixMarked: { en: "Fix the marked fields to continue.", ar: "صحّح الحقول المحدّدة للمتابعة." },
  required: { en: "Required.", ar: "مطلوب." },
  nameInvalid: { en: "Names can contain letters, spaces, hyphens, apostrophes and periods only.", ar: "الأسماء تحتوي على حروف ومسافات وشرطات وفواصل عليا ونقاط فقط." },
  phoneInvalid: { en: "Enter 8–15 digits, with an optional leading + (e.g. +201234567890).", ar: "أدخل ٨–١٥ رقمًا، مع + اختيارية في البداية (مثال: +201234567890)." },
  // The one 409 with a happy path: the person exists. Reloading the form (the generic conflict guidance)
  // would lead the operator to re-type and re-submit — manufacturing the duplicate record the identifier
  // check exists to prevent. The remedy is the search screen.
  alreadyRegistered: {
    en: "This identifier is already registered. Open Search / Manage to find the existing record — registering again would create a duplicate.",
    ar: "هذا المعرّف مسجَّل بالفعل. افتح «بحث / إدارة» للعثور على السجل الموجود — التسجيل مرة أخرى سينشئ سجلًا مكررًا.",
  },
  // A DIFFERENT remedy from the identifier clash, which is why it is a different message. A card conflict is
  // usually a mis-read or a card re-issued without the old one being retired — telling the operator to open
  // the existing record would be wrong advice if this is a genuinely different person.
  cardTaken: {
    en: "This card number is already held by another beneficiary. Check the number — if the card was re-issued, retire the old record first.",
    ar: "رقم البطاقة هذا مسجَّل لمستفيد آخر. تحقّق من الرقم — وإذا أُعيد إصدار البطاقة، فأنهِ السجل القديم أولًا.",
  },

  // ---- Sections -------------------------------------------------------------------------------------
  secIdentity: { en: "Identity", ar: "الهوية" },
  secPersonal: { en: "Personal details", ar: "البيانات الشخصية" },
  secContact: { en: "Contact", ar: "بيانات الاتصال" },
  secCoverage: { en: "Coverage", ar: "التغطية" },
  secReferences: { en: "Programme references", ar: "المراجع البرنامجية" },
  secNotes: { en: "Notes", ar: "الملاحظات" },
  secDocuments: { en: "Documents", ar: "المستندات" },

  cardNumber: { en: "Card number", ar: "رقم البطاقة" },
  // "Usually starts with #" told the operator about a character and nothing about the thing. The card
  // number is the key the whole record dedupes on, so the hint says where to read it from and that the #
  // is optional — which is the question the old wording actually raised.
  cardHelp: { en: "The number printed on the beneficiary's card. A leading # is optional.", ar: "الرقم المطبوع على بطاقة المستفيد. علامة # في البداية اختيارية." },
  cardInvalid: { en: "Letters, digits, hyphen and slash only.", ar: "حروف وأرقام وشرطة وشرطة مائلة فقط." },
  statusPending: { en: "Pending", ar: "قيد الانتظار" },
  statusLocked: {
    en: "Every registration starts as Pending. Activating a member is a supervisor's decision, taken once the documents are verified — you can change status later in Status & Reactivation.",
    ar: "يبدأ كل تسجيل بحالة «قيد الانتظار». تفعيل العضو قرار المشرف بعد التحقق من المستندات — ويمكن تغيير الحالة لاحقًا من «الحالة وإعادة التفعيل».",
  },
  gender: { en: "Gender", ar: "النوع" },
  nationality: { en: "Nationality", ar: "الجنسية" },
  age: { en: "Age", ar: "العمر" },
  ageYears: { en: "years", ar: "سنة" },
  ageDerived: {
    en: "Calculated from the birthdate — never stored, so it can never go stale.",
    ar: "يُحتسب من تاريخ الميلاد — ولا يُخزَّن، فلا يصبح قديمًا.",
  },
  ageUnknown: { en: "—", ar: "—" },
  approximate: { en: "Approximate date", ar: "تاريخ تقريبي" },
  dialCode: { en: "Country code", ar: "رمز الدولة" },
  phoneNumber: { en: "Number", ar: "الرقم" },
  plan: { en: "Plan", ar: "الخطة" },
  networkTier: { en: "Network tier", ar: "شريحة الشبكة" },
  contribution: { en: "Contribution", ar: "المشاركة" },
  contributionHelp: { en: "The member's share of the service price, as a %.", ar: "نسبة مشاركة العضو في تكلفة الخدمة (٪)." },
  contributionInvalid: { en: "Enter a percentage between 0 and 100.", ar: "أدخل نسبة بين ٠ و ١٠٠." },
  defaultBranch: { en: "Default branch", ar: "الفرع الافتراضي" },
  branchHelp: { en: "The internal clinic this member is normally seen at.", ar: "العيادة الداخلية التي يتابَع بها العضو عادة." },
  choose: { en: "Choose…", ar: "اختر…" },
  noBranch: { en: "No default branch", ar: "بدون فرع افتراضي" },
  individualNo: { en: "Individual no.", ar: "الرقم الفردي" },
  caseNo: { en: "Case no.", ar: "رقم الحالة" },
  coverageIntro: {
    en: "What this person is being registered onto. Coverage is created when a supervisor approves the registration.",
    ar: "التغطية التي يُسجَّل عليها هذا الشخص. تُنشأ التغطية عند اعتماد المشرف للتسجيل.",
  },
  notesIntro: {
    en: "Standing notes carried on the member's file. The diagnosis and insulin notes are clinical — they are recorded here and shown only to clinical roles.",
    ar: "ملاحظات دائمة في ملف العضو. ملاحظتا التشخيص والأنسولين سريريتان — تُسجَّلان هنا وتظهران للأدوار السريرية فقط.",
  },
  note1: { en: "Known diagnosis", ar: "التشخيص المعروف" },
  note2: { en: "Forecasted case cost", ar: "التكلفة المتوقعة للحالة" },
  note3: { en: "Insulin patient", ar: "مريض أنسولين" },
  note4: { en: "Most visited speciality", ar: "التخصص الأكثر زيارة" },
  note5: { en: "Note 5", ar: "ملاحظة ٥" },
  note6: { en: "Note 6", ar: "ملاحظة ٦" },
  clinicalNote: { en: "Clinical", ar: "سريري" },
  notesOptional: { en: "Optional", ar: "اختياري" },
  documentsAfter: {
    en: "Documents are filed against the member once the record exists. Register the person first — the next step takes you straight there.",
    ar: "تُرفق المستندات بالعضو بعد إنشاء السجل. سجّل الشخص أولًا — والخطوة التالية تنقلك إلى هناك مباشرة.",
  },
  modeOne: { en: "One member", ar: "عضو واحد" },
  modeMany: { en: "Many from a file", ar: "عدة أعضاء من ملف" },
  genderMale: { en: "Male", ar: "ذكر" },
  genderFemale: { en: "Female", ar: "أنثى" },
  genderOther: { en: "Other", ar: "آخر" },
  genderUnknown: { en: "Unknown", ar: "غير معروف" },
} satisfies Record<string, Localized>;

const ID_TYPES = ["NationalID", "Passport", "RefugeeID", "UNHCRNo"] as const;

/** The shape hint shown when an identifier value fails its type's format — the rule, not just "invalid". */
const ID_INVALID: Record<(typeof ID_TYPES)[number], Localized> = {
  NationalID: { en: "An Egyptian National ID is exactly 14 digits.", ar: "الرقم القومي المصري ١٤ رقمًا بالضبط." },
  Passport: { en: "A passport number is 5–20 letters and digits.", ar: "رقم جواز السفر ٥–٢٠ حرفًا ورقمًا." },
  RefugeeID: { en: "A refugee ID is 4–30 letters, digits or dashes.", ar: "بطاقة اللاجئ ٤–٣٠ حرفًا أو رقمًا أو شرطة." },
  UNHCRNo: { en: "A UNHCR number is 6–20 letters, digits or dashes.", ar: "رقم المفوضية ٦–٢٠ حرفًا أو رقمًا أو شرطة." },
};

const ID_TYPE_LABELS: Record<(typeof ID_TYPES)[number], Localized> = {
  NationalID: { en: "National ID", ar: "الرقم القومي" },
  Passport: { en: "Passport", ar: "جواز سفر" },
  RefugeeID: { en: "Refugee ID", ar: "بطاقة لاجئ" },
  UNHCRNo: { en: "UNHCR number", ar: "رقم المفوضية (UNHCR)" },
};

/*
 * Search / Manage and Status & Reactivation lived here and are GONE (19.7 nav rework).
 *
 * Search / Manage was a second, weaker search over the registry that the Beneficiaries list already
 * searches — two screens answering one question is two places for the answers to differ.
 *
 * Status & Reactivation was a screen whose whole job was: find a person you were usually just looking at,
 * then press one button. It is an action on a record, not a place. It now opens from the beneficiary's
 * detail as `Status change`, next to Change plan — see `BeneficiaryStatusDialog.tsx`, which holds the
 * transition table and the dialog unchanged.
 */


/**
 * Register new — the operational registration record (US-001).
 *
 * ============================================================================================================
 * WHY IT IS SECTIONED
 * ============================================================================================================
 * The previous form was six fields in one flat grid. This one carries twenty-two, and twenty-two inputs in an
 * undifferentiated grid is not a form an operator reads — it is one they scan for the next empty box. The
 * sections follow the order the desk actually works in: who is this (the card in their hand, then their
 * name), what are they (personal details), how do we reach them, what are they being put on, which case file
 * do they belong to, and what standing facts should follow them.
 *
 * They are real <fieldset>/<legend> pairs, so the grouping exists in the accessibility tree and not only in
 * the paint — a screen-reader user hears "Coverage, Plan" rather than "Plan".
 *
 * ============================================================================================================
 * TWO FIELDS THAT ARE NOT INPUTS
 * ============================================================================================================
 * STATUS is shown and locked. Every registration starts Pending; activation is a supervisor's decision taken
 * once the documents are verified (23 §1). Rendering an editable Active/Suspended/Closed control here would
 * offer the officer a choice the server refuses — and quietly invite them around the separation of duties the
 * approval endpoint exists to enforce. Shown rather than hidden, because "what state will this person be in"
 * is a real question and the answer is worth stating.
 *
 * AGE is derived and read-only. A stored age is wrong the day after it is written; the birthdate is the only
 * lasting fact, and every reader computes from it through one shared function.
 */
export function BeneficiaryRegister({ policyApi = policyApiForRegistration }: { policyApi?: PolicyApi } = {}) {
  const t = useLoc();
  const [mode, setMode] = useState("one");

  return (
    <>
      <PageHeader title={t(S.registerTitle)} />
      {/* Hundreds of members arrive from UNHCR at a time; typing them one at a time is not a workflow. The
          file path runs the same upload → validate → commit pipeline as Bulk & Imports, so the guarantee that
          nothing is applied until commit is the same guarantee. */}
      <div className="ben-mode">
        <SegmentedControl
          aria-label={t(S.registerTitle)}
          value={mode}
          onChange={setMode}
          segments={[
            { value: "one", label: t(S.modeOne) },
            { value: "many", label: t(S.modeMany) },
          ]}
        />
      </div>
      {mode === "one" ? <RegisterOneMember policyApi={policyApi} /> : <BatchIntake api={policyApi} />}
    </>
  );
}


/**
 * A combobox wearing `InputField`'s anatomy — label above, control, error below, all inside `.mrs-field`.
 *
 * The form mixes typed fields and chosen ones in one grid, and they only line up if every cell has the same
 * structure. Hand-rolling the label beside each control is what produced rows whose baselines disagreed by a
 * few pixels: enough to look untidy, and enough that the eye stops trusting the alignment as a grouping cue.
 */

/** A country flag. Decorative — every option that carries one is also named and searchable by name and code —
 *  so it is `alt=""` and hidden from assistive tech rather than announced as "Syria flag" beside "Syria". */
function Flag({ code }: { code: string }) {
  const src = flagUrl(code);
  return src ? <img className="mrs-flag" src={src} alt="" aria-hidden width={20} height={15} /> : null;
}

function ComboField({
  id, label, options, value, onChange, required, error, help, placeholder, disabled,
}: {
  id: string;
  label: string;
  options: ComboboxOption[];
  value: string | null;
  onChange: (v: string) => void;
  required?: boolean;
  error?: string;
  help?: string;
  placeholder?: string;
  disabled?: boolean;
}) {
  const labelId = `${id}-label`;
  return (
    <div className="mrs-field">
      <label className="mrs-label" id={labelId} htmlFor={id}>
        {label}
        {/* ONE mark, from one place. InputField renders its own from `required`; this mirrors that markup so
            a typed field and a chosen one are indistinguishable in the grid. */}
        {required && <span className="mrs-req" aria-hidden="true"> *</span>}
      </label>
      <Combobox
        id={id}
        aria-labelledby={labelId}
        /* Both, joined — the ternary announced the help text INSTEAD of the error whenever a field had
           both, which is exactly when the error matters most. `Labelled` in the design system joins them;
           this is the same contract, not a second one. */
        aria-describedby={[help && `${id}-help`, error && `${id}-err`].filter(Boolean).join(" ") || undefined}
        options={options}
        value={value}
        onChange={onChange}
        placeholder={placeholder}
        disabled={disabled}
        invalid={Boolean(error)}
      />
      {help && <div className="mrs-help" id={`${id}-help`}>{help}</div>}
      {error && (
        /* The icon is not decoration: without it a chosen field's error is red text and nothing else, so a
           reader who cannot see red sees an ordinary caption. Every typed field on this form already
           renders it (design-system `Labelled`); the chosen ones were the only ones that did not, which is
           why "Required." looked like a hint and "✕ Enter a real date" looked like a failure. */
        <div className="mrs-error" id={`${id}-err`} role="alert">
          <Icon name="cross" />
          <span>{error}</span>
        </div>
      )}
    </div>
  );
}

const GENDERS = ["Male", "Female", "Other", "Unknown"] as const;
const GENDER_LABELS: Record<(typeof GENDERS)[number], Localized> = {
  Male: S.genderMale, Female: S.genderFemale, Other: S.genderOther, Unknown: S.genderUnknown,
};

/** The six standing note slots, in order, with the label fixed by the slot. */
const NOTE_SLOTS: ReadonlyArray<{ slot: 1 | 2 | 3 | 4 | 5 | 6; label: Localized; clinical?: boolean }> = [
  { slot: 1, label: S.note1, clinical: true },
  { slot: 2, label: S.note2 },
  { slot: 3, label: S.note3, clinical: true },
  { slot: 4, label: S.note4 },
  { slot: 5, label: S.note5 },
  { slot: 6, label: S.note6 },
];

const EMPTY = {
  cardNumber: "", givenName: "", middleName: "", familyName: "",
  sex: "", nationalityCode: "", birthDate: "", approximate: false,
  dialCode: "+20", phoneNumber: "",
  planId: "", networkTierId: "", contribution: "", defaultBranchId: "",
  individualNo: "", caseNo: "",
  idType: "NationalID", idValue: "",
  notes: { 1: "", 2: "", 3: "", 4: "", 5: "", 6: "" } as Record<number, string>,
};

function RegisterOneMember({ policyApi }: { policyApi: PolicyApi }) {
  const api = useApi();
  const t = useLoc();
  const navigate = useNavigate();
  const write = useWrite();                       // 18.D1 — per-form idempotency key + typed failures
  const reference = useRegistrationReference(policyApi);

  const [f, setF] = useState(EMPTY);
  const [status, setStatus] = useState<"idle" | "saving" | "done">("idle");
  const [created, setCreated] = useState<{ id: string } | null>(null);
  const [touched, setTouched] = useState(false);

  // The value is read BEFORE the functional updater: React nulls `currentTarget` once the handler returns,
  // and the updater can run after that (re-render rebasing) — the old screen carried this crash latently.
  const set = (k: keyof typeof EMPTY) => (e: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => {
    const value = e.currentTarget.value;
    setF((s) => ({ ...s, [k]: value }));
  };
  const setValue = (k: keyof typeof EMPTY) => (value: string) => setF((s) => ({ ...s, [k]: value }));
  const setNote = (slot: number) => (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const value = e.currentTarget.value;
    setF((s) => ({ ...s, notes: { ...s.notes, [slot]: value } }));
  };

  const idType = f.idType as (typeof ID_TYPES)[number];
  const phone = `${f.dialCode}${f.phoneNumber.replace(/\D/g, "")}`;
  const filledNotes = NOTE_SLOTS.filter((n) => f.notes[n.slot]?.trim()).length;

  // ---- Validity, per field. The operator fixes what is MARKED; there is no banner naming everything at once.
  const nameOk = (v: string) => v.trim() !== "" && NAME_PATTERN.test(v.trim());
  const invalidField = (key: string): boolean => {
    switch (key) {
      case "cardNumber": return f.cardNumber.trim() === "" || !CARD_PATTERN.test(normalizeCard(f.cardNumber));
      case "givenName": return !nameOk(f.givenName);
      case "familyName": return !nameOk(f.familyName);
      case "middleName": return f.middleName.trim() !== "" && !NAME_PATTERN.test(f.middleName.trim());
      case "sex": return f.sex === "";
      case "nationalityCode": return f.nationalityCode === "";
      case "birthDate": return f.birthDate.trim() === "" || !isRealPastDate(f.birthDate.trim());
      case "phoneNumber": return !isValidPhone(phone);
      case "planId": return f.planId === "";
      case "networkTierId": return f.networkTierId === "";
      case "contribution": {
        const n = Number(f.contribution);
        return f.contribution.trim() === "" || Number.isNaN(n) || n < 0 || n > 100;
      }
      case "idValue": return f.idValue.trim() === "" || !ID_PATTERNS[idType].test(f.idValue.trim().replace(/\s/g, ""));
      default: return false;
    }
  };
  const REQUIRED_ORDER = [
    ["reg-card", "cardNumber", S.cardNumber], ["reg-given", "givenName", S.givenName],
    ["reg-middle", "middleName", S.middleName], ["reg-family", "familyName", S.familyName],
    ["reg-sex", "sex", S.gender], ["reg-nationality", "nationalityCode", S.nationality],
    ["reg-birth", "birthDate", S.birthDate], ["reg-phone", "phoneNumber", S.phone],
    ["reg-plan", "planId", S.plan], ["reg-tier", "networkTierId", S.networkTier],
    ["reg-contribution", "contribution", S.contribution], ["reg-id-value", "idValue", S.idValue],
  ] as const;
  const invalid = () => REQUIRED_ORDER.some(([, key]) => invalidField(key));

  /**
   * Is the field invalid only because nothing was typed in it?
   *
   * Every typed field failed with its FORMAT rule regardless of why it failed, so an operator who pressed
   * Register on an empty form was told "Names can contain letters, spaces, hyphens, apostrophes and periods
   * only" under three blank name boxes, and "Enter 8–15 digits, with an optional leading +" under a blank
   * phone. That is an answer to a question nobody asked: the field is not wrong, it is missing. Worse, the
   * chosen fields beside them said "Required." — so the same failure had two different explanations
   * depending on whether the control happened to be a text box or a droplist.
   *
   * The format rule is still exactly right once something HAS been typed, which is the case it was written
   * for. This only decides which of the two sentences applies.
   */
  const isMissing = (key: string): boolean => {
    switch (key) {
      case "cardNumber": return f.cardNumber.trim() === "";
      case "givenName": return f.givenName.trim() === "";
      case "familyName": return f.familyName.trim() === "";
      case "birthDate": return f.birthDate.trim() === "";
      case "phoneNumber": return f.phoneNumber.trim() === "";
      case "contribution": return f.contribution.trim() === "";
      case "idValue": return f.idValue.trim() === "";
      // `middleName` is optional: blank is valid, so it never reaches here.
      default: return false;
    }
  };
  const err = (key: string, message: Localized) =>
    touched && invalidField(key) ? t(isMissing(key) ? S.required : message) : undefined;

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setTouched(true);
    if (invalid()) {
      // QA P2-14: inline errors painted and NOTHING moved — a keyboard or screen-reader user got no signal
      // the submit was refused. Focus lands on the first invalid control (its error is tied via
      // aria-describedby, so it is announced) and a summary renders in the live region below.
      const first = REQUIRED_ORDER.find(([, key]) => invalidField(key));
      if (first) document.getElementById(first[0])?.focus();
      return;
    }
    setStatus("saving");
    const input: RegisterBeneficiaryInput = {
      cardNumber: normalizeCard(f.cardNumber),
      givenName: f.givenName.trim(),
      middleName: f.middleName.trim() || undefined,
      familyName: f.familyName.trim(),
      birthDate: f.birthDate.trim(),
      approximateBirthDate: f.approximate,
      sex: f.sex as RegisterBeneficiaryInput["sex"],
      nationalityCode: f.nationalityCode,
      identifierType: f.idType as RegisterBeneficiaryInput["identifierType"],
      identifierValue: f.idValue.trim(),
      phone,
      individualNo: f.individualNo.trim() || undefined,
      caseNo: f.caseNo.trim() || undefined,
      enrolment: {
        planId: f.planId,
        networkTierId: f.networkTierId,
        contributionPercent: Number(f.contribution),
        defaultBranchId: f.defaultBranchId || undefined,
      },
      // Only slots the operator actually filled. A blank slot and a cleared one read identically later, and
      // storing empties makes "is the diagnosis on file" unanswerable.
      notes: NOTE_SLOTS
        .filter((n) => f.notes[n.slot]?.trim())
        .map((n) => ({ slot: n.slot, value: f.notes[n.slot].trim() })),
    };

    // 18.D1 (U1): registering failed silently. The operator saw the spinner stop with the form still full,
    // retried, and created a second record for the same person — which then has to be merged.
    let result: { id: string } | null = null;
    const ok = await write.run(async (key) => (result = await api.registerBeneficiary(input, key)));
    if (ok) {
      setStatus("done");
      setCreated(result);
      setTouched(false);
      // Cleared ONLY on confirmed success: wiping a form after a failure destroys the operator's typing.
      setF(EMPTY);
    } else {
      setStatus("idle");
    }
  }

  const isDuplicateId = write.error?.problemType === "urn:hbmp:duplicate-identifier";
  const isDuplicateCard = write.error?.problemType === "urn:hbmp:duplicate-card-number";

  const planOptions: ComboboxOption[] = useMemo(
    () => reference.plans.map((p) => ({ value: p.planId, label: p.nameEn, hint: p.planCode, keywords: p.planCode })),
    [reference.plans],
  );
  const tierOptions: ComboboxOption[] = useMemo(
    () => reference.tiers.map((x) => ({ value: x.networkTierId, label: x.nameEn, hint: x.tierCode, keywords: x.tierCode })),
    [reference.tiers],
  );
  const branchOptions: ComboboxOption[] = useMemo(
    () => [{ value: "", label: t(S.noBranch) }, ...reference.branches.map((b) => ({ value: b.branchId, label: b.nameEn }))],
    [reference.branches, t],
  );
  // The flag is the fastest way to confirm the right country in a list of a hundred — but it is decoration
  // only: the option is named, and `keywords` lets the code be typed as well as the name, so a country whose
  // asset is missing is a label with nothing beside it rather than an unusable row.
  const nationalityOptions: ComboboxOption[] = useMemo(
    () => NATIONALITIES.map((n) => ({
      value: n.code, label: t(n.label), hint: n.code, keywords: `${n.code} ${n.label.en} ${n.label.ar}`,
      leading: <Flag code={n.code} />,
    })),
    [t],
  );
  const dialOptions: ComboboxOption[] = useMemo(
    () => DIAL_CODES.map((d) => ({
      value: d.code, label: d.code, hint: d.country, keywords: d.country, leading: <Flag code={d.country} />,
    })),
    [],
  );

  return (
    <Card as="section" style={{ padding: "var(--sp5)" }}>
      {/* Named up front rather than discovered as three empty droplists after the whole person is typed in. */}
      {reference.unavailable && <InlineAlert tone="bad">{t(reference.unavailable)}</InlineAlert>}

      {/* noValidate: the inputs carry `required` for assistive tech and autofill, but the browser's native
          bubbles must not pre-empt our submit handler — the app renders its own field errors, summary and
          focus management, in both languages. */}
      <form onSubmit={submit} noValidate className="ben-form" aria-label={t(S.registerTitle)}>

        {/*
          * The error summary, at the TOP of the form.
          *
          * It used to be the "fix the marked fields" line in the live region beside the Register button —
          * correct for a screen reader, and around 1300px below the first error for everyone else (audit
          * §5.10). This form is seven sections tall; a refusal reported only at the bottom leaves a sighted
          * operator scrolling a long page hunting for red.
          *
          * It NAMES the fields rather than saying how many, and each name is a link to its control, so the
          * summary is also the way back to the work. Focus still moves to the first invalid field on submit
          * (that is the faster path for a keyboard user, and it is where the field-level message is); this is
          * the map for everyone who then scrolls away from it.
          */}
        {touched && status === "idle" && !write.error && invalid() && (
          <div className="ben-errorsummary" role="alert" tabIndex={-1} data-testid="register-error-summary">
            <InlineAlert tone="bad">{t(S.fixMarked)}</InlineAlert>
            <ul>
              {REQUIRED_ORDER.filter(([, key]) => invalidField(key)).map(([id, key, label]) => (
                <li key={key}>
                  <a
                    href={`#${id}`}
                    onClick={(e) => { e.preventDefault(); document.getElementById(id)?.focus(); }}
                  >
                    {t(label)}
                  </a>
                </li>
              ))}
            </ul>
          </div>
        )}

        {/* ---- 1 · Identity ------------------------------------------------------------------------- */}
        <fieldset className="ben-section">
          <legend>{t(S.secIdentity)}</legend>
          <div className="ben-grid">
            <InputField
              id="reg-card" name="cardNumber" required
              label={t(S.cardNumber)} help={t(S.cardHelp)}
              value={f.cardNumber} error={err("cardNumber", S.cardInvalid)}
              onChange={set("cardNumber")} autoComplete="off"
            />
            <InputField
              id="reg-given" name="givenName" required label={t(S.givenName)}
              value={f.givenName} error={err("givenName", S.nameInvalid)}
              onChange={set("givenName")} autoComplete="given-name"
            />
            <InputField
              id="reg-middle" name="middleName" label={t(S.middleName)}
              value={f.middleName} error={err("middleName", S.nameInvalid)}
              onChange={set("middleName")} autoComplete="additional-name"
            />
            <InputField
              id="reg-family" name="familyName" required label={t(S.familyName)}
              value={f.familyName} error={err("familyName", S.nameInvalid)}
              onChange={set("familyName")} autoComplete="family-name"
            />
          </div>
        </fieldset>

        {/* ---- 2 · Personal ------------------------------------------------------------------------- */}
        <fieldset className="ben-section">
          <legend>{t(S.secPersonal)}</legend>
          <div className="ben-grid">
            <ComboField
              id="reg-sex" required label={t(S.gender)} placeholder={t(S.choose)}
              options={GENDERS.map((g) => ({ value: g, label: t(GENDER_LABELS[g]) }))}
              value={f.sex || null} onChange={setValue("sex")}
              error={touched && invalidField("sex") ? t(S.required) : undefined}
            />

            <ComboField
              id="reg-nationality" required label={t(S.nationality)} placeholder={t(S.choose)}
              options={nationalityOptions}
              value={f.nationalityCode || null} onChange={setValue("nationalityCode")}
              error={touched && invalidField("nationalityCode") ? t(S.required) : undefined}
            />

            {/* A real picker, which the old form deliberately avoided because staff transcribe partial dates
                from heterogeneous refugee documents and a native picker refuses what it cannot parse. The
                trade-off is resolved rather than re-made: the picker is here AND the approximate flag below
                gives the estimated date somewhere to go, so the easy case is easy and the hard one is not a
                dead end. */}
            <InputField
              id="reg-birth" name="birthDate" required type="date" label={t(S.birthDate)}
              value={f.birthDate} error={err("birthDate", S.birthDateInvalid)}
              onChange={set("birthDate")} autoComplete="bday"
              max={new Date().toISOString().slice(0, 10)}
            />

            {/* Qualifies the birthdate, so it sits on the CONTROL row beside it rather than up on the label
                row. The empty label is what puts it there: every sibling cell reserves a label box, and an
                EMPTY one of the same class reserves exactly the same height — so the two stay aligned if the
                label metrics ever change, which a hard-coded offset would not. Presentational only; the
                checkbox is named by its own <label>. */}
            <div className="mrs-field ben-checkbox-cell">
              <span className="mrs-label" aria-hidden="true" />
              <label className="ben-checkbox">
                <input
                  type="checkbox" className="mrs-checkbox" checked={f.approximate}
                  onChange={(e) => { const v = e.currentTarget.checked; setF((s) => ({ ...s, approximate: v })); }}
                />
                <span>{t(S.approximate)}</span>
              </label>
            </div>
          </div>
        </fieldset>

        {/* ---- 3 · Contact -------------------------------------------------------------------------- */}
        <fieldset className="ben-section">
          <legend>{t(S.secContact)}</legend>
          <div className="ben-grid">
            <div className="ben-phone ben-span-2">
              <ComboField
                id="reg-dial" label={t(S.dialCode)}
                options={dialOptions}
                value={f.dialCode} onChange={setValue("dialCode")}
              />
              <InputField
                id="reg-phone" name="phone" required inputMode="tel" label={t(S.phoneNumber)}
                value={f.phoneNumber} error={err("phoneNumber", S.phoneInvalid)}
                onChange={set("phoneNumber")} autoComplete="tel-national"
              />
            </div>

            {/* The identity document. Still required — it is what dedup matches on — but it belongs with the
                other things the person hands over, not above their name as it was. */}
            <ComboField
              id="reg-id-type" label={t(S.idType)}
              options={ID_TYPES.map((v) => ({ value: v, label: t(ID_TYPE_LABELS[v]) }))}
              value={f.idType} onChange={setValue("idType")}
            />
            <InputField
              id="reg-id-value" name="identifierValue" required label={t(S.idValue)}
              value={f.idValue} error={err("idValue", ID_INVALID[idType])}
              onChange={set("idValue")} autoComplete="off"
            />
          </div>
        </fieldset>

        {/* ---- 4 · Coverage ------------------------------------------------------------------------- */}
        <fieldset className="ben-section">
          <legend>{t(S.secCoverage)}</legend>
          <p className="ben-section-hint">{t(S.coverageIntro)}</p>
          <div className="ben-grid">
            <ComboField
              id="reg-plan" required label={t(S.plan)} placeholder={t(S.choose)}
              options={planOptions} value={f.planId || null} onChange={setValue("planId")}
              disabled={reference.loading}
              error={touched && invalidField("planId") ? t(S.required) : undefined}
            />

            <ComboField
              id="reg-tier" required label={t(S.networkTier)} placeholder={t(S.choose)}
              options={tierOptions} value={f.networkTierId || null} onChange={setValue("networkTierId")}
              disabled={reference.loading}
              error={touched && invalidField("networkTierId") ? t(S.required) : undefined}
            />

            <InputField
              id="reg-contribution" name="contribution" required type="number" inputMode="decimal"
              className="ben-w-num" min={0} max={100} step="0.01"
              label={`${t(S.contribution)} (%)`} help={t(S.contributionHelp)}
              value={f.contribution} error={err("contribution", S.contributionInvalid)}
              onChange={set("contribution")}
            />

            <ComboField
              id="reg-branch" label={t(S.defaultBranch)} help={t(S.branchHelp)}
              options={branchOptions} value={f.defaultBranchId || null}
              onChange={setValue("defaultBranchId")} placeholder={t(S.noBranch)}
            />
          </div>
        </fieldset>

        {/* ---- 5 · References ----------------------------------------------------------------------- */}
        <fieldset className="ben-section">
          <legend>{t(S.secReferences)}</legend>
          <div className="ben-grid">
            <InputField
              id="reg-individual" name="individualNo" label={t(S.individualNo)} className="ben-w-code"
              value={f.individualNo} onChange={set("individualNo")} autoComplete="off"
            />
            <InputField
              id="reg-case" name="caseNo" label={t(S.caseNo)} className="ben-w-code"
              value={f.caseNo} onChange={set("caseNo")} autoComplete="off"
            />
          </div>
        </fieldset>

        {/* ---- 6 · Notes (collapsed) ------------------------------------------------------------------
            Six optional prose boxes are the tallest thing on the form and the least often filled, so open by
            default they pushed the submit button below the fold on every registration that did not need them.
            A native <details> gets the disclosure semantics, keyboard behaviour and find-in-page for free —
            and the count in the summary means a collapsed section can still say what is inside it. */}
        <details className="ben-section ben-disclosure" open={filledNotes > 0}>
          <summary>
            <span className="ben-disclosure-title">{t(S.secNotes)}</span>
            <span className="ben-disclosure-count">
              {filledNotes > 0 ? `${filledNotes} / ${NOTE_SLOTS.length}` : t(S.notesOptional)}
            </span>
          </summary>
          <div className="ben-disclosure-body">
            {/* Said plainly, because the officer typing a diagnosis should know it will not be readable back
                to them. The rule is enforced on the server; this is what stops it looking like a bug. */}
            <p className="ben-section-hint">{t(S.notesIntro)}</p>
            <div className="ben-grid ben-grid--notes">
              {NOTE_SLOTS.map((n) => (
                <TextareaField
                  key={n.slot}
                  id={`reg-note-${n.slot}`}
                  label={n.clinical ? `${t(n.label)} · ${t(S.clinicalNote)}` : t(n.label)}
                  value={f.notes[n.slot]}
                  onChange={setNote(n.slot)}
                  rows={2}
                />
              ))}
            </div>
          </div>
        </details>

        {/* ---- 7 · Documents ------------------------------------------------------------------------ */}
        <fieldset className="ben-section">
          <legend>{t(S.secDocuments)}</legend>
          <InlineAlert tone="info">{t(S.documentsAfter)}</InlineAlert>
        </fieldset>

        {/* The refusal moved to the summary at the top of the form; this region carries what happens AFTER a
            submit the client accepted — the server's answer, and the two next steps on success. */}
        <div aria-live="polite" className="stack ben-actions" style={{ gap: "var(--sp3)", minHeight: 32 }}>
          {/* 18.D1 (U2): the server's own reason, translated and typed — a 409 reads differently from a
              dropped connection, because they demand opposite actions. The two duplicate conflicts get
              their own copy: their remedies are different from each other's and from the generic one. */}
          {write.error && (
            <InlineAlert tone="bad">
              {isDuplicateCard ? t(S.cardTaken) : isDuplicateId ? t(S.alreadyRegistered) : t(write.error.message)}
            </InlineAlert>
          )}
          {status === "done" && created && (
            <div style={{ display: "flex", gap: "var(--sp3)", alignItems: "center", flexWrap: "wrap" }}>
              <StatusChip kind="ok" label={t(S.registered)} />
              <span className="muted tnum">{t(S.registeredId)}: {created.id.slice(0, 8)}</span>
              {/* The two next steps ARE the message (QA P2-20). Opening the profile is also where documents
                  are filed, which is why the documents section points here. */}
              <Button variant="ghost" size="sm" onClick={() => navigate(`/patients/${encodeURIComponent(created.id)}`)}>{t(S.openProfile)}</Button>
              <Button variant="ghost" size="sm" onClick={() => navigate("/beneficiaries/eligibility")}>{t(S.toEligibility)}</Button>
            </div>
          )}
          <div>
            <Button type="submit" variant="primary" loading={status === "saving"} disabled={reference.unavailable !== null}>
              {t(S.register)}
            </Button>
          </div>
        </div>
      </form>
    </Card>
  );
}

/**
 * Client mirrors of the SERVER's validation (IdentifierValidation / PersonFieldValidation in
 * patient-service). The server remains authoritative; these exist so the operator hears about a
 * two-character National ID at the field, not as an RFC-7807 round trip.
 */
const ID_PATTERNS: Record<(typeof ID_TYPES)[number], RegExp> = {
  NationalID: /^\d{14}$/,
  Passport: /^[A-Za-z0-9]{5,20}$/,
  RefugeeID: /^[A-Za-z0-9-]{4,30}$/,
  UNHCRNo: /^[A-Za-z0-9-]{6,20}$/,
};
const NAME_PATTERN = /^[\p{L}\p{M}][\p{L}\p{M}'\-. ]*$/u;
const isValidPhone = (v: string) => /^\+?\d{8,15}$/.test(v.replace(/[\s\-()]/g, ""));

/** Mirrors `PersonFieldValidation` in patient-service. The '#' is a convention rather than data, so it is
 *  accepted and stripped rather than demanded — an operator who types it has not made a mistake, and one who
 *  omits it has not either. Normalizing before validation is also what stops "#A-1", "a 1" and "A-1" becoming
 *  three records for one card. */
const CARD_PATTERN = /^[A-Z0-9\-/]{1,40}$/;
const normalizeCard = (v: string) => v.trim().replace(/^#/, "").replace(/\s/g, "").toUpperCase();

/** True for a syntactically valid YYYY-MM-DD naming a real calendar day that is not in the future. */
function isRealPastDate(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const [y, m, d] = value.split("-").map(Number);
  const date = new Date(Date.UTC(y, m - 1, d));
  // Date.UTC rolls invalid days over (Feb 31 → Mar 3), so round-tripping detects them.
  if (date.getUTCFullYear() !== y || date.getUTCMonth() !== m - 1 || date.getUTCDate() !== d) return false;
  return date.getTime() <= Date.now();
}

/*
 * Registration Approvals moved to its own module when it grew the queue controls (search, status filter,
 * sortable columns, pagination), the notes thread and the bulk decision — it is a screen, not a section of
 * this one. Re-exported here so the lazy route and the existing tests keep their import path.
 */
export { RegistrationApprovals } from "./RegistrationApprovals";

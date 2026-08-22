import { useMemo, useState } from "react";
import {
  Button, Card, DataTable, Icon, InlineAlert, InputField, ComboboxField, KpiCard, Modal, SegmentedControl,
  StatusChip, useTheme,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import { availabilityApi, branchApi, rosterApi } from "../api/branchApi";
import type {
  AvailabilityRule, BranchPractitioner, BranchRef, CreateRosterExceptionBody, DayRoster, DayRosterLine,
  RosterException, RosterImpact, RosterKind,
} from "../api/branchApi";
import { useAsync } from "../api/useAsync";
import { useWrite, writeErrorText } from "../api/useWrite";
import { useAuth } from "../auth/AuthProvider";
import { isSetScopedRole } from "../shell/useBranchContext";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";
import { ChangeTimeline } from "./branch/ChangeTimeline";
import type { TimelineEntry } from "./branch/ChangeTimeline";
import type { Localized } from "../portals/catalog";

const S = {
  title: { en: "Roster & Availability", ar: "الجدول والإتاحة" },
  intro: {
    en: "Pick a clinician to see and change the week they normally work. Switch to Today's roster to see one clinic on one day, with the exceptions already applied.",
    ar: "اختر إكلينيكيًا لعرض أسبوع عمله المعتاد وتعديله. وانتقل إلى جدول اليوم لعرض عيادة واحدة في يوم واحد، مع تطبيق الاستثناءات.",
  },

  // ── the two views ─────────────────────────────────────────────────────────────────────────────────────
  viewPattern: { en: "Weekly pattern", ar: "النمط الأسبوعي" },
  viewDay: { en: "Today's roster", ar: "جدول اليوم" },
  viewLabel: { en: "Which view", ar: "أي عرض" },
  /* The filter bar's own accessible name. Deliberately NOT the view's name: the pane below carries that, and
     two regions with one name is a screen reader offering the same landmark twice. */
  scopeRegion: { en: "Filters", ar: "عوامل التصفية" },

  // ── scope ─────────────────────────────────────────────────────────────────────────────────────────────
  branchLabel: { en: "Clinic", ar: "العيادة" },
  allBranches: { en: "All clinics you run", ar: "كل العيادات التي تديرها" },
  clinicianLabel: { en: "Clinician", ar: "الإكلينيكي" },
  clinicianPlaceholder: { en: "Search by name…", ar: "ابحث بالاسم…" },
  noClinician: { en: "Nobody selected", ar: "لم يتم اختيار أحد" },

  // ── the clinician list ────────────────────────────────────────────────────────────────────────────────
  listHeading: { en: "Clinicians", ar: "الإكلينيكيون" },
  listEmpty: {
    en: "No clinicians are assigned to this clinic yet.",
    ar: "لا يوجد إكلينيكيون معينون لهذه العيادة بعد.",
  },
  noMatches: { en: "No clinician matches that search.", ar: "لا يوجد إكلينيكي مطابق لهذا البحث." },
  colClinics: { en: "Clinics", ar: "العيادات" },
  colDays: { en: "Days a week", ar: "أيام أسبوعيًا" },
  colWeekly: { en: "Slots a week", ar: "المواعيد أسبوعيًا" },
  noPatternShort: { en: "No pattern", ar: "بلا نمط" },

  // ── the pattern pane ──────────────────────────────────────────────────────────────────────────────────
  pickSomeone: {
    en: "Choose a clinician to see the week they normally work.",
    ar: "اختر إكلينيكيًا لعرض أسبوع عمله المعتاد.",
  },
  patternHeading: { en: "Weekly pattern", ar: "النمط الأسبوعي" },
  assignedClinics: { en: "Assigned clinics", ar: "العيادات المعينة" },
  noClinics: { en: "None recorded", ar: "لا يوجد" },
  /* "Add", not "Assign", and the key says so too — the house guard on `plus` reads the SOURCE, because a
     glyph meaning add-a-thing beside a verb that is not one is how an icon comes to mean nothing. It also
     reads better under the chips it adds to. */
  addClinic: { en: "Add a clinic", ar: "إضافة عيادة" },
  filterToClinic: { en: "Show only {clinic}", ar: "عرض {clinic} فقط" },
  showingAllClinics: { en: "Showing every clinic", ar: "عرض كل العيادات" },
  notWorking: { en: "Not working", ar: "لا يعمل" },
  addDay: { en: "Add", ar: "إضافة" },
  cannotAddYet: {
    en: "No clinic this clinician is assigned to has a weekly pattern yet. The first one at a clinic is created when its calendar is generated; after that, days can be added here.",
    ar: "لا توجد عيادة معينة لهذا الإكلينيكي لديها نمط أسبوعي بعد. يُنشأ أول نمط في العيادة عند توليد تقويمها، وبعد ذلك يمكن إضافة الأيام من هنا.",
  },
  hoursCol: { en: "Hours", ar: "الساعات" },
  slotLength: { en: "Slot length", ar: "مدة الموعد" },
  cap: { en: "Daily limit", ar: "الحد اليومي" },
  /* CAPACITY, per day — what the hours and the cap between them yield. Deliberately not "slots offered",
     which reads as "slots you can still book" and is a different number: the booking calendar shows what is
     LEFT after what is already taken, so a 18-slot Monday with two bookings shows 16 there and 18 here. */
  offered: { en: "Slots a day", ar: "المواعيد في اليوم" },
  /* The day view's own column keeps "offered", where Booked and Open sit beside it and settle the question. */
  offeredOnTheDay: { en: "Slots offered", ar: "المواعيد المتاحة" },
  noCap: { en: "No limit", ar: "بلا حد" },
  minutes: { en: "min", ar: "دقيقة" },
  capExplains: { en: "of {window} the hours allow", ar: "من {window} تسمح بها الساعات" },
  editPattern: { en: "Edit", ar: "تعديل" },
  historyAction: { en: "History", ar: "السجل" },
  retireAction: { en: "Remove", ar: "إزالة" },

  // ── editing ───────────────────────────────────────────────────────────────────────────────────────────
  editHeading: { en: "Edit the weekly pattern", ar: "تعديل النمط الأسبوعي" },
  addHeadingDay: { en: "Add a working day", ar: "إضافة يوم عمل" },
  startLabel: { en: "Starts", ar: "يبدأ" },
  endLabel: { en: "Ends", ar: "ينتهي" },
  slotMinutesLabel: { en: "Slot length (minutes)", ar: "مدة الموعد (بالدقائق)" },
  capLabel: { en: "Most patients per day", ar: "أقصى عدد مرضى في اليوم" },
  capHelp: {
    en: "Leave empty for no limit. The hours decide how many slots exist; this decides how many are offered. A six-hour day at 15 minutes offers 24 — a clinician who can safely see 20 says 20.",
    ar: "اتركه فارغًا لعدم وضع حد. الساعات تحدد عدد المواعيد الممكنة، وهذا يحدد عدد المعروض منها. يوم من ست ساعات بمواعيد ١٥ دقيقة يتيح ٢٤ موعدًا — والإكلينيكي الذي يمكنه استقبال ٢٠ بأمان يحدد ٢٠.",
  },
  savePattern: { en: "Save pattern", ar: "حفظ النمط" },
  capMustBePositive: {
    en: "A limit of zero would close the clinic silently. Leave it empty for no limit, or record a closure instead.",
    ar: "الحد صفر يغلق العيادة دون إشعار. اتركه فارغًا لعدم وضع حد، أو سجّل إغلاقًا بدلًا من ذلك.",
  },
  historyHeading: { en: "Change history", ar: "سجل التغييرات" },

  retireHeading: { en: "Remove this working day", ar: "إزالة يوم العمل هذا" },
  retireExplains: {
    en: "This removes the day from the weekly pattern, so no new slots are generated for it. It does NOT withdraw slots already generated, or cancel anything booked into them — those need a closure, which carries a reason and an impact preview.",
    ar: "يؤدي هذا إلى إزالة اليوم من النمط الأسبوعي، فلا تُنشأ مواعيد جديدة له. لكنه لا يسحب المواعيد المتاحة التي أُنشئت بالفعل ولا يلغي ما حُجز فيها — فذلك يحتاج إلى إغلاق يحمل سببًا ومعاينة للأثر.",
  },
  retired: { en: "Working day removed.", ar: "تمت إزالة يوم العمل." },

  assignHeading: { en: "Add a clinic", ar: "إضافة عيادة" },
  assignHelp: {
    en: "Adding a clinic lets this clinician be rostered and booked there from the date you choose. It does not move anything already booked elsewhere.",
    ar: "إضافة عيادة تتيح إدراج هذا الإكلينيكي في جدولها وحجز مواعيده فيها اعتبارًا من التاريخ الذي تختاره. ولا ينقل ذلك أي حجز قائم في مكان آخر.",
  },
  assignFrom: { en: "From", ar: "من" },
  assignSaved: { en: "Clinic assigned.", ar: "تم تعيين العيادة." },
  needBranch: { en: "Choose a clinic.", ar: "اختر عيادة." },

  // ── the day view ──────────────────────────────────────────────────────────────────────────────────────
  dateLabel: { en: "Date", ar: "التاريخ" },
  today: { en: "Today", ar: "اليوم" },
  /* The stepper's own name. NOT "Date" — that belongs to the field beside it, and two controls answering to
     one name leaves a screen-reader user unable to say which they are on. */
  stepDay: { en: "Change the date", ar: "تغيير التاريخ" },
  prevDay: { en: "Previous day", ar: "اليوم السابق" },
  nextDay: { en: "Next day", ar: "اليوم التالي" },
  dayHeading: { en: "On duty", ar: "في الخدمة" },
  dayEmpty: {
    en: "Nobody is rostered at this clinic on this date.",
    ar: "لا يوجد أحد مُدرج في جدول هذه العيادة في هذا التاريخ.",
  },
  kpiClinicians: { en: "Clinicians on duty", ar: "إكلينيكيون في الخدمة" },
  kpiOffered: { en: "Slots offered", ar: "المواعيد المتاحة" },
  kpiBooked: { en: "Booked", ar: "محجوزة" },
  kpiOpen: { en: "Still open", ar: "ما زالت متاحة" },
  colStatus: { en: "Status", ar: "الحالة" },
  colBooked: { en: "Booked", ar: "محجوزة" },
  colOpen: { en: "Open", ar: "متاحة" },
  statusWorking: { en: "Working", ar: "يعمل" },
  statusOff: { en: "Off", ar: "غائب" },
  statusExtra: { en: "Extra clinic", ar: "عيادة إضافية" },
  reducedBy: { en: "Shortened — {reason}", ar: "مختصر — {reason}" },
  noticesHeading: { en: "In force on this date", ar: "ساري في هذا التاريخ" },

  // ── exceptions, behind a button ───────────────────────────────────────────────────────────────────────
  exceptionsAction: { en: "Exceptions", ar: "الاستثناءات" },
  exceptionsHeading: { en: "Exceptions to the pattern", ar: "استثناءات النمط" },
  exceptionsDescription: {
    en: "Leave, public holidays, closures and extra clinics — dated departures from the weekly pattern.",
    ar: "الإجازات والعطلات الرسمية والإغلاقات والعيادات الإضافية — انحرافات مؤرخة عن النمط الأسبوعي.",
  },
  whyNotDelete: {
    en: "Adding an exception leaves the weekly pattern intact. Removing a working day to cover one absence removes every other week too.",
    ar: "إضافة استثناء تُبقي النمط الأسبوعي كما هو. أما إزالة يوم عمل لتغطية غياب واحد فتلغي كل الأسابيع الأخرى أيضًا.",
  },
  existing: { en: "Exceptions", ar: "الاستثناءات" },
  noneYet: { en: "No exceptions are recorded for the next 90 days.", ar: "لا توجد استثناءات مسجلة خلال التسعين يومًا القادمة." },
  kind: { en: "Kind", ar: "النوع" },
  dates: { en: "Dates", ar: "التواريخ" },
  hours: { en: "Hours", ar: "الساعات" },
  wholeDay: { en: "Whole day", ar: "يوم كامل" },
  reason: { en: "Reason", ar: "السبب" },
  effect: { en: "Effect", ar: "الأثر" },
  removes: { en: "Removes slots", ar: "يلغي المواعيد المتاحة" },
  adds: { en: "Adds slots", ar: "يضيف مواعيد متاحة" },
  appliesTo: { en: "Applies to", ar: "ينطبق على" },
  wholeClinic: { en: "The whole clinic", ar: "العيادة بأكملها" },
  withdraw: { en: "Withdraw", ar: "سحب" },
  withdrawn: { en: "Exception withdrawn. The days it removed are available again.", ar: "تم سحب الاستثناء. عادت الأيام التي ألغاها متاحة." },
  withdrawExplains: {
    en: "Withdrawing restores the availability this removed. It does NOT un-flag appointments already flagged — you may have rung those patients and moved them already.",
    ar: "السحب يعيد الإتاحة التي أُلغيت. لكنه لا يزيل تعليم المواعيد التي عُلّمت بالفعل — فقد تكون اتصلت بهؤلاء المستفيدين ونقلتهم بالفعل.",
  },

  addHeading: { en: "Record an exception", ar: "تسجيل استثناء" },
  kindLeave: { en: "Leave", ar: "إجازة" },
  kindHoliday: { en: "Public holiday", ar: "عطلة رسمية" },
  kindClosed: { en: "Clinic closed", ar: "إغلاق العيادة" },
  kindAdHoc: { en: "Extra clinic", ar: "عيادة إضافية" },
  from: { en: "From", ar: "من" },
  to: { en: "To", ar: "إلى" },
  startTime: { en: "Start time (optional)", ar: "وقت البدء (اختياري)" },
  endTime: { en: "End time (optional)", ar: "وقت الانتهاء (اختياري)" },
  timeHelp: {
    en: "Leave both blank for a whole day. An extra clinic must state its hours — there is no weekly pattern for it to inherit.",
    ar: "اترك الحقلين فارغين ليوم كامل. أما العيادة الإضافية فيجب تحديد ساعاتها — إذ لا يوجد نمط أسبوعي ترثه.",
  },
  reasonHelp: {
    en: "Required. A patient will ask why their appointment moved, and this is the answer they are owed.",
    ar: "مطلوب. سيسأل المستفيد عن سبب تغيير موعده، وهذا هو الجواب الذي يستحقه.",
  },
  whoLabel: { en: "Who is affected", ar: "من يتأثر" },
  whoHelp: {
    en: "Leave as the whole clinic to close it for everyone. Name a clinician to record their absence while the clinic stays open.",
    ar: "اتركه على العيادة بأكملها لإغلاقها للجميع. أو حدد إكلينيكيًا لتسجيل غيابه مع بقاء العيادة مفتوحة.",
  },
  whichClinic: { en: "Which clinic", ar: "أي عيادة" },
  whichClinicHelp: {
    en: "You supervise several clinics, so this change must name the one it applies to.",
    ar: "أنت تشرف على عدة عيادات، لذا يجب تحديد العيادة التي ينطبق عليها هذا التغيير.",
  },
  pickClinic: { en: "Choose a clinic…", ar: "اختر عيادة…" },
  needClinic: { en: "Choose which clinic this applies to.", ar: "اختر العيادة التي ينطبق عليها هذا." },

  preview: { en: "Check impact", ar: "فحص الأثر" },
  previewing: { en: "Checking…", ar: "جارٍ الفحص…" },
  impactNone: {
    en: "No booked appointments fall inside this period. Nothing will need reassigning.",
    ar: "لا توجد مواعيد محجوزة ضمن هذه الفترة. لن يحتاج أي موعد إلى إعادة توزيع.",
  },
  impactSome: { en: "booked appointment(s) fall inside this period", ar: "موعد/مواعيد محجوزة تقع ضمن هذه الفترة" },
  impactExplain: {
    en: "None of these will be cancelled. Each is flagged so you can ring the patient and move them. Confirm below once you have read the list.",
    ar: "لن يُلغى أي منها. سيُعلَّم كل موعد لتتمكن من الاتصال بالمستفيد ونقله. أكّد أدناه بعد قراءة القائمة.",
  },
  patient: { en: "Patient", ar: "المستفيد" },
  when: { en: "When", ar: "الموعد" },
  acknowledge: { en: "I have read the affected appointments", ar: "لقد اطلعت على المواعيد المتأثرة" },
  apply: { en: "Apply exception", ar: "تطبيق الاستثناء" },
  mustPreview: {
    en: "Check the impact before applying. The list of affected appointments is the point of this step.",
    ar: "افحص الأثر قبل التطبيق. قائمة المواعيد المتأثرة هي الغرض من هذه الخطوة.",
  },
  mustAcknowledge: { en: "Confirm you have read the affected appointments.", ar: "أكّد اطلاعك على المواعيد المتأثرة." },
  needReason: { en: "Enter a reason.", ar: "أدخل سببًا." },
  needFrom: { en: "Enter the date this starts.", ar: "أدخل تاريخ البدء." },
  applied: { en: "Exception applied.", ar: "تم تطبيق الاستثناء." },
  flaggedNoneCancelled: { en: "flagged for reassignment · 0 cancelled", ar: "مُعلَّم لإعادة التوزيع · 0 ملغى" },
  staleImpact: {
    en: "The number of affected appointments changed since you checked. Check the impact again.",
    ar: "تغيّر عدد المواعيد المتأثرة منذ الفحص. افحص الأثر مرة أخرى.",
  },
  close: { en: "Close", ar: "إغلاق" },
  cancel: { en: "Cancel", ar: "إلغاء" },
} satisfies Record<string, Localized>;

const KIND_LABEL: Record<RosterKind, Localized> = {
  Leave: S.kindLeave,
  PublicHoliday: S.kindHoliday,
  ClinicClosed: S.kindClosed,
  AdHocClinic: S.kindAdHoc,
};

/** .NET `DayOfWeek` — Sunday is 0, matching `provider_availability.day_of_week`. */
const DAY_LABEL: Localized[] = [
  { en: "Sunday", ar: "الأحد" },
  { en: "Monday", ar: "الإثنين" },
  { en: "Tuesday", ar: "الثلاثاء" },
  { en: "Wednesday", ar: "الأربعاء" },
  { en: "Thursday", ar: "الخميس" },
  { en: "Friday", ar: "الجمعة" },
  { en: "Saturday", ar: "السبت" },
];

const DAYS = [0, 1, 2, 3, 4, 5, 6];

/** Today in the ISO form both `<input type="date">` and the endpoint speak. Local, not UTC: the clinic's day. */
function todayIso(): string {
  const d = new Date();
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
}

function shiftIso(iso: string, days: number): string {
  const [y, m, d] = iso.split("-").map(Number);
  const next = new Date(y, m - 1, d + days);
  return `${next.getFullYear()}-${String(next.getMonth() + 1).padStart(2, "0")}-${String(next.getDate()).padStart(2, "0")}`;
}

type View = "pattern" | "day";

/**
 * (design 42 §4/§6, 33.10) — the roster, rebuilt around the two questions a clinic actually asks.
 *
 * ============================================================================================================
 * WHY IT IS TWO VIEWS AND NOT ONE TABLE
 * ============================================================================================================
 * This screen used to be a flat list of availability RULES: one row per clinician per weekday. A clinician
 * working five days appeared five times, a clinician working two clinics appeared under both with nothing
 * saying which row belonged to which building, and there was no way to ask the two questions the roster
 * exists for:
 *
 *   • "What does Dr Karim's week look like?" — his rows were scattered through a table sorted by day.
 *   • "Who is in at Dokki on Thursday?" — nowhere at all. The weekly pattern says what NORMALLY happens and
 *     the exception calendar says what does not; combining them by eye means applying four rules by hand.
 *
 * So the pattern view is now MASTER/DETAIL over people — each clinician once, their week in a pane beside the
 * list — and the day view is a separate, server-computed answer for one clinic on one date. The exceptions,
 * which are a maintenance task rather than something anyone reads on arrival, are folded behind a button.
 *
 * ============================================================================================================
 * THE DAY VIEW IS NOT DERIVED HERE
 * ============================================================================================================
 * It comes from `GET /roster/day`, which runs `SlotGeneration` — the one place availability is decided
 * (design 42 §7 rule 5) — for a single date. Computing it in the browser from the rules and the exceptions
 * this screen already holds would have been easy and wrong: it is a second implementation of "a whole-day
 * closure beats an extra clinic, a part-day absence shortens a session, the cap applies after subtraction",
 * in a language with no tests over any of it, and the first divergence would be a clinic telling a patient it
 * was open on a day the booking engine had already closed.
 *
 * ============================================================================================================
 * ONE SCREEN, TWO ROLES
 * ============================================================================================================
 * A branch coordinator and a clinics manager hold the SAME permissions (design 42 §1) and differ only in
 * reach. That is why the clinic control is a filter rather than a switch, why it disappears for a caller who
 * runs one clinic, and why every write that could land in more than one building names the building.
 */
export function BranchRoster() {
  const t = useLoc();
  const { lang } = useTheme();
  const { session } = useAuth();

  const [view, setView] = useState<View>("pattern");
  const [branchId, setBranchId] = useState("");
  const [clinicianId, setClinicianId] = useState("");
  const [date, setDate] = useState(todayIso());
  const [exceptionsOpen, setExceptionsOpen] = useState(false);

  const people = useAsync(() => branchApi.practitioners({ includeUnlicensed: true }), []);
  const branches = useAsync(() => branchApi.branches(), []);
  // Re-read on a clinic change: the server narrows by branch, so the filter is a REQUEST and not a client
  // predicate over a set that might not contain the other clinics' rules at all.
  const rules = useAsync(() => availabilityApi.list(branchId ? { branchId } : {}), [branchId]);
  const exceptions = useAsync(() => rosterApi.list(), []);

  // Memoized, all three, because each is a fresh array on every render otherwise and every derivation below
  // depends on them — the lists would be rebuilt and the table re-sorted on every keystroke in the combobox.
  const practitioners = useMemo(() => people.data ?? [], [people.data]);
  const branchList = useMemo(() => branches.data ?? [], [branches.data]);
  const ruleList = useMemo(() => rules.data ?? [], [rules.data]);

  const nameOf = useMemo(() => {
    const byId = new Map(practitioners.map((p) => [p.practitionerId, p]));
    return (id: string | null): string | null => {
      if (!id) return null;
      const p = byId.get(id);
      if (!p) return id.slice(0, 8);
      return lang === "ar" ? p.fullNameAr : p.fullNameEn;
    };
  }, [practitioners, lang]);

  const branchName = useMemo(() => {
    const byId = new Map(branchList.map((b) => [b.branchId, b]));
    return (id: string | null): string | null => {
      if (!id) return null;
      const b = byId.get(id);
      if (!b) return id.slice(0, 8);
      return lang === "ar" ? b.nameAr : b.nameEn;
    };
  }, [branchList, lang]);

  /*
    THE UNIQUE CLINICIAN LIST.

    Built from the PRACTITIONER directory rather than from the rules, so somebody with no pattern at all still
    appears — they are precisely the person a coordinator is looking for when a clinic is short. The rules are
    then folded in as a summary of the week.
  */
  const clinicians = useMemo(() => {
    const inScope = practitioners.filter((p) => !branchId || p.branches.includes(branchId));
    return inScope
      .map((p) => {
        const mine = ruleList.filter((r) => r.doctorId === p.practitionerId);
        return {
          person: p,
          rules: mine,
          days: new Set(mine.map((r) => r.dayOfWeek)).size,
          weekly: mine.reduce((n, r) => n + r.slotsPerDay, 0),
        };
      })
      .sort((a, b) => {
        const an = lang === "ar" ? a.person.fullNameAr : a.person.fullNameEn;
        const bn = lang === "ar" ? b.person.fullNameAr : b.person.fullNameEn;
        return an.localeCompare(bn, lang);
      });
  }, [practitioners, ruleList, branchId, lang]);

  const selected = clinicians.find((c) => c.person.practitionerId === clinicianId) ?? null;
  const exceptionCount = (exceptions.data ?? []).length;

  // Only a caller who reaches more than one clinic has a choice to make. A coordinator's clinic is decided by
  // the app-bar switcher, and offering them a picker with one option in it is a control that does nothing.
  const multiBranch = isSetScopedRole(session?.role ?? undefined) && branchList.length > 1;

  const branchOptions = [
    { value: "", label: t(S.allBranches) },
    ...branchList.map((b) => ({ value: b.branchId, label: lang === "ar" ? b.nameAr : b.nameEn, keywords: b.branchCode })),
  ];

  return (
    <div className="branch-screen">
      <PageHeader title={t(S.title)} />
      <p className="muted lede">{t(S.intro)}</p>

      {/*
        THE VIEW SWITCH AND THE EXCEPTIONS BUTTON ARE ONE ROW, and neither is in the page header.

        `PageHeader` renders nothing at all when there is no session — a reasonable guard for a component
        whose entire content is derived from the caller's portal, and a bad place for the ONLY route to a
        feature. Putting the exceptions trigger there made a whole surface disappear behind a condition that
        has nothing to do with it. Here it sits beside the control it belongs with, visible in both views.
      */}
      <div className="rst-toolbar">
        <SegmentedControl<View>
          aria-label={t(S.viewLabel)}
          segments={[
            { value: "pattern", label: t(S.viewPattern) },
            { value: "day", label: t(S.viewDay) },
          ]}
          value={view}
          onChange={setView}
        />
        {/*
          A FILLED button, not a ghost one. Ghost styling says "secondary to what is beside it", and what is
          beside it is a view switch — so the only route to leave, holidays and closures read as a caption on
          the control next to it and was missed. It is the screen's one action; it looks like one.
        */}
        <Button variant="primary" leadingIcon={<Icon name="calendar-off" />} onClick={() => setExceptionsOpen(true)}>
          {t(S.exceptionsAction)}
          {exceptionCount > 0 && <span className="rst-count tnum">{exceptionCount}</span>}
        </Button>
      </div>

      {view === "pattern" ? (
        <>
          <Card className="rst-scope" as="section" aria-label={t(S.scopeRegion)}>
            {multiBranch && (
              <ComboboxField
                label={t(S.branchLabel)}
                options={branchOptions}
                value={branchId}
                onChange={(v) => {
                  setBranchId(v);
                  // A clinician who does not work the newly-chosen clinic must not stay selected: the pane
                  // would keep showing a week that has nothing to do with what the filter now says.
                  setClinicianId("");
                }}
              />
            )}
            <ComboboxField
              label={t(S.clinicianLabel)}
              placeholder={t(S.clinicianPlaceholder)}
              options={clinicians.map((c) => ({
                value: c.person.practitionerId,
                label: lang === "ar" ? c.person.fullNameAr : c.person.fullNameEn,
                hint: c.person.primarySpecialty ?? undefined,
                // Found by clinic name as well as by their own — "who works Dokki" is a real way to search.
                keywords: [c.person.fullNameEn, c.person.fullNameAr, c.person.practitionerType,
                  ...c.person.branches.map((b) => branchName(b) ?? "")].join(" "),
              }))}
              value={clinicianId}
              onChange={setClinicianId}
            />
          </Card>

          <div className="split split-wide rst-split">
            <Card as="section" style={{ padding: "var(--sp3)" }}>
              <h2 className="section-h">{t(S.listHeading)}</h2>
              <AsyncSection state={people} isEmpty={() => clinicians.length === 0} emptyLabel={S.listEmpty}>
                {() => (
                  <ClinicianList
                    rows={clinicians}
                    selectedId={clinicianId}
                    onSelect={setClinicianId}
                    branchName={branchName}
                  />
                )}
              </AsyncSection>
            </Card>

            <div>
              {selected ? (
                <PatternPane
                  key={selected.person.practitionerId}
                  person={selected.person}
                  rules={selected.rules}
                  /* Every rule in reach, not only this clinician's. A NEW working day needs the clinic's
                     provider and location, which this screen cannot ask for and which any rule at that
                     clinic already carries — so a clinician with no pattern of their own can still be given
                     one wherever a colleague has one. */
                  allRules={ruleList}
                  branches={branchList}
                  branchName={branchName}
                  onChanged={() => {
                    rules.reload();
                    people.reload();
                  }}
                />
              ) : (
                <Card style={{ padding: "var(--sp6)" }}>
                  <p className="muted">{t(S.pickSomeone)}</p>
                </Card>
              )}
            </div>
          </div>
        </>
      ) : (
        <DayView
          date={date}
          onDate={setDate}
          branchId={branchId}
          onBranch={setBranchId}
          branchOptions={branchOptions}
          multiBranch={multiBranch}
          nameOf={nameOf}
          branchName={branchName}
        />
      )}

      <ExceptionsModal
        open={exceptionsOpen}
        onClose={() => setExceptionsOpen(false)}
        state={exceptions}
        practitioners={practitioners}
        branches={branchList}
        nameOf={nameOf}
        lang={lang}
        onChanged={() => exceptions.reload()}
      />
    </div>
  );
}

// ── The clinician list ──────────────────────────────────────────────────────────────────────────────────

interface ClinicianRow {
  person: BranchPractitioner;
  rules: AvailabilityRule[];
  days: number;
  weekly: number;
}

/**
 * Each clinician ONCE.
 *
 * The clinics column is the other half of the fix. A clinician who works two buildings used to appear twice
 * in a table that never named either, so "Dr Karim, Tuesday, 14:00" and "Dr Karim, Wednesday, 14:00" were
 * indistinguishable rows about different clinics. Naming the clinics here, and again on the pattern rows of
 * anyone who has more than one, means the question never has to be asked.
 */
function ClinicianList({
  rows,
  selectedId,
  onSelect,
  branchName,
}: {
  rows: ClinicianRow[];
  selectedId: string;
  onSelect: (id: string) => void;
  branchName: (id: string | null) => string | null;
}) {
  const t = useLoc();
  const { lang } = useTheme();

  const columns: Column<ClinicianRow>[] = useMemo(
    () => [
      {
        key: "name",
        header: t(S.clinicianLabel),
        cell: (r) => (
          <span className="rst-who">
            <strong>{lang === "ar" ? r.person.fullNameAr : r.person.fullNameEn}</strong>
            <span className="muted">
              {r.person.primarySpecialty ?? r.person.practitionerType}
            </span>
          </span>
        ),
        sortable: true,
        sortValue: (r) => (lang === "ar" ? r.person.fullNameAr : r.person.fullNameEn),
      },
      {
        key: "clinics",
        header: t(S.colClinics),
        cell: (r) =>
          r.person.branches.length === 0
            ? <span className="muted">{t(S.noClinics)}</span>
            : <span className="rst-clinics">{r.person.branches.map((b) => branchName(b)).join(" · ")}</span>,
      },
      {
        key: "days",
        header: t(S.colDays),
        numeric: true,
        cell: (r) => (r.days === 0 ? <span className="muted">{t(S.noPatternShort)}</span> : String(r.days)),
        sortable: true,
        sortValue: (r) => r.days,
      },
      {
        key: "weekly",
        header: t(S.colWeekly),
        numeric: true,
        cell: (r) => (r.weekly === 0 ? <span className="muted">—</span> : String(r.weekly)),
        sortable: true,
        sortValue: (r) => r.weekly,
      },
    ],
    [t, lang, branchName],
  );

  return (
    <DataTable
      caption={t(S.listHeading)}
      columns={columns}
      rows={rows}
      rowKey={(r) => r.person.practitionerId}
      interactive
      density="compact"
      selectedKey={selectedId || undefined}
      onSelect={(r) => onSelect(r.person.practitionerId)}
      emptyLabel={t(S.noMatches)}
    />
  );
}

// ── The weekly pattern pane ─────────────────────────────────────────────────────────────────────────────

interface DayRow {
  dayOfWeek: number;
  rule: AvailabilityRule | null;
}

function PatternPane({
  person,
  rules,
  allRules,
  branches,
  branchName,
  onChanged,
}: {
  person: BranchPractitioner;
  rules: AvailabilityRule[];
  allRules: AvailabilityRule[];
  branches: BranchRef[];
  branchName: (id: string | null) => string | null;
  onChanged: () => void;
}) {
  const t = useLoc();
  const { lang } = useTheme();
  const [editing, setEditing] = useState<{ day: number; rule: AvailabilityRule | null; branchId: string } | null>(null);
  const [viewingHistory, setViewingHistory] = useState<AvailabilityRule | null>(null);
  const [retiring, setRetiring] = useState<AvailabilityRule | null>(null);
  const [assigning, setAssigning] = useState(false);
  const [outcome, setOutcome] = useState<string | null>(null);
  /*
    THE CLINIC CHIPS FILTER.

    They started as labels — the fact that Dr Karim works Maadi and Dokki — and a label is where a reader's
    hand goes anyway when the seven-day table beside it is showing both. Six rows across two buildings is the
    case the Clinic column was added for, and narrowing to one is what somebody arranging Wednesday at Dokki
    actually wants. Null is "every clinic", which is the state the pane opens in.
  */
  const [clinicFilter, setClinicFilter] = useState<string | null>(null);

  const visible = clinicFilter ? rules.filter((r) => r.branchId === clinicFilter) : rules;

  // A clinician at two clinics needs the clinic named on every pattern row; one at a single clinic does not,
  // and a column repeating the same word seven times is noise on the narrower half of a split layout. Once
  // the chips have narrowed to one clinic the column says the same word again, so it goes.
  const showClinic = !clinicFilter
    && (new Set(rules.map((r) => r.branchId)).size > 1 || person.branches.length > 1);

  /*
    WHERE A NEW WORKING DAY CAN BE ADDED.

    A rule carries a provider and a location — the clinic's service point — and this screen has no way to ask
    for them, so a new day inherits them. It used to inherit from THIS CLINICIAN's other rules, which meant a
    clinician with no pattern at all could never be given one, and removing somebody's last day at a clinic
    took the Add button with it. Any rule at the same clinic carries the same service point, so the source is
    every rule in reach.
  */
  const addableAt = (branch: string | null): boolean =>
    !!branch && allRules.some((r) => r.branchId === branch);

  const addableClinics = person.branches.filter(addableAt);
  const canAdd = clinicFilter ? addableAt(clinicFilter) : addableClinics.length > 0;
  // The clinic a new day lands at, when there is no choice to make: the filter if one is set, else their only
  // addable clinic. Empty string means "the form has to ask".
  const defaultAddBranch = clinicFilter ?? (addableClinics.length === 1 ? addableClinics[0] : "");

  /*
    SEVEN DAYS, ALWAYS — including the ones with nothing in them.

    A table of only the days somebody works cannot show that Wednesday is free, which is the question asked by
    everyone trying to find cover. Days with more than one rule (two clinics on one weekday) expand to one row
    each rather than being collapsed, because they are two different sessions in two different buildings.
  */
  const rows: DayRow[] = useMemo(() => {
    const out: DayRow[] = [];
    for (const d of DAYS) {
      const onDay = visible.filter((r) => r.dayOfWeek === d)
        .sort((a, b) => a.startTime.localeCompare(b.startTime));
      if (onDay.length === 0) out.push({ dayOfWeek: d, rule: null });
      else for (const r of onDay) out.push({ dayOfWeek: d, rule: r });
    }
    return out;
  }, [visible]);

  const columns: Column<DayRow>[] = useMemo(
    () => [
      {
        key: "day",
        header: t(S.viewPattern),
        cell: (r) => <strong>{t(DAY_LABEL[r.dayOfWeek] ?? DAY_LABEL[0])}</strong>,
      },
      ...(showClinic
        ? [{
            key: "clinic",
            header: t(S.branchLabel),
            cell: (r: DayRow) => (r.rule ? branchName(r.rule.branchId) ?? "—" : "—"),
          }]
        : []),
      {
        key: "hours",
        header: t(S.hoursCol),
        cell: (r) =>
          r.rule
            ? `${r.rule.startTime}–${r.rule.endTime}`
            : <span className="muted">{t(S.notWorking)}</span>,
      },
      {
        key: "slot",
        header: t(S.slotLength),
        cell: (r) => (r.rule ? `${r.rule.slotMinutes} ${t(S.minutes)}` : <span className="muted">—</span>),
      },
      {
        key: "cap",
        header: t(S.cap),
        /*
          The cap and what it COSTS, together. A bare "12" leaves the reader to work out whether that is a
          restriction at all; "12 of 16 the hours allow" is the sentence somebody is checking when they ask
          why the calendar looks shorter than the opening times.
        */
        cell: (r) => {
          if (!r.rule) return <span className="muted">—</span>;
          if (r.rule.maxPerDay === null) return <span className="muted">{t(S.noCap)}</span>;
          return (
            <span>
              <strong>{r.rule.maxPerDay}</strong>{" "}
              <span className="muted">
                {t(S.capExplains).replace("{window}", String(r.rule.slotsFromWindow))}
              </span>
            </span>
          );
        },
      },
      {
        key: "offered",
        header: t(S.offered),
        numeric: true,
        cell: (r) => (r.rule ? String(r.rule.slotsPerDay) : <span className="muted">—</span>),
      },
      {
        key: "actions",
        header: "",
        /*
          ICONS, not three words per row.

          Seven rows × "Edit History Remove" is twenty-one links, and on a pane that already carries a
          clinic, hours, a slot length, a cap and a slot count they were the widest thing in the table —
          reading as body text rather than as controls, and pushing the numbers people come here for off the
          edge. Each carries `aria-label` and `title`, so the name is still there for a screen reader and for
          a hover, which is what 0B §6 requires of an icon-only control.
        */
        cell: (r) =>
          r.rule ? (
            <span className="row-actions rst-actions">
              <Button
                size="sm" variant="ghost"
                aria-label={t(S.editPattern)} title={t(S.editPattern)}
                onClick={() => setEditing({ day: r.dayOfWeek, rule: r.rule, branchId: r.rule?.branchId ?? "" })}
              >
                <Icon name="pen" />
              </Button>
              <Button
                size="sm" variant="ghost"
                aria-label={t(S.historyAction)} title={t(S.historyAction)}
                onClick={() => setViewingHistory(r.rule)}
              >
                <Icon name="history" />
              </Button>
              <Button
                size="sm" variant="ghost"
                aria-label={t(S.retireAction)} title={t(S.retireAction)}
                onClick={() => setRetiring(r.rule)}
              >
                <Icon name="bin" />
              </Button>
            </span>
          ) : canAdd ? (
            <Button
              size="sm" variant="ghost"
              aria-label={t(S.addDay)} title={t(S.addDay)}
              onClick={() => setEditing({ day: r.dayOfWeek, rule: null, branchId: defaultAddBranch })}
            >
              <Icon name="plus" />
            </Button>
          ) : null,
      },
    ],
    [t, showClinic, branchName, canAdd, defaultAddBranch],
  );

  return (
    <Card as="section" style={{ padding: "var(--sp4)" }} aria-label={t(S.patternHeading)}>
      <div className="result-head">
        <div>
          <h2 className="section-h" style={{ marginBottom: 0 }}>
            {lang === "ar" ? person.fullNameAr : person.fullNameEn}
          </h2>
          <p className="muted" style={{ margin: "4px 0 0" }}>
            {person.practitionerType}
            {person.primarySpecialty && <> · {person.primarySpecialty}</>}
          </p>
        </div>
      </div>

      {/*
        THE CLINICS THIS PERSON WORKS, on the pane rather than only in the list. It is the fact that makes
        every row below readable — a 14:00 Wednesday means something different at Maadi and at Dokki — and it
        is the thing a clinics manager comes here to change.
      */}
      <div className="rst-assign">
        <span className="mrs-label" id={`clinics-${person.practitionerId}`}>{t(S.assignedClinics)}</span>
        <div className="rst-chips" role="group" aria-labelledby={`clinics-${person.practitionerId}`}>
          {person.branches.length === 0 ? (
            <span className="muted">{t(S.noClinics)}</span>
          ) : (
            person.branches.map((b) => {
              const name = branchName(b) ?? b.slice(0, 8);
              const on = clinicFilter === b;
              return (
                <button
                  key={b}
                  type="button"
                  className="rst-chip"
                  // `aria-pressed`, not a checkbox: this is a toggle on the view, and the pressed state is
                  // what a screen reader needs to say. Colour alone would leave it unannounced and, for a
                  // colour-blind reader, unseen.
                  aria-pressed={on}
                  title={on ? t(S.showingAllClinics) : t(S.filterToClinic).replace("{clinic}", name)}
                  onClick={() => setClinicFilter(on ? null : b)}
                >
                  <Icon name="branch" />
                  <span>{name}</span>
                </button>
              );
            })
          )}
          <Button size="sm" variant="ghost" leadingIcon={<Icon name="plus" />} onClick={() => setAssigning(true)}>
            {t(S.addClinic)}
          </Button>
        </div>
      </div>

      <DataTable
        caption={t(S.patternHeading)}
        columns={columns}
        rows={rows}
        rowKey={(r) => r.rule?.availabilityId ?? `free-${r.dayOfWeek}`}
        density="compact"
      />

      {!canAdd && <p className="muted">{t(S.cannotAddYet)}</p>}

      {outcome && (
        <InlineAlert tone="ok">
          <span role="status" aria-live="polite">{outcome}</span>
        </InlineAlert>
      )}

      {editing && (
        <PatternForm
          person={person}
          dayOfWeek={editing.day}
          rule={editing.rule}
          presetBranch={editing.branchId}
          templates={allRules}
          addableClinics={addableClinics}
          branchName={branchName}
          onClose={() => setEditing(null)}
          onSaved={() => {
            setEditing(null);
            onChanged();
          }}
        />
      )}

      {viewingHistory && (
        <PatternHistory rule={viewingHistory} onClose={() => setViewingHistory(null)} />
      )}

      {retiring && (
        <RetirePattern
          rule={retiring}
          onClose={() => setRetiring(null)}
          onRetired={() => {
            setRetiring(null);
            setOutcome(t(S.retired));
            onChanged();
          }}
        />
      )}

      {assigning && (
        <AssignClinic
          person={person}
          branches={branches}
          onClose={() => setAssigning(false)}
          onSaved={() => {
            setAssigning(false);
            setOutcome(t(S.assignSaved));
            onChanged();
          }}
        />
      )}
    </Card>
  );
}

/**
 * One modal for editing a working day and for adding one.
 *
 * The two differ in exactly two places — whether a clinic has to be chosen, and which verb the button uses —
 * and splitting them would mean two copies of the cap rule, the validation and the write. The rule's IDENTITY
 * (clinician, clinic, weekday) is fixed once created: moving a Tuesday to a Wednesday is removing one rule and
 * stating another, because the slots already generated from it belong to the Tuesday.
 */
function PatternForm({
  person,
  dayOfWeek,
  rule,
  presetBranch,
  templates,
  addableClinics,
  branchName,
  onClose,
  onSaved,
}: {
  person: BranchPractitioner;
  dayOfWeek: number;
  rule: AvailabilityRule | null;
  /** The clinic the caller was already looking at — the chip filter, or their only one. "" ⇒ ask. */
  presetBranch: string;
  /** EVERY rule in reach, not this clinician's. See `addableAt` on the pane. */
  templates: AvailabilityRule[];
  /** The clinician's assigned clinics that have a pattern to inherit a service point from. */
  addableClinics: string[];
  branchName: (id: string | null) => string | null;
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const { lang } = useTheme();
  const branchChoices = addableClinics;

  const [branchId, setBranchId] = useState(rule?.branchId ?? presetBranch ?? branchChoices[0] ?? "");
  const [startTime, setStartTime] = useState(rule?.startTime ?? "09:00");
  const [endTime, setEndTime] = useState(rule?.endTime ?? "13:00");
  const [slotMinutes, setSlotMinutes] = useState(String(rule?.slotMinutes ?? 15));
  // Empty string means "no limit", which is a different value from 0 — and 0 is refused, because a clinic
  // that takes nobody is a closure, and closures carry a reason and an impact preview.
  const [cap, setCap] = useState(rule?.maxPerDay == null ? "" : String(rule.maxPerDay));
  const [validation, setValidation] = useState<string | null>(null);
  const write = useWrite();

  /*
    Provider and location name the CLINIC's service point — not the clinician's. They are neither editable
    here nor askable here, so a new day inherits them from a rule at the same clinic.

    This clinician's own rule there first, when they have one, and any colleague's otherwise. Preferring
    their own is not about correctness — every rule at a clinic carries that clinic's service point — but
    about the ordinary case staying the obvious one when somebody reads the request later.
  */
  const template =
    templates.find((r) => r.branchId === branchId && r.doctorId === person.practitionerId)
    ?? templates.find((r) => r.branchId === branchId)
    ?? null;

  const submit = async () => {
    const capValue = cap.trim() === "" ? null : Number(cap);
    if (capValue !== null && (!Number.isFinite(capValue) || capValue <= 0)) {
      setValidation(t(S.capMustBePositive));
      return;
    }
    if (!template) { setValidation(t(S.cannotAddYet)); return; }
    setValidation(null);

    const body = {
      providerId: rule?.providerId ?? template.providerId,
      locationId: rule?.locationId ?? template.locationId,
      doctorId: person.practitionerId,
      branchId: (rule?.branchId ?? branchId) || undefined,
      dayOfWeek,
      startTime,
      endTime,
      slotMinutes: Number(slotMinutes),
      maxPerDay: capValue,
    };

    const ok = await write.run(() =>
      rule ? availabilityApi.update(rule.availabilityId, body) : availabilityApi.create(body));
    if (ok) onSaved();
  };

  return (
    <Modal
      open
      onOpenChange={(next) => { if (!next) onClose(); }}
      title={rule ? t(S.editHeading) : t(S.addHeadingDay)}
      description={`${lang === "ar" ? person.fullNameAr : person.fullNameEn} · ${t(DAY_LABEL[dayOfWeek] ?? DAY_LABEL[0])}`}
      footer={
        <>
          <Button onClick={submit} disabled={write.busy}>{t(S.savePattern)}</Button>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
        </>
      }
    >
      {/* Only when there is a choice: a new day at a clinician who works two clinics has to say which. */}
      {!rule && branchChoices.length > 1 && (
        <ComboboxField
          label={t(S.branchLabel)}
          options={branchChoices.map((b) => ({ value: b, label: branchName(b) ?? b.slice(0, 8) }))}
          value={branchId}
          onChange={setBranchId}
          required
        />
      )}
      {rule && rule.branchId && (
        <p className="muted">{t(S.branchLabel)}: {branchName(rule.branchId)}</p>
      )}

      <div className="rst-pair">
        <InputField label={t(S.startLabel)} type="time" value={startTime} onChange={(e) => setStartTime(e.target.value)} required />
        <InputField label={t(S.endLabel)} type="time" value={endTime} onChange={(e) => setEndTime(e.target.value)} required />
      </div>
      <div className="rst-pair">
        <InputField label={t(S.slotMinutesLabel)} type="number" min={1} value={slotMinutes} onChange={(e) => setSlotMinutes(e.target.value)} required />
        <InputField label={t(S.capLabel)} type="number" min={1} value={cap} onChange={(e) => setCap(e.target.value)} />
      </div>
      <p className="muted">{t(S.capHelp)}</p>

      {validation && <InlineAlert tone="warn">{validation}</InlineAlert>}
      {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
    </Modal>
  );
}

function PatternHistory({ rule, onClose }: { rule: AvailabilityRule; onClose: () => void }) {
  const t = useLoc();
  const history = useAsync(() => availabilityApi.history(rule.availabilityId), [rule.availabilityId]);

  const entries: TimelineEntry[] = (history.data?.entries ?? []).map((e) => ({
    sequence: e.sequence,
    recordedAt: e.recordedAt,
    actorName: e.actorName,
    actorSubject: e.actorSubject,
    values: [
      { label: S.hoursCol, value: e.startTime && e.endTime ? `${e.startTime}–${e.endTime}` : null },
      { label: S.slotLength, value: e.slotMinutes === null ? null : `${e.slotMinutes} ${t(S.minutes)}` },
      { label: S.cap, value: e.maxPerDay === null ? t(S.noCap) : String(e.maxPerDay) },
    ],
  }));

  return (
    <Modal
      open
      onOpenChange={(next) => { if (!next) onClose(); }}
      title={t(S.historyHeading)}
      description={t(DAY_LABEL[rule.dayOfWeek] ?? DAY_LABEL[0])}
      wide
      footer={<Button variant="ghost" onClick={onClose}>{t(S.close)}</Button>}
    >
      <AsyncSection state={history} isEmpty={(d) => d.entries.length === 0} emptyLabel={S.historyHeading}>
        {() => <ChangeTimeline entries={entries} />}
      </AsyncSection>
    </Modal>
  );
}

/**
 * Removing a working day.
 *
 * The confirmation exists for the half people do not expect: retiring the rule stops FUTURE slots and leaves
 * the ones already generated — and everything booked into them — exactly where they are. Somebody who removes
 * a Tuesday to cover next Tuesday's absence has changed every Tuesday and cancelled nothing, which is the
 * opposite of what they wanted on both counts.
 */
function RetirePattern({
  rule,
  onClose,
  onRetired,
}: {
  rule: AvailabilityRule;
  onClose: () => void;
  onRetired: () => void;
}) {
  const t = useLoc();
  const { lang } = useTheme();
  const write = useWrite();

  return (
    <Modal
      open
      onOpenChange={(next) => { if (!next) onClose(); }}
      title={t(S.retireHeading)}
      description={`${t(DAY_LABEL[rule.dayOfWeek] ?? DAY_LABEL[0])} · ${rule.startTime}–${rule.endTime}`}
      footer={
        <>
          <Button
            onClick={async () => {
              const ok = await write.run(() => availabilityApi.retire(rule.availabilityId));
              if (ok) onRetired();
            }}
            disabled={write.busy}
          >
            {t(S.retireAction)}
          </Button>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
        </>
      }
    >
      <p>{t(S.retireExplains)}</p>
      <InlineAlert tone="info">{t(S.whyNotDelete)}</InlineAlert>
      {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
    </Modal>
  );
}

function AssignClinic({
  person,
  branches,
  onClose,
  onSaved,
}: {
  person: BranchPractitioner;
  branches: BranchRef[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const { lang } = useTheme();
  const [branchId, setBranchId] = useState("");
  const [validFrom, setValidFrom] = useState(todayIso());
  const [validation, setValidation] = useState<string | null>(null);
  const write = useWrite();

  // Only clinics they are not already at. Offering one they already work reads as an edit and is an insert.
  const options = branches
    .filter((b) => !person.branches.includes(b.branchId))
    .map((b) => ({ value: b.branchId, label: lang === "ar" ? b.nameAr : b.nameEn, keywords: b.branchCode }));

  return (
    <Modal
      open
      onOpenChange={(next) => { if (!next) onClose(); }}
      title={t(S.assignHeading)}
      description={lang === "ar" ? person.fullNameAr : person.fullNameEn}
      footer={
        <>
          <Button
            disabled={write.busy}
            onClick={async () => {
              if (!branchId) { setValidation(t(S.needBranch)); return; }
              setValidation(null);
              const ok = await write.run(() =>
                branchApi.assignBranch(person.practitionerId, { branchId, validFrom }));
              if (ok) onSaved();
            }}
          >
            {t(S.addClinic)}
          </Button>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
        </>
      }
    >
      <p className="muted">{t(S.assignHelp)}</p>
      <ComboboxField
        label={t(S.branchLabel)}
        options={[{ value: "", label: t(S.pickClinic) }, ...options]}
        value={branchId}
        onChange={setBranchId}
        required
      />
      <InputField label={t(S.assignFrom)} type="date" value={validFrom} onChange={(e) => setValidFrom(e.target.value)} required />
      {validation && <InlineAlert tone="warn">{validation}</InlineAlert>}
      {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
    </Modal>
  );
}

// ── Today's roster ──────────────────────────────────────────────────────────────────────────────────────

/**
 * One clinic, one date — the answer the two other tables could not give between them.
 *
 * <p>Every number here is the server's. `GET /roster/day` runs the same slot computation the booking engine
 * runs, for a single day, so "Working, 8 of 12" already has the leave, the closure, the extra clinic and the
 * cap applied to it. The screen's job is to say which of those changed the answer, which is why the exception
 * reason travels on the line rather than being looked up in a second table.</p>
 */
function DayView({
  date,
  onDate,
  branchId,
  onBranch,
  branchOptions,
  multiBranch,
  nameOf,
  branchName,
}: {
  date: string;
  onDate: (d: string) => void;
  branchId: string;
  onBranch: (id: string) => void;
  branchOptions: { value: string; label: string; keywords?: string }[];
  multiBranch: boolean;
  nameOf: (id: string | null) => string | null;
  branchName: (id: string | null) => string | null;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const day = useAsync(
    () => rosterApi.day({ branchId: branchId || undefined, date }),
    [branchId, date]);

  const columns: Column<DayRosterLine>[] = useMemo(
    () => [
      {
        key: "who",
        header: t(S.clinicianLabel),
        cell: (l) => nameOf(l.practitionerId) ?? t(S.wholeClinic),
        sortable: true,
        sortValue: (l) => nameOf(l.practitionerId) ?? "",
      },
      ...(multiBranch && !branchId
        ? [{ key: "clinic", header: t(S.branchLabel), cell: (l: DayRosterLine) => branchName(l.branchId) ?? "—" }]
        : []),
      {
        key: "hours",
        header: t(S.hoursCol),
        cell: (l) => `${l.startTime}–${l.endTime}`,
        sortable: true,
        sortValue: (l) => l.startTime,
      },
      { key: "slot", header: t(S.slotLength), cell: (l) => `${l.slotMinutes} ${t(S.minutes)}` },
      {
        key: "cap",
        header: t(S.cap),
        cell: (l) => (l.maxPerDay === null ? <span className="muted">{t(S.noCap)}</span> : String(l.maxPerDay)),
      },
      {
        key: "offered",
        header: t(S.offeredOnTheDay),
        numeric: true,
        /*
          The day's number and the week's, together, whenever they disagree. "8" on its own looks like the
          pattern; "8 of 12" says something took four away, and the status cell beside it says what.
        */
        cell: (l) =>
          l.slotsOffered === l.slotsFromPattern ? (
            String(l.slotsOffered)
          ) : (
            <span>
              <strong>{l.slotsOffered}</strong>{" "}
              <span className="muted">/ {l.slotsFromPattern}</span>
            </span>
          ),
        sortable: true,
        sortValue: (l) => l.slotsOffered,
      },
      { key: "booked", header: t(S.colBooked), numeric: true, cell: (l) => String(l.booked) },
      {
        key: "open",
        header: t(S.colOpen),
        numeric: true,
        cell: (l) => String(Math.max(0, l.slotsOffered - l.booked)),
      },
      {
        key: "status",
        header: t(S.colStatus),
        cell: (l) => <DayStatus line={l} />,
        sortable: true,
        sortValue: (l) => l.status,
      },
    ],
    [t, nameOf, branchName, multiBranch, branchId],
  );

  return (
    <>
      <Card className="rst-scope" as="section" aria-label={t(S.scopeRegion)}>
        {multiBranch && (
          <ComboboxField
            label={t(S.branchLabel)}
            options={branchOptions}
            value={branchId}
            onChange={onBranch}
          />
        )}
        <InputField label={t(S.dateLabel)} type="date" value={date} onChange={(e) => onDate(e.target.value)} />
        {/*
          STEPPING A DAY AT A TIME is how a roster is read — nobody types a date to see tomorrow.

          One bordered group rather than three ghost links floating beside the date field: the three were
          indistinguishable from the body copy around them, and "Previous day / Today / Next day" spelled out
          took more width than the field they belong to. Arrows for the steps, a word for the one that is not
          a step, and the arrows are rotated off the DOCUMENT direction so Arabic does not end up with
          "previous" pointing forwards.
        */}
        <div className="rst-step" role="group" aria-label={t(S.stepDay)}>
          <button
            type="button" className="rst-step-btn rst-prev"
            aria-label={t(S.prevDay)} title={t(S.prevDay)}
            onClick={() => onDate(shiftIso(date, -1))}
          >
            <Icon name="chevron" />
          </button>
          <button
            type="button" className="rst-step-btn rst-step-now"
            onClick={() => onDate(todayIso())}
            // Pressed when the date already IS today, so the control says where you are as well as where it
            // would take you — otherwise the only way to know is to read the date field and do the sum.
            aria-pressed={date === todayIso()}
          >
            {t(S.today)}
          </button>
          <button
            type="button" className="rst-step-btn rst-next"
            aria-label={t(S.nextDay)} title={t(S.nextDay)}
            onClick={() => onDate(shiftIso(date, 1))}
          >
            <Icon name="chevron" />
          </button>
        </div>
      </Card>

      <AsyncSection state={day} isEmpty={() => false} emptyLabel={S.dayEmpty}>
        {(d: DayRoster) => (
          <>
            <div className="dash-kpis">
              <KpiCard label={t(S.kpiClinicians)} value={String(d.summary.clinicians)} />
              <KpiCard label={t(S.kpiOffered)} value={String(d.summary.slotsOffered)} />
              <KpiCard label={t(S.kpiBooked)} value={String(d.summary.booked)} />
              <KpiCard label={t(S.kpiOpen)} value={String(d.summary.open)} tone={d.summary.open === 0 ? "warn" : undefined} />
            </div>

            {/*
              THE NOTICES. A closure on a day nobody was rostered removes no lines, so without this the screen
              says "nobody is working" for a public holiday and for a rota somebody forgot to enter, in
              identical words.
            */}
            {d.notices.length > 0 && (
              <InlineAlert tone={d.notices.some((n) => n.subtractive) ? "warn" : "info"}>
                <strong>{t(S.noticesHeading)}</strong>
                <ul className="rst-notices">
                  {d.notices.map((n) => (
                    <li key={n.exceptionId}>
                      {t(KIND_LABEL[n.kind as RosterKind] ?? S.kind)} · {n.reason}
                      {n.practitionerId && <> · {nameOf(n.practitionerId)}</>}
                      {!n.wholeDay && n.startTime && n.endTime && <> · {n.startTime}–{n.endTime}</>}
                    </li>
                  ))}
                </ul>
              </InlineAlert>
            )}

            <Card>
              <h2 className="section-h">{t(S.dayHeading)} · {fmt.date(`${d.date}T00:00:00`)}</h2>
              <DataTable
                caption={t(S.dayHeading)}
                columns={columns}
                rows={d.lines}
                rowKey={(l) => `${l.availabilityId ?? "extra"}-${l.practitionerId ?? "none"}-${l.startTime}`}
                emptyLabel={t(S.dayEmpty)}
              />
            </Card>
          </>
        )}
      </AsyncSection>
    </>
  );
}

/** Status with its reason attached — the reason is the actionable half, and it belongs beside the word. */
function DayStatus({ line }: { line: DayRosterLine }) {
  const t = useLoc();
  if (line.status === "Off") {
    return (
      <span className="rst-status">
        <StatusChip kind="warn" label={t(S.statusOff)} />
        {line.exceptionReason && <span className="muted">{line.exceptionReason}</span>}
      </span>
    );
  }
  if (line.status === "Extra") {
    return (
      <span className="rst-status">
        <StatusChip kind="info" label={t(S.statusExtra)} />
        {line.exceptionReason && <span className="muted">{line.exceptionReason}</span>}
      </span>
    );
  }
  // Working, but shortened: the cap did not do this and the hours column does not show it, so say so.
  const shortened = line.slotsOffered < line.slotsFromPattern && line.exceptionReason;
  return (
    <span className="rst-status">
      <StatusChip kind="ok" label={t(S.statusWorking)} />
      {shortened && (
        <span className="muted">{t(S.reducedBy).replace("{reason}", line.exceptionReason ?? "")}</span>
      )}
    </span>
  );
}

// ── Exceptions, behind a button ─────────────────────────────────────────────────────────────────────────

/**
 * Leave, holidays, closures and extra clinics — the whole exception surface, in one dialog.
 *
 * <p><b>Why it folded.</b> It used to occupy two thirds of the screen: a table of every exception for ninety
 * days, and beneath it a nine-field form that was always open. Both are MAINTENANCE — somebody records an
 * absence when it is known — and neither is what anyone opens the roster to read. Leaving them permanently
 * expanded pushed the weekly pattern above the fold and the day's roster off the page entirely, and the count
 * on the button says what the table used to say by being visible.</p>
 *
 * <p>Rendered only while open, so the impact preview inside it starts clean each time. A preview is a claim
 * about a moment, and one left over from a dialog closed twenty minutes ago is exactly the stale number the
 * acknowledgement exists to prevent.</p>
 */
function ExceptionsModal({
  open,
  onClose,
  state,
  practitioners,
  branches,
  nameOf,
  lang,
  onChanged,
}: {
  open: boolean;
  onClose: () => void;
  state: ReturnType<typeof useAsync<RosterException[]>>;
  practitioners: BranchPractitioner[];
  branches: BranchRef[];
  nameOf: (id: string | null) => string | null;
  lang: "en" | "ar";
  onChanged: () => void;
}) {
  const t = useLoc();
  if (!open) return null;

  return (
    <Modal
      open
      onOpenChange={(next) => { if (!next) onClose(); }}
      title={t(S.exceptionsHeading)}
      description={t(S.exceptionsDescription)}
      wide
      footer={<Button variant="ghost" onClick={onClose}>{t(S.close)}</Button>}
    >
      <InlineAlert tone="info">{t(S.whyNotDelete)}</InlineAlert>

      <Exceptions state={state} nameOf={nameOf} onChanged={onChanged} />

      <RecordException
        lang={lang}
        practitioners={practitioners}
        branches={branches}
        onApplied={onChanged}
      />
    </Modal>
  );
}

function Exceptions({
  state,
  nameOf,
  onChanged,
}: {
  state: ReturnType<typeof useAsync<RosterException[]>>;
  nameOf: (id: string | null) => string | null;
  onChanged: () => void;
}) {
  const t = useLoc();
  const { lang } = useTheme();
  const [confirming, setConfirming] = useState<RosterException | null>(null);
  const [outcome, setOutcome] = useState<string | null>(null);
  const write = useWrite();

  const columns: Column<RosterException>[] = useMemo(
    () => [
      { key: "kind", header: t(S.kind), cell: (e) => t(KIND_LABEL[e.kind]) },
      {
        key: "who",
        header: t(S.appliesTo),
        // Whose absence this is. Without it every row read as "the clinic is shut", which is what the form
        // could only ever record — and the two are a very different message to the desk.
        cell: (e) => nameOf(e.practitionerId) ?? t(S.wholeClinic),
      },
      { key: "dates", header: t(S.dates), cell: (e) => (e.dateFrom === e.dateTo ? e.dateFrom : `${e.dateFrom} → ${e.dateTo}`) },
      { key: "hours", header: t(S.hours), cell: (e) => (e.wholeDay ? t(S.wholeDay) : `${e.startTime}–${e.endTime}`) },
      {
        key: "effect",
        header: t(S.effect),
        // Subtractive vs additive is the one thing a reader must not have to infer from the kind name.
        cell: (e) =>
          e.subtractive ? (
            <StatusChip kind="warn" label={t(S.removes)} />
          ) : (
            <StatusChip kind="ok" label={t(S.adds)} />
          ),
      },
      { key: "reason", header: t(S.reason), cell: (e) => e.reason, sortable: true, sortValue: (e) => e.reason },
      {
        key: "actions",
        header: "",
        cell: (e) => (
          <Button size="sm" variant="ghost" onClick={() => setConfirming(e)}>{t(S.withdraw)}</Button>
        ),
      },
    ],
    [t, nameOf],
  );

  const doWithdraw = async () => {
    if (!confirming) return;
    const ok = await write.run(() => rosterApi.withdraw(confirming.exceptionId));
    if (ok) {
      setConfirming(null);
      setOutcome(t(S.withdrawn));
      onChanged();
    }
  };

  return (
    <>
      <AsyncSection state={state} isEmpty={(rows) => rows.length === 0} emptyLabel={S.noneYet}>
        {(rows) => (
          <DataTable caption={t(S.existing)} columns={columns} rows={rows} rowKey={(e) => e.exceptionId} density="compact" />
        )}
      </AsyncSection>

      {outcome && (
        <InlineAlert tone="ok">
          <span role="status" aria-live="polite">{outcome}</span>
        </InlineAlert>
      )}

      {confirming && (
        <Modal
          open
          onOpenChange={(next) => { if (!next) setConfirming(null); }}
          title={t(S.withdraw)}
          description={confirming.reason}
          footer={
            <>
              <Button onClick={doWithdraw} disabled={write.busy}>{t(S.withdraw)}</Button>
              <Button variant="ghost" onClick={() => setConfirming(null)}>{t(S.cancel)}</Button>
            </>
          }
        >
          {/* The half of withdrawal that surprises people: restoring availability does NOT un-flag the
              appointments this stranded, because the coordinator may already have rung those patients and
              moved them. Un-flagging would quietly undo work somebody did on the phone. */}
          <p>{t(S.withdrawExplains)}</p>
          {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
        </Modal>
      )}
    </>
  );
}

function RecordException({
  lang,
  practitioners,
  branches,
  onApplied,
}: {
  lang: "en" | "ar";
  practitioners: BranchPractitioner[];
  branches: BranchRef[];
  onApplied: () => void;
}) {
  const t = useLoc();
  const { session } = useAuth();
  /*
    A clinics manager reaches several clinics at once and has NO active branch until they filter, so the
    server cannot infer which clinic a write is for — and refuses (400) rather than guessing. This form never
    sent a branch at all, which made the supervisor of six clinics the one user who could not record an
    exception anywhere.

    A REACH distinction, not an authority one: both roles hold the same permission set. The picker appears
    because a manager has a choice to make, and a coordinator does not.
  */
  const mustChooseClinic = isSetScopedRole(session?.role ?? undefined);

  const [kind, setKind] = useState<RosterKind>("Leave");
  const [practitionerId, setPractitionerId] = useState("");
  const [branchId, setBranchId] = useState("");
  const [dateFrom, setDateFrom] = useState("");
  const [dateTo, setDateTo] = useState("");
  const [startTime, setStartTime] = useState("");
  const [endTime, setEndTime] = useState("");
  const [reason, setReason] = useState("");

  const [impact, setImpact] = useState<RosterImpact | null>(null);
  const [acknowledged, setAcknowledged] = useState(false);
  const [validation, setValidation] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<string | null>(null);
  // 18.D2/U7 — the impact list shows WHEN each appointment is; in the machine's zone that time is wrong,
  // and this is the list a coordinator rings people from.
  const fmt = useFormat();

  const previewWrite = useWrite();
  const applyWrite = useWrite();

  // Any edit invalidates a previous preview. Without this, an operator could preview a one-day closure,
  // widen it to a week, and apply against the narrow count they read — which is the exact hazard the
  // acknowledgement exists to prevent, re-created by the UI.
  const invalidate = <T,>(setter: (v: T) => void) => (v: T) => {
    setter(v);
    setImpact(null);
    setAcknowledged(false);
    setOutcome(null);
  };

  const body = (): CreateRosterExceptionBody => ({
    kind,
    dateFrom,
    dateTo: dateTo || dateFrom,
    reason: reason.trim(),
    startTime: startTime || undefined,
    endTime: endTime || undefined,
    practitionerId: practitionerId || undefined,
    branchId: branchId || undefined,
  });

  const invalid = (): string | null => {
    if (!dateFrom) return t(S.needFrom);
    if (!reason.trim()) return t(S.needReason);
    if (mustChooseClinic && !branchId) return t(S.needClinic);
    return null;
  };

  const runPreview = async () => {
    const problem = invalid();
    if (problem) { setValidation(problem); return; }
    setValidation(null);
    await previewWrite.run(async () => {
      const result = await rosterApi.preview(body());
      setImpact(result);
      setAcknowledged(false);
      return result;
    });
  };

  const runApply = async () => {
    if (!impact) { setValidation(t(S.mustPreview)); return; }
    if (impact.affectedCount > 0 && !acknowledged) { setValidation(t(S.mustAcknowledge)); return; }
    setValidation(null);
    const ok = await applyWrite.run(async () => {
      const result = await rosterApi.apply({ ...body(), acknowledgedImpactCount: impact.affectedCount });
      setOutcome(`${result.flagged} ${t(S.flaggedNoneCancelled)}`);
      return result;
    });
    if (ok) {
      setImpact(null);
      setAcknowledged(false);
      onApplied();
    }
  };

  const applyDisabled = applyWrite.busy || !impact || (impact.affectedCount > 0 && !acknowledged);

  return (
    <section aria-label={t(S.addHeading)} className="rst-record">
      <h3 className="section-h">{t(S.addHeading)}</h3>

      <div className="rst-pair">
        <ComboboxField
          label={t(S.kind)}
          options={(Object.keys(KIND_LABEL) as RosterKind[]).map((k) => ({ value: k, label: t(KIND_LABEL[k]) }))}
          value={kind}
          onChange={(v) => invalidate(setKind)(v as RosterKind)}
        />

        {/*
          WHO. Design 42 §4's motivating example is "Dr Hala is on leave next Tuesday", and this form could not
          express it: it never sent a practitionerId, so every exception it created closed the entire clinic.
          The server has accepted the field since 25.4.
        */}
        <ComboboxField
          label={t(S.whoLabel)}
          options={[
            { value: "", label: t(S.wholeClinic) },
            ...practitioners.map((p) => ({
              value: p.practitionerId,
              label: lang === "ar" ? p.fullNameAr : p.fullNameEn,
            })),
          ]}
          value={practitionerId}
          onChange={(v) => invalidate(setPractitionerId)(v)}
          help={t(S.whoHelp)}
        />
      </div>

      {mustChooseClinic && (
        <ComboboxField
          label={t(S.whichClinic)}
          options={[
            { value: "", label: t(S.pickClinic) },
            ...branches.map((b) => ({ value: b.branchId, label: lang === "ar" ? b.nameAr : b.nameEn })),
          ]}
          value={branchId}
          onChange={(v) => invalidate(setBranchId)(v)}
          help={t(S.whichClinicHelp)}
          required
        />
      )}

      <div className="rst-pair">
        <InputField label={t(S.from)} type="date" value={dateFrom} onChange={(e) => invalidate(setDateFrom)(e.target.value)} required />
        <InputField label={t(S.to)} type="date" value={dateTo} onChange={(e) => invalidate(setDateTo)(e.target.value)} />
      </div>
      <div className="rst-pair">
        <InputField label={t(S.startTime)} type="time" value={startTime} onChange={(e) => invalidate(setStartTime)(e.target.value)} help={t(S.timeHelp)} />
        <InputField label={t(S.endTime)} type="time" value={endTime} onChange={(e) => invalidate(setEndTime)(e.target.value)} />
      </div>
      <InputField label={t(S.reason)} value={reason} onChange={(e) => invalidate(setReason)(e.target.value)} help={t(S.reasonHelp)} required maxLength={300} />

      <div className="row-actions">
        <Button variant="ghost" onClick={runPreview} disabled={previewWrite.busy}>
          {previewWrite.busy ? t(S.previewing) : t(S.preview)}
        </Button>
      </div>

      {previewWrite.error && <InlineAlert tone="bad">{writeErrorText(previewWrite.error, lang)}</InlineAlert>}

      {impact && (
        <div role="status" aria-live="polite">
          {impact.affectedCount === 0 ? (
            <InlineAlert tone="ok">{t(S.impactNone)}</InlineAlert>
          ) : (
            <>
              <InlineAlert tone="warn">
                {impact.affectedCount} {t(S.impactSome)}
              </InlineAlert>
              <p>{t(S.impactExplain)}</p>
              <DataTable
                caption={t(S.impactSome)}
                columns={[
                  { key: "patient", header: t(S.patient), cell: (a) => a.beneficiaryName ?? a.beneficiaryId.slice(0, 8), sortable: true, sortValue: (a) => a.beneficiaryName },
                  {
                    key: "when",
                    header: t(S.when),
                    cell: (a) => fmt.dateTime(a.scheduledStart), sortable: true, sortValue: (a) => a.scheduledStart },
                ]}
                rows={impact.affected}
                rowKey={(a) => a.appointmentId}
                density="compact"
              />
              <label className="check">
                <input
                  type="checkbox"
                  checked={acknowledged}
                  onChange={(e) => setAcknowledged(e.target.checked)}
                />
                <span>{t(S.acknowledge)}</span>
              </label>
            </>
          )}
        </div>
      )}

      {validation && <InlineAlert tone="warn">{validation}</InlineAlert>}
      {applyWrite.error && (
        <InlineAlert tone="bad">
          {/* A 409 here means the count moved between preview and apply — the stale-preview guard firing. */}
          {applyWrite.error.problemType === "urn:hbmp:impact-acknowledgement-required"
            ? t(S.staleImpact)
            : writeErrorText(applyWrite.error, lang)}
        </InlineAlert>
      )}
      {outcome && (
        <InlineAlert tone="ok">
          <span role="status" aria-live="polite">{t(S.applied)} {outcome}</span>
        </InlineAlert>
      )}

      <div className="row-actions">
        <Button onClick={runApply} disabled={applyDisabled}>
          {t(S.apply)}
        </Button>
      </div>
    </section>
  );
}

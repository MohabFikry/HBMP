import { useMemo, useState } from "react";
import {
  Button, Card, DataTable, InlineAlert, InputField, ComboboxField, Modal, StatusChip, useTheme,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import { availabilityApi, branchApi, rosterApi } from "../api/branchApi";
import type {
  AvailabilityRule, BranchPractitioner, BranchRef, CreateRosterExceptionBody, RosterException, RosterImpact,
  RosterKind,
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
    en: "The weekly pattern says when the clinic normally runs and how many patients each clinician takes. Exceptions say when it does not — leave, a public holiday, a closure — or when it runs extra.",
    ar: "يحدد النمط الأسبوعي مواعيد العمل المعتادة وعدد المرضى لكل إكلينيكي. أما الاستثناءات فتحدد متى لا تعمل العيادة — إجازة أو عطلة رسمية أو إغلاق — أو متى تعمل بشكل إضافي.",
  },
  whyNotDelete: {
    en: "Adding an exception leaves the weekly pattern intact. Deleting the pattern to cover one absence removes every other week too.",
    ar: "إضافة استثناء تُبقي النمط الأسبوعي كما هو. أما حذف النمط لتغطية غياب واحد فيلغي كل الأسابيع الأخرى أيضًا.",
  },

  // ── weekly pattern ────────────────────────────────────────────────────────────────────────────────────
  patternHeading: { en: "Weekly pattern", ar: "النمط الأسبوعي" },
  noPattern: {
    en: "No weekly pattern is recorded for this clinic yet. Until one is, no appointment slots are generated.",
    ar: "لا يوجد نمط أسبوعي مسجل لهذه العيادة بعد. وحتى يُسجَّل، لن تُنشأ أي مواعيد متاحة.",
  },
  clinician: { en: "Clinician", ar: "الإكلينيكي" },
  day: { en: "Day", ar: "اليوم" },
  hoursCol: { en: "Hours", ar: "الساعات" },
  slotLength: { en: "Slot length", ar: "مدة الموعد" },
  cap: { en: "Daily limit", ar: "الحد اليومي" },
  offered: { en: "Slots offered", ar: "المواعيد المتاحة" },
  noCap: { en: "No limit", ar: "بلا حد" },
  minutes: { en: "min", ar: "دقيقة" },
  capExplains: {
    en: "of {window} the hours allow",
    ar: "من {window} تسمح بها الساعات",
  },
  editPattern: { en: "Edit", ar: "تعديل" },
  historyAction: { en: "History", ar: "السجل" },

  editHeading: { en: "Edit the weekly pattern", ar: "تعديل النمط الأسبوعي" },
  startLabel: { en: "Starts", ar: "يبدأ" },
  endLabel: { en: "Ends", ar: "ينتهي" },
  slotMinutesLabel: { en: "Slot length (minutes)", ar: "مدة الموعد (بالدقائق)" },
  capLabel: { en: "Most patients per day", ar: "أقصى عدد مرضى في اليوم" },
  capHelp: {
    en: "Leave empty for no limit. The hours decide how many slots exist; this decides how many are offered. A six-hour day at 15 minutes offers 24 — a clinician who can safely see 20 says 20.",
    ar: "اتركه فارغًا لعدم وضع حد. الساعات تحدد عدد المواعيد الممكنة، وهذا يحدد عدد المعروض منها. يوم من ست ساعات بمواعيد ١٥ دقيقة يتيح ٢٤ موعدًا — والإكلينيكي الذي يمكنه استقبال ٢٠ بأمان يحدد ٢٠.",
  },
  savePattern: { en: "Save pattern", ar: "حفظ النمط" },
  patternSaved: { en: "Weekly pattern updated.", ar: "تم تحديث النمط الأسبوعي." },
  capMustBePositive: {
    en: "A limit of zero would close the clinic silently. Leave it empty for no limit, or record a closure below.",
    ar: "الحد صفر يغلق العيادة دون إشعار. اتركه فارغًا لعدم وضع حد، أو سجّل إغلاقًا أدناه.",
  },
  historyHeading: { en: "Change history", ar: "سجل التغييرات" },

  // ── exceptions ────────────────────────────────────────────────────────────────────────────────────────
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

/**
 * (design 42 §4/§6) — the roster: the weekly pattern the clinic normally runs, and the exceptions to it.
 *
 * <b>The weekly pattern band is new, and its absence was the defect.</b> This screen opened by telling the
 * coordinator that "the weekly pattern says when the clinic normally runs" and then showed only the
 * exceptions to it — because `emr.provider_availability` had no read endpoint anywhere on the platform. Leave
 * could be recorded; working hours could not be seen, changed or retired. The sentence was describing data
 * the screen had no way to fetch.
 *
 * <b>The impact preview is not advisory.</b> Apply is disabled until the operator has run it AND ticked that
 * they read the list, and the server independently refuses an apply whose acknowledged count no longer
 * matches what it computes. Both halves are needed: the client stops the careless click, and the server stops
 * the stale one — a preview taken twenty minutes ago, before two more people booked, must not silently cover
 * them.
 */
export function BranchRoster() {
  const t = useLoc();
  const { lang } = useTheme();
  const exceptions = useAsync(() => rosterApi.list(), []);
  const rules = useAsync(() => availabilityApi.list(), []);
  // Names for the ids the roster rows carry. Both reads are cheap reference data and both are needed by the
  // two bands, so they are fetched once here rather than by each.
  const people = useAsync(() => branchApi.practitioners({ includeUnlicensed: true }), []);
  const branches = useAsync(() => branchApi.branches(), []);

  const nameOf = useMemo(() => {
    const byId = new Map((people.data ?? []).map((p) => [p.practitionerId, p]));
    return (id: string | null): string | null => {
      if (!id) return null;
      const p = byId.get(id);
      if (!p) return id.slice(0, 8);
      return lang === "ar" ? p.fullNameAr : p.fullNameEn;
    };
  }, [people.data, lang]);

  return (
    <div className="branch-screen">
      <PageHeader title={t(S.title)} />
      <p className="muted lede">{t(S.intro)}</p>

      <WeeklyPattern rules={rules} nameOf={nameOf} onChanged={() => rules.reload()} />

      <InlineAlert tone="info">{t(S.whyNotDelete)}</InlineAlert>

      <Exceptions
        state={exceptions}
        nameOf={nameOf}
        onChanged={() => exceptions.reload()}
      />

      <RecordException
        lang={lang}
        practitioners={people.data ?? []}
        branches={branches.data ?? []}
        onApplied={() => exceptions.reload()}
      />
    </div>
  );
}

// ── The weekly pattern ──────────────────────────────────────────────────────────────────────────────────

function WeeklyPattern({
  rules,
  nameOf,
  onChanged,
}: {
  rules: ReturnType<typeof useAsync<AvailabilityRule[]>>;
  nameOf: (id: string | null) => string | null;
  onChanged: () => void;
}) {
  const t = useLoc();
  const [editing, setEditing] = useState<AvailabilityRule | null>(null);
  const [viewingHistory, setViewingHistory] = useState<AvailabilityRule | null>(null);

  const columns: Column<AvailabilityRule>[] = useMemo(
    () => [
      { key: "clinician", header: t(S.clinician), cell: (r) => nameOf(r.doctorId) ?? t(S.wholeClinic) },
      {
        key: "day", header: t(S.day),
        cell: (r) => t(DAY_LABEL[r.dayOfWeek] ?? DAY_LABEL[0]),
        sortable: true, sortValue: (r) => r.dayOfWeek,
      },
      { key: "hours", header: t(S.hoursCol), cell: (r) => `${r.startTime}–${r.endTime}` },
      { key: "slot", header: t(S.slotLength), cell: (r) => `${r.slotMinutes} ${t(S.minutes)}` },
      {
        key: "cap",
        header: t(S.cap),
        /*
          The cap and what it COSTS, together.

          A bare "12" leaves the reader to work out whether that is a restriction at all. Showing "12 of 16
          the hours allow" makes the two facts one sentence, and it is the sentence somebody is checking when
          they ask why the calendar looks shorter than the opening times.
        */
        cell: (r) =>
          r.maxPerDay === null ? (
            <span className="muted">{t(S.noCap)}</span>
          ) : (
            <span>
              <strong>{r.maxPerDay}</strong>{" "}
              <span className="muted">
                {t(S.capExplains).replace("{window}", String(r.slotsFromWindow))}
              </span>
            </span>
          ),
      },
      { key: "offered", header: t(S.offered), cell: (r) => String(r.slotsPerDay) },
      {
        key: "actions",
        header: "",
        cell: (r) => (
          <>
            <Button size="sm" variant="ghost" onClick={() => setEditing(r)}>{t(S.editPattern)}</Button>
            <Button size="sm" variant="ghost" onClick={() => setViewingHistory(r)}>{t(S.historyAction)}</Button>
          </>
        ),
      },
    ],
    [t, nameOf],
  );

  return (
    <>
      <h2>{t(S.patternHeading)}</h2>
      <AsyncSection state={rules} isEmpty={(rows) => rows.length === 0} emptyLabel={S.noPattern}>
        {(rows) => (
          <Card>
            <DataTable
              caption={t(S.patternHeading)}
              columns={columns}
              rows={rows}
              rowKey={(r) => r.availabilityId}
            />
          </Card>
        )}
      </AsyncSection>

      {editing && (
        <EditPattern
          rule={editing}
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
    </>
  );
}

function EditPattern({
  rule,
  onClose,
  onSaved,
}: {
  rule: AvailabilityRule;
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const { lang } = useTheme();
  const [startTime, setStartTime] = useState(rule.startTime);
  const [endTime, setEndTime] = useState(rule.endTime);
  const [slotMinutes, setSlotMinutes] = useState(String(rule.slotMinutes));
  // Empty string means "no limit", which is a different value from 0 — and 0 is refused, because a clinic
  // that takes nobody is a closure and closures carry a reason and an impact preview.
  const [cap, setCap] = useState(rule.maxPerDay === null ? "" : String(rule.maxPerDay));
  const [validation, setValidation] = useState<string | null>(null);
  const write = useWrite();

  const submit = async () => {
    const capValue = cap.trim() === "" ? null : Number(cap);
    if (capValue !== null && (!Number.isFinite(capValue) || capValue <= 0)) {
      setValidation(t(S.capMustBePositive));
      return;
    }
    setValidation(null);
    const ok = await write.run(() =>
      availabilityApi.update(rule.availabilityId, {
        providerId: rule.providerId,
        locationId: rule.locationId,
        doctorId: rule.doctorId ?? undefined,
        branchId: rule.branchId ?? undefined,
        dayOfWeek: rule.dayOfWeek,
        startTime,
        endTime,
        slotMinutes: Number(slotMinutes),
        maxPerDay: capValue,
      }),
    );
    if (ok) onSaved();
  };

  return (
    <Modal
      open
      onOpenChange={(next) => { if (!next) onClose(); }}
      title={t(S.editHeading)}
      description={`${t(DAY_LABEL[rule.dayOfWeek] ?? DAY_LABEL[0])} · ${rule.startTime}–${rule.endTime}`}
      footer={
        <>
          <Button onClick={submit} disabled={write.busy}>{t(S.savePattern)}</Button>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
        </>
      }
    >
      <InputField label={t(S.startLabel)} type="time" value={startTime} onChange={(e) => setStartTime(e.target.value)} required />
      <InputField label={t(S.endLabel)} type="time" value={endTime} onChange={(e) => setEndTime(e.target.value)} required />
      <InputField label={t(S.slotMinutesLabel)} type="number" min={1} value={slotMinutes} onChange={(e) => setSlotMinutes(e.target.value)} required />
      <InputField label={t(S.capLabel)} type="number" min={1} value={cap} onChange={(e) => setCap(e.target.value)} help={t(S.capHelp)} />
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
      footer={<Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>}
    >
      <AsyncSection state={history} isEmpty={(d) => d.entries.length === 0} emptyLabel={S.historyHeading}>
        {() => <ChangeTimeline entries={entries} />}
      </AsyncSection>
    </Modal>
  );
}

// ── Exceptions ──────────────────────────────────────────────────────────────────────────────────────────

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
          <Card>
            <DataTable caption={t(S.existing)} columns={columns} rows={rows} rowKey={(e) => e.exceptionId} />
          </Card>
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
    <Card>
      <h2>{t(S.addHeading)}</h2>

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

      <InputField label={t(S.from)} type="date" value={dateFrom} onChange={(e) => invalidate(setDateFrom)(e.target.value)} required />
      <InputField label={t(S.to)} type="date" value={dateTo} onChange={(e) => invalidate(setDateTo)(e.target.value)} />
      <InputField label={t(S.startTime)} type="time" value={startTime} onChange={(e) => invalidate(setStartTime)(e.target.value)} help={t(S.timeHelp)} />
      <InputField label={t(S.endTime)} type="time" value={endTime} onChange={(e) => invalidate(setEndTime)(e.target.value)} />
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
    </Card>
  );
}

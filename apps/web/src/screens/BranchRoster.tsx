import { useMemo, useState } from "react";
import { Button, Card, DataTable, InlineAlert, InputField, SelectField, StatusChip, useTheme } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import { rosterApi } from "../api/branchApi";
import type { CreateRosterExceptionBody, RosterException, RosterImpact, RosterKind } from "../api/branchApi";
import { useAsync } from "../api/useAsync";
import { useWrite, writeErrorText } from "../api/useWrite";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";
import type { Localized } from "../portals/catalog";

const S = {
  title: { en: "Roster & Availability", ar: "الجدول والإتاحة" },
  intro: {
    en: "The weekly pattern says when the clinic normally runs. Exceptions say when it does not — leave, a public holiday, a closure — or when it runs extra.",
    ar: "يحدد النمط الأسبوعي مواعيد العمل المعتادة. أما الاستثناءات فتحدد متى لا تعمل العيادة — إجازة أو عطلة رسمية أو إغلاق — أو متى تعمل بشكل إضافي.",
  },
  whyNotDelete: {
    en: "Adding an exception leaves the weekly pattern intact. Deleting the pattern to cover one absence removes every other week too.",
    ar: "إضافة استثناء تُبقي النمط الأسبوعي كما هو. أما حذف النمط لتغطية غياب واحد فيلغي كل الأسابيع الأخرى أيضًا.",
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
  applied: { en: "Exception applied.", ar: "تم تطبيق الاستثناء." },
  flaggedNoneCancelled: { en: "flagged for reassignment · 0 cancelled", ar: "مُعلَّم لإعادة التوزيع · 0 ملغى" },
  staleImpact: {
    en: "The number of affected appointments changed since you checked. Check the impact again.",
    ar: "تغيّر عدد المواعيد المتأثرة منذ الفحص. افحص الأثر مرة أخرى.",
  },
} satisfies Record<string, Localized>;

const KIND_LABEL: Record<RosterKind, Localized> = {
  Leave: S.kindLeave,
  PublicHoliday: S.kindHoliday,
  ClinicClosed: S.kindClosed,
  AdHocClinic: S.kindAdHoc,
};

/**
 * 25.7 (design 42 §4/§6) — the roster, and the IMPACT PREVIEW that gates every change to it.
 *
 * <b>The preview is not advisory.</b> Apply is disabled until the operator has run it AND ticked that they
 * read the list, and the server independently refuses an apply whose acknowledged count no longer matches
 * what it computes. Both halves are needed: the client stops the careless click, and the server stops the
 * stale one — a preview taken twenty minutes ago, before two more people booked, must not silently cover them.
 *
 * The list is rendered, not just the count. "8 appointments" is a number; the list is what lets a coordinator
 * recognise the two who cannot easily travel again.
 */
export function BranchRoster() {
  const t = useLoc();
  const { lang } = useTheme();
  const state = useAsync(() => rosterApi.list(), []);

  const columns: Column<RosterException>[] = useMemo(
    () => [
      { key: "kind", header: t(S.kind), cell: (e) => t(KIND_LABEL[e.kind]) },
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
    ],
    [t],
  );

  return (
    <div className="branch-screen">
      <PageHeader title={t(S.title)} />
      <p className="muted lede">{t(S.intro)}</p>
      <InlineAlert tone="info">{t(S.whyNotDelete)}</InlineAlert>

      <AsyncSection state={state} isEmpty={(rows) => rows.length === 0} emptyLabel={S.noneYet}>
        {(rows) => (
          <Card>
            <DataTable caption={t(S.existing)} columns={columns} rows={rows} rowKey={(e) => e.exceptionId} />
          </Card>
        )}
      </AsyncSection>

      <RecordException lang={lang} onApplied={() => state.reload()} />
    </div>
  );
}

function RecordException({ lang, onApplied }: { lang: "en" | "ar"; onApplied: () => void }) {
  const t = useLoc();
  const [kind, setKind] = useState<RosterKind>("Leave");
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
  });

  const runPreview = async () => {
    if (!dateFrom) { setValidation(t(S.from)); return; }
    if (!reason.trim()) { setValidation(t(S.needReason)); return; }
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

      <SelectField
        label={t(S.kind)}
        options={(Object.keys(KIND_LABEL) as RosterKind[]).map((k) => ({ value: k, label: t(KIND_LABEL[k]) }))}
        value={kind}
        onChange={(v) => invalidate(setKind)(v as RosterKind)}
      />

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

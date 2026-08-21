import { useMemo, useState } from "react";
import { Button, Card, DataTable, DataTableView, Icon, InlineAlert, InputField, Modal, StatusChip, useTableQuery, useTheme } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import { branchApi } from "../api/branchApi";
import type { BranchPractitioner, FlaggedAppointment, LicenceAlert, LicenceImpact } from "../api/branchApi";
import { useAsync } from "../api/useAsync";
import { useWrite, writeErrorText } from "../api/useWrite";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";
import { LicenceStatus } from "./branch/LicenceStatus";
import { ChangeTimeline } from "./branch/ChangeTimeline";
import type { TimelineEntry } from "./branch/ChangeTimeline";
import type { Localized } from "../portals/catalog";

const S = {
  practitionersTitle: { en: "Practitioners", ar: "الممارسون" },
  practitionersIntro: {
    en: "The clinicians who work at this clinic, and the licences that decide whether they can be booked.",
    ar: "الإكلينيكيون العاملون بهذه العيادة، والتراخيص التي تحدد إمكانية الحجز معهم.",
  },
  alertsTitle: { en: "Licence Alerts", ar: "تنبيهات التراخيص" },
  alertsIntro: {
    en: "An expired licence stops new bookings from the day it lapses. Appointments already booked are flagged for you to reassign — never cancelled automatically.",
    ar: "يوقف الترخيص المنتهي الحجوزات الجديدة من يوم انتهائه. أما المواعيد المحجوزة مسبقًا فتُعلَّم لإعادة توزيعها — ولا تُلغى تلقائيًا.",
  },

  name: { en: "Name", ar: "الاسم" },
  type: { en: "Type", ar: "النوع" },
  specialty: { en: "Specialty", ar: "التخصص" },
  licence: { en: "Licence", ar: "الترخيص" },
  licenceNo: { en: "Licence number", ar: "رقم الترخيص" },
  expiry: { en: "Expiry", ar: "تاريخ الانتهاء" },
  search: { en: "Search", ar: "بحث" },
  rosterSearchHint: { en: "Name, licence number or specialty", ar: "الاسم أو رقم الترخيص أو التخصص" },
  expiredFilter: { en: "Needs renewal", ar: "يحتاج تجديدًا" },
  validFilter: { en: "Valid", ar: "ساري" },
  noMatches: {
    en: "No clinicians match. Change the search or clear the filters.",
    ar: "لا يوجد إكلينيكيون مطابقون. عدّل البحث أو أزل عوامل التصفية.",
  },
  status: { en: "Licence status", ar: "حالة الترخيص" },
  clinics: { en: "Clinics", ar: "العيادات" },
  none: { en: "—", ar: "—" },
  masked: { en: "Not shown to you", ar: "غير معروض لك" },

  noPractitioners: { en: "No clinicians are assigned to this clinic yet.", ar: "لا يوجد إكلينيكيون معينون لهذه العيادة بعد." },
  noAlerts: { en: "No licence expires in the next 90 days.", ar: "لا يوجد ترخيص ينتهي خلال التسعين يومًا القادمة." },
  noFlagged: { en: "No appointments are waiting to be reassigned.", ar: "لا توجد مواعيد بانتظار إعادة التوزيع." },

  renew: { en: "Record renewal", ar: "تسجيل التجديد" },
  renewFor: { en: "Record a renewed licence", ar: "تسجيل ترخيص مجدد" },
  renewHelp: {
    en: "Both the number and the expiry date are required. A licence with no expiry cannot be checked against a booking date, so it would look recorded and gate nothing.",
    ar: "الرقم وتاريخ الانتهاء كلاهما مطلوب. الترخيص بلا تاريخ انتهاء لا يمكن التحقق منه مقابل تاريخ الحجز، فيبدو مسجلًا دون أن يمنع شيئًا.",
  },
  save: { en: "Save licence", ar: "حفظ الترخيص" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  saved: { en: "Licence updated.", ar: "تم تحديث الترخيص." },
  needBoth: { en: "Enter the licence number and its expiry date.", ar: "أدخل رقم الترخيص وتاريخ انتهائه." },

  flaggedHeading: { en: "Appointments needing reassignment", ar: "مواعيد تحتاج إعادة توزيع" },
  flaggedIntro: {
    en: "These were booked before the licence lapsed. Nobody has been cancelled — ring the patient and move them to a colleague.",
    ar: "حُجزت هذه المواعيد قبل انتهاء الترخيص. لم يُلغَ أحد — اتصل بالمستفيد وانقله إلى زميل آخر.",
  },
  patient: { en: "Patient", ar: "المستفيد" },
  when: { en: "When", ar: "الموعد" },
  flaggedSince: { en: "Flagged", ar: "تاريخ التعليم" },
  neverCancelled: { en: "Still booked", ar: "ما زال محجوزًا" },

  // ── the impact preview on a shortened expiry ──────────────────────────────────────────────────────────
  shorteningWarning: {
    en: "This expiry is earlier than the one on record. From the day after it, this clinician cannot be booked — and appointments already booked beyond it will need moving.",
    ar: "تاريخ الانتهاء هذا أبكر من المسجل. اعتبارًا من اليوم التالي له، لا يمكن الحجز مع هذا الإكلينيكي — وستحتاج المواعيد المحجوزة بعده إلى نقل.",
  },
  checkImpact: { en: "Check impact", ar: "فحص الأثر" },
  checking: { en: "Checking…", ar: "جارٍ الفحص…" },
  impactNone: {
    en: "No booked appointments fall beyond this date. Nothing will need reassigning.",
    ar: "لا توجد مواعيد محجوزة بعد هذا التاريخ. لن يحتاج أي موعد إلى إعادة توزيع.",
  },
  impactSome: { en: "booked appointment(s) fall beyond this date", ar: "موعد/مواعيد محجوزة تقع بعد هذا التاريخ" },
  impactExplain: {
    en: "None of these will be cancelled. Each is flagged so you can ring the patient and move them to a colleague. Confirm below once you have read the list.",
    ar: "لن يُلغى أي منها. سيُعلَّم كل موعد لتتمكن من الاتصال بالمستفيد ونقله إلى زميل آخر. أكّد أدناه بعد قراءة القائمة.",
  },
  acknowledge: { en: "I have read the affected appointments", ar: "لقد اطلعت على المواعيد المتأثرة" },
  mustCheckImpact: {
    en: "Check the impact before saving. The list of affected appointments is the point of this step.",
    ar: "افحص الأثر قبل الحفظ. قائمة المواعيد المتأثرة هي الغرض من هذه الخطوة.",
  },
  mustAcknowledge: { en: "Confirm you have read the affected appointments.", ar: "أكّد اطلاعك على المواعيد المتأثرة." },

  historyAction: { en: "History", ar: "السجل" },
  historyHeading: { en: "Licence history", ar: "سجل الترخيص" },
  statusField: { en: "Status", ar: "الحالة" },
} satisfies Record<string, Localized>;

// ── Practitioners ────────────────────────────────────────────────────────────────────────────────────────

/**
 * 25.7 (design 42 §2/§6) — the clinic's clinicians and their licences.
 *
 * Reuses `PractitionerAdmin`'s shape rather than forking its 673 lines: this screen is READ + the one write a
 * coordinator owns (recording a renewal). Creating a clinician, assigning specialties and network-wide
 * administration stay on the network team's screen, which already does all of that behind `provider:write`.
 */
export function BranchPractitioners() {
  const t = useLoc();
  const { lang } = useTheme();
  // `includeUnlicensed` is the whole point of THIS screen as against the booking picker: a coordinator must
  // see exactly the people the picker hides, because those are the ones needing action.
  const state = useAsync(() => branchApi.practitioners({ includeUnlicensed: true }), []);
  const [renewing, setRenewing] = useState<BranchPractitioner | null>(null);
  const [viewingHistory, setViewingHistory] = useState<BranchPractitioner | null>(null);

  const columns: Column<BranchPractitioner>[] = useMemo(
    () => [
      { key: "name", header: t(S.name), cell: (p) => (lang === "ar" ? p.fullNameAr : p.fullNameEn) },
      { key: "type", header: t(S.type), cell: (p) => p.practitionerType, sortable: true, sortValue: (p) => p.practitionerType },
      { key: "specialty", header: t(S.specialty), cell: (p) => p.primarySpecialty ?? t(S.none), sortable: true, sortValue: (p) => p.primarySpecialty },
      {
        key: "status",
        header: t(S.status),
        cell: (p) => (
          <LicenceStatus
            licenseExpiry={p.licenseExpiry}
            licenceValid={p.licenceValid}
            daysUntilExpiry={p.daysUntilExpiry}
            lang={lang}
          />
        ),
      },
      {
        key: "licenceNo",
        header: t(S.licenceNo),
        // A masked licence renders as words, not as a blank cell: "not shown to you" is a different fact
        // from "none recorded", and a blank makes them look the same.
        cell: (p) => p.licenseNo ?? <span className="muted">{t(S.masked)}</span>, sortable: true, sortValue: (p) => p.licenseNo },
      { key: "expiry", header: t(S.expiry), cell: (p) => p.licenseExpiry ?? t(S.none), sortable: true, sortValue: (p) => p.licenseExpiry },
      {
        key: "actions",
        header: "",
        cell: (p) => (
          <>
            <Button size="sm" variant="ghost" onClick={() => setRenewing(p)}>
              {t(S.renew)}
            </Button>
            <Button size="sm" variant="ghost" onClick={() => setViewingHistory(p)}>
              {t(S.historyAction)}
            </Button>
          </>
        ),
      },
    ],
    [t, lang],
  );

  /*
    A branch's clinicians, filtered by the thing this screen is FOR: whether their licence has expired. The
    roster grows with headcount, and the expiry column was something to scan rather than something to ask
    for — on the one screen whose purpose is finding the records that need renewing.
  */
  const query = useTableQuery({
    rows: state.data ?? [],
    columns,
    searchText: (p) => [p.fullNameEn, p.fullNameAr, p.licenseNo, p.primarySpecialty].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.rosterSearchHint),
    filters: [{
      key: "licence",
      label: t(S.status),
      options: [
        { value: "invalid", label: t(S.expiredFilter) },
        { value: "valid", label: t(S.validFilter) },
      ],
      // `licenceValid` is nullable — an unlicensed record is neither valid nor expired, and coercing
      // null to "needs renewal" would put clinicians who never had a licence into the renewal queue.
      match: (p, value) => (value === "valid" ? p.licenceValid === true : p.licenceValid === false),
    }],
    pageSize: 25,
    // Soonest expiry first — the list is worked from the end that is running out.
    initialSortKey: "expiry",
    persistKey: "branch-licences-roster",
  });

  return (
    <div className="branch-screen">
      <PageHeader title={t(S.practitionersTitle)} />
      <p className="muted lede">{t(S.practitionersIntro)}</p>
      <AsyncSection state={state} isEmpty={(rows) => rows.length === 0} emptyLabel={S.noPractitioners}>
        {() => (
            <Card>
              <DataTableView
                query={query}
                columns={columns}
                rowKey={(p) => p.practitionerId}
                caption={t(S.practitionersTitle)}
                emptyLabel={t(S.noPractitioners)}
                noMatchesLabel={t(S.noMatches)}
              />
            </Card>
        )}
      </AsyncSection>
      {renewing && (
        <RenewLicence
          practitioner={renewing}
          onClose={() => setRenewing(null)}
          onSaved={() => {
            setRenewing(null);
            state.reload();
          }}
        />
      )}
      {viewingHistory && (
        <LicenceHistory practitioner={viewingHistory} onClose={() => setViewingHistory(null)} />
      )}
    </div>
  );
}

/**
 * Record a renewed licence.
 *
 * <b>A `Modal`, not a card appended to the page.</b> This used to render as a `Card` after the table, so
 * clicking "Record renewal" on row 20 of a 25-row page put the form below the fold with nothing to say it had
 * opened: no focus move, no `role="dialog"`, no Esc, and no focus returned to the row afterwards. On the
 * portal's primary write. `Modal` is Radix Dialog and supplies all four.
 *
 * <b>Shortening an expiry runs an impact preview first.</b> The roster has required one before closing a
 * clinic day since 25.4, and bringing a licence forward strands appointments the same way — a doctor whose
 * expiry moves from December to September cannot lawfully see anybody in October or November. The server
 * flags those appointments either way (`PractitionerLicenceExpired`), so this is not a veto; it is the
 * operator seeing what they are about to cause while they can still choose a different date.
 */
function RenewLicence({
  practitioner,
  onClose,
  onSaved,
}: {
  practitioner: BranchPractitioner;
  onClose: () => void;
  onSaved: () => void;
}) {
  const t = useLoc();
  const { lang } = useTheme();
  const fmt = useFormat();
  const [licenseNo, setLicenseNo] = useState(practitioner.licenseNo ?? "");
  const [expiry, setExpiry] = useState(practitioner.licenseExpiry ?? "");
  const [validation, setValidation] = useState<string | null>(null);
  const [impact, setImpact] = useState<LicenceImpact | null>(null);
  const [acknowledged, setAcknowledged] = useState(false);
  const write = useWrite();
  const previewWrite = useWrite();

  /*
    Is this expiry EARLIER than the one on record? Only then can it strand anybody — a renewal pushing the
    date out invalidates nothing, and making somebody acknowledge an empty impact list on every routine
    renewal is how an acknowledgement becomes a reflex click.

    A licence with no expiry recorded counts as shortening: going from "unbounded" to a date bounds it.
  */
  const shortening = expiry !== "" && (
    practitioner.licenseExpiry === null || expiry < practitioner.licenseExpiry
  );

  // Any edit to the date invalidates a preview taken against the old one.
  const changeExpiry = (value: string) => {
    setExpiry(value);
    setImpact(null);
    setAcknowledged(false);
  };

  const runPreview = async () => {
    if (!expiry) { setValidation(t(S.needBoth)); return; }
    setValidation(null);
    await previewWrite.run(async () => {
      const result = await branchApi.licenceImpact(practitioner.practitionerId, expiry);
      setImpact(result);
      setAcknowledged(false);
      return result;
    });
  };

  const submit = async () => {
    if (!licenseNo.trim() || !expiry) {
      setValidation(t(S.needBoth));
      return;
    }
    if (shortening && impact === null) { setValidation(t(S.mustCheckImpact)); return; }
    if (shortening && (impact?.affectedCount ?? 0) > 0 && !acknowledged) {
      setValidation(t(S.mustAcknowledge));
      return;
    }
    setValidation(null);
    const ok = await write.run(() =>
      branchApi.updateLicence(practitioner.practitionerId, { licenseNo: licenseNo.trim(), licenseExpiry: expiry }),
    );
    if (ok) onSaved();
  };

  const blocked = write.busy
    || (shortening && impact === null)
    || (shortening && (impact?.affectedCount ?? 0) > 0 && !acknowledged);

  return (
    <Modal
      open
      onOpenChange={(next) => { if (!next) onClose(); }}
      title={t(S.renewFor)}
      description={lang === "ar" ? practitioner.fullNameAr : practitioner.fullNameEn}
      footer={
        <>
          <Button leadingIcon={<Icon name="check2" />} onClick={submit} disabled={blocked}>
            {t(S.save)}
          </Button>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
        </>
      }
    >
      <p className="muted">{t(S.renewHelp)}</p>
      <InputField label={t(S.licenceNo)} value={licenseNo} onChange={(e) => setLicenseNo(e.target.value)} required />
      <InputField label={t(S.expiry)} type="date" value={expiry} onChange={(e) => changeExpiry(e.target.value)} required />

      {shortening && (
        <>
          <InlineAlert tone="warn">{t(S.shorteningWarning)}</InlineAlert>
          <div className="row-actions">
            <Button variant="ghost" onClick={runPreview} disabled={previewWrite.busy}>
              {previewWrite.busy ? t(S.checking) : t(S.checkImpact)}
            </Button>
          </div>
        </>
      )}

      {previewWrite.error && <InlineAlert tone="bad">{writeErrorText(previewWrite.error, lang)}</InlineAlert>}

      {impact && (
        <div role="status" aria-live="polite">
          {impact.affectedCount === 0 ? (
            <InlineAlert tone="ok">{t(S.impactNone)}</InlineAlert>
          ) : (
            <>
              <InlineAlert tone="warn">{impact.affectedCount} {t(S.impactSome)}</InlineAlert>
              <p>{t(S.impactExplain)}</p>
              <DataTable
                caption={t(S.impactSome)}
                columns={[
                  { key: "patient", header: t(S.patient), cell: (a) => a.beneficiaryName ?? a.beneficiaryId.slice(0, 8) },
                  { key: "when", header: t(S.when), cell: (a) => fmt.dateTime(a.scheduledStart) },
                ]}
                rows={impact.affected}
                rowKey={(a) => a.appointmentId}
              />
              <label className="check">
                <input type="checkbox" checked={acknowledged} onChange={(e) => setAcknowledged(e.target.checked)} />
                <span>{t(S.acknowledge)}</span>
              </label>
            </>
          )}
        </div>
      )}

      {validation && <InlineAlert tone="warn">{validation}</InlineAlert>}
      {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
    </Modal>
  );
}

/** The licence's own timeline — who renewed it, when, and what the date was before. */
function LicenceHistory({
  practitioner,
  onClose,
}: {
  practitioner: BranchPractitioner;
  onClose: () => void;
}) {
  const t = useLoc();
  const { lang } = useTheme();
  const history = useAsync(
    () => branchApi.practitionerHistory(practitioner.practitionerId),
    [practitioner.practitionerId],
  );

  const entries: TimelineEntry[] = (history.data?.entries ?? []).map((e) => ({
    sequence: e.sequence,
    recordedAt: e.recordedAt,
    actorName: e.actorName,
    actorSubject: e.actorSubject,
    values: [
      { label: S.licenceNo, value: e.licenseNo },
      { label: S.expiry, value: e.licenseExpiry },
      { label: S.statusField, value: e.status },
    ],
  }));

  return (
    <Modal
      open
      onOpenChange={(next) => { if (!next) onClose(); }}
      title={t(S.historyHeading)}
      description={lang === "ar" ? practitioner.fullNameAr : practitioner.fullNameEn}
      wide
      footer={<Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>}
    >
      <AsyncSection state={history} isEmpty={(d) => d.entries.length === 0} emptyLabel={S.historyHeading}>
        {() => <ChangeTimeline entries={entries} />}
      </AsyncSection>
    </Modal>
  );
}

// ── Licence alerts + the flagged worklist ────────────────────────────────────────────────────────────────

/**
 * 25.7 (design 42 §3/§6) — the coordinator's licence worklist, and the appointments a lapse stranded.
 *
 * TWO TABLES, ONE SCREEN, deliberately. The alerts answer "who do I chase"; the flagged appointments answer
 * "who do I ring today". Splitting them across two nav items would let someone act on the first and never
 * discover the second — and the second is the one with a person waiting at the end of it.
 */
export function BranchLicenceAlerts() {
  const t = useLoc();
  const { lang } = useTheme();
  // 18.D2/U7 — Cairo-pinned, app-locale formatting. A bare toLocaleString renders in the MACHINE's zone,
  // so a clinic PC set to UTC shows a 09:00 appointment as 07:00 and the patient is told the wrong time.
  const fmt = useFormat();
  const alerts = useAsync(() => branchApi.licenceAlerts(90), []);
  const flagged = useAsync(() => branchApi.reassignmentNeeded(), []);
  const [renewing, setRenewing] = useState<BranchPractitioner | null>(null);

  const alertColumns: Column<LicenceAlert>[] = useMemo(
    () => [
      { key: "name", header: t(S.name), cell: (a) => (lang === "ar" ? a.fullNameAr : a.fullNameEn) },
      { key: "type", header: t(S.type), cell: (a) => a.practitionerType, sortable: true, sortValue: (a) => a.practitionerType },
      {
        key: "status",
        header: t(S.status),
        cell: (a) => (
          <LicenceStatus
            licenseExpiry={a.licenseExpiry}
            licenceValid={a.status !== "Expired"}
            daysUntilExpiry={a.daysUntilExpiry}
            lang={lang}
          />
        ),
      },
      { key: "expiry", header: t(S.expiry), cell: (a) => a.licenseExpiry ?? t(S.none), sortable: true, sortValue: (a) => a.licenseExpiry },
      { key: "clinics", header: t(S.clinics), cell: (a) => String(a.branches.length), sortable: true, sortValue: (a) => a.branches.length },
      {
        key: "actions",
        header: "",
        /*
          THE ACTION, on the worklist that identifies the work.

          This table answers "who do I chase" and offered no way to do anything about it: the operator had to
          hold a name in their head, navigate to Practitioners, search for it, and find the row again. On a
          worklist whose entire purpose is that these records need action, and whose rows are sorted by how
          soon they run out.

          The same modal as the Practitioners screen, deliberately — including the impact preview. A renewal
          recorded from here has exactly the consequences a renewal recorded from there does, and a second,
          lighter form would be the one somebody uses to shorten an expiry without seeing whom it strands.
        */
        cell: (a) => (
          <Button size="sm" variant="ghost" onClick={() => setRenewing(alertAsPractitioner(a))}>
            {t(S.renew)}
          </Button>
        ),
      },
    ],
    [t, lang],
  );

  const flaggedColumns: Column<FlaggedAppointment>[] = useMemo(
    () => [
      { key: "patient", header: t(S.patient), cell: (a) => a.beneficiaryName ?? a.beneficiaryId.slice(0, 8), sortable: true, sortValue: (a) => a.beneficiaryName },
      { key: "when", header: t(S.when), cell: (a) => fmt.dateTime(a.scheduledStart), sortable: true, sortValue: (a) => a.scheduledStart },
      { key: "flaggedSince", header: t(S.flaggedSince), cell: (a) => fmt.date(a.reassignmentNeededAt), sortable: true, sortValue: (a) => a.reassignmentNeededAt },
      {
        key: "status",
        header: t(S.status),
        // Says STILL BOOKED, in words. The single most important thing this table communicates is that the
        // system did not cancel anybody — the operator must know the patient is still expecting to come.
        cell: () => <StatusChip kind="info" label={t(S.neverCancelled)} />,
      },
    ],
    [t, lang, fmt],
  );

  return (
    <>
      <PageHeader title={t(S.alertsTitle)} />
      <p className="muted lede">{t(S.alertsIntro)}</p>

      <AsyncSection state={alerts} isEmpty={(d) => d.alerts.length === 0} emptyLabel={S.noAlerts}>
        {(data) => (
            <Card>
              <DataTable
                caption={t(S.alertsTitle)}
                columns={alertColumns}
                rows={data.alerts}
                rowKey={(a) => a.practitionerId}
              />
            </Card>
        )}
      </AsyncSection>

      <h2>{t(S.flaggedHeading)}</h2>
      <p className="muted lede">{t(S.flaggedIntro)}</p>
      <AsyncSection state={flagged} isEmpty={(d) => d.appointments.length === 0} emptyLabel={S.noFlagged}>
        {(data) => (
            <Card>
              <DataTable
                caption={t(S.flaggedHeading)}
                columns={flaggedColumns}
                rows={data.appointments}
                rowKey={(a) => a.appointmentId}
              />
            </Card>
        )}
      </AsyncSection>

      {renewing && (
        <RenewLicence
          practitioner={renewing}
          onClose={() => setRenewing(null)}
          onSaved={() => {
            setRenewing(null);
            // Both tables: a renewal clears the alert AND stops adding to the flagged list, and a screen that
            // refreshed only the half you were looking at would leave the other contradicting it.
            alerts.reload();
            flagged.reload();
          }}
        />
      )}
    </>
  );
}

/**
 * A licence alert, in the shape the renewal modal takes.
 *
 * The alert row carries everything the modal reads — id, both names, the number and the expiry — so this is a
 * projection rather than a fetch. The unused fields are filled with what the alert actually implies: an alert
 * exists because a licence exists, and the practitioner is by definition still Active. Inventing a specialty
 * or a branch list would put values on screen that nothing checked, so those stay empty.
 */
function alertAsPractitioner(a: LicenceAlert): BranchPractitioner {
  return {
    practitionerId: a.practitionerId,
    practitionerType: a.practitionerType,
    fullNameEn: a.fullNameEn,
    fullNameAr: a.fullNameAr,
    primarySpecialty: null,
    specialties: [],
    branches: a.branches,
    status: "Active",
    licenseNo: a.licenseNo,
    licenseExpiry: a.licenseExpiry,
    licenceValid: a.status !== "Expired",
    daysUntilExpiry: a.daysUntilExpiry,
  };
}

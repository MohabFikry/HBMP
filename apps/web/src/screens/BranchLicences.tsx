import { useMemo, useState } from "react";
import { Button, Card, DataTable, InlineAlert, InputField, StatusChip, useTheme } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import { branchApi } from "../api/branchApi";
import type { BranchPractitioner, FlaggedAppointment, LicenceAlert } from "../api/branchApi";
import { useAsync } from "../api/useAsync";
import { useWrite, writeErrorText } from "../api/useWrite";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";
import { LicenceStatus } from "./branch/LicenceStatus";
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

  const columns: Column<BranchPractitioner>[] = useMemo(
    () => [
      { key: "name", header: t(S.name), cell: (p) => (lang === "ar" ? p.fullNameAr : p.fullNameEn) },
      { key: "type", header: t(S.type), cell: (p) => p.practitionerType },
      { key: "specialty", header: t(S.specialty), cell: (p) => p.primarySpecialty ?? t(S.none) },
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
        cell: (p) => p.licenseNo ?? <span className="muted">{t(S.masked)}</span>,
      },
      { key: "expiry", header: t(S.expiry), cell: (p) => p.licenseExpiry ?? t(S.none) },
      {
        key: "actions",
        header: "",
        cell: (p) => (
          <Button variant="ghost" onClick={() => setRenewing(p)}>
            {t(S.renew)}
          </Button>
        ),
      },
    ],
    [t, lang],
  );

  return (
    <div className="branch-screen">
      <PageHeader title={t(S.practitionersTitle)} />
      <p className="muted lede">{t(S.practitionersIntro)}</p>
      <AsyncSection state={state} isEmpty={(rows) => rows.length === 0} emptyLabel={S.noPractitioners}>
        {(rows) => (
            <Card>
              <DataTable
                caption={t(S.practitionersTitle)}
                columns={columns}
                rows={rows}
                rowKey={(p) => p.practitionerId}
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
    </div>
  );
}

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
  const [licenseNo, setLicenseNo] = useState(practitioner.licenseNo ?? "");
  const [expiry, setExpiry] = useState(practitioner.licenseExpiry ?? "");
  const [validation, setValidation] = useState<string | null>(null);
  const write = useWrite();

  const submit = async () => {
    if (!licenseNo.trim() || !expiry) {
      setValidation(t(S.needBoth));
      return;
    }
    setValidation(null);
    const ok = await write.run(() =>
      branchApi.updateLicence(practitioner.practitionerId, { licenseNo: licenseNo.trim(), licenseExpiry: expiry }),
    );
    if (ok) onSaved();
  };

  return (
    <Card>
      <h2>{t(S.renewFor)}</h2>
      <p>{lang === "ar" ? practitioner.fullNameAr : practitioner.fullNameEn}</p>
      <p className="muted">{t(S.renewHelp)}</p>
      <InputField label={t(S.licenceNo)} value={licenseNo} onChange={(e) => setLicenseNo(e.target.value)} required />
      <InputField label={t(S.expiry)} type="date" value={expiry} onChange={(e) => setExpiry(e.target.value)} required />
      {validation && <InlineAlert tone="warn">{validation}</InlineAlert>}
      {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
      <div className="row-actions">
        <Button onClick={submit} disabled={write.busy}>
          {t(S.save)}
        </Button>
        <Button variant="ghost" onClick={onClose}>
          {t(S.cancel)}
        </Button>
      </div>
    </Card>
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

  const alertColumns: Column<LicenceAlert>[] = useMemo(
    () => [
      { key: "name", header: t(S.name), cell: (a) => (lang === "ar" ? a.fullNameAr : a.fullNameEn) },
      { key: "type", header: t(S.type), cell: (a) => a.practitionerType },
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
      { key: "expiry", header: t(S.expiry), cell: (a) => a.licenseExpiry ?? t(S.none) },
      { key: "clinics", header: t(S.clinics), cell: (a) => String(a.branches.length) },
    ],
    [t, lang],
  );

  const flaggedColumns: Column<FlaggedAppointment>[] = useMemo(
    () => [
      { key: "patient", header: t(S.patient), cell: (a) => a.beneficiaryName ?? a.beneficiaryId.slice(0, 8) },
      { key: "when", header: t(S.when), cell: (a) => fmt.dateTime(a.scheduledStart) },
      { key: "flaggedSince", header: t(S.flaggedSince), cell: (a) => fmt.date(a.reassignmentNeededAt) },
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
    </>
  );
}

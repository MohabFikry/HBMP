import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, DataTable, StatusChip, InlineAlert } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AppointmentRow, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { ApiError } from "../api/http";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { VisitTimelineButton } from "./VisitTimeline";

const S = {
  title: { en: "My Visits", ar: "زياراتي" },
  empty: {
    en: "No patients of yours have checked in yet today.",
    ar: "لم يسجّل أي من مرضاك وصوله اليوم بعد.",
  },
  scope: {
    en: "Only appointments assigned to you, in your active branch.",
    ar: "المواعيد المسنَدة إليك فقط، في فرعك النشط.",
  },
  beneficiary: { en: "Patient", ar: "المريض" },
  type: { en: "Type", ar: "النوع" },
  time: { en: "Time", ar: "الوقت" },
  status: { en: "Status", ar: "الحالة" },
  actions: { en: "Actions", ar: "الإجراءات" },
  openFile: { en: "Patient file", ar: "ملف المريض" },
  startVisit: { en: "Start visit", ar: "بدء الزيارة" },
  waiting: {
    en: "Waiting for the desk to check this patient in.",
    ar: "بانتظار تسجيل وصول المريض من الاستقبال.",
  },
  notYours: {
    en: "This appointment is assigned to another practitioner.",
    ar: "هذا الموعد مسنَد إلى طبيب آخر.",
  },
  stale: {
    en: "This appointment changed since the list loaded — refreshing.",
    ar: "تغيّر هذا الموعد منذ تحميل القائمة — يجري التحديث.",
  },
} satisfies Record<string, Localized>;

/**
 * The doctor's own day list (23 §1). Two narrowings, both server-side: `?mine=true` resolves the practitioner
 * from the TOKEN's subject — a doctor asking for "my visits" must not be able to ask for a colleague's by
 * editing a query parameter — and branch scope narrows it to the active branch on top of that.
 *
 * "Start visit" appears only on a CheckedIn row, because a visit records care for someone who is in the
 * building. The rule is enforced where the encounter is created, not here: this button is a convenience over
 * POST /encounters, and a caller reaching that endpoint directly meets the same two checks.
 */
export function DoctorVisits() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Cairo times, app locale
  const navigate = useNavigate();
  const state = useAsync<AppointmentRow[]>(() => api.appointments("all", true), []);
  const [busy, setBusy] = useState<string | null>(null);
  const [stale, setStale] = useState(false);
  const [denied, setDenied] = useState<Localized | null>(null);

  async function start(row: AppointmentRow) {
    setBusy(row.id);
    setStale(false);
    setDenied(null);
    try {
      const { encounterId } = await api.startVisit(row.id, row.beneficiary.id);
      // Straight into the workspace: starting a visit and then hunting for it is two steps for one intent.
      navigate(encounterId ? `/clinician/encounter?encounter=${encodeURIComponent(encounterId)}` : "/clinician/encounter");
    } catch (e) {
      if (e instanceof ApiError && e.status === 403) {
        // The server refused the treating relationship. Say which refusal it was.
        setDenied(S.notYours);
        state.reload();
      } else if (e instanceof ApiError && (e.status === 409 || e.status === 412)) {
        // The row moved: checked out, cancelled, or a visit already open elsewhere.
        setStale(true);
        state.reload();
      } else {
        throw e;
      }
    } finally {
      setBusy(null);
    }
  }

  const cols: Column<AppointmentRow>[] = [
    { key: "beneficiary", header: t(S.beneficiary), cell: (r) => <span className="tnum">{r.beneficiary.token}</span> },
    { key: "type", header: t(S.type), cell: (r) => r.appointmentType },
    { key: "time", header: t(S.time), cell: (r) => <span className="tnum">{fmt.time(r.scheduledStart)}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    {
      key: "file",
      // A real header, not "": an empty <th> has no accessible name, so a screen-reader user hears nothing
      // for the column their cursor is in (axe empty-table-header).
      header: t(S.openFile),
      cell: (r) => (
        <Button variant="secondary" size="sm" onClick={() => navigate(`/patients/${encodeURIComponent(r.beneficiary.id)}`)}>
          {t(S.openFile)}
        </Button>
      ),
    },
    {
      key: "actions",
      header: t(S.actions),
      cell: (r) => (
        <span className="row-actions">
          <VisitTimelineButton row={r} />
          {r.startVisitEligible ? (
            <Button variant="primary" size="sm" loading={busy === r.id} onClick={() => void start(r)}>
              {t(S.startVisit)}
            </Button>
          ) : r.checkInEligible ? (
            // A Booked row is not the doctor's to act on yet — say what is being waited for rather than
            // rendering a dead cell the clinician reads as a broken screen.
            <span className="muted">{t(S.waiting)}</span>
          ) : null}
        </span>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <p className="muted">{t(S.scope)}</p>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {(rows) => (
            <div aria-live="polite">
              {stale && <StatusChip kind="warn" label={t(S.stale)} />}
              {denied && <InlineAlert tone="bad">{t(denied)}</InlineAlert>}
              <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.title)} />
            </div>
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

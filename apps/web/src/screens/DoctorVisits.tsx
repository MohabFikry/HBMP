import { useMemo, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { Button, Card, DataTableView, Icon, InlineAlert, StatusChip, useTableQuery } from "@mersal/design-system";
import type { Column, TableFilterSpec } from "@mersal/design-system";
import type { AppointmentRow, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useRefreshOnFocus } from "../api/useRefreshOnFocus";
import { ApiError } from "../api/http";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc, useOpenProfile } from "./_shared";
import { VisitTimelineButton } from "./VisitTimeline";
import { patientColumn } from "./booking/appointmentColumns";

const S = {
  title: { en: "My Visits", ar: "زياراتي" },
  empty: {
    en: "No patients of yours have checked in yet today.",
    ar: "لم يسجّل أي من مرضاك وصوله اليوم بعد.",
  },
  noMatches: {
    en: "No visits match. Change the search or clear the filters.",
    ar: "لا توجد زيارات مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },
  search: { en: "Search", ar: "بحث" },
  searchHint: { en: "Patient, type or status", ar: "المريض أو النوع أو الحالة" },
  statusFilter: { en: "Status", ar: "الحالة" },
  // The five appointment statuses, as the filter's option labels. Same wording as the chips in the Status
  // column (`apptStatusChip`) — an option reading "Arrived" against a chip reading "Checked in" is two names
  // for one state, and the operator has to work out that they are the same thing.
  stCheckedIn: { en: "Checked in", ar: "تم الوصول" },
  stBooked: { en: "Booked", ar: "محجوز" },
  stCompleted: { en: "Completed", ar: "مكتمل" },
  stNoShow: { en: "No-show", ar: "لم يحضر" },
  stCancelled: { en: "Cancelled", ar: "ملغى" },
  type: { en: "Type", ar: "النوع" },
  time: { en: "Time", ar: "الوقت" },
  status: { en: "Status", ar: "الحالة" },
  actions: { en: "Actions", ar: "الإجراءات" },
  openFile: { en: "Patient file", ar: "ملف المريض" },
  startVisit: { en: "Start visit", ar: "بدء الزيارة" },
  // The `title` on the DISABLED "Start visit" button — why it is not available yet. It is no longer a column
  // of its own: the word "Pending" sat where every other row had a button, so "can I start this visit?" had
  // to be inferred from the absence of a control rather than read off one.
  awaitingCheckIn: {
    en: "Available once reception checks this patient in.",
    ar: "يتاح بعد تسجيل وصول المريض في الاستقبال.",
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
  const location = useLocation();
  // Separate from `navigate` because it also records the origin, so the profile's Back control returns to
  // this board rather than guessing from history.
  const openProfile = useOpenProfile();
  const state = useAsync<AppointmentRow[]>(() => api.appointments("all", true), []);
  // This board is not the only writer of its own rows. Reception checks patients in, the encounter workspace
  // ends the visit, and both happen while this list is sitting on screen — so a day board left open keeps
  // offering "Start visit" against an appointment that was completed half an hour ago. Coming back to the tab
  // re-asks the server rather than trusting a snapshot of a clinic that has moved on.
  useRefreshOnFocus(state.reload);
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
      //
      // WITH the origin. Without it the workspace had no way back to this board at all — `useBackTarget`
      // renders nothing when there is neither a `from` nor history behind the entry, and "Start visit" is the
      // single most-used door into the workspace. The doctor finished a consultation and had to reach for the
      // nav rail to get back to their own day.
      navigate(
        encounterId ? `/clinician/encounter?encounter=${encodeURIComponent(encounterId)}` : "/clinician/encounter",
        { state: { from: `${location.pathname}${location.search}` } },
      );
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
    // The shared column: the patient's NAME, with the masked token as the fallback for a row booked before
    // names were captured. The treating doctor is the caller most entitled to it — they are about to call
    // this person into a room — and "•••4821" is unusable for that.
    patientColumn({ t }),
    { key: "type", header: t(S.type), cell: (r) => r.appointmentType, sortable: true,
      sortValue: (r) => r.appointmentType },
    // Sorts on the INSTANT, not on the rendered time: the cell reads "9:00 ص" in Arabic, and sorting that
    // string orders the clinic by glyph.
    { key: "time", header: t(S.time), cell: (r) => <span className="tnum">{fmt.time(r.scheduledStart)}</span>,
      sortable: true, sortValue: (r) => r.scheduledStart },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />,
      sortable: true, sortValue: (r) => r.status.label.en },
    {
      key: "file",
      // A real header, not "": an empty <th> has no accessible name, so a screen-reader user hears nothing
      // for the column their cursor is in (axe empty-table-header).
      header: t(S.openFile),
      // Icon + primary, the platform's patient-file action (`patientFileColumn` on reception's boards). Those
      // rows already carry a second primary — check-in — so the pairing with "Start visit" here is the same
      // shape and not a hierarchy this screen invents.
      cell: (r) => (
        <Button
          variant="primary"
          size="sm"
          leadingIcon={<Icon name="user" />}
          onClick={() => openProfile(r.beneficiary.id)}
        >
          {t(S.openFile)}
        </Button>
      ),
    },
    {
      key: "actions",
      header: t(S.actions),
      cell: (r) => {
        // ONE control, in two states, rather than a button on some rows and a word on others.
        //
        // A Booked row used to render the word "Pending" where every other row had a button. That said what
        // the Status column two cells to its left already said, and it said it in the shape of a label — so
        // the thing a doctor actually wants to know ("can I start this visit yet?") had to be inferred from
        // the ABSENCE of a control. A disabled button answers it directly: the action is visibly there, and
        // visibly not available yet.
        //
        // Disabled ONLY while the patient has not arrived. Once reception checks them in the same button
        // lights up in place, which is the state change the doctor is waiting for.
        const canStart = r.startVisitEligible;
        // Neither startable nor awaiting check-in — completed, cancelled, no-show. Nothing to offer at all;
        // a permanently dead button on a finished visit is worse than no button.
        if (!canStart && !r.checkInEligible) return <span className="row-actions"><VisitTimelineButton row={r} /></span>;
        return (
          <span className="row-actions">
            <VisitTimelineButton row={r} />
            <Button
              variant="primary"
              size="sm"
              leadingIcon={<Icon name="stethoscope" />}
              loading={busy === r.id}
              // `disabled`, which the DS renders with aria-disabled — the control keeps its place in the tab
              // order and keeps announcing itself, so a screen-reader user hears that starting the visit is
              // the action here and that it is not available yet. `title` says WHY, because a disabled
              // control with no explanation is the most common way an interface stops making sense.
              disabled={!canStart}
              title={canStart ? undefined : t(S.awaitingCheckIn)}
              onClick={canStart ? () => void start(r) : undefined}
            >
              {t(S.startVisit)}
            </Button>
          </span>
        );
      },
    },
  ];

  /**
   * Status filter.
   *
   * Matched on `status.label.en`, because the row carries the status only as a pre-resolved chip — the raw
   * emr enum is mapped to `{kind, label}` at the client boundary (`apptStatusChip`) so the four-cue
   * guarantee is consistent across screens and locales. The English label is the stable half of that pair;
   * matching the LOCALIZED one would break every filter the moment the portal is switched to Arabic.
   */
  const filters: TableFilterSpec<AppointmentRow>[] = useMemo(() => [
    {
      key: "status",
      label: t(S.statusFilter),
      // The `value` IS the English label, which is what `match` compares against.
      options: [
        { value: S.stCheckedIn.en, label: t(S.stCheckedIn) },
        { value: S.stBooked.en,    label: t(S.stBooked) },
        { value: S.stCompleted.en, label: t(S.stCompleted) },
        { value: S.stNoShow.en,    label: t(S.stNoShow) },
        { value: S.stCancelled.en, label: t(S.stCancelled) },
      ],
      match: (r, value) => r.status.label.en === value,
    },
  ], [t]);

  // The hook has to run on every render, so it reads the loaded rows directly rather than from inside
  // AsyncSection's render prop — a hook called there would be conditional on the load having finished.
  const rows = state.data ?? [];
  const query = useTableQuery<AppointmentRow>({
    rows,
    columns: cols,
    // Everything a doctor might type while looking at their day: the patient in front of them, the token off
    // a queue ticket, or the status word they can see on screen. Both languages of the status, because the
    // portal switches and a search that only matched English would go quiet in Arabic.
    searchText: (r) => [
      r.beneficiaryName, r.beneficiary.token, r.appointmentType,
      r.status.label.en, r.status.label.ar, r.branchName, r.note,
    ].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.searchHint),
    filters,
    pageSize: 10,
    // A clinic runs in time order, so that is the order it opens in.
    initialSortKey: "time",
    initialSortDir: "ascending",
    // Opening a patient file and coming back returns to the same search, filter and page. Without it the
    // doctor loses their place every time they check a record — the row they were on is somewhere in an
    // unfiltered page 1.
    persistKey: "doctor-visits",
  });

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {() => (
            <div aria-live="polite">
              {stale && <StatusChip kind="warn" label={t(S.stale)} />}
              {denied && <InlineAlert tone="bad">{t(denied)}</InlineAlert>}
              <DataTableView
                query={query}
                columns={cols}
                rowKey={(r) => r.id}
                caption={t(S.title)}
                emptyLabel={t(S.empty)}
                noMatchesLabel={t(S.noMatches)}
              />
            </div>
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

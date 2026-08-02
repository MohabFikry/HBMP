import { useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, DataTableView, StatusChip, InlineAlert, useTableQuery } from "@mersal/design-system";
import type { Column, TableFilterSpec } from "@mersal/design-system";
import type { AppointmentRow, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
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
  // One word, in the ACTIONS column, where the question is "what can I do with this row?" — the answer is
  // "nothing yet". The sentence that used to be here ("Waiting for the desk to check this patient in")
  // re-stated the Status column two cells to its left in longer form, and cost the widest part of the table
  // to do it.
  pending: { en: "Pending", ar: "قيد الانتظار" },
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
  // Separate from `navigate` because it also records the origin, so the profile's Back control returns to
  // this board rather than guessing from history.
  const openProfile = useOpenProfile();
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
      cell: (r) => (
        <Button variant="secondary" size="sm" onClick={() => openProfile(r.beneficiary.id)}>
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
            // A Booked row is not the doctor's to act on yet. One quiet word rather than an empty cell, which
            // a clinician reads as a broken screen.
            <span className="muted">{t(S.pending)}</span>
          ) : null}
        </span>
      ),
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

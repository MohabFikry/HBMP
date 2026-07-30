import { Card, DataTable, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { AppointmentRow, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { VisitTimelineButton } from "./VisitTimeline";
import { AppointmentNoteButton } from "./AppointmentNote";

const S = {
  title: { en: "Appointments", ar: "المواعيد" },
  empty: { en: "No appointments today in any branch you can reach.", ar: "لا توجد مواعيد اليوم في أي فرع متاح لك." },
  scope: {
    en: "Every branch you can reach — the call centre is not tied to one.",
    ar: "كل الفروع المتاحة لك — مركز الاتصال غير مرتبط بفرع واحد.",
  },
  /**
   * Why this board has no Book / Reschedule / Cancel. Those act on a member, and 15.x lets the call centre act
   * only inside a call whose caller has been VERIFIED — that is the whole point of verify-before-disclose. A
   * button here would have no interaction to attach to and the server would refuse it, so the board says where
   * the actions live instead of offering ones that cannot work.
   */
  actionsNote: {
    en: "To book, reschedule or cancel, open a call and verify the caller — reservations belong to a verified call.",
    ar: "للحجز أو إعادة الجدولة أو الإلغاء، افتح مكالمة وتحقّق من هوية المتصل — يرتبط الحجز بمكالمة مُتحقَّق منها.",
  },
  arrivals: {
    en: "Arrivals — check-in and no-show — are recorded by the branch desk, never here.",
    ar: "تسجيل الوصول وعدم الحضور يقوم بهما مكتب الفرع، وليس من هنا.",
  },
  beneficiary: { en: "Beneficiary", ar: "المستفيد" },
  type: { en: "Type", ar: "النوع" },
  time: { en: "Time", ar: "الوقت" },
  status: { en: "Status", ar: "الحالة" },
  note: { en: "Note", ar: "ملاحظة" },
  branch: { en: "Branch", ar: "الفرع" },
  noBranch: { en: "External", ar: "خارجي" },
} satisfies Record<string, Localized>;

/**
 * The call centre's cross-branch appointment board (15.3).
 *
 * READ-ONLY by construction, and that is not a shortcut: the call centre holds appointment:read and
 * appointment:reserve, and every reserve path runs through callcentre-service, which requires a verified
 * interaction. There is no verified caller on a board, so there is nothing legitimate to act on here.
 *
 * Cross-branch needs no control: the role is branch-unrestricted, so GET /appointments already spans every
 * branch it can reach. A branch picker would only ever narrow what it is entitled to see.
 */
export function CallCentreAppointments() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Cairo times, app locale
  const state = useAsync<AppointmentRow[]>(() => api.appointments("all"), []);

  const cols: Column<AppointmentRow>[] = [
    { key: "beneficiary", header: t(S.beneficiary), cell: (r) => <span className="tnum">{r.beneficiary.token}</span> },
    // WHICH branch, on a board that deliberately spans all of them. Without it the agent can tell a caller the
    // time of an appointment but not where to go, which is worse than not showing it at all.
    { key: "branch", header: t(S.branch), cell: (r) => r.branchName ?? (r.branchId ? "—" : t(S.noBranch)) },
    { key: "type", header: t(S.type), cell: (r) => r.appointmentType },
    { key: "time", header: t(S.time), cell: (r) => <span className="tnum">{fmt.time(r.scheduledStart)}</span> },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    // The timeline IS legitimate here: it answers "who moved this and when", which is most of what a member
    // rings to ask, and it needs no verified interaction because it discloses no identity.
    // 14.5 — the booking note. The call centre WRITES these, so it must be able to read back what it told
    // the clinic; the same note the doctor sees, from the same field.
    {
      key: "note", header: t(S.note),
      cell: (r) => <AppointmentNoteButton note={r.note} />,
    },
    { key: "timeline", header: "", cell: (r) => <VisitTimelineButton row={r} /> },
  ];

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <p className="muted">{t(S.scope)}</p>
        <p className="muted">{t(S.actionsNote)}</p>
        <p className="muted">{t(S.arrivals)}</p>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.title)} />}
        </AsyncSection>
      </Card>
    </>
  );
}

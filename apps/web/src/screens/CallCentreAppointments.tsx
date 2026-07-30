import { useMemo, useState } from "react";
import { Card, Combobox, DataTable, InlineAlert, InputField, TableToolbar } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type {
  AppointmentRow, BranchSummary, Localized, Practitioner, Specialty,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { VisitTimelineButton } from "./VisitTimeline";
import { doctorColumns, noteColumn, patientColumn, timeAndStatusColumns } from "./booking/appointmentColumns";
import { CallCentreCancelButton } from "./CallCentreCancel";
import { EditAppointmentButton } from "./booking/EditAppointment";
import { createHttpCcApi, type CcApi } from "./CallCentre";

/** ONE client for the module — a per-render instance makes every effort keyed on `api` re-run. */
const defaultCcApi = createHttpCcApi();

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
    en: "Cancelling here verifies the caller first. To book or reschedule, open a call — reservations belong to a verified call.",
    ar: "الإلغاء من هنا يتطلب التحقق من هوية المتصل أولاً. للحجز أو إعادة الجدولة، افتح مكالمة — يرتبط الحجز بمكالمة مُتحقَّق منها.",
  },
  arrivals: {
    en: "Arrivals — check-in and no-show — are recorded by the branch desk, never here.",
    ar: "تسجيل الوصول وعدم الحضور يقوم بهما مكتب الفرع، وليس من هنا.",
  },
  branch: { en: "Branch", ar: "الفرع" },
  cancelHeader: { en: "Cancel", ar: "إلغاء" },
  editHeader: { en: "Edit", ar: "تعديل" },
  timeline: { en: "Timeline", ar: "المسار" },
  noBranch: { en: "External", ar: "خارجي" },

  search: { en: "Search", ar: "بحث" },
  when: { en: "When", ar: "المدة" },
  today: { en: "Today", ar: "اليوم" },
  customRange: { en: "Custom range", ar: "مدة مخصصة" },
  from: { en: "From", ar: "من" },
  to: { en: "To", ar: "إلى" },
  rangeIncomplete: {
    en: "Pick both dates to apply the custom range — showing today until then.",
    ar: "اختر التاريخين لتطبيق المدة المخصصة — يتم عرض اليوم حتى ذلك الحين.",
  },
  searchHint: { en: "Patient, doctor or type", ar: "المريض أو الطبيب أو النوع" },
  allBranches: { en: "All branches", ar: "كل الفروع" },
  status: { en: "Status", ar: "الحالة" },
  fBooked: { en: "Booked", ar: "محجوز" },
  fCheckedIn: { en: "Checked in", ar: "تم الوصول" },
  fNoShow: { en: "No-show", ar: "لم يحضر" },
  noneMatch: {
    en: "No appointments match these filters. Clear a filter to see more.",
    ar: "لا توجد مواعيد مطابقة لعوامل التصفية. أزل أحد العوامل لعرض المزيد.",
  },
} satisfies Record<string, Localized>;

/** Stable empties: `?? []` mints a new array each render and defeats the memo keyed on it. */
const NO_PRACTITIONERS: Practitioner[] = [];
const NO_SPECIALTIES: Specialty[] = [];
const NO_BRANCHES: BranchSummary[] = [];

/**
 * The call centre's cross-branch appointment board (15.3, 14.5).
 *
 * <b>READ-ONLY by construction, and that is not a shortcut.</b> The call centre holds `appointment:read` and
 * `appointment:reserve`, and every reserve path runs through callcentre-service, which refuses without a
 * VERIFIED interaction. There is no verified caller on a board, so there is nothing legitimate to act on
 * here — which is why this table has the note, the search and the filters, but no cancel button. Cancelling
 * lives in the call workspace, where the agent has verified who they are speaking to; a cancel here would
 * have to call emr directly and would bypass the one gate the whole phase exists to enforce.
 *
 * <b>Branch is a FILTER here, never a restriction.</b> The role is branch-unrestricted, so the board already
 * spans every branch it can reach; the combobox narrows what is on screen for an agent working one caller's
 * region, and clearing it returns everything.
 */
export function CallCentreAppointments({ ccApi = defaultCcApi }: { ccApi?: CcApi } = {}) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Cairo times, app locale

  const [branchId, setBranchId] = useState<string | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [query, setQuery] = useState("");
  const [when, setWhen] = useState<string | null>("today");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");

  // A member rings about "my appointment next Tuesday", so the agent needs a date the board does not default
  // to. The range is applied only once BOTH ends are given — a half-filled range would silently narrow the
  // board to one day while the chip claimed a range.
  const customActive = when === "custom" && from !== "" && to !== "";
  const range = customActive ? { from, to } : undefined;

  // Branch and date are SERVER-side — they change which rows exist. Status and search are client-side over
  // what came back, so typing does not re-hit the API on every keystroke.
  const state = useAsync<AppointmentRow[]>(
    () => api.appointments("all", false, range, branchId ?? undefined),
    [branchId, range?.from, range?.to],
  );

  const branches = useAsync<BranchSummary[]>(() => api.branches(), []);
  const practitioners = useAsync<Practitioner[]>(() => api.practitioners({ type: "Doctor" }), []);
  const specialties = useAsync<Specialty[]>(() => api.specialties(), []);
  const doctorById = useMemo(
    () => new Map((practitioners.data ?? NO_PRACTITIONERS).map((d) => [d.id, d])),
    [practitioners.data],
  );

  const deps = { t, fmt, doctorById, specialties: specialties.data ?? NO_SPECIALTIES };

  const cols: Column<AppointmentRow>[] = [
    patientColumn(deps),
    // WHICH branch, on a board that deliberately spans all of them. Without it the agent can tell a caller
    // the time of an appointment but not where to go, which is worse than not showing it at all.
    {
      key: "branch", header: t(S.branch), sortable: true,
      cell: (r) => r.branchName ?? (r.branchId ? "—" : t(S.noBranch)),
      sortValue: (r) => r.branchName ?? "",
    },
    ...doctorColumns(deps),
    ...timeAndStatusColumns(deps),
    // 14.5 — the booking note. The call centre WRITES these, so it must be able to read back what it told
    // the clinic; the same note the doctor sees, from the same field.
    noteColumn(deps),
    // The timeline IS legitimate here: it answers "who moved this and when", which is most of what a member
    // rings to ask, and it needs no verified interaction because it discloses no identity.
    // A real header, not "": an empty <th> has no accessible name (axe empty-table-header).
    { key: "timeline", header: t(S.timeline), cell: (r) => <VisitTimelineButton row={r} /> },
    // Cancelling from the board, through the VERIFIED path — see `CallCentreCancelButton` for why it opens
    // its own call record rather than calling emr directly, which would have worked and would have stepped
    // around the gate the whole phase exists to enforce.
    {
      key: "edit", header: t(S.editHeader),
      cell: (r) => <EditAppointmentButton row={r} t={t} onSaved={state.reload} />,
    },
    {
      key: "cancel", header: t(S.cancelHeader),
      cell: (r) => <CallCentreCancelButton row={r} api={ccApi} t={t} onCancelled={state.reload} />,
    },
  ];

  const visible = (rows: AppointmentRow[]) => {
    const q = query.trim().toLowerCase();
    return rows.filter((r) => {
      if (status === "booked" && !r.checkInEligible) return false;
      if (status === "checked-in" && !r.checkedIn) return false;
      if (status === "no-show" && r.status.label.en !== "No-show") return false;
      if (!q) return true;
      const doctor = r.doctorId ? doctorById.get(r.doctorId)?.name.en ?? "" : "";
      return `${r.beneficiaryName ?? ""} ${r.beneficiary.token} ${r.appointmentType} ${doctor}`
        .toLowerCase().includes(q);
    });
  };

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <p className="muted">{t(S.scope)}</p>
        <p className="muted">{t(S.actionsNote)}</p>
        <p className="muted">{t(S.arrivals)}</p>

        <TableToolbar
          search={{ label: t(S.search), value: query, onChange: setQuery, placeholder: t(S.searchHint) }}
          filters={[
            {
              key: "when", label: t(S.when), value: when, onChange: setWhen,
              options: [{ value: "today", label: t(S.today) }, { value: "custom", label: t(S.customRange) }],
              // Beside the chip that reveals them, not at the far end of the bar.
              extra: when === "custom" ? (
                <>
                  <InputField label={t(S.from)} type="date" value={from} onChange={(e) => setFrom(e.currentTarget.value)} />
                  <InputField label={t(S.to)} type="date" value={to} onChange={(e) => setTo(e.currentTarget.value)} />
                </>
              ) : undefined,
            },
            {
              key: "status", label: t(S.status), value: status, onChange: setStatus,
              options: [
                { value: "booked", label: t(S.fBooked) },
                { value: "checked-in", label: t(S.fCheckedIn) },
                { value: "no-show", label: t(S.fNoShow) },
              ],
            },
          ]}
        >
          <div className="book-field" style={{ inlineSize: 220 }}>
            <span className="mrs-label" id="cc-branch-filter">{t(S.branch)}</span>
            {/* A combobox rather than chips: there are six clinics today and more later, and a chip per
                branch would take the whole bar. Clearing it returns every branch — the call centre is
                branch-unrestricted, so this narrows the view and never the entitlement. */}
            <Combobox
              aria-labelledby="cc-branch-filter"
              options={(branches.data ?? NO_BRANCHES).map((b) => ({ value: b.id, label: t(b.name), hint: b.city }))}
              value={branchId}
              placeholder={t(S.allBranches)}
              onChange={(v) => setBranchId(v || null)}
            />
          </div>
        </TableToolbar>

        {/* Chosen "Custom" but not finished filling it in — say what is actually on screen rather than
            leaving the board looking filtered when it is not. */}
        {when === "custom" && !customActive && <InlineAlert tone="info">{t(S.rangeIncomplete)}</InlineAlert>}

        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {(rows) => {
            const shown = visible(rows);
            return shown.length === 0 ? (
              // Distinct from the empty board above: rows EXIST, the filters hid them. Saying "no
              // appointments today" when the agent has simply filtered to a status with none is how someone
              // concludes the system lost a booking mid-call.
              <InlineAlert tone="info">{t(S.noneMatch)}</InlineAlert>
            ) : (
              <DataTable columns={cols} rows={shown} rowKey={(r) => r.id} caption={t(S.title)} />
            );
          }}
        </AsyncSection>
      </Card>
    </>
  );
}

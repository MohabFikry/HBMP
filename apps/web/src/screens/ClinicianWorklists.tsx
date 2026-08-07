import { useCallback, useMemo, useState } from "react";
import { useLocation, useNavigate } from "react-router-dom";
import { useFormat, type Formatters } from "../i18n/useFormat";
import { Button, Card, DataTableView, Icon, Modal, StatusChip, useTableQuery } from "@mersal/design-system";
import type { Column, TableFilterSpec } from "@mersal/design-system";
import type { Localized, OrderRow, PatientListItem, ResultDetail, RxRow } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc, useOpenProfile, useWhenFilter } from "./_shared";
import { RestrictedResultCard, RequestAccessDialog } from "./RestrictedResultCard";
import { EncounterTimelineButton } from "./VisitTimeline";
import { OrderDetailModal } from "./encounter/OrderDetailModal";
import { PrescriptionDetailModal } from "./encounter/PrescriptionDetailModal";

const S = {
  patientsTitle: { en: "My Patients", ar: "مرضاي" },
  patientsEmpty: { en: "No patients on your worklist.", ar: "لا يوجد مرضى في قائمتك." },
  patientsNoMatches: {
    en: "No patients match. Change the search or clear the filters.",
    ar: "لا يوجد مرضى مطابقون. عدّل البحث أو أزل عوامل التصفية.",
  },
  search: { en: "Search", ar: "بحث" },
  patientsSearchHint: { en: "Name, branch or encounter", ar: "الاسم أو الفرع أو رقم الزيارة" },
  branch: { en: "Branch", ar: "الفرع" },
  noBranch: { en: "Walk-in — no branch recorded", ar: "بدون موعد — لم يُسجَّل فرع" },
  encounters: { en: "Encounters", ar: "الزيارات" },
  encountersTitle: { en: "Previous encounters", ar: "الزيارات السابقة" },
  encountersHint: {
    en: "Open a visit to return to its consultation record.",
    ar: "افتح زيارة للعودة إلى سجل الكشف الخاص بها.",
  },
  openEncounter: { en: "Open this encounter", ar: "فتح هذه الزيارة" },
  ordersTitle: { en: "Orders", ar: "الطلبات" },
  ordersEmpty: { en: "You haven't placed any orders.", ar: "لم تقم بطلب أي فحوصات." },
  ordersNoMatches: {
    en: "No orders match. Change the search or clear the filters.",
    ar: "لا توجد طلبات مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },
  ordersSearchHint: { en: "Order, patient, code or status", ar: "الطلب أو المريض أو الرمز أو الحالة" },
  rxTitle: { en: "Prescriptions", ar: "الوصفات" },
  rxEmpty: { en: "You haven't written any prescriptions.", ar: "لم تكتب أي وصفات." },
  rxNoMatches: {
    en: "No prescriptions match. Change the search or clear the filters.",
    ar: "لا توجد وصفات مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },
  rxSearchHint: { en: "Reference, patient or status", ar: "المرجع أو المريض أو الحالة" },
  resultsTitle: { en: "Results Inbox", ar: "صندوق النتائج" },
  resultsEmpty: { en: "No completed results yet.", ar: "لا توجد نتائج مكتملة بعد." },
  resultsNoMatches: {
    en: "No results match. Change the search or clear the filters.",
    ar: "لا توجد نتائج مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },
  resultsSearchHint: { en: "Order, patient or code", ar: "الطلب أو المريض أو الرمز" },
  // Order type, as the filter's options. `orderType` is rendered verbatim in the Type column, so the option
  // VALUES are the English words the row carries and only the labels are localized.
  typeFilter: { en: "Type", ar: "النوع" },
  typeLab: { en: "Lab", ar: "مختبر" },
  typeRadiology: { en: "Radiology", ar: "أشعة" },
  // The four order statuses and the five prescription statuses, worded exactly as the chips in the Status
  // column (`orderStatus` / `rxStatus` at the client boundary). An option reading one thing against a chip
  // reading another is two names for one state.
  ordActive: { en: "Active", ar: "نشط" },
  ordPartial: { en: "Partially used", ar: "مُستخدم جزئياً" },
  ordCompleted: { en: "Completed", ar: "مكتمل" },
  ordCancelled: { en: "Cancelled", ar: "ملغى" },
  // "Verified", not "Approved" — and the VALUE matters as much as the label. The filter matches on
  // `status.label.en`, and `rxStatus` now renders an auto-cleared prescription as "Verified", so a chip
  // whose value stayed "Approved" would have matched only the team-approved ones. Since nothing yet writes a
  // team approval back to pharmacy, that chip would have read zero on every board — a filter that looks
  // available and can never select anything.
  rxVerified: { en: "Verified", ar: "تم التحقق" },
  rxActive: { en: "Active", ar: "نشطة" },
  rxPartial: { en: "Partially dispensed", ar: "صُرفت جزئياً" },
  rxDispensed: { en: "Dispensed", ar: "صُرفت" },
  rxCancelled: { en: "Cancelled", ar: "ملغاة" },
  openFile: { en: "Patient file", ar: "ملف المريض" },
  patient: { en: "Patient", ar: "المريض" },
  mrn: { en: "MRN", ar: "الرقم الطبي" },
  lastVisit: { en: "Last visit", ar: "آخر زيارة" },
  status: { en: "Status", ar: "الحالة" },
  orderNo: { en: "Order", ar: "الطلب" },
  rxNo: { en: "Reference", ar: "المرجع" },
  timeline: { en: "Timeline", ar: "المسار الزمني" },
  type: { en: "Type", ar: "النوع" },
  code: { en: "Code", ar: "الرمز" },
  lines: { en: "Lines", ar: "البنود" },
  placed: { en: "Placed", ar: "تاريخ الطلب" },
  submitted: { en: "Submitted", ar: "تاريخ الإرسال" },
  result: { en: "Result", ar: "النتيجة" },
  viewResult: { en: "View result", ar: "عرض النتيجة" },
  resultTitle: { en: "Result", ar: "النتيجة" },
  value: { en: "Value", ar: "القيمة" },
  accessRequested: { en: "Access request submitted — pending author / medical-director grant.", ar: "تم إرسال طلب الوصول — بانتظار موافقة الطبيب المُحرّر / المدير الطبي." },
  close: { en: "Close", ar: "إغلاق" },
} satisfies Record<string, Localized>;

// 18.D2 (U7): dates come from useFormat — pinned to Africa/Cairo and the APP locale. A bare
// toLocaleDateString uses the MACHINE zone, so a UTC-set clinic PC renders the wrong day near midnight.

/**
 * Put NAMES to the beneficiary ids on a clinician's own orders and prescriptions.
 *
 * ============================================================================================================
 * WHY THE JOIN IS DONE HERE AND NOT BY THE SERVER
 * ============================================================================================================
 * Neither orders-service nor pharmacy-service holds a beneficiary name — they are benefit and fulfilment
 * services and hold codes, quantities and statuses — and neither may go and fetch one: a service that reads a
 * sibling's data on the caller's behalf is the aggregation shape this platform forbids outright
 * (`NoServiceAccountArchitectureTests`).
 *
 * The obvious alternative, calling patient-service's name-only `/beneficiaries/summaries`, is not open to a
 * doctor either: that endpoint requires `patient:read`, which the doctor role does not hold (migration 0023
 * granted it to pharmacists specifically, so its absence here is a decision, not an oversight). Widening a
 * role's scope to relabel a column would be a platform-wide privilege change made for a cosmetic reason.
 *
 * So the names come from where this clinician is ALREADY entitled to read them: `/encounters/mine`, which
 * emr projects from its own `appointment.beneficiary_name` for the treating clinician and nobody else. This
 * is the CLIENT joining two responses its caller already holds — which is the caller assembling their own
 * screen, not a service aggregating on someone's behalf. No new scope, no new PHI reach, no new endpoint.
 *
 * ============================================================================================================
 * WHERE IT FALLS SHORT, AND WHY THAT IS SAFE
 * ============================================================================================================
 * `/encounters/mine` returns the clinician's 100 most recent encounters. An order written for a patient who
 * has since dropped off the end of that list resolves to no name, and the row keeps the masked token it has
 * always shown. That degrades to the status quo rather than to a blank or, far worse, to the wrong patient's
 * name — so the failure mode is "less informative", never "misleading".
 */
function usePatientNames(): (beneficiaryId: string) => Localized | null {
  const api = useApi();
  const state = useAsync<PatientListItem[]>(() => api.listPatients(), []);
  const byId = useMemo(() => {
    const map = new Map<string, Localized>();
    // Newest encounter wins on a re-entry: emr returns the list newest-first, so the FIRST name seen for a
    // beneficiary is the most recently captured spelling of it.
    for (const p of state.data ?? []) if (!map.has(p.beneficiaryId)) map.set(p.beneficiaryId, p.name);
    return map;
  }, [state.data]);
  return useCallback((beneficiaryId: string) => byId.get(beneficiaryId) ?? null, [byId]);
}

/**
 * The patient column on a worklist that carries only an id and a mask.
 *
 * The token stays as the fallback rather than a blank or a placeholder. It is what these boards showed
 * before, it is unambiguous, and it is still enough to match a row against a slip — whereas "Unknown patient"
 * would be a claim about the patient rather than about what this screen could resolve.
 */
function patientCell(name: Localized | null, token: string, t: (l: Localized) => string) {
  return name ? <strong>{t(name)}</strong> : <span className="tnum">{token}</span>;
}

/**
 * The Timeline column — what has happened to ONE transaction.
 *
 * <b>A column, not a section inside the detail dialog.</b> The two answer different questions and are reached
 * for at different moments: "what did I order" is asked once, on opening a row; "where has this got to" is
 * asked while scanning the board for the one thing that is stuck. Burying the second inside the first meant
 * opening a dialog you did not want in order to find out that a sample had been taken.
 *
 * These rows are CLICKABLE, so the button stops its click propagating — `EncounterTimelineButton` owns that,
 * because it is a property of the button rather than of any one board.
 *
 * A row with no `encounterId` cannot key a timeline at all and says so with an em dash, rather than offering
 * a button that opens onto nothing.
 */
function timelineColumn<Row>(
  t: (l: Localized) => string,
  encounterOf: (row: Row) => string | null,
  referenceOf: (row: Row) => string,
): Column<Row> {
  return {
    key: "timeline",
    header: t(S.timeline),
    cell: (r) => {
      const encounterId = encounterOf(r);
      return encounterId
        ? <EncounterTimelineButton encounterId={encounterId} reference={referenceOf(r)} />
        : <span className="muted">—</span>;
    },
  };
}

/**
 * ONE PERSON, one row — the panel folded from the encounter list underneath it.
 *
 * ============================================================================================================
 * WHY THE FOLD IS THE FEATURE
 * ============================================================================================================
 * `/encounters/mine` is a worklist of ENCOUNTERS, and its contract says so. A doctor who has seen the same
 * patient four times therefore got four rows with that patient's name on them — which is the correct answer to
 * "what have I done" and the wrong answer to "who are my patients". The panel is asked the second question:
 * it is where a clinician goes to find a person, and a list that repeats them is one they have to read
 * carefully to count how many people are actually on it.
 *
 * So the rows are grouped by BENEFICIARY, not by encounter. The visits do not disappear — they become the
 * timeline behind each row, which is the only place the per-visit detail was ever wanted.
 */
interface PatientPanelRow {
  beneficiaryId: string;
  name: Localized;
  /** Where they were last seen. Null for a walk-in, which was never booked and so has no branch. */
  branchName: string | null;
  /** ISO date of the most recent visit, or null if none of them carried one. */
  lastVisit: string | null;
  /** Every encounter this doctor has with them, newest first. The timeline, and the visit count. */
  visits: PatientListItem[];
}

/**
 * Fold the encounter rows into one row per person.
 *
 * Newest-first inside each group, and the group takes its branch from the LATEST visit rather than the first
 * one seen: a patient who moved from Nasr City to Maadi is a Maadi patient now, and a column headed "Branch"
 * beside one headed "Last visit" is read as the branch of that visit.
 */
function foldByPatient(rows: readonly PatientListItem[]): PatientPanelRow[] {
  const groups = new Map<string, PatientListItem[]>();
  for (const r of rows) {
    const existing = groups.get(r.beneficiaryId);
    if (existing) existing.push(r);
    else groups.set(r.beneficiaryId, [r]);
  }
  return [...groups.values()].map((visits) => {
    // A missing date sorts LAST rather than as the empty string, which would sort first and put an undated
    // encounter forward as the patient's most recent visit.
    const sorted = [...visits].sort((a, b) => (b.lastVisit ?? "").localeCompare(a.lastVisit ?? ""));
    const latest = sorted[0];
    return {
      beneficiaryId: latest.beneficiaryId,
      name: latest.name,
      branchName: latest.branchName,
      lastVisit: latest.lastVisit,
      visits: sorted,
    };
  });
}

/** My patients — one row per person the caller is treating (treating-relationship gated server-side). */
export function DoctorPatients() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  // Carries where we came from, so the profile's Back control returns to this worklist rather than guessing.
  const openProfile = useOpenProfile();
  const state = useAsync<PatientListItem[]>(() => api.listPatients(), []);
  const [timelineFor, setTimelineFor] = useState<PatientPanelRow | null>(null);

  const rows = useMemo(() => foldByPatient(state.data ?? []), [state.data]);

  const cols: Column<PatientPanelRow>[] = [
    // The patient's NAME. emr supplies it on this endpoint from the appointment the visit was started from;
    // a walk-in that was never booked still falls back to the masked token, which the client builds.
    { key: "patient", header: t(S.patient), cell: (r) => <strong>{t(r.name)}</strong>,
      sortable: true, sortValue: (r) => t(r.name) },
    {
      key: "branch",
      header: t(S.branch),
      // An em dash, not a blank: a walk-in genuinely has no branch, and an empty cell reads as a fault in
      // the row rather than as a fact about the visit.
      cell: (r) => r.branchName ?? <span className="muted">—</span>,
      // The unbranched rows sort together at one end instead of scattering through the alphabet.
      sortable: true, sortValue: (r) => r.branchName ?? "",
    },
    // Sorts on the ISO date, not the rendered one: `fmt.date` renders Arabic-Indic digits under the Arabic
    // locale, and sorting those orders the worklist by glyph.
    { key: "lastVisit", header: t(S.lastVisit), cell: (r) => <span className="tnum">{fmt.date(r.lastVisit)}</span>,
      sortable: true, sortValue: (r) => r.lastVisit ?? "" },
    {
      key: "encounters",
      header: t(S.encounters),
      // `doc` — the same glyph the nav rail gives the Encounter Workspace, because this button leads to it.
      //
      // The visit COUNT rides on the button rather than taking a column of its own: it is the answer to "is
      // this worth opening?", which is a question about the button, and a column of small integers beside a
      // column of dates is two numeric columns competing for the same glance.
      cell: (r) => (
        <Button variant="secondary" size="sm" leadingIcon={<Icon name="doc" />} onClick={() => setTimelineFor(r)}>
          {t(S.encounters)} ({r.visits.length})
        </Button>
      ),
    },
    {
      // design 39 §6's "search → profile" entry, from the list a clinician actually starts their day in.
      // The whole unified profile was unreachable from every clinical worklist without this.
      key: "file",
      // A real header, not "": an empty <th> has no accessible name, so a screen-reader user hears nothing
      // for the column their cursor is in (axe empty-table-header).
      header: t(S.openFile),
      // Icon + primary, matching the patient-file action on reception's boards. On this panel it is the row's
      // principal act — the panel exists to get a clinician into a patient's record — so it carries the
      // weight, and the timeline beside it stays secondary.
      cell: (r) => (
        <Button
          variant="primary"
          size="sm"
          leadingIcon={<Icon name="user" />}
          onClick={() => openProfile(r.beneficiaryId)}
        >
          {t(S.openFile)}
        </Button>
      ),
    },
  ];

  /**
   * Branch, derived from the rows rather than declared.
   *
   * There is no fixed branch vocabulary on the client — the names arrive from the `/branch-labels` lookup, and
   * which of them appear depends entirely on where this doctor works. A hardcoded list would show chips for
   * branches they have never set foot in and miss the one they moved to last month. The group only appears
   * when the panel actually spans two branches; on a single-branch doctor it would filter nothing.
   */
  const filters: TableFilterSpec<PatientPanelRow>[] = useMemo(() => {
    const branches = [...new Set(rows.map((r) => r.branchName).filter((b): b is string => Boolean(b)))]
      .sort((a, b) => a.localeCompare(b));
    if (branches.length < 2) return [];
    return [{
      key: "branch",
      label: t(S.branch),
      options: branches.map((b) => ({ value: b, label: b })),
      match: (r, value) => r.branchName === value,
    }];
  }, [rows, t]);

  // Read outside AsyncSection's render prop: a hook called there would be conditional on the load finishing.
  const query = useTableQuery<PatientPanelRow>({
    rows,
    columns: cols,
    // Both languages of the name, because the portal switches and a haystack in one language goes quiet in
    // the other. The encounter references of EVERY visit are in here too — a doctor holding a slip with
    // ENC-2026-000198 on it should find the patient, not be told there are no matches because that visit was
    // not their most recent one.
    searchText: (r) => [
      r.name.en, r.name.ar, r.branchName, ...r.visits.map((v) => v.mrn),
    ].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.patientsSearchHint),
    filters,
    pageSize: 10,
    // Most recently seen first — this is a panel a clinician scans for who they have just seen.
    initialSortKey: "lastVisit",
    initialSortDir: "descending",
    persistKey: "doctor-patients",
  });

  return (
    <>
      <PageHeader title={t(S.patientsTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.patientsEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.beneficiaryId}
              caption={t(S.patientsTitle)}
              emptyLabel={t(S.patientsEmpty)}
              noMatchesLabel={t(S.patientsNoMatches)}
            />
          )}
        </AsyncSection>
      </Card>

      <PatientEncountersModal patient={timelineFor} onClose={() => setTimelineFor(null)} />
    </>
  );
}

/**
 * Every consultation this doctor has had with one patient — and a door into each of them.
 *
 * ============================================================================================================
 * WHY NOT `VisitTimelineButton`
 * ============================================================================================================
 * That one is the step-by-step history of ONE appointment: booked, checked in, vitals recorded, note signed.
 * The question a row on this panel asks is the other one — when have I seen this person, and take me back to
 * that visit. Every encounter is already in hand from the list the panel folded, so opening this costs no
 * request and no second audited PHI read.
 *
 * ============================================================================================================
 * WHY THE ROWS CARRY THE ORIGIN WITH THEM
 * ============================================================================================================
 * Opening an encounter from here replaces the panel, and coming back must not cost the clinician their place.
 * Two separate mechanisms have to hold for that, and only one of them is automatic:
 *
 *  - WHERE back goes — `state.from` on the navigation. `useBackTarget` in the workspace prefers it over
 *    `navigate(-1)`, which is wrong on a pasted deep link and after a redirect. Without it the workspace
 *    renders no Back control at all on a fresh tab.
 *  - WHAT they come back TO — the panel's `persistKey`, which keeps the search, the branch filter and the page
 *    in session storage. It restores itself on re-mount; nothing here has to do anything for that.
 *
 * The pair is the point: sending them back to an unfiltered page 1 of the panel is technically "back" and
 * still loses the row they were working on.
 */
function PatientEncountersModal({ patient, onClose }: { patient: PatientPanelRow | null; onClose: () => void }) {
  const t = useLoc();
  const fmt = useFormat();
  const navigate = useNavigate();
  const location = useLocation();

  function openEncounter(encounterId: string) {
    // Closed FIRST. The dialog traps focus, and navigating out from under an open one leaves the trap on a
    // screen that no longer contains it.
    onClose();
    navigate(`/clinician/encounter?encounter=${encodeURIComponent(encounterId)}`, {
      state: { from: `${location.pathname}${location.search}` },
    });
  }

  return (
    <Modal
      open={patient !== null}
      onOpenChange={(open) => !open && onClose()}
      title={t(S.encountersTitle)}
      description={patient ? `${t(patient.name)} · ${t(S.encountersHint)}` : undefined}
      footer={<Button variant="secondary" onClick={onClose}>{t(S.close)}</Button>}
    >
      {patient && (
        // An ordered list, not a table: the sequence IS the content. Same two-line step shape as the
        // appointment timeline — the act and WHEN on the primary line, the reference beneath — so the two
        // read as one component even though they answer different questions.
        <ol className="vt-list">
          {patient.visits.map((v) => (
            <li key={v.id}>
              <button
                type="button"
                className="vt-step"
                onClick={() => openEncounter(v.id)}
                // The row's own text is a status chip, a date and a code; an accessible name assembled from
                // them announces as "Completed 14 May 2026 ENC-2026-000198" with no verb in it. This says
                // what pressing it DOES, which is the thing a screen-reader user is choosing between.
                aria-label={`${t(S.openEncounter)} — ${fmt.date(v.lastVisit)} · ${t(v.status.label)}`}
              >
                <StatusChip kind={v.status.kind} label={t(v.status.label)} />
                <span className="vt-when tnum">
                  <Icon name="clock" width={13} height={13} aria-hidden="true" className="vt-ico" />
                  {fmt.date(v.lastVisit)}
                </span>
                <span className="vt-meta">
                  <span className="vt-who">
                    {v.branchName ? (
                      <>
                        <Icon name="branch" width={13} height={13} aria-hidden="true" className="vt-ico" />
                        {v.branchName}
                      </>
                    ) : (
                      // A walk-in has no branch. Said in words rather than left blank, which reads as a fault.
                      <span className="muted">{t(S.noBranch)}</span>
                    )}
                  </span>
                  {/* The encounter reference — the door's number, not what is behind it. */}
                  {v.mrn && <code className="vt-ref tnum">{v.mrn}</code>}
                </span>
              </button>
            </li>
          ))}
        </ol>
      )}
    </Modal>
  );
}

// 18.D2 (U7): the formatter is PASSED IN rather than hooked here — this is a plain helper, not a component,
// so calling a hook inside it violates the rules of hooks (and would break if it were ever called twice).
//
// Every column that can be ordered carries a `sortValue`, and none of them sorts on what the cell RENDERS: a
// date cell reads "٢٦ يوليو" under the Arabic locale and a status cell is a chip, so ordering the rendered
// text orders the worklist by glyph. The dates sort on the ISO instant, the chips on their English label.
function orderColumns(
  t: (l: Localized) => string,
  fmt: Formatters,
  nameOf: (beneficiaryId: string) => Localized | null,
): Column<OrderRow>[] {
  return [
    { key: "orderNo", header: t(S.orderNo), cell: (r) => <span className="tnum">{r.orderNo}</span>,
      sortable: true, sortValue: (r) => r.orderNo },
    // The NAME, with the masked token as the fallback. This board is the ordering clinician's own work and
    // they read the full record behind every row of it — the masking belongs on the boards that genuinely do
    // not need identity (the bench, the counter, approvals), and a doctor scanning their own orders for one
    // patient cannot do it against a column of "•••4821".
    { key: "patient", header: t(S.patient),
      cell: (r) => patientCell(nameOf(r.beneficiary.id), r.beneficiary.token, t),
      // Sorts on whatever is DISPLAYED, so the order on screen matches the order in the column.
      sortable: true, sortValue: (r) => { const n = nameOf(r.beneficiary.id); return n ? t(n) : r.beneficiary.token; } },
    { key: "type", header: t(S.type), cell: (r) => r.orderType, sortable: true, sortValue: (r) => r.orderType },
    // No CODE column.
    //
    // It only ever showed `lines[0].code` — the first test on the order — which was a fair summary while the
    // row was all a clinician could see. Now that the row counts its lines and opens onto all of them, that
    // column names one of four tests with nothing to say it is one of four, which is worse than saying
    // nothing: a reader scanning it has no cue that anything is missing.
    //
    // Searching by code is UNAFFECTED. `orderHaystack` matches on `primaryCode` and on every line's code and
    // description, so a doctor holding a slip still finds the order by any test on it — including the ones
    // this column never showed.
    //
    // A COUNT — the same column the prescriptions worklist carries, and the cue that a row is worth opening.
    { key: "lines", header: t(S.lines), cell: (r) => r.lineCount,
      numeric: true, sortable: true, sortValue: (r) => r.lineCount },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />,
      sortable: true, sortValue: (r) => r.status.label.en },
    { key: "placed", header: t(S.placed), cell: (r) => <span className="tnum">{fmt.date(r.requestedAt)}</span>,
      sortable: true, sortValue: (r) => r.requestedAt },
    timelineColumn<OrderRow>(t, (r) => r.encounterId, (r) => r.orderNo),
  ];
}

/**
 * Everything an order row can be searched by.
 *
 * Both languages of the name and of the status, because the portal switches and a haystack in one language
 * goes quiet in the other. The masked token stays in it: it is what the row falls back to when the name
 * cannot be resolved, and a doctor holding a slip has it in hand.
 */
/** Stable accessors — an inline arrow would be a new identity each render and rebuild the filter's memo. */
const orderDate = (r: OrderRow) => r.requestedAt;
const rxDate = (r: RxRow) => r.submittedAt;

const orderHaystack = (r: OrderRow, name: Localized | null) =>
  [
    r.orderNo, r.beneficiary.token, r.orderType, r.primaryCode,
    r.status.label.en, r.status.label.ar, name?.en, name?.ar,
    // Every line's code and description, so an order is findable by any test on it and not only by the first
    // one — which is all the "Code" column shows.
    ...r.lines.flatMap((l) => [l.code, l.description]),
  ].filter(Boolean).join(" ");

/**
 * Lab / Radiology. Matched case-insensitively — `orderType` is emr's string, rendered verbatim.
 *
 * 29.1 — the Radiology option matches BOTH spellings (design 45 §1). Orders placed before the rename kept
 * `Imaging` in the row for the life of the order, and a filter that matched only the new value would show a
 * doctor an empty radiology worklist while their orders sat there under the old name — a true statement
 * ("nothing matching Radiology") standing in for a false one ("no radiology orders").
 */
function typeFilter(t: (l: Localized) => string): TableFilterSpec<OrderRow> {
  return {
    key: "type",
    label: t(S.typeFilter),
    options: [
      { value: S.typeLab.en, label: t(S.typeLab) },
      { value: S.typeRadiology.en, label: t(S.typeRadiology) },
    ],
    match: (r, value) => {
      const row = r.orderType.toLowerCase();
      const want = value.toLowerCase();
      if (want === "radiology") return row === "radiology" || row === "imaging";
      return row === want;
    },
  };
}

// Matched on the ENGLISH label: the row carries its status only as a pre-resolved `{kind, label}` chip, and
// matching the localized half would break the filter the moment the portal is switched to Arabic.
function orderStatusFilter(t: (l: Localized) => string): TableFilterSpec<OrderRow> {
  return {
    key: "status",
    label: t(S.status),
    options: [
      { value: S.ordActive.en,    label: t(S.ordActive) },
      { value: S.ordPartial.en,   label: t(S.ordPartial) },
      { value: S.ordCompleted.en, label: t(S.ordCompleted) },
      { value: S.ordCancelled.en, label: t(S.ordCancelled) },
    ],
    match: (r, value) => r.status.label.en === value,
  };
}

/** Orders — everything I've ordered (investigation orders I authored). */
export function DoctorOrders() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const nameOf = usePatientNames();
  const state = useAsync<OrderRow[]>(() => api.ordersMine(), []);
  const [viewing, setViewing] = useState<OrderRow | null>(null);
  const cols = orderColumns(t, fmt, nameOf);
  // Type, Status and WHEN. A clinician's own order list only grows, so "what did I raise recently" is
  // the question it is opened for far more often than any status.
  const when = useWhenFilter<OrderRow>(t, orderDate);
  const filters = useMemo(() => [typeFilter(t), orderStatusFilter(t), when], [t, when]);

  // Read outside AsyncSection's render prop: a hook called there would be conditional on the load finishing.
  const query = useTableQuery<OrderRow>({
    rows: state.data ?? [],
    columns: cols,
    searchText: (r) => orderHaystack(r, nameOf(r.beneficiary.id)),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.ordersSearchHint),
    filters,
    pageSize: 10,
    // Newest first — a doctor opening "my orders" is chasing what they placed today, not the backlog.
    initialSortKey: "placed",
    initialSortDir: "descending",
    persistKey: "doctor-orders",
  });

  return (
    <>
      <PageHeader title={t(S.ordersTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.ordersEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.ordersTitle)}
              emptyLabel={t(S.ordersEmpty)}
              noMatchesLabel={t(S.ordersNoMatches)}
              // The whole ROW opens the order, which is why this board carries no button to do it. `interactive`
              // is what makes that reachable without a mouse: it puts the rows in a grid with a roving tabindex
              // and arrow-key navigation, so the click target is also a keyboard target.
              interactive
              onSelect={setViewing}
            />
          )}
        </AsyncSection>
      </Card>

      <OrderDetailModal order={viewing} onOpenChange={(open) => !open && setViewing(null)} />
    </>
  );
}

/** Results inbox — my orders that are Completed (results are back). */
/**
 * Results inbox (Phase 4 US-032 + Phase 14.6/14.8). Opening a completed result fetches it through the sensitivity
 * gate: a Standard result (or one the caller authored / holds a grant for) shows its value; a restricted result
 * returns EXISTENCE-ONLY metadata, which renders the locked `RestrictedResultCard` + a "Request access" flow. The
 * server sends no values for a restricted result, so no clinical value can ever reach this DOM.
 */
export function DoctorResults() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  // The same named-patient column as the orders board — these are the same rows, filtered to Completed, and
  // showing a name on one and a mask on the other would read as two different boards.
  const nameOf = usePatientNames();
  const state = useAsync<OrderRow[]>(() => api.ordersMine("Completed"), []);
  const [open, setOpen] = useState(false);
  const [detail, setDetail] = useState<ResultDetail | null>(null);
  const [busy, setBusy] = useState(false);
  const [requesting, setRequesting] = useState(false);
  const [requested, setRequested] = useState(false);

  async function openResult(row: OrderRow) {
    if (!row.firstLineId) return;
    setBusy(true);
    setDetail(null);
    setRequesting(false);
    setRequested(false);
    setOpen(true);
    try {
      setDetail(await api.resultDetail(row.id, row.firstLineId));
    } finally {
      setBusy(false);
    }
  }

  async function submitRequest(input: { purposeCode: string; justification: string; requestedTtlHours: number }) {
    if (!detail) return;
    await api.requestReportAccess({ orderId: detail.orderId, lineId: detail.lineId, ...input });
    setRequesting(false);
    setRequested(true);
  }

  const cols: Column<OrderRow>[] = [
    ...orderColumns(t, fmt, nameOf),
    {
      key: "result",
      header: t(S.result),
      cell: (r) =>
        r.firstLineId ? (
          <Button variant="secondary" onClick={() => openResult(r)}>
            {t(S.viewResult)}
          </Button>
        ) : (
          <span className="muted">—</span>
        ),
    },
  ];

  // No STATUS filter here, unlike the orders worklist: this list is `ordersMine("Completed")`, so every row
  // has the same status and a group whose every option but one reads zero is chrome that answers nothing.
  const when = useWhenFilter<OrderRow>(t, orderDate);
  const filters = useMemo(() => [typeFilter(t), when], [t, when]);

  const query = useTableQuery<OrderRow>({
    rows: state.data ?? [],
    columns: cols,
    searchText: (r) => orderHaystack(r, nameOf(r.beneficiary.id)),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.resultsSearchHint),
    filters,
    pageSize: 10,
    // Newest result first — an inbox is worked from the top.
    initialSortKey: "placed",
    initialSortDir: "descending",
    persistKey: "doctor-results",
  });

  return (
    <>
      <PageHeader title={t(S.resultsTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.resultsEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.resultsTitle)}
              emptyLabel={t(S.resultsEmpty)}
              noMatchesLabel={t(S.resultsNoMatches)}
              // NOT `interactive`, unlike the orders board. This row carries a "View result" button, and a
              // button inside a selectable row fires both handlers on one click — the reader would get the
              // result they asked for AND an order dialog stacked over it. The row's action here is the
              // result, which is what this inbox is for; the order behind it is read from the orders board.
            />
          )}
        </AsyncSection>
      </Card>

      <Modal open={open} onOpenChange={setOpen} title={t(S.resultTitle)}>
        {busy && (
          <div role="status" aria-live="polite" style={{ padding: "var(--sp4)" }}>
            <span className="mrs-spin" aria-hidden="true" />
          </div>
        )}
        {!busy && detail && detail.restricted && !requesting && (
          <RestrictedResultCard result={detail} onRequestAccess={() => setRequesting(true)} />
        )}
        {!busy && detail && detail.restricted && requesting && (
          <RequestAccessDialog onSubmit={submitRequest} onCancel={() => setRequesting(false)} />
        )}
        {!busy && requested && (
          <p role="status" aria-live="polite" style={{ marginTop: "var(--sp3)" }}>
            {t(S.accessRequested)}
          </p>
        )}
        {!busy && detail && !detail.restricted && (
          <dl style={{ display: "grid", gridTemplateColumns: "auto 1fr", gap: "4px 12px" }}>
            <dt style={{ opacity: 0.7 }}>{t(S.type)}</dt>
            <dd>{detail.category}</dd>
            <dt style={{ opacity: 0.7 }}>{t(S.code)}</dt>
            <dd className="tnum">{detail.code}</dd>
            <dt style={{ opacity: 0.7 }}>{t(S.value)}</dt>
            <dd>{detail.value}</dd>
            <dt style={{ opacity: 0.7 }}>{t(S.status)}</dt>
            <dd>{detail.status}</dd>
          </dl>
        )}
      </Modal>
    </>
  );
}

/** Prescriptions — e-prescriptions I authored. */
export function DoctorPrescriptions() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const nameOf = usePatientNames();
  const state = useAsync<RxRow[]>(() => api.prescriptionsMine(), []);
  const [viewing, setViewing] = useState<RxRow | null>(null);
  const when = useWhenFilter<RxRow>(t, rxDate);
  const cols: Column<RxRow>[] = [
    // The Rx REFERENCE, not the surrogate id: it is what the pharmacy and the patient quote back, and the
    // one thing a prescriber can match a phone call against.
    { key: "rxNo", header: t(S.rxNo), cell: (r) => <span className="tnum">{r.rxNo}</span>,
      sortable: true, sortValue: (r) => r.rxNo },
    // The NAME, with the masked token as the fallback — see `usePatientNames`. A prescriber scanning their
    // own prescriptions for one patient cannot do it against a column of "•••4821".
    { key: "patient", header: t(S.patient),
      cell: (r) => patientCell(nameOf(r.beneficiary.id), r.beneficiary.token, t),
      sortable: true, sortValue: (r) => { const n = nameOf(r.beneficiary.id); return n ? t(n) : r.beneficiary.token; } },
    { key: "lines", header: t(S.lines), cell: (r) => r.lineCount, numeric: true,
      sortable: true, sortValue: (r) => r.lineCount },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />,
      sortable: true, sortValue: (r) => r.status.label.en },
    // Sorts on the ISO instant, not the rendered date — `fmt.date` renders Arabic-Indic digits under the
    // Arabic locale, and sorting those orders the list by glyph. An unsubmitted draft sorts last either way.
    { key: "submitted", header: t(S.submitted), cell: (r) => <span className="tnum">{fmt.date(r.submittedAt)}</span>,
      sortable: true, sortValue: (r) => r.submittedAt ?? "" },
    timelineColumn<RxRow>(t, (r) => r.encounterId, (r) => r.rxNo),
  ];

  const filters: TableFilterSpec<RxRow>[] = useMemo(() => [
    {
      key: "status",
      label: t(S.status),
      // Matched on the ENGLISH label, as everywhere else — the row carries its status only as a resolved chip.
      options: [
        { value: S.rxVerified.en,  label: t(S.rxVerified) },
        { value: S.rxActive.en,    label: t(S.rxActive) },
        { value: S.rxPartial.en,   label: t(S.rxPartial) },
        { value: S.rxDispensed.en, label: t(S.rxDispensed) },
        { value: S.rxCancelled.en, label: t(S.rxCancelled) },
      ],
      match: (r, value) => r.status.label.en === value,
    },
    // Dated by when it was SUBMITTED — an unsubmitted draft has no date and so falls in no window.
    when,
  ], [t, when]);

  const query = useTableQuery<RxRow>({
    rows: state.data ?? [],
    columns: cols,
    // The drug names too: "what did I put that patient on" is asked by the medication far more often than by
    // the reference number, and the lines ride along on the same response.
    searchText: (r) => {
      const n = nameOf(r.beneficiary.id);
      return [
        r.rxNo, r.beneficiary.token, n?.en, n?.ar, r.status.label.en, r.status.label.ar,
        ...r.lines.flatMap((l) => [l.drug?.en, l.drug?.ar]),
      ].filter(Boolean).join(" ");
    },
    searchLabel: t(S.search),
    searchPlaceholder: t(S.rxSearchHint),
    filters,
    pageSize: 10,
    initialSortKey: "submitted",
    initialSortDir: "descending",
    persistKey: "doctor-prescriptions",
  });

  return (
    <>
      <PageHeader title={t(S.rxTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.rxEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.rxTitle)}
              emptyLabel={t(S.rxEmpty)}
              noMatchesLabel={t(S.rxNoMatches)}
              // The whole row opens the prescription — the same gesture as the orders board, and the same
              // dialog the encounter workspace already opens from its Prescriptions tab.
              interactive
              onSelect={setViewing}
            />
          )}
        </AsyncSection>
      </Card>

      <PrescriptionDetailModal rx={viewing} onOpenChange={(open) => !open && setViewing(null)} />
    </>
  );
}

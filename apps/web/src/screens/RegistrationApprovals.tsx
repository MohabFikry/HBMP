import { useCallback, useEffect, useMemo, useState } from "react";
import {
  Button,
  Card,
  DataTableView,
  Icon,
  InlineAlert,
  Modal,
  StatusChip,
  TextareaField,
  useTableQuery,
  useToast,
} from "@mersal/design-system";
import type { Column, TableFilterSpec } from "@mersal/design-system";
import type {
  BeneficiaryDocument,
  BulkDecisionOutcome,
  Localized,
  RegistrationThreadEntry,
  RegistrationWorkItem,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useAuth } from "../auth/AuthProvider";
import { useWrite } from "../api/useWrite";
import { useFormat } from "../i18n/useFormat";
import { AsyncSection, PageHeader, useLoc, readErrorMessage } from "./_shared";
import { DOCUMENT_TYPES } from "./BeneficiaryDocuments";

/**
 * ============================================================================================================
 * REGISTRATION APPROVALS (US-003) — the approver's queue
 * ============================================================================================================
 *
 * Two roles share this screen with different halves. The OFFICER prepares: they tick the two guards as the
 * evidence arrives, open an application for a legacy record, read what a supervisor has asked for, and answer
 * it. The SUPERVISOR decides. The decision controls are hidden from the officer as a courtesy only (§6 — UI
 * gating is cosmetic); the server refuses a hand-crafted officer decision with `urn:hbmp:approver-required`,
 * because the person who vouched for the documents must not be the one who activates the member.
 *
 * ------------------------------------------------------------------------------------------------------------
 * WHAT THE LAST COLUMN USED TO BE, AND WHY IT WAS ALL DASHES
 * ------------------------------------------------------------------------------------------------------------
 * It was the DECISION column, and for an officer every row in it said "—": the only control it ever held was
 * the supervisor's Decide button. A column that is empty for a whole role is a column that role reads as
 * broken data, and it cost the widest part of the table. The actions column now carries something for
 * everybody — VIEW is not a decision, and an officer preparing an application is exactly who needs to open it
 * — so the dashes are gone because the cell has a job, not because the dash was replaced with a nicer dash.
 *
 * ------------------------------------------------------------------------------------------------------------
 * WHY THE INTRODUCTORY PARAGRAPH IS GONE
 * ------------------------------------------------------------------------------------------------------------
 * It said the queue was oldest-first, that approval needs verified documents and bound coverage, and that the
 * decision is a supervisor's. Every one of those is now stated by something the operator can act on: the date
 * column is sortable and starts on oldest, the two guards are checkboxes in the table, and the approve option
 * carries its own blocked reason inside the decision modal. Prose that restates the interface is prose that
 * gets skipped, and it pushed the first row below the fold on a laptop.
 *
 * ------------------------------------------------------------------------------------------------------------
 * WHY SEARCH / FILTER / SORT / PAGE ARE THE DESIGN-SYSTEM PATTERN AND NOT LOCAL CODE
 * ------------------------------------------------------------------------------------------------------------
 * `useTableQuery` + `DataTableView` (design-system) are the house standard for a portal table. The ordering
 * they enforce is the part that matters: sort applies to the whole result and THEN it is paged. A table that
 * sorts itself sorts the page it was handed, so "oldest first" would reorder 25 rows and leave the actual
 * oldest application sitting on page 4 — and it would look like it worked.
 */

const A = {
  title: { en: "Registration Approvals", ar: "اعتماد التسجيلات" },
  empty: { en: "No registrations waiting for review.", ar: "لا توجد تسجيلات بانتظار المراجعة." },
  noMatches: {
    en: "No registrations match. Change the search or clear the filters.",
    ar: "لا توجد تسجيلات مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },
  search: { en: "Search", ar: "بحث" },
  searchHint: { en: "Name, card, ID or officer", ar: "الاسم أو البطاقة أو الرقم أو الموظف" },

  person: { en: "Person", ar: "الشخص" },
  filed: { en: "Registered", ar: "تاريخ التسجيل" },
  officer: { en: "Registered by", ar: "سجّله" },
  application: { en: "Application", ar: "الطلب" },
  docs: { en: "Documents", ar: "المستندات" },
  coverage: { en: "Coverage", ar: "التغطية" },
  // The column HEADER is short because the column is 44px wide; the checkbox's accessible name is the
  // full guard, because "Documents — Omar Khaled" does not say what ticking it asserts.
  docsCheck: { en: "Documents verified", ar: "تم التحقق من المستندات" },
  coverageCheck: { en: "Coverage bound", ar: "تم ربط التغطية" },
  notes: { en: "Notes", ar: "ملاحظات" },
  actions: { en: "Actions", ar: "إجراءات" },

  decide: { en: "Decide", ar: "قرار" },
  // Named separately from the row button. Two controls reading "Decide" on one screen, one acting on a row
  // and one on a selection, is an ambiguity a keyboard or screen-reader user resolves by guessing.
  decideSelected: { en: "Decide selected", ar: "قرار على المحدد" },
  startReview: { en: "Start review", ar: "بدء المراجعة" },
  view: { en: "View registration", ar: "عرض التسجيل" },
  openNotes: { en: "Open notes", ar: "فتح الملاحظات" },
  noNotes: { en: "No notes yet", ar: "لا توجد ملاحظات بعد" },
  unknownOfficer: { en: "Unknown", ar: "غير معروف" },

  // Application-status chips (the beneficiary chip already says Pending — this is the WORKFLOW state).
  statusFilter: { en: "Application", ar: "الطلب" },
  appPending: { en: "In review", ar: "قيد المراجعة" },
  appInfo: { en: "Info requested", ar: "بانتظار معلومات" },
  appRejected: { en: "Rejected", ar: "مرفوض" },
  notStarted: { en: "Not started", ar: "لم تبدأ" },

  capped: {
    en: "Showing the oldest {n} of {total} pending registrations. Decide these first — the rest load as the queue clears.",
    ar: "يتم عرض أقدم {n} من {total} تسجيلًا معلقًا. ابدأ بهذه — وتظهر البقية كلما تقلصت القائمة.",
  },

  // ---- bulk ----
  selected: { en: "{n} selected", ar: "تم اختيار {n}" },
  clearSelection: { en: "Clear selection", ar: "إلغاء الاختيار" },
  bulkTitle: { en: "Decide {n} registrations", ar: "قرار على {n} تسجيلات" },
  bulkPartial: {
    en: "{ok} recorded, {failed} refused. The refused rows are listed below and are still in the queue.",
    ar: "تم تسجيل {ok} ورُفض {failed}. الصفوف المرفوضة مذكورة أدناه ولا تزال في القائمة.",
  },
  bulkAllOk: { en: "{ok} decisions recorded.", ar: "تم تسجيل {ok} قرارات." },

  // ---- decision modal ----
  decisionTitle: { en: "Registration decision", ar: "قرار التسجيل" },
  approve: { en: "Approve & activate", ar: "اعتماد وتفعيل" },
  requestInfo: { en: "Request information", ar: "طلب معلومات" },
  reject: { en: "Reject", ar: "رفض" },
  decisionLabel: { en: "Decision", ar: "القرار" },
  notesLabel: { en: "Notes", ar: "ملاحظات" },
  notesRequired: {
    en: "Notes are required — they go back to the officer (request info) or onto the record (reject).",
    ar: "الملاحظات مطلوبة — تعود إلى الموظف (طلب معلومات) أو تُسجَّل في الملف (رفض).",
  },
  notifies: {
    en: "The officer who registered this person is notified and can reply here.",
    ar: "يتم إخطار الموظف الذي سجّل هذا الشخص ويمكنه الرد هنا.",
  },
  approveBlocked: {
    en: "Approval needs both checks: documents verified and coverage bound.",
    ar: "يتطلب الاعتماد اكتمال الشرطين: التحقق من المستندات وربط التغطية.",
  },
  approveBlockedSome: {
    en: "{n} of the selected registrations are missing a check and cannot be approved.",
    ar: "{n} من التسجيلات المختارة ينقصها شرط ولا يمكن اعتمادها.",
  },
  approved: { en: "Approved — member number", ar: "تم الاعتماد — رقم العضوية" },
  decided: { en: "Decision recorded.", ar: "تم تسجيل القرار." },
  cancel: { en: "Cancel", ar: "إلغاء" },
  confirm: { en: "Confirm", ar: "تأكيد" },
  close: { en: "Close", ar: "إغلاق" },

  // ---- notes modal ----
  notesTitle: { en: "Notes", ar: "الملاحظات" },
  notesIntro: {
    en: "Every decision and every answer to one, oldest first. Entries cannot be edited or removed.",
    ar: "كل قرار وكل رد عليه، الأقدم أولًا. لا يمكن تعديل المدوّنات أو حذفها.",
  },
  threadEmpty: {
    en: "Nothing has been said about this registration yet.",
    ar: "لم يُسجَّل شيء عن هذا التسجيل بعد.",
  },
  replyLabel: { en: "Add a note", ar: "أضف ملاحظة" },
  replyHint: {
    en: "Answer what was asked, or record something the approver needs to know.",
    ar: "أجب عمّا طُلب، أو سجّل ما يحتاج المعتمِد إلى معرفته.",
  },
  send: { en: "Add note", ar: "إضافة" },
  replyEmpty: { en: "Write something before adding it.", ar: "اكتب شيئًا قبل الإضافة." },
  closedThread: {
    en: "This application is closed. Open a fresh review to continue the conversation.",
    ar: "هذا الطلب مغلق. ابدأ مراجعة جديدة لمتابعة النقاش.",
  },
  decisionEntry: { en: "Decision", ar: "قرار" },
  replyEntry: { en: "Note", ar: "ملاحظة" },

  // ---- detail modal ----
  detailTitle: { en: "Registration", ar: "التسجيل" },
  secPerson: { en: "Person", ar: "الشخص" },
  secApplication: { en: "Application", ar: "الطلب" },
  secCoverage: { en: "Coverage elected", ar: "التغطية المختارة" },
  secNotes: { en: "Standing notes", ar: "الملاحظات الثابتة" },
  secDocuments: { en: "Documents on file", ar: "المستندات المرفقة" },
  card: { en: "Card number", ar: "رقم البطاقة" },
  fullName: { en: "Name", ar: "الاسم" },
  birthDate: { en: "Date of birth", ar: "تاريخ الميلاد" },
  approxDate: { en: "approximate", ar: "تقريبي" },
  sex: { en: "Sex", ar: "النوع" },
  nationality: { en: "Nationality", ar: "الجنسية" },
  identifier: { en: "Identity document", ar: "مستند الهوية" },
  phone: { en: "Phone", ar: "الهاتف" },
  individualNo: { en: "Individual no.", ar: "رقم الفرد" },
  caseNo: { en: "Case no.", ar: "رقم الحالة" },
  plan: { en: "Plan", ar: "الخطة" },
  tier: { en: "Network tier", ar: "شبكة الخدمة" },
  contribution: { en: "Member share", ar: "مساهمة العضو" },
  branch: { en: "Default clinic", ar: "العيادة الافتراضية" },
  noCoverage: {
    en: "No coverage was elected at the desk. This registration is enrolled by hand after approval.",
    ar: "لم تُختر تغطية عند التسجيل. يتم إلحاق هذا التسجيل يدويًا بعد الاعتماد.",
  },
  noStandingNotes: { en: "No standing notes were recorded.", ar: "لم تُسجَّل ملاحظات ثابتة." },
  noDocuments: { en: "No documents have been filed yet.", ar: "لم تُرفق مستندات بعد." },
  withheld: { en: "On file — your role cannot read it", ar: "مسجّل — لا يمكن لدورك الاطلاع عليه" },
  notDisclosed: { en: "Not disclosed to your role", ar: "غير متاح لدورك" },
  uploadedBy: { en: "filed by", ar: "أرفقه" },
  noApplication: {
    en: "This person has no application yet. Start a review to open one.",
    ar: "لا يوجد طلب لهذا الشخص بعد. ابدأ مراجعة لفتح طلب.",
  },
  supervisorOnly: {
    en: "Decisions are made by a beneficiary-management supervisor.",
    ar: "القرارات من صلاحية مشرف إدارة المستفيدين.",
  },
  ownFiling: {
    en: "You filed this registration — another approver must decide it.",
    ar: "أنت من سجّل هذا الطلب — يجب أن يبتّ فيه معتمِد آخر.",
  },
} satisfies Record<string, Localized>;

type AppState = "Pending" | "InfoRequested" | "Rejected" | "None";
type Decision = "Approve" | "RequestInfo" | "Reject";

/** The workflow state of a row, with `None` for a beneficiary that has no application at all. */
const appStateOf = (item: RegistrationWorkItem): AppState =>
  item.registration ? item.registration.status === "Active" ? "Pending" : item.registration.status : "None";

const APP_CHIP: Record<AppState, { kind: "ok" | "info" | "warn" | "bad" | "neu"; label: Localized }> = {
  Pending: { kind: "info", label: A.appPending },
  InfoRequested: { kind: "warn", label: A.appInfo },
  Rejected: { kind: "bad", label: A.appRejected },
  None: { kind: "neu", label: A.notStarted },
};

/** Substitute `{token}`s in a localized string. Kept trivial on purpose — this is not a template engine. */
const fill = (text: string, values: Record<string, string | number>): string =>
  text.replace(/\{(\w+)\}/g, (_, k: string) => String(values[k] ?? `{${k}}`));

export function RegistrationApprovals() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const { session } = useAuth();
  const { toast } = useToast();
  const write = useWrite();

  const [reloadKey, setReloadKey] = useState(0);
  const state = useAsync(() => api.registrationWorklist(), [reloadKey]);
  const reload = useCallback(() => setReloadKey((k) => k + 1), []);
  const isSupervisor = session?.role === "beneficiary_mgmt_supervisor";

  /*
   * SELF-APPROVAL, shown before it is refused.
   *
   * The supervisor now holds the register pen (their portal is the officer's plus the decision), so a
   * supervisor CAN be the person who filed the application in front of them. The server refuses that decision
   * with `urn:hbmp:self-approval` — the rule is enforced there, not here — but letting the button through to
   * a 403 teaches the operator nothing except that the screen is unreliable. Named on the row instead.
   */
  const filedByMe = (r: RegistrationWorkItem): boolean =>
    Boolean(session?.userId) && r.registration?.createdBy === session!.userId;

  // The three things a row can open, and the bulk decision. Separate state rather than one "modal" union:
  // opening the notes for a row must not disturb which rows are selected for a bulk decision.
  const [detailOf, setDetailOf] = useState<RegistrationWorkItem | null>(null);
  const [notesOf, setNotesOf] = useState<RegistrationWorkItem | null>(null);
  const [decisionOf, setDecisionOf] = useState<RegistrationWorkItem[] | null>(null);
  const [selected, setSelected] = useState<ReadonlySet<string>>(new Set());

  // Read outside `AsyncSection` because `useTableQuery` is a hook and cannot live inside its render callback.
  // Empty while loading, which is the honest shape: the toolbar renders with nothing to filter rather than
  // popping into existence a frame after the table.
  const rows = state.status === "success" && state.data ? state.data.items : [];
  const total = state.status === "success" && state.data ? state.data.total : 0;

  const toggle = async (item: RegistrationWorkItem, key: "documentsVerified" | "coverageBound") => {
    if (!item.registration) return;
    const ok = await write.run(() => api.setRegistrationChecks(item.registration!.id, { [key]: !item.registration![key] }));
    if (ok) reload();
  };

  const start = async (item: RegistrationWorkItem) => {
    const ok = await write.run((key) => api.createRegistration(item.beneficiary.id, key));
    if (ok) reload();
  };

  const columns: Column<RegistrationWorkItem>[] = useMemo(() => [
    {
      key: "person",
      header: t(A.person),
      sortable: true,
      // By family name, which is how a person is looked up on paper — not by the rendered markup.
      sortValue: (r) => `${r.beneficiary.familyName} ${r.beneficiary.givenName}`,
      cell: (r) => (
        <span>
          {r.beneficiary.givenName} {r.beneficiary.familyName}
          <span className="muted tnum" style={{ display: "block" }}>
            {r.beneficiary.identifiers[0]
              ? `${r.beneficiary.identifiers[0].type}: ${r.beneficiary.identifiers[0].value}`
              : r.beneficiary.cardNumber ?? "—"}
          </span>
        </span>
      ),
    },
    {
      key: "filed",
      header: t(A.filed),
      sortable: true,
      // The ISO string, not the rendered date. "26 Jul 2026" sorts alphabetically into nonsense, and the
      // Arabic rendering sorts into different nonsense.
      sortValue: (r) => r.registration?.createdAt ?? null,
      cell: (r) => <span className="tnum">{r.registration ? fmt.date(r.registration.createdAt) : "—"}</span>,
    },
    {
      key: "officer",
      header: t(A.officer),
      sortable: true,
      sortValue: (r) => r.registration?.createdByName ?? null,
      cell: (r) =>
        r.registration?.createdByName
          ? <span>{r.registration.createdByName}</span>
          // Not blank: "nobody is recorded as having filed this" is a real state on applications that predate
          // the field, and it is exactly the case where a request for information has nowhere to go.
          : <span className="muted">{t(A.unknownOfficer)}</span>,
    },
    {
      key: "application",
      header: t(A.application),
      sortable: true,
      sortValue: (r) => appStateOf(r),
      cell: (r) => {
        const chip = APP_CHIP[appStateOf(r)];
        return <StatusChip kind={chip.kind} label={t(chip.label)} />;
      },
    },
    {
      // The two approval guards as real checkboxes: the officer records evidence as it arrives, and the
      // supervisor sees at a glance what is still missing. Disabled (not hidden) when there is no
      // application yet — the state is legible either way.
      key: "checks",
      header: t(A.docs),
      cell: (r) => (
        <input
          type="checkbox"
          className="mrs-checkbox"
          checked={r.registration?.documentsVerified ?? false}
          disabled={!r.registration || write.busy}
          onChange={() => void toggle(r, "documentsVerified")}
          aria-label={`${t(A.docsCheck)} — ${r.beneficiary.givenName} ${r.beneficiary.familyName}`}
        />
      ),
    },
    {
      key: "coverage",
      header: t(A.coverage),
      cell: (r) => (
        <input
          type="checkbox"
          className="mrs-checkbox"
          checked={r.registration?.coverageBound ?? false}
          disabled={!r.registration || write.busy}
          onChange={() => void toggle(r, "coverageBound")}
          aria-label={`${t(A.coverageCheck)} — ${r.beneficiary.givenName} ${r.beneficiary.familyName}`}
        />
      ),
    },
    {
      /*
       * NOTES AS AN AFFORDANCE, NOT AS PROSE IN A CELL.
       *
       * The note used to be printed in the row. It is free text an approver writes in sentences, so the column
       * had to be capped at 260px and wrapped, which made every row with a note twice as tall as the rows
       * around it and still truncated the ones that mattered. Worse, the cell could only ever show the LAST
       * thing said — the conversation behind it was invisible, and there was no way to answer.
       *
       * The count is the load-bearing part: it tells the operator whether opening this is worth a click, which
       * is the one thing the icon alone cannot say. Zero renders as a muted dash with a named state for a
       * screen reader, so "no notes" and "notes you have not opened" are never the same glyph.
       */
      key: "notes",
      header: t(A.notes),
      sortable: true,
      sortValue: (r) => r.registration?.threadCount ?? null,
      cell: (r) => {
        const count = r.registration?.threadCount ?? 0;
        if (!r.registration) return <span className="muted" aria-hidden="true">—</span>;
        if (count === 0) {
          return (
            <span className="muted">
              <span aria-hidden="true">—</span>
              <span className="sr-only">{t(A.noNotes)}</span>
            </span>
          );
        }
        return (
          <button
            type="button"
            className="reg-notebtn"
            onClick={() => setNotesOf(r)}
            aria-haspopup="dialog"
            aria-label={`${t(A.openNotes)} — ${r.beneficiary.givenName} ${r.beneficiary.familyName} (${count})`}
          >
            <Icon name="doc" width={18} height={18} aria-hidden="true" />
            <span className="reg-notecount tnum">{count}</span>
          </button>
        );
      },
    },
    {
      key: "actions",
      header: t(A.actions),
      // Pinned to the trailing edge. With the selection column and the decision button the table is wider
      // than its card at 1440px, and the column that fell past the fold was this one — the operator had to
      // scroll sideways on every row to reach the control they had come for.
      stickyEnd: true,
      cell: (r) => (
        <div className="reg-actions">
          {/* VIEW is available to both roles, which is what stops this column being empty for an officer. */}
          <button
            type="button"
            className="reg-iconbtn"
            onClick={() => setDetailOf(r)}
            aria-haspopup="dialog"
            aria-label={`${t(A.view)} — ${r.beneficiary.givenName} ${r.beneficiary.familyName}`}
          >
            <Icon name="eye" width={18} height={18} aria-hidden="true" />
          </button>
          {!r.registration || r.registration.status === "Rejected" ? (
            <Button variant="secondary" size="sm" onClick={() => void start(r)}>{t(A.startReview)}</Button>
          ) : isSupervisor ? (
            <Button
              variant="primary"
              size="sm"
              leadingIcon={<Icon name="check2" />}
              onClick={() => setDecisionOf([r])}
              disabled={filedByMe(r)}
              // Disabled buttons carry no accessible explanation of their own, so the reason is the name.
              title={filedByMe(r) ? t(A.ownFiling) : undefined}
              aria-label={filedByMe(r) ? `${t(A.decide)} — ${t(A.ownFiling)}` : undefined}
            >
              {t(A.decide)}
            </Button>
          ) : null}
        </div>
      ),
    },
    // `toggle`/`start` close over `write`, which changes identity on every mutation; the columns only need to
    // be rebuilt when the language, the role or the busy state changes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  ], [t, fmt, isSupervisor, write.busy]);

  const filters: TableFilterSpec<RegistrationWorkItem>[] = useMemo(() => [
    {
      key: "state",
      label: t(A.statusFilter),
      options: [
        { value: "Pending", label: t(A.appPending) },
        { value: "InfoRequested", label: t(A.appInfo) },
        { value: "Rejected", label: t(A.appRejected) },
        { value: "None", label: t(A.notStarted) },
      ],
      match: (r, value) => appStateOf(r) === value,
    },
  ], [t]);

  const query = useTableQuery<RegistrationWorkItem>({
    rows,
    columns,
    // Everything an operator plausibly has in hand: the name they were told, the card the person is holding,
    // the identity document number, the member number, and the officer whose work they are chasing. A search
    // that only matches the name fails on the one value the caller actually read out.
    searchText: (r) => [
      r.beneficiary.givenName, r.beneficiary.middleName, r.beneficiary.familyName,
      r.beneficiary.cardNumber, r.beneficiary.memberNo,
      r.beneficiary.individualNo, r.beneficiary.caseNo,
      r.registration?.createdByName,
      ...r.beneficiary.identifiers.map((i) => i.value),
    ].filter(Boolean).join(" "),
    searchLabel: t(A.search),
    searchPlaceholder: t(A.searchHint),
    filters,
    // Ten, not the server's twenty-five. A decision queue is worked a screenful at a time, and ten rows fit
    // above the fold on a laptop with the toolbar and the pager both visible — which is what makes the pager
    // a control the operator can see rather than one they scroll to discover.
    pageSize: 10,
    // Oldest first: this is a queue, and a queue that opens on the newest starves whoever arrived first.
    initialSortKey: "filed",
    initialSortDir: "ascending",
  });

  // A decidable row is one with an open application. Rows with none, and rows already Rejected, are excluded
  // from selection rather than silently dropped from the action: a checkbox that ticks and then does nothing
  // is worse than one that will not tick.
  // …and not one this approver filed themselves: the server refuses it, so enlisting it in a bulk decision
  // would guarantee one refusal in every batch the supervisor also registered into.
  const isDecidable = (r: RegistrationWorkItem) =>
    Boolean(r.registration) && r.registration!.status !== "Rejected" && !filedByMe(r);

  const selectedItems = useMemo(
    () => query.matched.filter((r) => selected.has(r.beneficiary.id) && isDecidable(r)),
    [query.matched, selected]);

  // Rows that vanish from the queue after a decision must not stay selected — otherwise the count in the bulk
  // bar counts work that is already done.
  useEffect(() => {
    const live = new Set(rows.map((r) => r.beneficiary.id));
    setSelected((prev) => {
      const next = new Set([...prev].filter((id) => live.has(id)));
      return next.size === prev.size ? prev : next;
    });
  }, [rows]);

  return (
    <>
      <PageHeader title={t(A.title)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}
        <AsyncSection state={state} isEmpty={(d) => d.items.length === 0} emptyLabel={A.empty}>
          {(page) => (
            <>
              {/* Said plainly rather than implied by a pager that stops at 100: a supervisor managing a queue
                  needs to know the queue is bigger than the screen. */}
              {page.total > page.items.length ? (
                <InlineAlert tone="info">
                  {fill(t(A.capped), { n: page.items.length, total: page.total })}
                </InlineAlert>
              ) : null}

              <DataTableView
                query={query}
                columns={columns}
                rowKey={(r) => r.beneficiary.id}
                caption={t(A.title)}
                emptyLabel={t(A.empty)}
                noMatchesLabel={t(A.noMatches)}
                selection={isSupervisor ? {
                  keys: selected,
                  onChange: setSelected,
                  isSelectable: isDecidable,
                  rowLabel: (r) => `${t(A.decide)} — ${r.beneficiary.givenName} ${r.beneficiary.familyName}`,
                  allLabel: t(A.selected).replace("{n}", String(query.rows.filter(isDecidable).length)),
                } : undefined}
                toolbarExtra={isSupervisor && selectedItems.length > 0 ? (
                  <div className="reg-bulkbar" role="group" aria-label={t(A.decisionLabel)}>
                    <strong className="tnum">{fill(t(A.selected), { n: selectedItems.length })}</strong>
                    <Button
                      variant="primary"
                      size="sm"
                      leadingIcon={<Icon name="check2" />}
                      onClick={() => setDecisionOf(selectedItems)}
                    >
                      {t(A.decideSelected)}
                    </Button>
                    <Button variant="ghost" size="sm" onClick={() => setSelected(new Set())}>
                      {t(A.clearSelection)}
                    </Button>
                  </div>
                ) : undefined}
              />
            </>
          )}
        </AsyncSection>
        {/* Stated ONCE, for the role it applies to, instead of once per row in a column of em dashes. */}
        {!isSupervisor && total > 0 ? <p className="muted reg-rolenote">{t(A.supervisorOnly)}</p> : null}
      </Card>

      {detailOf ? <DetailModal item={detailOf} onClose={() => setDetailOf(null)} /> : null}

      {notesOf?.registration ? (
        <NotesModal
          item={notesOf}
          onClose={() => setNotesOf(null)}
          onReplied={() => reload()}
        />
      ) : null}

      {decisionOf && decisionOf.length > 0 ? (
        <DecisionModal
          items={decisionOf}
          onClose={() => setDecisionOf(null)}
          onDecided={(message) => {
            setDecisionOf(null);
            setSelected(new Set());
            toast(message, "ok");
            reload();
          }}
        />
      ) : null}
    </>
  );
}

// ================================================================ DECISION (single and bulk)

function DecisionModal({
  items,
  onClose,
  onDecided,
}: {
  items: RegistrationWorkItem[];
  onClose: () => void;
  onDecided: (message: string) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const write = useWrite();

  const bulk = items.length > 1;
  const regs = items.map((i) => i.registration!).filter(Boolean);
  const blocked = regs.filter((r) => !(r.documentsVerified && r.coverageBound));
  const canApproveAll = blocked.length === 0;

  const [decision, setDecision] = useState<Decision | "">(canApproveAll ? "Approve" : "");
  const [notes, setNotes] = useState("");
  const [touched, setTouched] = useState(false);
  const [failures, setFailures] = useState<BulkDecisionOutcome[] | null>(null);

  const needsNotes = decision === "RequestInfo" || decision === "Reject";
  const notesError = touched && needsNotes && notes.trim() === "" ? t(A.notesRequired) : undefined;

  const confirm = async () => {
    setTouched(true);
    if (!decision) return;
    if (needsNotes && notes.trim() === "") return;

    if (!bulk) {
      const reg = regs[0];
      if (!reg) return;
      // The issued member number is the ONE fact the approver must hand onward (it goes on the card), so it
      // is captured out of the write rather than re-queried — a re-query races the projection and can miss it.
      let memberNo: string | undefined;
      const ok = await write.run(async () => {
        const r = await api.decideRegistration(reg.id, decision, notes.trim() || undefined);
        memberNo = r.memberNo;
        return r;
      });
      if (ok) onDecided(memberNo ? `${t(A.approved)}: ${memberNo}` : t(A.decided));
      return;
    }

    // Bulk. `decideRegistrations` never rejects — it reports per row — so this cannot use `write.run`'s
    // all-or-nothing success. Rows the server refused stay on screen with their reason; the rest are done.
    const outcomes = await api.decideRegistrations(regs.map((r) => r.id), decision, notes.trim() || undefined);
    const failed = outcomes.filter((o) => !o.ok);
    const succeeded = outcomes.length - failed.length;
    if (failed.length === 0) {
      onDecided(fill(t(A.bulkAllOk), { ok: succeeded }));
      return;
    }
    setFailures(failed);
  };

  const title = bulk
    ? fill(t(A.bulkTitle), { n: items.length })
    : `${t(A.decisionTitle)} — ${items[0]!.beneficiary.givenName} ${items[0]!.beneficiary.familyName}`;

  // Once some rows have been decided the modal is a REPORT, not a form: re-submitting would replay decisions
  // that already landed. So the footer collapses to a single acknowledgement that reloads the queue.
  if (failures) {
    return (
      <Modal open onOpenChange={(o) => !o && onClose()} title={title} closeLabel={t(A.close)} wide
        footer={<Button variant="primary" onClick={() => onDecided(t(A.decided))}>{t(A.close)}</Button>}>
        <InlineAlert tone="warn">
          {fill(t(A.bulkPartial), { ok: regs.length - failures.length, failed: failures.length })}
        </InlineAlert>
        <ul className="reg-failures">
          {failures.map((f) => {
            const item = items.find((i) => i.registration?.id === f.registrationId);
            return (
              <li key={f.registrationId}>
                <strong>{item ? `${item.beneficiary.givenName} ${item.beneficiary.familyName}` : f.registrationId}</strong>
                <span className="muted">{f.error}</span>
              </li>
            );
          })}
        </ul>
      </Modal>
    );
  }

  return (
    <Modal
      open
      onOpenChange={(o) => !o && onClose()}
      title={title}
      closeLabel={t(A.close)}
      footer={
        <>
          <Button variant="ghost" onClick={onClose}>{t(A.cancel)}</Button>
          <Button
            variant={decision === "Reject" ? "danger" : "primary"}
            leadingIcon={<Icon name={decision === "Reject" ? "cross" : "check2"} />}
            onClick={confirm}
            loading={write.busy}
            disabled={write.busy || !decision}
          >
            {t(A.confirm)}
          </Button>
        </>
      }
    >
      {write.error ? <InlineAlert tone="bad">{t(write.error.message)}</InlineAlert> : null}

      {bulk ? (
        <ul className="reg-bulklist">
          {items.map((i) => (
            <li key={i.beneficiary.id}>
              {i.beneficiary.givenName} {i.beneficiary.familyName}
              {i.registration && !(i.registration.documentsVerified && i.registration.coverageBound)
                ? <StatusChip kind="warn" label={t(A.appInfo)} />
                : null}
            </li>
          ))}
        </ul>
      ) : null}

      <fieldset className="mrs-choice">
        <legend className="mrs-label">{t(A.decisionLabel)}</legend>
        <label className="mrs-choice-opt">
          <input type="radio" name="decision" value="Approve" disabled={!canApproveAll} checked={decision === "Approve"} onChange={() => setDecision("Approve")} />
          <span>
            {t(A.approve)}
            {/* Disabled WITH the reason inside the option (§6 — the server re-checks either way): an approve
                option that is simply missing reads as a broken screen, not an incomplete application. In bulk
                the reason names how many rows are holding it up, because "which of the ten?" is the next
                question and the list above answers it. */}
            {!canApproveAll ? (
              <span className="mrs-choice-hint">
                {bulk ? fill(t(A.approveBlockedSome), { n: blocked.length }) : t(A.approveBlocked)}
              </span>
            ) : null}
          </span>
        </label>
        <label className="mrs-choice-opt">
          <input type="radio" name="decision" value="RequestInfo" checked={decision === "RequestInfo"} onChange={() => setDecision("RequestInfo")} />
          <span>
            {t(A.requestInfo)}
            {/* Says what the decision DOES. Request-info is the one decision whose effect happens somewhere
                the supervisor cannot see, and a supervisor who does not know it notifies anybody writes their
                note as if into a void. */}
            <span className="mrs-choice-hint">{t(A.notifies)}</span>
          </span>
        </label>
        <label className="mrs-choice-opt">
          <input type="radio" name="decision" value="Reject" checked={decision === "Reject"} onChange={() => setDecision("Reject")} />
          <span>{t(A.reject)}</span>
        </label>
      </fieldset>

      {needsNotes ? (
        <TextareaField
          label={t(A.notesLabel)}
          value={notes}
          error={notesError}
          rows={3}
          onChange={(e) => setNotes(e.currentTarget.value)}
        />
      ) : null}
    </Modal>
  );
}

// ================================================================ NOTES (the thread + the reply)

function NotesModal({
  item,
  onClose,
  onReplied,
}: {
  item: RegistrationWorkItem;
  onClose: () => void;
  onReplied: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const reg = item.registration!;
  const [entries, setEntries] = useState<RegistrationThreadEntry[] | null>(null);
  const [error, setError] = useState<Localized | null>(null);
  const [body, setBody] = useState("");
  const [touched, setTouched] = useState(false);
  const [busy, setBusy] = useState(false);

  // A closed application takes no more replies — the server answers `urn:hbmp:registration-closed`, and a
  // reply box that 409s is a box that should not have been offered.
  const closed = reg.status === "Rejected" || reg.status === "Active";

  const load = useCallback(async () => {
    try {
      setEntries(await api.registrationThread(reg.id));
    } catch (e) {
      setError(readErrorMessage(e));
    }
  }, [api, reg.id]);

  useEffect(() => { void load(); }, [load]);

  async function send() {
    setTouched(true);
    if (body.trim() === "") return;
    setBusy(true);
    setError(null);
    try {
      const entry = await api.replyToRegistration(reg.id, body.trim());
      // Appended locally as well as reloaded upstream: the operator sees their note land immediately, and the
      // queue behind the modal picks up the new count and the changed "current note" on the next read.
      setEntries((prev) => [...(prev ?? []), entry]);
      setBody("");
      setTouched(false);
      onReplied();
    } catch (e) {
      setError(readErrorMessage(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open
      onOpenChange={(o) => !o && onClose()}
      title={`${t(A.notesTitle)} — ${item.beneficiary.givenName} ${item.beneficiary.familyName}`}
      description={t(A.notesIntro)}
      closeLabel={t(A.close)}
      wide
      footer={<Button variant="ghost" onClick={onClose}>{t(A.close)}</Button>}
    >
      {error ? <InlineAlert tone="bad">{t(error)}</InlineAlert> : null}

      {entries && entries.length === 0 ? <InlineAlert tone="info">{t(A.threadEmpty)}</InlineAlert> : null}

      {entries && entries.length > 0 ? (
        <ol className="reg-thread">
          {entries.map((e) => (
            <li key={e.id} className={e.kind === "Decision" ? "reg-entry reg-entry-decision" : "reg-entry"}>
              <div className="reg-entry-head">
                {/* A ruling and an answer are never rendered the same way: a reply that reads as a decision is
                    a reply somebody acts on as if it were one. */}
                <StatusChip
                  kind={e.kind === "Decision" ? (e.decision === "Reject" ? "bad" : e.decision === "Approve" ? "ok" : "warn") : "neu"}
                  label={e.kind === "Decision" ? t(A.decisionEntry) : t(A.replyEntry)}
                />
                <span className="reg-entry-who">{e.authorName ?? t(A.unknownOfficer)}</span>
                <span className="muted tnum">{fmt.dateTime(e.createdAt)}</span>
              </div>
              <p className="reg-entry-body">{e.body}</p>
            </li>
          ))}
        </ol>
      ) : null}

      {closed ? (
        <InlineAlert tone="info">{t(A.closedThread)}</InlineAlert>
      ) : (
        <div className="reg-reply">
          <TextareaField
            label={t(A.replyLabel)}
            help={t(A.replyHint)}
            value={body}
            rows={3}
            error={touched && body.trim() === "" ? t(A.replyEmpty) : undefined}
            onChange={(e) => setBody(e.currentTarget.value)}
          />
          <div>
            <Button variant="primary" leadingIcon={<Icon name="plus" />} loading={busy} onClick={() => void send()}>
              {t(A.send)}
            </Button>
          </div>
        </div>
      )}
    </Modal>
  );
}

// ================================================================ DETAIL (the eye)

/**
 * The registration as it was filed — the same fields the register form captured, plus the paperwork.
 *
 * Almost none of this is a new read. The worklist endpoint has always returned the elected coverage and the
 * six standing notes, already minimum-necessary projected, and the client threw them away; the identity fields
 * come from the same disclosure the row is built from. Only the document list is a second request, and it is
 * made when the modal opens rather than for every row in the queue.
 *
 * Documents are METADATA here. Whether the paperwork is present is the review question; opening a scan is a
 * separate, separately-audited disclosure that belongs on the member's documents screen.
 */
function DetailModal({ item, onClose }: { item: RegistrationWorkItem; onClose: () => void }) {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const b = item.beneficiary;
  const reg = item.registration;
  const [docs, setDocs] = useState<BeneficiaryDocument[] | null>(null);
  const [docError, setDocError] = useState<Localized | null>(null);

  useEffect(() => {
    let live = true;
    api.beneficiaryDocuments(b.id)
      .then((d) => { if (live) setDocs(d); })
      .catch((e) => { if (live) setDocError(readErrorMessage(e)); });
    return () => { live = false; };
  }, [api, b.id]);

  // An undisclosed field and an empty one are different facts, and an operator who cannot tell them apart
  // asks the beneficiary for something the system already holds.
  const value = (v: string | undefined | null) =>
    v === undefined ? <span className="muted">{t(A.notDisclosed)}</span> : v === null || v === "" ? "—" : v;

  const identifier = b.identifiers.find((i) => i.isPrimary) ?? b.identifiers[0];
  const phone = b.contacts?.find((c) => c.type === "Phone" && c.isPrimary) ?? b.contacts?.find((c) => c.type === "Phone");

  return (
    <Modal
      open
      onOpenChange={(o) => !o && onClose()}
      title={`${t(A.detailTitle)} — ${b.givenName} ${b.familyName}`}
      closeLabel={t(A.close)}
      wide
      footer={<Button variant="ghost" onClick={onClose}>{t(A.close)}</Button>}
    >
      <section className="reg-detail">
        <h3>{t(A.secPerson)}</h3>
        <dl className="reg-kv">
          <div><dt>{t(A.fullName)}</dt><dd>{[b.givenName, b.middleName, b.familyName].filter(Boolean).join(" ")}</dd></div>
          <div><dt>{t(A.card)}</dt><dd className="tnum">{value(b.cardNumber)}</dd></div>
          <div>
            <dt>{t(A.birthDate)}</dt>
            <dd className="tnum">
              {b.birthDate === undefined ? value(undefined) : fmt.date(b.birthDate)}
              {b.birthDateIsApproximate ? <span className="muted"> ({t(A.approxDate)})</span> : null}
            </dd>
          </div>
          <div><dt>{t(A.sex)}</dt><dd>{value(b.sex)}</dd></div>
          <div><dt>{t(A.nationality)}</dt><dd>{value(b.nationalityCode)}</dd></div>
          <div>
            <dt>{t(A.identifier)}</dt>
            <dd className="tnum">{identifier ? `${identifier.type}: ${identifier.value}` : value(undefined)}</dd>
          </div>
          <div><dt>{t(A.phone)}</dt><dd className="tnum">{phone ? phone.value : value(b.contacts === undefined ? undefined : null)}</dd></div>
          <div><dt>{t(A.individualNo)}</dt><dd className="tnum">{value(b.individualNo)}</dd></div>
          <div><dt>{t(A.caseNo)}</dt><dd className="tnum">{value(b.caseNo)}</dd></div>
        </dl>
      </section>

      {reg ? (
        <>
          <section className="reg-detail">
            <h3>{t(A.secApplication)}</h3>
            <dl className="reg-kv">
              <div><dt>{t(A.application)}</dt><dd><StatusChip kind={APP_CHIP[appStateOf(item)].kind} label={t(APP_CHIP[appStateOf(item)].label)} /></dd></div>
              <div><dt>{t(A.filed)}</dt><dd className="tnum">{fmt.dateTime(reg.createdAt)}</dd></div>
              <div><dt>{t(A.officer)}</dt><dd>{reg.createdByName ?? t(A.unknownOfficer)}</dd></div>
              <div><dt>{t(A.docs)}</dt><dd><StatusChip kind={reg.documentsVerified ? "ok" : "warn"} label={t(reg.documentsVerified ? A.approve : A.appInfo)} /></dd></div>
              <div><dt>{t(A.coverage)}</dt><dd><StatusChip kind={reg.coverageBound ? "ok" : "warn"} label={t(reg.coverageBound ? A.approve : A.appInfo)} /></dd></div>
            </dl>
          </section>

          <section className="reg-detail">
            <h3>{t(A.secCoverage)}</h3>
            {reg.enrolment ? (
              <dl className="reg-kv">
                <div><dt>{t(A.plan)}</dt><dd className="tnum">{reg.enrolment.planId}</dd></div>
                <div><dt>{t(A.tier)}</dt><dd className="tnum">{reg.enrolment.networkTierId}</dd></div>
                <div><dt>{t(A.contribution)}</dt><dd className="tnum">{fmt.number(reg.enrolment.contributionPercent)}%</dd></div>
                <div><dt>{t(A.branch)}</dt><dd className="tnum">{value(reg.enrolment.defaultBranchId ?? null)}</dd></div>
              </dl>
            ) : (
              <InlineAlert tone="info">{t(A.noCoverage)}</InlineAlert>
            )}
          </section>

          <section className="reg-detail">
            <h3>{t(A.secNotes)}</h3>
            {reg.standingNotes.length === 0 ? (
              <p className="muted">{t(A.noStandingNotes)}</p>
            ) : (
              <dl className="reg-kv">
                {reg.standingNotes.map((n) => (
                  <div key={n.slot}>
                    <dt>{t({ en: n.labelEn, ar: n.labelAr })}</dt>
                    <dd>
                      {/* A withheld slot is a NAMED locked state, never an empty one: "no diagnosis is on
                          file" and "a diagnosis is on file you may not read" are different facts. */}
                      {n.withheld
                        ? <span className="muted"><Icon name="info" width={14} height={14} aria-hidden="true" /> {t(A.withheld)}</span>
                        : n.value}
                    </dd>
                  </div>
                ))}
              </dl>
            )}
          </section>
        </>
      ) : (
        <InlineAlert tone="warn">{t(A.noApplication)}</InlineAlert>
      )}

      <section className="reg-detail">
        <h3>{t(A.secDocuments)}</h3>
        {docError ? <InlineAlert tone="bad">{t(docError)}</InlineAlert> : null}
        {docs && docs.length === 0 ? <p className="muted">{t(A.noDocuments)}</p> : null}
        {docs && docs.length > 0 ? (
          <ul className="reg-docs">
            {docs.map((d) => {
              const known = DOCUMENT_TYPES.find((x) => x.documentClass === d.docType);
              return (
                <li key={d.id}>
                  <Icon name="doc" width={16} height={16} aria-hidden="true" />
                  <span>{known ? t(known.label) : d.docType}</span>
                  <span className="muted tnum">
                    {fmt.date(d.uploadedAt)}
                    {d.uploadedBy ? ` · ${t(A.uploadedBy)} ${d.uploadedBy}` : ""}
                  </span>
                </li>
              );
            })}
          </ul>
        ) : null}
      </section>
    </Modal>
  );
}

import { useMemo, useState } from "react";
import { useFormat } from "../i18n/useFormat";
import { Button, Card, ComboboxField, DataTable, DataTableView, InlineAlert, InputField, StatusChip, useTableQuery, useTheme } from "@mersal/design-system";
import type { Column, TableFilterSpec } from "@mersal/design-system";
import type {
  Beneficiary360,
  CaseListItem,
  CoordinationTask,
  Escalation,
  Localized,
  MaskedSection,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useWrite, writeErrorText } from "../api/useWrite";
import { AsyncSection, PageHeader, fillLocalized, useLoc } from "./_shared";
import { ConfirmAction } from "./ConfirmAction";

const S = {
  casesTitle: { en: "My Cases", ar: "حالاتي" },
  casesEmpty: { en: "You have no assigned cases.", ar: "لا توجد حالات مُسندة إليك." },
  caseNo: { en: "Case", ar: "الحالة" },
  search: { en: "Search", ar: "بحث" },
  casesSearchHint: { en: "Case number, member token or category", ar: "رقم الحالة أو رمز العضو أو الفئة" },
  escSearchHint: { en: "Case number or reason", ar: "رقم الحالة أو السبب" },
  noMatches: {
    en: "No rows match. Change the search or clear the filters.",
    ar: "لا توجد صفوف مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },
  beneficiary: { en: "Beneficiary", ar: "المستفيد" },
  category: { en: "Category", ar: "التصنيف" },
  priority: { en: "Priority", ar: "الأولوية" },
  status: { en: "Status", ar: "الحالة" },
  action: { en: "Action", ar: "إجراء" },
  open: { en: "Open 360", ar: "فتح 360" },
  pick: { en: "Select a case to open its coordination 360.", ar: "اختر حالة لفتح ملف التنسيق 360." },

  coverage: { en: "Coverage", ar: "التغطية" },
  plan: { en: "Plan", ar: "الخطة" },
  category2: { en: "Band", ar: "الفئة" },
  cap: { en: "Annual cap", ar: "الحد السنوي" },
  remaining: { en: "Remaining", ar: "المتبقّي" },
  carePlan: { en: "Care plan", ar: "خطة الرعاية" },
  goals: { en: "Goals", ar: "الأهداف" },
  reviewDue: { en: "Review due", ar: "موعد المراجعة" },
  appts: { en: "Appointments", ar: "المواعيد" },
  approvals: { en: "Open approvals", ar: "الموافقات المفتوحة" },
  clinical: { en: "Clinical summary (coordination)", ar: "ملخّص سريري (تنسيق)" },
  diagnoses: { en: "Active diagnoses", ar: "التشخيصات النشطة" },
  notes: { en: "Clinical notes", ar: "ملاحظات سريرية" },
  prescriptions: { en: "Prescriptions", ar: "الوصفات" },
  results: { en: "Results", ar: "النتائج" },
  summaryOnly: { en: "summary only", ar: "ملخّص فقط" },
  onFile: { en: "on file", ar: "في الملف" },
  tasksTitle: { en: "Coordination tasks", ar: "مهام التنسيق" },
  tasksEmpty: { en: "No coordination tasks on this case.", ar: "لا توجد مهام تنسيق لهذه الحالة." },

  escTitle: { en: "Escalations", ar: "التصعيدات" },
  escEmpty: { en: "No escalations raised.", ar: "لا توجد تصعيدات." },
  raisedTo: { en: "Raised to", ar: "مُصعّدة إلى" },
  reason: { en: "Reason", ar: "السبب" },
  raisedAt: { en: "Raised at", ar: "وقت التصعيد" },

  // ---- 33.7 — completing a coordination task ----
  taskActions: { en: "Actions", ar: "إجراءات" },
  start: { en: "Start", ar: "بدء" },
  complete: { en: "Complete", ar: "إنجاز" },
  outcome: { en: "What happened", ar: "ما الذي حدث" },
  outcomeHelp: {
    en: "One line for the next person to read. It stays on the task.",
    ar: "سطر واحد يقرأه من يأتي بعدك. يبقى مع المهمة.",
  },
  completeTitle: { en: "Complete this task?", ar: "إنجاز هذه المهمة؟" },
  completeBody: {
    en: "\"{0}\" is marked done and leaves your outstanding list. Completing a task is final — a task cannot be reopened.",
    ar: "تُعلَّم \"{0}\" كمنجزة وتغادر قائمتك المتبقّية. الإنجاز نهائي — لا يمكن إعادة فتح المهمة.",
  },
  completeIrreversible: { en: "Done is a terminal state on this task.", ar: "الإنجاز حالة نهائية لهذه المهمة." },

  // ---- 33.7 — raising and closing an escalation ----
  escalate: { en: "Escalate", ar: "تصعيد" },
  escalateTitle: { en: "Escalate this case?", ar: "تصعيد هذه الحالة؟" },
  escalateBody: {
    en: "{0} is asked to look at this case now, and is notified. Say what you need from them — the reason is what they read first.",
    ar: "يُطلب من {0} النظر في هذه الحالة الآن، ويُخطَر بذلك. اذكر ما تحتاجه منهم — فالسبب هو أول ما يقرؤونه.",
  },
  escalateReversible: {
    en: "An escalation can be resolved later; it stays on the case either way.",
    ar: "يمكن إغلاق التصعيد لاحقاً؛ ويبقى مسجّلاً على الحالة في الحالتين.",
  },
  raiseTo: { en: "Ask", ar: "الجهة" },
  escReason: { en: "What you need, and why now", ar: "ما تحتاجه ولماذا الآن" },
  escNeedsBoth: { en: "Choose who to ask, and say what you need.", ar: "اختر الجهة، واذكر ما تحتاجه." },
  acknowledge: { en: "Acknowledge", ar: "استلام" },
  resolve: { en: "Resolve", ar: "إغلاق" },
  resolveTitle: { en: "Resolve this escalation?", ar: "إغلاق هذا التصعيد؟" },
  resolveBody: {
    en: "It stops being outstanding on {0}. Record what settled it — that note is the only account of how it ended.",
    ar: "يتوقف عن كونه متبقياً على {0}. سجّل ما الذي أنهاه — فتلك الملاحظة هي السجل الوحيد لكيفية انتهائه.",
  },
  resolveIrreversible: { en: "Resolved is terminal on an escalation.", ar: "الإغلاق حالة نهائية للتصعيد." },
  resolutionNote: { en: "How it was settled", ar: "كيف أُنهي" },
  escActions: { en: "Actions", ar: "إجراءات" },
  resolvedNote: { en: "Resolution", ar: "الإغلاق" },

  // ---- 33.7 — closing the case ----
  closeCase: { en: "Close this case", ar: "إغلاق الحالة" },
  closeTitle: { en: "Close this case?", ar: "إغلاق هذه الحالة؟" },
  closeBody: {
    en: "{0} leaves your case load. Closed is the end of the line — the case cannot be reopened, and your access to this beneficiary's coordination view goes with the assignment.",
    ar: "تغادر {0} قائمة حالاتك. الإغلاق نهاية المسار — لا يمكن إعادة فتح الحالة، ويزول وصولك إلى ملف تنسيق هذا المستفيد مع الإسناد.",
  },
  openTasksWarning: {
    en: "{0} task(s) on this case are still outstanding. Closing does not complete them.",
    ar: "ما زالت {0} مهمة على هذه الحالة دون إنجاز. الإغلاق لا يُنجزها.",
  },
  closed: { en: "Case closed.", ar: "أُغلقت الحالة." },
} satisfies Record<string, Localized>;

/** The roles a caseworker escalates TO. The server takes any string; these are the ones that mean something. */
const ESCALATION_TARGETS: ReadonlyArray<{ value: string; label: Localized }> = [
  { value: "medical_approval", label: { en: "Medical Approval", ar: "الموافقة الطبية" } },
  { value: "medical_director", label: { en: "Medical Director", ar: "المدير الطبي" } },
  { value: "beneficiary_mgmt_supervisor", label: { en: "Registration Supervisor", ar: "مشرف التسجيل" } },
  { value: "policy_admin", label: { en: "Policy Administration", ar: "إدارة الوثائق" } },
];

const PRIORITY_KIND = { low: "neu", normal: "info", high: "warn", urgent: "bad" } as const;

/** My Cases → coordination-360 master/detail. The list is the caller's ASSIGNED cases; opening a case assembles the
 * field-scoped coordination view (a case the caller is not assigned to would resolve to an authorized deny state). */
export function MyCases() {
  const api = useApi();
  const t = useLoc();
  const cases = useAsync<CaseListItem[]>(() => api.myCases(), []);
  const [selected, setSelected] = useState<string | null>(null);

  const cols: Column<CaseListItem>[] = [
    { key: "caseNo", header: t(S.caseNo), cell: (r) => <span className="tnum">{r.caseNo}</span>, sortable: true, sortValue: (r) => r.caseNo },
    { key: "beneficiary", header: t(S.beneficiary), cell: (r) => <span className="tnum">{r.beneficiary.token}</span>, sortable: true, sortValue: (r) => r.beneficiary.token },
    { key: "category", header: t(S.category), cell: (r) => <span>{r.category}</span>, sortable: true, sortValue: (r) => r.category },
    { key: "priority", header: t(S.priority), cell: (r) => <StatusChip kind={PRIORITY_KIND[r.priority]} label={r.priority} />, sortable: true, sortValue: (r) => r.priority },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    {
      key: "action",
      header: t(S.action),
      cell: (r) => (
        <Button size="sm" variant="secondary" onClick={() => setSelected(r.id)}>
          {t(S.open)}
        </Button>
      ),
    },
  ];

  /*
    A caseworker's own load, which only grows. Priority and status are the two axes it is worked along —
    "what is urgent" and "what is still open" — and both vocabularies are the domain's, so they are declared
    rather than derived.

    Read outside AsyncSection's render prop: a hook in there would be conditional on the load finishing.
  */
  const filters: TableFilterSpec<CaseListItem>[] = useMemo(() => [
    {
      key: "priority",
      label: t(S.priority),
      options: [
        { value: "emergency", label: "emergency" },
        { value: "urgent", label: "urgent" },
        { value: "routine", label: "routine" },
      ],
      match: (r, value) => r.priority === value,
    },
  ], [t]);

  const query = useTableQuery<CaseListItem>({
    rows: cases.data ?? [],
    columns: cols,
    // The case number off a note, or the member token — the two things a caseworker arrives holding.
    searchText: (r) => [r.caseNo, r.beneficiary.token, r.category].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.casesSearchHint),
    filters,
    pageSize: 25,
    persistKey: "my-cases",
  });

  return (
    <>
      <PageHeader title={t(S.casesTitle)} />
      <div className="split split-wide">
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <AsyncSection state={cases} isEmpty={(d) => d.length === 0} emptyLabel={S.casesEmpty}>
            {() => (
              // 18.D3 (U6): interactive rows with no onSelect — a keyboard user could focus a case and
              // press Enter to no effect. Enter/Space now opens the 360 panel, same as the mouse.
              <DataTableView
                query={query}
                columns={cols}
                rowKey={(r) => r.id}
                caption={t(S.casesTitle)}
                interactive
                selectedKey={selected ?? undefined}
                onSelect={(r) => setSelected(r.id)}
                emptyLabel={t(S.casesEmpty)}
                noMatchesLabel={t(S.noMatches)}
              />
            )}
          </AsyncSection>
        </Card>
        <div>
          {selected ? (
            <Beneficiary360Panel key={selected} caseId={selected} t={t} onChanged={cases.reload} />
          ) : (
            <Card style={{ padding: "var(--sp6)" }}>
              <p className="muted">{t(S.pick)}</p>
            </Card>
          )}
        </div>
      </div>
    </>
  );
}

function Beneficiary360Panel({ caseId, t, onChanged }: { caseId: string; t: (l: Localized) => string; onChanged: () => void }) {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const view = useAsync<Beneficiary360>(() => api.beneficiary360(caseId), [caseId]);
  return (
    <AsyncSection state={view} emptyLabel={S.pick}>
      {(v) => (
        <div className="stack" style={{ gap: "var(--sp4)" }}>
          <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }}>
            <div className="result-head">
              <h2 className="section-h" style={{ margin: 0 }}>{v.caseNo}</h2>
              <span className="tnum muted">{v.beneficiary.token}</span>
            </div>
            <dl className="kv-grid">
              <div><dt>{t(S.coverage)}</dt><dd><StatusChip kind={v.coverage.status.kind} label={t(v.coverage.status.label)} /></dd></div>
              <div><dt>{t(S.plan)}</dt><dd>{t(v.coverage.planName)}</dd></div>
              <div><dt>{t(S.category2)}</dt><dd>{t(v.coverage.coverageCategory)}</dd></div>
              {v.coverage.remaining && <div><dt>{t(S.remaining)}</dt><dd className="tnum">{fmt.money(v.coverage.remaining)}</dd></div>}
            </dl>
          </Card>

          <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }}>
            <h2 className="section-h" style={{ margin: 0 }}>{t(S.carePlan)} · {t(v.carePlan.status)}</h2>
            <div>
              {/* 33.7 — was a bare <dt> with no <dl> parent, which axe reports as `dlitem` and a screen
                  reader announces as an orphaned term. It is a LABEL above a list, not a description-list
                  entry; the association is carried by aria-labelledby instead. Only found now because the
                  360 panel had never been axe-scanned: `a11y-routes` renders each route's landing state,
                  and this panel only exists once a case is selected. */}
              <p className="muted" id="care-plan-goals">{t(S.goals)}</p>
              <ul className="chip-list" aria-labelledby="care-plan-goals">
                {v.carePlan.goals.map((g, i) => <li key={i}><StatusChip kind="info" label={t(g)} /></li>)}
              </ul>
            </div>
          </Card>

          <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }}>
            <h2 className="section-h" style={{ margin: 0 }}>{t(S.appts)}</h2>
            <ul className="doc-list">
              {v.appointments.map((a) => (
                <li key={a.id}><span className="tnum">{fmt.dateTime(a.when)}</span> · {t(a.clinic)} · <StatusChip kind={a.status.kind} label={t(a.status.label)} /></li>
              ))}
            </ul>
            <h2 className="section-h" style={{ margin: 0 }}>{t(S.approvals)}</h2>
            <ul className="doc-list">
              {v.openApprovals.map((a) => (
                <li key={a.authNo}><span className="tnum">{a.authNo}</span> · <StatusChip kind={a.status.kind} label={t(a.status.label)} /></li>
              ))}
            </ul>
          </Card>

          {/* Coordination CLINICAL SUMMARY (min-necessary): diagnoses are coord-visible; notes / prescriptions /
              results are shown ONLY as a "summary only, N on file" affordance — never the record body. */}
          <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp3)" }}>
            <h2 className="section-h" style={{ margin: 0 }}>{t(S.clinical)}</h2>
            <div>
              <p className="muted" id="active-diagnoses">{t(S.diagnoses)}</p>
              <ul className="chip-list" aria-labelledby="active-diagnoses">
                {v.clinical.activeDiagnoses.map((d) => <li key={d.code}><StatusChip kind="info" label={`${d.code} · ${t(d.label)}`} /></li>)}
              </ul>
            </div>
            <dl className="kv-grid">
              <MaskedRow label={t(S.notes)} section={v.clinical.notes} t={t} />
              <MaskedRow label={t(S.prescriptions)} section={v.clinical.prescriptions} t={t} />
              <MaskedRow label={t(S.results)} section={v.clinical.results} t={t} />
            </dl>
          </Card>

          <CaseTasks caseId={caseId} t={t} onChanged={onChanged} />
          <CaseActions caseId={caseId} caseNo={v.caseNo} t={t} onChanged={onChanged} />
        </div>
      )}
    </AsyncSection>
  );
}

/**
 * The two acts a caseworker's whole job comes down to, and neither had a control.
 *
 * <p><b>Escalate.</b> The Escalations section listed escalations, and nothing in the platform could raise
 * one. <code>POST /cases/{id}/escalate</code> writes the row, moves an on-hold case back into the active
 * lane, emits <code>CaseEscalated</code> so the target role is told, and audits it — all in one transaction.
 * Every part of that existed and was unreachable, so the register was a list of things that could only have
 * arrived from somewhere else.</p>
 *
 * <p><b>Close.</b> <code>PATCH /cases/{id}/status</code>, likewise. A case load that cannot be closed is not
 * a case load; it is a growing list, and the count beside a caseworker's name stops meaning anything.</p>
 */
function CaseActions({
  caseId,
  caseNo,
  t,
  onChanged,
}: {
  caseId: string;
  caseNo: string;
  t: (l: Localized) => string;
  onChanged: () => void;
}) {
  const api = useApi();
  const { lang } = useTheme();
  const write = useWrite();
  const [escalating, setEscalating] = useState(false);
  const [target, setTarget] = useState("");
  const [reason, setReason] = useState("");
  const [closing, setClosing] = useState(false);
  const [done, setDone] = useState(false);

  async function escalate() {
    const ok = await write.run((key) => api.raiseEscalation(caseId, target, reason.trim(), key));
    setEscalating(false);
    if (ok) { setTarget(""); setReason(""); onChanged(); }
  }

  async function close() {
    const ok = await write.run(() => api.setCaseState(caseId, "closed"));
    setClosing(false);
    if (ok) { setDone(true); onChanged(); }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp4)", display: "grid", gap: "var(--sp3)" }}>
      <div aria-live="polite" className="stack" style={{ gap: "var(--sp2)" }}>
        {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
        {done && <StatusChip kind="ok" label={t(S.closed)} />}
      </div>
      <div className="row-actions">
        <Button variant="secondary" onClick={() => { setTarget(""); setReason(""); setEscalating(true); }}>
          {t(S.escalate)}
        </Button>{" "}
        <Button variant="danger" onClick={() => setClosing(true)}>{t(S.closeCase)}</Button>
      </div>

      <ConfirmAction
        open={escalating}
        onOpenChange={setEscalating}
        destructive={false}
        title={S.escalateTitle}
        body={fillLocalized(
          S.escalateBody,
          t(ESCALATION_TARGETS.find((x) => x.value === target)?.label ?? { en: "The role you choose", ar: "الجهة التي تختارها" }),
        )}
        description={S.escalateReversible}
        confirmLabel={S.escalate}
        // The server refuses either field blank (422 role-required / reason-required). Said here rather than
        // sent and bounced: an escalation is raised because something is urgent, and a round trip that comes
        // back "reason-required" spends the operator's attention on the form instead of the case.
        canConfirm={target !== "" && reason.trim() !== ""}
        onConfirm={escalate}
      >
        <div className="stack" style={{ gap: "var(--sp2)" }}>
          <ComboboxField
            label={t(S.raiseTo)}
            value={target || null}
            onChange={setTarget}
            options={ESCALATION_TARGETS.map((x) => ({ value: x.value, label: t(x.label) }))}
          />
          <InputField label={t(S.escReason)} value={reason} onChange={(e) => setReason(e.currentTarget.value)} />
          <div aria-live="polite">
            {(target === "" || reason.trim() === "") && <InlineAlert tone="info">{t(S.escNeedsBoth)}</InlineAlert>}
          </div>
        </div>
      </ConfirmAction>

      <ConfirmAction
        open={closing}
        onOpenChange={setClosing}
        destructive
        title={S.closeTitle}
        body={fillLocalized(S.closeBody, caseNo)}
        confirmLabel={S.closeCase}
        onConfirm={close}
      />
    </Card>
  );
}

function MaskedRow({ label, section, t }: { label: string; section: MaskedSection; t: (l: Localized) => string }) {
  return (
    <div>
      <dt>{label}</dt>
      <dd>
        <StatusChip kind="neu" label={`${section.count} ${t(S.onFile)}`} />{" "}
        <span className="muted">· {t(S.summaryOnly)}</span>
      </dd>
    </div>
  );
}

/**
 * The caseworker's tasks on this case — and, since 33.7, a way to finish one.
 *
 * <p>This table rendered a title and a status chip and offered nothing. case-service has taken
 * <code>PATCH /cases/{id}/tasks/{taskId}</code> under <code>case:write</code> since the phase began, and
 * <code>case_manager</code> has held that scope since the 0001 seed. So a coordination list could only ever
 * grow: every task a caseworker completed in the world stayed outstanding in the platform, and the count
 * beside their name meant nothing after the first week.</p>
 *
 * <p>The transitions offered are the ones <code>CaseWorkflow</code> allows from the row's current state, and
 * no others. Todo → InProgress → Done; Done and Cancelled are terminal and the server answers 409 for
 * anything else. Offering a button that returns 409 teaches an operator the platform is unreliable rather
 * than that the move is not available.</p>
 */
function CaseTasks({ caseId, t, onChanged }: { caseId: string; t: (l: Localized) => string; onChanged: () => void }) {
  const api = useApi();
  const { lang } = useTheme();
  const tasks = useAsync<CoordinationTask[]>(() => api.caseTasks(caseId), [caseId]);
  const write = useWrite();
  const [completing, setCompleting] = useState<CoordinationTask | null>(null);
  const [outcome, setOutcome] = useState("");

  async function start(task: CoordinationTask) {
    const ok = await write.run(() => api.updateCaseTask(caseId, task.id, "in_progress"));
    if (ok) { tasks.reload(); onChanged(); }
  }

  async function complete() {
    if (completing === null) return;
    const ok = await write.run(() => api.updateCaseTask(caseId, completing.id, "done", outcome.trim() || undefined));
    setCompleting(null);
    if (ok) { setOutcome(""); tasks.reload(); onChanged(); }
  }

  const cols: Column<CoordinationTask>[] = [
    { key: "title", header: t(S.tasksTitle), cell: (r) => t(r.title), sortable: true, sortValue: (r) => t(r.title) },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    {
      key: "actions",
      header: t(S.taskActions),
      cell: (r) => (
        <span className="row-actions">
          {r.state === "todo" && (
            <Button size="sm" variant="ghost" onClick={() => void start(r)}>{t(S.start)}</Button>
          )}
          {(r.state === "todo" || r.state === "in_progress") && (
            <Button size="sm" variant="secondary" onClick={() => { setOutcome(""); setCompleting(r); }}>
              {t(S.complete)}
            </Button>
          )}
          {/* Done and Cancelled are terminal in `CaseWorkflow`: the server answers 409 for any move out of
              them, so nothing is offered rather than offered-and-refused. */}
          {(r.state === "done" || r.state === "cancelled") && <span className="muted">—</span>}
        </span>
      ),
    },
  ];

  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <h2 className="section-h" style={{ margin: "0 0 var(--sp2)", paddingInline: "var(--sp2)" }}>{t(S.tasksTitle)}</h2>
      <div aria-live="polite" style={{ paddingInline: "var(--sp2)" }}>
        {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
      </div>
      <AsyncSection state={tasks} isEmpty={(d) => d.length === 0} emptyLabel={S.tasksEmpty}>
        {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.tasksTitle)} density="compact" />}
      </AsyncSection>

      <ConfirmAction
        open={completing !== null}
        onOpenChange={(o) => !o && setCompleting(null)}
        destructive={false}
        title={S.completeTitle}
        body={fillLocalized(S.completeBody, completing ? t(completing.title) : "")}
        description={S.completeIrreversible}
        confirmLabel={S.complete}
        onConfirm={complete}
      >
        <InputField
          label={t(S.outcome)}
          help={t(S.outcomeHelp)}
          value={outcome}
          onChange={(e) => setOutcome(e.currentTarget.value)}
        />
      </ConfirmAction>
    </Card>
  );
}

/**
 * Escalations raised from the caller's case load — and, since 33.7, closable.
 *
 * <p>Two things were wrong and they compounded. Every row rendered the SAME amber "Escalated" chip because
 * <code>HttpApiClient.escalations</code> wrote it as a literal, on the reasoning that "an escalation is by
 * definition something that needed raising" — true of the act, not of the record. case-service tracks
 * Raised → Acknowledged → Resolved. And nothing in the platform could move a row along that path, so a
 * register whose entire purpose is showing what is still outstanding showed everything as outstanding,
 * permanently, and there was no way for that to ever stop being true.</p>
 */
export function Escalations() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const { lang } = useTheme();
  const state = useAsync<Escalation[]>(() => api.escalations(), []);
  const write = useWrite();
  const [resolving, setResolving] = useState<Escalation | null>(null);
  const [note, setNote] = useState("");

  async function acknowledge(e: Escalation) {
    const ok = await write.run(() => api.updateEscalation(e.caseId, e.id, "acknowledged"));
    if (ok) state.reload();
  }

  async function resolve() {
    if (resolving === null) return;
    const ok = await write.run(() => api.updateEscalation(resolving.caseId, resolving.id, "resolved", note.trim() || undefined));
    setResolving(null);
    if (ok) { setNote(""); state.reload(); }
  }

  const cols: Column<Escalation>[] = [
    { key: "caseNo", header: t(S.caseNo), cell: (r) => <span className="tnum">{r.caseNo}</span>, sortable: true, sortValue: (r) => r.caseNo },
    { key: "raisedTo", header: t(S.raisedTo), cell: (r) => t(r.raisedToRole), sortable: true, sortValue: (r) => t(r.raisedToRole) },
    { key: "reason", header: t(S.reason), cell: (r) => r.reason, sortable: true, sortValue: (r) => r.reason },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "raisedAt", header: t(S.raisedAt), cell: (r) => <span className="tnum">{fmt.dateTime(r.raisedAt)}</span>, sortable: true, sortValue: (r) => r.raisedAt },
    {
      key: "actions",
      header: t(S.escActions),
      // The transitions `CaseWorkflow` allows from this row's state, and no others. A resolved escalation
      // shows the note instead: that line is the only account of how it ended, and hiding it once the row
      // goes green is how a register becomes a list of dates.
      cell: (r) => (r.state === "resolved" ? (
        <span className="muted">{r.resolutionNote ?? "—"}</span>
      ) : (
        <span className="row-actions">
          {r.state === "raised" && (
            <Button size="sm" variant="ghost" onClick={() => void acknowledge(r)}>{t(S.acknowledge)}</Button>
          )}{" "}
          <Button size="sm" variant="secondary" onClick={() => { setNote(""); setResolving(r); }}>
            {t(S.resolve)}
          </Button>
        </span>
      )),
    },
  ];

  /** An escalation register: append-only in practice, and read newest-first for what is still outstanding. */
  const query = useTableQuery<Escalation>({
    rows: state.data ?? [],
    columns: cols,
    searchText: (r) => [r.caseNo, r.reason, t(r.raisedToRole)].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.escSearchHint),
    pageSize: 25,
    initialSortKey: "raisedAt",
    initialSortDir: "descending",
    persistKey: "escalations",
  });

  return (
    <>
      <PageHeader title={t(S.escTitle)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <div aria-live="polite" style={{ paddingInline: "var(--sp2)" }}>
          {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
        </div>
        <AsyncSection state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.escEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.escTitle)}
              emptyLabel={t(S.escEmpty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>

      <ConfirmAction
        open={resolving !== null}
        onOpenChange={(o) => !o && setResolving(null)}
        destructive={false}
        title={S.resolveTitle}
        body={fillLocalized(S.resolveBody, resolving?.caseNo ?? "")}
        description={S.resolveIrreversible}
        confirmLabel={S.resolve}
        onConfirm={resolve}
      >
        <InputField label={t(S.resolutionNote)} value={note} onChange={(e) => setNote(e.currentTarget.value)} />
      </ConfirmAction>
    </>
  );
}

import { useMemo, useState } from "react";
import { useFormat } from "../i18n/useFormat";
import { Button, Card, DataTable, DataTableView, StatusChip, useTableQuery } from "@mersal/design-system";
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
import { AsyncSection, PageHeader, useLoc } from "./_shared";

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
} satisfies Record<string, Localized>;

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
            <Beneficiary360Panel key={selected} caseId={selected} t={t} />
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

function Beneficiary360Panel({ caseId, t }: { caseId: string; t: (l: Localized) => string }) {
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
              <dt className="muted">{t(S.goals)}</dt>
              <ul className="chip-list">{v.carePlan.goals.map((g, i) => <li key={i}><StatusChip kind="info" label={t(g)} /></li>)}</ul>
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
              <dt className="muted">{t(S.diagnoses)}</dt>
              <ul className="chip-list">
                {v.clinical.activeDiagnoses.map((d) => <li key={d.code}><StatusChip kind="info" label={`${d.code} · ${t(d.label)}`} /></li>)}
              </ul>
            </div>
            <dl className="kv-grid">
              <MaskedRow label={t(S.notes)} section={v.clinical.notes} t={t} />
              <MaskedRow label={t(S.prescriptions)} section={v.clinical.prescriptions} t={t} />
              <MaskedRow label={t(S.results)} section={v.clinical.results} t={t} />
            </dl>
          </Card>

          <CaseTasks caseId={caseId} t={t} />
        </div>
      )}
    </AsyncSection>
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

function CaseTasks({ caseId, t }: { caseId: string; t: (l: Localized) => string }) {
  const api = useApi();
  const tasks = useAsync<CoordinationTask[]>(() => api.caseTasks(caseId), [caseId]);
  const cols: Column<CoordinationTask>[] = [
    { key: "title", header: t(S.tasksTitle), cell: (r) => t(r.title), sortable: true, sortValue: (r) => t(r.title) },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
  ];
  return (
    <Card as="section" style={{ padding: "var(--sp3)" }}>
      <h2 className="section-h" style={{ margin: "0 0 var(--sp2)", paddingInline: "var(--sp2)" }}>{t(S.tasksTitle)}</h2>
      <AsyncSection state={tasks} isEmpty={(d) => d.length === 0} emptyLabel={S.tasksEmpty}>
        {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.tasksTitle)} density="compact" />}
      </AsyncSection>
    </Card>
  );
}

/** Escalations raised from the caller's case load — trackable, status-badged. */
export function Escalations() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const state = useAsync<Escalation[]>(() => api.escalations(), []);
  const cols: Column<Escalation>[] = [
    { key: "caseNo", header: t(S.caseNo), cell: (r) => <span className="tnum">{r.caseNo}</span>, sortable: true, sortValue: (r) => r.caseNo },
    { key: "raisedTo", header: t(S.raisedTo), cell: (r) => t(r.raisedToRole), sortable: true, sortValue: (r) => t(r.raisedToRole) },
    { key: "reason", header: t(S.reason), cell: (r) => r.reason, sortable: true, sortValue: (r) => r.reason },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    { key: "raisedAt", header: t(S.raisedAt), cell: (r) => <span className="tnum">{fmt.dateTime(r.raisedAt)}</span>, sortable: true, sortValue: (r) => r.raisedAt },
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
    </>
  );
}

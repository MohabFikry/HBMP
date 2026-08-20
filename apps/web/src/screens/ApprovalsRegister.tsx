import { useState } from "react";
import { Card, DataTable, DataTableView, InlineAlert, SegmentedControl, StatusChip, useTableQuery } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { ApprovalItem, AuthorizationItem, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";

const S = {
  title: { en: "Authorizations", ar: "التفويضات" },
  intro: {
    en: "Every authorization the platform has issued — the requests your team decided, and the record of what "
      + "counters and benches actually handed over. A dispense or a performed examination issues its own "
      + "authorization, separate from the prescription or order it was delivered against.",
    ar: "كل تفويض أصدرته المنصة — الطلبات التي بتّ فيها فريقك، وسجل ما صرفته النقاط وما نفّذته المعامل فعلياً. "
      + "كل صرف أو فحص منفَّذ يصدر تفويضه الخاص، منفصلاً عن الوصفة أو الطلب الذي نُفّذ بناءً عليه.",
  },
  filter: { en: "Show", ar: "عرض" },
  fulfilments: { en: "Delivered", ar: "المنفَّذ" },
  reviews: { en: "Awaiting decision", ar: "بانتظار القرار" },
  all: { en: "Everything", ar: "الكل" },
  empty: { en: "No authorizations to show.", ar: "لا توجد تفويضات لعرضها." },

  authNo: { en: "Authorization", ar: "التفويض" },
  search: { en: "Search", ar: "بحث" },
  searchHint: { en: "Authorization, member token or reference", ar: "التفويض أو رمز العضو أو المرجع" },
  noMatches: { en: "No authorizations match your search.", ar: "لا توجد تفويضات مطابقة لبحثك." },
  patient: { en: "Patient", ar: "المريض" },
  against: { en: "Issued against", ar: "صادر بناءً على" },
  source: { en: "Origin", ar: "المصدر" },
  state: { en: "State", ar: "الحالة" },
  kind: { en: "Kind", ar: "النوع" },
  kindReview: { en: "Decision", ar: "قرار" },
  kindFulfilment: { en: "Delivered", ar: "منفَّذ" },

  // ---- items ----
  itemsTitle: { en: "What was delivered", ar: "ما تم تنفيذه" },
  pick: {
    en: "Select an authorization to see what was delivered against it.",
    ar: "اختر تفويضاً لعرض ما تم تنفيذه بناءً عليه.",
  },
  ordered: { en: "Written", ar: "المكتوب" },
  fulfilled: { en: "Delivered", ar: "المنفَّذ" },
  quantity: { en: "Quantity", ar: "الكمية" },
  when: { en: "When", ar: "التاريخ" },
  substituted: { en: "Substituted", ar: "مستبدل" },
  noItems: {
    en: "Nothing has been delivered against this one. That is expected for a request still awaiting a "
      + "decision — there is nothing to deliver until somebody answers it.",
    ar: "لم يُنفَّذ شيء بناءً على هذا التفويض. وهذا متوقع لطلب ما زال بانتظار القرار — فلا شيء يُنفَّذ قبل "
      + "أن يجيب أحد عليه.",
  },
  substitutionNote: {
    en: "The prescription still says what the prescriber wrote. A substitution is recorded here and nowhere "
      + "else — the clinical record is not edited by a counter.",
    ar: "ما زالت الوصفة تحمل ما كتبه الطبيب. يُسجَّل الاستبدال هنا فقط — فالسجل الإكلينيكي لا تعدّله نقطة الصرف.",
  },
} satisfies Record<string, Localized>;

type Filter = "Fulfilment" | "Review" | "All";

/**
 * Every authorization, for the approval team.
 *
 * <b>Why this is not the worklist.</b> The worklist is a WORK QUEUE: a row on it means "this is waiting for
 * you". This is a REGISTER: a row on it means "this happened". Folding the second into the first would put a
 * few hundred dispenses a day in front of the reviewer who has twelve decisions to make, and the natural
 * response to a queue that is mostly noise is to stop reading it. So the server defaults `kind` to `Review`
 * and this screen asks for the other thing deliberately (ADR-0034 Decision 3).
 *
 * <b>Why the approval team can see fulfilments at all.</b> They are accountable for what the payer pays, and
 * until now they could only see the exceptions — everything authorized by rule rather than by review was
 * invisible to them. The projection carries codes, quantities and the substituting pharmacist's reason, and
 * nothing clinical: this answers "what was delivered against RX-2026-000410", which is a benefit question.
 */
export function ApprovalsRegister() {
  const api = useApi();
  const t = useLoc();
  const [filter, setFilter] = useState<Filter>("Fulfilment");
  const [selected, setSelected] = useState<ApprovalItem | null>(null);

  // The endpoint now answers with a page and its total; the register wants only the rows, because it is a
  // record rather than a work queue and nobody triages it against a cap.
  const list = useAsync<ApprovalItem[]>(async () => (await api.approvalWorklist(filter)).rows, [filter]);

  const cols: Column<ApprovalItem>[] = [
    { key: "authNo", header: t(S.authNo), cell: (r) => <span className="tnum">{r.id}</span>, sortable: true, sortValue: (r) => r.id },
    {
      key: "kind",
      header: t(S.kind),
      // Not decoration. A reviewer opening a row needs to know whether they are looking at a question or a
      // receipt before anything else on it means something.
      cell: (r) => (
        <StatusChip
          kind={r.kind === "Fulfilment" ? "ok" : "info"}
          label={t(r.kind === "Fulfilment" ? S.kindFulfilment : S.kindReview)}
        />
      ),
    },
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.patient.token}</span>, sortable: true, sortValue: (r) => r.patient.token },
    {
      key: "against",
      header: t(S.against),
      // The prescription / order NUMBER, which is the only string on this row a human can look up. An
      // authorization with no reference to what it was issued against is a number with nothing behind it.
      cell: (r) => <span className="tnum">{r.itemReference ?? "—"}</span>,
    },
    { key: "source", header: t(S.source), cell: (r) => <span>{r.source}</span>, sortable: true, sortValue: (r) => r.source },
    { key: "state", header: t(S.state), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
  ];

  /*
    A register of every authorization ever raised, and it had no way to find one. NO filter group here: the
    segmented control above already asks the only categorical question this register has — fulfilment or
    review — and it asks it of the SERVER (`approvalWorklist(filter)` refetches).

    Read outside AsyncSection's render prop: a hook in there would be conditional on the load finishing.
  */
  const query = useTableQuery<ApprovalItem>({
    rows: list.data ?? [],
    columns: cols,
    // The authorization id, the member token, and the reference of the order or prescription it was raised
    // against — a register is searched by whichever of the three the caller happens to be holding.
    searchText: (r) => [r.id, r.patient.token, r.service.code, r.itemReference].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.searchHint),
    pageSize: 25,
    persistKey: "approvals-register",
  });

  return (
    <>
      <PageHeader title={t(S.title)} />
      <p className="muted">{t(S.intro)}</p>

      <SegmentedControl<Filter>
        aria-label={t(S.filter)}
        value={filter}
        onChange={(v) => { setFilter(v); setSelected(null); }}
        segments={[
          { value: "Fulfilment", label: t(S.fulfilments) },
          { value: "Review", label: t(S.reviews) },
          { value: "All", label: t(S.all) },
        ]}
      />

      <Card as="section" style={{ padding: "var(--sp3)", marginBlockStart: "var(--sp4)" }}>
        <AsyncSection state={list} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.id}
              caption={t(S.title)}
              interactive
              selectedKey={selected?.id}
              onSelect={(r) => setSelected(r)}
              emptyLabel={t(S.empty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>

      <Card as="section" style={{ padding: "var(--sp5)", marginBlockStart: "var(--sp4)" }}>
        <h2 className="section-h" style={{ marginBlockStart: 0 }}>{t(S.itemsTitle)}</h2>
        {selected ? <Items authorizationId={selected.id} /> : <p className="muted">{t(S.pick)}</p>}
      </Card>
    </>
  );
}

function Items({ authorizationId }: { authorizationId: string }) {
  const api = useApi();
  const t = useLoc();
  const { date } = useFormat();
  const items = useAsync<AuthorizationItem[]>(() => api.authorizationItems(authorizationId), [authorizationId]);

  const cols: Column<AuthorizationItem>[] = [
    {
      key: "ordered",
      header: t(S.ordered),
      cell: (r) => (
        <span>
          <strong>{r.orderedLabel ?? r.orderedCode}</strong>
          <div className="muted tnum">{r.orderedCode}</div>
        </span>
      ),
    },
    {
      key: "fulfilled",
      header: t(S.fulfilled),
      // Written and delivered are shown SIDE BY SIDE, never one replacing the other. That is the whole
      // reason the authorization is a separate document: a substitution must not erase what the prescriber
      // chose, which is the fact a reviewer most needs.
      cell: (r) => (
        <span>
          <strong>{r.fulfilledLabel ?? r.fulfilledCode}</strong>
          <div className="muted tnum">{r.fulfilledCode}</div>
          {r.substituted && <StatusChip kind="warn" label={t(S.substituted)} />}
        </span>
      ),
    },
    { key: "quantity", header: t(S.quantity), cell: (r) => r.quantity, numeric: true, sortable: true, sortValue: (r) => r.quantity },
    {
      key: "reason",
      header: t(S.substituted),
      // The bounded non-clinical exception (ADR-0034 Decision 3): a substitution reason is logistics written
      // by a pharmacist, and it is the entire substance of what a reviewer is looking at. Routing them
      // through the PHI-audited review view to read one sentence would add an audited access to a patient's
      // record for a question that is not about the patient.
      cell: (r) => <span>{r.substitutionReason ?? "—"}</span>,
    },
    { key: "when", header: t(S.when), cell: (r) => <span className="tnum">{date(r.fulfilledAt)}</span> },
  ];

  return (
    <AsyncSection state={items} isEmpty={(d) => d.length === 0} emptyLabel={S.noItems}>
      {(rows) => (
        <>
          {rows.some((r) => r.substituted) && <InlineAlert tone="info">{t(S.substitutionNote)}</InlineAlert>}
          <DataTable columns={cols} rows={rows} rowKey={(r) => r.itemId} caption={t(S.itemsTitle)} />
        </>
      )}
    </AsyncSection>
  );
}

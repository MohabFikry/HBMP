import { useState } from "react";
import {
  Button,
  Card,
  DataTableView,
  InlineAlert,
  KpiCard,
  SegmentedControl,
  StatusChip,
  TextareaField,
  useTableQuery,
  useToast,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, RetrospectiveItem } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { useFormat } from "../i18n/useFormat";
import { ApiError } from "../api/http";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Break-glass Review", ar: "مراجعة التجاوزات" },
  intro: {
    en: "Every emergency approval, director override and manual authorization is marked for review afterwards. "
      + "That review is what makes break-glass defensible at all — an override is acceptable because somebody "
      + "checks it. Concluding that one was not justified does not reverse it: the care was already delivered "
      + "under it, and unwinding it would refuse a service that has happened to a beneficiary who had no part "
      + "in the decision. It is a finding.",
    ar: "كل اعتماد طارئ أو تجاوز إداري أو تفويض يدوي يُعلَّم للمراجعة لاحقاً. هذه المراجعة هي ما يجعل التجاوز "
      + "قابلاً للتبرير أصلاً — فالتجاوز مقبول لأن أحداً يراجعه. والحكم بعدم تبريره لا يلغيه: فقد قُدّمت الخدمة "
      + "بناءً عليه، وإلغاؤه بأثر رجعي يحرم مستفيداً لا ذنب له في القرار. إنه استنتاج للمتابعة، لا إلغاء.",
  },
  view: { en: "Show", ar: "عرض" },
  open: { en: "Awaiting review", ar: "بانتظار المراجعة" },
  closed: { en: "Reviewed", ar: "تمت المراجعة" },
  emptyOpen: { en: "Nothing is awaiting review.", ar: "لا يوجد ما ينتظر المراجعة." },
  emptyClosed: { en: "No reviews have been recorded yet.", ar: "لم تُسجَّل أي مراجعات بعد." },
  noMatches: { en: "Nothing matches your search.", ar: "لا توجد نتائج مطابقة لبحثك." },
  search: { en: "Search", ar: "بحث" },
  searchHint: { en: "Authorization number or service code", ar: "رقم التفويض أو رمز الخدمة" },

  authNo: { en: "Authorization", ar: "التفويض" },
  origin: { en: "Origin", ar: "المصدر" },
  services: { en: "Services", ar: "الخدمات" },
  decided: { en: "Decided", ar: "تاريخ القرار" },
  age: { en: "Waiting", ar: "مدة الانتظار" },
  days: { en: "d", ar: "ي" },
  outcome: { en: "Outcome", ar: "النتيجة" },
  reviewer: { en: "Reviewed by", ar: "المراجع" },
  action: { en: "Action", ar: "إجراء" },

  openCount: { en: "Awaiting review", ar: "بانتظار المراجعة" },
  oldest: { en: "Oldest open case", ar: "أقدم حالة مفتوحة" },
  none: { en: "—", ar: "—" },

  upheld: { en: "Justified", ar: "مبرَّر" },
  notJustified: { en: "Not justified", ar: "غير مبرَّر" },
  reviewAct: { en: "Review", ar: "مراجعة" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  rationale: { en: "What you concluded, and why", ar: "ما خلصت إليه، ولماذا" },
  needRationale: { en: "A written rationale is required.", ar: "المبرر المكتوب إلزامي." },
  recorded: { en: "Review recorded.", ar: "تم تسجيل المراجعة." },
  selfReview: {
    en: "You took this break-glass decision, so you cannot review it. A second, distinct reviewer is required.",
    ar: "أنت من اتخذ هذا القرار الاستثنائي، فلا يمكنك مراجعته. يلزم مراجع آخر مختلف.",
  },
  failed: { en: "Could not record the review.", ar: "تعذّر تسجيل المراجعة." },
} satisfies Record<string, Localized>;

/** An open case's age drives a chip, not just a number: two days and four months are the same integer to a
 *  reader skimming a column, and only one of them is a problem. */
function ageChip(days: number): { kind: "ok" | "warn" | "bad"; label: Localized } {
  if (days <= 7) return { kind: "ok", label: { en: "Recent", ar: "حديث" } };
  if (days <= 30) return { kind: "warn", label: { en: "Overdue", ar: "متأخر" } };
  return { kind: "bad", label: { en: "Long overdue", ar: "متأخر جداً" } };
}

/**
 * The break-glass retrospective-review queue.
 *
 * <b>Why this screen did not exist, and what that meant.</b> The queue endpoint has been served since phase
 * 7.3. Nothing in the web application ever called it, and — more seriously — nothing anywhere in the codebase
 * could ever close a case: `RetrospectiveReviewed` appeared in exactly two places, its own declaration and the
 * `NOT` predicate that reads it. No endpoint, service or job assigned it. So every emergency approval, every
 * director override and every manual authorization entered a list nobody could see, and none ever left.
 *
 * The flag therefore recorded that a review was <em>owed</em>, never that one happened. The audit trail could
 * not distinguish "reviewed and upheld" from "nobody ever looked" — which is the distinction the control is
 * entirely made of.
 *
 * <b>Segregation of duties, twice.</b> The server refuses a reviewer who is the break-glass actor (per person)
 * and the policy bundle withholds the action from `medical_approval`, who raise manual authorizations (per
 * role). One team acting as both actor and auditor is the arrangement this control replaces, not the one it
 * formalises.
 */
export function ApprovalsRetrospective() {
  const api = useApi();
  const t = useLoc();
  const fmt = useFormat();
  const [view, setView] = useState<"open" | "closed">("open");
  const state = useAsync<RetrospectiveItem[]>(() => api.retrospectiveQueue(view === "closed"), [view]);
  const [active, setActive] = useState<string | null>(null);

  const rows = state.data ?? [];
  const oldest = rows.reduce((max, r) => Math.max(max, r.ageDays), 0);

  const cols: Column<RetrospectiveItem>[] = [
    { key: "authNo", header: t(S.authNo), cell: (r) => <span className="tnum">{r.authNo}</span>, sortable: true, sortValue: (r) => r.authNo },
    { key: "origin", header: t(S.origin), cell: (r) => r.source, sortable: true, sortValue: (r) => r.source },
    {
      key: "services",
      header: t(S.services),
      cell: (r) => <span className="tnum">{r.serviceCodes.join(", ") || "—"}</span>,
    },
    {
      key: "decided",
      header: t(S.decided),
      cell: (r) => <span className="tnum">{r.decidedAt ? fmt.date(r.decidedAt) : "—"}</span>,
      sortable: true,
      sortValue: (r) => r.decidedAt ?? "",
    },
    ...(view === "open"
      ? [
          {
            key: "age",
            header: t(S.age),
            // Four cues, same as every other status in the product: hue, icon-bearing chip, shape and the
            // number itself in words a reader does not have to date-subtract.
            cell: (r: RetrospectiveItem) => (
              <span>
                <StatusChip kind={ageChip(r.ageDays).kind} label={t(ageChip(r.ageDays).label)} />{" "}
                <span className="tnum">{fmt.number(r.ageDays)}{t(S.days)}</span>
              </span>
            ),
            sortable: true,
            sortValue: (r: RetrospectiveItem) => -r.ageDays,
          } satisfies Column<RetrospectiveItem>,
        ]
      : [
          {
            key: "outcome",
            header: t(S.outcome),
            cell: (r: RetrospectiveItem) =>
              r.outcome === "Upheld"
                ? <StatusChip kind="ok" label={t(S.upheld)} />
                : <StatusChip kind="bad" label={t(S.notJustified)} />,
            sortable: true,
            sortValue: (r: RetrospectiveItem) => r.outcome ?? "",
          } satisfies Column<RetrospectiveItem>,
          {
            key: "reviewer",
            header: t(S.reviewer),
            cell: (r: RetrospectiveItem) => <span className="tnum">{r.reviewedBy ?? "—"}</span>,
          } satisfies Column<RetrospectiveItem>,
        ]),
    ...(view === "open"
      ? [
          {
            key: "act",
            header: t(S.action),
            cell: (r: RetrospectiveItem) => (
              <Button size="sm" variant="secondary" onClick={() => setActive(r.authorizationId)}>
                {t(S.reviewAct)}
              </Button>
            ),
          } satisfies Column<RetrospectiveItem>,
        ]
      : []),
  ];

  /* Read outside AsyncSection's render prop: a hook called in there would be conditional on the load. */
  const query = useTableQuery<RetrospectiveItem>({
    rows,
    columns: cols,
    searchText: (r) => [r.authNo, ...r.serviceCodes, r.source].join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.searchHint),
    pageSize: 25,
    persistKey: `approvals-retrospective-${view}`,
  });

  const selected = rows.find((r) => r.authorizationId === active) ?? null;

  return (
    <>
      <PageHeader title={t(S.title)} />
      <InlineAlert tone="info">{t(S.intro)}</InlineAlert>

      {view === "open" && (
        <div className="kpi-row" style={{ marginBlock: "var(--sp4)" }}>
          <KpiCard label={t(S.openCount)} value={fmt.number(rows.length)} />
          {/* The number that matters. A count alone looks identical whether the queue turned over yesterday
              or has been stuck since March, and only one of those is a finding. */}
          <KpiCard label={t(S.oldest)} value={rows.length ? `${fmt.number(oldest)}${t(S.days)}` : t(S.none)} />
        </div>
      )}

      <div style={{ marginBlock: "var(--sp3)" }}>
        <SegmentedControl<"open" | "closed">
          aria-label={t(S.view)}
          value={view}
          onChange={(v) => { setView(v); setActive(null); }}
          segments={[
            { value: "open", label: t(S.open) },
            { value: "closed", label: t(S.closed) },
          ]}
        />
      </div>

      <div className="split split-wide">
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <AsyncSection
            state={state}
            isEmpty={(d) => d.length === 0}
            emptyLabel={view === "open" ? S.emptyOpen : S.emptyClosed}
          >
            {() => (
              <DataTableView
                query={query}
                columns={cols}
                rowKey={(r) => r.authorizationId}
                caption={t(S.title)}
                interactive={view === "open"}
                selectedKey={active ?? undefined}
                onSelect={view === "open" ? (r) => setActive(r.authorizationId) : undefined}
                emptyLabel={t(view === "open" ? S.emptyOpen : S.emptyClosed)}
                noMatchesLabel={t(S.noMatches)}
              />
            )}
          </AsyncSection>
        </Card>
        <div>
          {selected && view === "open" ? (
            <ReviewPanel
              key={selected.authorizationId}
              item={selected}
              t={t}
              onDone={() => { setActive(null); state.reload(); }}
            />
          ) : null}
        </div>
      </div>
    </>
  );
}

function ReviewPanel({
  item,
  t,
  onDone,
}: {
  item: RetrospectiveItem;
  t: (l: Localized) => string;
  onDone: () => void;
}) {
  const api = useApi();
  const { toast } = useToast();
  const [rationale, setRationale] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  async function submit(outcome: "Upheld" | "NotJustified") {
    if (rationale.trim().length === 0) {
      setError(S.needRationale);
      return;
    }
    setError(null);
    setBusy(true);
    try {
      await api.completeRetrospectiveReview({
        authorizationId: item.authorizationId,
        outcome,
        rationale: rationale.trim(),
      });
      toast(t(S.recorded), "ok");
      onDone();
    } catch (e) {
      // The self-review refusal is the control WORKING, and it gets its own sentence. A 403 that reads only
      // "forbidden" on a segregation-of-duties check teaches the reviewer that the system is broken.
      const sod = e instanceof ApiError && e.status === 403;
      setError(sod ? S.selfReview : S.failed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      <div>
        <h2 className="section-h" style={{ marginBlockStart: 0 }}>{item.authNo}</h2>
        <dl className="rxv-meta">
          <dt>{t(S.origin)}</dt>
          <dd>{item.source}</dd>
          <dt>{t(S.services)}</dt>
          <dd className="tnum">{item.serviceCodes.join(", ") || "—"}</dd>
          <dt>{t(S.age)}</dt>
          <dd className="tnum">{item.ageDays}{t(S.days)}</dd>
        </dl>
      </div>

      <TextareaField
        label={t(S.rationale)}
        value={rationale}
        onChange={(e) => setRationale(e.currentTarget.value)}
        rows={4}
      />
      {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}

      <div className="rx-actions">
        <Button variant="danger" loading={busy} onClick={() => void submit("NotJustified")}>
          {t(S.notJustified)}
        </Button>
        <Button variant="primary" loading={busy} onClick={() => void submit("Upheld")}>
          {t(S.upheld)}
        </Button>
      </div>
    </Card>
  );
}

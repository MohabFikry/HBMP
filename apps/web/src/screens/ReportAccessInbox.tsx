import { useMemo, useState } from "react";
import { useFormat } from "../i18n/useFormat";
import {
  Button, Card, DataTableView, Icon, StatusChip, useTableQuery, useToast,
  type Column, type TableFilterSpec,
} from "@mersal/design-system";
import type { Localized, ReportAccessRequestRow } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";

/**
 * Phase 18.C2 (audit R2 W4) — the approver inbox for sensitive-result release requests (design 37 §6).
 *
 * The whole request/grant workflow shipped in 14.7 with no way to SEE a request. A clinician could raise one,
 * and the endpoint that decides it takes an id — an id nothing displayed. So the sensitive-result gate was
 * permanent-deny in practice: every request sat in a table until it expired, and the clinician on the other
 * end simply never got an answer. That is the worst failure mode for a break-glass-adjacent control, because
 * the pressure it creates is to route around it.
 *
 * The screen is deliberately CLINICAL-FREE. It shows who asked, for which order line, under what purpose and
 * why — never the result. An approver is deciding whether the REQUESTER may see it; showing it to them here
 * would disclose the exact thing being gated to everyone who can open the inbox.
 */
const S = {
  title: { en: "Result Access Requests", ar: "طلبات الوصول إلى النتائج" },
  empty: { en: "No requests awaiting a decision.", ar: "لا توجد طلبات بانتظار القرار." },
  noMatches: {
    en: "No requests match. Change the search or clear the filters.",
    ar: "لا توجد طلبات مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },
  search: { en: "Search", ar: "بحث" },
  searchHint: { en: "Requester, member, purpose or reason", ar: "مقدّم الطلب أو المستفيد أو الغرض أو المبرر" },
  requester: { en: "Requested by", ar: "مقدّم الطلب" },
  member: { en: "Member", ar: "المستفيد" },
  purpose: { en: "Purpose", ar: "الغرض" },
  justification: { en: "Justification", ar: "المبرر" },
  ttl: { en: "Requested for", ar: "المدة المطلوبة" },
  status: { en: "Status", ar: "الحالة" },
  raised: { en: "Raised", ar: "تاريخ الطلب" },
  actions: { en: "Decision", ar: "القرار" },
  approve: { en: "Approve", ar: "موافقة" },
  deny: { en: "Deny", ar: "رفض" },
  askInfo: { en: "Ask for more", ar: "طلب إيضاح" },
  reasonLabel: { en: "Reason for this decision (recorded in the audit trail)", ar: "سبب القرار (يُسجَّل في سجل التدقيق)" },
  reasonRequired: { en: "A reason is required — it is recorded against the beneficiary's record.", ar: "المبرر مطلوب — يُسجَّل في ملف المستفيد." },
  hours: { en: "hours", ar: "ساعة" },
  cappedNote: {
    en: "The granted window may be shorter than requested — policy caps it by the result's sensitivity.",
    ar: "قد تكون المدة الممنوحة أقصر من المطلوبة — تحددها حساسية النتيجة.",
  },
  failed: { en: "The decision could not be recorded. Nothing was changed — please try again.", ar: "تعذّر تسجيل القرار. لم يتم تغيير أي شيء — يرجى المحاولة مرة أخرى." },
  takeUnderReview: { en: "Take under review", ar: "بدء المراجعة" },
  respond: { en: "Respond", ar: "الرد" },
  send: { en: "Send", ar: "إرسال" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  supplementLabel: {
    en: "More information for the reviewer",
    ar: "معلومات إضافية للمراجع",
  },
  supplementHint: {
    en: "Added to your original justification, which stays as you wrote it. The request returns to review.",
    ar: "تُضاف إلى مبررك الأصلي الذي يبقى كما كتبته. يعود الطلب إلى المراجعة.",
  },
  supplementRequired: {
    en: "Write the answer before sending — an empty reply leaves the request where it is.",
    ar: "اكتب الرد قبل الإرسال — الرد الفارغ يترك الطلب كما هو.",
  },
  yourOriginal: { en: "Your original justification", ar: "مبررك الأصلي" },
  pickedUp: { en: "Now under your review — the clock starts here.", ar: "أصبح قيد مراجعتك — يبدأ العد من الآن." },
  supplied: { en: "Sent. The request is back with the reviewer.", ar: "تم الإرسال. عاد الطلب إلى المراجع." },
} satisfies Record<string, Localized>;

type Decision = "approve" | "deny" | "requestinfo";

export function ReportAccessInbox() {
  const fmt = useFormat();   // 18.D2 (U7) — Africa/Cairo + the app locale
  const api = useApi();
  const t = useLoc();
  const [reloadKey, setReloadKey] = useState(0);
  const state = useAsync<ReportAccessRequestRow[]>(() => api.reportAccessInbox(), [reloadKey]);
  const { toast } = useToast();
  const [reasons, setReasons] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const [supplements, setSupplements] = useState<Record<string, string>>({});
  const [answering, setAnswering] = useState<string | null>(null);

  /**
   * Pick-up: Requested → UnderReview (18.A4's routing step).
   *
   * <p>An ACT, never a side effect of rendering. It records the decider's identity and starts the SLA clock,
   * so firing it on mount would attribute the review to whoever happened to open the queue — the opposite of
   * what it was added for. Deciding without it stays allowed, because the server allows it: the decision
   * path records an implicit pick-up, and a screen inventing a stricter rule would block work the platform
   * permits.</p>
   */
  async function takeUnderReview(row: ReportAccessRequestRow) {
    setBusy(row.requestId);
    setError(null);
    try {
      await api.takeReportAccessUnderReview(row.requestId);
      toast(t(S.pickedUp));
      setReloadKey((k) => k + 1);
    } catch {
      setError(t(S.failed));
    } finally {
      setBusy(null);
    }
  }

  /** Answer a reviewer's question: InfoRequested → UnderReview. The only exit from that state. */
  async function supply(row: ReportAccessRequestRow) {
    const supplement = (supplements[row.requestId] ?? "").trim();
    if (!supplement) {
      setError(t(S.supplementRequired));
      return;
    }
    setBusy(row.requestId);
    setError(null);
    try {
      await api.supplyReportAccessInfo(row.requestId, supplement);
      toast(t(S.supplied));
      setAnswering(null);
      setSupplements((m) => ({ ...m, [row.requestId]: "" }));
      setReloadKey((k) => k + 1);
    } catch {
      setError(t(S.failed));
    } finally {
      setBusy(null);
    }
  }

  async function decide(row: ReportAccessRequestRow, decision: Decision) {
    const reason = (reasons[row.requestId] ?? "").trim();
    // A decision on someone's clinical record is not a bare button press. The reason is required BEFORE the
    // call, not validated by the server afterwards, so the approver is never told "invalid" about something
    // they were allowed to submit.
    if (!reason) {
      setError(t(S.reasonRequired));
      return;
    }
    setBusy(row.requestId);
    setError(null);
    try {
      await api.decideReportAccess(row.requestId, decision, reason, row.requestedTtlHours);
      setReloadKey((k) => k + 1);
    } catch {
      setError(t(S.failed));
    } finally {
      setBusy(null);
    }
  }

  const cols: Column<ReportAccessRequestRow>[] = [
    { key: "requester", header: t(S.requester), cell: (r) => <span>{r.requestedBy}{r.requestedForRole ? ` · ${r.requestedForRole}` : ""}</span>,
      sortable: true, sortValue: (r) => r.requestedBy },
    { key: "member", header: t(S.member), cell: (r) => <span className="tnum">{r.beneficiaryToken}</span>,
      sortable: true, sortValue: (r) => r.beneficiaryToken },
    { key: "purpose", header: t(S.purpose), cell: (r) => <StatusChip kind="info" label={r.purposeCode} />,
      sortable: true, sortValue: (r) => r.purposeCode },
    // Free prose, and the one column nobody orders a queue by.
    { key: "justification", header: t(S.justification), cell: (r) => <span>{r.justification}</span> },
    // A DURATION — a quantity compared down the column, so it right-aligns with tabular figures. The unit
    // rides in the cell because "6" alone under "Requested for" is not an answer.
    { key: "ttl", header: t(S.ttl), cell: (r) => (r.requestedTtlHours ? `${r.requestedTtlHours} ${t(S.hours)}` : "—"),
      numeric: true, sortable: true, sortValue: (r) => r.requestedTtlHours ?? 0 },
    { key: "status", header: t(S.status), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />,
      sortable: true, sortValue: (r) => r.status.label.en },
    // Sorts on the ISO instant, not the rendered stamp — `fmt.dateTime` renders Arabic-Indic digits under the
    // Arabic locale, and sorting those orders the queue by glyph.
    { key: "raised", header: t(S.raised), cell: (r) => <span className="tnum">{fmt.dateTime(r.createdAt)}</span>,
      sortable: true, sortValue: (r) => r.createdAt },
    {
      key: "actions",
      header: t(S.actions),
      cell: (r) => (
        <div style={{ display: "grid", gap: "var(--sp2)", minWidth: "18rem" }}>
          {/* 32.4 — WHAT IS OFFERED DEPENDS ON WHAT THE SERVER SAID THIS CALLER MAY DO. `canDecide` is an
              authorization answer computed in orders-service; deriving it here by comparing identity
              strings would put the rule in a browser. The requester's own row therefore carries Respond
              and nothing else: asking to see a result does not make you the person who may release it. */}
          {r.canDecide ? (
            <>
              <label htmlFor={`reason-${r.requestId}`} style={{ fontSize: "0.85rem" }}>{t(S.reasonLabel)}</label>
              <input
                id={`reason-${r.requestId}`}
                value={reasons[r.requestId] ?? ""}
                onChange={(e) => setReasons((m) => ({ ...m, [r.requestId]: e.target.value }))}
                style={{ minHeight: 44 }}
              />
              <div style={{ display: "flex", gap: "var(--sp2)", flexWrap: "wrap" }}>
                {/* Only from Requested: a request already under review has been picked up, and offering to
                    pick it up again would reset an attribution somebody else owns. */}
                {r.statusCode === "Requested" && (
                  <Button
                    size="sm"
                    variant="secondary"
                    leadingIcon={<Icon name="clock" aria-hidden="true" />}
                    onClick={() => void takeUnderReview(r)}
                    disabled={busy === r.requestId}
                  >
                    {t(S.takeUnderReview)}
                  </Button>
                )}
                <Button size="sm" onClick={() => void decide(r, "approve")} disabled={busy === r.requestId}>{t(S.approve)}</Button>
                <Button size="sm" variant="secondary" onClick={() => void decide(r, "deny")} disabled={busy === r.requestId}>{t(S.deny)}</Button>
                <Button size="sm" variant="ghost" onClick={() => void decide(r, "requestinfo")} disabled={busy === r.requestId}>{t(S.askInfo)}</Button>
              </div>
            </>
          ) : null}

          {r.isRequester && r.statusCode === "InfoRequested" ? (
            answering === r.requestId ? (
              <section
                aria-label={t(S.respond)}
                style={{ display: "grid", gap: "var(--sp2)" }}
              >
                <p style={{ margin: 0, fontSize: "0.8125rem", color: "var(--text-3)" }}>
                  <strong>{t(S.yourOriginal)}:</strong> {r.justification}
                </p>
                <label htmlFor={`supp-${r.requestId}`} style={{ fontSize: "0.85rem" }}>
                  {t(S.supplementLabel)}
                </label>
                <textarea
                  id={`supp-${r.requestId}`}
                  value={supplements[r.requestId] ?? ""}
                  onChange={(e) => setSupplements((m) => ({ ...m, [r.requestId]: e.target.value }))}
                  rows={3}
                />
                <p style={{ margin: 0, fontSize: "0.8125rem", color: "var(--text-3)" }}>{t(S.supplementHint)}</p>
                <div style={{ display: "flex", gap: "var(--sp2)" }}>
                  <Button size="sm" variant="ghost" onClick={() => setAnswering(null)}>{t(S.cancel)}</Button>
                  {/* Bare on purpose, and the icon policy says so: Send is a one-off verb, not a member of
                      a recurring action class with a glyph of its own. */}
                  <Button size="sm" onClick={() => void supply(r)} disabled={busy === r.requestId}>
                    {t(S.send)}
                  </Button>
                </div>
              </section>
            ) : (
              <Button
                size="sm"
                leadingIcon={<Icon name="check2" aria-hidden="true" />}
                onClick={() => setAnswering(r.requestId)}
              >
                {t(S.respond)}
              </Button>
            )
          ) : null}
        </div>
      ),
    },
  ];

  /**
   * Both groups are derived from the ROWS rather than declared.
   *
   * `purposeCode` is an open vocabulary the server owns — a hardcoded option list would show chips for
   * purposes nobody has requested and silently hide the one that arrived last week. Status is closed in
   * principle but has exactly two live values here (the inbox is pending-only), and deriving it keeps the two
   * groups honest in the same way. Both match on the stable half of the pair: the raw code, and the English
   * label — matching the localized one would break the filter the moment the portal is switched to Arabic.
   */
  // Memoized because the filter groups are DERIVED from it: `state.data ?? []` is a fresh empty array on every
  // render while the request is in flight, which would rebuild the options — and with them the chip counts —
  // on each one.
  const rows = useMemo(() => state.data ?? [], [state.data]);
  const filters: TableFilterSpec<ReportAccessRequestRow>[] = useMemo(() => {
    const distinct = (pick: (r: ReportAccessRequestRow) => string): string[] =>
      [...new Set(rows.map(pick).filter(Boolean))].sort((a, b) => a.localeCompare(b));
    const purposes = distinct((r) => r.purposeCode);
    const statuses = distinct((r) => r.status.label.en);
    const groups: TableFilterSpec<ReportAccessRequestRow>[] = [];
    // A group with one option filters nothing — every row already matches it — so it is chrome that costs a
    // click to discover. It appears once the queue actually holds two kinds.
    if (purposes.length > 1) {
      groups.push({
        key: "purpose",
        label: t(S.purpose),
        options: purposes.map((p) => ({ value: p, label: p })),
        match: (r, value) => r.purposeCode === value,
      });
    }
    if (statuses.length > 1) {
      groups.push({
        key: "status",
        label: t(S.status),
        options: statuses.map((s) => ({
          value: s,
          // The row already carries the localized label beside the English one; re-deriving it from the first
          // matching row is what keeps an Arabic portal from showing an English chip.
          label: t(rows.find((r) => r.status.label.en === s)!.status.label),
        })),
        match: (r, value) => r.status.label.en === value,
      });
    }
    return groups;
  }, [rows, t]);

  const query = useTableQuery<ReportAccessRequestRow>({
    rows,
    columns: cols,
    // Everything an approver would type: who asked, the token off the request, the purpose code, and the
    // words of the justification itself — which is the field they are actually weighing.
    searchText: (r) => [
      r.requestedBy, r.requestedForRole, r.beneficiaryToken, r.purposeCode, r.justification,
      r.status.label.en, r.status.label.ar,
    ].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.searchHint),
    filters,
    // Smaller than the usual 10: every row carries a reason field and three decision buttons, so ten of them
    // is a page nobody can see the end of.
    pageSize: 8,
    // Oldest first. This is a queue with an expiry on it — a request that sits undecided until it lapses is
    // the failure mode this screen exists to prevent, so the one closest to lapsing is the one on top.
    initialSortKey: "raised",
    initialSortDir: "ascending",
    persistKey: "doctor-result-access",
  });

  return (
    <>
      <PageHeader title={t(S.title)} />
      <Card as="section" style={{ padding: "var(--sp3)" }}>
        <p className="muted" style={{ marginTop: 0 }}>{t(S.cappedNote)}</p>
        {/* aria-live so a screen-reader user hears the outcome; the table below re-renders silently. */}
        <p role="alert" aria-live="polite" style={{ color: "var(--color-danger-fg, #b91c1c)" }}>
          {error ?? ""}
        </p>
        <AsyncSection<ReportAccessRequestRow[]> state={state} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
          {() => (
            <DataTableView
              query={query}
              columns={cols}
              rowKey={(r) => r.requestId}
              caption={t(S.title)}
              emptyLabel={t(S.empty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

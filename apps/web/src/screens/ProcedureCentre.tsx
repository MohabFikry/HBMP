import { useCallback, useState } from "react";
import { Button, Card, DataTable, DataTableView, Icon, InlineAlert, InputField, StatusChip, useTableQuery } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { Localized, ProcedureQueueItem } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { ApiError } from "../api/http";

/**
 * 29.2b — the EXTERNAL delivering provider's portal (design 45 §2b).
 *
 * Two modes on one screen, because they are one job: see the work routed to us, then verify the person in
 * front of us and record what we delivered.
 *
 * <p><b>No ownership filtering happens here.</b> The server scopes every row by `assigned_provider_id`. A
 * client-side filter would look like defence and be the opposite — it would make a server returning other
 * centres' rows render correctly, which is how audit R3's network-wide pharmacy queue survived unnoticed.</p>
 */
const S = {
  queueSearch: { en: "Search", ar: "بحث" },
  queueSearchHint: { en: "Order number or service code", ar: "رقم الطلب أو رمز الخدمة" },
  noMatches: { en: "No orders match your search.", ar: "لا توجد طلبات مطابقة لبحثك." },
  queueTitle: { en: "Our queue", ar: "قائمة أعمالنا" },
  counterTitle: { en: "Verify & deliver", ar: "التحقق والتنفيذ" },
  queueEmpty: { en: "No work is currently routed to your centre.", ar: "لا توجد أعمال موجّهة إلى مركزكم حالياً." },

  // ---- counter ----
  counterHint: {
    en: "Enter TWO of the person's identifiers to confirm who is in front of you.",
    ar: "أدخل اثنين من معرّفات الشخص للتأكد ممن أمامك.",
  },
  fCard: { en: "Card number", ar: "رقم البطاقة" },
  fMember: { en: "Member number", ar: "رقم العضوية" },
  fPassport: { en: "Passport", ar: "جواز السفر" },
  search: { en: "Verify", ar: "تحقق" },
  clear: { en: "Clear", ar: "مسح" },
  startHere: {
    en: "Enter two identifiers to begin.",
    ar: "أدخل معرّفين للبدء.",
  },
  twoIdentifiers: {
    en: "A card number on its own is not enough — it is printed on something that gets shared and "
      + "photographed. Add the member number or passport.",
    ar: "رقم البطاقة وحده لا يكفي — فهو مطبوع على ما يُتداول ويُصوَّر. أضف رقم العضوية أو جواز السفر.",
  },
  directoryDown: {
    en: "The directory could not be reached, so these identifiers could not be checked. This is NOT a "
      + "report that the person has no sessions booked.",
    ar: "تعذّر الوصول إلى الدليل، ولم يمكن التحقق من هذه المعرّفات. هذا ليس تأكيداً بعدم وجود جلسات.",
  },
  noMatch: { en: "No sessions are routed to your centre for that person.", ar: "لا توجد جلسات موجّهة إلى مركزكم لهذا الشخص." },

  // ---- table ----
  cOrder: { en: "Order", ar: "الطلب" },
  cService: { en: "Service", ar: "الخدمة" },
  cType: { en: "Type", ar: "النوع" },
  cProgress: { en: "Progress", ar: "التقدّم" },
  cContext: { en: "Referral reason", ar: "سبب الإحالة" },
  cAction: { en: "", ar: "" },

  notDisclosed: { en: "Not disclosed", ar: "لم يُفصح عنه" },
  notDisclosedHint: {
    en: "The ordering doctor did not share a reason. This does not mean there is none.",
    ar: "لم يشارك الطبيب سبباً. هذا لا يعني عدم وجود سبب.",
  },

  recordSession: { en: "Record session", ar: "تسجيل جلسة" },
  recording: { en: "Recording…", ar: "جارٍ التسجيل…" },
  allDelivered: { en: "All delivered", ar: "اكتملت الجلسات" },
  expired: { en: "Expired", ar: "منتهي" },
  expiredHint: {
    en: "This order's validity has passed; undelivered sessions are forfeited. The referring doctor can "
      + "request a revalidation.",
    ar: "انتهت صلاحية هذا الطلب؛ الجلسات غير المنفذة تسقط. يمكن للطبيب المُحيل طلب إعادة التفعيل.",
  },
  noneRemaining: {
    en: "Every authorised session has already been delivered. A further course needs a new order.",
    ar: "تم تنفيذ جميع الجلسات المصرّح بها. أي جلسات إضافية تحتاج طلباً جديداً.",
  },
  couldNotRecord: { en: "The session could not be recorded.", ar: "تعذّر تسجيل الجلسة." },
} satisfies Record<string, Localized>;

export default function ProcedureCentre({ mode = "queue" }: { mode?: "queue" | "counter" }) {
  const api = useApi();
  const t = useLoc();

  const [card, setCard] = useState("");
  const [member, setMember] = useState("");
  const [passport, setPassport] = useState("");
  const [submitted, setSubmitted] = useState(false);
  const [counterRows, setCounterRows] = useState<ProcedureQueueItem[] | null>(null);
  const [counterError, setCounterError] = useState<Localized | null>(null);

  const [busy, setBusy] = useState<string | null>(null);
  const [actionError, setActionError] = useState<Localized | null>(null);
  const [nonce, setNonce] = useState(0);

  const queue = useAsync(
    useCallback(() => (mode === "queue" ? api.procedureQueue() : Promise.resolve([])), [api, mode, nonce]),
    [api, mode, nonce],
  );

  async function verify() {
    setSubmitted(true);
    setCounterError(null);
    setCounterRows(null);
    try {
      setCounterRows(await api.procedureCounterSearch({ cardNumber: card, memberNo: member, passport }));
    } catch (e) {
      // THREE distinct outcomes, never collapsed: too-few-identifiers is a refusal to answer, unavailable is
      // "we could not ask", and an empty list is a real answer. Only the last means the person has nothing.
      const status = e instanceof ApiError ? e.status : 0;
      setCounterError(status === 422 ? S.twoIdentifiers : S.directoryDown);
    }
  }

  function clear() {
    setCard(""); setMember(""); setPassport("");
    setSubmitted(false); setCounterRows(null); setCounterError(null);
  }

  async function recordSession(row: ProcedureQueueItem) {
    setBusy(row.orderId);
    setActionError(null);
    try {
      // ONE key per tap, generated at the moment of the click. This is what makes a double-tap safe: the
      // second tap of the same click carries the same key and the server answers with the same progress.
      await api.recordProcedureSession(row.orderId, row.orderId, crypto.randomUUID(), { attended: true });
      setNonce((n) => n + 1);
      if (mode === "counter") await verify();
    } catch (e) {
      const status = e instanceof ApiError ? e.status : 0;
      setActionError(status === 422 ? S.noneRemaining : S.couldNotRecord);
    } finally {
      setBusy(null);
    }
  }

  const columns: Column<ProcedureQueueItem>[] = [
    { key: "orderNo", header: t(S.cOrder), cell: (r: ProcedureQueueItem) => r.orderNo, sortable: true, sortValue: (r) => r.orderNo },
    { key: "code", header: t(S.cService), cell: (r: ProcedureQueueItem) => `${r.code} — ${r.description ?? ""}` },
    { key: "type", header: t(S.cType), cell: (r: ProcedureQueueItem) => r.procedureTypeCode ?? "—", sortable: true, sortValue: (r) => r.procedureTypeCode },
    {
      key: "progress",
      header: t(S.cProgress),
      // The SAME sentence the ordering doctor's worklist shows. A course that reads differently at each end is
      // a course somebody delivers twice.
      cell: (r: ProcedureQueueItem) => r.progressLabel, sortable: true, sortValue: (r) => r.progressLabel },
    {
      key: "context",
      header: t(S.cContext),
      cell: (r: ProcedureQueueItem) =>
        r.sharedClinicalContext ?? (
          // NOT "none". The doctor chose to share nothing, which is different from there being nothing to
          // share — and a physiotherapist who reads it the other way treats someone as uncomplicated who
          // is not.
          <span title={t(S.notDisclosedHint)} className="muted">{t(S.notDisclosed)}</span>
        ), sortable: true, sortValue: (r) => r.sharedClinicalContext },
    {
      key: "action",
      header: t(S.cAction),
      cell: (r: ProcedureQueueItem) =>
        r.expired ? (
          <StatusChip kind="bad" label={t(S.expired)} />
        ) : r.sessionsRemaining <= 0 ? (
          <StatusChip kind="neu" label={t(S.allDelivered)} />
        ) : (
          <Button size="sm"
            variant="primary"
            disabled={busy === r.orderId}
            onClick={() => void recordSession(r)}
          >
            {busy === r.orderId ? t(S.recording) : t(S.recordSession)}
          </Button>
        ),
    },
  ];

  /** The centre's own delivery queue — it grows through the day and had no way to find one order in it. */
  const query = useTableQuery({
    rows: queue.data ?? [],
    columns,
    searchText: (r) => [r.orderNo, r.code, r.description, r.procedureTypeCode].filter(Boolean).join(" "),
    searchLabel: t(S.queueSearch),
    searchPlaceholder: t(S.queueSearchHint),
    pageSize: 25,
    persistKey: "procedure-queue",
  });

  if (mode === "counter") {
    return (
      <>
        <PageHeader title={t(S.counterTitle)} />
        <Card>
          <p>{t(S.counterHint)}</p>
          <InputField label={t(S.fCard)} value={card} onChange={(e) => setCard(e.target.value)} />
          <InputField label={t(S.fMember)} value={member} onChange={(e) => setMember(e.target.value)} />
          <InputField label={t(S.fPassport)} value={passport} onChange={(e) => setPassport(e.target.value)} />
          <Button leadingIcon={<Icon name="search" />} variant="primary" onClick={() => void verify()}>{t(S.search)}</Button>
          <Button variant="ghost" onClick={clear}>{t(S.clear)}</Button>
        </Card>

        {counterError && <InlineAlert tone="warn">{t(counterError)}</InlineAlert>}
        {actionError && <InlineAlert tone="warn">{t(actionError)}</InlineAlert>}

        {!submitted && <InlineAlert tone="info">{t(S.startHere)}</InlineAlert>}
        {submitted && !counterError && counterRows?.length === 0 && (
          <InlineAlert tone="info">{t(S.noMatch)}</InlineAlert>
        )}
        {counterRows && counterRows.length > 0 && (
          <Card>
            <DataTable columns={columns} rows={counterRows} rowKey={(r) => r.orderId} caption={t(S.counterTitle)} />
          </Card>
        )}
      </>
    );
  }


  return (
    <>
      <PageHeader title={t(S.queueTitle)} />
      {actionError && <InlineAlert tone="warn">{t(actionError)}</InlineAlert>}
      <Card>
        <AsyncSection state={queue} isEmpty={(rows) => rows.length === 0} emptyLabel={S.queueEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={columns}
              rowKey={(r) => r.orderId}
              caption={t(S.queueTitle)}
              emptyLabel={t(S.queueEmpty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>
    </>
  );
}

import { useCallback, useState } from "react";
import { Button, Card, DataTable, DataTableView, Icon, InlineAlert, InputField, Modal, StatusChip, useTableQuery } from "@mersal/design-system";
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

  // ---- 32.6 — who is in front of you ----
  verified: { en: "Verified", ar: "تم التحقق" },
  nameWithheld: { en: "Name not disclosed to your centre", ar: "لم يُفصح عن الاسم لمركزكم" },
  nameWithheldHint: {
    en: "Two identifiers matched one person, so the sessions below are theirs. The name itself was withheld "
      + "from your centre — this is not a record without a name.",
    ar: "تطابق معرّفان مع شخص واحد، فالجلسات أدناه تخصّه. أما الاسم فقد حُجب عن مركزكم — وهذا لا يعني أن "
      + "السجل بلا اسم.",
  },

  // ---- 32.6 — closing the referral loop (design 45 §7) ----
  reportBack: { en: "Report back", ar: "إرسال التقرير" },
  reported: { en: "Reported", ar: "تم الإبلاغ" },
  reportTitle: { en: "Report back to the ordering doctor", ar: "إرسال تقرير إلى الطبيب الطالب" },
  reportWhy: {
    en: "The doctor who sent this person here is still waiting to hear what happened. Until you report back, "
      + "the referral stays open on their worklist — an open loop is the classic way an outpatient referral "
      + "is lost.",
    ar: "لا يزال الطبيب الذي أحال هذا الشخص ينتظر معرفة ما جرى. وحتى ترسلوا التقرير تبقى الإحالة مفتوحة في "
      + "قائمة أعماله — والحلقة المفتوحة هي الطريقة المعتادة لضياع الإحالات الخارجية.",
  },
  reportFindings: { en: "What was found or done", ar: "ما تم إيجاده أو تنفيذه" },
  reportHint: {
    en: "The ordering doctor reads this and nothing else from your centre.",
    ar: "لن يقرأ الطبيب الطالب سوى هذا من مركزكم.",
  },
  reportPlaceholder: {
    en: "e.g. six sessions completed; range of movement improved from 80° to 115°; discharged to home exercise",
    ar: "مثال: اكتملت ست جلسات؛ تحسّن مدى الحركة من 80° إلى 115°؛ أُحيل إلى تمارين منزلية",
  },
  reportTooShort: {
    en: "Write at least a short sentence. An empty report closes the loop without saying anything, which is "
      + "worse than leaving it open.",
    ar: "اكتب جملة قصيرة على الأقل. التقرير الفارغ يُغلق الحلقة دون أن يقول شيئاً، وهذا أسوأ من تركها مفتوحة.",
  },
  reportSent: { en: "Reported. The referral loop is closed.", ar: "تم الإرسال. أُغلقت حلقة الإحالة." },
  reportFailed: { en: "The report could not be sent.", ar: "تعذّر إرسال التقرير." },
  send: { en: "Send report", ar: "إرسال التقرير" },
  cancel: { en: "Cancel", ar: "إلغاء" },
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
  // 32.6 — the order whose loop we are closing, or null when the dialog is shut.
  const [reporting, setReporting] = useState<ProcedureQueueItem | null>(null);
  const [reportOk, setReportOk] = useState<Localized | null>(null);

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
    setBusy(row.orderLineId);
    setActionError(null);
    try {
      // ONE key per tap, generated at the moment of the click. This is what makes a double-tap safe: the
      // second tap of the same click carries the same key and the server answers with the same progress.
      //
      // 32.6 — the LINE id, which this row now carries. It used to pass the ORDER id in both positions, so
      // the server looked for a line with the order's id, found none and answered 404 every single time. The
      // counter's one write had never worked; nothing on either side was checking that the two ids differed.
      await api.recordProcedureSession(row.orderId, row.orderLineId, crypto.randomUUID(), { attended: true });
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
      cell: (r: ProcedureQueueItem) => (
        <div className="row-actions">
          {r.expired ? (
            <StatusChip kind="bad" label={t(S.expired)} />
          ) : r.sessionsRemaining <= 0 ? (
            <StatusChip kind="neu" label={t(S.allDelivered)} />
          ) : (
            <Button size="sm"
              variant="primary"
              disabled={busy === r.orderLineId}
              onClick={() => void recordSession(r)}
            >
              {busy === r.orderLineId ? t(S.recording) : t(S.recordSession)}
            </Button>
          )}
          {/* 32.6 — closing the loop is a SEPARATE act from delivering, and it is offered whatever the
              session count says: a course abandoned after two of six still owes the ordering doctor an
              answer, and "all delivered" is not itself a report. Once closed it stops being an action —
              a second report is a second entry in the doctor's inbox for one episode. */}
          {r.completionReportedAt ? (
            <StatusChip kind="ok" label={t(S.reported)} />
          ) : (
            <Button size="sm" variant="ghost" onClick={() => { setReportOk(null); setReporting(r); }}>
              {t(S.reportBack)}
            </Button>
          )}
        </div>
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
        {reportOk && <InlineAlert tone="ok">{t(reportOk)}</InlineAlert>}

        {!submitted && <InlineAlert tone="info">{t(S.startHere)}</InlineAlert>}
        {submitted && !counterError && counterRows?.length === 0 && (
          <InlineAlert tone="info">{t(S.noMatch)}</InlineAlert>
        )}
        {counterRows && counterRows.length > 0 && (
          <Card>
            {/* 32.6 — WHO was verified, above the work.
                This section is called "Verify & Deliver" and it used to render nothing to verify against:
                the service passed a null name into a projection whose own contract says the name belongs on
                this path. A centre was checking a card number against the card number it had just typed. */}
            <dl className="kv-grid" aria-label={t(S.verified)}>
              <div>
                <dt>{t(S.verified)}</dt>
                <dd>
                  {counterRows[0].beneficiaryDisplayName ?? (
                    // NOT a blank and not a placeholder. A withheld name is a disclosure decision
                    // patient-service made about this caller; inventing "—" would read as a record that has
                    // no name, and inventing anything else would verify the wrong person.
                    <span className="muted" title={t(S.nameWithheldHint)}>{t(S.nameWithheld)}</span>
                  )}
                </dd>
              </div>
            </dl>
            <DataTable
              columns={columns}
              rows={counterRows}
              rowKey={(r) => r.orderLineId}
              caption={t(S.counterTitle)}
            />
          </Card>
        )}
        <ReportBackDialog
          row={reporting}
          onClose={() => setReporting(null)}
          onSent={(m) => { setReportOk(m); setReporting(null); void verify(); }}
        />
      </>
    );
  }


  return (
    <>
      <PageHeader title={t(S.queueTitle)} />
      {actionError && <InlineAlert tone="warn">{t(actionError)}</InlineAlert>}
      {reportOk && <InlineAlert tone="ok">{t(reportOk)}</InlineAlert>}
      <Card>
        <AsyncSection state={queue} isEmpty={(rows) => rows.length === 0} emptyLabel={S.queueEmpty}>
          {() => (
            <DataTableView
              query={query}
              columns={columns}
              // The row is one order paired with ONE deliverable line, so the line is what identifies it.
              rowKey={(r) => r.orderLineId}
              caption={t(S.queueTitle)}
              emptyLabel={t(S.queueEmpty)}
              noMatchesLabel={t(S.noMatches)}
            />
          )}
        </AsyncSection>
      </Card>
      <ReportBackDialog
        row={reporting}
        onClose={() => setReporting(null)}
        onSent={(m) => { setReportOk(m); setReporting(null); setNonce((n) => n + 1); }}
      />
    </>
  );
}

/**
 * 32.6 — the centre's report back to the ordering doctor (design 45 §7).
 *
 * <p>The endpoint and the client method had both existed since 29.2b; nothing on any screen called them, so
 * the obligation design 45 names — "a referral loop cannot close without a report back" — was one no centre
 * could discharge. The doctor's worklist showed the referral open for ever and the centre had no button.</p>
 *
 * <p>The minimum length is not decoration. The service refuses an empty body with a typed 422, and a client
 * that let one through would turn a deliberate refusal into a failed save the person cannot interpret.</p>
 */
function ReportBackDialog({
  row, onClose, onSent,
}: {
  row: ProcedureQueueItem | null;
  onClose: () => void;
  onSent: (message: Localized) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const [findings, setFindings] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);

  const ok = findings.trim().length >= 10;

  async function send() {
    if (!row) return;
    setBusy(true);
    setError(null);
    try {
      await api.reportProcedureCompletion(row.orderId, findings.trim());
      setFindings("");
      onSent(S.reportSent);
    } catch {
      setError(S.reportFailed);
    } finally {
      setBusy(false);
    }
  }

  return (
    <Modal
      open={row !== null}
      onOpenChange={(open) => { if (!open) { setFindings(""); setError(null); onClose(); } }}
      title={t(S.reportTitle)}
    >
      {row && (
        <p className="muted" style={{ marginBlockStart: 0 }}>
          <span className="tnum">{row.orderNo}</span> · <span className="tnum">{row.code}</span>
          {row.description ? ` — ${row.description}` : ""}
        </p>
      )}

      <InlineAlert tone="info">{t(S.reportWhy)}</InlineAlert>

      <label className="mc-field">
        <span className="mc-field-label">{t(S.reportFindings)}</span>
        <p className="muted" style={{ margin: 0 }}>{t(S.reportHint)}</p>
        <textarea
          className="rx-field-input"
          rows={4}
          placeholder={t(S.reportPlaceholder)}
          value={findings}
          onChange={(e) => setFindings(e.currentTarget.value)}
        />
      </label>
      {findings.trim().length > 0 && !ok && <InlineAlert tone="warn">{t(S.reportTooShort)}</InlineAlert>}

      <div aria-live="polite">{error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}</div>

      <div className="rx-actions">
        <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
        <Button variant="primary" disabled={!ok} loading={busy} onClick={() => void send()}>
          {t(S.send)}
        </Button>
      </div>
    </Modal>
  );
}

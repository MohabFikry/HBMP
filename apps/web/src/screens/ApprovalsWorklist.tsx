import { useState } from "react";
import {
  Button,
  Card,
  DataTableView,
  InlineAlert,
  InputField,
  SegmentedControl,
  StatusChip,
  TextareaField,
  useToast,
  useTableQuery,
} from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import {
  zDecisionRequest,
  type ApprovalItem,
  type ApprovalReview,
  type BreakGlassKind,
  type DecisionKind,
  type Localized,
} from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { useAsync } from "../api/useAsync";
import { newIdempotencyKey } from "../api/http";
import { PatientContextBar } from "./PatientProfile";
import { AsyncSection, extraCodes, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Approval Worklist", ar: "قائمة الموافقات" },
  empty: { en: "No authorizations awaiting your review.", ar: "لا توجد تفويضات بانتظار مراجعتك." },
  patient: { en: "Patient", ar: "المريض" },
  service: { en: "Service", ar: "الخدمة" },
  priority: { en: "Priority", ar: "الأولوية" },
  sla: { en: "SLA", ar: "المهلة" },
  owner: { en: "Reviewer", ar: "المراجع" },
  unowned: { en: "Unassigned", ar: "غير مُسند" },
  mine: { en: "Mine", ar: "لي" },
  fOwner: { en: "Assignment", ar: "الإسناد" },
  fAny: { en: "Anyone", ar: "الجميع" },
  truncated: {
    en: "Showing the first {shown} of {total} matching requests. Narrow the filters to see the rest.",
    ar: "يتم عرض أول {shown} من {total} طلباً مطابقاً. ضيّق عوامل التصفية لعرض الباقي.",
  },
  kindExtension: { en: "Validity extension", ar: "تمديد صلاحية" },
  extTitle: { en: "Validity extension", ar: "تمديد صلاحية" },
  extWhat: { en: "Expired item", ar: "العنصر المنتهي" },
  extAskedBy: { en: "Asked by", ar: "مقدم الطلب" },
  extReason: { en: "Reason given", ar: "السبب المذكور" },
  extNoReason: { en: "No reason was recorded.", ar: "لم يُسجَّل سبب." },
  extEffect: {
    en: "Approving resets the validity to the tenant's configured period, counted from today. Rejecting "
      + "leaves it expired — the patient needs a new prescription or order from a clinician.",
    ar: "الموافقة تعيد ضبط الصلاحية للمدة المحددة للجهة، محسوبة من اليوم. الرفض يُبقيها منتهية — وسيحتاج "
      + "المريض إلى وصفة أو طلب جديد من الطبيب.",
  },
  extNoClinical: {
    en: "There is no clinical review for this kind of request — it is a question about a date, not about "
      + "care. Everything the decision rests on is above.",
    ar: "لا توجد مراجعة سريرية لهذا النوع من الطلبات — فهو سؤال عن تاريخ، لا عن الرعاية. كل ما يستند إليه "
      + "القرار مذكور أعلاه.",
  },
  extApprove: { en: "Approve — revalidate", ar: "موافقة — إعادة التفعيل" },
  extReject: { en: "Reject", ar: "رفض" },
  extRationale: { en: "Your rationale", ar: "مبرر القرار" },
  state: { en: "State", ar: "الحالة" },
  action: { en: "Action", ar: "إجراء" },
  review: { en: "Review", ar: "مراجعة" },
  pick: { en: "Select a request to review its clinical justification and decide.", ar: "اختر طلباً لمراجعة مبرره السريري واتخاذ القرار." },
  justification: { en: "Clinical justification", ar: "المبرر السريري" },
  codes: { en: "Requested services", ar: "الخدمات المطلوبة" },
  documents: { en: "Documents", ar: "المستندات" },
  decision: { en: "Decision", ar: "القرار" },
  approve: { en: "Approve", ar: "موافقة" },
  partial: { en: "Partial", ar: "جزئي" },
  reject: { en: "Reject", ar: "رفض" },
  requestInfo: { en: "Request info", ar: "طلب معلومات" },
  rationale: { en: "Rationale", ar: "المبرر" },
  rationaleHint: { en: "Explain the decision — required for reject, partial, and request-info.", ar: "اشرح القرار — إلزامي للرفض والموافقة الجزئية وطلب المعلومات." },
  rationaleReq: { en: "A rationale is required for reject, partial, and request-info.", ar: "المبرر إلزامي للرفض والموافقة الجزئية وطلب المعلومات." },
  approvedAmount: { en: "Approved amount", ar: "المبلغ المعتمد" },
  amountReq: { en: "An approved amount is required for a partial approval.", ar: "المبلغ المعتمد إلزامي للموافقة الجزئية." },
  breakGlass: { en: "Break-glass override", ar: "تجاوز طارئ" },
  bgJust: { en: "Break-glass justification", ar: "مبرر التجاوز" },
  bgReq: { en: "Break-glass requires an extra justification.", ar: "التجاوز يتطلب مبرراً إضافياً." },
  submit: { en: "Submit decision", ar: "إرسال القرار" },
  ok: { en: "Decision recorded.", ar: "تم تسجيل القرار." },
  replay: { en: "Already recorded (idempotent replay).", ar: "مُسجّل مسبقاً (إعادة متكافئة)." },
  fail: { en: "Could not record the decision.", ar: "تعذّر تسجيل القرار." },
  search: { en: "Search", ar: "بحث" },
  searchHint: {
    en: "Member token, service code or reference",
    ar: "رمز العضو أو رمز الخدمة أو المرجع",
  },
  noMatches: {
    en: "No requests match. Change the search or clear the filters.",
    ar: "لا توجد طلبات مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },
  more: { en: "+{n} more", ar: "+{n} أخرى" },
  fPriority: { en: "Priority", ar: "الأولوية" },
  fRoutine: { en: "Routine", ar: "عادي" },
  fUrgent: { en: "Urgent", ar: "عاجل" },
  fEmergency: { en: "Emergency", ar: "طارئ" },
  fSla: { en: "SLA", ar: "مستوى الخدمة" },
  fBreached: { en: "Breached", ar: "متجاوَز" },
  fDue: { en: "Still in time", ar: "ضمن المهلة" },
  breached: { en: "Breached", ar: "متجاوَز" },
  dueIn: { en: "due in", ar: "خلال" },
  min: { en: "min", ar: "دقيقة" },
} satisfies Record<string, Localized>;

const PRIORITY_KIND = { routine: "neu", urgent: "warn", emergency: "bad" } as const;

export function ApprovalsWorklist() {
  const api = useApi();
  const t = useLoc();
  /*
    THE FILTERS THAT NARROW THE SERVER'S QUERY, versus the ones that narrow what is already on screen.

    Priority, SLA and assignment are sent to the endpoint — it has always accepted all three and the client
    sent none of them, taking the server's 200-row page and filtering that in the browser. A tenant with three
    hundred pending requests choosing "breached" was filtering a truncated list and was told nothing about it.

    Search stays client-side: it spans the member token and the item reference, which the endpoint does not
    index, and a round trip per keystroke would be worse than filtering the page in hand.
  */
  const [priority, setPriority] = useState<string>("");
  const [slaOnly, setSlaOnly] = useState(false);
  const [owner, setOwner] = useState<"any" | "me" | "unassigned">("any");
  const worklist = useAsync(
    () => api.approvalWorklist("Review", {
      priority: priority || undefined,
      slaBreached: slaOnly || undefined,
      assignedTo: owner,
    }),
    [priority, slaOnly, owner],
  );
  const rows = worklist.data?.rows ?? [];
  const total = worklist.data?.total ?? rows.length;
  const [selected, setSelected] = useState<string | null>(null);
  /** The selected ROW, not just its id — an extension is decided from what the queue already carries. */
  const selectedRow = rows.find((r) => r.id === selected) ?? null;

  const cols: Column<ApprovalItem>[] = [
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.patient.token}</span>, sortable: true, sortValue: (r) => r.patient.token },
    {
      key: "service",
      header: t(S.service),
      // A validity extension has no service code and no cost. Rendering "— · Validity extension" beside two
      // rows that DO carry both is how a reviewer opens it looking for a diagnosis it was never going to
      // have; the item's own reference is the thing that identifies it instead.
      cell: (r) =>
        r.source === "ValidityExtension" ? (
          <span>
            <StatusChip kind="info" label={t(S.kindExtension)} />{" "}
            <span className="tnum">{r.itemReference ?? "—"}</span>
          </span>
        ) : (
          // Every requested code. A three-service authorization used to render as its first code alone, with
          // the rest relabelled "supporting codes" in the panel — so the queue understated what was asked.
          <span>
            <span className="tnum">{r.service.code}</span> · {t(r.service.label)}
            {extraCodes(r.serviceCodes) > 0 && (
              <> · <span className="muted">{t(S.more).replace("{n}", String(extraCodes(r.serviceCodes)))}</span></>
            )}
          </span>
        ),
    },
    { key: "priority", header: t(S.priority), cell: (r) => <StatusChip kind={PRIORITY_KIND[r.priority]} label={r.priority} />, sortable: true, sortValue: (r) => r.priority },
    {
      key: "sla",
      header: t(S.sla),
      cell: (r) =>
        !r.sla ? (
          // No SLA on a fulfilment authorization: nothing waited on anybody. A countdown here would be a
          // clock ticking towards a deadline that does not exist.
          <span className="muted">—</span>
        ) : r.sla.breached ? (
          <StatusChip kind="bad" label={t(S.breached)} />
        ) : (
          <span className="tnum">{t(S.dueIn)} {r.sla.minutesRemaining} {t(S.min)}</span>
        ),
    },
    /*
      THERE IS NO "EST. COST" COLUMN, and its absence is deliberate.

      It used to be here, declared `numeric: true, sortable: true` over a value the client set to the literal
      string "—" on every row: the column sorted a constant. approvals-service holds no prices — there is no
      amount on the Authorization aggregate, no tariff client, and no column in its schema to source one from.

      A column headed "Est. cost" that is always blank tells a reviewer the platform knows the cost and is
      declining to show it. Removing it says the true thing: this system does not price a request at review
      time. A real one would come from the tariff service, and that is a different change with a real cost.
    */
    {
      key: "owner",
      header: t(S.owner),
      // The queue is shared. Whether anyone is holding a row is the second thing a reviewer needs after how
      // urgent it is, and the projection carried no ownership at all until now.
      cell: (r) => (r.assignedReviewerId ? <StatusChip kind="info" label={t(S.mine)} /> : <span className="muted">{t(S.unowned)}</span>),
      sortable: true,
      sortValue: (r) => (r.assignedReviewerId ? 0 : 1),
    },
    { key: "state", header: t(S.state), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} />, sortable: true, sortValue: (r) => t(r.status.label) },
    {
      key: "review",
      header: t(S.action),
      cell: (r) => (
        <Button size="sm" variant="secondary" onClick={() => setSelected(r.id)}>
          {t(S.review)}
        </Button>
      ),
    },
  ];

  /* Read OUTSIDE AsyncSection's render prop: a hook called in there would be conditional on the load. */
  const query = useTableQuery<ApprovalItem>({
    rows,
    columns: cols,
    // The three things a reviewer arrives holding: the member's token, the service code, or the reference of
    // the order or prescription the request was raised against.
    searchText: (r) => [r.patient.token, r.service.code, t(r.service.label), r.itemReference]
      .filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.searchHint),
    pageSize: 25,
    persistKey: "approvals-worklist",
  });

  return (
    <>
      <PageHeader title={t(S.title)} />
      {/* The three questions the SERVER answers. They were all served and none was ever asked. */}
      <div style={{ display: "flex", flexWrap: "wrap", gap: "var(--sp3)", marginBottom: "var(--sp3)" }}>
        <SegmentedControl
          aria-label={t(S.fPriority)}
          value={priority}
          onChange={setPriority}
          segments={[
            { value: "", label: t(S.fAny) },
            { value: "Emergency", label: t(S.fEmergency) },
            { value: "Urgent", label: t(S.fUrgent) },
            { value: "Routine", label: t(S.fRoutine) },
          ]}
        />
        <SegmentedControl<"any" | "me" | "unassigned">
          aria-label={t(S.fOwner)}
          value={owner}
          onChange={setOwner}
          segments={[
            { value: "any", label: t(S.fAny) },
            { value: "me", label: t(S.mine) },
            { value: "unassigned", label: t(S.unowned) },
          ]}
        />
        <label className="check">
          <input type="checkbox" checked={slaOnly} onChange={(e) => setSlaOnly(e.currentTarget.checked)} />
          <span>{t(S.fBreached)}</span>
        </label>
      </div>
      {/* THE CAP SAYS SO. The endpoint pages at 200 and the screen used to present that page as the whole
          answer; a reviewer narrowing a truncated list had no way to know they were doing it. */}
      {total > rows.length && (
        <InlineAlert tone="info">
          {t(S.truncated).replace("{shown}", String(rows.length)).replace("{total}", String(total))}
        </InlineAlert>
      )}
      <div className="split split-wide">
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <AsyncSection state={worklist} isEmpty={(d) => d.rows.length === 0} emptyLabel={S.empty}>
            {() => (
              // 18.D3 (U6): rows were focusable (interactive) with NO onSelect, so a keyboard user could
              // tab to a row, press Enter, and nothing happened — the worklist was reachable but not
              // operable. Enter/Space now opens the same review the mouse opens.
              <DataTableView
                query={query}
                columns={cols}
                rowKey={(r) => r.id}
                caption={t(S.title)}
                interactive
                selectedKey={selected ?? undefined}
                onSelect={(r) => setSelected(r.id)}
                emptyLabel={t(S.empty)}
                noMatchesLabel={t(S.noMatches)}
              />
            )}
          </AsyncSection>
        </Card>
        <div>
          {selectedRow?.source === "ValidityExtension" ? (
            <ExtensionReviewPanel
              key={selectedRow.id}
              item={selectedRow}
              t={t}
              onDone={() => { setSelected(null); worklist.reload(); }}
            />
          ) : selected ? (
            <ReviewPanel key={selected} approvalId={selected} t={t} onDone={() => { setSelected(null); worklist.reload(); }} />
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

/**
 * Deciding a validity-extension request.
 *
 * <b>Why it does not use the clinical review view.</b> That endpoint assembles a field-scoped EMR excerpt
 * and records a PHI read under a purpose-of-use. This request is a question about a DATE — whether a
 * prescription written three weeks ago may still be dispensed — and there is no diagnosis, service code or
 * cost behind it to read. Routing it through the clinical view would add an audited access to the patient's
 * record for a question that is not about the patient, and hand the reviewer a screen full of fields that
 * are all empty.
 *
 * Everything the decision rests on — what expired, who is asking, and why — already arrived with the
 * worklist row, and is shown here in full.
 */
function ExtensionReviewPanel({
  item,
  t,
  onDone,
}: {
  item: ApprovalItem;
  t: (l: Localized) => string;
  onDone: () => void;
}) {
  const api = useApi();
  const { toast } = useToast();
  const [rationale, setRationale] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function decide(kind: "approve" | "reject") {
    // The shared contract requires a rationale on a rejection. Checked here so the reviewer is told before
    // the round trip; the server refuses it either way.
    if (kind === "reject" && rationale.trim().length === 0) {
      setError(t(S.rationaleReq));
      return;
    }
    setError(null);
    setBusy(true);
    try {
      await api.decide({
        approvalId: item.id,
        idempotencyKey: newIdempotencyKey(),
        decision: kind,
        rationale: rationale.trim(),
      });
      toast(t(S.ok), "ok");
      onDone();
    } catch {
      // An approval that could not be APPLIED is refused by the server with a 502 and nothing is recorded —
      // so "failed" here is the truth, and retrying is a first attempt rather than a repair.
      toast(t(S.fail), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      {/* The context bar, so a decision is never taken against the wrong record. */}
      <PatientContextBar beneficiaryId={item.patient.id} />

      <div>
        <h2 className="section-h" style={{ marginBlockStart: 0 }}>{t(S.extTitle)}</h2>
        <dl className="rxv-meta">
          <dt>{t(S.extWhat)}</dt>
          <dd className="tnum">{item.itemReference ?? "—"}</dd>
          <dt>{t(S.extAskedBy)}</dt>
          <dd>{t(item.requestedBy)}</dd>
        </dl>

        <h3 className="rxv-h">{t(S.extReason)}</h3>
        {/* The whole substance of the decision. An absent reason is said in words rather than left blank —
            a blank box reads as a rendering fault, and this one would be a refusal waiting to happen. */}
        {item.extensionReason
          ? <p>{item.extensionReason}</p>
          : <p className="rxv-missing">{t(S.extNoReason)}</p>}

        <InlineAlert tone="info">{t(S.extEffect)}</InlineAlert>
        <p className="muted">{t(S.extNoClinical)}</p>
      </div>

      <label className="mc-field">
        <span className="mc-field-label">{t(S.extRationale)}</span>
        <textarea
          className="rx-field-input"
          rows={2}
          value={rationale}
          onChange={(e) => setRationale(e.currentTarget.value)}
        />
      </label>
      {error && <InlineAlert tone="bad">{error}</InlineAlert>}

      <div className="rx-actions">
        <Button variant="danger" loading={busy} onClick={() => void decide("reject")}>{t(S.extReject)}</Button>
        <Button variant="primary" loading={busy} onClick={() => void decide("approve")}>{t(S.extApprove)}</Button>
      </div>
    </Card>
  );
}

function ReviewPanel({ approvalId, t, onDone }: { approvalId: string; t: (l: Localized) => string; onDone: () => void }) {
  const api = useApi();
  const review = useAsync<ApprovalReview>(() => api.approvalReview(approvalId), [approvalId]);
  return (
    <AsyncSection state={review} emptyLabel={S.pick}>
      {(r) => <DecisionForm review={r} t={t} onDone={onDone} />}
    </AsyncSection>
  );
}

function DecisionForm({ review, t, onDone }: { review: ApprovalReview; t: (l: Localized) => string; onDone: () => void }) {
  const api = useApi();
  const { toast } = useToast();
  const [decision, setDecision] = useState<DecisionKind>("approve");
  const [rationale, setRationale] = useState("");
  const [approvedAmount, setApprovedAmount] = useState("");
  const [bgOn, setBgOn] = useState(false);
  const [bgKind, setBgKind] = useState<BreakGlassKind>("emergency");
  const [bgJust, setBgJust] = useState("");
  const [errors, setErrors] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);

  async function submit() {
    // US-060 is enforced by the SHARED contract refine — the UI validates with the same schema the server does,
    // so reject/partial/request-info without a rationale (or break-glass without a justification) never leaves
    // the client.
    const candidate = {
      approvalId: review.id,
      idempotencyKey: newIdempotencyKey(),
      decision,
      rationale,
      approvedAmount: decision === "partial" ? approvedAmount : undefined,
      breakGlass: bgOn ? { kind: bgKind, justification: bgJust } : undefined,
    };
    const parsed = zDecisionRequest.safeParse(candidate);
    if (!parsed.success) {
      const next: Record<string, string> = {};
      for (const issue of parsed.error.issues) {
        const path = issue.path.join(".");
        if (path.startsWith("rationale")) next.rationale = t(S.rationaleReq);
        else if (path.startsWith("approvedAmount")) next.approvedAmount = t(S.amountReq);
        else if (path.startsWith("breakGlass")) next.bgJust = t(S.bgReq);
      }
      setErrors(next);
      return;
    }
    setErrors({});
    setBusy(true);
    try {
      const res = await api.decide(parsed.data);
      toast(t(res.replayed ? S.replay : S.ok), "ok");
      onDone();
    } catch {
      toast(t(S.fail), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <Card as="section" style={{ padding: "var(--sp5)", display: "grid", gap: "var(--sp4)" }}>
      {/* Phase 20 — the context bar, so an approval is never decided against the wrong record. */}
      <PatientContextBar beneficiaryId={review.patient.id} />
      {/* Field-scoped review (min-necessary): coded reason + justification + docs — not the full EMR. */}
      <div>
        <h2 style={{ margin: 0 }}>{review.service.code} · {t(review.service.label)}</h2>
        {/* No "requested amount": approvals-service does not price a request. See the note on the removed
            cost column above — a field that is always "—" reads as missing data rather than as absent data. */}
        <p className="muted" style={{ margin: "4px 0 0" }}>{review.patient.token}</p>
      </div>
      <dl className="kv-grid">
        <div><dt>{t(S.justification)}</dt><dd>{review.clinicalJustification}</dd></div>
        <div>
          <dt>{t(S.codes)}</dt>
          <dd><ul className="chip-list">{review.requestedServices.map((c) => <li key={c.code}><StatusChip kind="info" label={`${c.code} · ${t(c.label)}`} /></li>)}</ul></dd>
        </div>
        <div>
          <dt>{t(S.documents)}</dt>
          <dd><ul className="doc-list">{review.documents.map((d) => <li key={d.id}>{d.name}</li>)}</ul></dd>
        </div>
      </dl>

      <form
        className="stack"
        aria-label={t(S.decision)}
        onSubmit={(e) => { e.preventDefault(); void submit(); }}
      >
        <fieldset className="fieldset">
          <legend>{t(S.decision)}</legend>
          <SegmentedControl<DecisionKind>
            aria-label={t(S.decision)}
            value={decision}
            onChange={setDecision}
            segments={[
              { value: "approve", label: t(S.approve) },
              { value: "partial", label: t(S.partial) },
              { value: "reject", label: t(S.reject) },
              { value: "request_info", label: t(S.requestInfo) },
            ]}
          />
        </fieldset>

        {decision === "partial" && (
          <InputField
            label={t(S.approvedAmount)}
            value={approvedAmount}
            onChange={(e) => setApprovedAmount(e.currentTarget.value)}
            error={errors.approvedAmount}
          />
        )}

        <TextareaField
          label={t(S.rationale)}
          help={t(S.rationaleHint)}
          value={rationale}
          onChange={(e) => setRationale(e.currentTarget.value)}
          error={errors.rationale}
          rows={3}
        />

        <label className="check">
          <input type="checkbox" checked={bgOn} onChange={(e) => setBgOn(e.currentTarget.checked)} />
          <span>{t(S.breakGlass)}</span>
        </label>
        {bgOn && (
          <div className="stack">
            <SegmentedControl<BreakGlassKind>
              aria-label={t(S.breakGlass)}
              value={bgKind}
              onChange={setBgKind}
              segments={[
                { value: "emergency", label: "emergency" },
                { value: "override", label: "override" },
                { value: "manual", label: "manual" },
              ]}
            />
            <TextareaField
              label={t(S.bgJust)}
              value={bgJust}
              onChange={(e) => setBgJust(e.currentTarget.value)}
              error={errors.bgJust}
              rows={2}
            />
          </div>
        )}

        <div>
          <Button type="submit" variant={decision === "reject" ? "danger" : "primary"} loading={busy}>
            {t(S.submit)}
          </Button>
        </div>
      </form>
    </Card>
  );
}

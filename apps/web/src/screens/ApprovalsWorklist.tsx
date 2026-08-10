import { useState } from "react";
import {
  Button,
  Card,
  DataTable,
  InlineAlert,
  InputField,
  SegmentedControl,
  StatusChip,
  TextareaField,
  useToast,
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
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Approval Worklist", ar: "قائمة الموافقات" },
  empty: { en: "No authorizations awaiting your review.", ar: "لا توجد تفويضات بانتظار مراجعتك." },
  patient: { en: "Patient", ar: "المريض" },
  service: { en: "Service", ar: "الخدمة" },
  priority: { en: "Priority", ar: "الأولوية" },
  sla: { en: "SLA", ar: "المهلة" },
  cost: { en: "Est. cost", ar: "التكلفة" },
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
  codes: { en: "Supporting codes", ar: "الرموز الداعمة" },
  documents: { en: "Documents", ar: "المستندات" },
  requested: { en: "Requested amount", ar: "المبلغ المطلوب" },
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
  breached: { en: "Breached", ar: "متجاوَز" },
  dueIn: { en: "due in", ar: "خلال" },
  min: { en: "min", ar: "دقيقة" },
} satisfies Record<string, Localized>;

const PRIORITY_KIND = { routine: "neu", urgent: "warn", emergency: "bad" } as const;

export function ApprovalsWorklist() {
  const api = useApi();
  const t = useLoc();
  const worklist = useAsync<ApprovalItem[]>(() => api.approvalWorklist(), []);
  const [selected, setSelected] = useState<string | null>(null);
  /** The selected ROW, not just its id — an extension is decided from what the queue already carries. */
  const selectedRow = (worklist.data ?? []).find((r) => r.id === selected) ?? null;

  const cols: Column<ApprovalItem>[] = [
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.patient.token}</span> },
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
          <span><span className="tnum">{r.service.code}</span> · {t(r.service.label)}</span>
        ),
    },
    { key: "priority", header: t(S.priority), cell: (r) => <StatusChip kind={PRIORITY_KIND[r.priority]} label={r.priority} /> },
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
    { key: "cost", header: t(S.cost), cell: (r) => r.estimatedCost, numeric: true },
    { key: "state", header: t(S.state), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    {
      key: "review",
      header: t(S.action),
      cell: (r) => (
        <Button size="sm" variant={selected === r.id ? "primary" : "secondary"} onClick={() => setSelected(r.id)}>
          {t(S.review)}
        </Button>
      ),
    },
  ];

  return (
    <>
      <PageHeader title={t(S.title)} />
      <div className="split split-wide">
        <Card as="section" style={{ padding: "var(--sp3)" }}>
          <AsyncSection state={worklist} isEmpty={(d) => d.length === 0} emptyLabel={S.empty}>
            {(rows) => (
              // 18.D3 (U6): rows were focusable (interactive) with NO onSelect, so a keyboard user could
              // tab to a row, press Enter, and nothing happened — the worklist was reachable but not
              // operable. Enter/Space now opens the same review the mouse opens.
              <DataTable
                columns={cols}
                rows={rows}
                rowKey={(r) => r.id}
                caption={t(S.title)}
                interactive
                selectedKey={selected ?? undefined}
                onSelect={(r) => setSelected(r.id)}
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
        <p className="muted" style={{ margin: "4px 0 0" }}>{review.patient.token} · {t(S.requested)}: <span className="tnum">{review.requestedAmount}</span></p>
      </div>
      <dl className="kv-grid">
        <div><dt>{t(S.justification)}</dt><dd>{review.clinicalJustification}</dd></div>
        <div>
          <dt>{t(S.codes)}</dt>
          <dd><ul className="chip-list">{review.supportingCodes.map((c) => <li key={c.code}><StatusChip kind="info" label={`${c.code} · ${t(c.label)}`} /></li>)}</ul></dd>
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

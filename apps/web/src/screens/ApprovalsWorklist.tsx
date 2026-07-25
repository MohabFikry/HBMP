import { useState } from "react";
import {
  Button,
  Card,
  DataTable,
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
import { AsyncSection, PageHeader, useLoc } from "./_shared";

const S = {
  title: { en: "Approval worklist", ar: "قائمة الموافقات" },
  empty: { en: "No authorizations awaiting your review.", ar: "لا توجد تفويضات بانتظار مراجعتك." },
  patient: { en: "Patient", ar: "المريض" },
  service: { en: "Service", ar: "الخدمة" },
  priority: { en: "Priority", ar: "الأولوية" },
  sla: { en: "SLA", ar: "المهلة" },
  cost: { en: "Est. cost", ar: "التكلفة" },
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

  const cols: Column<ApprovalItem>[] = [
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.patient.token}</span> },
    { key: "service", header: t(S.service), cell: (r) => <span><span className="tnum">{r.service.code}</span> · {t(r.service.label)}</span> },
    { key: "priority", header: t(S.priority), cell: (r) => <StatusChip kind={PRIORITY_KIND[r.priority]} label={r.priority} /> },
    {
      key: "sla",
      header: t(S.sla),
      cell: (r) =>
        r.sla.breached ? (
          <StatusChip kind="bad" label={t(S.breached)} />
        ) : (
          <span className="tnum">{t(S.dueIn)} {r.sla.minutesRemaining} {t(S.min)}</span>
        ),
    },
    { key: "cost", header: t(S.cost), cell: (r) => <span className="tnum">{r.estimatedCost}</span> },
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
            {(rows) => <DataTable columns={cols} rows={rows} rowKey={(r) => r.id} caption={t(S.title)} interactive />}
          </AsyncSection>
        </Card>
        <div>
          {selected ? (
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
      {/* Field-scoped review (min-necessary): coded reason + justification + docs — not the full EMR. */}
      <div>
        <h2 style={{ margin: 0 }}>{review.service.code} · {t(review.service.label)}</h2>
        <p className="muted" style={{ margin: "4px 0 0" }}>{review.patient.token} · {t(S.requested)}: <span className="tnum">{review.requestedAmount}</span></p>
      </div>
      <div className="kv-grid">
        <div><dt>{t(S.justification)}</dt><dd>{review.clinicalJustification}</dd></div>
        <div>
          <dt>{t(S.codes)}</dt>
          <dd><ul className="chip-list">{review.supportingCodes.map((c) => <li key={c.code}><StatusChip kind="info" label={`${c.code} · ${t(c.label)}`} /></li>)}</ul></dd>
        </div>
        <div>
          <dt>{t(S.documents)}</dt>
          <dd><ul className="doc-list">{review.documents.map((d) => <li key={d.id}>{d.name}</li>)}</ul></dd>
        </div>
      </div>

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

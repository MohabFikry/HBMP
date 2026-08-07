import { useMemo, useState, type SetStateAction } from "react";
import { z } from "zod";
import { Button, Icon, InlineAlert, Modal, useToast } from "@mersal/design-system";
import type {
  CheckState, CptSection, InvestigationDraftLine, InvestigationOrderType, Localized,
  OrderAcknowledgement, OrderCheckKind, OrderFinding, OrderValidationResult,
} from "@mersal/contracts";
import { zInvestigationDraftLine, zOrderAcknowledgement, zOrderValidationResult } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useDraft } from "../draftStore";
import { useLoc } from "../_shared";
import { CptCombobox } from "./CptCombobox";
import { LineStatusChip } from "../prescribing/LineStatusChip";

const S = {
  addLine: { en: "Add another", ar: "إضافة سطر آخر" },
  removeLine: { en: "Remove", ar: "إزالة" },
  quantity: { en: "Quantity", ar: "الكمية" },
  note: { en: "Note for the site", ar: "ملاحظة للمنفّذ" },
  notePlaceholder: { en: "e.g. left knee, fasting", ar: "مثال: الركبة اليسرى، صائم" },
  status: { en: "Status", ar: "الحالة" },
  validate: { en: "Check", ar: "تحقّق" },
  submit: { en: "Send order", ar: "إرسال الطلب" },
  staleRun: {
    en: "A line changed since the last check. Check again before sending.",
    ar: "تم تعديل أحد الأسطر بعد آخر تحقق. يرجى التحقق مرة أخرى قبل الإرسال.",
  },
  noDiagnosis: {
    en: "No diagnosis is recorded on this encounter, so there is nothing to check the tests against.",
    ar: "لا يوجد تشخيص مسجل في هذه الزيارة، لذلك لا يوجد ما يمكن التحقق من الفحوصات مقابله.",
  },
  blocked: { en: "A line cannot be ordered. It must be removed or changed.", ar: "أحد الأسطر لا يمكن طلبه. يجب حذفه أو تعديله." },
  unacknowledged: { en: "Every warning needs a reason before you can send.", ar: "يجب ذكر سبب لكل تحذير قبل الإرسال." },
  needTest: { en: "Choose a procedure for every line.", ar: "اختر إجراءً لكل سطر." },
  cancel: { en: "Cancel", ar: "إلغاء" },
  discard: { en: "Discard", ar: "حذف المسودة" },
  confirmDiscard: { en: "Discard this order?", ar: "حذف هذا الطلب؟" },
  confirmDiscardBody: {
    en: "The composed lines and their checks are thrown away. Nothing has been ordered, so there is nothing "
      + "to cancel — but the reasons you gave for any warnings go with them.",
    ar: "سيتم حذف الأسطر المُعدّة وفحوصاتها. لم يُطلب أي إجراء، لذا لا يوجد ما يُلغى — لكن الأسباب التي "
      + "ذكرتها لأي تحذير ستُحذف معها.",
  },
  reason: { en: "Reason to proceed", ar: "سبب المتابعة" },
  reasonPlaceholder: { en: "Why proceed?", ar: "لماذا المتابعة؟" },
  checksFor: { en: "Checks —", ar: "الفحوصات —" },
  viewChecks: { en: "Checks for", ar: "فحوصات" },
  sources: { en: "Sources", ar: "المصادر" },
  sentLab: { en: "Lab order sent.", ar: "تم إرسال طلب المختبر." },
  sentImaging: { en: "Imaging order sent.", ar: "تم إرسال طلب الأشعة." },
  sentForApproval: { en: "Sent to the approval team.", ar: "تم الإرسال إلى فريق الموافقات." },
  submitFailed: { en: "The order was refused.", ar: "تم رفض الطلب." },
  checkFailed: { en: "The check could not run.", ar: "تعذّر إجراء التحقق." },
};

const KIND_LABEL: Record<OrderCheckKind, Localized> = {
  Code: { en: "Procedure code", ar: "كود الإجراء" },
  Section: { en: "Section", ar: "القسم" },
  Duplicate: { en: "Already ordered", ar: "مطلوب مسبقاً" },
  PriorAuthorization: { en: "Pre-authorization", ar: "الموافقة المسبقة" },
  Indication: { en: "Indication", ar: "دواعي الإجراء" },
};

/**
 * The persisted draft's shape, held to the same contracts the API is. See `draftStore` — restored bytes are
 * untrusted, and a draft that does not parse is discarded rather than repaired.
 */
const ORDER_DRAFT = z.object({
  lines: z.array(zInvestigationDraftLine),
  result: zOrderValidationResult.nullable(),
  validatedFingerprint: z.string().nullable(),
  acknowledgements: z.array(zOrderAcknowledgement),
});
type OrderDraft = z.infer<typeof ORDER_DRAFT>;

function emptyDraft(): OrderDraft {
  return { lines: [newLine()], result: null, validatedFingerprint: null, acknowledgements: [] };
}

/**
 * "Nothing has been composed here" — one definition, used for three things that must agree: what the draft
 * store keeps, whether Discard has anything to discard, and whether the encounter screen will let the visit
 * be closed. See the prescribing workspace, which draws the same line for the same reason.
 */
function isEmptyDraft(d: OrderDraft): boolean {
  return d.result === null && d.lines.length === 1 && d.lines[0].test === null;
}

function newLine(): InvestigationDraftLine {
  return { lineId: crypto.randomUUID(), test: null, quantity: 1, note: "" };
}

/** Changing any of this invalidates the last check — the same staleness rule the prescribing workspace uses. */
function fingerprint(lines: InvestigationDraftLine[]): string {
  return lines.map((l) => `${l.lineId}|${l.test?.code ?? ""}|${l.quantity}|${l.note}`).join(";");
}

/**
 * Ordering investigations — one workspace, used by the Labs tab and the Imaging tab.
 *
 * <b>What it replaces.</b> A modal with two text inputs pre-filled with a hard-coded LOINC code and the
 * words "Complete blood count". One line only, no catalogue behind it, no checks, and a 422 the first time
 * anyone typed a real code — the same shape of defect the prescribing modal had, in the next tab along.
 *
 * <b>Why it mirrors prescribing so closely.</b> Both sit in the same encounter, are used by the same doctor
 * within a minute of each other, and ask the same question of the clinician: compose several lines, see per-
 * line verdicts, give a reason for anything you are overriding, then send. Two different sequences for that
 * would be two things to learn and two places to make a different mistake. So: the same five states, the
 * same staleness rule, the same acknowledgement-gates-submission rule, the same chip.
 *
 * <b>What it does NOT copy is the checks themselves.</b> There is no procedure-indication reference in this
 * platform, so the Indication check reports NotChecked with the reason rather than a pass. Inventing a
 * clinical opinion to fill the same number of rows as the prescribing panel would be the worst possible
 * reason to show one.
 *
 * The verdict here is ADVISORY. orders-service re-derives everything on create and reads nothing this
 * returned, so nothing below is a security control.
 */
export function InvestigationWorkspace({
  encounterId,
  orderType,
  diagnosisIcdCodes,
  onDone,
}: {
  encounterId: string;
  orderType: InvestigationOrderType;
  diagnosisIcdCodes: string[];
  onDone?: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();

  // A TAB IS NOT A SECTION. Imaging is one; Labs is two, because a sample run on an analyser (Laboratory,
  // 80047–87999 + 89049–89398) and a specimen read by a pathologist (Pathology, 88000–88749) are ordered
  // from the same tab and are not the same kind of work. The Labs tab used to be "codes beginning 8", which
  // happened to cover both without saying so — and could not be narrowed or widened without also changing
  // what "beginning 8" means.
  const sections: CptSection[] = orderType === "Imaging" ? ["Imaging"] : ["Laboratory", "Pathology"];

  /**
   * The composer's whole state, kept across a reload — see `draftStore`.
   *
   * Keyed by ORDER TYPE as well as encounter: the Labs tab and the Imaging tab are the same component with
   * different sections, and one key would let a half-composed imaging order restore into the labs tab.
   *
   * One object rather than four `useState`s, because the four are only meaningful together: a restored
   * `result` without its `validatedFingerprint` reads as a check that was never run against these lines.
   */
  const [draft, setDraft] = useDraft(
    `order:${orderType}:${encounterId}`,
    ORDER_DRAFT,
    emptyDraft,
    isEmptyDraft,
  );
  const { lines, result, validatedFingerprint, acknowledgements } = draft;

  // Field-shaped setters over the single object, so every call site below reads exactly as it did when these
  // were four separate states — including the updater form, which keeps them free of stale closures.
  const setLines = (u: SetStateAction<InvestigationDraftLine[]>) =>
    setDraft((d) => ({ ...d, lines: typeof u === "function" ? u(d.lines) : u }));
  const setAcknowledgements = (u: SetStateAction<OrderAcknowledgement[]>) =>
    setDraft((d) => ({ ...d, acknowledgements: typeof u === "function" ? u(d.acknowledgements) : u }));
  const setChecked = (r: OrderValidationResult | null, fingerprint: string | null) =>
    setDraft((d) => ({ ...d, result: r, validatedFingerprint: fingerprint }));

  const [busy, setBusy] = useState(false);
  const [discarding, setDiscarding] = useState(false);
  const composed = !isEmptyDraft(draft);
  const [openChecks, setOpenChecks] = useState<string | null>(null);

  const current = fingerprint(lines);
  const stale = result !== null && validatedFingerprint !== current;
  const allLinesHaveTests = lines.every((l) => l.test !== null);

  const warnings = useMemo(
    () => (result?.findings ?? []).filter((f) => f.requiresAcknowledgement),
    [result],
  );
  const unacknowledged = warnings.filter(
    (f) => !acknowledgements.some((a) => a.lineId === f.lineId && a.findingKind === f.kind && a.reason.trim().length > 0),
  );
  const blocked = (result?.findings ?? []).filter((f) => f.isBlocking);

  const canSubmit =
    allLinesHaveTests && result !== null && !stale && unacknowledged.length === 0 && blocked.length === 0 && !busy;

  function patch(lineId: string, change: Partial<InvestigationDraftLine>) {
    setLines((prev) => prev.map((l) => (l.lineId === lineId ? { ...l, ...change } : l)));
    // An acknowledgement belongs to the finding it was given for; editing the line re-derives the findings,
    // so carrying the reason forward would attach a justification to something the doctor never saw.
    setAcknowledgements((prev) => prev.filter((a) => a.lineId !== lineId));
  }

  async function validate() {
    setBusy(true);
    try {
      const r = await api.validateInvestigationOrder({ encounterId, orderType, lines, diagnosisIcdCodes });
      setChecked(r, fingerprint(lines));
    } catch {
      setChecked(null, null);
      toast(t(S.checkFailed), "bad");
    } finally {
      setBusy(false);
    }
  }

  async function submit() {
    setBusy(true);
    try {
      const res = await api.submitInvestigationOrder({ encounterId, orderType, lines, acknowledgements });
      // Which of the two happened is not a detail: an order that went for approval is NOT with the lab yet,
      // and a doctor who reads "sent" and expects a result tomorrow has been misinformed by one word.
      toast(
        res.requiresApproval
          ? `${t(S.sentForApproval)} ${res.orderNo}`
          : `${t(orderType === "Imaging" ? S.sentImaging : S.sentLab)} ${res.orderNo}`,
        "ok",
      );
      // Back to empty, with the findings and acknowledgements going WITH the lines they were derived from.
      // Empty again — which is also how it leaves the draft store, since an empty composer is the one thing
      // `useDraft` does not keep. A sent order is no longer a draft, and leaving one behind would let a
      // reload restore an unsent-looking copy of something already recorded.
      setDraft(emptyDraft());
      setOpenChecks(null);
      onDone?.();
    } catch {
      toast(t(S.submitFailed), "bad");
    } finally {
      setBusy(false);
    }
  }

  function stateFor(lineId: string): CheckState {
    if (!result || stale) return "NotChecked";
    return result.lineStates[lineId] ?? "NotChecked";
  }

  return (
    <div className="rx-workspace stack">
      {diagnosisIcdCodes.length === 0 && <InlineAlert tone="info">{t(S.noDiagnosis)}</InlineAlert>}

      <ul className="rx-lines">
        {lines.map((line) => (
          <li key={line.lineId} className="rx-line">
            <div className="rx-line-main">
              <CptCombobox
                value={line.test}
                sections={sections}
                onChange={(test) => patch(line.lineId, { test })}
                disabled={busy}
              />
              <div className="rx-line-fields">
                <label className="rx-field">
                  <span className="rx-field-label">{t(S.quantity)}</span>
                  <input
                    className="rx-field-input"
                    type="number"
                    min={1}
                    value={line.quantity}
                    disabled={busy}
                    onChange={(e) => patch(line.lineId, { quantity: Math.max(1, Number(e.currentTarget.value) || 1) })}
                  />
                </label>
                <label className="rx-field rx-field--wide">
                  <span className="rx-field-label">{t(S.note)}</span>
                  <input
                    className="rx-field-input"
                    placeholder={t(S.notePlaceholder)}
                    value={line.note}
                    disabled={busy}
                    onChange={(e) => patch(line.lineId, { note: e.currentTarget.value })}
                  />
                </label>
                <div className="rx-field">
                  <span className="rx-field-label">{t(S.status)}</span>
                  <LineStatusChip
                    state={stateFor(line.lineId)}
                    detailLabel={line.test ? `${t(S.viewChecks)} ${line.test.description}` : undefined}
                    onClick={
                      result && !stale && result.findings.some((f) => f.lineId === line.lineId)
                        ? () => setOpenChecks(line.lineId)
                        : undefined
                    }
                  />
                </div>
              </div>
            </div>

            {lines.length > 1 && (
              <button
                type="button"
                className="rx-line-remove"
                disabled={busy}
                onClick={() => {
                  setLines((prev) => prev.filter((l) => l.lineId !== line.lineId));
                  setAcknowledgements((prev) => prev.filter((a) => a.lineId !== line.lineId));
                }}
              >
                {t(S.removeLine)}
              </button>
            )}

            {result && !stale && (
              <OrderChecksModal
                open={openChecks === line.lineId}
                onOpenChange={(v) => setOpenChecks(v ? line.lineId : null)}
                testName={line.test?.description ?? ""}
                findings={result.findings.filter((f) => f.lineId === line.lineId)}
                acknowledgements={acknowledgements}
                onAcknowledge={(finding, reason) =>
                  setAcknowledgements((prev) => [
                    ...prev.filter((a) => !(a.lineId === finding.lineId && a.findingKind === finding.kind)),
                    { lineId: finding.lineId, findingKind: finding.kind, reason },
                  ])
                }
              />
            )}
          </li>
        ))}
      </ul>

      <Button
        variant="secondary"
        size="sm"
        disabled={busy}
        onClick={() => setLines((prev) => [...prev, newLine()])}
        leadingIcon={<Icon name="plus" width={16} height={16} aria-hidden="true" />}
      >
        {t(S.addLine)}
      </Button>

      {stale && <InlineAlert tone="warn">{t(S.staleRun)}</InlineAlert>}
      {blocked.length > 0 && <InlineAlert tone="bad">{t(S.blocked)}</InlineAlert>}
      {result && !stale && unacknowledged.length > 0 && <InlineAlert tone="warn">{t(S.unacknowledged)}</InlineAlert>}
      {!allLinesHaveTests && <InlineAlert tone="info">{t(S.needTest)}</InlineAlert>}

      <div className="rx-actions">
        {/*
          The other way out, and the reason the encounter screen can insist on one — closing a visit is
          refused while anything sits composed-but-unsent here. See the prescribing workspace.
        */}
        <Button variant="ghost" disabled={!composed || busy} onClick={() => setDiscarding(true)}>
          {t(S.discard)}
        </Button>
        <Button variant="secondary" loading={busy} disabled={!allLinesHaveTests} onClick={() => void validate()}>
          {t(S.validate)}
        </Button>
        <Button variant="primary" disabled={!canSubmit} onClick={() => void submit()}>
          {t(S.submit)}
        </Button>
      </div>

      {/*
        Confirmed, because this throws away work a reload is now guaranteed to have preserved. Building
        persistence to save a composed order and then letting one mis-click wipe it would be the two halves
        of the same feature disagreeing.
      */}
      <Modal
        open={discarding}
        onOpenChange={setDiscarding}
        title={t(S.confirmDiscard)}
        footer={
          <>
            <Button variant="ghost" onClick={() => setDiscarding(false)}>{t(S.cancel)}</Button>
            <Button
              variant="danger"
              onClick={() => { setDraft(emptyDraft()); setOpenChecks(null); setDiscarding(false); }}
            >
              {t(S.discard)}
            </Button>
          </>
        }
      >
        <p style={{ margin: 0 }}>{t(S.confirmDiscardBody)}</p>
      </Modal>
    </div>
  );
}

/** One line's checks on demand — the same weight and the same reasoning as the prescribing dialog. */
function OrderChecksModal({
  open,
  onOpenChange,
  testName,
  findings,
  acknowledgements,
  onAcknowledge,
}: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  testName: string;
  findings: OrderFinding[];
  acknowledgements: OrderAcknowledgement[];
  onAcknowledge: (finding: OrderFinding, reason: string) => void;
}) {
  const t = useLoc();

  return (
    <Modal open={open} onOpenChange={onOpenChange} title={`${t(S.checksFor)} ${testName}`}>
      <ul className="rx-checks">
        {findings.map((f) => {
          const ack = acknowledgements.find((a) => a.lineId === f.lineId && a.findingKind === f.kind);
          return (
            <li key={f.kind} className="rx-check">
              <span className="rx-check-kind">{t(KIND_LABEL[f.kind])}</span>
              <LineStatusChip state={f.state} />
              <p className="rx-check-message">{t(f.message)}</p>
              {f.requiresAcknowledgement && (
                <label className="rx-check-ack">
                  <span className="rx-field-label">{t(S.reason)}</span>
                  <input
                    className="rx-field-input"
                    placeholder={t(S.reasonPlaceholder)}
                    value={ack?.reason ?? ""}
                    onChange={(e) => onAcknowledge(f, e.currentTarget.value)}
                  />
                </label>
              )}
            </li>
          );
        })}
      </ul>

      <details className="rx-sources">
        <summary>{t(S.sources)}</summary>
        <ul className="rx-sources-list">
          {findings
            .filter((f) => f.sourceName || f.caveat)
            .map((f) => (
              <li key={f.kind}>
                <span className="rx-check-kind">{t(KIND_LABEL[f.kind])}</span>
                {" — "}
                {f.sourceName ?? "—"}
                {f.caveat && <span className="rx-finding-caveat"> {f.caveat}</span>}
              </li>
            ))}
        </ul>
      </details>
    </Modal>
  );
}

import { useMemo, useState, type SetStateAction } from "react";
import { z } from "zod";
import { Button, Icon, InlineAlert, Modal, useToast } from "@mersal/design-system";
import type {
  CheckKind, CheckState, ClinicalSeverity, Finding, LineAcknowledgement, Localized,
  PrescriptionDraftLine, ValidationResult,
} from "@mersal/contracts";
import { SEVERITY_RANK } from "@mersal/contracts";
import { zLineAcknowledgement, zPrescriptionDraftLine, zValidationResult } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useDraft } from "../draftStore";
import { useLoc } from "../_shared";
import { DrugCombobox } from "./DrugCombobox";
import { LineStatusChip, SeverityChip, worstSeverity } from "./LineStatusChip";

const S = {
  title: { en: "Prescribe", ar: "وصف دواء" },
  addLine: { en: "Add medicine", ar: "إضافة دواء" },
  removeLine: { en: "Remove", ar: "إزالة" },
  dose: { en: "Dose", ar: "الجرعة" },
  duration: { en: "Duration (days)", ar: "المدة (أيام)" },
  quantity: { en: "Quantity", ar: "الكمية" },
  status: { en: "Status", ar: "الحالة" },
  validate: { en: "Validate", ar: "تحقّق" },
  submit: { en: "Submit", ar: "إرسال" },
  cancel: { en: "Cancel", ar: "إلغاء" },
  discard: { en: "Discard", ar: "حذف المسودة" },
  confirmDiscard: { en: "Discard this prescription?", ar: "حذف هذه الوصفة؟" },
  confirmDiscardBody: {
    en: "The composed lines and their checks are thrown away. Nothing has been prescribed, so there is "
      + "nothing to cancel — but the reasons you gave for any warnings go with them.",
    ar: "سيتم حذف الأسطر المُعدّة وفحوصاتها. لم يُوصف أي دواء، لذا لا يوجد ما يُلغى — لكن الأسباب التي "
      + "ذكرتها لأي تحذير ستُحذف معها.",
  },
  notValidated: { en: "Validate before submitting.", ar: "تحقّق قبل الإرسال." },
  staleRun: {
    en: "A line changed since the last check. Validate again before submitting.",
    ar: "تم تعديل أحد الأسطر بعد آخر تحقق. يرجى التحقق مرة أخرى قبل الإرسال.",
  },
  noDiagnosis: {
    en: "No diagnosis is recorded on this encounter, so the indication check cannot run.",
    ar: "لا يوجد تشخيص مسجل في هذه الزيارة، لذلك يتعذّر إجراء التحقق من دواعي الاستعمال.",
  },
  blocked: {
    en: "A benefit rule refuses a line. It cannot be submitted.",
    ar: "إحدى قواعد التغطية ترفض أحد الأسطر. لا يمكن إرساله.",
  },
  unacknowledged: {
    en: "Every warning needs a reason before you can submit.",
    ar: "يجب ذكر سبب لكل تحذير قبل الإرسال.",
  },
  reason: { en: "Reason to proceed", ar: "سبب المتابعة" },
  reasonPlaceholder: { en: "Why proceed?", ar: "لماذا المتابعة؟" },
  checksFor: { en: "Checks —", ar: "الفحوصات —" },
  sources: { en: "Sources", ar: "المصادر" },
  viewChecks: { en: "Checks for", ar: "فحوصات" },
  needDrug: { en: "Choose a medicine for every line.", ar: "اختر دواءً لكل سطر." },
  submitted: { en: "Prescription submitted.", ar: "تم إرسال الوصفة." },
  submitFailed: { en: "Submission was refused.", ar: "تم رفض الإرسال." },
  findings: { en: "Checks", ar: "الفحوصات" },
};

const KIND_LABEL: Record<CheckKind, { en: string; ar: string }> = {
  Indication: { en: "Indication", ar: "دواعي الاستعمال" },
  Interaction: { en: "Interaction", ar: "التداخلات الدوائية" },
  Allergy: { en: "Allergy", ar: "الحساسية" },
  DoseDuration: { en: "Dose & duration", ar: "الجرعة والمدة" },
  Benefit: { en: "Coverage", ar: "التغطية" },
  Duplication: { en: "Duplicate therapy", ar: "ازدواج علاجي" },
  Contraindication: { en: "Contraindication", ar: "موانع الاستعمال" },
};

/**
 * The persisted draft's shape, held to the same contracts the API is.
 *
 * Restored bytes are untrusted: they may have been written by an older bundle, or edited by hand. A draft
 * that does not parse is discarded rather than repaired — a composer half-populated from an unrecognised
 * shape is worse than an empty one, because it looks like something a doctor composed.
 */
const RX_DRAFT = z.object({
  lines: z.array(zPrescriptionDraftLine),
  result: zValidationResult.nullable(),
  validatedFingerprint: z.string().nullable(),
  acknowledgements: z.array(zLineAcknowledgement),
});
type RxDraft = z.infer<typeof RX_DRAFT>;

function emptyDraft(): RxDraft {
  return { lines: [newLine()], result: null, validatedFingerprint: null, acknowledgements: [] };
}

/**
 * "Nothing has been composed here" — one definition, used for three things that must agree.
 *
 * It decides what the draft store keeps, whether Discard has anything to discard, and — through the store —
 * whether the encounter screen will let the visit be closed. Three separate predicates would drift, and the
 * way they would drift is a gate that reports clean over a composer that is not.
 */
function isEmptyDraft(d: RxDraft): boolean {
  return d.result === null && d.lines.length === 1 && d.lines[0].drug === null;
}

function newLine(): PrescriptionDraftLine {
  return { lineId: crypto.randomUUID(), drug: null, dose: "", durationDays: null, quantity: 1 };
}

/** A stable fingerprint of the composed lines — changing any of it invalidates the last validation run. */
function fingerprint(lines: PrescriptionDraftLine[]): string {
  return lines.map((l) => `${l.lineId}|${l.drug?.drugId ?? ""}|${l.dose}|${l.durationDays ?? ""}|${l.quantity}`).join(";");
}

/**
 * The prescribing workspace (phase 26.5, doc 43 §6).
 *
 * Replaces a modal with four plain text inputs and hard-coded defaults, which sent the ATC code string
 * where the API expects a drug uuid — a path that could not work against real data at all.
 *
 * <b>What gates submission.</b> Not the warning: the ACKNOWLEDGEMENT. A prescriber may proceed past any
 * clinical warning by recording a reason, because blocking care on automated advice of uncertain provenance
 * would be the greater harm (doc 43 D1). What they may not do is proceed silently. A benefit refusal is
 * different — it is a factual statement about a policy, and it blocks.
 *
 * The validation result here is ADVISORY. The server re-runs the whole thing on submit and ignores whatever
 * this screen concluded, so nothing below is a security control.
 */
export function PrescribingWorkspace({
  encounterId,
  diagnosisIcdCodes,
  onDone,
}: {
  encounterId: string;
  /**
   * The encounter's staged diagnoses. The modal this replaces received only an encounter id, so the
   * indication check had nothing to compare against. An EMPTY list is meaningful and is surfaced: the check
   * reports "no diagnosis recorded" rather than passing.
   */
  diagnosisIcdCodes: string[];
  onDone?: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();

  /**
   * The composer's whole state, kept across a reload.
   *
   * One object rather than four `useState`s, because the four are only meaningful TOGETHER: a restored
   * `result` without its `validatedFingerprint` reads as a check that was never run against these lines, and
   * lines without their acknowledgements read as warnings nobody answered. Persisting them separately would
   * make a torn read — three of four restored — a state the screen can actually reach.
   */
  const [draft, setDraft] = useDraft(
    `rx:${encounterId}`,
    RX_DRAFT,
    // An untouched composer is what a fresh workspace already shows, so it is not worth storing — and
    // clearing on the way back to empty means "cleared" is a real clear rather than a stored emptiness.
    emptyDraft,
    isEmptyDraft,
  );
  const { lines, result, validatedFingerprint, acknowledgements } = draft;

  // Field-shaped setters over the single object, so every call site below reads exactly as it did when these
  // were four separate states — including the updater form, which is what keeps them free of stale closures.
  const setLines = (u: SetStateAction<PrescriptionDraftLine[]>) =>
    setDraft((d) => ({ ...d, lines: typeof u === "function" ? u(d.lines) : u }));
  const setAcknowledgements = (u: SetStateAction<LineAcknowledgement[]>) =>
    setDraft((d) => ({ ...d, acknowledgements: typeof u === "function" ? u(d.acknowledgements) : u }));
  const setChecked = (r: ValidationResult | null, fingerprint: string | null) =>
    setDraft((d) => ({ ...d, result: r, validatedFingerprint: fingerprint }));

  const [busy, setBusy] = useState(false);
  /** Which line's checks are open. One at a time — they are read, not compared. */
  const [openChecks, setOpenChecks] = useState<string | null>(null);
  const [discarding, setDiscarding] = useState(false);
  const composed = !isEmptyDraft(draft);

  const current = fingerprint(lines);
  const stale = result !== null && validatedFingerprint !== current;
  const allLinesHaveDrugs = lines.every((l) => l.drug !== null);

  /*
   * WHAT GATES SUBMISSION, AFTER 28.4.
   *
   * `requiresAcknowledgement` is computed on the SERVER and is now severity-aware: only Contraindicated and
   * Major set it, plus any finding with no severity at all (a manufacturer label states an effect rather
   * than a rank, so an ungraded finding still interrupts). Moderate and Minor render beside the line and
   * never stand between the prescriber and Submit.
   *
   * This screen deliberately does NOT re-derive that from the severity string. Two implementations of the
   * gating rule would drift, and the way they would drift is a screen that lets a line through which the
   * server then refuses — or worse, one that blocks a line the server would have accepted.
   */
  const warnings = useMemo(
    () => (result?.findings ?? []).filter((f) => f.requiresAcknowledgement),
    [result],
  );
  const unacknowledged = warnings.filter(
    (f) => !acknowledgements.some((a) => a.lineId === f.lineId && a.findingKind === f.kind && a.reason.trim().length > 0),
  );
  const blocked = (result?.findings ?? []).filter((f) => f.isBlocking);

  const canSubmit =
    allLinesHaveDrugs && result !== null && !stale && unacknowledged.length === 0 && blocked.length === 0 && !busy;

  function patch(lineId: string, change: Partial<PrescriptionDraftLine>) {
    setLines((prev) => prev.map((l) => (l.lineId === lineId ? { ...l, ...change } : l)));
    // An acknowledgement belongs to the finding it was given for. Editing the line re-derives the findings,
    // so carrying the reason forward would attach a justification to something the doctor never saw.
    setAcknowledgements((prev) => prev.filter((a) => a.lineId !== lineId));
  }

  async function validate() {
    setBusy(true);
    try {
      const r = await api.validatePrescription({ encounterId, lines, diagnosisIcdCodes });
      setChecked(r, fingerprint(lines));
    } catch {
      setChecked(null, null);
      toast(t({ en: "Validation could not run.", ar: "تعذّر إجراء التحقق." }), "bad");
    } finally {
      setBusy(false);
    }
  }

  async function submit() {
    setBusy(true);
    try {
      await api.submitPrescription({ encounterId, lines, diagnosisIcdCodes, acknowledgements });
      toast(t(S.submitted), "ok");
      // The composer goes back to empty. It used to keep the submitted lines on screen, with their validated
      // status chips still green — so the only evidence of success was a toast that had already gone, and the
      // screen still showed an unsent-looking draft. A doctor reading that concludes it did not save, and the
      // reasonable next move is to press Submit again.
      //
      // The findings and acknowledgements go WITH the lines. They were derived from a prescription that has
      // now been written; keeping them would attach one prescription's warnings to the next one's draft.
      // Empty again — which is also how it leaves the draft store, since an empty composer is the one thing
      // `useDraft` does not keep. A sent prescription is no longer a draft, and leaving one behind would let a
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

  /** The worst severity on a line, for the chip beside its status. Null when nothing on it is graded. */
  function severityFor(lineId: string): ClinicalSeverity | null {
    if (!result || stale) return null;
    return worstSeverity(result.findings.filter((f) => f.lineId === lineId));
  }

  return (
    <div className="rx-workspace stack">
      {diagnosisIcdCodes.length === 0 && (
        <InlineAlert tone="info">{t(S.noDiagnosis)}</InlineAlert>
      )}

      <ul className="rx-lines">
        {lines.map((line) => (
          <li key={line.lineId} className="rx-line">
            <div className="rx-line-main">
              <DrugCombobox
                value={line.drug}
                onChange={(drug) => patch(line.lineId, { drug })}
                disabled={busy}
              />
              <div className="rx-line-fields">
                <label className="rx-field">
                  <span className="rx-field-label">{t(S.dose)}</span>
                  <input
                    className="rx-field-input"
                    value={line.dose}
                    disabled={busy}
                    onChange={(e) => patch(line.lineId, { dose: e.currentTarget.value })}
                  />
                </label>
                <label className="rx-field">
                  <span className="rx-field-label">{t(S.duration)}</span>
                  <input
                    className="rx-field-input"
                    type="number"
                    min={1}
                    value={line.durationDays ?? ""}
                    disabled={busy}
                    onChange={(e) =>
                      patch(line.lineId, {
                        durationDays: e.currentTarget.value === "" ? null : Number(e.currentTarget.value),
                      })
                    }
                  />
                </label>
                <label className="rx-field">
                  <span className="rx-field-label">{t(S.quantity)}</span>
                  <input
                    className="rx-field-input"
                    type="number"
                    min={1}
                    value={line.quantity}
                    disabled={busy}
                    onChange={(e) => patch(line.lineId, { quantity: Number(e.currentTarget.value) })}
                  />
                </label>
                <div className="rx-field">
                  <span className="rx-field-label">{t(S.status)}</span>
                  {/*
                    Severity beside the state, not inside it. The state chip says whether the check produced
                    an ANSWER; this says how much the answer matters — and before phase 28 the second was a
                    word interpolated into a message string, invisible until the modal was opened. The
                    prescriber must be able to tell a contraindicated line from a minor one while scanning
                    the column (doc 44 §2).
                  */}
                  {severityFor(line.lineId) && <SeverityChip severity={severityFor(line.lineId)!} />}
                  <LineStatusChip
                    state={stateFor(line.lineId)}
                    detailLabel={line.drug ? `${t(S.viewChecks)} ${t(line.drug.tradeName)}` : undefined}
                    // Only a run that produced findings has anything to open.
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
              <LineChecksModal
                open={openChecks === line.lineId}
                onOpenChange={(v) => setOpenChecks(v ? line.lineId : null)}
                drugName={line.drug ? t(line.drug.tradeName) : ""}
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
      {result && !stale && unacknowledged.length > 0 && (
        <InlineAlert tone="warn">{t(S.unacknowledged)}</InlineAlert>
      )}
      {!allLinesHaveDrugs && <InlineAlert tone="info">{t(S.needDrug)}</InlineAlert>}

      <div className="rx-actions">
        {/*
          The other way out, and the reason the encounter screen can insist on one.

          Closing a visit is refused while anything sits composed-but-unsent here, which is only a fair rule
          if "I have changed my mind about this" is an action the screen offers. It was not: the composer
          could be filled and validated, and the only control that emptied it was a successful submit.
        */}
        <Button variant="ghost" disabled={!composed || busy} onClick={() => setDiscarding(true)}>
          {t(S.discard)}
        </Button>
        <Button variant="secondary" loading={busy} disabled={!allLinesHaveDrugs} onClick={() => void validate()}>
          {t(S.validate)}
        </Button>
        <Button variant="primary" disabled={!canSubmit} onClick={() => void submit()}>
          {t(S.submit)}
        </Button>
      </div>

      {/*
        Confirmed, because this throws away clinical work that a reload is now guaranteed to have preserved.
        Building persistence to save a composed prescription and then letting one mis-click wipe it would be
        the two halves of the same feature disagreeing.
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

/**
 * The five checks for one line, on demand.
 *
 * <b>Why a dialog rather than an always-open panel.</b> Five checks × five lines is twenty-five rows of
 * prose under a form the prescriber is still filling in, and almost all of it says "nothing to report". The
 * summary state — the one cue the whole design turns on — stays on the row itself; the reasoning behind it
 * is one click away, which is the right weight for something read once and only when it matters.
 *
 * <b>Provenance is kept, but collapsed.</b> Doc 43 §1 rule 2 requires every advisory to carry its source:
 * a warning a clinician cannot attribute is one they are right to ignore. It is no longer printed under
 * every line — that was three lines of dataset names per check — but it is one disclosure away, and the
 * caveat that matters most (the indication map is clinical judgement, not a published dataset) travels with
 * it.
 */
function LineChecksModal({
  open,
  onOpenChange,
  drugName,
  findings,
  acknowledgements,
  onAcknowledge,
}: {
  open: boolean;
  onOpenChange: (v: boolean) => void;
  drugName: string;
  findings: Finding[];
  acknowledgements: LineAcknowledgement[];
  onAcknowledge: (finding: Finding, reason: string) => void;
}) {
  const t = useLoc();

  return (
    <Modal open={open} onOpenChange={onOpenChange} title={`${t(S.checksFor)} ${drugName}`}>
      <ul className="rx-checks">
        {groupBySeverityThenKind(findings).map(([kind, group]) => {
          const ack = acknowledgements.find((a) => a.lineId === group[0].lineId && a.findingKind === kind);
          const needsReason = group.find((f) => f.requiresAcknowledgement);
          return (
            <li key={kind} className="rx-check">
              <span className="rx-check-kind">{t(KIND_LABEL[kind])}</span>
              {worstSeverity(group) && <SeverityChip severity={worstSeverity(group)!} />}
              {/* The worst of the group. A check answered by two sources has one summary, and it is the more
                  serious of the two — an "unavailable" beside a "nothing found" is still unavailable. */}
              <LineStatusChip state={worst(group)} />
              {group.map((f, i) => (
                <p className="rx-check-message" key={`${f.state}-${i}`}>
                  {shorten(t({ en: f.messageEn, ar: f.messageAr }), f.state, t)}
                  {f.referenceText && (
                    <q className="rx-check-quote" dir="ltr" lang="en">{f.referenceText}</q>
                  )}
                </p>
              ))}
              {/* ONE reason per check, not per finding. Submission is gated on (line, kind) server-side, and
                  two boxes bound to the same record would silently overwrite each other as they were typed. */}
              {needsReason && (
                <label className="rx-check-ack">
                  <span className="rx-field-label">{t(S.reason)}</span>
                  <input
                    className="rx-field-input"
                    placeholder={t(S.reasonPlaceholder)}
                    value={ack?.reason ?? ""}
                    onChange={(e) => onAcknowledge(needsReason, e.currentTarget.value)}
                  />
                </label>
              )}
            </li>
          );
        })}
      </ul>

      {/* Collapsed, not removed. See the note on this component. */}
      <details className="rx-sources">
        <summary>{t(S.sources)}</summary>
        <ul className="rx-sources-list">
          {findings
            .filter((f) => f.sourceName)
            .map((f, i) => (
              // Keyed by index as well as kind: interactions are answered by two independent sources, and
              // both of their names and caveats have to be listed — that is the point of this disclosure.
              <li key={`${f.kind}-${i}`}>
                <span className="rx-check-kind">{t(KIND_LABEL[f.kind])}</span>
                {" — "}
                {f.sourceName}
                {f.sourceVersion ? ` (${f.sourceVersion})` : ""}
                {f.caveat && <span className="rx-finding-caveat"> {f.caveat}</span>}
              </li>
            ))}
        </ul>
      </details>
    </Modal>
  );
}

/**
 * Groups a line's findings by check, preserving the order the checks are presented in.
 *
 * <p>Interactions are answered by two independent sources — the curated pair list and manufacturer label
 * text — so a line legitimately carries more than one finding of the same kind. Rendering them as separate
 * rows would show the prescriber "Interactions" twice with different verdicts and no way to tell which is
 * which, and would put two acknowledgement boxes on screen bound to the same stored reason.</p>
 */
function groupByKind(findings: Finding[]): [CheckKind, Finding[]][] {
  const groups = new Map<CheckKind, Finding[]>();
  for (const f of findings) groups.set(f.kind, [...(groups.get(f.kind) ?? []), f]);
  return [...groups.entries()];
}

/**
 * The same grouping, ordered by SEVERITY first (28.4, doc 44 §2).
 *
 * A modal that lists checks in a fixed order buries a contraindicated interaction under an indication note
 * that says "not checked". Ordering by what matters means the prescriber reads the thing they have to act on
 * first — and an ungraded finding sorts with Major rather than last, because ungraded is not harmless.
 */
function groupBySeverityThenKind(findings: Finding[]): [CheckKind, Finding[]][] {
  return groupByKind(findings).sort((a, b) => rank(b[1]) - rank(a[1]));
}

function rank(group: Finding[]): number {
  const s = worstSeverity(group);
  if (s) return SEVERITY_RANK[s];
  // Ungraded but interrupting — a manufacturer-label interaction — sorts with Major. Ungraded and not
  // interrupting sorts below everything graded.
  return group.some((f) => f.requiresAcknowledgement) ? SEVERITY_RANK.Major : -1;
}

/** Blocked > Unavailable > Warning > NotChecked > Ok — the same precedence the server rolls up with. */
function worst(findings: Finding[]): CheckState {
  for (const rank of ["Blocked", "Unavailable", "Warning", "NotChecked"] as const) {
    if (findings.some((f) => f.state === rank)) return rank;
  }
  return "Ok";
}

/**
 * Drops a prefix the status chip beside it already states.
 *
 * The engine's messages are written to stand alone — they are stored with the prescription and read later
 * without a chip next to them — so "Not checked — no allergies are recorded" is right in the record and
 * redundant on screen. Only the duplicated lead is removed; the reason itself is never touched.
 */
function shorten(message: string, state: CheckState, t: (l: Localized) => string): string {
  const lead = `${t(STATE_WORD[state])} — `;
  return message.startsWith(lead) ? message.slice(lead.length) : message;
}

const STATE_WORD: Record<CheckState, Localized> = {
  Ok: { en: "OK", ar: "سليم" },
  Warning: { en: "Warning", ar: "تحذير" },
  Blocked: { en: "Blocked", ar: "محظور" },
  NotChecked: { en: "Not checked", ar: "لم يتم التحقق" },
  Unavailable: { en: "Check unavailable", ar: "تعذّر التحقق" },
};

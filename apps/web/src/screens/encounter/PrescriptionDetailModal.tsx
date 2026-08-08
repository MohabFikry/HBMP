import { useCallback, useEffect, useState } from "react";
import { Button, InlineAlert, Modal, StatusChip } from "@mersal/design-system";
import type { AmendReasonOption, RxRow, RxRowLine } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { AmendLineDialog } from "../AmendLineDialog";
import { ServiceHistoryModal } from "../ServiceHistoryModal";
import type { AmendAction, LineLockedReason } from "../AmendLineDialog";
import { useFormat } from "../../i18n/useFormat";
import { useLoc } from "../_shared";

/**
 * The prescription as it was written, read back by its author.
 *
 * <b>Why a modal and not a route.</b> A doctor opens this mid-consultation to check one thing — did I already
 * give them this, at what dose — and then goes back to the note they were writing. A route would take the
 * encounter off screen and lose the draft's place in it.
 *
 * <b>It costs no fetch.</b> Every field here already arrived with the row: `/prescriptions/mine` has always
 * returned the full lines, and the list was discarding them at `lineCount`. That matters beyond latency —
 * emr and pharmacy audit each PHI read, so a per-open request would enter the patient's audit trail once per
 * glance and make "who read this record, and how often" harder to answer than the reading justified.
 *
 * <b>Nothing here is invented.</b> The three sig fields, the drug name and the prescriber are all nullable on
 * the wire, and every one of them is rendered as a stated absence rather than filled in. The dispensing queue
 * printed the word "Medication" into that gap for months, which is not a placeholder: the name of a field
 * where its value belongs reads as data, and no one downstream can tell it apart from one.
 */

const S = {
  title: { en: "Prescription", ar: "الوصفة" },
  status: { en: "Status", ar: "الحالة" },
  prescriber: { en: "Prescriber", ar: "الطبيب الواصف" },
  prescriberMissing: { en: "Not recorded", ar: "غير مسجّل" },
  written: { en: "Written on", ar: "تاريخ الكتابة" },
  validUntil: { en: "Valid until", ar: "صالحة حتى" },
  noExpiry: { en: "No expiry set", ar: "بدون تاريخ انتهاء" },
  medications: { en: "Medications", ar: "الأدوية" },
  drugMissing: { en: "Medication not recorded", ar: "الدواء غير مسجّل" },
  drugMissingHint: {
    en: "This line was written before the medication name was stored with the prescription, so only the "
      + "catalogue reference remains. The dispensing pharmacy resolves the product from that reference.",
    ar: "كُتب هذا السطر قبل حفظ اسم الدواء مع الوصفة، لذلك لم يبقَ سوى المرجع في الكتالوج. تحدّد الصيدلية "
      + "المنتج من هذا المرجع.",
  },
  dose: { en: "Dose", ar: "الجرعة" },
  route: { en: "Route", ar: "طريق الإعطاء" },
  frequency: { en: "Frequency", ar: "التكرار" },
  // 31.5 — the course length, which the record has always held and this dialog never showed. A prescriber
  // reading back their own script cannot check a quantity without it.
  duration: { en: "Duration", ar: "المدة" },
  days: { en: "{n} day(s)", ar: "{n} يوم" },
  quantity: { en: "Quantity prescribed", ar: "الكمية الموصوفة" },
  dispensed: { en: "Dispensed to date", ar: "المصروف حتى الآن" },
  refills: { en: "Refills allowed", ar: "مرات الصرف المسموح بها" },
  // 29.4 (design 45 §4) — "has this patient had this medicine before, and what happened?"
  history: { en: "History", ar: "السجل" },
  viewHistory: { en: "Previous prescriptions of this medicine", ar: "الوصفات السابقة لهذا الدواء" },
  noLines: { en: "This prescription has no lines.", ar: "لا تحتوي هذه الوصفة على أسطر." },

  // ---- 30.6 amend / cancel (design 46 §1-§3, §10) — worded identically to the order twin, because they
  // are the same act on two record kinds and a prescriber who learns one must not have to relearn the other.
  withdraw: { en: "Withdraw", ar: "سحب" },
  amend: { en: "Amend", ar: "تعديل" },
  lockedDispensed: { en: "Dispensed — cannot be changed", ar: "تم صرفه — لا يمكن تغييره" },
  lockedWithdrawn: { en: "Withdrawn", ar: "مسحوب" },
  lockedAmended: { en: "Replaced by a newer version", ar: "استُبدل بنسخة أحدث" },
  lockedExpired: { en: "The prescription has expired", ar: "انتهت صلاحية الوصفة" },
  failed: {
    en: "That change could not be applied. Nothing was altered — reopen the prescription to see its current "
      + "state.",
    ar: "تعذّر تطبيق التغيير. لم يُعدَّل شيء — أعد فتح الوصفة لعرض حالتها الحالية.",
  },
};

/** An em dash, not a blank: a missing sig field must read as "not recorded", never as a rendering fault. */
const DASH = "—";

/**
 * Why this line cannot be changed, or null when it can. The medication twin of the order modal's `lockOf`,
 * and it errs the same way: toward OFFERING the control, because a wrongly-enabled button produces a
 * specific 409 the prescriber can read and a wrongly-hidden one produces a prescriber who believes the
 * feature does not exist.
 */
function lockOf(rx: RxRow, line: RxRowLine): LineLockedReason | null {
  const status = line.status.label.en;
  if (status === "Dispensed") return { what: "Dispensed" };
  if (status === "Cancelled" || status === "Withdrawn") return { what: "Cancelled" };
  if (status === "Superseded") return { what: "Superseded" };
  if (rx.expiresAt && new Date(rx.expiresAt) <= new Date()) return { what: "Expired" };
  return null;
}

export function PrescriptionDetailModal({
  rx,
  onOpenChange,
  onChanged,
}: {
  /** The prescription to show, or null when the dialog is closed. */
  rx: RxRow | null;
  onOpenChange: (open: boolean) => void;
  /** Called after a line is withdrawn or amended, so the list behind can refetch. */
  onChanged?: () => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const api = useApi();

  const [acting, setActing] = useState<{ line: RxRowLine; action: AmendAction } | null>(null);
  /** 29.4 — which medicine's history is open. One at a time; it is read, not compared. */
  const [historyFor, setHistoryFor] = useState<RxRowLine | null>(null);
  const [reasons, setReasons] = useState<AmendReasonOption[]>([]);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    if (!rx) return;
    let live = true;
    // "prescription" scope, so the picker offers Dose correction and Drug unavailable — the two reasons a
    // lab order must never be given.
    // Guarded, and the guard is not defensive clutter: this list is an ENRICHMENT of a dialog that must
    // open regardless. A throw here — an older client, a transport failure — used to take down the whole
    // encounter screen, which is a catastrophic response to a picker that could not be filled. An empty
    // picker is honest and safe: the dialog already refuses to submit without a reason, so the worst case
    // is a doctor who cannot withdraw, not one who withdraws without recording why.
    Promise.resolve(api.amendmentReasons?.("prescription") ?? [])
      .then((r) => { if (live) setReasons(r); })
      .catch(() => { if (live) setReasons([]); });
    return () => { live = false; };
  }, [api, rx]);

  const confirm = useCallback(
    async (input: { reasonCode: string; reasonText?: string; quantity?: number }) => {
      if (!rx || !acting) return;
      setFailed(false);
      try {
        // Withdraw only — 31.2 moved amending to the transaction row, so there is no longer a control here
        // that can raise any other action. An `else` branch calling `amendPrescriptionLine` would be code
        // nothing reaches, which is the kind that rots without anyone noticing.
        await api.cancelPrescriptionLine(rx.id, acting.line.id, input.reasonCode, input.reasonText);
        setActing(null);
        onChanged?.();
        onOpenChange(false);
      } catch {
        setFailed(true);
        setActing(null);
      }
    },
    [api, rx, acting, onChanged, onOpenChange],
  );

  if (!rx) return null;

  return (
    <Modal
      open={rx !== null}
      onOpenChange={onOpenChange}
      // The Rx number IS the title. It is what the pharmacy quotes back and what is printed on the patient's
      // copy, so it is also the thing the reader is matching against while this is open.
      title={`${t(S.title)} ${rx.rxNo}`}
      wide
    >
      <dl className="rxv-meta">
        <dt>{t(S.status)}</dt>
        <dd><StatusChip kind={rx.status.kind} label={t(rx.status.label)} /></dd>

        <dt>{t(S.prescriber)}</dt>
        <dd>
          {rx.prescriber
            ? t(rx.prescriber)
            : <span className="rxv-missing">{t(S.prescriberMissing)}</span>}
        </dd>

        <dt>{t(S.written)}</dt>
        <dd className="tnum">{fmt.dateTime(rx.submittedAt)}</dd>

        <dt>{t(S.validUntil)}</dt>
        <dd className="tnum">
          {rx.expiresAt
            ? fmt.dateTime(rx.expiresAt)
            : <span className="rxv-missing">{t(S.noExpiry)}</span>}
        </dd>
      </dl>

      <h3 className="rxv-h">
        {t(S.medications)} <span className="tnum rxv-count">({rx.lines.length})</span>
      </h3>

      {rx.lines.length === 0 ? (
        <p className="muted">{t(S.noLines)}</p>
      ) : (
        // An ordered list, because the ORDER is part of what was written — a prescriber reading their own
        // prescription back counts down it, and "the second one" has to mean the same thing here as on the
        // sheet the patient is holding.
        <ol className="rxv-lines">
          {rx.lines.map((line, i) => (
            <RxLineCard
              key={line.id} line={line} index={i + 1} t={t} fmt={fmt}
              lock={lockOf(rx, line)}
              onAct={(action) => { setFailed(false); setActing({ line, action }); }}
              // 29.4 — "has this patient had this medicine before?" (design 45 §4). THE shared modal and
              // THE one endpoint, opened from a prescription line exactly as it is from a lab line — the
              // half of design 45 §4 that named prescriptions first and reached them last.
              onHistory={line.drugId ? () => setHistoryFor(line) : undefined}
            />
          ))}
        </ol>
      )}

      {failed && <InlineAlert tone="bad">{t(S.failed)}</InlineAlert>}

      {/*
        29.4 — THE shared service-history modal (design 45 §4). Design 45 §4 names prescriptions FIRST in
        "every service line — prescription, lab, radiology, OP procedure", and this is where that reaches
        them. One component and one endpoint: not a prescription-shaped copy of the investigation one.
      */}
      {historyFor?.drugId && (
        <ServiceHistoryModal
          beneficiaryId={rx.beneficiary.id}
          serviceType="Prescription"
          code={historyFor.drugId}
          label={historyFor.drug ? t(historyFor.drug) : undefined}
          onClose={() => setHistoryFor(null)}
        />
      )}

      <AmendLineDialog
        open={acting !== null}
        action={acting?.action ?? "cancel"}
        lineLabel={acting ? (acting.line.drug ? t(acting.line.drug) : t(S.drugMissing)) : ""}
        currentQuantity={acting?.line.quantityPrescribed}
        reasons={reasons}
        onCancel={() => setActing(null)}
        onConfirm={confirm}
      />
    </Modal>
  );
}

function RxLineCard({
  line,
  index,
  t,
  fmt,
  lock,
  onAct,
  onHistory,
}: {
  line: RxRowLine;
  index: number;
  t: (l: { en: string; ar: string }) => string;
  fmt: ReturnType<typeof useFormat>;
  lock: LineLockedReason | null;
  onAct: (action: AmendAction) => void;
  /** 29.4 — open this medicine's history. Undefined when the row carries no drug id to ask about. */
  onHistory?: () => void;
}) {
  const lockedWord =
    lock?.what === "Dispensed" ? S.lockedDispensed
    : lock?.what === "Cancelled" ? S.lockedWithdrawn
    : lock?.what === "Superseded" ? S.lockedAmended
    : S.lockedExpired;
  return (
    <li className="rxv-line" data-recorded={line.drug ? undefined : "no"}>
      <div className="rxv-line-h">
        <span className="rxv-line-n tnum" aria-hidden="true">{index}</span>
        {line.drug ? (
          <span className="rxv-drug">{t(line.drug)}</span>
        ) : (
          // Dashed and hollow, the treatment this app already gives every unanswered state. It is a
          // statement about the RECORD, not about the medicine, and it has to look like one.
          <span className="rxv-drug rxv-missing" title={t(S.drugMissingHint)}>
            <span className="rxv-missing-glyph" aria-hidden="true">○</span>
            {t(S.drugMissing)}
          </span>
        )}
        <StatusChip kind={line.status.kind} label={t(line.status.label)} />
        {/* 29.4 — the SAME icon, the same modal and the same endpoint the investigation tabs use. */}
        {onHistory && (
          <Button
            variant="ghost"
            size="sm"
            aria-label={`${t(S.viewHistory)} — ${line.drug ? t(line.drug) : t(S.drugMissing)}`}
            onClick={onHistory}
          >
            {t(S.history)}
          </Button>
        )}
      </div>
      <dl className="rxv-grid">
        <div className="rxv-cell">
          <dt>{t(S.dose)}</dt>
          <dd>{line.dose ?? DASH}</dd>
        </div>
        <div className="rxv-cell">
          <dt>{t(S.route)}</dt>
          <dd>{line.route ?? DASH}</dd>
        </div>
        <div className="rxv-cell">
          <dt>{t(S.frequency)}</dt>
          <dd>{line.frequency ?? DASH}</dd>
        </div>
        <div className="rxv-cell">
          <dt>{t(S.duration)}</dt>
          {/* NULL is "the prescriber recorded none", and it says so in words. A missing duration and a
              one-day course look identical in an empty cell, and only one of them is worth a phone call. */}
          <dd className="tnum">
            {line.durationDays ? t(S.days).replace("{n}", String(line.durationDays)) : DASH}
          </dd>
        </div>
        <div className="rxv-cell">
          <dt>{t(S.quantity)}</dt>
          {/* 31.3 — with its unit. A prescription's quantity is a box count wherever the catalogue records
              what a box holds, and the dose total where it does not; the figure alone does not say which. */}
          <dd className="tnum">
            {fmt.number(line.quantityPrescribed)}{line.quantityUnit ? ` ${line.quantityUnit}` : ""}
          </dd>
        </div>
        {/*
          Kept apart from the prescribed quantity and never subtracted from it. This dialog answers "what did
          I write"; the running total is the pharmacy's answer to a different question, and folding the two
          into one "remaining" figure would make the original unreadable from here.
        */}
        <div className="rxv-cell">
          <dt>{t(S.dispensed)}</dt>
          <dd className="tnum">{fmt.number(line.quantityDispensed)}</dd>
        </div>
        <div className="rxv-cell">
          <dt>{t(S.refills)}</dt>
          <dd className="tnum">{fmt.number(line.refillsAllowed)}</dd>
        </div>
      </dl>

      {/*
        31.2 — AMEND IS NOT OFFERED HERE.

        This dialog answers "what did I write"; amending is a different act, and it now lives on the
        transaction row where the doctor can reach it without opening the record first. Two entry points to
        one act meant two dialogs to keep in step, and the per-line one could only ever change a quantity —
        so the way to remove a line from a prescription was to amend it to zero, which the write path
        refuses. Withdraw stays, because withdrawing THIS line is a genuinely per-line decision.

        Disabled, not hidden, with the reason beside it and tied to it for a screen reader (design 46 §10).
      */}
      <div className="rxv-line-actions">
        <Button
          variant="danger" size="sm" disabled={lock !== null} onClick={() => onAct("cancel")}
          aria-describedby={lock ? `rxlock-${line.id}` : undefined}
        >
          {t(S.withdraw)}
        </Button>
        {lock && <span id={`rxlock-${line.id}`} className="rxv-missing">{t(lockedWord)}</span>}
      </div>
    </li>
  );
}

import { Modal, StatusChip } from "@mersal/design-system";
import type { RxRow, RxRowLine } from "@mersal/contracts";
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
  quantity: { en: "Quantity prescribed", ar: "الكمية الموصوفة" },
  dispensed: { en: "Dispensed to date", ar: "المصروف حتى الآن" },
  refills: { en: "Refills allowed", ar: "مرات الصرف المسموح بها" },
  noLines: { en: "This prescription has no lines.", ar: "لا تحتوي هذه الوصفة على أسطر." },
};

/** An em dash, not a blank: a missing sig field must read as "not recorded", never as a rendering fault. */
const DASH = "—";

export function PrescriptionDetailModal({
  rx,
  onOpenChange,
}: {
  /** The prescription to show, or null when the dialog is closed. */
  rx: RxRow | null;
  onOpenChange: (open: boolean) => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
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
            <RxLineCard key={line.id} line={line} index={i + 1} t={t} fmt={fmt} />
          ))}
        </ol>
      )}
    </Modal>
  );
}

function RxLineCard({
  line,
  index,
  t,
  fmt,
}: {
  line: RxRowLine;
  index: number;
  t: (l: { en: string; ar: string }) => string;
  fmt: ReturnType<typeof useFormat>;
}) {
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
          <dt>{t(S.quantity)}</dt>
          <dd className="tnum">{fmt.number(line.quantityPrescribed)}</dd>
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
    </li>
  );
}

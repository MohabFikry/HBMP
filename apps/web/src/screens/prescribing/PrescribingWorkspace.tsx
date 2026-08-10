import { useEffect, useMemo, useState, type SetStateAction } from "react";
import { z } from "zod";
import { Button, Icon, InlineAlert, Modal, useToast } from "@mersal/design-system";
import type {
  CheckKind, CheckState, ClinicalSeverity, Finding, LineAcknowledgement, Localized,
  PrescriptionDraftLine, ValidationResult,
} from "@mersal/contracts";
import { SEVERITY_RANK } from "@mersal/contracts";
import { zLineAcknowledgement, zPrescriptionDraftLine, zPrescriptionKind, zValidationResult } from "@mersal/contracts";
import type { ChronicPreview, RefillFrequency } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useDraft } from "../draftStore";
import { useLoc } from "../_shared";
import { ServiceHistoryModal } from "../ServiceHistoryModal";
import { DrugCombobox } from "./DrugCombobox";
import { LineStatusChip, SeverityChip, worstSeverity } from "./LineStatusChip";

const S = {
  title: { en: "Prescribe", ar: "وصف دواء" },
  addLine: { en: "Add medicine", ar: "إضافة دواء" },
  removeLine: { en: "Remove", ar: "إزالة" },
  emptyLine: { en: "empty line", ar: "سطر فارغ" },
  // 29.4 (design 45 §4) — asked on the line being COMPOSED, which is the only moment the answer can still
  // change what is prescribed.
  lineHistory: { en: "Previous prescriptions of this medicine", ar: "الوصفات السابقة لهذا الدواء" },
  dose: { en: "Dose", ar: "الجرعة" },
  timesPerDay: { en: "Times per day", ar: "مرات يومياً" },
  duration: { en: "Duration (days)", ar: "المدة (أيام)" },
  // 31.2 — what the pharmacy counts out. Stated beside the units it came from, so the prescriber can check
  // the conversion rather than take it on trust.
  boxes: { en: "box", ar: "علبة" },
  boxesPlural: { en: "boxes", ar: "علب" },
  boxesUnknown: {
    en: "The catalogue does not record how much one box of this holds, so this is the total dose, not a "
      + "box count.",
    ar: "لا يسجّل الكتالوج سعة العبوة الواحدة من هذا المنتج، لذلك هذه هي الجرعة الإجمالية وليست عدد العلب.",
  },
  // 31.3 — the box's contents, said once beside the box count. The prescriber can overrule the number; they
  // cannot check it unless they are told what one box holds.
  perBox: { en: "per box", ar: "لكل علبة" },
  quantityNotChecked: {
    en: "The quantity to dispense could not be computed.",
    ar: "تعذّر حساب الكمية المطلوب صرفها.",
  },

  // ---- 31.4 — copying a prescription that has already been written ----
  cloned: {
    en: "Copied {n} medicine(s) from {ref}. Nothing has been prescribed yet — check the doses and submit.",
    ar: "تم نسخ {n} دواء من {ref}. لم يتم وصف أي شيء بعد — راجع الجرعات ثم أرسل.",
  },
  clonePartial: {
    en: "{n} medicine(s) could not be copied: the catalogue no longer offers them. Everything else was.",
    ar: "تعذّر نسخ {n} دواء: لم يعد الكتالوج يوفرها. تم نسخ الباقي.",
  },
  cloneEmpty: {
    en: "Nothing on {ref} could be copied: its items do not record which catalogue product they are.",
    ar: "لا يوجد ما يمكن نسخه من {ref}: بنودها لا تسجّل المنتج المقابل في الكتالوج.",
  },
  cloneFailed: {
    en: "That prescription could not be copied — the drug catalogue could not be reached. Nothing was added.",
    ar: "تعذّر نسخ الوصفة — لم يمكن الوصول إلى كتالوج الأدوية. لم يُضف أي شيء.",
  },

  // ---- 29.5 (design 45 §5) — acute / chronic ----
  kindLegend: { en: "Prescription type", ar: "نوع الوصفة" },
  acute: { en: "Acute", ar: "حادة" },
  chronic: { en: "Chronic", ar: "مزمنة" },
  acuteHint: { en: "One collection.", ar: "صرف واحد." },
  chronicHint: { en: "Collected in dated windows.", ar: "تُصرف على فترات محددة." },
  refillFrequency: { en: "Refill frequency", ar: "تكرار الصرف" },
  chooseFrequency: { en: "Choose a cadence…", ar: "اختر التكرار…" },
  treatmentDuration: { en: "Treatment duration (days)", ar: "مدة العلاج (أيام)" },
  durationFromLines: { en: "from the longest line below", ar: "من أطول سطر بالأسفل" },
  scheduleTitle: { en: "Collection schedule", ar: "جدول الصرف" },
  colWindow: { en: "Window", ar: "الفترة" },
  colDue: { en: "Due", ar: "الاستحقاق" },
  colFrom: { en: "Collectable from", ar: "يمكن الصرف من" },
  colUntil: { en: "Closes", ar: "ينتهي" },
  colAllocated: { en: "Quantity", ar: "الكمية" },
  scheduleTotal: { en: "Total", ar: "الإجمالي" },
  scheduleHint: {
    en: "The windows add up to the total exactly. A window not collected before it closes is forfeited.",
    ar: "مجموع الفترات يساوي الإجمالي تماماً. الفترة التي لا تُصرف قبل انتهائها تسقط.",
  },
  notChronic: {
    en: "A chronic prescription needs a duration of more than one month. A 14-day course is not chronic — "
      + "write it as acute.",
    ar: "الوصفة المزمنة تحتاج مدة أكثر من شهر. العلاج لمدة ١٤ يوماً ليس مزمناً — اكتبه كوصفة حادة.",
  },
  needFrequency: {
    en: "Choose a refill frequency. Without one the script has no windows and cannot be dispensed.",
    ar: "اختر تكرار الصرف. بدونه لا توجد فترات صرف ولا يمكن صرف الوصفة.",
  },
  scheduleUnavailable: {
    en: "The collection schedule could not be requested, so this prescription cannot be written as chronic "
      + "yet. Nothing has changed — it is worth trying again in a moment.",
    ar: "تعذّر طلب جدول الصرف، لذلك لا يمكن كتابة هذه الوصفة كمزمنة الآن. لم يتغيّر شيء — يمكن المحاولة "
      + "مرة أخرى بعد قليل.",
  },
  scheduleNotChecked: {
    en: "This medicine cannot be written as chronic: the drug catalogue does not record the pack facts its "
      + "refill quantities are computed from. Write it as acute, or ask for the catalogue to be completed.",
    ar: "لا يمكن كتابة هذا الدواء كوصفة مزمنة: لا يسجّل كتالوج الأدوية بيانات العبوة التي تُحسب منها كميات "
      + "الصرف. اكتبه كوصفة حادة، أو اطلب استكمال بيانات الكتالوج.",
  },
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
  // 29.6 (design 45 §6) — "Quantity", not "Pack size": what the prescriber is being told is how much will
  // be dispensed. The pack is the reason the answer can be absent, not the subject of the check.
  Quantity: { en: "Quantity", ar: "الكمية" },
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

  // ---- 29.5 (design 45 §5) — the script's own shape, not any one line's ----------------------------
  //
  // Held at the DRAFT level because "is this chronic?" is a property of the prescription: one authorisation
  // covers the whole script, and the windows are what it is dispensed in. A per-line answer would let one
  // prescription be two things at once, which the server's CHECK forbids and nothing on screen would show.
  kind: zPrescriptionKind.default("Acute"),
  refillFrequencyCode: z.string().nullable().default(null),
  // 31.1 — the script-level `durationDays` is GONE. It sat beside each line's own duration, so one fact had
  // two fields, and the schedule was computed from whichever the doctor filled in second. The script's
  // length is now DERIVED from the lines (see `scriptDurationDays`), which is the only place it was ever
  // really recorded.
});
type RxDraft = z.infer<typeof RX_DRAFT>;

function emptyDraft(): RxDraft {
  return {
    lines: [newLine()], result: null, validatedFingerprint: null, acknowledgements: [],
    // Acute is today's behaviour, unchanged, and the default everywhere — the schema's, the server's
    // column's, and this composer's.
    kind: "Acute", refillFrequencyCode: null,
  };
}

/**
 * "Nothing has been composed here" — one definition, used for three things that must agree.
 *
 * It decides what the draft store keeps, whether Discard has anything to discard, and — through the store —
 * whether the encounter screen will let the visit be closed. Three separate predicates would drift, and the
 * way they would drift is a gate that reports clean over a composer that is not.
 */
function isEmptyDraft(d: RxDraft): boolean {
  return d.result === null && d.lines.length === 1 && d.lines[0].drug === null
    // 29.5 — the script's own shape counts as composition. Choosing Chronic, a cadence or a duration is
    // work the doctor did, and without this it is silently discarded: the store does not persist an "empty"
    // draft, so a remount would put the toggle back to Acute with nothing said. The same omission would let
    // the encounter screen report the composer clean and close a visit over a half-written chronic script.
    && d.kind === "Acute" && d.refillFrequencyCode === null;
}

function newLine(): PrescriptionDraftLine {
  return {
    lineId: crypto.randomUUID(), drug: null, dose: "",
    doseAmount: null, timesPerDay: null, durationDays: null,
    // 31.3 — no unit until something has been computed. A new line's "1" counts nothing yet, and labelling
    // it with a guess would be the one place this screen invents a fact.
    quantity: 1, quantityUnit: "", quantityEdited: false,
  };
}

/**
 * The sig as it is STORED and read back — "1 Tablet x 3/day".
 *
 * <p>Derived from the numbers rather than typed alongside them. This string is what the pharmacist reads at
 * the counter and what is printed on the patient's copy; a free-text box beside a numeric dose is two
 * statements of one instruction, and they drift the first time somebody edits only one of them.</p>
 *
 * <p>Empty while the dose is unset — an absent instruction must not render as a formatted one.</p>
 */
/**
 * 31.3 — the unit this line is counted in, as a prescriber writes it: `tabs`, `caps`, `IU`, `ml`.
 *
 * <p>Empty while no medicine is chosen, and empty for the 838 catalogue rows whose unit cannot be derived —
 * a label reading "Dose" alone is honest, and a word invented to fill the gap would sit beside the field
 * reading as data.</p>
 *
 * <p>The short form comes from the SERVER, which owns the vocabulary. `prescribingUnit` — "Tablet" — is the
 * database's word and is kept for the stored sig; it is not what goes on a label.</p>
 */
function unitOf(line: PrescriptionDraftLine): string {
  return line.drug?.prescribingUnitShort ?? line.drug?.prescribingUnit ?? "";
}

function sigOf(line: PrescriptionDraftLine): string {
  if (line.doseAmount === null) return "";
  const unit = line.drug?.prescribingUnit ? ` ${line.drug.prescribingUnit}` : "";
  const times = line.timesPerDay ? ` x ${line.timesPerDay}/day` : "";
  return `${line.doseAmount}${unit}${times}`;
}

/** A stable fingerprint of the composed lines — changing any of it invalidates the last validation run. */
function fingerprint(lines: PrescriptionDraftLine[]): string {
  return lines
    .map((l) => `${l.lineId}|${l.drug?.drugId ?? ""}|${l.doseAmount ?? ""}|${l.timesPerDay ?? ""}`
      + `|${l.durationDays ?? ""}|${l.quantity}`)
    .join(";");
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
/**
 * 31.4 — a transaction to copy into this composer, as the row that offered it knows it.
 *
 * <p>Ids and numbers, not draft lines: the composer owns line identity, and a caller minting `lineId`s would
 * be a second place deciding what a line IS. The medicine itself is re-read from the catalogue here, so a
 * clone carries today's pack facts and price rather than a snapshot taken when the original was written.</p>
 */
export interface PrescriptionClone {
  /** What the doctor is copying, for the confirmation — "RX-2026-000312". */
  reference: string;
  items: {
    drugId: string;
    label: string;
    quantity: number;
    /** 31.3's unit, carried with its number — a bare "1" copied off a box count is the ambiguity that field
     *  exists to close, and it would be reintroduced by dropping it here. */
    quantityUnit: string | null;
    /** 31.5 — the numbers the original was written from. Null on a line written before they were kept. */
    doseAmount: number | null;
    timesPerDay: number | null;
    durationDays: number | null;
  }[];
}

export function PrescribingWorkspace({
  encounterId,
  beneficiaryId,
  diagnosisIcdCodes,
  clone,
  onCloneApplied,
  onDone,
}: {
  encounterId: string;
  /**
   * 29.4 — whose history the composer may ask about. Passed in rather than derived from the encounter,
   * because the service-history endpoint is keyed on the BENEFICIARY and an encounter id in that slot is the
   * exact substitution that made the 29.4 modal answer for nobody.
   */
  beneficiaryId: string;
  /**
   * The encounter's staged diagnoses. The modal this replaces received only an encounter id, so the
   * indication check had nothing to compare against. An EMPTY list is meaningful and is surfaced: the check
   * reports "no diagnosis recorded" rather than passing.
   */
  diagnosisIcdCodes: string[];
  /** 31.4 — set by a row's Clone action; consumed once and reported back through `onCloneApplied`. */
  clone?: PrescriptionClone | null;
  onCloneApplied?: () => void;
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
  const { lines, result, validatedFingerprint, acknowledgements, kind, refillFrequencyCode } = draft;

  // Field-shaped setters over the single object, so every call site below reads exactly as it did when these
  // were four separate states — including the updater form, which is what keeps them free of stale closures.
  const setLines = (u: SetStateAction<PrescriptionDraftLine[]>) =>
    setDraft((d) => ({ ...d, lines: typeof u === "function" ? u(d.lines) : u }));
  const setAcknowledgements = (u: SetStateAction<LineAcknowledgement[]>) =>
    setDraft((d) => ({ ...d, acknowledgements: typeof u === "function" ? u(d.acknowledgements) : u }));
  const setChecked = (r: ValidationResult | null, fingerprint: string | null) =>
    setDraft((d) => ({ ...d, result: r, validatedFingerprint: fingerprint }));

  const [busy, setBusy] = useState(false);

  /*
   * 31.4 — COPY AN EXISTING PRESCRIPTION INTO THIS COMPOSER.
   *
   * A repeat script is the commonest thing a returning patient needs, and writing one meant finding each
   * medicine in a catalogue of 22,653 again. The row's Clone action hands over the ids; the work here is
   * re-reading each one from the CATALOGUE rather than trusting the copy stored on the old line — a clone
   * should carry today's pack facts, price and availability, not last year's.
   *
   * THREE THINGS IT REFUSES TO DO.
   *
   * It does not discard what is already composed: the copied medicines are APPENDED, and the only line it
   * removes is a single empty placeholder, which is not work. A doctor who has half-written a script and
   * reaches for Clone must not watch it disappear.
   *
   * It does not silently drop a medicine the catalogue no longer offers — the count that failed is stated.
   * A short prescription that looks complete is worse than one that says it is short.
   *
   * What it carries of the ORIGINAL is what the record actually holds. Since 31.5 that includes the dose,
   * the frequency and the duration; before it, those three were sent at prescribing time, used by the
   * checks and discarded, so a copy arrived with the fields empty. A line written before 31.5 still does —
   * absent is carried through as absent rather than filled in by parsing the sig this app formatted.
   */
  useEffect(() => {
    if (!clone) return;
    let live = true;

    // A transaction whose every line predates the drug id — or which carries no lines at all — copies
    // NOTHING, and must say so. Returning quietly here would leave the request unconsumed and the doctor
    // watching a button that appears to do nothing.
    if (clone.items.length === 0) {
      toast(t(S.cloneEmpty).replace("{ref}", clone.reference), "bad");
      onCloneApplied?.();
      return;
    }

    (async () => {
      try {
        const resolved = await Promise.all(
          clone.items.map(async (item) => ({
            item,
            drug: await api.prescribableDrugById(item.drugId).catch(() => null),
          })),
        );
        if (!live) return;

        const usable = resolved.filter((r) => r.drug !== null);
        if (usable.length > 0) {
          setLines((prev) => [
            // An empty placeholder is not work, so it makes way. Anything else the doctor typed stays.
            ...prev.filter((l) => l.drug !== null || prev.length > 1),
            ...usable.map(({ item, drug }) => ({
              ...newLine(),
              drug: drug!,
              quantity: item.quantity,
              quantityUnit: item.quantityUnit ?? "",
              // 31.5 — the dose and frequency come across because the RECORD NOW HOLDS THEM. Until it did,
              // a copy arrived with these two fields empty and the quantity check with nothing to compute
              // from; the only route back to them was parsing the sig this app had formatted.
              doseAmount: item.doseAmount,
              timesPerDay: item.timesPerDay,
              durationDays: item.durationDays,
            })),
          ]);
          // The copy changes what is composed, so any check already run against the old set is stale.
          setChecked(null, null);
        }

        const lost = resolved.length - usable.length;
        toast(
          t(S.cloned).replace("{n}", String(usable.length)).replace("{ref}", clone.reference),
          usable.length > 0 ? "ok" : "bad",
        );
        if (lost > 0) toast(t(S.clonePartial).replace("{n}", String(lost)), "bad");
      } catch {
        if (live) toast(t(S.cloneFailed), "bad");
      } finally {
        if (live) onCloneApplied?.();
      }
    })();

    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clone]);

  /*
   * 29.5 — the chronic half (design 45 §5).
   *
   * The cadences come from MASTER DATA, and the schedule from the SERVER. Neither is derived here: the
   * cadence list is supervisor-administered, and re-deriving largest-remainder in TypeScript would fork the
   * one calculation in this phase that must not be forked — the doctor would be shown a schedule the
   * pharmacy never honours, with both sides able to cite correct-looking numbers.
   */
  const chronic = kind === "Chronic";
  const [frequencies, setFrequencies] = useState<RefillFrequency[]>([]);
  const [preview, setPreview] = useState<ChronicPreview | null>(null);
  /**
   * WHY the schedule is missing, not merely THAT it is.
   *
   * `notChecked` — master data does not record this drug's pack facts, so the quantity cannot be computed
   * and the write path would refuse the script too. A real, permanent answer about THIS medicine.
   * `unavailable` — the request itself failed. Transient, and worth retrying.
   *
   * Collapsing the two into one red box is what made this read as a system fault when it was a catalogue
   * gap, and left the prescriber with nothing to act on.
   */
  const [previewFailed, setPreviewFailed] = useState<null | "notChecked" | "unavailable">(null);
  const [notCheckedDetail, setNotCheckedDetail] = useState<string | null>(null);

  useEffect(() => {
    if (!chronic || frequencies.length > 0) return;
    let live = true;
    api.refillFrequencies().then(
      (rows) => { if (live) setFrequencies(rows); },
      () => { if (live) setFrequencies([]); },
    );
    return () => { live = false; };
  }, [api, chronic, frequencies.length]);

  // The first composed line's dose facts drive the preview: the schedule is per LINE, and showing the first
  // one is what makes the shape of the split visible. The server recomputes every line on submit.
  const previewLine = lines.find((l) => l.drug !== null) ?? null;

  /**
   * 31.1 — the SCRIPT's treatment length, DERIVED from the lines.
   *
   * <p>There used to be a second field for this above the composer, so one fact had two places to be stated
   * and the schedule was built from whichever the doctor filled in second. The longest line is the script's
   * length: a chronic prescription runs until its last medicine does, and windowing it to the shortest would
   * strand the rest.</p>
   */
  const scriptDurationDays = useMemo(() => {
    const days = lines.map((l) => l.durationDays).filter((d): d is number => typeof d === "number" && d > 0);
    return days.length > 0 ? Math.max(...days) : null;
  }, [lines]);

  const durationIsChronic = (scriptDurationDays ?? 0) > 30;
  const canPreview = chronic && durationIsChronic && !!refillFrequencyCode;

  useEffect(() => {
    if (!canPreview) { setPreview(null); setPreviewFailed(null); return; }
    let live = true;
    api.chronicPreview({
      durationDays: scriptDurationDays!,
      refillFrequencyCode: refillFrequencyCode!,
      doseAmount: previewLine?.doseAmount ?? 1,
      timesPerDay: previewLine?.timesPerDay ?? 1,
      // The DRUG, so the server resolves its pack facts from master data — the same lookup the write path
      // makes. Sending nothing here is what made every preview fail: the endpoint took pack facts from the
      // request body, the composer had none to send, and so no drug could ever be scheduled.
      drugId: previewLine?.drug?.drugId,
    }).then(
      (p) => { if (live) { setPreview(p); setPreviewFailed(null); setNotCheckedDetail(null); } },
      // ABSENCE IS NEVER A CLEAN RESULT — and WHICH absence matters. A catalogue gap is permanent and
      // specific to this medicine; a failed request is transient. They are different sentences.
      (err: unknown) => {
        if (!live) return;
        setPreview(null);
        const problem = (err as { problem?: { title?: string; detail?: string } })?.problem;
        if (problem?.title === "quantity-not-checked") {
          setPreviewFailed("notChecked");
          setNotCheckedDetail(problem.detail ?? null);
        } else {
          setPreviewFailed("unavailable");
          setNotCheckedDetail(null);
        }
      },
    );
    return () => { live = false; };
  }, [api, canPreview, scriptDurationDays, refillFrequencyCode,
      previewLine?.doseAmount, previewLine?.timesPerDay, previewLine?.drug?.drugId]);

  /*
   * 29.6 — THE QUANTITY, COMPUTED BY THE SERVER, PER LINE (design 45 §6).
   *
   * `QuantityMath` is the ONE implementation of this arithmetic: the validation check grades against it and
   * the dispensing counter meters against it. Multiplying three numbers here instead would be a second
   * answer to "how much medicine does this person get", and the two would be found to disagree at a counter
   * rather than in a test.
   *
   * The result PREFILLS a field the prescriber can still overrule. What it must never do is overwrite a
   * number they typed — see `quantityEdited`.
   */
  const [quantityNote, setQuantityNote] = useState<Record<string, string>>({});

  // Keyed on exactly what the answer depends on, so a re-render that changes nothing asks nothing.
  const quantityKey = lines
    .map((l) => `${l.lineId}:${l.drug?.drugId ?? ""}:${l.doseAmount ?? ""}:${l.timesPerDay ?? ""}:${l.durationDays ?? ""}:${l.quantityEdited}`)
    .join("|");

  useEffect(() => {
    let live = true;
    const askable = lines.filter(
      (l) => l.drug !== null && !l.quantityEdited
        && l.doseAmount !== null && l.timesPerDay !== null && l.durationDays !== null,
    );
    if (askable.length === 0) return;

    void Promise.all(askable.map(async (l) => {
      try {
        const p = await api.quantityPreview({
          // The DRUG, so the server resolves its pack facts itself — the same lookup the write path makes.
          // A composer that fetched a pack size and handed it back would be a second reader of the
          // catalogue, and the one that drifted would be the one on screen.
          drugId: l.drug!.drugId,
          doseAmount: l.doseAmount,
          timesPerDay: l.timesPerDay,
          durationDays: l.durationDays,
        });
        if (!live) return;
        setLines((prev) => prev.map((x) =>
          // `quantityEdited` is re-checked HERE, not only above: the doctor may have typed while the request
          // was in flight, and answering it afterwards would overwrite what they just wrote.
          //
          // 31.3 — BOXES where the box's contents are known, and the raw dispensing units where they are
          // not. A prescription is written in the thing the patient carries home; "2250" beside an insulin
          // pen is a number of international units, and no pharmacy counts those out. The field's LABEL
          // says which of the two this is, so the number is never ambiguous.
          //
          // The unit is set with the number and travels with it: it labels the field here, it is persisted
          // in the draft, and it is SENT, so a dispensing counter reading the figure back never reads it
          // without knowing what it counts.
          x.lineId === l.lineId && !x.quantityEdited
            ? {
                ...x,
                quantity: p.boxes ?? p.dispenseQuantity,
                quantityUnit: p.boxes ? t(p.boxes === 1 ? S.boxes : S.boxesPlural) : unitOf(l),
              }
            : x));
        setQuantityNote((prev) => ({
          ...prev,
          /*
           * ONLY WHAT THE NUMBER DOES NOT ALREADY SAY.
           *
           * "Computed from dose x frequency x duration" used to sit under every quantity, restating the
           * three fields immediately to its left. What remains is the conversion the number cannot carry:
           * the dose total it came from and what one box holds, so a prescriber can CHECK the box count
           * rather than trust it — or, where the catalogue does not record the box's contents, the fact
           * that this is a dose total and not a box count at all.
           */
          [l.lineId]: p.boxes && p.packContent
            ? `${p.totalUnits} ${unitOf(l)} — ${p.packContent} ${unitOf(l)} ${t(S.perBox)}`
            : p.boxes
              ? `${p.totalUnits} ${unitOf(l)}`
              : t(S.boxesUnknown),
        }));
      } catch (err) {
        if (!live) return;
        // ABSENCE IS NEVER A NUMBER (invariant 8). The missing field is NAMED and the quantity is left
        // alone — a guessed quantity is a dispensing error that looks exactly like a correct one.
        const problem = (err as { problem?: { title?: string; detail?: string } })?.problem;
        // Nothing was computed, so the field is whatever the prescriber last saw and the label must not
        // claim it is a box count. Cleared to a bare "Quantity"; the note below says what was missing.
        setLines((prev) => prev.map((x) => x.lineId === l.lineId ? { ...x, quantityUnit: "" } : x));
        setQuantityNote((prev) => ({
          ...prev,
          [l.lineId]: problem?.title === "quantity-not-checked" && problem.detail
            ? `${t(S.quantityNotChecked)} ${problem.detail}`
            : t(S.quantityNotChecked),
        }));
      }
    }));
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, quantityKey]);

  /*
   * 31.2 — REFRESH A RESTORED DRAFT'S CATALOGUE SNAPSHOT.
   *
   * `useDraft` persists the whole drug object, which is right: a composer that lost its medicine on reload
   * would be worse than one holding a stale name. But it means the name, the price, the pack facts and the
   * lowest-price flag are frozen at the moment the line was composed — so a catalogue load between then and
   * now leaves a doctor reading last week's data and reporting it as a bug in this week's.
   *
   * That is not hypothetical: it is exactly what a draft composed before the 31.1 master-data load showed —
   * uncapitalised names and no price chip, while the API was serving both correctly.
   *
   * Runs ONCE per mount, and only refreshes what actually changed so it does not churn the draft. A failure
   * leaves the snapshot alone: a stale name is still the medicine the prescriber chose.
   */
  const [refreshed, setRefreshed] = useState(false);
  useEffect(() => {
    if (refreshed) return;
    const ids = [...new Set(lines.map((l) => l.drug?.drugId).filter((id): id is string => !!id))];
    if (ids.length === 0) return;
    setRefreshed(true);

    let live = true;
    void Promise.all(ids.map((id) => api.prescribableDrugById(id).catch(() => null))).then((rows) => {
      if (!live) return;
      const fresh = new Map(rows.filter((d) => d !== null).map((d) => [d!.drugId, d!]));
      setLines((prev) => prev.map((l) => {
        const next = l.drug ? fresh.get(l.drug.drugId) : undefined;
        // Compared by VALUE, so an unchanged catalogue produces no state update and no re-render loop.
        return next && JSON.stringify(next) !== JSON.stringify(l.drug) ? { ...l, drug: next } : l;
      }));
    });
    return () => { live = false; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [api, refreshed]);

  /** Which line's checks are open. One at a time — they are read, not compared. */
  const [openChecks, setOpenChecks] = useState<string | null>(null);
  /** 29.4 — which medicine's history is open, if any. One modal for every line. */
  const [historyFor, setHistoryFor] = useState<PrescriptionDraftLine["drug"]>(null);
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

  // 29.5 — a chronic script needs a duration over one month AND a cadence, and the server refuses without
  // either. Gating here turns two rejections into two fields the doctor can see are unfilled.
  const chronicIsComplete = !chronic || (durationIsChronic && !!refillFrequencyCode && preview !== null);

  const canSubmit =
    allLinesHaveDrugs && chronicIsComplete && result !== null && !stale
    && unacknowledged.length === 0 && blocked.length === 0 && !busy;

  function patch(lineId: string, change: Partial<PrescriptionDraftLine>) {
    setLines((prev) => prev.map((l) => {
      if (l.lineId !== lineId) return l;
      const next = { ...l, ...change };
      // The stored sig is DERIVED, not typed. It is what the pharmacist reads at the counter and what is
      // printed on the patient's copy, so it has to describe the same prescription the quantity was
      // computed from — two independently-edited fields would eventually disagree.
      return { ...next, dose: sigOf(next) };
    }));
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
      await api.submitPrescription({
        encounterId, lines, diagnosisIcdCodes, acknowledgements,
        // 29.5 — sent only when chronic. An acute script carries no schedule at all: the server refuses
        // `acute-has-no-schedule` if one arrives, because "is this chronic?" must have one answer.
        ...(chronic
          ? { kind: "Chronic" as const, refillFrequencyCode, durationDays: scriptDurationDays }
          : {}),
      });
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

      {/*
        29.5 — ACUTE OR CHRONIC (design 45 §5). A property of the SCRIPT, so it sits above the lines rather
        than on one of them: one authorisation covers the whole prescription, and the windows are what it is
        dispensed in.

        A radio group rather than a switch, because the two are not on/off — they are two kinds of
        prescription, and "off" is not a name a prescriber would give to acute.
      */}
      <fieldset className="rx-kind">
        <legend className="rx-field-label">{t(S.kindLegend)}</legend>
        {/*
          TWO options and no more. `zPrescriptionKind` is a closed pair and the server's CHECK constraint
          agrees, so a third card here would be a state the write path refuses.

          Presented as two selectable CARDS rather than a bare radio row: each carries the one sentence that
          distinguishes it ("One collection" / "Collected in dated windows"), which is the actual difference
          a prescriber is choosing between. The input stays a real radio — it is what gives arrow-key
          navigation, the grouped announcement, and a selected state that does not depend on colour.
        */}
        <div className="rx-kind-options">
          {(["Acute", "Chronic"] as const).map((k) => (
            <label key={k} className="rx-kind-option" data-selected={kind === k ? "yes" : undefined}>
              <input
                type="radio"
                name={`rx-kind-${encounterId}`}
                value={k}
                checked={kind === k}
                disabled={busy}
                onChange={() =>
                  // Switching back to Acute CLEARS the cadence rather than keeping it hidden. A retained
                  // frequency would travel with an acute submission, which the server refuses — and the
                  // doctor would have no field on screen explaining why.
                  setDraft((d) => ({
                    ...d,
                    kind: k,
                    refillFrequencyCode: k === "Chronic" ? d.refillFrequencyCode : null,
                  }))
                }
              />
              <span className="rx-kind-name">{t(k === "Acute" ? S.acute : S.chronic)}</span>
              <span className="muted">{t(k === "Acute" ? S.acuteHint : S.chronicHint)}</span>
            </label>
          ))}
        </div>
      </fieldset>

      {chronic && (
        // NOT `rx-line-fields` — that is `display: contents`, which only makes sense inside the line grid.
        <div className="rx-chronic-fields">
          <label className="rx-field">
            <span className="rx-field-label">{t(S.refillFrequency)}</span>
            <select
              className="rx-field-input"
              value={refillFrequencyCode ?? ""}
              disabled={busy}
              onChange={(e) => {
                // Read the value BEFORE the updater. `setDraft`'s callback runs after React has released
                // the synthetic event, so `e.currentTarget` is null by then — which throws inside a state
                // updater and takes the whole composer down with it.
                const code = e.currentTarget.value || null;
                setDraft((d) => ({ ...d, refillFrequencyCode: code }));
              }}
            >
              <option value="">{t(S.chooseFrequency)}</option>
              {frequencies.map((f) => (
                <option key={f.code} value={f.code}>{t(f.name)}</option>
              ))}
            </select>
          </label>
          {/*
            31.1 — the treatment length is READ from the lines, not asked for again. One fact, one field:
            a second box here meant the doctor could state 90 days above and 30 on the line, and the
            schedule was built from whichever they filled in second.
          */}
          <p className="rx-field">
            <span className="rx-field-label">{t(S.treatmentDuration)}</span>
            <span className="tnum">{scriptDurationDays ?? "—"}</span>
            <span className="muted">{t(S.durationFromLines)}</span>
          </p>
        </div>
      )}

      {/*
        The refusal is stated the moment the duration says so, not held back until submit. "A 14-day course
        is not chronic" is a thing the prescriber can act on while they are still typing.
      */}
      {chronic && scriptDurationDays !== null && !durationIsChronic && (
        <InlineAlert tone="warn">{t(S.notChronic)}</InlineAlert>
      )}
      {chronic && durationIsChronic && !refillFrequencyCode && (
        <InlineAlert tone="info">{t(S.needFrequency)}</InlineAlert>
      )}
      {/* Could-not-compute is never rendered as an empty schedule — that reads as "no collections due". */}
      {/* The catalogue gap: specific, permanent, and NAMING the field — so the prescriber knows this is a
          master-data problem about this medicine rather than a system fault, and that acute still works. */}
      {previewFailed === "notChecked" && (
        <InlineAlert tone="warn">
          {t(S.scheduleNotChecked)}
          {notCheckedDetail && <> {notCheckedDetail}</>}
        </InlineAlert>
      )}
      {previewFailed === "unavailable" && <InlineAlert tone="bad">{t(S.scheduleUnavailable)}</InlineAlert>}

      {/*
        THE SCHEDULE, BEFORE SUBMIT (design 45 §5). The doctor sees 34/33/33 and can adjust — which is the
        whole point of showing it, because a chronic script commits a patient's benefit across months and
        this is the last moment changing it is free.
      */}
      {preview && (
        <div className="rx-schedule" data-testid="chronic-schedule">
          <h4 className="section-h">{t(S.scheduleTitle)}</h4>
          <table className="mini-table">
            <caption className="mini-table-cap sr-only">{t(S.scheduleTitle)}</caption>
            <thead>
              <tr>
                <th scope="col">{t(S.colWindow)}</th>
                <th scope="col">{t(S.colDue)}</th>
                <th scope="col">{t(S.colFrom)}</th>
                <th scope="col">{t(S.colUntil)}</th>
                <th scope="col">{t(S.colAllocated)}</th>
              </tr>
            </thead>
            <tbody>
              {preview.windows.map((w) => (
                <tr key={w.windowNo}>
                  <td className="tnum">{w.windowNo}</td>
                  <td className="tnum">{w.scheduledOpen}</td>
                  <td className="tnum">{w.opensAt}</td>
                  <td className="tnum">{w.closesAt}</td>
                  <td className="tnum">{w.allocatedQuantity}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              {/* The total, beside the windows that must sum to it — invariant 5, made checkable by the
                  person who signs the prescription rather than only by a test. */}
              <tr>
                <th scope="row" colSpan={4}>{t(S.scheduleTotal)}</th>
                <td className="tnum">{preview.total}</td>
              </tr>
            </tfoot>
          </table>
          <p className="muted">{t(S.scheduleHint)}</p>
        </div>
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
                {/*
                  29.6 — A NUMBER AND ITS UNIT, not free text (design 45 §6).

                  This was one text box, and `doseAmount` / `timesPerDay` were never sent — so the Quantity
                  check reported "no numeric dose, frequency and duration to compute a quantity from" on
                  every prescription this platform had ever written. The check was correct and complete;
                  nothing fed it a number.

                  The unit comes from MASTER DATA and is shown, not chosen: it is a fact about the product.
                  A drug whose unit the catalogue does not record shows the field bare rather than a guess.
                */}
                {/*
                  31.3 — THE LABEL CARRIES THE UNIT, because the unit is what the field means.

                  It used to sit as a separate chip beside the box, which cost a column of width on the
                  narrowest row of the composer and left the label saying "Dose" on every medicine in the
                  catalogue. "Dose (IU)" for insulin, "Dose (tabs)" for a tablet, "Dose (puffs)" for an
                  inhaler — one field, read at a glance, in the words a prescription is written in.

                  A drug whose unit the catalogue does not record shows the label bare. That is honest; a
                  word invented for it would sit beside the field reading as data.
                */}
                <label className="rx-field">
                  <span className="rx-field-label">
                    {t(S.dose)}{unitOf(line) && ` (${unitOf(line)})`}
                  </span>
                  <input
                    className="rx-field-input"
                    type="number"
                    min={0}
                    step="any"
                    inputMode="decimal"
                    value={line.doseAmount ?? ""}
                    disabled={busy}
                    onChange={(e) => {
                      const raw = e.currentTarget.value;
                      patch(line.lineId, { doseAmount: raw === "" ? null : Number(raw) });
                    }}
                  />
                </label>
                <label className="rx-field">
                  <span className="rx-field-label">{t(S.timesPerDay)}</span>
                  <input
                    className="rx-field-input"
                    type="number"
                    min={1}
                    value={line.timesPerDay ?? ""}
                    disabled={busy}
                    onChange={(e) => {
                      const raw = e.currentTarget.value;
                      patch(line.lineId, { timesPerDay: raw === "" ? null : Number(raw) });
                    }}
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
                {/*
                  THE QUANTITY: computed, shown, and still the prescriber's to overrule.

                  Filled in from the SERVER's arithmetic — `QuantityMath`, the same code the validation check
                  grades against and the counter meters against. Typing here sets `quantityEdited`, after
                  which no recomputation touches it: a doctor who deliberately writes 90 because the patient
                  is travelling must not watch it snap back on the next keystroke.
                */}
                <div className="rx-field">
                  {/* 31.3 — "Quantity (boxes)" or "Quantity (IU)". The number alone does not say which, and
                      the difference between them is one box and two thousand two hundred and fifty units. */}
                  <label className="rx-field-label" htmlFor={`rx-qty-${line.lineId}`}>
                    {t(S.quantity)}{line.quantityUnit && ` (${line.quantityUnit})`}
                  </label>
                  <input
                    id={`rx-qty-${line.lineId}`}
                    className="rx-field-input"
                    type="number"
                    min={1}
                    value={line.quantity}
                    disabled={busy}
                    // DESCRIBED by the note, not NAMED by it. Wrapping the sentence in the <label> folds it
                    // into the field's accessible name, so the box announces as "Quantity Computed from dose
                    // x frequency x duration…" — and any test or screen reader looking for a field called
                    // "Duration" finds this one too.
                    aria-describedby={quantityNote[line.lineId] ? `rx-qty-note-${line.lineId}` : undefined}
                    onChange={(e) => {
                      const raw = e.currentTarget.value;
                      patch(line.lineId, { quantity: Number(raw), quantityEdited: true });
                    }}
                  />
                  {/* HOW the number was reached. A prescriber can overrule a quantity; they cannot check
                      one unless they are told what it was computed from — or which fact was missing. */}
                  {quantityNote[line.lineId] && (
                    <span id={`rx-qty-note-${line.lineId}`} className="muted rx-quantity-note">
                      {quantityNote[line.lineId]}
                    </span>
                  )}
                </div>
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

            {/*
              29.4 — "HAS THIS PATIENT HAD THIS MEDICINE BEFORE?" ON THE LINE BEING COMPOSED.

              The same icon, the same modal and the same endpoint the sent rows use — but reachable at the one
              moment the answer can still change the decision. It was offered only on prescriptions already
              written, which is precisely when it is too late to matter.
            */}
            <div className="rx-line-actions">
              {line.drug && (
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={busy}
                  aria-label={`${t(S.lineHistory)} — ${t(line.drug.tradeName)}`}
                  onClick={() => setHistoryFor(line.drug)}
                >
                  <Icon name="clock" />
                </Button>
              )}
              {lines.length > 1 && (
                <Button
                  // DANGER, not ghost. Removing a composed line destroys clinical work, and the control
                  // that does it should not look like the one beside it that opens a history panel.
                  variant="danger"
                  size="sm"
                  disabled={busy}
                  // The line is NAMED. A column of bare crosses is a screen-reader user hearing "Remove"
                  // five times with no way to tell which medicine they are about to drop.
                  aria-label={`${t(S.removeLine)} — ${line.drug ? t(line.drug.tradeName) : t(S.emptyLine)}`}
                  onClick={() => {
                    setLines((prev) => prev.filter((l) => l.lineId !== line.lineId));
                    setAcknowledgements((prev) => prev.filter((a) => a.lineId !== line.lineId));
                  }}
                >
                  <Icon name="cross" />
                </Button>
              )}
            </div>

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

      {/* Frameless. This is not a decision of the same weight as Validate or Submit, and a bordered button
          beside them reads as though it were. */}
      <Button
        variant="ghost"
        size="sm"
        className="rx-add-line"
        disabled={busy}
        onClick={() => setLines((prev) => [...prev, newLine()])}
        leadingIcon={<Icon name="plus" aria-hidden="true" />}
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
        {/* Discarding throws away a composed prescription. It is the destructive action on this row and
            reads as one, rather than as the quietest control on the screen. */}
        <Button variant="danger" disabled={!composed || busy} onClick={() => setDiscarding(true)}>
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
      {/*
        29.4 — THE shared service-history modal (design 45 §4). The SAME component the encounter tabs and the
        prescription dialog open, against the SAME endpoint — not a composer-shaped copy of it.
      */}
      {historyFor && (
        <ServiceHistoryModal
          beneficiaryId={beneficiaryId}
          serviceType="Prescription"
          code={historyFor.drugId}
          label={t(historyFor.tradeName)}
          onClose={() => setHistoryFor(null)}
        />
      )}

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

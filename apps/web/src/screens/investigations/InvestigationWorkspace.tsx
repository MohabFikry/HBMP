import { useEffect, useMemo, useState, type SetStateAction } from "react";
import { z } from "zod";
import { Button, Icon, InlineAlert, Modal, useToast } from "@mersal/design-system";
import type {
  CheckState, CptSection, InvestigationDraftLine, InvestigationOrderType, Localized,
  OrderAcknowledgement, OrderCheckKind, OrderFinding, OrderValidationResult, ProcedureType,
} from "@mersal/contracts";
import { zInvestigationDraftLine, zOrderAcknowledgement, zOrderValidationResult } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useDraft } from "../draftStore";
import { useLoc } from "../_shared";
import { ServiceHistoryModal } from "../ServiceHistoryModal";
import { CptCombobox } from "./CptCombobox";
import { LineStatusChip } from "../prescribing/LineStatusChip";

const S = {
  addLine: { en: "Add another", ar: "إضافة سطر آخر" },
  removeLine: { en: "Remove", ar: "إزالة" },
  emptyLine: { en: "empty line", ar: "سطر فارغ" },
  // 29.4 (design 45 §4) — asked on the line being COMPOSED, which is the only moment the answer can still
  // change what is ordered. Duplicate ordering is the thing this question exists to prevent.
  lineHistory: { en: "Previous occurrences of this service", ar: "الحالات السابقة لهذه الخدمة" },
  quantity: { en: "Quantity", ar: "الكمية" },
  // 29.2 (design 45 §2) — the OP-Procedure kind, and the sessions field its flag reveals.
  procedureType: { en: "Procedure type", ar: "نوع الإجراء" },
  chooseType: { en: "Choose a type…", ar: "اختر النوع…" },
  sessions: { en: "Sessions", ar: "عدد الجلسات" },
  // 31.4 — copying an order that has already been raised. The KIND and the session count are order-level
  // facts the worklist row does not carry, so a copied course arrives without them and says so.
  cloned: {
    en: "Copied {n} item(s) from {ref}. Nothing has been ordered yet — check it and send.",
    ar: "تم نسخ {n} بند من {ref}. لم يتم طلب أي شيء بعد — راجعه ثم أرسل.",
  },
  cloneEmpty: {
    en: "Nothing on {ref} could be copied — it records no items.",
    ar: "لا يوجد ما يمكن نسخه من {ref} — لا تسجّل أي بنود.",
  },
  // 31.1 — the course, at the level it is decided.
  courseLegend: { en: "Procedure course", ar: "خطة الإجراء" },
  quantityPerSession: { en: "Quantity per session", ar: "الكمية لكل جلسة" },
  courseTotal: { en: "Total to deliver", ar: "الإجمالي المطلوب تنفيذه" },
  courseHint: {
    en: "One kind and one number of sessions for the whole order — a course is one decision. Each line's "
      + "quantity is what is delivered at EACH session.",
    ar: "نوع واحد وعدد جلسات واحد للطلب كله — الخطة قرار واحد. كمية كل سطر هي ما يُنفَّذ في كل جلسة.",
  },
  needCourseType: {
    en: "Choose a procedure type. The type decides how the order is delivered and counted.",
    ar: "اختر نوع الإجراء. النوع يحدد كيفية تنفيذ الطلب واحتسابه.",
  },
  // 29.2 — the vehicle, said in the doctor's words rather than as an enum. The difference between the two
  // is not cosmetic: a referral is not finished until a report comes back.
  willCreate: { en: "Creates", ar: "سيُنشئ" },
  vehicleProcedure: { en: "Procedure order", ar: "طلب إجراء" },
  vehicleReferral: { en: "Referral", ar: "إحالة" },
  vehicleHintReferral: {
    en: "An external evaluation is a referral. It is not complete until a report comes back.",
    ar: "التقييم الخارجي إحالة. لا تكتمل إلا بعودة التقرير.",
  },
  targetSpecialty: { en: "Referred to (specialty)", ar: "الإحالة إلى (التخصص)" },
  specialtyPlaceholder: { en: "e.g. Cardiology", ar: "مثال: القلب" },
  needSpecialty: {
    en: "Name the specialty each referral is addressed to. A referral without one cannot be routed.",
    ar: "حدّد التخصص لكل إحالة. الإحالة بدون تخصص لا يمكن توجيهها.",
  },
  notOrderable: {
    en: "This code cannot be raised from here.",
    ar: "لا يمكن طلب هذا الكود من هنا.",
  },
  sentReferral: { en: "Referral raised.", ar: "تم إنشاء الإحالة." },
  needType: {
    en: "Choose a procedure type for every line. The type decides how the order is delivered and counted.",
    ar: "اختر نوع الإجراء لكل سطر. النوع يحدد كيفية تنفيذ الطلب واحتسابه.",
  },
  typesUnavailable: {
    en: "The procedure types could not be loaded, so an order cannot be composed. This is NOT a report that "
      + "none are configured — nothing has changed, and it is worth trying again in a moment.",
    ar: "تعذّر تحميل أنواع الإجراءات، لذلك لا يمكن إعداد الطلب. هذا ليس تقريراً بعدم وجود أنواع مضبوطة — "
      + "لم يتغيّر شيء، ويمكن المحاولة مرة أخرى بعد قليل.",
  },
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
  // 29.1 — was "Imaging order sent." The rename left this behind, and because the toast was chosen by
  // `orderType === "Imaging"` — never true after the switch — a RADIOLOGY order confirmed itself as a lab
  // order. Both halves of that were wrong, in a sentence the doctor reads and believes.
  sentRadiology: { en: "Radiology order sent.", ar: "تم إرسال طلب الأشعة." },
  sentProcedure: { en: "Procedure order sent.", ar: "تم إرسال طلب الإجراء." },
  sentForApproval: { en: "Sent to the approval team.", ar: "تم الإرسال إلى فريق الموافقات." },
  submitFailed: { en: "The order was refused.", ar: "تم رفض الطلب." },
  checkFailed: { en: "The check could not run.", ar: "تعذّر إجراء التحقق." },
};

/**
 * What the confirmation says, per order type. A total map rather than a ternary chain, for the reason
 * `sectionsFor` is one: the chain here picked its arm on `orderType === "Imaging"`, which stopped being
 * true at the 29.1 switch, so radiology and procedure orders both confirmed themselves as lab orders.
 */
const SENT: Record<InvestigationOrderType, Localized> = {
  Lab: S.sentLab,
  Radiology: S.sentRadiology,
  Imaging: S.sentRadiology,
  Procedure: S.sentProcedure,
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

  // ---- 31.1 — the OP-Procedure COURSE, at the level it is decided (design 45 §2, revised) ---------
  //
  // Held at the DRAFT level because a procedure order is ONE clinical decision: one kind, one number of
  // attendances. Per line, a two-item course could carry two kinds and two session counts — not a course
  // any centre can deliver — and there was nowhere at all to record "three of these each time", because
  // the quantity slot was spent on the session count.
  procedureTypeCode: z.string().nullable().default(null),
  /** The course length in attendances. NULL when the chosen type is not session-based — a different fact
   *  from 1, and only the second should ever render a session field. */
  sessions: z.number().int().nullable().default(null),
});
type OrderDraft = z.infer<typeof ORDER_DRAFT>;

function emptyDraft(): OrderDraft {
  return {
    lines: [newLine()], result: null, validatedFingerprint: null, acknowledgements: [],
    procedureTypeCode: null, sessions: null,
  };
}

/**
 * "Nothing has been composed here" — one definition, used for three things that must agree: what the draft
 * store keeps, whether Discard has anything to discard, and whether the encounter screen will let the visit
 * be closed. See the prescribing workspace, which draws the same line for the same reason.
 */
function isEmptyDraft(d: OrderDraft): boolean {
  return d.result === null && d.lines.length === 1 && d.lines[0].test === null
    // 31.1 — the course counts as composition. Choosing a procedure type or a session count is work the
    // doctor did, and without this the store discards it: a remount would silently blank both, and the
    // encounter screen would report the composer clean and close the visit over it.
    && d.procedureTypeCode === null && d.sessions === null;
}

function newLine(): InvestigationDraftLine {
  return {
    lineId: crypto.randomUUID(), test: null, quantity: 1, note: "",
    procedureTypeCode: null, vehicle: null, targetSpecialty: null,
  };
}

/** 29.2 — an E/M line becomes a referral; everything else on this tab becomes an order. */
function isReferral(line: InvestigationDraftLine): boolean {
  return line.vehicle === "Referral";
}

/** Changing any of this invalidates the last check — the same staleness rule the prescribing workspace uses. */
function fingerprint(lines: InvestigationDraftLine[], typeCode: string | null, sessions: number | null): string {
  // The COURSE is part of the fingerprint: changing the kind or the session count changes what is being
  // ordered, and a check run against the old pair is stale in exactly the way a changed line is.
  return `${typeCode ?? ""}/${sessions ?? ""}::` + lines
    .map((l) => `${l.lineId}|${l.test?.code ?? ""}|${l.quantity}|${l.note}`
      + `|${l.vehicle ?? ""}|${l.targetSpecialty ?? ""}`)
    .join(";");
}

/**
 * Which CPT sections this tab's catalogue search is narrowed to.
 *
 * <p><b>A TAB IS NOT A SECTION.</b> Radiology is one; Labs is two, because a sample run on an analyser
 * (Laboratory) and a specimen read by a pathologist (Pathology) are ordered from the same tab and are not
 * the same kind of work. OP Procedures is two as well — design 45 §2 routes Surgery and Medicine to a
 * Procedure order.</p>
 *
 * <p><b>Why this is a function and not a ternary.</b> It WAS a ternary —
 * `orderType === "Imaging" ? ["Imaging"] : [...]` — written before 29.1 renamed the order type. After the
 * rename the encounter passes `"Radiology"`, that test was never true again, and the radiology tab
 * silently searched a catalogue of blood panels while every test stayed green. `"Procedure"` inherited the
 * same wrong arm. A union that has grown twice needs an exhaustive map, not a fall-through.</p>
 *
 * <p>E/M appears ONLY in the Procedure arm, and choosing one raises a REFERRAL rather than an order
 * (invariant 3). It is offered rather than hidden because the tab's job is to take a clinical decision and
 * route it; what must never happen is an E/M code becoming a Procedure ORDER, and that is enforced on the
 * write path by pharmacy's `not-a-referral-service` refusal, not by this list.</p>
 */
export function sectionsFor(orderType: InvestigationOrderType): CptSection[] {
  switch (orderType) {
    // Both spellings, until the legacy value is dropped. `Imaging` is retained on pre-switch orders for
    // the life of the order, and the SECTION vocabulary keeps that name regardless — it is masterdata's
    // chapter title, not the role or the order type that 29.1 renamed.
    case "Radiology":
    case "Imaging":
      return ["Imaging"];
    case "Procedure":
      // Surgery and Medicine become a Procedure ORDER; E/M becomes a REFERRAL. All three are offered here
      // because "the doctor picks a service; the SYSTEM decides the vehicle" (design 45 §2) — hiding E/M
      // would make the doctor's next move a phone call rather than a referral.
      return ["Surgery", "Medicine", "EvaluationAndManagement"];
    case "Lab":
      return ["Laboratory", "Pathology"];
  }
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
/**
 * 31.4 — an order to copy into this composer, as the row that offered it knows it.
 *
 * <p>No `procedureTypeCode` or `sessions`: those are ORDER-level facts (31.1) and the worklist row does not
 * carry them, so a copied procedure course arrives with its items and without its kind. Stated rather than
 * defaulted — a session count invented here is a course nobody prescribed.</p>
 */
export interface OrderClone {
  /** What the doctor is copying, for the confirmation — "ORD-2026-000118". */
  reference: string;
  items: { code: string; description: string | null; quantity: number }[];
}

export function InvestigationWorkspace({
  encounterId,
  beneficiaryId,
  orderType,
  diagnosisIcdCodes,
  clone,
  onCloneApplied,
  onDone,
}: {
  encounterId: string;
  /** 29.4 — whose history the composer may ask about. The service-history endpoint is keyed on the
   *  BENEFICIARY; an encounter id in that slot answers for nobody. */
  beneficiaryId: string;
  orderType: InvestigationOrderType;
  diagnosisIcdCodes: string[];
  /** 31.4 — set by a row's Clone action; consumed once and reported back through `onCloneApplied`. */
  clone?: OrderClone | null;
  onCloneApplied?: () => void;
  onDone?: () => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();

  const sections = sectionsFor(orderType);

  // 29.2 — the OP-Procedure kinds, from MASTER DATA (design 45 §2). Fetched only for the tab that needs
  // them: the Labs and Radiology tabs neither show the field nor pay for the call.
  const isProcedure = orderType === "Procedure";
  const [procedureTypes, setProcedureTypes] = useState<ProcedureType[]>([]);
  const [typesFailed, setTypesFailed] = useState(false);
  useEffect(() => {
    if (!isProcedure) return;
    let live = true;
    api.procedureTypes().then(
      (rows) => { if (live) { setProcedureTypes(rows); setTypesFailed(false); } },
      // Absence is never a clean result. An empty combobox would read as "this platform offers no procedure
      // kinds", when the truth is that masterdata could not be reached — and the doctor would compose an
      // order the write path is certain to refuse.
      () => { if (live) setTypesFailed(true); },
    );
    return () => { live = false; };
  }, [api, isProcedure]);

  const typeByCode = useMemo(
    () => new Map(procedureTypes.map((p) => [p.code, p])),
    [procedureTypes],
  );

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
  const { lines, result, validatedFingerprint, acknowledgements, procedureTypeCode, sessions } = draft;

  // Field-shaped setters over the single object, so every call site below reads exactly as it did when these
  // were four separate states — including the updater form, which keeps them free of stale closures.
  const setLines = (u: SetStateAction<InvestigationDraftLine[]>) =>
    setDraft((d) => ({ ...d, lines: typeof u === "function" ? u(d.lines) : u }));
  const setAcknowledgements = (u: SetStateAction<OrderAcknowledgement[]>) =>
    setDraft((d) => ({ ...d, acknowledgements: typeof u === "function" ? u(d.acknowledgements) : u }));
  const setChecked = (r: OrderValidationResult | null, fingerprint: string | null) =>
    setDraft((d) => ({ ...d, result: r, validatedFingerprint: fingerprint }));

  const [busy, setBusy] = useState(false);

  /*
   * 31.4 — COPY AN EXISTING ORDER INTO THIS COMPOSER.
   *
   * Repeat bloods on a chronic patient are the commonest thing on this tab, and re-ordering them meant
   * finding each code in the CPT book again. The row hands over the codes; nothing needs resolving, because
   * a line here IS a code and a description.
   *
   * It APPENDS rather than replaces, and the only line it removes is a single empty placeholder — a doctor
   * who has half-composed an order and reaches for Clone must not watch it disappear. The copy makes any
   * check already run stale, which is said by clearing the result rather than by leaving a verdict that was
   * reached about a different set of tests.
   */
  useEffect(() => {
    if (!clone) return;
    // An order carrying no lines copies nothing, and must say so rather than leaving the request unconsumed
    // and the doctor watching a button that appears to do nothing.
    if (clone.items.length === 0) {
      toast(t(S.cloneEmpty).replace("{ref}", clone.reference), "bad");
      onCloneApplied?.();
      return;
    }
    setLines((prev) => [
      ...prev.filter((l) => l.test !== null || prev.length > 1),
      ...clone.items.map((item) => ({
        ...newLine(),
        test: { code: item.code, description: item.description ?? item.code },
        quantity: item.quantity,
      })),
    ]);
    setChecked(null, null);
    toast(
      t(S.cloned).replace("{n}", String(clone.items.length)).replace("{ref}", clone.reference),
      "ok",
    );
    onCloneApplied?.();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [clone]);

  const [discarding, setDiscarding] = useState(false);
  const composed = !isEmptyDraft(draft);
  const [openChecks, setOpenChecks] = useState<string | null>(null);
  /** 29.4 — which service's history is open, if any. One modal for every line. */
  const [historyFor, setHistoryFor] = useState<InvestigationDraftLine["test"]>(null);

  /** The ORDER's chosen kind, resolved once. Its `isSessionBased` flag is what reveals the session field
   *  and relabels each line's quantity — never the type's NAME, because dialysis and rehabilitation are
   *  session-based too and a check against "Physiotherapy" would be wrong for both. */
  const selectedType = procedureTypeCode ? typeByCode.get(procedureTypeCode) : undefined;

  const current = fingerprint(lines, procedureTypeCode, sessions);
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

  // 29.2 — a Procedure line without a type is refused 422 by orders-service. Stopping it here turns a
  // rejected submission into a field the doctor can see is unfilled; the write path still decides.
  // A type is required on a Procedure ORDER line. A referral has no procedure type — it is not delivered
  // by a centre against a session count — so requiring one would make E/M unsubmittable.
  // 31.1 — ONE type for the order. A referral carries none — it is not delivered by a centre against a
  // session count — so an order composed entirely of referrals needs no type at all.
  const orderNeedsType = isProcedure && lines.some((l) => l.test !== null && !isReferral(l));
  const allLinesHaveTypes = !orderNeedsType || (procedureTypeCode ?? "") !== "";

  // 29.2 — a referral needs the specialty it is addressed to, and every Procedure line needs a RESOLVED
  // vehicle: submitting with a null vehicle would mean guessing which object to create.
  const allReferralsAddressed = lines.every((l) => !isReferral(l) || (l.targetSpecialty ?? "").trim() !== "");
  const allVehiclesKnown = !isProcedure || lines.every((l) => l.test === null || l.vehicle !== null);
  const noUnorderableLines = lines.every((l) => l.vehicle !== "NotOrderable");

  const canSubmit =
    allLinesHaveTests && allLinesHaveTypes && allReferralsAddressed && allVehiclesKnown && noUnorderableLines
    && result !== null && !stale
    && unacknowledged.length === 0 && blocked.length === 0 && !busy;

  function patch(lineId: string, change: Partial<InvestigationDraftLine>) {
    setLines((prev) => prev.map((l) => (l.lineId === lineId ? { ...l, ...change } : l)));
    // An acknowledgement belongs to the finding it was given for; editing the line re-derives the findings,
    // so carrying the reason forward would attach a justification to something the doctor never saw.
    setAcknowledgements((prev) => prev.filter((a) => a.lineId !== lineId));
  }

  /**
   * 29.2 — choosing a service, and learning what it will become (design 45 §2).
   *
   * <p>The vehicle is asked for on SELECTION rather than on submit, because the whole point is that the
   * doctor sees it while the choice is still free. Only the Procedure tab asks: Labs and Radiology have one
   * vehicle each and always did.</p>
   *
   * <p>If the lookup fails the line keeps a null vehicle, the composer shows no claim about what it will
   * create, and submit is blocked — rather than guessing "order" and raising the wrong object.</p>
   */
  async function chooseTest(lineId: string, test: InvestigationDraftLine["test"]) {
    patch(lineId, { test, vehicle: null, targetSpecialty: null });
    if (!isProcedure || !test) return;

    try {
      const matches = await api.orderableServices(test.code, ["ProcedureOrder", "Referral"]);
      const hit = matches.find((m) => m.code === test.code) ?? null;
      patch(lineId, { test, vehicle: hit?.orderable ? hit.vehicle : hit ? "NotOrderable" : null });
    } catch {
      // Absence is never a clean result: no vehicle claim, and canSubmit refuses below.
      patch(lineId, { test, vehicle: null });
    }
  }

  async function validate() {
    setBusy(true);
    try {
      const r = await api.validateInvestigationOrder({ encounterId, orderType, lines, diagnosisIcdCodes });
      setChecked(r, fingerprint(lines, procedureTypeCode, sessions));
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
      /*
       * 29.2 — THE DOCTOR PICKS A SERVICE; THE SYSTEM DECIDES THE VEHICLE (design 45 §2, invariant 3).
       *
       * E/M lines become REFERRALS and everything else becomes an order, and both may be composed in one
       * sitting — a knee arthroscopy and a cardiology opinion are one clinical decision. Each referral is
       * raised individually because a referral IS individual: it carries one specialty and closes with one
       * report, and batching them would give the loop nothing specific to close against.
       */
      const referralLines = lines.filter(isReferral);
      const orderLines = lines.filter((l) => !isReferral(l));

      const referrals = [];
      for (const l of referralLines) {
        referrals.push(await api.createReferral({
          encounterId,
          targetSpecialty: (l.targetSpecialty ?? "").trim(),
          reason: l.note.trim() || undefined,
          requestedServiceCode: l.test?.code ?? "",
        }));
      }

      if (orderLines.length === 0) {
        toast(`${t(S.sentReferral)} ${referrals.map((r) => r.referralNo).join(", ")}`, "ok");
        setDraft(emptyDraft());
        setOpenChecks(null);
        onDone?.();
        return;
      }

      const res = await api.submitInvestigationOrder({
        encounterId, orderType, lines: orderLines, acknowledgements,
        // 31.1 — the COURSE travels with the order, not with each line.
        procedureTypeCode, sessions,
      });
      // Which of the two happened is not a detail: an order that went for approval is NOT with the lab yet,
      // and a doctor who reads "sent" and expects a result tomorrow has been misinformed by one word.
      // Both outcomes are named when both happened. "Order sent" alone, after a referral was also raised,
      // would leave the doctor believing the referral did not go.
      const orderMsg = res.requiresApproval
        ? `${t(S.sentForApproval)} ${res.orderNo}`
        : `${t(SENT[orderType])} ${res.orderNo}`;
      toast(
        referrals.length > 0
          ? `${orderMsg} ${t(S.sentReferral)} ${referrals.map((r) => r.referralNo).join(", ")}`
          : orderMsg,
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

      {/*
        31.1 — THE COURSE, ABOVE THE LINES (design 45 §2, revised).

        A procedure order is ONE clinical decision: one kind, one number of attendances. Per line, a two-item
        course could carry two kinds and two session counts, which is not a course any centre can deliver —
        and there was nowhere to record "three of these each time", because the quantity slot was already
        spent on the session count.
      */}
      {isProcedure && (
        <fieldset className="rx-course">
          <legend className="rx-field-label">{t(S.courseLegend)}</legend>
          <div className="rx-course-fields">
            <label className="rx-field">
              <span className="rx-field-label">{t(S.procedureType)}</span>
              <select
                className="rx-field-input"
                value={procedureTypeCode ?? ""}
                disabled={busy || typesFailed}
                onChange={(e) => {
                  // Read the value BEFORE the updater — `setDraft`'s callback runs after React has released
                  // the synthetic event, and reaching for `currentTarget` there throws inside a state
                  // updater and takes the whole composer down with it.
                  const code = e.currentTarget.value || null;
                  const picked = code ? typeByCode.get(code) : undefined;
                  setDraft((d) => ({
                    ...d,
                    procedureTypeCode: code,
                    // A session-based type starts at its own default rather than blank — six is the
                    // commonest physiotherapy course, and a blank field makes the common case a typing
                    // task. A non-session type gets NULL, not 1: "this kind has no sessions" and "a
                    // one-session course" are different facts, and only the second shows a count.
                    sessions: picked?.isSessionBased ? (picked.defaultSessions ?? 1) : null,
                  }));
                }}
              >
                <option value="">{t(S.chooseType)}</option>
                {procedureTypes.map((p) => (
                  <option key={p.code} value={p.code}>{t(p.name)}</option>
                ))}
              </select>
            </label>

            {/* Revealed by the TYPE's `isSessionBased` flag and never by its NAME — dialysis and
                rehabilitation are session-based too, so a check written against the physiotherapy name
                would be wrong for both. */}
            {selectedType?.isSessionBased && (
              <label className="rx-field">
                <span className="rx-field-label">{t(S.sessions)}</span>
                <input
                  className="rx-field-input"
                  type="number"
                  min={1}
                  // The type's own ceiling, so the composer stops the doctor where masterdata does. The
                  // verdict that BINDS is still the write path's — this one is display state.
                  max={selectedType.maxSessions ?? undefined}
                  value={sessions ?? ""}
                  disabled={busy}
                  onChange={(e) => {
                    const raw = e.currentTarget.value;
                    setDraft((d) => ({ ...d, sessions: raw === "" ? null : Math.max(1, Number(raw)) }));
                  }}
                />
              </label>
            )}
          </div>
          <p className="muted">{t(S.courseHint)}</p>
        </fieldset>
      )}

      <ul className="rx-lines">
        {lines.map((line) => (
          <li key={line.lineId} className="rx-line">
            <div className="rx-line-main">
              <CptCombobox
                value={line.test}
                sections={sections}
                onChange={(test) => void chooseTest(line.lineId, test)}
                disabled={busy}
              />

              {/*
                29.2 — WHAT THIS WILL CREATE, before the doctor commits (design 45 §2). The stated purpose
                of `/orderable-services`, which existed with no caller: "so the UI can show the doctor what
                will happen before they commit". A referral and a procedure order are different objects with
                different endings, and the doctor choosing between them is entitled to know which they are
                choosing.
              */}
              {isProcedure && line.vehicle && (
                <p className="rx-vehicle">
                  <span className="rx-field-label">{t(S.willCreate)}</span>{" "}
                  <span className="rx-combobox-chip" data-kind={isReferral(line) ? "referral" : "order"}>
                    {t(isReferral(line) ? S.vehicleReferral : S.vehicleProcedure)}
                  </span>
                  {isReferral(line) && <span className="muted"> {t(S.vehicleHintReferral)}</span>}
                </p>
              )}
              <div className="rx-line-fields">
                {/*
                  31.1 — the procedure TYPE is no longer here. It belongs to the ORDER: one kind for one
                  clinical decision (see the course block above). What remains on the line is how much of
                  THIS item is delivered at each attendance.
                */}
                {isProcedure && isReferral(line) && (
                  <label className="rx-field rx-field--wide">
                    <span className="rx-field-label">{t(S.targetSpecialty)}</span>
                    <input
                      className="rx-field-input"
                      placeholder={t(S.specialtyPlaceholder)}
                      value={line.targetSpecialty ?? ""}
                      disabled={busy}
                      onChange={(e) => {
                        const v = e.currentTarget.value;   // read before the updater
                        patch(line.lineId, { targetSpecialty: v });
                      }}
                    />
                  </label>
                )}


                {/*
                  31.1 — THE QUANTITY IS NOW PER SESSION on a session-based course, and separate from the
                  session count above.

                  They used to be the same field: "sessions ARE the quantity" (design 45 §2). That left
                  nowhere to record "three of these at each attendance", which is an ordinary thing to
                  prescribe. The metered total the server stores is sessions x this, so consume, partial
                  approval and the delivering centre's queue all count the same units they always did.
                */}
                {/* A referral has no quantity: it is one request for one opinion, and a "quantity 3"
                    referral is not a thing the state machine can close three times. */}
                {!isReferral(line) && (
                <label className="rx-field">
                  <span className="rx-field-label">
                    {t(selectedType?.isSessionBased ? S.quantityPerSession : S.quantity)}
                  </span>
                  <input
                    className="rx-field-input"
                    type="number"
                    min={1}
                    value={line.quantity}
                    disabled={busy}
                    onChange={(e) => patch(line.lineId, { quantity: Math.max(1, Number(e.currentTarget.value) || 1) })}
                  />
                  {/* The total the centre will be asked to deliver, stated where the two numbers that
                      produce it can both be seen. A course whose total only appears after submission is a
                      course nobody checked. */}
                  {selectedType?.isSessionBased && sessions !== null && (
                    <span className="muted">
                      {t(S.courseTotal)}: <span className="tnum">{sessions * line.quantity}</span>
                    </span>
                  )}
                </label>
                )}
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

            {/*
              29.4 — "HAS THIS PATIENT HAD THIS BEFORE?" ON THE LINE BEING COMPOSED. The same icon, modal and
              endpoint the sent rows use, at the one moment the answer can still stop a duplicate order.
            */}
            <div className="rx-line-actions">
              {line.test && (
                <Button
                  variant="ghost"
                  size="sm"
                  disabled={busy}
                  aria-label={`${t(S.lineHistory)} — ${line.test.code}`}
                  onClick={() => setHistoryFor(line.test)}
                >
                  <Icon name="clock" />
                </Button>
              )}
              {lines.length > 1 && (
                <Button
                  // DANGER — see the prescribing workspace. Removing a composed line is destructive and
                  // sits next to a history icon that is not.
                  variant="danger"
                  size="sm"
                  disabled={busy}
                  aria-label={`${t(S.removeLine)} — ${line.test ? line.test.code : t(S.emptyLine)}`}
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

      {/* Frameless — see the prescribing workspace. Adding a line is not a decision of the same weight as
          Check or Send order, and a bordered button beside them reads as though it were. */}
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
      {result && !stale && unacknowledged.length > 0 && <InlineAlert tone="warn">{t(S.unacknowledged)}</InlineAlert>}
      {!allLinesHaveTests && <InlineAlert tone="info">{t(S.needTest)}</InlineAlert>}
      {typesFailed && <InlineAlert tone="bad">{t(S.typesUnavailable)}</InlineAlert>}
      {allLinesHaveTests && !allLinesHaveTypes && !typesFailed && (
        <InlineAlert tone="info">{t(S.needCourseType)}</InlineAlert>
      )}
      {allLinesHaveTests && !allReferralsAddressed && <InlineAlert tone="info">{t(S.needSpecialty)}</InlineAlert>}
      {!noUnorderableLines && <InlineAlert tone="warn">{t(S.notOrderable)}</InlineAlert>}

      <div className="rx-actions">
        {/*
          The other way out, and the reason the encounter screen can insist on one — closing a visit is
          refused while anything sits composed-but-unsent here. See the prescribing workspace.
        */}
        <Button variant="danger" disabled={!composed || busy} onClick={() => setDiscarding(true)}>
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
      {/* 29.4 — THE shared modal, one component and one endpoint, exactly as the tabs above open it. */}
      {historyFor && (
        <ServiceHistoryModal
          beneficiaryId={beneficiaryId}
          serviceType={orderType}
          code={historyFor.code}
          label={historyFor.description}
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

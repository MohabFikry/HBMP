import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, Icon, InlineAlert, InputField, StatusChip, TextareaField, useTheme, useToast } from "@mersal/design-system";
import type { Coded, DispenseLine, Localized, Prescription, PrescriptionLine, RxPricing } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useWrite, writeErrorText } from "../../api/useWrite";
import { writeErrorMessage } from "../../api/writeError";
import { PatientContextBar } from "../PatientProfile";
import { PageHeader, productName, useLoc } from "../_shared";
import { useFormat } from "../../i18n/useFormat";
import { SubstituteModal } from "./SubstituteModal";

const S = {
  back: { en: "Back to search", ar: "العودة إلى البحث" },
  loading: { en: "Opening the prescription…", ar: "جارٍ فتح الوصفة…" },
  notFound: {
    en: "That prescription could not be opened. It may have been dispensed in full, cancelled, or the "
      + "reference may be wrong.",
    ar: "تعذّر فتح هذه الوصفة. ربما صُرفت بالكامل أو أُلغيت أو أن الرقم غير صحيح.",
  },
  prescriber: { en: "Prescriber", ar: "الطبيب الواصف" },
  written: { en: "Written", ar: "تاريخ الوصف" },
  expires: { en: "Valid until", ar: "صالحة حتى" },
  expiresUnknown: { en: "Not recorded", ar: "غير مسجّل" },
  daysLeft: { en: "{n} days left", ar: "متبقٍ {n} يوم" },
  lastDay: { en: "Last day", ar: "آخر يوم" },
  lapsed: { en: "Lapsed", ar: "منتهية" },
  diagnosis: { en: "Diagnosis", ar: "التشخيص" },
  noDiagnosis: {
    en: "No diagnosis was recorded on the encounter",
    ar: "لم يُسجَّل تشخيص على الزيارة",
  },
  diagnosisSnapshot: {
    en: "As recorded when the prescription was written. A later correction to the encounter does not change "
      + "what was checked here.",
    ar: "كما سُجّل وقت كتابة الوصفة. أي تصحيح لاحق للزيارة لا يغيّر ما جرى فحصه هنا.",
  },

  // ---- lines ----
  medicines: { en: "Medicines", ar: "الأدوية" },
  drug: { en: "Medicine", ar: "الدواء" },
  unitPrice: { en: "Unit price", ar: "سعر الوحدة" },
  prescribed: { en: "Prescribed", ar: "الموصوف" },
  dispensedSoFar: { en: "Dispensed", ar: "المصروف" },
  remaining: { en: "Remaining", ar: "المتبقي" },
  dispenseNow: { en: "Dispense now", ar: "الصرف الآن" },
  noPrice: { en: "No price", ar: "بلا سعر" },
  priceUnavailable: { en: "Price could not be read", ar: "تعذّرت قراءة السعر" },
  noIngredient: { en: "Active ingredient not recorded", ar: "المادة الفعالة غير مسجّلة" },
  ingredientUnavailable: {
    en: "Ingredient could not be read",
    ar: "تعذّرت قراءة المادة الفعالة",
  },
  noDuration: { en: "Duration not recorded", ar: "المدة غير مسجّلة" },
  forDays: { en: "for {n} days", ar: "لمدة {n} يوم" },
  substitute: { en: "Substitute", ar: "استبدال" },
  substitutedTo: { en: "Substituted → {drug}", ar: "مستبدل ← {drug}" },
  undoSubstitute: { en: "Undo", ar: "تراجع" },
  fillLine: { en: "Fill the remaining quantity", ar: "صرف الكمية المتبقية" },
  outOfStock: { en: "Out of stock", ar: "غير متوفر" },
  overRemaining: {
    en: "Only {n} left on this line",
    ar: "المتبقي على هذا البند {n} فقط",
  },
  fixQuantities: {
    en: "One line asks for more than is left on it. Correct it before submitting.",
    ar: "أحد البنود يطلب أكثر من المتبقي عليه. صححه قبل الإرسال.",
  },
  authNote: {
    en: "Dispensing issues its own authorization — a record of what was actually handed over, separate from "
      + "the prescription. A substitution lands on that record; the prescription keeps saying what the "
      + "prescriber wrote.",
    ar: "يصدر الصرف تفويضه الخاص — سجلاً لما تم صرفه فعلياً، منفصلاً عن الوصفة. ويُسجَّل الاستبدال على ذلك "
      + "السجل، بينما تظل الوصفة تحمل ما كتبه الطبيب.",
  },

  // ---- money ----
  totals: { en: "What this prescription costs", ar: "تكلفة هذه الوصفة" },
  total: { en: "Prescription total", ar: "إجمالي الوصفة" },
  memberShare: { en: "Patient pays", ar: "يدفع المريض" },
  payerShare: { en: "Payer pays", ar: "يدفع الممول" },
  totalHint: { en: "List price of everything prescribed", ar: "سعر قائمة كل ما وُصف" },
  memberHint: { en: "Their share under this plan", ar: "حصته وفق هذه الخطة" },
  payerHint: { en: "Covered by the benefit", ar: "ما تغطيه المنفعة" },
  notQuoted: { en: "Cannot be quoted", ar: "تعذّر التسعير" },
  pricingLoading: { en: "Pricing…", ar: "جارٍ التسعير…" },
  repricing: { en: "Repricing…", ar: "جارٍ إعادة التسعير…" },
  tier: { en: "Tier {code}", ar: "الشريحة {code}" },
  ofTotal: { en: "{pct}% of {amount}", ar: "{pct}٪ من {amount}" },
  // Which of the two questions the share tiles are answering. The labels change with the basis rather than
  // the figures changing silently under one label.
  basisAll: {
    en: "If all of it is collected",
    ar: "إذا استُلمت بالكامل",
  },
  basisNow: {
    en: "For the {qty} units being dispensed now",
    ar: "مقابل {qty} وحدة تُصرف الآن",
  },
  basisNowOne: {
    en: "For the 1 unit being dispensed now",
    ar: "مقابل وحدة واحدة تُصرف الآن",
  },
  basisNote: {
    en: "The patient and payer shares follow what you are dispensing. They are re-quoted through the plan "
      + "each time you change a quantity — not scaled from the prescription total, because a deductible is "
      + "met before coinsurance applies.",
    ar: "تتبع حصتا المريض والممول ما تصرفه الآن. ويُعاد احتسابهما عبر الخطة مع كل تغيير في الكمية — لا تُشتق "
      + "نسبياً من إجمالي الوصفة، لأن التحمّل يُستوفى قبل تطبيق نسبة المشاركة.",
  },
  pricingFailed: {
    en: "The cost of this prescription could not be worked out. This is NOT a report that it is free — do "
      + "not quote a figure to the patient from this screen.",
    ar: "تعذّر احتساب تكلفة هذه الوصفة. هذا ليس تقريراً بأنها مجانية — لا تُبلغ المريض بأي مبلغ من هذه الشاشة.",
  },
  estimate: {
    en: "An estimate at today's list prices, priced through the same rules a claim is settled by. The final "
      + "amount is set when the claim is adjudicated.",
    ar: "تقدير بأسعار القائمة اليوم، محسوب بالقواعد نفسها التي تُسوّى بها المطالبة. ويُحدَّد المبلغ النهائي "
      + "عند تسوية المطالبة.",
  },

  // ---- the action bar ----
  dispenseAll: { en: "Dispense all", ar: "صرف الكل" },
  clearAll: { en: "Clear", ar: "مسح" },
  audit: { en: "Audit", ar: "مراجعة" },
  auditing: { en: "Checking…", ar: "جارٍ الفحص…" },
  submit: { en: "Submit", ar: "إرسال" },
  selectedCount: { en: "{n} of {total} lines · {qty} units", ar: "{n} من {total} بنود · {qty} وحدة" },
  nothingSelected: { en: "Nothing selected", ar: "لم يُحدد شيء" },
  auditClean: {
    en: "Checked against the server just now — the prescription and the price on this screen are current.",
    ar: "تمت المطابقة مع الخادم الآن — الوصفة والسعر المعروضان محدَّثان.",
  },
  auditMoved: {
    en: "This screen was out of date and has been refreshed: {what}. Check the quantities before submitting.",
    ar: "كانت هذه الشاشة قديمة وتم تحديثها: {what}. راجع الكميات قبل الإرسال.",
  },
  auditFailed: {
    en: "The prescription could not be re-read, so nothing on this screen has been confirmed. Do not treat it "
      + "as current.",
    ar: "تعذّرت إعادة قراءة الوصفة، لذا لم يتم التحقق من أي شيء على هذه الشاشة. لا تعتبرها محدَّثة.",
  },
  driftQty: { en: "quantities dispensed elsewhere", ar: "كميات صُرفت في مكان آخر" },
  driftPrice: { en: "the price", ar: "السعر" },
  driftExpiry: { en: "the validity window", ar: "مدة الصلاحية" },
  driftLines: { en: "which lines are outstanding", ar: "البنود المتبقية" },

  // ---- dispensing ----
  nothing: { en: "Nothing to dispense — enter a quantity.", ar: "لا يوجد ما يُصرف — أدخل كمية." },
  confirmTitle: { en: "Confirm dispense", ar: "تأكيد الصرف" },
  done: { en: "Dispensed.", ar: "تم الصرف." },
  partial: { en: "Partially dispensed.", ar: "تم الصرف جزئياً." },
  replay: { en: "Already recorded — nothing was dispensed twice.", ar: "مسجل مسبقاً — لم يتم الصرف مرتين." },
  fail: { en: "Could not dispense.", ar: "تعذّر الصرف." },

  // ---- the counter's note ----
  noteTitle: { en: "Note on this handover", ar: "ملاحظة على هذا الصرف" },
  noteHint: {
    en: "Optional. What happened at the counter — collection arrangements, a replaced lot, who collected. "
      + "It is recorded with the dispense and is NOT a message to the prescriber.",
    ar: "اختياري. ما جرى عند نقطة الصرف — ترتيبات الاستلام، تغيير التشغيلة، من استلم. يُسجَّل مع الصرف وليس "
      + "رسالة إلى الطبيب الواصف.",
  },
  noteTooLong: { en: "Keep it under 500 characters.", ar: "أبقِها دون 500 حرف." },

  // ---- printing ----
  print: { en: "Print", ar: "طباعة" },
  printTitle: { en: "Dispense record", ar: "سجل الصرف" },
  printHint: {
    en: "Print what was just handed over. The payer-side authorization number is issued by the approval team "
      + "moments later and is not on this slip — the prescription number is the reference both sides share.",
    ar: "اطبع ما تم صرفه للتو. يصدر رقم التفويض لدى الممول من فريق الموافقات بعد لحظات ولا يظهر على هذه "
      + "القسيمة — ورقم الوصفة هو المرجع المشترك بين الطرفين.",
  },
  routed: {
    en: "Sent to the approval team. The medicine has NOT been handed over — the substitution you chose is "
      + "outside the approved list, so someone qualified has to decide it.",
    ar: "أُرسل إلى فريق الموافقات. لم يُصرف الدواء — فالبديل الذي اخترته خارج القائمة المعتمدة، وعلى جهة "
      + "مختصة أن تبتّ فيه.",
  },
  expiredBody: {
    en: "This prescription is past the window it was written for, so nothing on it can be dispensed. The "
      + "approval team can revalidate it from the search screen.",
    ar: "تجاوزت هذه الوصفة المدة المحددة لها، فلا يمكن صرف أي شيء منها. يمكن لفريق الموافقات إعادة تفعيلها "
      + "من شاشة البحث.",
  },
} satisfies Record<string, Localized>;

/**
 * One prescription, on its own page.
 *
 * <b>Why a page and not the side panel it replaces.</b> The panel sat beside the search results in a column
 * roughly a third of the viewport, so the medicines, the quantities and the money competed for width with a
 * table the pharmacist had already finished with. Dispensing is the task; the search is how you get to it.
 * A page also gives the prescription a URL, which is what lets a pharmacist reopen the one they were on
 * after a reload, or send it to a colleague.
 *
 * <b>Prescribed and dispensed are shown side by side, never subtracted into one number.</b> "14 of 30" and
 * "16 remaining" answer different questions, and a counter that only shows the remainder cannot tell a
 * partially-collected course from a fresh one.
 *
 * <b>The patient is NAMED, not tokenised.</b> The masked ref belongs on a worklist, where the question is
 * "which row"; here the question is "is this the person in front of me", and a pharmacist confirms that
 * against a name. It comes from the profile strip every clinical screen uses — one server-side, min-necessary,
 * PHI-audited projection — rather than a name field bolted onto the pharmacy contract.
 */
export function PrescriptionPage({ rxNo }: { rxNo: string }) {
  const api = useApi();
  const t = useLoc();
  const navigate = useNavigate();
  const { date } = useFormat();

  const [rx, setRx] = useState<Prescription | null>(null);
  const [state, setState] = useState<"loading" | "ready" | "missing">("loading");
  /** ICD code → title, resolved from master data. A bare "J01.0" is not a diagnosis to anyone at a counter. */
  const [icd, setIcd] = useState<Map<string, string>>(new Map());
  /**
   * Did the catalogue answer?
   *
   * <p>Its own state, because "no ingredient recorded" and "we could not ask" are different facts and the
   * screen must not print the first when the second is true. 2,786 of 31,651 products genuinely record none;
   * an outage is not one of them.</p>
   */
  const [ingredientsRead, setIngredientsRead] = useState(true);

  /**
   * Re-read the prescription.
   *
   * <b>A failed RE-read never blanks the page.</b> The first load has nothing to fall back on, so a failure
   * there is "could not be opened". Once a prescription is on screen the pharmacist has a patient in front of
   * them and is working from it — replacing all of that with an error because a refresh timed out takes away
   * the thing they were reading and tells them nothing they can act on. The audit says the re-read failed and
   * the screen stays as it was, which is the honest state: stale, and known to be stale.
   */
  const load = useCallback(async (): Promise<Prescription | null> => {
    const fail = () => { setState((prev) => (prev === "loading" ? "missing" : prev)); return null; };
    try {
      const rows = await api.pharmacySearch({ rxNo });
      const found = rows.find((p) => p.rxNo === rxNo) ?? rows[0] ?? null;
      if (!found) return fail();

      // The ingredient join. Master data owns what a product contains, so the browser — which holds an
      // authorised read of both — puts the two together, exactly as icdTitles and branchLabels do.
      //
      // A failure here does NOT fail the page: the prescription is still dispensable without knowing the
      // molecule. It is recorded as a failed read so the row can say so instead of claiming the catalogue
      // holds nothing.
      let ingredients = new Map<string, string>();
      try {
        ingredients = await api.drugIngredients(found.lines.map((l) => l.drug.code));
        setIngredientsRead(true);
      } catch {
        setIngredientsRead(false);
      }
      const withIngredients: Prescription = {
        ...found,
        lines: found.lines.map((l) => ({
          ...l,
          activeIngredient: l.activeIngredient ?? ingredients.get(l.drug.code) ?? null,
        })),
      };
      setRx(withIngredients);
      setState("ready");
      // Titles for the snapshot's codes — the same client-side join the ingredient uses, and for the same
      // reason: what a code MEANS is master data's fact, not pharmacy's.
      if (found.diagnosisCodes.length > 0) setIcd(await api.icdTitles(found.diagnosisCodes));
      return withIngredients;
    } catch {
      return fail();
    }
  }, [api, rxNo]);

  useEffect(() => { setState("loading"); void load(); }, [load]);

  const days = rx?.expiresAt
    ? Math.ceil((Date.parse(rx.expiresAt) - Date.now()) / 86_400_000)
    : null;

  return (
    <>
      <PageHeader title={rxNo} />
      <Button variant="ghost" size="sm" onClick={() => navigate("/pharmacy/dispense")}>
        {t(S.back)}
      </Button>

      {state === "loading" && <p className="muted">{t(S.loading)}</p>}
      {state === "missing" && <InlineAlert tone="warn">{t(S.notFound)}</InlineAlert>}

      {state === "ready" && rx && (
        <div className="rx-page">
          <Card as="section" className="rx-head">
            {/* The identity strip, not a masked token. Same component, same projection and same audit trail
                as every other clinical screen — a second way of naming a patient is a second thing to keep
                in step with the permission matrix. */}
            <PatientContextBar beneficiaryId={rx.patient.id} namedAllergens />

            <div className="rx-head-meta">
              <h2 className="rx-head-no tnum">{rx.rxNo}</h2>
              <StatusChip kind={rx.expired ? "bad" : rx.status.kind} label={t(rx.status.label)} />
              <dl className="rx-meta">
                <div>
                  <dt>{t(S.prescriber)}</dt>
                  <dd>{t(rx.prescriber.label)}</dd>
                </div>
                <div>
                  <dt>{t(S.written)}</dt>
                  <dd className="tnum">{date(rx.submittedAt)}</dd>
                </div>
                <div className="rx-meta-wide">
                  <dt>{t(S.diagnosis)}</dt>
                  {/* The snapshot, resolved to titles. Codes only would make the pharmacist look them up, and
                      an empty list says so in words — "no diagnosis recorded" and "a diagnosis nobody
                      displayed" are different facts and only one is a reason to ring the prescriber. */}
                  <dd>
                    {rx.diagnosisCodes.length === 0 ? (
                      <span className="rx-unrecorded">{t(S.noDiagnosis)}</span>
                    ) : (
                      <span className="rx-dx">
                        {rx.diagnosisCodes.map((code) => (
                          <span
                            key={code}
                            className={code === rx.primaryIcdCode ? "rx-dx-chip rx-dx-chip--primary" : "rx-dx-chip"}
                            title={t(S.diagnosisSnapshot)}
                          >
                            <span className="tnum">{code}</span>
                            {icd.get(code) ? ` · ${icd.get(code)}` : ""}
                          </span>
                        ))}
                      </span>
                    )}
                  </dd>
                </div>
                <div>
                  <dt>{t(S.expires)}</dt>
                  {/* The date AND how long is left. A date alone makes the pharmacist do the arithmetic, and
                      "expires 13 Aug" is the fact that matters least — "2 days left" is what changes what
                      they say to the patient about coming back for the rest. */}
                  <dd className="tnum">
                    {rx.expiresAt ? date(rx.expiresAt) : <span className="muted">{t(S.expiresUnknown)}</span>}
                    {days !== null && (
                      <span className={days <= 2 ? "rx-meta-note rx-meta-note--soon" : "rx-meta-note"}>
                        {rx.expired || days < 0
                          ? t(S.lapsed)
                          : days === 0
                            ? t(S.lastDay)
                            : t(S.daysLeft).replace("{n}", String(days))}
                      </span>
                    )}
                  </dd>
                </div>
              </dl>
            </div>
          </Card>

          <DispenseBody key={rx.id} rx={rx} reload={load} ingredientsRead={ingredientsRead} />
        </div>
      )}
    </>
  );
}

/** What one line's numbers add up to, in one place so the row, the totals and the confirm text agree. */
type LineDraft = { quantity: number; substitute?: Coded; substitutionReason?: string };

function DispenseBody({
  rx, reload, ingredientsRead,
}: {
  rx: Prescription;
  reload: () => Promise<Prescription | null>;
  /** False when the catalogue could not be reached — the row then says so rather than claiming it holds
   *  no ingredient for the medicine. */
  ingredientsRead: boolean;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const { lang } = useTheme();
  const { money } = useFormat();
  const write = useWrite();

  // Quantities default to ZERO, never to the remaining amount. Pre-filling the maximum makes "dispense
  // everything" the path of least resistance and turns a partial dispense — the common case when stock is
  // short — into a correction of a number that already looked right. `Dispense all` is the explicit act.
  const [draft, setDraft] = useState<Record<string, LineDraft>>({});
  const [substituting, setSubstituting] = useState<PrescriptionLine | null>(null);
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState("");
  /** What was handed over in the last successful dispense, so the counter can print it. */
  const [lastDispensed, setLastDispensed] = useState<
    { lines: { name: string; quantity: number }[]; at: string; note: string } | null
  >(null);

  const [pricing, setPricing] = useState<RxPricing | null>(null);
  const [priceState, setPriceState] = useState<"loading" | "repricing" | "ready" | "error">("loading");

  /**
   * Which response is allowed to land.
   *
   * <p>Typing "14" fires a quote for 1 and a quote for 14, and the network does not promise to answer in that
   * order. Without a sequence the counter would occasionally settle on the share for a quantity nobody
   * entered — a wrong figure that looks entirely ordinary. Only the newest request may write.</p>
   */
  const priceSeq = useRef(0);

  const loadPricing = useCallback(async (
    dispenseNow?: Record<string, number>,
    mode: "loading" | "repricing" = "loading",
  ) => {
    const seq = ++priceSeq.current;
    setPriceState(mode);
    try {
      const next = await api.prescriptionPricing(rx.id, dispenseNow);
      if (seq !== priceSeq.current) return;
      setPricing(next);
      setPriceState("ready");
    } catch {
      if (seq !== priceSeq.current) return;
      // A failed re-quote clears the figures rather than leaving the previous ones beside a changed
      // quantity. A stale share next to a new number is not a smaller error than no share at all — it is
      // the one a pharmacist would read out to a patient without hesitating.
      setPricing(null);
      setPriceState("error");
    }
  }, [api, rx.id]);

  const remaining = (l: PrescriptionLine) => Math.max(0, l.quantity - l.dispensed);
  const qty = (id: string) => draft[id]?.quantity ?? 0;

  /**
   * The basis for the cost share: what is about to be handed over, by line.
   *
   * <p>Clamped to what is left on each line, because that is what the server will accept and what will
   * actually be dispensed. Quoting a member for 20 when 14 can be handed over states a debt that cannot be
   * incurred.</p>
   */
  const dispenseNow = useMemo(() => {
    const basis: Record<string, number> = {};
    for (const l of rx.lines) {
      if (l.outOfStock) continue;
      const q = Math.min(draft[l.id]?.quantity ?? 0, Math.max(0, l.quantity - l.dispensed));
      if (q > 0) basis[l.id] = q;
    }
    return basis;
  }, [draft, rx.lines]);

  // Serialised so the effect below re-runs on a CHANGE of basis rather than on every render — a fresh object
  // identity each render would put the counter into a permanent re-quote loop.
  const basisKey = useMemo(
    () => Object.entries(dispenseNow).sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => `${k}:${v}`).join("|"),
    [dispenseNow],
  );

  /**
   * Re-quote the split whenever what is being handed over changes.
   *
   * <p><b>Why the server is asked again instead of the figure being scaled.</b> The split runs a deductible
   * before a copay before coinsurance (`libs/money`), so the member's share of 7 units is not half their
   * share of 14. A browser multiplying the whole-prescription figure by a ratio would produce a confident
   * number that the claim later contradicts — and the counter is the one place in the platform with no
   * reviewer in the loop.</p>
   *
   * <p>Debounced because each quote is a live eligibility check: a re-quote per keystroke would put a
   * benefit engine behind the number keys.</p>
   */
  const first = useRef(true);
  useEffect(() => {
    if (first.current) {
      first.current = false;
      void loadPricing(undefined, "loading");
      return;
    }
    const id = window.setTimeout(() => {
      void loadPricing(basisKey ? dispenseNow : undefined, "repricing");
    }, 400);
    return () => window.clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [basisKey, loadPricing]);

  const unitPrice = useCallback(
    (l: PrescriptionLine) =>
      l.unitPriceEgp ?? pricing?.lines.find((p) => p.prescriptionLineId === l.id)?.unitPriceEgp ?? null,
    [pricing],
  );

  const pending = (): DispenseLine[] =>
    rx.lines
      .filter((l) => !l.outOfStock && qty(l.id) > 0)
      .map((l) => ({
        lineId: l.id,
        quantity: Math.min(qty(l.id), remaining(l)),
        substitute: draft[l.id]?.substitute,
        substitutionReason: draft[l.id]?.substitutionReason,
      }));

  // A line asking for more than is left on it. Reported per-field AND on the bar, because a pharmacist who
  // has scrolled past the offending row needs to know why Submit will not move.
  const overLines = rx.lines.filter((l) => qty(l.id) > remaining(l));

  const selected = useMemo(() => {
    const lines = rx.lines.filter((l) => !l.outOfStock && qty(l.id) > 0);
    return { count: lines.length, units: lines.reduce((s, l) => s + Math.min(qty(l.id), remaining(l)), 0) };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [draft, rx.lines]);

  const fillable = rx.lines.filter((l) => !l.outOfStock && remaining(l) > 0);

  function setQty(id: string, quantity: number) {
    setDraft((d) => ({ ...d, [id]: { ...d[id], quantity: Math.max(0, quantity) } }));
  }

  async function dispense() {
    const lines = pending();
    if (lines.length === 0) return;
    setBusy(true);
    try {
      const res = await api.dispense({
        prescriptionId: rx.id,
        idempotencyKey: write.idempotencyKey,
        lines,
        note: note.trim() || undefined,
      });
      if (res.replayed) toast(t(S.replay), "info");
      else toast(t(res.linesOutstanding === 0 ? S.done : S.partial), "ok");
      // Captured BEFORE the reload, because the reload moves the quantities on. This is what the printed
      // slip describes: what left the counter just now, not what the prescription says afterwards.
      setLastDispensed({
        lines: lines.map((l) => ({
          name: productName(t(rx.lines.find((x) => x.id === l.lineId)?.drug.label ?? { en: "", ar: "" })),
          quantity: l.quantity,
        })),
        at: new Date().toISOString(),
        note: note.trim(),
      });
      setDraft({});
      setNote("");
      await reload();
      await loadPricing();
    } catch (e) {
      // A substitution outside the approved list is not a failure to dispense — the server routed it to the
      // approval team and said so. Reporting it as "could not dispense" would send the pharmacist looking
      // for a fault, when what actually happened is that a decision is now pending.
      const problem = writeErrorMessage(e);
      const routed = problem.problemType === "urn:hbmp:substitution-not-approved";
      // "info", not "bad": nothing failed. The server refused to hand over an off-formulary substitute and
      // routed the question to the approval team, which is the control working — telling the pharmacist it
      // is an error sends them looking for a fault instead of telling the patient to come back.
      toast(routed ? t(S.routed) : writeErrorText(problem, lang) ?? t(S.fail), routed ? "info" : "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Card as="section" className="rx-card">
        <div className="rx-card-head">
          <h2 className="section-h">{t(S.medicines)}</h2>
          {!rx.expired && fillable.length > 0 && (
            <div className="rx-card-actions">
              <Button
                variant="secondary"
                size="sm"
                onClick={() =>
                  setDraft((d) => {
                    const next = { ...d };
                    for (const l of fillable) next[l.id] = { ...next[l.id], quantity: remaining(l) };
                    return next;
                  })
                }
              >
                {t(S.dispenseAll)}
              </Button>
              <Button variant="ghost" size="sm" onClick={() => setDraft({})}>{t(S.clearAll)}</Button>
            </div>
          )}
        </div>

        {rx.expired && <InlineAlert tone="warn">{t(S.expiredBody)}</InlineAlert>}
        {/* Only once a substitution is actually in play. An unconditional paragraph of policy above every
            prescription is a paragraph nobody reads. */}
        {Object.values(draft).some((d) => d.substitute) && (
          <InlineAlert tone="info">{t(S.authNote)}</InlineAlert>
        )}

        <div className="rx-dispense-scroll">
          <table className="rx-dispense-table">
            <thead>
              <tr>
                <th scope="col">{t(S.drug)}</th>
                {/* Its own column, with the header off-screen. The button lived inside the Medicine cell and
                    the widest column in the table pushed it to a different x on every row — an action that
                    moves as you scan down is one you have to hunt for. A header-less column is not an option
                    either: a screen reader announcing "column 2" tells you nothing. */}
                <th scope="col" className="rx-col-act"><span className="sr-only">{t(S.substitute)}</span></th>
                <th scope="col" className="rx-num">{t(S.unitPrice)}</th>
                {/* Prescribed and dispensed stay APART. Collapsing them into "remaining" alone loses whether
                    this is a fresh course or one the patient has been collecting for a fortnight. */}
                <th scope="col" className="rx-num">{t(S.prescribed)}</th>
                <th scope="col" className="rx-num">{t(S.dispensedSoFar)}</th>
                <th scope="col" className="rx-num">{t(S.remaining)}</th>
                <th scope="col" className="rx-col-qty">{t(S.dispenseNow)}</th>
              </tr>
            </thead>
            <tbody>
              {rx.lines.map((l) => {
                const sub = draft[l.id]?.substitute;
                const price = unitPrice(l);
                const left = remaining(l);
                return (
                  <tr key={l.id} className={qty(l.id) > 0 ? "rx-row rx-row--picked" : "rx-row"}>
                    <td>
                      {/* No flex wrapper any more — the substitute control moved to its own column, so this
                          cell is just the medicine and its detail, stacked. */}
                      <div className="rx-drug-main">
                          <strong className="rx-drug-name">{productName(t(l.drug.label))}</strong>
                          {/* The MOLECULE, under the trade name. Two trade names holding one ingredient is
                              the commonest prescribing duplication, and it is what a pharmacist checks the
                              packet against. */}
                          {/* Three states, not two. A molecule; "not recorded", which is a fact about the
                              catalogue; and "could not be read", which is a fact about the network — and
                              printing the second when the third is true is the failed-read-as-finding
                              mistake the clinical checks have an ADR about. */}
                          <span className={l.activeIngredient ? "rx-drug-ing" : "rx-drug-ing rx-unrecorded"}>
                            {l.activeIngredient
                              ? productName(l.activeIngredient)
                              : ingredientsRead ? t(S.noIngredient) : t(S.ingredientUnavailable)}
                          </span>
                          <span className="rx-drug-sig">
                            {[l.dose, l.route, l.frequency].filter(Boolean).join(" · ")}
                            {l.durationDays
                              ? ` · ${t(S.forDays).replace("{n}", String(l.durationDays))}`
                              : null}
                          </span>
                          {/* An absent duration says so. A blank reads as a one-day course, and only one of
                              those is a reason to ring the prescriber before handing anything over. */}
                          {!l.durationDays && <span className="rx-drug-sig rx-unrecorded">{t(S.noDuration)}</span>}
                          {sub && (
                            <span className="rx-sub-note">
                              {t(S.substitutedTo).replace("{drug}", productName(t(sub.label)))}
                              <Button
                                variant="ghost"
                                size="sm"
                                onClick={() => setDraft((d) => ({ ...d, [l.id]: { ...d[l.id], substitute: undefined, substitutionReason: undefined } }))}
                              >
                                {t(S.undoSubstitute)}
                              </Button>
                            </span>
                          )}
                      </div>
                    </td>
                    <td className="rx-col-act">
                      {/* The accessible NAME includes the medicine. One identically-labelled button per row
                          is unusable by keyboard or screen reader on a five-line prescription — and picking
                          the wrong row swaps the wrong drug. */}
                      <button
                        type="button"
                        className="rx-icon-btn"
                        aria-label={`${t(S.substitute)} — ${productName(t(l.drug.label))}`}
                        title={t(S.substitute)}
                        onClick={() => setSubstituting(l)}
                      >
                        <Icon name="replace" width={18} height={18} aria-hidden="true" />
                      </button>
                    </td>
                    <td className="rx-num tnum">
                      {/* "No price" is the catalogue's answer; "could not be read" is the pricing call
                          failing. A counter that cannot tell them apart quotes a gap as a fact. */}
                      {price === null
                        ? (
                          <span className="rx-unrecorded">
                            {priceState === "error" ? t(S.priceUnavailable) : t(S.noPrice)}
                          </span>
                        )
                        : money(price)}
                    </td>
                    <td className="rx-num tnum">{l.quantity}</td>
                    <td className="rx-num tnum">{l.dispensed}</td>
                    <td className="rx-num tnum">{left}</td>
                    <td className="rx-col-qty">
                      {l.outOfStock ? (
                        <StatusChip kind="warn" label={t(S.outOfStock)} />
                      ) : (
                        <div className="rx-qty">
                          {/* The label names the medicine for assistive tech and is HIDDEN on screen. Five
                              rows each printing "Dispense now — Augmentin 600mg vial for i.v 600 mg vial"
                              above a number box is the column header repeated five times, at three times the
                              row height — while a screen reader still needs it, because "edit, 0" five times
                              over is not navigable. */}
                          {/* Over-quantity is REPORTED, not silently clamped. `max` on a number input does
                              not stop typing, and rewriting 17 to 14 under the pharmacist's hand changes the
                              figure they are about to confirm without telling them — which is the same
                              defect as an audit that silently corrects a row. What they typed stays, the
                              field says what is wrong, and Submit refuses until it is fixed rather than
                              sending it for the server to reject with a 422. */}
                          <InputField
                            label={`${t(S.dispenseNow)} — ${productName(t(l.drug.label))}`}
                            hideLabel
                            type="number"
                            min={0}
                            max={left}
                            value={qty(l.id)}
                            error={qty(l.id) > left ? t(S.overRemaining).replace("{n}", String(left)) : undefined}
                            disabled={rx.expired || left === 0}
                            onChange={(e) => setQty(l.id, Number(e.currentTarget.value))}
                          />
                          {/* The tick fills THIS line's remainder. It replaces a second wordy button per row:
                              the quantity is almost always "all of it", and a counter types less when the
                              common case is one tap. Named for the medicine, like every other row control. */}
                          <button
                            type="button"
                            className={qty(l.id) === left && left > 0 ? "rx-icon-btn rx-icon-btn--on" : "rx-icon-btn"}
                            aria-label={`${t(S.fillLine)} — ${productName(t(l.drug.label))}`}
                            title={t(S.fillLine)}
                            disabled={rx.expired || left === 0}
                            aria-pressed={qty(l.id) === left && left > 0}
                            onClick={() => setQty(l.id, qty(l.id) === left ? 0 : left)}
                          >
                            <Icon name="check2" width={18} height={18} aria-hidden="true" />
                          </button>
                        </div>
                      )}
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      </Card>

      <Card as="section" className="rx-card">
        <div className="rx-card-head">
          <h2 className="section-h">{t(S.noteTitle)}</h2>
        </div>
        <TextareaField
          label={t(S.noteTitle)}
          hideLabel
          help={t(S.noteHint)}
          rows={2}
          maxLength={500}
          value={note}
          error={note.length > 500 ? t(S.noteTooLong) : undefined}
          onChange={(e) => setNote(e.currentTarget.value)}
        />
      </Card>

      <PricingTiles pricing={pricing} state={priceState} units={selected.units} />

      {lastDispensed && <PrintSlip rx={rx} dispensed={lastDispensed} />}

      <ActionBar
        rx={rx}
        selected={selected}
        invalid={overLines.length > 0}
        invalidText={t(S.fixQuantities)}
        busy={busy}
        lastDispensed={lastDispensed}
        onSubmit={() => {
          if (overLines.length > 0) { toast(t(S.fixQuantities), "bad"); return; }
          if (pending().length === 0) { toast(t(S.nothing), "bad"); return; }
          void dispense();
        }}
        reload={reload}
        reloadPricing={() => loadPricing(basisKey ? dispenseNow : undefined, "repricing")}
        pricing={pricing}
      />

      {substituting && (
        <SubstituteModal
          open
          onOpenChange={(open) => { if (!open) setSubstituting(null); }}
          drug={substituting.drug}
          onChosen={(drug, reason) => {
            setDraft((d) => ({
              ...d,
              [substituting.id]: { ...d[substituting.id], quantity: d[substituting.id]?.quantity ?? 0, substitute: drug, substitutionReason: reason },
            }));
            setSubstituting(null);
          }}
        />
      )}

    </>
  );
}

/**
 * What the counter hands over on paper.
 *
 * <b>It describes the handover, not the prescription.</b> The quantities are the ones that just left the
 * counter, captured before the reload moved them on — a slip reprinting the prescription's running totals
 * would tell a patient collecting the second half of a course that they received all of it.
 *
 * <b>What it deliberately does not carry.</b> The payer-side authorization number. Issuance is asynchronous
 * (the dispense enqueues to approvals, which mints AUTH-YYYY-NNNNNN moments later) and a pharmacist's role
 * does not hold `auth:read`, so the number is not the counter's to print. The prescription number is on the
 * slip instead, which is the reference the counter, the patient and the payer all share — and printing a
 * blank where an authorization number belongs would be worse than printing neither.
 */
function PrintSlip({
  rx, dispensed,
}: {
  rx: Prescription;
  dispensed: { lines: { name: string; quantity: number }[]; at: string; note: string };
}) {
  const t = useLoc();
  const { date } = useFormat();

  return (
    <section className="rx-slip" aria-hidden="true">
      <h1>{t(S.printTitle)}</h1>
      <div className="rx-slip-meta">
        <span>{t(S.drug)}</span><span>{rx.rxNo}</span>
        <span>{t(S.prescriber)}</span><span>{t(rx.prescriber.label)}</span>
        <span>{t(S.written)}</span><span>{date(rx.submittedAt)}</span>
        <span>{t(S.dispensedSoFar)}</span><span>{date(dispensed.at)}</span>
      </div>

      <table>
        <thead>
          <tr>
            <th scope="col">{t(S.drug)}</th>
            <th scope="col">{t(S.dispensedSoFar)}</th>
          </tr>
        </thead>
        <tbody>
          {dispensed.lines.map((l) => (
            <tr key={l.name}>
              <td>{l.name}</td>
              <td>{l.quantity}</td>
            </tr>
          ))}
        </tbody>
      </table>

      {dispensed.note && (
        <p className="rx-slip-foot"><strong>{t(S.noteTitle)}:</strong> {dispensed.note}</p>
      )}
      <p className="rx-slip-foot">{t(S.printHint)}</p>
    </section>
  );
}

/**
 * The submit bar, at the end of the page.
 *
 * <b>It sits in normal flow.</b> It was pinned to the foot of the viewport so the running total stayed
 * visible while quantities were typed; in practice a bar floating over the content costs a strip of every
 * counter screen and reads as an overlay on a page that is otherwise a stack of cards. It goes after the
 * thing it acts on.
 *
 * <b>What Audit does, and what it does not.</b> It re-reads the prescription and the price from the server and
 * reports what moved: a quantity dispensed at another branch, a price that has changed, a validity window that
 * has since lapsed. It fixes the SCREEN, which is the thing that goes stale while a counter is open; it does
 * not edit the prescription, and a control on a dispensing screen that quietly corrected clinical data would
 * be a worse idea than the staleness it cured.
 */
function ActionBar({
  rx, selected, invalid, invalidText, busy, lastDispensed, onSubmit, reload, reloadPricing, pricing,
}: {
  rx: Prescription;
  selected: { count: number; units: number };
  /** A line asks for more than is left on it. Submit is refused until it is corrected. */
  invalid: boolean;
  invalidText: string;
  busy: boolean;
  /** What left the counter on the last successful submit. Null until there has been one — the slip
   *  describes a handover that happened, so there is nothing to print before one has. */
  lastDispensed: { lines: { name: string; quantity: number }[]; at: string; note: string } | null;
  onSubmit: () => void;
  reload: () => Promise<Prescription | null>;
  reloadPricing: () => Promise<void>;
  pricing: RxPricing | null;
}) {
  const t = useLoc();
  const [auditing, setAuditing] = useState(false);
  const [outcome, setOutcome] = useState<{ tone: "ok" | "warn" | "bad"; text: string } | null>(null);

  async function audit() {
    setAuditing(true);
    setOutcome(null);
    const before = {
      dispensed: rx.lines.map((l) => `${l.id}:${l.dispensed}`).join("|"),
      lineIds: rx.lines.map((l) => l.id).sort().join("|"),
      expiresAt: rx.expiresAt ?? "",
      expired: rx.expired,
      total: pricing?.totalEgp ?? null,
      member: pricing?.memberShareEgp ?? null,
    };
    try {
      const fresh = await reload();
      await reloadPricing();
      if (!fresh) { setOutcome({ tone: "bad", text: t(S.auditFailed) }); return; }

      const moved: string[] = [];
      if (fresh.lines.map((l) => `${l.id}:${l.dispensed}`).join("|") !== before.dispensed) moved.push(t(S.driftQty));
      if (fresh.lines.map((l) => l.id).sort().join("|") !== before.lineIds) moved.push(t(S.driftLines));
      if ((fresh.expiresAt ?? "") !== before.expiresAt || fresh.expired !== before.expired) moved.push(t(S.driftExpiry));

      // The price is compared AFTER its own refetch, so this reads the value the tiles now show rather than
      // the one they showed a moment ago.
      const priced = await Promise.resolve(pricing);
      if (priced && (priced.totalEgp ?? null) !== before.total) moved.push(t(S.driftPrice));

      setOutcome(moved.length === 0
        ? { tone: "ok", text: t(S.auditClean) }
        : { tone: "warn", text: t(S.auditMoved).replace("{what}", moved.join(", ")) });
    } catch {
      setOutcome({ tone: "bad", text: t(S.auditFailed) });
    } finally {
      setAuditing(false);
    }
  }

  return (
    <div className="rx-actionbar" role="region" aria-label={t(S.medicines)}>
      {/* aria-live so the audit result is announced without moving focus off the quantity being typed. */}
      <div className="rx-actionbar-msg" aria-live="polite">
        {invalid && <InlineAlert tone="bad">{invalidText}</InlineAlert>}
        {!invalid && outcome && (
          <InlineAlert tone={outcome.tone === "ok" ? "ok" : outcome.tone === "warn" ? "warn" : "bad"}>
            {outcome.text}
          </InlineAlert>
        )}
      </div>

      <div className="rx-actionbar-row">
        <span className={selected.count > 0 ? "rx-actionbar-count rx-actionbar-count--on" : "rx-actionbar-count"}>
          {selected.count === 0
            ? t(S.nothingSelected)
            : t(S.selectedCount)
                .replace("{n}", String(selected.count))
                .replace("{total}", String(rx.lines.length))
                .replace("{qty}", String(selected.units))}
        </span>

        <div className="rx-actionbar-buttons">
          <Button variant="ghost" loading={auditing} onClick={() => void audit()}>
            {auditing ? t(S.auditing) : t(S.audit)}
          </Button>
          {/* Print appears only AFTER something has been handed over. A print button on an empty counter
              would produce a slip describing nothing, and the one thing a receipt must not do is exist for a
              transaction that did not happen. */}
          {lastDispensed && (
            <Button variant="secondary" leadingIcon={<Icon name="doc" />} onClick={() => window.print()}>
              {t(S.print)}
            </Button>
          )}
          <Button
            variant="primary"
            loading={busy}
            disabled={rx.expired || invalid || selected.count === 0}
            leadingIcon={<Icon name="check2" />}
            onClick={onSubmit}
          >
            {t(S.submit)}
          </Button>
        </div>
      </div>
    </div>
  );
}

/**
 * The three figures the counter quotes.
 *
 * <b>Why an unknown split is never rendered as 0.00.</b> At a dispensing counter a zero reads as "free". A
 * beneficiary told their medication is free — who then receives a bill, or who declines something they could
 * have afforded — has been misinformed by a screen that looked confident. So the member and payer tiles show
 * "cannot be quoted" with the reason whenever the plan does not price pharmacy at this provider's tier, and
 * the total is still shown because the list price IS known.
 *
 * <b>Why it says "estimate".</b> The split comes from the same rules a claim is settled by, but the claim is
 * adjudicated later against accumulators this screen cannot see — a deductible partly met this morning, a
 * limit reached elsewhere. Presenting it as final would be a promise the platform cannot keep.
 *
 * <b>Why the total is fixed and the two shares move.</b> They answer different questions. The total is what
 * the prescriber wrote and does not change while a pharmacist works; the shares are what somebody is about to
 * pay, and a partial dispense is the ordinary case. Leaving the shares on the whole-prescription figure while
 * half of it is handed over overstates what is owed at that moment, by exactly the part not being collected.
 * Each tile's hint says which basis it is on, so the two are never confused for one another.
 */
function PricingTiles({
  pricing, state, units,
}: {
  pricing: RxPricing | null;
  state: "loading" | "repricing" | "ready" | "error";
  /** Units entered at the counter — the figure the share hint names, so it is unambiguous what was priced. */
  units: number;
}) {
  const t = useLoc();
  const { money } = useFormat();

  const amount = (v: number | null | undefined) => (v === null || v === undefined ? null : money(v));

  // The denominator is what the split was QUOTED ON, not the prescription total. On a partial dispense those
  // differ, and dividing by the total would report a 20% coinsurance as 10% — a percentage that contradicts
  // the plan the patient is on.
  const basis = pricing?.quotedOnEgp ?? pricing?.totalEgp ?? null;
  const share = (v: number | null | undefined) =>
    v === null || v === undefined || !basis
      ? null
      : t(S.ofTotal)
          .replace("{pct}", String(Math.round((v / basis) * 100)))
          .replace("{amount}", money(basis));

  const onNow = pricing?.quotedOnDispenseNow === true;
  const shareHint = !onNow
    ? t(S.basisAll)
    : units === 1
      ? t(S.basisNowOne)
      : t(S.basisNow).replace("{qty}", String(units));

  return (
    <Card as="section" className="rx-card">
      <div className="rx-card-head">
        <h2 className="section-h">{t(S.totals)}</h2>
        {pricing?.tierCode && (
          <StatusChip kind="neu" label={t(S.tier).replace("{code}", pricing.tierCode)} />
        )}
      </div>

      {state === "loading" && <p className="muted">{t(S.pricingLoading)}</p>}
      {/* A failed fetch is never rendered as free. */}
      {state === "error" && <InlineAlert tone="bad">{t(S.pricingFailed)}</InlineAlert>}

      {(state === "ready" || state === "repricing") && pricing && (
        <>
          {/* aria-busy, not a spinner that replaces the figures: blanking the tiles on every keystroke would
              make the section flicker, and a pharmacist would learn to read whatever appeared next. */}
          <div className={state === "repricing" ? "rx-tiles rx-tiles--busy" : "rx-tiles"} aria-busy={state === "repricing"}>
            <Tile
              label={t(S.total)}
              hint={t(S.totalHint)}
              value={amount(pricing.totalEgp)}
              fallback={t(S.notQuoted)}
            />
            <Tile
              label={t(S.memberShare)}
              hint={shareHint}
              value={amount(pricing.memberShareEgp)}
              note={share(pricing.memberShareEgp)}
              fallback={t(S.notQuoted)}
              emphasis
            />
            <Tile
              label={t(S.payerShare)}
              hint={shareHint}
              value={amount(pricing.payerShareEgp)}
              note={share(pricing.payerShareEgp)}
              fallback={t(S.notQuoted)}
            />
          </div>

          {/* aria-live so a screen-reader user typing a quantity hears the share change, rather than the
              figures moving silently behind them. */}
          <p className="muted rx-estimate" aria-live="polite">
            {state === "repricing" ? t(S.repricing) : t(S.basisNote)}
          </p>

          {pricing.reason && <InlineAlert tone="warn">{pricing.reason}</InlineAlert>}
          {pricing.determinate && <p className="muted rx-estimate">{t(S.estimate)}</p>}
        </>
      )}
    </Card>
  );
}

function Tile({
  label, hint, value, note, fallback, emphasis,
}: {
  label: string; hint: string; value: string | null; note?: string | null; fallback: string; emphasis?: boolean;
}) {
  return (
    <div className={emphasis ? "rx-tile rx-tile--emphasis" : "rx-tile"}>
      <span className="rx-tile-label">{label}</span>
      {value === null
        // Not a number, and not styled like one: an unquotable figure must not sit in the same visual slot
        // as a real amount, or it will be read as one at a glance.
        ? <span className="rx-tile-unknown">{fallback}</span>
        : <span className="rx-tile-value tnum">{value}</span>}
      {/* The share of the total, only when there IS a total to be a share of. It is what turns two amounts
          into a split a pharmacist can sanity-check out loud. */}
      {value !== null && note && <span className="rx-tile-note tnum">{note}</span>}
      <span className="rx-tile-hint">{hint}</span>
    </div>
  );
}

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, Icon, InlineAlert, InputField, StatusChip, useTheme, useToast } from "@mersal/design-system";
import type { InvestigationOrder, InvestigationOrderLine, Localized, OrderPricing } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useWrite, writeErrorText } from "../../api/useWrite";
import { writeErrorMessage } from "../../api/writeError";
import { PatientContextBar } from "../PatientProfile";
import { PageHeader, productName, useLoc } from "../_shared";
import { useFormat } from "../../i18n/useFormat";
import { SubstitutionRequestModal } from "./SubstitutionRequestModal";

const S = {
  back: { en: "Back to the queue", ar: "العودة إلى الطابور" },
  loading: { en: "Opening the order…", ar: "جارٍ فتح الطلب…" },
  notFound: {
    en: "That order could not be opened. It may have been completed, cancelled, or the reference may be wrong.",
    ar: "تعذّر فتح هذا الطلب. ربما اكتمل أو أُلغي أو أن الرقم غير صحيح.",
  },
  placed: { en: "Ordered", ar: "تاريخ الطلب" },
  expires: { en: "Valid until", ar: "صالح حتى" },
  expiresUnknown: { en: "Not recorded", ar: "غير مسجّل" },
  daysLeft: { en: "{n} days left", ar: "متبقٍ {n} يوم" },
  lastDay: { en: "Last day", ar: "آخر يوم" },
  lapsed: { en: "Lapsed", ar: "منتهٍ" },

  // ---- lines ----
  examinations: { en: "Examinations", ar: "الفحوصات" },
  examination: { en: "Examination", ar: "الفحص" },
  unitPrice: { en: "Unit price", ar: "سعر الوحدة" },
  noPrice: { en: "No price", ar: "بلا سعر" },
  orderedQty: { en: "Ordered", ar: "المطلوب" },
  performed: { en: "Performed", ar: "المنفَّذ" },
  remaining: { en: "Remaining", ar: "المتبقي" },
  performNow: { en: "Perform now", ar: "التنفيذ الآن" },
  fillLine: { en: "Fill the remaining quantity", ar: "تنفيذ الكمية المتبقية" },
  substitute: { en: "Ask about a different examination", ar: "الاستفسار عن فحص بديل" },
  overRemaining: { en: "Only {n} left on this line", ar: "المتبقي على هذا البند {n} فقط" },
  fixQuantities: {
    en: "One line asks for more than is left on it. Correct it before submitting.",
    ar: "أحد البنود يطلب أكثر من المتبقي عليه. صححه قبل الإرسال.",
  },

  // ---- money ----
  totals: { en: "What this order costs", ar: "تكلفة هذا الطلب" },
  total: { en: "Order total", ar: "إجمالي الطلب" },
  memberShare: { en: "Patient pays", ar: "يدفع المريض" },
  payerShare: { en: "Payer pays", ar: "يدفع الممول" },
  totalHint: { en: "List price of everything ordered", ar: "سعر قائمة كل ما طُلب" },
  memberHint: { en: "Their share under this plan", ar: "حصته وفق هذه الخطة" },
  payerHint: { en: "Covered by the benefit", ar: "ما تغطيه المنفعة" },
  notQuoted: { en: "Cannot be quoted", ar: "تعذّر التسعير" },
  pricingLoading: { en: "Pricing…", ar: "جارٍ التسعير…" },
  repricing: { en: "Repricing…", ar: "جارٍ إعادة التسعير…" },
  tier: { en: "Tier {code}", ar: "الشريحة {code}" },
  ofTotal: { en: "{pct}% of {amount}", ar: "{pct}٪ من {amount}" },
  // Which of the two questions the share tiles answer. The labels change with the basis rather than the
  // figures changing silently under one label.
  basisAll: { en: "If the whole order is delivered", ar: "إذا نُفِّذ الطلب بالكامل" },
  basisNow: { en: "For the {qty} being performed now", ar: "مقابل {qty} يُجرى الآن" },
  basisNowOne: { en: "For the 1 being performed now", ar: "مقابل فحص واحد يُجرى الآن" },
  basisNote: {
    en: "The patient and payer shares follow what you are performing. They are re-quoted through the plan "
      + "each time you change a quantity — not scaled from the order total, because a deductible is met "
      + "before coinsurance applies.",
    ar: "تتبع حصتا المريض والممول ما تُجريه الآن. ويُعاد احتسابهما عبر الخطة مع كل تغيير في الكمية — لا "
      + "تُشتق نسبياً من إجمالي الطلب، لأن التحمّل يُستوفى قبل تطبيق نسبة المشاركة.",
  },
  pricingFailed: {
    en: "The cost of this order could not be worked out. This is NOT a report that it is free — do not quote "
      + "a figure to the patient from this screen.",
    ar: "تعذّر احتساب تكلفة هذا الطلب. هذا ليس تقريراً بأنه مجاني — لا تُبلغ المريض بأي مبلغ من هذه الشاشة.",
  },
  estimate: {
    en: "An estimate at today's list prices, priced through the same rules a claim is settled by. The final "
      + "amount is set when the claim is adjudicated.",
    ar: "تقدير بأسعار القائمة اليوم، محسوب بالقواعد نفسها التي تُسوّى بها المطالبة. ويُحدَّد المبلغ النهائي "
      + "عند تسوية المطالبة.",
  },

  // ---- the action bar ----
  performAll: { en: "Perform all", ar: "تنفيذ الكل" },
  clearAll: { en: "Clear", ar: "مسح" },
  audit: { en: "Audit", ar: "مراجعة" },
  auditing: { en: "Checking…", ar: "جارٍ الفحص…" },
  submit: { en: "Submit", ar: "إرسال" },
  print: { en: "Print", ar: "طباعة" },
  printTitle: { en: "Fulfilment record", ar: "سجل التنفيذ" },
  printHint: {
    en: "Print what was just performed. The payer-side authorization number is issued by the approval team "
      + "moments later and is not on this slip — the order number is the reference both sides share.",
    ar: "اطبع ما تم تنفيذه للتو. يصدر رقم التفويض لدى الممول من فريق الموافقات بعد لحظات ولا يظهر على هذه "
      + "القسيمة — ورقم الطلب هو المرجع المشترك بين الطرفين.",
  },
  selectedCount: { en: "{n} of {total} lines · {qty} units", ar: "{n} من {total} بنود · {qty} وحدة" },
  nothingSelected: { en: "Nothing selected", ar: "لم يُحدد شيء" },
  auditClean: {
    en: "Checked against the server just now — the order and the price on this screen are current.",
    ar: "تمت المطابقة مع الخادم الآن — الطلب والسعر المعروضان محدَّثان.",
  },
  auditMoved: {
    en: "This screen was out of date and has been refreshed: {what}. Check the quantities before submitting.",
    ar: "كانت هذه الشاشة قديمة وتم تحديثها: {what}. راجع الكميات قبل الإرسال.",
  },
  auditFailed: {
    en: "The order could not be re-read, so nothing on this screen has been confirmed. Do not treat it as "
      + "current.",
    ar: "تعذّرت إعادة قراءة الطلب، لذا لم يتم التحقق من أي شيء على هذه الشاشة. لا تعتبرها محدَّثة.",
  },
  driftQty: { en: "quantities performed elsewhere", ar: "كميات نُفّذت في مكان آخر" },
  driftPrice: { en: "the price", ar: "السعر" },
  driftExpiry: { en: "the validity window", ar: "مدة الصلاحية" },
  driftLines: { en: "which lines are outstanding", ar: "البنود المتبقية" },

  // ---- consuming ----
  nothing: { en: "Nothing to record — enter a quantity.", ar: "لا يوجد ما يُسجل — أدخل كمية." },
  confirmTitle: { en: "Confirm fulfilment", ar: "تأكيد التنفيذ" },
  done: { en: "Order fulfilled.", ar: "تم تنفيذ الطلب." },
  partial: { en: "Order partially fulfilled.", ar: "تم تنفيذ الطلب جزئياً." },
  replay: { en: "Already recorded — nothing was consumed twice.", ar: "مسجل مسبقاً — لم يُنفَّذ مرتين." },
  fail: { en: "Could not record the fulfilment.", ar: "تعذّر تسجيل التنفيذ." },
  expiredBody: {
    en: "This order is past the window it was written for, so nothing on it can be recorded. The approval "
      + "team can revalidate it from the queue — the patient does not need a new order from a doctor.",
    ar: "تجاوز هذا الطلب المدة المحددة له، فلا يمكن تسجيل أي شيء عليه. يمكن لفريق الموافقات إعادة تفعيله من "
      + "الطابور — ولا يحتاج المريض إلى طلب جديد من الطبيب.",
  },
} satisfies Record<string, Localized>;

/**
 * One investigation order, on its own page — the bench's counterpart of the prescription page.
 *
 * <b>Why the two pages are the same page.</b> A lab bench and a dispensing counter are the same situation:
 * someone standing in front of a patient, working through a document, recording what was actually delivered
 * and telling them what it costs. The queue could only show an order collapsed to its first test and a panel
 * count, so a three-line order was three facts squeezed into one number — and the money was not shown at all.
 *
 * <b>Ordered, performed and remaining are three columns and are never subtracted into one.</b> "2 of 5" and
 * "3 remaining" answer different questions, and a bench that only shows the remainder cannot tell a fresh
 * order from one a patient has been working through across three visits.
 *
 * <b>What the per-line control does here, and why it differs from pharmacy's.</b> It asks the approval team,
 * rather than offering alternatives. See <see cref="SubstitutionRequestModal"/>: examinations have no
 * equivalence set in master data, and inventing one from the category would put "any radiology procedure"
 * behind a button.
 */
export function InvestigationOrderPage({ orderNo }: { orderNo: string }) {
  const api = useApi();
  const t = useLoc();
  const navigate = useNavigate();
  const { date } = useFormat();

  const [order, setOrder] = useState<InvestigationOrder | null>(null);
  const [state, setState] = useState<"loading" | "ready" | "missing">("loading");
  const [sent, setSent] = useState<Localized | null>(null);

  /**
   * Re-read the order.
   *
   * <b>A failed RE-read never blanks the page.</b> The first load has nothing to fall back on, so a failure
   * there is "could not be opened". Once an order is on screen the technician has a patient in front of them
   * and is working from it; replacing all of that with an error because a refresh timed out takes away the
   * thing they were reading and tells them nothing they can act on. The audit says the re-read failed and the
   * screen stays as it was — stale, and known to be stale.
   */
  const load = useCallback(async (): Promise<InvestigationOrder | null> => {
    const fail = () => { setState((prev) => (prev === "loading" ? "missing" : prev)); return null; };
    try {
      const found = await api.investigationOrder(orderNo);
      if (!found) return fail();
      setOrder(found);
      setState("ready");
      return found;
    } catch {
      return fail();
    }
  }, [api, orderNo]);

  useEffect(() => { setState("loading"); void load(); }, [load]);

  const days = order?.expiresAt
    ? Math.ceil((Date.parse(order.expiresAt) - Date.now()) / 86_400_000)
    : null;

  return (
    <>
      <PageHeader title={orderNo} />
      <Button variant="ghost" size="sm" onClick={() => navigate(-1)}>{t(S.back)}</Button>

      {state === "loading" && <p className="muted">{t(S.loading)}</p>}
      {state === "missing" && <InlineAlert tone="warn">{t(S.notFound)}</InlineAlert>}

      {/* The outcome of a substitution request, announced without moving focus — the technician's next move
          is the next patient, not this row. */}
      <div aria-live="polite">
        {sent && <InlineAlert tone="info">{t(sent)}</InlineAlert>}
      </div>

      {state === "ready" && order && (
        <div className="rx-page">
          <Card as="section" className="rx-head">
            {/* Phase 20 — the context bar, carried over from the consume modal this page replaced. A lab's
                projection is min-header + ALLERGIES only (design 39 §4), which is exactly what matters at the
                bench: contrast and reagent reactions. It also NAMES the patient, which is the identity check
                a technician performs before drawing a sample — a masked token answers "which row" and nothing
                a bench needs. */}
            <PatientContextBar beneficiaryId={order.patient.id} namedAllergens />

            <div className="rx-head-meta">
              <h2 className="rx-head-no tnum">{order.orderNo}</h2>
              <StatusChip kind={order.expired ? "bad" : order.status.kind} label={t(order.status.label)} />
              <dl className="rx-meta">
                <div>
                  <dt>{t(S.placed)}</dt>
                  <dd className="tnum">{date(order.placedAt)}</dd>
                </div>
                <div>
                  <dt>{t(S.expires)}</dt>
                  {/* The date AND how long is left. A date alone makes the technician do the arithmetic, and
                      "expires 13 Aug" is the fact that matters least — "2 days left" is what changes what
                      they say to the patient about coming back for the rest. */}
                  <dd className="tnum">
                    {order.expiresAt ? date(order.expiresAt) : <span className="muted">{t(S.expiresUnknown)}</span>}
                    {days !== null && (
                      <span className={days <= 2 ? "rx-meta-note rx-meta-note--soon" : "rx-meta-note"}>
                        {order.expired || days < 0
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

          <FulfilBody key={order.id} order={order} reload={load} onSent={setSent} />
        </div>
      )}
    </>
  );
}

function FulfilBody({
  order, reload, onSent,
}: {
  order: InvestigationOrder;
  reload: () => Promise<InvestigationOrder | null>;
  onSent: (m: Localized) => void;
}) {
  const api = useApi();
  const t = useLoc();
  const { toast } = useToast();
  const { lang } = useTheme();
  const { money } = useFormat();
  const write = useWrite();

  // Quantities default to ZERO, never to the remainder. Pre-filling the maximum makes "record everything"
  // the path of least resistance and turns a partial fulfilment — the common case when one analyser is down
  // — into a correction of a number that already looked right. `Perform all` is the explicit act.
  const [qty, setQty] = useState<Record<string, number>>({});
  const [asking, setAsking] = useState<InvestigationOrderLine | null>(null);
  const [busy, setBusy] = useState(false);
  /** What was performed in the last successful consume, so the bench can print it. */
  const [lastDone, setLastDone] = useState<{ lines: { name: string; quantity: number }[]; at: string } | null>(null);

  const [pricing, setPricing] = useState<OrderPricing | null>(null);
  const [priceState, setPriceState] = useState<"loading" | "repricing" | "ready" | "error">("loading");

  /**
   * Which response is allowed to land. Typing "12" fires a quote for 1 and a quote for 12, and the network
   * does not promise to answer in that order; without a sequence the bench would occasionally settle on the
   * share for a quantity nobody entered. Only the newest request may write.
   */
  const priceSeq = useRef(0);

  // Lifted out of the tiles so the ROWS can use it too: the per-line unit price lives on the same payload,
  // and fetching it twice would let the row and the total disagree about what an examination costs.
  const loadPricing = useCallback(async (
    performNow?: Record<string, number>,
    mode: "loading" | "repricing" = "loading",
  ) => {
    const seq = ++priceSeq.current;
    setPriceState(mode);
    try {
      const next = await api.orderPricing(order.id, performNow);
      if (seq !== priceSeq.current) return;
      setPricing(next);
      setPriceState("ready");
    } catch {
      if (seq !== priceSeq.current) return;
      // A failed re-quote clears the figures rather than leaving the previous ones beside a changed
      // quantity — a stale share next to a new number is the error a technician would read out without
      // hesitating.
      setPricing(null);
      setPriceState("error");
    }
  }, [api, order.id]);

  const remaining = (l: InvestigationOrderLine) => Math.max(0, l.quantityOrdered - l.quantityConsumed);
  const at = (id: string) => qty[id] ?? 0;

  /** The basis for the cost share: what is about to be performed, clamped to what is left on each line. */
  const performNow = useMemo(() => {
    const basis: Record<string, number> = {};
    for (const l of order.lines) {
      const q = Math.min(qty[l.id] ?? 0, Math.max(0, l.quantityOrdered - l.quantityConsumed));
      if (q > 0) basis[l.id] = q;
    }
    return basis;
  }, [qty, order.lines]);

  // Serialised so the effect re-runs on a CHANGE of basis rather than on every render — a fresh object
  // identity each render would put the bench into a permanent re-quote loop.
  const basisKey = useMemo(
    () => Object.entries(performNow).sort(([a], [b]) => a.localeCompare(b)).map(([k, v]) => `${k}:${v}`).join("|"),
    [performNow],
  );

  /**
   * Re-quote the split whenever what is being performed changes.
   *
   * <p><b>Why the server is asked again instead of the figure being scaled.</b> The split runs a deductible
   * before a copay before coinsurance (`libs/money`), so the member's share of one examination is not a third
   * of their share of three. A browser multiplying by a ratio would produce a confident number the claim
   * later contradicts. Debounced because each quote is a live eligibility check.</p>
   */
  const first = useRef(true);
  useEffect(() => {
    if (first.current) {
      first.current = false;
      void loadPricing(undefined, "loading");
      return;
    }
    const id = window.setTimeout(() => {
      void loadPricing(basisKey ? performNow : undefined, "repricing");
    }, 400);
    return () => window.clearTimeout(id);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [basisKey, loadPricing]);

  const unitPrice = useCallback(
    (l: InvestigationOrderLine) =>
      pricing?.lines.find((p) => p.orderLineId === l.id)?.unitPriceEgp ?? null,
    [pricing],
  );

  const pending = () =>
    order.lines
      .filter((l) => at(l.id) > 0)
      .map((l) => ({ lineId: l.id, quantity: Math.min(at(l.id), remaining(l)) }));

  // A line asking for more than is left on it. Reported per-field AND on the bar, because a technician who
  // has scrolled past the offending row needs to know why Submit will not move.
  const overLines = order.lines.filter((l) => at(l.id) > remaining(l));

  const selected = useMemo(() => {
    const lines = order.lines.filter((l) => at(l.id) > 0);
    return { count: lines.length, units: lines.reduce((s, l) => s + Math.min(at(l.id), remaining(l)), 0) };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [qty, order.lines]);

  const fillable = order.lines.filter((l) => remaining(l) > 0);

  function fillAll() {
    setQty((s) => {
      const next = { ...s };
      for (const l of fillable) next[l.id] = remaining(l);
      return next;
    });
  }

  async function consume() {
    const lines = pending();
    if (lines.length === 0) return;
    setBusy(true);
    try {
      const res = await api.consume({
        orderId: order.id,
        idempotencyKey: write.idempotencyKey,
        panels: lines.reduce((s, l) => s + l.quantity, 0),
        lines,
      });
      if (res.replayed) toast(t(S.replay), "info");
      else toast(t(res.panelsDone >= res.panelsTotal ? S.done : S.partial), "ok");
      // Captured BEFORE the reload, because the reload moves the quantities on. The slip describes what was
      // performed just now, not what the order says afterwards.
      setLastDone({
        lines: lines.map((l) => ({
          name: productName(t(order.lines.find((x) => x.id === l.lineId)?.test.label ?? { en: "", ar: "" })),
          quantity: l.quantity,
        })),
        at: new Date().toISOString(),
      });
      setQty({});
      await reload();
      await loadPricing();
    } catch (e) {
      toast(writeErrorText(writeErrorMessage(e), lang) ?? t(S.fail), "bad");
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <Card as="section" className="rx-card">
        <div className="rx-card-head">
          <h2 className="section-h">{t(S.examinations)}</h2>
          {!order.expired && fillable.length > 0 && (
            <div className="rx-card-actions">
              <Button variant="secondary" size="sm" onClick={fillAll}>{t(S.performAll)}</Button>
              <Button variant="ghost" size="sm" onClick={() => setQty({})}>{t(S.clearAll)}</Button>
            </div>
          )}
        </div>

        {/* An expired order is still SHOWN in full — a technician with the patient in front of them needs to
            see what it says. What is withheld is the input, because consume answers 409 for it and offering
            a box would be a promise the screen cannot keep. */}
        {order.expired && <InlineAlert tone="warn">{t(S.expiredBody)}</InlineAlert>}

        <div className="rx-dispense-scroll mrs-scroll mrs-scroll-focusable" tabIndex={0}>
          <table className="rx-dispense-table">
            <thead>
              <tr>
                <th scope="col">{t(S.examination)}</th>
                {/* Its own column, header off-screen — see the counter's note. Inside the widest cell the
                    button landed at a different x on every row, and an action that moves as you scan down is
                    one you have to hunt for. */}
                <th scope="col" className="rx-col-act"><span className="sr-only">{t(S.substitute)}</span></th>
                <th scope="col" className="rx-num">{t(S.unitPrice)}</th>
                {/* Ordered and performed stay APART. Collapsing them into "remaining" alone loses whether
                    this is a fresh order or one the patient has been working through for a fortnight. */}
                <th scope="col" className="rx-num">{t(S.orderedQty)}</th>
                <th scope="col" className="rx-num">{t(S.performed)}</th>
                <th scope="col" className="rx-num">{t(S.remaining)}</th>
                <th scope="col" className="rx-col-qty">{t(S.performNow)}</th>
              </tr>
            </thead>
            <tbody>
              {order.lines.map((l) => {
                const price = unitPrice(l);
                const left = remaining(l);
                return (
                  <tr key={l.id} className={at(l.id) > 0 ? "rx-row rx-row--picked" : "rx-row"}>
                    <td>
                      <div className="rx-drug-main">
                        <strong className="rx-drug-name">{productName(t(l.test.label))}</strong>
                        <span className="rx-drug-sig tnum">{l.test.code}</span>
                      </div>
                    </td>
                    <td className="rx-col-act">
                      {/* The icon carries an accessible NAME that includes the examination. One identically
                          labelled button per row is unusable by keyboard or screen reader on a multi-line
                          order — and picking the wrong row asks about the wrong test. */}
                      <button
                        type="button"
                        className="rx-icon-btn"
                        aria-label={`${t(S.substitute)} — ${productName(t(l.test.label))}`}
                        title={t(S.substitute)}
                        onClick={() => setAsking(l)}
                      >
                        <Icon name="replace" width={18} height={18} aria-hidden="true" />
                      </button>
                    </td>
                    {/* Per-line price, for the same reason the counter has one: "480 for the order" does not
                        tell a technician which examination is the expensive one, which is the conversation
                        when a patient cannot afford all of it today. No examination in master data carries a
                        price yet, so today every one of these reads "No price" — the honest state. */}
                    <td className="rx-num tnum">
                      {price === null ? <span className="rx-unrecorded">{t(S.noPrice)}</span> : money(price)}
                    </td>
                    <td className="rx-num tnum">{l.quantityOrdered}</td>
                    <td className="rx-num tnum">{l.quantityConsumed}</td>
                    <td className="rx-num tnum">{left}</td>
                    <td className="rx-col-qty">
                      {order.expired ? (
                        <StatusChip kind="bad" label={t(S.lapsed)} />
                      ) : (
                        <div className="rx-qty">
                          {/* The label names the examination for assistive tech and is HIDDEN on screen —
                              see the counter's note: the column header repeated once per row is three times
                              the row height and says nothing a sighted user has not already read, while a
                              screen reader still needs it. */}
                          <InputField
                            label={`${t(S.performNow)} — ${productName(t(l.test.label))}`}
                            hideLabel
                            type="number"
                            min={0}
                            max={left}
                            value={at(l.id)}
                            error={at(l.id) > left ? t(S.overRemaining).replace("{n}", String(left)) : undefined}
                            disabled={left === 0}
                            // The value is read BEFORE the state updater, not inside it. React nulls
                            // `currentTarget` once the handler returns, and a functional updater runs later —
                            // so `e.currentTarget.value` in there reads from a detached event and the entry
                            // is silently dropped. The DOM keeps what was typed while state does not, which
                            // is the worst shape of all: the box shows 99 and the page believes 0.
                            onChange={(e) => {
                              const next = Math.max(0, Number(e.currentTarget.value));
                              setQty((s) => ({ ...s, [l.id]: next }));
                            }}
                          />
                          {/* Fills THIS line's remainder — the same one-tap control the dispensing counter
                              has, because the common case at a bench is also "all of it". */}
                          <button
                            type="button"
                            className={at(l.id) === left && left > 0 ? "rx-icon-btn rx-icon-btn--on" : "rx-icon-btn"}
                            aria-label={`${t(S.fillLine)} — ${productName(t(l.test.label))}`}
                            title={t(S.fillLine)}
                            disabled={left === 0}
                            aria-pressed={at(l.id) === left && left > 0}
                            onClick={() => setQty((s) => ({ ...s, [l.id]: at(l.id) === left ? 0 : left }))}
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

      <PricingTiles pricing={pricing} state={priceState} units={selected.units} />

      {lastDone && <PrintSlip order={order} done={lastDone} />}

      <ActionBar
        order={order}
        selected={selected}
        invalid={overLines.length > 0}
        invalidText={t(S.fixQuantities)}
        busy={busy}
        lastDone={lastDone}
        onSubmit={() => {
          if (overLines.length > 0) { toast(t(S.fixQuantities), "bad"); return; }
          if (pending().length === 0) { toast(t(S.nothing), "bad"); return; }
          void consume();
        }}
        reload={reload}
        reloadPricing={() => loadPricing(basisKey ? performNow : undefined, "repricing")}
        pricing={pricing}
      />

      {asking && (
        <SubstitutionRequestModal
          open
          onOpenChange={(open) => { if (!open) setAsking(null); }}
          order={order}
          line={asking}
          onSent={(m) => { onSent(m); setAsking(null); }}
        />
      )}

    </>
  );
}

/**
 * What the bench hands over on paper.
 *
 * <b>It describes the fulfilment, not the order.</b> The quantities are the ones just performed, captured
 * before the reload moved them on — a slip reprinting the order's running totals would tell a patient
 * returning for the second half of a panel set that all of it was done.
 *
 * <b>No authorization number, deliberately.</b> Issuance is asynchronous and a technician's role does not
 * hold `auth:read`, so the number is not the bench's to print; the order number is the reference the bench,
 * the patient and the payer all share.
 */
function PrintSlip({
  order, done,
}: {
  order: InvestigationOrder;
  done: { lines: { name: string; quantity: number }[]; at: string };
}) {
  const t = useLoc();
  const { date } = useFormat();

  return (
    <section className="rx-slip" aria-hidden="true">
      <h1>{t(S.printTitle)}</h1>
      <div className="rx-slip-meta">
        <span>{t(S.examination)}</span><span>{order.orderNo}</span>
        <span>{t(S.placed)}</span><span>{date(order.placedAt)}</span>
        <span>{t(S.performed)}</span><span>{date(done.at)}</span>
      </div>

      <table>
        <thead>
          <tr>
            <th scope="col">{t(S.examination)}</th>
            <th scope="col">{t(S.performed)}</th>
          </tr>
        </thead>
        <tbody>
          {done.lines.map((l) => (
            <tr key={l.name}><td>{l.name}</td><td>{l.quantity}</td></tr>
          ))}
        </tbody>
      </table>

      <p className="rx-slip-foot">{t(S.printHint)}</p>
    </section>
  );
}

/**
 * The submit bar — the dispensing counter's, for the bench.
 *
 * <b>It sits in normal flow</b>, for the reason given on the counter's: a bar floating over the content costs
 * a strip of every screen it appears on, and reads as an overlay on a page that is otherwise a stack of
 * cards.
 *
 * <b>What Audit does, and what it does not.</b> It re-reads the order and the price from the server and
 * reports what moved: a panel performed at another site, a price that has changed, a validity window that has
 * since lapsed. It fixes the SCREEN, which is the thing that goes stale while a bench is open; it does not
 * edit the order, and a control that quietly corrected a clinician's request would be a worse idea than the
 * staleness it cured.
 */
function ActionBar({
  order, selected, invalid, invalidText, busy, lastDone, onSubmit, reload, reloadPricing, pricing,
}: {
  order: InvestigationOrder;
  selected: { count: number; units: number };
  /** A line asks for more than is left on it. Submit is refused until it is corrected. */
  invalid: boolean;
  invalidText: string;
  busy: boolean;
  /** What was performed on the last successful submit. Null until there has been one. */
  lastDone: { lines: { name: string; quantity: number }[]; at: string } | null;
  onSubmit: () => void;
  reload: () => Promise<InvestigationOrder | null>;
  reloadPricing: () => Promise<void>;
  pricing: OrderPricing | null;
}) {
  const t = useLoc();
  const [auditing, setAuditing] = useState(false);
  const [outcome, setOutcome] = useState<{ tone: "ok" | "warn" | "bad"; text: string } | null>(null);

  async function audit() {
    setAuditing(true);
    setOutcome(null);
    const before = {
      consumed: order.lines.map((l) => `${l.id}:${l.quantityConsumed}`).join("|"),
      lineIds: order.lines.map((l) => l.id).sort().join("|"),
      expiresAt: order.expiresAt ?? "",
      expired: order.expired,
      total: pricing?.totalEgp ?? null,
    };
    try {
      const fresh = await reload();
      await reloadPricing();
      if (!fresh) { setOutcome({ tone: "bad", text: t(S.auditFailed) }); return; }

      const moved: string[] = [];
      if (fresh.lines.map((l) => `${l.id}:${l.quantityConsumed}`).join("|") !== before.consumed) moved.push(t(S.driftQty));
      if (fresh.lines.map((l) => l.id).sort().join("|") !== before.lineIds) moved.push(t(S.driftLines));
      if ((fresh.expiresAt ?? "") !== before.expiresAt || fresh.expired !== before.expired) moved.push(t(S.driftExpiry));
      if ((pricing?.totalEgp ?? null) !== before.total) moved.push(t(S.driftPrice));

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
    <div className="rx-actionbar" role="region" aria-label={t(S.examinations)}>
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
                .replace("{total}", String(order.lines.length))
                .replace("{qty}", String(selected.units))}
        </span>

        <div className="rx-actionbar-buttons">
          <Button variant="ghost" loading={auditing} onClick={() => void audit()}>
            {auditing ? t(S.auditing) : t(S.audit)}
          </Button>
          {/* Print appears only AFTER something has been performed — a slip for work that did not happen is
              the one thing a record must never be. `Perform all` lives on the card head with the lines it
              acts on, so the bar carries only what applies to the whole submission. */}
          {lastDone && (
            <Button variant="secondary" leadingIcon={<Icon name="doc" />} onClick={() => window.print()}>
              {t(S.print)}
            </Button>
          )}
          <Button
            variant="primary"
            loading={busy}
            disabled={order.expired || invalid || selected.count === 0}
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
 * The three figures the bench quotes.
 *
 * <b>Why an unknown split is never rendered as 0.00.</b> Identical to the dispensing counter's rule and for
 * the identical reason: at a counter a zero reads as "free". A beneficiary told their scan is free — who then
 * receives a bill, or who declines something they could have afforded — has been misinformed by a screen that
 * looked confident.
 *
 * <b>Today every one of these will read "cannot be quoted".</b> No examination in master data carries a
 * price. That is the mechanism working: the honest state is stated with its reason until a real tariff is
 * loaded.
 */
function PricingTiles({
  pricing, state, units,
}: {
  pricing: OrderPricing | null;
  state: "loading" | "repricing" | "ready" | "error";
  /** Units entered at the bench — the figure the share hint names, so what was priced is unambiguous. */
  units: number;
}) {
  const t = useLoc();
  const { money } = useFormat();

  const amount = (v: number | null | undefined) => (v === null || v === undefined ? null : money(v));

  // The denominator is what the split was QUOTED ON, not the order total. On a partial those differ, and
  // dividing by the total would report a 20% coinsurance as a smaller number than the plan actually charges.
  const basis = pricing?.quotedOnEgp ?? pricing?.totalEgp ?? null;
  const share = (v: number | null | undefined) =>
    v === null || v === undefined || !basis
      ? null
      : t(S.ofTotal)
          .replace("{pct}", String(Math.round((v / basis) * 100)))
          .replace("{amount}", money(basis));

  const onNow = pricing?.quotedOnPerformNow === true;
  const shareHint = !onNow
    ? t(S.basisAll)
    : units === 1
      ? t(S.basisNowOne)
      : t(S.basisNow).replace("{qty}", String(units));

  return (
    <Card as="section" className="rx-card">
      <div className="rx-card-head">
        <h2 className="section-h">{t(S.totals)}</h2>
        {pricing?.tierCode && <StatusChip kind="neu" label={t(S.tier).replace("{code}", pricing.tierCode)} />}
      </div>

      {state === "loading" && <p className="muted">{t(S.pricingLoading)}</p>}
      {/* A failed fetch is never rendered as free. */}
      {state === "error" && <InlineAlert tone="bad">{t(S.pricingFailed)}</InlineAlert>}

      {(state === "ready" || state === "repricing") && pricing && (
        <>
          {/* Dimmed, not blanked, while a quote is in flight: replacing the figures on every keystroke would
              make the section flicker and teach a technician to read whatever appears next. */}
          <div className={state === "repricing" ? "rx-tiles rx-tiles--busy" : "rx-tiles"} aria-busy={state === "repricing"}>
            <Tile label={t(S.total)} hint={t(S.totalHint)} value={amount(pricing.totalEgp)} fallback={t(S.notQuoted)} />
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

          {/* aria-live so a screen-reader user typing a quantity hears the share change rather than the
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
          into a split a technician can sanity-check out loud. */}
      {value !== null && note && <span className="rx-tile-note tnum">{note}</span>}
      <span className="rx-tile-hint">{hint}</span>
    </div>
  );
}

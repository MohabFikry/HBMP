import { useCallback, useEffect, useState } from "react";
import { Button, InlineAlert, Modal, StatusChip } from "@mersal/design-system";
import type { AmendReasonOption, Localized, OrderRow, OrderRowLine } from "@mersal/contracts";
import { useApi } from "../../api/ApiProvider";
import { useFormat } from "../../i18n/useFormat";
import { useLoc } from "../_shared";
import { AmendLineDialog } from "../AmendLineDialog";
import type { AmendAction, LineLockedReason } from "../AmendLineDialog";

/**
 * The investigation order as it was raised, read back by the clinician who raised it.
 *
 * <b>The sibling of `PrescriptionDetailModal`, deliberately.</b> An order and a prescription are the same
 * situation from a prescriber's side — I asked for several things at once, and I want to see what I asked for
 * — so they share a shape, a class vocabulary (`.rxv-*`) and the same rules about absent values. Two dialogs
 * that answer the same question in two different layouts is how a portal stops reading as one product.
 *
 * <b>It costs no fetch.</b> Every field here already arrived with the row: `/investigation-orders/mine` has
 * always returned the full lines, and the worklist was discarding them at `lines[0].code`. That matters
 * beyond latency — orders audits each PHI read, so a per-open request would enter the patient's audit trail
 * once per glance and make "who read this record, and how often" harder to answer than the reading justified.
 *
 * <b>Ordered and performed are two figures and are never collapsed into one.</b> "2 of 5 performed" and "3
 * remaining" answer different questions, and this dialog is asked the first one: what did I request. The
 * bench's own view is where the remainder belongs.
 */
const S = {
  title: { en: "Order", ar: "الطلب" },
  status: { en: "Status", ar: "الحالة" },
  type: { en: "Type", ar: "النوع" },
  placed: { en: "Raised on", ar: "تاريخ الطلب" },
  validUntil: { en: "Valid until", ar: "صالح حتى" },
  noExpiry: { en: "No expiry set", ar: "بدون تاريخ انتهاء" },
  tests: { en: "Tests", ar: "الفحوصات" },
  testMissing: { en: "Test name not recorded", ar: "اسم الفحص غير مسجّل" },
  testMissingHint: {
    en: "The catalogue holds no description for this code, so only the code itself remains. The performing "
      + "provider resolves the examination from that code.",
    ar: "لا يحتوي الكتالوج على وصف لهذا الرمز، لذلك لم يبقَ سوى الرمز نفسه. تحدّد الجهة المنفّذة الفحص من هذا الرمز.",
  },
  code: { en: "Code", ar: "الرمز" },
  ordered: { en: "Quantity ordered", ar: "الكمية المطلوبة" },
  performed: { en: "Performed to date", ar: "المنفَّذ حتى الآن" },
  noLines: { en: "This order has no lines.", ar: "لا يحتوي هذا الطلب على أسطر." },

  // ---- 30.6 amend / cancel (design 46 §1-§3, §10) ----------------------------------------------------
  withdraw: { en: "Withdraw", ar: "سحب" },
  amend: { en: "Amend", ar: "تعديل" },
  /** Shown BESIDE the disabled control, never instead of it — a hidden action reads as a missing feature. */
  lockedConsumed: { en: "Delivered — cannot be changed", ar: "تم تنفيذه — لا يمكن تغييره" },
  lockedWithdrawn: { en: "Withdrawn", ar: "مسحوب" },
  lockedAmended: { en: "Replaced by a newer version", ar: "استُبدل بنسخة أحدث" },
  lockedExpired: { en: "The order has expired", ar: "انتهت صلاحية الطلب" },
  failed: {
    en: "That change could not be applied. Nothing was altered — reopen the order to see its current state.",
    ar: "تعذّر تطبيق التغيير. لم يُعدَّل شيء — أعد فتح الطلب لعرض حالته الحالية.",
  },
} satisfies Record<string, Localized>;

/**
 * Why this line cannot be changed, or null when it can.
 *
 * <p>Derived from what the row ALREADY carries — no extra request, and no second opinion about a rule the
 * server also enforces. The server is authoritative; this only decides whether to offer the control, and it
 * errs toward offering it: a wrongly-enabled button produces a specific 409 the doctor can read, while a
 * wrongly-hidden one produces a doctor who believes the feature does not exist.</p>
 */
function lockOf(order: OrderRow, line: OrderRowLine): LineLockedReason | null {
  const status = line.status.label.en;
  if (status === "Completed" || status === "Performed") return { what: "Consumed" };
  if (status === "Cancelled" || status === "Withdrawn") return { what: "Cancelled" };
  if (status === "Superseded") return { what: "Superseded" };
  if (order.expiresAt && new Date(order.expiresAt) <= new Date()) return { what: "Expired" };
  return null;
}

export function OrderDetailModal({
  order,
  onOpenChange,
  onChanged,
}: {
  /** The order to show, or null when the dialog is closed. */
  order: OrderRow | null;
  onOpenChange: (open: boolean) => void;
  /** Called after a line is withdrawn or amended, so the list behind can refetch. */
  onChanged?: () => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
  const api = useApi();

  const [acting, setActing] = useState<{ line: OrderRowLine; action: AmendAction } | null>(null);
  const [reasons, setReasons] = useState<AmendReasonOption[]>([]);
  const [failed, setFailed] = useState(false);

  // Fetched once the dialog is open, not on every row render: the vocabulary is the same for every line and
  // a request per line would be seven requests to fill one picker.
  useEffect(() => {
    if (!order) return;
    let live = true;
    // Guarded, and the guard is not defensive clutter: this list is an ENRICHMENT of a dialog that must
    // open regardless. A throw here — an older client, a transport failure — used to take down the whole
    // encounter screen, which is a catastrophic response to a picker that could not be filled. An empty
    // picker is honest and safe: the dialog already refuses to submit without a reason, so the worst case
    // is a doctor who cannot withdraw, not one who withdraws without recording why.
    Promise.resolve(api.amendmentReasons?.("order") ?? [])
      .then((r) => { if (live) setReasons(r); })
      .catch(() => { if (live) setReasons([]); });
    return () => { live = false; };
  }, [api, order]);

  const confirm = useCallback(
    async (input: { reasonCode: string; reasonText?: string; quantity?: number }) => {
      if (!order || !acting) return;
      setFailed(false);
      try {
        if (acting.action === "cancel") {
          await api.cancelOrderLine(order.id, acting.line.id, input.reasonCode, input.reasonText);
        } else {
          await api.amendOrderLine(
            order.id, acting.line.id, input.quantity ?? acting.line.quantityOrdered,
            input.reasonCode, input.reasonText);
        }
        setActing(null);
        onChanged?.();
        onOpenChange(false);
      } catch {
        // The server refused — a race, an expiry, a scope. It answers with a SPECIFIC problem type; this
        // says the safe thing (nothing changed) and sends the reader to the current state rather than
        // guessing which refusal it was.
        setFailed(true);
        setActing(null);
      }
    },
    [api, order, acting, onChanged, onOpenChange],
  );

  if (!order) return null;

  return (
    <Modal
      open={order !== null}
      onOpenChange={onOpenChange}
      // The order number IS the title — it is what the bench and the patient's slip quote back, and so the
      // thing the reader is matching against while this is open.
      title={`${t(S.title)} ${order.orderNo}`}
      wide
    >
      <dl className="rxv-meta">
        <dt>{t(S.status)}</dt>
        <dd><StatusChip kind={order.status.kind} label={t(order.status.label)} /></dd>

        <dt>{t(S.type)}</dt>
        <dd>{order.orderType}</dd>

        <dt>{t(S.placed)}</dt>
        <dd className="tnum">{fmt.dateTime(order.requestedAt)}</dd>

        <dt>{t(S.validUntil)}</dt>
        <dd className="tnum">
          {order.expiresAt
            ? fmt.dateTime(order.expiresAt)
            : <span className="rxv-missing">{t(S.noExpiry)}</span>}
        </dd>
      </dl>

      <h3 className="rxv-h">
        {t(S.tests)} <span className="tnum rxv-count">({order.lines.length})</span>
      </h3>

      {order.lines.length === 0 ? (
        <p className="muted">{t(S.noLines)}</p>
      ) : (
        // An ordered list, because the ORDER is part of what was raised — a clinician reading their own
        // request back counts down it, and "the second one" has to mean the same thing here as on the slip
        // the patient is carrying to the bench.
        <ol className="rxv-lines">
          {order.lines.map((line, i) => (
            <OrderLineCard
              key={line.id} line={line} index={i + 1} t={t} fmt={fmt}
              lock={lockOf(order, line)}
              onAct={(action) => { setFailed(false); setActing({ line, action }); }}
            />
          ))}
        </ol>
      )}

      {failed && <InlineAlert tone="bad">{t(S.failed)}</InlineAlert>}

      <AmendLineDialog
        open={acting !== null}
        action={acting?.action ?? "cancel"}
        lineLabel={acting ? `${acting.line.code} — ${acting.line.description ?? t(S.testMissing)}` : ""}
        currentQuantity={acting?.line.quantityOrdered}
        reasons={reasons}
        onCancel={() => setActing(null)}
        onConfirm={confirm}
      />
    </Modal>
  );
}

function OrderLineCard({
  line,
  index,
  t,
  fmt,
  lock,
  onAct,
}: {
  line: OrderRowLine;
  index: number;
  t: (l: Localized) => string;
  fmt: ReturnType<typeof useFormat>;
  lock: LineLockedReason | null;
  onAct: (action: AmendAction) => void;
}) {
  const lockedWord =
    lock?.what === "Consumed" ? S.lockedConsumed
    : lock?.what === "Cancelled" ? S.lockedWithdrawn
    : lock?.what === "Superseded" ? S.lockedAmended
    : S.lockedExpired;
  return (
    <li className="rxv-line" data-recorded={line.description ? undefined : "no"}>
      <div className="rxv-line-h">
        <span className="rxv-line-n tnum" aria-hidden="true">{index}</span>
        {line.description ? (
          <span className="rxv-drug">{line.description}</span>
        ) : (
          // Dashed and hollow, the treatment this app gives every unanswered state. It is a statement about
          // the RECORD, not about the examination, and it has to look like one — the code below still names
          // the test to anyone who can read a code, which is exactly who performs it.
          <span className="rxv-drug rxv-missing" title={t(S.testMissingHint)}>
            <span className="rxv-missing-glyph" aria-hidden="true">○</span>
            {t(S.testMissing)}
          </span>
        )}
        <StatusChip kind={line.status.kind} label={t(line.status.label)} />
      </div>
      <dl className="rxv-grid">
        <div className="rxv-cell">
          <dt>{t(S.code)}</dt>
          {/* The system alongside the code: "80053" means nothing without knowing it is a CPT. */}
          <dd className="tnum">{line.codeSystem ? `${line.codeSystem} ${line.code}` : line.code}</dd>
        </div>
        <div className="rxv-cell">
          <dt>{t(S.ordered)}</dt>
          <dd className="tnum">{fmt.number(line.quantityOrdered)}</dd>
        </div>
        {/*
          Kept apart from the ordered quantity and never subtracted from it. This dialog answers "what did I
          request"; how much of it has been performed is the bench's answer to a different question, and
          folding the two into one "remaining" figure would make the original unreadable from here.
        */}
        <div className="rxv-cell">
          <dt>{t(S.performed)}</dt>
          <dd className="tnum">{fmt.number(line.quantityConsumed)}</dd>
        </div>
      </dl>

      {/*
        30.6 — the actions, DISABLED rather than hidden when the line can no longer change, with the reason
        in words beside them (design 46 §10). "A hidden control makes the doctor think the feature is
        missing" — and then they ring the pharmacy instead of reading the sentence that would have answered
        them. `aria-describedby` ties the two together, so a screen reader gets the explanation with the
        button rather than as unrelated text further down.
      */}
      <div className="rxv-line-actions">
        <Button
          variant="secondary" size="sm" disabled={lock !== null} onClick={() => onAct("amend")}
          aria-describedby={lock ? `lock-${line.id}` : undefined}
        >
          {t(S.amend)}
        </Button>
        <Button
          variant="danger" size="sm" disabled={lock !== null} onClick={() => onAct("cancel")}
          aria-describedby={lock ? `lock-${line.id}` : undefined}
        >
          {t(S.withdraw)}
        </Button>
        {lock && <span id={`lock-${line.id}`} className="rxv-missing">{t(lockedWord)}</span>}
      </div>
    </li>
  );
}

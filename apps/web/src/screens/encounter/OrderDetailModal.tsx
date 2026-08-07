import { Modal, StatusChip } from "@mersal/design-system";
import type { Localized, OrderRow, OrderRowLine } from "@mersal/contracts";
import { useFormat } from "../../i18n/useFormat";
import { useLoc } from "../_shared";

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
} satisfies Record<string, Localized>;

export function OrderDetailModal({
  order,
  onOpenChange,
}: {
  /** The order to show, or null when the dialog is closed. */
  order: OrderRow | null;
  onOpenChange: (open: boolean) => void;
}) {
  const t = useLoc();
  const fmt = useFormat();
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
            <OrderLineCard key={line.id} line={line} index={i + 1} t={t} fmt={fmt} />
          ))}
        </ol>
      )}
    </Modal>
  );
}

function OrderLineCard({
  line,
  index,
  t,
  fmt,
}: {
  line: OrderRowLine;
  index: number;
  t: (l: Localized) => string;
  fmt: ReturnType<typeof useFormat>;
}) {
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
    </li>
  );
}

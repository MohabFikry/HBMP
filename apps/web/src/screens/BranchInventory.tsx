import { useMemo, useState } from "react";
import { Button, Card, DataTable, InlineAlert, InputField, StatusChip, useTheme } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import { inventoryApi } from "../api/branchApi";
import type { ItemCategory, Movement, MovementKind, StockLine } from "../api/branchApi";
import { useAsync } from "../api/useAsync";
import { useWrite, writeErrorText } from "../api/useWrite";
import { AsyncSection, PageHeader, useLoc } from "./_shared";
import { useFormat } from "../i18n/useFormat";
import type { Localized } from "../portals/catalog";

const S = {
  title: { en: "Inventory", ar: "المخزون" },
  intro: {
    en: "What is on the shelf, derived from every movement recorded against it. Consumables used during care — never anything dispensed to a patient, which goes through pharmacy.",
    ar: "ما هو متوفر على الرف، محسوبًا من كل حركة مسجلة عليه. مستهلكات تُستخدم أثناء الرعاية — وليست أدوية تُصرف للمستفيد، فتلك تمر عبر الصيدلية.",
  },

  medical: { en: "Medical", ar: "طبي" },
  nonMedical: { en: "Non-medical", ar: "غير طبي" },
  ledger: { en: "Movement ledger", ar: "سجل الحركات" },

  item: { en: "Item", ar: "الصنف" },
  sku: { en: "SKU", ar: "الرمز" },
  batch: { en: "Batch", ar: "التشغيلة" },
  expiry: { en: "Expiry", ar: "تاريخ الانتهاء" },
  onHand: { en: "On hand", ar: "المتوفر" },
  unit: { en: "Unit", ar: "الوحدة" },
  reorder: { en: "Reorder at", ar: "إعادة الطلب عند" },
  stockStatus: { en: "Stock status", ar: "حالة المخزون" },

  ok: { en: "In stock", ar: "متوفر" },
  low: { en: "Low — reorder", ar: "منخفض — أعد الطلب" },
  quarantined: { en: "EXPIRED — quarantined", ar: "منتهٍ — محجوز" },
  quarantineHelp: {
    en: "Expired stock cannot be issued. Clear it with a write-off recording why.",
    ar: "لا يمكن صرف المخزون المنتهي. تخلّص منه بعملية إهلاك مع تسجيل السبب.",
  },

  noStock: { en: "No stock is recorded for this clinic yet.", ar: "لا يوجد مخزون مسجل لهذه العيادة بعد." },
  noMovements: { en: "No movements have been recorded yet.", ar: "لم تُسجل أي حركات بعد." },

  kind: { en: "Movement", ar: "الحركة" },
  quantity: { en: "Quantity", ar: "الكمية" },
  reason: { en: "Reason", ar: "السبب" },
  actor: { en: "Recorded by", ar: "سجلها" },
  when: { en: "When", ar: "التاريخ" },

  record: { en: "Record a movement", ar: "تسجيل حركة" },
  kindReceipt: { en: "Receipt (stock in)", ar: "استلام (وارد)" },
  kindIssue: { en: "Issue (used in clinic)", ar: "صرف (استُخدم بالعيادة)" },
  kindReturn: { en: "Return (unused, back on shelf)", ar: "مرتجع (غير مستخدم، عاد للرف)" },
  kindAdjustment: { en: "Adjustment (correction)", ar: "تسوية (تصحيح)" },
  kindWriteOff: { en: "Write-off (destroyed or expired)", ar: "إهلاك (تالف أو منتهٍ)" },
  kindCount: { en: "Stock-take variance", ar: "فرق الجرد" },
  qtyLabel: { en: "Quantity", ar: "الكمية" },
  qtyHelp: {
    en: "Enter a positive amount. Whether it adds to or removes from stock is decided by the movement you chose.",
    ar: "أدخل كمية موجبة. أما إن كانت تضيف للمخزون أو تخصم منه فيحدده نوع الحركة الذي اخترته.",
  },
  reasonRequired: {
    en: "Required for adjustments, write-offs and stock-take variances — these say the records were wrong, and a ledger without a reason stops being evidence.",
    ar: "مطلوب للتسويات والإهلاك وفروق الجرد — فهذه تقر بأن السجلات كانت خاطئة، والسجل بلا سبب يفقد قيمته كدليل.",
  },
  post: { en: "Record movement", ar: "تسجيل الحركة" },
  posted: { en: "Movement recorded.", ar: "تم تسجيل الحركة." },
  replayed: { en: "Already recorded — applied once, not twice.", ar: "مسجلة بالفعل — طُبقت مرة واحدة فقط." },
  needQty: { en: "Enter a quantity greater than zero.", ar: "أدخل كمية أكبر من صفر." },
  needReason: { en: "Enter a reason for this movement.", ar: "أدخل سببًا لهذه الحركة." },
  needItem: { en: "Choose an item.", ar: "اختر صنفًا." },
} satisfies Record<string, Localized>;

const WRITABLE_KINDS: Array<{ kind: MovementKind; label: Localized }> = [
  { kind: "Receipt", label: S.kindReceipt },
  { kind: "Issue", label: S.kindIssue },
  { kind: "Return", label: S.kindReturn },
  { kind: "Adjustment", label: S.kindAdjustment },
  { kind: "WriteOff", label: S.kindWriteOff },
  { kind: "Count", label: S.kindCount },
];

const REASON_REQUIRED: MovementKind[] = ["Adjustment", "WriteOff", "Count"];

/**
 * 25.7 (design 42 §5/§6) — clinic stock.
 *
 * <b>Medical and non-medical are separate tabs</b> because their rules genuinely differ: batch and expiry are
 * mandatory and visible on the medical tab and absent from the other, so a storekeeper is never asked for a
 * lot number on a box of printer paper, and never allowed to skip one on a box of sutures.
 *
 * <b>On-hand is presented as derived.</b> The ledger sits on the same screen, not behind a link: the number
 * and the movements that produce it are one thing, and separating them is how a balance becomes a figure
 * people quote without being able to explain.
 */
export function BranchInventory() {
  const t = useLoc();
  const { lang } = useTheme();
  const fmt = useFormat();   // 18.D2/U7 — Cairo-pinned; never a bare toLocaleString.
  const [tab, setTab] = useState<ItemCategory>("Medical");

  const stock = useAsync(() => inventoryApi.stock({ category: tab }), [tab]);
  const movements = useAsync(() => inventoryApi.movements({ pageSize: 50 }), []);

  const medical = tab === "Medical";

  const stockColumns: Column<StockLine>[] = useMemo(() => {
    const base: Column<StockLine>[] = [
      { key: "item", header: t(S.item), cell: (l) => (lang === "ar" ? l.nameAr : l.nameEn) },
      { key: "sku", header: t(S.sku), cell: (l) => l.sku },
      { key: "onHand", header: t(S.onHand), cell: (l) => `${l.onHand} ${l.unitOfMeasure}` },
      { key: "reorder", header: t(S.reorder), cell: (l) => String(l.reorderLevel) },
      {
        key: "status",
        header: t(S.stockStatus),
        // Four cues, same discipline as the licence chip: quarantined stock is red + cross + square + a word
        // that says it cannot be issued, never a grey "expired".
        cell: (l) =>
          l.isQuarantined ? (
            <StatusChip kind="bad" label={t(S.quarantined)} />
          ) : l.isLow ? (
            <StatusChip kind="warn" label={t(S.low)} />
          ) : (
            <StatusChip kind="ok" label={t(S.ok)} />
          ),
      },
    ];
    // Batch and expiry are shown ONLY for medical stock — mandatory there, meaningless here.
    return medical
      ? [
          base[0], base[1],
          { key: "batch", header: t(S.batch), cell: (l) => l.batchNo ?? "—" },
          { key: "expiry", header: t(S.expiry), cell: (l) => l.expiryDate ?? "—" },
          ...base.slice(2),
        ]
      : base;
  }, [t, lang, medical]);

  const movementColumns: Column<Movement>[] = useMemo(
    () => [
      { key: "when", header: t(S.when), cell: (m) => fmt.dateTime(m.occurredAt) },
      { key: "kind", header: t(S.kind), cell: (m) => m.kind },
      // The SIGN is shown, because it is what makes the running total explicable: +40, −15, −6.
      { key: "qty", header: t(S.quantity), cell: (m) => (m.quantity > 0 ? `+${m.quantity}` : String(m.quantity)) },
      { key: "reason", header: t(S.reason), cell: (m) => m.reason ?? "—" },
      { key: "actor", header: t(S.actor), cell: (m) => m.actor },
    ],
    [t, lang, fmt],
  );

  return (
    <>
      <PageHeader title={t(S.title)} />
      <p className="lede">{t(S.intro)}</p>

      <div role="tablist" aria-label={t(S.title)} className="tabs">
        {(["Medical", "NonMedical"] as ItemCategory[]).map((c) => (
          <button
            key={c}
            role="tab"
            type="button"
            aria-selected={tab === c}
            onClick={() => setTab(c)}
          >
            {t(c === "Medical" ? S.medical : S.nonMedical)}
          </button>
        ))}
      </div>

      {medical && <InlineAlert tone="info">{t(S.quarantineHelp)}</InlineAlert>}

      <AsyncSection state={stock} isEmpty={(d) => d.stock.length === 0} emptyLabel={S.noStock}>
        {(data) => (
          <Card>
            <DataTable
              caption={t(medical ? S.medical : S.nonMedical)}
              columns={stockColumns}
              rows={data.stock}
              rowKey={(l) => `${l.branchId}:${l.itemId}:${l.batchId ?? "-"}`}
            />
          </Card>
        )}
      </AsyncSection>

      <RecordMovement
        lang={lang}
        stock={stock.data?.stock ?? []}
        onPosted={() => {
          stock.reload();
          movements.reload();
        }}
      />

      <h2>{t(S.ledger)}</h2>
      <AsyncSection state={movements} isEmpty={(d) => d.movements.length === 0} emptyLabel={S.noMovements}>
        {(data) => (
          <Card>
            <DataTable
              caption={t(S.ledger)}
              columns={movementColumns}
              rows={data.movements}
              rowKey={(m) => m.movementId}
            />
          </Card>
        )}
      </AsyncSection>
    </>
  );
}

function RecordMovement({
  lang,
  stock,
  onPosted,
}: {
  lang: "en" | "ar";
  stock: StockLine[];
  onPosted: () => void;
}) {
  const t = useLoc();
  const [line, setLine] = useState("");
  const [kind, setKind] = useState<MovementKind>("Receipt");
  const [quantity, setQuantity] = useState("");
  const [reason, setReason] = useState("");
  const [validation, setValidation] = useState<string | null>(null);
  const [outcome, setOutcome] = useState<string | null>(null);
  const write = useWrite();

  const selected = stock.find((l) => `${l.itemId}:${l.batchId ?? "-"}` === line);
  const reasonRequired = REASON_REQUIRED.includes(kind);

  const submit = async () => {
    if (!selected) { setValidation(t(S.needItem)); return; }
    const qty = Number(quantity);
    if (!Number.isFinite(qty) || qty <= 0) { setValidation(t(S.needQty)); return; }
    if (reasonRequired && !reason.trim()) { setValidation(t(S.needReason)); return; }
    setValidation(null);

    // The idempotency key comes from `useWrite`, which mints ONE key per intent and reuses it across
    // retries — so a slow network cannot turn one receipt into two phantom units of stock.
    const ok = await write.run(async (idempotencyKey) => {
      const result = await inventoryApi.postMovement(idempotencyKey, {
        branchId: selected.branchId,
        itemId: selected.itemId,
        batchId: selected.batchId ?? undefined,
        kind,
        quantity: qty,
        reason: reason.trim() || undefined,
      });
      setOutcome(result.replayed ? t(S.replayed) : t(S.posted));
      return result;
    });
    if (ok) {
      setQuantity("");
      setReason("");
      onPosted();
    }
  };

  return (
    <Card>
      <h2>{t(S.record)}</h2>

      <label className="field">
        <span className="field-label">{t(S.item)}</span>
        <select value={line} onChange={(e) => setLine(e.target.value)}>
          <option value="">—</option>
          {stock.map((l) => (
            <option key={`${l.itemId}:${l.batchId ?? "-"}`} value={`${l.itemId}:${l.batchId ?? "-"}`}>
              {(lang === "ar" ? l.nameAr : l.nameEn) + (l.batchNo ? ` · ${l.batchNo}` : "")}
            </option>
          ))}
        </select>
      </label>

      <label className="field">
        <span className="field-label">{t(S.kind)}</span>
        <select value={kind} onChange={(e) => setKind(e.target.value as MovementKind)}>
          {WRITABLE_KINDS.map((k) => (
            <option key={k.kind} value={k.kind}>{t(k.label)}</option>
          ))}
        </select>
      </label>

      <InputField
        label={t(S.qtyLabel)}
        type="number"
        min={0}
        step="0.001"
        value={quantity}
        onChange={(e) => setQuantity(e.target.value)}
        help={t(S.qtyHelp)}
        required
      />
      <InputField
        label={t(S.reason)}
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        help={reasonRequired ? t(S.reasonRequired) : undefined}
        required={reasonRequired}
        maxLength={300}
      />

      {validation && <InlineAlert tone="warn">{validation}</InlineAlert>}
      {write.error && <InlineAlert tone="bad">{writeErrorText(write.error, lang)}</InlineAlert>}
      {outcome && (
        <InlineAlert tone="ok">
          <span role="status" aria-live="polite">{outcome}</span>
        </InlineAlert>
      )}

      <div className="row-actions">
        <Button onClick={submit} disabled={write.busy}>
          {t(S.post)}
        </Button>
      </div>
    </Card>
  );
}

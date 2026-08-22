import { useMemo, useState } from "react";
import { Button, Card, DataTable, DataTableView, InlineAlert, InputField, Modal, Pagination, SegmentedControl, ComboboxField, StatusChip, useTableQuery, useTheme } from "@mersal/design-system";
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
  search: { en: "Search", ar: "بحث" },
  stockSearchHint: { en: "Item name, SKU or batch", ar: "اسم الصنف أو الرمز أو التشغيلة" },
  noMatches: {
    en: "No stock lines match. Change the search or clear the filters.",
    ar: "لا توجد أصناف مطابقة. عدّل البحث أو أزل عوامل التصفية.",
  },

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
  recordDescription: {
    en: "Receipts, issues, returns, adjustments, write-offs and stock-take variances. Every one is appended to the ledger and none is edited afterwards.",
    ar: "الاستلام والصرف والمرتجعات والتسويات والإهلاك وفروق الجرد. تُضاف كل حركة إلى السجل ولا تُعدَّل بعد ذلك.",
  },
  close: { en: "Close", ar: "إغلاق" },
  cancel: { en: "Cancel", ar: "إلغاء" },
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
  const [recording, setRecording] = useState(false);

  const stock = useAsync(() => inventoryApi.stock({ category: tab }), [tab]);
  /*
    THE LEDGER IS SERVER-PAGED, and it was truncated.

    An append-only stock ledger has no natural size. This asked for fifty rows, rendered them, and threw
    `total` away — so movement 51 was unreachable and nothing on screen said the list had an end. That is the
    same defect the policy book had, arriving here through a different door.

    Paged on the SERVER rather than through `useTableQuery`, because the endpoint already pages and a ledger
    is exactly the thing that outgrows a browser. The stock table below is the opposite case — one branch's
    lines, a bounded list — and is filtered in the browser.
  */
  const [ledgerPage, setLedgerPage] = useState(1);
  const [ledgerSize, setLedgerSize] = useState(25);
  const movements = useAsync(
    () => inventoryApi.movements({ page: ledgerPage, pageSize: ledgerSize }), [ledgerPage, ledgerSize]);

  const medical = tab === "Medical";

  const stockColumns: Column<StockLine>[] = useMemo(() => {
    const base: Column<StockLine>[] = [
      { key: "item", header: t(S.item), cell: (l) => (lang === "ar" ? l.nameAr : l.nameEn) },
      { key: "sku", header: t(S.sku), cell: (l) => l.sku, sortable: true, sortValue: (l) => l.sku },
      { key: "onHand", header: t(S.onHand), cell: (l) => `${l.onHand} ${l.unitOfMeasure}` },
      { key: "reorder", header: t(S.reorder), cell: (l) => String(l.reorderLevel), sortable: true, sortValue: (l) => l.reorderLevel },
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
          { key: "batch", header: t(S.batch), cell: (l) => l.batchNo ?? "—", sortable: true, sortValue: (l) => l.batchNo },
          { key: "expiry", header: t(S.expiry), cell: (l) => l.expiryDate ?? "—", sortable: true, sortValue: (l) => l.expiryDate },
          ...base.slice(2),
        ]
      : base;
  }, [t, lang, medical]);

  const movementColumns: Column<Movement>[] = useMemo(
    () => [
      { key: "when", header: t(S.when), cell: (m) => fmt.dateTime(m.occurredAt), sortable: true, sortValue: (m) => m.occurredAt },
      { key: "kind", header: t(S.kind), cell: (m) => m.kind, sortable: true, sortValue: (m) => m.kind },
      // The SIGN is shown, because it is what makes the running total explicable: +40, −15, −6.
      { key: "qty", header: t(S.quantity), cell: (m) => (m.quantity > 0 ? `+${m.quantity}` : String(m.quantity)) },
      { key: "reason", header: t(S.reason), cell: (m) => m.reason ?? "—", sortable: true, sortValue: (m) => m.reason },
      { key: "actor", header: t(S.actor), cell: (m) => m.actor, sortable: true, sortValue: (m) => m.actor },
    ],
    [t, lang, fmt],
  );

  /** One branch's stock lines — a bounded list, so search and filter happen in the browser. */
  const stockQuery = useTableQuery<StockLine>({
    rows: stock.data?.stock ?? [],
    columns: stockColumns,
    searchText: (l) => [l.nameEn, l.nameAr, l.sku, l.batchNo].filter(Boolean).join(" "),
    searchLabel: t(S.search),
    searchPlaceholder: t(S.stockSearchHint),
    // The two states a storekeeper looks for. Quarantined stock cannot be issued at all; low stock is what
    // the next order is built from.
    filters: [{
      key: "state",
      label: t(S.stockStatus),
      options: [
        { value: "quarantined", label: t(S.quarantined) },
        { value: "low", label: t(S.low) },
      ],
      match: (l, value) => (value === "quarantined" ? l.isQuarantined : l.isLow && !l.isQuarantined),
    }],
    pageSize: 25,
    persistKey: `branch-stock-${tab}`,
  });

  return (
    <div className="branch-screen">
      <PageHeader title={t(S.title)} />
      <p className="muted lede">{t(S.intro)}</p>

      {/* Was a hand-rolled `role="tablist"` of BARE buttons under a `.tabs` class that is defined nowhere.
          `aria-selected` was set, so a screen reader knew which category was showing and a sighted user had
          NO cue at all — two identical default-grey browser buttons, and the only way to tell which list you
          were looking at was to read the table. SegmentedControl is the house control for an in-screen
          switch (ApprovalsWorklist, BeneficiaryPortal use it) and carries the selected state, the roving
          focus and the 44px targets. Not `Tabs`: this filters one table rather than swapping panels. */}
      {/*
        THE CATEGORY SWITCH AND THE ONE WRITE, on one row.

        Recording a movement is a nine-field form and it sat permanently open BETWEEN the stock table and the
        ledger — so the two things this screen exists to show, a balance and the movements that produce it,
        were separated by the form that produces them. Reading the ledger meant scrolling past a form nobody
        had asked for; and on a screen where both reads were failing, the form was the only thing on it that
        looked healthy.
      */}
      <div className="branch-toolbar">
        <SegmentedControl<ItemCategory>
          aria-label={t(S.title)}
          segments={[
            { value: "Medical", label: t(S.medical) },
            { value: "NonMedical", label: t(S.nonMedical) },
          ]}
          value={tab}
          onChange={setTab}
        />
        <Button variant="primary" onClick={() => setRecording(true)}>{t(S.record)}</Button>
      </div>

      {medical && <InlineAlert tone="info">{t(S.quarantineHelp)}</InlineAlert>}

      <AsyncSection state={stock} isEmpty={(d) => d.stock.length === 0} emptyLabel={S.noStock}>
        {() => (
          <Card>
            <DataTableView
              query={stockQuery}
              columns={stockColumns}
              rowKey={(l) => `${l.branchId}:${l.itemId}:${l.batchId ?? "-"}`}
              caption={t(medical ? S.medical : S.nonMedical)}
              emptyLabel={t(S.noStock)}
              noMatchesLabel={t(S.noMatches)}
            />
          </Card>
        )}
      </AsyncSection>

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
            {/* Shown always, like the membership book's: "1–25 of 4,812" is the answer to "how much has
                moved through this branch", which is a question a ledger is opened with. */}
            <Pagination
              page={ledgerPage}
              pageSize={ledgerSize}
              total={data.total}
              onPageChange={setLedgerPage}
              onPageSizeChange={(n) => { setLedgerSize(n); setLedgerPage(1); }}
              pageSizeOptions={[10, 25, 50, 100]}
            />
          </Card>
        )}
      </AsyncSection>
      {recording && (
        <RecordMovement
          lang={lang}
          stock={stock.data?.stock ?? []}
          onClose={() => setRecording(false)}
          onPosted={() => {
            stock.reload();
            movements.reload();
          }}
        />
      )}
    </div>
  );
}

/**
 * The one write on this screen, in a dialog.
 *
 * <p>Rendered only while open, which matters more here than it looks: the form holds a chosen item, a
 * movement kind and a quantity, and those are a claim about a moment. A quantity left over from a dialog
 * closed twenty minutes ago, against stock that has moved since, is exactly the kind of stale write an
 * append-only ledger cannot take back.</p>
 *
 * <p>It stays a dialog rather than a route because it is short and it belongs to the table behind it: the
 * item list it offers IS the stock on screen.</p>
 */
function RecordMovement({
  lang,
  stock,
  onClose,
  onPosted,
}: {
  lang: "en" | "ar";
  stock: StockLine[];
  onClose: () => void;
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
    <Modal
      open
      onOpenChange={(next) => { if (!next) onClose(); }}
      title={t(S.record)}
      description={t(S.recordDescription)}
      footer={
        <>
          <Button onClick={submit} disabled={write.busy}>{t(S.post)}</Button>
          <Button variant="ghost" onClick={onClose}>{t(S.cancel)}</Button>
        </>
      }
    >
      <ComboboxField
        label={t(S.item)}
        // The empty option is gone: `placeholder` is how Select says "nothing chosen", and an em-dash in the
        // list read as a selectable item called "—".
        placeholder="—"
        options={stock.map((l) => ({
          value: `${l.itemId}:${l.batchId ?? "-"}`,
          label: (lang === "ar" ? l.nameAr : l.nameEn) + (l.batchNo ? ` · ${l.batchNo}` : ""),
        }))}
        value={line || null}
        onChange={setLine}
      />

      <ComboboxField
        label={t(S.kind)}
        options={WRITABLE_KINDS.map((k) => ({ value: k.kind, label: t(k.label) }))}
        value={kind}
        onChange={(v) => setKind(v as MovementKind)}
      />

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
      {/* The dialog stays OPEN on success. A storekeeper booking in a delivery records several lines in a
          row, and closing after each one would make them reopen it and re-choose an item they are still
          holding. The tables behind have already reloaded. */}
      {outcome && (
        <InlineAlert tone="ok">
          <span role="status" aria-live="polite">{outcome}</span>
        </InlineAlert>
      )}
    </Modal>
  );
}

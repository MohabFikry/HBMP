import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Button, Card, DataTable, InlineAlert, InputField, StatusChip } from "@mersal/design-system";
import type { Column } from "@mersal/design-system";
import type { LabOrder, Localized } from "@mersal/contracts";
import { useApi } from "../api/ApiProvider";
import { RequestExtensionModal } from "./extensions/RequestExtensionModal";
import { PageHeader, useLoc } from "./_shared";
import { ApiError } from "../api/http";

const S = {
  labTitle: { en: "Perform an order", ar: "تنفيذ طلب" },
  imagingTitle: { en: "Perform an order", ar: "تنفيذ طلب" },
  empty: { en: "No orders in the queue.", ar: "لا توجد طلبات في الطابور." },

  // ---- search (27.8) ----
  searchTitle: { en: "Find a patient's orders", ar: "ابحث عن طلبات المريض" },
  searchHint: {
    en: "Search by order number, or by TWO of the patient's identifiers.",
    ar: "ابحث برقم الطلب، أو باثنين من معرّفات المريض.",
  },
  fOrderNo: { en: "Order number", ar: "رقم الطلب" },
  fCard: { en: "Card number", ar: "رقم البطاقة" },
  fMember: { en: "Member number", ar: "رقم العضوية" },
  fPassport: { en: "Passport", ar: "جواز السفر" },
  phOrderNo: { en: "ORD-2026-000001", ar: "ORD-2026-000001" },
  search: { en: "Search", ar: "بحث" },
  clear: { en: "Clear", ar: "مسح" },
  startHere: {
    en: "Enter an order number, or two of the patient's identifiers, to begin.",
    ar: "أدخل رقم الطلب أو اثنين من معرّفات المريض للبدء.",
  },
  twoIdentifiers: {
    en: "A card number on its own is not enough — it is printed on something that gets shared and "
      + "photographed. Add the member number or passport, or search by order number instead.",
    ar: "رقم البطاقة وحده لا يكفي — فهو مطبوع على ما يُتداول ويُصوَّر. أضف رقم العضوية أو جواز السفر، "
      + "أو ابحث برقم الطلب.",
  },
  directoryDown: {
    en: "The patient directory could not be reached, so these identifiers could not be checked. "
      + "This is NOT a report that the patient has no orders — try again.",
    ar: "تعذّر الوصول إلى دليل المرضى، لذلك لم يتم التحقق من هذه المعرّفات. هذا ليس تقريراً بعدم وجود "
      + "طلبات — أعد المحاولة.",
  },
  noMatch: { en: "No order matches that search.", ar: "لا يوجد طلب يطابق هذا البحث." },
  fail: { en: "The search failed.", ar: "فشل البحث." },
  test: { en: "Test", ar: "الفحص" },
  patient: { en: "Patient", ar: "المريض" },
  priority: { en: "Priority", ar: "الأولوية" },
  progress: { en: "Progress", ar: "التقدّم" },
  state: { en: "State", ar: "الحالة" },
  action: { en: "Action", ar: "إجراء" },
  open: { en: "Open", ar: "فتح" },
  ref: { en: "Order", ar: "الطلب" },

  // ---- expired + validity extension ----
  expired: { en: "Expired", ar: "منتهٍ" },
  requestExtension: { en: "Request extension", ar: "طلب تمديد" },
  requestTitle: { en: "Ask for this order to be revalidated", ar: "طلب إعادة تفعيل هذا الطلب" },
  expiredBody: {
    en: "This order is past the window it was written for and cannot be fulfilled. The approval team can "
      + "revalidate it — the patient does not need a new order from a doctor.",
    ar: "تجاوز هذا الطلب المدة المحددة له ولا يمكن تنفيذه. يمكن لفريق الموافقات إعادة تفعيله — ولا يحتاج "
      + "المريض إلى طلب جديد من الطبيب.",
  },
  reason: { en: "Why does this need extending?", ar: "لماذا يحتاج هذا إلى تمديد؟" },
  reasonHint: {
    en: "The approval team sees this and nothing else. Say what happened — the whole decision rests on it.",
    ar: "لن يرى فريق الموافقات سوى هذا. اذكر ما حدث — فالقرار كله يستند إليه.",
  },
  reasonPlaceholder: {
    en: "e.g. patient travelled today and the order lapsed while they were waiting for transport",
    ar: "مثال: حضر المريض اليوم وانتهت صلاحية الطلب أثناء انتظاره المواصلات",
  },
  reasonTooShort: {
    en: "Write at least a short sentence. An approver with an empty box is deciding on who asked, not on why.",
    ar: "اكتب جملة قصيرة على الأقل. المُوافِق بدون سبب يقرّر بناءً على من طلب، لا على السبب.",
  },
  send: { en: "Send request", ar: "إرسال الطلب" },
  requestSent: {
    en: "Sent to the approval team as {authNo}. The order stays expired until they decide.",
    ar: "أُرسل إلى فريق الموافقات برقم {authNo}. يبقى الطلب منتهياً حتى يصدر قرارهم.",
  },
  alreadyRequested: {
    en: "Someone has already asked for this one. It is with the approval team.",
    ar: "سبق أن طلب أحدهم ذلك. الطلب لدى فريق الموافقات.",
  },
  requestFailed: { en: "The request could not be sent.", ar: "تعذّر إرسال الطلب." },
} satisfies Record<string, Localized>;

const PRIORITY_KIND = { routine: "neu", urgent: "warn", emergency: "bad" } as const;

/**
 * The fulfilment bench.
 *
 * <b>Search-first, not browse-first (27.8).</b> This screen used to list every open order in the tenant — a
 * board a technician scrolls looking for the patient standing in front of them. That is both the wrong
 * workflow and the wrong disclosure: it puts other patients' orders on screen to reach one. The bench's real
 * question is "what do I have for THIS patient", so the screen opens on the question. Exactly the change the
 * dispensing counter made, through the same shared beneficiary lookup, so the two answer identically —
 * including on the failure paths, which are the ones that matter.
 *
 * Two ways in, and the asymmetry is deliberate. An ORDER NUMBER identifies the order on its own: it is the
 * reference printed on the paper in the patient's hand. A CARD NUMBER does not identify a person — it is
 * printed on something that gets shared, photographed and reused — so it takes a second identifier alongside
 * it (doc 43 §7 D5). The server enforces that; this screen explains it.
 */
export function LabQueue({ kind }: { kind: "lab" | "imaging" }) {
  const api = useApi();
  const t = useLoc();
  const navigate = useNavigate();
  const [form, setForm] = useState({ orderNo: "", cardNumber: "", memberNo: "", passport: "" });
  const [results, setResults] = useState<LabOrder[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<Localized | null>(null);
  /** The expired order an extension is being requested for, or null. */
  const [extending, setExtending] = useState<LabOrder | null>(null);
  const [sent, setSent] = useState<Localized | null>(null);

  const field = (k: keyof typeof form) => ({
    value: form[k],
    onChange: (e: { currentTarget: { value: string } }) => {
      const v = e.currentTarget.value;
      setForm((prev) => ({ ...prev, [k]: v }));
    },
  });

  async function search() {
    setBusy(true);
    setError(null);
    try {
      setResults(await api.labSearch(kind, form));
    } catch (e) {
      setResults(null);
      // Three refusals, three meanings, and only one of them is about the patient. A 503 rendered as "no
      // orders" would be a wrong clinical answer with a calm face on it.
      const status = e instanceof ApiError ? e.status : 0;
      setError(status === 422 ? S.twoIdentifiers : status === 503 ? S.directoryDown : S.fail);
    } finally {
      setBusy(false);
    }
  }

  function clear() {
    setForm({ orderNo: "", cardNumber: "", memberNo: "", passport: "" });
    setResults(null);
    setError(null);
  }

  const canSearch = Object.values(form).some((v) => v.trim() !== "") && !busy;

  const cols: Column<LabOrder>[] = [
    // The order's own reference. A technician reads it back to the patient and writes it on the sample; the
    // internal id is not a thing anyone downstream has seen.
    { key: "orderNo", header: t(S.ref), cell: (r) => <span className="tnum">{r.orderNo}</span> },
    { key: "test", header: t(S.test), cell: (r) => <span><span className="tnum">{r.test.code}</span> · {t(r.test.label)}</span> },
    { key: "patient", header: t(S.patient), cell: (r) => <span className="tnum">{r.patient.token}</span> },
    { key: "priority", header: t(S.priority), cell: (r) => <StatusChip kind={PRIORITY_KIND[r.priority]} label={r.priority} /> },
    { key: "progress", header: t(S.progress), cell: (r) => <span className="tnum">{r.panelsDone}/{r.panelsTotal}</span> },
    { key: "state", header: t(S.state), cell: (r) => <StatusChip kind={r.status.kind} label={t(r.status.label)} /> },
    {
      key: "action",
      header: t(S.action),
      // An expired order is IN the queue — dropping it left a technician with the patient in front of them
      // looking at an empty list and nothing to tell them. What changes is the action: consume is refused by
      // the server (409 order-expired), so offering it would be a promise the screen cannot keep. The
      // recovery is offered in its place.
      cell: (r) =>
        r.expired ? (
          <Button size="sm" variant="secondary" onClick={() => setExtending(r)}>
            {t(S.requestExtension)}
          </Button>
        ) : (
          // Open, not Consume. The modal could only ever fulfil the order's FIRST line against one panel
          // count, so a three-line order was three facts squeezed into one number — and it showed the
          // technician nothing about what the patient would be charged. The order gets its own page for the
          // same reasons the prescription did (ADR-0034), and a URL so a reload lands back on it.
          <Button
            size="sm"
            variant="primary"
            disabled={r.panelsDone >= r.panelsTotal}
            onClick={() => navigate(`/${kind}/order/${encodeURIComponent(r.orderNo)}`)}
          >
            {t(S.open)}
          </Button>
        ),
    },
  ];

  return (
    <>
      <PageHeader title={t(kind === "lab" ? S.labTitle : S.imagingTitle)} />
      {/* The outcome of an extension request, announced without moving focus away from the results — the
          technician's next move is the next patient, not this row. */}
      <div aria-live="polite">
        {sent && <InlineAlert tone="info">{t(sent)}</InlineAlert>}
      </div>

      <Card as="section" style={{ padding: "var(--sp5)", marginBottom: "var(--sp4)" }}>
        <h2 className="section-h" style={{ marginBlockStart: 0 }}>{t(S.searchTitle)}</h2>
        <p className="muted" style={{ marginBlockStart: 0 }}>{t(S.searchHint)}</p>
        {/* A real form, so Enter submits — a bench is typed at, not clicked through. */}
        <form className="rx-search" onSubmit={(e) => { e.preventDefault(); if (canSearch) void search(); }}>
          <InputField label={t(S.fOrderNo)} placeholder={t(S.phOrderNo)} {...field("orderNo")} />
          <InputField label={t(S.fCard)} {...field("cardNumber")} />
          <InputField label={t(S.fMember)} {...field("memberNo")} />
          <InputField label={t(S.fPassport)} {...field("passport")} />
          <div className="rx-search-actions">
            <Button type="submit" variant="primary" loading={busy} disabled={!canSearch}>{t(S.search)}</Button>
            <Button type="button" variant="ghost" onClick={clear}>{t(S.clear)}</Button>
          </div>
        </form>
        {/* aria-live: the outcome of a search the user just triggered, announced without moving focus. */}
        <div aria-live="polite">
          {error && <InlineAlert tone="bad">{t(error)}</InlineAlert>}
          {!error && results?.length === 0 && <InlineAlert tone="info">{t(S.noMatch)}</InlineAlert>}
        </div>
      </Card>

      <Card as="section" style={{ padding: "var(--sp3)" }}>
        {results === null || results.length === 0 ? (
          <p className="muted" style={{ margin: "var(--sp3)" }}>{t(S.startHere)}</p>
        ) : (
          <DataTable columns={cols} rows={results} rowKey={(r) => r.id} caption={t(kind === "lab" ? S.labTitle : S.imagingTitle)} />
        )}
      </Card>

      {extending && (
        <RequestExtensionModal
          open
          onOpenChange={(o) => !o && setExtending(null)}
          item={{
            itemType: "InvestigationOrder",
            itemId: extending.id,
            itemReference: extending.orderNo,
            beneficiaryId: extending.patient.id,
            expiredAt: extending.expiresAt ?? null,
          }}
          placeholder={S.reasonPlaceholder}
          sentMessage={S.requestSent}
          alreadyRequestedMessage={S.alreadyRequested}
          onSent={(m) => { setSent(m); setExtending(null); }}
        />
      )}
    </>
  );
}
